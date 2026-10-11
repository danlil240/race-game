# Foundations Continuation Implementation Plan

> **For agentic workers:** Use subagent-driven-development to implement and review the tasks in order. Track execution evidence in `IMPLEMENTATION_STATUS.md`.

**Goal:** Continue Milestone 1 with reliable local profiles, shared driving input, bilingual shell text, and a launchable Unity foundation where the installed environment permits.

**Architecture:** Keep file persistence, input composition, and text lookup in the Unity-free Core assembly. Unity adapters own Input System devices, JsonUtility, UI Toolkit, persistent-data paths, scene creation, and platform builds. A foundation control sandbox demonstrates input only; it is not a driving prototype or completed race.

**Added requirement (2026-10-11):** Task 3 must establish command-driven control and full logical-state streaming as specified in `GAME_PLAN.md`, section 3. The requirement is planned, not implemented; the existing 133 Core checks do not demonstrate a running agent-control interface.

**Tech Stack:** Unity 6000.3.26f1, C# 9, .NET Standard 2.1-compatible Core, URP, Input System, UI Toolkit, Unity Test Framework; .NET 10 NUnit harness for shared tests.

**Spec:** `GAME_PLAN.md`, Milestone 1 and technical foundations. Existing profile contracts: `docs/tasks/foundation-profiles.md`.

## Global Constraints

- Continue in the requested project root on the existing unborn `feature/family-racing` branch; preserve all existing untracked files. Do not commit or push project work in this pass.
- No paid assets, external runtime services, networking, race simulation, rewards, purchases, or campaign progression.
- Do not substitute Unity 6000.6 for the pinned 6000.3.26f1 without approval.
- Ask before network access, installations requiring elevation, or downloads.
- Core has no UnityEngine dependency and no .NET 10-only production APIs.
- Keep save schema at 1 and never silently overwrite corrupt or newer data.
- Use normal logical-order Hebrew, complete localized strings, deliberate RTL layout, and LTR diagnostic values.
- Do not claim platform launch, Hebrew rendering, or physical-device qualification from pure C# tests.
- Every implemented control/feature must have command access through the same production path, observable state/events, and a recorded agent scenario. Dedicated local validation transport is allowed; gameplay/LAN networking remains out of this milestone.

## Review Focus

- Future or corrupt profiles, including orphaned/invalid backups: preserve files and disable persistence with a safe message.
- Simultaneous opposing steering inputs and overlapping keyboard/touch controls: neutral opposing input, held actions combine predictably.
- Focus loss, UI rebuild, and pointer cancellation: clear held touch input to avoid stuck steering or boost.
- Failed settings writes: do not present an unsaved edit as persisted; retain the previously saved settings.
- Missing fonts/editor/modules/license: report explicit setup or validation gaps, not a passing milestone.
- Duplicate commands, stuck held input, predicted-vs-authoritative confusion, and incomplete/broken state capture must produce explicit failures rather than false-positive agent evidence.

## Task 1: Profile Persistence

**Files:** existing `Core/Profiles/*.cs`, `Tests/EditMode/ProfileTests.cs`, `Tools/CoreTests/FamilyRacing.CoreTests.csproj`, `.gitignore`.

**Interfaces:** Preserve all signatures and defaults in `docs/tasks/foundation-profiles.md`. Share one focused `ProfileValidation` helper between factory and store if needed.

- [x] Run existing tests against the stubs and record actual RED evidence.
- [x] Implement factory defaults and validation of every existing invariant.
- [x] Implement read-only load and backup recovery, blocking newer primary schemas even with valid older backups.
- [x] Serialize, deserialize, and validate proposed output before touching disk. Verify serialized identity and slot match the submitted profile.
- [x] Save with unique sibling temporary files, flushed contents, atomic replacement, and previous valid primary backup. Refuse identity replacement and writes over invalid/future primary or backup files or orphaned backups.
- [x] Delete only temporary files created by this save operation; leave abandoned or unknown temporary files untouched.
- [x] Run profile tests and record GREEN output. Fix the blanket csproj ignore only for the checked-in CoreTests project; ignore its bin/obj outputs.

No destructive recovery operation is added in this pass. Recovered profiles may be inspected but settings writes remain blocked until deliberate recovery tooling is implemented.

## Task 2: Shared Input and Bilingual Text

