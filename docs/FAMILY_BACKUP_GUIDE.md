# Family Racing — Profile Backup & Recovery Guide (for parents)

The game keeps each player's progress in four profile slots. This guide
shows how to copy those profiles to safety and how to put them back —
**on the same PC or the same tablet only**. These tools deliberately do
not move profiles between devices and never merge progress.

## What you need

- The package's `release-manifest.json` (shipped next to the APK/ZIP).
  Keep it — the tools use it to prove they are working on the right
  installation.
- The game **closed** on the device you are working on. The tools refuse
  to run while the game is open, so a half-written save can never be
  captured.

## What gets backed up

Exactly the eight possible files inside the game's `Profiles` folder:

```
profile-0.json  profile-0.json.bak
profile-1.json  profile-1.json.bak
profile-2.json  profile-2.json.bak
profile-3.json  profile-3.json.bak
```

Nothing else is copied — no settings, no receipts outside these files,
no other folders. Each backup directory contains a `backup-manifest.json`
recording which PC/tablet it came from, each slot's profile id and save
format, the SHA-256 fingerprint of every file, and which files were
absent. A restore checks all of that before writing anything.

## Windows — back up

Profiles live at (per Windows user):

```
C:\Users\<you>\AppData\LocalLow\Family Racing\Family Racing\Profiles
```

From the repository's `Tools` folder (or wherever the tools were copied):

```powershell
powershell -NoProfile -File Tools\Backup-FamilyProfiles.ps1 `
    -Destination D:\FamilyRacingBackups\2026-10-17 `
    -ExpectedManifest C:\path\to\release-manifest.json
```

`-Destination` must be a **new** folder — existing folders are refused so
backups can never overwrite each other. On success the tool reports how
many of the eight files it copied.

**Keep the backup on a different disk or another computer.** A backup
that stays on the same drive is lost with that drive.

## Windows — restore

Always run the preview first; it validates everything and writes
nothing:

```powershell
# Preview: shows the plan for each file, writes nothing:
powershell -NoProfile -File Tools\Restore-FamilyProfiles.ps1 `
    -Backup D:\FamilyRacingBackups\2026-10-17 `
    -ExpectedManifest C:\path\to\release-manifest.json

# Apply: actually writes the files:
powershell -NoProfile -File Tools\Restore-FamilyProfiles.ps1 `
    -Backup D:\FamilyRacingBackups\2026-10-17 `
    -ExpectedManifest C:\path\to\release-manifest.json -Apply
```

The plan line for each file is one of:

| Plan | Meaning |
|---|---|
| `restore` | File missing on disk, or the same profile with different bytes — the backup copy is written |
| `skip-identical` | Already byte-identical — nothing to do |
| `absent` | Was absent when the backup was taken — stays absent |
| `preserve` | The on-disk file cannot be verified as the same profile (unreadable or written by a newer version) — **left untouched** and named |

A restore **refuses outright** when it finds:

- a backup from a different PC/user or tablet (installation identity
  mismatch — no cross-device transfer);
- a file whose stored slot or profile id does not match its name or the
  manifest (foreign data);
- a file written by a newer version of the game (unsupported schema);
- an on-disk file that provably belongs to a **different** profile —
  the whole restore aborts rather than overwriting someone else's save;
- a tampered or incomplete backup (bad hash, missing file, extra file).

If a restore is interrupted (power loss, closed window), the tool leaves
a `.family-restore-*` folder inside `Profiles` holding a journal and a
full copy of every pre-restore byte. Before relaunching the game run:

```powershell
# finish what was started:
powershell -NoProfile -File Tools\Restore-FamilyProfiles.ps1 -Resume

# or put every byte back exactly as it was:
powershell -NoProfile -File Tools\Restore-FamilyProfiles.ps1 -Rollback
```

A new restore refuses to start while a pending journal exists — finish
or roll back first. Completed journals (state `completed`) keep the
pre-restore snapshot as an extra safety copy; delete the
`.family-restore-*` folder once the game has been launched and the
profiles look right.

## Android (tablet) — status: NOT yet qualified

**There is no tested tablet path yet — no Android device was available
when this guide was written.** The tools implement the intended path
below, but treat it as unproven until the physical-tablet row in the
runbook is closed. Do not rely on it as the only copy of anyone's
progress.

The intended procedure requires a PC with USB debugging enabled on the
tablet (`Settings → Developer options → USB debugging`) and the `adb`
tool that ships with Unity (`...\AndroidPlayer\SDK\platform-tools\adb.exe`):

```powershell
# Back up (ProfilesPath defaults to the app's own data directory):
powershell -NoProfile -File Tools\Backup-FamilyProfiles.ps1 -Platform Android `
    -DeviceSerial <serial from `adb devices`> `
    -Destination D:\FamilyRacingBackups\tab-a7-2026-10-17 `
    -ExpectedManifest C:\path\to\release-manifest.json

# Restore (preview first, then -Apply):
powershell -NoProfile -File Tools\Restore-FamilyProfiles.ps1 -Platform Android `
    -DeviceSerial <same serial> -Backup <backup dir> `
    -ExpectedManifest C:\path\to\release-manifest.json -Apply
```

The device path is scoped to `Android/data/com.daniel.familyracing/…`
only — the tools refuse any other location, and they check the device
serial against the backup so a backup can never be pushed onto a
different tablet. If the tablet refuses shell access to that directory
(newer Android versions restrict `Android/data`), the tool fails
honestly rather than pretending; that failure is an open qualification
row, not a supported workaround. Do **not** copy files via a random
phone file-manager app — that path is untested and unsafe to document.

## Important rules

- **Uninstalling or "clear data" is NOT an update step.** Updating means
  installing the new APK/ZIP over the old one in place. Uninstalling
  deletes all four profiles — there is no cloud copy.
- **The `.json.bak` files are not a real backup.** They exist so the
  *game* can recover a single corrupted save on the next start. They sit
  on the same device and are lost with it — that is what this external
  backup is for.
- **Same installation only.** Restoring onto a different PC user,
  different PC, or different tablet is refused by design. Progress does
  not transfer between devices.
- **Backups are per point-in-time.** Restoring rolls the installation
  back to the backup's state for the slots it restores; it never merges
  coins, unlocks, or receipts between versions of a profile.

## Where to keep backups

Any private place off the device: another folder on the PC, a USB stick,
a sibling's computer, a private shared folder. Each backup folder is
self-contained (manifest + the profile files) and small — keep the last
few rather than overwriting one.
