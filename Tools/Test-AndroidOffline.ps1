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

function Flavor-For-Mode([string]$Mode) {
    switch ($Mode) {
        'AndroidVisualBenchmark'         { return 'Android-VisualBenchmark' }
        'AndroidVisualBenchmarkOrdinary' { return 'Android-VisualBenchmark-Ordinary' }
        'AndroidOrdinary'                { return 'Android-Ordinary' }
        'AndroidFullContent'             { return 'Android-FullContent' }
        'AndroidFullContentOrdinary'     { return 'Android-FullContent-Ordinary' }
        'AndroidFullContentPreview'      { return 'Android-FullContent-Preview' }
        default                          { return 'Android' }
    }
}

function Run-Case([string]$Name, [string]$Behavior = 'success', [string]$Missing = '', [string]$Mode = 'Android') {
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
    $published = Join-Path $project ('Builds\' + (Flavor-For-Mode $Mode) + '\FamilyRacing.apk')
    New-Item -ItemType Directory -Path (Split-Path $published) -Force | Out-Null
    Set-Content -LiteralPath $published -Value 'old APK remains on failure' -NoNewline
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $toolsDir 'Verify.ps1') -Mode $Mode -EditorRoot $editorRoot 2>&1 | Out-String
    $code = $LASTEXITCODE
    $ErrorActionPreference = $oldPreference
    $results = @(Get-ChildItem -LiteralPath (Join-Path $project 'Logs') -Filter result.json -Recurse -File -ErrorAction SilentlyContinue)
    $result = if ($results.Count -eq 1) { Get-Content -LiteralPath $results[0].FullName -Raw | ConvertFrom-Json } else { $null }
    return @{ Project = $project; ExitCode = $code; Output = $output; Result = $result; Published = $published; Android = $android }
}