**Files:** `Core/Driving/DriverInput.cs`, `Core/Driving/DriverInputComposer.cs`, `Core/Localization/ShellText.cs`, `Tests/EditMode/InputTests.cs`, `Tests/EditMode/LocalizationTests.cs`, test harness linked-source entries.

**Interfaces:**

- Immutable `DriverInput` struct in `FamilyRacing.Core.Driving`: properties `float Steering`, `bool Drift`, `bool Boost`, `bool Gadget`, `bool Recover`, `bool ChangeCamera`, `bool Pause`; constructor accepts these in that order. Clamp finite steering to [-1, 1], neutralize nonfinite steering.
- `DriverInputComposer.Compose(DriverInput keyboard, DriverInput touch): DriverInput`: clamp steering sum; combine booleans with OR. `DriverInputComposer.FromButtons(bool left, bool right, bool drift, bool boost, bool gadget): DriverInput`: right minus left and false for remaining commands.
- `ShellText.Get(string key, string language): string`, `ShellText.Keys: IReadOnlyCollection<string>`, and `ShellText.IsRightToLeft(string language): bool` in `FamilyRacing.Core.Localization`.
- Supported languages are exactly `en` and `he`; reject invalid languages and unknown keys rather than displaying broken keys.
- Catalog keys cover title, foundation disclaimer, profile selection and four animal names, language, camera and camera choices, reduced shake, three volume labels, input sandbox, steering/drift/boost/gadget/recovery/pause, back, save, saved, blocked/recovered/save-failed statuses, and missing-font warning. Use complete sentences for statuses; do not concatenate translated sentence fragments.

- [x] Write and run failing input tests: steering extremes/NaN/infinity; left/right/both/neither; keyboard plus touch; all booleans combined; default input neutral.
- [x] Implement input value/composition contracts without simulation or device dependencies.
- [x] Write and run failing catalog tests: every key nonempty in both languages, no duplicate keys, RTL only for Hebrew, unsupported language and unknown key rejected.
- [x] Implement the small static bilingual catalog.
- [x] Run the shared input/localization tests; record evidence.

## Task 3: Unity Foundation Shell and Build Seams

**Status (2026-10-11, updated):** Task 3 locally executable qualification is verified — Unity 6.3 imports, Windows Mono development build succeeded, the offline Android path builds a real APK fully offline (latest run `Logs\android-offline\20261011-134021-98837ad9964547dba7f88ab9fbe916cb\result.json`), a real saved-profile restart passed 6/6 checks on the final build (`Logs\qualification-fonts\restart-final\result.json`), and the unchanged external `AgentScenario` passed 11/11 checks including the packaged-font gate (`Logs\qualification-fonts\agent-evidence\result.json`). Packaged Noto fonts (Latin/Hebrew/Symbols2) are bundled with OFL license, Advanced Text Generator is enabled, and live font readiness is derived from the actual bound configuration. The stream-robustness runtime gate at that stage was EditMode 249/249 and PlayMode 25/25 (`Logs\qualification-stream-robustness\final-editmode.xml`, `final-playmode.xml`); that gate is historical and was not re-run for the tooling-only external qualification. The current reviewed runtime gate is EditMode 249/249 and PlayMode 30/30 from the settings-parity pass (`docs/tasks/foundation-settings-parity-report.md`). External expected-fault qualification passed 18/18 and was re-verified on the post-gap build (`Logs\qualification-gaps\stream-robustness\result.json`). The foundation-gaps wave (`docs/FOUNDATION_GAPS_PLAN.md`, report `docs/tasks/foundation-gaps-report.md`) then closed the remaining automatable surfaces: finish delivery to a stalled non-reading controller (`FinishDeliveryScenario` 6/6), graceful-exit finalization + normal user profile path (`NormalProfileScenario` 6/6 + `FoundationQuitPlayTests`), and synthetic physical-input/UI parity (`FoundationInputPlayTests` + `FoundationScreensPlayTests`). Gate: EditMode 253/253, PlayMode 53/53, Core `dotnet test` 225/225. Still open: physical Android device/ADB validation, human Hebrew review, IL2CPP build qualification (module now installed), OS-level input coverage. `Tools/Verify.ps1` still exposes `CoreOffline`, `Core`, `CheckEnvironment`, `Setup`, `EditMode`, `PlayMode`, `Windows`, and `Android` modes; Unity commands are documented in `README.md`.

