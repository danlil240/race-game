# M7 Task 0 — family release prerequisite reconciliation and baseline gate

Date: 2026-10-17. Branch: `feat/m7-family-release`. Measured baseline:
`4d97a20` (`docs: add the milestone-7 family-release implementation
plan`) **plus the pre-existing uncommitted working-tree modifications**
described below — every suite figure is a fresh run on that revision.

Plan: `docs/superpowers/plans/2026-10-16-family-release.md`, Task 0.
Dispatch rulings (binding): no hardware is available in this
environment (owner-confirmed); no signing material exists and none was
generated here; the inherited M4→M5→M6 production chain stays open —
this task records each row's state and next action, it does not close
gates, produce substitute content, or bypass `FullContentValidation`.

**Status: reconciliation complete; the production-prerequisite chain
is unchanged-open.** Fresh automated evidence is green (one
pre-existing credential-gated skip). Every hardware/human/
authored-content/signing row stays `notVerified` or `blocked` with a
named owner. No milestone was relabeled; this report is not milestone
acceptance. Fixture-eligible M7 tooling (Tasks 1–5) may proceed;
real candidate generation stays blocked on the inherited chain.

## Environment facts (recorded, not secrets)

- **Source.** HEAD `4d97a20ad8e91c2205847a565d0556ed42f8580d` on
  `feat/m7-family-release`. Working tree at task start: **50 modified
  files, 0 untracked** (the pre-existing set documented in the plan's
  Starting point) — by area: 28 `Assets/FamilyRacing/Art/`
  (VisualBenchmark materials + character/vehicle assets), 19
  `Assets/FamilyRacing/Generated/` (Foundation scene, font assets,
  Driving/Physics/VisualBenchmark assets), 1
  `Assets/Plugins/Android/AndroidManifest.xml`, 2 `ProjectSettings/`
  (`ProjectSettings.asset`, `GraphicsSettings.asset`). Of these, only
  `Generated/FoundationLatinFont.asset` (regenerated font atlas,
  ~31k-line diff) and `ProjectSettings.asset` (`ps4Passcode`
  field populated) carry content diffs; the rest are line-ending /
  stat-only churn. **51 files at report time** — the baseline Unity
  batch runs re-touched `Generated/Driving/graybox-01.asset` (no
  content diff); FontAssets are rewritten by batch reimport, so the
  dirty set drifts during test runs. Nothing was staged, reverted, or
  modified by this task.
- **Devices.** Bundled platform-tools
  `adb devices` (`6000.3.26f1` AndroidPlayer SDK) re-run this session:
  daemon started, **"List of devices attached" — zero entries**. No
  tablet or second device is attached. All device-dependent rows stay
  open — none are closed or inferred.
- **Signing material.** No `*.keystore`/`*.jks` anywhere in the repo;
  no `FAMILY_RACING_KEYSTORE`, `FAMILY_RACING_KEY_ALIAS`,
  `FAMILY_RACING_KEYSTORE_PASSWORD`, or `FAMILY_RACING_KEY_PASSWORD`
  environment variables are set in this environment. No key was
  generated, no secret was read into any log. The owner has authorized
  generating a release key later in Task 1.
- **Release identity inputs (for `Release/release.json`, Task 1).**
  Serialized project settings: Android `applicationIdentifier`
  `com.daniel.familyracing` (`ProjectSettings.asset`), company/product
  `Family Racing`, `AndroidBundleVersionCode: 1`,
  `AndroidMinSdkVersion: 25`, `AndroidTargetSdkVersion: 0` (auto =
  highest installed; the exact target SDK must be resolved from the
  qualified toolchain during execution, per the shared contract),
  `bundleVersion: 0.1.0` — and `FoundationProjectSetup.cs:206`
  re-writes `bundleVersion = "0.1.0"` on every generic setup pass, so
  no durable release-version source exists yet.
  **Version-code ruling (recorded):** no discoverable same-identity
  *signed* predecessor exists → the intended `release.json` uses
  `versionName "1.0.0"`, `androidVersionCode 1`,
  `previousAndroidVersionCode 0`. Debug-signed installs (the only
  APKs ever produced here) are a **separate migration problem** — an
  in-place release-key update over a debug-key install is not claimable.
