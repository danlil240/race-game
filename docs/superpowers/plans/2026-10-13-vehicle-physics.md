# Vehicle Physics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (when delegation is authorized) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Read the design first. This document is the requested plan; creating it does not begin implementation.

**Goal:** Replace arcade vehicle motion with physical cars and automatically balanced motorcycles, including engine/brake forces, mass/inertia, suspension, grip, weight transfer, real rollovers, and qualified solo/LAN play.

**Architecture:** Keep rules and force calculations in Unity-free Core; simulate dynamic chassis in a Runtime local PhysicsScene through an injected world interface. Run two 60 Hz physics substeps within each existing 30 Hz logical boundary, with all racers advanced together. Preserve the arcade backend during migration, and replace local prediction with bounded physical replay before enabling the physical backend for LAN.

**Tech Stack:** Unity 6000.3.26f1; C# 9 / .NET Standard 2.1-compatible Core; existing URP, Input System, UI Toolkit, NUnit, NGO 2.7.0, Unity Transport 2.6.0, PowerShell verification and Python scenario tools. No added package is required.

**Spec:** [Physical vehicles with driving assists](../specs/2026-10-13-vehicle-physics-design.md). Also read [GAME_PLAN.md](../../../GAME_PLAN.md), [current status](../../../IMPLEMENTATION_STATUS.md), and [LAN runbook](../../NETWORK_TEST_RUNBOOK.md). The new spec proposes explicit changes to the arcade-motor contract; unrelated release requirements remain in force.

## Global Constraints

- Platforms: Android tablets and Windows PC.
- Required tablet: Samsung Galaxy Tab A7 Lite SM-T220; assume the weaker 3 GB configuration.
- Multiplayer: Two to four people over home Wi-Fi; four racers total.
- Connectivity: All gameplay works offline after installation.
- Any supported tablet or PC can host. Tablet-to-tablet racing must work without a PC.
- Require matching gameplay/content versions.
- Normalize vehicle performance within the selected class, including upgrades. Keep cosmetics.
- The host owns checkpoints, pickups, gadget effects, standings, and final results.
- Preserve English/Hebrew, touch controls, camera preferences, shared pause, reserved-seat rejoin, AI takeover, once-only rewards, and safe profile persistence.
- Keep Core Unity-free, C# 9 / .NET Standard 2.1 compatible. Use Unity 6000.3.26f1, NGO 2.7.0, and Unity Transport 2.6.0; no engine/package upgrade or purchased dependency is part of this work.
- Keep 30 Hz logical ticks, 15 Hz snapshots, and four racers. Use two fixed 1/60-second physics substeps per logical tick.
- All controls use the shared command path. Capture complete logical state and ordered transient events. Keep validation token-protected, loopback-only, and excluded from ordinary builds.
- Tablet: 30 fps target, 95th-percentile steady-state frame time at or below 40 ms, without recurring stalls above 100 ms.
- Start with an 800 MB process-memory budget on the 3 GB tablet; investigate growth across repeated races.
- PC: 60 fps at 1080p on the available reference PC.
- Immediate local steering feedback; network corrections must not routinely produce visible large jumps.

## Review Focus

1. Missing/expired throttle and explicit throttle zero differ; released brake, focus loss, pause and ownership changes must not leave a hidden held control — Tasks 1 and 7.
2. Four racers must advance one shared physics world twice per logical tick, never once per racer; pause/countdown must not accumulate physical movement — Tasks 3 and 4.
3. Brake-held parking, legitimate long jumps, inverted contact, and occupied recovery positions need distinct recovery behavior without false checkpoint/landing credit — Task 5.
4. Quaternion wrap, restore after collision, stale epochs, and replay overflow must not cause visual spins, duplicate events, or uncontrolled tablet replay bursts — Tasks 9 and 10.
5. More diagnostic state must not exceed wire/trace budgets, leak scenes/bodies on rematch, or turn desktop tests into claimed physical-device acceptance — Tasks 11 and 12.

## Execution rules and baseline

Planning inspected `c3e15dd` on `fix/driving-playtest-1` on 2026-10-13. The checkout was dirty, including active network, driving-world, generated-content, and tool changes. Do not reset, clean, stash, regenerate, or commit those changes wholesale. Reinspect `git status --short`, applicable AGENTS.md files, and the current implementations before execution. An isolated checkout must deliberately include any required local fixes; branching from HEAD alone will not copy uncommitted work.

Use focused TDD for behavior changes. Add meaningful assertions, observe the expected failure, implement, rerun the affected suite, then commit only the task's owned changes. Do not accept failures caused by broken tooling as evidence that a behavior test is valid. New Unity files require stable `.meta` files; include those in the owned change set. No placeholder implementation, fabricated test counts, or weakening assertions to hide a physics regression.

Use existing verification entry points from the repository root:

```powershell
Tools\Verify.ps1 -Mode CoreOffline
Tools\Verify.ps1 -Mode EditMode
Tools\Verify.ps1 -Mode PlayMode
```

`Verify.ps1` does not currently expose a test filter. Run its applicable mode, or add an explicit supported filter in a separately tested tooling change if suite time becomes a problem; do not invent a filter flag. Successful checks require exit code 0 and the new tests present/passing in results. CoreOffline verifies pure Core only; it cannot qualify PhysX. Establish current results and distinguish pre-existing failures from new ones. Preserve known external/hardware gates honestly.

