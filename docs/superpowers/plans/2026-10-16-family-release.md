# Milestone 7: Family Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans or, if selected by the owner, superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a privately distributed, signed Android APK and Windows x64 package that preserve family progress through updates and pass the physical-device and family acceptance gates.

**Architecture:** Extend the existing full-content build pipeline with explicit release identity, Android release signing, package verification, and an evidence-based release gate. Reuse the existing profile store, content validators, external scenarios, and performance collectors. Keep development qualification builds separate from the ordinary packages the family installs; qualify both from the same source and content.

**Tech Stack:** Unity 6000.3.26f1, C# 9 / .NET Standard 2.1 Core, URP 17.3.0, NGO 2.7.0 / UTP 2.6.0, Android ARM64 IL2CPP / OpenGL ES 3, Windows x64 Mono, PowerShell and Python tooling.

**Spec:** [Approved game design](../../../GAME_PLAN.md), especially sections 3–5. Prerequisites remain owned by the [M5 plan](2026-10-14-complete-gameplay-loop.md) and [M6 plan](2026-10-15-full-content.md). This document plans M7; it does not approve or claim completion of those prerequisites.

## Global constraints

- “The initial release is privately distributed to the family.” No store publication, accounts, advertisements, purchases, internet multiplayer, cloud saves, or profile transfers.
- “All gameplay works offline after installation”; include every course, font, translation, sound, and voice prompt in the packages.
- “Samsung Galaxy Tab A7 Lite SM-T220; assume the weaker 3 GB configuration.” Keep tablet-to-tablet operation without a PC and four physical racers in scope.
- “Three worlds, six shared courses, one combined campaign”; four cars, four motorcycles, 78 authored events, Hebrew and English.
- “Tablet: 30 fps target, 95th-percentile steady-state frame time at or below 40 ms, without recurring stalls above 100 ms.” Start with the specified 800 MB process-memory budget; investigate growth across rematches. PC target: 60 fps at 1080p.
- “Preserve the Android signing key and application identity so updates retain saves.” Keep `com.daniel.familyracing`, company `Family Racing`, product `Family Racing`, and the existing `persistentDataPath/Profiles` layout.
- “Validate loaded values; preserve unreadable or newer-version saves rather than silently overwriting them.” Keep atomic saves, `.bak` recovery, existing reward receipts, and all four profile slots.
- Arcade stays the release backend; `PhysicalAssisted` remains opt-in development content. Do not change profile schema, observation schema, or LAN protocol just to add packaging metadata.
- “Every game control and feature is command-accessible; complete logical game state is streamed to a structured sink for end-to-end agent testing.” Preserve that development-build contract; ordinary family builds keep validation endpoints compiled out.
- “The budget is a planning cap, not authorization to purchase.” No paid certificates, tools, assets, or services are required by this plan.

## Review focus

1. Setup silently resets the release version or Android identity: Task 1 tests repeated setup and effective exported metadata.
2. An ordinary Unity export is still Gradle-debuggable or debug-signed: Task 2 verifies the final APK, not only `BuildOptions.None`.
3. A passing fixture, partial inventory, stale binary, or missing device row becomes a release pass: Tasks 3 and 5 bind package hashes and required evidence independently.
4. Updating or restoring loses progress, duplicates rewards, overwrites newer saves, or touches another installation: Tasks 4–5 test all slots, identity checks, interrupted operations, and negative update cases.
5. Development automation passes while ordinary touch, Hebrew, offline LAN, or sustained play fails: Task 6 requires human-driven ordinary packages on the actual hardware.

## Starting point — inspected 2026-10-16

Source: `main` at `e4d4876e748a9e4df3ee1ebc4898c0d5c00f0f19`. Existing material, generated asset, scene, Android manifest, and ProjectSettings edits were present before planning. They must not be reset, staged wholesale, or silently excluded from a future release candidate.

