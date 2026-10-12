using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using LanSettings = FamilyRacing.Core.Networking.NetworkSettings;

namespace FamilyRacing.Networking
{
    /// <summary>
    /// Lifecycle of a <see cref="LanTransport"/>. Connecting is the only
    /// transient state: a denied or failed start returns to Idle, it never
    /// lingers as a pending join.
    /// </summary>
    public enum LanTransportState
    {
        Idle,
        Hosting,
        Connecting,
        Connected
    }

    /// <summary>
    /// LAN gameplay transport over the pinned Unity Transport (UTP 2.x)
    /// UDP socket. A host binds and listens; a client connects to an
    /// explicit IPv4 endpoint. Two channels share each connection:
    /// the reliable-ordered channel (session protocol, lifecycle,
    /// ownership, results, and authoritative event batches) and the
    /// sequenced-unreliable gameplay channel (input batches inbound,
    /// state snapshots outbound). Connection ids are small
    /// locally-assigned handles (1, 2, 3, ...), stable for the life of
    /// the connection and recycled only after Stop.
    ///
    /// All events are dispatched inside <see cref="Pump"/>, which callers
    /// invoke on the Unity main thread — callbacks therefore always run on
    /// the Unity thread and never need marshalling. This class owns only
    /// gameplay traffic on the LAN; validation/instrumentation sockets
    /// stay loopback-only elsewhere.
    /// </summary>
    public sealed class LanTransport : IDisposable
    {
        /// <summary>Default gameplay UDP port (configurable per start call
        /// so multiple processes can share one machine). The value's single
        /// definition lives in Core NetworkSettings.</summary>
        public const ushort DefaultGameplayPort = LanSettings.DefaultGameplayPort;

        // Binding milestone timing budget: a failed join and a silent peer
        // must each resolve within 5 s of monotonic wall time. UTP reports
        // join failure after connectTimeoutMS * maxConnectAttempts and a
        // dead peer after disconnectTimeoutMS; both products stay <= 5 s.
        // The values' single definition is Core NetworkSettings.
        /// <summary>Maximum wall time a join may stay pending.</summary>
        public const int JoinTimeoutMilliseconds = LanSettings.JoinTimeoutMilliseconds;
        /// <summary>Maximum wall time a silent peer stays connected.</summary>
        public const int PeerTimeoutMilliseconds = LanSettings.PeerTimeoutMilliseconds;
        private const int ConnectAttemptIntervalMilliseconds =
            LanSettings.ConnectAttemptIntervalMilliseconds;
        private const int MaxConnectAttempts =
            JoinTimeoutMilliseconds / ConnectAttemptIntervalMilliseconds;

        private struct Peer
        {
            public ulong Id;
            public NetworkConnection Connection;
        }

        private readonly ILanNetworkAccess access;
        private NetworkDriver driver;
        // Task 5: two typed lanes share each connection. reliablePipeline
        // carries session/lobby/lifecycle/ownership/results traffic plus
        // authoritative event batches — ordered and guaranteed.
        // gameplayPipeline carries input batches and state snapshots —
        // sequenced so reordered datagrams drop at the transport, loss is
        // tolerated, and payloads may fragment up to the snapshot ceiling.
        private NetworkPipeline reliablePipeline;
        private NetworkPipeline gameplayPipeline;
        private readonly List<Peer> peers = new List<Peer>(4);
        private Peer clientPeer;
        private ulong nextPeerId = 1;
        private LanTransportState state = LanTransportState.Idle;

        public LanTransport() : this(LanNetworkAccess.Create()) { }

        public LanTransport(ILanNetworkAccess access)
        {
            this.access = access ?? throw new ArgumentNullException(nameof(access));
        }

        /// <summary>Current lifecycle state.</summary>
        public LanTransportState State => state;

        /// <summary>True while a client connect is in flight.</summary>
        public bool IsConnecting => state == LanTransportState.Connecting;

