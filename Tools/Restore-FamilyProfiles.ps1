# Restores a backup made by Backup-FamilyProfiles.ps1 into the SAME
# installation/device it came from -- and only that.
#
#   -Backup <dir> -ExpectedManifest <path>          validate + preview only
#   -Backup <dir> -ExpectedManifest <path> -Apply   validate + apply
#   -Resume                                        complete an interrupted apply
#   -Rollback                                      revert an interrupted apply
#
# Guarantees:
#  * Identity: the backup's recorded installation key (windows|PC|user or
#    android|serial) must match this target, and applicationId/company/
#    product/profileSchema must match -ExpectedManifest. Anything else is
#    a foreign/ cross-device restore and is refused.
#  * Schema: every restored file is re-validated (slot matches filename,
#    id matches the manifest, 1 <= schemaVersion <= the manifest's
#    recorded profile schema). Unsupported newer schema is rejected.
#  * Preservation: an on-disk file that cannot be verified as the same
#    profile (unreadable, newer schema, or a parseable DIFFERENT profile
#    id/slot) is never overwritten. Different-id/slot files abort the
#    whole restore before any write; unreadable/newer files are preserved
#    and named -- the game's own .bak recovery covers in-place corruption.
#  * Recovery: before the first write every existing byte of the eight
#    allowed names is snapshotted under Profiles\.family-restore-<id>\pre
#    plus a journal.json state file. An interruption leaves a recoverable
#    journal: -Resume completes the apply, -Rollback returns the exact
#    pre-restore bytes. A new apply refuses while a journal is pending.
#  * Scope: only profile-{0..3}.json(.bak) names are ever written; no
#    recursive copy/delete outside the validated Profiles directory; the
#    backup directory is read-only for this tool.
#
# No coin/unlock merging. No cross-device transfer. The game must NOT be
# running (Windows process check / adb shell pidof).
param(
    [ValidateSet('Windows', 'Android')]
    [string]$Platform = 'Windows',

    [string]$Backup = '',
    [string]$ExpectedManifest = '',
    [string]$ProfilesPath = '',
    [string]$DeviceSerial = '',
    [string]$AdbPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe',
    [string[]]$GameProcessNames = @(),
    [switch]$Apply,
    [switch]$Resume,
    [switch]$Rollback,
    # Test hook only: simulate a kill after N applied file writes.
    [int]$InterruptAfterFiles = -1,
    # Test hook only: simulate a kill after N snapshot copies inside the
    # pre-restore snapshot loop (leaves a journal with partial pre/ bytes).
    [int]$InterruptDuringSnapshot = -1
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
$JournalPrefix = '.family-restore-'

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
            throw ("The game appears to be running (process '" + $running[0].Name + "'). Close Family Racing before restoring profiles.")
        }
        return
    }
    $pidCheck = Invoke-Adb @('-s', $DeviceSerial, 'shell', 'pidof', $PackageId)
    if ($pidCheck.Code -eq 0 -and $pidCheck.Text.Trim().Length -gt 0) {
        throw ("The game is still running on $DeviceSerial (pidof $PackageId -> $($pidCheck.Text.Trim())). Close it before restoring.")
    }
}

# Same classification the backup tool records: unreadable bytes, invalid
# shape, foreign slot, newer-than-supported schema, or valid.
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

# ---------------- target filesystem (Windows dir or device path) ---------
function Target-ListEntryNames() {
    if ($Platform -eq 'Windows') {
        return @(Get-ChildItem -LiteralPath $script:ProfilesResolved | ForEach-Object { $_.Name })
    }
    $ls = Invoke-Adb @('-s', $DeviceSerial, 'shell', 'ls', '-a', '-1', $script:ProfilesResolved)
    if ($ls.Code -ne 0) {
        throw "Cannot list $($script:ProfilesResolved) on ${DeviceSerial}: $($ls.Text.Trim())"
    }
    return @($ls.Text -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_.Trim() })
}

function Target-FileExists([string]$Name) {
    if ($Platform -eq 'Windows') {
        return (Test-Path -LiteralPath (Join-Path $script:ProfilesResolved $Name) -PathType Leaf)
    }
    $r = Invoke-Adb @('-s', $DeviceSerial, 'shell', 'test', '-f', ($script:ProfilesResolved + '/' + $Name))
    return ($r.Code -eq 0)
}

function Target-DirExists([string]$Name) {
    if ($Platform -eq 'Windows') {
        return (Test-Path -LiteralPath (Join-Path $script:ProfilesResolved $Name) -PathType Container)
    }
    $r = Invoke-Adb @('-s', $DeviceSerial, 'shell', 'test', '-d', ($script:ProfilesResolved + '/' + $Name))
    return ($r.Code -eq 0)
}

function Target-FetchToLocal([string]$Name, [string]$LocalPath) {
    if ($Platform -eq 'Windows') {
        Copy-Item -LiteralPath (Join-Path $script:ProfilesResolved $Name) -Destination $LocalPath
        return
    }
    $r = Invoke-Adb @('-s', $DeviceSerial, 'pull', ($script:ProfilesResolved + '/' + $Name), $LocalPath)
    if ($r.Code -ne 0 -or -not (Test-Path -LiteralPath $LocalPath)) {
        throw "adb pull failed for $($script:ProfilesResolved)/$Name : $($r.Text.Trim())"
    }
}

