# Physical vehicles with driving assists

Date: 2026-10-13. Status: proposed design accompanying the requested AI-agent implementation plan. This planning task does not implement or certify the feature.

## Intent and scope

Replace the current car and motorcycle movement model with force-driven vehicles: physical mass, rotational inertia, engine force, braking, tire grip, suspension, weight transfer, airborne rotation, and collisions. Cars must be able to overturn. Motorcycles must lean and balance automatically while remaining capable of falling. Preserve an approachable family racing game for ages five and older.

The user requested a plan for this change after discussing real physics with driving assists, a car prototype first, and subsequent motorcycle and multiplayer integration. The following are proposed implementation defaults, not separately expressed user preferences: automatic acceleration remains the normal control scheme; add a held brake; use a common raycast-wheel force model; retain the existing logical/network tick rates; enable the new backend only after qualification. Numeric tuning and engineering acceptance targets below are starting targets, not measured results.

Deliver a working car, a working motorcycle, solo/tutorial integration, physical racer collisions, LAN authority and prediction, observable state, and repeatable verification. Defer manual gearbox/clutch, tire temperature/wear/deformation, rider ragdolls, damage simulation, new art, new tracks beyond validation fixtures, new progression systems, and implementing currently unavailable gadgets. Provide an explicit throttle override through the existing input command for agents/AI and experiments; a manual-driving settings screen is outside this scope.

## Existing implementation and implications

Inspected working tree: `c3e15dd`, branch `fix/driving-playtest-1`, with pre-existing uncommitted changes on 2026-10-13. Reinspect before execution; current files take precedence over historical reports.

- `Core/Driving/ArcadeMotor.cs` directly advances heading/speed, uses swept contacts and controlled gravity, and injects lateral drift. `MotorState.cs` carries yaw and speed components, without a physical attitude or angular velocity.
- `Runtime/Driving/VehiclePresentation.cs` applies yaw to the root and cosmetic lean to a child. These visuals have no physical colliders.
- `Core/Racing/DrivingSession.cs` invokes `ArcadeMotor.Step` inside each session. `NetworkRace.cs` steps racers sequentially with artificial nudges. Physical racers instead need forces applied to all bodies before advancing their shared world.
- `Runtime/FoundationApp.cs` owns the 30 Hz accumulator and observation boundary. `Runtime/Networking/NetworkRaceBridge.cs` controls host stepping, client input, prediction, and replication.
- `Core/Networking/ClientPrediction.cs` replays `ArcadeMotor.Step`; changing only the visible vehicle or host motor would leave clients using the wrong simulation.
- `RecoveryRules.cs` currently treats long airtime as off-course and prolonged stationary ground contact as beached. Neither is sufficient once braking, deliberate stops, and longer physical jumps exist.
- `DriverInput` has no throttle/brake fields. Keyboard, touch, automation, wire frames, AI, snapshots, and lease expiry must agree on the new controls.

Source paths above are relative to `Assets/FamilyRacing/`. The implementation plan supplies exact create/modify/test paths.

## Global constraints

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

## Architecture decision

Use a dynamic Unity `Rigidbody` chassis with compound primitive colliders and explicit wheel-contact forces. Four wheel rays support a car; two support a motorcycle. Unity resolves chassis motion, inertia, body contacts, and tumbling. Unity-free functions calculate suspension, combined tire grip, drive/brake demand, steering limits, and assistance. Keep the existing arcade backend available during migration and comparative testing.

This is a simplified physical vehicle model, not a complete tire/drivetrain simulator. It gives real momentum, angular motion, load transfer, and contact-driven rollover while keeping the force model inspectable and bounded.

Alternatives considered:

| Approach | Tradeoff | Decision |
|---|---|---|
| Add visual roll/flip to the arcade motor | Cheap but does not deliver the requested physical motion | Reject as the final solution |
| Rigidbody plus Unity WheelCollider | Built-in suspension, slip, motor/brake torque; useful prototype option, but internal wheel state complicates controlled replay | Keep as a documented fallback if the chosen tire model fails its prototype gate |
| Rigidbody plus explicit raycast-wheel forces | More initial force-model work; shared car/bike model and explicit controller state for capture/replay | Selected |

Do not build two production tire backends. If prototype results favor WheelCollider, record that decision and revise state/prediction tasks before continuing. Full rigidbody replacement is not a parameter change to `ArcadeMotor`.

Keep one authoritative physics world per active solo/host race. Create it as a local `PhysicsScene`, with collision-only instances of the same authored course geometry and all participating chassis. Use that scene's query APIs. Render meshes remain separate and read committed poses. Do not run a physical car against colliders that exist only in a different scene.

