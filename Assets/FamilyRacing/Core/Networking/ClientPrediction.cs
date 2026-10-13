using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Networking
{
    // Local motor prediction for one participant (Task 6). Sent input
    // samples advance the predicted pose immediately through the same
    // ArcadeMotor.Step and the same normalized tuning/static course
    // queries the authoritative host runs — the local player feels input
    // before the host round trip. Each delivered snapshot restores the
    // authoritative motor, drops acknowledged samples, and replays only
    // the unacknowledged remainder. The pending buffer is bounded at
    // PredictionInputCapacity; overflow explicitly resynchronizes
    // (backlog cleared, ResyncRequested latched) instead of replaying a
    // stale burst.
    //
    // Authority boundaries by contract: prediction simulates only the
    // motor — host authority remains decisive for dynamic racer contacts,
    // recovery, checkpoints, and landing facts. One-shot actions
    // (recover) and their ids are counted as suppressed and never applied
    // or re-emitted here, so a cancelled/replayed recover can never fire
    // twice. Replay step events are counted and discarded — prediction
    // never produces a DrivingEvent and never touches persistence.
    // Reconciliation is not assumed bit-identical across Android/Windows
    // physics: errors under CorrectionEpsilonMeters are noise.
    public sealed class ClientPrediction
    {
        private readonly string participantId;
        private MotorState predicted;
        private MotorState lastAuthoritative;
        private string raceId = "";
        private long appliedSnapshotSequence = -1;
        private long lastAckedSequence;
        // The injected single-body simulation prediction replays through
        // (Task 4 boundary): the same IVehicleSimulation seam the host
        // steps, seeded once per use with the participant's own tuning.
        // Null keeps the legacy inline ArcadeMotor path for callers that
        // still pass an IMotorWorld.
        private IVehicleSimulation simulation;
        private bool simulationSeeded;
        // Sent-but-unacknowledged samples in sequence order, plus the
        // per-sequence predicted pose that lets a reconcile measure the
        // divergence exactly at the ack horizon.
        private readonly List<NetworkInputFrame> pending =
            new List<NetworkInputFrame>();
        private readonly Dictionary<long, MotorState> history =
            new Dictionary<long, MotorState>();

        public ClientPrediction(string participantId)
        {
            this.participantId = participantId ?? "";
        }

        public string ParticipantId { get { return participantId; } }

        // The prediction loop's injected simulation (the same seam the
        // authoritative race steps). The caller owns its lifetime; the
        // roster seeds lazily on first use because the participant's
        // tuning/state only exist once a snapshot has arrived.
        public void SetSimulation(IVehicleSimulation value)
        {
            simulation = value;
            simulationSeeded = false;
        }

        public IVehicleSimulation Simulation { get { return simulation; } }

        public bool HasPrediction { get { return predicted != null; } }
        public int PendingCount { get { return pending.Count; } }
        public int HistoryCount { get { return history.Count; } }
        public long LastAckedSequence { get { return lastAckedSequence; } }
        public int LastReplayCount { get; private set; }
        public float LastErrorDistance { get; private set; }
        public bool LastErrorMeasured { get; private set; }
        public bool LastCorrectionSnap { get; private set; }
        public long Corrections { get; private set; }
        public long Snaps { get; private set; }
        public bool ResyncRequested { get; private set; }
        public int StaleSnapshots { get; private set; }
        public long SuppressedMotorEvents { get; private set; }
        public long SuppressedOneShots { get; private set; }

        public MotorState Predicted
        {
            get { return predicted != null ? predicted.Copy() : null; }
        }

        public MotorState LastAuthoritative
        {
            get { return lastAuthoritative != null ? lastAuthoritative.Copy() : null; }
        }

        // One local 30 Hz sample already committed to the wire: buffer it
        // for later replay and advance the predicted pose now. Returns a
        // copy of the post-step predicted state; null until the first
        // authoritative snapshot seeds a spawn pose.
        public MotorState Predict(NetworkInputFrame frame,
            VehicleTuning tuning, IMotorWorld world)
        {
            if (frame == null)
                return predicted != null ? predicted.Copy() : null;
            // One-shots (recover) and pending action ids are host-decisive:
            // they are counted and never applied or re-emitted here.
            if (frame.input.Recover) SuppressedOneShots++;
            if (frame.actionIds != null)
                SuppressedOneShots += frame.actionIds.Count;
            if (pending.Count >= NetworkSettings.PredictionInputCapacity)
            {
                // Replay bound: overflow explicitly resynchronizes — the
                // backlog clears rather than replaying stale input.
                pending.Clear();
                history.Clear();
                ResyncRequested = true;
            }
            pending.Add(frame.Copy());
            if (predicted == null || tuning == null) return Predicted;
            var step = ReplayStep(predicted, frame.input, tuning, world);
            if (step == null) return Predicted;
            SuppressedMotorEvents += step.events.Count;
            if (step.rejected)
                return predicted.Copy();
            predicted = step.state;
            history[frame.inputSequence] = predicted.Copy();
            return predicted.Copy();
        }

        // One replayed sample through whichever backend prediction owns:
        // the injected simulation batch when present, the legacy inline
        // motor otherwise. Both produce one 1/30-second interval per
        // sample — exactly the interval the host consumes for that
        // sequence number. Returns null when no usable backend exists.
        private MotorStepResult ReplayStep(MotorState state,
            DriverInput input, VehicleTuning tuning, IMotorWorld world)
        {
            if (simulation != null)
            {
                if (!simulationSeeded)
                {
                    simulation.Initialize(new List<VehicleSpawn>
                    {
                        new VehicleSpawn
                        {
                            participantId = participantId,
                            tuning = tuning,
                            initialState = state
                        }
                    });
                    simulationSeeded = true;
                }
                // Restore keeps the body's own record aligned with the
                // prediction state before the interval runs — the same
                // authoritative-reset contract the coordinator uses.
                simulation.Restore(participantId, state);
                var results = simulation.Step(new List<VehicleTickRequest>
                {
                    new VehicleTickRequest
                    {
                        participantId = participantId,
                        input = input,
                        rulesBefore = state,
                        simulateMotion = true
                    }
                });
                if (results != null)
                    foreach (var result in results)
                        if (result != null && result.step != null
                            && result.participantId == participantId)
                            return result.step;
                return MotorStepResult.Rejected(state, "missingResult");
            }
            if (world == null) return null;
            return ArcadeMotor.Step(state, input, tuning, world,
                DrivingSession.StepSeconds);
        }

        // Apply one authoritative snapshot: drop acknowledged samples,
        // restore the authoritative motor, replay the unacknowledged
        // remainder through the same step, and record the correction the
        // presentation layer may blend (or must snap). Reordered,
        // duplicated, or stale-race snapshots never roll state back.
        public MotorState Reconcile(NetworkSnapshot snapshot,
            VehicleTuning tuning, IMotorWorld world)
        {
            if (snapshot == null || snapshot.state == null)
                return Predicted;
            var raceChanged = !string.Equals(raceId, snapshot.raceId ?? "",
                StringComparison.Ordinal);
            if (raceChanged)
            {
                // New race/ownership identity: pre-boundary history can
                // never steer this race — the correction snaps.
                pending.Clear();
                history.Clear();
                raceId = snapshot.raceId ?? "";
                appliedSnapshotSequence = -1;
            }
            if (snapshot.snapshotSequence <= appliedSnapshotSequence)
            {
                StaleSnapshots++;
                return Predicted;
            }
            appliedSnapshotSequence = snapshot.snapshotSequence;

            var racer = FindRacer(snapshot.state);
            var authoritative = racer != null && racer.session != null
                ? racer.session.motor : null;
            if (authoritative == null) return Predicted;
            var snap = raceChanged || authoritative.recovering;
            lastAuthoritative = authoritative.Copy();

            var ack = FindAck(snapshot);
            if (ack != null && ack.inputSequence > lastAckedSequence)
                lastAckedSequence = ack.inputSequence;
            while (pending.Count > 0
                && pending[0].inputSequence <= lastAckedSequence)
                pending.RemoveAt(0);

            // Error is measured at the ack horizon: the predicted pose
            // after the last consumed sample versus the authoritative
            // pose the snapshot carries. Held-input divergence is a real
            // measured correction.
            MotorState horizon;
            LastErrorMeasured = history.TryGetValue(lastAckedSequence,
                out horizon);
            LastErrorDistance = LastErrorMeasured
                ? Distance(horizon, authoritative) : 0f;
            if (snap)
                history.Clear();
            else
                PruneHistory();
            if (LastErrorDistance > NetworkSettings.CorrectionSnapMeters)
                snap = true;
            LastCorrectionSnap = snap;
            if (snap) Snaps++;
            if (snap || LastErrorDistance
                    > NetworkSettings.CorrectionEpsilonMeters)
                Corrections++;

            // Restore authoritative, then replay only unacknowledged
            // samples. Host authority stays decisive for contacts,
            // recovery, checkpoints and landing facts — those never come
            // from this path.
            var state = authoritative.Copy();
            LastReplayCount = 0;
            foreach (var frame in pending)
            {
                if (tuning == null) break;
                var step = ReplayStep(state, frame.input, tuning, world);
                if (step == null) break;
                SuppressedMotorEvents += step.events.Count;
                if (step.rejected) break;
                state = step.state;
                LastReplayCount++;
                history[frame.inputSequence] = state.Copy();
            }
            predicted = state;
            return predicted.Copy();
        }

        // Every retained fact dies: pending samples, per-sequence history,
        // the seeded pose and diagnostics. Called on race adoption,
        // ownership-epoch change, reconnect and teardown.
        public void Reset()
        {
            predicted = null;
            lastAuthoritative = null;
            raceId = "";
            appliedSnapshotSequence = -1;
            lastAckedSequence = 0;
            pending.Clear();
            history.Clear();
            LastReplayCount = 0;
            LastErrorDistance = 0f;
            LastErrorMeasured = false;
            LastCorrectionSnap = false;
            Corrections = 0;
            Snaps = 0;
            ResyncRequested = false;
            StaleSnapshots = 0;
            SuppressedMotorEvents = 0;
            SuppressedOneShots = 0;
            // The injected simulation survives a Reset (its owner manages
            // the roster); only the seed flag resets so the next race
            // re-initializes it with fresh facts.
            simulationSeeded = false;
        }

        // The resync latch is a one-shot signal, not a state: the wire
        // layer clears it once the resend request has actually been sent
        // upstream, so a cleared backlog never re-requests forever.
        public void ClearResyncRequest()
        {
            ResyncRequested = false;
        }

        // The observable prediction facts for the validation stream.
        public NetworkPredictionState State()
        {
            return new NetworkPredictionState
            {
                participantId = participantId,
                active = predicted != null,
                predictedMotor = predicted != null ? predicted.Copy() : null,
                lastAuthoritativeMotor = lastAuthoritative != null
                    ? lastAuthoritative.Copy() : null,
                acknowledgedInputSequence = lastAckedSequence,
                pendingInputCount = pending.Count,
                lastReplayCount = LastReplayCount,
                lastErrorDistance = LastErrorDistance,
                lastErrorMeasured = LastErrorMeasured,
                lastCorrectionSnap = LastCorrectionSnap,
                corrections = Corrections,
                snaps = Snaps,
                resyncRequested = ResyncRequested,
                staleSnapshots = StaleSnapshots,
                suppressedMotorEvents = SuppressedMotorEvents,
                suppressedOneShots = SuppressedOneShots
            };
        }

        private NetworkRacerState FindRacer(NetworkRaceState state)
        {
            if (state.racers == null) return null;
            foreach (var racer in state.racers)
                if (racer != null
                    && string.Equals(racer.participantId, participantId,
                        StringComparison.Ordinal))
                    return racer;
            return null;
        }

        private NetworkInputAck FindAck(NetworkSnapshot snapshot)
        {
            if (snapshot.acks == null) return null;
            foreach (var ack in snapshot.acks)
                if (ack != null
                    && string.Equals(ack.participantId, participantId,
                        StringComparison.Ordinal))
                    return ack;
            return null;
        }

        // Acknowledged history entries are dead weight: only the entry at
        // the ack horizon (already consumed for the error measure) and
        // older go; unacknowledged entries survive for the next measure.
        private void PruneHistory()
        {
            var dead = new List<long>();
            foreach (var seq in history.Keys)
                if (seq <= lastAckedSequence) dead.Add(seq);
            foreach (var seq in dead) history.Remove(seq);
        }

        private static float Distance(MotorState a, MotorState b)
        {
            var dx = a.x - b.x;
            var dy = a.y - b.y;
            var dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    // Observable local-prediction facts delivered in the validation
    // state under `prediction` — predicted and authoritative motors
    // stay clearly separated; predicted data never appears authoritative
    // through the `driving` field.
    [Serializable]
    public sealed class NetworkPredictionState
    {
        public string participantId = "";
        public bool active;
        public MotorState predictedMotor;
        public MotorState lastAuthoritativeMotor;
        public long acknowledgedInputSequence;
        public int pendingInputCount;
        public int lastReplayCount;
        public float lastErrorDistance;
        public bool lastErrorMeasured;
        public bool lastCorrectionSnap;
        public long corrections;
        public long snaps;
        public bool resyncRequested;
        public int staleSnapshots;
        public long suppressedMotorEvents;
        public long suppressedOneShots;

        public NetworkPredictionState Copy()
        {
            var copy = (NetworkPredictionState)MemberwiseClone();
            copy.predictedMotor = predictedMotor != null
                ? predictedMotor.Copy() : null;
            copy.lastAuthoritativeMotor = lastAuthoritativeMotor != null
                ? lastAuthoritativeMotor.Copy() : null;
            return copy;
        }
    }
}