# Atomically install $TempPath as $DestPath on the same volume. File.Replace
# needs a real swap path under .NET Framework (null/Empty are rejected); the
# swap file is removed immediately after.
function Write-AtomicLocalFile([string]$TempPath, [string]$DestPath) {
    if (Test-Path -LiteralPath $DestPath -PathType Leaf) {
        $swap = $DestPath + '.replace-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
        [IO.File]::Replace($TempPath, $DestPath, $swap)
        Remove-Item -LiteralPath $swap -Force
    } else {
        [IO.File]::Move($TempPath, $DestPath)
    }
}

# Atomically place $LocalPath's bytes as <Profiles>/<Name>.
function Target-WriteAtomically([string]$LocalPath, [string]$Name) {
    $tag = [guid]::NewGuid().ToString('N').Substring(0, 8)
    if ($Platform -eq 'Windows') {
        $tmp = Join-Path $script:ProfilesResolved ('.' + $Name + '.' + $tag + '.tmp')
        Copy-Item -LiteralPath $LocalPath -Destination $tmp
        Write-AtomicLocalFile $tmp (Join-Path $script:ProfilesResolved $Name)
        return
    }
    $remoteTmp = $script:ProfilesResolved + '/' + $Name + '.restore-tmp-' + $tag
    $push = Invoke-Adb @('-s', $DeviceSerial, 'push', $LocalPath, $remoteTmp)
    if ($push.Code -ne 0) { throw "adb push failed for $remoteTmp : $($push.Text.Trim())" }
    $mv = Invoke-Adb @('-s', $DeviceSerial, 'shell', 'mv', '-f', $remoteTmp, ($script:ProfilesResolved + '/' + $Name))
    if ($mv.Code -ne 0) { throw "adb mv failed for $remoteTmp : $($mv.Text.Trim())" }
}

function Target-Remove([string]$Name) {
    if ($Platform -eq 'Windows') {
        Remove-Item -LiteralPath (Join-Path $script:ProfilesResolved $Name) -Force
        return
    }
    $r = Invoke-Adb @('-s', $DeviceSerial, 'shell', 'rm', '-f', ($script:ProfilesResolved + '/' + $Name))
    if ($r.Code -ne 0) { throw "adb rm failed for $Name : $($r.Text.Trim())" }
}

# Journal dirs (.family-restore-*) living INSIDE the profiles directory.
function Target-ListJournalDirNames() {
    return @(Target-ListEntryNames | Where-Object {
        $_.StartsWith($JournalPrefix) -and (Target-DirExists $_)
    })
}

function Target-JournalMkdir([string]$DirName) {
    if ($Platform -eq 'Windows') {
        New-Item -ItemType Directory -Path (Join-Path (Join-Path $script:ProfilesResolved $DirName) 'pre') -Force | Out-Null
        return
    }
    $jdir = $script:ProfilesResolved + '/' + $DirName
    $r = Invoke-Adb @('-s', $DeviceSerial, 'shell', 'mkdir', '-p', ($jdir + '/pre'))
    if ($r.Code -ne 0) { throw "adb mkdir failed for $jdir : $($r.Text.Trim())" }
}

# Snapshot the live bytes into the journal's pre/ dir ATOMICALLY: a kill
# mid-copy can only leave a stray .snap-tmp-* name, never a torn
# pre/<name> that would pass an existence check and later be trusted as
# authoritative pre-restore bytes.
function Target-JournalSnapshot([string]$DirName, [string]$Name) {
    $tag = [guid]::NewGuid().ToString('N').Substring(0, 8)
    if ($Platform -eq 'Windows') {
        $preDir = Join-Path (Join-Path $script:ProfilesResolved $DirName) 'pre'
        $tmp = Join-Path $preDir ($Name + '.snap-tmp-' + $tag)
        Copy-Item -LiteralPath (Join-Path $script:ProfilesResolved $Name) -Destination $tmp
        Write-AtomicLocalFile $tmp (Join-Path $preDir $Name)
        return
    }
    $remotePre = $script:ProfilesResolved + '/' + $DirName + '/pre/' + $Name
    $remoteTmp = $remotePre + '.snap-tmp-' + $tag
    $r = Invoke-Adb @('-s', $DeviceSerial, 'shell', 'cp',
        ($script:ProfilesResolved + '/' + $Name), $remoteTmp)
    if ($r.Code -ne 0) { throw "adb snapshot cp failed for $Name : $($r.Text.Trim())" }
    $m = Invoke-Adb @('-s', $DeviceSerial, 'shell', 'mv', '-f', $remoteTmp, $remotePre)
    if ($m.Code -ne 0) { throw "adb snapshot mv failed for $Name : $($m.Text.Trim())" }
}

