using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Networking
{
    // Local motor prediction for one participant (Tasks 6 + 10). Sent
    // input samples advance the predicted pose immediately through the
    // injected IVehicleSimulation — the same force model and the same
    // two-fixed-substep logical interval the authoritative host runs —
    // so the local player feels input before the host round trip. Each
    // delivered snapshot restores the authoritative body/controller
    // state at the acknowledged input boundary, drops acknowledged
    // samples, and replays only the unacknowledged remainder through
    // the identical seam. The pending buffer is bounded at
    // PredictionInputCapacity; overflow explicitly resynchronizes
    // (backlog cleared, ResyncRequested latched) instead of replaying a
    // stale burst.
    //
    // Authority boundaries by contract: prediction simulates only the
    // local body — host authority remains decisive for dynamic racer
    // contacts, recovery, checkpoints, and landing facts. One-shot
    // actions (recover) and their ids are counted as suppressed and
    // never applied or re-emitted here, so a cancelled/replayed recover
    // can never fire twice. Replay step events are counted and
    // discarded — prediction never produces a DrivingEvent, never
    // invokes the rule layer, and never touches persistence.
    // Reconciliation is not assumed bit-identical across Android/Windows
    // physics: errors under CorrectionEpsilonMeters/Degrees are noise.
    //
    // Task 10 bounds: one rendered frame may spend at most
    // PredictionReplayTicksPerFrame replayed logical ticks across ALL
    // corrections it processes (BeginFrame resets the shared budget) —
    // a correction that would exceed it holds the fresh authoritative
    // pose and resynchronizes rather than spreading a partially
    // replayed world across frames. The injected simulation restores
    // the full physical state — attitude, angular velocity, controller
    // and suspension histories — and recovery-epoch ordering fences
    // reordered boundaries: an old snapshot can never undo a recovery.
    public sealed class ClientPrediction
    {
        private readonly string participantId;
        private MotorState predicted;
        private MotorState lastAuthoritative;
        private string raceId = "";
        private long appliedSnapshotSequence = -1;
        private long lastAckedSequence;
        // Newest recovery epoch ever restored. Snapshot sequence alone
        // cannot fence a reordered pre-respawn boundary — the epoch can.
        private long appliedRecoveryEpoch;
        // The injected single-body simulation prediction replays through
        // (Task 4/10 boundary): the same IVehicleSimulation seam the host
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
        // Replay ticks consumed by corrections this rendered frame —
        // shared across every reconcile the frame processes. The frame
        // owner calls BeginFrame once per rendered frame.
        private int frameReplayTicks;

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
        public int FrameReplayTicks { get { return frameReplayTicks; } }
        public double LastReplaySeconds { get; private set; }
        public float LastErrorDistance { get; private set; }
        public float LastErrorDegrees { get; private set; }
        public bool LastErrorMeasured { get; private set; }
        public bool LastCorrectionSnap { get; private set; }
        public long Corrections { get; private set; }
        public long Snaps { get; private set; }
        public bool ResyncRequested { get; private set; }
        public string LastResyncReason { get; private set; } = "";
        public long ResyncCount { get; private set; }
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

        // One rendered frame boundary: resets the shared replay budget.
        // The frame owner (NetworkRaceBridge.PumpClient) calls this once
        // per frame so a burst of delivered snapshots can never multiply
        // the replay work a frame spends.
        public void BeginFrame()
        {
            frameReplayTicks = 0;
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
                RequestResync("historyOverflow");
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

        // Seed the injected simulation's single-body roster once per use —
        // the roster exists only once the local participant's tuning and
        // an authoritative pose do.
        private void EnsureSeeded(MotorState state, VehicleTuning tuning)
        {
            if (simulation == null || simulationSeeded || tuning == null)
                return;
            simulation.Initialize(new List<VehicleSpawn>
            {
                new VehicleSpawn
                {
                    participantId = participantId,
                    tuning = tuning,
                    initialState = state.Copy()
                }
            });
            simulationSeeded = true;
        }

        // One replayed sample through whichever backend prediction owns:
        // the injected simulation batch when present, the legacy inline
        // motor otherwise. Both produce one 1/30-second interval per
        // sample — exactly the interval the host consumes for that
        // sequence number. The injected body simply continues: the
        // authoritative restore happens once at the reconcile boundary,
        // never per step, so PhysX contact/sleep caches inside the
        // interval chain stay honest instead of being teleported each
        // sample. Returns null when no usable backend exists.
        private MotorStepResult ReplayStep(MotorState state,
            DriverInput input, VehicleTuning tuning, IMotorWorld world)
        {
            if (simulation != null)
            {
                EnsureSeeded(state, tuning);
                var results = simulation.Step(new List<VehicleTickRequest>
                {
                    new VehicleTickRequest
                    {
                        participantId = participantId,
                        input = input,
                        rulesBefore = NormalizedRules(state, true),
                        simulateMotion = true
                    }
                });
                if (results != null)
                    foreach (var result in results)
                        if (result != null && result.step != null
                            && result.participantId == participantId)
                        {
                            // The tick is a commit marker, not backend
                            // state: the host stamps it after each
                            // interval (DrivingSession.CompleteStep), so
                            // prediction does too — one logical tick per
                            // replayed sample on EVERY backend. The
                            // physical body merges rulesBefore.tick
                            // verbatim (already +1); the arcade adapter
                            // increments inside its own step and would
                            // otherwise double-count.
                            if (result.step.state != null)
                                result.step.state.tick = state.tick + 1;
                            return result.step;
                        }
                return MotorStepResult.Rejected(state, "missingResult");
            }
            if (world == null) return null;
            return ArcadeMotor.Step(NormalizedRules(state, false), input,
                tuning, world, DrivingSession.StepSeconds);
        }

        // The rule-facts view a replayed interval may see: the body's own
        // merged state with the per-tick recovery boundary flag cleared.
        // A snapshot marked `recovering` describes the boundary the host
        // committed — replaying it would freeze the predicted body for
        // the whole tail (the sim treats a recovering rulesBefore as a
        // kinematic interval), so the flag is normalized away and only
        // the respawned pose/velocities carry forward. `advanceTick`
        // supplies the interval's own tick count for backends that merge
        // the rule tick verbatim (the physical sim); the arcade motor
        // increments tick inside its step and takes the unmodified
        // view — the returned step state is re-stamped post-step either
        // way so one predicted sample always advances tick once.
        private static MotorState NormalizedRules(MotorState state,
            bool advanceTick)
        {
            var rules = state.Copy();
            rules.recovering = false;
            if (advanceTick) rules.tick = state.tick + 1;
            return rules;
        }

        // A snap-boundary restore copy: suspension damper and controller
        // histories are invalidated (contactValid cleared) so a teleport
        // cannot carry stale damper/steering memory across it, while the
        // authoritative pose, velocities and episode bookkeeping are
        // restored verbatim.
        private static MotorState SnapRestoreState(MotorState state)
        {
            var copy = state.Copy();
            var physics = copy.physics;
            if (physics == null) return copy;
            physics.filteredSteering = 0f;
            physics.filteredRollRate = 0f;
            physics.balancePrevError = 0f;
            physics.filteredLeanTarget = 0f;
            physics.driftAmount = 0f;
            if (physics.wheels != null)
                foreach (var wheel in physics.wheels)
                    if (wheel != null)
                    {
                        wheel.grounded = false;
                        wheel.compression = 0f;
                        wheel.previousCompression = 0f;
                        wheel.contactValid = false;
                    }
            return copy;
        }

        // A resynchronizing boundary: the fresh authoritative pose is
        // restored without replay — a usable presentation in its own
        // right — the backlog/history clear, and the resend request
        // latches for the wire layer.
        private MotorState ResyncHold(MotorState authoritative,
            string reason, bool snapBoundary, VehicleTuning tuning)
        {
            if (simulation != null)
            {
                EnsureSeeded(authoritative, tuning);
                simulation.Restore(participantId, snapBoundary
                    ? SnapRestoreState(authoritative) : authoritative);
            }
            pending.Clear();
            history.Clear();
            predicted = authoritative.Copy();
            LastReplayCount = 0;
            LastReplaySeconds = 0;
            LastErrorMeasured = false;
            LastErrorDistance = 0f;
            LastErrorDegrees = 0f;
            LastCorrectionSnap = true;
            Snaps++;
            Corrections++;
            RequestResync(reason);
            return predicted.Copy();
        }

        private void RequestResync(string reason)
        {
            ResyncRequested = true;
            LastResyncReason = reason ?? "";
            ResyncCount++;
        }

        // Apply one authoritative snapshot: drop acknowledged samples,
        // restore the authoritative body+controller state, replay the
        // unacknowledged remainder through the same step, and record the
        // correction the presentation layer may blend (or must snap).
        // Reordered, duplicated, stale-race, or older-recovery-epoch
        // snapshots never roll state back.
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
                appliedRecoveryEpoch = 0;
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
            var authoritativeEpoch = authoritative.physics != null
                ? authoritative.physics.recoveryEpoch : 0;
            // Recovery-epoch ordering: a reordered pre-respawn boundary
            // may carry a HIGHER snapshot sequence — only the epoch sees
            // that it would undo a recovery the client already restored.
            if (authoritativeEpoch < appliedRecoveryEpoch)
            {
                StaleSnapshots++;
                return Predicted;
            }
            var snap = raceChanged || authoritative.recovering
                || authoritativeEpoch > appliedRecoveryEpoch;
            appliedRecoveryEpoch = authoritativeEpoch;
            lastAuthoritative = authoritative.Copy();

            var ack = FindAck(snapshot);
            if (ack != null && ack.inputSequence > lastAckedSequence)
                lastAckedSequence = ack.inputSequence;
            while (pending.Count > 0
                && pending[0].inputSequence <= lastAckedSequence)
                pending.RemoveAt(0);
            // A hole in the retained tail means the acknowledged boundary
            // can never be replayed faithfully — the missing samples the
            // host consumed are gone, so resync rather than guess.
            for (var i = 1; i < pending.Count; i++)
                if (pending[i].inputSequence
                        != pending[i - 1].inputSequence + 1)
                    return ResyncHold(authoritative, "missingHistory",
                        snap, tuning);

            // One rendered frame shares a single replay budget across
            // every correction it processes — a packet burst can never
            // multiply a PhysX replay burst. A correction that would
            // exceed the remaining budget holds the fresh authoritative
            // pose rather than spreading a partially replayed world
            // across later frames.
            if (frameReplayTicks + pending.Count
                    > NetworkSettings.PredictionReplayTicksPerFrame)
                return ResyncHold(authoritative, "replayBudget",
                    snap, tuning);

            // Error is measured at the ack horizon: the predicted pose
            // after the last consumed sample versus the authoritative
            // pose the snapshot carries — distance AND full-attitude
            // angle. Held-input divergence is a real measured correction.
            MotorState horizon;
            LastErrorMeasured = history.TryGetValue(lastAckedSequence,
                out horizon);
            LastErrorDistance = LastErrorMeasured
                ? Distance(horizon, authoritative) : 0f;
            LastErrorDegrees = LastErrorMeasured
                ? OrientationDegrees(horizon, authoritative) : 0f;
            if (snap)
                history.Clear();
            else
                PruneHistory();
            if (LastErrorDistance > NetworkSettings.CorrectionSnapMeters
                || LastErrorDegrees > NetworkSettings.CorrectionSnapDegrees)
                snap = true;
            // An unmeasured ack horizon is NOT an integrity failure:
            // pending still holds every unacknowledged input and the
            // snapshot carries the authoritative boundary, so
            // restore+replay stays faithful. The horizon only goes
            // missing when it sits below the retained window — the
            // post-clear gap, a pruned repeat ack, or an ack on a
            // never-predicted sequence — and a resync that clears
            // history can never recreate that entry: requesting one
            // here re-arms the identical miss on every snapshot (the
            // resync loop this clause replaces). The correction snaps
            // instead: an unverified divergence is never presented as
            // small. Resync stays reserved for genuinely missing
            // unacknowledged inputs (the pending hole above) and for
            // backlog overflow.
            if (!LastErrorMeasured) snap = true;
            LastCorrectionSnap = snap;
            if (snap) Snaps++;
            if (snap || LastErrorDistance
                    > NetworkSettings.CorrectionEpsilonMeters
                || LastErrorDegrees
                    > NetworkSettings.CorrectionEpsilonDegrees)
                Corrections++;

            // Restore the full authoritative state on the injected body —
            // pose, velocities, attitude, angular velocity, controller
            // and suspension histories — then replay only unacknowledged
            // samples through the same seam. Snap boundaries restore
            // through the invalidated-history copy. Host authority stays
            // decisive for contacts, recovery, checkpoints and landing
            // facts — those never come from this path.
            var state = authoritative.Copy();
            if (simulation != null)
            {
                EnsureSeeded(authoritative, tuning);
                simulation.Restore(participantId, snap
                    ? SnapRestoreState(authoritative) : authoritative);
            }
            LastReplayCount = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // Replaying into a frozen racer is meaningless: countdown,
            // pause, finished and DNF boundaries hold the authoritative
            // pose while the retained tail waits for a live boundary.
            var live = racer.session.phase == "running"
                && !racer.finished && !racer.dnf
                && !snapshot.state.paused;
            foreach (var frame in pending)
            {
                if (tuning == null || !live) break;
                var step = ReplayStep(state, frame.input, tuning, world);
                if (step == null) break;
                SuppressedMotorEvents += step.events.Count;
                if (step.rejected) break;
                state = step.state;
                LastReplayCount++;
                frameReplayTicks++;
                history[frame.inputSequence] = state.Copy();
            }
            watch.Stop();
            LastReplaySeconds = watch.Elapsed.TotalSeconds;
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
            appliedRecoveryEpoch = 0;
            pending.Clear();
            history.Clear();
            frameReplayTicks = 0;
            LastReplayCount = 0;
            LastReplaySeconds = 0;
            LastErrorDistance = 0f;
            LastErrorDegrees = 0f;
            LastErrorMeasured = false;
            LastCorrectionSnap = false;
            Corrections = 0;
            Snaps = 0;
            ResyncRequested = false;
            LastResyncReason = "";
            ResyncCount = 0;
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
                historyCount = history.Count,
                lastReplayCount = LastReplayCount,
                frameReplayTicks = frameReplayTicks,
                lastReplaySeconds = LastReplaySeconds,
                lastErrorDistance = LastErrorDistance,
                lastErrorDegrees = LastErrorDegrees,
                lastErrorMeasured = LastErrorMeasured,
                lastCorrectionSnap = LastCorrectionSnap,
                corrections = Corrections,
                snaps = Snaps,
                resyncRequested = ResyncRequested,
                resyncReason = LastResyncReason ?? "",
                resyncCount = ResyncCount,
                recoveryEpoch = appliedRecoveryEpoch,
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

        // Full-attitude error in degrees: the quaternion angle between
        // the two physics attitudes when both exist (shortest arc via
        // the absolute dot product), the wrapped-yaw angle otherwise.
        private static float OrientationDegrees(MotorState a, MotorState b)
        {
            var pa = a != null ? a.physics : null;
            var pb = b != null ? b.physics : null;
            if (pa != null && pb != null)
            {
                var dot = Math.Abs(
                    pa.attitudeX * pb.attitudeX
                    + pa.attitudeY * pb.attitudeY
                    + pa.attitudeZ * pb.attitudeZ
                    + pa.attitudeW * pb.attitudeW);
                if (dot > 1f) dot = 1f;
                return (float)(2.0 * Math.Acos(dot) * (180.0 / Math.PI));
            }
            return Math.Abs(WrapAngle(b.heading - a.heading))
                * (float)(180.0 / Math.PI);
        }

        private static float WrapAngle(float radians)
        {
            if (float.IsNaN(radians) || float.IsInfinity(radians)) return 0f;
            var twoPi = (float)(Math.PI * 2.0);
            while (radians > Math.PI) radians -= twoPi;
            while (radians <= -Math.PI) radians += twoPi;
            return radians;
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
        // Retained per-sequence predicted poses — the measurement window
        // a reconcile scores divergence against. Zero while predicts run
        // means the predict loop is never writing history (the missing-
        // history resync signature), not that replay is broken.
        public int historyCount;
        public int lastReplayCount;
        // Replay ticks consumed by corrections this rendered frame —
        // the shared budget leg of the 12-tick bound.
        public int frameReplayTicks;
        // Wall seconds the last reconcile spent replaying — the replay
        // cost observation.
        public double lastReplaySeconds;
        public float lastErrorDistance;
        // Full-attitude error at the ack horizon, degrees.
        public float lastErrorDegrees;
        public bool lastErrorMeasured;
        public bool lastCorrectionSnap;
        public long corrections;
        public long snaps;
        public bool resyncRequested;
        // Why the most recent resync latched: "historyOverflow",
        // "missingHistory", or "replayBudget".
        public string resyncReason = "";
        public long resyncCount;
        // The recovery epoch the prediction body currently rides —
        // fencing fact against reordered pre-respawn boundaries.
        public long recoveryEpoch;
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