## Milestones and dependencies

| Milestone | Tasks | Reviewable result |
|---|---|---|
| A: car laboratory | 1–3 | Configurable physical car, force tests, rollover fixture, early load measurement |
| B: complete solo driving | 4–8 | Integrated car/bike, controls, recovery, AI, presentation, tutorial |
| C: physical LAN | 9–10 | Versioned state, shared host collisions, bounded local prediction |
| D: qualification and rollout | 11–12 | Scenarios, evidence, builds, device matrix, gated default switch |

Execute in order. Task 11's scenario work can begin once Task 8 is stable, but its LAN checks depend on Task 10. Do not delegate edits to the same simulation/input/state files concurrently. A missing tablet does not prevent independent implementation or desktop tests; it prevents claiming device acceptance and the final default-switch gate.

## File responsibility map

All `Core/`, `Runtime/`, `Editor/`, and `Tests/` prefixes below mean `Assets/FamilyRacing/` followed by that path. `Tools/` and `docs/` are repository-root paths.

| Area | New units | Existing integration points |
|---|---|---|
| Physical data/forces | `Core/Driving/VehiclePhysicsTuning.cs`, `VehiclePhysicsState.cs`, `VehicleForceModel.cs`, `VehicleAssistRules.cs` | `MotorState.cs`, `DriverInput.cs`, `VehicleTuning.cs` |
| Simulation seam | `Core/Driving/IVehicleSimulation.cs`, `ArcadeVehicleSimulation.cs` | `DrivingSession.cs`, `NetworkRace.cs`, `FoundationService.cs` |
| Runtime physics | `Runtime/Driving/PhysicsVehicleSimulation.cs`, `PhysicsVehicleBody.cs`, `PhysicsCourseWorld.cs`, `WheelContactSolver.cs`, `MotorcycleBalanceController.cs` | `DrivingWorld.cs`, `FoundationApp.cs` |
| Authoring | `Runtime/Content/VehiclePhysicsTuningAsset.cs`, `Editor/VehiclePhysicsSetup.cs` | Existing driving/network setup, vehicle/track assets |
| Presentation/controls | Extend existing input, shell, camera, vehicle presentation | Existing bilingual tutorial and localization |
| Networking | `Runtime/Networking/PhysicsPredictionSimulation.cs`, `Core/Networking/NetworkPhysicsState.cs` | Existing input, snapshots, codec, prediction, smoothing, race bridge |
| Qualification | Physical Core/PlayMode tests; `Tools/PhysicsScenario.py`, `Tools/PhysicsSoak.py` | Existing scenario libraries, verification/build entry points |

Do not move unrelated systems or combine the new world/controller code into the already large foundation service.

### Task 1: Physical state, tuning, and explicit control semantics

**Files**

- Create: `Core/Driving/VehiclePhysicsTuning.cs`, `Core/Driving/VehiclePhysicsState.cs`, `Runtime/Content/VehiclePhysicsTuningAsset.cs`.
- Modify: `Core/Driving/{MotorState,VehicleTuning,DriverInput,DriverInputComposer}.cs`, `Runtime/Content/VehicleDefinitionAsset.cs`.
- Test: `Tests/EditMode/VehiclePhysicsStateTests.cs`, `VehiclePhysicsTuningTests.cs`, existing `InputTests.cs`.

**Interfaces**

- `VehiclePhysicsTuning.Validate(out string error): bool` and `Copy(): VehiclePhysicsTuning`; serialize SI parameters and initial car/bike presets from the spec. `VehicleTuning` references validated physics tuning when the backend is physical.
- `VehiclePhysicsState.Copy(): VehiclePhysicsState`, `Validate(out string error): bool`; scalar quaternion/linear/angular velocity, wheel/controller histories, contact/air episode state, `long recoveryEpoch`. Add `MotorState.physics` while preserving its legacy rule fields.
- Define `WheelPhysicsState` with wheel index, grounded flag, contact point/normal, compression, previous compression/contact-valid flag, steering, visual spin, normal load and tire slip diagnostics. Every history that affects forces must be copied/restored.
- Extend `DriverInput` through a backward-compatible constructor overload with `float? throttleOverride, float brake`. Properties: `HasThrottleOverride`, effective `Throttle`, `Brake`. `default(DriverInput).Throttle == 1f`; explicit override 0 remains 0. Composition follows the spec's override/min/max rules.

- [ ] Write failing `DefaultInputUsesAutoThrottle_ExplicitZeroCoasts`, `InputCompositionKeepsBrakeAndOverride`, `PhysicsCopyDoesNotAliasWheels`, and tuning validation tests. Pin these assertions:

  ```csharp
  Assert.That(default(DriverInput).Throttle, Is.EqualTo(1f));
  Assert.That(explicitCoast.Throttle, Is.EqualTo(0f));
  Assert.That(composed.Brake, Is.EqualTo(0.8f));
  Assert.That(copy.physics.wheels[0].compression, Is.EqualTo(originalCompression));
  Assert.That(invalidMass.Validate(out _), Is.False);
  Assert.That(nonFiniteQuaternion.Validate(out _), Is.False);
  ```

