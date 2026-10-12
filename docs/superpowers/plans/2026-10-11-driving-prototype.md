# Milestone 2: Driving Prototype Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking. This document is a proposed implementation plan, not evidence that Milestone 1 or 2 is complete.

**Goal:** A child can complete a short tutorial and drive an introductory graybox course in either a rally car or an automatically balancing motorcycle, using either camera, with real drift, boost, jumps, checkpoints, and recovery.

**Architecture:** Extend the existing shared command/application path. Keep simulation state, tuning, checkpoint rules, and tutorial rules in Unity-free Core; Unity adapters own collision queries, content assets, camera transforms, audio, and UI. One fixed-step driving session feeds both presentation and the validation stream, with separate observation and simulation clocks.

**Tech Stack:** Unity 6000.3.26f1, C# 9, .NET Standard 2.1-compatible Core, URP 17.3.0, Input System 1.20.0, UI Toolkit, Unity Test Framework 1.6.0, NUnit/.NET 10 harness, Python external scenarios.

**Spec:** [GAME_PLAN.md](../../../GAME_PLAN.md), sections 2–4, especially Milestone 2 and the command/state contract. Existing behavior: [COMMANDS.md](../../COMMANDS.md), [FEATURE_COVERAGE.md](../../FEATURE_COVERAGE.md). Implementation decisions below are proposed defaults within that approved scope; they are not additional approved product requirements.

**Assessment date / baseline:** 2026-10-11; branch `feature/family-racing`, commit `15934d6`; tracked working tree was clean before this planning pass. No product code or build configuration changed in this pass.

## Current state and evidence

The project is an extensively tested foundation shell. It has four local profiles, guarded atomic persistence, English/Hebrew UI and bundled fonts, settings, keyboard/touch input composition, a control sandbox, and a token-protected local validation bridge. It has no vehicle motor, track, race, real driving cameras, or tutorial.

| Area | Current assessment | Evidence and limits |
|---|---|---|
| Core | Ready to extend | **Fresh:** `Tools/Verify.ps1 -Mode Core` passed 225/225. One existing CS0649 test-fixture warning; no failures. |
| Environment | Required installed components found | **Fresh:** `Tools/Verify.ps1 -Mode CheckEnvironment` passed, including Windows IL2CPP and Android SDK/NDK/JDK. This does not run Unity or prove a device launch. |
| Scenario/tooling tests | Healthy baseline | **Fresh:** `python -B -m unittest discover -s Tools -p 'Test*.py'`: 52 passed. `Tools/Test-VerifyTooling.ps1`: 12 passed. |
| Unity integration | Latest recorded suites green | **Inspected, not rerun:** `Logs/verify-editmode-results.xml`: 253/253; `Logs/verify-playmode-results.xml`: 53/53. |
| Windows player | Latest reviewed Mono development player exists | **Inspected, not rebuilt:** `Logs/verify-unity-windows.log` records success, zero errors/warnings. All 251 current build-file hashes match the latest restart/stream/finish/normal-profile evidence. |
| External scenarios | Strong foundation evidence | **Inspected, not rerun:** `Logs/qualification-gaps/`: Agent 11/11, Restart 6/6, StreamRobustness 18/18, FinishDelivery 6/6, NormalProfile 6/6. Fault-test passes mean faults were detected; those captures remain invalid. |
| Android | APK built, not device-qualified, behind current Windows source | `Logs/android-offline/20261011-134021-98837ad9964547dba7f88ab9fbe916cb/result.json`; current APK hash matches, but this build predates later stream/settings/input fixes. Rebuild before qualification. |
| Human/device acceptance | Open | No Android on-device launch, ADB validation, physical touch acceptance, human Hebrew review, or gameplay/performance acceptance is claimed. |
| Milestones | M1 in progress; M2 not started | Later gameplay remains explicitly `notImplemented` in `Core/Foundation/FoundationState.cs`. |

### Documentation discrepancies

`IMPLEMENTATION_STATUS.md`'s latest foundation-gaps entry and the actual artifacts supersede older entries. README still says NuGet approval is blocked and the Windows IL2CPP module is missing; both are resolved. It also lists normal-profile persistence, graceful exit, and stalled finish-delivery gaps that now have evidence. Earlier 249/30 Unity counts are historical; the latest recorded counts are 253/53.

