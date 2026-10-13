using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Driving
{
    // Which simulation backend advances a vehicle. Legacy content keeps
    // Arcade; PhysicalAssisted is opt-in and requires a validated
    // VehiclePhysicsTuning on the owning VehicleTuning. The enum lives on
    // the simulation seam: both the per-vehicle selection
    // (VehicleTuning.simulationBackend) and the race-level selection
    // (DrivingContentCatalog.SimulationBackend) share it, and the runtime
    // — never Core — maps it to a concrete IVehicleSimulation.
    public enum VehicleSimulationBackend
    {
        Arcade = 0,
        PhysicalAssisted = 1
    }

    // One vehicle joining a physical simulation. `tuning` is the full
    // VehicleTuning envelope — its `physics` member carries the validated
    // SI tuning the backend consumes and must select PhysicalAssisted.
    // `initialState` supplies the spawn pose/velocities; a null `physics`
    // member means the pose is derived from the legacy x/y/z/heading/speed
    // fields and all controller/wheel histories start empty.
    public sealed class VehicleSpawn
    {
        public string participantId;
        public VehicleTuning tuning;
        public MotorState initialState;
    }

    // An authoritative body reset the rule layer asks the simulation to
    // apply. `state` is the post-reset MotorState (pose, velocities and
    // the `recovering` flag) as the rules committed it — the coordinator
    // routes it to IVehicleSimulation.Restore, never through a stepped
    // request, so a reset can never be mistaken for a movement segment.
    public sealed class VehicleReset
    {
        public string participantId;
        public MotorState state;
    }

    // One racer's demand for a single logical interval. `rulesBefore`
    // supplies logical motor facts owned by the rule layer (boost economy,
    // drift flags, recovery bookkeeping, tick): it is merged into the
    // returned state but never overwrites the live body pose. A null
    // rulesBefore merges nothing. `simulateMotion` false marks a frozen
    // body (disabled/countdown/finished): it does not move and does not
    // interfere with active racers, but still reports its state.
    // `reset`, when non-null, carries a pending/manual reset the
    // coordinator applies to the body BEFORE the batch runs — such a
    // request is always frozen, so the reset tick never manufactures a
    // crossing.
    public sealed class VehicleTickRequest
    {
        public string participantId;
        public DriverInput input;
        public MotorState rulesBefore;
        public bool simulateMotion = true;
        public VehicleReset reset;
    }

    // A pose sampled inside one Step's two fixed substeps: offsetSeconds
    // is 0 (pre-step), 1/60 (after substep one) or 1/30 (after substep
    // two). Rule consumers evaluate gate crossings on the two
    // chronological segments, not the end-to-end chord.
    public struct VehiclePathSample
    {
        public float offsetSeconds;
        public MotorPose pose;
    }

    // One racer's result for a stepped interval. `step` carries the merged
    // MotorState plus ordered transient events; `path` is the three
    // substep-boundary poses (frozen/ rejected bodies report the unchanged
    // pose at every sample).
    public sealed class VehicleTickResult
    {
        public string participantId;
        public MotorStepResult step;
        public VehiclePathSample[] path;
    }

    // The physical-backend seam the rule layer steps once per logical
    // interval. Implementations own one authoritative world; Unity-free
    // callers only see these DTOs.
    //
    // Semantics (binding — see the design spec's "Simulation and rule
    // boundary" section):
    // - Step is exactly one logical interval (1/30 s), i.e. two fixed 1/60
    //   substeps of the shared world, always — never an arbitrary dt and
    //   never per-racer stepping. Forces for every active body are applied
    //   before each single shared simulate.
    // - The whole request batch is prevalidated before any body mutates;
    //   an invalid request yields a rejected result for that participant
    //   and cannot partially move others.
    // - Disabled/countdown/finished bodies (simulateMotion=false) are
    //   frozen and non-interfering.
    // - Restore updates body pose, all explicit controller histories,
    //   sleep/contact bookkeeping and the recovery epoch, and emits no
    //   gameplay events. Capture returns an independent deep copy.
    // - Collision callbacks are collected during simulation and exposed
    //   in stable order afterwards; they never recursively step the world
    //   or mutate race rules from inside a callback.
    public interface IVehicleSimulation : IDisposable
    {
        // (Re)configures the world roster. Every spawn is validated as a
        // batch before any body exists; a re-initialize replaces the
        // previous roster atomically.
        void Initialize(IReadOnlyList<VehicleSpawn> vehicles);

        // Advances exactly one logical interval (two 1/60 substeps) and
        // returns one result per request in request order.
        IReadOnlyList<VehicleTickResult> Step(IReadOnlyList<VehicleTickRequest> requests);

        // Authoritative reset of one body (recovery/spawn/prediction
        // restore). Restores pose, velocities, wheel and controller
        // histories, episode bookkeeping and recoveryEpoch from
        // state.physics when present, or derives them from the legacy
        // fields otherwise. No gameplay events are emitted.
        void Restore(string participantId, MotorState state);

        // An independent deep copy of one participant's merged state;
        // mutating the result can never touch the live body. Returns null
        // for an unknown participant.
        MotorState Capture(string participantId);
    }
}