- [ ] Run CoreOffline; confirm failures exercise missing/new behavior. Include negative/non-finite mass, zero inertia multipliers, invalid wheel count/layout, force bounds, quaternion norm and null-array cases.
- [ ] Implement the DTOs and authoring asset. Normalize only usable quaternions; reject zero/non-finite quaternions before mutation. Add explicit units/tooltips and validate asset conversion atomically. Legacy assets continue selecting arcade until deliberately configured.
- [ ] Rerun CoreOffline and EditMode; verify old input callers compile, copies are independent, and legacy asset conversion still succeeds.
- [ ] Commit owned state/tuning/control changes: `feat: define physical vehicle state and tuning`.

### Task 2: Pure suspension, tire, braking, and assist calculations

**Files**

- Create: `Core/Driving/VehicleForceModel.cs`, `Core/Driving/VehicleAssistRules.cs`.
- Test: `Tests/EditMode/VehicleForceModelTests.cs`, `VehicleAssistRulesTests.cs`.

**Interfaces**

- `VehicleForceModel.SuspensionForce(float compression, float compressionSpeed, float spring, float damper, float maxLoad): float`.
- `VehicleForceModel.LimitTireForces(float longitudinal, float lateral, float normalLoad, float friction): TireForces`; `TireForces` has `longitudinal` and `lateral` fields.
- `VehicleForceModel.BrakeForce(float contactSpeed, float requestedForce, float supportedMass, float dt): float` returns signed longitudinal force; slope hold is a separate contact demand before the combined friction limit.
- `VehicleAssistRules.SteeringTarget(float input, float speed, VehiclePhysicsTuning tuning): float` returns road-wheel radians. `BikeLeanTarget(float speed, float yawRate, float maxLeanRadians): float` returns a bounded lean target; torque application remains Runtime.

- [ ] Write failing tests with physical oracles: `SuspensionCannotPullGround`, `CombinedGripSharesOneBudget`, `BrakeCannotReverseVelocity`, `NoNormalLoadMeansNoTraction`, and `SteeringAndLeanAreBounded`.

  ```csharp
  Assert.That(VehicleForceModel.SuspensionForce(-0.1f, 0, 40000, 3500, 20000), Is.Zero);
  var force = VehicleForceModel.LimitTireForces(3000, 4000, 2500, 1);
  Assert.That(force.longitudinal, Is.EqualTo(1500).Within(0.01));
  Assert.That(force.lateral, Is.EqualTo(2000).Within(0.01));
  Assert.That(VehicleForceModel.BrakeForce(10, 2000, 100, 1), Is.EqualTo(-1000));
  ```

- [ ] Run CoreOffline and observe the expected failure.
- [ ] Implement finite, allocation-free scalar calculations; use the spec's combined friction and braking model. Cover contact loss/reacquisition, zero speed, reverse rolling while braking, maximum force saturation, banked contact basis inputs, and low-speed steering behavior. Do not implement a second chassis integrator in Core.
- [ ] Run CoreOffline; assert energy-opposing braking/lateral forces and finite output across a parameter sweep, including zero load and large slip.
- [ ] Commit: `feat: calculate suspension and combined tire forces`.

### Task 3: Shared physics world and a playable car laboratory

**Files**

- Create: `Core/Driving/IVehicleSimulation.cs`; `Runtime/Driving/{PhysicsVehicleSimulation,PhysicsVehicleBody,PhysicsCourseWorld,WheelContactSolver}.cs`; `Editor/VehiclePhysicsSetup.cs`.
- Generate: `Assets/FamilyRacing/Generated/Physics/` tuning assets and collision/validation fixtures, with stable metadata. Do not overwrite unrelated generated assets.
- Test: `Tests/PlayMode/PhysicsTestWorld.cs` (fixture helper), `PhysicsVehiclePlayTests.cs`, `PhysicsClockPlayTests.cs`.

**Interfaces**

Define these Unity-free types in `IVehicleSimulation.cs`:

- `VehicleSpawn`: `string participantId`, `VehicleTuning tuning`, `MotorState initialState`.
- `VehicleTickRequest`: `string participantId`, `DriverInput input`, `MotorState rulesBefore`, `bool simulateMotion`. `rulesBefore` supplies logical motor facts; it does not overwrite the live body pose each tick.
- `VehiclePathSample`: `float offsetSeconds`, `MotorPose pose` (0, 1/60, 1/30 within a tick).
- `VehicleTickResult`: `string participantId`, `MotorStepResult step`, `VehiclePathSample[] path`.
- `IVehicleSimulation : IDisposable`: `void Initialize(IReadOnlyList<VehicleSpawn> vehicles)`, `IReadOnlyList<VehicleTickResult> Step(IReadOnlyList<VehicleTickRequest> requests)`, `void Restore(string participantId, MotorState state)`, `MotorState Capture(string participantId)`.

`Step` means exactly one logical interval, not arbitrary dt. Prevalidate the whole batch before mutating bodies. Disabled/countdown/finished bodies are frozen and non-interfering with active racers. Restore updates the body, all explicit controller histories, sleep/contact bookkeeping and epoch, with no gameplay events. Capture returns an independent copy. Collision callbacks are collected and ordered after simulation, not used to recursively step or mutate race rules.

