using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using LanSettings = FamilyRacing.Core.Networking.NetworkSettings;

namespace FamilyRacing.Networking
{
    /// <summary>
    /// One discovered LAN host advertisement.
    /// </summary>
    public sealed class LanHostEntry
    {
        public string Address { get; internal set; }
        public ushort Port { get; internal set; }
        public string HostName { get; internal set; }
        /// <summary>The responder's full compatibility token
        /// (gameplay | content | fingerprint) — a protocol-2
        /// advertisement always carries it, so a mismatched token can
        /// fence a join before any datagram is dialed.</summary>
        public string Compat { get; internal set; }
        public DateTime LastSeenUtc { get; internal set; }
        public DateTime ExpiresUtc { get; internal set; }
    }

    /// <summary>
    /// Bounded IPv4 UDP broadcast discovery. A host runs a responder on the
    /// discovery port that answers query datagrams with its gameplay port;
    /// a joiner broadcasts a query once per second on every active LAN
    /// adapter and collects responses into expiring entries (3 s TTL).
    /// All parsing is bounded: any datagram above
    /// <see cref="MaxDatagramBytes"/> or failing the magic/shape checks is
    /// dropped. Sockets are only opened while a role is running —
    /// <see cref="Stop"/>/Dispose close them, and every per-send/per-
    /// adapter failure is contained so a broadcast-blocking adapter cannot
    /// take down discovery. Manual unicast join never goes through this
    /// class at all.
    /// </summary>
    public sealed class LanDiscovery : IDisposable
    {
        /// <summary>Default discovery UDP port — the single definition
        /// lives in Core; this alias keeps the public API callers use.</summary>
        public const ushort DefaultDiscoveryPort = LanSettings.DefaultDiscoveryPort;

        /// <summary>Hard ceiling for any query or response datagram.</summary>
        public const int MaxDatagramBytes = 1024;

        /// <summary>How often a query is broadcast while searching.</summary>
        public static readonly TimeSpan QueryInterval = TimeSpan.FromSeconds(1);

        /// <summary>How long an unseen entry stays listed.</summary>
        public static readonly TimeSpan EntryLifetime = TimeSpan.FromSeconds(3);

        // Datagram schema 2: the response payload gains a compatibility
        // token, so the schema moves — a protocol-2 query is never
        // answered by a schema-1 responder, which is exactly the fence a
        // legacy host needs.
        private static readonly byte[] QueryMagic = Encoding.ASCII.GetBytes("FRLANQ02");
        private static readonly byte[] ResponseMagic = Encoding.ASCII.GetBytes("FRLANR02");

        private readonly ILanNetworkAccess access;
        private readonly List<LanHostEntry> entries = new List<LanHostEntry>(8);
        private UdpClient responderSocket;
        private UdpClient querySocket;
        private byte[] responderPayload = new byte[0];
        private ushort discoveryPort;
        private DateTime nextQueryUtc = DateTime.MinValue;

        public LanDiscovery() : this(LanNetworkAccess.Create()) { }

        public LanDiscovery(ILanNetworkAccess access)
        {
            this.access = access ?? throw new ArgumentNullException(nameof(access));
        }

        /// <summary>Current visible hosts (deduplicated, unexpired).</summary>
        public IReadOnlyList<LanHostEntry> Entries => entries;

        /// <summary>Fired on the calling (Unity) thread whenever the entry
        /// set changes — new host, refreshed metadata or expiry.</summary>
        public event Action EntriesChanged;

        /// <summary>Last operational failure; empty when clean.</summary>
        public string LastError { get; private set; } = "";

        public bool IsResponding => responderSocket != null;
        public bool IsSearching => querySocket != null;

        /// <summary>
        /// Start answering queries on <paramref name="discoveryPort"/> with
        /// an advertisement for the given gameplay port.
        /// </summary>
        public bool StartResponder(ushort discoveryPort, ushort gamePort,
            string hostName, string compat)
        {
            if (!CheckAccess()) return false;
            StopResponder();
            try
            {
                var socket = new UdpClient();
                socket.EnableBroadcast = true;
                socket.Client.SetSocketOption(SocketOptionLevel.Socket,
                    SocketOptionName.ReuseAddress, true);
                socket.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
                responderSocket = socket;
                responderPayload = BuildResponse(gamePort, hostName ?? "",
                    compat ?? "");
                this.discoveryPort = discoveryPort;
                LastError = "";
                return true;
            }
            catch (Exception e)
            {
                LastError = "discovery responder bind failed on port " +
                    discoveryPort + ": " + e.Message;
                StopResponder();
                return false;
            }
        }

