# Export with the pinned Unity editor, then invoke its bundled Gradle/JDK.
# Missing cache entries fail under --offline; this script never retries online.
# -Presentation selects the content flavor (Graybox default = the bootstrap
# scene; VisualBenchmark = the M4 benchmark scene; FullContent = the M6
# three-world production catalog; FullContentPreview = the development-only
# partial-content check); -Configuration selects Development (validation
# endpoints in) or Ordinary (compiled out). The flavor decides the export
# entry point and every output directory, so a benchmark or full-content
# run can never overwrite the graybox APK or mix export roots.
# FullContentPreview is development-only: an ordinary preview is refused
# outright rather than built as a quietly-partial candidate.
# -Configuration Release is the signed family-release path: allowed only
# with -Presentation FullContent, it exports ordinary full content,
# builds :launcher:assembleRelease (unsigned), then zipaligns, signs and
# verifies through Tools/Sign-AndroidRelease.ps1 before publishing to
# Builds\Android-FamilyRelease. Development/Ordinary keep assembleDebug.
param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..'),
    [string]$EditorRoot = 'C:\Program Files\Unity\Hub\Editor',
    [ValidateSet('Graybox', 'VisualBenchmark', 'FullContent', 'FullContentPreview')]
    [string]$Presentation = 'Graybox',
    [ValidateSet('Development', 'Ordinary', 'Release')]
    [string]$Configuration = 'Development'
)