        /// <summary>True while this transport is in the client role —
        /// connecting to or connected with a host. Clients send through
        /// the parameterless <see cref="Send(byte[])"/> overload.</summary>
        public bool IsClient => state == LanTransportState.Connecting
            || state == LanTransportState.Connected;

        /// <summary>
        /// The port this transport is actually bound to — after
        /// StartHost(0) this reports the OS-assigned port. Zero when no
        /// socket is open, which is also the proof Stop released it.
        /// </summary>
        public ushort BoundPort { get; private set; }

        /// <summary>Last start/send failure in plain terms; empty when the
        /// last operation succeeded.</summary>
        public string LastError { get; private set; } = "";

        /// <summary>Fired on the Unity thread when a connection becomes
        /// usable. The id is a local connection identifier (host: peer,
        /// client: the server connection).</summary>
        public event Action<ulong> Connected;

        /// <summary>Fired on the Unity thread when a connection ends.</summary>
        public event Action<ulong> Disconnected;

        /// <summary>Fired on the Unity thread for every received datagram
        /// on the reliable-ordered channel (session protocol and other
        /// ordered control traffic).</summary>
        public event Action<ulong, byte[]> Message;

        /// <summary>Fired on the Unity thread for every datagram received
        /// on the sequenced-unreliable gameplay channel (input batches
        /// host-side, state snapshots client-side). The pipeline already
        /// dropped reordered datagrams; gaps report loss, not stall.</summary>
        public event Action<ulong, byte[]> GameplayMessage;

        /// <summary>Bind a host socket on all local interfaces.</summary>
        public bool StartHost(ushort port)
        {
            if (!CheckAccess()) return false;
            if (state != LanTransportState.Idle)
            {
                LastError = "transport already started (state " + state + ")";
                return false;
            }
            try
            {
                DisposeDriver();
                driver = CreateDriver();
                CreatePipelines();
                var endpoint = NetworkEndpoint.AnyIpv4.WithPort(port);
                int bindResult = driver.Bind(endpoint);
                if (bindResult != 0)
                {
                    LastError = "bind failed on port " + port + " (code " + bindResult + ")";
                    DisposeDriver();
                    return false;
                }
                int listenResult = driver.Listen();
                if (listenResult != 0)
                {
                    LastError = "listen failed (code " + listenResult + ")";
                    DisposeDriver();
                    return false;
                }
                BoundPort = (ushort)driver.GetLocalEndpoint().Port;
                state = LanTransportState.Hosting;
                LastError = "";
                return true;
            }
            catch (Exception e)
            {
                LastError = "host start failed: " + e.Message;
                DisposeDriver();
                return false;
            }
        }

        /// <summary>Connect to an explicit IPv4:port endpoint (manual join
        /// uses the same path as a discovered one).</summary>
        public bool StartClient(string address, ushort port)
        {
            if (!CheckAccess()) return false;
            if (state != LanTransportState.Idle)
            {
                LastError = "transport already started (state " + state + ")";
                return false;
            }
            if (!NetworkEndpoint.TryParse(address, port, out NetworkEndpoint endpoint)
                || endpoint.Family != NetworkFamily.Ipv4)
            {
                LastError = "invalid endpoint '" + address + ":" + port + "'";
                return false;
            }
            try
            {
                DisposeDriver();
                driver = CreateDriver();
                CreatePipelines();
                var connection = driver.Connect(endpoint);
                clientPeer = new Peer { Id = nextPeerId++, Connection = connection };
                state = LanTransportState.Connecting;
                BoundPort = (ushort)driver.GetLocalEndpoint().Port;
                LastError = "";
                return true;
            }
            catch (Exception e)
            {
                LastError = "client start failed: " + e.Message;
                DisposeDriver();
                return false;
            }
        }

