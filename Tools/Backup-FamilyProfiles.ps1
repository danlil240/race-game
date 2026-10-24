# Backs up the Family Racing profile store for ONE installation/device.
#
# Scope: only profile-{0..3}.json and their .json.bak siblings inside the
# validated Profiles directory are copied -- never anything else, never
# recursively beyond that directory. The staged copy is hashed and a
# backup-manifest.json records the original installation/device label,
# per-slot profile ids/schemas, exact allowed filenames + SHA-256 and the
# original missing-file states; the manifest is written only after every
# staged byte verifies, so a partial copy is never marked usable.
#
# The game must NOT be running (Windows: process check; Android:
# `adb shell pidof`). The source is read-only for this tool.
#
#   -Platform Windows -ProfilesPath defaults to
#       %USERPROFILE%\AppData\LocalLow\Family Racing\Family Racing\Profiles
#   -Platform Android requires -DeviceSerial; -ProfilesPath is the
#       device-side path and must stay inside the scoped app data dir
#       (/sdcard/Android/data/com.daniel.familyracing/...). Requires USB
#       debugging + shell access to that path -- a non-rooted tablet may
#       refuse; the tool then fails honestly instead of pretending.
#
# -ExpectedManifest is the release-manifest.json that shipped with the
# installed package; its identity fields and profile schema are recorded
# so a later restore can prove it targets the same installation.
param(
    [ValidateSet('Windows', 'Android')]
    [string]$Platform = 'Windows',

    [Parameter(Mandatory = $true)]
    [string]$Destination,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedManifest,

    [string]$ProfilesPath = '',
    [string]$DeviceSerial = '',
    [string]$InstallationLabel = '',
    [string]$AdbPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe',
    # Extra process names to treat as "the game is running"; the built-in
    # set (FamilyRacing / Family Racing) is always checked as well.
    [string[]]$GameProcessNames = @()
)

$ErrorActionPreference = 'Stop'

$AllowedNames = @(
    for ($i = 0; $i -lt 4; $i++) { "profile-$i.json"; "profile-$i.json.bak" }
)
$SupportedProfileSchema = 3
$SlotCount = 4
$PackageId = 'com.daniel.familyracing'
$DefaultWindowsProfiles = Join-Path $env:USERPROFILE 'AppData\LocalLow\Family Racing\Family Racing\Profiles'
$AndroidDataRoots = @(
    '/sdcard/Android/data/com.daniel.familyracing/',
    '/storage/emulated/0/Android/data/com.daniel.familyracing/'
)

function Require-File([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description missing: $Path"
    }
}

function Quote-NativeArgument([string]$Value) {
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Invoke-Adb([string[]]$Arguments) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $AdbPath
    $start.Arguments = ($Arguments | ForEach-Object { Quote-NativeArgument $_ }) -join ' '
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $child = New-Object System.Diagnostics.Process
    $child.StartInfo = $start
    try {
        if (-not $child.Start()) { throw "Process did not start: $AdbPath" }
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        $child.WaitForExit()
        return @{ Code = $child.ExitCode; Text = $stdout.Result + "`r`n" + $stderr.Result }
    }
    finally { $child.Dispose() }
}

function Assert-GameStopped() {
    if ($Platform -eq 'Windows') {
        $names = @('FamilyRacing', 'Family Racing') + $GameProcessNames | Select-Object -Unique
        $running = @(Get-Process -Name $names -ErrorAction SilentlyContinue)
        if ($running.Count -gt 0) {
            throw ("The game appears to be running (process '" + $running[0].Name + "'). Close Family Racing before backing up profiles -- a backup taken mid-save can capture half-written bytes.")
        }
        return
    }
    $pidCheck = Invoke-Adb @('-s', $DeviceSerial, 'shell', 'pidof', $PackageId)
    if ($pidCheck.Code -eq 0 -and $pidCheck.Text.Trim().Length -gt 0) {
        throw ("The game is still running on $DeviceSerial (pidof $PackageId -> $($pidCheck.Text.Trim())). Close it before backing up.")
    }
}

