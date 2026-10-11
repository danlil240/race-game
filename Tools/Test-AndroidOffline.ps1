# Behavioral fixture: launches fake external tools, never Unity or Gradle.
# Breaks caught: losing --offline, corrupt path quoting, ignored exit codes,
# stale APK publication, missing-tool fallthrough, and incomplete exports.
param([string]$VerifyScript = (Join-Path $PSScriptRoot 'Verify.ps1'))

$ErrorActionPreference = 'Stop'
$passed = 0
$failed = 0
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('android-fixture-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$fixtureRoot = [IO.Path]::GetFullPath($fixtureRoot)
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null

function Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
    if ($Condition) { $script:passed++; Write-Host "PASS $Name" }
    else { $script:failed++; Write-Host "FAIL $Name ($Detail)" }
}

function Run-Case([string]$Name, [string]$Behavior = 'success', [string]$Missing = '') {
    $project = Join-Path $fixtureRoot ($Name + ' [test] & spaces')
    $toolsDir = Join-Path $project 'Tools'
    New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
    Copy-Item -LiteralPath $VerifyScript -Destination (Join-Path $toolsDir 'Verify.ps1')
    $helper = Join-Path (Split-Path $VerifyScript) 'Build-AndroidOffline.ps1'
    if (Test-Path -LiteralPath $helper) {
        Copy-Item -LiteralPath $helper -Destination $toolsDir
    }
    $editorRoot = Join-Path $project 'Installed Editors'
    $editor = Join-Path $editorRoot '6000.3.26f1\Editor'
    $android = Join-Path $editor 'Data\PlaybackEngines\AndroidPlayer'
    foreach ($directory in @($editor, (Join-Path $android 'OpenJDK\bin'),
            (Join-Path $android 'Tools\gradle\lib'), (Join-Path $android 'SDK\platform-tools'),
            (Join-Path $android 'NDK'))) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    if ($Missing -ne 'editor') { Copy-Item -LiteralPath $fixtureExe -Destination (Join-Path $editor 'Unity.exe') }
    if ($Missing -ne 'java') { Copy-Item -LiteralPath $fixtureExe -Destination (Join-Path $android 'OpenJDK\bin\java.exe') }
    if ($Missing -ne 'gradle') {
        Set-Content -LiteralPath (Join-Path $android 'Tools\gradle\lib\gradle-launcher-9.3.1.jar') -Value 'fixture'
    }
    Set-Content -LiteralPath (Join-Path $android 'SDK\platform-tools\adb.exe') -Value 'fixture'
    Set-Content -LiteralPath (Join-Path $android 'NDK\ndk-build.cmd') -Value 'fixture'
    Set-Content -LiteralPath (Join-Path $editor 'behavior.txt') -Value $Behavior
    Set-Content -LiteralPath (Join-Path $android 'OpenJDK\bin\behavior.txt') -Value $Behavior
    $published = Join-Path $project 'Builds\Android\FamilyRacing.apk'
    New-Item -ItemType Directory -Path (Split-Path $published) -Force | Out-Null
    Set-Content -LiteralPath $published -Value 'old APK remains on failure' -NoNewline
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $toolsDir 'Verify.ps1') -Mode Android -EditorRoot $editorRoot 2>&1 | Out-String
    $code = $LASTEXITCODE
    $ErrorActionPreference = $oldPreference
    $results = @(Get-ChildItem -LiteralPath (Join-Path $project 'Logs') -Filter result.json -Recurse -File -ErrorAction SilentlyContinue)
    $result = if ($results.Count -eq 1) { Get-Content -LiteralPath $results[0].FullName -Raw | ConvertFrom-Json } else { $null }
    return @{ Project = $project; ExitCode = $code; Output = $output; Result = $result; Published = $published; Android = $android }
}