Unity supports force application at contact points, which also produces torque, and collider-derived inertia tensors. Fixed-step simulation is supported for local physics scenes. These APIs support the design; they do not guarantee identical replay across platforms. See [AddForceAtPosition](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Rigidbody.AddForceAtPosition.html), [inertiaTensor](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Rigidbody-inertiaTensor.html), and [PhysicsScene.Simulate](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/PhysicsScene.Simulate.html).

## Physical behavior

### Units, bodies, and tuning

Use metres, seconds, kilograms, newtons, newton-metres, and radians in simulation DTOs. Define vehicle axes as +X right, +Y up, +Z forward. Unity-facing steering/visual APIs may use degrees; convert explicitly at the boundary. Spawn/recovery poses remain position plus heading; live attitude is a normalized quaternion.

Author mass, center-of-mass offset, dimensions, wheel positions/radii, suspension travel, spring/damper rates, tire coefficients, engine/brake force, steering curve, and assistance in validated content. Compute the default inertia tensor from chassis colliders after geometry/mass changes, then optionally apply positive per-axis tuning multipliers. Do not freeze rotation axes or install an always-upright constraint.

Suggested first tuning pass:

| Parameter | Car | Motorcycle including rider |
|---|---:|---:|
| Mass | 1200 kg | 220 kg |
| Wheelbase | 2.4 m | 1.45 m |
| Track width | 1.6 m | Two inline contact points |
| Wheel radius | 0.32 m | 0.30 m |
| Suspension rest length / extra droop | 0.35 / 0.10 m | 0.30 / 0.10 m |
| Spring rate per wheel | 40,000 N/m | 18,000 N/m |
| Damper per wheel | 3,500 N s/m | 1,500 N s/m |
| Peak total drive force | 7,200 N | 1,760 N |
| Peak total brake force | 12,000 N | 2,200 N |
| Road friction coefficient | 1.1 | 1.0 |
| Soft cruise speed / boost speed | 18 / 26 m/s | 20 / 28 m/s |
| Maximum low-speed road-wheel steer | 30 degrees | 25 degrees |

These are editable starting values. Record final values, stopping distance, acceleration, cornering response, and rollover fixtures in the evidence report. Do not silently hard-code a chosen COM height: record its height above settled ground, and verify that wheel/chassis geometry, spawn clearance, and suspension sag agree. Standard gravity is 9.81 m/s² for this backend; update ramp feasibility rather than carrying over the arcade gravity of 22 m/s² unnoticed.

### Force model

Each 1/60-second substep:

1. Cast from each authored suspension mount down the chassis suspension axis. Exclude self, triggers, and non-drivable layers; reject wall-like normals. Establish longitudinal/lateral directions on the hit plane, with the front-wheel steering angle included.
2. Evaluate nonnegative normal support from spring compression and compression velocity: `N = clamp(k*x + c*xDot, 0, authoredMaxLoad)`. Invalidate compression history when contact is lost or on spawn/recovery; on first contact initialize previous compression to current compression, avoiding a false damper impulse. An authoritative prediction restore preserves the supplied valid compression history instead of resetting it.
3. Get velocity at the contact point, including chassis angular velocity. Divide total engine demand across grounded driven wheels. Derive lateral force opposing lateral contact velocity. Brake demand opposes travel; cap the braking contribution so it cannot reverse longitudinal velocity in one step. Near rest, brake hold may cancel gravity along a slope only within the tire's grip budget.
4. Apply one combined friction limit: the longitudinal/lateral force pair must fit within `mu*N` (or an explicitly authored ellipse). A tire cannot spend full grip independently on acceleration, braking, and cornering. With no contact, tire drive and braking forces are zero.
5. Apply support and tire forces at their physically consistent points using `ForceMode.Force`, then drag/rolling resistance and bounded assistance. Do not multiply a force by dt before passing it to `AddForce`. Do not overwrite linear/angular velocity to steer, hit cruise speed, or prevent rollovers.
6. Apply every racer's forces, simulate the shared scene once, then read all bodies. Repeat for substep two. Emit contacts/airborne/landing transitions from the completed substeps in stable event order.

Cruise/boost speeds are controller targets: taper engine force near the target, allow coasting and downhill overspeed, and preserve momentum. Drift reduces rear lateral grip progressively and permits controlled slip; it does not inject the old sideways velocity kick. Boost adds bounded drive demand subject to grip. Keep existing boost-charge, activation, duration, and reward semantics unless a physical eligibility rule is explicitly updated and tested.

