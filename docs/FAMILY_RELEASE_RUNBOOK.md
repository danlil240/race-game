# Family Release Runbook (M7 — signing pipeline + package assembly)

Packaging and verification contract for the privately distributed family
release. This document covers the signed Android pipeline that exists
today. **No release candidate has been built yet** — production content
(the bound `FullContent` catalog) is still absent, so the export step
fails honestly at `FullContentSetup`; device, update, backup and family
acceptance rows stay explicitly open below.

## Release identity

`Release/release.json` (schema 1) is the tracked source of truth:

| Field | Value | Checked where |
| --- | --- | --- |
| `versionName` / `androidVersionCode` | `1.0.0` / `1` | `aapt` badging of the APK |
| `previousAndroidVersionCode` | `0` (no signed predecessor) | update qualification, Task 5 |
| `applicationId` | `com.daniel.familyracing` | `aapt` badging |
| `androidMinSdkVersion` / `androidTargetSdkVersion` | `25` / `36` | `aapt` badging + `apksigner --min-sdk-version` |
| `signingKeyAlias` | `familyracing` | signing step |
| `expectedCertificateSha256` | `b1cb8bf7…270ead` | `apksigner verify --print-certs` |

No passwords or keystore paths belong in tracked files. The owner-held
keystore lives at `%USERPROFILE%\.family-racing\family-release.keystore`
with credentials in `CREDENTIALS-LOCAL.txt` next to it; both are local
secrets — never commit, log, or copy them.

## Commands

```powershell
# Signed release APK (production-gated: fails at the content gate
# until the FullContent inventory exists):
powershell -NoProfile -File Tools/Verify.ps1 -Mode AndroidFamilyRelease

# Same path via the wrapper directly:
powershell -NoProfile -File Tools/Build-AndroidOffline.ps1 `
    -Presentation FullContent -Configuration Release

# Sign/verify a single unsigned release APK (used by the wrapper):
powershell -NoProfile -File Tools/Sign-AndroidRelease.ps1 `
    -UnsignedApk <unsigned.apk> -OutputApk <new-or-existing-path> `
    -ExpectedCertificateSha256 <64-hex> [-ExpectedApplicationId ...] `
    [-ExpectedVersionName ...] [-ExpectedVersionCode n] `
    [-ExpectedMinSdk n] [-ExpectedTargetSdk n] [-ApkMinSdkVersion n] `
    [-RequireArm64]
```

`-Configuration Release` is allowed **only** with `-Presentation
FullContent`; `Graybox`/`VisualBenchmark`/`FullContentPreview` +
`Release` are refused before any build step. Development and Ordinary
keep `:launcher:assembleDebug` and publish `launcher-debug.apk`
unchanged.

## Signing credentials (process environment only)

Before running the release mode, set these in the shell environment —
never on a command line, never in a file under the repo:

```powershell
$env:FAMILY_RACING_KEYSTORE          = "$env:USERPROFILE\.family-racing\family-release.keystore"
$env:FAMILY_RACING_KEY_ALIAS         = 'familyracing'
$env:FAMILY_RACING_KEYSTORE_PASSWORD = <from CREDENTIALS-LOCAL.txt>
$env:FAMILY_RACING_KEY_PASSWORD      = <from CREDENTIALS-LOCAL.txt>
```

The signer forwards them to `apksigner` as `env:` references, so secret
values never reach a command line or log. Missing variables fail with
the variable names listed.

## Pipeline (Verify -Mode AndroidFamilyRelease)

1. **Export** — `FoundationBuild.ExportAndroidFamilyRelease`:
   `FullContentSetup` content gate, generic setup, then
   `FamilyReleaseSettings.Apply` stamps the tracked identity. Ordinary
   export (no development flag — validation endpoints compiled out),
   `AcceptExternalModificationsToPlayer`, export dir must be new.
2. **Gradle** — bundled Gradle `--offline :launcher:assembleRelease`
   producing `launcher-release-unsigned.apk`.
3. **Inspect** — exported `launcher\build.gradle` must carry no
   `signingConfig` and no `debuggable true`; a pre-signed
   `launcher-release.apk` is rejected (debug-key fallback check).