- [ ] Write failing PlayMode tests: `EqualForceRespectsMass`, `OffCenterForceChangesAngularVelocity`, `FourBodiesAdvanceOnlyTwoSubsteps`, `RenderCadenceDoesNotChangeElapsedPhysicsTime`, `BrakeStopsWithoutReversing`, and `SceneLifecycleDoesNotLeak`.

  ```csharp
  Assert.That(lightDeltaVelocity, Is.EqualTo(0.3f).Within(0.005)); // 3000 N, 1000 kg, 0.1 s
  Assert.That(heavyDeltaVelocity, Is.EqualTo(0.15f).Within(0.005)); // same force, 2000 kg
  Assert.That(substepsForOneLogicalTickWithFourRacers, Is.EqualTo(2));
  Assert.That(simulatedSecondsAfter300Ticks, Is.EqualTo(10).Within(0.001));
  ```

- [ ] Run PlayMode. Isolate mass/torque tests from drag, assists, ground and tire limits; otherwise the oracle is invalid.
- [ ] Implement local-scene ownership, correct scene queries, primitive colliders, computed inertia, contact-point velocity, steering/force application and shared substeps. Use preallocated wheel/contact buffers. No force application in render `Update`/automatic `FixedUpdate`, no frozen rotation, and no visual transform writing into the chassis.
- [ ] Generate a flat acceleration/braking lane, slope, ordinary turn, slalom/curb, high-COM roll fixture and jump/landing fixture. Make a developer car laboratory selectable through the existing validation/build setup. Test stock car rollover on the aggressive curb fixture and a physical untripped roll with the labeled high-COM preset; assert `dot(bodyUp, worldUp) < 0` without assigning a flip rotation.
- [ ] Run PlayMode and record car handling, settled COM height, frame/physics timing, and four-body load. Produce `docs/tasks/vehicle-physics-prototype-report.md` with reproducible fixture geometry/inputs and measured outcomes. Continue integration only after technical prototype tests pass; leave inaccessible human/tablet rows pending, and keep the backend opt-in.
- [ ] Commit: `feat: add fixed-step physical car prototype`.

### Task 4: Separate race rules from all-racer physical advancement

**Files**

- Create: `Core/Driving/ArcadeVehicleSimulation.cs`.
- Modify: `Core/Racing/{DrivingSession,DrivingSessionState,NetworkRace,RacerContacts}.cs`, `Core/Foundation/{FoundationService,FoundationService.Network}.cs`, `Runtime/FoundationApp.cs`, `Runtime/Driving/DrivingWorld.cs`, `Runtime/Networking/NetworkRaceBridge.cs`.
- Test: existing `Tests/EditMode/{DrivingClockTests,CheckpointTests,NetworkRaceTests}.cs`; create `Tests/EditMode/VehicleSimulationBoundaryTests.cs`; extend `Tests/PlayMode/PhysicsClockPlayTests.cs`.

**Interfaces**

- `DrivingSession.PrepareStep(DriverInput input): VehicleTickRequest` and `CompleteStep(VehicleTickResult result): DrivingStepResult` split the current step. Define `VehicleReset` (`participantId`, `MotorState state`) and return an optional reset with `DrivingStepResult`; coordinators apply it before publishing.
- `NetworkRace.Step(IReadOnlyDictionary<string, DriverInput> inputs, IVehicleSimulation simulation): NetworkRaceState` prepares all racers, invokes one batch, completes all racers, applies resets, then publishes. Keep a legacy adapter overload only while callers migrate.
- Define `VehicleSimulationBackend { Arcade, PhysicalAssisted }` in the simulation seam and add `DrivingContentCatalog.SimulationBackend`, with the existing catalog factory defaulting to Arcade. Runtime selects the implementation once at race creation; backend and tuning participate in content compatibility. No mid-race backend changes.
- Inject the simulation into solo/host/prediction ownership explicitly; Core never constructs a Unity object. `ArcadeVehicleSimulation` wraps the current motor and pre-step nudge semantics for regression comparison. Physical mode excludes `RacerContacts` artificial nudges.

- [ ] Write failing `PrepareAllBeforeSimulateBeforeComplete`, `CountdownAndPauseFreezeBodiesAndTimers`, `RecoveryResetPrecedesPublication`, `SubstepPathsCrossOrderedGates`, and `RejectedBatchDoesNotPartiallyAdvance` tests. A spy simulator must record `prepare x4 -> step x1 -> complete x4`; elapsed race ticks increment once, with 60-second finish grace unchanged.
- [ ] Run CoreOffline/PlayMode to expose the old per-racer stepping/clock assumptions.
- [ ] Refactor at the boundary above while retaining action latching, immutable results, event draining and attribution. Apply pending/manual resets before physics; rule-triggered resets after completion but before snapshots. A reset boundary cannot count movement segments. Ensure only solo or LAN owns a physical world in a given screen.
- [ ] Run CoreOffline and PlayMode with the arcade adapter first, then physical fixtures; test pause at a rollover, a frame backlog, countdown transition, a simultaneous finish and scene exit. Keep wall-time leases/transport alive during pause.
- [ ] Commit: `refactor: advance vehicle worlds outside per-racer rules`.

### Task 5: Physical drift, boost, jumps, rollover, and recovery

**Files**

- Modify: `Core/Driving/DriftBoostRules.cs`, `Core/Racing/{RecoveryRules,CourseDefinition,DrivingSession}.cs`, `Runtime/Content/{TrackDefinitionAsset,EventDefinitionAsset}.cs`, physical force/body files from Tasks 2–3 and `Editor/VehiclePhysicsSetup.cs`.
- Test: existing `Tests/EditMode/{DriftBoostTests,RecoveryTests,CheckpointTests}.cs`; create `Tests/PlayMode/PhysicsRecoveryPlayTests.cs` and `PhysicsHandlingPlayTests.cs`.

