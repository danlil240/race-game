using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // Async command lifecycle and authority for the LAN surface: non-host
    // rejections, shared-pause routing, lobby availability reasons, client
    // requests completing only after the host answers, and solo commands
    // refusing while a session is active.
    public sealed class NetworkCommandTests
    {
        private string directory;
        private FoundationService service;
        private LanSessionTests.FakeLanLink link;
        private LanSessionTests.FakeLanProbe probe;
        private LanSessionService lan;

        private static GameCommand Cmd(string action,
            Dictionary<string, string> parameters, string commandId = null)
        {
            return LanSessionTests.Cmd(action, parameters, commandId);
        }

        private static Dictionary<string, string> P(params string[] keyValues)
        {
            return LanSessionTests.P(keyValues);
        }

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "family-racing-netcmd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            service = new FoundationService(
                new ProfileStore(directory, new LanSessionTests.LanTestJsonSerializer()),
                "en", "lan-session-test");
            service.SetDrivingContent(LanSessionTests.LanCatalog());
            link = new LanSessionTests.FakeLanLink();
            probe = new LanSessionTests.FakeLanProbe();
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

        private static LanSessionService JoinedClient(
            LanSessionTests.FakeLanLink clientLink,
            LanSessionTests.FakeLanProbe clientProbe, out string joinCommandId)
        {
            var client = LanSessionTests.StandaloneService(clientLink, clientProbe, "client-p");
            var join = client.Execute(Cmd("lan.join",
                P("address", "10.0.0.2", "port", "47777")));
            Assert.That(join.status, Is.EqualTo(CommandResult.Accepted));
            joinCommandId = join.commandId;
            clientLink.DrainSent(); // transport-level noise
            client.OnLinkConnected(3);
            clientLink.DrainSent(); // join.request
            client.OnLinkMessage(3, LanSessionTests.JoinAccept(
                "s-1", "client-p", 1, cid: joinCommandId));
            var joinOutcome = client.DrainOutcomes();
            Assert.That(joinOutcome.Count, Is.EqualTo(1));
            Assert.That(joinOutcome[0].status, Is.EqualTo(CommandResult.Applied),
                "join setup failed");
            return client;
        }

        // --- named task tests -------------------------------------------------

        [Test]
        public void NonHostCannotStartOrConfigure()
        {
            var clientLink = new LanSessionTests.FakeLanLink();
            var clientProbe = new LanSessionTests.FakeLanProbe();
            string joinId;
            var client = JoinedClient(clientLink, clientProbe, out joinId);

            var state = client.Snapshot();
            Assert.That(state.role, Is.EqualTo(LanSessionService.RoleClient));
            Assert.That(state.phase, Is.EqualTo(LanSessionService.PhaseLobby));

            Assert.That(client.Execute(Cmd("lan.start", null)).status,
                Is.EqualTo(CommandResult.Rejected));
            Assert.That(client.Execute(Cmd("lan.start", null)).code,
                Is.EqualTo("hostOnly"));
            Assert.That(client.Execute(Cmd("lan.configure",
                P("eventId", "lan-mixed", "classId", "car"))).code,
                Is.EqualTo("hostOnly"));
            Assert.That(client.Execute(Cmd("lan.pause.set",
                P("paused", "true"))).code, Is.EqualTo("hostOnly"));
        }

        [Test]
        public void ClassChangeClearsReady()
        {
            var host = LanSessionTests.StandaloneHost(link, probe);
            Assert.That(host.Execute(Cmd("lan.configure",
                P("eventId", "lan-mixed", "classId", "car"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(host.Execute(Cmd("lan.ready", P("ready", "true"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(host.Snapshot().participants[0].ready, Is.True);

            Assert.That(host.Execute(Cmd("lan.configure",
                P("eventId", "lan-mixed", "classId", "motorcycle"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(host.Snapshot().participants[0].ready, Is.False,
                "a class change must clear lobby readiness");
        }

        // --- async lifecycle --------------------------------------------------

        [Test]
        public void JoinAcceptCompletesAppliedAndDedupes()
        {
            CreateProfile();
            var command = Cmd("lan.join", P("address", "10.0.0.2", "port", "47777"));
            var accepted = service.Execute(command);
            Assert.That(accepted.status, Is.EqualTo(CommandResult.Accepted));
            Assert.That(service.Lookup(command.CommandId).status,
                Is.EqualTo(CommandResult.Accepted),
                "pending lookup must return accepted");

            lan.OnLinkConnected(3);
            link.DrainSent();
            lan.OnLinkMessage(3, LanSessionTests.JoinAccept(
                "s-1", "unused", 1, cid: command.CommandId));

            var outcomes = service.DrainNetworkOutcomes();
            Assert.That(outcomes.Count, Is.EqualTo(1));
            Assert.That(outcomes[0].status, Is.EqualTo(CommandResult.Applied),
                "successful completion returns applied");
            Assert.That(service.CompleteNetworkCommand(outcomes[0]), Is.True);
            var lookup = service.Lookup(command.CommandId);
            Assert.That(lookup.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(lookup.duplicate, Is.False);

            // Duplicate terminal delivery returns the same terminal result.
            var duplicate = service.Execute(command);
            Assert.That(duplicate.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(duplicate.duplicate, Is.True);
            Assert.That(service.Snapshot().network.phase,
                Is.EqualTo(LanSessionService.PhaseLobby));
        }

        [Test]
        public void JoinRejectCompletesRejected()
        {
            CreateProfile();
            var command = Cmd("lan.join", P("address", "10.0.0.2", "port", "47777"));
            Assert.That(service.Execute(command).status,
                Is.EqualTo(CommandResult.Accepted));

            lan.OnLinkConnected(3);
            link.DrainSent();
            lan.OnLinkMessage(3, NetworkProtocol.Encode(
                NetworkProtocol.MsgJoinReject, "code", "lobbyFull",
                "cid", command.CommandId));

            var outcomes = service.DrainNetworkOutcomes();
            Assert.That(outcomes.Count, Is.EqualTo(1));
            Assert.That(outcomes[0].status, Is.EqualTo(CommandResult.Rejected),
                "failure returns failed/rejected");
            Assert.That(outcomes[0].code, Is.EqualTo("lobbyFull"));
            Assert.That(service.CompleteNetworkCommand(outcomes[0]), Is.True);
            Assert.That(service.Lookup(command.CommandId).status,
                Is.EqualTo(CommandResult.Rejected));
            Assert.That(service.Snapshot().network.phase,
                Is.EqualTo(LanSessionService.PhaseIdle));
        }

        [Test]
        public void ClientReadyCompletesAfterHostAck()
        {
            // Two real services over paired fake links: the client's request
            // is applied only after the host answers, not when it is queued.
            var hostLink = new LanSessionTests.FakeLanLink();
            var hostProbe = new LanSessionTests.FakeLanProbe();
            var host = LanSessionTests.StandaloneHost(hostLink, hostProbe);
            Assert.That(host.Execute(Cmd("lan.configure",
                P("eventId", "lan-mixed", "classId", "car"))).status,
                Is.EqualTo(CommandResult.Applied));

            var clientLink = new LanSessionTests.FakeLanLink();
            var clientProbe = new LanSessionTests.FakeLanProbe();
            var client = LanSessionTests.StandaloneService(clientLink, clientProbe, "client-p");

            var join = client.Execute(Cmd("lan.join",
                P("address", "10.0.0.2", "port", "47777")));
            Assert.That(join.status, Is.EqualTo(CommandResult.Accepted));
            host.OnLinkConnected(9);
            client.OnLinkConnected(3);
            clientLink.DeliverTo(host, 9);
            hostLink.DeliverTo(client, 3);
            var joinOutcome = client.DrainOutcomes();
            Assert.That(joinOutcome.Count, Is.EqualTo(1));
            Assert.That(joinOutcome[0].status, Is.EqualTo(CommandResult.Applied));

            var ready = client.Execute(Cmd("lan.ready", P("ready", "true")));
            Assert.That(ready.status, Is.EqualTo(CommandResult.Accepted));
            Assert.That(client.DrainOutcomes().Count, Is.EqualTo(0),
                "queuing the request must not complete it");

            clientLink.DeliverTo(host, 9);
            hostLink.DeliverTo(client, 3);
            var readyOutcome = client.DrainOutcomes();
            Assert.That(readyOutcome.Count, Is.EqualTo(1));
            Assert.That(readyOutcome[0].commandId, Is.EqualTo(ready.commandId));
            Assert.That(readyOutcome[0].status, Is.EqualTo(CommandResult.Applied));

            var hostRoster = host.Snapshot().participants;
            var sawReady = false;
            foreach (var p in hostRoster)
                if (p.occupied && p.participantId == "client-p" && p.ready)
                    sawReady = true;
            Assert.That(sawReady, Is.True, "host roster must show the client ready");
        }

        [Test]
        public void SoloCommandsRejectDuringLanSession()
        {
            CreateProfile();
            Assert.That(service.Execute(Cmd("lan.host", P("port", "47777"))).status,
                Is.EqualTo(CommandResult.Applied));

            var state = service.Snapshot();
            Assert.That(state.network.role, Is.EqualTo(LanSessionService.RoleHost));
            Assert.That(state.stateSchemaVersion, Is.EqualTo(3));
            Assert.That(state.network, Is.Not.Null);

            foreach (var action in new[]
                { "race.start", "race.restart", "tutorial.start" })
            {
                var parameters = action == "race.start"
                    ? P("vehicleId", "car-01", "eventId", "lan-mixed")
                    : (action == "tutorial.start" ? P("vehicleId", "car-01") : null);
                var result = service.Execute(Cmd(action, parameters));
                Assert.That(result.status, Is.EqualTo(CommandResult.Rejected), action);
                Assert.That(result.code, Is.EqualTo("networkActive"), action);
            }
        }

        [Test]
        public void PauseToggleRoutesByRole()
        {
            // Host: pause.toggle is a shared toggle (applied synchronously).
            CreateProfile();
            Assert.That(service.Execute(Cmd("lan.host", P("port", "47777"))).status,
                Is.EqualTo(CommandResult.Applied));
            var toggled = service.Execute(Cmd("pause.toggle", null));
            Assert.That(toggled.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(service.Snapshot().network.paused, Is.True,
                "host pause.toggle must flip the shared pause flag");
            Assert.That(service.Snapshot().paused, Is.False,
                "network pause must not leak into the solo pause flag");

            // Client: pause.toggle is a request the host must register.
            var clientLink = new LanSessionTests.FakeLanLink();
            var clientProbe = new LanSessionTests.FakeLanProbe();
            string joinId;
            var client = JoinedClient(clientLink, clientProbe, out joinId);
            var request = client.Execute(Cmd("pause.toggle", null));
            Assert.That(request.status, Is.EqualTo(CommandResult.Accepted));
            client.OnLinkMessage(3, NetworkProtocol.Encode(
                NetworkProtocol.MsgOpResult, "cid", request.commandId,
                "st", CommandResult.Applied, "code", ""));
            var outcomes = client.DrainOutcomes();
            Assert.That(outcomes.Count, Is.EqualTo(1));
            Assert.That(outcomes[0].status, Is.EqualTo(CommandResult.Applied));
        }
    }
}