Installed Windows IL2CPP support is distinct from an IL2CPP build. `FoundationProjectSetup.ApplyPlayerSettings` explicitly selects Windows Mono, and `Verify.ps1` has no Windows IL2CPP mode. Keep that as a separate build-hardening item; the M1 acceptance table does not require a particular Windows scripting backend.

### Why this milestone next

Choose the **driving prototype** already specified in the roadmap. It tests the largest remaining product risk: whether the game is understandable and enjoyable to drive. More general foundation hardening would not answer that; networking would introduce synchronization work before a motor and race rules exist.

The immediate next execution step is the bounded M1 closeout in Task 0. The approved roadmap requires a playable build and review before expanding milestone scope. Planning M2 can finish now; do not silently mark M1 complete or bypass that review when implementation begins.

## Global constraints

- Preserve Unity 6000.3.26f1 and committed package versions; this milestone needs no new package or paid asset.
- Core remains free of UnityEngine dependencies and .NET 10-only production APIs. New Core folders must also compile through `Tools/CoreTests/FamilyRacing.CoreTests.csproj`.
- Automatic acceleration; no automatic steering through ordinary corners. Motorcycle balance is automatic.
- One graybox course shared by `car-01` and `motorcycle-01`; one local human racer. This is a driving trial, not Quick Race against AI.
- English and logical-order Hebrew, large semantic touch controls, offline bundled content. Timers remain LTR and steering direction keeps its physical meaning.
- Preserve four profile slots, save schema 1, existing identity protection, safe writes, and explicit draft/save behavior. Tutorial progress is session-local and repeatable in M2.
- Every implemented feature uses the same command/application path for UI, physical input, and agents, with observable state/events and recorded feature evidence.
- Validation remains opt-in in editor/development builds, loopback/token protected; ordinary family builds expose no validation endpoint.
- Keep incomplete captures invalid. Never discard required history or silently drop simulation steps to make validation pass.
- Tablet target: 30 fps, steady-state p95 frame time at or below 40 ms, no recurring stalls above 100 ms; initial process-memory budget 800 MB. PC target: 60 fps at 1080p. These remain targets until measured.
- No campaign, economy, upgrades, final animal animation, AI opponents, pickups/gadgets, LAN, or production course art in M2. Gadget onboarding is explicitly deferred with gadget gameplay; do not advertise the full-release tutorial as complete.

## Proposed playable scope

Start at profile selection, enter **Driving Practice**, choose car or motorcycle, optionally run the tutorial, then complete a two-lap trial. Finish shows elapsed time, vehicle, and retry/vehicle-selection actions. It grants no currency, stars, unlocks, or records.

The course uses readable primitives: broad turns, a drift zone, one landmark ramp and forgiving landing, barriers, recovery zones, and **two optional shortcut branches**. Both branches rejoin before the next mandatory checkpoint; neither bypasses required progress. Use a one-lap introductory event for tutorial graduation. Laps, speeds, steering, drift thresholds, charge rates, and recovery thresholds are editable content, not literals distributed through UI/code.

Both vehicles share the same motor/race interfaces. Car tuning emphasizes weight and grip; motorcycle tuning emphasizes quicker steering and visual lean. Prove parameter differences automatically and evaluate feel with a person. Avoid free-rigidbody motorcycle balancing and a second physics system.

## Architecture and contracts

