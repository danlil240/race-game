# Behavioral fixture for the family release signing pipeline:
# Tools/Sign-AndroidRelease.ps1 (zipalign -> apksigner sign -> verify ->
# metadata/certificate inspection -> atomic publish) and the
# -Configuration Release gate in Tools/Build-AndroidOffline.ps1 reached
# through Tools/Verify.ps1 -Mode AndroidFamilyRelease.
#
# Every keystore used here is a temporary test keystore generated into a
# fixture temp directory by this script; fixture APKs are never
# release-key-signed and never count as release evidence. Real Unity,
# Gradle, apksigner, zipalign and aapt are replaced by the compiled
# AndroidToolFixture.exe; process boundaries, filesystem checks,
# argument plumbing and exit propagation stay real.
#
# Breaks caught: missing signing env vars, wrong signer certificate,
# empty/pre-signed/debuggable input, sign/verify/zipalign failures,
# Gradle failure, Preview+Release attempts, stale package reuse after a
# failed run, and secrets leaking into logs or command lines.
param([string]$ToolsDir = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$passed = 0
$failed = 0
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('family-release-fixture-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$fixtureRoot = [IO.Path]::GetFullPath($fixtureRoot)
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null

$script:FixtureCert = '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'
$script:StoreSentinel = 'K5-T0RE-S3NT1NEL'
$script:KeySentinel = 'K3Y-S3NT1NEL'
$script:FixtureBadging = @(
    "package: name='com.daniel.familyracing' versionCode='1' versionName='1.0.0' platformBuildVersionName='16'",
    "sdkVersion:'25'",
    "targetSdkVersion:'36'",
    "application-label:'Family Racing'",
    "uses-permission: name='android.permission.INTERNET'",
    "native-code: 'arm64-v8a'"
) -join "`r`n"
$script:SignEnvVars = @('FAMILY_RACING_KEYSTORE', 'FAMILY_RACING_KEY_ALIAS',
    'FAMILY_RACING_KEYSTORE_PASSWORD', 'FAMILY_RACING_KEY_PASSWORD')

function Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
    if ($Condition) { $script:passed++; Write-Host "PASS $Name" }
    else { $script:failed++; Write-Host "FAIL $Name ($Detail)" }
}

function Clear-SignEnv() {
    foreach ($v in $script:SignEnvVars) {
        Remove-Item -LiteralPath "env:$v" -ErrorAction SilentlyContinue
    }
}

function Set-SignEnv([string]$KeystorePath, [string[]]$Omit = @()) {
    Clear-SignEnv
    foreach ($v in $script:SignEnvVars) {
        if ($Omit -contains $v) { continue }
        switch ($v) {
            'FAMILY_RACING_KEYSTORE'          { Set-Item -LiteralPath "env:$v" -Value $KeystorePath }
            'FAMILY_RACING_KEY_ALIAS'         { Set-Item -LiteralPath "env:$v" -Value 'fixture-alias' }
            'FAMILY_RACING_KEYSTORE_PASSWORD' { Set-Item -LiteralPath "env:$v" -Value $script:StoreSentinel }
            'FAMILY_RACING_KEY_PASSWORD'      { Set-Item -LiteralPath "env:$v" -Value $script:KeySentinel }
        }
    }
}

function Write-ZipEntry($Zip, [string]$Name, [string]$Content) {
    $entry = $Zip.CreateEntry($Name)
    $writer = New-Object IO.StreamWriter($entry.Open())
    try { $writer.Write($Content) } finally { $writer.Dispose() }
}

# Minimal ZIP posing as an APK: entry names are all the signing
# inspection consumes (META-INF signature files, lib/arm64-v8a).
function New-FixtureApk([string]$Path, [bool]$Arm64 = $true, [bool]$PreSigned = $false) {
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Write-ZipEntry $zip 'AndroidManifest.xml' 'fixture manifest'
        Write-ZipEntry $zip 'classes.dex' 'fixture dex'
        if ($Arm64) { Write-ZipEntry $zip 'lib/arm64-v8a/libil2cpp.so' 'fixture native' }
        if ($PreSigned) {
            Write-ZipEntry $zip 'META-INF/FIXTURE.SF' 'fixture signature file'
            Write-ZipEntry $zip 'META-INF/FIXTURE.RSA' 'fixture signature block'
        }
    }
    finally { $zip.Dispose() }
}

