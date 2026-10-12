using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using FamilyRacing.Networking;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FamilyRacing.Tests
{
    // Task 7 lifecycle over the real UDP transport: two (sometimes three)
    // full rigs — session service + LanSessionLink + LanTransport +
    // replication bridge — exchange the actual wire packets over
    // 127.0.0.1. Assertions pin the shared-pause authority, reserved
    // slots, credentialed rejoin with epoch fencing, and host-loss
    // behavior the EditMode suite pins at policy level.
    public sealed class NetworkLifecyclePlayTests
    {
        private sealed class StubNetworkAccess : ILanNetworkAccess
        {
            public LanNetworkAccessState State =>
                LanNetworkAccessState.Available;
            public string Detail => "stub";
            public void EnsureAccess() { }
        }

        private sealed class FlatWorld : IMotorWorld
        {
            public GroundHit ProbeGround(MotorPose pose, float probeDistance)
            {
                return new GroundHit
                {
                    hit = pose.y <= probeDistance,
                    x = pose.x, y = 0f, z = pose.z,
                    normalY = 1f, distance = pose.y, surfaceId = "ground"
                };
            }

            public SweepHit SweepBody(MotorPose pose, float dx, float dy,
                float dz, float halfWidth, float halfLength, float height)
            {
                return new SweepHit { hit = false, fraction = 1f };
            }
        }

        private static readonly FlatWorld World = new FlatWorld();
        private static readonly DriverInput Neutral =
            new DriverInput(0f, false, false, false, false, false, false);
        private const float TimeoutSeconds = 12f;

        private sealed class Rig : IDisposable
        {
            public readonly LanTransport Transport;
            public readonly LanDiscovery Discovery;
            public readonly LanSessionLink Link;
            public readonly LanSessionService Session;
            public readonly NetworkRaceBridge Bridge;
            public double Now;
            // The connection id this rig's transport assigned to its last
            // outbound (client-side server) link.
            public ulong ServerConnectionId;

            public Rig(string participantId, ILanNetworkAccess access,
                string credentialStorePath = null)
            {
                Transport = new LanTransport(access);
                Discovery = new LanDiscovery(access);
                Link = new LanSessionLink(Transport, Discovery);
                Session = new LanSessionService(Link, Link, "gameplay-test");
                if (credentialStorePath != null)
                    Session.SetReconnectStore(
                        new ReconnectCredentialStore(credentialStorePath));
                Link.Bind(Session);
                Session.SetLocalContext(participantId, "fox", 0, true, true);
                Session.SetContent(Catalog());
                Bridge = new NetworkRaceBridge(Transport, Session);
                Bridge.SetContent(Catalog());
                Transport.Connected += id => ServerConnectionId = id;
            }

            public void PumpFrame()
            {
                Now += 0.033;
                Link.Pump(DateTime.UtcNow);
                Session.AdvanceWallTime(Now);
                Bridge.Pump();
            }

            public void Dispose()
            {
                Bridge.Dispose();
                Link.Dispose();
            }
        }

        private static DrivingContentCatalog Catalog()
        {
            var course = new CourseDefinition
            {
                id = "course-lan",
                checkpoints = new List<CheckpointGate>
                {
                    new CheckpointGate
                    {
                        id = "start", x = 0f, y = 0f, z = 0f,
                        halfWidth = 6f, dirX = 0f, dirZ = 1f,
                        recoverX = 0f, recoverZ = -3f
                    },
                    new CheckpointGate
                    {
                        id = "gate-1", x = 0f, y = 0f, z = 2000f,
                        halfWidth = 6f, dirX = 0f, dirZ = 1f,
                        recoverX = 0f, recoverZ = 1500f
                    }
                },
                spawnSlots = new List<MotorPose>
                {
                    new MotorPose { x = -6f, z = 0f },
                    new MotorPose { x = -2f, z = 0f },
                    new MotorPose { x = 2f, z = 0f },
                    new MotorPose { x = 6f, z = 0f }
                }
            };
            var mixed = new EventDefinition
            {
                id = "lan-mixed",
                courseId = "course-lan",
                laps = 1,
                countdownTicks = 8,
                vehicleIds = new List<string> { "car-01", "motorcycle-01" }
            };
            return DrivingContentCatalog.Create("lan-content-1",
                new[] { VehicleTuning.Car01(), VehicleTuning.Motorcycle01() },
                new[] { course }, new[] { mixed });
        }

        private static GameCommand Cmd(string action,
            Dictionary<string, string> parameters)
        {
            return new GameCommand(1, "lan-lifecycle-play",
                Guid.NewGuid().ToString("N"), "local", action, parameters);
        }

        private static Dictionary<string, string> P(params string[] keyValues)
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i + 1 < keyValues.Length; i += 2)
                map[keyValues[i]] = keyValues[i + 1];
            return map;
        }

        private static IEnumerator PumpUntil(Func<bool> done,
            params Rig[] rigs)
        {
            return PumpUntil(done, "", rigs);
        }

        private static IEnumerator PumpUntil(Func<bool> done,
            string what, params Rig[] rigs)
        {
            // Deadlines ride the real clock, not Time.unscaledTime —
            // batchmode advances the game clock in bursts, while peer
            // timeouts below are wall-clock on the socket.
            var deadline = DateTime.UtcNow.AddSeconds(TimeoutSeconds);
            var frames = 0;
            while (DateTime.UtcNow < deadline && !done())
            {
                foreach (var rig in rigs) rig.PumpFrame();
                frames++;
                yield return null;
            }
            var diag = "";
            foreach (var rig in rigs)
            {
                if (rig == null) continue;
                var s = rig.Session.Snapshot();
                var ack = rig.Bridge.AckFor("p-guest");
                diag += " | " + rig.Session.Role + "/" + s.phase
                    + " paused=" + s.paused
                    + " err=" + s.lastError
                    + " ops=" + s.pendingOperations.Count
                    + " conns=" + (s.role == "host"
                        ? string.Join("+", rig.Session.HostConnections())
                        : "-")
                    + " parts=" + string.Join(",",
                        s.participants.ConvertAll(p =>
                            p.participantId + ":" + p.connected + ":"
                            + p.controlSource + ":e" + p.ownershipEpoch))
                    + " tstate=" + rig.Transport.State
                    + " terr=" + rig.Transport.LastError
                    + " lastErr=" + rig.Bridge.LastError
                    + " lastInErr=" + rig.Bridge.LastInputError
                    + " drops=" + rig.Bridge.DroppedPackets
                    + " stale=" + rig.Bridge.StaleSnapshots
                    + " gpk=" + rig.Bridge.GameplayPacketsSeen
                    + " gconn=" + rig.Bridge.LastGameplayConnection
                    + " spub=" + rig.Bridge.SnapshotsPublished
                    + " ack=" + (ack != null ? ack.inputSequence.ToString() : "-")
                    + " race=" + (rig.Bridge.LatestState != null
                        ? rig.Bridge.LatestState.phase : "none")
                    + " ht=" + (rig.Bridge.LatestState != null
                        ? rig.Bridge.LatestState.hostTick.ToString() : "-")
                    + " sseq=" + (rig.Bridge.CurrentSnapshot != null
                        ? rig.Bridge.CurrentSnapshot.snapshotSequence
                            .ToString() : "-");
            }
            Assert.That(done(), Is.True,
                "Timed out waiting for LAN condition (" + what
                + ", frames=" + frames + ")." + diag);
        }

        private static int Occupied(NetworkSessionState state)
        {
            var count = 0;
            foreach (var p in state.participants) if (p.occupied) count++;
            return count;
        }

        private static ParticipantState Find(NetworkSessionState state,
            string participantId)
        {
            foreach (var p in state.participants)
                if (p.participantId == participantId) return p;
            return null;
        }

        private static NetworkRacerState RacerOf(NetworkRaceState state,
            string participantId)
        {
            if (state == null || state.racers == null) return null;
            foreach (var racer in state.racers)
                if (racer != null && racer.participantId == participantId)
                    return racer;
            return null;
        }

        private static void Tick(Rig host, Rig client,
            DriverInput hostInput, DriverInput clientInput,
            List<DrivingEvent> hostEvents, List<DrivingEvent> clientEvents)
        {
            host.PumpFrame();
            client.PumpFrame();
            host.Bridge.StepTick(hostInput, World);
            client.Bridge.StepTick(clientInput, null);
            if (hostEvents != null)
                hostEvents.AddRange(host.Bridge.DrainRaceEvents());
            if (clientEvents != null)
                clientEvents.AddRange(client.Bridge.DrainRaceEvents());
        }

        private static IEnumerator StartTwoPlayerRace(Rig host, Rig client)
        {
            var hosted = host.Session.Execute(Cmd("lan.host", P("port", "0")));
            Assert.That(hosted.status, Is.EqualTo(CommandResult.Applied),
                "lan.host failed: " + hosted.code);
            Assert.That(host.Transport.BoundPort, Is.GreaterThan(0));

            var joined = client.Session.Execute(Cmd("lan.join",
                P("address", "127.0.0.1",
                  "port", host.Transport.BoundPort.ToString(
                      System.Globalization.CultureInfo.InvariantCulture))));
            Assert.That(joined.status, Is.EqualTo(CommandResult.Accepted),
                "lan.join rejected: " + joined.code);

            yield return PumpUntil(() =>
                client.Session.Snapshot().phase == LanSessionService.PhaseLobby
                && Occupied(host.Session.Snapshot()) == 2,
                host, client);

            Assert.That(host.Session.Execute(Cmd("lan.configure",
                P("eventId", "lan-mixed", "classId", "car"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(host.Session.Execute(Cmd("lan.ready",
                P("ready", "true"))).status, Is.EqualTo(CommandResult.Applied));
            var ready = client.Session.Execute(Cmd("lan.ready",
                P("ready", "true")));
            Assert.That(ready.status, Is.Not.EqualTo(CommandResult.Rejected),
                "client ready rejected: " + ready.code);
            yield return PumpUntil(() =>
                Find(host.Session.Snapshot(), "p-guest")?.ready == true,
                host, client);

            var started = host.Session.Execute(Cmd("lan.start", null));
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied),
                "lan.start failed: " + started.code);

            yield return PumpUntil(() =>
            {
                host.PumpFrame();
                client.PumpFrame();
                host.Bridge.StepTick(Neutral, World);
                client.Bridge.StepTick(Neutral, null);
                return host.Bridge.LatestState != null
                    && client.Bridge.LatestState != null;
            }, host, client);
            Assert.That(client.Bridge.LatestState.raceId,
                Is.EqualTo(host.Bridge.LatestState.raceId));
        }

        // The socket dies exactly like a process suspend/kill; the session
        // is told what the transport would report, and the host notices
        // through its participant-keyed disconnect policy (the same call a
        // heartbeat/peer-timeout monitor makes).
        private static void KillClientLink(Rig host, Rig client,
            string participantId)
        {
            client.Transport.Stop();
            client.Session.OnLinkDisconnected(client.ServerConnectionId,
                "suspended");
            host.Session.OnDisconnected(participantId, "peerTimeout");
        }

        private static IEnumerator ReconnectClient(Rig host, Rig client,
            string participantId, long expectedEpoch)
        {
            var rejoin = client.Session.Execute(Cmd("lan.reconnect", null));
            Assert.That(rejoin.status, Is.EqualTo(CommandResult.Accepted),
                "lan.reconnect rejected: " + rejoin.code);
            yield return PumpUntil(() =>
            {
                var p = Find(host.Session.Snapshot(), participantId);
                var phase = client.Session.Snapshot().phase;
                // A shared pause freezes the session in countdown — both
                // live phases mean the reclaim landed.
                return p != null && p.connected
                    && p.controlSource == "remote"
                    && p.ownershipEpoch == expectedEpoch
                    && (phase == LanSessionService.PhaseRacing
                        || phase == LanSessionService.PhaseCountdown);
            }, "reconnect " + participantId + " e" + expectedEpoch,
            host, client);
        }

        [UnityTest]
        public IEnumerator ReconnectKeepsSameRacerAndProgress()
        {
            var storePath = Path.Combine(Path.GetTempPath(),
                "fr-reconnect-" + Guid.NewGuid().ToString("N"),
                "lan-reconnect.json");
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess(), storePath);
            try
            {
                yield return StartTwoPlayerRace(host, client);
                for (var i = 0; i < 24; i++)
                {
                    Tick(host, client, Neutral,
                        new DriverInput(0.8f, false, false, false,
                            false, false, false),
                        null, null);
                    if (i % 4 == 0) yield return null;
                }
                var before = RacerOf(host.Bridge.LatestState, "p-guest");
                Assert.That(before, Is.Not.Null);
                var tickBefore = before.session.simulationTick;
                var lapBefore = before.session.lap;

                // Client process dies mid-race; slot is reserved under AI.
                KillClientLink(host, client, "p-guest");
                var reserved = Find(host.Session.Snapshot(), "p-guest");
                Assert.That(reserved.connected, Is.False);
                Assert.That(reserved.controlSource, Is.EqualTo("ai"));

                yield return ReconnectClient(host, client, "p-guest", 2);

                // Same slot, same participant, same session — the
                // reservation never reset the racer's progress, and the
                // session keeps advancing on live ticks after rejoin.
                var after = RacerOf(host.Bridge.LatestState, "p-guest");
                Assert.That(after, Is.Not.Null);
                Assert.That(after.slot, Is.EqualTo(before.slot));
                Assert.That(after.participantId, Is.EqualTo("p-guest"));
                Assert.That(after.session.simulationTick,
                    Is.GreaterThanOrEqualTo(tickBefore),
                    "the reserved racer's progress must carry across rejoin");
                Assert.That(after.session.lap,
                    Is.GreaterThanOrEqualTo(lapBefore));
                for (var i = 0; i < 8; i++)
                {
                    Tick(host, client, Neutral, Neutral, null, null);
                    if (i % 4 == 0) yield return null;
                }
                after = RacerOf(host.Bridge.LatestState, "p-guest");
                Assert.That(after.session.simulationTick,
                    Is.GreaterThan(tickBefore),
                    "the same session keeps ticking after rejoin");
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator OldEpochCannotControlReclaimedSlot()
        {
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess());
            try
            {
                yield return StartTwoPlayerRace(host, client);
                for (var i = 0; i < 10; i++)
                {
                    Tick(host, client, Neutral, Neutral, null, null);
                    if (i % 4 == 0) yield return null;
                }
                KillClientLink(host, client, "p-guest");
                yield return ReconnectClient(host, client, "p-guest", 2);

                // Forge an old-epoch input burst on the live connection:
                // epoch 1 must be fenced out before it can steer.
                var baseline = host.Bridge.LatestState;
                var rejectsBefore = host.Bridge.InputRejections;
                var forged = new List<NetworkInputFrame>
                {
                    new NetworkInputFrame
                    {
                        sessionId = baseline.sessionId,
                        raceId = baseline.raceId,
                        participantId = "p-guest",
                        ownershipEpoch = 1,
                        inputSequence = 900,
                        clientTick = 900,
                        steering = 1f
                    }
                };
                byte[] packet;
                string error;
                Assert.That(NetworkWireCodec.TryEncodeInputBatch(forged,
                    out packet, out error), Is.True, error);
                Assert.That(client.Transport.SendGameplay(packet), Is.True);
                yield return PumpUntil(() =>
                    host.Bridge.InputRejections > rejectsBefore
                    && host.Bridge.LastInputError == "staleEpoch",
                    host, client);

                // The held input the fenced frame tried to plant never
                // reaches the racer.
                for (var i = 0; i < 6; i++)
                    Tick(host, client, Neutral, Neutral, null, null);
                var racer = RacerOf(host.Bridge.LatestState, "p-guest");
                Assert.That(racer.session.input.steering,
                    Is.EqualTo(0f).Within(0.0001f),
                    "old-epoch input must never control a reclaimed racer");
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator RejoinBeforeTimeoutSupersedesOldConnection()
        {
            var storeDir = Path.Combine(Path.GetTempPath(),
                "fr-supersede-" + Guid.NewGuid().ToString("N"));
            var storePath = Path.Combine(storeDir, "lan-reconnect.json");
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess(), storePath);
            try
            {
                yield return StartTwoPlayerRace(host, client);
                for (var i = 0; i < 8; i++)
                {
                    Tick(host, client, Neutral, Neutral, null, null);
                    if (i % 4 == 0) yield return null;
                }
                // The credential must have been persisted privately —
                // process restart is exactly this file surviving.
                Assert.That(File.Exists(storePath), Is.True,
                    "the credential store must persist across restart");

                // The old socket stays alive (pre-timeout): the restarted
                // process rejoins on the stored credential from a fresh rig.
                var connBefore = host.Session.HostConnections();
                Assert.That(connBefore.Count, Is.EqualTo(1));
                var oldConn = connBefore[0];
                var restarted = new Rig("p-guest",
                    new StubNetworkAccess(), storePath);
                try
                {
                    yield return ReconnectClient(host, restarted,
                        "p-guest", 2);

                    Assert.That(host.Session.ParticipantIdFor(oldConn),
                        Is.Null,
                        "the superseded connection must lose ownership");
                    // The fenced process is told the session closed for it.
                    yield return PumpUntil(() =>
                        client.Session.Snapshot().phase
                            == LanSessionService.PhaseRecovery,
                        client);
                    // ParticipantIdFor is keyed by the HOST-side conn id —
                    // after supersede exactly one mapped conn remains and
                    // it must own p-guest (and not be the fenced one).
                    var remaining = host.Session.HostConnections();
                    Assert.That(remaining.Count, Is.EqualTo(1),
                        "supersede must leave exactly one owned connection");
                    Assert.That(remaining[0], Is.Not.EqualTo(oldConn));
                    Assert.That(host.Session.ParticipantIdFor(remaining[0]),
                        Is.EqualTo("p-guest"));
                }
                finally
                {
                    restarted.Dispose();
                }
            }
            finally
            {
                host.Dispose();
                client.Dispose();
                if (Directory.Exists(storeDir))
                    Directory.Delete(storeDir, true);
            }
        }

        [UnityTest]
        public IEnumerator ClientCannotResumeHostPause()
        {
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess());
            try
            {
                yield return StartTwoPlayerRace(host, client);
                Assert.That(host.Session.Execute(Cmd("lan.pause.set",
                    P("paused", "true"))).status,
                    Is.EqualTo(CommandResult.Applied));
                // Publish runs inside HostTick — the wait must keep the
                // 30 Hz boundary moving for the paused fact to flow.
                yield return PumpUntil(() =>
                {
                    Tick(host, client, Neutral, Neutral, null, null);
                    return client.Bridge.LatestState != null
                        && client.Bridge.LatestState.paused;
                }, host, client);

                // The shared race clock freezes: host ticks, countdown and
                // elapsed race time all hold while wall time advances.
                var frozen = host.Bridge.LatestState.hostTick;
                for (var i = 0; i < 8; i++)
                {
                    Tick(host, client, Neutral, Neutral, null, null);
                    if (i % 4 == 0) yield return null;
                }
                Assert.That(host.Bridge.LatestState.hostTick,
                    Is.EqualTo(frozen),
                    "pause must freeze the shared race clock");

                // A client cannot set the shared pause state directly…
                var set = client.Session.Execute(Cmd("lan.pause.set",
                    P("paused", "false")));
                Assert.That(set.status, Is.EqualTo(CommandResult.Rejected));
                Assert.That(set.code, Is.EqualTo("hostOnly"));

                // …its pause toggle is only ever a request the host sees.
                var toggle = client.Session.Execute(Cmd("pause.toggle", null));
                Assert.That(toggle.status, Is.EqualTo(CommandResult.Accepted));
                yield return PumpUntil(() =>
                    host.Session.Snapshot().pauseRequests.Contains("p-guest"),
                    host, client);
                Assert.That(host.Session.Snapshot().paused, Is.True,
                    "a client request can never resume the host's pause");
                Assert.That(host.Bridge.LatestState.paused, Is.True);

                // Host decision resumes for everyone.
                Assert.That(host.Session.Execute(Cmd("lan.pause.set",
                    P("paused", "false"))).status,
                    Is.EqualTo(CommandResult.Applied));
                yield return PumpUntil(() =>
                {
                    Tick(host, client, Neutral, Neutral, null, null);
                    return client.Bridge.LatestState != null
                        && !client.Bridge.LatestState.paused;
                }, host, client);
                var resumed = host.Bridge.LatestState.hostTick;
                Tick(host, client, Neutral, Neutral, null, null);
                Assert.That(host.Bridge.LatestState.hostTick,
                    Is.GreaterThan(resumed),
                    "the shared clock runs again after the host resumes");
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator PauseCancelsInboundOneShots()
        {
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess());
            var clientEvents = new List<DrivingEvent>();
            try
            {
                yield return StartTwoPlayerRace(host, client);
                for (var i = 0; i < 10; i++)
                {
                    Tick(host, client, Neutral, Neutral, null, clientEvents);
                    if (i % 4 == 0) yield return null;
                }

                // A one-shot in flight when the shared pause lands must be
                // cancelled, never latched to fire after resume.
                client.Bridge.SubmitAction("pa-1");
                Assert.That(host.Session.Execute(Cmd("lan.pause.set",
                    P("paused", "true"))).status,
                    Is.EqualTo(CommandResult.Applied));
                yield return PumpUntil(() =>
                {
                    Tick(host, client, Neutral, Neutral, null, clientEvents);
                    return clientEvents.Exists(e =>
                        e.commandId == "pa-1"
                        && e.kind == "action.cancelled");
                }, host, client);
                var cancel = clientEvents.Find(e => e.commandId == "pa-1");
                Assert.That(cancel.detail, Is.EqualTo("paused"),
                    "pause-entry one-shot clears must be attributed to pause");
                yield return PumpUntil(() =>
                    client.Bridge.PendingOneShotCount == 0, client);
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator DisconnectDuringSharedPauseKeepsReservation()
        {
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess());
            try
            {
                yield return StartTwoPlayerRace(host, client);
                Assert.That(host.Session.Execute(Cmd("lan.pause.set",
                    P("paused", "true"))).status,
                    Is.EqualTo(CommandResult.Applied));
                yield return PumpUntil(() =>
                {
                    Tick(host, client, Neutral, Neutral, null, null);
                    return client.Bridge.LatestState != null
                        && client.Bridge.LatestState.paused;
                }, host, client);

                KillClientLink(host, client, "p-guest");
                var reserved = Find(host.Session.Snapshot(), "p-guest");
                Assert.That(reserved.connected, Is.False);
                Assert.That(reserved.controlSource, Is.EqualTo("ai"));

                // Rejoin while still paused: control returns but the race
                // clock stays frozen until the host resumes.
                yield return ReconnectClient(host, client, "p-guest", 2);
                for (var i = 0; i < 4; i++)
                    Tick(host, client, Neutral, Neutral, null, null);
                Assert.That(host.Bridge.LatestState.paused, Is.True);
                var back = Find(host.Session.Snapshot(), "p-guest");
                Assert.That(back.connected, Is.True);
                Assert.That(back.controlSource, Is.EqualTo("remote"));

                Assert.That(host.Session.Execute(Cmd("lan.pause.set",
                    P("paused", "false"))).status,
                    Is.EqualTo(CommandResult.Applied));
                var tickNow = host.Bridge.LatestState.hostTick;
                Tick(host, client, Neutral, Neutral, null, null);
                Assert.That(host.Bridge.LatestState.hostTick,
                    Is.GreaterThan(tickNow));
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator RepeatedReconnectsStayConsistent()
        {
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess());
            try
            {
                yield return StartTwoPlayerRace(host, client);
                for (var cycle = 1; cycle <= 2; cycle++)
                {
                    KillClientLink(host, client, "p-guest");
                    yield return ReconnectClient(host, client,
                        "p-guest", cycle + 1);
                    // The newest connection owns the racer: its inputs are
                    // consumed on real host boundaries and acked back.
                    // Read the HOST's lane ack — the client's latest
                    // snapshot can still be a pre-rejoin frame whose stale
                    // ack would satisfy the check spuriously.
                    yield return PumpUntil(() =>
                    {
                        Tick(host, client, Neutral, Neutral, null, null);
                        var ack = host.Bridge.AckFor("p-guest");
                        return ack != null && ack.inputSequence >= 1;
                    }, "ack cycle " + cycle, host, client);
                }
                var roster = host.Session.Snapshot();
                var claims = 0;
                foreach (var p in roster.participants)
                    if (p.participantId == "p-guest") claims++;
                Assert.That(claims, Is.EqualTo(1),
                    "repeated reconnects must never duplicate the slot");
                Assert.That(Find(roster, "p-guest").ownershipEpoch,
                    Is.EqualTo(3));
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        // A suspended host stops answering: the client's peer timeout
        // lands it in recovery — no migration, no inferred result — and a
        // credentialed rejoin after the host wakes reclaims the slot.
        [UnityTest]
        public IEnumerator SuspendedPausedHostTimesOutClientThenReconnects()
        {
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess());
            try
            {
                yield return StartTwoPlayerRace(host, client);
                Assert.That(host.Session.Execute(Cmd("lan.pause.set",
                    P("paused", "true"))).status,
                    Is.EqualTo(CommandResult.Applied));
                for (var i = 0; i < 4; i++)
                    Tick(host, client, Neutral, Neutral, null, null);

                // The host app is suspended: it stops pumping entirely
                // (backgrounded process) while the client keeps running.
                var clientDeadline = Time.unscaledTime
                    + LanTransport.PeerTimeoutMilliseconds / 1000f + 1.5f;
                while (Time.unscaledTime < clientDeadline
                    && client.Session.Snapshot().phase
                        != LanSessionService.PhaseRecovery)
                {
                    client.PumpFrame();
                    yield return null;
                }
                Assert.That(client.Session.Snapshot().phase,
                    Is.EqualTo(LanSessionService.PhaseRecovery),
                    "a silent host must drive the client to recovery");
                Assert.That(client.Session.Snapshot().lastError,
                    Is.Not.Empty);

                // The host process wakes and pumps again — the dead peer
                // resolves to a reserved AI slot on its side too.
                yield return PumpUntil(() =>
                {
                    var p = Find(host.Session.Snapshot(), "p-guest");
                    return p != null && !p.connected;
                }, host, client);
                Assert.That(Find(host.Session.Snapshot(), "p-guest")
                    .controlSource, Is.EqualTo("ai"));

                yield return ReconnectClient(host, client, "p-guest", 2);
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator HostLossNeverProducesCompletionReward()
        {
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess());
            var clientEvents = new List<DrivingEvent>();
            try
            {
                yield return StartTwoPlayerRace(host, client);
                for (var i = 0; i < 10; i++)
                {
                    Tick(host, client, Neutral, Neutral, null, clientEvents);
                    if (i % 4 == 0) yield return null;
                }
                Assert.That(client.Bridge.LatestState, Is.Not.Null);

                // Abrupt host kill: no session.closed is guaranteed — the
                // peer timeout carries the client to recovery either way.
                host.Dispose();
                yield return PumpUntil(() =>
                    client.Session.Snapshot().phase
                        == LanSessionService.PhaseRecovery,
                    client);

                var snap = client.Session.Snapshot();
                Assert.That(snap.phase,
                    Is.EqualTo(LanSessionService.PhaseRecovery),
                    "host loss must land in recovery — never in results");
                Assert.That(snap.lastError, Is.Not.Empty,
                    "recovery must carry a diagnosable reason");
                Assert.That(snap.descriptor.joinable, Is.False);
                client.PumpFrame();
                Assert.That(client.Bridge.LatestState, Is.Null,
                    "a dead host's race must not linger as an inferred result");
                Assert.That(clientEvents.Exists(
                    e => e.kind == "race.finalized"), Is.False,
                    "host loss must never mint a finalized result");
                Assert.That(clientEvents.Exists(
                    e => e.kind == "race.finished"), Is.False);
                Assert.That(client.Session.Execute(Cmd("lan.results.request",
                    P("raceId", "any"))).status,
                    Is.EqualTo(CommandResult.Rejected));
            }
            finally
            {
                client.Dispose();
            }
        }

        // --- private credential store ---------------------------------------

        [Test]
        public void CredentialStoreRoundTripsScopedToProfile()
        {
            var dir = Path.Combine(Path.GetTempPath(),
                "fr-credstore-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(dir, "lan-reconnect.json");
            try
            {
                var store = new ReconnectCredentialStore(path);
                store.Save(new ReconnectMaterial
                {
                    participantId = "p-guest",
                    sessionId = "lan-abc",
                    hostAddress = "192.168.7.7",
                    hostPort = 47777,
                    credential = "secret-1"
                });
                Assert.That(File.Exists(path), Is.True);

                // A new instance is a process restart: the material is
                // still there, scoped to the local profile that stored it.
                var reloaded = new ReconnectCredentialStore(path);
                ReconnectMaterial material;
                Assert.That(reloaded.TryLoad("p-guest", out material), Is.True);
                Assert.That(material.sessionId, Is.EqualTo("lan-abc"));
                Assert.That(material.hostAddress, Is.EqualTo("192.168.7.7"));
                Assert.That(material.hostPort, Is.EqualTo(47777));
                Assert.That(material.credential, Is.EqualTo("secret-1"));
                Assert.That(reloaded.TryLoad("p-other", out material),
                    Is.False, "credentials are scoped to the local profile");
                Assert.That(material, Is.Null);

                reloaded.Clear();
                Assert.That(File.Exists(path), Is.False);
                Assert.That(reloaded.TryLoad("p-guest", out material), Is.False);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Test]
        public void CredentialNeverEntersObservationState()
        {
            var dir = Path.Combine(Path.GetTempPath(),
                "fr-credleak-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(dir, "lan-reconnect.json");
            try
            {
                var store = new ReconnectCredentialStore(path);
                store.Save(new ReconnectMaterial
                {
                    participantId = "p-1",
                    sessionId = "lan-xyz",
                    hostAddress = "10.0.0.5",
                    hostPort = 40001,
                    credential = "credential-that-must-not-leak"
                });
                // The serialized observation snapshots never contain the
                // credential — it rides only the private join-accept wire.
                var link = new StubLink();
                var probe = new StubProbe();
                var session = new LanSessionService(link, probe, "gameplay-test");
                session.SetReconnectStore(store);
                session.SetLocalContext("p-1", "fox", 0, true, true);
                var state = session.Snapshot();
                var json = JsonUtility.ToJson(state);
                Assert.That(json.Contains("credential-that-must-not-leak"),
                    Is.False, "the credential must never enter emitted state");
                Assert.That(json.Contains("credential"), Is.False);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        private sealed class StubLink : ILanSessionLink
        {
            public bool StartHost(ushort port, out ushort boundPort,
                out string error)
            { boundPort = port; error = ""; return true; }
            public bool StartClient(string address, ushort port,
                out string error)
            { error = ""; return true; }
            public void Stop() { }
            public bool Send(ulong connectionId, string payload)
            { return true; }
        }

        private sealed class StubProbe : ILanDiscoveryProbe
        {
            public bool StartSearch(out string error)
            { error = ""; return true; }
            public void StopSearch() { }
            public bool StartResponder(ushort gamePort, string hostName,
                out string error)
            { error = ""; return true; }
            public void StopResponder() { }
            public IReadOnlyList<LanDiscoveredHost> Entries =>
                new List<LanDiscoveredHost>();
        }
    }
}