- M6 multiworld progression, schema-7 observations, full-content validation/build entry points, and the `FullContentScenario` harness are present. `NetworkSettings` currently declares LAN protocol 3 and gameplay rules `gameplay-4`; profile schema remains 3.
- `Assets/FamilyRacing/Generated/FullContent` and `Runtime/Driving/CourseLoader.cs` are absent. Full-content setup currently validates inventory; it does not bind a production catalog/course into the runtime. The M6 production loader/binding work must be completed before these flavors can qualify.
- The full-content report explicitly covers Task 9 slice A tooling only. There is no production 78-event traversal or accepted full-content package. Older status entries describing only M6 Task 0 are incomplete summaries of source progress, not proof that later gates passed.
- `Tools/Build-AndroidOffline.ps1` runs `:launcher:assembleDebug` and publishes `launcher-debug.apk` for both Development and Ordinary. Unity development flags and Android signing/build type are separate concerns.
- `FoundationProjectSetup.ApplyPlayerSettings()` resets `bundleVersion` to `0.1.0`; the serialized Android version code is 1. No durable release version source exists.
- `ProfileStore` already writes `profile-0.json` through `profile-3.json`, uses `.json.bak`, and rejects unsafe overwrites. Extend its qualification, not its save format.
- M1–M6 physical/human acceptance remains open. The last recorded device check found no attached tablet; device availability was not rechecked for this planning-only change. The fourth physical device and licensed, reviewed voice prompts remain dependencies.
- No tests, builds, key generation, installations, purchases, or device qualification were run while creating this plan. Historical passing counts are not fresh M7 evidence.

## Approach and boundaries

| Approach | Decision |
|---|---|
| Extend ordinary full-content builds, then qualify immutable packages | **Recommended.** Keeps existing runtime/save behavior and adds the missing release guarantees. |
| Ship the current debug APK and graybox player | Reject: no production catalog, durable release signing, or complete acceptance evidence. |
| Add stores, an installer/updater, cloud backups, or a new vehicle backend | Defer: expands the approved private-family release without resolving its current blockers. |

M7 owns packaging, version identity, signing, same-installation backup/recovery instructions and tools, update qualification, final acceptance reconciliation, and the family handoff. M4 tablet visual approval, M5 first-world production, and M6 remaining content/audio/pacing stay in their existing plans. Do not duplicate those implementations here.

## Dependencies and execution order

`0 prerequisite reconciliation → 1 release identity → 2 Android signing → 3 package assembly → 4 backups → 5 update/evidence gate → 6 physical and family acceptance → 7 handoff`

Tasks 1–5 may develop and test their tooling with clearly labeled fixtures while hardware/content is unavailable. A fixture never qualifies a package. Real candidate generation requires M4/M5/M6 production prerequisites; final release requires every inherited mandatory acceptance row. No fixed deadline is promised.

| Gate | Required evidence | Owner |
|---|---|---|
| M4 tablet benchmark | Real bake, owner visual approval, both classes/cameras, A7 Lite hosting four racers, sustained render/memory evidence | Daniel supplies hardware/review; implementer measures and fixes |
| M5 first world | Two courses, 26 events, runtime catalog/course binding and first-world family review | Implementer, then family |
| M6 complete content | Six courses/eight models/78 events; real per-event objectives/rewards; final reviewed EN/HE audio; measured campaign pacing | Implementer + Hebrew reviewer + family |
| Signing custody | Existing family release key if one exists; otherwise a new owner-controlled key, recoverable backup, recorded public certificate fingerprint | Daniel controls key storage/recovery; implementer prepares tooling |
| Physical matrix | Two tablets + PC + fourth supported device, disconnected-internet Wi-Fi and impairment capability | Daniel supplies access; implementer runs qualification |
| Candidate baseline | Recorded source commit, reviewed asset state, tool versions, content fingerprint, and exact package hashes | Implementer |

## File and interface map

Paths below are relative to the repository. New files are proposals, not existing APIs.

