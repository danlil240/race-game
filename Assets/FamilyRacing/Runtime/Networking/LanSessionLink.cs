using System;
using System.Collections.Generic;
using System.Text;
using FamilyRacing.Core.Networking;

namespace FamilyRacing.Networking
{
    /// <summary>
    /// Runtime adapter that drives the Unity-free LanSessionService over
    /// the real Task-1 sockets: <see cref="LanTransport"/> for gameplay
    /// datagrams and <see cref="LanDiscovery"/> for bounded broadcast
    /// discovery. Session-protocol payloads are UTF-8 strings produced by
    /// Core NetworkProtocol; transport connection ids flow through as
    /// opaque handles. Everything runs on the Unity thread via
    /// <see cref="Pump"/>.
    /// </summary>
    public sealed class LanSessionLink : ILanSessionLink, ILanDiscoveryProbe, IDisposable
    {
        private readonly LanTransport transport;
        private readonly LanDiscovery discovery;
        private LanSessionService session;

        public LanSessionLink(LanTransport transport, LanDiscovery discovery)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
            transport.Connected += OnConnected;
            transport.Disconnected += OnDisconnected;
            transport.Message += OnMessage;
        }

        /// <summary>The session service receiving transport callbacks.</summary>
        public void Bind(LanSessionService service)
        {
            session = service;
        }

        /// <summary>Poll both sockets; call once per frame on the Unity
        /// main thread. Callbacks dispatch into the bound session.</summary>
        public void Pump(DateTime utcNow)
        {
            transport.Pump();
            discovery.Pump(utcNow);
        }

        // --- ILanSessionLink -----------------------------------------------

        public bool StartHost(ushort port, out ushort boundPort, out string error)
        {
            if (!transport.StartHost(port))
            {
                boundPort = 0;
                error = transport.LastError;
                return false;
            }
            boundPort = transport.BoundPort;
            error = "";
            return true;
        }

        public bool StartClient(string address, ushort port, out string error)
        {
            if (!transport.StartClient(address, port))
            {
                error = transport.LastError;
                return false;
            }
            error = "";
            return true;
        }

        public void Stop()
        {
            transport.Stop();
            discovery.StopResponder();
        }

        public bool Send(ulong connectionId, string payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var bytes = Encoding.UTF8.GetBytes(payload);
            // Client transports have exactly one connection (the host);
            // it is addressed through the parameterless send overload.
            if (transport.IsClient) return transport.Send(bytes);
            return transport.Send(connectionId, bytes);
        }

        // --- ILanDiscoveryProbe ---------------------------------------------

        public bool StartSearch(out string error)
        {
            if (!discovery.StartSearch(LanDiscovery.DefaultDiscoveryPort))
            {
                error = discovery.LastError;
                return false;
            }
            error = "";
            return true;
        }

        public void StopSearch()
        {
            discovery.StopSearch();
        }

        public bool StartResponder(ushort gamePort, string hostName, out string error)
        {
            if (!discovery.StartResponder(
                    LanDiscovery.DefaultDiscoveryPort, gamePort, hostName))
            {
                error = discovery.LastError;
                return false;
            }
            error = "";
            return true;
        }

        public void StopResponder()
        {
            discovery.StopResponder();
        }

        public IReadOnlyList<LanDiscoveredHost> Entries
        {
            get
            {
                var mapped = new List<LanDiscoveredHost>(discovery.Entries.Count);
                foreach (var entry in discovery.Entries)
                {
                    mapped.Add(new LanDiscoveredHost
                    {
                        address = entry.Address ?? "",
                        port = entry.Port,
                        hostName = entry.HostName ?? ""
                    });
                }
                return mapped;
            }
        }

        public void Dispose()
        {
            transport.Connected -= OnConnected;
            transport.Disconnected -= OnDisconnected;
            transport.Message -= OnMessage;
            transport.Dispose();
            discovery.Dispose();
        }

        private void OnConnected(ulong connectionId)
        {
            session?.OnLinkConnected(connectionId);
        }

        private void OnDisconnected(ulong connectionId)
        {
            session?.OnLinkDisconnected(connectionId, "disconnect");
        }

        private void OnMessage(ulong connectionId, byte[] payload)
        {
            if (session == null || payload == null) return;
            if (payload.Length > NetworkSettings.MaxWireMessageChars) return;
            session.OnLinkMessage(connectionId, Encoding.UTF8.GetString(payload));
        }
    }
}