function Target-PreFileExists([string]$DirName, [string]$Name) {
    if ($Platform -eq 'Windows') {
        return (Test-Path -LiteralPath (Join-Path $script:ProfilesResolved ($DirName + '\pre\' + $Name)) -PathType Leaf)
    }
    $r = Invoke-Adb @('-s', $DeviceSerial, 'shell', 'test', '-f',
        ($script:ProfilesResolved + '/' + $DirName + '/pre/' + $Name))
    return ($r.Code -eq 0)
}

# SHA-256 of the bytes stored under the journal's pre/ dir; $null when
# absent. A snapshot is evidence only when it hashes as recorded.
function Target-PreFileSha256([string]$DirName, [string]$Name) {
    if ($Platform -eq 'Windows') {
        $pre = Join-Path $script:ProfilesResolved ($DirName + '\pre\' + $Name)
        if (-not (Test-Path -LiteralPath $pre -PathType Leaf)) { return $null }
        return (Get-FileHash -LiteralPath $pre -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if (-not (Target-PreFileExists $DirName $Name)) { return $null }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('family-pre-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.bin')
    try {
        Target-FetchToLocal ($DirName + '/pre/' + $Name) $tmp
        return (Get-FileHash -LiteralPath $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
}

# SHA-256 of the live target file; $null when absent.
function Target-LiveFileSha256([string]$Name) {
    if (-not (Target-FileExists $Name)) { return $null }
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ('family-live-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.bin')
    try {
        Target-FetchToLocal $Name $tmp
        return (Get-FileHash -LiteralPath $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
}

function Target-JournalWriteJson([string]$DirName, $Journal) {
    $json = $Journal | ConvertTo-Json -Depth 8
    if ($Platform -eq 'Windows') {
        $jdir = Join-Path $script:ProfilesResolved $DirName
        $tmp = Join-Path $jdir ('journal.json.' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.tmp')
        Set-Content -LiteralPath $tmp -Value $json -Encoding UTF8
        Write-AtomicLocalFile $tmp (Join-Path $jdir 'journal.json')
        return
    }
    $localTmp = Join-Path ([IO.Path]::GetTempPath()) ('journal-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.json')
    try {
        Set-Content -LiteralPath $localTmp -Value $json -Encoding UTF8
        $r = Invoke-Adb @('-s', $DeviceSerial, 'push', $localTmp, ($script:ProfilesResolved + '/' + $DirName + '/journal.json'))
        if ($r.Code -ne 0) { throw "adb push failed for journal.json : $($r.Text.Trim())" }
    }
    finally { Remove-Item -LiteralPath $localTmp -Force -ErrorAction SilentlyContinue }
}

function Target-JournalRead([string]$DirName) {
    $remote = $script:ProfilesResolved + '/' + $DirName + '/journal.json'
    if ($Platform -eq 'Windows') {
        $local = Join-Path $script:ProfilesResolved ($DirName + '\journal.json')
        if (-not (Test-Path -LiteralPath $local -PathType Leaf)) { return $null }
        return (Get-Content -LiteralPath $local -Raw | ConvertFrom-Json)
    }
    $r = Invoke-Adb @('-s', $DeviceSerial, 'shell', 'cat', $remote)
    if ($r.Code -ne 0 -or $r.Text.Trim().Length -eq 0) { return $null }
    return ($r.Text | ConvertFrom-Json)
}

# Copy one snapshotted pre-restore byte set back over the live file.
function Target-RestorePreBytes([string]$DirName, [string]$Name) {
    if ($Platform -eq 'Windows') {
        $pre = Join-Path $script:ProfilesResolved ($DirName + '\pre\' + $Name)
        $tmp = Join-Path $script:ProfilesResolved ('.' + $Name + '.' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.tmp')
        Copy-Item -LiteralPath $pre -Destination $tmp
        Write-AtomicLocalFile $tmp (Join-Path $script:ProfilesResolved $Name)
        return
    }
    # Copy the snapshot to a temp name and rename it over the live file:
    # an mv FROM pre/ would consume the snapshot, so a rollback killed
    # mid-loop would leave entries still 'done' with their snapshots
    # gone and every retry permanently wedged. Keeping pre/ intact until
    # the journal completes gives Android the same idempotent-retry
    # semantics the Windows path already has, while the temp+rename keeps
    # the live write atomic.
    $remoteTmp = $script:ProfilesResolved + '/' + $Name + '.restore-tmp-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $cp = Invoke-Adb @('-s', $DeviceSerial, 'shell', 'cp',
        ($script:ProfilesResolved + '/' + $DirName + '/pre/' + $Name), $remoteTmp)
    if ($cp.Code -ne 0) { throw "adb rollback cp failed for $Name : $($cp.Text.Trim())" }
    $mv = Invoke-Adb @('-s', $DeviceSerial, 'shell', 'mv', '-f', $remoteTmp,
        ($script:ProfilesResolved + '/' + $Name))
    if ($mv.Code -ne 0) { throw "adb rollback mv failed for $Name : $($mv.Text.Trim())" }
}

function Target-ListPreEntryNames([string]$DirName) {
    if ($Platform -eq 'Windows') {
        $preDir = Join-Path $script:ProfilesResolved ($DirName + '\pre')
        return @(Get-ChildItem -LiteralPath $preDir -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
    }
    $ls = Invoke-Adb @('-s', $DeviceSerial, 'shell', 'ls', '-a', '-1',
        ($script:ProfilesResolved + '/' + $DirName + '/pre'))
    if ($ls.Code -ne 0) { return @() }
    return @($ls.Text -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_.Trim() })
}

function Target-RemovePreFile([string]$DirName, [string]$Name) {
    if ($Platform -eq 'Windows') {
        Remove-Item -LiteralPath (Join-Path $script:ProfilesResolved ($DirName + '\pre\' + $Name)) -Force -ErrorAction SilentlyContinue
        return
    }
    Invoke-Adb @('-s', $DeviceSerial, 'shell', 'rm', '-f',
        ($script:ProfilesResolved + '/' + $DirName + '/pre/' + $Name)) | Out-Null
}

function Target-RemoveStrayTemps() {
    foreach ($name in (Target-ListEntryNames)) {
        if ($name -match '\.restore-tmp-' -or ($name -match '^\.profile-\d\.json(\.bak)?\.[0-9a-f]{8}\.tmp$')) {
            try { Target-Remove $name } catch { }
        }
        # Stray .snap-tmp-* names inside a journal's pre/ dir are the
        # remains of a mid-copy kill; they are never authoritative and
        # are safe to clear before resume/rollback.
        if ($name.StartsWith($JournalPrefix) -and (Target-DirExists $name)) {
            foreach ($preName in (Target-ListPreEntryNames $name)) {
                if ($preName -match '\.snap-tmp-') {
                    try { Target-RemovePreFile $name $preName } catch { }
                }
            }
        }
    }
}

# ---------------- backup validation --------------------------------------
function Read-And-Validate-Backup([string]$Dir, [int]$MaxSchema) {
    $manifestPath = Join-Path $Dir 'backup-manifest.json'
    Require-File $manifestPath 'Backup manifest'
    $m = $null
    try { $m = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json }
    catch { throw "Backup manifest does not parse: $manifestPath ($($_.Exception.Message))" }
    if ($null -eq $m -or $m.schemaVersion -ne 1) {
        throw "Backup manifest has unsupported schema version: $manifestPath"
    }
    if ([string]$m.platform -ne $Platform) {
        throw "Backup was taken on platform '$($m.platform)'; this restore targets '$Platform'."
    }
    # Every manifest file entry must be an allowed flat name -- anything
    # else (path traversal, other slots, other files) is rejected.
    $byName = @{}
    foreach ($f in @($m.files)) {
        if ([string]$f.name -notmatch '^profile-[0-3]\.json(\.bak)?$') {
            throw "Backup manifest contains a non-profile file name '$($f.name)'; refusing to trust it."
        }
        if ($byName.ContainsKey($f.name)) {
            throw "Backup manifest lists '$($f.name)' twice."
        }
        $byName[$f.name] = $f
        $expectedSlot = [int]($f.name -replace '^profile-(\d)\..*$', '$1')
        $expectedKind = if ($f.name.EndsWith('.bak')) { 'bak' } else { 'primary' }
        if (-not [bool]$f.present) { continue }
        if ($f.slot -ne $expectedSlot -or $f.kind -ne $expectedKind) {
            throw "Backup manifest entry '$($f.name)' has inconsistent slot/kind fields."
        }
        if ([string]$f.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
            throw "Backup manifest entry '$($f.name)' has a malformed sha256 hash."
        }
        $local = Join-Path $Dir $f.name
        if (-not (Test-Path -LiteralPath $local -PathType Leaf)) {
            throw "Backup manifest lists '$($f.name)' as present but the file is missing -- the backup is a partial copy."
        }
        $actualHash = (Get-FileHash -LiteralPath $local -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne ([string]$f.sha256).ToLowerInvariant()) {
            throw "Backup file '$($f.name)' hash mismatch: manifest sha256 $($f.sha256), actual $actualHash."
        }
        if ($f.sizeBytes -ne (Get-Item -LiteralPath $local).Length) {
            throw "Backup file '$($f.name)' size does not match the manifest."
        }
        $status = [string]$f.profileStatus
        if ($status -eq 'newer-schema') {
            throw "Backup file '$($f.name)' has an unsupported newer schema; this backup cannot be restored by this tool."
        }
        if (@('valid', 'unreadable', 'invalid', 'slot-mismatch') -notcontains $status) {
            throw "Backup manifest entry '$($f.name)' has unknown profileStatus '$status'."
        }
        $info = Get-ProfileFileInfo $local $expectedSlot $MaxSchema
        if ($status -eq 'valid') {
            if ($info.Status -ne 'valid') {
                throw "Backup file '$($f.name)' was recorded valid but now classifies as '$($info.Status)'; the manifest does not match its bytes."
            }
            if ($info.Slot -ne $expectedSlot) {
                throw "Backup file '$($f.name)' declares slot $($info.Slot), not slot $expectedSlot -- a foreign slot is refused."
            }
            if ($info.SchemaVersion -ne [int]$f.profileSchemaVersion) {
                throw "Backup file '$($f.name)' schema $($info.SchemaVersion) differs from the manifest record."
            }
            if ($info.Id -ne [string]$f.profileId) {
                throw "Backup file '$($f.name)' id '$($info.Id)' does not match the manifest-recorded profile id."
            }
        }
    }
    # The backup directory must contain ONLY the manifest and the listed
    # present files -- no extras, no subdirectories, no traversal material.
    foreach ($item in (Get-ChildItem -LiteralPath $Dir)) {
        if ($item.PSIsContainer) {
            throw "Backup directory contains an unexpected subdirectory '$($item.Name)'."
        }
        if ($item.Name -ne 'backup-manifest.json' -and
                (-not $byName.ContainsKey($item.Name) -or -not [bool]$byName[$item.Name].present)) {
            throw "Backup directory contains an unexpected file '$($item.Name)'; refusing it."
        }
    }
    return $m
}

# ---------------- main ----------------------------------------------------
$exitCode = 1
$observeDir = $null
try {
    if ($Resume -and $Rollback) { throw '-Resume and -Rollback are mutually exclusive.' }

    # Platform wiring shared by all modes.
    if ($Platform -eq 'Windows') {
        $script:ProfilesResolved = if ($ProfilesPath) { [IO.Path]::GetFullPath($ProfilesPath) } else { $DefaultWindowsProfiles }
        if (-not (Test-Path -LiteralPath $script:ProfilesResolved -PathType Container)) {
            throw "Profiles directory missing: $($script:ProfilesResolved)"
        }
        $installationKey = "windows|$env:COMPUTERNAME|$env:USERNAME"
    } else {
        if ([string]::IsNullOrEmpty($DeviceSerial)) { throw 'Android platform requires -DeviceSerial.' }
        Require-File $AdbPath 'adb'
        $script:ProfilesResolved = if ($ProfilesPath) { $ProfilesPath.Replace('\', '/') } else { "$($AndroidDataRoots[0])files/Profiles" }
        $scoped = $false
        foreach ($root in $AndroidDataRoots) { if ($script:ProfilesResolved.StartsWith($root)) { $scoped = $true } }
        if (-not $scoped -or $script:ProfilesResolved.Contains('..')) {
            throw "Android -ProfilesPath must stay inside the scoped app data directory (Android/data/$PackageId/); got '$($script:ProfilesResolved)'."
        }
        $state = Invoke-Adb @('-s', $DeviceSerial, 'get-state')
        if ($state.Code -ne 0 -or $state.Text.Trim() -ne 'device') {
            throw "Device $DeviceSerial is not attached/ready (adb get-state: $($state.Text.Trim()))."
        }
        $pm = Invoke-Adb @('-s', $DeviceSerial, 'shell', 'pm', 'path', $PackageId)
        if ($pm.Code -ne 0 -or $pm.Text -notmatch 'package:') {
            throw "Package $PackageId is not installed on $DeviceSerial."
        }
        $installationKey = "android|$DeviceSerial"
    }
    Assert-GameStopped

    # Interrupted-restore detection comes before anything else. A journal
    # whose JSON cannot be read still blocks new restores — it may hold the
    # only pre-restore snapshot.
    $applying = @()
    foreach ($dirName in (Target-ListJournalDirNames)) {
        $j = $null
        try { $j = Target-JournalRead $dirName }
        catch { $j = $null }
        if ($null -eq $j) {
            $applying += @{ DirName = $dirName; Journal = $null }
        } elseif ($j.state -eq 'applying') {
            $applying += @{ DirName = $dirName; Journal = $j }
        }
    }

    if ($Resume -or $Rollback) {
        if ($applying.Count -eq 0) { throw 'No interrupted restore journal found; nothing to resume or roll back.' }
        if ($applying.Count -gt 1) {
            throw ('Multiple interrupted restore journals exist: ' + (($applying | ForEach-Object { $_.DirName }) -join ', '))
        }
        $jdir = $applying[0].DirName
        $journal = $applying[0].Journal
        if ($null -eq $journal) {
            throw "Journal $jdir exists but journal.json is unreadable; recover the pre-restore bytes under $jdir manually."
        }
        if ([string]$journal.platform -ne $Platform) {
            throw "Journal platform '$($journal.platform)' does not match -Platform '$Platform'."
        }
        # A journal may only complete/roll back the installation it was
        # recorded for — a copied .family-restore-* dir must not push its
        # recorded bytes into a different installation.
        $recordedPath = [string]$journal.profilesPath
        if ([string]::IsNullOrEmpty($recordedPath) -or
                $recordedPath.TrimEnd('\', '/') -ine $script:ProfilesResolved.TrimEnd('\', '/')) {
            throw "Journal profiles path '$recordedPath' does not match the resolved target '$($script:ProfilesResolved)'; refusing a foreign-installation resume/rollback."
        }
        if ($journal.installationKey -and [string]$journal.installationKey -ne $installationKey) {
            throw "Journal installation key '$($journal.installationKey)' does not match this installation '$installationKey'."
        }
        if ($journal.deviceSerial -and $journal.deviceSerial -ne $DeviceSerial) {
            throw "Journal device serial '$($journal.deviceSerial)' does not match -DeviceSerial '$DeviceSerial'."
        }
        # Journaled entry names flow into Join-Path / adb paths — re-apply
        # the allowed-name gate so a tampered journal can never write or
        # delete outside the Profiles directory.
        foreach ($entry in @($journal.entries)) {
            if ([string]$entry.name -notmatch '^profile-[0-3]\.json(\.bak)?$') {
                throw "Journal $jdir contains a non-profile entry name '$($entry.name)'; refusing to resume or roll back."
            }
        }
        # Re-verify the recorded backup before resuming an apply.
        $journalBackup = [string]$journal.backupDir
        if ($Resume) {
            $bmPath = Join-Path $journalBackup 'backup-manifest.json'
            Require-File $bmPath 'Backup manifest recorded in the journal'
            $bmHash = (Get-FileHash -LiteralPath $bmPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($bmHash -ne [string]$journal.backupManifestSha256) {
                throw "The backup manifest changed since the interrupted apply; refusing to resume."
            }
        }
        Target-RemoveStrayTemps
        if ($Resume) {
            # Repair pass: an apply killed inside the snapshot loop (up to
            # ~16 adb round-trips on Android) leaves an 'applying' journal
            # whose preExisted entries may have missing or torn pre/
            # bytes. Any entry that was never written — pending restores
            # and every non-restore action — still holds its untouched
            # pre-restore bytes on disk, so a missing or unverifiable
            # snapshot is recaptured now BEFORE the first write;
            # otherwise those bytes are lost and a later rollback wedges
            # on the missing pre/ file.
            $repaired = $false
            foreach ($entry in @($journal.entries)) {
                if (-not $entry.preExisted) { continue }
                if ($entry.action -eq 'restore' -and $entry.state -eq 'done') { continue }
                $name = [string]$entry.name
                # An existing pre/ file is trusted ONLY when it hashes as
                # the journal recorded: existence alone is not evidence
                # (a mid-copy kill can leave torn bytes), and a snapshot
                # without a recorded hash is treated as absent.
                $needsCapture = $true
                if (Target-PreFileExists $jdir $name) {
                    $recordedPre = [string]$entry.preSha256
                    if (-not [string]::IsNullOrEmpty($recordedPre)) {
                        if ((Target-PreFileSha256 $jdir $name) -eq $recordedPre) {
                            $needsCapture = $false
                        } else {
                            Write-Host "Restore(resume): pre-restore snapshot for $name fails its journaled hash; recapturing from the still-untouched live file."
                        }
                    }
                }
                if (-not $needsCapture) { continue }
                if (Target-FileExists $name) {
                    # Capture, then verify the stored bytes equal the live
                    # file — a torn snapshot is retried once and never
                    # recorded as authoritative.
                    Target-JournalSnapshot $jdir $name
                    $liveHash = Target-LiveFileSha256 $name
                    $preHash = Target-PreFileSha256 $jdir $name
                    if ($preHash -ne $liveHash) {
                        Target-JournalSnapshot $jdir $name
                        $preHash = Target-PreFileSha256 $jdir $name
                        if ($preHash -ne $liveHash) {
                            throw "Pre-restore snapshot for $name could not be verified after retry (live $liveHash, stored $preHash); the live file was left unmodified."
                        }
                    }
                    $entry.preSha256 = $liveHash
                    Write-Host "Restore(resume): captured missed pre-restore bytes for $name"
                } else {
                    $entry.preExisted = $false
                    Write-Host "Restore(resume): $name vanished before its snapshot could be captured; nothing to preserve."
                }
                $repaired = $true
            }
            if ($repaired) {
                $journal.updatedUtc = [DateTime]::UtcNow.ToString('o')
                Target-JournalWriteJson $jdir $journal
            }
        }
        $resumedWrites = 0
        foreach ($entry in @($journal.entries)) {
            if ($Resume) {
                if ($entry.action -ne 'restore' -or $entry.state -eq 'done') { continue }
                $src = Join-Path $journalBackup $entry.name
                Require-File $src "Backup file for journal entry $($entry.name)"
                $srcHash = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($srcHash -ne [string]$entry.postSha256) {
                    throw "Backup file '$($entry.name)' no longer matches the journaled hash; refusing to resume."
                }
                Target-WriteAtomically $src $entry.name
                $entry.state = 'done'
                $journal.updatedUtc = [DateTime]::UtcNow.ToString('o')
                Target-JournalWriteJson $jdir $journal
                Write-Host "Restore(resume): wrote $($entry.name)"
                $resumedWrites++
                if ($InterruptAfterFiles -ge 0 -and $resumedWrites -ge $InterruptAfterFiles) {
                    Write-Host "Restore(resume): INTERRUPTED by test hook after $resumedWrites file(s); journal left in 'applying' state."
                    exit 3
                }
            } else {
                if ($entry.action -ne 'restore' -or $entry.state -ne 'done') { continue }
                if ($entry.preExisted) {
                    $name = [string]$entry.name
                    # The snapshot is authoritative only when it hashes
                    # exactly as the journal recorded: a missing recorded
                    # hash is treated as absent, a missing file stays a
                    # named refusal, and a hash mismatch means torn or
                    # tampered bytes that must never reach the live file.
                    $recordedPre = [string]$entry.preSha256
                    if ([string]::IsNullOrEmpty($recordedPre)) {
                        throw "Rollback cannot proceed: the journal records no pre-restore hash for '$name' under $jdir; the snapshot cannot be verified and is treated as absent. Recover manually from $jdir."
                    }
                    if (-not (Target-PreFileExists $jdir $name)) {
                        throw "Rollback cannot proceed: pre-restore bytes for '$name' are missing under $jdir (the file was already written but its snapshot was never captured). Recover manually from $jdir."
                    }
                    $preHash = Target-PreFileSha256 $jdir $name
                    if ($preHash -ne $recordedPre) {
                        throw "Rollback cannot proceed: pre-restore bytes for '$name' under $jdir fail the journaled hash check (recorded $recordedPre, stored $preHash) -- the snapshot is torn or tampered and was NOT restored. Recover manually from $jdir."
                    }
                    Target-RestorePreBytes $jdir $entry.name
                    Write-Host "Restore(rollback): returned pre-restore bytes for $($entry.name)"
                } else {
                    if (Target-FileExists $entry.name) { Target-Remove $entry.name }
                    Write-Host "Restore(rollback): removed newly written $($entry.name)"
                }
                $entry.state = 'rolled-back'
            }
        }
        $journal.state = $(if ($Resume) { 'completed' } else { 'rolled-back' })
        $journal.updatedUtc = [DateTime]::UtcNow.ToString('o')
        Target-JournalWriteJson $jdir $journal
        Write-Host "Restore: journal $jdir is now $($journal.state)."
        $exitCode = 0
        exit $exitCode
    }

    if ($applying.Count -gt 0) {
        throw ("An interrupted restore exists at " + ($applying[0].DirName) +
            " -- run with -Resume to complete it or -Rollback to revert it before starting a new restore.")
    }

    # --- Validate the backup + identity ------------------------------------
    if ([string]::IsNullOrEmpty($Backup) -or -not (Test-Path -LiteralPath $Backup -PathType Container)) {
        throw "Backup directory missing: $Backup"
    }
    $backupFull = (Resolve-Path -LiteralPath $Backup).Path
    $bm = Read-And-Validate-Backup $backupFull $SupportedProfileSchema
    $expected = Read-ExpectedManifest $ExpectedManifest
    if ([int]$expected.Manifest.gameplay.profileSchema -ne [int]$bm.profileSchema) {
        throw "Expected manifest profileSchema $($expected.Manifest.gameplay.profileSchema) does not match the backup's recorded $($bm.profileSchema); refusing a cross-version restore."
    }
    foreach ($field in @('applicationId', 'companyName', 'productName')) {
        if ([string]$bm.installation.$field -ne [string]$expected.Manifest.identity.$field) {
            throw "Expected manifest $field '$($expected.Manifest.identity.$field)' does not match the backup's '$($bm.installation.$field)' -- a foreign installation is refused."
        }
    }
    if ([string]$bm.installation.key -ne $installationKey) {
        throw "Backup installation key '$($bm.installation.key)' does not match this installation '$installationKey'; restores are same-installation only (no cross-device transfer)."
    }

    # --- Observe current on-disk state --------------------------------------
    $observeDir = Join-Path ([IO.Path]::GetTempPath()) ('family-restore-observe-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $observeDir | Out-Null
    $plan = @()
    $conflicts = @()
    foreach ($f in @($bm.files)) {
        $name = [string]$f.name
        $slot = [int]($name -replace '^profile-(\d)\..*$', '$1')
        if (-not [bool]$f.present) {
            $plan += @{ name = $name; action = 'absent'; note = 'absent in backup -- untouched' }
            continue
        }
        if (-not (Target-FileExists $name)) {
            $plan += @{ name = $name; action = 'restore'; note = 'missing on disk' }
            continue
        }
        $observed = Join-Path $observeDir $name
        Target-FetchToLocal $name $observed
        $currentHash = (Get-FileHash -LiteralPath $observed -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($currentHash -eq ([string]$f.sha256).ToLowerInvariant()) {
            $plan += @{ name = $name; action = 'skip-identical'; note = 'already identical' }
            continue
        }
        $info = Get-ProfileFileInfo $observed $slot $SupportedProfileSchema
        if ($info.Status -eq 'unreadable' -or $info.Status -eq 'invalid') {
            $plan += @{ name = $name; action = 'preserve'; note = "current file is $($info.Status) -- left untouched (the game's own .bak recovery handles in-place corruption)" }
            continue
        }
        if ($info.Status -eq 'newer-schema') {
            $plan += @{ name = $name; action = 'preserve'; note = "current file has newer schema $($info.SchemaVersion) -- left untouched" }
            continue
        }
        if ($info.Status -eq 'slot-mismatch') {
            $plan += @{ name = $name; action = 'conflict'; note = "current file declares slot $($info.Slot), not slot $slot -- foreign slot, refusing to overwrite" }
            $conflicts += $name
            continue
        }
        if ([string]$f.profileStatus -eq 'valid' -and $info.Id -ne [string]$f.profileId) {
            $plan += @{ name = $name; action = 'conflict'; note = "current file belongs to a different profile (id $($info.Id)); refusing to overwrite a foreign profile" }
            $conflicts += $name
            continue
        }
        if ([string]$f.profileStatus -ne 'valid') {
            # Never overwrite existing bytes with bytes the manifest itself
            # could not validate.
            $plan += @{ name = $name; action = 'preserve'; note = "backup copy is $($f.profileStatus) -- not overwriting existing bytes" }
            continue
        }
        $plan += @{ name = $name; action = 'restore'; note = 'same profile id -- recovering recorded bytes' }
    }

    foreach ($entry in $plan) {
        Write-Host ("Restore plan: {0,-22} {1}" -f $entry.name, ($entry.action + ' -- ' + $entry.note))
    }
    if ($conflicts.Count -gt 0) {
        throw ("Foreign profile data on disk (slot/id mismatch) for: " + ($conflicts -join ', ') +
            ". Refusing to overwrite another profile -- remove it manually first if it is genuinely unwanted.")
    }
    if (-not $Apply) {
        Write-Host 'Restore: preview only (no -Apply). Nothing was written.'
        $exitCode = 0
        exit $exitCode
    }

    # --- Journal + snapshot + apply ------------------------------------------
    $jdir = $JournalPrefix + [guid]::NewGuid().ToString('N').Substring(0, 8)
    Target-JournalMkdir $jdir
    $journal = [ordered]@{
        schemaVersion = 1
        state = 'applying'
        createdUtc = [DateTime]::UtcNow.ToString('o')
        updatedUtc = [DateTime]::UtcNow.ToString('o')
        platform = $Platform
        profilesPath = $script:ProfilesResolved
        installationKey = $installationKey
        deviceSerial = $(if ($Platform -eq 'Android') { $DeviceSerial } else { $null })
        backupDir = $backupFull
        backupManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $backupFull 'backup-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        expectedManifestSha256 = $expected.Sha256
        entries = @()
    }
    # Snapshot coverage is the ON-DISK set of allowed names, independent of
    # what the backup recorded — a file created after the backup still gets
    # its bytes preserved.
    $pre = @{}
    foreach ($name in $AllowedNames) {
        $pre[$name] = @{ preExisted = (Target-FileExists $name); preSha256 = $null }
    }
    $jEntries = @()
    foreach ($entry in $plan) {
        $post = $null
        if ($entry.action -eq 'restore') {
            $post = [string](@($bm.files | Where-Object { $_.name -eq $entry.name })[0].sha256)
        }
        $jEntries += [ordered]@{
            name = $entry.name; action = $entry.action; note = $entry.note
            preExisted = $pre[$entry.name].preExisted; preSha256 = $null
            postSha256 = $post
            state = $(if ($entry.action -eq 'restore') { 'pending' } else { 'done' })
        }
    }
    $journal.entries = $jEntries
    # Write the journal BEFORE snapshotting bytes: an interruption here
    # still leaves a resumable/rollbackable record instead of an orphaned
    # journal dir that wedges every future restore.
    Target-JournalWriteJson $jdir $journal
    $snapshotted = 0
    foreach ($entry in $journal.entries) {
        if (-not $entry.preExisted) { continue }
        $name = [string]$entry.name
        Target-JournalSnapshot $jdir $name
        $observed = Join-Path $observeDir $name
        if (-not (Test-Path -LiteralPath $observed -PathType Leaf)) {
            Target-FetchToLocal $name $observed
        }
        $entry.preSha256 = (Get-FileHash -LiteralPath $observed -Algorithm SHA256).Hash.ToLowerInvariant()
        # The stored snapshot must carry exactly those bytes — a torn
        # copy would otherwise be recorded as authoritative and silently
        # restored on rollback.
        $storedHash = Target-PreFileSha256 $jdir $name
        if ($storedHash -ne $entry.preSha256) {
            throw "Pre-restore snapshot for $name failed verification right after capture (live $($entry.preSha256), stored $storedHash); leaving the journal for -Resume/-Rollback."
        }
        # Persist the verified hash per entry: a kill here leaves an
        # 'applying' journal whose recorded hashes let resume distinguish
        # a merely-missing snapshot from a torn one (named mismatch
        # instead of blind recapture).
        $journal.updatedUtc = [DateTime]::UtcNow.ToString('o')
        Target-JournalWriteJson $jdir $journal
        $snapshotted++
        if ($InterruptDuringSnapshot -ge 0 -and $snapshotted -ge $InterruptDuringSnapshot) {
            Write-Host "Restore: INTERRUPTED by test hook inside the snapshot loop after $snapshotted file(s); journal left in 'applying' state with a partial pre/ snapshot."
            exit 3
        }
    }
    Target-JournalWriteJson $jdir $journal

    $applied = 0
    foreach ($entry in $journal.entries) {
        if ($entry.action -ne 'restore') { continue }
        $src = Join-Path $backupFull $entry.name
        $srcHash = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($srcHash -ne ([string]$entry.postSha256).ToLowerInvariant()) {
            throw "Backup file '$($entry.name)' changed mid-restore; leaving the journal for -Resume/-Rollback."
        }
        Target-WriteAtomically $src $entry.name
        $entry.state = 'done'
        $journal.updatedUtc = [DateTime]::UtcNow.ToString('o')
        Target-JournalWriteJson $jdir $journal
        $applied++
        Write-Host "Restore: wrote $($entry.name)"
        if ($InterruptAfterFiles -ge 0 -and $applied -ge $InterruptAfterFiles) {
            Write-Host "Restore: INTERRUPTED by test hook after $applied file(s); journal left in 'applying' state."
            exit 3
        }
    }
    $journal.state = 'completed'
    $journal.updatedUtc = [DateTime]::UtcNow.ToString('o')
    Target-JournalWriteJson $jdir $journal
    Write-Host "Restore: completed. $applied file(s) written; journal + pre-restore snapshot kept at $($script:ProfilesResolved)/$jdir until the game has been relaunched and checked."
    $exitCode = 0
}
catch {
    Write-Host "Restore failed: $($_.Exception.Message)"
    $exitCode = 1
}
finally {
    if ($null -ne $observeDir -and (Test-Path -LiteralPath $observeDir)) {
        Remove-Item -LiteralPath $observeDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
exit $exitCode