try {
    $fixtureExe = Join-Path $fixtureRoot 'AndroidToolFixture.exe'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /target:exe /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "/out:$fixtureExe" (Join-Path $PSScriptRoot 'Tests\AndroidToolFixture.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile external-process fixture.' }

    $ok = Run-Case 'success'
    Check 'Android mode completes export and offline build with paths containing spaces and brackets' ($ok.ExitCode -eq 0) $ok.Output
    Check 'publishes only fresh APK from this export' ((Get-Content -LiteralPath $ok.Published -Raw) -eq 'fresh fixture APK')
    Check 'writes successful build evidence' ($null -ne $ok.Result -and $ok.Result.status -eq 'succeeded')
    $javaArguments = if ($null -ne $ok.Result -and (Test-Path -LiteralPath (Join-Path $ok.Result.exportPath 'java-arguments.txt'))) {
        @(Get-Content -LiteralPath (Join-Path $ok.Result.exportPath 'java-arguments.txt'))
    } else { @() }
    Check 'Gradle receives offline and bounded local-tool arguments' ($javaArguments -contains '--offline' -and $javaArguments -contains '--no-daemon' -and $javaArguments -contains '-Dorg.gradle.java.installations.auto-download=false' -and $javaArguments -contains '-Pandroid.builder.sdkDownload=false')
    # Development and Ordinary must keep building the debug task; the
    # signed release path is a separate -Configuration Release flavor.
    Check 'development build keeps the debug Gradle task' ($javaArguments -contains ':launcher:assembleDebug' -and $javaArguments -notcontains ':launcher:assembleRelease') ($javaArguments -join ' ')
    $expectedJar = Join-Path $ok.Android 'Tools\gradle\lib\gradle-launcher-9.3.1.jar'
    Check 'Gradle uses bundled launcher JAR as one intact argument' ($javaArguments -contains $expectedJar)
    Check 'export and Gradle logs are distinct and preserved' ($null -ne $ok.Result -and $ok.Result.exportLog -ne $ok.Result.gradleLog -and (Test-Path -LiteralPath $ok.Result.exportLog) -and (Test-Path -LiteralPath $ok.Result.gradleLog))
    # The visual-benchmark flavors must not move the defaults: -Mode Android
    # still selects the graybox development export under Builds\Android.
    $defaultUnityArgs = @()
    if (Test-Path -LiteralPath (Join-Path $ok.Project 'unity-arguments.txt')) {
        $defaultUnityArgs = @(Get-Content -LiteralPath (Join-Path $ok.Project 'unity-arguments.txt'))
    }
    $defaultMethodIndex = [array]::IndexOf($defaultUnityArgs, '-executeMethod')
    Check 'default Android mode keeps the graybox development export entry point' ($defaultMethodIndex -ge 0 -and $defaultMethodIndex + 1 -lt $defaultUnityArgs.Count -and $defaultUnityArgs[$defaultMethodIndex + 1] -eq 'FamilyRacing.EditorTools.FoundationBuild.ExportAndroid') ($defaultUnityArgs -join ' ')
    $defaultExportIndex = [array]::IndexOf($defaultUnityArgs, '--android-export-dir')
    Check 'default export dir stays under Builds\Android\offline' ($defaultExportIndex -ge 0 -and $defaultExportIndex + 1 -lt $defaultUnityArgs.Count -and $defaultUnityArgs[$defaultExportIndex + 1].Replace('/', '\').StartsWith((Join-Path $ok.Project 'Builds\Android\offline') + '\')) ($defaultUnityArgs -join ' ')
    Check 'default run records graybox development defaults' ($null -ne $ok.Result -and $ok.Result.presentation -eq 'Graybox' -and $ok.Result.configuration -eq 'Development') ($ok.Result | ConvertTo-Json -Compress)

    # Visual-benchmark flavors: matching export entry points, ordinary
    # flag carried by the entry point name, flavor-scoped offline roots
    # and published APK paths.
    foreach ($case in @(
        @{ Name = 'vb-dev'; Mode = 'AndroidVisualBenchmark';         Method = 'FamilyRacing.EditorTools.FoundationBuild.ExportAndroidVisualBenchmark';         Flavor = 'Android-VisualBenchmark';         Configuration = 'Development' },
        @{ Name = 'vb-ord'; Mode = 'AndroidVisualBenchmarkOrdinary'; Method = 'FamilyRacing.EditorTools.FoundationBuild.ExportAndroidVisualBenchmarkOrdinary'; Flavor = 'Android-VisualBenchmark-Ordinary'; Configuration = 'Ordinary' })) {
        $vb = Run-Case $case.Name 'success' '' $case.Mode
        Check "$($case.Mode) completes export and offline build" ($vb.ExitCode -eq 0) $vb.Output
        Check "$($case.Mode) publishes fresh APK under Builds\$($case.Flavor)" ($vb.Published -eq (Join-Path $vb.Project ('Builds\' + $case.Flavor + '\FamilyRacing.apk')) -and (Get-Content -LiteralPath $vb.Published -Raw) -eq 'fresh fixture APK') ("published=" + $vb.Published)
        $vbUnityArgs = @()
        if (Test-Path -LiteralPath (Join-Path $vb.Project 'unity-arguments.txt')) {
            $vbUnityArgs = @(Get-Content -LiteralPath (Join-Path $vb.Project 'unity-arguments.txt'))
        }
        $vbMethodIndex = [array]::IndexOf($vbUnityArgs, '-executeMethod')
        Check "$($case.Mode) selects $($case.Method)" ($vbMethodIndex -ge 0 -and $vbMethodIndex + 1 -lt $vbUnityArgs.Count -and $vbUnityArgs[$vbMethodIndex + 1] -eq $case.Method) ($vbUnityArgs -join ' ')
        $vbExportIndex = [array]::IndexOf($vbUnityArgs, '--android-export-dir')
        Check "$($case.Mode) export dir stays under Builds\$($case.Flavor)\offline" ($vbExportIndex -ge 0 -and $vbExportIndex + 1 -lt $vbUnityArgs.Count -and $vbUnityArgs[$vbExportIndex + 1].Replace('/', '\').StartsWith((Join-Path $vb.Project ('Builds\' + $case.Flavor + '\offline')) + '\')) ($vbUnityArgs -join ' ')
        Check "$($case.Mode) result records VisualBenchmark/$($case.Configuration)" ($null -ne $vb.Result -and $vb.Result.presentation -eq 'VisualBenchmark' -and $vb.Result.configuration -eq $case.Configuration) ($vb.Result | ConvertTo-Json -Compress)
    }
    $vbFail = Run-Case 'vb-export-failure' 'export-failure' '' 'AndroidVisualBenchmark'
    Check 'benchmark export failure propagates exit and keeps stale APK' ($vbFail.ExitCode -eq 29 -and (Get-Content -LiteralPath $vbFail.Published -Raw) -eq 'old APK remains on failure') ("exit=" + $vbFail.ExitCode)

    # Full-content flavors (M6 Task 4): matching export entry points,
    # flavor-scoped offline roots and published APK paths. The preview is
    # development-only and records FullContentPreview/Development.
    foreach ($case in @(
        @{ Name = 'fc-dev';  Mode = 'AndroidFullContent';         Method = 'FamilyRacing.EditorTools.FoundationBuild.ExportAndroidFullContent';         Flavor = 'Android-FullContent';         Configuration = 'Development'; Presentation = 'FullContent' },
        @{ Name = 'fc-ord';  Mode = 'AndroidFullContentOrdinary'; Method = 'FamilyRacing.EditorTools.FoundationBuild.ExportAndroidFullContentOrdinary'; Flavor = 'Android-FullContent-Ordinary'; Configuration = 'Ordinary';    Presentation = 'FullContent' },
        @{ Name = 'fc-prev'; Mode = 'AndroidFullContentPreview';  Method = 'FamilyRacing.EditorTools.FoundationBuild.ExportAndroidFullContentPreview';  Flavor = 'Android-FullContent-Preview';  Configuration = 'Development'; Presentation = 'FullContentPreview' })) {
        $fc = Run-Case $case.Name 'success' '' $case.Mode
        Check "$($case.Mode) completes export and offline build" ($fc.ExitCode -eq 0) $fc.Output
        Check "$($case.Mode) publishes fresh APK under Builds\$($case.Flavor)" ($fc.Published -eq (Join-Path $fc.Project ('Builds\' + $case.Flavor + '\FamilyRacing.apk')) -and (Get-Content -LiteralPath $fc.Published -Raw) -eq 'fresh fixture APK') ("published=" + $fc.Published)
        $fcUnityArgs = @()
        if (Test-Path -LiteralPath (Join-Path $fc.Project 'unity-arguments.txt')) {
            $fcUnityArgs = @(Get-Content -LiteralPath (Join-Path $fc.Project 'unity-arguments.txt'))
        }
        $fcMethodIndex = [array]::IndexOf($fcUnityArgs, '-executeMethod')
        Check "$($case.Mode) selects $($case.Method)" ($fcMethodIndex -ge 0 -and $fcMethodIndex + 1 -lt $fcUnityArgs.Count -and $fcUnityArgs[$fcMethodIndex + 1] -eq $case.Method) ($fcUnityArgs -join ' ')
        $fcExportIndex = [array]::IndexOf($fcUnityArgs, '--android-export-dir')
        Check "$($case.Mode) export dir stays under Builds\$($case.Flavor)\offline" ($fcExportIndex -ge 0 -and $fcExportIndex + 1 -lt $fcUnityArgs.Count -and $fcUnityArgs[$fcExportIndex + 1].Replace('/', '\').StartsWith((Join-Path $fc.Project ('Builds\' + $case.Flavor + '\offline')) + '\')) ($fcUnityArgs -join ' ')
        Check "$($case.Mode) result records $($case.Presentation)/$($case.Configuration)" ($null -ne $fc.Result -and $fc.Result.presentation -eq $case.Presentation -and $fc.Result.configuration -eq $case.Configuration) ($fc.Result | ConvertTo-Json -Compress)
    }
    # An ordinary preview is refused outright: a partial-content build can
    # never pose as an accepted or ordinary full-content candidate.
    $helperScript = Join-Path (Split-Path $VerifyScript) 'Build-AndroidOffline.ps1'
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $rejectOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $helperScript `
        -ProjectPath $fixtureRoot -EditorRoot (Join-Path $fixtureRoot 'no-editor') `
        -Presentation FullContentPreview -Configuration Ordinary 2>&1 | Out-String
    $rejectCode = $LASTEXITCODE
    $ErrorActionPreference = $oldPreference
    Check 'FullContentPreview+Ordinary is refused before any build step' ($rejectCode -ne 0 -and $rejectOutput -match 'development-only') ("exit=$rejectCode output=$rejectOutput")

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