- **Credentials.** `AZURE_SPEECH_KEY` unset — the EditMode voice-clip
  `[Ignore]` remains environmental and final bilingual audio is
  credential-gated on the owner.
- **Baseline note.** All totals below are "commit `4d97a20` +
  uncommitted pre-existing modifications", not a clean-tree run.

## Baseline battery — fresh runs on the measured baseline

| Suite | Command | Result |
|---|---|---|
| Core (offline runner) | `Tools\Verify.ps1 -Mode CoreOffline` | **843 passed / 0 failed / 0 skipped** (exit 0) |
| EditMode | `Tools\Verify.ps1 -Mode EditMode` | **959 passed / 0 failed / 1 skipped** (960 total; `Logs\verify-editmode-results.xml`, log `Logs\verify-unity-editmode.log`) — the skip is `AdapterEditModeTests.EveryCueHasWiredClipForBothLocales` (pre-existing `[Ignore]`; tutorial voice clips pending owner `AZURE_SPEECH_KEY`) |
| PlayMode | `Tools\Verify.ps1 -Mode PlayMode` | **322 passed / 0 failed / 0 skipped** (`Logs\verify-playmode-results.xml`, log `Logs\verify-unity-playmode.log`) |
| Python tooling | `python -m unittest discover -s Tools -p "Test*.py"` | **316 tests, OK — 0 failed / 0 errors** (~356 s) |
| Verify tooling | `Tools\Test-VerifyTooling.ps1` | **25 passed / 0 failed** |
| Android offline tooling | `Tools\Test-AndroidOffline.ps1` | **46 passed / 0 failed** |