        /// <summary>
        /// Start broadcasting queries on <paramref name="discoveryPort"/>.
        /// Queries go to the limited broadcast address and to each active
        /// LAN adapter's directed broadcast address.
        /// </summary>
        public bool StartSearch(ushort discoveryPort)
        {
            if (!CheckAccess()) return false;
            StopSearch();
            try
            {
                var socket = new UdpClient();
                socket.EnableBroadcast = true;
                // Ephemeral local port: responses are unicast back to the
                // sender endpoint, so no fixed bind is needed and multiple
                // searchers can share one machine.
                socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                querySocket = socket;
                this.discoveryPort = discoveryPort;
                nextQueryUtc = DateTime.MinValue;
                LastError = "";
                return true;
            }
            catch (Exception e)
            {
                LastError = "discovery search start failed: " + e.Message;
                StopSearch();
                return false;
            }
        }

        /// <summary>
        /// Service both roles: send due queries, drain received datagrams,
        /// expire entries. Call each frame on the Unity thread.
        /// </summary>
        public void Pump(DateTime utcNow)
        {
            if (querySocket != null && utcNow >= nextQueryUtc)
            {
                SendQueries(utcNow);
                nextQueryUtc = utcNow + QueryInterval;
            }
            if (querySocket != null) DrainSearchReplies(utcNow);
            if (responderSocket != null) DrainQueries();
            ExpireEntries(utcNow);
        }

        /// <summary>Close only the responder socket.</summary>
        public void StopResponder()
        {
            if (responderSocket == null) return;
            try { responderSocket.Close(); } catch (Exception) { }
            responderSocket = null;
        }

        /// <summary>Close only the search socket and forget the results.</summary>
        public void StopSearch()
        {
            if (querySocket != null)
            {
                try { querySocket.Close(); } catch (Exception) { }
                querySocket = null;
            }
            if (entries.Count > 0)
            {
                entries.Clear();
                EntriesChanged?.Invoke();
            }
        }

        /// <summary>Close both sockets.</summary>
        public void Stop()
        {
            StopResponder();
            StopSearch();
        }

        public void Dispose()
        {
            Stop();
        }

        private void SendQueries(DateTime utcNow)
        {
            var failures = 0;
            foreach (var target in BroadcastTargets(discoveryPort))
            {
                try
                {
                    querySocket.Send(QueryMagic, QueryMagic.Length, target);
                }
                catch (Exception)
                {
                    // One adapter refusing broadcast must not stop the rest.
                    failures++;
                }
            }
            if (failures > 0)
                LastError = failures + " broadcast target(s) refused the discovery query";
        }

        private void DrainSearchReplies(DateTime utcNow)
        {
            while (querySocket.Available > 0)
            {
                IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] datagram;
                try
                {
                    datagram = querySocket.Receive(ref remote);
                }
                catch (Exception) { break; }
                if (!TryParseResponse(datagram, out ushort gamePort,
                        out string hostName, out string compat))
                    continue;
                Upsert(remote.Address.ToString(), gamePort, hostName,
                    compat, utcNow);
            }
        }

