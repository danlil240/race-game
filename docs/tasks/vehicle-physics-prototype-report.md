# Vehicle physics technical prototype report — Task 3

Date: 2026-10-13 (UTC). Task: "Shared physics world and a playable car
laboratory" (`.superpowers/sdd/2026-10-13-vehicle-physics/task-3-brief.md`,
binding design
`docs/superpowers/specs/2026-10-13-vehicle-physics-design.md`).

Status: **all automatable gates green.** The physical backend is opt-in
(`VehicleSimulationBackend.PhysicalAssisted`); the arcade motor remains
the default everywhere. Inaccessible human/tablet rows remain pending —
this report covers the technical prototype only.

## Environment

- Unity 6000.3.26f1 (pinned by `Tools\Verify.ps1`), Windows 11 x64.
- All numbers below come from `Logs\verify-unity-playmode.log` and
  `Logs\verify-playmode-results.xml` of the final verification pass.

## Suite results (this pass, same tree)

| Suite | Result | Notes |
|---|---|---|
| `Verify.ps1 -Mode PlayMode` | **187/188 passed, 1 failed** | Only failure: `NetworkPredictionPlayTests.ImpairedLinkExercisesPredictionAndInterpolation` — pre-existing environment issue (LAN sockets fail to bind: `Failed to start primary listening socket at (0.0.0.0:55504-55512)`), unrelated to physics. |
| `Verify.ps1 -Mode EditMode` | **552/553 passed, 1 skipped** | Pre-existing skip. |
| `Verify.ps1 -Mode CoreOffline` | **513/513 passed** | Pure Core assembly (Unity-free). |

## Clock and substep semantics (pinned assertions)

- `FourBodiesAdvanceOnlyTwoSubsteps`: one logical `Step` for four racers
  performs exactly **2 shared substeps** (not 8). Bodies never step
  independently.
- `RenderCadenceDoesNotChangeElapsedPhysicsTime`: 300 logical ticks at
  1/30 s equal **10.000 ± 0.001 s** simulated whether ticks run in a
  tight loop or spread across 300 rendered frames; end-of-run trajectory
  matches within 0.05 m. `SubstepsSimulated` = 600.
- Forces for all active bodies are applied before a single shared
  `PhysicsScene.Simulate(1/60)` per substep; nothing steps from render
  `Update` or automatic `FixedUpdate`.

## Measured prototype outcomes

### Settled COM height

`SettledComHeightAndFourBodyLoadAreRecorded` logs (log line marked
`PHYSICS-LAB`):

- **Settled COM height: 0.546 m** (body origin y = 0.696 m) for the stock
  `Car01` preset on flat ground — COM sits 0.15 m below the body origin
  per the authored `comOffsetY`, resting on the raycast suspension.

### Frame/physics timing and four-body load

- **Four-body load: 0.10 ms/logical tick** (0.05 ms per 1/60 substep)
  over 300 ticks x 4 driven bodies — ~330x inside the 33 ms 30 Hz
  budget. Assertion bound: < 30 ms/tick.
- Stepping is allocation-free after warmup: wheel raycast hits,
  contact points and force buffers are preallocated per body.

### Handling (qualification outcomes, all reproducible)

- **Acceleration/cruise**: auto-throttle tapers a drive force toward the
  authored 18 m/s cruise target — force, never velocity overwrite.
- **Braking**: from >8 m/s, a held full brake stops the car below
  0.25 m/s within 6 s; longitudinal speed never goes negative
  (worst reverse 0.0 m/s — `BrakeStopsWithoutReversing`).
- **Steering**: road-wheel target from `VehicleAssistRules.SteeringTarget`
  is rate-limited by `steerRateRadiansPerSecond` (4 rad/s) with the
  filtered value persisted in `VehiclePhysicsState.filteredSteering`
  (`SteeringIsRateLimitedAndFiltered`).
- **Tripped rollover — aggressive curb fixture**: stock `Car01`
  sliding laterally into the curb pivots over the curb line:
  `dot(bodyUp, worldUp)` falls 0.94 -> 0.52 -> 0.27 -> <0 within ~2 s.
  No flip torque or attitude assignment anywhere
  (`StockCarTripsOnAggressiveCurb`).
- **Untripped rollover — labeled high-COM preset**: comOffsetY lifted
  to +0.55, grip 1.8, roll damping off; flat-ground full-lock turn at
  16 m/s rolls the car past `dot < 0`
  (`HighComPresetRollsUntripped`).
- **Mass/torque oracles**: equal 3000 N x 0.1 s on isolated bodies —
  1000 kg gains 0.3 m/s, 2000 kg gains 0.15 m/s (±0.005); an
  off-center force changes angular velocity, a CoM force does not
  (`EqualForceRespectsMass`, `OffCenterForceChangesAngularVelocity`
  — drag, assists, ground and tire limits all disabled).
- **Frozen bodies**: a `simulateMotion=false` request keeps the body
  kinematic at its spawn pose while an active car drives through its
  slot — non-interfering (`FrozenBodyDoesNotMoveOrInterfere`).