        /// <summary>
        /// Poll the driver and dispatch connect/data/disconnect events.
        /// Call once per frame on the Unity main thread.
        /// </summary>
        public void Pump()
        {
            if (!driver.IsCreated) return;
            driver.ScheduleUpdate().Complete();

            if (state == LanTransportState.Hosting)
            {
                NetworkConnection incoming;
                while ((incoming = driver.Accept()) != default)
                {
                    var peer = new Peer { Id = nextPeerId++, Connection = incoming };
                    peers.Add(peer);
                    Connected?.Invoke(peer.Id);
                }
                // Snapshot: a Disconnect event may mutate the live list.
                var snapshot = peers.ToArray();
                foreach (var peer in snapshot)
                    PumpPeer(peer);
            }
            else if (state == LanTransportState.Connecting
                     || state == LanTransportState.Connected)
            {
                PumpPeer(clientPeer);
            }
        }

        /// <summary>Host: send a datagram to one accepted connection on
        /// the reliable-ordered channel.</summary>
        public bool Send(ulong connectionId, byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var connection = Find(connectionId);
            if (!connection.IsCreated)
            {
                LastError = "unknown connection " + connectionId;
                return false;
            }
            return Send(connection, payload, reliablePipeline);
        }

        /// <summary>Client: send a datagram to the host on the
        /// reliable-ordered channel.</summary>
        public bool Send(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (!clientPeer.Connection.IsCreated)
            {
                LastError = "not connected";
                return false;
            }
            return Send(clientPeer.Connection, payload, reliablePipeline);
        }

        /// <summary>Host: send a datagram to one accepted connection on
        /// the sequenced-unreliable gameplay channel. Intended for
        /// high-rate state snapshots where loss is tolerable.</summary>
        public bool SendGameplay(ulong connectionId, byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var connection = Find(connectionId);
            if (!connection.IsCreated)
            {
                LastError = "unknown connection " + connectionId;
                return false;
            }
            return Send(connection, payload, gameplayPipeline);
        }

        /// <summary>Client: send a datagram to the host on the
        /// sequenced-unreliable gameplay channel (input batches).</summary>
        public bool SendGameplay(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (!clientPeer.Connection.IsCreated)
            {
                LastError = "not connected";
                return false;
            }
            return Send(clientPeer.Connection, payload, gameplayPipeline);
        }

        /// <summary>
        /// Release every connection and the underlying socket. After Stop
        /// the same port can be bound again immediately.
        /// </summary>
        public void Stop()
        {
            if (driver.IsCreated)
            {
                var snapshot = peers.ToArray();
                foreach (var peer in snapshot)
                    driver.Disconnect(peer.Connection);
                if (clientPeer.Connection.IsCreated)
                    driver.Disconnect(clientPeer.Connection);
            }
            peers.Clear();
            clientPeer = default;
            nextPeerId = 1;
            DisposeDriver();
            state = LanTransportState.Idle;
        }

        public void Dispose()
        {
            Stop();
        }

        private void PumpPeer(Peer peer)
        {
            if (!peer.Connection.IsCreated) return;
            NetworkEvent.Type command;
            while ((command = driver.PopEventForConnection(peer.Connection,
                       out DataStreamReader reader, out NetworkPipeline pipeline))
                   != NetworkEvent.Type.Empty)
            {
                switch (command)
                {
                    case NetworkEvent.Type.Connect:
                        state = LanTransportState.Connected;
                        Connected?.Invoke(peer.Id);
                        break;
                    case NetworkEvent.Type.Data:
                        // Channel demux: sequenced-unreliable traffic is
                        // gameplay data; everything else (reliable ordered
                        // or a raw default-pipeline datagram) is control.
                        if (pipeline == gameplayPipeline)
                            GameplayMessage?.Invoke(peer.Id, ReadAll(ref reader));
                        else
                            Message?.Invoke(peer.Id, ReadAll(ref reader));
                        break;
                    case NetworkEvent.Type.Disconnect:
                        HandleDisconnect(peer);
                        return; // Connection is dead; no more events for it.
                }
            }
        }

