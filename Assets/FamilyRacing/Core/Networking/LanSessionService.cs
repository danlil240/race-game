using System;
using System.Collections.Generic;
using System.Globalization;
using FamilyRacing.Core.Automation;
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
    // this process's own host socket.
    public interface ILanDiscoveryProbe
    {
        bool StartSearch(out string error);
        void StopSearch();
        bool StartResponder(ushort gamePort, string hostName, out string error);
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
        // Host side: per-participant reconnect secrets. These never enter
        // the descriptor, the roster, or any observation DTO.
        private readonly Dictionary<string, string> credentials =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<string> pauseRequests = new List<string>();

        private DrivingContentCatalog content;
        private string role = RoleNone;
        private string phase = PhaseIdle;
        private double now;
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
        // held in memory only and removed by an explicit lan.leave.
        private string storedSessionId;
        private string storedJoinAddress;
        private int storedJoinPort;
        private string storedCredential;
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

        // Identity of the local human: stable participant id plus cosmetic
        // identity and profile readiness. The caller refreshes this at each
        // command boundary so a profile change is seen by the next check.
        public void SetLocalContext(string participantId, string avatar,
            int colorIndex, bool selected, bool writable)
        {
            localParticipantId = participantId ?? "";
            localAvatar = avatar ?? "";
            localColorIndex = colorIndex;
            profileSelected = selected;
            profileWritable = writable;
        }

        public CommandResult Execute(GameCommand command)
        {
            if (command == null || string.IsNullOrEmpty(command.Action))
                return Result(null, CommandResult.Rejected, "invalidCommand");
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
        }

        public NetworkSessionState Snapshot()
        {
            RefreshDescriptor();
            var state = new NetworkSessionState
            {
                role = role,
                phase = phase,
                localParticipantId = role != RoleNone ? localParticipantId : "",
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
            return state;
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
                    else { Deny(out available, out reason, "noResult"); }
                    break;
                case "lan.results.retrySave":
                    Deny(out available, out reason, "noPendingResult");
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
                var participant = FindParticipant(participantId);
                if (participant == null) return;
                if (phase == PhaseLobby)
                {
                    // Lobby membership is not protected: a leaver frees
                    // the slot and the credential reservation with it.
                    ClearParticipant(participant);
                    credentials.Remove(participantId);
                }
                else
                {
                    // Mid-race the slot is retained for the remainder of
                    // the race and control passes to AI (Task 4 owns the
                    // driver itself).
                    participant.connected = false;
                    participant.ready = false;
                    participant.controlSource = ControlAi;
                }
                pauseRequests.Remove(participantId);
                BroadcastSessionState();
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
            descriptor = new SessionDescriptor
            {
                sessionId = sessionId,
                hostAddress = "",
                hostPort = boundPort,
                lanProtocol = NetworkSettings.LanProtocolVersion,
                gameplayVersion = gameplayVersion,
                contentVersion = ContentVersion()
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
            // Advertise on discovery so nearby players see this host; a
            // responder failure never blocks manual joins.
            if (discovery != null)
                discovery.StartResponder(boundPort, localAvatar, out error);
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
            foreach (var participant in slots)
            {
                if (participant.occupied && participant.connected
                    && IsHumanControl(participant.controlSource) && !participant.ready)
                    return Result(command, CommandResult.Rejected, "notReady");
            }
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
            Teardown();
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
            // Result storage/delivery lands in Task 8; nothing can be
            // fetched yet and the refusal stays honest.
            return Result(command, CommandResult.Rejected, "noResult");
        }

        private CommandResult ResultsRetrySave(GameCommand command)
        {
            return Result(command, CommandResult.Rejected, "noPendingResult");
        }

        // --- host inbound handlers ------------------------------------------

        private void HandleJoinRequest(ulong connectionId,
            Dictionary<string, string> fields)
        {
            string version, gameplay, contentVersion, participantId, credential;
            fields.TryGetValue("v", out version);
            fields.TryGetValue("gv", out gameplay);
            fields.TryGetValue("cv", out contentVersion);
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
            if (version != NetworkSettings.LanProtocolVersion.ToString(
                    CultureInfo.InvariantCulture)
                || gameplay != gameplayVersion
                || contentVersion != ContentVersion())
            {
                SendJoinReject(connectionId, "incompatible", commandId);
                return;
            }
            // Authenticated rejoin: a stored credential reclaims its slot
            // even when the session is full or mid-race.
            string stored;
            if (!string.IsNullOrEmpty(credential)
                && credentials.TryGetValue(participantId, out stored)
                && stored == credential)
            {
                var reclaim = FindParticipant(participantId);
                if (reclaim != null)
                {
                    openConnections.Remove(connectionId);
                    connections[connectionId] = participantId;
                    reclaim.connected = true;
                    reclaim.controlSource = ControlRemote;
                    reclaim.ownershipEpoch++;
                    SendJoinAccept(connectionId, reclaim, commandId, stored);
                    BroadcastSessionState();
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
            credential = Guid.NewGuid().ToString("N");
            credentials[participantId] = credential;
            openConnections.Remove(connectionId);
            connections[connectionId] = participantId;
            SendJoinAccept(connectionId, participant, commandId, credential);
            BroadcastSessionState();
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
            var participant = FindParticipant(participantId);
            if (participant == null) return;
            if (phase == PhaseLobby)
            {
                ClearParticipant(participant);
                credentials.Remove(participantId);
            }
            else
            {
                participant.connected = false;
                participant.ready = false;
                participant.controlSource = ControlAi;
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
            int occupied;
            if (NetworkProtocol.IntField(fields, "occ", out occupied))
                descriptor.occupancy = occupied;
            descriptor.joinable = Field(fields, "jn") == "1";
            phase = NormalizePhase(Field(fields, "ph"));
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
                "jn", descriptor.joinable ? "1" : "0"
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
            if (!evt.AllowsVehicle(vehicleId)
                || NetworkProtocol.VehicleClass(vehicleId) != descriptor.classId)
            { code = "invalidVehicle"; return false; }
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

        private void ClearReconnectMaterial()
        {
            storedSessionId = null;
            storedJoinAddress = null;
            storedJoinPort = 0;
            storedCredential = null;
        }

        private void StopDiscovery()
        {
            if (discovery != null) discovery.StopSearch();
            phase = PhaseIdle;
        }

        private void Teardown()
        {
            link.Stop();
            if (discovery != null) discovery.StopResponder();
            openConnections.Clear();
            connections.Clear();
            credentials.Clear();
            foreach (var slot in slots) ClearParticipant(slot);
            pauseRequests.Clear();
            paused = false;
            descriptor = null;
            sessionId = "";
            serverConnection = 0;
            role = RoleNone;
            phase = PhaseIdle;
        }

        private string ContentVersion()
        {
            return content != null ? content.Version : "none";
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