| Boundary | Proposed responsibility / interface |
|---|---|
| `Core/Driving/MotorState.cs` | Serializable position, rotation/heading, velocity, grounded/airborne state, drift state, boost charge/activity, recovery state, last contact and simulation tick. Use Unity-free numeric values. |
| `Core/Driving/ArcadeMotor.cs` | `MotorStepResult Step(MotorState previous, DriverInput input, VehicleTuning tuning, IMotorWorld world, float deltaSeconds)`. Result contains the new state and ordered transient events. |
| `Core/Driving/IMotorWorld.cs` | Unity-free ground-probe and swept-collision request/results, implemented by a fake world in Core tests and `Runtime/Driving/UnityMotorWorld.cs` against the course. Define concrete query types here before motor implementation. |
| `Core/Racing/DrivingSession.cs` | Own selected vehicle/event, phase, simulation tick, elapsed race ticks, checkpoint/lap progress, finish and recovery state. `DrivingStepResult Step(DriverInput input, IMotorWorld world)` advances exactly one 1/30-second step; `DrivingSessionState Snapshot()` returns an independent copy. |
| `Core/Racing/CheckpointProgress.cs` | `CheckpointResult Advance(CourseDefinition course, CheckpointState state, MotorPose previous, MotorPose current)`: ordered directional crossings using the swept segment, never raw trigger overlap alone. |
| `Runtime/Content/*.cs` | ScriptableObject assets convert authored vehicle/course/event settings into validated Core definitions. Stable IDs and content version travel with evidence. |
| Existing `FoundationService` / `FoundationApp` | One command identity cache and command validation route. Delegate driving behavior to the session; do not add a parallel agent-only dispatcher. |
| `Runtime/Driving/DrivingWorld.cs` | Bind the graybox environment, instantiate/display the selected vehicle, implement collision queries, and apply committed motor state to visuals. |
| `Runtime/Presentation/DrivingCamera.cs` | Consume motor and profile state; update actual chase/overhead cameras without modifying physics. |

Retain the bootstrap scene and service for the run. Reference a graybox world prefab from the bootstrap and activate it for practice; avoid scene reloads that reset command identities or profile drafts. Load and validate its definitions before reporting `race.start` available. Scene/session changes still emit a full snapshot.

### Timing and input contract

The existing `FoundationApp.Update` uses unscaled time for observation. `FoundationService.AdvanceTick()` expires leases and clears recovery/gadget pulses; it explicitly is not a race clock. Integrating a motor after that call would lose one-shot input.

Each 30 Hz boundary must: sample held input and consume pending one-shot actions; advance driving only when its phase allows it and it is not paused; commit motor/rules/events; expire held leases through the observation tick; publish the resulting boundary. Commands publish immediate state/outcomes between boundaries. Observation ticks and stream sequence continue during pause; simulation tick and elapsed race time freeze. Countdown advances simulation state but not race elapsed time.

Latch accepted recovery actions with their command identities until a simulation boundary consumes them; a later neutral input sample must not erase an earlier press. Use a bounded pending-action queue, reject overflow before mutating input, and consume at most one recovery request per step. Duplicate commands do not enqueue twice. Pending actions and their consumed/cancelled events are observable; input-command completion is distinct from a later recovery event. Camera and pause remain immediate application actions.

Pausing, leaving practice, focus loss, disconnect, or disabling controls neutralizes held input and cancels queued actions. After resume/focus regain, physical controls must release and rearm before submitting another held action; an agent must send a fresh command. A key remaining held across pause must not be reasserted by the existing 500 ms refresh. Keep unsimulated accumulator time when limiting work per frame; expose backlog/overrun diagnostics and fail qualification if sustained overload occurs.

### Command and observation contract

Extend discovery with these semantic actions and phase-specific availability reasons:

- `race.start(vehicleId, eventId)`; initial IDs `car-01`/`motorcycle-01`, `practice-02` (two laps) and `intro-01` (one lap).
- `race.restart()` and `race.exit()`; retry creates a new race identity inside the same validation run. Duplicate delivery must not restart twice.
- `tutorial.start(vehicleId)`, `tutorial.replay()`, `tutorial.exit()`. Lessons advance from genuine driving events; there is no normal `tutorial.complete` or checkpoint-teleport command.
- Reuse `input.set`, `input.clear`, `camera.toggle`, and `pause.toggle`; update sandbox-only guards to permit the appropriate driving/tutorial phases. Unimplemented gadget actions return an explicit unavailable reason in driving.
- Provide validation-only screenshot capture through the bridge with a correlated completion outcome. Store artifacts inside the current run directory; do not accept arbitrary output paths.

Keep command protocol version 1 if envelopes/semantics remain compatible. Introduce explicit `stateSchemaVersion: 2` for expanded state, retain the meaning of top-level observation `tick`, and add `driving.simulationTick`. Update consumers to negotiate supported state schemas and reject unsupported ones. Version content separately from the current `contentVersion = buildVersion` shortcut.

