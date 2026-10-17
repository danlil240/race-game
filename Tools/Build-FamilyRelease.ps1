# Assemble the traceable family release package set.
#
#   powershell -File Tools/Build-FamilyRelease.ps1 -Config Release\release.json `
#       -Output <new-directory> [-EditorRoot <root>]
#
# Full path (no -Rehearsal): runs the real orchestration --
#   Verify -Mode WindowsFamilyRelease  (content gate + ordinary Mono x64
#     player via FoundationBuild.WindowsFamilyRelease)
#   Verify -Mode AndroidFamilyRelease  (unsigned release Gradle output,
#     zipalign + release-key signing + certificate/metadata verification
#     via Sign-AndroidRelease.ps1; credentials arrive only through the
#     FAMILY_RACING_* process environment)
# then assembles:
#   <Output>\FamilyRacing-<version>-Windows-x64.zip   entire ordinary
#     player (exe + FamilyRacing_Data + UnityPlayer.dll + Mono runtime)
#     under a top-level FamilyRacing\ folder, minus DoNotShip symbol
#     bundles / pdb / logs -- never keystores, saves or validation tokens
#   <Output>\FamilyRacing-<version>-Android.apk       signed APK bytes
#     copied unchanged
#   <Output>\THIRD-PARTY-NOTICES.txt + INSTALL-GUIDE.txt
#   <Output>\release-manifest.json                    schema 1, OUTSIDE
#     the zip so hashes are never self-referential; per-file sha256+size
#     computed after final assembly; acceptanceStatus stays
#     "unqualified" until the evidence gate (Task 5) says otherwise.
#
# -Rehearsal packages EXISTING build outputs (-WindowsSource /
# -AndroidSource) without invoking Unity -- proof that the assembly and
#   manifest machinery works end-to-end on real build output while the
#   production content gate is open. Rehearsal packages are stamped
#   packageKind="rehearsal", acceptanceStatus="unqualified", and never
#   called a release candidate; a missing android artifact is recorded
#   as a named blocker rather than fabricated.
#
# The resolved -Config path is exported as FAMILY_RACING_RELEASE_CONFIG
# for every child Verify/Unity/sign invocation, so the whole pipeline
# stamps the same release identity the manifest records.
#
# Every failure is named and publishes nothing: reused output dirs,
# stale/mixed/partial source-content manifests, missing player data or
# runtime DLLs, planted saves/keys/validation tokens, and missing
# notices/guide sources all stop the run before any artifact lands.
param(
    [Parameter(Mandatory = $true)]
    [string]$Config,
    [Parameter(Mandatory = $true)]
    [string]$Output,
    # NOTE: $PSScriptRoot cannot be used in a default value here — with
    # Mandatory parameters bound first it evaluates to an empty string
    # (PS 5.1 binding order). Resolved in the body instead.
    [string]$ProjectPath = '',
    [string]$EditorRoot = 'C:\Program Files\Unity\Hub\Editor',
    [string]$WindowsSource = '',
    [string]$AndroidSource = '',
    [switch]$Rehearsal
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not $ProjectPath) { $ProjectPath = Join-Path $PSScriptRoot '..' }
$script:staging = $null
$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$editorVersion = '6000.3.26f1'
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$runStart = [DateTime]::UtcNow
# Logs land under the repo's gitignored Logs\ -- never inside the output
# directory (generated logs stay out of distribution). Created lazily so
# the source-identity check sees the tree exactly as the caller left it.
$logsDir = Join-Path $project "Logs\family-release\$runId"

# Expected fingerprints -- the constants the shipped runtime actually
# reports, pinned here so a stale or mixed build can never slip through.
$ExpectedGameplayVersion = 'gameplay-4'          # NetworkSettings.GameplayVersion
$ExpectedContentVersion  = 'full-content-1'      # GameplayCatalog.ContentVersion
$ExpectedDrivingVersion  = 'full-content-1'      # FullContentSetup.DrivingVersion
$ExpectedLanProtocol     = 3                     # NetworkSettings.LanProtocolVersion
$ExpectedObservationSchema = 7                   # NetworkSettings.StateSchemaVersion
$ExpectedProfileSchema   = 3                     # ProfileValidation.SchemaVersion