# Runs Sign-AndroidRelease.ps1 in a child process against fixture tools.
# Options: OmitEnv, NoKeystoreFile, EmptyApk, NotZip, NoArm64, PreSigned,
# ZipalignBehavior, AaptBehavior, SignerBehavior, CertDigest,
# ExpectedCert, Badging, NoMetadata, ExistingOutput.
function Run-SignCase([string]$Name, [hashtable]$Opt = @{}) {
    $dir = Join-Path $fixtureRoot ('sign-' + $Name)
    $tools = Join-Path $dir 'tools'
    New-Item -ItemType Directory -Path (Join-Path $tools 'lib') -Force | Out-Null
    foreach ($tool in @('zipalign.exe', 'aapt.exe', 'java.exe')) {
        Copy-Item -LiteralPath $fixtureExe -Destination (Join-Path $tools $tool)
    }
    Set-Content -LiteralPath (Join-Path $tools 'lib\apksigner.jar') -Value 'fixture jar'
    if ($Opt.ContainsKey('ZipalignBehavior')) {
        Set-Content -LiteralPath (Join-Path $tools 'zipalign-behavior.txt') -Value $Opt.ZipalignBehavior
    }
    if ($Opt.ContainsKey('AaptBehavior')) {
        Set-Content -LiteralPath (Join-Path $tools 'aapt-behavior.txt') -Value $Opt.AaptBehavior
    }
    if ($Opt.ContainsKey('SignerBehavior')) {
        Set-Content -LiteralPath (Join-Path $tools 'apksigner-behavior.txt') -Value $Opt.SignerBehavior
    }
    $certDigest = if ($Opt.ContainsKey('CertDigest')) { $Opt.CertDigest } else { $script:FixtureCert }
    Set-Content -LiteralPath (Join-Path $tools 'cert-sha256.txt') -Value $certDigest
    $badging = if ($Opt.ContainsKey('Badging')) { $Opt.Badging } else { $script:FixtureBadging }
    Set-Content -LiteralPath (Join-Path $tools 'badging.txt') -Value $badging

    $unsigned = Join-Path $dir 'unsigned.apk'
    if ($Opt.ContainsKey('EmptyApk')) {
        Set-Content -LiteralPath $unsigned -Value '' -NoNewline
    } elseif ($Opt.ContainsKey('NotZip')) {
        Set-Content -LiteralPath $unsigned -Value 'this is not a zip archive' -NoNewline
    } else {
        New-FixtureApk $unsigned -Arm64:(-not $Opt.ContainsKey('NoArm64')) -PreSigned:($Opt.ContainsKey('PreSigned'))
    }
    $keystore = Join-Path $dir 'test.keystore'
    if (-not $Opt.ContainsKey('NoKeystoreFile')) {
        Set-Content -LiteralPath $keystore -Value 'temporary fixture test keystore - not a real key'
    }
    $outputApk = Join-Path $dir 'out\signed.apk'
    if ($Opt.ContainsKey('ExistingOutput')) {
        New-Item -ItemType Directory -Path (Split-Path $outputApk) -Force | Out-Null
        Set-Content -LiteralPath $outputApk -Value $Opt.ExistingOutput -NoNewline
    }
    $omit = if ($Opt.ContainsKey('OmitEnv')) { [string[]]$Opt.OmitEnv } else { @() }
    Set-SignEnv -KeystorePath $keystore -Omit $omit

    $expected = if ($Opt.ContainsKey('ExpectedCert')) { $Opt.ExpectedCert } else { $certDigest }
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $signScript,
        '-UnsignedApk', $unsigned, '-OutputApk', $outputApk,
        '-ExpectedCertificateSha256', $expected,
        '-BuildToolsDir', $tools, '-JavaExe', (Join-Path $tools 'java.exe'))
    if (-not $Opt.ContainsKey('NoMetadata')) {
        $argList += @('-ExpectedApplicationId', 'com.daniel.familyracing',
            '-ExpectedVersionName', '1.0.0', '-ExpectedVersionCode', '1',
            '-ExpectedMinSdk', '25', '-ExpectedTargetSdk', '36', '-RequireArm64')
    }
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = & powershell.exe $argList 2>&1 | Out-String
    $code = $LASTEXITCODE
    $ErrorActionPreference = $oldPreference
    Clear-SignEnv
    return @{ Dir = $dir; Tools = $tools; ExitCode = $code; Output = $output;
              OutputApk = $outputApk; Unsigned = $unsigned; Keystore = $keystore }
}

