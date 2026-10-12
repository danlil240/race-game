using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;
using FamilyRacing.Networking;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FamilyRacing.Tests
{
    // Task 8 over the real UDP transport: result offers, persistence acks,
    // redelivery, and retrieval flow as real wire packets between full
    // rigs — session service + LanSessionLink + LanTransport + bridge —
    // with real file-backed, isolated ProfileStores standing in for each
    // process's private save directory. A rig disposed and rebuilt on the
    // same directory is a process restart at the persistence level; the
    // OS-process boundary itself is pinned by the external restart runner.
    public sealed class NetworkResultsPlayTests
    {
        private sealed class StubNetworkAccess : ILanNetworkAccess
        {
            public LanNetworkAccessState State =>
                LanNetworkAccessState.Available;
            public string Detail => "stub";
            public void EnsureAccess() { }
        }

        // Each transport pump costs ~1 s of real time in batchmode (the
        // UTP socket poll blocks briefly), so a multi-hop wait only gets a
        // handful of pump iterations per Unity frame. The budget is sized
        // for several request/reply round trips under that pacing.
        private const float TimeoutSeconds = 60f;

        private sealed class Rig : IDisposable
        {
            public readonly LanTransport Transport;
            public readonly LanDiscovery Discovery;
            public readonly LanSessionLink Link;
            public readonly LanSessionService Session;
            public readonly NetworkRaceBridge Bridge;
            public readonly ProfileStore Store;
            public readonly ProfileSave Profile;
            public double Now;
            public ulong ServerConnectionId;

            public Rig(ProfileSave profile, ProfileStore store,
                ILanNetworkAccess access, string credentialStorePath = null)
            {
                Store = store;
                Profile = profile;
                Transport = new LanTransport(access);
                Discovery = new LanDiscovery(access);
                Link = new LanSessionLink(Transport, Discovery);
                Session = new LanSessionService(Link, Link, "gameplay-test");
                if (credentialStorePath != null)
                    Session.SetReconnectStore(
                        new ReconnectCredentialStore(credentialStorePath));
                Link.Bind(Session);
                Session.SetLocalContext(profile.id, profile.avatar,
                    profile.colorIndex, true, true);
                Session.SetContent(Catalog());
                // The composition seam FoundationService installs at
                // runtime: the finalized result is applied against the
                // selected profile through the atomic store write.
                var saved = profile;
                var profileStore = store;
                Session.SetRewardApplier(result =>
                    RaceRewardService.Apply(result, saved.id, saved,
                        profileStore));
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
            return new GameCommand(1, "lan-results-play",
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
                    + " ops=" + s.pendingOperations.Count
                    + " results=" + Deliveries(s)
                    + " tstate=" + rig.Transport.State
                    + " terr=" + rig.Transport.LastError;
            }
            Assert.That(done(), Is.True,
                "Timed out waiting for LAN condition (" + what
                + ", frames=" + frames + ")." + diag);
        }

        private static string Deliveries(NetworkSessionState state)
        {
            var parts = new List<string>();
            foreach (var entry in state.results)
                parts.Add(entry.resultId + ":" + entry.participantId
                    + "=" + entry.state);
            return parts.Count == 0 ? "none" : string.Join(",", parts);
        }

        private static ResultDeliveryState DeliveryOf(
            NetworkSessionState state, string resultId, string participantId)
        {
            foreach (var entry in state.results)
                if (entry.resultId == resultId
                    && entry.participantId == participantId)
                    return entry;
            return null;
        }

        private static ParticipantState Find(NetworkSessionState state,
            string participantId)
        {
            foreach (var p in state.participants)
                if (p.participantId == participantId) return p;
            return null;
        }

        // A finalized result exactly as the coordinator publishes it:
        // both humans finished; the two AI-filled slots split a finish
        // and a DNF.
        private static RaceResult FinalResult(string sessionId, string raceId,
            string hostPid, string guestPid)
        {
            var result = new RaceResult
            {
                resultId = raceId + "/result",
                sessionId = sessionId,
                raceId = raceId,
                eventId = "lan-mixed",
                contentVersion = "lan-content-1",
                finalized = true,
                finalizedHostTick = 300,
                finalizedElapsedTicks = 210,
                completionCoins = NetworkSettings.LanCompletionCoins
            };
            var outcomes = new[,]
            {
                { hostPid, "finished", "human" },
                { guestPid, "finished", "human" },
                { "ai-slot-2", "finished", "ai" },
                { "ai-slot-3", "dnf", "ai" }
            };
            for (var i = 0; i < 4; i++)
            {
                var finished = outcomes[i, 1] == "finished";
                result.racers.Add(new RaceResultEntry
                {
                    position = i + 1,
                    slot = i,
                    participantId = outcomes[i, 0],
                    vehicleId = "car-01",
                    attribution = outcomes[i, 2],
                    outcome = outcomes[i, 1],
                    finishTicks = finished ? 210 + i : 0,
                    lapsCompleted = finished ? 1 : 0,
                    coinsAwarded = finished
                        ? NetworkSettings.LanCompletionCoins : 0
                });
            }
            return result;
        }

        private static string TempDir(string tag)
        {
            var dir = Path.Combine(Path.GetTempPath(),
                "fr-results-" + tag + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static ProfileSave SavedProfile(ProfileStore store, int coins)
        {
            var profile = ProfileFactory.Create(0, "en");
            profile.coins = coins;
            store.Save(profile);
            return store.Load(0).Profile;
        }

        // Session to the results phase with a finalized result published:
        // lobby -> configured -> ready -> start -> race -> publish ->
        // finish. The published result is stored in box[0] (iterators
        // cannot carry out parameters).
        private static IEnumerator RaceToResults(Rig host, Rig client,
            RaceResult[] box)
        {
            var hosted = host.Session.Execute(Cmd("lan.host", P("port", "0")));
            Assert.That(hosted.status, Is.EqualTo(CommandResult.Applied),
                "lan.host failed: " + hosted.code);
            var joined = client.Session.Execute(Cmd("lan.join",
                P("address", "127.0.0.1",
                  "port", host.Transport.BoundPort.ToString(
                      System.Globalization.CultureInfo.InvariantCulture))));
            Assert.That(joined.status, Is.EqualTo(CommandResult.Accepted),
                "lan.join rejected: " + joined.code);
            yield return PumpUntil(() =>
                client.Session.Snapshot().phase == LanSessionService.PhaseLobby,
                "join lobby", host, client);

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
            {
                var guest = Find(host.Session.Snapshot(), client.Profile.id);
                return guest != null && guest.ready;
            }, "client ready", host, client);

            Assert.That(host.Session.Execute(Cmd("lan.start", null)).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(host.Session.BeginRace(), Is.True,
                "countdown -> racing failed");

            var sessionId = host.Session.Snapshot().descriptor.sessionId;
            var published = FinalResult(sessionId, sessionId + "-r1",
                host.Profile.id, client.Profile.id);
            Assert.That(host.Session.PublishResult(published), Is.True,
                "the finalized result must publish");
            Assert.That(host.Session.FinishRace(), Is.True,
                "racing -> results failed");
            box[0] = published;
        }

        [UnityTest]
        public IEnumerator ResultDeliversAppliesAndPersistsOverWire()
        {
            var hostDir = TempDir("host");
            var clientDir = TempDir("client");
            try
            {
                var host = new Rig(SavedProfile(
                    new ProfileStore(hostDir, new UnityProfileSerializer()), 7),
                    new ProfileStore(hostDir, new UnityProfileSerializer()),
                    new StubNetworkAccess());
                var clientStore = new ProfileStore(clientDir,
                    new UnityProfileSerializer());
                var client = new Rig(SavedProfile(clientStore, 7), clientStore,
                    new StubNetworkAccess());
                try
                {
                    var box = new RaceResult[1];
                    yield return RaceToResults(host, client, box);
                    var published = box[0];

                    // The offer crosses real UDP, the client applies the
                    // authored reward through the atomic store write, and
                    // the persistence ack travels back.
                    yield return PumpUntil(() =>
                        client.Profile.coins == 7 + 20
                        && DeliveryOf(host.Session.Snapshot(),
                            published.resultId, client.Profile.id) != null
                        && DeliveryOf(host.Session.Snapshot(),
                            published.resultId, client.Profile.id).state
                            == "persisted",
                        "delivery + apply + ack", host, client);

                    Assert.That(client.Profile.coins, Is.EqualTo(27));
                    Assert.That(client.Profile.appliedResults.Count,
                        Is.EqualTo(1));
                    Assert.That(client.Profile.appliedResults[0].fingerprint,
                        Is.EqualTo(published.Fingerprint()));
                    Assert.That(client.Profile.appliedResults[0].awardedCoins,
                        Is.EqualTo(20));

                    // The host's own reward persisted through the same
                    // atomic write on its store.
                    Assert.That(host.Profile.coins, Is.EqualTo(27));
                    Assert.That(host.Profile.appliedResults.Count,
                        Is.EqualTo(1));

                    // A fresh store (process restart at the persistence
                    // level) proves the coins+receipt pair hit disk
                    // together.
                    var reloaded = new ProfileStore(clientDir,
                        new UnityProfileSerializer()).Load(0).Profile;
                    Assert.That(reloaded.coins, Is.EqualTo(27));
                    Assert.That(reloaded.appliedResults.Count, Is.EqualTo(1));
                }
                finally
                {
                    host.Dispose();
                    client.Dispose();
                }
            }
            finally
            {
                if (Directory.Exists(hostDir)) Directory.Delete(hostDir, true);
                if (Directory.Exists(clientDir)) Directory.Delete(clientDir, true);
            }
        }

        [UnityTest]
        public IEnumerator RestartedClientRedeliversResultOnce()
        {
            var hostDir = TempDir("host");
            var clientDir = TempDir("client");
            var storeDir = TempDir("cred");
            var credentialPath = Path.Combine(storeDir, "lan-reconnect.json");
            try
            {
                var host = new Rig(SavedProfile(
                    new ProfileStore(hostDir, new UnityProfileSerializer()), 7),
                    new ProfileStore(hostDir, new UnityProfileSerializer()),
                    new StubNetworkAccess());
                var clientStore = new ProfileStore(clientDir,
                    new UnityProfileSerializer());
                var client = new Rig(SavedProfile(clientStore, 7), clientStore,
                    new StubNetworkAccess(), credentialPath);
                var clientId = client.Profile.id;
                var box = new RaceResult[1];
                try
                {
                    yield return RaceToResults(host, client, box);
                    yield return PumpUntil(() =>
                        client.Profile.coins == 27
                        && DeliveryOf(host.Session.Snapshot(),
                            box[0].resultId, clientId) != null
                        && DeliveryOf(host.Session.Snapshot(),
                            box[0].resultId, clientId).state == "persisted",
                        "first delivery", host, client);
                }
                finally
                {
                    client.Dispose();
                }
                var published = box[0];

                // Client process dies during results; its slot stays
                // reserved and the credential file survives.
                host.Session.OnDisconnected(clientId, "peerTimeout");
                yield return PumpUntil(() =>
                {
                    var guest = Find(host.Session.Snapshot(), clientId);
                    return guest != null && !guest.connected;
                }, "client disconnect noticed", host);
                Assert.That(File.Exists(credentialPath), Is.True,
                    "reconnect material must survive the restart");

                // The restarted process rejoins on the stored credential
                // and a fresh, disk-loaded profile.
                var restartedStore = new ProfileStore(clientDir,
                    new UnityProfileSerializer());
                var restartedProfile = restartedStore.Load(0).Profile;
                Assert.That(restartedProfile.coins, Is.EqualTo(27));
                Assert.That(restartedProfile.appliedResults.Count,
                    Is.EqualTo(1));
                var restarted = new Rig(restartedProfile, restartedStore,
                    new StubNetworkAccess(), credentialPath);
                try
                {
                    var rejoin = restarted.Session.Execute(
                        Cmd("lan.reconnect", null));
                    Assert.That(rejoin.status,
                        Is.EqualTo(CommandResult.Accepted),
                        "lan.reconnect rejected: " + rejoin.code);
                    yield return PumpUntil(() =>
                    {
                        var guest = Find(host.Session.Snapshot(), clientId);
                        // The host flips connected before the client's
                        // join.accept lands — the client must also have
                        // left the reconnecting phase before commands
                        // that gate on a live-session phase will run.
                        return guest != null && guest.connected
                            && restarted.Session.Snapshot().phase
                                == LanSessionService.PhaseResults;
                    }, "credentialed reclaim", host, restarted);

                    // Retrieval rights survive the restart: asking again
                    // replays the immutable result, never re-awards. The
                    // reclaim outcome is drained so only the request's
                    // terminal result is awaited below.
                    restarted.Session.DrainOutcomes();
                    var request = restarted.Session.Execute(
                        Cmd("lan.results.request",
                            P("raceId", published.raceId)));
                    Assert.That(request.status,
                        Is.EqualTo(CommandResult.Accepted),
                        "results.request rejected: " + request.code);
                    // DrainOutcomes is destructive — collect outside the
                    // predicate so the wait and the final assert see the
                    // same accumulated result instead of re-draining.
                    var requestOutcomes = new List<CommandResult>();
                    yield return PumpUntil(() =>
                    {
                        requestOutcomes.AddRange(
                            restarted.Session.DrainOutcomes());
                        return requestOutcomes.Count > 0;
                    }, "results.request outcome", host, restarted);
                    Assert.That(requestOutcomes[0].status,
                        Is.EqualTo(CommandResult.Applied),
                        "results.request outcome: " + requestOutcomes[0].code);
                    yield return PumpUntil(() =>
                        DeliveryOf(host.Session.Snapshot(),
                            published.resultId, clientId) != null
                        && DeliveryOf(host.Session.Snapshot(),
                            published.resultId, clientId).state == "persisted",
                        "redelivery ack", host, restarted);

                    Assert.That(restarted.Profile.coins, Is.EqualTo(27),
                        "redelivery must never double-award");
                    Assert.That(restarted.Profile.appliedResults.Count,
                        Is.EqualTo(1),
                        "exactly one receipt survives every replay");
                    var onDisk = new ProfileStore(clientDir,
                        new UnityProfileSerializer()).Load(0).Profile;
                    Assert.That(onDisk.coins, Is.EqualTo(27));
                    Assert.That(onDisk.appliedResults.Count, Is.EqualTo(1));
                }
                finally
                {
                    restarted.Dispose();
                }
                host.Dispose();
            }
            finally
            {
                if (Directory.Exists(hostDir)) Directory.Delete(hostDir, true);
                if (Directory.Exists(clientDir)) Directory.Delete(clientDir, true);
                if (Directory.Exists(storeDir)) Directory.Delete(storeDir, true);
            }
        }
    }
}
