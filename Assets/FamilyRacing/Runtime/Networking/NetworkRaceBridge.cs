using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using UnityEngine;

namespace FamilyRacing.Networking
{
    /// <summary>
    /// Task-9 replication spine (protocol 2): the authoritative Task-3
    /// <see cref="NetworkRace"/> on the host, replicated over the Task-1
    /// transport's two channels under Task-2 session/ownership policy.
    ///
    /// Boundaries are explicit: <see cref="Pump"/> runs once per Unity
    /// frame after the transport pump — it applies lifecycle diffs
    /// (race create/end, pause, control-source and ownership-epoch
    /// transfers) and flushes staged authoritative events in reliable
    /// ordered batches. <see cref="StepTick"/> runs once per local 30 Hz
    /// logical tick — on the host it feeds the local racer through the
    /// same validated input path as remote clients, consumes at most one
    /// buffered sample per racer, steps the coordinator, and publishes a
    /// full four-racer snapshot on every second tick; on a client it
    /// emits one input sample carrying up to the last four frames.
    ///
    /// Physical races run every seated racer through one shared injected
    /// simulation world (<see cref="SimulationProvider"/>) — collisions
    /// between racers are resolved by that single authoritative world,
    /// and snapshots carry each racer's compact versioned physical state
    /// (NetworkPhysicsState) so a client's prediction can reconcile its
    /// own racer while remote poses ride the shared envelope.
    ///
    /// Security posture: the host accepts only P1IB input batches from
    /// clients, resolves the owning participant from the authenticated
    /// connection (never the payload), and validates each frame through
    /// <see cref="HostInputBuffer"/>. Clients cannot submit race progress,
    /// results, contacts, or rewards — the wire vocabulary cannot
    /// express them.
    /// </summary>
    public sealed class NetworkRaceBridge : IDisposable
    {
        private readonly LanTransport transport;
        private readonly LanSessionService session;
        private DrivingContentCatalog content;
        // Task 4: the runtime injects the world provider once — backend
        // selection comes from the catalog's SimulationBackend at race
        // creation. Null keeps the legacy IMotorWorld arcade fallback.
        // The bridge owns every simulation it creates and disposes them
        // with the race they served.
        internal Func<DrivingContentCatalog, IVehicleSimulation>
            SimulationProvider;
        // Task 10: the client prediction world provider — the physical
        // backend supplies a PhysicsPredictionSimulation over its own
        // collision-only scene (exactly one local body; remote racers
        // are display-only and never collide there). When unset the
        // prediction world falls back to SimulationProvider (a single-
        // body roster seeds it identically), then to the arcade adapter.
        internal Func<DrivingContentCatalog, IVehicleSimulation>
            PredictionSimulationProvider;

        // --- host -------------------------------------------------------
        private NetworkRace race;
        private IVehicleSimulation hostSimulation;
        private IMotorWorld hostWorld;
        private readonly HostInputBuffer inputs = new HostInputBuffer();
        private string hostSessionId = "";
        private string raceId = "";
        private int raceOrdinal;
        private long snapshotSequence;
        private long eventBatchSequence;
        // A client's upstream resend request (P1RS) forces the next host
        // tick to publish rather than waiting for the snapshot cadence.
        private bool snapshotRequested;
        private readonly List<DrivingEvent> stagedEvents = new List<DrivingEvent>();
        // participantId -> applied session facts; diffed every Pump so a
        // control transfer or epoch bump is applied at a host boundary.
        private readonly Dictionary<string, long> appliedEpochs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> appliedControl =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private NetworkSnapshot lastPublished;
        // The pause flag as last applied — a transition forces an
        // immediate snapshot so clients see the freeze without waiting on
        // a (frozen) race-clock cadence.
        private bool appliedPaused;
        // Bridge-emitted lifecycle events share a per-race id namespace:
        // "race.seated" takes e1, explicit one-shot cancellations e2+.
        private long bridgeEventSequence;

        // --- shared -----------------------------------------------------
        private readonly List<DrivingEvent> drainedEvents = new List<DrivingEvent>();
        private long inputSequence;      // local stream: +1 per local tick
        private long localTick;

        // --- client -----------------------------------------------------
        private NetworkSnapshot latestSnapshot;
        private string appliedRaceId = "";
        private int appliedRaceOrdinal = -1;
        private long latestSnapshotSeq = -1;
        private readonly List<NetworkInputFrame> recentOut =
            new List<NetworkInputFrame>();
        // One-shot ids awaiting a host apply/cancel acknowledgment; they
        // ride outbound frames in submission order — windowed to the
        // host's per-frame bound — until an event batch confirms them,
        // and they survive replay and reconnect boundaries.
        private readonly HashSet<string> pendingActions =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> pendingActionOrder = new List<string>();
        private readonly HashSet<string> seenEventIds =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> seenEventOrder = new Queue<string>();
        // Task 6: local prediction + remote interpolation. The local
        // racer's sent-but-unacknowledged samples replay through the same
        // ArcadeMotor the host runs; remote racers render from the
        // interpolation buffer behind the newest host tick. Both are
        // client-only — the host's local racer is already authoritative.
        private ClientPrediction prediction;
        // The prediction loop's own world: a single-body simulation the
        // client replays through the same seam the host steps — never a
        // second authoritative world.
        private IVehicleSimulation predictionSimulation;
        private IMotorWorld predictionWorld;
        // Newest accepted snapshot awaiting reconcile this frame. A
        // packet burst coalesces to the newest boundary — replay runs
        // once per frame against it, never once per delivered packet,
        // so the shared 12-tick replay budget is honest.
        private NetworkSnapshot pendingReconcile;
        private readonly RemoteInterpolation interpolation =
            new RemoteInterpolation();
        // Visual smoothing is deliberately separate from sim correction:
        // Reconcile corrects the predicted motor immediately while the
        // smoother eases (or snaps) what the player sees.
        private readonly CorrectionSmoother smoother =
            new CorrectionSmoother();
        private MotorState smoothedLocal;
        private double lastSmoothTime = double.NaN;
        private IMotorWorld clientWorld;
        private long lastSentEpoch = -1;
        private long lastSnapshotHostTick;
        private double lastSnapshotReceivedAt = double.NaN;
        // Render-time clock, injectable so tests can drive deterministic
        // host-time estimates; defaults to unscaled real time.
        internal Func<double> NowSeconds =
            () => Time.realtimeSinceStartupAsDouble;