# Full Verify -Mode AndroidFamilyRelease fixture: fake editor tree
# (Unity.exe + OpenJDK java.exe + Gradle launcher + SDK build-tools
# fixtures), fake project with Tools + Release/release.json, and a
# pre-seeded published APK that must survive every failure.
function Run-ReleaseCase([string]$Name, [string]$Behavior = 'success', [hashtable]$Opt = @{}) {
    $project = Join-Path $fixtureRoot ('rel-' + $Name)
    $fixtureTools = Join-Path $project 'Tools'
    New-Item -ItemType Directory -Path $fixtureTools -Force | Out-Null
    foreach ($toolScript in @('Verify.ps1', 'Build-AndroidOffline.ps1', 'Sign-AndroidRelease.ps1')) {
        $source = Join-Path $ToolsDir $toolScript
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $fixtureTools }
    }
    if (-not $Opt.ContainsKey('NoReleaseConfig')) {
        $releaseDir = Join-Path $project 'Release'
        New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
        $config = [ordered]@{
            schemaVersion = 1; versionName = '1.0.0'; androidVersionCode = 1
            previousAndroidVersionCode = 0; applicationId = 'com.daniel.familyracing'
            companyName = 'Family Racing'; productName = 'Family Racing'
            androidMinSdkVersion = 25; androidTargetSdkVersion = 36
            signingKeyAlias = 'fixture-alias'
            expectedCertificateSha256 = $script:FixtureCert
        }
        ($config | ConvertTo-Json) | Set-Content -LiteralPath (Join-Path $releaseDir 'release.json') -Encoding UTF8
    }
    $editorRoot = Join-Path $project 'Installed Editors'
    $editor = Join-Path $editorRoot '6000.3.26f1\Editor'
    $android = Join-Path $editor 'Data\PlaybackEngines\AndroidPlayer'
    $jdkBin = Join-Path $android 'OpenJDK\bin'
    $buildTools = Join-Path $android 'SDK\build-tools\36.0.0'
    foreach ($directory in @($editor, $jdkBin, (Join-Path $android 'Tools\gradle\lib'),
            (Join-Path $android 'SDK\platform-tools'), (Join-Path $android 'NDK'),
            (Join-Path $buildTools 'lib'))) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    Copy-Item -LiteralPath $fixtureExe -Destination (Join-Path $editor 'Unity.exe')
    Copy-Item -LiteralPath $fixtureExe -Destination (Join-Path $jdkBin 'java.exe')
    Copy-Item -LiteralPath $fixtureExe -Destination (Join-Path $buildTools 'zipalign.exe')
    Copy-Item -LiteralPath $fixtureExe -Destination (Join-Path $buildTools 'aapt.exe')
    Set-Content -LiteralPath (Join-Path $android 'Tools\gradle\lib\gradle-launcher-9.3.1.jar') -Value 'fixture'
    Set-Content -LiteralPath (Join-Path $android 'SDK\platform-tools\adb.exe') -Value 'fixture'
    Set-Content -LiteralPath (Join-Path $android 'NDK\ndk-build.cmd') -Value 'fixture'
    Set-Content -LiteralPath (Join-Path $buildTools 'lib\apksigner.jar') -Value 'fixture jar'
    Set-Content -LiteralPath (Join-Path $editor 'behavior.txt') -Value $Behavior
    Set-Content -LiteralPath (Join-Path $jdkBin 'behavior.txt') -Value $Behavior
    Set-Content -LiteralPath (Join-Path $jdkBin 'cert-sha256.txt') -Value $script:FixtureCert
    if ($Opt.ContainsKey('SignerBehavior')) {
        Set-Content -LiteralPath (Join-Path $jdkBin 'apksigner-behavior.txt') -Value $Opt.SignerBehavior
    }
    $badging = if ($Opt.ContainsKey('Badging')) { $Opt.Badging } else { $script:FixtureBadging }
    Set-Content -LiteralPath (Join-Path $buildTools 'badging.txt') -Value $badging
    if ($Opt.ContainsKey('AaptBehavior')) {
        Set-Content -LiteralPath (Join-Path $buildTools 'aapt-behavior.txt') -Value $Opt.AaptBehavior
    }
    $published = Join-Path $project 'Builds\Android-FamilyRelease\FamilyRacing.apk'
    New-Item -ItemType Directory -Path (Split-Path $published) -Force | Out-Null
    Set-Content -LiteralPath $published -Value 'previous valid package bytes' -NoNewline
    $keystore = Join-Path $project 'test.keystore'
    Set-Content -LiteralPath $keystore -Value 'temporary fixture test keystore - not a real key'
    $omit = if ($Opt.ContainsKey('OmitEnv')) { [string[]]$Opt.OmitEnv } else { @() }
    Set-SignEnv -KeystorePath $keystore -Omit $omit
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $fixtureTools 'Verify.ps1') `
        -Mode AndroidFamilyRelease -EditorRoot $editorRoot 2>&1 | Out-String
    $code = $LASTEXITCODE
    $ErrorActionPreference = $oldPreference
    Clear-SignEnv
    $results = @(Get-ChildItem -LiteralPath (Join-Path $project 'Logs') -Filter result.json -Recurse -File -ErrorAction SilentlyContinue)
    $result = if ($results.Count -eq 1) { Get-Content -LiteralPath $results[0].FullName -Raw | ConvertFrom-Json } else { $null }
    return @{ Project = $project; ExitCode = $code; Output = $output; Result = $result;
              Published = $published; Android = $android; JdkBin = $jdkBin; BuildTools = $buildTools }
}

try {
    $fixtureExe = Join-Path $fixtureRoot 'AndroidToolFixture.exe'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /target:exe /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll "/out:$fixtureExe" (Join-Path $ToolsDir 'Tests\AndroidToolFixture.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile external-process fixture.' }
    $signScript = Join-Path $ToolsDir 'Sign-AndroidRelease.ps1'

    # --- Sign-AndroidRelease.ps1 happy path -----------------------------
    $ok = Run-SignCase 'success'
    Check 'sign: valid unsigned APK signs, verifies and publishes' ($ok.ExitCode -eq 0 -and (Test-Path -LiteralPath $ok.OutputApk)) $ok.Output
    if (Test-Path -LiteralPath $ok.OutputApk) {
        Check 'sign: published bytes came through the signer' ((Get-Content -LiteralPath $ok.OutputApk -Raw).EndsWith('SIGNED-BY-FIXTURE'))
    } else { Check 'sign: published bytes came through the signer' $false 'no output' }
    Check 'sign: staging directory removed after success' (@(Get-ChildItem -LiteralPath (Split-Path $ok.OutputApk) -Directory -Filter '.staged-*' -ErrorAction SilentlyContinue).Count -eq 0)
    Check 'sign: signer certificate digest reported in output' ($ok.Output -match $script:FixtureCert) $ok.Output
    $apksignerArgs = ''
    $apksignerArgsPath = Join-Path $ok.Tools 'apksigner-arguments.txt'
    if (Test-Path -LiteralPath $apksignerArgsPath) { $apksignerArgs = (Get-Content -LiteralPath $apksignerArgsPath) -join ' ' }
    Check 'sign: passwords passed as env: references, never values' `
        ($apksignerArgs -match 'env:FAMILY_RACING_KEYSTORE_PASSWORD' -and $apksignerArgs -match 'env:FAMILY_RACING_KEY_PASSWORD') $apksignerArgs
    Check 'sign: keystore password sentinel never reaches command line' ($apksignerArgs -notmatch [regex]::Escape($script:StoreSentinel)) $apksignerArgs
    Check 'sign: key password sentinel never reaches command line' ($apksignerArgs -notmatch [regex]::Escape($script:KeySentinel)) $apksignerArgs
    Check 'sign: console output carries no secret values' `
        ($ok.Output -notmatch [regex]::Escape($script:StoreSentinel) -and $ok.Output -notmatch [regex]::Escape($script:KeySentinel)) $ok.Output

    # --- missing key variables -------------------------------------------
    foreach ($var in $script:SignEnvVars) {
        $case = Run-SignCase ('missing-' + $var.Replace('_', '-').ToLower()) @{ OmitEnv = @($var) }
        Check "sign: missing $var fails and names the variable" `
            ($case.ExitCode -ne 0 -and $case.Output -match $var -and -not (Test-Path -LiteralPath $case.OutputApk)) `
            ("exit=" + $case.ExitCode + " output=" + $case.Output)
    }
    $case = Run-SignCase 'missing-keystore-file' @{ NoKeystoreFile = $true }
    Check 'sign: missing keystore file fails' ($case.ExitCode -ne 0 -and $case.Output -match 'keystore') $case.Output

    # --- input and verification failures ----------------------------------
    $case = Run-SignCase 'wrong-cert' @{ ExpectedCert = ('f' * 64) }
    Check 'sign: wrong signer certificate is rejected' ($case.ExitCode -ne 0 -and $case.Output -match 'certificate') $case.Output
    Check 'sign: wrong certificate leaves no output package' (-not (Test-Path -LiteralPath $case.OutputApk))
    $case = Run-SignCase 'empty-apk' @{ EmptyApk = $true }
    Check 'sign: empty unsigned APK is rejected' ($case.ExitCode -ne 0 -and $case.Output -match 'empty') $case.Output
    $case = Run-SignCase 'not-zip' @{ NotZip = $true }
    Check 'sign: non-ZIP input is rejected' ($case.ExitCode -ne 0) $case.Output
    $case = Run-SignCase 'pre-signed' @{ PreSigned = $true }
    Check 'sign: already-signed input is rejected (debug-key fallback)' ($case.ExitCode -ne 0 -and $case.Output -match 'signed') $case.Output
    $case = Run-SignCase 'sign-failure' @{ SignerBehavior = 'sign-failure' }
    Check 'sign: apksigner sign failure propagates' ($case.ExitCode -ne 0 -and -not (Test-Path -LiteralPath $case.OutputApk)) $case.Output
    $case = Run-SignCase 'verify-failure' @{ SignerBehavior = 'verify-failure' }
    Check 'sign: apksigner verify failure propagates' ($case.ExitCode -ne 0 -and -not (Test-Path -LiteralPath $case.OutputApk)) $case.Output
    $case = Run-SignCase 'zipalign-failure' @{ ZipalignBehavior = 'zipalign-failure' }
    Check 'sign: zipalign failure propagates' ($case.ExitCode -ne 0 -and -not (Test-Path -LiteralPath $case.OutputApk)) $case.Output

    # --- metadata inspection ----------------------------------------------
    $debugBadging = $script:FixtureBadging + "`r`napplication-debuggable"
    $case = Run-SignCase 'debuggable' @{ Badging = $debugBadging }
    Check 'sign: debuggable APK is rejected' ($case.ExitCode -ne 0 -and $case.Output -match 'debuggable') $case.Output
    $wrongIdBadging = $script:FixtureBadging -replace 'com\.daniel\.familyracing', 'com.example.other'
    $case = Run-SignCase 'wrong-id' @{ Badging = $wrongIdBadging }
    Check 'sign: wrong applicationId is rejected' ($case.ExitCode -ne 0 -and -not (Test-Path -LiteralPath $case.OutputApk)) $case.Output
    $case = Run-SignCase 'no-arm64' @{ NoArm64 = $true }
    Check 'sign: missing ARM64 payload is rejected' ($case.ExitCode -ne 0 -and $case.Output -match 'arm64') $case.Output

    # --- stale output reuse -------------------------------------------------
    $case = Run-SignCase 'stale' @{ ExpectedCert = ('f' * 64); ExistingOutput = 'previous valid package bytes' }
    Check 'sign: failed run never overwrites a previous valid package' `
        ($case.ExitCode -ne 0 -and (Get-Content -LiteralPath $case.OutputApk -Raw) -eq 'previous valid package bytes') $case.Output

    # --- Build-AndroidOffline -Configuration Release ------------------------
    $rel = Run-ReleaseCase 'success'
    Check 'release: Verify -Mode AndroidFamilyRelease completes end-to-end' ($rel.ExitCode -eq 0) $rel.Output
    Check 'release: signed APK published under Builds\Android-FamilyRelease' `
        ($rel.Published -eq (Join-Path $rel.Project 'Builds\Android-FamilyRelease\FamilyRacing.apk') -and
         (Get-Content -LiteralPath $rel.Published -Raw).EndsWith('SIGNED-BY-FIXTURE')) ("published=" + $rel.Published)
    Check 'release: result records FullContent/Release and the release export method' `
        ($null -ne $rel.Result -and $rel.Result.status -eq 'succeeded' -and
         $rel.Result.presentation -eq 'FullContent' -and $rel.Result.configuration -eq 'Release' -and
         $rel.Result.buildFlavor -eq 'Android-FamilyRelease' -and
         $rel.Result.exportMethod -eq 'FamilyRacing.EditorTools.FoundationBuild.ExportAndroidFamilyRelease') `
        ($rel.Result | ConvertTo-Json -Compress)
    $relJavaArgs = @()
    if ($null -ne $rel.Result -and (Test-Path -LiteralPath (Join-Path $rel.Result.exportPath 'java-arguments.txt'))) {
        $relJavaArgs = @(Get-Content -LiteralPath (Join-Path $rel.Result.exportPath 'java-arguments.txt'))
    }
    Check 'release: Gradle builds the unsigned release task offline' `
        ($relJavaArgs -contains ':launcher:assembleRelease' -and $relJavaArgs -notcontains ':launcher:assembleDebug' -and $relJavaArgs -contains '--offline') ($relJavaArgs -join ' ')
    Check 'release: sign log contains no secret values' `
        ($null -ne $rel.Result -and (Test-Path -LiteralPath $rel.Result.signLog) -and
         (Get-Content -LiteralPath $rel.Result.signLog -Raw) -notmatch ([regex]::Escape($script:StoreSentinel) + '|' + [regex]::Escape($script:KeySentinel))) 'sign.log leaked a secret'

    $rel = Run-ReleaseCase 'gradle-failure' 'dependency-missing'
    Check 'release: Gradle failure propagates and keeps the previous package' `
        ($rel.ExitCode -ne 0 -and (Get-Content -LiteralPath $rel.Published -Raw) -eq 'previous valid package bytes') ("exit=" + $rel.ExitCode)
    $rel = Run-ReleaseCase 'presigned' 'presigned-gradle'
    Check 'release: pre-signed Gradle output is rejected before signing' `
        ($rel.ExitCode -ne 0 -and $rel.Output -match 'signing config|pre-signed' -and
         (Get-Content -LiteralPath $rel.Published -Raw) -eq 'previous valid package bytes') $rel.Output
    $rel = Run-ReleaseCase 'missing-env' 'success' @{ OmitEnv = @('FAMILY_RACING_KEYSTORE_PASSWORD') }
    Check 'release: missing signing secret fails at the sign stage' `
        ($rel.ExitCode -ne 0 -and $rel.Output -match 'FAMILY_RACING_KEYSTORE_PASSWORD' -and
         (Get-Content -LiteralPath $rel.Published -Raw) -eq 'previous valid package bytes') $rel.Output
    $rel = Run-ReleaseCase 'sign-failure' 'success' @{ SignerBehavior = 'sign-failure' }
    Check 'release: signer failure keeps the previous package' `
        ($rel.ExitCode -ne 0 -and (Get-Content -LiteralPath $rel.Published -Raw) -eq 'previous valid package bytes') $rel.Output
    $rel = Run-ReleaseCase 'debuggable' 'success' @{ Badging = $debugBadging }
    Check 'release: debuggable artifact is refused' `
        ($rel.ExitCode -ne 0 -and (Get-Content -LiteralPath $rel.Published -Raw) -eq 'previous valid package bytes') $rel.Output

    # --- attempted Preview/Other + Release ---------------------------------
    $helperScript = Join-Path $ToolsDir 'Build-AndroidOffline.ps1'
    foreach ($presentation in @('Graybox', 'VisualBenchmark', 'FullContentPreview')) {
        $oldPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $rejectOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $helperScript `
            -ProjectPath $fixtureRoot -EditorRoot (Join-Path $fixtureRoot 'no-editor') `
            -Presentation $presentation -Configuration Release 2>&1 | Out-String
        $rejectCode = $LASTEXITCODE
        $ErrorActionPreference = $oldPreference
        Check "release: $presentation+Release is refused before any build step" `
            ($rejectCode -ne 0 -and $rejectOutput -match 'Release.*FullContent|FullContent.*Release') `
            ("exit=$rejectCode output=$rejectOutput")
    }
}
finally {
    Clear-SignEnv
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($fixtureRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path $fixtureRoot -Leaf) -match '^family-release-fixture-[0-9a-f]{8}$') {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    } else { throw "Refusing fixture cleanup outside temp: $fixtureRoot" }
}
Write-Host "RESULT: $passed passed, $failed failed."
exit $(if ($failed -eq 0) { 0 } else { 1 })
