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

# Builds a fake ordinary Windows player tree under $Dir. Opt flags
# remove/plant pieces so the assembly gate's failure classes can fire:
# MissingExe, MissingData, MissingRuntimeDll, MissingMono, NoManifest,
# ManifestStage, ManifestDriving, ManifestContent, StaleManifest,
# PlantProfile, PlantKeystore, PlantToken, PlantDoNotShip.
function New-FixtureWindowsPlayer([string]$Dir, [hashtable]$Opt = @{}) {
    $data = Join-Path $Dir 'FamilyRacing_Data'
    $managed = Join-Path $data 'Managed'
    foreach ($d in @($managed, (Join-Path $data 'Resources'),
        (Join-Path $Dir 'MonoBleedingEdge\etc\mono\4.5'),
        (Join-Path $Dir 'MonoBleedingEdge\EmbedRuntime'))) {
        New-Item -ItemType Directory -Path $d -Force | Out-Null
    }
    if (-not $Opt.ContainsKey('MissingExe')) {
        Set-Content -LiteralPath (Join-Path $Dir 'FamilyRacing.exe') -Value 'fixture player exe' -NoNewline
    }
    Set-Content -LiteralPath (Join-Path $Dir 'UnityPlayer.dll') -Value 'fixture unityplayer' -NoNewline
    Set-Content -LiteralPath (Join-Path $Dir 'UnityCrashHandler64.exe') -Value 'fixture crash handler' -NoNewline
    foreach ($f in @('level0', 'sharedassets0.assets', 'sharedassets0.assets.resS',
        'resources.assets', 'globalgamemanagers', 'globalgamemanagers.assets',
        'boot.config', 'app.info')) {
        Set-Content -LiteralPath (Join-Path $data $f) -Value ('fixture ' + $f) -NoNewline
    }
    foreach ($f in @('FamilyRacing.Core.dll', 'UnityEngine.CoreModule.dll',
        'mscorlib.dll', 'netstandard.dll', 'Newtonsoft.Json.dll')) {
        Set-Content -LiteralPath (Join-Path $managed $f) -Value ('fixture ' + $f) -NoNewline
    }
    if (-not $Opt.ContainsKey('MissingRuntimeDll')) {
        Set-Content -LiteralPath (Join-Path $managed 'FamilyRacing.Runtime.dll') -Value 'fixture runtime dll' -NoNewline
    }
    if (-not $Opt.ContainsKey('MissingMono')) {
        Set-Content -LiteralPath (Join-Path $Dir 'MonoBleedingEdge\etc\mono\4.5\machine.config') -Value 'fixture mono config' -NoNewline
        Set-Content -LiteralPath (Join-Path $Dir 'MonoBleedingEdge\EmbedRuntime\mono-posix.dll') -Value 'fixture mono runtime' -NoNewline
    }
    if ($Opt.ContainsKey('MissingData')) {
        Remove-Item -LiteralPath $data -Recurse -Force
    }
    # An ordinary player carries no production content manifest — only
    # full-content flavors write one. Rehearsal fixtures therefore get a
    # manifest only when a Manifest* option asks for it; the real path
    # always seeds the release flavor's own complete manifest.
    $hasManifestOpt = $Opt.ContainsKey('ManifestStage') -or $Opt.ContainsKey('ManifestDriving') -or
        $Opt.ContainsKey('ManifestContent') -or $Opt.ContainsKey('ManifestCandidate')
    $writeManifest = (-not $Opt.ContainsKey('NoManifest')) -and
        ((-not $Opt.ContainsKey('Rehearsal')) -or $hasManifestOpt)
    if ($writeManifest) {
        $stage = if ($Opt.ContainsKey('ManifestStage')) { $Opt.ManifestStage } else { 'complete' }
        $driving = if ($Opt.ContainsKey('ManifestDriving')) { $Opt.ManifestDriving } else { 'full-content-1' }
        $content = if ($Opt.ContainsKey('ManifestContent')) { $Opt.ManifestContent } else { 'full-content-1' }
        $candidate = if ($Opt.ContainsKey('ManifestCandidate')) { $Opt.ManifestCandidate } else { ($stage -eq 'complete') }
        $candJson = if ($candidate) { 'true' } else { 'false' }
        $m = "{`"schemaVersion`":1,`"presentation`":`"FullContent`",`"configuration`":`"Ordinary`"," +
            "`"contentStage`":`"$stage`",`"contentVersion`":`"$content`",`"drivingVersion`":`"$driving`",`"candidate`":$candJson}"
        $mPath = Join-Path $Dir 'manifest.json'
        Set-Content -LiteralPath $mPath -Value $m -Encoding UTF8
        if ($Opt.ContainsKey('StaleManifest')) {
            (Get-Item -LiteralPath $mPath).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-2)
        }
    }
    if ($Opt.ContainsKey('PlantProfile')) {
        New-Item -ItemType Directory -Path (Join-Path $data 'Profiles') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $data 'Profiles\profile-0.json') -Value '{"schemaVersion":3}' -NoNewline
    }
    if ($Opt.ContainsKey('PlantKeystore')) {
        Set-Content -LiteralPath (Join-Path $Dir 'release.keystore') -Value 'fake key bytes' -NoNewline
    }
    if ($Opt.ContainsKey('PlantToken')) {
        Set-Content -LiteralPath (Join-Path $data 'validation-token.txt') -Value 'fixture validation token' -NoNewline
    }
    if ($Opt.ContainsKey('PlantDoNotShip')) {
        New-Item -ItemType Directory -Path (Join-Path $Dir 'Family Racing_BurstDebugInformation_DoNotShip') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $Dir 'Family Racing_BurstDebugInformation_DoNotShip\burst-symbols.pdb') -Value 'fixture symbols' -NoNewline
        Set-Content -LiteralPath (Join-Path $managed 'FamilyRacing.Runtime.pdb') -Value 'fixture symbols' -NoNewline
        Set-Content -LiteralPath (Join-Path $Dir 'unity-build.log') -Value 'fixture build log' -NoNewline
    }
}

# Full Build-FamilyRelease.ps1 fixture: fake project with Tools scripts,
# Release/release.json, tracked notices + install guide, a real git repo
# (source identity is recorded honestly), a fixture editor tree plus
# signing env for the real build path, and a fake ordinary Windows player.
# Options: Rehearsal, NoWindows, NoAndroid, MissingExe, MissingData,
# MissingRuntimeDll, MissingMono, NoManifest, ManifestStage,
# ManifestDriving, ManifestContent, ManifestCandidate, StaleManifest,
# PlantProfile, PlantKeystore, PlantToken, PlantDoNotShip, NoNotices,
# NoGuide, NoGit, DirtyFile, ExistingOutput, OmitEnv.
function Run-PackageCase([string]$Name, [hashtable]$Opt = @{}) {
    $project = Join-Path $fixtureRoot ('pkg-' + $Name)
    $tools = Join-Path $project 'Tools'
    New-Item -ItemType Directory -Path $tools -Force | Out-Null
    foreach ($toolScript in @('Verify.ps1', 'Build-AndroidOffline.ps1', 'Build-Windows.ps1',
            'Sign-AndroidRelease.ps1', 'Build-FamilyRelease.ps1')) {
        $source = Join-Path $ToolsDir $toolScript
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $tools }
    }
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
    if (-not $Opt.ContainsKey('NoNotices')) {
        Set-Content -LiteralPath (Join-Path $releaseDir 'THIRD-PARTY-NOTICES.txt') -Value 'fixture third-party notices' -NoNewline
    }
    if (-not $Opt.ContainsKey('NoGuide')) {
        New-Item -ItemType Directory -Path (Join-Path $project 'docs') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $project 'docs\FAMILY_INSTALL_GUIDE.md') -Value 'fixture install guide' -NoNewline
    }

    if (-not $Opt.ContainsKey('Rehearsal')) {
        # Editor tree + signing env for the real orchestrated path: the
        # Android export/sign chain runs end-to-end on fixture tools.
        $editorRoot = Join-Path $project 'Installed Editors'
        $editor = Join-Path $editorRoot '6000.3.26f1\Editor'
        $android = Join-Path $editor 'Data\PlaybackEngines\AndroidPlayer'
        $jdkBin = Join-Path $android 'OpenJDK\bin'
        $buildTools = Join-Path $android 'SDK\build-tools\36.0.0'
        $standalone = Join-Path $editor 'Data\PlaybackEngines\WindowsStandaloneSupport'
        foreach ($directory in @($editor, $jdkBin, $standalone,
                (Join-Path $android 'Tools\gradle\lib'),
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
        Set-Content -LiteralPath (Join-Path $editor 'behavior.txt') -Value 'success'
        Set-Content -LiteralPath (Join-Path $jdkBin 'behavior.txt') -Value 'success'
        Set-Content -LiteralPath (Join-Path $jdkBin 'cert-sha256.txt') -Value $script:FixtureCert
        Set-Content -LiteralPath (Join-Path $buildTools 'badging.txt') -Value $script:FixtureBadging
        $keystore = Join-Path $project 'test.keystore'
        Set-Content -LiteralPath $keystore -Value 'temporary fixture test keystore - not a real key'
        $omit = if ($Opt.ContainsKey('OmitEnv')) { [string[]]$Opt.OmitEnv } else { @() }
        Set-SignEnv -KeystorePath $keystore -Omit $omit
    }

    $winSrcName = if ($Opt.ContainsKey('Rehearsal')) { 'Windows-Ordinary' } else { 'Windows-FamilyRelease' }
    $winSrc = Join-Path $project "Builds\$winSrcName"
    if (-not $Opt.ContainsKey('NoWindows')) {
        New-FixtureWindowsPlayer $winSrc -Opt $Opt
    }
    if ($Opt.ContainsKey('Rehearsal') -and -not $Opt.ContainsKey('NoAndroid')) {
        $apkDir = Join-Path $project 'Builds\Android-FamilyRelease'
        New-Item -ItemType Directory -Path $apkDir -Force | Out-Null
        New-FixtureApk (Join-Path $apkDir 'FamilyRacing.apk') -PreSigned:$true
    }

    # Source identity: a real git repo so the manifest records an actual
    # commit and an honest clean/dirty verdict.
    if (-not $Opt.ContainsKey('NoGit')) {
        $oldPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & git -C $project init -q 2>&1 | Out-Null
        & git -C $project add -A 2>&1 | Out-Null
        & git -C $project -c user.email=fixture@test -c user.name=fixture commit -qm 'fixture baseline' 2>&1 | Out-Null
        $ErrorActionPreference = $oldPreference
        if ($Opt.ContainsKey('DirtyFile')) {
            Set-Content -LiteralPath (Join-Path $releaseDir 'dirty-marker.txt') -Value 'uncommitted' -NoNewline
        }
    }

    $output = Join-Path $project 'out\package'
    if ($Opt.ContainsKey('ExistingOutput')) {
        New-Item -ItemType Directory -Path $output -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $output 'leftover.txt') -Value 'previous run' -NoNewline
    }
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $tools 'Build-FamilyRelease.ps1'),
        '-Config', (Join-Path $releaseDir 'release.json'), '-Output', $output, '-ProjectPath', $project)
    if (-not $Opt.ContainsKey('Rehearsal')) {
        $argList += @('-EditorRoot', (Join-Path $project 'Installed Editors'))
    } else {
        $argList += @('-Rehearsal', '-WindowsSource', $winSrc)
        if (-not $Opt.ContainsKey('NoAndroid')) { $argList += @('-AndroidSource', (Join-Path $project 'Builds\Android-FamilyRelease\FamilyRacing.apk')) }
    }
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $pkgOutput = & powershell.exe $argList 2>&1 | Out-String
    $pkgCode = $LASTEXITCODE
    $ErrorActionPreference = $oldPreference
    if (-not $Opt.ContainsKey('Rehearsal')) { Clear-SignEnv }
    return @{ Project = $project; ExitCode = $pkgCode; Output = $pkgOutput; OutDir = $output;
              WinZip = (Join-Path $output 'FamilyRacing-1.0.0-Windows-x64.zip');
              Apk = (Join-Path $output 'FamilyRacing-1.0.0-Android.apk');
              ManifestPath = (Join-Path $output 'release-manifest.json');
              WinSrc = $winSrc }
}

function Get-ZipEntries([string]$Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try { return @($zip.Entries | ForEach-Object { $_.FullName }) }
    finally { $zip.Dispose() }
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

    # --- Build-FamilyRelease.ps1 package assembly -------------------------
    # Rehearsal path: packages an existing ordinary player output into a
    # clearly-labeled, traceable package — never a release candidate.
    $pkg = Run-PackageCase 'rehearsal-ok' @{ Rehearsal = $true }
    Check 'pkg: rehearsal package assembles end-to-end' ($pkg.ExitCode -eq 0) $pkg.Output
    Check 'pkg: windows zip produced' (Test-Path -LiteralPath $pkg.WinZip)
    Check 'pkg: android artifact copied' (Test-Path -LiteralPath $pkg.Apk)
    Check 'pkg: manifest written OUTSIDE the zip' (Test-Path -LiteralPath $pkg.ManifestPath)
    $manifest = $null
    if (Test-Path -LiteralPath $pkg.ManifestPath) {
        $manifest = Get-Content -LiteralPath $pkg.ManifestPath -Raw | ConvertFrom-Json
    }
    Check 'pkg: manifest is schema 1, unqualified, marked rehearsal' `
        ($null -ne $manifest -and $manifest.schemaVersion -eq 1 -and
         $manifest.acceptanceStatus -eq 'unqualified' -and
         $manifest.packageKind -eq 'rehearsal') ($pkg.ManifestPath)
    Check 'pkg: manifest records honest non-complete content stage' `
        ($null -ne $manifest -and $manifest.contentStage -ne 'complete') 'contentStage overclaimed'
    if (Test-Path -LiteralPath $pkg.WinZip) {
        $entries = Get-ZipEntries $pkg.WinZip
        Check 'pkg: archive contains the ENTIRE player, not just the exe' `
            (@($entries | Where-Object { $_ -eq 'FamilyRacing/FamilyRacing.exe' }).Count -eq 1 -and
             @($entries | Where-Object { $_ -eq 'FamilyRacing/FamilyRacing_Data/Managed/FamilyRacing.Runtime.dll' }).Count -eq 1 -and
             @($entries | Where-Object { $_ -eq 'FamilyRacing/FamilyRacing_Data/level0' }).Count -eq 1 -and
             @($entries | Where-Object { $_ -eq 'FamilyRacing/FamilyRacing_Data/globalgamemanagers' }).Count -eq 1 -and
             @($entries | Where-Object { $_ -eq 'FamilyRacing/UnityPlayer.dll' }).Count -eq 1 -and
             @($entries | Where-Object { $_ -like 'FamilyRacing/MonoBleedingEdge/etc/mono/*' }).Count -ge 1) ($entries -join ' ')
        Check 'pkg: third-party notices inside the archive' `
            (@($entries | Where-Object { $_ -eq 'FamilyRacing/THIRD-PARTY-NOTICES.txt' }).Count -eq 1)
        Check 'pkg: install guide inside the archive' `
            (@($entries | Where-Object { $_ -eq 'FamilyRacing/INSTALL-GUIDE.txt' }).Count -eq 1)
        Check 'pkg: release manifest NOT inside the archive' `
            (@($entries | Where-Object { $_ -match 'release-manifest' }).Count -eq 0)
    } else {
        Check 'pkg: archive contains the ENTIRE player, not just the exe' $false 'no zip'
        Check 'pkg: third-party notices inside the archive' $false 'no zip'
        Check 'pkg: install guide inside the archive' $false 'no zip'
        Check 'pkg: release manifest NOT inside the archive' $false 'no zip'
    }
    if ($null -ne $manifest -and (Test-Path -LiteralPath $pkg.WinZip)) {
        $winEntry = @($manifest.files | Where-Object { $_.path -eq 'FamilyRacing-1.0.0-Windows-x64.zip' })
        $actualHash = (Get-FileHash -LiteralPath $pkg.WinZip -Algorithm SHA256).Hash.ToLowerInvariant()
        $actualSize = (Get-Item -LiteralPath $pkg.WinZip).Length
        Check 'pkg: manifest records post-assembly zip sha256+size' `
            ($winEntry.Count -eq 1 -and $winEntry[0].sha256 -eq $actualHash -and
             $winEntry[0].sizeBytes -eq $actualSize) ($manifest.files | ConvertTo-Json -Compress -Depth 4)
    } else { Check 'pkg: manifest records post-assembly zip sha256+size' $false 'no manifest/zip' }
    if ($null -ne $manifest -and (Test-Path -LiteralPath $pkg.Apk)) {
        $apkEntry = @($manifest.files | Where-Object { $_.path -eq 'FamilyRacing-1.0.0-Android.apk' })
        $srcHash = (Get-FileHash -LiteralPath (Join-Path $pkg.Project 'Builds\Android-FamilyRelease\FamilyRacing.apk') -Algorithm SHA256).Hash.ToLowerInvariant()
        Check 'pkg: android artifact bytes copied unchanged' `
            ($apkEntry.Count -eq 1 -and $apkEntry[0].sha256 -eq $srcHash)
    } else { Check 'pkg: android artifact bytes copied unchanged' $false 'no manifest/apk' }
    Check 'pkg: manifest records identity + fingerprints' `
        ($null -ne $manifest -and $manifest.identity.versionName -eq '1.0.0' -and
         $manifest.identity.androidVersionCode -eq 1 -and
         $manifest.identity.applicationId -eq 'com.daniel.familyracing' -and
         $manifest.gameplay.gameplayVersion -eq 'gameplay-4' -and
         $manifest.gameplay.contentVersion -eq 'full-content-1' -and
         $manifest.gameplay.lanProtocol -eq 3 -and
         $manifest.gameplay.observationSchema -eq 7 -and
         $manifest.gameplay.profileSchema -eq 3 -and
         $manifest.gameplay.backend -eq 'arcade' -and
         $manifest.windows.arch -eq 'x64' -and $manifest.windows.backend -eq 'Mono' -and
         $manifest.windows.authenticodeSigned -eq $false) 'manifest fields wrong'

    $pkg = Run-PackageCase 'git-clean' @{ Rehearsal = $true }
    $m = if (Test-Path -LiteralPath $pkg.ManifestPath) { Get-Content -LiteralPath $pkg.ManifestPath -Raw | ConvertFrom-Json } else { $null }
    $head = (& git -C $pkg.Project rev-parse HEAD 2>$null)
    Check 'pkg: clean source recorded with real commit' `
        ($null -ne $m -and $m.source.commit -eq $head -and $m.source.cleanSource -eq $true) `
        ("commit=$($m.source.commit) head=$head")
    $pkg = Run-PackageCase 'git-dirty' @{ Rehearsal = $true; DirtyFile = $true }
    $m = if (Test-Path -LiteralPath $pkg.ManifestPath) { Get-Content -LiteralPath $pkg.ManifestPath -Raw | ConvertFrom-Json } else { $null }
    Check 'pkg: dirty source recorded honestly with named areas' `
        ($null -ne $m -and $m.source.cleanSource -eq $false -and
         @($m.source.dirtyAreas).Count -ge 1) ($m.source | ConvertTo-Json -Compress)

    # --- stale / mixed / partial content manifests -----------------------
    $pkg = Run-PackageCase 'partial-manifest' @{ Rehearsal = $true; ManifestStage = 'partial' }
    Check 'pkg: partial content manifest rejected as release package' `
        ($pkg.ExitCode -ne 0 -and $pkg.Output -match 'partial' -and
         -not (Test-Path -LiteralPath $pkg.WinZip)) $pkg.Output
    $pkg = Run-PackageCase 'mixed-manifest' @{ Rehearsal = $true; ManifestDriving = 'legacy-9' }
    Check 'pkg: mixed source-content manifest rejected' `
        ($pkg.ExitCode -ne 0 -and $pkg.Output -match 'drivingVersion|content' -and
         -not (Test-Path -LiteralPath $pkg.WinZip)) $pkg.Output
    $pkg = Run-PackageCase 'noncandidate' @{ Rehearsal = $true; ManifestCandidate = $false }
    Check 'pkg: non-candidate manifest rejected' `
        ($pkg.ExitCode -ne 0 -and $pkg.Output -match 'candidate' -and
         -not (Test-Path -LiteralPath $pkg.WinZip)) $pkg.Output

    # --- incomplete player trees ------------------------------------------
    $pkg = Run-PackageCase 'missing-data' @{ Rehearsal = $true; MissingData = $true }
    Check 'pkg: missing player data dir rejected' `
        ($pkg.ExitCode -ne 0 -and $pkg.Output -match 'FamilyRacing_Data') $pkg.Output
    $pkg = Run-PackageCase 'missing-runtime-dll' @{ Rehearsal = $true; MissingRuntimeDll = $true }
    Check 'pkg: missing runtime DLL rejected' `
        ($pkg.ExitCode -ne 0 -and $pkg.Output -match 'FamilyRacing\.Runtime\.dll') $pkg.Output
    $pkg = Run-PackageCase 'missing-mono' @{ Rehearsal = $true; MissingMono = $true }
    Check 'pkg: missing Mono runtime rejected' `
        ($pkg.ExitCode -ne 0 -and $pkg.Output -match 'MonoBleedingEdge') $pkg.Output
    $pkg = Run-PackageCase 'missing-exe' @{ Rehearsal = $true; MissingExe = $true }
    Check 'pkg: missing player exe rejected' `
        ($pkg.ExitCode -ne 0 -and $pkg.Output -match 'FamilyRacing\.exe') $pkg.Output

    # --- contaminants that must never ship ---------------------------------
    $pkg = Run-PackageCase 'plant-profile' @{ Rehearsal = $true; PlantProfile = $true }
    Check 'pkg: planted profile save rejected' `
        ($pkg.ExitCode -ne 0 -and $pkg.Output -match 'profile-0\.json') $pkg.Output
    $pkg = Run-PackageCase 'plant-keystore' @{ Rehearsal = $true; PlantKeystore = $true }
    Check 'pkg: planted keystore rejected' `
        ($pkg.ExitCode -ne 0 -and $pkg.Output -match 'keystore') $pkg.Output
    $pkg = Run-PackageCase 'plant-token' @{ Rehearsal = $true; PlantToken = $true }
    Check 'pkg: validation token rejected' `
        ($pkg.ExitCode -ne 0 -and $pkg.Output -match 'validation') $pkg.Output
    $pkg = Run-PackageCase 'donotship' @{ Rehearsal = $true; PlantDoNotShip = $true }
    Check 'pkg: burst/symbol/log byproducts excluded, package still assembles' `
        ($pkg.ExitCode -eq 0) $pkg.Output
    if ($pkg.ExitCode -eq 0 -and (Test-Path -LiteralPath $pkg.WinZip)) {
        $entries = Get-ZipEntries $pkg.WinZip
        Check 'pkg: no DoNotShip/pdb/log entries in archive' `
            (@($entries | Where-Object { $_ -match 'DoNotShip|\.pdb$|\.log$' }).Count -eq 0) ($entries -join ' ')
    } else { Check 'pkg: no DoNotShip/pdb/log entries in archive' $false 'no zip' }

    # --- required package documents ----------------------------------------
    $pkg = Run-PackageCase 'no-notices' @{ Rehearsal = $true; NoNotices = $true }
    Check 'pkg: missing third-party notices rejected' `
        ($pkg.ExitCode -ne 0 -and $pkg.Output -match 'NOTICES|notices') $pkg.Output
    $pkg = Run-PackageCase 'no-guide' @{ Rehearsal = $true; NoGuide = $true }
    Check 'pkg: missing install guide rejected' `
        ($pkg.ExitCode -ne 0 -and $pkg.Output -match 'install guide|INSTALL') $pkg.Output

    # --- output hygiene ------------------------------------------------------
    $pkg = Run-PackageCase 'reused-output' @{ Rehearsal = $true; ExistingOutput = $true }
    Check 'pkg: reused output directory rejected' `
        ($pkg.ExitCode -ne 0 -and $pkg.Output -match 'exists|reused|empty') $pkg.Output
    $pkg = Run-PackageCase 'failed-no-artifacts' @{ Rehearsal = $true; ManifestStage = 'partial' }
    Check 'pkg: failed run publishes no packages' `
        ($pkg.ExitCode -ne 0 -and -not (Test-Path -LiteralPath $pkg.WinZip) -and
         -not (Test-Path -LiteralPath $pkg.Apk)) ''
    $pkg = Run-PackageCase 'no-android' @{ Rehearsal = $true; NoAndroid = $true }
    Check 'pkg: rehearsal without android artifact still assembles + names blocker' `
        ($pkg.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $pkg.Apk)) $pkg.Output
    if ($pkg.ExitCode -eq 0 -and (Test-Path -LiteralPath $pkg.ManifestPath)) {
        $m = Get-Content -LiteralPath $pkg.ManifestPath -Raw | ConvertFrom-Json
        Check 'pkg: absent android artifact recorded as named blocker' `
            ($null -eq $m.packages.android.package -and
             @($m.blockers | Where-Object { $_ -match 'android' }).Count -ge 1) `
            ($m | ConvertTo-Json -Compress -Depth 5)
    } else { Check 'pkg: absent android artifact recorded as named blocker' $false 'no manifest' }

    # --- real orchestrated path (validation -> builds -> signing -> assembly)
    $pkg = Run-PackageCase 'real-path' @{}
    Check 'pkg: full pipeline assembles end-to-end' ($pkg.ExitCode -eq 0) $pkg.Output
    $unityArgs = ''
    $argsPath = Join-Path $pkg.Project 'unity-arguments.txt'
    if (Test-Path -LiteralPath $argsPath) { $unityArgs = (Get-Content -LiteralPath $argsPath) -join "`n" }
    Check 'pkg: pipeline invoked the release Windows + Android exports' `
        ($unityArgs -match 'FoundationBuild\.WindowsFamilyRelease' -and
         $unityArgs -match 'FoundationBuild\.ExportAndroidFamilyRelease') $unityArgs
    Check 'pkg: pipeline produced both packages + manifest' `
        ((Test-Path -LiteralPath $pkg.WinZip) -and (Test-Path -LiteralPath $pkg.Apk) -and
         (Test-Path -LiteralPath $pkg.ManifestPath))
    if (Test-Path -LiteralPath $pkg.ManifestPath) {
        $m = Get-Content -LiteralPath $pkg.ManifestPath -Raw | ConvertFrom-Json
        Check 'pkg: release path is NOT labeled rehearsal and apk cert recorded' `
            ($m.packageKind -eq 'release' -and
             $m.packages.android.certificateSha256 -eq $script:FixtureCert -and
             $m.acceptanceStatus -eq 'unqualified') ($m | ConvertTo-Json -Compress -Depth 5)
    } else { Check 'pkg: release path is NOT labeled rehearsal and apk cert recorded' $false 'no manifest' }

    $pkg = Run-PackageCase 'real-missing-env' @{ OmitEnv = @('FAMILY_RACING_KEYSTORE') }
    Check 'pkg: missing signing credential fails NAMED with no package' `
        ($pkg.ExitCode -ne 0 -and $pkg.Output -match 'FAMILY_RACING_KEYSTORE' -and
         -not (Test-Path -LiteralPath $pkg.WinZip) -and -not (Test-Path -LiteralPath $pkg.Apk)) $pkg.Output
    $pkg = Run-PackageCase 'real-stale-player' @{ StaleManifest = $true }
    Check 'pkg: stale player manifest rejected on the real path' `
        ($pkg.ExitCode -ne 0 -and $pkg.Output -match 'stale|older|this run' -and
         -not (Test-Path -LiteralPath $pkg.WinZip)) $pkg.Output
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