        public NetworkRaceBridge(LanTransport transport, LanSessionService session)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            transport.GameplayMessage += OnGameplayMessage;
            transport.Message += OnReliableMessage;
        }

        // The validated driving catalog the host needs to build races;
        // content-version compatibility was already enforced at join.
        public void SetContent(DrivingContentCatalog catalog)
        {
            content = catalog;
        }

        // --- diagnostics / observation -------------------------------------

        /// <summary>Last codec/publish failure in plain terms.</summary>
        public string LastError { get; private set; } = "";
        /// <summary>Gameplay packets dropped: oversized, malformed, wrong
        /// direction, or foreign-session.</summary>
        public long DroppedPackets { get; private set; }
        /// <summary>Snapshots dropped client-side as reordered or
        /// stale-race.</summary>
        public long StaleSnapshots { get; private set; }
        /// <summary>Host-side input frames refused by validation.</summary>
        public long InputRejections { get; private set; }
        /// <summary>Last validation error code from HostInputBuffer.</summary>
        public string LastInputError { get; private set; } = "";
        /// <summary>Authoritative events dropped by the staged-send cap or
        /// already-seen dedup on the client.</summary>
        public long DroppedEvents { get; private set; }
        /// <summary>Diagnostic: gameplay datagrams seen at the transport
        /// demux, and the last connection id they arrived on.</summary>
        public long GameplayPacketsSeen { get; private set; }
        /// <summary>Diagnostic: host-side conn id of the most recent
        /// inbound gameplay datagram.</summary>
        public ulong LastGameplayConnection { get; private set; }
        /// <summary>Diagnostic: host-side snapshot datagrams pushed.</summary>
        public long SnapshotsPublished { get; private set; }
        /// <summary>One-shot action ids still awaiting a host apply/cancel
        /// acknowledgment — diagnostic proving retries terminate.</summary>
        public int PendingOneShotCount { get { return pendingActions.Count; } }

        /// <summary>Task 11: this endpoint's replication-link counters as
        /// a stream-ready slice — the packet/loss facts that distinguish
        /// "no snapshots arrived" (transport starvation) from "snapshots
        /// arrived and were rejected" (session fence, stale-race drop).</summary>
        public NetworkLinkState LinkState()
        {
            return new NetworkLinkState
            {
                gameplayPacketsSeen = GameplayPacketsSeen,
                droppedPackets = DroppedPackets,
                staleSnapshots = StaleSnapshots,
                droppedEvents = DroppedEvents,
                snapshotsPublished = SnapshotsPublished,
                inputRejections = InputRejections,
                pendingOneShots = PendingOneShotCount,
                lastSnapshotHostTick = lastSnapshotHostTick,
                lastError = LastError,
                lastInputError = LastInputError,
            };
        }

        /// <summary>Latest authoritative race state: the coordinator's own
        /// snapshot on the host, the newest accepted snapshot on a
        /// client.</summary>
        public NetworkRaceState LatestState
        {
            get
            {
                if (race != null) return race.Snapshot();
                return latestSnapshot != null ? latestSnapshot.state : null;
            }
        }

        /// <summary>The most recent snapshot this bridge published (host)
        /// or accepted (client); null before the first.</summary>
        public NetworkSnapshot CurrentSnapshot
        {
            get { return race != null ? lastPublished : latestSnapshot; }
        }

        /// <summary>Latest processed-input acknowledgment for a
        /// participant: from the host buffer on the host, from the newest
        /// accepted snapshot on a client.</summary>
        public NetworkInputAck AckFor(string participantId)
        {
            if (race != null) return inputs.AckFor(participantId);
            if (latestSnapshot != null && latestSnapshot.acks != null)
                foreach (var ack in latestSnapshot.acks)
                    if (ack.participantId == participantId) return ack;
            return null;
        }

        /// <summary>Authoritative race events pending local consumption —
        /// produced events on the host, deduplicated received events on a
        /// client. Each event surfaces exactly once.</summary>
        public List<DrivingEvent> DrainRaceEvents()
        {
            var drained = new List<DrivingEvent>(drainedEvents);
            drainedEvents.Clear();
            return drained;
        }

        /// <summary>Task 6: the local racer's predicted motor state —
        /// null on the host (authoritative), before the first snapshot
        /// seeds a spawn pose, or while prediction is not running.</summary>
        public MotorState PredictedMotor
        {
            get { return prediction != null ? prediction.Predicted : null; }
        }

        /// <summary>Task 6: local prediction facts for the validation
        /// stream's `prediction` slice — predicted and authoritative
        /// motors, ack horizon, pending buffer, corrections. Null where
        /// no prediction is running: on the host, before the client's
        /// first authoritative snapshot seeds a race, and after the race
        /// ends — the slice may never outlive `networkRace`.</summary>
        public NetworkPredictionState Prediction
        {
            get
            {
                if (prediction == null || latestSnapshot == null) return null;
                return prediction.State();
            }
        }

        /// <summary>Task 6: per-remote-participant interpolation facts
        /// for the validation stream's `remotePresentation` slice.</summary>
        public RemoteInterpolationState RemotePresentation
        {
            get { return interpolation.State(); }
        }

        /// <summary>Estimated host time in seconds (hostTick /
        /// SimulationTicksPerSecond extrapolated by local elapsed time
        /// since the last accepted snapshot).</summary>
        public double EstimatedHostSeconds(double nowSeconds)
        {
            if (double.IsNaN(lastSnapshotReceivedAt)) return 0;
            return lastSnapshotHostTick
                / (double)NetworkSettings.SimulationTicksPerSecond
                + (nowSeconds - lastSnapshotReceivedAt);
        }

        /// <summary>The host-time coordinate remote racers render at:
        /// estimated host time minus the interpolation delay.</summary>
        public double RenderHostTime(double nowSeconds)
        {
            return EstimatedHostSeconds(nowSeconds)
                - NetworkSettings.InterpolationDelaySeconds;
        }

