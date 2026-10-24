using System;
using System.Collections.Generic;
using System.Globalization;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Networking
{
    // Core-owned transport seam consumed by the Runtime adapter
    // (LanTransport/LanDiscovery) and by in-memory fakes in tests. Payloads
    // are opaque encoded NetworkProtocol messages; connection ids are
    // transport identities only — the service maps them to participants
    // and never trusts a payload to claim ownership.
    public interface ILanSessionLink
    {
        // Host bind+listen; boundPort reports the real port (needed when
        // the caller asked for an ephemeral one). Failure must leave no
        // open socket.
        bool StartHost(ushort port, out ushort boundPort, out string error);
        // Begin an outbound join; completion arrives through
        // OnLinkConnected/OnLinkDisconnected.
        bool StartClient(string address, ushort port, out string error);
        void Stop();
        bool Send(ulong connectionId, string payload);
    }

    // Bounded discovery seam: searching for nearby hosts and advertising
    // this process's own host socket. `compat` is the full compatibility
    // token (gameplay | content | fingerprint) the responder advertises —
    // a protocol-2 advertisement always carries it, so a discovered host
    // whose token does not match exactly is fenced before any dial.
    public interface ILanDiscoveryProbe
    {
        bool StartSearch(out string error);
        void StopSearch();
        bool StartResponder(ushort gamePort, string hostName,
            string compat, out string error);
        void StopResponder();
        IReadOnlyList<LanDiscoveredHost> Entries { get; }
    }

    // Unity-free LAN session policy: strict phase transitions, a four-slot
    // roster, exact-compatibility admission, shared pause policy, and the
    // asynchronous command lifecycle (accepted -> terminal outcome exactly
    // once). Wall time only advances through AdvanceWallTime so joins and
    // requests resolve inside the milestone's 5-second budgets without
    // real clocks or threads. All socket work goes through the injected
    // seams; this type never references UnityEngine, NGO, or UTP.
    public sealed class LanSessionService
    {
        public const string RoleNone = "none";
        public const string RoleHost = "host";
        public const string RoleClient = "client";

        public const string PhaseIdle = "idle";
        public const string PhaseDiscovering = "discovering";
        public const string PhaseConnecting = "connecting";
        public const string PhaseLobby = "lobby";
        public const string PhaseCountdown = "countdown";
        public const string PhaseRacing = "racing";
        public const string PhaseResults = "results";
        public const string PhaseReconnecting = "reconnecting";
        public const string PhaseRecovery = "recovery";

        private const string ControlLocal = "local";
        private const string ControlRemote = "remote";
        private const string ControlAi = "ai";
        private const string ControlNone = "none";

        private const int MaxIdentityChars = 64;

        private sealed class PendingOp
        {
            internal string commandId;
            internal string action;
            internal double startedSeconds;
            // Join-kind ops own the connecting/reconnecting transport
            // attempt; request-kind ops are in-session host round-trips.
            internal bool joinKind;
        }

        private readonly ILanSessionLink link;
        private readonly ILanDiscoveryProbe discovery;
        private readonly string gameplayVersion;
        private readonly List<PendingOp> pending = new List<PendingOp>();
        private readonly List<CommandResult> outcomes = new List<CommandResult>();
        private readonly ParticipantState[] slots =
            new ParticipantState[NetworkSettings.ParticipantCapacity];
        // Host side: transport ids mapped to authenticated participants;
        // a connection that has not completed join lives only in
        // openConnections and can never steer a racer.
        private readonly Dictionary<ulong, string> connections =
            new Dictionary<ulong, string>();
        private readonly HashSet<ulong> openConnections = new HashSet<ulong>();
        // Host side: participant -> owning connection, for fencing the
        // superseded link when a credentialed rejoin lands before the old
        // socket times out.
        private readonly Dictionary<string, ulong> connectionIdFor =
            new Dictionary<string, ulong>(StringComparer.Ordinal);
        // Host side: credentialed slot reservations live in the registry.
        // They never enter the descriptor, the roster, or any observation
        // DTO — only join.request/join.accept carry them on the wire.
        private readonly ReconnectRegistry reconnectRegistry =
            new ReconnectRegistry();
        private readonly List<string> pauseRequests = new List<string>();
        // Task 8: immutable finalized results retained for the whole host
        // session — across rematches and restarts of clients — each with
        // a per-participant delivery ledger. Results are never silently
        // evicted; a full ledger refuses new starts instead.
        private readonly List<RaceResult> retainedResults =
            new List<RaceResult>();
        // Client side: results already offered by the host, retained so
        // redelivery and retries replay the receipt instead of re-awarding.
        private readonly List<RaceResult> deliveredResults =
            new List<RaceResult>();
        // Host side: participants who explicitly left the session
        // (lan.leave/MsgLeave) — permanent departures, distinct from a
        // transient link drop that still owns a reserved slot. Their
        // outstanding delivery rows settle as terminal "undeliverable"
        // instead of retrying a participant that can never come back.
        // The mark retires the moment a deliberate return lands — a
        // successful fresh join or credentialed reclaim with the same
        // participant id — so future results are deliverable again while
        // the abandoned rows stay terminal.
        private readonly HashSet<string> departedParticipants =
            new HashSet<string>(StringComparer.Ordinal);
        // Composition seam: applies a finalized result to the selected
        // profile through the atomic store write. FoundationService wires
        // it at runtime; tests substitute the same service directly.
        private Func<RaceResult, RewardApplyResult> rewardApplier;
        // Composition seam (M5 Task 4): binds and durably stores the
        // local reward context for one host-allocated race identity
        // BEFORE that participant acknowledges start readiness. Returns
        // an error string on failure — a failed preparation on the host
        // refuses lan.start; on a client it reports failure and keeps
        // that participant from starting (it leaves the session).
        // Invoked with the race identity only: the profile's captured
        // bounty definitions never leave the local device.
        private Func<string, string> racePreparer;
        // Host-side race ordinal: lan.start allocates
        // "<sessionId>-r<N>" so the identity is fixed before any
        // participant begins the race — the coordinator consumes the
        // same identity via the broadcast session state.
        private int raceOrdinal;
        private string currentRaceId = "";
        private string preparedRaceId = "";

        private DrivingContentCatalog content;
        // The bound gameplay catalog (M5 Task 10): when non-legacy it
        // supplies the admission fingerprint so peers prove identical
        // rules — objectives, economy, gadgets and difficulty — before
        // a seat is allocated, not just identical driving data. Null or
        // legacy catalogs keep driving-fingerprint admission so
        // unupgraded peers stay interoperable.
        private GameplayCatalog gameplayContent;
        private string role = RoleNone;
        private string phase = PhaseIdle;
        private double now;
        // Armed when the host broadcasts session.closed: the link stays up
        // for one flush window so the reliable close datagram is actually
        // delivered before Teardown() stops the transport.
        private double closeFlushUntil = double.NegativeInfinity;
        private string lastError = "";
        private string sessionId = "";
        private SessionDescriptor descriptor;
        private string localParticipantId = "";
        private string localAvatar = "";
        private int localColorIndex;
        private bool profileSelected;
        private bool profileWritable;
        private ulong serverConnection;
        private bool paused;
        // Private client reconnect material (stored credential + endpoint);
        // held in memory and mirrored into the profile-scoped credential
        // store so a client-process restart can reclaim its slot. An
        // explicit lan.leave removes both copies.
        private string storedSessionId;
        private string storedJoinAddress;
        private int storedJoinPort;
        private string storedCredential;
        private IReconnectCredentialStore reconnectStore;
        private bool reconnectStoreChecked;
        private string joinAddress = "";
        private int joinPort;

        public LanSessionService(ILanSessionLink link, ILanDiscoveryProbe discovery,
            string gameplayVersion)
        {
            if (link == null) throw new ArgumentNullException("link");
            this.link = link;
            this.discovery = discovery;
            this.gameplayVersion = gameplayVersion ?? "";
            for (var i = 0; i < slots.Length; i++)
                slots[i] = new ParticipantState { slot = i };
        }

        public string Role { get { return role; } }
        public string Phase { get { return phase; } }
        public bool InSession { get { return role != RoleNone; } }

        // The catalog defines which events/vehicles a lobby may configure;
        // its Version is the exact-match content fingerprint for joins.
        public void SetContent(DrivingContentCatalog catalog)
        {
            content = catalog;
        }

        // The gameplay catalog that owns the admission fingerprint when
        // bound and non-legacy; null clears the binding back to
        // driving-fingerprint admission.
        public void SetGameplayContent(GameplayCatalog catalog)
        {
            gameplayContent = catalog;
        }

        // Composition seam for private reconnect-credential persistence —
        // the runtime wires a file-backed, profile-scoped store; tests
        // substitute memory. Credential bytes never leave this seam into
        // observation state, streams, or logs.
        public void SetReconnectStore(IReconnectCredentialStore store)
        {
            reconnectStore = store;
        }

        // Identity of the local human: stable participant id plus cosmetic
        // identity and profile readiness. The caller refreshes this at each
        // command boundary so a profile change is seen by the next check.
        public void SetLocalContext(string participantId, string avatar,
            int colorIndex, bool selected, bool writable)
        {
            if ((participantId ?? "") != localParticipantId)
                reconnectStoreChecked = false;
            localParticipantId = participantId ?? "";
            localAvatar = avatar ?? "";
            localColorIndex = colorIndex;
            profileSelected = selected;
            profileWritable = writable;
        }

        // Composition seam for Task 8: applies an immutable finalized
        // result to the local profile (coins + receipt, atomically). Null
        // means this process cannot persist rewards — deliveries then
        // stay honest pending/failed instead of pretending to persist.
        public void SetRewardApplier(
            Func<RaceResult, RewardApplyResult> applier)
        {
            rewardApplier = applier;
        }

        // Composition seam (M5 Task 4): the local reward-context bind.
        // Called with the host-allocated race identity as soon as it is
        // known — on the host inside lan.start before the race begins,
        // on a client when the broadcast countdown phase arrives — so a
        // preparation save failure reports failure and keeps that
        // participant from starting. Must be idempotent per identity:
        // reclaim/rejoin and repeated session-state broadcasts can
        // present the same raceId more than once.
        public void SetRacePreparer(Func<string, string> preparer)
        {
            racePreparer = preparer;
        }

        public CommandResult Execute(GameCommand command)
        {
            if (command == null || string.IsNullOrEmpty(command.Action))
                return Result(null, CommandResult.Rejected, "invalidCommand");
            // A pending close only keeps the transport alive to flush the
            // final reliable datagram; the session itself is already over.
            // Any new command takes precedence over the flush — tear the
            // link down now rather than blocking the user behind the
            // flush window.
            if (closeFlushUntil != double.NegativeInfinity)
                Teardown();
            switch (command.Action)
            {
                case "lan.host": return Host(command);
                case "lan.discover": return Discover(command);
                case "lan.discovery.stop": return DiscoveryStop(command);
                case "lan.join": return Join(command);
                case "lan.configure": return Configure(command);
                case "lan.vehicle": return Vehicle(command);
                case "lan.ready": return Ready(command);
                case "lan.start": return StartRace(command);
                case "lan.pause.request": return PauseRequest(command);
                case "lan.pause.set": return PauseSet(command);
                case "pause.toggle": return PauseToggle(command);
                case "lan.reconnect": return Reconnect(command);
                case "lan.results.request": return ResultsRequest(command);
                case "lan.results.retrySave": return ResultsRetrySave(command);
                case "lan.rematch": return Rematch(command);
                case "lan.leave": return Leave(command);
                default: return Result(command, CommandResult.Rejected, "unknownAction");
            }
        }

        // Monotonic wall-clock advance: pending operations expire exactly
        // once inside the milestone's 5-second budgets.
        public void AdvanceWallTime(double monotonicSeconds)
        {
            if (monotonicSeconds > now) now = monotonicSeconds;
            if (closeFlushUntil != double.NegativeInfinity
                && now >= closeFlushUntil)
            {
                Teardown();
                return;
            }
            var expired = new List<PendingOp>();
            foreach (var op in pending)
            {
                var budget = op.joinKind
                    ? NetworkSettings.JoinTimeoutSeconds
                    : NetworkSettings.RequestTimeoutSeconds;
                if (now - op.startedSeconds >= budget) expired.Add(op);
            }
            foreach (var op in expired)
            {
                if (op.joinKind)
                {
                    CompletePending(op, CommandResult.Failed, "joinTimeout");
                    // A dead join attempt frees the socket and returns the
                    // client to idle — never lingers as a phantom session.
                    link.Stop();
                    serverConnection = 0;
                    role = RoleNone;
                    phase = PhaseIdle;
                }
                else
                {
                    CompletePending(op, CommandResult.Failed, "requestTimeout");
                }
            }
            // Unacknowledged result offers are retransmitted on a slow
            // cadence: the offer is immutable, so a resend is byte-for-byte
            // identical and the client's receipt dedup makes it safe.
            if (role == RoleHost) OfferPending(now);
        }

        public NetworkSessionState Snapshot()
        {
            RefreshDescriptor();
            var state = new NetworkSessionState
            {
                role = role,
                phase = phase,
                localParticipantId = role != RoleNone ? localParticipantId : "",
                // The allocated race identity stays visible through
                // results so observers correlate deliveries; it clears
                // when the session returns to lobby or tears down.
                currentRaceId = currentRaceId,
                descriptor = descriptor != null ? descriptor.Copy() : null,
                paused = paused,
                lastError = lastError
            };
            foreach (var slot in slots) state.participants.Add(slot.Copy());
            foreach (var requester in pauseRequests) state.pauseRequests.Add(requester);
            foreach (var op in pending)
            {
                state.pendingOperations.Add(new PendingOperationState
                {
                    commandId = op.commandId,
                    action = op.action,
                    startedSeconds = op.startedSeconds
                });
            }
            if (discovery != null && discovery.Entries != null)
                foreach (var entry in discovery.Entries)
                    if (entry != null) state.discovered.Add(entry.Copy());
            // Task 8: expose the delivery ledger honestly — pending,
            // failed, and persisted are observable, never implied.
            if (role == RoleHost)
            {
                foreach (var result in retainedResults)
                    foreach (var delivery in result.deliveries)
                        state.results.Add(new ResultDeliveryState
                        {
                            resultId = result.resultId,
                            raceId = result.raceId,
                            participantId = delivery.participantId,
                            state = delivery.state,
                            code = delivery.code
                        });
            }
            else if (role == RoleClient)
            {
                foreach (var result in deliveredResults)
                    state.results.Add(new ResultDeliveryState
                    {
                        resultId = result.resultId,
                        raceId = result.raceId,
                        participantId = localParticipantId,
                        state = result.deliveryStatus,
                        code = result.deliveryCode
                    });
            }
            return state;
        }

        // Task 5: host-side resolution of a transport connection to its
        // authenticated participant — null while the connection is
        // unauthenticated (open but not joined). Gameplay validation
        // derives ownership from this lookup, never from payload identity
        // fields.
        public string ParticipantIdFor(ulong connectionId)
        {
            string participantId;
            return connections.TryGetValue(connectionId, out participantId)
                ? participantId : null;
        }

        // Task 5: host fan-out — a snapshot of the authenticated
        // connection ids for reliable/unreliable sends. Callers must not
        // cache the list across transport callbacks.
        public List<ulong> HostConnections()
        {
            return new List<ulong>(connections.Keys);
        }

        // Terminal outcomes for operations accepted earlier; each is
        // produced exactly once, in completion order.
        public IReadOnlyList<CommandResult> DrainOutcomes()
        {
            var drained = new List<CommandResult>(outcomes);
            outcomes.Clear();
            return drained;
        }

        // Availability answers the same codes Execute would reject with,
        // so discovery records an honest reason for every lan.* action.
        public void Availability(string action, out bool available, out string reason)
        {
            available = true;
            reason = "";
            switch (action)
            {
                case "lan.host":
                    if (role != RoleNone) { Deny(out available, out reason, "inSession"); }
                    else if (!profileWritable) { Deny(out available, out reason, "requiresSelectedProfile"); }
                    break;
                case "lan.discover":
                    if (discovery == null) { Deny(out available, out reason, "unavailable"); }
                    else if (role != RoleNone) { Deny(out available, out reason, "inSession"); }
                    else if (phase == PhaseDiscovering) { Deny(out available, out reason, "alreadyDiscovering"); }
                    break;
                case "lan.discovery.stop":
                    if (phase != PhaseDiscovering) { Deny(out available, out reason, "notDiscovering"); }
                    break;
                case "lan.join":
                    if (role != RoleNone || phase == PhaseConnecting || phase == PhaseReconnecting)
                    { Deny(out available, out reason, "inSession"); }
                    else if (!profileSelected) { Deny(out available, out reason, "requiresSelectedProfile"); }
                    break;
                case "lan.reconnect":
                    EnsureReconnectMaterial();
                    if (storedCredential == null) { Deny(out available, out reason, "noSession"); }
                    break;
                case "lan.configure":
                case "lan.start":
                case "lan.pause.set":
                case "lan.rematch":
                    if (role == RoleNone) { Deny(out available, out reason, "noSession"); }
                    else if (role != RoleHost) { Deny(out available, out reason, "hostOnly"); }
                    else if (action == "lan.rematch" && phase != PhaseResults)
                    { Deny(out available, out reason, "invalidPhase"); }
                    else if (action != "lan.rematch" && phase != PhaseLobby)
                    { Deny(out available, out reason, "invalidPhase"); }
                    else if (action == "lan.start"
                        && retainedResults.Count >= NetworkSettings.ResultRetentionCapacity)
                    { Deny(out available, out reason, "sessionFull"); }
                    break;
                case "lan.vehicle":
                case "lan.ready":
                    if (role == RoleNone) { Deny(out available, out reason, "noSession"); }
                    else if (phase != PhaseLobby) { Deny(out available, out reason, "invalidPhase"); }
                    break;
                case "lan.pause.request":
                case "pause.toggle":
                    if (role == RoleNone) { Deny(out available, out reason, "noSession"); }
                    else if (!PausePhase(phase)) { Deny(out available, out reason, "invalidPhase"); }
                    break;
                case "lan.results.request":
                    if (role == RoleNone) { Deny(out available, out reason, "noSession"); }
                    else if (role == RoleHost && retainedResults.Count == 0)
                    { Deny(out available, out reason, "noResult"); }
                    else if (role == RoleClient && !ResultPhase(phase))
                    { Deny(out available, out reason, "invalidPhase"); }
                    break;
                case "lan.results.retrySave":
                    if (role == RoleNone) { Deny(out available, out reason, "noSession"); }
                    else if (!HasFailedLocalDelivery())
                    { Deny(out available, out reason, "noPendingResult"); }
                    break;
                case "lan.leave":
                    if (role == RoleNone) { Deny(out available, out reason, "noSession"); }
                    break;
                default:
                    Deny(out available, out reason, "unknownAction");
                    break;
            }
        }

        // --- transport callbacks (Runtime adapter pumps these on the
        // game thread; tests invoke them directly) ------------------------

        public void OnLinkConnected(ulong connectionId)
        {
            if (role == RoleHost)
            {
                openConnections.Add(connectionId);
                return;
            }
            if (role == RoleClient
                && (phase == PhaseConnecting || phase == PhaseReconnecting))
            {
                serverConnection = connectionId;
                var credential = phase == PhaseReconnecting ? storedCredential : "";
                var op = FindJoinOp();
                link.Send(connectionId, NetworkProtocol.Encode(
                    NetworkProtocol.MsgJoinRequest,
                    "v", NetworkSettings.LanProtocolVersion.ToString(CultureInfo.InvariantCulture),
                    "gv", gameplayVersion,
                    "cv", ContentVersion(),
                    "fp", AdmissionFingerprint(),
                    "pid", localParticipantId,
                    "av", localAvatar,
                    "ci", localColorIndex.ToString(CultureInfo.InvariantCulture),
                    "cid", op != null ? op.commandId : "",
                    "cred", credential ?? ""));
            }
        }

        public void OnLinkMessage(ulong connectionId, string payload)
        {
            string type;
            Dictionary<string, string> fields;
            if (!NetworkProtocol.TryDecode(payload, out type, out fields))
            {
                lastError = "badMessage";
                return;
            }
            if (role == RoleHost)
            {
                switch (type)
                {
                    case NetworkProtocol.MsgJoinRequest:
                        HandleJoinRequest(connectionId, fields);
                        return;
                    case NetworkProtocol.MsgReadySet:
                        HandleClientReady(connectionId, fields);
                        return;
                    case NetworkProtocol.MsgVehicleSet:
                        HandleClientVehicle(connectionId, fields);
                        return;
                    case NetworkProtocol.MsgPauseRequest:
                        HandleClientPauseRequest(connectionId, fields);
                        return;
                    case NetworkProtocol.MsgLeave:
                        HandleClientLeave(connectionId);
                        return;
                    case NetworkProtocol.MsgResultRequest:
                        HandleResultRequest(connectionId, fields);
                        return;
                    case NetworkProtocol.MsgResultAck:
                        HandleResultAck(connectionId, fields);
                        return;
                    default:
                        return;
                }
            }
            if (role == RoleClient && connectionId == serverConnection)
            {
                switch (type)
                {
                    case NetworkProtocol.MsgJoinAccept:
                        HandleJoinAccept(fields);
                        return;
                    case NetworkProtocol.MsgJoinReject:
                        HandleJoinReject(fields);
                        return;
                    case NetworkProtocol.MsgSessionState:
                        HandleSessionState(fields);
                        return;
                    case NetworkProtocol.MsgOpResult:
                        HandleOpResult(fields);
                        return;
                    case NetworkProtocol.MsgResultOffer:
                        HandleResultOffer(fields);
                        return;
                    case NetworkProtocol.MsgSessionClosed:
                        HandleSessionClosed(fields);
                        return;
                    default:
                        return;
                }
            }
        }

        public void OnLinkDisconnected(ulong connectionId, string reason)
        {
            openConnections.Remove(connectionId);
            if (role == RoleHost)
            {
                string participantId;
                if (!connections.TryGetValue(connectionId, out participantId)) return;
                connections.Remove(connectionId);
                connectionIdFor.Remove(participantId);
                var participant = FindParticipant(participantId);
                if (participant == null) return;
                HostParticipantDisconnected(participant, participantId);
                return;
            }
            if (role != RoleClient || connectionId != serverConnection) return;
            serverConnection = 0;
            var join = FindJoinOp();
            if (join != null)
            {
                CompletePending(join, CommandResult.Failed, "connectFailed");
                role = RoleNone;
                phase = PhaseIdle;
                return;
            }
            if (InSession)
            {
                phase = PhaseRecovery;
                lastError = string.IsNullOrEmpty(reason) ? "hostLost" : reason;
                var local = FindParticipant(localParticipantId);
                if (local != null) local.connected = false;
                if (descriptor != null) descriptor.joinable = false;
                FailPending("hostLost");
            }
        }

        // Participant-keyed disconnect policy for the host — the call a
        // heartbeat or peer-timeout monitor makes when it knows which
        // participant went silent rather than which socket died. Any link
        // still mapped to the participant is severed alongside, so the
        // slot can never be double-owned.
        public void OnDisconnected(string participantId, string reason)
        {
            if (role != RoleHost || string.IsNullOrEmpty(participantId)) return;
            ulong connectionId;
            if (connectionIdFor.TryGetValue(participantId, out connectionId))
            {
                connections.Remove(connectionId);
                openConnections.Remove(connectionId);
                connectionIdFor.Remove(participantId);
            }
            var participant = FindParticipant(participantId);
            if (participant == null) return;
            HostParticipantDisconnected(participant, participantId);
        }

        // The shared disconnect tail: lobby membership is unprotected and
        // frees slot + credentials; anywhere past it the slot is reserved
        // for the remainder of the race and control passes to AI until a
        // credentialed rejoin takes it back.
        private void HostParticipantDisconnected(ParticipantState participant,
            string participantId)
        {
            if (phase == PhaseLobby)
            {
                ClearParticipant(participant);
                reconnectRegistry.Revoke(participantId);
            }
            else
            {
                participant.connected = false;
                participant.ready = false;
                participant.controlSource = ControlAi;
                reconnectRegistry.MarkReserved(participantId, true);
            }
            pauseRequests.Remove(participantId);
            BroadcastSessionState();
        }

        // --- host command handlers ----------------------------------------

        private CommandResult Host(GameCommand command)
        {
            if (role != RoleNone)
                return Result(command, CommandResult.Rejected, "inSession");
            if (!profileWritable)
                return Result(command, CommandResult.Rejected, "requiresSelectedProfile");
            int port;
            if (!ParseIntParam(command, "port", out port) || port < 0 || port > 65535)
                return Result(command, CommandResult.Rejected, "invalidParameter");
            if (phase == PhaseDiscovering) StopDiscovery();
            ushort boundPort;
            string error;
            if (!link.StartHost((ushort)port, out boundPort, out error))
            {
                lastError = error ?? "hostStartFailed";
                return Result(command, CommandResult.Failed, "hostStartFailed");
            }
            role = RoleHost;
            phase = PhaseLobby;
            sessionId = "lan-" + Guid.NewGuid().ToString("N");
            reconnectRegistry.BeginSession(sessionId);
            descriptor = new SessionDescriptor
            {
                sessionId = sessionId,
                hostAddress = "",
                hostPort = boundPort,
                lanProtocol = NetworkSettings.LanProtocolVersion,
                gameplayVersion = gameplayVersion,
                contentVersion = ContentVersion(),
                contentFingerprint = AdmissionFingerprint()
            };
            var host = slots[0];
            host.participantId = localParticipantId;
            host.occupied = true;
            host.avatar = localAvatar;
            host.colorIndex = localColorIndex;
            host.vehicleId = "";
            host.connected = true;
            host.ready = false;
            host.controlSource = ControlLocal;
            host.ownershipEpoch = 1;
            // Advertise on discovery so nearby players see this host —
            // including the exact compatibility token a joiner must match
            // before it may dial; a responder failure never blocks manual
            // joins.
            if (discovery != null)
                discovery.StartResponder(boundPort, localAvatar,
                    CompatToken(), out error);
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult Configure(GameCommand command)
        {
            if (role != RoleHost)
                return Result(command, CommandResult.Rejected,
                    role == RoleNone ? "noSession" : "hostOnly");
            if (phase != PhaseLobby)
                return Result(command, CommandResult.Rejected, "invalidPhase");
            if (content == null)
                return Result(command, CommandResult.Rejected, "contentUnavailable");
            string eventId, classId;
            if (!command.Parameters.TryGetValue("eventId", out eventId)
                || !command.Parameters.TryGetValue("classId", out classId))
                return Result(command, CommandResult.Rejected, "missingParameter");
            EventDefinition evt;
            if (!content.Events.TryGetValue(eventId, out evt))
                return Result(command, CommandResult.Rejected, "unknownEvent");
            if (!ClassAvailable(evt, classId))
                return Result(command, CommandResult.Rejected, "invalidClass");
            var changed = descriptor.eventId != eventId || descriptor.classId != classId;
            descriptor.eventId = eventId;
            descriptor.classId = classId;
            if (changed)
            {
                // A configuration change invalidates readiness and any
                // vehicle that is no longer legal for the class.
                ClearReady();
                AssignDefaultVehicles();
            }
            BroadcastSessionState();
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult StartRace(GameCommand command)
        {
            if (role != RoleHost)
                return Result(command, CommandResult.Rejected,
                    role == RoleNone ? "noSession" : "hostOnly");
            if (phase != PhaseLobby)
                return Result(command, CommandResult.Rejected, "invalidPhase");
            if (descriptor == null || descriptor.eventId == "")
                return Result(command, CommandResult.Rejected, "notConfigured");
            // The retained-result ledger is bounded: at the cap a new
            // race could never publish its result, so the start itself
            // is refused with a clear session-full diagnostic — history
            // is never silently evicted to make room.
            if (retainedResults.Count >= NetworkSettings.ResultRetentionCapacity)
                return Result(command, CommandResult.Rejected, "sessionFull");
            foreach (var participant in slots)
            {
                if (participant.occupied && participant.connected
                    && IsHumanControl(participant.controlSource) && !participant.ready)
                    return Result(command, CommandResult.Rejected, "notReady");
            }
            // Allocate the race identity up front (Task 4): every
            // participant binds its local reward context against THIS
            // identity before acknowledging start readiness — the
            // coordinator later consumes the same id from the broadcast
            // session state instead of minting its own.
            var allocated = sessionId + "-r" + (++raceOrdinal)
                .ToString(CultureInfo.InvariantCulture);
            // The host's own preparation must land before the race may
            // begin: a durable-context save failure keeps every
            // participant from starting and reports honestly.
            if (!PrepareForRace(allocated))
            {
                raceOrdinal--;
                lastError = "preparationFailed";
                return Result(command, CommandResult.Failed,
                    "preparationFailed");
            }
            currentRaceId = allocated;
            // Start freezes the roster: the session stops being joinable
            // and only credentialed rejoins are honored from here on.
            phase = PhaseCountdown;
            BroadcastSessionState();
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult PauseSet(GameCommand command)
        {
            if (role != RoleHost)
                return Result(command, CommandResult.Rejected,
                    role == RoleNone ? "noSession" : "hostOnly");
            if (!PausePhase(phase))
                return Result(command, CommandResult.Rejected, "invalidPhase");
            string raw;
            if (!command.Parameters.TryGetValue("paused", out raw)
                || (raw != "true" && raw != "false"))
                return Result(command, CommandResult.Rejected, "invalidParameter");
            paused = raw == "true";
            BroadcastSessionState();
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult PauseToggle(GameCommand command)
        {
            if (role == RoleNone)
                return Result(command, CommandResult.Rejected, "noSession");
            if (!PausePhase(phase))
                return Result(command, CommandResult.Rejected, "invalidPhase");
            if (role == RoleClient)
                return SubmitRequest(command, NetworkProtocol.MsgPauseRequest);
            // The host's toggle flips the shared pause directly.
            paused = !paused;
            BroadcastSessionState();
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult PauseRequest(GameCommand command)
        {
            if (role == RoleNone)
                return Result(command, CommandResult.Rejected, "noSession");
            if (!PausePhase(phase))
                return Result(command, CommandResult.Rejected, "invalidPhase");
            if (role == RoleClient)
                return SubmitRequest(command, NetworkProtocol.MsgPauseRequest);
            if (!pauseRequests.Contains(localParticipantId))
                pauseRequests.Add(localParticipantId);
            BroadcastSessionState();
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult Rematch(GameCommand command)
        {
            if (role != RoleHost)
                return Result(command, CommandResult.Rejected,
                    role == RoleNone ? "noSession" : "hostOnly");
            if (phase != PhaseResults)
                return Result(command, CommandResult.Rejected, "invalidPhase");
            phase = PhaseLobby;
            paused = false;
            pauseRequests.Clear();
            // The concluded race's allocated identity retires with the
            // lifecycle — the next lan.start allocates a fresh one.
            currentRaceId = "";
            // A race-slot reservation dies with the race lifecycle: the
            // rematch back to lobby frees slots held for disconnected
            // players. Their issued credentials stay authenticatable in
            // the registry so result retrieval rights survive the rematch.
            foreach (var slot in slots)
            {
                if (!slot.occupied || slot.connected) continue;
                reconnectRegistry.Release(slot.participantId);
                connectionIdFor.Remove(slot.participantId);
                ClearParticipant(slot);
            }
            ClearReady();
            AssignDefaultVehicles();
            BroadcastSessionState();
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult Leave(GameCommand command)
        {
            if (role == RoleNone)
                return Result(command, CommandResult.Rejected, "noSession");
            if (role == RoleClient)
            {
                link.Send(serverConnection, NetworkProtocol.Encode(
                    NetworkProtocol.MsgLeave, "pid", localParticipantId));
            }
            else
            {
                // Closing the host ends the session; there is no migration.
                var closed = NetworkProtocol.Encode(
                    NetworkProtocol.MsgSessionClosed, "rsn", "hostClosed");
                foreach (var connectionId in new List<ulong>(connections.Keys))
                    link.Send(connectionId, closed);
            }
            FailPending("leaveCancelled");
            // Explicit leave removes the stored reconnect credential.
            ClearReconnectMaterial();
            // The session is observably over the moment leave applies: the
            // UI returns to the browser now, not after the flush window.
            // Only the transport teardown is deferred — stopping the link
            // in the same call risks dropping the reliable
            // leave/session.closed datagram, and peers would then see a
            // bare transport disconnect instead of the explicit reason.
            closeFlushUntil = now
                + NetworkSettings.SessionCloseFlushSeconds;
            role = RoleNone;
            phase = PhaseIdle;
            return Result(command, CommandResult.Applied, "");
        }

        // --- client command handlers ---------------------------------------

        private CommandResult Discover(GameCommand command)
        {
            if (discovery == null)
                return Result(command, CommandResult.Rejected, "unavailable");
            if (role != RoleNone)
                return Result(command, CommandResult.Rejected, "inSession");
            if (phase == PhaseDiscovering)
                return Result(command, CommandResult.Rejected, "alreadyDiscovering");
            string error;
            if (!discovery.StartSearch(out error))
            {
                lastError = error ?? "discoveryUnavailable";
                return Result(command, CommandResult.Failed, "discoveryFailed");
            }
            phase = PhaseDiscovering;
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult DiscoveryStop(GameCommand command)
        {
            if (phase != PhaseDiscovering)
                return Result(command, CommandResult.Rejected, "notDiscovering");
            StopDiscovery();
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult Join(GameCommand command)
        {
            if (role != RoleNone || phase == PhaseConnecting || phase == PhaseReconnecting)
                return Result(command, CommandResult.Rejected, "inSession");
            if (FindJoinOp() != null)
                return Result(command, CommandResult.Rejected, "joinPending");
            if (!profileSelected || localParticipantId == "")
                return Result(command, CommandResult.Rejected, "requiresSelectedProfile");
            string address;
            int port;
            if (!command.Parameters.TryGetValue("address", out address)
                || string.IsNullOrEmpty(address)
                || !ParseIntParam(command, "port", out port)
                || port < 0 || port > 65535)
                return Result(command, CommandResult.Rejected, "invalidParameter");
            if (!PendingCapacityOk())
                return Result(command, CommandResult.Rejected, "pendingFull");
            // Discovery fence: a host we can already see advertises its
            // full compatibility token — a mismatch (or an absent token
            // from a legacy advertisement) is a permanent rejection
            // decided before a single datagram is dialed, so it surfaces
            // as the join's own rejection, never a late disconnect.
            if (discovery != null && discovery.Entries != null)
            {
                var expected = CompatToken();
                foreach (var entry in discovery.Entries)
                {
                    if (entry == null) continue;
                    if (!string.Equals(entry.address, address,
                        StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.port != port) continue;
                    if (!string.Equals(entry.compat, expected,
                        StringComparison.Ordinal))
                        return Result(command, CommandResult.Rejected,
                            "incompatible");
                }
            }
            if (phase == PhaseDiscovering) StopDiscovery();
            string error;
            if (!link.StartClient(address, (ushort)port, out error))
            {
                lastError = error ?? "connectFailed";
                return Result(command, CommandResult.Failed, "connectFailed");
            }
            role = RoleClient;
            phase = PhaseConnecting;
            joinAddress = address;
            joinPort = port;
            pending.Add(new PendingOp
            {
                commandId = command.CommandId,
                action = command.Action,
                startedSeconds = now,
                joinKind = true
            });
            return Result(command, CommandResult.Accepted, "");
        }

        private CommandResult Reconnect(GameCommand command)
        {
            // A restarted process has no in-memory material — the private
            // credential store is what survives the restart.
            EnsureReconnectMaterial();
            if (storedCredential == null || storedJoinAddress == null)
                return Result(command, CommandResult.Rejected, "noSession");
            if (role == RoleHost)
                return Result(command, CommandResult.Rejected, "hostOnly");
            if (phase == PhaseConnecting || phase == PhaseReconnecting
                || FindJoinOp() != null)
                return Result(command, CommandResult.Rejected, "joinPending");
            if (role == RoleClient && phase != PhaseRecovery)
                return Result(command, CommandResult.Rejected, "invalidPhase");
            if (!PendingCapacityOk())
                return Result(command, CommandResult.Rejected, "pendingFull");
            // The endpoint fields feed both the join.accept descriptor and
            // the re-stored reconnect material; refresh them from the stored
            // endpoint so an intervening failed join elsewhere can never
            // corrupt where the next reconnect points.
            joinAddress = storedJoinAddress;
            joinPort = storedJoinPort;
            string error;
            if (!link.StartClient(storedJoinAddress, (ushort)storedJoinPort, out error))
            {
                lastError = error ?? "connectFailed";
                return Result(command, CommandResult.Failed, "connectFailed");
            }
            role = RoleClient;
            phase = PhaseReconnecting;
            pending.Add(new PendingOp
            {
                commandId = command.CommandId,
                action = command.Action,
                startedSeconds = now,
                joinKind = true
            });
            return Result(command, CommandResult.Accepted, "");
        }

        private CommandResult Vehicle(GameCommand command)
        {
            if (role == RoleNone)
                return Result(command, CommandResult.Rejected, "noSession");
            if (phase != PhaseLobby)
                return Result(command, CommandResult.Rejected, "invalidPhase");
            string vehicleId;
            if (!command.Parameters.TryGetValue("vehicleId", out vehicleId))
                return Result(command, CommandResult.Rejected, "missingParameter");
            if (!VehicleAllowed(vehicleId, out var code))
                return Result(command, CommandResult.Rejected, code);
            if (role == RoleClient)
            {
                return SubmitRequest(command, NetworkProtocol.MsgVehicleSet,
                    "veh", vehicleId);
            }
            var local = FindParticipant(localParticipantId);
            if (local == null)
                return Result(command, CommandResult.Failed, "internalError");
            local.vehicleId = vehicleId;
            BroadcastSessionState();
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult Ready(GameCommand command)
        {
            if (role == RoleNone)
                return Result(command, CommandResult.Rejected, "noSession");
            if (phase != PhaseLobby)
                return Result(command, CommandResult.Rejected, "invalidPhase");
            string raw;
            if (!command.Parameters.TryGetValue("ready", out raw)
                || (raw != "true" && raw != "false"))
                return Result(command, CommandResult.Rejected, "invalidParameter");
            if (role == RoleClient)
                return SubmitRequest(command, NetworkProtocol.MsgReadySet,
                    "rdy", raw == "true" ? "1" : "0");
            var local = FindParticipant(localParticipantId);
            if (local == null)
                return Result(command, CommandResult.Failed, "internalError");
            local.ready = raw == "true";
            BroadcastSessionState();
            return Result(command, CommandResult.Applied, "");
        }

        private CommandResult ResultsRequest(GameCommand command)
        {
            if (role == RoleNone)
                return Result(command, CommandResult.Rejected, "noSession");
            string raceId;
            if (!command.Parameters.TryGetValue("raceId", out raceId)
                || string.IsNullOrEmpty(raceId))
                return Result(command, CommandResult.Rejected, "missingParameter");
            if (role == RoleClient)
            {
                // Only live-session phases can reach the host; recovery
                // and reconnecting fail fast instead of queueing a
                // request that can never complete.
                if (!ResultPhase(phase))
                    return Result(command, CommandResult.Rejected,
                        "invalidPhase");
                return SubmitRequest(command, NetworkProtocol.MsgResultRequest,
                    "race", raceId);
            }
            // Host-local retrieval: the retained immutable result exists
            // or it does not — the refusal stays honest either way.
            foreach (var result in retainedResults)
                if (result.raceId == raceId)
                    return Result(command, CommandResult.Applied, "");
            return Result(command, CommandResult.Rejected, "noResult");
        }

        private CommandResult ResultsRetrySave(GameCommand command)
        {
            if (role == RoleNone)
                return Result(command, CommandResult.Rejected, "noSession");
            // A retry re-applies the identical immutable result against
            // the local store — the receipt dedup makes it safe and the
            // retry is the only thing that can clear a failed save.
            if (role == RoleClient)
            {
                foreach (var result in deliveredResults)
                {
                    if (result.deliveryStatus != "failed") continue;
                    var applied = ApplyDelivered(result);
                    return applied.status == RaceRewardService.StatusApplied
                        || applied.status == RaceRewardService.StatusAlreadyApplied
                        ? Result(command, CommandResult.Applied, "")
                        : Result(command, CommandResult.Failed,
                            applied.code.Length > 0 ? applied.code : "saveFailed");
                }
                return Result(command, CommandResult.Rejected, "noPendingResult");
            }
            foreach (var result in retainedResults)
            {
                var delivery = DeliveryFor(result, localParticipantId);
                if (delivery == null || delivery.state != "failed") continue;
                ApplyLocalResult(result);
                return delivery.state == "persisted"
                    ? Result(command, CommandResult.Applied, "")
                    : Result(command, CommandResult.Failed,
                        delivery.code.Length > 0 ? delivery.code : "saveFailed");
            }
            return Result(command, CommandResult.Rejected, "noPendingResult");
        }

        // --- Task 8: immutable result retention + delivery -----------------

        // Called by the race coordinator exactly once per finalized race.
        // The result is retained for the host session's lifetime, offered
        // to every connected participant named in it, and applied to the
        // host's own profile immediately. Refuses when the ledger is full
        // or a conflicting result id was already retained — history is
        // never silently evicted or overwritten.
        public bool PublishResult(RaceResult result)
        {
            if (role != RoleHost || result == null || !result.finalized
                || string.IsNullOrEmpty(result.resultId)
                || string.IsNullOrEmpty(result.raceId))
                return false;
            var fingerprint = result.Fingerprint();
            foreach (var kept in retainedResults)
            {
                if (kept.resultId != result.resultId) continue;
                // An identical republish is idempotent; a conflicting one
                // is a defect the host must not absorb.
                if (kept.Fingerprint() == fingerprint) return true;
                lastError = "conflict";
                return false;
            }
            if (retainedResults.Count >= NetworkSettings.ResultRetentionCapacity)
            {
                lastError = "sessionFull";
                return false;
            }
            var retained = result.Copy();
            retained.deliveryStatus = "pending";
            retained.deliveries.Clear();
            // Retrieval rights belong to every participant the result
            // names — including ones that later disconnect or rematch.
            foreach (var slot in slots)
            {
                if (!slot.occupied
                    || EntryFor(retained, slot.participantId) == null)
                    continue;
                // A participant who explicitly left before the result
                // published can never receive the automatic offer: the
                // row records the honest terminal verdict rather than a
                // forever-pending lie.
                var departed = departedParticipants.Contains(
                    slot.participantId);
                retained.deliveries.Add(new RaceResultDelivery
                {
                    participantId = slot.participantId,
                    state = departed ? "failed" : "pending",
                    code = departed ? "undeliverable" : "",
                    lastOfferedAt = double.NegativeInfinity
                });
            }
            RefreshDeliveryStatus(retained);
            retainedResults.Add(retained);
            // The host's own reward applies through the same atomic write
            // every client uses — no privileged local path.
            ApplyLocalResult(retained);
            OfferPending(now);
            return true;
        }

        // Retained results currently published this session (host only).
        // Returned as deep copies so observers can never mutate history.
        public List<RaceResult> RetainedResults()
        {
            var copies = new List<RaceResult>();
            foreach (var result in retainedResults) copies.Add(result.Copy());
            return copies;
        }

        // Results this client has been offered, retained locally for
        // dedup and retry. Returned as deep copies.
        public List<RaceResult> DeliveredResults()
        {
            var copies = new List<RaceResult>();
            foreach (var result in deliveredResults) copies.Add(result.Copy());
            return copies;
        }

        private void ApplyLocalResult(RaceResult result)
        {
            var delivery = DeliveryFor(result, localParticipantId);
            if (delivery == null) return;
            if (rewardApplier == null)
            {
                // No profile seam installed: honest pending, never a
                // fabricated persistence claim.
                delivery.state = "pending";
                delivery.code = "";
                RefreshDeliveryStatus(result);
                return;
            }
            RewardApplyResult applied;
            try
            {
                applied = rewardApplier(result);
            }
            catch (Exception)
            {
                applied = RewardApplyResult.Failed("saveFailed");
            }
            if (applied == null)
                applied = RewardApplyResult.Failed("saveFailed");
            ApplyOutcome(delivery, applied);
            RefreshDeliveryStatus(result);
        }

        // Maps a reward-apply outcome onto a delivery row. Applied and
        // alreadyApplied both mean the client durably owns the receipt.
        private static void ApplyOutcome(RaceResultDelivery delivery,
            RewardApplyResult applied)
        {
            if (applied.status == RaceRewardService.StatusApplied
                || applied.status == RaceRewardService.StatusAlreadyApplied)
            {
                delivery.state = "persisted";
                delivery.code = "";
            }
            else
            {
                delivery.state = "failed";
                // Same verdict code the remote ack path records —
                // ineligible carries its permanent prefix so the local
                // row reads identically to the wire-acknowledged row.
                delivery.code =
                    applied.status == RaceRewardService.StatusIneligible
                        ? "ineligible:" + applied.code
                        : applied.code;
            }
        }

        // Retransmits unacknowledged offers to connected participants on
        // the redelivery cadence. Persisted deliveries and permanent
        // failures (conflict/ineligible) are never resent — everything
        // else keeps retrying until the ack lands.
        private void OfferPending(double at)
        {
            foreach (var result in retainedResults)
            {
                string offer = null;
                foreach (var delivery in result.deliveries)
                {
                    if (delivery.state == "persisted") continue;
                    if (delivery.state == "failed"
                        && PermanentDeliveryFailure(delivery.code)) continue;
                    // Local deliveries apply directly; the wire never
                    // carries the host's own reward.
                    if (delivery.participantId == localParticipantId) continue;
                    ulong connectionId;
                    if (!connectionIdFor.TryGetValue(delivery.participantId,
                            out connectionId)) continue;
                    if (at - delivery.lastOfferedAt
                        < NetworkSettings.ResultRedeliverySeconds) continue;
                    if (offer == null)
                        offer = NetworkProtocol.EncodeResultOffer(result);
                    delivery.lastOfferedAt = at;
                    link.Send(connectionId, offer);
                }
            }
        }

        // conflict, undeliverable and ineligible:* can never resolve by
        // resending the same immutable bytes — conflict/undeliverable
        // name why resending is hopeless (conflicting retained payload,
        // participant gone for good); every other failure (saveFailed,
        // ...) is treated as transient and keeps retrying.
        private static bool PermanentDeliveryFailure(string code)
        {
            return code == "conflict" || code == "undeliverable"
                || (code != null && code.StartsWith("ineligible:"));
        }

        private void RefreshDeliveryStatus(RaceResult result)
        {
            var status = "persisted";
            foreach (var delivery in result.deliveries)
            {
                if (delivery.state == "persisted") continue;
                status = delivery.state == "failed" ? "failed" : "pending";
                if (status == "pending") break;
            }
            result.deliveryStatus = result.deliveries.Count == 0
                ? "pending" : status;
        }

        private static RaceResultEntry EntryFor(RaceResult result,
            string participantId)
        {
            if (result.racers == null || participantId == null) return null;
            foreach (var entry in result.racers)
                if (entry != null && entry.participantId == participantId)
                    return entry;
            return null;
        }

        private static RaceResultDelivery DeliveryFor(RaceResult result,
            string participantId)
        {
            foreach (var delivery in result.deliveries)
                if (delivery.participantId == participantId) return delivery;
            return null;
        }

        private RaceResult FindRetained(string resultId)
        {
            foreach (var result in retainedResults)
                if (result.resultId == resultId) return result;
            return null;
        }

        private RaceResult FindDelivered(string resultId)
        {
            foreach (var result in deliveredResults)
                if (result.resultId == resultId) return result;
            return null;
        }

        private bool HasFailedLocalDelivery()
        {
            if (role == RoleClient)
            {
                foreach (var result in deliveredResults)
                    if (result.deliveryStatus == "failed") return true;
                return false;
            }
            foreach (var result in retainedResults)
            {
                var delivery = DeliveryFor(result, localParticipantId);
                if (delivery != null && delivery.state == "failed") return true;
            }
            return false;
        }

        private static bool ResultPhase(string phase)
        {
            return phase == PhaseLobby || phase == PhaseCountdown
                || phase == PhaseRacing || phase == PhaseResults;
        }

        // Applies a delivered result to the local profile and reports the
        // outcome back to the host. Every code path acknowledges — an
        // unacknowledged offer is what triggers host redelivery.
        private RewardApplyResult ApplyDelivered(RaceResult result)
        {
            RewardApplyResult applied;
            try
            {
                applied = rewardApplier != null
                    ? rewardApplier(result)
                    : RewardApplyResult.Failed("saveFailed");
            }
            catch (Exception)
            {
                applied = RewardApplyResult.Failed("saveFailed");
            }
            if (applied == null)
                applied = RewardApplyResult.Failed("saveFailed");
            var persisted =
                applied.status == RaceRewardService.StatusApplied
                || applied.status == RaceRewardService.StatusAlreadyApplied;
            result.deliveryStatus = persisted ? "persisted" : "failed";
            // Ineligible is a permanent verdict on immutable facts (dnf,
            // foreign profile, no result) — the ack prefix lets the host
            // stop resending, while the granular reason stays observable.
            var code = persisted ? ""
                : applied.status == RaceRewardService.StatusIneligible
                    ? "ineligible:" + applied.code
                    : applied.code;
            result.deliveryCode = code;
            link.Send(serverConnection, NetworkProtocol.Encode(
                NetworkProtocol.MsgResultAck,
                "rid", result.resultId,
                "st", persisted ? "persisted" : "failed",
                "code", code));
            return applied;
        }

        // Client inbound: an immutable result offer from the host. First
        // offers are retained; identical re-offers dedup against the
        // retained copy; a conflicting re-offer is rejected by fingerprint
        // and can never overwrite local facts.
        private void HandleResultOffer(Dictionary<string, string> fields)
        {
            RaceResult result;
            if (!NetworkProtocol.TryDecodeResultOffer(fields, out result)
                || result == null || !result.finalized
                || string.IsNullOrEmpty(result.resultId))
            {
                lastError = "badMessage";
                return;
            }
            var known = FindDelivered(result.resultId);
            if (known != null)
            {
                if (known.Fingerprint() != result.Fingerprint())
                {
                    link.Send(serverConnection, NetworkProtocol.Encode(
                        NetworkProtocol.MsgResultAck,
                        "rid", result.resultId,
                        "st", "failed", "code", "conflict"));
                    return;
                }
                result = known;
            }
            else
            {
                result.deliveryStatus = "pending";
                result.deliveries.Clear();
                deliveredResults.Add(result);
            }
            ApplyDelivered(result);
        }

        // Host inbound: a participant's persistence acknowledgment. The
        // owning participant is derived from the authenticated connection
        // — a payload can only ever update its own delivery row.
        private void HandleResultAck(ulong connectionId,
            Dictionary<string, string> fields)
        {
            var participant = ParticipantFor(connectionId);
            if (participant == null) return;
            var result = FindRetained(Field(fields, "rid"));
            if (result == null) return;
            var delivery = DeliveryFor(result, participant.participantId);
            if (delivery == null) return;
            if (Field(fields, "st") == "persisted")
            {
                delivery.state = "persisted";
                delivery.code = "";
            }
            else
            {
                delivery.state = "failed";
                delivery.code = Field(fields, "code");
            }
            RefreshDeliveryStatus(result);
        }

        // Host inbound: explicit retrieval. Rights come from the
        // authenticated connection's participant appearing in the
        // result's racer rows — credentials survive rematches and
        // disconnects, so a released slot does not forfeit them.
        private void HandleResultRequest(ulong connectionId,
            Dictionary<string, string> fields)
        {
            var commandId = Field(fields, "cid");
            var participant = ParticipantFor(connectionId);
            if (participant == null)
            {
                SendOpResult(connectionId, commandId,
                    CommandResult.Rejected, "noSession");
                return;
            }
            var raceId = Field(fields, "race");
            RaceResult found = null;
            foreach (var result in retainedResults)
                if (result.raceId == raceId) { found = result; break; }
            if (found == null
                || EntryFor(found, participant.participantId) == null)
            {
                SendOpResult(connectionId, commandId,
                    CommandResult.Rejected, "noResult");
                return;
            }
            var delivery = DeliveryFor(found, participant.participantId);
            if (delivery == null)
            {
                delivery = new RaceResultDelivery
                {
                    participantId = participant.participantId,
                    state = "pending",
                    lastOfferedAt = double.NegativeInfinity
                };
                found.deliveries.Add(delivery);
            }
            // The request forces an immediate re-offer of the identical
            // immutable payload straight to the requester — OfferPending
            // skips persisted rows, but a restarted process whose local
            // copy is gone must still be able to fetch the result; the
            // client's receipt dedup makes the replay safe.
            delivery.lastOfferedAt = now;
            link.Send(connectionId,
                NetworkProtocol.EncodeResultOffer(found));
            SendOpResult(connectionId, commandId,
                CommandResult.Applied, "");
        }

        // --- host inbound handlers ------------------------------------------

        private void HandleJoinRequest(ulong connectionId,
            Dictionary<string, string> fields)
        {
            string version, gameplay, contentVersion, participantId, credential;
            string fingerprint;
            fields.TryGetValue("v", out version);
            fields.TryGetValue("gv", out gameplay);
            fields.TryGetValue("cv", out contentVersion);
            fields.TryGetValue("fp", out fingerprint);
            fields.TryGetValue("pid", out participantId);
            fields.TryGetValue("cred", out credential);
            var commandId = Field(fields, "cid");

            if (string.IsNullOrEmpty(participantId)
                || participantId.Length > MaxIdentityChars)
            {
                SendJoinReject(connectionId, "badRequest", commandId);
                return;
            }
            // Exact compatibility is enforced before any allocation.
            // `fp` is the canonical tuned-data fingerprint — the
            // strongest fence: an absent field is itself an incompatible
            // peer, and a hash mismatch means the catalogs differ
            // somewhere a human-readable version cannot see.
            if (version != NetworkSettings.LanProtocolVersion.ToString(
                    CultureInfo.InvariantCulture)
                || gameplay != gameplayVersion
                || contentVersion != ContentVersion()
                || fingerprint != AdmissionFingerprint())
            {
                SendJoinReject(connectionId, "incompatible", commandId);
                return;
            }
            // Credentialed rejoin: the registry binds a reservation to this
            // session, so a reconnect reclaims its slot even when the lobby
            // is full or the race is live — and a display name / profile id
            // alone can never steal the reservation. A successful reclaim
            // bumps the ownership epoch and fences the old connection.
            if (!string.IsNullOrEmpty(credential)
                && FindParticipant(participantId) != null)
            {
                string owner;
                long epoch;
                if (reconnectRegistry.TryReclaim(credential, sessionId,
                        out owner, out epoch) && owner == participantId)
                {
                    var reclaim = FindParticipant(participantId);
                    ulong previous;
                    var hadPrevious =
                        connectionIdFor.TryGetValue(participantId, out previous)
                        && previous != connectionId;
                    openConnections.Remove(connectionId);
                    connections[connectionId] = participantId;
                    connectionIdFor[participantId] = connectionId;
                    if (hadPrevious)
                    {
                        // The superseded socket is fenced: it loses its
                        // participant mapping and is told its session ended.
                        connections.Remove(previous);
                        link.Send(previous, NetworkProtocol.Encode(
                            NetworkProtocol.MsgSessionClosed,
                            "rsn", "superseded"));
                    }
                    reclaim.connected = true;
                    reclaim.controlSource = ControlRemote;
                    reclaim.ownershipEpoch = epoch;
                    // A deliberate return revives delivery eligibility for
                    // FUTURE rows: the departed mark only stamped new
                    // PublishResult rows, and the old undeliverable rows
                    // stay terminal history — they are never rewritten.
                    departedParticipants.Remove(participantId);
                    // The presented credential is retired the moment it is
                    // honored; accept carries the rotated replacement.
                    var rotated = reconnectRegistry.Rotate(participantId);
                    SendJoinAccept(connectionId, reclaim, commandId,
                        rotated ?? credential);
                    BroadcastSessionState();
                    // A credentialed rejoin resumes unacknowledged result
                    // delivery immediately: the offer cadence throttles
                    // resends to the SAME connection — this connection is
                    // brand new, so every pending row for the reclaiming
                    // participant is due now, not one redelivery cycle
                    // later.
                    foreach (var retained in retainedResults)
                    {
                        var row = DeliveryFor(retained, participantId);
                        if (row != null && row.state == "pending")
                            row.lastOfferedAt = double.NegativeInfinity;
                    }
                    OfferPending(now);
                    return;
                }
            }
            if (phase != PhaseLobby)
            {
                SendJoinReject(connectionId, "raceInProgress", commandId);
                return;
            }
            if (FindParticipant(participantId) != null)
            {
                SendJoinReject(connectionId, "identityInUse", commandId);
                return;
            }
            var slot = -1;
            for (var i = 0; i < slots.Length; i++)
                if (!slots[i].occupied) { slot = i; break; }
            if (slot < 0)
            {
                SendJoinReject(connectionId, "lobbyFull", commandId);
                return;
            }
            var avatar = Field(fields, "av");
            if (avatar.Length > MaxIdentityChars) avatar = avatar.Substring(0, MaxIdentityChars);
            int colorIndex;
            if (!NetworkProtocol.IntField(fields, "ci", out colorIndex)) colorIndex = 0;
            var participant = slots[slot];
            participant.participantId = participantId;
            participant.occupied = true;
            participant.avatar = avatar;
            participant.colorIndex = colorIndex;
            participant.vehicleId = DefaultVehicle();
            participant.connected = true;
            participant.ready = false;
            participant.controlSource = ControlRemote;
            participant.ownershipEpoch = 1;
            credential = reconnectRegistry.Issue(participantId);
            openConnections.Remove(connectionId);
            connections[connectionId] = participantId;
            connectionIdFor[participantId] = connectionId;
            // A fresh join reusing a participant id that explicitly left
            // is a deliberate return: future results must be deliverable
            // again. Prior undeliverable rows stay terminal.
            departedParticipants.Remove(participantId);
            SendJoinAccept(connectionId, participant, commandId, credential);
            BroadcastSessionState();
            OfferPending(now);
        }

        private void HandleClientReady(ulong connectionId,
            Dictionary<string, string> fields)
        {
            var participant = ParticipantFor(connectionId);
            var commandId = Field(fields, "cid");
            if (participant == null || phase != PhaseLobby)
            {
                SendOpResult(connectionId, commandId, CommandResult.Rejected, "invalidPhase");
                return;
            }
            participant.ready = Field(fields, "rdy") == "1";
            SendOpResult(connectionId, commandId, CommandResult.Applied, "");
            BroadcastSessionState();
        }

        private void HandleClientVehicle(ulong connectionId,
            Dictionary<string, string> fields)
        {
            var participant = ParticipantFor(connectionId);
            var commandId = Field(fields, "cid");
            var vehicleId = Field(fields, "veh");
            if (participant == null || phase != PhaseLobby)
            {
                SendOpResult(connectionId, commandId, CommandResult.Rejected, "invalidPhase");
                return;
            }
            string code;
            if (!VehicleAllowed(vehicleId, out code))
            {
                SendOpResult(connectionId, commandId, CommandResult.Rejected, code);
                return;
            }
            participant.vehicleId = vehicleId;
            SendOpResult(connectionId, commandId, CommandResult.Applied, "");
            BroadcastSessionState();
        }

        private void HandleClientPauseRequest(ulong connectionId,
            Dictionary<string, string> fields)
        {
            var participant = ParticipantFor(connectionId);
            var commandId = Field(fields, "cid");
            if (participant == null || !PausePhase(phase))
            {
                SendOpResult(connectionId, commandId, CommandResult.Rejected, "invalidPhase");
                return;
            }
            if (!pauseRequests.Contains(participant.participantId))
                pauseRequests.Add(participant.participantId);
            SendOpResult(connectionId, commandId, CommandResult.Applied, "");
            BroadcastSessionState();
        }

        private void HandleClientLeave(ulong connectionId)
        {
            string participantId;
            if (!connections.TryGetValue(connectionId, out participantId)) return;
            connections.Remove(connectionId);
            connectionIdFor.Remove(participantId);
            var participant = FindParticipant(participantId);
            if (participant == null) return;
            // Explicit leave is a permanent departure — unlike a link
            // drop, which keeps the reserved slot reclaimable. Every
            // outstanding delivery row for the departed participant
            // settles as a terminal failure; persisted rows and prior
            // permanent verdicts are immutable history, never rewritten.
            departedParticipants.Add(participantId);
            foreach (var result in retainedResults)
            {
                var delivery = DeliveryFor(result, participantId);
                if (delivery == null || delivery.state == "persisted") continue;
                if (delivery.state == "failed"
                    && PermanentDeliveryFailure(delivery.code)) continue;
                delivery.state = "failed";
                delivery.code = "undeliverable";
                RefreshDeliveryStatus(result);
            }
            if (phase == PhaseLobby)
            {
                // An explicit leave in the lobby ends all credential rights.
                ClearParticipant(participant);
                reconnectRegistry.Revoke(participantId);
            }
            else
            {
                participant.connected = false;
                participant.ready = false;
                participant.controlSource = ControlAi;
                reconnectRegistry.MarkReserved(participantId, true);
            }
            pauseRequests.Remove(participantId);
            BroadcastSessionState();
        }

        // --- client inbound handlers ----------------------------------------

        private void HandleJoinAccept(Dictionary<string, string> fields)
        {
            if (phase != PhaseConnecting && phase != PhaseReconnecting) return;
            int slot;
            if (!NetworkProtocol.IntField(fields, "slot", out slot)
                || slot < 0 || slot >= slots.Length) return;
            sessionId = Field(fields, "sid");
            descriptor = new SessionDescriptor
            {
                sessionId = sessionId,
                hostAddress = joinAddress,
                hostPort = joinPort,
                lanProtocol = NetworkSettings.LanProtocolVersion,
                gameplayVersion = gameplayVersion,
                contentVersion = ContentVersion(),
                contentFingerprint = AdmissionFingerprint(),
                eventId = Field(fields, "ev"),
                classId = Field(fields, "cl")
            };
            // Reconnect material is private: it lives in memory, never in
            // the descriptor, roster, or any emitted state.
            storedSessionId = sessionId;
            storedJoinAddress = joinAddress;
            storedJoinPort = joinPort;
            var credential = Field(fields, "cred");
            if (credential.Length > 0) storedCredential = credential;
            PersistReconnectMaterial();
            var participant = slots[slot];
            participant.participantId = localParticipantId;
            participant.occupied = true;
            participant.avatar = localAvatar;
            participant.colorIndex = localColorIndex;
            participant.connected = true;
            participant.controlSource = ControlLocal;
            int epoch;
            if (!NetworkProtocol.IntField(fields, "ep", out epoch)) epoch = 1;
            participant.ownershipEpoch = epoch;
            phase = NormalizePhase(Field(fields, "ph"));
            // Credentialed reclaim into a live race presents the same
            // allocated identity: binding is idempotent so a prepared
            // participant is a no-op and a device that never prepared
            // binds now, before any further racing boundaries. The
            // accept packet is authoritative — an empty field clears a
            // stale identity rather than leaving it bound.
            var acceptedRace = Field(fields, "rcid");
            currentRaceId = acceptedRace;
            if (acceptedRace.Length > 0
                && (phase == PhaseCountdown || phase == PhaseRacing))
            {
                if (!PrepareForRace(acceptedRace))
                {
                    // Cannot bind the reward context: do not start —
                    // report the failure and leave the session the same
                    // observable way a state-offer failure does.
                    lastError = "preparationFailed";
                    FailPending("preparationFailed");
                    link.Send(serverConnection, NetworkProtocol.Encode(
                        NetworkProtocol.MsgLeave, "pid", localParticipantId));
                    closeFlushUntil = now
                        + NetworkSettings.SessionCloseFlushSeconds;
                    role = RoleNone;
                    phase = PhaseIdle;
                    currentRaceId = "";
                    return;
                }
            }
            var op = FindJoinOp(Field(fields, "cid"));
            if (op != null) CompletePending(op, CommandResult.Applied, "");
        }

        private void HandleJoinReject(Dictionary<string, string> fields)
        {
            var op = FindJoinOp(Field(fields, "cid"));
            var code = Field(fields, "code");
            if (code.Length == 0) code = "joinRejected";
            if (op != null) CompletePending(op, CommandResult.Rejected, code);
            link.Stop();
            serverConnection = 0;
            role = RoleNone;
            phase = PhaseIdle;
        }

        private void HandleSessionState(Dictionary<string, string> fields)
        {
            if (descriptor == null || Field(fields, "sid") != sessionId) return;
            descriptor.eventId = Field(fields, "ev");
            descriptor.classId = Field(fields, "cl");
            // The host-allocated race identity arrives with the countdown
            // phase: the local reward context binds against it BEFORE
            // the race clock may start — a preparation failure keeps
            // this participant from starting (Task 4).
            var offeredRace = Field(fields, "rcid");
            var offeredPhase = NormalizePhase(Field(fields, "ph"));
            // The broadcast identity is authoritative in both
            // directions: an empty field clears the concluded race's id
            // so lobby/rematch snapshots can never carry a stale one.
            currentRaceId = offeredRace;
            if (offeredRace.Length > 0
                && (offeredPhase == PhaseCountdown || offeredPhase == PhaseRacing)
                && !PrepareForRace(offeredRace))
            {
                lastError = "preparationFailed";
                FailPending("preparationFailed");
                link.Send(serverConnection, NetworkProtocol.Encode(
                    NetworkProtocol.MsgLeave, "pid", localParticipantId));
                // The datagram gets one flush window, exactly like an
                // explicit leave — the observable session is already over.
                closeFlushUntil = now
                    + NetworkSettings.SessionCloseFlushSeconds;
                role = RoleNone;
                phase = PhaseIdle;
                currentRaceId = "";
                return;
            }
            int occupied;
            if (NetworkProtocol.IntField(fields, "occ", out occupied))
                descriptor.occupancy = occupied;
            descriptor.joinable = Field(fields, "jn") == "1";
            phase = offeredPhase;
            paused = Field(fields, "pd") == "1";
            pauseRequests.Clear();
            foreach (var requester in Field(fields, "pr").Split(','))
                if (requester.Length > 0) pauseRequests.Add(requester);
            for (var i = 0; i < slots.Length; i++)
            {
                var raw = Field(fields, "p" + i.ToString(CultureInfo.InvariantCulture));
                var slot = slots[i];
                if (raw.Length == 0)
                {
                    // The local participant is only emptied by a roster
                    // that explicitly drops its slot.
                    ClearParticipant(slot);
                    continue;
                }
                var parts = raw.Split(',');
                if (parts.Length < 9) continue;
                slot.participantId = parts[0];
                slot.occupied = true;
                slot.avatar = parts[2];
                int number;
                slot.colorIndex = int.TryParse(parts[3], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out number) ? number : 0;
                slot.vehicleId = parts[4];
                slot.connected = parts[5] == "1";
                slot.ready = parts[6] == "1";
                slot.controlSource = parts[7];
                slot.ownershipEpoch = long.TryParse(parts[8], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var epoch) ? epoch : 1;
                // From this device's view its own racer is locally
                // controlled even though the host reports it as remote.
                if (slot.participantId == localParticipantId)
                    slot.controlSource = ControlLocal;
            }
        }

        private void HandleOpResult(Dictionary<string, string> fields)
        {
            var commandId = Field(fields, "cid");
            if (commandId.Length == 0) return;
            PendingOp op = null;
            foreach (var candidate in pending)
                if (candidate.commandId == commandId) { op = candidate; break; }
            if (op == null || op.joinKind) return;
            var status = Field(fields, "st");
            if (status != CommandResult.Applied && status != CommandResult.Rejected
                && status != CommandResult.Failed)
                status = CommandResult.Failed;
            CompletePending(op, status, Field(fields, "code"));
        }

        private void HandleSessionClosed(Dictionary<string, string> fields)
        {
            FailPending("sessionClosed");
            var reason = Field(fields, "rsn");
            lastError = reason.Length > 0 ? reason : "hostClosed";
            link.Stop();
            phase = PhaseRecovery;
            serverConnection = 0;
            foreach (var slot in slots)
                if (slot.occupied) slot.connected = false;
            if (descriptor != null) descriptor.joinable = false;
        }

        // --- shared helpers -------------------------------------------------

        // A client request is accepted now and completes only when the
        // host's op.result arrives — never at queue time.
        private CommandResult SubmitRequest(GameCommand command, string messageType,
            params string[] extraFields)
        {
            if (!PendingCapacityOk())
                return Result(command, CommandResult.Rejected, "pendingFull");
            foreach (var op in pending)
                if (op.commandId == command.CommandId)
                    return Result(command, CommandResult.Rejected, "duplicatePending");
            var fields = new List<string> { "cid", command.CommandId };
            if (extraFields != null) fields.AddRange(extraFields);
            if (!link.Send(serverConnection,
                    NetworkProtocol.Encode(messageType, fields.ToArray())))
            {
                return Result(command, CommandResult.Failed, "sendFailed");
            }
            pending.Add(new PendingOp
            {
                commandId = command.CommandId,
                action = command.Action,
                startedSeconds = now
            });
            return Result(command, CommandResult.Accepted, "");
        }

        private void SendJoinAccept(ulong connectionId, ParticipantState participant,
            string commandId, string credential = "")
        {
            link.Send(connectionId, NetworkProtocol.Encode(
                NetworkProtocol.MsgJoinAccept,
                "sid", sessionId,
                "ph", phase,
                "pid", participant.participantId,
                "slot", participant.slot.ToString(CultureInfo.InvariantCulture),
                "ev", descriptor != null ? descriptor.eventId : "",
                "cl", descriptor != null ? descriptor.classId : "",
                "ep", participant.ownershipEpoch.ToString(CultureInfo.InvariantCulture),
                "cred", credential,
                // A reclaim into countdown/racing must learn the
                // allocated race identity to bind its reward context
                // (Task 4) — empty while the lobby has not started.
                // "rcid" is deliberately distinct from the result-offer
                // "rid" so no decode path can ever confuse the two.
                "rcid", currentRaceId,
                "cid", commandId ?? ""));
        }

        private void SendJoinReject(ulong connectionId, string code, string commandId)
        {
            link.Send(connectionId, NetworkProtocol.Encode(
                NetworkProtocol.MsgJoinReject, "code", code, "cid", commandId ?? ""));
        }

        private void SendOpResult(ulong connectionId, string commandId,
            string status, string code)
        {
            if (commandId == null || commandId.Length == 0) return;
            link.Send(connectionId, NetworkProtocol.Encode(
                NetworkProtocol.MsgOpResult,
                "cid", commandId, "st", status, "code", code ?? ""));
        }

        private void BroadcastSessionState()
        {
            if (role != RoleHost || descriptor == null) return;
            RefreshDescriptor();
            var requesters = string.Join(",", pauseRequests.ToArray());
            var fields = new List<string>
            {
                "sid", sessionId,
                "ph", phase,
                "ev", descriptor.eventId,
                "cl", descriptor.classId,
                "pd", paused ? "1" : "0",
                "pr", requesters,
                "occ", descriptor.occupancy.ToString(CultureInfo.InvariantCulture),
                "jn", descriptor.joinable ? "1" : "0",
                // The allocated race identity rides the broadcast so each
                // local device binds its reward context against the same
                // id the coordinator will run (Task 4). "rcid" is
                // deliberately distinct from the result-offer "rid" so no
                // decode path can ever confuse the two.
                "rcid", currentRaceId
            };
            for (var i = 0; i < slots.Length; i++)
            {
                var p = slots[i];
                fields.Add("p" + i.ToString(CultureInfo.InvariantCulture));
                fields.Add(p.occupied
                    ? string.Join(",", new[]
                    {
                        p.participantId,
                        p.slot.ToString(CultureInfo.InvariantCulture),
                        p.avatar,
                        p.colorIndex.ToString(CultureInfo.InvariantCulture),
                        p.vehicleId ?? "",
                        p.connected ? "1" : "0",
                        p.ready ? "1" : "0",
                        p.controlSource ?? ControlNone,
                        p.ownershipEpoch.ToString(CultureInfo.InvariantCulture)
                    })
                    : "");
            }
            var payload = NetworkProtocol.Encode(NetworkProtocol.MsgSessionState,
                fields.ToArray());
            foreach (var connectionId in new List<ulong>(connections.Keys))
                link.Send(connectionId, payload);
        }

        private void RefreshDescriptor()
        {
            if (descriptor == null) return;
            var occupied = 0;
            foreach (var slot in slots) if (slot.occupied) occupied++;
            descriptor.occupancy = occupied;
            descriptor.capacity = NetworkSettings.ParticipantCapacity;
            descriptor.joinable = role == RoleHost
                && phase == PhaseLobby
                && occupied < NetworkSettings.ParticipantCapacity;
        }

        // The local reward-context bind for one host-allocated race
        // identity: idempotent per identity, and a missing preparer is
        // an honest no-op (older compositions simply do not bind — the
        // race itself is unaffected; Task 7 then finds no context and
        // rejects progression evaluation rather than guessing).
        private bool PrepareForRace(string allocatedRaceId)
        {
            if (string.IsNullOrEmpty(allocatedRaceId)) return true;
            if (preparedRaceId == allocatedRaceId) return true;
            if (racePreparer == null)
            {
                preparedRaceId = allocatedRaceId;
                return true;
            }
            var error = racePreparer(allocatedRaceId);
            if (!string.IsNullOrEmpty(error)) return false;
            preparedRaceId = allocatedRaceId;
            return true;
        }

        private void ClearReady()
        {
            foreach (var slot in slots) if (slot.occupied) slot.ready = false;
        }

        private void AssignDefaultVehicles()
        {
            var fallback = DefaultVehicle();
            foreach (var slot in slots)
            {
                if (!slot.occupied) continue;
                string code;
                if (!VehicleAllowed(slot.vehicleId, out code))
                    slot.vehicleId = fallback;
            }
        }

        private string DefaultVehicle()
        {
            if (content == null || descriptor == null || descriptor.eventId == "")
                return "";
            EventDefinition evt;
            if (!content.Events.TryGetValue(descriptor.eventId, out evt)) return "";
            foreach (var vehicleId in evt.vehicleIds)
                if (NetworkProtocol.VehicleClass(vehicleId) == descriptor.classId)
                    return vehicleId;
            return "";
        }

        private bool ClassAvailable(EventDefinition evt, string classId)
        {
            if (string.IsNullOrEmpty(classId)) return false;
            foreach (var vehicleId in evt.vehicleIds)
                if (NetworkProtocol.VehicleClass(vehicleId) == classId) return true;
            return false;
        }

        private bool VehicleAllowed(string vehicleId, out string code)
        {
            code = "invalidVehicle";
            if (string.IsNullOrEmpty(vehicleId)) return false;
            if (content == null) { code = "contentUnavailable"; return false; }
            if (!content.Vehicles.ContainsKey(vehicleId))
            { code = "unknownVehicle"; return false; }
            if (descriptor == null || descriptor.eventId == "")
            { code = "notConfigured"; return false; }
            EventDefinition evt;
            if (!content.Events.TryGetValue(descriptor.eventId, out evt))
            { code = "notConfigured"; return false; }
            // The shared mode-eligibility verdict (M5 Task 8): LAN
            // exposes every implemented vehicle in the configured event
            // under the configured class — normalized racing never
            // consults profile unlocks. The lobby's historical codes
            // stay stable on top of the verdict's reason.
            var decision = RaceModeRules.Evaluate(RaceMode.Lan, "",
                descriptor.eventId, descriptor.classId, vehicleId,
                null, content, null);
            if (!decision.allowed)
            {
                code = decision.code == "unknownVehicle"
                    ? "unknownVehicle" : "invalidVehicle";
                return false;
            }
            return true;
        }

        private bool PendingCapacityOk()
        {
            return pending.Count < NetworkSettings.PendingOperationCapacity;
        }

        private PendingOp FindJoinOp(string commandId = null)
        {
            PendingOp fallback = null;
            foreach (var op in pending)
            {
                if (!op.joinKind) continue;
                if (commandId != null && op.commandId == commandId) return op;
                fallback = op;
            }
            return fallback;
        }

        private void CompletePending(PendingOp op, string status, string code)
        {
            if (!pending.Remove(op)) return;
            outcomes.Add(new CommandResult
            {
                commandId = op.commandId,
                action = op.action,
                status = status,
                code = code ?? "",
                tick = 0,
                duplicate = false
            });
        }

        private void FailPending(string code)
        {
            var copy = new List<PendingOp>(pending);
            foreach (var op in copy) CompletePending(op, CommandResult.Failed, code);
        }

        private ParticipantState FindParticipant(string participantId)
        {
            foreach (var slot in slots)
                if (slot.occupied && slot.participantId == participantId) return slot;
            return null;
        }

        private ParticipantState ParticipantFor(ulong connectionId)
        {
            string participantId;
            return connections.TryGetValue(connectionId, out participantId)
                ? FindParticipant(participantId) : null;
        }

        private void ClearParticipant(ParticipantState slot)
        {
            slot.participantId = "";
            slot.occupied = false;
            slot.avatar = "";
            slot.colorIndex = 0;
            slot.vehicleId = "";
            slot.connected = false;
            slot.ready = false;
            slot.controlSource = ControlNone;
            slot.ownershipEpoch = 0;
        }

        // Loads the persisted credential once per profile identity — this
        // is what lets a restarted client process present its reservation.
        private void EnsureReconnectMaterial()
        {
            if (storedCredential != null || reconnectStoreChecked
                || reconnectStore == null || localParticipantId == "")
                return;
            reconnectStoreChecked = true;
            ReconnectMaterial material;
            if (!reconnectStore.TryLoad(localParticipantId, out material)
                || material == null)
                return;
            storedSessionId = material.sessionId;
            storedJoinAddress = material.hostAddress;
            storedJoinPort = material.hostPort;
            storedCredential = material.credential;
        }

        // Mirrors the in-memory credential into the private store so a
        // process restart can reclaim. Rotated credentials overwrite the
        // previous record — only the newest is ever usable for reclaim.
        private void PersistReconnectMaterial()
        {
            if (reconnectStore == null || storedCredential == null
                || localParticipantId == "") return;
            reconnectStore.Save(new ReconnectMaterial
            {
                participantId = localParticipantId,
                sessionId = storedSessionId,
                hostAddress = storedJoinAddress,
                hostPort = storedJoinPort,
                credential = storedCredential
            });
        }

        private void ClearReconnectMaterial()
        {
            storedSessionId = null;
            storedJoinAddress = null;
            storedJoinPort = 0;
            storedCredential = null;
            if (reconnectStore != null) reconnectStore.Clear();
        }

        private void StopDiscovery()
        {
            if (discovery != null) discovery.StopSearch();
            phase = PhaseIdle;
        }

        private void Teardown()
        {
            closeFlushUntil = double.NegativeInfinity;
            link.Stop();
            if (discovery != null) discovery.StopResponder();
            openConnections.Clear();
            connections.Clear();
            connectionIdFor.Clear();
            reconnectRegistry.BeginSession("");
            foreach (var slot in slots) ClearParticipant(slot);
            pauseRequests.Clear();
            paused = false;
            retainedResults.Clear();
            deliveredResults.Clear();
            departedParticipants.Clear();
            descriptor = null;
            sessionId = "";
            // A torn-down session keeps no race identity and no
            // preparation claim — the next session binds fresh.
            currentRaceId = "";
            raceOrdinal = 0;
            preparedRaceId = "";
            serverConnection = 0;
            role = RoleNone;
            phase = PhaseIdle;
        }

        private string ContentVersion()
        {
            // Compatibility includes the simulation backend: arcade
            // catalogs keep the bare authored version (existing wire
            // fixtures are byte-identical), while a non-arcade backend
            // folds into the token so an arcade build can never silently
            // join a physical race.
            return content != null ? content.CompatibilityVersion : "none";
        }

        // The canonical tuned-data fingerprint of the local catalog —
        // retained for legacy/unbound admission and for peers that
        // carry no gameplay identity at all.
        private string ContentFingerprint()
        {
            return content != null ? content.CompatibilityFingerprint : "";
        }

        // The admission fingerprint both sides must hold bit-identically
        // before a seat is allocated (M5 Task 10): the gameplay
        // catalog's fingerprint when one is bound and non-legacy — it
        // covers the driving body plus objectives, campaign, bounties,
        // economy, gadgets and difficulties — and the driving
        // fingerprint otherwise, so legacy content stays compatible
        // with unupgraded peers and an absent catalog fingerprints
        // empty (still a valid token).
        private string AdmissionFingerprint()
        {
            return gameplayContent != null && !gameplayContent.IsLegacy
                ? gameplayContent.CompatibilityFingerprint
                : ContentFingerprint();
        }

        // The whole discovery-advertised compatibility token in one
        // string — the datagram schema already binds the protocol, so
        // the token carries gameplay version, content version and the
        // canonical fingerprint.
        private string CompatToken()
        {
            return gameplayVersion + "|" + ContentVersion()
                + "|" + AdmissionFingerprint();
        }

        private static bool PausePhase(string phase)
        {
            return phase == PhaseLobby || phase == PhaseCountdown || phase == PhaseRacing;
        }

        private static bool IsHumanControl(string controlSource)
        {
            return controlSource == ControlLocal || controlSource == ControlRemote;
        }

        private static string NormalizePhase(string value)
        {
            switch (value)
            {
                case PhaseLobby:
                case PhaseCountdown:
                case PhaseRacing:
                case PhaseResults:
                    return value;
                default:
                    return PhaseLobby;
            }
        }

        private static string Field(Dictionary<string, string> fields, string key)
        {
            string value;
            return fields != null && fields.TryGetValue(key, out value) && value != null
                ? value : "";
        }

        private static bool ParseIntParam(GameCommand command, string name, out int value)
        {
            value = 0;
            string raw;
            if (!command.Parameters.TryGetValue(name, out raw)) return false;
            return int.TryParse(raw, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value);
        }

        private static void Deny(out bool available, out string reason, string code)
        {
            available = false;
            reason = code;
        }

        private static CommandResult Result(GameCommand command, string status, string code)
        {
            return new CommandResult
            {
                commandId = command == null ? "" : command.CommandId ?? "",
                action = command == null ? "" : command.Action ?? "",
                status = status,
                code = code ?? "",
                tick = 0,
                duplicate = false
            };
        }

        // --- race coordinator hooks (Task 3+ drives the transitions) ---------

        // countdown -> racing once the shared race clock is live.
        public bool BeginRace()
        {
            if (role != RoleHost || phase != PhaseCountdown) return false;
            phase = PhaseRacing;
            BroadcastSessionState();
            return true;
        }

        // racing -> results when the race produces a finalized outcome.
        public bool FinishRace()
        {
            if (role != RoleHost || phase != PhaseRacing) return false;
            phase = PhaseResults;
            BroadcastSessionState();
            return true;
        }
    }
}
