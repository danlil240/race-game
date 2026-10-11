# Export with the pinned Unity editor, then invoke its bundled Gradle/JDK.
# Missing cache entries fail under --offline; this script never retries online.
param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..'),
    [string]$EditorRoot = 'C:\Program Files\Unity\Hub\Editor'
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$editorVersion = '6000.3.26f1'
$editor = Join-Path $EditorRoot "$editorVersion\Editor\Unity.exe"
$android = Join-Path $EditorRoot "$editorVersion\Editor\Data\PlaybackEngines\AndroidPlayer"
$java = Join-Path $android 'OpenJDK\bin\java.exe'
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')
$runDirectory = Join-Path $project "Builds\Android\offline\$runId"
$logs = Join-Path $project "Logs\android-offline\$runId"
$export = Join-Path $runDirectory 'gradle'
$published = Join-Path $project 'Builds\Android\FamilyRacing.apk'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$resultPath = Join-Path $logs 'result.json'
$result = [ordered]@{
    schemaVersion = 1
    runId = $runId
    status = 'failed'
    stage = 'prerequisites'
    offline = $true
    startedUtc = [DateTime]::UtcNow.ToString('o')
    finishedUtc = $null
    editorVersion = $editorVersion
    editorPath = $editor
    exportPath = $export
    exportLog = (Join-Path $logs 'export.log')
    exportProcessLog = (Join-Path $logs 'export-process.log')
    gradleLog = (Join-Path $logs 'gradle.log')
    builtApk = $null
    publishedApk = $null
    apkSha256 = $null
    exitCode = 1
    error = ''
}

function Require-File([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description missing: $Path. Install/cache prerequisites separately; this build will not download them."
    }
}

function Quote-NativeArgument([string]$Value) {
    # Windows CRT argument quoting: double backslashes before a quote and
    # before the closing quote. No shell interprets these arguments.
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Invoke-BuildProcess([string]$Executable, [string[]]$Arguments,
    [string]$WorkingDirectory, [string]$LogPath) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = ($Arguments | ForEach-Object { Quote-NativeArgument $_ }) -join ' '
    $start.WorkingDirectory = $WorkingDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $child = New-Object System.Diagnostics.Process
    $child.StartInfo = $start
    try {
        if (-not $child.Start()) { throw "Process did not start: $Executable" }
        # Drain both streams concurrently to avoid full-pipe deadlocks.
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        $child.WaitForExit()
        $code = $child.ExitCode
        $text = "Executable: $Executable`r`nArguments: $($start.Arguments)`r`n" +
            "Working directory: $WorkingDirectory`r`nExit code: $code`r`n`r`n" +
            $stdout.Result + "`r`n--- stderr ---`r`n" + $stderr.Result
        [IO.File]::WriteAllText($LogPath, $text)
        return $code
    }
    finally { $child.Dispose() }
}

try {
    Require-File $editor "Pinned Unity editor $editorVersion"
    Require-File $java 'Bundled OpenJDK'
    Require-File (Join-Path $android 'SDK\platform-tools\adb.exe') 'Bundled Android SDK'
    Require-File (Join-Path $android 'NDK\ndk-build.cmd') 'Bundled Android NDK'
    $gradleLib = Join-Path $android 'Tools\gradle\lib'
    $launchers = @(Get-ChildItem -LiteralPath $gradleLib -Filter 'gradle-launcher-*.jar' -File -ErrorAction SilentlyContinue)
    if ($launchers.Count -ne 1) {
        throw "Expected exactly one bundled Gradle launcher JAR in $gradleLib; found $($launchers.Count)."
    }
    New-Item -ItemType Directory -Path $runDirectory | Out-Null
    $result.stage = 'export'
    Write-Host "Android: exporting Gradle project; log: $($result.exportLog)"
    $unityArguments = @('-batchmode', '-quit', '-projectPath', $project,
        '-buildTarget', 'Android', '-executeMethod', 'FamilyRacing.EditorTools.FoundationBuild.ExportAndroid',
        '-logFile', $result.exportLog, '--android-export-dir', $export)
    $code = Invoke-BuildProcess $editor $unityArguments $project $result.exportProcessLog
    if ($code -ne 0) {
        $result.exitCode = $code
        throw "Unity export exited $code. See $($result.exportLog) and $($result.exportProcessLog)."
    }
    foreach ($relative in @('settings.gradle', 'build.gradle', 'launcher\build.gradle', 'unityLibrary\build.gradle')) {
        Require-File (Join-Path $export $relative) 'Exported Gradle project file'
    }

    $result.stage = 'gradle'
    Write-Host "Android: building with bundled Gradle --offline; log: $($result.gradleLog)"
    $gradleArguments = @('-classpath', $launchers[0].FullName, 'org.gradle.launcher.GradleMain',
        '-Dorg.gradle.jvmargs=-Xmx4096m', '-Dorg.gradle.java.installations.auto-download=false',
        '-Pandroid.builder.sdkDownload=false', '--offline', '--no-daemon', '--console=plain',
        '--project-dir', $export, ':launcher:assembleDebug')
    $code = Invoke-BuildProcess $java $gradleArguments $export $result.gradleLog
    if ($code -ne 0) {
        $result.exitCode = $code
        throw "Offline Gradle exited $code. Missing cached dependencies require a separate authorized setup; no online retry was attempted. See $($result.gradleLog)."
    }

    $result.stage = 'publish'
    $apk = Join-Path $export 'launcher\build\outputs\apk\debug\launcher-debug.apk'
    Require-File $apk 'APK from this export'
    if ((Get-Item -LiteralPath $apk).Length -eq 0) { throw "Gradle produced an empty APK: $apk" }
    $result.builtApk = $apk
    $result.apkSha256 = (Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash
    Copy-Item -LiteralPath $apk -Destination $published -Force
    $result.publishedApk = $published
    $result.stage = 'complete'
    $result.status = 'succeeded'
    $result.exitCode = 0
    Write-Host "Android offline build succeeded: $published"
}
catch {
    $result.error = $_.Exception.Message
    Write-Host "Android offline build failed during $($result.stage): $($result.error)"
}
finally {
    $result.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resultPath -Encoding UTF8
    Write-Host "Android build evidence: $resultPath"
}
exit $result.exitCode