        /// <summary>The interpolated (or held-stale) presentation pose
        /// for a remote participant at the delayed render coordinate;
        /// null before its first buffered snapshot.</summary>
        public MotorState SampleRemotePose(string participantId,
            double nowSeconds)
        {
            return interpolation.Sample(participantId,
                RenderHostTime(nowSeconds));
        }

        /// <summary>Task 6 render seam: the local racer's pose to draw —
        /// the LIVE predicted motor plus the decaying correction offset.
        /// Feeding prediction.Predicted (not a reconcile-time pose) is
        /// the point: between 15 Hz snapshot accepts the rendered car
        /// keeps advancing with local input, while the banked offset
        /// alone guarantees visual continuity across corrections. The
        /// pose the Task-9 LAN screen maps onto the local participant's
        /// vehicle view; null until prediction is seeded.</summary>
        public MotorState SmoothedLocalMotor(double nowSeconds)
        {
            var live = prediction != null ? prediction.Predicted : null;
            if (live == null) return null;
            var dt = double.IsNaN(lastSmoothTime)
                ? 0 : nowSeconds - lastSmoothTime;
            lastSmoothTime = nowSeconds;
            smoothedLocal = smoother.Apply(live, dt);
            return smoothedLocal;
        }

        // Every reconcile: the simulated pose corrects immediately; the
        // rendered pose keeps the offset the player was seeing so the
        // correction eases over CorrectionBlendSeconds — or snaps on
        // race boundary/recovery/>3 m errors.
        private void BlendCorrection()
        {
            var corrected = prediction != null ? prediction.Predicted : null;
            if (corrected == null)
            {
                smoothedLocal = null;
                return;
            }
            if (smoothedLocal != null)
                smoother.Correct(smoothedLocal, corrected,
                    prediction.LastCorrectionSnap);
        }

        /// <summary>Queue a one-shot action (e.g. recover) for the local
        /// racer. The id rides outbound frames — at most
        /// MaxFrameActionIds oldest ids per frame — until the host's
        /// action.queued/consumed/cancelled event acknowledges it;
        /// retried across packet loss and reconnect, never applied twice.
        /// The host's own actions take the identical path.</summary>
        public void SubmitAction(string actionId)
        {
            if (string.IsNullOrEmpty(actionId)
                || actionId.Length > NetworkSettings.ActionIdLengthMax
                || !pendingActions.Add(actionId)) return;
            pendingActionOrder.Add(actionId);
        }

        // --- per-frame boundary ----------------------------------------------

        /// <summary>Call once per Unity frame after the transport pump:
        /// applies lifecycle diffs and flushes staged authoritative
        /// events in reliable ordered batches.</summary>
        public void Pump()
        {
            var snap = session.Snapshot();
            if (snap.role == LanSessionService.RoleHost)
            {
                PumpHost(snap);
                FlushEventBatch();
            }
            else if (snap.role == LanSessionService.RoleClient)
            {
                PumpClient(snap);
            }
            else
            {
                TeardownAll();
            }
        }

        /// <summary>One local 30 Hz logical boundary. Host: validated
        /// input consumption + coordinator step + 15 Hz publish. Client:
        /// one input sample carrying up to four recent frames.</summary>
        public void StepTick(DriverInput localInput, IMotorWorld world)
        {
            var snap = session.Snapshot();
            clientWorld = world;
            if (snap.role == LanSessionService.RoleHost && race != null)
                HostTick(localInput, world, snap);
            else if (snap.role == LanSessionService.RoleClient)
                ClientTick(localInput, snap);
            localTick++;
        }

        // --- host ----------------------------------------------------------

        private void PumpHost(NetworkSessionState snap)
        {
            var racingPhase = snap.phase == LanSessionService.PhaseCountdown
                || snap.phase == LanSessionService.PhaseRacing
                || snap.phase == LanSessionService.PhaseResults;
            if (race == null)
            {
                // A race only ever spawns out of countdown/racing — never
                // during results, lobby, or recovery.
                if (snap.phase == LanSessionService.PhaseCountdown
                    || snap.phase == LanSessionService.PhaseRacing)
                    TryStartRace(snap);
                return;
            }
            if (!racingPhase)
            {
                // Lobby/idle/recovery: the race is gone; the buffer closes
                // so stale traffic cannot reach a future session.
                EndRace();
                return;
            }
            SyncHostControl(snap);
        }

        private void TryStartRace(NetworkSessionState snap)
        {
            var descriptor = snap.descriptor;
            if (descriptor == null || string.IsNullOrEmpty(descriptor.eventId)
                || content == null) return;
            hostSessionId = descriptor.sessionId;
            raceOrdinal++;
            raceId = hostSessionId + "-r" + raceOrdinal.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            var specs = new List<NetworkRacerSpec>();
            foreach (var participant in snap.participants)
            {
                var spec = new NetworkRacerSpec { slot = participant.slot };
                if (participant.occupied)
                {
                    spec.participantId = participant.participantId;
                    spec.vehicleId = string.IsNullOrEmpty(participant.vehicleId)
                        ? DefaultVehicle(descriptor) : participant.vehicleId;
                    spec.controlSource = participant.controlSource;
                }
                else
                {
                    // AI fills vacant slots (Task 4) under a stable
                    // synthetic participant id.
                    spec.participantId = "ai-slot-" + participant.slot.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                    spec.vehicleId = DefaultVehicle(descriptor);
                    spec.controlSource = "ai";
                }
                specs.Add(spec);
            }
            string error;
            NetworkRace created;
            if (!NetworkRace.TryCreate(hostSessionId, raceId, content,
                    descriptor.eventId, descriptor.classId, specs,
                    out created, out error))
            {
                LastError = "raceCreate:" + (error ?? "unknown");
                return;
            }
            race = created;
            inputs.BeginRace(hostSessionId, raceId);
            snapshotSequence = 0;
            eventBatchSequence = 0;
            bridgeEventSequence = 0;
            stagedEvents.Clear();
            appliedEpochs.Clear();
            appliedControl.Clear();
            foreach (var participant in snap.participants)
            {
                if (!participant.occupied) continue;
                appliedEpochs[participant.participantId] = participant.ownershipEpoch;
                appliedControl[participant.participantId] = participant.controlSource;
                if (participant.controlSource == "local"
                    || participant.controlSource == "remote")
                    inputs.SeatParticipant(participant.participantId,
                        participant.ownershipEpoch);
            }
            // The roster freeze is itself a lifecycle fact worth
            // publishing on the reliable channel.
            stagedEvents.Add(new DrivingEvent
            {
                id = raceId + "-bridge-e" + (++bridgeEventSequence).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                tick = 0,
                kind = "race.seated",
                commandId = "",
                detail = raceId,
                participantId = "",
                attribution = ""
            });
        }