Suspension and the tire forces create load transfer. An optional capped anti-roll spring couples left/right compression without locking body attitude. It must not produce tire grip in the air or prevent every rollover. Begin without an extra artificial downward force.

### Rollovers, bike balance, and assists

A car may slide, lift wheels, or roll depending on grip, speed, center of mass, track width, terrain, and impacts. No `steering > threshold => flip` branch or canned flip torque is allowed. Default ordinary corners should be manageable. Demonstrate a real rollover using a reproducible aggressive curb/turn fixture; use a separately labeled high-COM/high-grip fixture to demonstrate untripped corner rollover. The stock car need not flip on every flat sharp corner.

Car assistance: reduce maximum steering angle as speed rises, rate-limit steering changes, and offer bounded roll damping/anti-roll force. Assistance cannot directly author heading or cancel all contact-induced rotation. No automatic navigation through normal corners.

Motorcycle: use a combined bike/rider body and front/rear wheel contacts. Target lean from turn demand and speed, bounded initially to 45 degrees; a proportional/derivative torque controller follows it. Include low-speed balance assistance so a stationary bike is usable. Assistance is contact-aware, capped, and disabled beyond a 70-degree fall angle until ordinary recovery; it cannot levitate an airborne bike upright. Airborne orientation follows inertia with damping only, without a world-upright spring. A hard lateral collision or excess grip demand must still allow a fall. Rendering rider lean must not double-apply the physical chassis roll.

### Controls and recovery

- Preserve automatic throttle by default. `input.set` may specify `throttle` in [0,1] and `brake` in [0,1]. Missing throttle means automatic throttle, including default/cleared input; explicit throttle zero means coast. Represent that distinction explicitly, not with an ambiguous zero-initialized float.
- Any positive brake suppresses engine/boost drive force while braking. Boost activation is refused while braking; an already active boost follows the existing drain/end policy but supplies no drive force. Brake release resumes normal auto-throttle. Braking does not engage reverse.
- Keyboard brake: S or Down. Touch: an additional held brake control. Keep existing steering/drift/boost/gadget/recovery/camera/pause mappings and command parity. Combine keyboard/touch brake using max; steering keeps existing opposing-input cancellation. An explicit throttle override uses the lower requested value when both sources override; an absent source does not cancel a present override.
- Preserve input lease bounds, focus/cancel/disconnect cleanup and input ownership. Malformed/non-finite command parameters reject atomically. Local constructor sanitization remains defensive. No gameplay forces during countdown/pause/terminal states.
- Manual recovery remains R/the existing action, once per command/press edge. Add auto-recovery after continuous overturned support/contact for 2 seconds. A bike fall uses the same recovery mechanism. Emit the reason and increment a recovery epoch.
- Keep kill-height recovery; add explicit authored course recovery bounds with a 1.5-second outside-bounds grace. Airtime inside the permitted jump volume alone is not failure. Preserve stuck recovery only when drive is requested and progress is blocked. Deliberately braking or coasting to a stop must not trigger beached recovery.
- Recover to the last accepted safe pose; clear all linear/angular velocity, wheel/controller histories, and transient drive/drift effects; preserve boost charge. Check spawn clearance and exclude the teleport from checkpoints, landing rewards, and physical contact events. If a racer blocks the safe pose, use deterministic authored nearby safe candidates; do not spawn overlapping bodies.

## Simulation and rule boundary

Core remains authoritative for race phase, clocks, actions, gates, laps, recovery policy, AI, boost economy, and results. Runtime supplies physical advancement behind `IVehicleSimulation`. Keep `MotorState` as the compatibility/rule envelope with an optional full `VehiclePhysicsState`; legacy x/y/z, heading and speed fields are derived from physical state, never an independent physical authority.

At each logical tick: validate/sample all inputs; prepare all per-racer requests and pending resets; apply resets; apply all forces and run two shared substeps; complete all racer rules from their returned paths; apply any rule-requested recovery resets; publish one committed boundary. A world is stepped once per substep regardless of racer count. Checkpoint evaluation uses the two chronological movement segments, excluding resets, so a curved/fast path is not replaced by a misleading end-to-end chord. Timer advancement remains once per 30 Hz tick, never once per physics substep.

The 30 Hz accumulator remains the sole race clock. Do not add another force loop in `FixedUpdate` or use render delta time for physical integration. Local scenes are manually advanced only when eligible. Paused network transport, command leases, and capture wall time continue. Backlog is measured and bounded by the existing frame work budget; do not enlarge dt or silently claim unprocessed physics ticks happened. Normal simulation must allocate no managed objects per wheel/substep after warmup.