# Build byproducts that never ship (excluded from the archive, not a
# failure): burst debug info, compiler symbols, generated logs.
$ExcludePatterns = @(
    '*_DoNotShip*', '*.pdb', '*.ilk', '*.exp', '*.log', '*.tmp', '.staged-*'
)
# Contamination that MUST fail the run -- secrets, player saves, and
# development validation material can never ride inside a package.
$ForbiddenPatterns = @(
    '*.keystore', '*.jks', '*.p12', '*.pfx', '*.pem', '*.key',
    'profile-*.json', 'profile-*.json.bak', 'Profiles\*',
    '*validation-token*', '*validation_token*', '*validation-secret*',
    '*validation_secret*', 'validation-token*', '*CREDENTIALS*'
)

function Require-File([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description missing: $Path"
    }
}

function Test-ForbiddenName([string]$RelativePath) {
    $name = Split-Path $RelativePath -Leaf
    foreach ($pattern in $ForbiddenPatterns) {
        if ($RelativePath -like $pattern -or $name -like $pattern) { return $pattern }
    }
    return $null
}

function Test-ExcludedName([string]$RelativePath) {
    $name = Split-Path $RelativePath -Leaf
    foreach ($pattern in $ExcludePatterns) {
        if ($RelativePath -like $pattern -or $name -like $pattern) { return $true }
    }
    return $false
}

function Invoke-ChildTool([string]$Script, [string[]]$Arguments, [string]$LogName) {
    $logPath = Join-Path $logsDir $LogName
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $text = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Script @Arguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    $ErrorActionPreference = $oldPreference
    [IO.File]::WriteAllText($logPath, $text)
    return @{ Code = $code; Text = $text; Log = $logPath }
}