**Files:** `Packages/manifest.json`, generated package lock, `ProjectSettings/ProjectVersion.txt`, generated Unity project settings and `.meta` files, `Assets/FamilyRacing/Runtime/*`, `Assets/FamilyRacing/Editor/FoundationProjectSetup.cs`, `Assets/FamilyRacing/Editor/FoundationBuild.cs`, generated shell scene/settings, `Tools/Verify.ps1` (Unity setup/build/test modes to add), `README.md`, `IMPLEMENTATION_STATUS.md`.

**Interfaces:**

- `UnityProfileSerializer : IProfileSerializer` uses JsonUtility.
- `FoundationInput : MonoBehaviour` exposes `DriverInput Current`; keyboard bindings match GAME_PLAN. Transient commands are pressed-this-frame; drift/boost are held. Touch steering/drift/boost are held, gadget is a press, and release/cancel clears state. Clear on focus loss and disable.
- `FoundationShell : MonoBehaviour` owns four profile loads, explicit selection/creation, profile-specific settings, and the input sandbox. It does not modify coins or unlocks.
- `FoundationProjectSetup.Create()` creates the scene, PanelSettings and lightweight URP assets with 0.75 Android render scale, then adds the scene to build settings. It must be repeatable and never overwrite authored scenes/assets.
- `FoundationBuild.Windows()` and `FoundationBuild.Android()` build that enabled scene and fail explicitly on missing setup/modules or a failed BuildReport. Android uses OpenGL ES 3, IL2CPP ARM64, landscape, and a stable application identifier. Development APKs are not release-signed.
- `Tools/Verify.ps1` locates the exact pinned editor or accepts a matching path, dispatches setup, EditMode tests, or either platform build, records logs/results, and propagates failures. The core-test mode uses the checked-in harness.