4. **Sign** — `Sign-AndroidRelease.ps1`: input must be a non-empty ZIP
   with no `META-INF/*.SF|RSA|DSA|EC` signature block (pre-signed input
   refused); `aapt dump badging` must match applicationId, versionName,
   versionCode, min/target SDK and must not be debuggable; ARM64
   payload required; `zipalign -f -p 4`, then
   `apksigner sign` + `apksigner verify --verbose --print-certs`
   requiring a v2/v3 signature and the expected signer SHA-256.
5. **Publish** — staged in a sibling temp dir; the signed APK replaces
   `Builds\Android-FamilyRelease\FamilyRacing.apk` only after every
   check passes, so a failed run never overwrites a previous valid
   package. Evidence: `Logs\android-offline\<runId>\result.json` plus
   `export.log`/`gradle.log`/`sign.log`.

## Package assembly (M7 Task 3)

`Tools/Build-FamilyRelease.ps1 -Config <release.json> -Output <new-dir>`
orchestrates the whole chain and assembles the traceable package set:

```powershell
# Full release path (today this fails honestly at the FullContent
# production gate inside the Windows entry point):
powershell -NoProfile -File Tools/Build-FamilyRelease.ps1 `
    -Config Release\release.json -Output <new-directory>

# Rehearsal path: package EXISTING build outputs to prove the assembly
# machinery end-to-end on real build output. Never a release candidate.
powershell -NoProfile -File Tools/Build-FamilyRelease.ps1 `
    -Config Release\release.json -Output <new-directory> -Rehearsal `
    -WindowsSource Builds\Windows-Ordinary [-AndroidSource <apk>]
```

Pipeline stages (every failure is named and publishes nothing):

1. **Config** — required `release.json` fields enforced.
2. **Output dir** — must be new; any existing/reused directory is refused.
3. **Source identity** — `git rev-parse HEAD` + `status --porcelain`;
   dirty trees record `cleanSource:false` with named areas, never a
   fabricated clean claim.
4. **Builds** (full path only) — `Verify -Mode WindowsFamilyRelease`
   (FoundationBuild.WindowsFamilyRelease: production content gate +
   ordinary Mono x64 player) then `Verify -Mode AndroidFamilyRelease`
   (the signed pipeline above). Signing env vars are preflight-checked
   before any Unity run and named on failure.
5. **Player completeness** — requires the ENTIRE ordinary player:
   `FamilyRacing.exe`, `FamilyRacing_Data` (level0, globalgamemanagers,
   resources.assets, boot.config, app.info, `Managed` with
   `FamilyRacing.Core/Runtime.dll` + `UnityEngine*.dll`),
   `UnityPlayer.dll`, `MonoBleedingEdge` runtime config.
6. **Content manifest** — the build's `manifest.json` must declare
   `contentStage=complete`, `candidate=true` and the expected
   `drivingVersion`/`contentVersion` (`full-content-1`); partial,
   non-candidate, mixed and (on the real path) stale manifests are
   rejected.
7. **Contamination scan** — keystores/keys, `Profiles\`,
   `profile-*.json`, validation tokens and credential files anywhere in
   the player tree fail the run. `*_DoNotShip`, `*.pdb`, `*.log` are
   excluded from the archive silently (build byproducts, not content).
8. **Assemble** — player staged under a top-level `FamilyRacing\`
   folder plus `THIRD-PARTY-NOTICES.txt` + `INSTALL-GUIDE.txt`, zipped
   with canonical forward-slash entries; the signed APK is copied byte
   for byte. A self-check re-opens the archive and asserts required
   entries, no forbidden entries, and no embedded manifest. All
   assembly happens under a temp staging dir OUTSIDE `-Output`; the
   output directory is created and populated only after every check
   passes, so any failure — including post-zip — publishes nothing.
9. **Manifest** — `release-manifest.json` schema 1 is written to the
   output root AFTER final assembly and stays OUTSIDE the zip: source
   commit + cleanSource + dirty areas, tool versions, release identity,
   Android SDK/ABI/certificate, Windows x64/Mono + honest
   `authenticodeSigned:false`, arcade backend, gameplay/content
   fingerprints, `contentStage`, per-file sha256+size, named
   `blockers`, and `acceptanceStatus:"unqualified"` — the Task 5 gate
   owns any status change.

Package layout on success:

```
<Output>\
  FamilyRacing-<version>-Windows-x64.zip
  FamilyRacing-<version>-Android.apk     (full path; absent in rehearsal)
  THIRD-PARTY-NOTICES.txt
  INSTALL-GUIDE.txt
  release-manifest.json
