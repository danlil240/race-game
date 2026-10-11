using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;

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

    // Ordered progress marker for one course gate. Task 2 attaches the
    // crossing rules; the state shape is fixed now.
    [Serializable]
    public sealed class CheckpointState
    {
        public string id;
        public int index;
        public bool crossed;

        public CheckpointState Copy()
        {
            return new CheckpointState { id = id, index = index, crossed = crossed };
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
        public List<CheckpointState> checkpoints = new List<CheckpointState>();
        public bool finished;
        public long finishTicks;
        public MotorState motor;
        public InputState input;
        public List<PendingActionState> pendingActions = new List<PendingActionState>();
        public DrivingCameraState camera;
        public string tutorial = "notImplemented";
        public List<string> controls = new List<string>();
    }

    // Result of one DrivingSession.Step: the post-step independent state
    // copy plus the ordered events emitted by that step (already drained).
    public sealed class DrivingStepResult
    {
        public DrivingSessionState state;
        public List<DrivingEvent> events = new List<DrivingEvent>();
    }
}
