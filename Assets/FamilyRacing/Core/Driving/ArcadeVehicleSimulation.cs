using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Driving
{
    // The IVehicleSimulation implementation that wraps the legacy
    // kinematic motor: every active request is one ArcadeMotor.Step over
    // the shared IMotorWorld, and the artificial racer-contact nudges are
    // rebuilt each batch through RacerContacts from the single pre-step
    // body view — exactly the semantics the old per-racer coordinator
    // path produced, so arcade behaviour is bit-for-bit preserved.
    // Physical mode deliberately never uses this fabric (real chassis
    // collision replaces it).
    //
    // Boundary contract (same as every backend): the whole request batch
    // is prevalidated before any body mutates; frozen requests
    // (countdown/manual-reset/finished) report their unchanged state and
    // still act as contact sources, matching the old coordinator, which
    // included every unfinalized racer in the contact set. A reset the
    // coordinator applied through Restore lands in the stored state
    // before any request reads it.
    public sealed class ArcadeVehicleSimulation : IVehicleSimulation
    {
        private sealed class Body
        {
            internal VehicleTuning tuning;
            internal MotorState state;
        }

        private readonly IMotorWorld world;
        private readonly Dictionary<string, Body> bodies =
            new Dictionary<string, Body>(StringComparer.Ordinal);
        private readonly List<VehicleTickResult> results =
            new List<VehicleTickResult>();
        private bool initialized;
        private bool disposed;

        public ArcadeVehicleSimulation(IMotorWorld world)
        {
            this.world = world;
        }

        public IMotorWorld World { get { return world; } }

        // Roster setup mirrors the physical backend: whole-batch
        // validation before any body exists; re-initialize replaces the
        // previous roster atomically. Arcade bodies are pure MotorState
        // records — no Unity objects — so Initialize never throws after
        // validation succeeds.
        public void Initialize(IReadOnlyList<VehicleSpawn> vehicles)
        {
            if (vehicles == null) vehicles = new List<VehicleSpawn>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < vehicles.Count; i++)
            {
                var spawn = vehicles[i];
                if (spawn == null || string.IsNullOrEmpty(spawn.participantId))
                    throw new ArgumentException(
                        "VehicleSpawn[" + i + "]: participantId required");
                if (!seen.Add(spawn.participantId))
                    throw new ArgumentException(
                        "Duplicate participant: " + spawn.participantId);
                if (spawn.tuning == null)
                    throw new ArgumentException(
                        spawn.participantId + ": tuning required");
                string tuningError;
                if (!spawn.tuning.Validate(out tuningError))
                    throw new ArgumentException(spawn.participantId
                        + ": invalid tuning field " + tuningError);
                if (spawn.initialState == null)
                    throw new ArgumentException(spawn.participantId
                        + ": initialState required");
                if (!Finite(spawn.initialState.x)
                    || !Finite(spawn.initialState.y)
                    || !Finite(spawn.initialState.z)
                    || !Finite(spawn.initialState.heading)
                    || !Finite(spawn.initialState.speed)
                    || !Finite(spawn.initialState.lateralSpeed)
                    || !Finite(spawn.initialState.verticalSpeed))
                    throw new ArgumentException(spawn.participantId
                        + ": non-finite initial pose");
            }
            bodies.Clear();
            foreach (var spawn in vehicles)
                bodies[spawn.participantId] = new Body
                {
                    tuning = spawn.tuning,
                    state = spawn.initialState.Copy()
                };
            initialized = true;
        }

        // One logical interval per request — ArcadeMotor's own 1/30 step.
        // The batch is fully validated before the first motor runs, so a
        // structurally broken request can never partially move the healthy
        // roster; the contact fabric freezes the pre-step view before any
        // body advances.
        public IReadOnlyList<VehicleTickResult> Step(
            IReadOnlyList<VehicleTickRequest> requests)
        {
            if (disposed) throw new ObjectDisposedException(
                nameof(ArcadeVehicleSimulation));
            if (!initialized) Initialize(null);
            results.Clear();

            var count = requests != null ? requests.Count : 0;
            var errors = new string[count];
            var befores = new MotorState[count];
            var actives = new bool[count];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var request = requests[i];
                Body body = null;
                if (request == null
                    || string.IsNullOrEmpty(request.participantId))
                {
                    errors[i] = "invalidRequest";
                }
                else if (!seen.Add(request.participantId))
                {
                    errors[i] = "duplicateParticipant";
                }
                else if (!bodies.TryGetValue(request.participantId,
                    out body) || body == null)
                {
                    errors[i] = "unknownParticipant";
                }
                else
                {
                    // The request's rule state is the step source, exactly
                    // like the old session->motor hand-off; a request with
                    // no rulesBefore steps from the stored body instead.
                    befores[i] = request.rulesBefore ?? body.state;
                    if (!Finite(befores[i].x) || !Finite(befores[i].y)
                        || !Finite(befores[i].z) || !Finite(befores[i].heading)
                        || !Finite(befores[i].speed)
                        || !Finite(befores[i].lateralSpeed)
                        || !Finite(befores[i].verticalSpeed))
                        errors[i] = "invalidRulesBefore";
                    else
                        actives[i] = request.simulateMotion;
                }
            }

            // The contact fabric for this batch: every valid participant
            // (frozen included) offers its pre-step body — the same set
            // the old coordinator's BuildContacts produced for the
            // unfinalized roster.
            var entries = new List<RacerContacts.Entry>(count);
            for (var i = 0; i < count; i++)
                if (errors[i] == null)
                    entries.Add(new RacerContacts.Entry
                    {
                        participantId = requests[i].participantId,
                        motor = befores[i]
                    });
            var contacts = new RacerContacts(entries);

            for (var i = 0; i < count; i++)
            {
                var request = requests[i];
                var result = new VehicleTickResult
                {
                    participantId = request != null
                        ? request.participantId ?? "" : ""
                };
                if (errors[i] != null)
                {
                    result.step = MotorStepResult.Rejected(
                        befores[i], errors[i]);
                    result.path = FixedPath(
                        befores[i] != null ? befores[i].Pose
                            : new MotorPose());
                }
                else if (!actives[i])
                {
                    // Frozen: report the unchanged state; the body stays
                    // where Restore/the last step left it. Rule-side
                    // fields still sync so recovery flags and bookkeeping
                    // are observable through Capture.
                    result.step = new MotorStepResult
                    {
                        state = befores[i].Copy()
                    };
                    result.path = FixedPath(befores[i].Pose);
                    var stored = bodies[request.participantId];
                    if (stored != null) stored.state = befores[i].Copy();
                }
                else
                {
                    var body = bodies[request.participantId];
                    var step = ArcadeMotor.Step(befores[i], request.input,
                        body.tuning, world, 1f / 30f,
                        contacts.ForParticipant(request.participantId));
                    result.step = step;
                    var end = step != null && step.state != null
                        ? step.state.Pose : befores[i].Pose;
                    result.path = step != null && step.rejected
                        ? FixedPath(befores[i].Pose)
                        : MovementPath(befores[i].Pose, end);
                    if (step != null && step.state != null)
                        body.state = step.state;
                }
                results.Add(result);
            }
            return results;
        }

        // Authoritative reset of one body (recovery/spawn/prediction
        // restore): the stored state is replaced atomically after finite
        // validation, and no events are emitted — same contract as the
        // physical backend's Restore.
        public void Restore(string participantId, MotorState state)
        {
            Body body;
            if (participantId == null || state == null
                || !bodies.TryGetValue(participantId, out body)
                || body == null)
                return;
            if (!Finite(state.x) || !Finite(state.y) || !Finite(state.z)
                || !Finite(state.heading) || !Finite(state.speed)
                || !Finite(state.lateralSpeed)
                || !Finite(state.verticalSpeed))
                return;
            body.state = state.Copy();
        }

        public MotorState Capture(string participantId)
        {
            Body body;
            return participantId != null
                && bodies.TryGetValue(participantId, out body)
                && body != null
                ? body.state.Copy()
                : null;
        }

        public void Dispose()
        {
            disposed = true;
            bodies.Clear();
        }

        // A frozen/rejected body reports the identical pose at all three
        // samples — rule consumers see zero movement segments.
        internal static VehiclePathSample[] FixedPath(MotorPose pose)
        {
            var path = new VehiclePathSample[3];
            path[0] = new VehiclePathSample
                { offsetSeconds = 0f, pose = pose };
            path[1] = new VehiclePathSample
                { offsetSeconds = 1f / 60f, pose = pose };
            path[2] = new VehiclePathSample
                { offsetSeconds = 1f / 30f, pose = pose };
            return path;
        }

        // The kinematic motor computes one end pose per interval: its
        // two chronological segments collapse to (before -> end, end ->
        // end), which evaluates identically to the old single-chord
        // crossing test.
        internal static VehiclePathSample[] MovementPath(MotorPose before,
            MotorPose end)
        {
            var path = new VehiclePathSample[3];
            path[0] = new VehiclePathSample
                { offsetSeconds = 0f, pose = before };
            path[1] = new VehiclePathSample
                { offsetSeconds = 1f / 60f, pose = end };
            path[2] = new VehiclePathSample
                { offsetSeconds = 1f / 30f, pose = end };
            return path;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