**Interfaces**

- Extend `RecoverySettings` with `overturnedSeconds = 2f` and `outsideBoundsSeconds = 1.5f`; author course recovery bounds/safe candidates and copy/validate them.
- Add `RecoveryRules.Evaluate(MotorState motor, DriverInput input, CourseDefinition course, RecoverySettings settings, float deltaSeconds): string` for physical mode. Preserve the old arcade evaluator through its adapter until rollout.
- Add `VehicleAssistRules.CanApplyGroundAssist(VehiclePhysicsState state): bool`; contacts and limits govern assist eligibility, never steering-triggered flip logic.

- [ ] Write failing cases: `BrakeHeldForTenSecondsDoesNotRecover`, `CoastingParkDoesNotRecover`, `ValidTwoSecondJumpDoesNotRecover`, `InvertedSupportedForTwoSecondsRecoversOnce`, `RecoveryDoesNotCrossGateOrChargeBoost`, `OccupiedSafePoseUsesClearCandidate`, and `AirborneWheelsCannotDriveOrBrake`.

  ```csharp
  Assert.That(recoveriesDuringTenSecondBrakeHold, Is.Zero);
  Assert.That(recoveriesBeforeInvertedContactSeconds2, Is.Zero);
  Assert.That(recoveriesAfterThreshold, Is.EqualTo(1));
  Assert.That(chargeAfterRecovery, Is.EqualTo(chargeBeforeRecovery));
  Assert.That(checkpointsAddedByTeleport, Is.Zero);
  ```

- [ ] Run CoreOffline and PlayMode; confirm the old beached/airtime behavior fails the new physical cases.
- [ ] Implement demand-based stuck detection, bounds/kill-height checks, overturned contact timer, clearance-tested safe placement, full controller reset and recovery epoch. Determine real grounded/airborne/clean-landing episodes from wheel/body contacts and impact/attitude. Preserve event names consumed by the tutorial where meaning still agrees.
- [ ] Replace arcade drift kicks with progressive rear-grip reduction; keep one combined grip budget and finite steering/anti-roll assistance. Boost applies force with no air traction and no force while braking. Test stock normal turns stay usable and aggressive fixtures still roll with assists enabled. Record any necessary ramp/safe-pose edits for the new gravity.
- [ ] Run the affected suites and a complete physical car course; commit: `feat: integrate physical handling and recovery rules`.

### Task 6: Motorcycle dynamics and automatic balance

**Files**

- Create: `Runtime/Driving/MotorcycleBalanceController.cs`.
- Modify: `VehicleAssistRules.cs`, `PhysicsVehicleBody.cs`, `VehiclePhysicsSetup.cs`, and motorcycle tuning assets under `Generated/Physics/`.
- Test: `Tests/EditMode/VehicleAssistRulesTests.cs`; create `Tests/PlayMode/MotorcyclePhysicsPlayTests.cs`.

**Interfaces**

- `MotorcycleBalanceController.Apply(PhysicsVehicleBody body, DriverInput input, VehiclePhysicsTuning tuning, float stepSeconds): void` applies bounded torque only. Store any filter/integral history in `VehiclePhysicsState`, not an uncaptured MonoBehaviour field.
- Use the Task 2 lean target, maximum 45 degrees; cease balance torque beyond 70 degrees of fall. Define angles against the appropriate supported ground frame; reject near-vertical support normals.

- [ ] Write failing `BikeBalancesAtRestAndLowSpeed`, `BikeLeansIntoBothTurnDirections`, `AirborneBalanceDoesNotRightTheBike`, `HardSideImpactCanKnockBikeOver`, `FallenBikeRecoversOnce`, and `BikeRestoreReplaysControllerHistory` tests. Require a stationary supported bike to stay within 10 degrees for 10 seconds with ordinary assistance; compare airborne angular motion with the documented damping-only model.
- [ ] Run PlayMode; verify tests cannot pass from locked rotational constraints or a cosmetic lean.
- [ ] Implement front steering/rear drive, two suspension contacts, capped contact-aware lean/balance torque, fall disablement, and the same force/recovery lifecycle as the car. Use combined bike/rider mass; no ragdoll or hidden side support wheels.
- [ ] Run CoreOffline and PlayMode; drive three consecutive motorcycle laps with no invalid state. Test slopes, jumps, braking during lean, low-speed steering reversal and post-impact recovery.
- [ ] Commit: `feat: add assisted physical motorcycle handling`.

### Task 7: Commands, keyboard/touch braking, AI, and tutorial

**Files**

- Modify: `Core/Foundation/{FoundationService,FoundationState}.cs`, `Core/Driving/DriverInputComposer.cs`, `Runtime/FoundationInput.cs`, `Runtime/{FoundationShell,FoundationShell.Driving,FoundationShell.Network}.cs`, `Core/Racing/AiDriver.cs`, `Core/Tutorial/DrivingTutorial.cs`, `Core/Localization/ShellText.cs`, `Runtime/Content/TutorialDefinitionAsset.cs`, existing tutorial setup.
- Test: `Tests/EditMode/{InputTests,DrivingCommandTests,NetworkAiTests,TutorialTests}.cs`, `Tests/PlayMode/{FoundationInputPlayTests,DrivingTutorialPlayTests,NetworkScreensPlayTests}.cs`.

