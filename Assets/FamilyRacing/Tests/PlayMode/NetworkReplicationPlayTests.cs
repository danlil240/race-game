using System;
using System.Collections;
using System.Collections.Generic;
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
    // Task 5 replication over real NGO/UTP traffic: two full rigs (session
    // service + transport + link + replication bridge) exchange the actual
    // wire packets over 127.0.0.1 — no fake transport in the path. The
    // sequenced-unreliable channel carries input batches and snapshots;
    // the reliable-ordered channel carries session control and event
    // batches. Assertions pin host authority: only the host's simulation
    // mutates standings/results, stale or foreign traffic drops, and
    // transient events arrive exactly once.
    public sealed class NetworkReplicationPlayTests
    {
        private sealed class StubNetworkAccess : ILanNetworkAccess
        {
            public LanNetworkAccessState State =>
                LanNetworkAccessState.Available;
            public string Detail => "stub";
            public void EnsureAccess() { }
        }

        // A minimal physically consistent world: flat ground at y=0,
        // nothing to collide with — enough for the authoritative sim.
        private sealed class FlatWorld : IMotorWorld
        {
            public GroundHit ProbeGround(MotorPose pose, float probeDistance)
            {
                return new GroundHit
                {
                    hit = pose.y <= probeDistance,
                    x = pose.x,
                    y = 0f,
                    z = pose.z,
                    normalY = 1f,
                    distance = pose.y,
                    surfaceId = "ground"
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
        private const float TimeoutSeconds = 8f;

        [UnityTest]
        public IEnumerator FinalSnapshotRetriesAfterReliableQueueBackpressure()
        {
            var access = new StubNetworkAccess();
            var host = new Rig("p-host", access);
            var client = new Rig("p-guest", access);
            try
            {
                var content = Catalog();
                var course = content.Courses["course-lan"];
                course.isOpenRoute = true;
                course.checkpoints[1].z = 30;
                course.checkpoints[1].recoverZ = 25;
                content.Events["lan-mixed"].countdownTicks = 8;
                host.Session.SetContent(content);
                host.Bridge.SetContent(content);
                client.Session.SetContent(content);
                client.Bridge.SetContent(content);
                yield return StartTwoPlayerRace(host, client);
                // Stop pumping sockets while the genuine race runs to its
                // finish. Its normal snapshots fill the reliable send queue.
                for (int i = 0; i < 1000
                    && host.Bridge.LatestState.phase != NetworkRace.PhaseFinished; i++)
                    host.Bridge.StepTick(Neutral, World);
                Assert.That(host.Bridge.LatestState.phase, Is.EqualTo(NetworkRace.PhaseFinished));
                Assert.That(host.Bridge.LatestState.hostTick % 2, Is.EqualTo(1),
                    "Exercise the odd final boundary that has no regular publish cadence");
                Assert.That(host.Bridge.LastError, Does.Contain("snapshotSend"),
                    "Test must encounter real transport backpressure");
                yield return PumpUntil(() => {
                    host.Bridge.StepTick(Neutral, World);
                    return client.Bridge.LatestState != null
                        && client.Bridge.LatestState.phase == NetworkRace.PhaseFinished;
                }, host, client);
                Assert.That(client.Bridge.LatestState.result, Is.Not.Null);
            }
            finally { host.Dispose(); client.Dispose(); }
        }

        // One full peer: real UDP transport + real session protocol + the
        // Task-5 replication bridge, driven the same way FoundationApp
        // drives them.
        private sealed class Rig : IDisposable
        {
            public readonly LanTransport Transport;
            public readonly LanDiscovery Discovery;
            public readonly LanSessionLink Link;
            public readonly LanSessionService Session;
            public readonly NetworkRaceBridge Bridge;
            // Session wall time starts at zero and advances ~33 ms per
            // pumped frame — bounded budgets (5 s join/peer) stay honest.
            public double Now = 0.0;

            public Rig(string participantId, ILanNetworkAccess access)
            {
                Transport = new LanTransport(access);
                Discovery = new LanDiscovery(access);
                Link = new LanSessionLink(Transport, Discovery);
                Session = new LanSessionService(Link, Link, "gameplay-test");
                Link.Bind(Session);
                Session.SetLocalContext(participantId, "fox", 0, true, true);
                Session.SetContent(Catalog());
                Bridge = new NetworkRaceBridge(Transport, Session);
                Bridge.SetContent(Catalog());
            }

            // One frame boundary in production order: sockets into the
            // session first (callbacks land), wall clock, then the bridge
            // lifecycle pump.
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
                    // Recover lands mid-course: far enough from gate-1
                    // that a recovery pulse can never auto-finish a lap.
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
                new[] { course },
                new[] { mixed });
        }

        private static GameCommand Cmd(string action,
            Dictionary<string, string> parameters)
        {
            return new GameCommand(1, "lan-replication-play",
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
            var until = Time.unscaledTime + TimeoutSeconds;
            while (Time.unscaledTime < until && !done())
            {
                foreach (var rig in rigs) rig.PumpFrame();
                yield return null;
            }
            var diag = "";
            foreach (var rig in rigs)
            {
                if (rig == null) continue;
                var s = rig.Session.Snapshot();
                diag += " | " + (rig.Session.Role) + "/" + s.phase
                    + " occ=" + Occupied(s)
                    + " err=" + s.lastError
                    + " ops=" + s.pendingOperations.Count
                    + " tstate=" + rig.Transport.State
                    + " terr=" + rig.Transport.LastError
                    + " lastErr=" + rig.Bridge.LastError
                    + " lastInErr=" + rig.Bridge.LastInputError
                    + " drops=" + rig.Bridge.DroppedPackets
                    + " race=" + (rig.Bridge.LatestState != null
                        ? rig.Bridge.LatestState.phase : "none");
                var outcomes = rig.Session.DrainOutcomes();
                foreach (var o in outcomes)
                    diag += " out:" + o.action + "=" + o.status + "/" + o.code;
            }
            Assert.That(done(), Is.True,
                "Timed out waiting for LAN condition." + diag);
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

        // One simulated boundary on both rigs: frame pump (sockets,
        // lifecycle) then one 30 Hz StepTick (input publish / host sim +
        // 15 Hz snapshot publish). Drained authoritative events land in
        // the provided sink so exactly-once delivery is observable.
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

        // Hosts a lobby, joins the client over real UDP, marks everyone
        // ready and starts the race; returns once both bridges hold a
        // live authoritative race and the client has applied a snapshot.
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

            // Host bridge must create the coordinator on the first pump
            // after countdown begins; the client adopts it from the first
            // delivered snapshot.
            var spawned = false;
            yield return PumpUntil(() =>
            {
                host.PumpFrame();
                client.PumpFrame();
                host.Bridge.StepTick(Neutral, World);
                client.Bridge.StepTick(Neutral, null);
                spawned = host.Bridge.LatestState != null;
                return spawned && client.Bridge.LatestState != null;
            }, host, client);
            var hostState = host.Bridge.LatestState;
            var clientState = client.Bridge.LatestState;
            Assert.That(clientState.raceId, Is.EqualTo(hostState.raceId),
                "client must adopt the host race identity");
            Assert.That(clientState.sessionId, Is.EqualTo(hostState.sessionId));
        }

        [UnityTest]
        public IEnumerator HostClientRaceReplicatesAuthorityFieldsOverRealUdp()
        {
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess());
            var hostEvents = new List<DrivingEvent>();
            var clientEvents = new List<DrivingEvent>();
            try
            {
                yield return StartTwoPlayerRace(host, client);

                // Two one-shot actions land on the same host tick — the
                // reliable batch must deliver both, exactly once each.
                host.Bridge.SubmitAction("act-h1");
                host.Bridge.SubmitAction("act-h2");
                client.Bridge.SubmitAction("act-c1");

                // 30 ticks ≈ one second of sim: countdown ends, real
                // snapshots flow at 15 Hz and inputs flow back.
                for (var i = 0; i < 30; i++)
                {
                    Tick(host, client, Neutral, Neutral,
                        hostEvents, clientEvents);
                    if (i % 4 == 0) yield return null;
                }
                yield return PumpUntil(() => clientEvents.Count >= 3
                    && client.Bridge.LatestState != null
                    && client.Bridge.LatestState.phase
                        == NetworkRace.PhaseRunning,
                    host, client);

                var hostState = host.Bridge.LatestState;
                var clientState = client.Bridge.LatestState;
                Assert.That(clientState.raceId, Is.EqualTo(hostState.raceId));
                Assert.That(clientState.phase, Is.EqualTo(hostState.phase),
                    "authority phase must agree after delivery");
                Assert.That(clientState.paused, Is.EqualTo(hostState.paused));
                Assert.That(clientState.hostTick,
                    Is.LessThanOrEqualTo(hostState.hostTick));
                Assert.That(hostState.hostTick - clientState.hostTick,
                    Is.LessThanOrEqualTo(20),
                    "client must not fall more than ~2/3 s behind");
                CollectionAssert.AreEqual(hostState.standings,
                    clientState.standings);
                var racerDiag = "";
                foreach (var r in clientState.racers)
                    racerDiag += " " + r.participantId + ":fin=" + r.finished
                        + ",dnf=" + r.dnf + ",lap="
                        + (r.session != null ? r.session.lap : -1)
                        + ",tick=" + hostState.hostTick;
                Assert.That(clientState.result, Is.Null,
                    "race unexpectedly finalized:" + racerDiag);
                for (var slot = 0;
                     slot < hostState.racers.Count; slot++)
                {
                    Assert.That(clientState.racers[slot].participantId,
                        Is.EqualTo(hostState.racers[slot].participantId));
                    Assert.That(clientState.racers[slot].finished,
                        Is.EqualTo(hostState.racers[slot].finished));
                }

                // The client consumed-input ack proves its samples crossed
                // the wire, validated, and fed real host boundaries.
                var ack = client.Bridge.AckFor("p-guest");
                Assert.That(ack, Is.Not.Null);
                Assert.That(ack.inputSequence, Is.GreaterThan(0),
                    "client inputs were never consumed on the host");
                Assert.That(ack.hostTick, Is.GreaterThan(0));

                // Exactly-once, stable-identity event delivery — including
                // the same-tick pair queued above.
                var seenIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var ev in clientEvents)
                {
                    Assert.That(seenIds.Add(ev.id), Is.True,
                        "event redelivered: " + ev.id + " (" + ev.kind + ")");
                }
                var queuedKinds = clientEvents.FindAll(
                    e => e.kind == "action.queued");
                Assert.That(queuedKinds.Count, Is.GreaterThanOrEqualTo(3),
                    "same-tick one-shot events must both arrive");
                var h1 = clientEvents.Find(e => e.commandId == "act-h1");
                var h2 = clientEvents.Find(e => e.commandId == "act-h2");
                Assert.That(h1, Is.Not.Null, "host one-shot never delivered");
                Assert.That(h2, Is.Not.Null, "host one-shot never delivered");
                Assert.That(h1.tick, Is.EqualTo(h2.tick),
                    "act-h1/act-h2 were queued on one host tick");
                Assert.That(clientEvents.Find(e => e.commandId == "act-c1"),
                    Is.Not.Null, "client one-shot never acknowledged");
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ReorderedSnapshotsDropAndLostSnapshotsAreTolerated()
        {
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess());
            try
            {
                yield return StartTwoPlayerRace(host, client);
                for (var i = 0; i < 12; i++)
                {
                    Tick(host, client, Neutral, Neutral, null, null);
                    if (i % 4 == 0) yield return null;
                }
                var latest = client.Bridge.CurrentSnapshot;
                Assert.That(latest, Is.Not.Null);
                var latestSeq = latest.snapshotSequence;
                var latestTick = client.Bridge.LatestState.hostTick;
                var clientPeer = host.Session.HostConnections()[0];

                // A reordered datagram (an already-seen sequence) must
                // drop without rolling the client back. The forged body
                // carries a sentinel tick so adoption would be obvious;
                // real in-flight snapshots may still legitimately advance
                // the client meanwhile.
                var stale = new NetworkSnapshot
                {
                    sessionId = latest.sessionId,
                    raceId = latest.raceId,
                    raceOrdinal = latest.raceOrdinal,
                    snapshotSequence = latestSeq - 1,
                    state = latest.state.Copy()
                };
                stale.state.hostTick = -111;
                byte[] packet;
                string error;
                Assert.That(NetworkWireCodec.TryEncodeSnapshot(stale,
                    out packet, out error), Is.True, error);
                Assert.That(host.Transport.SendGameplay(clientPeer, packet),
                    Is.True);
                yield return PumpUntil(() => client.Bridge.StaleSnapshots > 0,
                    host, client);
                Assert.That(client.Bridge.LatestState.hostTick,
                    Is.GreaterThanOrEqualTo(latestTick)
                        .And.Not.EqualTo(-111),
                    "a reordered snapshot must never roll the client back");

                // A packet from a previous race identity drops too.
                var staleRace = new NetworkSnapshot
                {
                    sessionId = latest.sessionId,
                    raceId = latest.sessionId + "-r0",
                    raceOrdinal = latest.raceOrdinal - 1,
                    snapshotSequence = latestSeq + 1,
                    state = latest.state.Copy()
                };
                staleRace.state.hostTick = -222;
                Assert.That(NetworkWireCodec.TryEncodeSnapshot(staleRace,
                    out packet, out error), Is.True, error);
                Assert.That(host.Transport.SendGameplay(clientPeer, packet),
                    Is.True);
                var staleBefore = client.Bridge.StaleSnapshots;
                yield return PumpUntil(() =>
                    client.Bridge.StaleSnapshots > staleBefore, host, client);
                Assert.That(client.Bridge.LatestState.hostTick,
                    Is.Not.EqualTo(-222));

                // Lost datagrams are tolerated: a snapshot arriving with a
                // sequence gap is accepted — the newest wins, missing
                // intermediates are simply skipped.
                var gapped = new NetworkSnapshot
                {
                    sessionId = latest.sessionId,
                    raceId = latest.raceId,
                    raceOrdinal = latest.raceOrdinal,
                    snapshotSequence = latestSeq + 40,
                    state = latest.state.Copy()
                };
                gapped.state.hostTick = 424242; // sentinel proving adoption
                Assert.That(NetworkWireCodec.TryEncodeSnapshot(gapped,
                    out packet, out error), Is.True, error);
                Assert.That(host.Transport.SendGameplay(clientPeer, packet),
                    Is.True);
                yield return PumpUntil(() =>
                    client.Bridge.CurrentSnapshot != null
                    && client.Bridge.CurrentSnapshot.snapshotSequence
                        == latestSeq + 40,
                    host, client);
                Assert.That(client.Bridge.LatestState.hostTick,
                    Is.EqualTo(424242));
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        // The wire vocabulary a client can emit is only P1IB input
        // batches: there is no client->host message type that can carry
        // progress, finish state, standings, or rewards. Forged control
        // payloads must be dropped before they can touch authoritative
        // state, and a foreign participant claim must fail ownership.
        [UnityTest]
        public IEnumerator ClientCannotSubmitFinishOrCoins()
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
                var baseline = host.Bridge.LatestState;
                Assert.That(baseline, Is.Not.Null);
                Assert.That(baseline.result, Is.Null);
                var baselineTick = baseline.hostTick;
                var dropsBefore = host.Bridge.DroppedPackets;
                var rejectsBefore = host.Bridge.InputRejections;

                // Forged snapshot claiming a finished race with a coin
                // award — sent upstream where only input belongs.
                var forged = new NetworkSnapshot
                {
                    sessionId = baseline.sessionId,
                    raceId = baseline.raceId,
                    raceOrdinal = 1,
                    snapshotSequence = 999,
                    state = baseline.Copy()
                };
                forged.state.phase = NetworkRace.PhaseFinished;
                forged.state.result = new RaceResult
                {
                    raceId = baseline.raceId,
                    completionCoins = 9999
                };
                byte[] packet;
                string error;
                Assert.That(NetworkWireCodec.TryEncodeSnapshot(forged,
                    out packet, out error), Is.True, error);
                Assert.That(client.Transport.SendGameplay(packet), Is.True,
                    "forged snapshot must still send at the socket layer");
                // The same forged payload on the reliable channel must
                // equally fail to reach authority.
                Assert.That(client.Transport.Send(packet), Is.True);

                // Forged authoritative event batch claiming a finish.
                var forgedEvents = new NetworkEventBatch
                {
                    sessionId = baseline.sessionId,
                    raceId = baseline.raceId,
                    raceOrdinal = 1,
                    batchSequence = 1
                };
                forgedEvents.events.Add(new DrivingEvent
                {
                    id = baseline.raceId + "-forged",
                    kind = "race.finished",
                    detail = "coins=9999"
                });
                Assert.That(NetworkWireCodec.TryEncodeEventBatch(forgedEvents,
                    out packet, out error), Is.True, error);
                Assert.That(client.Transport.SendGameplay(packet), Is.True);
                Assert.That(client.Transport.Send(packet), Is.True);

                // An input batch that claims the host's own participant —
                // foreign ownership must be rejected even though the
                // session/race identity is genuine.
                var foreign = new List<NetworkInputFrame>
                {
                    new NetworkInputFrame
                    {
                        sessionId = baseline.sessionId,
                        raceId = baseline.raceId,
                        participantId = "p-host",
                        ownershipEpoch = 1,
                        inputSequence = 1,
                        clientTick = 1,
                        steering = 1f
                    }
                };
                Assert.That(NetworkWireCodec.TryEncodeInputBatch(foreign,
                    out packet, out error), Is.True, error);
                Assert.That(client.Transport.SendGameplay(packet), Is.True);

                yield return PumpUntil(() =>
                    host.Bridge.DroppedPackets > dropsBefore
                    && host.Bridge.InputRejections > rejectsBefore,
                    host, client);

                // The host's authoritative state is untouched: race still
                // live, no result, no coins, standings intact, and no
                // forged event ever surfaced.
                var after = host.Bridge.LatestState;
                Assert.That(after.phase,
                    Is.Not.EqualTo(NetworkRace.PhaseFinished),
                    "client traffic must never finalize the race");
                Assert.That(after.result, Is.Null,
                    "client-supplied results/coins must never apply");
                CollectionAssert.AreEqual(baseline.standings,
                    after.standings);
                foreach (var racer in after.racers)
                {
                    Assert.That(racer.finished, Is.False,
                        racer.participantId + " finished via client traffic");
                    Assert.That(racer.dnf, Is.False);
                }
                var hostDrained = host.Bridge.DrainRaceEvents();
                foreach (var ev in hostDrained)
                    Assert.That(ev.id, Does.Not.Contain("forged"));
                Assert.That(host.Session.Snapshot().phase,
                    Is.Not.EqualTo(LanSessionService.PhaseResults));
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        // Validation applies at every local logical boundary regardless of
        // the 15 Hz snapshot cadence: held steering keeps steering for
        // under InputStaleTicks silent ticks, then neutralizes — observed
        // through the delivered authoritative snapshots.
        [UnityTest]
        public IEnumerator SilentClientInputNeutralizesAtHostBoundaries()
        {
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess());
            try
            {
                yield return StartTwoPlayerRace(host, client);
                var steer = new DriverInput(1f, false, false, false,
                    false, false, false);
                // Drive the client's sample into the host buffer and let
                // the host consume it.
                for (var i = 0; i < 6; i++)
                {
                    Tick(host, client, Neutral, steer, null, null);
                    if (i % 4 == 0) yield return null;
                }
                yield return PumpUntil(() =>
                {
                    var ack = client.Bridge.AckFor("p-guest");
                    return ack != null && ack.inputSequence > 0;
                }, host, client);

                // Now the client goes silent: no StepTick means no input
                // samples at all — the host must neutralize the held
                // steering after exactly InputStaleTicks silent ticks.
                for (var i = 0; i < NetworkSettings.InputStaleTicks + 6; i++)
                {
                    host.PumpFrame();
                    client.PumpFrame();
                    host.Bridge.StepTick(Neutral, World);
                    if (i % 4 == 0) yield return null;
                }
                yield return PumpUntil(() =>
                {
                    var state = client.Bridge.LatestState;
                    if (state == null) return false;
                    foreach (var racer in state.racers)
                        if (racer.participantId == "p-guest"
                            && racer.session != null
                            && racer.session.input != null)
                            return Mathf.Approximately(
                                racer.session.input.steering, 0f);
                    return false;
                }, host, client);
                var state = client.Bridge.LatestState;
                foreach (var racer in state.racers)
                {
                    if (racer.participantId != "p-guest") continue;
                    Assert.That(racer.session.input.steering,
                        Is.EqualTo(0f).Within(0.0001f),
                        "held steering must clear after "
                        + NetworkSettings.InputStaleTicks + " silent ticks");
                    Assert.That(racer.session.input.drift, Is.False);
                }
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        // The negative leg of the one-shot contract — "acknowledge/retry
        // until applied or cancelled". Six ids in one burst exceed the
        // session pending queue (capacity 4): the overflow ids must come
        // back as explicit action.cancelled events, and the client must
        // retire them instead of retrying forever.
        [UnityTest]
        public IEnumerator DeclinedOneShotEmitsCancelledAndClientStopsRetrying()
        {
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess());
            var hostEvents = new List<DrivingEvent>();
            var clientEvents = new List<DrivingEvent>();
            try
            {
                yield return StartTwoPlayerRace(host, client);
                var ids = new[]
                    { "cx-1", "cx-2", "cx-3", "cx-4", "cx-5", "cx-6" };
                foreach (var id in ids) client.Bridge.SubmitAction(id);
                for (var i = 0; i < 20; i++)
                {
                    Tick(host, client, Neutral, Neutral,
                        hostEvents, clientEvents);
                    if (i % 4 == 0) yield return null;
                }
                // Every submitted id resolves: latched ones produce
                // action.queued, the two past the session's pending cap
                // produce the explicit cancel leg on the same reliable
                // channel. The predicate drains because PumpUntil only
                // pumps frames — StepTick boundaries already ran.
                yield return PumpUntil(() =>
                {
                    clientEvents.AddRange(client.Bridge.DrainRaceEvents());
                    return client.Bridge.PendingOneShotCount == 0
                        && clientEvents.Exists(
                            e => e.kind == "action.cancelled");
                }, host, client);
                var cancelled = clientEvents.FindAll(
                    e => e.kind == "action.cancelled");
                Assert.That(cancelled.Count, Is.GreaterThanOrEqualTo(2),
                    "overflow ids must be cancelled, not dropped");
                var resolved = new HashSet<string>(StringComparer.Ordinal);
                foreach (var ev in clientEvents)
                    if (ev.commandId != null
                        && ev.commandId.StartsWith("cx-",
                            StringComparison.Ordinal))
                        resolved.Add(ev.commandId);
                foreach (var id in ids)
                    Assert.That(resolved.Contains(id), Is.True,
                        "submitted id never got a terminal event: " + id);
                // The first four drain in submission order and latch; the
                // last two overflow the pending queue and are cancelled.
                Assert.That(clientEvents.Exists(e =>
                    e.kind == "action.cancelled" && e.commandId == "cx-5"),
                    Is.True, "cx-5 must be cancelled");
                Assert.That(clientEvents.Exists(e =>
                    e.kind == "action.cancelled" && e.commandId == "cx-6"),
                    Is.True, "cx-6 must be cancelled");
                // Cancel events carry the racer's identity so consumers
                // can attribute the rejection.
                foreach (var ev in cancelled)
                    if (ev.commandId.StartsWith("cx-",
                        StringComparison.Ordinal))
                        Assert.That(ev.participantId,
                            Is.EqualTo("p-guest"));
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        // Regression: a pending one-shot backlog deeper than
        // MaxFrameActionIds must window onto outbound frames — the oldest
        // bound's worth per frame — instead of copying the whole list
        // into every frame. Before windowing, a >MaxFrameActionIds
        // backlog made every frame trip the host's tooManyActions
        // rejection: the client's whole input stream was refused, the
        // backlog could never be acknowledged, and the deadlock was
        // permanent. Submit MaxFrameActionIds + 4 ids; the stream must
        // stay valid (never tooManyActions) and the backlog must drain
        // to zero across multiple frames with a terminal event per id.
        [UnityTest]
        public IEnumerator OversizedOneShotBacklogWindowsAndDrains()
        {
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess());
            var clientEvents = new List<DrivingEvent>();
            try
            {
                yield return StartTwoPlayerRace(host, client);
                var ids = new List<string>();
                for (var i = 0; i < NetworkSettings.MaxFrameActionIds + 4;
                     i++)
                    ids.Add("win-" + i);
                foreach (var id in ids) client.Bridge.SubmitAction(id);
                for (var i = 0; i < 30; i++)
                {
                    Tick(host, client, Neutral, Neutral, null, clientEvents);
                    if (i % 4 == 0) yield return null;
                }
                // The backlog must drain entirely: acknowledged ids slide
                // out of the send window so the next ids reach the host
                // on later frames — it must not wedge at 8+.
                yield return PumpUntil(() =>
                {
                    clientEvents.AddRange(client.Bridge.DrainRaceEvents());
                    return client.Bridge.PendingOneShotCount == 0;
                }, host, client);
                // No frame ever carried more than MaxFrameActionIds —
                // the host's most recent validation error would be
                // pinned to tooManyActions (it recurs every frame) if
                // any oversized list had reached the wire.
                Assert.That(host.Bridge.LastInputError,
                    Is.Not.EqualTo("tooManyActions"),
                    "an oversized actionIds list must never reach the wire");
                // Every submitted id resolves to a terminal
                // acknowledgment — applied or explicitly cancelled —
                // and the client retires it rather than retrying forever.
                var resolved = new HashSet<string>(StringComparer.Ordinal);
                foreach (var ev in clientEvents)
                    if (ev.commandId != null
                        && ev.commandId.StartsWith("win-",
                            StringComparison.Ordinal)
                        && (ev.kind == "action.queued"
                            || ev.kind == "action.consumed"
                            || ev.kind == "action.cancelled"))
                        resolved.Add(ev.commandId);
                foreach (var id in ids)
                    Assert.That(resolved.Contains(id), Is.True,
                        "one-shot id never acknowledged: " + id);
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }
    }
}