- **Capture/restore**: `Capture` returns a deep copy; `Restore` resets
  pose, wheel compression history, steering filter, episode bookkeeping
  and `recoveryEpoch` deterministically
  (`RestoreAndCaptureRoundTripControllerHistoryAndEpoch`).
- **Lifecycle**: create/dispose cycles leave `scene.isLoaded == false`
  and `SceneManager.sceneCount` unchanged
  (`SceneLifecycleDoesNotLeak`).

## Reproducible fixture geometry

All fixtures are primitive `BoxCollider` geometry on a dedicated local
`PhysicsScene` (created per test by `PhysicsTestWorld`; collision-only,
no renderers in the sim scene).

| Fixture | Geometry (m) | Input |
|---|---|---|
| Flat ground | box top at y=0, 200-300 m square | auto-throttle `Drive()` |
| Accel/brake lane | flat ground 60 x 300 | 120 ticks drive, then `brake:1` x 180 ticks |
| Trip curb | 1.0 wide x 0.35 tall x 120 long at x=-1.0 | spawn (7, SpawnY, -10) heading 0, forward 6 m/s plus authored lateral slide vx=-22 m/s; `Drive()` 300 ticks |
| High-COM roll | flat ground 200 m square; preset `comOffsetY=0.55`, `frictionCoefficient=1.8`, `steerReferenceSpeed=60`, `rollDamping=0` | spawn heading 0, 16 m/s, `steering:1` x 300 ticks |
| Load lane | flat ground 80 x 200 at z=50 | 4 cars, spawn x = -9..+9 step 6, `Drive()` x 120 + 300 timed |

The developer lab mirrors these plus a slope, slalom posts, an
ordinary-turn skidpad and a jump ramp — see below.

## Developer car laboratory

`Editor/VehiclePhysicsSetup.cs` (menu **FamilyRacing > Physics Lab
Setup**, also invoked from `FoundationProjectSetup.Create`) generates,
only under `Assets/FamilyRacing/Generated/Physics/`:

- `car-physics-01.asset` / `car-physics-highcom-01.asset` —
  `VehiclePhysicsTuningAsset` copies of the Core presets (stock and the
  labeled high-COM roll preset).
- `car-physics-01-vehicle.asset` — `VehicleDefinitionAsset` identical to
  `car-01` except `simulationBackend = PhysicalAssisted` (opt-in).
- `PhysicsLabCourse.prefab` — flat 30 x 300 m lane, 100 m skidpad,
  -14 deg slope, six slalom posts at +-4 m, the 0.45 m trip curb and an
  -18 deg jump ramp.
- `PhysicsLab.unity` — a developer-playable scene (`PhysicsLabWorld`
  component + light + camera). Deliberately **not** added to build
  settings; opened by hand or validation tooling.

No files under `Generated/Driving/` or other generated folders are
written by the physics setup.

## Architecture notes

- `Core/Driving/IVehicleSimulation.cs` — Unity-free seam:
  `Initialize` / `Step` / `Restore` / `Capture`, `IDisposable`, with
  `VehicleSpawn`, `VehicleTickRequest`, `VehiclePathSample`,
  `VehicleTickResult` DTOs.
- `Runtime/Driving/PhysicsVehicleSimulation.cs` — batch prevalidation
  (rejection is atomic; a bad request never partially mutates the
  roster), shared substep orchestration, contact event collection in
  stable order (no recursive stepping, no rules mutation from
  callbacks), `SimulatedSeconds`/`TicksSimulated`/`SubstepsSimulated`
  counters.
- `Runtime/Driving/PhysicsCourseWorld.cs` — owns a local `PhysicsScene`;
  course clones are collider-only; synchronous `UnloadScene` on dispose
  (async unload would defer destruction past the leak-free contract;
  the obsolete-API pragma is deliberate).
- `Runtime/Driving/PhysicsVehicleBody.cs` — primitive chassis box,
  collider-computed `inertiaTensor` x tuning multipliers, authored COM
  offset, no rotation-axis freeze, moderate chassis friction (0.5) so
  curbs can trip the body; `ApplyRestore`/`ReadBack` keep MotorState
  and rigidbody in lockstep.
- `Runtime/Driving/WheelContactSolver.cs` — per-substep: raycast down
  each suspension axis -> nonnegative spring/damper support ->
  contact-point velocity -> combined mu*N tire-force limit ->
  `AddForceAtPosition(ForceMode.Force)`; then drag, rolling resistance,
  optional anti-roll coupling and bounded roll damping.
- `Runtime/Driving/PhysicsContactProbe.cs` — MonoBehaviour contact
  forwarder feeding the ordered event collector.

## Concerns / open items

- Pre-existing PlayMode failure
  `NetworkPredictionPlayTests.ImpairedLinkExercisesPredictionAndInterpolation`
  (LAN socket bind failure in this environment) persists on the same
  tree with and without this change; physics tests all pass.
- `SceneManager.UnloadScene` is obsolete in Unity 6; kept deliberately
  (pragma-suppressed) because the Dispose contract requires synchronous
  teardown — `UnloadSceneAsync` defers destruction a frame.
- Human/tablet feel rows (real-player handling judgement on device)
  remain pending per the brief; this report covers measured
  technical-prototype outcomes only.
