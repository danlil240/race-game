# Build the Windows player with the pinned Unity editor in batch mode.
#
#   -Configuration Development  FoundationBuild.Windows -> Mono development
#                               player at Builds\Windows\FamilyRacing.exe
#                               (validation endpoints compiled in).
#   -Configuration Ordinary     FoundationBuild.WindowsOrdinary -> Mono
#                               player at Builds\Windows-Ordinary\
#                               FamilyRacing.exe (no DEVELOPMENT_BUILD;
#                               validation endpoints compiled out).
#   -Configuration Physics      FoundationBuild.WindowsPhysics -> Mono
#                               development player at
#                               Builds\Windows-Physics\FamilyRacing.exe
#                               booting the opt-in PhysicalAssisted race
#                               scene (validation endpoints compiled in).
#   -Configuration FamilyRelease FoundationBuild.WindowsFamilyRelease ->
#                               ordinary Mono player stamped with the
#                               release identity at
#                               Builds\Windows-FamilyRelease\FamilyRacing.exe
#                               (FullContent production gate runs first;
#                               validation endpoints compiled out).
#
# Evidence lands under Logs\windows-build\<flavor>\<run-id>\: the editor
# -logFile (unity.log), the child process stdout/stderr (unity-process.log),
# and result.json with the outcome and exe SHA-256.
param(
    [ValidateSet('Development', 'Ordinary', 'Physics', 'FamilyRelease', 'PlayfulChaos')]
    [string]$Configuration = 'Development',
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..'),
    [string]$EditorRoot = 'C:\Program Files\Unity\Hub\Editor'
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$editorVersion = '6000.3.26f1'
$editor = Join-Path $EditorRoot "$editorVersion\Editor\Unity.exe"
$flavors = @{
    playfulchaos = @{
        Method = 'FamilyRacing.EditorTools.FoundationBuild.WindowsPlayfulChaos'
        Exe = 'Builds\Windows-PlayfulChaos\FamilyRacing.exe'
    }
    development = @{
        Method = 'FamilyRacing.EditorTools.FoundationBuild.Windows'
        Exe = 'Builds\Windows\FamilyRacing.exe'
    }
    ordinary = @{
        Method = 'FamilyRacing.EditorTools.FoundationBuild.WindowsOrdinary'
        Exe = 'Builds\Windows-Ordinary\FamilyRacing.exe'
    }
    physics = @{
        Method = 'FamilyRacing.EditorTools.FoundationBuild.WindowsPhysics'
        Exe = 'Builds\Windows-Physics\FamilyRacing.exe'
    }
    familyrelease = @{
        Method = 'FamilyRacing.EditorTools.FoundationBuild.WindowsFamilyRelease'
        Exe = 'Builds\Windows-FamilyRelease\FamilyRacing.exe'
    }
}
$flavor = $Configuration.ToLowerInvariant()
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N')
$logs = Join-Path $project "Logs\windows-build\$flavor\$runId"
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$resultPath = Join-Path $logs 'result.json'
$result = [ordered]@{
    schemaVersion = 1
    runId = $runId
    status = 'failed'
    stage = 'prerequisites'
    configuration = $flavor
    startedUtc = [DateTime]::UtcNow.ToString('o')
    finishedUtc = $null
    editorVersion = $editorVersion
    editorPath = $editor
    executeMethod = $flavors[$flavor].Method
    unityLog = (Join-Path $logs 'unity.log')
    processLog = (Join-Path $logs 'unity-process.log')
    builtExe = $null
    exeSha256 = $null
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
    $standalone = Join-Path $EditorRoot "$editorVersion\Editor\Data\PlaybackEngines\WindowsStandaloneSupport"
    if (-not (Test-Path -LiteralPath $standalone -PathType Container)) {
        throw "Windows Build Support missing: $standalone. Add 'Windows Build Support (Mono)' to $editorVersion in Unity Hub."
    }

    $result.stage = 'build'
    Write-Host "Windows ($flavor): building; log: $($result.unityLog)"
    $unityArguments = @('-batchmode', '-quit', '-projectPath', $project,
        '-executeMethod', $flavors[$flavor].Method, '-logFile', $result.unityLog)
    $code = Invoke-BuildProcess $editor $unityArguments $project $result.processLog
    if ($code -ne 0) {
        $result.exitCode = $code
        throw "Unity build exited $code. See $($result.unityLog) and $($result.processLog)."
    }

    $result.stage = 'verify'
    $exe = Join-Path $project $flavors[$flavor].Exe
    Require-File $exe 'Player from this build'
    if ((Get-Item -LiteralPath $exe).Length -eq 0) { throw "Unity produced an empty exe: $exe" }
    $result.builtExe = $exe
    $result.exeSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    $result.stage = 'complete'
    $result.status = 'succeeded'
    $result.exitCode = 0
    Write-Host "Windows ($flavor) build succeeded: $exe"
}
catch {
    $result.error = $_.Exception.Message
    Write-Host "Windows ($flavor) build failed during $($result.stage): $($result.error)"
}
finally {
    $result.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resultPath -Encoding UTF8
    Write-Host "Windows build evidence: $resultPath"
}
exit $result.exitCode
