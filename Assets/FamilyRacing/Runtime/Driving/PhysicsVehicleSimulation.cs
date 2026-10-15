using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using UnityEngine;

namespace FamilyRacing
{
    // The IVehicleSimulation implementation over a dedicated local
    // PhysicsScene (PhysicsCourseWorld). Step is exactly one logical
    // interval — two fixed 1/60 substeps, shared by every active body —
    // always; there is no per-racer stepping and no force work in render
    // Update or FixedUpdate. The request batch is prevalidated before any
    // body mutates; frozen requests (disabled/countdown/finished or a
    // flagged recovery boundary) produce non-interfering kinematic
    // ghosts; collision callbacks are collected during Simulate and only
    // turned into ordered MotorEvents afterwards.
    public sealed class PhysicsVehicleSimulation : IVehicleSimulation,
        IVehicleSimulationDiagnostics
    {
        public const float TickSeconds = 1f / 30f;
        public const float SubstepSeconds = 1f / 60f;
        // An airborne episode shorter than this does not earn a landed
        // event: suspension flicker over a lip is not a jump.
        private const float LandedMinAirSeconds = 0.10f;

        private sealed class Plan
        {
            public VehicleTickRequest request;
            public PhysicsVehicleBody body;
            public string error;
            public bool active;
        }

        private readonly PhysicsCourseWorld world;
        private readonly Dictionary<string, PhysicsVehicleBody> bodies =
            new Dictionary<string, PhysicsVehicleBody>(StringComparer.Ordinal);
        private readonly List<PhysicsVehicleBody> ordered =
            new List<PhysicsVehicleBody>();
        private readonly List<Plan> plan = new List<Plan>();
        private readonly List<VehicleTickResult> results =
            new List<VehicleTickResult>();
        // Per-substep pose staging for the three-sample path.
        private readonly List<MotorPose> substepPoses = new List<MotorPose>();
        private bool disposed;
        private bool initialized;
        private double lastStepSeconds;
        private double totalStepSeconds;
        private long invalidStateFaults;

        public PhysicsVehicleSimulation(PhysicsCourseWorld courseWorld)
        {
            world = courseWorld
                ?? throw new ArgumentNullException(nameof(courseWorld));
        }

        public PhysicsCourseWorld World { get { return world; } }
        public long TicksSimulated { get; private set; }
        public long SubstepsSimulated { get; private set; }
        public double SimulatedSeconds
        {
            get { return TicksSimulated * (1.0 / 30.0); }
        }

        // Observable cost/shape facts for the state stream (Task 11):
        // the tick/substep counters, live body roster, wall-clock step
        // cost, and the invalid-state refusal count. An independent
        // snapshot each call — the diagnostic never crosses the LAN wire.
        public VehicleSimulationDiagnostics Diagnostics
        {
            get
            {
                return new VehicleSimulationDiagnostics
                {
                    backend = "physicalAssisted",
                    ticksSimulated = TicksSimulated,
                    substepsSimulated = SubstepsSimulated,
                    bodies = ordered.Count,
                    lastStepSeconds = lastStepSeconds,
                    totalStepSeconds = totalStepSeconds,
                    invalidStateFaults = invalidStateFaults
                };
            }
        }

        // --- test/lab seams -------------------------------------------------
        // Deterministic force injection for the mass/torque oracles: the
        // force is applied every substep until cleared. World-frame; the
        // offset variant applies at a body-local offset to exercise torque.
        public void DebugSetForce(string participantId, Vector3 force)
        {
            var body = Find(participantId);
            if (body == null) return;
            body.debugForceActive = true;
            body.debugForce = force;
            body.debugForceAtOffset = false;
        }

        public void DebugSetForceAtOffset(string participantId,
            Vector3 force, Vector3 localOffset)
        {
            var body = Find(participantId);
            if (body == null) return;
            body.debugForceActive = true;
            body.debugForce = force;
            body.debugForceAtOffset = true;
            body.debugForceLocalOffset = localOffset;
        }

        public void DebugClearForces(string participantId)
        {
            var body = Find(participantId);
            if (body == null) return;
            body.debugForceActive = false;
            body.debugForceAtOffset = false;
        }

        // Observation only: the lab and tests read COM height / up vectors
        // from the real body. Never a write path back into the sim.
        public Rigidbody DebugRigidbody(string participantId)
        {
            var body = Find(participantId);
            return body == null ? null : body.body;
        }

        private PhysicsVehicleBody Find(string participantId)
        {
            PhysicsVehicleBody body;
            return participantId != null
                && bodies.TryGetValue(participantId, out body)
                ? body
                : null;
        }

        // --- roster ---------------------------------------------------------
        // Whole-batch validation before any body exists: a bad spawn can
        // never partially populate the world. Re-initialization replaces
        // the previous roster atomically.
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
                if (spawn.tuning.physics == null)
                    throw new ArgumentException(spawn.participantId
                        + ": physical backend requires tuning.physics");
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
                if (spawn.initialState.physics != null
                    && !spawn.initialState.physics.Validate(out var stateError))
                    throw new ArgumentException(spawn.participantId
                        + ": invalid initial physics field " + stateError);
            }
            ClearBodies();
            foreach (var spawn in vehicles)
            {
                var body = new PhysicsVehicleBody(world, spawn);
                bodies.Add(spawn.participantId, body);
                ordered.Add(body);
            }
            initialized = true;
        }

        private void ClearBodies()
        {
            foreach (var body in ordered)
                if (body != null && body.body != null)
                    UnityEngine.Object.DestroyImmediate(body.body.gameObject);
            bodies.Clear();
            ordered.Clear();
        }

        // --- the logical interval -------------------------------------------
        // One Step = two shared 1/60 substeps, always. The batch is fully
        // planned before a single force or pose is touched: per-request
        // rejection is atomic, and a structurally broken request can never
        // partially move the healthy roster.
        public IReadOnlyList<VehicleTickResult> Step(
            IReadOnlyList<VehicleTickRequest> requests)
        {
            if (disposed) throw new ObjectDisposedException(
                nameof(PhysicsVehicleSimulation));
            if (!initialized)
                Initialize(null);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            TicksSimulated++;
            results.Clear();
            BuildPlan(requests);

            // path[0]: pre-step poses for every participant in the plan.
            substepPoses.Clear();
            foreach (var entry in plan)
            {
                substepPoses.Add(entry.body == null
                    ? new MotorPose()
                    : entry.body.PoseNow());
                // Rule facts merge BEFORE forces: boost state suppresses
                // drive demand inside this interval's force application.
                if (entry.body != null && entry.error == null)
                    entry.body.MergeRules(entry.request.rulesBefore);
            }

            // Two shared substeps: every active body's forces, then ONE
            // scene simulate; never a per-racer world.
            var sampleAt = new float[] { 0f, SubstepSeconds, TickSeconds };
            var samples = new List<MotorPose>[plan.Count];
            for (var i = 0; i < plan.Count; i++)
            {
                samples[i] = new List<MotorPose>(3);
                samples[i].Add(substepPoses[i]);
            }
            for (var substep = 0; substep < 2; substep++)
            {
                foreach (var entry in plan)
                {
                    if (!entry.active) continue;
                    WheelContactSolver.ApplySubstepForces(entry.body,
                        entry.request.input, SubstepSeconds, world,
                        entry.request.modifiers);
                }
                world.Simulate(SubstepSeconds);
                SubstepsSimulated++;
                for (var i = 0; i < plan.Count; i++)
                {
                    var entry = plan[i];
                    if (!entry.active) continue;
                    entry.body.ProcessContacts();
                    entry.body.ReadBack(SubstepSeconds);
                    samples[i].Add(entry.body.PoseNow());
                }
                foreach (var b in ordered) b.probe.ResetFrame();
            }

            for (var i = 0; i < plan.Count; i++)
            {
                var entry = plan[i];
                var result = new VehicleTickResult
                {
                    participantId = entry.request == null
                        ? ""
                        : entry.request.participantId ?? ""
                };
                if (entry.body == null || entry.error != null)
                {
                    result.step = MotorStepResult.Rejected(
                        entry.body == null ? new MotorState() : entry.body.live,
                        entry.error ?? "unknownParticipant");
                    result.path = RejectedPath(samples[i], sampleAt);
                }
                else
                {
                    var state = entry.body.live.Copy();
                    var step = new MotorStepResult { state = state };
                    if (!entry.active)
                    {
                        // Frozen: identical pose at every path sample.
                        var pose = entry.body.PoseNow();
                        result.path = new VehiclePathSample[3];
                        for (var s = 0; s < 3; s++)
                            result.path[s] = new VehiclePathSample
                            {
                                offsetSeconds = sampleAt[s],
                                pose = pose
                            };
                    }
                    else
                    {
                        // Ordered transient events: chassis contact first
                        // (stable record order collapsed to the peak new
                        // contact), then the airborne transition, then
                        // the landing transition — arcade parity. A
                        // landing that qualifies (real airtime, real
                        // drop, no barrier clip) emits cleanLanding
                        // first carrying the authored credit; the rule
                        // layer applies it so a flagged boundary can
                        // never mint charge.
                        if (entry.body.newContactThisTick)
                            step.events.Add(new MotorEvent("contact",
                                entry.body.peakContactSpeed));
                        if (entry.body.becameAirborneThisTick)
                            step.events.Add(new MotorEvent("becameAirborne"));
                        if (entry.body.landedThisTick)
                        {
                            var envelope = entry.body.envelope;
                            if (envelope != null
                                && entry.body.landedAirSeconds
                                    >= envelope.landingMinAirSeconds
                                && entry.body.landedPeakDrop
                                    >= envelope.landingMinDrop
                                && !entry.body.landedWallContact)
                                step.events.Add(new MotorEvent(
                                    "cleanLanding",
                                    envelope.cleanLandingCredit));
                            step.events.Add(new MotorEvent("landed",
                                entry.body.landedAirSeconds));
                        }
                        result.path = new VehiclePathSample[3];
                        for (var s = 0; s < 3; s++)
                            result.path[s] = new VehiclePathSample
                            {
                                offsetSeconds = sampleAt[s],
                                pose = samples[i][s]
                            };
                    }
                    result.step = step;
                }
                results.Add(result);
            }
            foreach (var b in ordered)
            {
                b.newContactThisTick = false;
                b.peakContactSpeed = 0f;
                b.becameAirborneThisTick = false;
                b.landedThisTick = false;
            }
            for (var i = 0; i < plan.Count; i++)
                if (plan[i].error != null)
                    invalidStateFaults++;
            watch.Stop();
            lastStepSeconds = watch.Elapsed.TotalSeconds;
            totalStepSeconds += lastStepSeconds;
            return results;
        }

        private static VehiclePathSample[] RejectedPath(
            List<MotorPose> poses, float[] offsets)
        {
            var pose = poses != null && poses.Count > 0
                ? poses[0]
                : new MotorPose();
            var path = new VehiclePathSample[3];
            for (var s = 0; s < 3; s++)
                path[s] = new VehiclePathSample
                {
                    offsetSeconds = offsets[s],
                    pose = pose
                };
            return path;
        }

        // Whole-batch validation ahead of mutation: unknown/duplicate
        // participants and non-finite rule states are rejected per
        // request; activity is decided once so frozen/active bookkeeping
        // is coherent before forces are applied.
        private void BuildPlan(IReadOnlyList<VehicleTickRequest> requests)
        {
            plan.Clear();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (requests != null)
                foreach (var request in requests)
                {
                    var entry = new Plan { request = request };
                    if (request == null
                        || string.IsNullOrEmpty(request.participantId))
                    {
                        entry.error = "invalidRequest";
                    }
                    else if (!seen.Add(request.participantId))
                    {
                        entry.error = "duplicateParticipant";
                    }
                    else if (!bodies.TryGetValue(request.participantId,
                        out entry.body))
                    {
                        entry.error = "unknownParticipant";
                    }
                    else if (request.rulesBefore != null
                        && request.rulesBefore.physics != null
                        && !request.rulesBefore.physics.Validate(out _))
                    {
                        // rulesBefore supplies logical motor facts; a
                        // corrupt physical claim is refused atomically.
                        entry.error = "invalidRulesBefore";
                    }
                    else
                    {
                        // A flagged recovery boundary freezes the body for
                        // this interval the same way an explicit
                        // simulateMotion=false does.
                        entry.active = request.simulateMotion
                            && (request.rulesBefore == null
                                || !request.rulesBefore.recovering);
                    }
                    plan.Add(entry);
                }
            foreach (var entry in plan)
                if (entry.body != null)
                    entry.body.SetFrozen(!entry.active);
            // Participants absent from the batch stay as they were: an
            // already-frozen body remains frozen; an active body missing
            // from the request list is frozen rather than left drifting.
            foreach (var body in ordered)
            {
                var mentioned = false;
                foreach (var entry in plan)
                    if (entry.body == body) { mentioned = true; break; }
                if (!mentioned) body.SetFrozen(true);
            }
        }

        // Authoritative reset of one body: pose, velocities,
        // wheel/controller histories, episode bookkeeping and epoch, with
        // no gameplay events. A state that fails finite/unit validation is
        // refused atomically before the body is touched.
        public void Restore(string participantId, MotorState state)
        {
            var body = Find(participantId);
            if (body == null || state == null) return;
            // The legacy (physics == null) restore path derives body
            // velocity straight from the legacy speed fields — a
            // non-finite value there would write NaN/Infinity into
            // PhysX. Reject atomically; never clamp silently. A refused
            // restore is an observable fault, never a silent drop.
            if (!Finite(state.x) || !Finite(state.y) || !Finite(state.z)
                || !Finite(state.heading) || !Finite(state.speed)
                || !Finite(state.lateralSpeed)
                || !Finite(state.verticalSpeed))
            {
                invalidStateFaults++;
                return;
            }
            if (state.physics != null
                && !state.physics.Validate(out _))
            {
                invalidStateFaults++;
                return;
            }
            body.ApplyRestore(state);
        }

        public MotorState Capture(string participantId)
        {
            var body = Find(participantId);
            return body == null ? null : body.live.Copy();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ClearBodies();
            world.Dispose();
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