| Responsibility | Files |
|---|---|
| Release configuration | New `Release/release.json`, `Release/acceptance-matrix.json`; new `Assets/FamilyRacing/Editor/FamilyReleaseSettings.cs` and its EditModeAdapter tests |
| Build integration | Modify `Editor/FoundationProjectSetup.cs`, `Editor/FoundationBuild.cs` under `Assets/FamilyRacing`; `Tools/Build-AndroidOffline.ps1`, `Tools/Verify.ps1`, `Tools/Test-AndroidOffline.ps1`, `Tools/Test-VerifyTooling.ps1` |
| Signing and package assembly | New `Tools/Build-FamilyRelease.ps1`, `Tools/Sign-AndroidRelease.ps1`, `Tools/Test-FamilyReleaseTooling.ps1` |
| Profile backups | New `Tools/Backup-FamilyProfiles.ps1`, `Tools/Restore-FamilyProfiles.ps1`, `Tools/Test-FamilyProfileBackup.ps1`; extend `Tests/EditMode/ProfileTests.cs` under `Assets/FamilyRacing` only for uncovered storage behavior |
| Update/acceptance reconciliation | New `Tools/ReleaseQualification.py`, `Tools/TestReleaseQualification.py`; reuse existing scenario helpers and `ProfileStore` |
| Parent instructions/evidence | New `docs/FAMILY_RELEASE_RUNBOOK.md`, `docs/FAMILY_INSTALL_GUIDE.md`, `docs/FAMILY_BACKUP_GUIDE.md`, `docs/tasks/family-release-report.md`; update `docs/ASSET_INVENTORY.md`, `docs/FEATURE_COVERAGE.md`, `README.md`, `IMPLEMENTATION_STATUS.md` |

### Shared contracts

- `Release/release.json` schema 1 stores `versionName`, positive `androidVersionCode`, nonnegative `previousAndroidVersionCode`, fixed application/company/product identities, exact Android target SDK, and expected signing certificate SHA-256. Set the first family version to `1.0.0`; choose its code strictly above every known same-identity installed predecessor. Task 0 records the predecessor evidence supporting `previousAndroidVersionCode`; zero is allowed only when no predecessor exists. Resolve SDK and certificate values during execution from the qualified toolchain/key, not guesses or placeholders. No passwords or key paths belong in this tracked file.
- `FamilyReleaseSettings.Apply(string configPath) -> void` applies release settings **after** normal setup; `Validate(string configPath) -> IReadOnlyList<string>` reports all invalid identity/version fields. Generic setup must preserve an already selected release version. Release build methods restore temporary editor settings in `finally`.
- `Tools/Build-FamilyRelease.ps1 -Config <json> -Output <new-directory>` orchestrates existing full-content validation, ordinary platform builds, signing, and assembly. Missing credentials/content fails with named reasons and no published release. Existing debug/preview workflows remain available.
- `Tools/Sign-AndroidRelease.ps1 -UnsignedApk <path> -OutputApk <new-path> -ExpectedCertificateSha256 <hex>` consumes `FAMILY_RACING_KEYSTORE`, `FAMILY_RACING_KEY_ALIAS`, `FAMILY_RACING_KEYSTORE_PASSWORD`, and `FAMILY_RACING_KEY_PASSWORD` from its process environment. Logs contain public metadata only. Test fixtures use temporary test keys and cannot become release evidence.
- The assembled `release-manifest.json` schema 1 records source commit and clean-source status, Unity/tool versions, version name/code, Android application ID/SDKs/ABI/certificate, Windows architecture/backend, arcade backend, actual gameplay/content fingerprints, content stage, per-file SHA-256/size, and relative package paths. It records `acceptanceStatus: "unqualified"` initially; M6's structural `candidate=true` alone is insufficient.
- `ReleaseQualification.py --manifest <path> --matrix <path> --evidence <new-directory> --records <record.json>...` validates all required rows and referenced hashes, then writes `result.json` with `passed|failed|partial`, per-row reasons, and `notVerified`. Exit 0 means every required row passed; any fail/gap exits nonzero. Never edits the candidate or fills absent evidence with success.
- `Release/acceptance-matrix.json` is the independent required-row inventory. Each evidence record names a row ID, measured artifact hashes, source/content identity, device/OS/configuration, test method, observation time, evidence paths/hashes, outcome, and human reviewer where required. Same-source development evidence identifies its own artifact; it cannot satisfy an ordinary-package row.

## Task 0: Reconcile prerequisites without relabeling them

**Files:** Read the M4–M6 plans/reports, `docs/CONTENT_MATRIX.md`, `docs/FULL_CONTENT_RUNBOOK.md`; create the initial `docs/tasks/family-release-report.md` dependency table.

**Interfaces:** Produces named prerequisites and their evidence links for Tasks 1–7. Existing task reports remain authoritative for their recorded runs.