Driving observations include race/participant/content identity, selected definitions, phase/pause/countdown, elapsed ticks, motor/input, boost/drift/air/recovery, ordered checkpoints/laps/finish, tutorial/cue state, actual camera mode/pose/target, semantic HUD controls, and transient events with IDs/ticks/command correlation. Do not stream only transforms or HUD text.

At first integration retain full `state` records for compatibility. Task 6 introduces ordered deltas plus a state boundary after every simulated tick, even unchanged ticks, and immediately after non-simulation changes. A new observer/session/resynchronization gets a complete snapshot. An independent reconstructor must reproduce every boundary; old full-state fixtures remain supported by the tooling. Schema-2 consumers must not silently interpret a delta as a full state.

## Review focus

1. Recovery or repeated gate overlap must not award checkpoint/lap/finish or clean-landing charge; Task 2 and Task 3 tests own this.
2. A recovery pulse followed by multiple catch-up ticks must execute once; pause must freeze driving while input leases still expire; Task 1 tests own this.
3. A vehicle can cross several gates at boost speed or backwards; progress must respect ordered directional crossings without tunneling; Task 2 tests own this.
4. Changing language/camera or cancelling a touch during a held turn must not stick controls, obscure the world, or claim an unsaved camera preference was persisted; Task 4 tests own this.
5. Growing motor/event state can exhaust stream queues, trace storage, or command identities during repeated play; Task 6/7 tests and measurements own this. Retain fail-closed behavior instead of evicting deduplication history.

## Task 0: Close the remaining M1 acceptance gates

**Files:** update `README.md`, `IMPLEMENTATION_STATUS.md`, `docs/FEATURE_COVERAGE.md`; create `docs/tasks/foundation-device-qualification-report.md`. Add Android launch/ADB tooling only if needed for reproducible qualification, with its launch tests under `Tools/`.

**Consumes:** latest reviewed foundation tree and existing build/scenario tools. **Produces:** a reviewed M1 acceptance record or precise outstanding device/human blockers.

- [ ] Reconcile stale README/current-status summaries against the evidence table above; preserve historical evidence as historical.
- [ ] Rebuild Android from this tree with `Tools/Verify.ps1 -Mode Android`; record source revision, dirty-tree state, tool versions, build result and artifact hash in a fresh run directory.
- [ ] Install on the A7 Lite, launch offline, exercise English/Hebrew profiles/settings/touch, explicitly save and restart. Capture device/OS/build identity and evidence; do not erase family data to simulate a fresh install.
- [ ] Qualify the actual Android validation launch/connection path: protected loopback, ADB forwarding, isolated profiles, successful command outcomes and complete capture. Windows command-line validation flags alone do not prove Android startup works; add an editor/development-only Android launch adapter if required, then rebuild and test it.
- [ ] Record physical Windows keyboard and tablet touch checks plus human Hebrew reading/layout acceptance. Keep pronunciation acceptance separate until tutorial audio exists.
- [ ] Review M1 acceptance against `GAME_PLAN.md`. Hardware/human availability can leave the gate open; that does not prevent reviewing this M2 plan. Any implementation overlap with an open M1 gate needs an explicit recorded scope decision.

**Done:** latest-source Android and Windows foundation acceptance is evidenced and reviewed. An installed IL2CPP module, stale APK, or automated font check is insufficient evidence.

## Task 1: Establish one real, observable driving loop

**Create:** `Core/Driving/MotorState.cs`, `VehicleTuning.cs`, `ArcadeMotor.cs`, `IMotorWorld.cs`, `Core/Racing/DrivingSession.cs`, `DrivingSessionState.cs`, `DrivingEvent.cs`, `CourseDefinition.cs`, `EventDefinition.cs`, `Runtime/Driving/UnityMotorWorld.cs`, `DrivingWorld.cs`, `Runtime/Content/VehicleDefinitionAsset.cs`, `TrackDefinitionAsset.cs`, `EventDefinitionAsset.cs`, `Runtime/FoundationShell.Driving.cs`, `Editor/DrivingPrototypeSetup.cs`. Paths under `Assets/FamilyRacing/` unless stated otherwise.