        // Participants who never picked a vehicle get the first event-
        // legal vehicle of the lobby-selected class; AI filler slots take
        // the same default so the roster stays honest.
        private string DefaultVehicle(SessionDescriptor descriptor)
        {
            if (descriptor == null || content == null) return "";
            EventDefinition evt;
            if (content.Events != null
                && content.Events.TryGetValue(descriptor.eventId, out evt)
                && evt != null && evt.vehicleIds != null)
            {
                foreach (var vehicleId in evt.vehicleIds)
                    if (VehicleTuning.ClassIdOf(vehicleId)
                            == descriptor.classId)
                        return vehicleId;
            }
            return "";
        }

        private void EndRace()
        {
            race = null;
            // The host simulation dies with the race: a physics world is
            // a whole local scene, so it unloads here — never lazily.
            if (hostSimulation != null) hostSimulation.Dispose();
            hostSimulation = null;
            hostWorld = null;
            inputs.EndRace();
            appliedEpochs.Clear();
            appliedControl.Clear();
            stagedEvents.Clear();
            lastPublished = null;
        }

        // The authoritative world the host steps: built once per race
        // from the injected provider (backend selected by the catalog),
        // or the legacy arcade adapter over the tick's IMotorWorld. The
        // roster initializes from the race's own spawns.
        private IVehicleSimulation EnsureHostSimulation(IMotorWorld world)
        {
            if (SimulationProvider == null && hostSimulation != null
                && hostWorld != world)
            {
                // The legacy adapter binds a specific world; a changed
                // world rebuilds it (the provider path ignores `world`
                // and stays stable).
                hostSimulation.Dispose();
                hostSimulation = null;
            }
            if (hostSimulation != null) return hostSimulation;
            hostWorld = world;
            hostSimulation = SimulationProvider != null
                ? SimulationProvider(content)
                : (world != null
                    ? (IVehicleSimulation)new ArcadeVehicleSimulation(world)
                    : null);
            if (hostSimulation != null && race != null)
                hostSimulation.Initialize(race.VehicleSpawns());
            return hostSimulation;
        }

        // The client prediction world: created lazily once a race exists
        // and the input world is known, disposed on race adoption and
        // teardown. One body — the local participant — so this is never a
        // second authoritative world.
        private IVehicleSimulation EnsurePredictionSimulation()
        {
            if (SimulationProvider == null && predictionSimulation != null
                && predictionWorld != clientWorld)
            {
                predictionSimulation.Dispose();
                predictionSimulation = null;
            }
            if (predictionSimulation != null) return predictionSimulation;
            predictionWorld = clientWorld;
            predictionSimulation = PredictionSimulationProvider != null
                ? PredictionSimulationProvider(content)
                : (SimulationProvider != null
                    ? SimulationProvider(content)
                    : (clientWorld != null
                        ? (IVehicleSimulation)new ArcadeVehicleSimulation(clientWorld)
                        : null));
            return predictionSimulation;
        }

        private void ReleasePredictionSimulation()
        {
            if (predictionSimulation != null) predictionSimulation.Dispose();
            predictionSimulation = null;
            predictionWorld = null;
        }

        private void BindPredictionSimulation()
        {
            if (prediction == null) return;
            var sim = appliedRaceOrdinal >= 0
                ? EnsurePredictionSimulation() : null;
            if (prediction.Simulation != sim)
                prediction.SetSimulation(sim);
        }

        // Pause plus control/epoch diffs apply at a host boundary: the
        // shared pause freezes validated input alongside the race clock,
        // a confirmed disconnect seats the AI and releases everything the
        // dead driver held or queued, and a credentialed rejoin hands the
        // slot back under a bumped epoch — old-epoch input can never
        // steer the reclaimed racer.
        private void SyncHostControl(NetworkSessionState snap)
        {
            race.SetPaused(snap.paused);
            inputs.SetPaused(snap.paused);
            if (snap.paused != appliedPaused)
            {
                appliedPaused = snap.paused;
                // The race clock freezes while paused, so the cadence
                // publish can stall on an odd tick — force the next tick
                // to deliver the paused fact.
                snapshotRequested = true;
            }
            foreach (var participant in snap.participants)
            {
                if (!participant.occupied) continue;
                string previousControl;
                if (appliedControl.TryGetValue(participant.participantId,
                        out previousControl)
                    && previousControl != participant.controlSource)
                {
                    var lostHumanControl =
                        (previousControl == "local" || previousControl == "remote")
                        && participant.controlSource == "ai";
                    // Ownership/control transitions always publish a full
                    // state — a rejoined participant needs it immediately.
                    snapshotRequested = true;
                    race.SetControlSource(participant.participantId,
                        participant.controlSource);
                    if (lostHumanControl)
                    {
                        // Control loss clears held input and queued
                        // one-shots; the released ids are acknowledged as
                        // cancelled so client retry loops terminate.
                        ReleaseParticipantInput(participant.participantId);
                    }
                    else if (participant.controlSource == "local"
                        || participant.controlSource == "remote")
                        inputs.SeatParticipant(participant.participantId,
                            participant.ownershipEpoch);
                }
                else
                {
                    long previousEpoch;
                    if (appliedEpochs.TryGetValue(participant.participantId,
                            out previousEpoch)
                        && previousEpoch != participant.ownershipEpoch)
                    {
                        // Same control source, new epoch (rejoin that
                        // superseded the old connection): fence the lane.
                        // One-shots the superseded connection queued die
                        // with it — acknowledged as cancelled so the old
                        // owner's retries terminate.
                        inputs.SetOwnershipEpoch(participant.participantId,
                            participant.ownershipEpoch);
                        snapshotRequested = true;
                        foreach (var actionId in inputs.DrainActions(
                                participant.participantId))
                        {
                            var cancel = CancelledAction(
                                participant.participantId, actionId,
                                "superseded", race != null ? race.HostTick : 0);
                            stagedEvents.Add(cancel);
                            drainedEvents.Add(cancel);
                        }
                    }
                }
                appliedControl[participant.participantId] = participant.controlSource;
                appliedEpochs[participant.participantId] = participant.ownershipEpoch;
            }
        }

