# Foundation Gaps Implementation Plan

> **For agentic workers:** Use subagent-driven-development — one fresh implementer per task, scoped task review after each, then a unified gate. Track evidence in `IMPLEMENTATION_STATUS.md`.

**Goal:** Close the remaining automatable `notVerified` gaps of Milestone 1 that need no hardware, human reviewer, or network: graceful-exit finalization evidence, the normal user profile path, finish delivery to a stalled controller, and physical-input/UI parity coverage.

**Spec:** `GAME_PLAN.md` §3 (agent-testability contract), `docs/FOUNDATIONS_PLAN.md` Task 3 checkboxes, `docs/FEATURE_COVERAGE.md` "Known QA gaps". Not in scope (blocked): Android ADB/device run, human Hebrew review, Windows IL2CPP module, NuGet restore — all need hardware/people/network.

## Global Constraints

- Continue in the project root on `feature/family-racing`; preserve all untracked files. **Do not commit or push.**
- No network access, downloads, or installs requiring elevation without owner approval.
- Keep the wire/status schema backward compatible: `capture-status.json` fields stay `captureValid`, `captureComplete`, `captureError`, `recordsEmitted`; validation flags remain editor/development-only and ordinary launches ignore them.
- **Unity serialization:** only ONE process may run Unity at a time (Library lock). Task 1 is the sole Unity runner in this wave; Tasks 2–3 must not invoke `Tools/Verify.ps1` or any Unity executable.
- Evidence language discipline: PlayMode callbacks are synthetic, not OS input; `--validation-*` runs are development builds; a scenario pass on an injected fault means correct detection, not capture certification.
- Do not claim gaps closed without recorded artifacts under `Logs/`.

## Agreed cross-task contracts

- **`--validation-normal-profiles`**: valueless flag, only meaningful together with `--validation-dir` (and only in `UNITY_EDITOR || DEVELOPMENT_BUILD`). Selects `Path.Combine(Application.persistentDataPath, "Profiles")` — the ordinary user store — instead of the synthetic validation store. Mutually exclusive with `--validation-profile-dir` (both present ⇒ `ArgumentException`). Without `--validation-dir` it is ignored entirely.
- **Graceful exit (Windows player):** `taskkill /pid <pid>` (no `/F`) posts WM_CLOSE → `OnApplicationQuit` → `ValidationBridge.Dispose` → `StateStream.Dispose` writes terminal `capture-status.json`: `captureValid:false, captureComplete:false, captureError:"processExit"` when no valid finish was stamped; a completed finish stays `true/true/""`. Unsaved `settings.update` drafts are **not** auto-persisted on exit (designed draft/save separation); explicitly saved profiles must survive intact.

## Task 1: Graceful-exit finalization + normal-profile selector

**Files:** `Assets/FamilyRacing/Runtime/FoundationApp.cs`, `Assets/FamilyRacing/Tests/EditModeAdapter/ValidationProfileDirectoryTests.cs`, `Assets/FamilyRacing/Tests/PlayMode/FoundationQuitPlayTests.cs` (new + `.meta`), `Logs/qualification-exit/` artifacts.

- Extend `SelectProfilesDirectory` with the `--validation-normal-profiles` contract above; EditMode tests cover flag parsing, exclusivity, and ordinary-launch ignoring.
- New PlayMode fixture `FoundationQuitPlayTests` exercising `ValidationBridge`/`StateStream` disposal as `OnApplicationQuit` performs it: unfinished capture ⇒ terminal `processExit` status; valid finish then dispose ⇒ status stays `true/true`; controller attached mid-dispose ⇒ still terminal, no hang beyond bounded joins.
- Audit `FoundationApp.OnApplicationQuit` ordering (input.clear dispatch vs bridge dispose) — fix only if a real defect is found; flag findings in the report.
- RED→GREEN where a change is behavior-changing (flag tests); regression fixtures that lock in already-correct dispose semantics may be GREEN-on-arrival — say so honestly.
- Sole Unity runner this wave. Final evidence: focused PlayMode + EditMode XML under `Logs/qualification-exit/`.

## Task 2: Physical-input and screens UI-parity PlayMode tests (write-only)

**Files:** `Assets/FamilyRacing/Tests/PlayMode/FoundationInputPlayTests.cs` (new + `.meta`), `Assets/FamilyRacing/Tests/PlayMode/FoundationScreensPlayTests.cs` (new + `.meta`), `Assets/FamilyRacing/Tests/PlayMode/FamilyRacing.Tests.PlayMode.asmdef` (add `Unity.InputSystem.TestFramework` ref only — sole owner).

- `FoundationInputPlayTests` (derives `InputTestFixture`): real Keyboard device simulation driving `FoundationInput.Pump` → `app.Dispatch` → service state. Arrows/A-D steering incl. opposing→neutral, Space drift + Shift boost held, E/R transient pulses, C→`camera.toggle`, Esc→`pause.toggle`; off-sandbox suppression; agent-ownership suppression; `OnDisable`/focus-loss clearing.
- `FoundationScreensPlayTests`: pointer down/up parity on remaining screens — profile cards (create/select), profiles-screen language buttons, navigation/back buttons, sandbox touch buttons (down→held, up→release, cancel→release) producing the same service state as commands.
- No Unity execution; the unified gate runs the suite. Mirror `FoundationSettingsPlayTests` fixture patterns (inactive host, reflection injection, synthetic store, `Press` helper).

## Task 3: External scenarios — finish-to-stalled controller, normal profile path, graceful exit

**Files:** `Tools/FinishDeliveryScenario.py` + `Tools/TestFinishDeliveryScenario.py` (new), `Tools/NormalProfileScenario.py` + `Tools/TestNormalProfileScenario.py` (new). Python only — no Unity, no player runs (wave-2 gate executes them).

- `FinishDeliveryScenario`: stalled non-reading controller (small `SO_RCVBUF`, reads once then stalls) sends `finish`; assert bounded writer failure → terminal invalid status (`streamError`), game stays responsive (new controller rejected `captureClosed`), trace prefix frozen, no false-complete. Model on `StreamRobustnessScenario.py` conventions + `Client`/`FaultClient` reuse from `AgentScenario`/`RestartScenario` imports.
- `NormalProfileScenario`: launches the dev player with `--validation-dir` + `--validation-normal-profiles`, backs up/restores any pre-existing real `persistentDataPath\Profiles` tree, then create→settings.save→graceful exit via `taskkill` (no `/F`)→relaunch→select: asserts terminal `processExit` status, saved profile survives, unsaved draft fields not persisted, real-store files valid.
- Integrity suites `Test*.py` mirror the existing `TestStreamRobustnessScenario.py`/`TestRestartScenario.py` patterns and must pass.

## Wave 2 (controller, after all tasks)

- Unified gate: `-Mode EditMode`, `-Mode PlayMode`, `-Mode Windows`; run `AgentScenario`, `RestartScenario`, `StreamRobustnessScenario`, `FinishDeliveryScenario`, `NormalProfileScenario` on the built player; fix loop via resumed implementers.
- Update `docs/FEATURE_COVERAGE.md`, `IMPLEMENTATION_STATUS.md`, `docs/FOUNDATIONS_PLAN.md` ledger, new `docs/tasks/foundation-gaps-report.md`; scoped reviews per task + final whole-diff review.

## Execution Ledger

- 2026-10-11: plan authored; Tasks 1–3 dispatched to parallel implementers (Task 1 sole Unity runner, Task 2 write-only, Task 3 Python-only).