try {
    # --- stage: config -----------------------------------------------------
    Require-File $Config 'Release config'
    $Config = (Resolve-Path -LiteralPath $Config).Path
    # Every child pipeline (Verify modes, the Unity exports, the offline
    # Android build and its signer) resolves the release config through
    # FAMILY_RACING_RELEASE_CONFIG. Propagating the resolved -Config here
    # guarantees the identity/version/certificate the manifest records is
    # exactly the identity stamped into the artifacts — a non-default
    # -Config can never diverge from what the builds actually used.
    $env:FAMILY_RACING_RELEASE_CONFIG = $Config
    $releaseConfig = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
    foreach ($field in @('versionName', 'androidVersionCode', 'applicationId',
            'companyName', 'productName', 'androidMinSdkVersion',
            'androidTargetSdkVersion', 'expectedCertificateSha256')) {
        if ($null -eq $releaseConfig.$field) {
            throw "Release config $Config is missing '$field'."
        }
    }
    $version = [string]$releaseConfig.versionName

    # --- stage: output dir --------------------------------------------------
    $outputFull = [IO.Path]::GetFullPath($Output)
    if (Test-Path -LiteralPath $outputFull) {
        $existing = @(Get-ChildItem -LiteralPath $outputFull -Force -ErrorAction SilentlyContinue)
        throw "Output directory is reused/not empty: $outputFull ($($existing.Count) item(s)) -- release output must go to a NEW directory."
    }

    # --- stage: source identity ----------------------------------------------
    $sourceCommit = 'unknown'
    $cleanSource = $false
    $dirtyAreas = @('not a git working tree')
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $head = (& git -C $project rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $head) { $sourceCommit = ($head | Select-Object -First 1).Trim() }
    $porcelain = @(& git -C $project status --porcelain 2>$null)
    $ErrorActionPreference = $oldPreference
    if ($sourceCommit -ne 'unknown') {
        if ($porcelain.Count -eq 0) {
            $cleanSource = $true
            $dirtyAreas = @()
        } else {
            $cleanSource = $false
            # Name the dirty AREAS (top two path segments), not every file.
            $areas = @{}
            foreach ($line in $porcelain) {
                $path = ($line -replace '^\s*..\s+', '') -replace '\\', '/'
                $segments = $path -split '/'
                $area = if ($segments.Count -ge 2) { $segments[0] + '/' + $segments[1] } else { $segments[0] }
                $areas[$area] = $true
            }
            $dirtyAreas = @($areas.Keys | Sort-Object | Select-Object -First 10)
        }
    }

    # --- stage: builds ---------------------------------------------------------
    New-Item -ItemType Directory -Path $logsDir -Force | Out-Null

    if (-not $Rehearsal) {
        # Signing credentials are preflight-checked BEFORE burning a
        # Unity run -- the signer itself re-checks, but a missing variable
        # must fail the orchestration with the variable named.
        $requiredEnv = @('FAMILY_RACING_KEYSTORE', 'FAMILY_RACING_KEY_ALIAS',
            'FAMILY_RACING_KEYSTORE_PASSWORD', 'FAMILY_RACING_KEY_PASSWORD')
        $missingEnv = @($requiredEnv | Where-Object {
            [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($_)) })
        if ($missingEnv.Count -gt 0) {
            throw ("Missing signing environment variable(s): " + ($missingEnv -join ', ') +
                ". Set them in the process environment only; no release is published.")
        }

        $verify = Join-Path $PSScriptRoot 'Verify.ps1'
        Require-File $verify 'Verification entry point'

        Write-Host "FamilyRelease: Verify -Mode WindowsFamilyRelease (content gate + ordinary Mono x64 player)"
        $win = Invoke-ChildTool $verify @('-Mode', 'WindowsFamilyRelease', '-EditorRoot', $EditorRoot) 'windows-family-release.log'
        if ($win.Code -ne 0) {
            Write-Host $win.Text.TrimEnd()
            throw "Windows family release build failed (exit $($win.Code)) -- the FullContent production gate and ordinary build must pass first. See $($win.Log)."
        }
        Write-Host "FamilyRelease: Verify -Mode AndroidFamilyRelease (export -> Gradle release -> zipalign/sign/verify)"
        $apk = Invoke-ChildTool $verify @('-Mode', 'AndroidFamilyRelease', '-EditorRoot', $EditorRoot) 'android-family-release.log'
        if ($apk.Code -ne 0) {
            Write-Host $apk.Text.TrimEnd()
            throw "Android family release build/sign failed (exit $($apk.Code)); no release is published. See $($apk.Log)."
        }
        if (-not $WindowsSource) { $WindowsSource = Join-Path $project 'Builds\Windows-FamilyRelease' }
        if (-not $AndroidSource) { $AndroidSource = Join-Path $project 'Builds\Android-FamilyRelease\FamilyRacing.apk' }
    } else {
        if (-not $WindowsSource) { $WindowsSource = Join-Path $project 'Builds\Windows-FamilyRelease' }
        Write-Host "FamilyRelease REHEARSAL: packaging existing output $WindowsSource -- not a release candidate."
    }

    # --- stage: player completeness -------------------------------------------
    $winSrcFull = [IO.Path]::GetFullPath($WindowsSource)
    if (-not (Test-Path -LiteralPath $winSrcFull -PathType Container)) {
        throw "Windows player source missing: $winSrcFull"
    }
    $exe = Join-Path $winSrcFull 'FamilyRacing.exe'
    $data = Join-Path $winSrcFull 'FamilyRacing_Data'
    Require-File $exe 'Windows player exe'
    if (-not (Test-Path -LiteralPath $data -PathType Container)) {
        throw "Windows player data directory FamilyRacing_Data missing: $data"
    }
    foreach ($rel in @('Managed\FamilyRacing.Core.dll', 'Managed\FamilyRacing.Runtime.dll',
            'level0', 'globalgamemanagers', 'resources.assets', 'boot.config', 'app.info')) {
        Require-File (Join-Path $data $rel) "Windows player payload 'FamilyRacing_Data\$rel'"
    }
    Require-File (Join-Path $winSrcFull 'UnityPlayer.dll') 'Unity player runtime UnityPlayer.dll'
    if (-not (Test-Path -LiteralPath (Join-Path $winSrcFull 'MonoBleedingEdge') -PathType Container)) {
        throw "Mono runtime directory MonoBleedingEdge missing: $(Join-Path $winSrcFull 'MonoBleedingEdge')"
    }
    Require-File (Join-Path $winSrcFull 'MonoBleedingEdge\etc\mono\4.5\machine.config') 'Mono runtime configuration'
    # At least one managed engine assembly must exist -- a data folder
    # holding only our DLLs means a truncated copy.
    $engineDlls = @(Get-ChildItem -LiteralPath (Join-Path $data 'Managed') -Filter 'UnityEngine*.dll' -File -ErrorAction SilentlyContinue)
    if ($engineDlls.Count -eq 0) {
        throw "Windows player Managed payload carries no UnityEngine*.dll runtime assemblies: $(Join-Path $data 'Managed')"
    }

    # --- stage: content manifest (stale / mixed / partial / non-candidate) ----
    $contentStage = 'graybox'
    $contentManifest = Join-Path $winSrcFull 'manifest.json'
    $blockers = @()
    if (Test-Path -LiteralPath $contentManifest -PathType Leaf) {
        $cm = Get-Content -LiteralPath $contentManifest -Raw | ConvertFrom-Json
        $cmStage = [string]$cm.contentStage
        if ($cmStage -eq 'partial') {
            throw "Player content manifest declares contentStage=partial -- a partial inventory can never become a family release package ($contentManifest)."
        }
        if ($cmStage -ne 'complete') {
            throw "Player content manifest declares unknown contentStage '$cmStage' ($contentManifest)."
        }
        if ($cm.candidate -ne $true) {
            throw "Player content manifest marks candidate=false -- this build was never a full-content candidate ($contentManifest)."
        }
        if ([string]$cm.drivingVersion -ne $ExpectedDrivingVersion -or
            [string]$cm.contentVersion -ne $ExpectedContentVersion) {
            throw ("Player content manifest carries mixed source-content identity " +
                "(drivingVersion=$($cm.drivingVersion) contentVersion=$($cm.contentVersion), " +
                "expected $ExpectedDrivingVersion/$ExpectedContentVersion): $contentManifest")
        }
        if (-not $Rehearsal) {
            # The manifest must come from THIS run's build -- an older
            # timestamp means a stale binary is being packaged.
            $written = (Get-Item -LiteralPath $contentManifest).LastWriteTimeUtc
            if ($written -lt $runStart.AddMinutes(-5)) {
                throw "Player content manifest is stale (written $written UTC, this run started $runStart UTC) -- rebuild through the family release entry point."
            }
            $contentStage = 'complete'
        } else {
            $contentStage = $cmStage
        }
    } else {
        # No content manifest at all: the ordinary graybox player writes
        # none. Rehearsal may package it as an honest non-complete stage;
        # the real path demands the release flavor's own manifest.
        if (-not $Rehearsal) {
            throw "Windows player carries no content manifest ($contentManifest) -- the family release flavor writes one after a passed content gate."
        }
        $contentStage = 'graybox'
        $blockers += "windows player has no content manifest: packaged ordinary graybox content, not the production FullContent inventory"
    }
    if ($Rehearsal) {
        $blockers += "rehearsal package built from existing outputs; the production FullContent gate did not produce this player"
    }

    # --- stage: contamination scan ----------------------------------------------
    $allFiles = @(Get-ChildItem -LiteralPath $winSrcFull -Recurse -File -Force)
    foreach ($file in $allFiles) {
        $rel = $file.FullName.Substring($winSrcFull.Length).TrimStart('\', '/')
        $hit = Test-ForbiddenName $rel
        if ($null -ne $hit) {
            throw "Player tree contains forbidden content '$rel' (pattern $hit) -- saves, keys and validation tokens never ship."
        }
    }

    # --- stage: required package documents --------------------------------------
    $noticesSrc = Join-Path $project 'Release\THIRD-PARTY-NOTICES.txt'
    Require-File $noticesSrc 'Third-party notices (Release\THIRD-PARTY-NOTICES.txt)'
    $guideSrc = Join-Path $project 'docs\FAMILY_INSTALL_GUIDE.md'
    Require-File $guideSrc 'Family install guide (docs\FAMILY_INSTALL_GUIDE.md)'

    # --- stage: assemble ---------------------------------------------------------
    # Everything builds under a TEMP staging dir OUTSIDE -Output: any
    # post-assembly failure (archive self-check, missing artifact,
    # forbidden entry) publishes nothing — the output directory is
    # created only after every artifact passed every check.
    $script:staging = Join-Path ([IO.Path]::GetTempPath()) ('family-release-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $stageRoot = Join-Path $script:staging 'FamilyRacing'
    New-Item -ItemType Directory -Path $stageRoot | Out-Null
    foreach ($file in $allFiles) {
        $rel = $file.FullName.Substring($winSrcFull.Length).TrimStart('\', '/')
        if (Test-ExcludedName $rel) { continue }
        $dest = Join-Path $stageRoot ($rel -replace '/', '\')
        New-Item -ItemType Directory -Path (Split-Path $dest -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $dest
    }
    # Include any empty dirs the file walk skipped (e.g. etc/mono/2.0).
    foreach ($dir in @(Get-ChildItem -LiteralPath $winSrcFull -Recurse -Directory -Force)) {
        $rel = $dir.FullName.Substring($winSrcFull.Length).TrimStart('\', '/')
        if (Test-ExcludedName $rel) { continue }
        New-Item -ItemType Directory -Path (Join-Path $stageRoot ($rel -replace '/', '\')) -Force | Out-Null
    }
    Copy-Item -LiteralPath $noticesSrc -Destination (Join-Path $stageRoot 'THIRD-PARTY-NOTICES.txt')
    Copy-Item -LiteralPath $guideSrc -Destination (Join-Path $stageRoot 'INSTALL-GUIDE.txt')

    $zipName = "FamilyRacing-$version-Windows-x64.zip"
    $zipPath = Join-Path $script:staging $zipName
    # Entries are written manually with forward-slash names so the
    # archive uses canonical zip paths (CreateFromDirectory emits
    # backslash names on .NET Framework).
    $stageParent = Split-Path $stageRoot -Parent
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in @(Get-ChildItem -LiteralPath $stageRoot -Recurse -File -Force)) {
            $rel = 'FamilyRacing/' + ($file.FullName.Substring($stageRoot.Length).TrimStart('\', '/') -replace '\\', '/')
            $entry = $zip.CreateEntry($rel, [IO.Compression.CompressionLevel]::Optimal)
            $stream = $entry.Open()
            try {
                $input = [IO.File]::OpenRead($file.FullName)
                try { $input.CopyTo($stream) } finally { $input.Dispose() }
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $zip.Dispose() }

    # --- stage: archive self-check ------------------------------------------------
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try { $entries = @($zip.Entries | ForEach-Object { $_.FullName }) }
    finally { $zip.Dispose() }
    foreach ($required in @('FamilyRacing/FamilyRacing.exe',
            'FamilyRacing/FamilyRacing_Data/Managed/FamilyRacing.Runtime.dll',
            'FamilyRacing/FamilyRacing_Data/level0',
            'FamilyRacing/UnityPlayer.dll',
            'FamilyRacing/THIRD-PARTY-NOTICES.txt',
            'FamilyRacing/INSTALL-GUIDE.txt')) {
        if (@($entries | Where-Object { $_ -eq $required }).Count -eq 0) {
            throw "Assembled archive is missing required entry '$required'."
        }
    }
    if (@($entries | Where-Object { $_ -like 'FamilyRacing/MonoBleedingEdge/etc/mono/*' }).Count -eq 0) {
        throw "Assembled archive carries no Mono runtime configuration."
    }
    foreach ($entry in $entries) {
        $relEntry = ($entry -replace 'FamilyRacing/', '') -replace '/', '\'
        $hit = Test-ForbiddenName $relEntry
        if ($null -ne $hit) {
            throw "Assembled archive contains forbidden entry '$entry' (pattern $hit)."
        }
        if ($entry -match 'release-manifest') {
            throw "Assembled archive must not embed release-manifest.json -- the manifest stays outside the zip."
        }
        if ($entry -match 'DoNotShip|\.pdb$|\.log$') {
            throw "Assembled archive carries a build byproduct '$entry' that must stay out of distribution."
        }
    }

    # --- stage: android artifact ---------------------------------------------------
    $apkName = "FamilyRacing-$version-Android.apk"
    $apkDest = Join-Path $script:staging $apkName
    $apkPackaged = $false
    if ($AndroidSource -and (Test-Path -LiteralPath $AndroidSource -PathType Leaf)) {
        # Bytes are copied unchanged -- the artifact was already verified
        # by the signing pipeline; re-hashing happens after assembly.
        Copy-Item -LiteralPath $AndroidSource -Destination $apkDest
        $apkPackaged = $true
    } elseif (-not $Rehearsal) {
        throw "Signed android artifact missing: $AndroidSource"
    } else {
        $blockers += "android release artifact absent: requires the production export plus the release signing key (test-key rehearsal signing not performed -- no unsigned release APK exists)"
    }

    # --- stage: package docs --------------------------------------------------------
    Copy-Item -LiteralPath $noticesSrc -Destination (Join-Path $script:staging 'THIRD-PARTY-NOTICES.txt')
    Copy-Item -LiteralPath $guideSrc -Destination (Join-Path $script:staging 'INSTALL-GUIDE.txt')
    # The zip source tree is done contributing; drop it before hashing.
    Remove-Item -LiteralPath $stageRoot -Recurse -Force

    # --- stage: manifest (after final assembly; stays OUTSIDE the zip) -------------
    $files = @()
    foreach ($file in @(Get-ChildItem -LiteralPath $script:staging -File | Where-Object { $_.Name -ne 'release-manifest.json' })) {
        $files += [ordered]@{
            path = $file.Name
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            sizeBytes = $file.Length
        }
    }
    $gitVersion = 'unknown'
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $gv = (& git --version 2>$null)
    if ($gv) { $gitVersion = ($gv | Select-Object -First 1) -replace '^git version ', '' }
    $ErrorActionPreference = $oldPreference

    $androidSection = [ordered]@{
        package = $null; sha256 = $null; sizeBytes = $null
        applicationId = $releaseConfig.applicationId
        minSdk = $releaseConfig.androidMinSdkVersion; targetSdk = $releaseConfig.androidTargetSdkVersion
        abi = 'arm64-v8a'; signed = $false
        certificateSha256 = $null; signer = 'none'
    }
    if ($apkPackaged) {
        $apkEntry = @($files | Where-Object { $_.path -eq $apkName })[0]
        $androidSection.package = $apkName
        $androidSection.sha256 = $apkEntry.sha256
        $androidSection.sizeBytes = $apkEntry.sizeBytes
        $androidSection.signed = $true
        $androidSection.certificateSha256 = $releaseConfig.expectedCertificateSha256
        $androidSection.signer = $(if ($Rehearsal) { 'test' } else { 'release' })
    }
    $zipEntry = @($files | Where-Object { $_.path -eq $zipName })[0]
    $windowsSection = [ordered]@{
        package = $zipName; sha256 = $zipEntry.sha256; sizeBytes = $zipEntry.sizeBytes
        arch = 'x64'; backend = 'Mono'; authenticodeSigned = $false
    }

    if (-not $Rehearsal) {
        $blockers += "acceptance evidence absent: device matrix, update/backup qualification and family sessions run in Tasks 5-6"
    } else {
        $blockers += "acceptance evidence absent: rehearsal output proves assembly machinery only"
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        packageKind = $(if ($Rehearsal) { 'rehearsal' } else { 'release' })
        releaseCandidate = $false
        acceptanceStatus = 'unqualified'
        source = [ordered]@{
            commit = $sourceCommit
            cleanSource = $cleanSource
            dirtyAreas = $dirtyAreas
        }
        tools = [ordered]@{
            unityEditor = $editorVersion
            powershell = $PSVersionTable.PSVersion.ToString()
            git = $gitVersion
        }
        identity = [ordered]@{
            versionName = $releaseConfig.versionName
            androidVersionCode = $releaseConfig.androidVersionCode
            previousAndroidVersionCode = $releaseConfig.previousAndroidVersionCode
            applicationId = $releaseConfig.applicationId
            companyName = $releaseConfig.companyName
            productName = $releaseConfig.productName
        }
        gameplay = [ordered]@{
            gameplayVersion = $ExpectedGameplayVersion
            contentVersion = $ExpectedContentVersion
            lanProtocol = $ExpectedLanProtocol
            observationSchema = $ExpectedObservationSchema
            profileSchema = $ExpectedProfileSchema
            backend = 'arcade'
        }
        contentStage = $contentStage
        windows = $windowsSection
        packages = [ordered]@{ android = $androidSection }
        files = $files
        blockers = $blockers
    }
    $manifestPath = Join-Path $script:staging 'release-manifest.json'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

    # --- stage: publish --------------------------------------------------------------
    # Every check has passed and every artifact (zip, apk, docs,
    # manifest) is complete and hashed -- only now does the caller's
    # output directory come into existence, so a failed run can never
    # leave a half-package behind.
    New-Item -ItemType Directory -Path $outputFull | Out-Null
    Move-Item -Path (Join-Path $script:staging '*') -Destination $outputFull -Force
    Write-Host "FamilyRelease: assembled $outputFull (kind=$($manifest.packageKind), stage=$contentStage, acceptance=unqualified)"
    Write-Host "FamilyRelease: manifest $(Join-Path $outputFull 'release-manifest.json')"
    exit 0
}
catch {
    Write-Host "FamilyRelease failed: $($_.Exception.Message)"
    exit 1
}
finally {
    if ($script:staging -and (Test-Path -LiteralPath $script:staging)) {
        Remove-Item -LiteralPath $script:staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}
