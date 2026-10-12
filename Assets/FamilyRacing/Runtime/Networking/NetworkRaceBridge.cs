using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Networking
{
    /// <summary>
    /// Task-5 replication spine: the authoritative Task-3
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
    /// Security posture: the host accepts only N5IB input batches from
    /// clients, resolves the owning participant from the authenticated
    /// connection (never the payload), and validates each frame through
    /// <see cref="HostInputBuffer"/>. Clients cannot submit race progress,
    /// results, or rewards — the wire vocabulary cannot express them.
    /// </summary>
    public sealed class NetworkRaceBridge : IDisposable
    {
        private readonly LanTransport transport;
        private readonly LanSessionService session;
        private DrivingContentCatalog content;

        // --- host -------------------------------------------------------
        private NetworkRace race;
        private readonly HostInputBuffer inputs = new HostInputBuffer();
        private string hostSessionId = "";
        private string raceId = "";
        private int raceOrdinal;
        private long snapshotSequence;
        private long eventBatchSequence;
        private readonly List<DrivingEvent> stagedEvents = new List<DrivingEvent>();
        // participantId -> applied session facts; diffed every Pump so a
        // control transfer or epoch bump is applied at a host boundary.
        private readonly Dictionary<string, long> appliedEpochs =
            new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> appliedControl =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private NetworkSnapshot lastPublished;

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
        // ride every outbound frame until an event batch confirms them —
        // they survive replay and reconnect boundaries.
        private readonly HashSet<string> pendingActions =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> pendingActionOrder = new List<string>();
        private readonly HashSet<string> seenEventIds =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> seenEventOrder = new Queue<string>();

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

        /// <summary>Queue a one-shot action (e.g. recover) for the local
        /// racer. The id rides every outbound frame until the host's
        /// action.queued/consumed/cancelled event acknowledges it —
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
                id = raceId + "-bridge-e1",
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
            inputs.EndRace();
            appliedEpochs.Clear();
            appliedControl.Clear();
            stagedEvents.Clear();
            lastPublished = null;
        }

        // Pause plus control/epoch diffs apply at a host boundary: a
        // confirmed disconnect seats the AI, a credentialed rejoin hands
        // the slot back under a bumped epoch — old-epoch input can never
        // steer the reclaimed racer.
        private void SyncHostControl(NetworkSessionState snap)
        {
            race.SetPaused(snap.paused);
            foreach (var participant in snap.participants)
            {
                if (!participant.occupied) continue;
                string previousControl;
                if (appliedControl.TryGetValue(participant.participantId,
                        out previousControl)
                    && previousControl != participant.controlSource)
                {
                    race.SetControlSource(participant.participantId,
                        participant.controlSource);
                    if (participant.controlSource == "local"
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
                        inputs.SetOwnershipEpoch(participant.participantId,
                            participant.ownershipEpoch);
                    }
                }
                appliedControl[participant.participantId] = participant.controlSource;
                appliedEpochs[participant.participantId] = participant.ownershipEpoch;
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
            // queue — the session consumes at most one per boundary.
            foreach (var participantId in appliedControl.Keys)
            {
                var actions = inputs.DrainActions(participantId);
                foreach (var actionId in actions)
                {
                    string ignored;
                    race.QueuePending(participantId, "recover", actionId,
                        out ignored);
                }
            }

            var state = race.Step(map, world);
            if (state.events != null && state.events.Count > 0)
            {
                // Transient events leave the snapshot channel entirely:
                // they ride the reliable ordered batch and the host
                // consumes them locally exactly once.
                stagedEvents.AddRange(state.events);
                drainedEvents.AddRange(state.events);
                state.events.Clear();
            }
            if (race.HostTick % NetworkSettings.SnapshotEveryTicks == 0)
                Publish(BuildSnapshot(state));
            // The coordinator owns the session phase transitions the
            // session service exposes: countdown -> racing once the shared
            // clock runs, racing -> results once the outcome finalizes.
            if (race.Phase == NetworkRace.PhaseRunning
                && snap.phase == LanSessionService.PhaseCountdown)
                session.BeginRace();
            else if (race.Phase == NetworkRace.PhaseFinished
                && snap.phase == LanSessionService.PhaseRacing)
                session.FinishRace();
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
                pause = localInput.Pause
            };
            foreach (var id in pendingActionOrder) frame.actionIds.Add(id);
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

        /// <summary>Publish a full four-racer snapshot to every
        /// authenticated connection on the sequenced-unreliable channel.
        /// The 16 KiB ceiling is enforced at encode AND at send; an
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
            }
        }

        private void ClientTick(DriverInput localInput, NetworkSessionState snap)
        {
            var frame = BuildLocalFrame(SessionId(snap), appliedRaceId,
                snap.localParticipantId, LocalEpoch(snap), localInput);
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

        private void OnReliableMessage(ulong connectionId, byte[] payload)
        {
            // Shared reliable channel: FRN1 text belongs to the session
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
            pendingActions.Clear();
            pendingActionOrder.Clear();
            seenEventIds.Clear();
            seenEventOrder.Clear();
            inputSequence = 0;
        }

        public void Dispose()
        {
            transport.GameplayMessage -= OnGameplayMessage;
            transport.Message -= OnReliableMessage;
        }
    }
}