- [ ] Record current source, local modifications, available devices and signing material metadata without reading secrets into logs. Reconcile M6 Tasks 1–4/9 tooling against source and keep missing content/loader/audio explicit.
- [ ] Finish the inherited M4 → M5 → M6 production chain through those plans before attempting real release candidates. While blocked, proceed only with fixture-eligible M7 tooling; no generated substitute catalog or bypass of `FullContentValidation`.
- [ ] At execution start run `Tools\Verify.ps1 -Mode CoreOffline`, `-Mode EditMode`, `-Mode PlayMode`, `python -m unittest discover -s Tools -p 'Test*.py'`, `Tools\Test-VerifyTooling.ps1`, and `Tools\Test-AndroidOffline.ps1`. Record actual totals and named skips against that revision. Freeze any reused evidence before commands overwrite common `Logs` paths.
- [ ] Record the next action and owner for every open row. Commit only this task's report when executing; a reconciliation commit is not milestone acceptance.

## Task 1: Make release identity repeatable

**Files:** `Release/release.json`; `Assets/FamilyRacing/Editor/FamilyReleaseSettings.cs`; modify `FoundationProjectSetup.cs` and `FoundationBuild.cs`; create `Assets/FamilyRacing/Tests/EditModeAdapter/FamilyReleaseSettingsTests.cs`.

**Interfaces:** Produces the configuration and `Apply`/`Validate` contracts above. Later tasks consume effective metadata from exported artifacts as well as configuration.

- [ ] Add failing tests `RepeatedSetupPreservesReleaseVersion`, `ReleaseRejectsChangedApplicationIdentity`, `ReleaseRejectsNonIncreasingVersionCode`, and `BuildRestoresSettingsAfterFailure`. Assert company/product/application ID stay fixed, invalid config causes no partial settings mutation, and an attempted code equal to/below the predecessor is rejected.
- [ ] Run EditMode and observe those specific failures before implementation. Implement the release settings object and ordinary full-content release entry points. Resolve exact SDK/certificate/version-code inputs from Task 0; retain min SDK 25 unless qualification demonstrates an approved change is needed.
- [ ] Run EditMode green. Verify two setup passes preserve selected release metadata, and both successful/failed builds restore prior editor settings. Inspect the exported manifest rather than relying solely on serialized project settings.
- [ ] Commit `feat: add stable family release identity and versioning`.

## Task 2: Build and verify a release-signed Android APK

**Files:** `Tools/Sign-AndroidRelease.ps1`; modify `Tools/Build-AndroidOffline.ps1`, `Tools/Verify.ps1`, `Assets/FamilyRacing/Editor/FoundationBuild.cs`; extend Android/Verify tooling tests; create `Tools/Test-FamilyReleaseTooling.ps1`; begin `docs/FAMILY_RELEASE_RUNBOOK.md`.

**Interfaces:** Add `-Configuration Release` to the Android wrapper, allowed only with `-Presentation FullContent`; output to `Builds/Android-FamilyRelease`. Add `Verify -Mode AndroidFamilyRelease`. The existing Development/Ordinary behavior does not silently change meaning.

- [ ] Add failing tooling tests for missing key variables, wrong certificate, empty APK, Gradle/signature failure, attempted Preview/Release, stale output reuse, secret leakage, and debug flags. Assert failure never overwrites a previous valid package.
- [ ] Export without development options, use offline `:launcher:assembleRelease` with an unsigned release output, align before signing, then sign using environment-based password inputs. Inspect the exported Gradle signing configuration and reject unexpected pre-signing/debug-key fallback.
- [ ] Verify the final APK with bundled `apksigner verify --verbose --print-certs` and APK metadata inspection: expected signer, application ID/version/code, ARM64, recorded SDKs, and `android:debuggable=false`. Require full content, bundled licenses/audio/fonts, and no validation endpoint at runtime.
- [ ] Run `Tools\Test-AndroidOffline.ps1`, `Tools\Test-VerifyTooling.ps1`, and `Tools\Test-FamilyReleaseTooling.ps1` green. After production prerequisites and the owner-controlled key are available, build a real signed APK; record hash/certificate and recoverability of the key backup without copying secrets into evidence or packages.
- [ ] Commit `feat: package a verified signed Android family build`.

## Task 3: Assemble complete, traceable family packages

**Files:** `Tools/Build-FamilyRelease.ps1`; extend `Tools/Test-FamilyReleaseTooling.ps1`; modify `FoundationBuild.cs`/`Verify.ps1` for `WindowsFamilyRelease`; update `docs/ASSET_INVENTORY.md`; create `docs/FAMILY_INSTALL_GUIDE.md`.