        // Everything the lost driver held or had queued is dropped; its
        // pending one-shots come back as explicit cancellations.
        private void ReleaseParticipantInput(string participantId)
        {
            var released = inputs.ReleaseLane(participantId);
            var hostTick = race != null ? race.HostTick : 0;
            foreach (var actionId in released)
            {
                var cancel = CancelledAction(participantId, actionId,
                    "controlLost", hostTick);
                stagedEvents.Add(cancel);
                drainedEvents.Add(cancel);
            }
        }

        private void HostTick(DriverInput localInput, IMotorWorld world,
            NetworkSessionState snap)
        {
            // The host's local racer rides the identical validated input
            // path as remote clients — no special case.
            var localParticipantId = snap.localParticipantId;
            long epoch;
            if (!string.IsNullOrEmpty(localParticipantId)
                && appliedEpochs.TryGetValue(localParticipantId, out epoch)
                && appliedControl.TryGetValue(localParticipantId, out var control)
                && (control == "local" || control == "remote"))
            {
                var frame = BuildLocalFrame(hostSessionId, raceId,
                    localParticipantId, epoch, localInput);
                string error;
                if (!inputs.TryEnqueue(frame, localParticipantId, out error))
                {
                    InputRejections++;
                    LastInputError = error;
                }
                else if (frame.actionIds.Count > 0)
                {
                    // Loopback path: accepted ids are guaranteed to reach
                    // the pending queue this boundary — stop retransmitting.
                    foreach (var id in frame.actionIds)
                        if (pendingActions.Remove(id))
                            pendingActionOrder.Remove(id);
                }
            }

            var hostTick = race.HostTick + 1;
            var map = new Dictionary<string, DriverInput>(StringComparer.Ordinal);
            foreach (var participantId in appliedControl.Keys)
                map[participantId] = inputs.Consume(participantId, hostTick);
            // Accepted one-shot ids route through the racer's own pending
            // queue — the session consumes at most one per boundary. A
            // declined id (race over, racer finalized, pending queue full)
            // is acknowledged with an explicit action.cancelled event so
            // the client's retry loop terminates instead of retransmitting
            // forever — "acknowledge/retry until applied or cancelled".
            foreach (var participantId in appliedControl.Keys)
            {
                var actions = inputs.DrainActions(participantId);
                foreach (var actionId in actions)
                {
                    // A one-shot that arrived during the shared pause can
                    // never apply — cancel it explicitly instead of letting
                    // it sit latched to fire on resume.
                    if (race.Paused)
                    {
                        var pauseCancel = CancelledAction(participantId,
                            actionId, "paused", hostTick);
                        stagedEvents.Add(pauseCancel);
                        drainedEvents.Add(pauseCancel);
                        continue;
                    }
                    string queueError;
                    if (race.QueuePending(participantId, "recover", actionId,
                            out queueError)) continue;
                    var cancel = CancelledAction(participantId, actionId,
                        queueError, hostTick);
                    stagedEvents.Add(cancel);
                    drainedEvents.Add(cancel);
                }
            }

            var state = race.Step(map, EnsureHostSimulation(world));
            if (state.events != null && state.events.Count > 0)
            {
                // Transient events leave the snapshot channel entirely:
                // they ride the reliable ordered batch and the host
                // consumes them locally exactly once.
                stagedEvents.AddRange(state.events);
                drainedEvents.AddRange(state.events);
                state.events.Clear();
            }
            // A client's resend request publishes immediately instead of
            // waiting for the every-other-tick cadence.
            if (snapshotRequested || race.HostTick % NetworkSettings.SnapshotEveryTicks == 0)
            {
                snapshotRequested = false;
                Publish(BuildSnapshot(state));
            }
            // The coordinator owns the session phase transitions the
            // session service exposes: countdown -> racing once the shared
            // clock runs, racing -> results once the outcome finalizes.
            if (race.Phase == NetworkRace.PhaseRunning
                && snap.phase == LanSessionService.PhaseCountdown)
                session.BeginRace();
            else if (race.Phase == NetworkRace.PhaseFinished
                && snap.phase == LanSessionService.PhaseRacing)
            {
                // The regular every-other-tick publish can skip the
                // final boundary entirely: a race that finalizes on an
                // odd host tick would leave every remote client's last
                // race snapshot at "running" forever — the result and
                // phase facts ride the ledger, but the networkRace slice
                // must land "finished" too, so publish once more
                // explicitly before stepping stops.
                Publish(BuildSnapshot(state));
                // Task 8: the immutable result is retained and offered to
                // every participant before the phase flips — delivery and
                // its persistence acks are the session service's job from
                // here on.
                if (race.Result != null)
                    session.PublishResult(race.Result);
                session.FinishRace();
            }
        }