Snapshot physical attitude, world velocity, angular velocity, per-wheel suspension/contact state, filtered steering, assistance state, airborne/collision episode state, and recovery epoch. Copy/restore all controller histories required by replay. Expose tuning/content identity and use finite/bounds validation before anything reaches PhysX. On a runtime invalid-state fault, publish a diagnostic and recover safely without awarding progress.

## LAN authority, prediction, and compatibility

Host simulates all four colliding chassis in one world; remove the old artificial racer nudge for that backend. Input messages never let a client set mass, force limits, pose, or ownership. Maintain authenticated ownership epochs and input sequence windows.

Predict only the local physical vehicle in a separate client local physics scene containing static course colliders. Remote display vehicles do not collide in that scene; authoritative host contacts correct the local prediction. This intentionally does not predict mutual racer collisions. Qualify those corrections explicitly. Do not pretend a kinematic remote proxy has the same collision response as the host's dynamic body.

On an authoritative snapshot, restore body and explicit controller state at that acknowledged boundary, discard acknowledged inputs, replay remaining input through the same force model and two substeps, and suppress all replay side effects. PhysX contact caches are not completely represented by these DTOs; replay is approximate. Judge convergence and user-visible corrections, not cross-platform bitwise identity. NGO does not provide this loop automatically; see its [prediction/anticipation documentation](https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.7/manual/advanced-topics/client-anticipation.html).

Bound history to 120 logical inputs and replay work to 12 logical ticks per rendered frame across all corrections processed in that frame. Coalesce incoming snapshots to the newest valid authoritative boundary before reconciliation. If a correction requires more than the remaining replay budget or matching history is absent, clear prediction, request/resume from a fresh authoritative state, and expose the resync. Never run a 120-tick PhysX burst on a tablet. Start with existing position policy (3 m hard snap, 0.1-second visual blend); add a 45-degree orientation snap threshold. Recovery, ownership change, and new race always snap and clear old history. Smooth only rendered position/quaternion, never the authoritative body. Interpolate remote attitude by quaternion slerp; retain 0.1-second extrapolation cap.

Provisional no-contact correction targets after warmup: on ordinary LAN, p95 position error <=0.25 m and p95 attitude error <=5 degrees; with 100 ms RTT, +/-30 ms jitter and 2% loss, <=0.75 m and <=12 degrees. These are engineering targets to validate, not existing promises. Contact trials separately require convergence within 0.5 seconds after separation, no duplicate effects, and a human assessment of collision feel. If targets fail, improve replay/state/clock alignment before enabling physics LAN by default.

For the proposed protocol revision: LAN version 2, envelope magic `FRN2`, gameplay version `gameplay-2`, state schema 4, and content identity `physics-1` (plus a canonical tuning/course fingerprint for generated data). Typed packet tags become `P1IB`, `P1SS`, `P1EB`, `P1RS`. Update discovery/join and all consumers together; reject legacy peers before slot allocation. Keep command envelope version 1 if only discoverable additive parameters change; update discovery schemas and scenario validators. Profile schema is independent and need not change. Preserve the 16 KiB snapshot and 4 KiB input limits; use a compact wire DTO containing replay-required state if full diagnostic wheel data will not fit.

## Staged acceptance and rollout

1. **Car laboratory:** a generated flat/sloped/curb/ramp fixture and physical car prove force, braking, suspension, cornering, rollover, and fixed-step behavior. This is a developer-selectable backend; existing gameplay stays usable. Capture an early four-body load measurement. Human feel and tablet performance may remain pending if access is unavailable.
2. **Solo integration:** car/bike, controls, camera, tutorial, drift/boost/jumps, recovery and progress work through real commands. Three consecutive laps per class on the current course without invalid state or unintended recovery; human handling review remains separately recorded.
3. **LAN integration:** protocol, host collisions, local prediction, rejoin, shared pause and results meet existing lifecycle gates plus the physics correction targets.
4. **Qualification/default switch:** Windows development and ordinary builds, offline Android build, 30-minute soak, impaired-network trials, and the actual device/human matrix. Keep physical-device gates explicitly open when equipment is absent. Enable the physical backend as the shipped default only after the required gates pass.

The existing milestone reports are historical evidence for the arcade implementation, not evidence for the new physics. Preserve them. Record the new milestone and any changed `GAME_PLAN.md` commitments explicitly. Any fallback to arcade is a session-wide backend/content choice with a new race, never a silent mid-race or per-client switch.
