using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // LAN lobby/session policy: join admission, bounded capacity, exact
    // compatibility, async join lifecycle and identity dedup. Transport is
    // faked through the Core ILanSessionLink/ILanDiscoveryProbe seams so the
    // policy layer is proven without Unity or sockets.
    public sealed class LanSessionTests
    {
        private const string RunId = "lan-session-test";

        private string directory;
        private FoundationService service;
        private FakeLanLink link;
        private FakeLanProbe probe;
        private LanSessionService lan;

        // --- shared fakes/helpers (also used by NetworkCommandTests) ----------

        internal sealed class FakeLanLink : ILanSessionLink
        {
            public int StartHostCalls;
            public int StartClientCalls;
            public int StopCalls;
            public readonly Queue<KeyValuePair<ulong, string>> Sent =
                new Queue<KeyValuePair<ulong, string>>();

            public bool StartHost(ushort port, out ushort boundPort, out string error)
            {
                StartHostCalls++;
                boundPort = port == 0 ? (ushort)47777 : port;
                error = "";
                return true;
            }

            public bool StartClient(string address, ushort port, out string error)
            {
                StartClientCalls++;
                error = "";
                return true;
            }

            public void Stop()
            {
                StopCalls++;
            }

            public bool Send(ulong connectionId, string payload)
            {
                Sent.Enqueue(new KeyValuePair<ulong, string>(connectionId, payload));
                return true;
            }

            public List<string> DrainSent()
            {
                var drained = new List<string>();
                while (Sent.Count > 0) drained.Add(Sent.Dequeue().Value);
                return drained;
            }

            // Routes every queued outbound payload to the given service as
            // if it arrived on the supplied connection id.
            public void DeliverTo(LanSessionService target, ulong asConnectionId)
            {
                while (Sent.Count > 0)
                    target.OnLinkMessage(asConnectionId, Sent.Dequeue().Value);
            }
        }

        internal sealed class FakeLanProbe : ILanDiscoveryProbe
        {
            public int SearchStarts;
            public int SearchStops;
            public int ResponderStarts;
            public readonly List<LanDiscoveredHost> Hosts = new List<LanDiscoveredHost>();

            public bool StartSearch(out string error)
            {
                SearchStarts++;
                error = "";
                return true;
            }

            public void StopSearch()
            {
                SearchStops++;
            }

            public bool StartResponder(ushort gamePort, string hostName, out string error)
            {
                ResponderStarts++;
                error = "";
                return true;
            }

            public void StopResponder() { }

            public IReadOnlyList<LanDiscoveredHost> Entries
            {
                get { return Hosts; }
            }
        }

        internal sealed class LanTestJsonSerializer : IProfileSerializer
        {
#if UNITY_5_3_OR_NEWER
            public string Serialize(ProfileSave profile) { return UnityEngine.JsonUtility.ToJson(profile); }
            public ProfileSave Deserialize(string json) { return UnityEngine.JsonUtility.FromJson<ProfileSave>(json); }
#else
            private static readonly System.Text.Json.JsonSerializerOptions Options =
                new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
            public string Serialize(ProfileSave profile) { return System.Text.Json.JsonSerializer.Serialize(profile, Options); }
            public ProfileSave Deserialize(string json) { return System.Text.Json.JsonSerializer.Deserialize<ProfileSave>(json, Options); }
#endif
        }

        internal static DrivingContentCatalog LanCatalog()
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
                        recoverX = 0f, recoverZ = 1997f
                    }
                }
            };
            var mixed = new EventDefinition
            {
                id = "lan-mixed",
                courseId = "course-lan",
                laps = 1,
                countdownTicks = 90,
                vehicleIds = new List<string> { "car-01", "motorcycle-01" }
            };
            return DrivingContentCatalog.Create("lan-content-1",
                new[] { VehicleTuning.Car01(), VehicleTuning.Motorcycle01() },
                new[] { course },
                new[] { mixed });
        }

        internal static GameCommand Cmd(string action, Dictionary<string, string> parameters,
            string commandId = null)
        {
            return new GameCommand(1, RunId, commandId ?? Guid.NewGuid().ToString("N"),
                "local", action, parameters);
        }

        internal static Dictionary<string, string> P(params string[] keyValues)
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i + 1 < keyValues.Length; i += 2)
                map[keyValues[i]] = keyValues[i + 1];
            return map;
        }

        internal static string JoinRequest(string participantId,
            string contentVersion = "lan-content-1",
            string gameplayVersion = "gameplay-test",
            string credential = "")
        {
            return NetworkProtocol.Encode(NetworkProtocol.MsgJoinRequest,
                "v", NetworkProtocol.LanProtocolVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                "gv", gameplayVersion,
                "cv", contentVersion,
                "pid", participantId,
                "av", "fox",
                "ci", "1",
                "cid", "c-" + participantId,
                "cred", credential);
        }

        internal static string JoinAccept(string sessionId, string participantId,
            int slot, string phase = "lobby", string eventId = "lan-mixed",
            string classId = "car", string credential = "secret-1", string cid = "")
        {
            return NetworkProtocol.Encode(NetworkProtocol.MsgJoinAccept,
                "sid", sessionId,
                "ph", phase,
                "pid", participantId,
                "slot", slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "ev", eventId,
                "cl", classId,
                "ep", "1",
                "cred", credential,
                "cid", cid);
        }

        internal static LanSessionService StandaloneService(FakeLanLink fakeLink,
            FakeLanProbe fakeProbe, string participantId)
        {
            var session = new LanSessionService(fakeLink, fakeProbe, "gameplay-test");
            session.SetLocalContext(participantId, "fox", 0, true, true);
            session.SetContent(LanCatalog());
            return session;
        }

        internal static LanSessionService StandaloneHost(FakeLanLink fakeLink,
            FakeLanProbe fakeProbe)
        {
            var host = StandaloneService(fakeLink, fakeProbe, "host-p");
            var result = host.Execute(Cmd("lan.host", P("port", "47777")));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied),
                "lan.host setup failed");
            return host;
        }

        // --- fixture ----------------------------------------------------------

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "family-racing-lan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            service = new FoundationService(
                new ProfileStore(directory, new LanTestJsonSerializer()), "en", RunId);
            service.SetDrivingContent(LanCatalog());
            link = new FakeLanLink();
            probe = new FakeLanProbe();
            lan = new LanSessionService(link, probe, "gameplay-test");
            service.SetLanSession(lan);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        private void CreateProfile()
        {
            var created = service.Execute(Cmd("profile.create",
                P("slot", "0", "language", "en")));
            Assert.That(created.status, Is.EqualTo(CommandResult.Applied),
                "profile.create setup failed");
        }

        // --- named task tests -------------------------------------------------

        [Test]
        public void DuplicatePendingJoinStartsOneConnection()
        {
            CreateProfile();
            var command = Cmd("lan.join", P("address", "192.168.1.4", "port", "47777"));
            var first = service.Execute(command);
            Assert.That(first.status, Is.EqualTo(CommandResult.Accepted));

            // Identical redelivery replays the cached pending result and
            // must not resubmit transport work.
            var again = service.Execute(command);
            Assert.That(again.status, Is.EqualTo(CommandResult.Accepted));
            Assert.That(again.duplicate, Is.True);

            // A distinct join while one is pending is refused without
            // touching the transport either.
            var other = service.Execute(Cmd("lan.join",
                P("address", "192.168.1.5", "port", "47777")));
            Assert.That(other.status, Is.EqualTo(CommandResult.Rejected));

            Assert.That(link.StartClientCalls, Is.EqualTo(1),
                "duplicate pending join started extra transport work");
            Assert.That(service.Lookup(command.CommandId).status,
                Is.EqualTo(CommandResult.Accepted));
            Assert.That(service.DrainNetworkOutcomes().Count, Is.EqualTo(0));
        }

        [Test]
        public void IdentityConflictDoesNotMutateLobby()
        {
            CreateProfile();
            var hosted = service.Execute(Cmd("lan.host", P("port", "47777")));
            Assert.That(hosted.status, Is.EqualTo(CommandResult.Applied));

            var configure = Cmd("lan.configure",
                P("eventId", "lan-mixed", "classId", "car"), "cfg-1");
            Assert.That(service.Execute(configure).status,
                Is.EqualTo(CommandResult.Applied));

            // Same commandId, different payload: identity conflict wins and
            // the lobby keeps the first configuration.
            var conflict = service.Execute(Cmd("lan.configure",
                P("eventId", "lan-mixed", "classId", "motorcycle"), "cfg-1"));
            Assert.That(conflict.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(conflict.code, Is.EqualTo("identityConflict"));

            var state = service.Snapshot().network;
            Assert.That(state.descriptor.eventId, Is.EqualTo("lan-mixed"));
            Assert.That(state.descriptor.classId, Is.EqualTo("car"));
        }

        [Test]
        public void FifthParticipantRejected()
        {
            var host = StandaloneHost(link, probe);
            for (var i = 1; i <= 3; i++)
            {
                var conn = (ulong)i;
                host.OnLinkConnected(conn);
                host.OnLinkMessage(conn, JoinRequest("p-" + i));
            }
            link.DrainSent();
            var roster = host.Snapshot().participants;
            var occupied = 0;
            foreach (var p in roster) if (p.occupied) occupied++;
            Assert.That(occupied, Is.EqualTo(4), "host plus three guests expected");

            host.OnLinkConnected(9);
            host.OnLinkMessage(9, JoinRequest("p-4"));

            var replies = link.DrainSent();
            string reject = null;
            foreach (var payload in replies)
            {
                string type;
                Dictionary<string, string> fields;
                Assert.That(NetworkProtocol.TryDecode(payload, out type, out fields));
                if (type == NetworkProtocol.MsgJoinReject) reject = fields["code"];
            }
            Assert.That(reject, Is.EqualTo("lobbyFull"));

            var after = host.Snapshot().participants;
            var stillOccupied = 0;
            foreach (var p in after)
            {
                if (p.occupied) stillOccupied++;
                Assert.That(p.participantId, Is.Not.EqualTo("p-4"),
                    "fifth participant must not take a slot");
            }
            Assert.That(stillOccupied, Is.EqualTo(4));
        }

        [Test]
        public void IncompatibleContentRejectedBeforeSpawn()
        {
            var host = StandaloneHost(link, probe);
            host.OnLinkConnected(5);
            host.OnLinkMessage(5, JoinRequest("p-1", contentVersion: "other-content"));

            var replies = link.DrainSent();
            string reject = null;
            foreach (var payload in replies)
            {
                string type;
                Dictionary<string, string> fields;
                Assert.That(NetworkProtocol.TryDecode(payload, out type, out fields));
                if (type == NetworkProtocol.MsgJoinReject) reject = fields["code"];
            }
            Assert.That(reject, Is.EqualTo("incompatible"));

            var roster = host.Snapshot().participants;
            var occupied = 0;
            foreach (var p in roster) if (p.occupied) occupied++;
            Assert.That(occupied, Is.EqualTo(1),
                "incompatible guest must be rejected before slot allocation");
            Assert.That(roster[0].participantId, Is.EqualTo("host-p"));
        }

        [Test]
        public void JoinTimesOutOnce()
        {
            CreateProfile();
            service.AdvanceNetworkWallTime(100.0);
            var join = service.Execute(Cmd("lan.join",
                P("address", "192.168.1.9", "port", "47777")));
            Assert.That(join.status, Is.EqualTo(CommandResult.Accepted));

            service.AdvanceNetworkWallTime(105.1);
            var outcomes = service.DrainNetworkOutcomes();
            Assert.That(outcomes.Count, Is.EqualTo(1));
            Assert.That(outcomes[0].commandId, Is.EqualTo(join.commandId));
            Assert.That(outcomes[0].status, Is.EqualTo(CommandResult.Failed));
            Assert.That(outcomes[0].code, Is.EqualTo("joinTimeout"));
            Assert.That(service.CompleteNetworkCommand(outcomes[0]), Is.True);
            Assert.That(service.Lookup(join.commandId).status,
                Is.EqualTo(CommandResult.Failed));

            // The timeout fires exactly once: later wall-time advances
            // produce no further outcomes and the session is back at idle.
            service.AdvanceNetworkWallTime(200.0);
            Assert.That(service.DrainNetworkOutcomes().Count, Is.EqualTo(0));
            Assert.That(service.Snapshot().network.phase, Is.EqualTo("idle"));
            Assert.That(link.StopCalls, Is.GreaterThanOrEqualTo(1));
            Assert.That(service.CompleteNetworkCommand(outcomes[0]), Is.False,
                "terminal cache update must apply exactly once");
        }
    }
}