**Modify:** `Core/Foundation/FoundationService.cs`, `FoundationState.cs`, `Runtime/FoundationApp.cs`, `FoundationInput.cs`, `FoundationShell.cs`, `Runtime/Automation/StateStream.cs`, `ValidationBridge.cs`, `Core/Localization/ShellText.cs`, `Editor/FoundationProjectSetup.cs`, `Tools/CoreTests/FamilyRacing.CoreTests.csproj`, `Tools/AgentScenario.py` and shared scenario protocol helpers/tests, `docs/COMMANDS.md`. Generate graybox prefab/definitions under `Assets/FamilyRacing/Generated/Driving/` with Unity `.meta` files.

**Tests:** create `Tests/EditMode/DrivingClockTests.cs`, `MotorTests.cs`, `DrivingCommandTests.cs`, `Tests/PlayMode/DrivingWorldPlayTests.cs`.

**Interfaces:** establish the architecture contracts above and the start/restart/exit command schemas. Define `MotorPose` and `MotorStepResult` alongside `MotorState`; `DrivingStepResult` alongside `DrivingSessionState`. Core session returns immutable/copy-safe state and events; only the Unity world implementation references Physics/Transforms. Course/event data types exist now; Task 2 adds their progress rules.

- [ ] Write tests proving auto-acceleration moves a car with neutral steering; steering changes heading; wall sweeps bound penetration; NaN/invalid tuning rejects without state changes; snapshots do not alias live state.
- [ ] Add clock/input tests: recovery→neutral before a boundary preserves one action; multiple commands retain order/correlation; a pulse reaches the session once across three catch-up steps; queue overflow rejects without partial mutation. Actual recovery rules arrive in Task 2. Paused driving state/time freeze while observation advances and leases expire; lifecycle transitions cancel queued actions; a physically held key cannot rearm until released; duplicate start/restart preserves its original race identity/outcome.
- [ ] Run `Tools/Verify.ps1 -Mode Core` and capture expected failures. Change `FoundationTests.UnknownActionRejected` to a genuinely unknown action when `race.start` becomes supported; preserve the unknown-action assertion.
- [ ] Implement the shared fixed-step loop and a car on the graybox course. Keep kinematic movement, ground probes, swept obstacles and controlled airborne states behind the Core motor/world interface. Update the .NET project to include new Core source directories.
- [ ] Wire race entry/exit, existing controls, deep-copy driving state and ordered events through the current service/bridge. Add minimal localized practice entry/exit controls and a transparent driving screen with fixed overview framing so the first slice is visibly playable. Task 4 expands this into the final prototype HUD and switchable cameras. Keep the existing sandbox functional; persist the command cache for the whole run.
- [ ] Add state-schema/content-version metadata, supported-schema validation and schema-2 full-state support to the bridge and scenario readers now. Test old full-state fixtures, new driving-state fixtures and explicit rejection of unsupported schemas before recording the first driving scenario. Task 6 adds deltas and independent reconstruction.
- [ ] Run Core plus focused Unity PlayMode movement/command-parity checks, then record a real-player command trace that starts practice and changes actual position/heading.

**Done:** a car moves and turns on the real course through both ordinary input and agent commands, with reconstructable observed motor state. Indicator LEDs alone cannot pass.

## Task 2: Make laps, finish, and recovery trustworthy

**Create:** `Core/Racing/CheckpointProgress.cs`, `RecoveryRules.cs`, `Tests/EditMode/CheckpointTests.cs`, `RecoveryTests.cs`, `Tests/PlayMode/CourseProgressPlayTests.cs`. **Modify:** course/event definitions, session/world/content setup from Task 1. Define `CheckpointState` and `CheckpointResult` in `CheckpointProgress.cs` before integrating its interface.

**Interfaces:** ordered gates have stable IDs, forward direction, bounded gate geometry and a safe recovery pose. Session owns `expectedCheckpoint`, `lap`, `lastValidRecoveryPose`, `raceId`, phase and elapsed ticks; checkpoint results produce ordered events consumed by tutorial and observation.