        private void HandleDisconnect(Peer peer)
        {
            if (state == LanTransportState.Hosting)
            {
                for (var i = 0; i < peers.Count; i++)
                {
                    if (peers[i].Id != peer.Id) continue;
                    peers.RemoveAt(i);
                    break;
                }
            }
            else
            {
                // Client side: a dead connection owns a bound driver. Free
                // it now — leaving it alive would leak the socket on the
                // next StartClient, which overwrites the struct handle.
                clientPeer = default;
                state = LanTransportState.Idle;
                DisposeDriver();
            }
            Disconnected?.Invoke(peer.Id);
        }

        private NetworkConnection Find(ulong connectionId)
        {
            foreach (var peer in peers)
                if (peer.Id == connectionId)
                    return peer.Connection;
            return default;
        }

        private bool Send(NetworkConnection connection, byte[] payload,
            NetworkPipeline pipeline)
        {
            if (!driver.IsCreated)
            {
                LastError = "not started";
                return false;
            }
            if (driver.BeginSend(pipeline, connection,
                    out DataStreamWriter writer) != 0)
            {
                LastError = "send could not begin (len " + payload.Length + ")";
                return false;
            }
            for (var i = 0; i < payload.Length; i++)
                writer.WriteByte(payload[i]);
            var sent = driver.EndSend(writer);
            if (sent < 0)
            {
                LastError = "send failed (code " + sent + ", len "
                    + payload.Length + ", pipe " + pipeline + ")";
                return false;
            }
            LastError = "";
            return true;
        }

        private static byte[] ReadAll(ref DataStreamReader reader)
        {
            var length = reader.Length;
            var payload = new byte[length];
            for (var i = 0; i < length; i++)
                payload[i] = reader.ReadByte();
            return payload;
        }

        private bool CheckAccess()
        {
            access.EnsureAccess();
            switch (access.State)
            {
                case LanNetworkAccessState.Available:
                    return true;
                case LanNetworkAccessState.Requesting:
                    LastError = "LAN access still requesting (" + access.Detail + ")";
                    return false;
                default:
                    // Denied or unsupported: report immediately and never
                    // open a socket — the caller sees failure now instead of
                    // discovering it as an endless join timeout.
                    LastError = "LAN access denied (" + access.Detail + ")";
                    return false;
            }
        }

        private static NetworkDriver CreateDriver()
        {
            var settings = new NetworkSettings();
            settings.WithNetworkConfigParameters(
                connectTimeoutMS: ConnectAttemptIntervalMilliseconds,
                maxConnectAttempts: MaxConnectAttempts,
                disconnectTimeoutMS: PeerTimeoutMilliseconds);
            // The fragmentation stage defaults to a 4 KiB payload
            // capacity — far below the 16 KiB snapshot ceiling — so an
            // oversized gameplay packet fails EndSend with
            // NetworkPacketOverflow. Raise the capacity past the largest
            // typed wire bound; EndSend overflow then matches the codec
            // ceiling instead of a hidden 4 KiB limit.
            settings.WithFragmentationStageParameters(
                payloadCapacity: LanSettings.SnapshotCeilingBytes
                    + LanSettings.MaxInputPacketBytes);
            return NetworkDriver.Create(settings);
        }

        // Both roles create identical pipelines before bind/connect. Stage
        // order matters: fragmentation runs first so a large payload is
        // split before sequencing/reliability wrap the fragments (UTP
        // pipelines doc). Reliable+fragmented carries control traffic;
        // unreliable-sequenced+fragmented carries gameplay streams.
        private void CreatePipelines()
        {
            reliablePipeline = driver.CreatePipeline(
                typeof(FragmentationPipelineStage),
                typeof(ReliableSequencedPipelineStage));
            gameplayPipeline = driver.CreatePipeline(
                typeof(FragmentationPipelineStage),
                typeof(UnreliableSequencedPipelineStage));
        }

        private void DisposeDriver()
        {
            if (driver.IsCreated) driver.Dispose();
            driver = default;
            BoundPort = 0;
        }
    }
}