- [x] Resolve package versions from exact-editor local templates/metadata or official docs after network approval; never invent a package lock or claim import without running it. — Done fully offline: URP 17.3.0, Input System 1.20.0, Newtonsoft.Json 3.2.2, Test Framework 1.6.0 resolved from the bundled editor packages/cache; genuine `packages-lock.json` generated.
- [x] Add adapter tests for JsonUtility round-trip, keyboard/touch lifecycle where executable, shell profile switching, failed settings saves, and repeatable setup; run RED where Unity is available. — Serializer round-trip, strict-parse and repeatable-setup EditMode tests plus loopback PlayMode tests (incl. isolated-store fixtures, disable-clears-input, finish/status/disconnect failure cases) done; settings-screen UI automation covered by `FoundationSettingsPlayTests`; physical keyboard lifecycle + remaining screens' UI parity now covered synthetically by `FoundationInputPlayTests` (InputTestFixture keyboard) and `FoundationScreensPlayTests` (pointer events) — `docs/tasks/foundation-gaps-report.md`. Still synthetic, not OS-level input.
- [x] Implement UI Toolkit shell with four large portrait/color profile buttons, English/Hebrew selector, settings, and clearly labeled foundation-only input sandbox. Use a warm island-inspired style, large touch targets, and a responsive scrollable layout. — `FoundationShell`: warm coastal palette, four animal cards, en/he selector, settings, sandbox; large touch targets, wrap layout, in-place indicator updates. NOTE: earlier completion was not visually verified — a scene `panelSettings` serialization bug rendered a blank preview; fixed upstream in `FoundationProjectSetup` (+`FoundationShell` rebind) with real-scene regression tests — `docs/tasks/blank-shell-fix.md`.
- [x] Persist settings immediately on explicit save, using a candidate copy; only replace displayed saved state after successful persistence. Blocked/recovered slots never auto-reset or create profiles. — Draft/saved separation in `FoundationService` verified by Core + scenario checks.
- [x] Use bundled dynamic Hebrew/Latin font assets and Advanced Text Generator when fonts are available/licensed; if missing, show an explicit warning and leave Hebrew rendering unqualified. Never reverse Hebrew strings manually. — Pinned Noto fonts bundled under `Assets/FamilyRacing/Fonts/` with OFL license + provenance (`docs/FOUNDATION_QUALIFICATION_PLAN.md` Task 1); generated dynamic FontAssets bound via `FoundationFontSetup`; ATG enabled in project settings and on the bound root; runtime readiness (`FoundationFonts.IsReady`) verifies the actual chain every refresh; a broken configuration still surfaces the explicit `fontWarning`. Hebrew strings remain logical-order, RTL via `languageDirection`.
- [x] Run available exact-editor compile, EditMode tests, and Windows/Android builds once as the final gate. Preserve logs and distinguish unavailable checks from failures. — Latest gate on the pinned editor fully green: EditMode 249/249 and PlayMode 22/22 (`Logs\qualification-fonts\final-editmode.xml`, `final-playmode.xml`); Windows Mono build `result=Succeeded` (`Logs\qualification-fonts\final-build-windows.log`) and passed the real restart scenario 6/6; Android built a 47 MB APK fully offline (`Verify.ps1 -Mode Android` run `20261011-134021-98837ad9964547dba7f88ab9fbe916cb`) with the bundled OFL verified byte-identical inside the APK. Earlier gate results (`Logs\qualification-continuation\`) stay historical.
- [x] Document exact commands, offline behavior, keyboard/touch bindings, profile safety, asset/font licensing, actual setup status, and remaining device checks. Keep Milestone 1 in progress until acceptance evidence exists. — `README.md`, `IMPLEMENTATION_STATUS.md`, `docs/tasks/foundation-runtime-report.md`.

### Task 3 Agent-Testability Acceptance

Implement alongside the foundation shell, not after it. Before product-code work, expand these requirements into a focused implementation plan with concrete interfaces, file ownership, and failing tests.

- [x] Establish versioned `GameCommand`/`CommandResult` discovery, validation, same-service dispatch, terminal outcomes, correlation, and duplicate-side-effect protection. — `FoundationService` + `Discover()`; duplicate/cache semantics verified by Core tests and the agent scenario.
- [x] Establish `GameStateSnapshot`/`GameStateDelta`/`IGameStateSink`, NDJSON live/file delivery, full reconstruction, ordering, attachment/resynchronization, and explicit capture failure on gaps/overflow/sink errors. — `StateStream` sequenced NDJSON to live socket + `state.ndjson`; fresh snapshot on attach at current global sequence; sticky invalidation on disconnect/queue overflow/trace limit; `capture-status.json` on start/failure/finish.
- [ ] Add token-protected loopback validation access for Windows and Android (ADB forwarding); leave ordinary family builds disabled by default. Use synthetic profiles and an isolated save directory. — Windows done (loopback + token + isolated store, opt-in flag only in editor/development; `--validation-profile-dir` lets validation runs share one synthetic store across fresh capture dirs and is ignored by ordinary launches); the Android APK is now produced by the enforced-offline build but no ADB/device validation has been attempted.
- [x] Expose profile creation/selection, settings/save/language, navigation, every sandbox press/hold/release control, camera and pause commands; stream UI, input, settings and persistence outcomes, including failed saves and blocked/recovered slots. — All ten command actions reachable over the wire; UI/input/settings/persistence/presentation streamed.
- [x] Run external-agent foundation scenarios proving command/UI parity, settings persistence across restart, duplicate-save safety, invalid-command rejection without mutation, held-input cleanup, paused-state observation, and slow/broken-sink failure. Record command/state traces, assertions, logs and relevant screenshots. — Lead scenario **passed all 9 checks** on the wave-3 build (`Logs\evidence-lead\result.json`, frozen) and 11/11 on the post-gap build (`Logs\qualification-gaps\agent-evidence\result.json`); `RestartScenario.py` 6/6; `StreamRobustnessScenario.py` 18/18 re-verified on the post-gap build (`Logs\qualification-gaps\stream-robustness\`). New: `FinishDeliveryScenario.py` 6/6 (finish to a stalled non-reading controller → terminal `streamError`, `captureClosed` probes, frozen prefix) and `NormalProfileScenario.py` 6/6 (real `persistentDataPath` store via `--validation-normal-profiles`, `taskkill` graceful exit → `processExit`, drafts never autopersist). Remaining `notVerified`: OS-level physical input/UI parity (synthetic only), Android device/ADB, human Hebrew review, IL2CPP build.
- [x] Register remaining tutorial/driving/progression/LAN/content features as unimplemented coverage gaps to be completed in their owning milestones; do not mark them passing from foundation-only tests. — `FeatureCoverage` reports all racing features `notImplemented` in every snapshot/discovery.

## Execution Ledger

- Initial inspection: only profile stubs and tests exist; no Unity project settings/packages/scene yet.
- Tasks 1–2 complete: profile persistence and shared input/bilingual catalog verified offline, 133/133 tests (see `docs/tasks/foundations-continuation-report.md`).
- Task 3 implementation delivered and verified (2026-10-11): EditMode 233/233, PlayMode 5/5, Windows Mono dev build passed the lead's external agent scenario (9/9 checks) — `docs/tasks/foundation-runtime-report.md`. Remaining: Android offline build outcome, restart/sink-failure scenarios, fonts, device checks, Windows IL2CPP module, NuGet restore (network pending).
- Qualification continuation (2026-10-11): repeatable offline Android build delivered and exercised (`Verify.ps1 -Mode Android` succeeded, offline `--offline` Gradle); development-only `--validation-profile-dir` override added and a real saved-profile restart passed 6/6 checks — `docs/tasks/foundation-qualification-report.md`. Remaining: packaged fonts (network approval pending), reconnect/sink-failure scenarios, Android device validation, Windows IL2CPP, NuGet restore.
- Font qualification phase (2026-10-11): packaged Noto fonts implemented per `docs/FOUNDATION_QUALIFICATION_PLAN.md` Task 1 — suites fully green (EditMode 249/249, PlayMode 22/22), `AgentScenario` 11/11 including the bilingual-font gate, `RestartScenario` 6/6 on the final build, fresh offline Android APK with bundled license, lead-accepted English/Hebrew window screenshots. Task 1 packaged-font implementation marked done pending human Hebrew review; Task 3 external-scenario step complete. Remaining: human Hebrew acceptance, Android device/ADB validation, reconnect/sink-failure scenarios, Windows IL2CPP, NuGet restore.
- External stream qualification (2026-10-11): `docs/FOUNDATION_EXTERNAL_STREAM_PLAN.md` executed — lead-authored `Tools/StreamRobustnessScenario.py` + `Tools/TestStreamRobustnessScenario.py` integrity suite (6 RED for the missing companion, 7 GREEN; three later corruption mutants RED3 then GREEN7 after validator hardening). Accepted run `external-run-2` passed 18/18 on the unchanged reviewed build (251-file fingerprint identical to the prep record); full Python suite 24/24 before the validator-only refinement. All three captures are expected-invalid — a pass records correct fault detection, not capture certification. Remaining: finish delivery on the original non-reading controller (PlayMode-only), graceful-exit autosave/normal user profile path, physical input + UI parity, Android device/ADB, human Hebrew review, Windows IL2CPP, NuGet restore.
- Settings UI/command parity (2026-10-11): `docs/FOUNDATION_SETTINGS_PARITY_PLAN.md` executed — `FoundationShell.Screens.cs` `UpdateSettings` merges the live `Snapshot().draftProfile` instead of the build-time captured state; new `FoundationSettingsPlayTests` went RED (4 expected assertion failures) then GREEN (5/5); final gate EditMode 249/249, PlayMode 30/30, Windows Mono build `result=Succeeded` (`Logs\qualification-settings-parity\`; `docs/tasks/foundation-settings-parity-report.md`). Reviewed slice: lead accepted implementation and evidence after scoped review; a test-only refinement (editor `UnityPlatform` gate + intermediate six-field assertion) re-verified 5/5 (`refinement-playmode.xml`), production unchanged. Remaining: physical keyboard/touch lifecycle, other screens' UI automation, Android device/ADB, human Hebrew review, normal-profile path, graceful-exit autosave, Windows IL2CPP, NuGet restore.
- Foundation gaps wave (2026-10-11): `docs/FOUNDATION_GAPS_PLAN.md` executed — parallel implementers + per-task scoped reviews + unified gate. Three production changes: `--validation-normal-profiles` flag, validation-only `Application.runInBackground` (gate-found defect: unfocused validation player froze `Pump`), `AddHoldButton` trickle-down registrations (test-found real defect: `Button.Clickable` swallowed primary pointer-downs — hold buttons unusable on real input). New evidence: EditMode 253/253, PlayMode 53/53, Core `dotnet test` 225/225 (NuGet approved), Windows Mono build OK, AgentScenario 11/11, Restart 6/6, StreamRobustness 18/18, FinishDelivery 6/6, NormalProfile 6/6 (`Logs\qualification-gaps\`, `Logs\qualification-exit\`; `docs/tasks/foundation-gaps-report.md`). Windows IL2CPP module installed (`CheckEnvironment` OK); no IL2CPP build yet. Remaining: Android device/ADB, human Hebrew review, IL2CPP build qualification, OS-level input coverage.