- [ ] Add failing tests for reverse crossing, gate re-entry, missing gate, start-line crossing before all gates, two valid gates crossed in one fast step, both legal shortcut routes, and duplicate finish.
- [ ] Add recovery tests: manual, off-course, overturned/invalid pose, and stuck recovery return to the last accepted safe pose; progress never increases because of respawn; velocity/held actions reset; race clock continues; repeated recovery cannot create finish or charge.
- [ ] Implement previous-to-current swept gate intersections in travel order, exactly one lap increment per valid gate cycle, one terminal finish, and data-driven recovery thresholds. Recovery movement is flagged and excluded from progress/landing evaluation.
- [ ] Validate authoring: unique IDs, positive lap count, usable start/recovery poses, finite values, valid gate geometry, and all required asset references. Reject malformed definitions before `race.start` becomes available.
- [ ] Run Core and PlayMode progress/recovery checks. Complete a two-lap car trial through real controls; record finish/retry/exit, rejection of illegal progress and unchanged profile currency/unlocks.

**Done:** completing the route works; skipping or reversing gates and respawning cannot manufacture completion. Retry gives a fresh race identity without replaying side effects.

## Task 3: Add the second vehicle and the driving mechanics

**Modify:** motor/tuning/state/events and graybox assets from Task 1; add `Core/Driving/DriftBoostRules.cs`, `Runtime/Driving/VehiclePresentation.cs`, `Tests/EditMode/DriftBoostTests.cs`, `Tests/PlayMode/VehicleClassPlayTests.cs`.

**Interfaces:** charge is normalized to `[0,1]`; tuning defines drift eligibility/charge rate, boost drain/speed modifier, and clean-landing credit. Events include drift start/end, boost start/end, takeoff/landing, impact and recovery; an airborne episode receives at most one landing credit.

- [ ] Write failing tests for drift eligibility, charge cap/empty boost, bounded boost duration, hold/release behavior, clean versus invalid landing, repeated ground-contact jitter, and recovery during drift/boost/air. Finite motor state and bounded collision response are invariants.
- [ ] Implement drift and boost using shared input, plus jump/landing transitions from real ground probes. Bound barrier impacts; retain an explicit future seam for racer nudges without adding AI racers.
- [ ] Add motorcycle tuning and automatic balance/visual lean with the same motor interface. Author visibly distinct car/bike graybox silhouettes, not two identical cubes. Keep lean cosmetic relative to the stable collision body.
- [ ] Run Core/PlayMode tests and agent-driven laps for both vehicles, including ramp, drift zone, both shortcut routes and recovery. Compare response metrics for the two tuning sets, then record an owner feel review separately from automated results.

**Done:** both classes complete the course; drift/boost/jumps change real motion; the motorcycle needs no balance input. Neither charge nor checkpoint progress can be farmed through recovery/contact jitter.

## Task 4: Deliver the playable cameras, HUD, and controls

**Create:** `Runtime/Presentation/DrivingCamera.cs`, `Tests/PlayMode/DrivingPresentationPlayTests.cs`. **Modify:** `Runtime/FoundationShell.Driving.cs`, `FoundationShell.cs`, `FoundationShell.Screens.cs`, `FoundationInput.cs`, `Core/Localization/ShellText.cs`, existing service/observation DTOs.

**Interfaces:** consume session state and selected profile settings. Use stable UI names such as `driving.steerLeft`, `driving.boost`, `driving.recover`, `driving.pause`, `driving.retry`; wire them to the same commands as keyboard and agents.

- [ ] Write failing PlayMode tests for actual camera switching, HUD/session agreement, simultaneous steer+drift+boost, release/cancel/focus cleanup, pause/resume, and camera/locale changes during a held input.
- [ ] Add chase and elevated overhead cameras with obstacle handling and reduced-shake behavior. Test ramp/landing, respawn and both shortcut views. Record actual camera pose/target/mode in presentation state.
- [ ] Build a transparent race HUD around the 3D view; the current opaque foundation root must not cover it. Show lap/next gate, boost, elapsed time, pause and finish/retry states. Display unavailable gadget functionality honestly.
- [ ] Preserve current camera preference semantics: toggle updates the active draft and actual camera; explicit settings save persists it. Do not silently commit unrelated volume/language drafts when changing camera. Verify saved preference on process restart.
- [ ] Localize complete English/Hebrew strings, keep timers LTR, and test semantic touch/keyboard/agent parity. Run physical simultaneous touches on the A7 Lite and keyboard checks on Windows; synthetic callbacks alone cannot close this gate.