**Interfaces:** Produces `FamilyRacing-<version>-Android.apk`, `FamilyRacing-<version>-Windows-x64.zip`, parent guides, third-party notices, and the shared release manifest under a new caller-selected directory. The manifest sits outside the ZIP to avoid self-referential hashes.

- [ ] Add failing tests rejecting stale/mixed source-content manifests, partial inventories, missing player data/runtime DLLs, missing notices, extra save files/keys/validation tokens, and reused output directories. Assert the archive includes the entire ordinary Windows player, not just its EXE.
- [ ] Build Windows x64 Mono with ordinary options and the actual bound full-content scene. Package all runtime dependencies, copy the Android artifact without changing its bytes, and generate hashes after final assembly. Keep generated logs, synthetic profiles, keystores, and compiler/debug symbol bundles outside distribution.
- [ ] Reconcile the inventory for shipped content and dependencies, include their required license/attribution text, and record costs. Verify fonts and EN/HE prompts are physically packaged. Windows Authenticode is not an assumed purchase or acceptance requirement; document the actual publisher/signature status accurately.
- [ ] Run packaging tooling tests green. Extract the ZIP to a clean location and launch offline under a test Windows account without Unity installed; verify settings/profiles use the established data directory and all content is accessible under its intended unlock rules. Archive exact package hashes and mark acceptance unqualified.
- [ ] Commit `feat: assemble traceable offline family release packages`.

## Task 4: Provide qualified same-installation backup and recovery

**Files:** New backup/restore PowerShell tools and `Tools/Test-FamilyProfileBackup.ps1`; `docs/FAMILY_BACKUP_GUIDE.md`; existing `ProfileStore.cs`/`ProfileTests.cs` only if a new regression demonstrates a storage defect.

**Interfaces:** Both tools accept `-Platform Windows|Android`, explicit `-ProfilesPath`, and `-DeviceSerial` for Android. Backup accepts `-Destination <new-directory>` and the installed release manifest. Restore accepts `-Backup <directory> -ExpectedManifest <path> -Apply`; without `-Apply` it validates and previews only. The backup manifest records original installation/device label, profile IDs/slots/schema, exact allowed filenames and hashes, and original missing-file states.

- [ ] Add failing tests for malformed/mismatched hashes, path traversal, unexpected files, foreign slot/identity, unsupported newer schema, running game, disconnected device, partial copy, and interrupted restore. Assert failure preserves existing primary and backup bytes and retains a pre-restore snapshot. No recursive copy/delete outside the validated Profiles directory.
- [ ] Back up only `profile-{0..3}.json` and their `.bak` siblings after the game stops. Stage and hash-check a complete backup before marking it usable. Restore only to the same installation/device with explicit matching identity and validated schema; do not merge coins/unlocks or offer transfer between devices. Keep a recovery journal and all pre-restore bytes so an interruption can be completed or rolled back before relaunch.
- [ ] On Windows qualify the actual LocalLow path. On Android discover and verify the installed player's real data location, then qualify scoped ADB pull/push on each supported tablet with its ordinary APK; no root or development-only `run-as` assumption. If access is denied, leave this row blocked and resolve a supported recovery path before claiming it works. Do not present a speculative phone file-manager procedure as tested.
- [ ] Run `Tools\Test-FamilyProfileBackup.ps1` and relevant Core profile tests green. Using disposable profiles, prove automatic `.bak` recovery and an external backup round-trip on each platform. Verify unreadable/newer saves remain untouched. Explain that uninstall/clear-data is not an update step and that local `.bak` files are not a backup against device loss.
- [ ] Commit `feat: add verified family profile backup and recovery tools`.

## Task 5: Prove updates and implement the release evidence gate

**Files:** New `Release/acceptance-matrix.json`, `Tools/ReleaseQualification.py`, `Tools/TestReleaseQualification.py`; extend the release/backup guides; reuse `Tools/NormalProfileScenario.py`, `GameplayLoopScenario.py`, `FullContentScenario.py`, and Core profile migration/reward tests.

**Interfaces:** Produces the shared evidence gate and update records. Use a same-key predecessor with lower version code to rehearse the first release update; retain that artifact as the baseline for future updates. A rehearsal is explicitly labeled, not presented as an upgrade from a shipped version.