**Interfaces**

- Extend discovery and `InputState` with effective throttle, throttle-override presence, and brake. `input.set` optional decimal `throttle`/`brake` must reject non-finite/out-of-range values atomically.
- Keep `AiDriver.Sample(DrivingSessionState racer, CourseDefinition course): DriverInput`; extend route lookahead with a curvature/speed target and brake demand using normalized class tuning. It must use the same commands, forces and recovery rules as humans.

- [ ] Write failing command/input tests for throttle absent vs zero, invalid mixed command atomicity, max-composed brake, S/Down, touch pointer cancel, focus loss, expired lease, pause, and takeover. Assert all representations agree and `brake > 0` yields zero engine drive demand.
- [ ] Run CoreOffline and PlayMode.
- [ ] Add keyboard and held touch brake through existing input dispatch, with stable semantic IDs and English/Hebrew copy. Preserve touch target sizes/RTL directional meaning. Add a brief braking lesson and fall/recovery guidance; reuse icons/text/cues where possible. Missing newly required voice audio remains an explicit content gate, not a fabricated asset.
- [ ] Teach AI to slow before tight turns, avoid boosting while braking/leaning hard, and request ordinary recovery. Test three laps in both classes, four AI racers, and disconnect takeover without teleport/force bonuses. Ensure unavailable gadgets remain honestly unavailable.
- [ ] Rerun relevant suites and commit: `feat: expose braking and adapt driving assistance`.

### Task 8: Physical presentation, cameras, and reproducible content

**Files**

- Modify: `Runtime/Driving/{VehiclePresentation,DrivingWorld}.cs`, `Runtime/Presentation/DrivingCamera.cs`, `Editor/{DrivingPrototypeSetup,NetworkPrototypeSetup,FoundationProjectSetup}.cs`, `Editor/VehiclePhysicsSetup.cs`, generated vehicle/course/tutorial assets as deliberately required.
- Test: `Tests/PlayMode/{DrivingPresentationPlayTests,VehicleClassPlayTests,DrivingWorldPlayTests}.cs`; create `Tests/EditModeAdapter/VehiclePhysicsSetupTests.cs`.

**Interfaces**

- Preserve `VehiclePresentation.ApplyPose(MotorState motor, float steering, float alpha)`; physical mode reads `motor.physics` attitude and wheel states, while arcade retains its existing presentation path.
- `VehiclePhysicsSetup.Create(): void` is idempotent and called by the established setup entry point. A developer backend selection is bound to content identity, not exposed as an implementation detail in ordinary player UI.

- [ ] Write failing tests for quaternion interpolation through +/-180 degrees, visible wheels following suspension/steering, motorcycle lean applied once, a full car roll, horizon-stable chase/overview camera, and snap on recovery epoch.
- [ ] Run PlayMode/EditMode; confirm existing presentation forces the chassis root upright.
- [ ] Render the complete physical pose, interpolate between committed boundaries, and keep camera roll independent of vehicle roll. Add bounded landing/suspension feedback respecting reduced-camera-shake and volume settings. Derive wheel spin from travel for this simplified model; do not claim an engine-RPM simulation exists.
- [ ] Wire reproducible assets and explicit physical/arcade backend selection; regenerate only owned assets. Run setup twice and verify stable semantic output/GUIDs. Validate hull/ray placement, ramp clearances, four spawn positions and respawn candidates in both classes.
- [ ] Run EditMode/PlayMode and solo play; commit: `feat: present physical vehicles and generate their content`.

### Task 9: Versioned physical LAN state and authoritative collisions

**Files**

- Create: `Core/Networking/NetworkPhysicsState.cs`.
- Modify: `Core/Networking/{NetworkSettings,NetworkProtocol,NetworkInputFrame,NetworkSnapshot,HostInputBuffer,LanSessionService}.cs`, `Runtime/Networking/{NetworkWireCodec,NetworkRaceBridge,LanDiscovery}.cs`, network race integration from Task 4.
- Test: `Tests/EditMode/{HostInputBufferTests,LanSessionTests,NetworkRaceTests,NetworkLifecycleTests}.cs`, `Tests/EditModeAdapter/NetworkWireCodecTests.cs`, `Tests/PlayMode/{NetworkRaceWorldPlayTests,NetworkReplicationPlayTests}.cs`.

**Interfaces**

- `NetworkPhysicsState.FromMotor(MotorState motor): NetworkPhysicsState` and `ToMotor(): MotorState` map compact bounded replay state. Preserve quaternion, velocities, recovery epoch and all controller histories; diagnostic-only contact data may stay in the full observation DTO.
- Version values are pinned by the spec: LAN 2/`FRN2`, gameplay `gameplay-2`, observation schema 4, physical content `physics-1` plus canonical fingerprint; typed tags `P1IB/P1SS/P1EB/P1RS`. Inspect the current tree first in case a newer revision already occupies those versions, then document any required next version consistently.

