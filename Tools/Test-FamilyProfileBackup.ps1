# Behavioral fixture for the family profile backup/recovery tools:
# Tools/Backup-FamilyProfiles.ps1 and Tools/Restore-FamilyProfiles.ps1.
#
# Windows cases run against real temp profile directories; Android cases
# run against the compiled AdbFixture.exe simulating `adb` (device fs is
# a host directory via device-root.txt; devices/pidof/pm behaviors come
# from behavior files -- fixture evidence is never device qualification).
#
# Breaks caught: mismatched/malformed hashes, malformed manifest, path
# traversal, unexpected files, foreign slot/identity, unsupported newer
# schema, running game, disconnected device, partial copy, interrupted
# restore (resume/rollback), non-atomic write attempts, and any write or
# recursive copy/delete outside the validated Profiles directory.
param([string]$ToolsDir = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
$passed = 0
$failed = 0
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('family-backup-fixture-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$fixtureRoot = [IO.Path]::GetFullPath($fixtureRoot)
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null

$AllowedNames = @('profile-0.json', 'profile-0.json.bak',
    'profile-1.json', 'profile-1.json.bak',
    'profile-2.json', 'profile-2.json.bak',
    'profile-3.json', 'profile-3.json.bak')

function Check([string]$Name, [bool]$Condition, [string]$Detail = '') {
    if ($Condition) { $script:passed++; Write-Host "PASS $Name" }
    else { $script:failed++; Write-Host "FAIL $Name ($Detail)" }
}

# Minimal valid profile JSON. Schema 1 lacks the later collections -- the
# tools must accept schema 1..3 like the game's own reader does.
function New-ProfileJson([int]$Slot, [string]$Id, [int]$Schema = 3, [int]$Coins = 10) {
    $p = [ordered]@{
        schemaVersion = $Schema; id = $Id; slot = $Slot; avatar = 'fox'
        colorIndex = $Slot; language = 'en'; camera = 'chase'
        reducedCameraShake = $false; musicVolume = 0.8; effectsVolume = 0.8
        voiceVolume = 1.0; coins = $Coins; unlockedVehicles = @('car-01')
        localRaceOrdinal = 0
    }
    if ($Schema -ge 2) { $p['appliedResults'] = @() }
    if ($Schema -ge 3) {
        foreach ($f in @('eventProgress', 'worldProgress', 'finaleProgress',
                'vehicleUpgrades', 'ownedCosmetics', 'equippedCosmetics',
                'activeBounties', 'timeTrialRecords', 'purchaseReceipts',
                'pendingRaceContexts')) { $p[$f] = @() }
        $p['bountyRotationCursor'] = 0
    }
    return ($p | ConvertTo-Json -Compress -Depth 4)
}

# Builds a Profiles fixture dir. Options: SlotCount, WithBaks, ExtraFile,
# ExtraDir, Unreadable (slot), NewerSchema (slot), ForeignId (slot),
# ForeignSlot (slot), CorruptBak (slot), Schema.
function New-ProfilesDir([string]$Dir, [hashtable]$Opt = @{}) {
    New-Item -ItemType Directory -Path $Dir -Force | Out-Null
    $count = if ($Opt.ContainsKey('SlotCount')) { $Opt.SlotCount } else { 4 }
    $schema = if ($Opt.ContainsKey('Schema')) { $Opt.Schema } else { 3 }
    for ($slot = 0; $slot -lt $count; $slot++) {
        $id = [guid]::NewGuid().ToString()
        $fileSchema = $schema
        if ($Opt.ContainsKey('NewerSchema') -and $Opt.NewerSchema -eq $slot) { $fileSchema = 4 }
        if ($Opt.ContainsKey('ForeignSlot') -and $Opt.ForeignSlot -eq $slot) {
            $json = New-ProfileJson -Slot (($slot + 1) % 4) -Id $id -Schema $schema
        } else {
            $json = New-ProfileJson -Slot $slot -Id $id -Schema $fileSchema -Coins (10 + $slot)
        }
        if ($Opt.ContainsKey('ForeignId') -and $Opt.ForeignId -eq $slot) {
            $json = New-ProfileJson -Slot $slot -Id ([guid]::NewGuid().ToString()) -Schema $schema
        }
        if ($Opt.ContainsKey('Unreadable') -and $Opt.Unreadable -eq $slot) {
            $json = 'corrupt{not json!!'
        }
        Set-Content -LiteralPath (Join-Path $Dir "profile-$slot.json") -Value $json -NoNewline -Encoding UTF8
        if ($Opt.ContainsKey('WithBaks') -and $Opt.WithBaks) {
            $bak = if ($Opt.ContainsKey('CorruptBak') -and $Opt.CorruptBak -eq $slot) { 'corrupt bak bytes' } else { $json }
            Set-Content -LiteralPath (Join-Path $Dir "profile-$slot.json.bak") -Value $bak -NoNewline -Encoding UTF8
        }
    }
    if ($Opt.ContainsKey('ExtraFile')) {
        Set-Content -LiteralPath (Join-Path $Dir 'notes.txt') -Value 'not a profile' -NoNewline
    }
    if ($Opt.ContainsKey('ExtraDir')) {
        New-Item -ItemType Directory -Path (Join-Path $Dir 'extra-subdir') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $Dir 'extra-subdir\junk.tmp') -Value 'junk' -NoNewline
    }
}

# Minimal release manifest fixture (schema 1 shape: identity + gameplay).
function Write-FixtureReleaseManifest([string]$Path, [string]$AppId = 'com.daniel.familyracing',
        [int]$ProfileSchema = 3) {
    $m = [ordered]@{
        schemaVersion = 1; packageKind = 'rehearsal'; acceptanceStatus = 'unqualified'
        identity = [ordered]@{
            versionName = '1.0.0'; androidVersionCode = 1; previousAndroidVersionCode = 0
            applicationId = $AppId; companyName = 'Family Racing'; productName = 'Family Racing'
        }
        gameplay = [ordered]@{
            gameplayVersion = 'gameplay-4'; contentVersion = 'full-content-1'
            lanProtocol = 3; observationSchema = 7; profileSchema = $ProfileSchema; backend = 'arcade'
        }
        files = @()
    }
    ($m | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Read-BackupManifest([string]$BackupDir) {
    return Get-Content -LiteralPath (Join-Path $BackupDir 'backup-manifest.json') -Raw | ConvertFrom-Json
}

# Top-level file fingerprint map (name -> sha256) for "bytes preserved"
# assertions; subdirectories (restore journals) are ignored.
function Get-FileMap([string]$Dir) {
    $map = @{}
    if (-not (Test-Path -LiteralPath $Dir)) { return $map }
    foreach ($f in (Get-ChildItem -LiteralPath $Dir -File -ErrorAction SilentlyContinue)) {
        $map[$f.Name] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $map
}

function Compare-FileMap($A, $B) {
    if ($A.Count -ne $B.Count) { return $false }
    foreach ($k in $A.Keys) { if (-not $B.ContainsKey($k) -or $B[$k] -ne $A[$k]) { return $false } }
    return $true
}

function Update-BackupManifestEntry([string]$BackupDir, [string]$Name, [hashtable]$Fields) {
    $path = Join-Path $BackupDir 'backup-manifest.json'
    $m = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    foreach ($f in $m.files) {
        if ($f.name -eq $Name) { foreach ($k in $Fields.Keys) { $f.$k = $Fields[$k] } }
    }
    ($m | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $path -Encoding UTF8
}

# Re-hash a tampered backup file so the manifest stays consistent -- used
# to fire semantic checks (slot/id/schema) rather than the hash check.
function Sync-BackupEntry([string]$BackupDir, [string]$Name, [hashtable]$Extra = @{}) {
    $f = Join-Path $BackupDir $Name
    $fields = @{ sha256 = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLowerInvariant()
                 sizeBytes = (Get-Item -LiteralPath $f).Length }
    foreach ($k in $Extra.Keys) { $fields[$k] = $Extra[$k] }
    Update-BackupManifestEntry $BackupDir $Name $fields
}

function Invoke-Tool([string]$Script, [string[]]$ToolArgs) {
    $oldPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Script @ToolArgs 2>&1 | Out-String
    $code = $LASTEXITCODE
    $ErrorActionPreference = $oldPreference
    return @{ ExitCode = $code; Output = $output }
}

# Runs Backup-FamilyProfiles.ps1. $Opt: Platform, ProfilesPath, Destination,
# ExpectedManifest, DeviceSerial, AdbPath, GameProcessNames, ExtraArgs.
function Run-BackupCase([string]$Name, [hashtable]$Opt) {
    $tool = Join-Path $ToolsDir 'Backup-FamilyProfiles.ps1'
    $args = @()
    if ($Opt.ContainsKey('Platform')) { $args += @('-Platform', $Opt.Platform) }
    if ($Opt.ContainsKey('Destination')) { $args += @('-Destination', $Opt.Destination) }
    if ($Opt.ContainsKey('ExpectedManifest')) { $args += @('-ExpectedManifest', $Opt.ExpectedManifest) }
    if ($Opt.ContainsKey('ProfilesPath')) { $args += @('-ProfilesPath', $Opt.ProfilesPath) }
    if ($Opt.ContainsKey('DeviceSerial')) { $args += @('-DeviceSerial', $Opt.DeviceSerial) }
    if ($Opt.ContainsKey('AdbPath')) { $args += @('-AdbPath', $Opt.AdbPath) }
    if ($Opt.ContainsKey('GameProcessNames')) { $args += @('-GameProcessNames', ($Opt.GameProcessNames -join ',')) }
    if ($Opt.ContainsKey('ExtraArgs')) { $args += $Opt.ExtraArgs }
    return Invoke-Tool $tool $args
}

# Runs Restore-FamilyProfiles.ps1. $Opt adds Backup, Apply, Resume, Rollback,
# InterruptAfterFiles on top of the shared parameters.
function Run-RestoreCase([hashtable]$Opt) {
    $tool = Join-Path $ToolsDir 'Restore-FamilyProfiles.ps1'
    $args = @()
    if ($Opt.ContainsKey('Platform')) { $args += @('-Platform', $Opt.Platform) }
    if ($Opt.ContainsKey('Backup')) { $args += @('-Backup', $Opt.Backup) }
    if ($Opt.ContainsKey('ExpectedManifest')) { $args += @('-ExpectedManifest', $Opt.ExpectedManifest) }
    if ($Opt.ContainsKey('ProfilesPath')) { $args += @('-ProfilesPath', $Opt.ProfilesPath) }
    if ($Opt.ContainsKey('DeviceSerial')) { $args += @('-DeviceSerial', $Opt.DeviceSerial) }
    if ($Opt.ContainsKey('AdbPath')) { $args += @('-AdbPath', $Opt.AdbPath) }
    if ($Opt.ContainsKey('GameProcessNames')) { $args += @('-GameProcessNames', ($Opt.GameProcessNames -join ',')) }
    if ($Opt.Apply) { $args += '-Apply' }
    if ($Opt.Resume) { $args += '-Resume' }
    if ($Opt.Rollback) { $args += '-Rollback' }
    if ($Opt.ContainsKey('InterruptAfterFiles')) { $args += @('-InterruptAfterFiles', [string]$Opt.InterruptAfterFiles) }
    if ($Opt.ContainsKey('ExtraArgs')) { $args += $Opt.ExtraArgs }
    return Invoke-Tool $tool $args
}

# Creates a case dir with a Profiles dir + release manifest; runs backup;
# returns @{ Profiles; Backup; Manifest; Run; Map }.
function New-BackedUpCase([string]$Name, [hashtable]$Opt = @{}) {
    $dir = Join-Path $fixtureRoot $Name
    $profiles = Join-Path $dir 'Profiles'
    $profilesOpt = if ($Opt.ContainsKey('ProfilesOpt')) { $Opt.ProfilesOpt } else { @{ WithBaks = $true } }
    New-ProfilesDir $profiles $profilesOpt
    $manifest = Join-Path $dir 'release-manifest.json'
    Write-FixtureReleaseManifest $manifest
    $backup = Join-Path $dir 'backup'
    $run = Run-BackupCase $Name @{
        Destination = $backup; ExpectedManifest = $manifest; ProfilesPath = $profiles
    }
    return @{ Dir = $dir; Profiles = $profiles; Backup = $backup
              Manifest = $manifest; Run = $run; Map = (Get-FileMap $profiles) }
}

# Fake adb device tree for Android cases. Options: NoDevice, OfflineDevice,
# Pid (string), NotInstalled, Serial.
function New-AdbCase([string]$Name, [hashtable]$Opt = @{}) {
    $dir = Join-Path $fixtureRoot $Name
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Copy-Item -LiteralPath $adbExe -Destination (Join-Path $dir 'adb.exe')
    $serial = if ($Opt.ContainsKey('Serial')) { $Opt.Serial } else { 'SM-T220-TEST1' }
    if ($Opt.ContainsKey('NoDevice')) {
        Set-Content -LiteralPath (Join-Path $dir 'devices.txt') -Value '' -NoNewline
    } elseif ($Opt.ContainsKey('OfflineDevice')) {
        Set-Content -LiteralPath (Join-Path $dir 'devices.txt') -Value "$serial`toffline" -NoNewline
    } else {
        $lines = "$serial`tdevice"
        if ($Opt.ContainsKey('ExtraSerial')) { $lines += "`n" + $Opt.ExtraSerial + "`tdevice" }
        Set-Content -LiteralPath (Join-Path $dir 'devices.txt') -Value $lines -NoNewline
    }
    if ($Opt.ContainsKey('Pid')) {
        Set-Content -LiteralPath (Join-Path $dir 'pidof.txt') -Value $Opt.Pid -NoNewline
    }
    if ($Opt.ContainsKey('NotInstalled')) {
        Set-Content -LiteralPath (Join-Path $dir 'installed.txt') -Value 'com.example.other' -NoNewline
    }
    $root = Join-Path $dir 'device-root'
    Set-Content -LiteralPath (Join-Path $dir 'device-root.txt') -Value $root -NoNewline
    $devProfiles = '/sdcard/Android/data/com.daniel.familyracing/files/Profiles'
    $hostProfiles = Join-Path $root 'sdcard\Android\data\com.daniel.familyracing\files\Profiles'
    if (-not $Opt.ContainsKey('NoProfilesDir')) {
        New-ProfilesDir $hostProfiles @{ WithBaks = $true }
    }
    return @{ Dir = $dir; Adb = (Join-Path $dir 'adb.exe'); Serial = $serial
              DevProfiles = $devProfiles; HostProfiles = $hostProfiles; Root = $root }
}

try {
    # Compile the adb fixture.
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $adbExe = Join-Path $fixtureRoot 'adb-fixture\adb.exe'
    New-Item -ItemType Directory -Path (Split-Path $adbExe) -Force | Out-Null
    & $compiler /nologo /target:exe "/out:$adbExe" (Join-Path $ToolsDir 'Tests\AdbFixture.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile the adb fixture.' }

    # ============================ WINDOWS BACKUP =========================
    $case = New-BackedUpCase 'b-happy' @{ ProfilesOpt = @{ WithBaks = $true; ExtraFile = $true; ExtraDir = $true } }
    Check 'backup: happy path exits 0 and creates destination' `
        ($case.Run.ExitCode -eq 0 -and (Test-Path -LiteralPath $case.Backup)) $case.Run.Output
    $bm = $null
    if (Test-Path -LiteralPath (Join-Path $case.Backup 'backup-manifest.json')) {
        $bm = Read-BackupManifest $case.Backup
    }
    Check 'backup: manifest is schema 1 with platform/installation identity' `
        ($null -ne $bm -and $bm.schemaVersion -eq 1 -and $bm.platform -eq 'Windows' -and
         $bm.installation.key -match '^windows\|' -and
         $bm.installation.applicationId -eq 'com.daniel.familyracing') 'manifest missing/invalid'
    Check 'backup: manifest records all 8 allowed names with present flags' `
        ($null -ne $bm -and @($bm.files).Count -eq 8 -and
         @($bm.files | Where-Object { $AllowedNames -notcontains $_.name }).Count -eq 0 -and
         @($bm.files | Where-Object { $_.present -eq $true }).Count -eq 8) `
        (($bm.files | ForEach-Object { $_.name }) -join ',')
    Check 'backup: manifest records per-file slot, id, schema and hash' `
        ($null -ne $bm -and @($bm.files | Where-Object {
            $_.present -and $_.sha256 -match '^[0-9a-f]{64}$' -and $_.slot -ge 0 -and
            $_.profileId -match '^[0-9a-f-]{36}$' -and $_.profileSchemaVersion -eq 3 -and
            $_.profileStatus -eq 'valid' }).Count -eq 8) 'per-file fields wrong'
    Check 'backup: manifest records expected-manifest hash and profile schema' `
        ($null -ne $bm -and $bm.release.manifestSha256 -match '^[0-9a-f]{64}$' -and
         $bm.release.profileSchema -eq 3 -and $bm.release.applicationId -eq 'com.daniel.familyracing') 'release block wrong'
    Check 'backup: only allowed names copied (no extra files/dirs)' `
        (@(Get-ChildItem -LiteralPath $case.Backup -ErrorAction SilentlyContinue | Where-Object {
            $_.Name -ne 'backup-manifest.json' -and $AllowedNames -notcontains $_.Name }).Count -eq 0 -and
         @($bm.files | Where-Object { $_.name -eq 'notes.txt' }).Count -eq 0) `
        ((Get-ChildItem -LiteralPath $case.Backup -Recurse -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) -join ',')
    Check 'backup: source directory is read-only (fingerprint unchanged)' `
        (Compare-FileMap $case.Map (Get-FileMap $case.Profiles)) 'source modified'

    $case = New-BackedUpCase 'b-missing-slots' @{ ProfilesOpt = @{ SlotCount = 2; WithBaks = $false } }
    $bm = if (Test-Path -LiteralPath (Join-Path $case.Backup 'backup-manifest.json')) { Read-BackupManifest $case.Backup } else { $null }
    Check 'backup: missing slots/baks recorded as present:false' `
        ($case.Run.ExitCode -eq 0 -and $null -ne $bm -and
         @($bm.files | Where-Object { $_.name -eq 'profile-2.json' -and $_.present -eq $false }).Count -eq 1 -and
         @($bm.files | Where-Object { $_.name -eq 'profile-0.json.bak' -and $_.present -eq $false }).Count -eq 1 -and
         @($bm.files | Where-Object { $_.present -eq $true }).Count -eq 2) $case.Run.Output

    $dir = Join-Path $fixtureRoot 'b-existing'
    New-ProfilesDir (Join-Path $dir 'Profiles') @{}
    $dest = Join-Path $dir 'backup'
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Write-FixtureReleaseManifest (Join-Path $dir 'release-manifest.json')
    $run = Run-BackupCase 'b-existing' @{
        Destination = $dest; ExpectedManifest = (Join-Path $dir 'release-manifest.json')
        ProfilesPath = (Join-Path $dir 'Profiles') }
    Check 'backup: existing destination is refused' ($run.ExitCode -ne 0 -and $run.Output -match 'exist') $run.Output

    $run = Run-BackupCase 'b-noprofiles' @{
        Destination = (Join-Path $fixtureRoot 'b-noprofiles\backup')
        ExpectedManifest = (Join-Path $fixtureRoot 'b-noprofiles-missing.json')
        ProfilesPath = (Join-Path $fixtureRoot 'b-noprofiles\Profiles') }
    Check 'backup: missing profiles directory fails' ($run.ExitCode -ne 0) $run.Output

    $dir = Join-Path $fixtureRoot 'b-nomanifest'
    New-ProfilesDir (Join-Path $dir 'Profiles') @{}
    $run = Run-BackupCase 'b-nomanifest' @{
        Destination = (Join-Path $dir 'backup')
        ExpectedManifest = (Join-Path $dir 'no-such-manifest.json')
        ProfilesPath = (Join-Path $dir 'Profiles') }
    Check 'backup: missing expected manifest fails' ($run.ExitCode -ne 0 -and $run.Output -match 'manifest') $run.Output

    # Running-game refusal: a live process named in the check list.
    $dir = Join-Path $fixtureRoot 'b-running'
    New-ProfilesDir (Join-Path $dir 'Profiles') @{}
    Write-FixtureReleaseManifest (Join-Path $dir 'release-manifest.json')
    $notepad = Start-Process notepad.exe -PassThru
    try {
        $run = Run-BackupCase 'b-running' @{
            Destination = (Join-Path $dir 'backup')
            ExpectedManifest = (Join-Path $dir 'release-manifest.json')
            ProfilesPath = (Join-Path $dir 'Profiles')
            GameProcessNames = @('notepad') }
    } finally { Stop-Process -Id $notepad.Id -Force -ErrorAction SilentlyContinue }
    Check 'backup: refuses while a game process is running' `
        ($run.ExitCode -ne 0 -and $run.Output -match 'notepad' -and
         -not (Test-Path -LiteralPath (Join-Path $dir 'backup'))) $run.Output

    # ============================ WINDOWS RESTORE ========================
    # Preview must write nothing.
    $case = New-BackedUpCase 'r-preview'
    Remove-Item -LiteralPath (Join-Path $case.Profiles 'profile-1.json') -Force
    $preMap = Get-FileMap $case.Profiles
    $run = Run-RestoreCase @{
        Backup = $case.Backup; ExpectedManifest = $case.Manifest; ProfilesPath = $case.Profiles }
    Check 'restore: preview exits 0, reports plan, writes nothing' `
        ($run.ExitCode -eq 0 -and $run.Output -match 'profile-1\.json' -and
         (Compare-FileMap $preMap (Get-FileMap $case.Profiles)) -and
         @(Get-ChildItem -LiteralPath $case.Profiles -Directory -Filter '.family-restore-*' -ErrorAction SilentlyContinue).Count -eq 0) $run.Output

    # Full round-trip after total profile loss.
    $case = New-BackedUpCase 'r-roundtrip'
    $backupMap = Get-FileMap $case.Backup
    Remove-Item -LiteralPath $case.Profiles -Recurse -Force
    New-Item -ItemType Directory -Path $case.Profiles | Out-Null
    $run = Run-RestoreCase @{
        Backup = $case.Backup; ExpectedManifest = $case.Manifest
        ProfilesPath = $case.Profiles; Apply = $true }
    $restoredMap = Get-FileMap $case.Profiles
    $expected = @{}
    foreach ($n in $AllowedNames) { $expected[$n] = $backupMap[$n] }
    Check 'restore: apply round-trips every present file byte-identically' `
        ($run.ExitCode -eq 0 -and (Compare-FileMap $expected $restoredMap)) $run.Output
    Check 'restore: completed journal + pre-restore snapshot retained' `
        (@(Get-ChildItem -LiteralPath $case.Profiles -Directory -Filter '.family-restore-*' -ErrorAction SilentlyContinue |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'journal.json') }).Count -ge 1) 'no journal'

    # No-op apply over an intact directory.
    $case = New-BackedUpCase 'r-noop'
    $run = Run-RestoreCase @{
        Backup = $case.Backup; ExpectedManifest = $case.Manifest
        ProfilesPath = $case.Profiles; Apply = $true }
    Check 'restore: identical files are skipped (no-op apply)' `
        ($run.ExitCode -eq 0 -and (Compare-FileMap $case.Map (Get-FileMap $case.Profiles))) $run.Output

    # ---- validation failures: current bytes must survive every one ----
    function Test-RestoreFailurePreserves([string]$Name, [scriptblock]$Tamper, [string]$ExpectPattern) {
        $safe = ($Name -replace '[^a-zA-Z0-9]', '-')
        $c = New-BackedUpCase ('r-fail-' + $safe.Substring(0, [Math]::Min(40, $safe.Length)))
        try { & $Tamper $c; $tamperError = '' }
        catch { $tamperError = $_.Exception.Message }
        if ($tamperError -ne '') {
            Check $Name $false ('tamper fixture failed: ' + $tamperError)
            return
        }
        $preMap = Get-FileMap $c.Profiles
        $r = Run-RestoreCase @{
            Backup = $c.Backup; ExpectedManifest = $c.Manifest
            ProfilesPath = $c.Profiles; Apply = $true }
        $preserved = Compare-FileMap $preMap (Get-FileMap $c.Profiles)
        Check $Name ($r.ExitCode -ne 0 -and $r.Output -match $ExpectPattern -and $preserved) $r.Output
    }

    Test-RestoreFailurePreserves 'restore: tampered backup file fails hash check' {
        param($c)
        Add-Content -LiteralPath (Join-Path $c.Backup 'profile-1.json') -Value ' ' -NoNewline
    } 'hash|mismatch|checksum'

    Test-RestoreFailurePreserves 'restore: malformed backup manifest fails' {
        param($c)
        Set-Content -LiteralPath (Join-Path $c.Backup 'backup-manifest.json') -Value '{ not json' -NoNewline
    } 'manifest|parse'

    Test-RestoreFailurePreserves 'restore: malformed hash entry fails' {
        param($c)
        Update-BackupManifestEntry $c.Backup 'profile-1.json' @{ sha256 = 'nothex' }
    } 'hash|sha256|manifest'

    Test-RestoreFailurePreserves 'restore: path traversal name fails' {
        param($c)
        Update-BackupManifestEntry $c.Backup 'profile-1.json' @{ name = '..\evil.json' }
    } 'name|path|manifest'

    Test-RestoreFailurePreserves 'restore: disallowed file name fails' {
        param($c)
        Update-BackupManifestEntry $c.Backup 'profile-1.json' @{ name = 'profile-9.json' }
    } 'name|manifest'

    Test-RestoreFailurePreserves 'restore: unexpected file in backup dir fails' {
        param($c)
        Set-Content -LiteralPath (Join-Path $c.Backup 'evil.txt') -Value 'x' -NoNewline
    } 'unexpected|extra'

    Test-RestoreFailurePreserves 'restore: missing manifest-listed file fails (partial copy)' {
        param($c)
        Remove-Item -LiteralPath (Join-Path $c.Backup 'profile-2.json') -Force
    } 'missing|present'

    Test-RestoreFailurePreserves 'restore: foreign slot inside file fails' {
        param($c)
        $path = Join-Path $c.Backup 'profile-1.json'
        $j = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $j.slot = 0
        ($j | ConvertTo-Json -Compress -Depth 6) | Set-Content -LiteralPath $path -NoNewline -Encoding UTF8
        Sync-BackupEntry $c.Backup 'profile-1.json'
    } 'slot'

    Test-RestoreFailurePreserves 'restore: file id differing from manifest fails' {
        param($c)
        Update-BackupManifestEntry $c.Backup 'profile-1.json' @{ profileId = [guid]::NewGuid().ToString() }
    } 'id|identity'

    Test-RestoreFailurePreserves 'restore: unsupported newer schema in backup fails' {
        param($c)
        $path = Join-Path $c.Backup 'profile-1.json'
        $j = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $j.schemaVersion = 4
        ($j | ConvertTo-Json -Compress -Depth 6) | Set-Content -LiteralPath $path -NoNewline -Encoding UTF8
        Sync-BackupEntry $c.Backup 'profile-1.json' @{ profileSchemaVersion = 4 }
    } 'schema|newer'

    Test-RestoreFailurePreserves 'restore: foreign current profile id is refused' {
        param($c)
        Set-Content -LiteralPath (Join-Path $c.Profiles 'profile-0.json') `
            -Value (New-ProfileJson -Slot 0 -Id ([guid]::NewGuid().ToString())) -NoNewline -Encoding UTF8
    } 'foreign|identity|different profile'

    Test-RestoreFailurePreserves 'restore: foreign installation key fails' {
        param($c)
        $path = Join-Path $c.Backup 'backup-manifest.json'
        $m = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $m.installation.key = 'windows|OTHER-PC|otheruser'
        ($m | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $path -Encoding UTF8
    } 'installation|identity'

    Test-RestoreFailurePreserves 'restore: foreign applicationId manifest fails' {
        param($c)
        Write-FixtureReleaseManifest $c.Manifest -AppId 'com.example.other'
    } 'applicationId|identity'

    # Newer-schema and unreadable CURRENT files: preserved, named, others restore.
    $case = New-BackedUpCase 'r-newer-current'
    Remove-Item -LiteralPath (Join-Path $case.Profiles 'profile-2.json') -Force
    $newerBytes = New-ProfileJson -Slot 1 -Id ([guid]::NewGuid().ToString()) -Schema 4
    Set-Content -LiteralPath (Join-Path $case.Profiles 'profile-1.json') -Value $newerBytes -NoNewline -Encoding UTF8
    $newerHash = (Get-FileHash -LiteralPath (Join-Path $case.Profiles 'profile-1.json') -Algorithm SHA256).Hash
    $run = Run-RestoreCase @{
        Backup = $case.Backup; ExpectedManifest = $case.Manifest
        ProfilesPath = $case.Profiles; Apply = $true }
    Check 'restore: newer-schema current file is preserved untouched' `
        ($run.ExitCode -eq 0 -and
         (Get-FileHash -LiteralPath (Join-Path $case.Profiles 'profile-1.json') -Algorithm SHA256).Hash -eq $newerHash -and
         $run.Output -match 'profile-1\.json' -and $run.Output -match 'preserv|newer|untouched') $run.Output
    Check 'restore: other slots still restore around a preserved file' `
        ((Get-FileMap $case.Profiles)['profile-2.json'] -eq (Get-FileMap $case.Backup)['profile-2.json']) 'slot 2 not restored'

    $case = New-BackedUpCase 'r-unreadable-current'
    Set-Content -LiteralPath (Join-Path $case.Profiles 'profile-0.json') -Value 'garbage{{{not-json' -NoNewline
    Remove-Item -LiteralPath (Join-Path $case.Profiles 'profile-3.json') -Force
    $run = Run-RestoreCase @{
        Backup = $case.Backup; ExpectedManifest = $case.Manifest
        ProfilesPath = $case.Profiles; Apply = $true }
    Check 'restore: unreadable current file is preserved untouched' `
        ($run.ExitCode -eq 0 -and
         (Get-Content -LiteralPath (Join-Path $case.Profiles 'profile-0.json') -Raw) -eq 'garbage{{{not-json' -and
         $run.Output -match 'profile-0\.json') $run.Output
    Check 'restore: backup copy of an unreadable-marked file restores verbatim' `
        ((Get-FileMap $case.Profiles)['profile-3.json'] -eq (Get-FileMap $case.Backup)['profile-3.json']) 'slot 3 not restored'

    # Backup of a corrupt .bak round-trips verbatim (bytes are data too).
    $case = New-BackedUpCase 'r-corrupt-bak' @{ ProfilesOpt = @{ WithBaks = $true; CorruptBak = 1 } }
    Remove-Item -LiteralPath (Join-Path $case.Profiles 'profile-1.json.bak') -Force
    $run = Run-RestoreCase @{
        Backup = $case.Backup; ExpectedManifest = $case.Manifest
        ProfilesPath = $case.Profiles; Apply = $true }
    Check 'restore: unreadable backup member restores byte-for-byte' `
        ($run.ExitCode -eq 0 -and
         (Get-Content -LiteralPath (Join-Path $case.Profiles 'profile-1.json.bak') -Raw) -eq 'corrupt bak bytes') $run.Output

    # Running game blocks restore.
    $case = New-BackedUpCase 'r-running'
    Remove-Item -LiteralPath (Join-Path $case.Profiles 'profile-0.json') -Force
    $preMap = Get-FileMap $case.Profiles
    $notepad = Start-Process notepad.exe -PassThru
    try {
        $run = Run-RestoreCase @{
            Backup = $case.Backup; ExpectedManifest = $case.Manifest
            ProfilesPath = $case.Profiles; Apply = $true; GameProcessNames = @('notepad') }
    } finally { Stop-Process -Id $notepad.Id -Force -ErrorAction SilentlyContinue }
    Check 'restore: refuses while a game process is running' `
        ($run.ExitCode -ne 0 -and $run.Output -match 'notepad' -and
         (Compare-FileMap $preMap (Get-FileMap $case.Profiles))) $run.Output

    # Missing inputs.
    $run = Run-RestoreCase @{
        Backup = (Join-Path $fixtureRoot 'no-such-backup')
        ExpectedManifest = (Join-Path $fixtureRoot 'r-preview\release-manifest.json')
        ProfilesPath = (Join-Path $fixtureRoot 'r-preview\Profiles'); Apply = $true }
    Check 'restore: nonexistent backup dir fails' ($run.ExitCode -ne 0) $run.Output
    $case = New-BackedUpCase 'r-nomanifest'
    Remove-Item -LiteralPath (Join-Path $case.Backup 'backup-manifest.json') -Force -ErrorAction SilentlyContinue
    $run = Run-RestoreCase @{
        Backup = $case.Backup; ExpectedManifest = $case.Manifest
        ProfilesPath = $case.Profiles; Apply = $true }
    Check 'restore: missing backup manifest fails' ($run.ExitCode -ne 0 -and $run.Output -match 'manifest') $run.Output
    $case = New-BackedUpCase 'r-noexpected'
    $run = Run-RestoreCase @{
        Backup = $case.Backup
        ExpectedManifest = (Join-Path $case.Dir 'no-such.json')
        ProfilesPath = $case.Profiles; Apply = $true }
    Check 'restore: missing expected manifest fails' ($run.ExitCode -ne 0 -and $run.Output -match 'manifest') $run.Output

    # Extra files in the profiles dir are never touched; backup dir is read-only.
    $case = New-BackedUpCase 'r-extras'
    Set-Content -LiteralPath (Join-Path $case.Profiles 'keep-me.txt') -Value 'precious' -NoNewline
    $backupMap = Get-FileMap $case.Backup
    Remove-Item -LiteralPath (Join-Path $case.Profiles 'profile-0.json') -Force
    $run = Run-RestoreCase @{
        Backup = $case.Backup; ExpectedManifest = $case.Manifest
        ProfilesPath = $case.Profiles; Apply = $true }
    Check 'restore: non-profile files in the profiles dir are untouched' `
        ($run.ExitCode -eq 0 -and
         (Get-Content -LiteralPath (Join-Path $case.Profiles 'keep-me.txt') -Raw) -eq 'precious') $run.Output
    Check 'restore: backup directory is read-only during restore' `
        (Compare-FileMap $backupMap (Get-FileMap $case.Backup)) 'backup modified'

    # ===================== INTERRUPTED RESTORE ===========================
    $case = New-BackedUpCase 'i-interrupt'
    Remove-Item -LiteralPath (Join-Path $case.Profiles 'profile-0.json'),
        (Join-Path $case.Profiles 'profile-1.json'), (Join-Path $case.Profiles 'profile-2.json') -Force
    $preMap = Get-FileMap $case.Profiles
    $run = Run-RestoreCase @{
        Backup = $case.Backup; ExpectedManifest = $case.Manifest
        ProfilesPath = $case.Profiles; Apply = $true; InterruptAfterFiles = 1 }
    $journalDir = @(Get-ChildItem -LiteralPath $case.Profiles -Directory -Filter '.family-restore-*' -ErrorAction SilentlyContinue)
    $journalState = ''
    if ($journalDir.Count -eq 1 -and (Test-Path -LiteralPath (Join-Path $journalDir[0].FullName 'journal.json'))) {
        $journalState = (Get-Content -LiteralPath (Join-Path $journalDir[0].FullName 'journal.json') -Raw | ConvertFrom-Json).state
    }
    Check 'restore: interrupted apply leaves an applying journal' `
        ($run.ExitCode -ne 0 -and $journalState -eq 'applying') `
        ($run.Output + ' exit=' + $run.ExitCode)
    $run2 = Run-RestoreCase @{
        Backup = $case.Backup; ExpectedManifest = $case.Manifest
        ProfilesPath = $case.Profiles; Apply = $true }
    Check 'restore: new apply refuses while an interrupted restore is pending' `
        ($run2.ExitCode -ne 0 -and $run2.Output -match 'interrupt|resume|rollback|journal') $run2.Output
    $run3 = Run-RestoreCase @{ ProfilesPath = $case.Profiles; Resume = $true }
    $restoredMap = Get-FileMap $case.Profiles
    $backupMap = Get-FileMap $case.Backup
    $allRestored = $true
    foreach ($n in $AllowedNames) { if ($restoredMap[$n] -ne $backupMap[$n]) { $allRestored = $false } }
    Check 'restore: -Resume completes the interrupted restore byte-identically' `
        ($run3.ExitCode -eq 0 -and $allRestored) $run3.Output

    $case = New-BackedUpCase 'i-rollback'
    Remove-Item -LiteralPath (Join-Path $case.Profiles 'profile-1.json'),
        (Join-Path $case.Profiles 'profile-2.json') -Force
    $preMap = Get-FileMap $case.Profiles
    $run = Run-RestoreCase @{
        Backup = $case.Backup; ExpectedManifest = $case.Manifest
        ProfilesPath = $case.Profiles; Apply = $true; InterruptAfterFiles = 1 }
    $run4 = Run-RestoreCase @{ ProfilesPath = $case.Profiles; Rollback = $true }
    Check 'restore: -Rollback restores the exact pre-restore state' `
        ($run4.ExitCode -eq 0 -and (Compare-FileMap $preMap (Get-FileMap $case.Profiles))) `
        ($run4.Output + ' exit=' + $run4.ExitCode)

    # ============================ ANDROID ================================
    $ac = New-AdbCase 'a-backup-ok'
    $dest = Join-Path $ac.Dir 'backup'
    Write-FixtureReleaseManifest (Join-Path $ac.Dir 'release-manifest.json')
    $run = Run-BackupCase 'a-backup-ok' @{
        Platform = 'Android'; Destination = $dest
        ExpectedManifest = (Join-Path $ac.Dir 'release-manifest.json')
        ProfilesPath = $ac.DevProfiles; DeviceSerial = $ac.Serial; AdbPath = $ac.Adb }
    $bm = if (Test-Path -LiteralPath (Join-Path $dest 'backup-manifest.json')) { Read-BackupManifest $dest } else { $null }
    Check 'android backup: pulls scoped files and exits 0' `
        ($run.ExitCode -eq 0 -and $null -ne $bm -and
         @($bm.files | Where-Object { $_.present -eq $true }).Count -eq 8) $run.Output
    Check 'android backup: manifest records platform + device serial' `
        ($null -ne $bm -and $bm.platform -eq 'Android' -and
         $bm.installation.deviceSerial -eq $ac.Serial -and
         $bm.installation.key -eq ('android|' + $ac.Serial)) 'identity wrong'

    $ac = New-AdbCase 'a-backup-nodevice' @{ NoDevice = $true }
    Write-FixtureReleaseManifest (Join-Path $ac.Dir 'release-manifest.json')
    $run = Run-BackupCase 'a-backup-nodevice' @{
        Platform = 'Android'; Destination = (Join-Path $ac.Dir 'backup')
        ExpectedManifest = (Join-Path $ac.Dir 'release-manifest.json')
        ProfilesPath = $ac.DevProfiles; DeviceSerial = $ac.Serial; AdbPath = $ac.Adb }
    Check 'android backup: disconnected device fails' ($run.ExitCode -ne 0) $run.Output

    $ac = New-AdbCase 'a-backup-offline' @{ OfflineDevice = $true }
    Write-FixtureReleaseManifest (Join-Path $ac.Dir 'release-manifest.json')
    $run = Run-BackupCase 'a-backup-offline' @{
        Platform = 'Android'; Destination = (Join-Path $ac.Dir 'backup')
        ExpectedManifest = (Join-Path $ac.Dir 'release-manifest.json')
        ProfilesPath = $ac.DevProfiles; DeviceSerial = $ac.Serial; AdbPath = $ac.Adb }
    Check 'android backup: offline device state fails' ($run.ExitCode -ne 0) $run.Output

    $ac = New-AdbCase 'a-backup-running' @{ Pid = '4321' }
    Write-FixtureReleaseManifest (Join-Path $ac.Dir 'release-manifest.json')
    $run = Run-BackupCase 'a-backup-running' @{
        Platform = 'Android'; Destination = (Join-Path $ac.Dir 'backup')
        ExpectedManifest = (Join-Path $ac.Dir 'release-manifest.json')
        ProfilesPath = $ac.DevProfiles; DeviceSerial = $ac.Serial; AdbPath = $ac.Adb }
    Check 'android backup: running game on device fails (pidof)' `
        ($run.ExitCode -ne 0 -and $run.Output -match 'running|pidof') $run.Output

    $ac = New-AdbCase 'a-backup-scope' @{}
    Write-FixtureReleaseManifest (Join-Path $ac.Dir 'release-manifest.json')
    $run = Run-BackupCase 'a-backup-scope' @{
        Platform = 'Android'; Destination = (Join-Path $ac.Dir 'backup')
        ExpectedManifest = (Join-Path $ac.Dir 'release-manifest.json')
        ProfilesPath = '/data/data/com.daniel.familyracing/files/Profiles'
        DeviceSerial = $ac.Serial; AdbPath = $ac.Adb }
    Check 'android backup: path outside the scoped app data dir is refused' `
        ($run.ExitCode -ne 0 -and $run.Output -match 'scope|scoped|Android/data|path') $run.Output

    $ac = New-AdbCase 'a-backup-notinstalled' @{ NotInstalled = $true }
    Write-FixtureReleaseManifest (Join-Path $ac.Dir 'release-manifest.json')
    $run = Run-BackupCase 'a-backup-notinstalled' @{
        Platform = 'Android'; Destination = (Join-Path $ac.Dir 'backup')
        ExpectedManifest = (Join-Path $ac.Dir 'release-manifest.json')
        ProfilesPath = $ac.DevProfiles; DeviceSerial = $ac.Serial; AdbPath = $ac.Adb }
    Check 'android backup: package not installed fails' ($run.ExitCode -ne 0) $run.Output

    # Android restore round-trip on the emulated device fs.
    $ac = New-AdbCase 'a-restore-ok' @{}
    Write-FixtureReleaseManifest (Join-Path $ac.Dir 'release-manifest.json')
    $dest = Join-Path $ac.Dir 'backup'
    $run = Run-BackupCase 'a-restore-ok' @{
        Platform = 'Android'; Destination = $dest
        ExpectedManifest = (Join-Path $ac.Dir 'release-manifest.json')
        ProfilesPath = $ac.DevProfiles; DeviceSerial = $ac.Serial; AdbPath = $ac.Adb }
    $backupMap = Get-FileMap $dest
    Remove-Item -LiteralPath $ac.HostProfiles -Recurse -Force
    New-Item -ItemType Directory -Path $ac.HostProfiles | Out-Null
    $run = Run-RestoreCase @{
        Platform = 'Android'; Backup = $dest
        ExpectedManifest = (Join-Path $ac.Dir 'release-manifest.json')
        ProfilesPath = $ac.DevProfiles; DeviceSerial = $ac.Serial
        AdbPath = $ac.Adb; Apply = $true }
    $devMap = Get-FileMap $ac.HostProfiles
    $expected = @{}
    foreach ($n in $AllowedNames) { $expected[$n] = $backupMap[$n] }
    Check 'android restore: round-trip pushes every present file byte-identically' `
        ($run.ExitCode -eq 0 -and (Compare-FileMap $expected $devMap)) $run.Output

    $ac = New-AdbCase 'a-restore-wrongserial' @{ ExtraSerial = 'OTHER-SERIAL' }
    Write-FixtureReleaseManifest (Join-Path $ac.Dir 'release-manifest.json')
    $dest = Join-Path $ac.Dir 'backup'
    $run = Run-BackupCase 'a-restore-wrongserial' @{
        Platform = 'Android'; Destination = $dest
        ExpectedManifest = (Join-Path $ac.Dir 'release-manifest.json')
        ProfilesPath = $ac.DevProfiles; DeviceSerial = $ac.Serial; AdbPath = $ac.Adb }
    $run = Run-RestoreCase @{
        Platform = 'Android'; Backup = $dest
        ExpectedManifest = (Join-Path $ac.Dir 'release-manifest.json')
        ProfilesPath = $ac.DevProfiles; DeviceSerial = 'OTHER-SERIAL'
        AdbPath = $ac.Adb; Apply = $true }
    Check 'android restore: foreign device serial fails identity check' `
        ($run.ExitCode -ne 0 -and $run.Output -match 'serial|device|identity') $run.Output

    $ac = New-AdbCase 'a-restore-nodevice' @{ NoDevice = $true }
    Write-FixtureReleaseManifest (Join-Path $ac.Dir 'release-manifest.json')
    $run = Run-RestoreCase @{
        Platform = 'Android'; Backup = (Join-Path $fixtureRoot 'a-restore-ok\backup')
        ExpectedManifest = (Join-Path $ac.Dir 'release-manifest.json')
        ProfilesPath = $ac.DevProfiles; DeviceSerial = 'SM-T220-TEST1'
        AdbPath = $ac.Adb; Apply = $true }
    Check 'android restore: disconnected device fails' ($run.ExitCode -ne 0) $run.Output

    $ac = New-AdbCase 'a-restore-running' @{ Pid = '4321' }
    Write-FixtureReleaseManifest (Join-Path $ac.Dir 'release-manifest.json')
    $run = Run-RestoreCase @{
        Platform = 'Android'; Backup = (Join-Path $fixtureRoot 'a-restore-ok\backup')
        ExpectedManifest = (Join-Path $ac.Dir 'release-manifest.json')
        ProfilesPath = $ac.DevProfiles; DeviceSerial = 'SM-T220-TEST1'
        AdbPath = $ac.Adb; Apply = $true }
    Check 'android restore: running game on device fails' ($run.ExitCode -ne 0) $run.Output
}
finally {
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host ""
Write-Host ("Test-FamilyProfileBackup: {0} passed, {1} failed" -f $passed, $failed)
if ($failed -gt 0) { exit 1 }
exit 0