- [ ] Add failing reconciler tests `MissingRequiredRowIsPartial`, `FixtureCannotPassProduction`, `ArtifactHashMismatchFails`, `DevelopmentCannotSatisfyOrdinaryRow`, `TamperedEvidenceFails`, `UnknownOrDuplicateRowFails`, and `HumanRowNeedsNamedReview`. Verify an independently complete fixture matrix passes only the tooling test, never a production release.
- [ ] Implement the gate contract without arbitrary success overrides. Matrix rows cover every inherited acceptance requirement, all physical rows in Task 6, both platform update/backup paths, licenses, key custody, and package identity. Required failures/skips/absences block release.
- [ ] On disposable test installations, earn representative progress through normal gameplay in all four slots: both locales/cameras, tutorial progress, coins, purchases, unlocks, regional stars, finale receipts, bounties, cosmetics, and Time Trial records. Save, quit, back up, install the newer signed APK **over** the predecessor, and replace the Windows binaries while preserving LocalLow data. Reopen each slot, continue a race, and verify prior receipts cannot reward twice. Compare semantic state plus retained evidence, allowing only documented migration differences. Rehearsal fixtures must use distinct increasing version codes and preserve the real candidate's configured predecessor evidence.
- [ ] Test rejection of a mismatched Android signer, non-increasing code in the build pipeline, unavailable storage, and unreadable/newer-schema profiles. Installation rejection must leave the old app/data usable. Debug-signed predecessors are a separate migration problem: never silently uninstall them or claim release-key continuity. Preserve their saves and document the tested transition before any removal.
- [ ] Run `python -m unittest discover -s Tools -p 'TestReleaseQualification.py'` and applicable existing profile/reward tests green. Repeat update/restart checks on actual ordinary packages; development state capture can support comparison but cannot replace physical ordinary-package observations.
- [ ] Commit `test: gate family releases on update and acceptance evidence`.

## Task 6: Qualify the exact packages with devices and family

**Files:** `Release/acceptance-matrix.json`, `docs/FAMILY_RELEASE_RUNBOOK.md`, `docs/tasks/family-release-report.md`; existing scenario/performance tools, with focused fixes and regression tests only when failures require them.

**Interfaces:** Produces immutable evidence records for Task 5, with actual APK/ZIP hashes and named human reviews. Required duration is measured racing time, not process uptime.

- [ ] Run the production M6 catalog/campaign/modes/LAN scopes and inherited command/state reconstruction checks on matching development builds, with no fixture substitutions. Require all 78 events and each course/class Quick Race/Time Trial combination, correct objectives/rewards, generation-2 memory samples after rejoin, and M6 pacing evidence.
- [ ] Run the physical matrix below with ordinary packages; use OS/device measurements and the existing permitted performance recorder, not validation sockets. Check the absence of validation listeners/tokens even when validation arguments are supplied. Measure instrumentation overhead separately with matched workloads.
- [ ] Run family sessions in both languages. A five-year-old completes onboarding, identifies controls, finishes the introductory race, understands the reward, and starts another event with little reading help. Record assistance, readability, touch issues, camera comfort, pronunciation, enjoyment, and voluntary replay. A Hebrew-speaking reviewer checks RTL, mixed numbers/IP fields, complete text, and every prompt; owner reviews visual/audio quality on the A7 Lite.
- [ ] Fix failures in the owning subsystem with focused regression tests. A new binary/content hash requires fresh affected evidence and a new candidate; reuse only explicitly unaffected evidence with its original identity disclosed. Do not weaken the tablet/four-player targets to get a pass.
- [ ] Re-run the evidence gate; it must return `passed` with zero required `notVerified` rows. Commit the report and any separately reviewed fixes as `test: qualify family release on physical devices` only when the recorded work supports that description.

### Required physical matrix