        private NetworkInputFrame BuildLocalFrame(string sessionId,
            string raceIdentity, string participantId, long epoch,
            DriverInput localInput)
        {
            var frame = new NetworkInputFrame
            {
                sessionId = sessionId,
                raceId = raceIdentity,
                participantId = participantId,
                ownershipEpoch = epoch,
                inputSequence = ++inputSequence,
                clientTick = localTick + 1,
                steering = localInput.Steering,
                drift = localInput.Drift,
                boost = localInput.Boost,
                gadget = localInput.Gadget,
                recover = localInput.Recover,
                changeCamera = localInput.ChangeCamera,
                pause = localInput.Pause,
                brake = localInput.Brake,
                hasThrottleOverride = localInput.HasThrottleOverride,
                throttle = localInput.HasThrottleOverride
                    ? localInput.Throttle
                    : 0f
            };
            // The host rejects the whole frame past MaxFrameActionIds, so
            // a deeper backlog would deadlock the input stream — every
            // frame would fail tooManyActions and no id could ever be
            // acknowledged. Send only the oldest window per frame; each
            // id keeps riding until the host confirms it by event, then
            // the window slides forward to the next ids.
            var actionWindow = Math.Min(pendingActionOrder.Count,
                NetworkSettings.MaxFrameActionIds);
            for (var i = 0; i < actionWindow; i++)
                frame.actionIds.Add(pendingActionOrder[i]);
            return frame;
        }

        private NetworkSnapshot BuildSnapshot(NetworkRaceState state)
        {
            var snapshot = new NetworkSnapshot
            {
                sessionId = hostSessionId,
                raceId = raceId,
                raceOrdinal = raceOrdinal,
                snapshotSequence = ++snapshotSequence,
                state = state
            };
            foreach (var participantId in appliedControl.Keys)
            {
                var ack = inputs.AckFor(participantId);
                if (ack != null) snapshot.acks.Add(ack);
            }
            return snapshot;
        }

        // The host's explicit negative acknowledgment for a dedup'd
        // one-shot the coordinator declined. Ids share the per-race
        // bridge namespace so event identity stays unique and stable.
        private DrivingEvent CancelledAction(string participantId,
            string actionId, string error, long tick)
        {
            return new DrivingEvent
            {
                id = raceId + "-bridge-e" + (++bridgeEventSequence).ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                tick = tick,
                kind = "action.cancelled",
                commandId = actionId ?? "",
                detail = error ?? "rejected",
                participantId = participantId ?? "",
                attribution = "human"
            };
        }

        /// <summary>Publish a full four-racer snapshot to every
        /// authenticated connection on the sequenced-unreliable channel.
        /// The snapshot ceiling is enforced at encode AND at send; an
        /// overflowing snapshot fails explicitly instead of truncating
        /// racer state.</summary>
        public bool Publish(NetworkSnapshot snapshot)
        {
            byte[] packet;
            string error;
            if (!NetworkWireCodec.TryEncodeSnapshot(snapshot, out packet, out error))
            {
                LastError = error ?? "snapshotOverflow";
                return false;
            }
            if (packet.Length > NetworkSettings.SnapshotCeilingBytes)
            {
                LastError = "snapshotOverflow";
                return false;
            }
            lastPublished = snapshot;
            SnapshotsPublished++;
            var sent = false;
            foreach (var connectionId in session.HostConnections())
            {
                var ok = transport.SendGameplay(connectionId, packet);
                if (!ok)
                    LastError = "snapshotSend:" + transport.LastError
                        + " len=" + packet.Length;
                sent |= ok;
            }
            return sent;
        }

        // Reliable ordered delivery of staged authoritative events. Each
        // batch carries identities so consumers apply every event once
        // even across redelivery.
        private void FlushEventBatch()
        {
            if (stagedEvents.Count == 0) return;
            var count = Math.Min(stagedEvents.Count,
                NetworkSettings.EventBatchCapacity);
            byte[] packet = null;
            string error = null;
            while (count > 0)
            {
                var batch = new NetworkEventBatch
                {
                    sessionId = hostSessionId,
                    raceId = raceId,
                    raceOrdinal = raceOrdinal,
                    batchSequence = ++eventBatchSequence
                };
                batch.events.AddRange(stagedEvents.GetRange(0, count));
                if (NetworkWireCodec.TryEncodeEventBatch(batch, out packet, out error))
                    break;
                packet = null;
                count /= 2; // shrink until the typed byte bound holds
                eventBatchSequence--;
            }
            if (packet == null)
            {
                LastError = error ?? "eventBatchOverflow";
                return;
            }
            stagedEvents.RemoveRange(0, count);
            foreach (var connectionId in session.HostConnections())
                if (!transport.Send(connectionId, packet))
                    LastError = "eventSend:" + transport.LastError
                        + " len=" + packet.Length;
        }

        // --- client ----------------------------------------------------------

        private void PumpClient(NetworkSessionState snap)
        {
            // Back in the lobby (or recovery) no race is displayed: clear
            // applied race state so a rematch's first snapshot starts a
            // fresh sequence baseline rather than aliasing the old race.
            var inactive = snap.phase == LanSessionService.PhaseLobby
                || snap.phase == LanSessionService.PhaseIdle
                || snap.phase == LanSessionService.PhaseRecovery;
            if (inactive && appliedRaceOrdinal >= 0)
            {
                appliedRaceOrdinal = -1;
                appliedRaceId = "";
                latestSnapshotSeq = -1;
                latestSnapshot = null;
                recentOut.Clear();
                pendingReconcile = null;
                if (prediction != null)
                {
                    prediction.SetSimulation(null);
                    prediction.Reset();
                }
                interpolation.Reset();
                ResetPresentation();
                ReleasePredictionSimulation();
            }
            // One rendered-frame boundary: the shared replay budget
            // resets here, then the newest coalesced snapshot of the
            // frame reconciles exactly once — a packet burst can never
            // multiply the frame's PhysX replay work.
            if (prediction != null) prediction.BeginFrame();
            if (pendingReconcile != null && prediction != null)
            {
                var snapshot = pendingReconcile;
                pendingReconcile = null;
                BindPredictionSimulation();
                prediction.Reconcile(snapshot, LocalTuning(), clientWorld);
                BlendCorrection();
            }
        }

        private void ResetPresentation()
        {
            smoother.Reset();
            smoothedLocal = null;
            lastSmoothTime = double.NaN;
        }