- [ ] Write failing round-trip tests for upside-down/airborne state, brake/throttle override, controller history, normalized quaternions and max-size four-racer snapshots. Assert old protocol/content/tuning fingerprints are rejected before slot allocation and malformed state causes no partial mutation.
- [ ] Run CoreOffline/EditMode and observe legacy-codec failures.
- [ ] Update discovery, handshake, gameplay codecs and schema consumers together. Canonical fingerprints must be stable across Windows/Android, use sorted data/culture-independent encoding, and avoid runtime `GetHashCode`. Preserve 16 KiB snapshots, 4 KiB input packets, bounded arrays and ownership validation; prove worst-case serialized byte sizes including maximum allowed IDs/events.
- [ ] Wire one host physical world for all four racers; preserve normalized tuning and remove arcade nudges only in physical mode. Test front/side collisions, a racer landing on another racer, paused contact, disconnect takeover, occupied respawn and simultaneous finishes. Clients send inputs; host alone computes contact/progress/rewards.
- [ ] Run CoreOffline/EditMode/PlayMode and commit: `feat: replicate authoritative physical vehicle state`.

### Task 10: Bounded client prediction and full-attitude interpolation

**Files**

- Create: `Runtime/Networking/PhysicsPredictionSimulation.cs`.
- Modify: `Core/Networking/{ClientPrediction,RemoteInterpolation,CorrectionSmoother}.cs`, `Runtime/Networking/NetworkRaceBridge.cs`, `Core/Networking/NetworkSettings.cs`.
- Test: `Tests/EditMode/ClientPredictionTests.cs`, `Tests/PlayMode/NetworkPredictionPlayTests.cs`; create `Tests/PlayMode/PhysicsPredictionPlayTests.cs`.

**Interfaces**

- `PhysicsPredictionSimulation` implements Task 3's `IVehicleSimulation` for one local racer and a separate collision-only static course scene. Inject `IVehicleSimulation` into `ClientPrediction`; remove hard-coded ArcadeMotor replay from the physical path.
- Add to prediction observations: position/orientation error, logical ticks replayed, replay time, pending count, recovery epoch, resync reason/count. Continue the existing acknowledgment/sequence/epoch contract.

- [ ] Write failing `RestoreIncludesAngularAndControllerState`, `ReplayNeverPublishesGameplayEvents`, `RecoveryAndOwnershipClearHistory`, `OldSnapshotsCannotUndoRecovery`, `HistoryOverflowRequestsResync`, and `ReplayBudgetNeverExceedsTwelveTicks` tests. Pin 120 retained inputs, 12 replay ticks/frame, 3 m/45-degree snap thresholds, 0.1-second visual blend and extrapolation cap.
- [ ] Run CoreOffline/PlayMode to expose yaw-only and ArcadeMotor assumptions.
- [ ] Implement restore/replay of local physics, using the authoritative acknowledged boundary and aligned input ticks. Remote visuals never participate as dynamic bodies in this scene. Collect physics callbacks needed for contact state while suppressing their gameplay effects/rewards during replay; do not mutate the actual host/rule state or course while replaying. Authoritative restore preserves valid suspension history; spawn/recovery invalidates it.
- [ ] Apply immediate simulation correction and position/quaternion presentation smoothing. Coalesce queued snapshots to the newest valid boundary and enforce the 12-tick replay budget across the entire rendered frame, including packet bursts. Recovery/new race/new owner snaps; budget/history failure requests a full resync and holds usable authoritative presentation. Do not spread a partially replayed world over frames while presenting it as current.
- [ ] Run a fresh local host/client through ordinary LAN and 100 ms RTT, +/-30 ms jitter, 2% loss. Measure the spec's no-contact p95 error targets, contact convergence <=0.5 seconds, no duplicated boost/landing/result events, and replay frame cost. Use tolerances, never cross-device byte-for-byte PhysX equality. Keep physics LAN opt-in if these gates fail.
- [ ] Commit: `feat: predict and reconcile physical vehicle movement`.

### Task 11: Observable physics and agent-driven qualification tools

**Files**

- Modify: `Core/Foundation/FoundationState.cs`, `Core/Racing/DrivingSessionState.cs`, `Runtime/Automation/{StateStream,ValidationJson,ControllerSession}.cs`, `Tools/{AgentScenario,DrivingScenario,NetworkScenario,NetworkSoak}.py` and their relevant `Test*.py` files.
- Create: `Tools/PhysicsScenario.py`, `Tools/TestPhysicsScenario.py`, `Tools/PhysicsSoak.py`, `Tools/TestPhysicsSoak.py`.
- Test: existing `Tests/PlayMode/{DrivingStreamPlayTests,NetworkStreamPlayTests,ValidationStreamRobustnessPlayTests}.cs`.
- Update: `docs/COMMANDS.md`, `docs/FEATURE_COVERAGE.md`, `docs/NETWORK_TEST_RUNBOOK.md`.

**Interfaces**

- `python Tools\PhysicsScenario.py <connection.json> --evidence <new-dir>`: normal command-path trials on generated validation events plus current-course regression. The CLI must be delivered in this task; it does not exist at planning time.
- `python Tools\PhysicsSoak.py --connections <host.json> <client.json> ... --minutes 30 --evidence <new-dir>`: reuse established lifecycle/trace helpers for repeated physical races/rematches and health sampling.
- Evidence includes build/content/tuning hashes, backend, device/runtime, test inputs/fixture identity, raw metrics, thresholds, observed errors and a pass/fail/not-verified status. No release debug teleport or arbitrary-force command is required; unit fixtures may set initial state, player scenarios may only select authored events and use ordinary controls.

