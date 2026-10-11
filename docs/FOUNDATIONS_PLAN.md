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

**Status (2026-10-11, updated):** Task 3 implementation is largely verified — Unity 6.3 imports, Windows Mono development build succeeded, the lead's external agent scenario passed all 9 checks (`docs/tasks/foundation-runtime-report.md`), the offline Android path built a real APK (`Verify.ps1 -Mode Android`, `Logs\android-offline\20261011-123733-d853a75210284129ba058d708bd3606e\result.json`), and a real saved-profile restart passed 6/6 checks (`Logs\qualification-continuation\restart-final\result.json`, `docs/tasks/foundation-qualification-report.md`). The qualification final gate ran once: EditMode 241/245 and PlayMode 18/20 — all remaining failures are the pre-existing packaged-font fixtures that depend on deferred Task 1 font work. Windows IL2CPP is still missing (Mono used), NuGet restore still needs network approval, and Hebrew font assets remain absent with an explicit warning. `Tools/Verify.ps1` still exposes `CoreOffline`, `Core`, `CheckEnvironment`, `Setup`, `EditMode`, `PlayMode`, `Windows`, and `Android` modes; Unity commands are documented in `README.md`.

**Files:** `Packages/manifest.json`, generated package lock, `ProjectSettings/ProjectVersion.txt`, generated Unity project settings and `.meta` files, `Assets/FamilyRacing/Runtime/*`, `Assets/FamilyRacing/Editor/FoundationProjectSetup.cs`, `Assets/FamilyRacing/Editor/FoundationBuild.cs`, generated shell scene/settings, `Tools/Verify.ps1` (Unity setup/build/test modes to add), `README.md`, `IMPLEMENTATION_STATUS.md`.

**Interfaces:**

- `UnityProfileSerializer : IProfileSerializer` uses JsonUtility.
- `FoundationInput : MonoBehaviour` exposes `DriverInput Current`; keyboard bindings match GAME_PLAN. Transient commands are pressed-this-frame; drift/boost are held. Touch steering/drift/boost are held, gadget is a press, and release/cancel clears state. Clear on focus loss and disable.
- `FoundationShell : MonoBehaviour` owns four profile loads, explicit selection/creation, profile-specific settings, and the input sandbox. It does not modify coins or unlocks.
- `FoundationProjectSetup.Create()` creates the scene, PanelSettings and lightweight URP assets with 0.75 Android render scale, then adds the scene to build settings. It must be repeatable and never overwrite authored scenes/assets.
- `FoundationBuild.Windows()` and `FoundationBuild.Android()` build that enabled scene and fail explicitly on missing setup/modules or a failed BuildReport. Android uses OpenGL ES 3, IL2CPP ARM64, landscape, and a stable application identifier. Development APKs are not release-signed.
- `Tools/Verify.ps1` locates the exact pinned editor or accepts a matching path, dispatches setup, EditMode tests, or either platform build, records logs/results, and propagates failures. The core-test mode uses the checked-in harness.

- [x] Resolve package versions from exact-editor local templates/metadata or official docs after network approval; never invent a package lock or claim import without running it. — Done fully offline: URP 17.3.0, Input System 1.20.0, Newtonsoft.Json 3.2.2, Test Framework 1.6.0 resolved from the bundled editor packages/cache; genuine `packages-lock.json` generated.
- [ ] Add adapter tests for JsonUtility round-trip, keyboard/touch lifecycle where executable, shell profile switching, failed settings saves, and repeatable setup; run RED where Unity is available. — Partially: serializer round-trip, strict-parse and repeatable-setup EditMode tests plus loopback PlayMode tests (incl. isolated-store fixtures, disable-clears-input, finish/status/disconnect failure cases) done; physical input lifecycle and shell UI automation still untested.
- [x] Implement UI Toolkit shell with four large portrait/color profile buttons, English/Hebrew selector, settings, and clearly labeled foundation-only input sandbox. Use a warm island-inspired style, large touch targets, and a responsive scrollable layout. — `FoundationShell`: warm coastal palette, four animal cards, en/he selector, settings, sandbox; large touch targets, wrap layout, in-place indicator updates. NOTE: earlier completion was not visually verified — a scene `panelSettings` serialization bug rendered a blank preview; fixed upstream in `FoundationProjectSetup` (+`FoundationShell` rebind) with real-scene regression tests — `docs/tasks/blank-shell-fix.md`.
- [x] Persist settings immediately on explicit save, using a candidate copy; only replace displayed saved state after successful persistence. Blocked/recovered slots never auto-reset or create profiles. — Draft/saved separation in `FoundationService` verified by Core + scenario checks.
- [x] Use bundled dynamic Hebrew/Latin font assets and Advanced Text Generator when fonts are available/licensed; if missing, show an explicit warning and leave Hebrew rendering unqualified. Never reverse Hebrew strings manually. — No font assets bundled this iteration; explicit `fontWarning`/`warning.missingFont` surfaced in UI and presentation state; logical-order catalog unchanged.
- [x] Run available exact-editor compile, EditMode tests, and Windows/Android builds once as the final gate. Preserve logs and distinguish unavailable checks from failures. — Qualification final gate ran 2026-10-11 on the pinned editor: EditMode 241/245 and PlayMode 18/20 — the only failures are the pre-existing packaged-font fixtures (`FoundationFontSetupTests` ×4, `FoundationFontPlayTests` ×2) awaiting Task 1 font downloads; no unrelated failures (`Logs\qualification-continuation\final-editmode.xml`, `final-playmode.xml`). Windows Mono build Succeeded (`Logs\qualification-continuation\final-build-windows.log`) and passed the real restart scenario; Android built a 46 MB APK fully offline (`Verify.ps1 -Mode Android` run `20261011-123733-d853a75210284129ba058d708bd3606e`).
- [x] Document exact commands, offline behavior, keyboard/touch bindings, profile safety, asset/font licensing, actual setup status, and remaining device checks. Keep Milestone 1 in progress until acceptance evidence exists. — `README.md`, `IMPLEMENTATION_STATUS.md`, `docs/tasks/foundation-runtime-report.md`.