**Done:** both camera modes are usable during a full lap and real controls remain responsive through UI/pause transitions.

## Task 5: Add onboarding that proves learning through play

**Create:** `Core/Tutorial/DrivingTutorial.cs`, `Runtime/Presentation/TutorialPresenter.cs`, `Runtime/Content/TutorialDefinitionAsset.cs`, `Tests/EditMode/TutorialTests.cs`, `Tests/PlayMode/DrivingTutorialPlayTests.cs`, bundled prompts under `Assets/FamilyRacing/Audio/Tutorial/`. **Modify:** session/service, localization, shell and the Core test project for the new directory.

**Interfaces:** `DrivingTutorial.Advance(DrivingSessionState state, IReadOnlyList<DrivingEvent> events)` advances lessons; `TutorialState Snapshot()` exposes lesson ID, objective, completion and cue identity. `tutorial.replay` repeats the current cue without progressing the lesson.

- [ ] Add failing tests for ordered steering → drift → boost → jump → recovery lessons, replay with no advancement, retry/exit, both vehicle classes/locales, and no completion from setup/fixture commands or skipped checkpoints.
- [ ] Author short demonstrations, icons and bundled English/Hebrew spoken prompts. Use licensed local recordings or separately authorized production assets; no runtime speech service or credentials. Missing audio remains an acceptance gap, not a silent text-only substitute.
- [ ] Connect lessons to actual driving events, then unlock the one-lap introductory trial. A child can replay a cue and retry indefinitely; no failure deletes profile progress. Keep tutorial state session-local without changing save schema.
- [ ] Run automated lesson traversal through real controls and a Windows/Android smoke pass. Obtain Hebrew pronunciation/layout review and a child playtest with observed assistance, confusion points and finish result.

**Done:** a child can finish the introductory trial after onboarding with little reading assistance. Automated lesson traversal is necessary evidence, not a substitute for this check. Gadget lessons remain explicitly deferred.

## Task 6: Qualify complete driving observations and external scenarios

**Modify:** `Runtime/Automation/StateStream.cs`, `ValidationBridge.cs`, `ValidationJson.cs`, expanded state/event DTOs, `Tools/AgentScenario.py` and protocol assumptions in existing scenario helpers/tests. **Create:** `Tools/DrivingScenario.py`, `Tools/TestDrivingScenario.py`, `Tests/PlayMode/DrivingStreamPlayTests.cs`; update `docs/COMMANDS.md` and `docs/FEATURE_COVERAGE.md`.

**Interfaces:** schema-2 snapshot/delta/state-boundary records reconstruct the complete application state. Deltas identify their base sequence and changed/removed fields; events carry stable IDs. A tick boundary exists even when no fields change. Implement a consumer independent of the producer's delta logic.

- [ ] Add reconstruction fixtures and mutation tests: missing, duplicate, reordered and corrupt records must fail; an explicit resnapshot restores observation but cannot repair missing required history into a passing capture. Test all old full-state fixtures too.
- [ ] Add tests for transient start/end events in one tick, pause/menu changes, start/retry/exit snapshots, deep-copy state, screenshot completion, and unsupported schema rejection. Extend real UI/input tests to compare actual motor effects with command effects.
- [ ] Implement deltas/events without losing per-tick or command boundaries. Queue immutable logical records and serialize/write off the race thread where possible; measure copy cost rather than sharing mutable Unity objects with the writer.
- [ ] Implement `DrivingScenario.py <connection.json> --evidence <new-dir>`: discover, select vehicle, start, drive using bounded holds and observed position/gates, drift/boost/jump, recover, pause/camera switch, finish, retry and repeat for the other class. Wait on outcomes/state predicates with timeouts; do not use arbitrary sleeps as proof. Never teleport or mark checkpoints complete to pass a lap.
- [ ] Preserve command replay/identity conflict checks and sticky failure behavior. Measure the 10,000-identity cache, 256-record outgoing queue and 256 MiB trace cap under a representative 30-minute input schedule. Do not evict identities to hide capacity pressure; revise capacity/storage explicitly if measured use requires it.
- [ ] Run `python -B -m unittest discover -s Tools -p 'Test*.py'`, Core, relevant PlayMode checks, then external driving and existing foundation scenarios against a fresh Windows build. Record assertions, outcomes, full stream, screenshots, seed/content/build identity and logs.

