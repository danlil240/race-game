# Family Release Runbook (M7 Task 2 — Android signing pipeline)

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
- **Family acceptance** — human sessions on the ordinary packages
  (Task 6).
- **Windows packaging, manifest, qualification** — Tasks 3–5.

## Tooling tests

`powershell -NoProfile -File Tools/Test-FamilyReleaseTooling.ps1`
(38 checks) covers missing signing variables, wrong certificate,
empty/pre-signed/debuggable input, zipalign/sign/verify/Gradle
failures, missing ARM64, metadata mismatch, stale-package preservation
on failure, secret leakage, and the Preview/other+Release refusals.
Fixtures use temporary test keystores and a simulated toolchain only —
fixture artifacts are never release evidence.