```

Run logs go to `Logs\family-release\<runid>\` — never into packages or
the output directory. Fixture artifacts (test keystore, simulated
toolchain) are never release evidence.

## Profile backup & recovery (M7 Task 4)

Two tools provide qualified same-installation backup and restore of the
profile store (`persistentDataPath/Profiles`; the game writes
`profile-{0..3}.json` + `.json.bak` siblings, schema 3, atomic saves —
unchanged by this task):

```powershell
# Backup — Destination must be a NEW directory:
powershell -NoProfile -File Tools\Backup-FamilyProfiles.ps1 `
    -Destination <new-dir> -ExpectedManifest <release-manifest.json> `
    [-Platform Windows|Android] [-ProfilesPath <dir>] `
    [-DeviceSerial <serial>] [-InstallationLabel <label>] `
    [-AdbPath <adb.exe>] [-GameProcessNames <extra-names>]

# Restore — preview (validates + plans, writes nothing) then -Apply:
powershell -NoProfile -File Tools\Restore-FamilyProfiles.ps1 `
    -Backup <dir> -ExpectedManifest <release-manifest.json> `
    [-Platform Windows|Android] [-ProfilesPath <dir>] `
    [-DeviceSerial <serial>] [-Apply]

# After an interrupted apply (journal left in Profiles):
powershell -NoProfile -File Tools\Restore-FamilyProfiles.ps1 -Resume
powershell -NoProfile -File Tools\Restore-FamilyProfiles.ps1 -Rollback
```

Contract highlights (the tooling test pins each one):

- **Scope** — only `profile-{0..3}.json` and `.json.bak` siblings are
  ever copied or written; non-profile entries are ignored and recorded,
  never touched; no recursion outside the validated Profiles directory;
  the backup directory is read-only during restore.
- **Backup manifest** (`backup-manifest.json`, schema 1) — installation
  key + label (`windows|<PC>|<user>` or `android|<serial>`),
  application identity copied from the installed release manifest with
  its SHA-256, per-file slot/kind/profileId/schemaVersion/SHA-256/size,
  original missing-file states, and the supported profile schema. The
  manifest is written only after every staged byte hashes correctly, so
  a partial copy is never marked usable.
- **Restore identity gate** — backup platform, installation key,
  applicationId/company/product, and profile schema must all match the
  target + `-ExpectedManifest`; foreign slot/identity inside a file or a
  mismatched manifest is rejected.
- **Preservation** — on-disk files that are unreadable, newer-schema, or
  provably a different profile id/slot are never overwritten; parseable
  foreign-id/slot files abort the whole restore before any write.
  Restoring never merges progress.
- **Recovery journal** — `-Apply` first snapshots every existing byte of
  the eight names into `Profiles\.family-restore-<id>\pre` plus a
  `journal.json` state file; each write is staged then renamed. An
  interruption leaves state `applying`: `-Resume` completes it (backup
  hash re-verified), `-Rollback` returns the exact pre-restore bytes; a
  new apply refuses while a journal is pending.
- **Running-game refusal** — Windows checks the process list
  (`FamilyRacing`, `Family Racing`, plus `-GameProcessNames`); Android
  runs `adb shell pidof com.daniel.familyracing`.
- **Android wiring** — requires `-DeviceSerial`; `get-state` must be
  `device`, `pm path com.daniel.familyracing` must resolve, and
  `-ProfilesPath` must stay under
  `Android/data/com.daniel.familyracing/` (default
  `/sdcard/Android/data/com.daniel.familyracing/files/Profiles`). Pull/
  push via the pinned SDK adb with push-to-temp + `mv` atomic placement.

Qualification status: **Windows is qualified** — a real backup +
preview + delete/restore round-trip ran byte-identically against the
actual `LocalLow\Family Racing\Family Racing\Profiles` path on the
build machine (rehearsal profiles), and the tooling suite proves every
failure class on disposable dirs. **Android is implemented but
unqualified** — `adb devices` reports zero attached devices on this
machine (verified this task), so real pull/push on a tablet stays an
open row below.

## Update qualification + the evidence gate (M7 Task 5)