Comparison against the M6 merged-tree baselines at `e4d4876`
(CoreOffline 843/843, EditMode 959/960 +1 credential skip — recorded in
the M6 execution ledger): **no new failures, same totals.** PlayMode
322 vs the M5-closeout 309 reflects the PlayMode coverage added by M6
Tasks 1–3. Python 316 vs the M5-closeout 279 includes
`TestFullContentScenario` (32 tests). The only non-pass remains the
documented credential-gated `[Ignore]`. Pre-battery result files were
frozen to `Logs\m7-t0-frozen\` before the runs overwrote the common
`Logs\verify-*` paths; console captures are under
`Logs\m7-t0-battery\` (gitignored).

Execution note: the first EditMode launch aborted on a transient
"Multiple Unity instances cannot open the same project" lock
contention (a prior instance was releasing the lock at that moment);
it was re-run after PlayMode completed and produced the fresh green
result above. No source defect.

## Source reconciliation at `4d97a20`

| Claim in prior reports / plan | Verified at HEAD |
|---|---|
| Packaged player binds no gameplay catalog | Confirmed — `Generated/Foundation.unity`: `gameplayCatalog: {fileID: 0}` (line 426); `track`/`coursePrefab` still single-bind the shared graybox course (lines 417, 428) |
| `CourseLoader`, `GameplayLoopSetup`, `SunnyIslandCampaignAuthoring` do not exist | Confirmed absent — no `Runtime/Driving/CourseLoader.cs` (no `CourseLoader` reference anywhere in `Assets/`), no `Editor/GameplayLoopSetup.cs`, no `Editor/SunnyIslandCampaignAuthoring.cs` |
| `Generated/FullContent` production inventory | Confirmed absent — no `Assets/FamilyRacing/Generated/FullContent` directory; `Generated/Driving/` holds only graybox/tutorial/practice/LAN assets |
| M6 Tasks 1–3 multiworld/progression code exists | Confirmed — `GameplayCatalog.Campaigns`/`FindCampaign`/`FindCampaignForEvent` (`Core/Progression/GameplayCatalog.cs:34,73,86`), `AdventureRules.Evaluate`/`CanAccessCourse` (`Core/Progression/AdventureRules.cs:48,97`), `campaign.selectWorld` and schema-7 `progression.worlds` in `FoundationService(.Progression).cs`/`FoundationState.cs` |
| M6 Task 4 slice-A validation/build plumbing exists | Confirmed — `Editor/FullContentValidation.cs` (`Validate(GameplayCatalogAsset, DrivingContentCatalog)`, line 110), `Editor/FullContentSetup.cs`; `Verify.ps1` `ValidateSet` (lines 55–59) lists `FullContentSetup`, `WindowsFullContent(Ordinary)`, `WindowsFullContentPreview`, `AndroidFullContent(Ordinary)`, `AndroidFullContentPreview`; `FullContentValidationTests.cs` pins the gate on incomplete inventory. Full-content flavors call `FullContentSetup.Create()` first (`FoundationBuild.cs:130–160`) and therefore fail honestly at the content gate today |
| M6 Task 9 slice-A external harness exists | Confirmed — `Tools/FullContentScenario.py` + `Tools/TestFullContentScenario.py` (in the 316-test Python run). Live scopes stay slice-B-deferred: no production inventory to traverse |
| GameplayLoop build flavors do not exist | Confirmed — no `*GameplayLoop*` modes in the `Verify.ps1` `ValidateSet`; `Build-Windows.ps1` accepts only `Development|Ordinary|Physics`; `Build-AndroidOffline.ps1` has no `-Presentation GameplayLoop` |
| Android wrapper builds debug APKs only | Confirmed — `Build-AndroidOffline.ps1` runs `:launcher:assembleDebug` (line 169) and publishes `launcher\build\outputs\apk\debug\launcher-debug.apk` (line 177) for **both** `-Configuration Development` and `Ordinary`; no release/signing path exists |
| No release tooling/config exists | Confirmed — no `Release/` directory, no `FamilyReleaseSettings.cs`, no `Build-FamilyRelease.ps1`/`Sign-AndroidRelease.ps1`/`Backup-FamilyProfiles.ps1`/`ReleaseQualification.py`, no `docs/FAMILY_*` guides |
| Contract identities | LAN wire protocol **3** (`NetworkSettings.cs:17`), observation schema **7** (`NetworkSettings.cs:33`), gameplay rules **gameplay-4** (`NetworkSettings.cs:47`), content version **full-content-1** (`GameplayCatalog.cs:25`, `FullContentSetup.cs:31`), profile schema **3** (`ProfileValidation.cs:20`) |
| `ProfileStore` save layout | Confirmed — `persistentDataPath/Profiles` selected in `FoundationApp.cs:879`; `profile-{0..3}.json` primaries + `.json.bak` siblings (`ProfileStore.cs:79,84`), `File.Replace` atomic save (line 55), unsafe-overwrite rejection via `ProfileValidation.Check` |
| M5 authored-content external evidence | Carried, unchanged — `Logs\t0-smoke`/`task11-*` verdicts `partial` with `catalog.available=false`, `campaignEvents=0`; still the honest ceiling of a no-catalog build |
| AndroidManifest | `Assets/Plugins/Android/AndroidManifest.xml` carries only LAN permissions (INTERNET, ACCESS_NETWORK_STATE, CHANGE_WIFI_MULTICAST_STATE); no signing/debuggable attributes — final-APK inspection is a Task 2 gate |

## Open dependency / evidence table

Every inherited or M7-introduced open row, its state at baseline,
evidence anchor, next action, and owner. Fixture-labeled M7 tooling
work (Tasks 1–5) may proceed on rows marked "fixture-eligible"; rows
marked "production" gate real candidate generation.

| # | Open row | State at `4d97a20` | Evidence | Next action | Owner |
|---|---|---|---|---|---|
| 1 | M4 A7 Lite visual/performance acceptance (real bake, owner approval on SM-T220, both classes/cameras, four racers tablet-hosted, 30-min sustained render + memory) | `notVerified` — no device | `docs/tasks/visual-benchmark-report.md`; `adb devices` empty (re-run this session) | Attach SM-T220; run the M4 device matrix on a fresh benchmark build | Daniel (hardware + acceptance); implementer measures/fixes — **production gate** |
| 2 | Real lightmap bake (`LightingDataAsset` for `VisualBenchmark.unity`) | `notVerified` | `visual-benchmark-report.md` — GI flags set, no bake; no `LightingDataAsset`/`.exr` in Assets | Bake in the editor before the device arm | Implementer — **production**, bundled in row 1 |
| 3 | M5 Task 9: two authored Sunny Island courses, 26 events, `CourseLoader`, `GameplayLoopSetup`, four GameplayLoop flavors | `blocked` on row 1; absent in source | `docs/tasks/gameplay-loop-report.md`; source checks above | Dispatch under the M5 plan only after row 1 evidence; then re-run M5 smoke/campaign/LAN on the authored build | Implementer dispatch — **production** |
| 4 | M5 authored-content external rows (`campaign.*`, `garage.purchase`, `timeTrial.record`, `lan.unevenUnlocks`, `lan.tuningBaseline`, `lan.delivery.conflicting`) | `notVerified` (`contentUnavailable`) | `Logs\task11-*` via `gameplay-loop-report.md` | Re-run `GameplayLoopScenario` scopes on the authored build | Follows row 3 |
| 5 | M6 Tasks 5–8: six courses, eight models, 78 authored events (Redrock → Snowy order), liveries/rivals, production content under `Generated/FullContent` | `blocked` on rows 1+3 | `docs/CONTENT_MATRIX.md` — every row `notVerified`; `FULL_CONTENT_RUNBOOK.md` "Current state" | Execute M6 Tasks 5–8 once the M5 first world exists | Implementer — **production** |
| 6 | M6 Task 4 slice B: `CourseLoader` extension, `DrivingWorld` multi-course binding, course-switch PlayMode tests | `blocked` on row 3 | M6 ledger `.superpowers/sdd/2026-10-15-full-content/progress.md` | Resume after authored courses exist | Implementer — **production** |
| 7 | M6 Task 9 slice B: live 78-event traversal, modes 12-combination run, LAN scope, pacing | `blocked` on rows 1+3+5 | `docs/tasks/full-content-report.md` (slice A harness delivered) | Run `FullContentScenario.py` scopes on the production build | Implementer + family pacing playtests — **production** |
| 8 | M6 Task 10 device qualification | `blocked` on all above | M6 ledger | After content + hardware exist | Implementer with hardware — **production** |
| 9 | Physical LAN matrix: tablet↔tablet, tablet↔PC both directions, offline Wi-Fi, four humans incl. a **fourth physical device**, impaired network, ordinary-build play | `notVerified` — zero devices attached | `visual-benchmark-report.md` rows 6–12; `gameplay-loop-report.md` row 19 | Supply devices incl. the fourth; run `NetworkSoak`/`FullContentScenario --connections` on disconnected Wi-Fi | Daniel (devices); implementer runs |
| 10 | Owner / Hebrew / child / family reviews: visual quality, RTL + pronunciation, comprehension, losses progress, first-world then full-campaign duration/enjoyment | `notVerified` — no human sessions | `visual-benchmark-report.md` rows 15–16; `gameplay-loop-report.md` row 20 | Human sessions once an authored build exists; M7 Task 6 sessions on ordinary packages | Daniel + Hebrew reviewer + child/family |
| 11 | Final bilingual voice clips (14 cues × 2 locales) + listening review | `notVerified` — credential-gated | `AZURE_SPEECH_KEY` unset this session; `EveryCueHasWiredClipForBothLocales` `[Ignore]` in fresh EditMode | Set credential, run `Generate-TutorialCues`/M6 Task 8 authoring, un-ignore, listen | Credential owner (Daniel) + Hebrew reviewer |
| 12 | Ordinary-build performance arm: matched-workload instrumentation-overhead A/B + ordinary device play | `notVerified` | `visual-benchmark-report.md` row 13; ordinary endpoints compile out by construction | PC A/B arm runnable now; device arm waits on row 1 | Implementer (+ hardware for device arm) |
| 13 | Gen-2 (post-rejoin) process-memory evidence | `notVerified` — tooling fixed, no qualifying run | `GenerationSamplers` shipped in M5 Task 1; qual-5/6 evidence predates it | Produce gen-2 samples in a fresh qualifying run | Implementer |
| 14 | Family signing key custody | `open` — no key exists | This report: no `*.keystore`/`*.jks`, no `FAMILY_RACING_*` env | Task 1/2 generates a new owner-controlled key, records the public certificate SHA-256, establishes recoverable backup — never in tracked files/logs | Daniel controls storage/recovery; implementer prepares tooling — **production** for real candidates |
| 15 | Release identity + version progression | `resolved-by-ruling` for inputs | Serialized settings above; ruling: `versionName 1.0.0`, `androidVersionCode 1`, `previousAndroidVersionCode 0` | Task 1 implements `release.json` + `FamilyReleaseSettings` applying these exact values; debug-signed installs stay a separate documented migration | Implementer — fixture-eligible |
| 16 | M1–M3 device/human acceptance rows (install/touch/on-device perf, feel review, LAN acceptance) | `notVerified` — unchanged | `IMPLEMENTATION_STATUS.md` milestone ledger | Fold into M7 Task 6 physical-matrix runs | Daniel (hardware/humans) + implementer |
| 17 | Fresh-install offline + update/backup qualification on ordinary packages | `notVerified` | No release packages exist yet | M7 Tasks 2–5 build the tooling with fixtures; physical rows run in Task 6 | Implementer (tooling); Daniel + implementer (physical) |

Closed-in-prior-milestone rows re-confirmed non-recurring in the fresh
battery: the credentialed-rejoin input stall, the impaired-link
PlayMode failures, and the font-fixture EditMode failure (all fixed in
M5 Task 1) did not recur — EditMode 959/960, PlayMode 322/322.

## Milestone posture — unchanged

M1 in progress (device/human), M2 in progress (device/human + ordinary
perf), M3 development complete / acceptance open (physical matrix),
M4 development complete / acceptance open (A7 Lite matrix, owner
review, bake), M5 code complete for dispatched scope / acceptance not
met (authored content + device/human rows), M6 fixture scope complete /
production blocked (Tasks 5–8, 4-B, 9-B, 10 open), M7 Task 0
reconciliation recorded. **No milestone is accepted; this task changed
none of them.** Real release-candidate generation stays blocked on
rows 1–8 + 14; fixture-eligible M7 Tasks 1–5 proceed next.

## Self-review notes

- All suite figures are fresh runs on `4d97a20` + pre-existing dirty
  files, not carried M6 numbers: `Logs\verify-editmode-results.xml`
  (04:22Z), `Logs\verify-playmode-results.xml` (04:21Z), console
  captures under `Logs\m7-t0-battery\`. The single skip is the
  documented credential-gated `[Ignore]` — not a new failure.
- `adb devices` was re-run (not trusted from the M6 report): zero
  devices.
- No keystores or `FAMILY_RACING_*`/`AZURE_SPEECH_KEY` env vars exist;
  only metadata was recorded — no secret material entered any log.
- The working tree's pre-existing modifications were left untouched;
  Unity batch runs added stat-only churn (`graybox-01.asset`) and
  rewrote font atlases — recorded, not staged. This task commits only
  `docs/tasks/family-release-report.md`.
- Every source claim above was verified by direct inspection at HEAD
  (file presence, line-level constants, tool parameter sets) — not
  copied from prior reports.
- Nothing was pushed; no vault writes.