- [ ] Write failing schema reconstruction/event-order tests and Python harness tests against small synthetic traces. Include a tire losing and regaining contact within one logical tick, flip/recovery, changed wheel arrays, replay suppression, duplicate packets, stale schema, invalid capture, and truncated evidence.
- [ ] Run the affected Python tests and PlayMode. A trace validator must reject unsupported schema before trusting physics claims.
- [ ] Expose full physical/controller state, tuning identity, force/contact diagnostics, recovery reason/epoch, physics cost/substep counts and correction telemetry. Keep ordered substep transitions while publishing the regular 30 Hz logical boundary. Preserve overflow-invalidates-capture behavior and existing stream bounds; do not silently discard new diagnostics.
- [ ] Implement acceleration/coast/brake, grip/slalom, rollover, jump/landing, intentional parking, manual/automatic recovery, both vehicle classes, pause/focus cleanup, and LAN contact/rejoin scenarios. Use actual observed motion and state assertions; screenshots alone cannot prove physical dynamics.
- [ ] Run `python -m unittest discover -s Tools -p 'Test*.py'`, PlayMode and the new scenarios on a fresh development player. Measure a 30-minute trace against the existing 256 MiB cap and check world/body/history counts return to baseline after rematches. Commit: `test: qualify physical vehicle behavior through agent controls`.

### Task 12: Build, device qualification, documentation, and rollout

**Files**

- Create: `docs/tasks/vehicle-physics-report.md`.
- Update: `GAME_PLAN.md`, `IMPLEMENTATION_STATUS.md`, `README.md`, `docs/FEATURE_COVERAGE.md`, `docs/NETWORK_TEST_RUNBOOK.md`, backend-default content only after the gate.

**Interfaces**

- Final report is the single new physics acceptance record. Keep implementation status, automated verification, physical-device qualification and human handling acceptance distinct.

- [ ] Run the final relevant automated gate on the final source state. Record exit codes, test counts, named failures/skips and evidence paths. Re-run a check only when a change or unresolved failure warrants it.

  ```powershell
  Tools\Verify.ps1 -Mode CoreOffline
  Tools\Verify.ps1 -Mode EditMode
  Tools\Verify.ps1 -Mode PlayMode
  python -m unittest discover -s Tools -p 'Test*.py'
  Tools\Test-VerifyTooling.ps1
  Tools\Verify.ps1 -Mode Windows
  Tools\Verify.ps1 -Mode WindowsOrdinary
  Tools\Verify.ps1 -Mode Android
  ```

- [ ] Run fresh Windows solo and LAN physical scenarios and 30-minute soaks, including both classes, collisions, pause, takeover, rejoin, host loss and repeated results/rematches. Hash the tested binaries/content and preserve source provenance including uncommitted build inputs. Verify ordinary builds exclude validation access.
- [ ] Qualify actual devices: A7 Lite 3 GB as host with four physical racers, tablet-to-tablet offline Wi-Fi, mixed Windows/Android, ordinary and instrumented performance separately, touch/Hebrew and thermal behavior. Meet the spec's frame/memory/network targets. A same-PC four-process run is useful evidence but does not satisfy these rows.
- [ ] Obtain human handling evidence: braking is discoverable, ordinary corners remain controllable, car/motorcycle feel distinct, assisted bike works at low speed, rollovers feel consequential without being constant, and recovery/camera remain comfortable. Include child/tutorial acceptance. If people/devices are unavailable, mark those rows `not verified` and preserve a usable opt-in build.
- [ ] Review the full change against the spec, especially dual clocks, mutable snapshots, rollout versioning, command parity, physical replay limitations, respawn overlap, and once-only rewards. Resolve actionable defects and rerun affected checks; do not reuse arcade milestone results as physics evidence.
- [ ] Update release documents to describe the new behavior and precise remaining gates. Only when required automated/device/human gates pass, switch the shipped content to physical mode, rebuild and smoke-test that exact default. Keep the arcade comparator as a developer fallback for this milestone; removal is later cleanup, not a condition of success.
- [ ] Commit owned qualification/default changes: `feat: qualify and enable assisted vehicle physics` only if enabled, otherwise `docs: record physical vehicle qualification and open gates`.

## Completion contract for the implementing agent

The work is complete when the requested physical behavior exists in both vehicle classes; solo/tutorial and four-racer LAN use the correct backend; controls, observations and prediction agree; gates above are supported by fresh evidence; and the final rollout status is explicit. Do not describe an opt-in desktop prototype as a fully shipped tablet physics replacement.

At each milestone report: changed behavior, tests/evidence, measured performance/correction metrics, current backend/default, and remaining external gates. If a design target fails, report the measured failure and revise the smallest affected design/task; do not quietly replace physical behavior with animation or drop multiplayer prediction.

## Ready-to-use agent handoff

> Implement `docs/superpowers/plans/2026-10-13-vehicle-physics.md` after reading its linked design. Reinspect the dirty workspace and preserve existing work. Start with Tasks 1–3 and produce the car prototype evidence, then follow the dependencies through solo, motorcycle, LAN prediction and qualification. Keep the physical backend opt-in until its gates pass. Use the existing shared input/command path, keep Core Unity-free, advance all physical racers together, and never claim deterministic cross-platform PhysX replay. Record real test/build/scenario results and leave inaccessible hardware/human acceptance explicitly open. Do not spend money, upgrade Unity/packages, implement unrelated game features, or change the release default before qualification.