        private void DrainQueries()
        {
            while (responderSocket.Available > 0)
            {
                IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] datagram;
                try
                {
                    datagram = responderSocket.Receive(ref remote);
                }
                catch (Exception) { break; }
                if (!IsQuery(datagram)) continue;
                try
                {
                    responderSocket.Send(responderPayload, responderPayload.Length, remote);
                }
                catch (Exception)
                {
                    // A single undeliverable reply is dropped silently; the
                    // next 1 Hz query round retries it.
                }
            }
        }

        private void Upsert(string address, ushort gamePort, string hostName,
            string compat, DateTime utcNow)
        {
            var expiry = utcNow + EntryLifetime;
            foreach (var entry in entries)
            {
                if (entry.Address != address || entry.Port != gamePort) continue;
                if (entry.HostName == hostName && entry.Compat == compat)
                {
                    entry.LastSeenUtc = utcNow;
                    entry.ExpiresUtc = expiry;
                    return;
                }
                entry.HostName = hostName;
                entry.Compat = compat;
                entry.LastSeenUtc = utcNow;
                entry.ExpiresUtc = expiry;
                EntriesChanged?.Invoke();
                return;
            }
            entries.Add(new LanHostEntry
            {
                Address = address,
                Port = gamePort,
                HostName = hostName,
                Compat = compat,
                LastSeenUtc = utcNow,
                ExpiresUtc = expiry
            });
            EntriesChanged?.Invoke();
        }

        private void ExpireEntries(DateTime utcNow)
        {
            var removed = false;
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].ExpiresUtc > utcNow) continue;
                entries.RemoveAt(i);
                removed = true;
            }
            if (removed) EntriesChanged?.Invoke();
        }

        private static bool IsQuery(byte[] datagram)
        {
            if (datagram == null || datagram.Length != QueryMagic.Length
                || datagram.Length > MaxDatagramBytes)
                return false;
            for (var i = 0; i < QueryMagic.Length; i++)
                if (datagram[i] != QueryMagic[i]) return false;
            return true;
        }

        // Response layout (all little-endian, total <= MaxDatagramBytes):
        //   8 B magic | 2 B gameplay port | 1 B host-name length
        //   | host name UTF-8 | 2 B compat-token length | compat UTF-8
        private static byte[] BuildResponse(ushort gamePort, string hostName,
            string compat)
        {
            var nameBytes = Encoding.UTF8.GetBytes(hostName);
            if (nameBytes.Length > 200) nameBytes = Truncate(nameBytes, 200);
            var compatBytes = Encoding.UTF8.GetBytes(compat);
            // A compatibility token longer than the datagram budget can
            // never be advertised; refuse to emit a truncated token a
            // peer might mistake for the whole one.
            if (compatBytes.Length > 500) compatBytes = new byte[0];
            var payload = new byte[ResponseMagic.Length + 5
                + nameBytes.Length + compatBytes.Length];
            Array.Copy(ResponseMagic, payload, ResponseMagic.Length);
            payload[ResponseMagic.Length] = (byte)(gamePort & 0xFF);
            payload[ResponseMagic.Length + 1] = (byte)(gamePort >> 8);
            payload[ResponseMagic.Length + 2] = (byte)nameBytes.Length;
            Array.Copy(nameBytes, 0, payload, ResponseMagic.Length + 3,
                nameBytes.Length);
            var compatOffset = ResponseMagic.Length + 3 + nameBytes.Length;
            payload[compatOffset] = (byte)(compatBytes.Length & 0xFF);
            payload[compatOffset + 1] = (byte)(compatBytes.Length >> 8);
            Array.Copy(compatBytes, 0, payload, compatOffset + 2,
                compatBytes.Length);
            return payload;
        }

        private static bool TryParseResponse(byte[] datagram, out ushort gamePort,
            out string hostName, out string compat)
        {
            gamePort = 0;
            hostName = null;
            compat = null;
            if (datagram == null || datagram.Length > MaxDatagramBytes
                || datagram.Length < ResponseMagic.Length + 5)
                return false;
            for (var i = 0; i < ResponseMagic.Length; i++)
                if (datagram[i] != ResponseMagic[i]) return false;
            gamePort = (ushort)(datagram[ResponseMagic.Length]
                | (datagram[ResponseMagic.Length + 1] << 8));
            var nameLength = datagram[ResponseMagic.Length + 2];
            var compatOffset = ResponseMagic.Length + 3 + nameLength;
            if (datagram.Length < compatOffset + 2) return false;
            var compatLength = datagram[compatOffset]
                | (datagram[compatOffset + 1] << 8);
            if (datagram.Length != compatOffset + 2 + compatLength)
                return false;
            hostName = Encoding.UTF8.GetString(datagram,
                ResponseMagic.Length + 3, nameLength);
            compat = Encoding.UTF8.GetString(datagram,
                compatOffset + 2, compatLength);
            return true;
        }

        private static byte[] Truncate(byte[] bytes, int length)
        {
            var cut = new byte[length];
            Array.Copy(bytes, cut, length);
            return cut;
        }

        // Every active, non-loopback IPv4 adapter's directed broadcast plus
        // the limited broadcast address. Adapter enumeration failures are
        // contained: a broken NIC yields no extra target, never an abort.
        private static List<IPEndPoint> BroadcastTargets(ushort port)
        {
            var targets = new List<IPEndPoint>
            {
                new IPEndPoint(IPAddress.Broadcast, port)
            };
            NetworkInterface[] adapters;
            try
            {
                adapters = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (Exception)
            {
                return targets;
            }
            foreach (var adapter in adapters)
            {
                IPInterfaceProperties properties;
                try
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up
                        || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback
                        || !adapter.Supports(NetworkInterfaceComponent.IPv4))
                        continue;
                    properties = adapter.GetIPProperties();
                }
                catch (Exception) { continue; }
                foreach (var unicast in properties.UnicastAddresses)
                {
                    try
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork
                            || unicast.IPv4Mask == null)
                            continue;
                        var address = unicast.Address.GetAddressBytes();
                        var mask = unicast.IPv4Mask.GetAddressBytes();
                        var broadcast = new byte[4];
                        for (var i = 0; i < 4; i++)
                            broadcast[i] = (byte)(address[i] | ~mask[i]);
                        targets.Add(new IPEndPoint(new IPAddress(broadcast), port));
                    }
                    catch (Exception) { }
                }
            }
            return targets;
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
                    LastError = "LAN access denied (" + access.Detail + ")";
                    return false;
            }
        }
    }
}