try {
    $fixtureExe = Join-Path $fixtureRoot 'AndroidToolFixture.exe'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /target:exe "/out:$fixtureExe" (Join-Path $PSScriptRoot 'Tests\AndroidToolFixture.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile external-process fixture.' }

    $ok = Run-Case 'success'
    Check 'Android mode completes export and offline build with paths containing spaces and brackets' ($ok.ExitCode -eq 0) $ok.Output
    Check 'publishes only fresh APK from this export' ((Get-Content -LiteralPath $ok.Published -Raw) -eq 'fresh fixture APK')
    Check 'writes successful build evidence' ($null -ne $ok.Result -and $ok.Result.status -eq 'succeeded')
    $javaArguments = if ($null -ne $ok.Result -and (Test-Path -LiteralPath (Join-Path $ok.Result.exportPath 'java-arguments.txt'))) {
        @(Get-Content -LiteralPath (Join-Path $ok.Result.exportPath 'java-arguments.txt'))
    } else { @() }
    Check 'Gradle receives offline and bounded local-tool arguments' ($javaArguments -contains '--offline' -and $javaArguments -contains '--no-daemon' -and $javaArguments -contains '-Dorg.gradle.java.installations.auto-download=false' -and $javaArguments -contains '-Pandroid.builder.sdkDownload=false')
    $expectedJar = Join-Path $ok.Android 'Tools\gradle\lib\gradle-launcher-9.3.1.jar'
    Check 'Gradle uses bundled launcher JAR as one intact argument' ($javaArguments -contains $expectedJar)
    Check 'export and Gradle logs are distinct and preserved' ($null -ne $ok.Result -and $ok.Result.exportLog -ne $ok.Result.gradleLog -and (Test-Path -LiteralPath $ok.Result.exportLog) -and (Test-Path -LiteralPath $ok.Result.gradleLog))

    foreach ($missing in @('editor', 'java', 'gradle')) {
        $case = Run-Case ('missing-' + $missing) 'success' $missing
        Check "missing $missing fails before export" ($case.ExitCode -ne 0 -and -not (Test-Path -LiteralPath (Join-Path $case.Project 'unity-arguments.txt')))
    }
    $exportFailure = Run-Case 'export-failure' 'export-failure'
    Check 'propagates export process exit code' ($exportFailure.ExitCode -eq 29) ("exit=" + $exportFailure.ExitCode)
    Check 'failed export cannot replace prior APK' ((Get-Content -LiteralPath $exportFailure.Published -Raw) -eq 'old APK remains on failure')
    $incomplete = Run-Case 'incomplete-export' 'incomplete-export'
    Check 'zero export exit without Gradle project fails' ($incomplete.ExitCode -ne 0 -and $null -ne $incomplete.Result -and $incomplete.Result.stage -eq 'export')
    $dependency = Run-Case 'dependency-missing' 'dependency-missing'
    Check 'propagates missing offline dependency exit code' ($dependency.ExitCode -eq 31) ("exit=" + $dependency.ExitCode)
    Check 'missing dependency diagnostics remain in Gradle log' ($null -ne $dependency.Result -and (Test-Path -LiteralPath $dependency.Result.gradleLog) -and (Get-Content -LiteralPath $dependency.Result.gradleLog -Raw) -match 'No cached version')
    foreach ($behavior in @('no-apk', 'empty-apk')) {
        $case = Run-Case $behavior $behavior
        Check "$behavior fails without publishing stale APK" ($case.ExitCode -ne 0 -and (Get-Content -LiteralPath $case.Published -Raw) -eq 'old APK remains on failure')
    }
}
finally {
    # This script owns exactly its GUID fixture directory, never a repository.
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($fixtureRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path $fixtureRoot -Leaf) -match '^android-fixture-[0-9a-f]{8}$') {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    } else { throw "Refusing fixture cleanup outside temp: $fixtureRoot" }
}
Write-Host "RESULT: $passed passed, $failed failed."
exit $(if ($failed -eq 0) { 0 } else { 1 })