### Task 3 Agent-Testability Acceptance

Implement alongside the foundation shell, not after it. Before product-code work, expand these requirements into a focused implementation plan with concrete interfaces, file ownership, and failing tests.

- [x] Establish versioned `GameCommand`/`CommandResult` discovery, validation, same-service dispatch, terminal outcomes, correlation, and duplicate-side-effect protection. — `FoundationService` + `Discover()`; duplicate/cache semantics verified by Core tests and the agent scenario.
- [x] Establish `GameStateSnapshot`/`GameStateDelta`/`IGameStateSink`, NDJSON live/file delivery, full reconstruction, ordering, attachment/resynchronization, and explicit capture failure on gaps/overflow/sink errors. — `StateStream` sequenced NDJSON to live socket + `state.ndjson`; fresh snapshot on attach at current global sequence; sticky invalidation on disconnect/queue overflow/trace limit; `capture-status.json` on start/failure/finish.
- [ ] Add token-protected loopback validation access for Windows and Android (ADB forwarding); leave ordinary family builds disabled by default. Use synthetic profiles and an isolated save directory. — Windows done (loopback + token + isolated store, opt-in flag only in editor/development; `--validation-profile-dir` lets validation runs share one synthetic store across fresh capture dirs and is ignored by ordinary launches); the Android APK is now produced by the enforced-offline build but no ADB/device validation has been attempted.
- [x] Expose profile creation/selection, settings/save/language, navigation, every sandbox press/hold/release control, camera and pause commands; stream UI, input, settings and persistence outcomes, including failed saves and blocked/recovered slots. — All ten command actions reachable over the wire; UI/input/settings/persistence/presentation streamed.
- [ ] Run external-agent foundation scenarios proving command/UI parity, settings persistence across restart, duplicate-save safety, invalid-command rejection without mutation, held-input cleanup, paused-state observation, and slow/broken-sink failure. Record command/state traces, assertions, logs and relevant screenshots. — Lead scenario **passed all 9 checks** on the wave-3 build (`Logs\evidence-lead\result.json`, frozen; preview PID 52200 at `Logs\run-validation-lead`); restart persistence of explicitly saved synthetic profiles now evidenced by `Tools\RestartScenario.py` 6/6 checks (`Logs\qualification-continuation\restart-final\result.json`). Still unverified per `notVerified`: reconnect, slow/broken-sink failure injection, graceful-exit autosave and the normal user profile path, physical input + UI parity.
- [x] Register remaining tutorial/driving/progression/LAN/content features as unimplemented coverage gaps to be completed in their owning milestones; do not mark them passing from foundation-only tests. — `FeatureCoverage` reports all racing features `notImplemented` in every snapshot/discovery.

## Execution Ledger

- Initial inspection: only profile stubs and tests exist; no Unity project settings/packages/scene yet.
- Tasks 1–2 complete: profile persistence and shared input/bilingual catalog verified offline, 133/133 tests (see `docs/tasks/foundations-continuation-report.md`).
- Task 3 implementation delivered and verified (2026-10-11): EditMode 233/233, PlayMode 5/5, Windows Mono dev build passed the lead's external agent scenario (9/9 checks) — `docs/tasks/foundation-runtime-report.md`. Remaining: Android offline build outcome, restart/sink-failure scenarios, fonts, device checks, Windows IL2CPP module, NuGet restore (network pending).
- Qualification continuation (2026-10-11): repeatable offline Android build delivered and exercised (`Verify.ps1 -Mode Android` succeeded, offline `--offline` Gradle); development-only `--validation-profile-dir` override added and a real saved-profile restart passed 6/6 checks — `docs/tasks/foundation-qualification-report.md`. Remaining: packaged fonts (network approval pending), reconnect/sink-failure scenarios, Android device validation, Windows IL2CPP, NuGet restore.