$ErrorActionPreference = 'Stop'
if ($Configuration -eq 'Release' -and $Presentation -ne 'FullContent') {
    throw "-Configuration Release is allowed only with -Presentation FullContent; $Presentation can never be a release candidate."
}
$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$editorVersion = '6000.3.26f1'
$editor = Join-Path $EditorRoot "$editorVersion\Editor\Unity.exe"
$android = Join-Path $EditorRoot "$editorVersion\Editor\Data\PlaybackEngines\AndroidPlayer"
$java = Join-Path $android 'OpenJDK\bin\java.exe'
$flavor = "$Presentation-$Configuration"
$buildFlavor = switch ($flavor) {
    'Graybox-Development'            { 'Android' }
    'Graybox-Ordinary'               { 'Android-Ordinary' }
    'VisualBenchmark-Development'    { 'Android-VisualBenchmark' }
    'VisualBenchmark-Ordinary'       { 'Android-VisualBenchmark-Ordinary' }
    'FullContent-Development'        { 'Android-FullContent' }
    'FullContent-Ordinary'           { 'Android-FullContent-Ordinary' }
    'FullContentPreview-Development' { 'Android-FullContent-Preview' }
    'FullContentPreview-Ordinary'    { throw "FullContentPreview is development-only; an ordinary preview can never be a full-content candidate." }
    'FullContent-Release'            { 'Android-FamilyRelease' }
    default { throw "Unsupported presentation/configuration: $flavor" }
}
$exportMethod = switch ($buildFlavor) {
    'Android'                            { 'FamilyRacing.EditorTools.FoundationBuild.ExportAndroid' }
    'Android-Ordinary'                   { 'FamilyRacing.EditorTools.FoundationBuild.ExportAndroidOrdinary' }
    'Android-VisualBenchmark'            { 'FamilyRacing.EditorTools.FoundationBuild.ExportAndroidVisualBenchmark' }
    'Android-VisualBenchmark-Ordinary'   { 'FamilyRacing.EditorTools.FoundationBuild.ExportAndroidVisualBenchmarkOrdinary' }
    'Android-FullContent'                { 'FamilyRacing.EditorTools.FoundationBuild.ExportAndroidFullContent' }
    'Android-FullContent-Ordinary'       { 'FamilyRacing.EditorTools.FoundationBuild.ExportAndroidFullContentOrdinary' }
    'Android-FullContent-Preview'        { 'FamilyRacing.EditorTools.FoundationBuild.ExportAndroidFullContentPreview' }
    'Android-FamilyRelease'              { 'FamilyRacing.EditorTools.FoundationBuild.ExportAndroidFamilyRelease' }
}
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')
$runDirectory = Join-Path $project "Builds\$buildFlavor\offline\$runId"
$logs = Join-Path $project "Logs\android-offline\$runId"
$export = Join-Path $runDirectory 'gradle'
$published = Join-Path $project "Builds\$buildFlavor\FamilyRacing.apk"
# CMake/ninja enforce the classic 260-char path limit and the mandated
# Builds\<flavor>\offline\<runId>\gradle root can exceed it once the .cxx
# prefab tree nests inside (observed on Android-VisualBenchmark-Ordinary).
# Gradle therefore runs through a short TEMP junction: the real export
# stays under Builds\..., native tooling sees a path short enough to work.
$buildAlias = Join-Path ([IO.Path]::GetTempPath()) ('aob-' + $runId.Substring(16, 8))
$gradleProjectDir = $export
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
    presentation = $Presentation
    configuration = $Configuration
    buildFlavor = $buildFlavor
    exportMethod = $exportMethod
    editorVersion = $editorVersion
    editorPath = $editor
    exportPath = $export
    exportLog = (Join-Path $logs 'export.log')
    exportProcessLog = (Join-Path $logs 'export-process.log')
    gradleLog = (Join-Path $logs 'gradle.log')
    signLog = (Join-Path $logs 'sign.log')
    builtApk = $null
    publishedApk = $null
    apkSha256 = $null
    gradleProjectDir = $export
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
        '-buildTarget', 'Android', '-executeMethod', $exportMethod,
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
    try {
        if (Test-Path -LiteralPath $buildAlias) { [IO.Directory]::Delete($buildAlias) }
        New-Item -ItemType Junction -Path $buildAlias -Target $export | Out-Null
        $gradleProjectDir = $buildAlias
    }
    catch {
        Write-Host "Android: TEMP junction unavailable ($($_.Exception.Message)); building at the real export path."
    }
    $result.gradleProjectDir = $gradleProjectDir
    Write-Host "Android: building with bundled Gradle --offline; log: $($result.gradleLog)"
    # Development/Ordinary keep the debug task. Release produces the
    # unsigned release APK so signing happens only through
    # Sign-AndroidRelease.ps1 with the environment-supplied family key.
    $gradleTask = if ($Configuration -eq 'Release') { ':launcher:assembleRelease' } else { ':launcher:assembleDebug' }
    $gradleArguments = @('-classpath', $launchers[0].FullName, 'org.gradle.launcher.GradleMain',
        '-Dorg.gradle.jvmargs=-Xmx4096m', '-Dorg.gradle.java.installations.auto-download=false',
        '-Pandroid.builder.sdkDownload=false', '--offline', '--no-daemon', '--console=plain',
        '--project-dir', $gradleProjectDir, $gradleTask)
    $code = Invoke-BuildProcess $java $gradleArguments $gradleProjectDir $result.gradleLog
    if ($code -ne 0) {
        $result.exitCode = $code
        throw "Offline Gradle exited $code. Missing cached dependencies require a separate authorized setup; no online retry was attempted. See $($result.gradleLog)."
    }

    $result.stage = 'publish'
    if ($Configuration -eq 'Release') {
        # Inspect the exported Gradle project before any signing step:
        # a signingConfigs block or a pre-signed launcher-release.apk
        # means a debug-key/pre-signing fallback crept in — refuse it.
        $result.stage = 'inspect'
        $launcherGradle = Get-Content -LiteralPath (Join-Path $export 'launcher\build.gradle') -Raw
        if ($launcherGradle -match 'signingConfig') {
            throw "Exported launcher\build.gradle carries a signing config; release signing must happen only through Sign-AndroidRelease.ps1."
        }
        if ($launcherGradle -match 'debuggable\s+true') {
            throw "Exported launcher\build.gradle marks the release build debuggable."
        }
        $unsignedApk = Join-Path $export 'launcher\build\outputs\apk\release\launcher-release-unsigned.apk'
        Require-File $unsignedApk 'Unsigned release APK from this export'
        if ((Get-Item -LiteralPath $unsignedApk).Length -eq 0) { throw "Gradle produced an empty unsigned APK: $unsignedApk" }
        $presignedApk = Join-Path $export 'launcher\build\outputs\apk\release\launcher-release.apk'
        if (Test-Path -LiteralPath $presignedApk) {
            throw "Gradle produced a pre-signed release APK ($presignedApk); the release pipeline accepts only unsigned output."
        }
        $result.builtApk = $unsignedApk

        $result.stage = 'sign'
        $releaseConfigPath = Join-Path $project 'Release\release.json'
        Require-File $releaseConfigPath 'Release identity config'
        $releaseConfig = Get-Content -LiteralPath $releaseConfigPath -Raw | ConvertFrom-Json
        foreach ($field in @('applicationId', 'versionName', 'androidVersionCode',
                'androidMinSdkVersion', 'androidTargetSdkVersion', 'expectedCertificateSha256')) {
            if ($null -eq $releaseConfig.$field) { throw "Release config $releaseConfigPath is missing '$field'." }
        }
        $buildToolsParent = Join-Path $android 'SDK\build-tools'
        $buildToolsDirs = @(Get-ChildItem -LiteralPath $buildToolsParent -Directory -ErrorAction SilentlyContinue)
        if ($buildToolsDirs.Count -ne 1) {
            throw "Expected exactly one bundled build-tools directory under $buildToolsParent; found $($buildToolsDirs.Count)."
        }
        $signScript = Join-Path $PSScriptRoot 'Sign-AndroidRelease.ps1'
        Require-File $signScript 'Release signing tool'
        # Secrets travel only through the process environment into the
        # child signer; its stdout/stderr is public metadata we keep.
        $signOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $signScript `
            -UnsignedApk $unsignedApk -OutputApk $published `
            -ExpectedCertificateSha256 $releaseConfig.expectedCertificateSha256 `
            -ExpectedApplicationId $releaseConfig.applicationId `
            -ExpectedVersionName $releaseConfig.versionName `
            -ExpectedVersionCode $releaseConfig.androidVersionCode `
            -ExpectedMinSdk $releaseConfig.androidMinSdkVersion `
            -ExpectedTargetSdk $releaseConfig.androidTargetSdkVersion `
            -ApkMinSdkVersion $releaseConfig.androidMinSdkVersion `
            -BuildToolsDir $buildToolsDirs[0].FullName -JavaExe $java -RequireArm64 2>&1 | Out-String
        $signCode = $LASTEXITCODE
        $signOutput | Set-Content -LiteralPath $result.signLog -Encoding UTF8
        Write-Host $signOutput.TrimEnd()
        if ($signCode -ne 0) {
            $result.exitCode = $signCode
            throw "Release signing/verification failed (exit $signCode); the previous package was left untouched. See $($result.signLog)."
        }
        $result.publishedApk = $published
        $result.apkSha256 = (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash
    } else {
        $apk = Join-Path $export 'launcher\build\outputs\apk\debug\launcher-debug.apk'
        Require-File $apk 'APK from this export'
        if ((Get-Item -LiteralPath $apk).Length -eq 0) { throw "Gradle produced an empty APK: $apk" }
        $result.builtApk = $apk
        $result.apkSha256 = (Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash
        Copy-Item -LiteralPath $apk -Destination $published -Force
        $result.publishedApk = $published
    }
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
    if (Test-Path -LiteralPath $buildAlias) {
        # Removes the junction only; the real export under Builds\ is untouched.
        [IO.Directory]::Delete($buildAlias)
    }
    $result.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resultPath -Encoding UTF8
    Write-Host "Android build evidence: $resultPath"
}
exit $result.exitCode
