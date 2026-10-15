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
    // M5 Task 5 gadget authority over real NGO/UTP traffic: two full
    // rigs exchange actual wire packets over 127.0.0.1. Assertions pin
    // that ONLY the host grants items — the client's gadget press rides
    // its input stream as a pending request until the host's snapshot
    // lands; the decoded gadget world validates atomically; and a
    // credentialed rejoin restores the same authoritative gadget state.
    public sealed class GadgetNetworkPlayTests
    {
        private sealed class StubNetworkAccess : ILanNetworkAccess
        {
            public LanNetworkAccessState State =>
                LanNetworkAccessState.Available;
            public string Detail => "stub";
            public void EnsureAccess() { }
        }

        // Flat world: ground at y=0, no obstacles — enough for the
        // authoritative sim to drive racers straight forward.
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
                // M5 Task 5: the authored gadget catalog seeds the host's
                // authoritative world at race creation; the client sets
                // it too but never creates the race — snapshots own its
                // gadget truth.
                Bridge.SetGadgetContent(GadgetCatalog());
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

        // The pinned authored defaults — one of each kind so the seeded
        // draw bag cycles turbo -> shield -> foam deterministically.
        private static GadgetDefinitionCatalog GadgetCatalog()
        {
            var catalog = new GadgetDefinitionCatalog
            {
                pickupRespawnTicks = 300,
                heldSlotCapacity = 1
            };
            catalog.gadgets.Add(new GadgetDefinition
            {
                id = "turbo-01", kind = GadgetKind.Turbo,
                durationTicks = 60, accelerationMultiplier = 1.25f,
                speedCapMultiplier = 1.10f
            });
            catalog.gadgets.Add(new GadgetDefinition
            {
                id = "shield-01", kind = GadgetKind.BubbleShield,
                charges = 1
            });
            catalog.gadgets.Add(new GadgetDefinition
            {
                id = "foam-01", kind = GadgetKind.FoamTrap,
                patchRadiusMeters = 3f, patchLifetimeTicks = 180,
                slowMultiplier = 0.70f, slowDurationTicks = 45,
                immunityTicks = 60
            });
            string error;
            Assert.IsTrue(catalog.Validate(out error), error);
            return catalog;
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
                },
                // One pickup column dead ahead of slot 0 (the host's
                // lane): auto-acceleration carries the host through it.
                pickupSites = new List<GadgetPickupSite>
                {
                    new GadgetPickupSite
                    {
                        id = "lane-zero", x = -6f, y = 0f, z = 40f,
                        radiusMeters = 3f, verticalRangeMeters = 2.5f
                    }
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
            return new GameCommand(1, "lan-gadget-play",
                Guid.NewGuid().ToString("N"), "local", action, parameters);
        }

        private static Dictionary<string, string> P(
            params string[] keyValues)
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i + 1 < keyValues.Length; i += 2)
                map[keyValues[i]] = keyValues[i + 1];
            return map;
        }

        private static IEnumerator PumpUntil(Func<bool> done,
            string what, params Rig[] rigs)
        {
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
                diag += " | " + rig.Session.Role + "/" + s.phase
                    + " err=" + s.lastError
                    + " lastErr=" + rig.Bridge.LastError
                    + " drops=" + rig.Bridge.DroppedPackets
                    + " race=" + (rig.Bridge.LatestState != null
                        ? rig.Bridge.LatestState.phase : "none")
                    + " gadgets=" + (rig.Bridge.LatestState != null
                        && rig.Bridge.LatestState.gadgets != null
                        ? rig.Bridge.LatestState.gadgets.pickups.Count
                            .ToString() + "p" : "none");
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
                "lobby", host, client);

            Assert.That(host.Session.Execute(Cmd("lan.configure",
                P("eventId", "lan-mixed", "classId", "car"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(host.Session.Execute(Cmd("lan.ready",
                P("ready", "true"))).status,
                Is.EqualTo(CommandResult.Applied));
            var ready = client.Session.Execute(Cmd("lan.ready",
                P("ready", "true")));
            Assert.That(ready.status,
                Is.Not.EqualTo(CommandResult.Rejected),
                "client ready rejected: " + ready.code);
            yield return PumpUntil(() =>
                Find(host.Session.Snapshot(), "p-guest")?.ready == true,
                "ready", host, client);

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
            }, "race spawn", host, client);
            Assert.That(client.Bridge.LatestState.raceId,
                Is.EqualTo(host.Bridge.LatestState.raceId));
        }

        private static GadgetRacerState GadgetRow(
            NetworkRaceState state, string participantId)
        {
            return state != null && state.gadgets != null
                ? state.gadgets.Racer(participantId) : null;
        }

        // Tick both rigs until done() or the pump budget expires —
        // sim-boundary granularity for assertions that only hold once
        // snapshots have crossed the wire.
        private static IEnumerator TickUntil(Func<bool> done, string what,
            Rig host, Rig client, DriverInput hostInput,
            List<DrivingEvent> hostEvents, List<DrivingEvent> clientEvents,
            int maxTicks = 600)
        {
            var deadline = DateTime.UtcNow.AddSeconds(TimeoutSeconds);
            var ticks = 0;
            while (DateTime.UtcNow < deadline && ticks < maxTicks && !done())
            {
                Tick(host, client, hostInput, Neutral, hostEvents,
                    clientEvents);
                ticks++;
                if (ticks % 4 == 0) yield return null;
            }
            Assert.That(done(), Is.True,
                "Timed out waiting for sim condition (" + what
                + ", ticks=" + ticks + ")");
        }

        [UnityTest]
        public IEnumerator GadgetWorldReplicatesAndHostGrantsCollection()
        {
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess());
            try
            {
                yield return StartTwoPlayerRace(host, client);

                // The authoritative gadget world rides the snapshot:
                // the client never fabricates one.
                yield return PumpUntil(() =>
                    client.Bridge.LatestState != null
                    && client.Bridge.LatestState.gadgets != null
                    && client.Bridge.LatestState.gadgets.pickups.Count == 1,
                    "gadget world replication", host, client);
                Assert.That(
                    client.Bridge.LatestState.gadgets.pickups[0].siteId,
                    Is.EqualTo("lane-zero"));

                // Host's auto-accelerating racer sweeps the site column:
                // the host grants, the client's next snapshots carry the
                // grant — presentation-side truth only.
                yield return TickUntil(() =>
                {
                    var row = GadgetRow(client.Bridge.LatestState, "p-host");
                    return row != null
                        && !string.IsNullOrEmpty(row.heldItemId)
                        && !client.Bridge.LatestState.gadgets
                            .pickups[0].active;
                }, "host collection", host, client, Neutral, null, null);

                var granted = GadgetRow(client.Bridge.LatestState, "p-host")
                    .heldItemId;
                Assert.That(
                    GadgetRow(host.Bridge.LatestState, "p-host").heldItemId,
                    Is.EqualTo(granted),
                    "host and client observe the same authoritative grant");
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ClientGadgetPressIsPendingUntilHostApplies()
        {
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess());
            var clientEvents = new List<DrivingEvent>();
            try
            {
                yield return StartTwoPlayerRace(host, client);

                // The client's gadget press rides its input frames as a
                // pending request. With an empty slot the host grants
                // nothing — clients cannot mint items.
                var press = new DriverInput(0f, false, false, true,
                    false, false, false);
                for (var i = 0; i < 30; i++)
                {
                    Tick(host, client, Neutral, press, null, clientEvents);
                    if (i % 4 == 0) yield return null;
                }
                var guestRow = GadgetRow(client.Bridge.LatestState, "p-guest");
                Assert.That(guestRow == null
                    || string.IsNullOrEmpty(guestRow.heldItemId),
                    "a client press cannot mint an item — only the host "
                    + "grants, and only through a pickup");

                // Host collects then uses: the use fact publishes to the
                // client with human attribution and consumes the slot
                // on both ends.
                yield return TickUntil(() =>
                {
                    var row = GadgetRow(client.Bridge.LatestState, "p-host");
                    return row != null
                        && !string.IsNullOrEmpty(row.heldItemId);
                }, "host collection", host, client, Neutral, null,
                    clientEvents);

                // One press edge on the host seat: the use fact and the
                // consumed slot must both land on the client.
                var hostPress = new DriverInput(0f, false, false, true,
                    false, false, false);
                Tick(host, client, hostPress, Neutral, null,
                    clientEvents);
                yield return TickUntil(() =>
                {
                    foreach (var ev in clientEvents)
                        if (ev.kind == "gadget.used"
                            && ev.participantId == "p-host")
                            return true;
                    return false;
                }, "gadget.used replication", host, client, Neutral,
                    null, clientEvents);
                var used = false;
                foreach (var ev in clientEvents)
                    if (ev.kind == "gadget.used"
                        && ev.participantId == "p-host")
                    {
                        Assert.That(ev.attribution, Is.EqualTo("human"),
                            "the host's own seat is a human fact");
                        used = true;
                    }
                Assert.That(used);
                yield return TickUntil(() =>
                {
                    var row = GadgetRow(client.Bridge.LatestState, "p-host");
                    return row != null
                        && string.IsNullOrEmpty(row.heldItemId);
                }, "slot consumed", host, client, Neutral, null,
                    clientEvents);
            }
            finally
            {
                host.Dispose();
                client.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator RejoinRestoresTheAuthoritativeGadgetWorld()
        {
            var storePath = Path.Combine(Path.GetTempPath(),
                "fr-gadget-rejoin-" + Guid.NewGuid().ToString("N"),
                "lan-reconnect.json");
            var host = new Rig("p-host", new StubNetworkAccess());
            var client = new Rig("p-guest", new StubNetworkAccess(),
                storePath);
            try
            {
                yield return StartTwoPlayerRace(host, client);
                yield return TickUntil(() =>
                {
                    var row = GadgetRow(client.Bridge.LatestState, "p-host");
                    return row != null
                        && !string.IsNullOrEmpty(row.heldItemId);
                }, "host collection", host, client, Neutral, null, null);
                var heldBefore = GadgetRow(client.Bridge.LatestState,
                    "p-host").heldItemId;
                var tickBefore = client.Bridge.LatestState.gadgets.tick;

                // Client process dies mid-race; the host reserves the
                // slot under AI control.
                client.Transport.Stop();
                client.Session.OnLinkDisconnected(
                    client.ServerConnectionId, "suspended");
                host.Session.OnDisconnected("p-guest", "peerTimeout");

                var rejoin = client.Session.Execute(
                    Cmd("lan.reconnect", null));
                Assert.That(rejoin.status,
                    Is.EqualTo(CommandResult.Accepted),
                    "lan.reconnect rejected: " + rejoin.code);
                yield return PumpUntil(() =>
                {
                    var p = Find(host.Session.Snapshot(), "p-guest");
                    return p != null && p.connected
                        && p.controlSource == "remote"
                        && p.ownershipEpoch == 2
                        && (client.Session.Snapshot().phase
                                == LanSessionService.PhaseRacing
                            || client.Session.Snapshot().phase
                                == LanSessionService.PhaseCountdown);
                }, "rejoin", host, client);

                // After the reclaim the gadget world streams back with
                // the same rows — the held item and the clock carry
                // across the disconnect.
                yield return TickUntil(() =>
                {
                    var row = GadgetRow(client.Bridge.LatestState,
                        "p-host");
                    return client.Bridge.LatestState != null
                        && client.Bridge.LatestState.gadgets != null
                        && row != null;
                }, "gadget world after rejoin", host, client, Neutral,
                    null, null);
                var rowAfter = GadgetRow(client.Bridge.LatestState,
                    "p-host");
                Assert.That(rowAfter.heldItemId,
                    Is.EqualTo(heldBefore),
                    "the authoritative grant survives the rejoin");
                Assert.That(client.Bridge.LatestState.gadgets.tick,
                    Is.GreaterThanOrEqualTo(tickBefore));
            }
            finally
            {
                host.Dispose();
                client.Dispose();
                try
                {
                    var dir = Path.GetDirectoryName(storePath);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                }
                catch (Exception) { }
            }
        }

        // --- wire-level gadget validation over the real codec ---------

        private static NetworkSnapshot GadgetSnapshot(
            Action<GadgetWorldState> corrupt)
        {
            var world = GadgetRules.Create(GadgetCatalog(),
                new[]
                {
                    new GadgetPickupSite
                    {
                        id = "lane-zero", x = -6f, y = 0f, z = 40f
                    }
                },
                0xC0FFEEu,
                new List<string> { "p-host", "p-guest" });
            if (corrupt != null) corrupt(world);
            return new NetworkSnapshot
            {
                sessionId = "s",
                raceId = "r",
                raceOrdinal = 1,
                snapshotSequence = 1,
                state = new NetworkRaceState
                {
                    sessionId = "s",
                    raceId = "r",
                    gadgets = world
                }
            };
        }

        private static bool RoundTrips(NetworkSnapshot snapshot,
            out string error)
        {
            byte[] packet;
            string encodeError;
            if (!NetworkWireCodec.TryEncodeSnapshot(snapshot, out packet,
                    out encodeError))
            {
                error = "encode:" + encodeError;
                return false;
            }
            NetworkSnapshot decoded;
            return NetworkWireCodec.TryDecodeSnapshot(packet, out decoded,
                out error);
        }

        [Test]
        public void ValidGadgetWorldRoundTripsTheWire()
        {
            string error;
            Assert.That(RoundTrips(GadgetSnapshot(null), out error),
                Is.True, error);
        }

        [Test]
        public void DuplicatePickupIdsRejectTheWholeSnapshot()
        {
            string error;
            var rejected = !RoundTrips(GadgetSnapshot(world =>
            {
                world.pickups.Add(new GadgetPickupState
                {
                    siteId = "lane-zero", x = 1f, z = 1f,
                    radiusMeters = 2f
                });
            }), out error);
            Assert.That(rejected,
                "duplicate site ids must kill the snapshot, not merge");
            Assert.That(error, Is.EqualTo("gadgetPickup.duplicate"));
        }

        [Test]
        public void OverCapacityGadgetListRejectsTheWholeSnapshot()
        {
            string error;
            var rejected = !RoundTrips(GadgetSnapshot(world =>
            {
                for (var i = 0;
                    i <= NetworkSettings.GadgetPatchCapacity; i++)
                    world.patches.Add(new GadgetPatchState
                    {
                        patchId = "p" + i, ownerParticipantId = "p-host",
                        x = i, radiusMeters = 3f, ticksRemaining = 60,
                        slowMultiplier = 0.7f
                    });
            }), out error);
            Assert.That(rejected,
                "an over-capacity patch list never applies partially");
            Assert.That(error, Is.EqualTo("gadgetCapacity"));
        }

        [Test]
        public void StructuralGadgetCorruptionRejectsTheWholeSnapshot()
        {
            // "gadgets":null on the wire is an explicit-null body — the
            // codec's structural pass fences it as malformed rather than
            // decoding it into a phantom empty world.
            var json = "{\"state\":{\"racers\":[],\"gadgets\":null}}";
            var body = System.Text.Encoding.UTF8.GetBytes(json);
            var packet = new byte[NetworkWireCodec.HeaderBytes
                + body.Length];
            Buffer.BlockCopy(
                System.Text.Encoding.ASCII.GetBytes(
                    NetworkWireCodec.TagSnapshot), 0, packet, 0, 4);
            Buffer.BlockCopy(body, 0, packet,
                NetworkWireCodec.HeaderBytes, body.Length);
            NetworkSnapshot decoded;
            string error;
            Assert.That(NetworkWireCodec.TryDecodeSnapshot(packet,
                out decoded, out error), Is.False);
            Assert.That(decoded, Is.Null);
        }
    }
}