`Release/acceptance-matrix.json` is the independent required-row
inventory for the release: 47 rows covering candidate baseline, package
identity/content/licenses on both platforms, signing certificate and key
custody, every inherited M1–M6 acceptance requirement, all Task 6
physical-matrix row families (fresh install, host permutations, join
failures, session recovery, impaired Wi-Fi, sustained play, performance,
updates/recovery, input/presentation, validation-endpoint absence, clean
account), family/human sessions, the parent-guide walkthrough, the
debug-predecessor migration doc — plus eight `tooling.*` rows that qualify
pipeline/update *behavior* and are the only rows fixture evidence may
satisfy.

`Tools/ReleaseQualification.py` is the gate:

```powershell
python Tools\ReleaseQualification.py `
    --manifest <release-manifest.json> `
    --matrix Release\acceptance-matrix.json `
    --evidence <new-output-directory> `
    --records <record.json> [<more records or directories>...]
```

It writes `<evidence>\result.json` and exits 0 only when every required
row passed; any failure or gap exits nonzero with verdict `failed` or
`partial`. Semantics pinned by `Tools/TestReleaseQualification.py`:

- **Integrity is hard-fail.** A measured artifact hash that contradicts
  the manifest, a missing or tampered evidence file, a malformed record,
  an unknown row id, or a second record for the same row → `failed`.
  The manifest itself is sanity-checked (schema, file hashes against the
  files on disk, and `androidVersionCode` strictly above
  `previousAndroidVersionCode`).
- **Inadmissible evidence is a gap, not a pass and not a fabrication.**
  Fixture/rehearsal records on a `production` row, development-build
  records on an `ordinary`-package row, records whose source commit or
  content identity is not the candidate's, a human-review row without a
  named `humanReviewer`, and a physical row without device identification
  all leave the row `notVerified` → verdict `partial`.
- **Nothing is filled in.** The gate never edits the candidate, the
  matrix, or a record, and has no success override.

Evidence record (schema 1): `rowId`, `outcome`
(`passed|failed|skipped|notVerified`), `evidenceClass`
(`production|fixture`), `buildKind` (`ordinary|development|fixture`),
`source` `{commit, contentVersion}`, `method`, `observedUtc`, and as
required `artifact` `{path, sha256}`, `device` `{label, os,
configuration}`, `humanReviewer`, `evidenceFiles` `[{path, sha256}]`
(paths resolve relative to the record file; every hash is re-verified).

### Fixture update rehearsal (what is proven now)