**Done:** independently reconstructed state proves the real driving features at every boundary, and corrupted/slow/broken captures fail honestly. Command coverage ships with each earlier task; this task completes reconstruction and integrated evidence, not a deferred observability retrofit.

## Task 7: Review the milestone on both target platforms

**Files:** create `docs/tasks/driving-prototype-report.md`; update `IMPLEMENTATION_STATUS.md`, `README.md`, `docs/FEATURE_COVERAGE.md`. Build tooling changes, if needed, stay in existing Editor/Tools entry points and must preserve offline Android behavior.

- [ ] Run Core, Unity EditMode/PlayMode and Python tooling suites; record complete results with no undisclosed failures or skipped acceptance cases.
- [ ] Build Windows and Android from the same reviewed revision/content. Add an explicit ordinary-build mode alongside validation development builds if necessary; do not infer normal-build performance or disabled validation from a development player. Avoid setup silently resetting the selected backend/build mode.
- [ ] Run the real-player driving scenario on Windows and the A7 Lite through ADB forwarding with isolated profiles. Rerun the existing foundation restart/stream fault scenarios affected by state-schema changes.
- [ ] Measure 30-minute graybox sessions, both vehicles and cameras, repeated restarts, peak memory and frame-time distribution. Compare ordinary and instrumented builds; record trace size/growth, command counts and capture overhead separately. This single-racer result does not qualify the later four-racer/tablet-host target.
- [ ] Record owner acceptance of distinct vehicle feel, usable cameras, readable Hebrew and the child's tutorial/introductory finish. Document unresolved issues honestly and fix milestone failures before adding content.
- [ ] Update the milestone ledger only after the evidence and human/device review pass. Preserve later networking, four-device and final-art gates.

**Done:** one playable, reviewed Windows/Android driving prototype satisfies every M2 acceptance item, with evidence attached and explicit later-scope gaps.

## Execution order and review checkpoints

`M1 closeout/review → Task 1 → Task 2 → Task 3 → Task 5 → Task 6 → Task 7`.

Task 4 can proceed alongside Tasks 2–3 after Task 1 freezes state/command interfaces. Tutorial presentation needs Task 4 and its rules need Tasks 2–3. Stream/scenario work starts in Task 1 and continues with every feature; Task 6 is the integrated gate. Keep one owner for the shared service/state/bridge files to avoid conflicting changes. Commit independently verified task slices when implementation is authorized.

Use three owner checkpoints: **first complete car lap**, **both vehicles with cameras and driving mechanics**, **onboarding and tablet acceptance**. Tune feel at those checkpoints before polishing art. There is no fixed calendar estimate: engine integration and device/child feedback are the uncertainties, and the approved roadmap is quality-based.

Recommended execution is native implementation with focused parallel review and a separate camera/HUD workstream once interfaces settle. Most tasks share session/state contracts; parallelizing all gameplay systems before the first lap would increase integration work.

## Milestone acceptance checklist

- [ ] One course, two legal optional shortcuts, one landmark jump, safe recovery routes.
- [ ] Car and motorcycle complete the route and feel distinct; motorcycle balance is automatic.
- [ ] Drift, bounded boost, jumps/landings, impacts and manual/automatic recovery work through normal controls.
- [ ] Ordered checkpoints/laps cannot be bypassed by reversing, speed, shortcuts or recovery.
- [ ] Both real cameras, pause, retry and exit work on Windows and Android.
- [ ] English/Hebrew onboarding has icons and offline spoken cues; a child completes the intro trial.
- [ ] Every implemented action has command access, semantic UI/state/event coverage and real-player evidence.
- [ ] State reconstructs every captured boundary; invalid captures cannot be reported as passing.
- [ ] Fresh same-revision platform builds and measured single-racer performance meet the stated scope.
- [ ] M1 closeout and M2 acceptance are reviewed; no later milestone or full-release tutorial is falsely marked complete.
