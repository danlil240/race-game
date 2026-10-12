using System;
using System.Collections;
using System.Text;
using FamilyRacing.Networking;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FamilyRacing.Tests
{
    // Smoke tests proving the pinned LAN transport really moves bytes:
    // a host and a client run as two real UDP sockets in one process and
    // exchange payloads over 127.0.0.1 — no mock transport anywhere in the
    // path. Ports come from the OS (host binds port 0) so the suite can run
    // alongside a live game or a second editor process.
    public sealed class LanTransportPlayTests
    {
        private sealed class StubNetworkAccess : ILanNetworkAccess
        {
            private LanNetworkAccessState state;

            public StubNetworkAccess(LanNetworkAccessState initial)
            {
                state = initial;
            }

            public LanNetworkAccessState State => state;
            public string Detail => "stub";
            public int EnsureCalls { get; private set; }

            public void EnsureAccess()
            {
                EnsureCalls++;
            }

            public void Set(LanNetworkAccessState next)
            {
                state = next;
            }
        }

        private const float TimeoutSeconds = 5f;

        private static IEnumerator PumpUntil(Func<bool> done,
            params LanTransport[] transports)
        {
            return PumpUntil(done, TimeoutSeconds, transports);
        }

        private static IEnumerator PumpUntil(Func<bool> done, float timeout,
            params LanTransport[] transports)
        {
            var until = Time.unscaledTime + timeout;
            while (Time.unscaledTime < until && !done())
            {
                foreach (var transport in transports)
                    transport?.Pump();
                yield return null;
            }
            Assert.That(done(), Is.True, "Timed out waiting for LAN transport condition.");
        }

        [UnityTest]
        public IEnumerator HostClientExchangeUsesRealTransport()
        {
            var host = new LanTransport(new StubNetworkAccess(LanNetworkAccessState.Available));
            var client = new LanTransport(new StubNetworkAccess(LanNetworkAccessState.Available));
            try
            {
                Assert.That(host.StartHost(0), Is.True, "Host bind failed: " + host.LastError);
                Assert.That(host.BoundPort, Is.GreaterThan(0));
                Assert.That(client.StartClient("127.0.0.1", host.BoundPort), Is.True,
                    "Client start failed: " + client.LastError);

                ulong hostPeer = 0;
                ulong clientPeer = 0;
                host.Connected += id => hostPeer = id;
                client.Connected += id => clientPeer = id;
                yield return PumpUntil(() => hostPeer != 0 && clientPeer != 0, host, client);

                byte[] hostReceived = null;
                byte[] clientReceived = null;
                host.Message += (id, payload) => hostReceived = payload;
                client.Message += (id, payload) => clientReceived = payload;

                var request = Encoding.UTF8.GetBytes("hello-host");
                Assert.That(client.Send(request), Is.True);
                yield return PumpUntil(() => hostReceived != null, host, client);
                Assert.That(Encoding.UTF8.GetString(hostReceived), Is.EqualTo("hello-host"));

                var reply = Encoding.UTF8.GetBytes("hello-client");
                Assert.That(host.Send(hostPeer, reply), Is.True);
                yield return PumpUntil(() => clientReceived != null, host, client);
                Assert.That(Encoding.UTF8.GetString(clientReceived), Is.EqualTo("hello-client"));
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator StopReleasesSockets()
        {
            var access = new StubNetworkAccess(LanNetworkAccessState.Available);
            var host = new LanTransport(access);
            var client = new LanTransport(access);
            try
            {
                Assert.That(host.StartHost(0), Is.True, "First bind failed: " + host.LastError);
                var port = host.BoundPort;
                Assert.That(port, Is.GreaterThan(0));
                host.Stop();
                Assert.That(host.State, Is.EqualTo(LanTransportState.Idle));

                // The released port must be rebindable: a lingering socket
                // would fail this second bind on the exact same port.
                var restarted = false;
                for (var attempt = 0; attempt < 5 && !restarted; attempt++)
                {
                    restarted = host.StartHost(port);
                    if (!restarted) yield return null;
                }
                Assert.That(restarted, Is.True,
                    "Port not released by Stop: " + host.LastError);

                Assert.That(client.StartClient("127.0.0.1", port), Is.True);
                var connected = false;
                client.Connected += _ => connected = true;
                yield return PumpUntil(() => connected, host, client);
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        // The binding milestone timing budget is 5 s of monotonic wall
        // time for a failed join; UTP's default would hang ~60 s. The test
        // waits at most 8 s — far below the old default — so it only
        // passes when the configured timeout actually fires near 5 s.
        [UnityTest]
        public IEnumerator JoinDeadHostTimesOutWithinJoinBudget()
        {
            var access = new StubNetworkAccess(LanNetworkAccessState.Available);
            ushort deadPort;
            using (var probe = new LanTransport(access))
            {
                Assert.That(probe.StartHost(0), Is.True);
                deadPort = probe.BoundPort;
            }
            var client = new LanTransport(access);
            try
            {
                Assert.That(client.StartClient("127.0.0.1", deadPort), Is.True);
                Assert.That(client.State, Is.EqualTo(LanTransportState.Connecting));

                var disconnected = false;
                client.Disconnected += _ => disconnected = true;
                var start = Time.unscaledTime;
                var deadline = start + 8f;
                while (Time.unscaledTime < deadline && client.IsConnecting)
                {
                    client.Pump();
                    yield return null;
                }
                var elapsed = Time.unscaledTime - start;
                Assert.That(client.IsConnecting, Is.False,
                    "Join still pending after 8 s — join timeout not bounded to 5 s (elapsed "
                    + elapsed + " s)");
                Assert.That(disconnected, Is.True,
                    "Join timeout must surface as a Disconnect event, not silence");
                Assert.That(client.State, Is.EqualTo(LanTransportState.Idle));
            }
            finally
            {
                client.Dispose();
            }
        }

        // A remote disconnect used to leave the client driver bound; the
        // next StartClient would overwrite the live struct and leak the
        // socket. This exercises that exact rejoin path.
        [UnityTest]
        public IEnumerator ReconnectAfterRemoteDisconnectSucceeds()
        {
            var access = new StubNetworkAccess(LanNetworkAccessState.Available);
            var hostA = new LanTransport(access);
            var hostB = new LanTransport(access);
            var client = new LanTransport(access);
            try
            {
                Assert.That(hostA.StartHost(0), Is.True);
                Assert.That(client.StartClient("127.0.0.1", hostA.BoundPort), Is.True);
                var connected = false;
                client.Connected += _ => connected = true;
                yield return PumpUntil(() => connected, hostA, client);

                hostA.Stop();
                var dropped = false;
                client.Disconnected += _ => dropped = true;
                // Peer-loss detection fires at the 5 s peer timeout;
                // allow margin above it.
                yield return PumpUntil(() => dropped, 10f, client);
                Assert.That(client.State, Is.EqualTo(LanTransportState.Idle));

                Assert.That(hostB.StartHost(0), Is.True);
                Assert.That(client.StartClient("127.0.0.1", hostB.BoundPort), Is.True,
                    "Rejoin after remote disconnect failed: " + client.LastError);
                var reconnected = false;
                client.Connected += _ => reconnected = true;
                yield return PumpUntil(() => reconnected, hostB, client);

                byte[] received = null;
                hostB.Message += (id, payload) => received = payload;
                Assert.That(client.Send(Encoding.UTF8.GetBytes("rejoin")), Is.True);
                yield return PumpUntil(() => received != null, hostB, client);
            }
            finally
            {
                hostA.Dispose();
                hostB.Dispose();
                client.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator DeniedAccessDoesNotOpenSockets()
        {
            var access = new StubNetworkAccess(LanNetworkAccessState.Denied);
            var host = new LanTransport(access);
            var client = new LanTransport(access);
            try
            {
                Assert.That(host.StartHost(0), Is.False);
                Assert.That(host.LastError, Does.Contain("denied"));
                Assert.That(host.BoundPort, Is.EqualTo(0));

                Assert.That(client.StartClient("127.0.0.1", LanTransport.DefaultGameplayPort),
                    Is.False);
                Assert.That(client.LastError, Does.Contain("denied"));

                // Denial must not leave a pending connect that could hang a
                // join: pump several frames and confirm the transport stays
                // idle instead of entering a connecting state.
                var deadline = Time.unscaledTime + 0.5f;
                while (Time.unscaledTime < deadline)
                {
                    host.Pump();
                    client.Pump();
                    yield return null;
                }
                Assert.That(host.State, Is.EqualTo(LanTransportState.Idle));
                Assert.That(client.State, Is.EqualTo(LanTransportState.Idle));
                Assert.That(client.IsConnecting, Is.False);
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }
    }
}
