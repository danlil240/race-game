# Tooling regression fixture for Tools/Verify.ps1 CheckEnvironment and the
# offline runner's -NunitFrameworkPath handling. Builds its own fake editor
# layout under %TEMP%; no Unity, .NET restore, or network required.
# Exits 0 when every check passes, 1 otherwise.
param(
    [string]$VerifyScript = (Join-Path $PSScriptRoot "Verify.ps1"),
    [string]$RunnerScript = (Join-Path $PSScriptRoot "OfflineTestRunner\Run-OfflineTests.ps1")
)

$ErrorActionPreference = "Continue"
$pwsh = "powershell"
$passed = 0
$failed = 0

function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) {
        $script:passed++
        Write-Host "PASS $name"
    } else {
        $script:failed++
        Write-Host "FAIL $name  ($detail)"
    }
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ("verify-fixture-" + [guid]::NewGuid().ToString("N"))
$editorRoot = Join-Path $temp "Editor"
New-Item -ItemType Directory -Force -Path $editorRoot | Out-Null

# 1. Pinned editor absent -> nonzero, reported missing.
$output = & $pwsh -NoProfile -File $VerifyScript -Mode CheckEnvironment -EditorRoot $editorRoot 2>&1 | Out-String
Check "absent pinned editor exits nonzero" ($LASTEXITCODE -ne 0) "exit=$LASTEXITCODE"
Check "absent pinned editor reported missing" ($output -match "MISSING.*Pinned editor") "output missing report line"

# 2. Editor with generic il2cpp dir + bare AndroidPlayer (no SDK/NDK/JDK):
#    Windows IL2CPP and Android SDK/NDK/JDK must all report MISSING.
$editorDir = Join-Path $editorRoot "6000.3.26f1"
New-Item -ItemType File -Force -Path (Join-Path $editorDir "Editor\Unity.exe") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $editorDir "Editor\Data\il2cpp") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $editorDir "Editor\Data\PlaybackEngines\AndroidPlayer") | Out-Null
$output = & $pwsh -NoProfile -File $VerifyScript -Mode CheckEnvironment -EditorRoot $editorRoot 2>&1 | Out-String
Check "generic il2cpp dir still exits nonzero" ($LASTEXITCODE -ne 0) "exit=$LASTEXITCODE"
Check "generic il2cpp dir does not count as Windows IL2CPP" ($output -match "MISSING.*IL2CPP") "Windows IL2CPP wrongly accepted"
Check "bare AndroidPlayer still reports missing SDK" ($output -match "MISSING.*platform-tools|MISSING.*SDK") "Android SDK wrongly accepted"
Check "bare AndroidPlayer still reports missing NDK" ($output -match "MISSING.*NDK") "Android NDK wrongly accepted"
Check "bare AndroidPlayer still reports missing OpenJDK" ($output -match "MISSING.*JDK|MISSING.*OpenJDK") "Android OpenJDK wrongly accepted"

# 3. Complete fake layout -> exit 0 with scoped success wording.
New-Item -ItemType Directory -Force -Path (Join-Path $editorDir "Editor\Data\PlaybackEngines\WindowsStandaloneSupport\Variations\win64_player_il2cpp") | Out-Null
New-Item -ItemType File -Force -Path (Join-Path $editorDir "Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe") | Out-Null
New-Item -ItemType File -Force -Path (Join-Path $editorDir "Editor\Data\PlaybackEngines\AndroidPlayer\NDK\ndk-build.cmd") | Out-Null
New-Item -ItemType File -Force -Path (Join-Path $editorDir "Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK\bin\java.exe") | Out-Null
$output = & $pwsh -NoProfile -File $VerifyScript -Mode CheckEnvironment -EditorRoot $editorRoot 2>&1 | Out-String
Check "complete fake layout exits zero" ($LASTEXITCODE -eq 0) "exit=$LASTEXITCODE output: $output"
Check "success wording stays scoped" ($output -match "checked file prerequisites") "success wording wrong"
Check "no overclaimed 'all prerequisites'" ($output -notmatch "all prerequisites present") "overclaim present"

# 4. Invalid explicit -NunitFrameworkPath -> nonzero, suite not run.
$missingDll = Join-Path $temp "does-not-exist-nunit.framework.dll"
$output = & $pwsh -NoProfile -File $RunnerScript -NunitFrameworkPath $missingDll 2>&1 | Out-String
Check "invalid explicit NUnit path exits nonzero" ($LASTEXITCODE -ne 0) "exit=$LASTEXITCODE"
Check "suite not run on invalid NUnit path" ($output -notmatch "RESULT:") "suite ran anyway"

Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "RESULT: $passed passed, $failed failed."
exit $(if ($failed -eq 0) { 0 } else { 1 })