        private void ClientTick(DriverInput localInput, NetworkSessionState snap)
        {
            var epoch = LocalEpoch(snap);
            if (prediction == null
                || prediction.ParticipantId != snap.localParticipantId)
            {
                prediction = new ClientPrediction(snap.localParticipantId);
                prediction.SetSimulation(null);
            }
            BindPredictionSimulation();
            // An ownership-epoch bump (rejoin supersedes this client)
            // fences the whole pending history: the host will reject
            // old-epoch samples as staleEpoch, so replaying them is dead
            // work — clear and resync under the new epoch.
            if (lastSentEpoch >= 0 && epoch != lastSentEpoch)
            {
                prediction.Reset();
                ResetPresentation();
            }
            var frame = BuildLocalFrame(SessionId(snap), appliedRaceId,
                snap.localParticipantId, epoch, localInput);
            lastSentEpoch = epoch;
            // Predict the fresh sample immediately — the local player
            // feels it this tick, not a round trip later. Only while the
            // authoritative picture says the local racer is live and
            // unpaused: countdown/paused/finished racers are frozen and
            // predicting them would manufacture false corrections.
            if (LocalRacerPredictable(snap))
                prediction.Predict(frame, LocalTuning(), clientWorld);
            recentOut.Add(frame);
            while (recentOut.Count > NetworkSettings.InputPacketSampleCapacity)
                recentOut.RemoveAt(0);
            // No race identity is known until the first authoritative
            // snapshot arrives; sending earlier would only produce
            // guaranteed race-fence rejections on the host.
            if (appliedRaceId.Length == 0 || !transport.IsClient) return;
            byte[] packet;
            string error;
            if (!NetworkWireCodec.TryEncodeInputBatch(recentOut, out packet, out error))
            { LastError = error; return; }
            transport.SendGameplay(packet);
            // The prediction backlog resynchronized: ask the host for an
            // immediate authoritative snapshot instead of waiting for the
            // cadence. The latch clears only once the request actually
            // left this process.
            if (prediction != null && prediction.ResyncRequested)
            {
                byte[] request;
                string requestError;
                if (NetworkWireCodec.TryEncodeResyncRequest(SessionId(snap),
                        appliedRaceId, snap.localParticipantId, epoch,
                        out request, out requestError)
                    && transport.SendGameplay(request))
                    prediction.ClearResyncRequest();
            }
        }

        private string SessionId(NetworkSessionState snap)
        {
            return snap.descriptor != null ? snap.descriptor.sessionId : "";
        }

        private long LocalEpoch(NetworkSessionState snap)
        {
            foreach (var participant in snap.participants)
                if (participant.occupied
                    && participant.participantId == snap.localParticipantId)
                    return participant.ownershipEpoch;
            return 0;
        }

        // The local racer's normalized tuning, resolved the same way the
        // host does — catalog baseline for the vehicle the snapshot
        // reports, never a claimed profile upgrade.
        private VehicleTuning LocalTuning()
        {
            var state = latestSnapshot != null ? latestSnapshot.state : null;
            var localId = session.Snapshot().localParticipantId;
            if (state == null || state.racers == null
                || string.IsNullOrEmpty(localId) || content == null)
                return null;
            foreach (var racer in state.racers)
            {
                if (racer == null || racer.participantId != localId)
                    continue;
                VehicleTuning tuning;
                if (content.Vehicles.TryGetValue(racer.vehicleId ?? "",
                        out tuning))
                    return tuning;
                return null;
            }
            return null;
        }

        // Prediction runs only while the authoritative picture shows the
        // local racer live: race running, unpaused, not finished/DNF.
        private bool LocalRacerPredictable(NetworkSessionState snap)
        {
            var state = latestSnapshot != null ? latestSnapshot.state : null;
            if (state == null || state.paused || state.racers == null
                || state.phase != NetworkRace.PhaseRunning)
                return false;
            foreach (var racer in state.racers)
            {
                if (racer == null
                    || racer.participantId != snap.localParticipantId)
                    continue;
                if (racer.finished || racer.dnf) return false;
                return racer.session != null
                    && racer.session.phase == "running";
            }
            return false;
        }

        private void ApplySnapshot(NetworkSnapshot snapshot)
        {
            var snap = session.Snapshot();
            var descriptor = snap.descriptor;
            if (descriptor == null
                || !string.Equals(snapshot.sessionId, descriptor.sessionId,
                    StringComparison.Ordinal))
            {
                DroppedPackets++; // foreign session
                return;
            }
            if (snapshot.raceOrdinal > appliedRaceOrdinal)
                AdoptRace(snapshot.raceId, snapshot.raceOrdinal);
            else if (snapshot.raceOrdinal < appliedRaceOrdinal
                || snapshot.raceId != appliedRaceId
                || snapshot.snapshotSequence <= latestSnapshotSeq)
            {
                // Reordered or stale-race traffic never rolls the client back.
                StaleSnapshots++;
                return;
            }
            latestSnapshotSeq = snapshot.snapshotSequence;
            latestSnapshot = snapshot;
            // Every accepted snapshot feeds remote interpolation now and
            // queues a local-prediction reconcile for the frame boundary:
            // a burst coalesces to the newest authoritative boundary —
            // restore authoritative, replay unacknowledged — exactly
            // once per frame inside the shared replay budget.
            lastSnapshotHostTick = snapshot.state != null
                ? snapshot.state.hostTick : 0;
            lastSnapshotReceivedAt = NowSeconds();
            interpolation.Push(snapshot);
            pendingReconcile = snapshot;
        }

        private void AdoptRace(string newRaceId, int ordinal)
        {
            appliedRaceId = newRaceId ?? "";
            appliedRaceOrdinal = ordinal;
            latestSnapshotSeq = -1;
            // One-shot retries belong to the race they were issued in; a
            // new race must not replay them.
            pendingActions.Clear();
            pendingActionOrder.Clear();
            // Prediction/interpolation history is per-race: the new race
            // reseeds from its own snapshots.
            if (prediction != null)
            {
                prediction.SetSimulation(null);
                prediction.Reset();
            }
            interpolation.Reset();
            ResetPresentation();
            ReleasePredictionSimulation();
        }