| Row family | Required observations |
|---|---|
| Fresh install/offline | Both platforms launch with no internet; complete tutorial and representative content; bundled fonts/audio work; settings/progress survive restart |
| Host permutations | Tablet↔tablet without PC; tablet↔PC with each hosting; four physical devices including A7 Lite host; also PC host |
| Join failures | Permission denial where applicable, blocked discovery, manual-IP fallback, full lobby, unreachable host, incompatible gameplay/content builds; useful bilingual recovery |
| Session recovery | Disconnect during countdown, race, pause, finish, result delivery; reconnect ownership/AI attribution; host loss; no duplicate/aborted rewards |
| Impaired Wi-Fi | Normal baseline plus measured/injected 100 ms RTT, ±30 ms jitter and 2% loss; no routine large visible correction jumps |
| Sustained ordinary play | At least 30 racing minutes for A7 Lite-host and PC-host runs; both classes/cameras, rematches/course switches, profile switching, application restarts; inspect thermal slowdown and memory growth |
| Performance | A7 Lite p95 ≤40 ms, target 30 fps, recurring >100 ms stalls absent; initial 800 MB memory budget; PC target 60 fps at 1080p; report actual measurements and all deviations |
| Updates/recovery | All four profiles survive same-key Android update and Windows binary replacement; `.bak` and external same-installation backup recovery qualified on each platform |
| Ordinary input/presentation | Real keyboard/touch, simultaneous/held input, focus loss/recovery, both cameras, reduced shake, volume/replay controls, EN/HE layout and voice review |

## Task 7: Prepare the family handoff and close the milestone

**Files:** Finalize parent guides, license inventory, `README.md`, `IMPLEMENTATION_STATUS.md`, `docs/FEATURE_COVERAGE.md`, and `docs/tasks/family-release-report.md`; freeze final release output outside tracked source.

**Interfaces:** Produces the accepted APK/ZIP, checksum manifest, concise English/Hebrew parent instructions, recovery instructions, and an evidence index. Distribution is a separate owner-directed action after the concrete package is reviewable.

- [ ] Have a parent follow the guides on ordinary devices: verify/download from the chosen private location, install/update, start offline, host/join, permit private-network firewall access without disabling the firewall, recover a failed join, back up and recover progress. Fix documentation discovered to be unusable.
- [ ] Confirm the owner can locate and recover the signing key backup and distinguish it from player-save backups. Confirm all bundled third-party notices and provenance are complete; retain the previous accepted packages and corresponding manifest/evidence for recovery.
- [ ] Run the full automated gate once on the final source, rebuild if source/content changed, verify final package hashes/signature and rerun affected qualification. Run `ReleaseQualification.py` against the final required matrix; archive its `passed` result and exact commands/tool versions. Do not run a compiler after freezing the final hash and then reuse the old approval.
- [ ] Present the concrete candidate, remaining nonblocking known issues, guides and evidence for owner release review. Only after the required family/device rows and final owner acceptance pass, change M7 to accepted and reconcile inherited milestone rows using their own evidence. Retain any unresolved mandatory row as open rather than declaring release complete.
- [ ] Commit `docs: finalize the qualified family release handoff`. Share/upload only to the private destination explicitly requested by the owner; no store or automatic publication.

## Definition of done

- Signed, non-debuggable Android release APK and complete ordinary Windows x64 package, with the production catalog actually loaded, all content offline, and arcade as default.
- Stable application/save identity, recorded version progression and signer, recoverable owner-held signing key, same-installation backup/recovery and tested updates preserving all four profiles.
- Every required M1–M7 acceptance row linked to matching evidence; physical four-player LAN, sustained ordinary performance, Hebrew review, child/family playtests, and campaign pacing complete.
- Parent installation/update/recovery guides and bundled license inventory verified against the exact delivered artifacts. No fixture, skipped check, or structural inventory pass counted as playtest evidence.

## Platform references checked for this plan

- Android updates rely on signing identity continuity; retain the family signing key and verify the predecessor before promising an in-place update. [Android signing](https://developer.android.com/studio/publish/app-signing)
- Android uses `versionCode` to order releases; record it separately from the user-facing version. [Android versioning](https://developer.android.com/studio/publish/versioning)
- Align before signing, verify the finished APK, and pass passwords through supported environment inputs. [apksigner reference](https://developer.android.com/tools/apksigner)
- Unity's persistent data location is platform-dependent and identity-sensitive. Verify the actual path on supported installations before documenting backup access. [Unity 6.3 persistentDataPath](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Application-persistentDataPath.html)

## Plan review and handoff

Self-review covers approved M7 deliverables, inherited gates, exact file/interface ownership, evidence identity, and the five failure classes above. Recommended execution: native, task-by-task, because packaging/signing/update checks share one tightly coupled artifact chain. Select execution separately after reviewing this plan; this planning change does not start implementation.
