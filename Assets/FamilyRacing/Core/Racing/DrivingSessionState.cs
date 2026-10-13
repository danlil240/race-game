using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Tutorial;

namespace FamilyRacing.Core.Racing
{
    // A latched one-shot action (e.g. recovery) awaiting a simulation
    // boundary. Observable so observers can reconcile "command applied"
    // with the later consumed/cancelled outcome.
    [Serializable]
    public sealed class PendingActionState
    {
        public string kind;
        public string commandId;
        public long enqueuedTick;

        public PendingActionState Copy()
        {
            return new PendingActionState
            {
                kind = kind,
                commandId = commandId,
                enqueuedTick = enqueuedTick
            };
        }
    }

    // Ordered progress marker for one course gate in the published
    // snapshot. The session-internal tracker it mirrors is
    // CheckpointProgress.CheckpointState.
    [Serializable]
    public sealed class CheckpointMark
    {
        public string id;
        public int index;
        public bool crossed;

        public CheckpointMark Copy()
        {
            return new CheckpointMark { id = id, index = index, crossed = crossed };
        }
    }

    // Runtime-published actual camera facts for the driving screen. The
    // sim never reads them; they exist so captures can reconstruct framing.
    [Serializable]
    public sealed class DrivingCameraState
    {
        public string mode = "none";
        public float px;
        public float py;
        public float pz;
        public float tx;
        public float ty;
        public float tz;

        public DrivingCameraState Copy()
        {
            return new DrivingCameraState
            {
                mode = mode,
                px = px,
                py = py,
                pz = pz,
                tx = tx,
                ty = ty,
                tz = tz
            };
        }
    }

    // Serializable, copy-safe driving observation. Every Snapshot() returns
    // an independent deep copy; fields are public for the existing JSON
    // serializers (JsonUtility / System.Text.Json IncludeFields /
    // Newtonsoft).
    [Serializable]
    public sealed class DrivingSessionState
    {
        public string raceId;
        public string vehicleId;
        public string eventId;
        public string courseId;
        public string contentVersion;
        public string phase;              // "countdown" | "running" | "finished"
        public int countdownTicksRemaining;
        public long simulationTick;       // advances on every simulated boundary
        public long elapsedTicks;         // race clock: running phase only
        public int lap;
        public int lapsRequired;
        public int nextCheckpoint;
        // Progress facts for authoritative standings: gates accepted on
        // the current lap plus clamped metres along the segment into the
        // next expected gate. Zero-based positions; they never regress.
        public int acceptedGatesThisLap;
        public float segmentProgressMeters;
        public List<CheckpointMark> checkpoints = new List<CheckpointMark>();
        public bool finished;
        public long finishTicks;
        public MotorState motor;
        public InputState input;
        public List<PendingActionState> pendingActions = new List<PendingActionState>();
        public DrivingCameraState camera;
        // Ordered lesson/trial onboarding state while a tutorial session
        // runs (null outside a tutorial). Filled by the owning service —
        // the session itself never mutates tutorial progress. The
        // full-release tutorial incl. gadget onboarding stays deferred;
        // featureCoverage keeps reporting that honestly.
        public TutorialState tutorial;
        public List<string> controls = new List<string>();

        // Deep copy matching the rest of the DTO family: the Task-5 wire
        // decode path produces fresh objects, but observation copies must
        // never alias a live or shared session state either.
        public DrivingSessionState Copy()
        {
            var copy = new DrivingSessionState
            {
                raceId = raceId,
                vehicleId = vehicleId,
                eventId = eventId,
                courseId = courseId,
                contentVersion = contentVersion,
                phase = phase,
                countdownTicksRemaining = countdownTicksRemaining,
                simulationTick = simulationTick,
                elapsedTicks = elapsedTicks,
                lap = lap,
                lapsRequired = lapsRequired,
                nextCheckpoint = nextCheckpoint,
                acceptedGatesThisLap = acceptedGatesThisLap,
                segmentProgressMeters = segmentProgressMeters,
                finished = finished,
                finishTicks = finishTicks,
                motor = motor != null ? motor.Copy() : null,
                input = input == null ? null : new InputState
                {
                    steering = input.steering,
                    drift = input.drift,
                    boost = input.boost,
                    gadget = input.gadget,
                    recover = input.recover,
                    changeCamera = input.changeCamera,
                    pause = input.pause,
                    brake = input.brake,
                    hasThrottleOverride = input.hasThrottleOverride,
                    throttle = input.throttle
                },
                camera = camera != null ? camera.Copy() : null,
                tutorial = tutorial != null ? tutorial.Copy() : null
            };
            foreach (var mark in checkpoints)
                copy.checkpoints.Add(mark != null ? mark.Copy() : null);
            foreach (var action in pendingActions)
                copy.pendingActions.Add(action != null ? action.Copy() : null);
            copy.controls.AddRange(controls);
            return copy;
        }
    }

    // Result of one DrivingSession boundary completion: the post-step
    // independent state copy plus the ordered events emitted by that
    // step (already drained). `reset`, when non-null, is a rule-requested
    // recovery the coordinator must land on the body through
    // IVehicleSimulation.Restore BEFORE this boundary publishes — the
    // committed `state` already carries the post-reset pose.
    public sealed class DrivingStepResult
    {
        public DrivingSessionState state;
        public List<DrivingEvent> events = new List<DrivingEvent>();
        public VehicleReset reset;
    }
}