# Classifies one on-disk profile file copy the way the game's own reader
# does at the level this tool can verify: unreadable bytes, invalid shape,
# foreign slot, newer-than-supported schema, or valid.
function Get-ProfileFileInfo([string]$Path, [int]$ExpectedSlot, [int]$MaxSchema) {
    $info = @{ Status = 'unreadable'; SchemaVersion = $null; Slot = $null; Id = $null }
    $j = $null
    try { $j = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    catch { return $info }
    if ($null -eq $j) { return $info }
    $svProp = $j.PSObject.Properties['schemaVersion']
    $slotProp = $j.PSObject.Properties['slot']
    $idProp = $j.PSObject.Properties['id']
    if ($null -eq $svProp -or $null -eq $slotProp -or $null -eq $idProp) {
        $info.Status = 'invalid'; return $info
    }
    try {
        $sv = [long]$svProp.Value; $slot = [long]$slotProp.Value
    } catch { $info.Status = 'invalid'; return $info }
    $info.SchemaVersion = $sv; $info.Slot = $slot
    $info.Id = [string]$idProp.Value
    if ($sv -gt $MaxSchema) { $info.Status = 'newer-schema'; return $info }
    if ($sv -lt 1 -or $slot -lt 0 -or $slot -ge $SlotCount) { $info.Status = 'invalid'; return $info }
    if ($slot -ne $ExpectedSlot) { $info.Status = 'slot-mismatch'; return $info }
    $idOk = $false
    try { $idOk = ([guid]$info.Id) -ne [guid]::Empty } catch { $idOk = $false }
    if (-not $idOk) { $info.Status = 'invalid'; return $info }
    if ($sv -ge 2 -and $null -eq $j.PSObject.Properties['appliedResults']) {
        $info.Status = 'invalid'; return $info
    }
    if ($sv -ge 3) {
        foreach ($f in @('eventProgress', 'worldProgress', 'finaleProgress',
                'vehicleUpgrades', 'ownedCosmetics', 'equippedCosmetics',
                'activeBounties', 'timeTrialRecords', 'purchaseReceipts',
                'pendingRaceContexts')) {
            if ($null -eq $j.PSObject.Properties[$f]) { $info.Status = 'invalid'; return $info }
        }
    }
    $info.Status = 'valid'
    return $info
}

# Reads the installed release manifest and requires the identity fields
# the restore side compares against.
function Read-ExpectedManifest([string]$Path) {
    Require-File $Path 'Expected release manifest'
    $m = $null
    try { $m = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
    catch { throw "Expected release manifest does not parse: $Path ($($_.Exception.Message))" }
    foreach ($field in @('applicationId', 'companyName', 'productName')) {
        if ($null -eq $m.identity -or [string]::IsNullOrEmpty([string]$m.identity.$field)) {
            throw "Expected release manifest lacks identity.$field : $Path"
        }
    }
    if ($null -eq $m.gameplay -or $null -eq $m.gameplay.profileSchema) {
        throw "Expected release manifest lacks gameplay.profileSchema : $Path"
    }
    return @{ Manifest = $m
              Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
}

$exitCode = 1
$staging = $null
try {
    # --- Destination -----------------------------------------------------
    $destFull = [IO.Path]::GetFullPath($Destination)
    if (Test-Path -LiteralPath $destFull) {
        throw "Destination must be a new directory; it already exists: $destFull"
    }
    $destParent = Split-Path $destFull -Parent
    if (-not (Test-Path -LiteralPath $destParent -PathType Container)) {
        throw "Destination parent directory does not exist: $destParent"
    }

    # --- Expected manifest ------------------------------------------------
    $expected = Read-ExpectedManifest $ExpectedManifest
    $profileSchema = [int]$expected.Manifest.gameplay.profileSchema
    if ($profileSchema -lt 1 -or $profileSchema -gt $SupportedProfileSchema) {
        throw "Expected manifest profileSchema $profileSchema is outside this tool's supported range (1..$SupportedProfileSchema)."
    }

    # --- Platform wiring --------------------------------------------------
    $entries = @()
    $ignored = @()
    if ($Platform -eq 'Windows') {
        $profilesResolved = if ($ProfilesPath) { [IO.Path]::GetFullPath($ProfilesPath) } else { $DefaultWindowsProfiles }
        if (-not (Test-Path -LiteralPath $profilesResolved -PathType Container)) {
            throw "Profiles directory missing: $profilesResolved"
        }
        $installationKey = "windows|$env:COMPUTERNAME|$env:USERNAME"
        if (-not $InstallationLabel) {
            $InstallationLabel = "Windows PC $env:COMPUTERNAME (user $env:USERNAME)"
        }
        Assert-GameStopped
        foreach ($item in (Get-ChildItem -LiteralPath $profilesResolved)) {
            if ($AllowedNames -contains $item.Name -and -not $item.PSIsContainer) {
                $entries += $item.Name
            } else {
                $ignored += $item.Name
            }
        }
    } else {
        if ([string]::IsNullOrEmpty($DeviceSerial)) {
            throw 'Android platform requires -DeviceSerial.'
        }
        Require-File $AdbPath 'adb'
        $profilesResolved = if ($ProfilesPath) { $ProfilesPath.Replace('\', '/') } else { "$($AndroidDataRoots[0])files/Profiles" }
        $scoped = $false
        foreach ($root in $AndroidDataRoots) { if ($profilesResolved.StartsWith($root)) { $scoped = $true } }
        if (-not $scoped -or $profilesResolved.Contains('..')) {
            throw "Android -ProfilesPath must stay inside the scoped app data directory (Android/data/$PackageId/); got '$profilesResolved'."
        }
        $state = Invoke-Adb @('-s', $DeviceSerial, 'get-state')
        if ($state.Code -ne 0 -or $state.Text.Trim() -ne 'device') {
            throw "Device $DeviceSerial is not attached/ready (adb get-state: $($state.Text.Trim()))."
        }
        $pm = Invoke-Adb @('-s', $DeviceSerial, 'shell', 'pm', 'path', $PackageId)
        if ($pm.Code -ne 0 -or $pm.Text -notmatch 'package:') {
            throw "Package $PackageId is not installed on $DeviceSerial."
        }
        Assert-GameStopped
        $ls = Invoke-Adb @('-s', $DeviceSerial, 'shell', 'ls', '-a', '-1', $profilesResolved)
        if ($ls.Code -ne 0) {
            throw "Cannot list $profilesResolved on ${DeviceSerial}: $($ls.Text.Trim())"
        }
        foreach ($name in ($ls.Text -split "`r?`n")) {
            $name = $name.Trim()
            if ($name.Length -eq 0) { continue }
            if ($AllowedNames -contains $name) { $entries += $name } else { $ignored += $name }
        }
        $model = (Invoke-Adb @('-s', $DeviceSerial, 'shell', 'getprop', 'ro.product.model')).Text.Trim()
        $installationKey = "android|$DeviceSerial"
        if (-not $InstallationLabel) {
            $InstallationLabel = "Android device $DeviceSerial" + $(if ($model) { " ($model)" } else { '' })
        }
    }

    # --- Stage + hash ------------------------------------------------------
    $staging = Join-Path $destParent ('.staging-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $staging | Out-Null
    $fileRecords = @()
    foreach ($name in $AllowedNames) {
        $slot = [int]($name -replace '^profile-(\d)\..*$', '$1')
        $kind = if ($name.EndsWith('.bak')) { 'bak' } else { 'primary' }
        if ($entries -notcontains $name) {
            $fileRecords += [ordered]@{ name = $name; present = $false; slot = $slot; kind = $kind }
            continue
        }
        $staged = Join-Path $staging $name
        if ($Platform -eq 'Windows') {
            Copy-Item -LiteralPath (Join-Path $profilesResolved $name) -Destination $staged
        } else {
            $pull = Invoke-Adb @('-s', $DeviceSerial, 'pull', ($profilesResolved + '/' + $name), $staged)
            if ($pull.Code -ne 0 -or -not (Test-Path -LiteralPath $staged)) {
                throw "adb pull failed for $profilesResolved/$name : $($pull.Text.Trim())"
            }
        }
        $hash = (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash.ToLowerInvariant()
        $size = (Get-Item -LiteralPath $staged).Length
        $info = Get-ProfileFileInfo $staged $slot $profileSchema
        $fileRecords += [ordered]@{
            name = $name; present = $true; slot = $slot; kind = $kind
            sha256 = $hash; sizeBytes = $size
            profileId = $info.Id; profileSchemaVersion = $info.SchemaVersion
            profileStatus = $info.Status
        }
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        createdUtc = [DateTime]::UtcNow.ToString('o')
        tool = 'Backup-FamilyProfiles.ps1'
        platform = $Platform
        installation = [ordered]@{
            key = $installationKey
            label = $InstallationLabel
            applicationId = [string]$expected.Manifest.identity.applicationId
            companyName = [string]$expected.Manifest.identity.companyName
            productName = [string]$expected.Manifest.identity.productName
            deviceSerial = $(if ($Platform -eq 'Android') { $DeviceSerial } else { $null })
            profilesPath = $profilesResolved
        }
        release = [ordered]@{
            manifestSha256 = $expected.Sha256
            versionName = [string]$expected.Manifest.identity.versionName
            androidVersionCode = $expected.Manifest.identity.androidVersionCode
            applicationId = [string]$expected.Manifest.identity.applicationId
            profileSchema = $profileSchema
        }
        profileSchema = $profileSchema
        slotCount = $SlotCount
        files = $fileRecords
        ignoredEntries = @($ignored)
    }
    ($manifest | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath (Join-Path $staging 'backup-manifest.json') -Encoding UTF8

    # --- Verify staged bytes before publishing -----------------------------
    foreach ($record in $fileRecords) {
        if (-not $record.present) { continue }
        $staged = Join-Path $staging $record.name
        $actual = (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $record.sha256) {
            throw "Staged copy hash mismatch for $($record.name); the backup is not usable."
        }
    }

    # --- Publish -----------------------------------------------------------
    Move-Item -LiteralPath $staging -Destination $destFull
    $staging = $null
    $presentCount = @($fileRecords | Where-Object { $_.present }).Count
    Write-Host "Backup: $presentCount of $($AllowedNames.Count) profile files -> $destFull"
    Write-Host "Backup: installation '$InstallationLabel' ($installationKey), profile schema $profileSchema"
    if ($ignored.Count -gt 0) {
        Write-Host ("Backup: ignored non-profile entries: " + ($ignored -join ', '))
    }
    $exitCode = 0
}
catch {
    Write-Host "Backup failed: $($_.Exception.Message)"
    $exitCode = 1
}
finally {
    if ($null -ne $staging -and (Test-Path -LiteralPath $staging)) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}
exit $exitCode