        private void ApplyEventBatch(NetworkEventBatch batch)
        {
            var snap = session.Snapshot();
            var descriptor = snap.descriptor;
            if (descriptor == null
                || !string.Equals(batch.sessionId, descriptor.sessionId,
                    StringComparison.Ordinal))
            {
                DroppedPackets++;
                return;
            }
            if (batch.raceOrdinal < appliedRaceOrdinal) return; // stale race
            if (batch.raceOrdinal > appliedRaceOrdinal)
                AdoptRace(batch.raceId, batch.raceOrdinal);
            foreach (var ev in batch.events)
            {
                if (ev == null || string.IsNullOrEmpty(ev.id)) continue;
                if (!seenEventIds.Add(ev.id)) { DroppedEvents++; continue; }
                seenEventOrder.Enqueue(ev.id);
                while (seenEventOrder.Count > NetworkSettings.SeenEventIdCapacity)
                    seenEventIds.Remove(seenEventOrder.Dequeue());
                drainedEvents.Add(ev);
                // Applied or cancelled one-shot ids stop retrying the
                // moment the host confirms them by event identity.
                if (ev.commandId != null && ev.commandId.Length > 0
                    && (ev.kind == "action.queued" || ev.kind == "action.consumed"
                        || ev.kind == "action.cancelled")
                    && pendingActions.Remove(ev.commandId))
                    pendingActionOrder.Remove(ev.commandId);
            }
        }

        // --- transport callbacks --------------------------------------------

        private void OnGameplayMessage(ulong connectionId, byte[] packet)
        {
            GameplayPacketsSeen++;
            LastGameplayConnection = connectionId;
            if (!NetworkWireCodec.IsGameplayPacket(packet))
            {
                DroppedPackets++;
                return;
            }
            var role = session.Snapshot().role;
            switch (NetworkWireCodec.TagOf(packet))
            {
                case NetworkWireCodec.TagInputBatch:
                    if (role != LanSessionService.RoleHost) { DroppedPackets++; return; }
                    HostInputBatch(connectionId, packet);
                    return;
                case NetworkWireCodec.TagResyncRequest:
                    if (role != LanSessionService.RoleHost) { DroppedPackets++; return; }
                    HostResyncRequest(connectionId, packet);
                    return;
                case NetworkWireCodec.TagSnapshot:
                    if (role != LanSessionService.RoleClient) { DroppedPackets++; return; }
                    string error;
                    NetworkSnapshot snapshot;
                    if (!NetworkWireCodec.TryDecodeSnapshot(packet, out snapshot, out error))
                    {
                        DroppedPackets++;
                        LastError = error;
                        return;
                    }
                    ApplySnapshot(snapshot);
                    return;
                default:
                    DroppedPackets++;
                    return;
            }
        }

        private void HostInputBatch(ulong connectionId, byte[] packet)
        {
            List<NetworkInputFrame> frames;
            string error;
            if (!NetworkWireCodec.TryDecodeInputBatch(packet, out frames, out error))
            {
                DroppedPackets++;
                LastError = error;
                return;
            }
            // Ownership derives from the authenticated connection — a
            // payload claiming another participant is foreign input.
            var participantId = session.ParticipantIdFor(connectionId);
            foreach (var frame in frames)
            {
                string frameError;
                if (!inputs.TryEnqueue(frame, participantId ?? "", out frameError))
                {
                    InputRejections++;
                    LastInputError = frameError;
                }
            }
        }

        // The prediction-resync wire: only a currently authenticated
        // participant in the live session/race may ask for a snapshot —
        // anything else is fenced like stale input.
        private void HostResyncRequest(ulong connectionId, byte[] packet)
        {
            NetworkWireCodec.ResyncBody body;
            string error;
            if (!NetworkWireCodec.TryDecodeResyncRequest(packet, out body,
                    out error))
            {
                DroppedPackets++;
                LastError = error;
                return;
            }
            var participantId = session.ParticipantIdFor(connectionId);
            if (string.IsNullOrEmpty(participantId)
                || body.participantId != participantId
                || body.sessionId != hostSessionId
                || body.raceId != raceId)
            {
                DroppedPackets++;
                return;
            }
            // The claimed ownership epoch must match the live roster — a
            // resync sent by the superseded side of a reconnect carries
            // the retired epoch and must not force a publish.
            var roster = session.Snapshot();
            foreach (var participant in roster.participants)
            {
                if (!participant.occupied
                    || participant.participantId != participantId) continue;
                if (participant.ownershipEpoch != body.ownershipEpoch)
                {
                    DroppedPackets++;
                    return;
                }
                break;
            }
            snapshotRequested = true;
        }

        private void OnReliableMessage(ulong connectionId, byte[] payload)
        {
            // Shared reliable channel: FRN2 text belongs to the session
            // protocol (LanSessionLink); only gameplay envelopes apply here.
            if (!NetworkWireCodec.IsGameplayPacket(payload)) return;
            if (NetworkWireCodec.TagOf(payload) == NetworkWireCodec.TagEventBatch
                && session.Snapshot().role == LanSessionService.RoleClient)
            {
                NetworkEventBatch batch;
                string error;
                if (!NetworkWireCodec.TryDecodeEventBatch(payload, out batch, out error))
                {
                    DroppedPackets++;
                    LastError = error;
                    return;
                }
                ApplyEventBatch(batch);
            }
        }

        private void TeardownAll()
        {
            EndRace();
            latestSnapshot = null;
            appliedRaceId = "";
            appliedRaceOrdinal = -1;
            latestSnapshotSeq = -1;
            recentOut.Clear();
            pendingReconcile = null;
            pendingActions.Clear();
            pendingActionOrder.Clear();
            seenEventIds.Clear();
            seenEventOrder.Clear();
            if (prediction != null)
            {
                prediction.SetSimulation(null);
                prediction.Reset();
            }
            interpolation.Reset();
            ResetPresentation();
            ReleasePredictionSimulation();
            lastSentEpoch = -1;
            lastSnapshotHostTick = 0;
            lastSnapshotReceivedAt = double.NaN;
            inputSequence = 0;
        }

        public void Dispose()
        {
            transport.GameplayMessage -= OnGameplayMessage;
            transport.Message -= OnReliableMessage;
            if (hostSimulation != null) hostSimulation.Dispose();
            hostSimulation = null;
            ReleasePredictionSimulation();
        }
    }
}