Under `Logs\release-qualification\rehearsal-2026-10-17\` (gitignored;
regenerate with `rehearse.py` next to it):

- **Same-key APK pair, codes 1→2** — two minimal APKs for
  `com.daniel.familyracing` built with the bundled `aapt` and signed
  through the real `Sign-AndroidRelease.ps1` pipeline under a disposable
  test keystore. `apksigner --print-certs` reports one identical
  certificate SHA-256 on both; `aapt` badging confirms codes 1 and 2.
  This is signer-continuity/update mechanics only — a labeled fixture
  pair, not an upgrade from a shipped version; `release.json`
  `previousAndroidVersionCode` stays 0.
- **Windows binary replacement** — the rehearsal player was extracted, a
  disposable `LocalLow\Family Racing\Family Racing\Profiles` tree of four
  synthetic schema-3 slots (+`.bak`) was written, then the entire binary
  set was swapped for a distinct v2 set. All eight profile files hashed
  byte-identically after the swap.
- **Rejections at pipeline level** — `Sign-AndroidRelease.ps1` refused a
  fixture-B-signed APK when fixture-A's certificate was expected (no
  output published); the gate refused a manifest whose
  `androidVersionCode` was not strictly above its predecessor (the same
  rule is pinned by EditMode test
  `FamilyReleaseSettingsTests.ReleaseRejectsNonIncreasingVersionCode`).

Gate result on this rehearsal evidence set: **verdict `partial` —
8 tooling rows passed, 39 production rows `notVerified`, 0 failed, 0
integrity failures.** The open rows are exactly the hardware/ordinary-
package/human rows; none may be closed by fixture evidence.

### Debug-signed predecessors

Any tablet that ever ran a Gradle-debug/dev APK cannot be updated in
place to the release signature — Android refuses cross-signer updates by
design. That migration is a separate, documented procedure in
`docs/FAMILY_BACKUP_GUIDE.md` (preserve the saves with the backup tools,
verify, then remove): the release pipeline never silently uninstalls a
debug build and never claims release-key continuity over one.

## Open rows (explicitly not closed by this task)

- **Real release candidate build** — gated on the bound production
  `FullContent` inventory (M6 Tasks 5–8); until then step 1 fails at
  the content gate by design. When it runs: record package SHA-256 and
  signer certificate in the release manifest (Task 3) and evidence
  records (Task 5). The keystore backup's recoverability is an owner
  step: restore `%USERPROFILE%\.family-racing\` to a clean location and
  re-verify the certificate fingerprint.
- **Device installation / update-preserves-saves** — needs the physical
  matrix (Task 6): SM-T220 plus second tablet/PC/fourth device.
- **Android backup/restore on-device qualification** — OPEN. The tools
  implement scoped `adb pull/push` with serial checks, `pidof`
  running-game detection and the same journal contract, exercised
  against a simulated device fs in the tooling suite — but no physical
  tablet was attached (`adb devices`: zero devices, re-verified this
  task), so discovery of the installed player's real data path and the
  pull/push round-trip are **unqualified on each supported tablet**
  (ordinary APK, no root/`run-as`). Shell access to `Android/data/` may
  be refused on newer Android; that outcome must be recorded honestly,
  not worked around.
- **Family acceptance** — human sessions on the ordinary packages
  (Task 6).
- **Clean-account offline launch** — OPEN. The Task 3 rehearsal
  extracted the Windows ZIP to a clean directory and launched it
  offline under the build/owner account; that is accepted rehearsal
  evidence only. A qualification-grade row still requires launch under
  a separate clean Windows account — no new account is created in this
  task; the row belongs to Task 6 qualification.
- **Qualification + acceptance** — the manifest is frozen at
  `acceptanceStatus:"unqualified"`; the Task 5 evidence gate owns the
  change. On the rehearsal evidence set the gate reports `partial`
  (8 tooling rows pass; all 39 production rows `notVerified`). Physical
  install/update/backup/family rows stay in Task 6.
- **Physical update + install-rejection rows** — OPEN. The fixture
  rehearsals proved signer continuity, binary replacement, and pipeline
  rejections at tooling level; on-device install-over with earned
  progress, mismatched-signer install rejection, and Windows binary
  replacement on the shipped package still require hardware + ordinary
  packages (Task 6).
- **Debug-predecessor migration** — documented path only (see the backup
  guide); no device has been observed carrying a debug install, and no
  cross-signer update is claimed.

## Tooling tests

`python -m unittest discover -s Tools -p "TestReleaseQualification.py"`
(12 tests) covers
the evidence-gate contract: absent required rows are `partial`; fixture
records can never satisfy production rows (a fully fixture matrix passes
only as a tooling self-test); artifact-hash and evidence-file tampering
fail; development-build records cannot satisfy ordinary-package rows;
unknown/duplicate row ids fail; human rows need a named reviewer; and
failed/skipped outcomes map to `failed`/`notVerified`.

`powershell -NoProfile -File Tools/Test-FamilyProfileBackup.ps1`
(64 checks) covers the backup/restore contract: manifest hash/size/slot/
id/schema enforcement, traversal and unexpected-file rejection, foreign
slot/id/installation/serial, newer-schema rejection plus preservation of
unreadable/newer on-disk files, running-game refusal (real process check
+ `pidof`), disconnected/offline device, partial copy, preview-vs-apply,
interrupted apply with `-Resume`/`-Rollback`, byte-identical Windows and
(simulated) Android round-trips, and read-only source/backup dirs.

`powershell -NoProfile -File Tools/Test-FamilyReleaseTooling.ps1`
(79 checks) covers: the signing-pipeline cases above plus package
assembly — rehearsal + full-path assembly, entire-player archiving,
partial/mixed/non-candidate/stale content manifests, missing player
data/runtime DLLs/Mono, planted saves/keystores/validation tokens,
missing notices/guide, reused output dirs, failed-run publishes
nothing (pre-assembly AND post-zip self-check failures leave no output
directory at all — assembly is staged in temp and published
atomically), manifest outside the zip with post-assembly hashes,
honest dirty-source recording, and absent-android named blockers.
Fixtures use temporary test keystores and a simulated toolchain only —
fixture artifacts are never release evidence.
