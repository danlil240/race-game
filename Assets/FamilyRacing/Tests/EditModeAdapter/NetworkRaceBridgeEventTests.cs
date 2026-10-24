using System.Collections.Generic;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using FamilyRacing.Networking;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // M5 Task 5 fix review: the client bridge's event dedup window is
    // per-race bookkeeping. A rematch adopts a fresh race identity —
    // identical minted ids from the new race must still deliver, while
    // same-race redeliveries and stale-race batches stay fenced. These
    // run against a real bridge/session pair with the sockets stubbed —
    // the live-wire rematch proof is PlayMode-side.
    public sealed class NetworkRaceBridgeEventTests
    {
        private sealed class StubAccess : ILanNetworkAccess
        {
            public LanNetworkAccessState State =>
                LanNetworkAccessState.Available;
            public string Detail => "stub";
            public void EnsureAccess() { }
        }

        private sealed class StubLink : ILanSessionLink
        {
            public bool StartHost(ushort port, out ushort boundPort,
                out string error)
            {
                boundPort = port == 0 ? (ushort)47777 : port;
                error = "";
                return true;
            }

            public bool StartClient(string address, ushort port,
                out string error)
            {
                error = "";
                return true;
            }

            public void Stop() { }
            public bool Send(ulong connectionId, string payload) => true;
        }

        private sealed class StubProbe : ILanDiscoveryProbe
        {
            public bool StartSearch(out string error)
            {
                error = "";
                return true;
            }

            public void StopSearch() { }
            public bool StartResponder(ushort gamePort, string hostName,
                string compat, out string error)
            {
                error = "";
                return true;
            }

            public void StopResponder() { }
            public IReadOnlyList<LanDiscoveredHost> Entries =>
                new List<LanDiscoveredHost>();
        }

        private static GameCommand Cmd(string action,
            Dictionary<string, string> parameters)
        {
            return new GameCommand(1, "bridge-events-test",
                "cmd-" + action, "local", action, parameters);
        }

        private static Dictionary<string, string> P(
            params string[] keyValues)
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i + 1 < keyValues.Length; i += 2)
                map[keyValues[i]] = keyValues[i + 1];
            return map;
        }

        private static LanSessionService HostedSession(out string sessionId)
        {
            var session = new LanSessionService(new StubLink(),
                new StubProbe(), "gameplay-test");
            session.SetLocalContext("p-host", "fox", 0, true, true);
            var hosted = session.Execute(Cmd("lan.host",
                P("port", "0")));
            Assert.That(hosted.status, Is.EqualTo(CommandResult.Applied),
                "lan.host failed: " + hosted.code);
            sessionId = session.Snapshot().descriptor.sessionId;
            Assert.IsFalse(string.IsNullOrEmpty(sessionId));
            return session;
        }

        private static DrivingEvent GadgetEvent(string id, string kind)
        {
            return new DrivingEvent
            {
                id = id,
                tick = 7,
                kind = kind,
                commandId = "",
                detail = "turbo-01",
                participantId = "p-host",
                attribution = "human"
            };
        }

        private static NetworkEventBatch Batch(string sessionId,
            string raceId, int ordinal, long sequence,
            params DrivingEvent[] events)
        {
            var batch = new NetworkEventBatch
            {
                sessionId = sessionId,
                raceId = raceId,
                raceOrdinal = ordinal,
                batchSequence = sequence
            };
            batch.events.AddRange(events);
            return batch;
        }

        [Test]
        public void AdoptedRaceAdmitsIdenticallyMintedEventIds()
        {
            string sessionId;
            var session = HostedSession(out sessionId);
            var transport = new LanTransport(new StubAccess());
            var bridge = new NetworkRaceBridge(transport, session);
            try
            {
                // Race 1 delivers its gadget events once — a redelivery
                // of the same ids stays deduplicated.
                bridge.ApplyEventBatch(Batch(sessionId, sessionId + "-r1",
                    1, 1, GadgetEvent("gadget-s-r1-7-1", "gadget.used")));
                var drained = bridge.DrainRaceEvents();
                Assert.That(drained.Count, Is.EqualTo(1));
                Assert.That(drained[0].id,
                    Is.EqualTo("gadget-s-r1-7-1"));

                bridge.ApplyEventBatch(Batch(sessionId, sessionId + "-r1",
                    1, 2, GadgetEvent("gadget-s-r1-7-1", "gadget.used")));
                Assert.That(bridge.DrainRaceEvents().Count, Is.EqualTo(0),
                    "a same-race redelivery of the same id is deduped");
                Assert.That(bridge.DroppedEvents, Is.EqualTo(1));

                // The rematch adopts race 2: the dedup window retires
                // with the old race, so even an id that literally
                // matches a race-1 id delivers. (Race-scoped minting
                // makes such collisions impossible in practice; the
                // window clear keeps adoption honest regardless.)
                bridge.ApplyEventBatch(Batch(sessionId, sessionId + "-r2",
                    2, 1, GadgetEvent("gadget-s-r1-7-1", "gadget.used")));
                drained = bridge.DrainRaceEvents();
                Assert.That(drained.Count, Is.EqualTo(1),
                    "race-2 events must not die in race-1's dedup window");

                // A stale-race batch is still fenced by its ordinal.
                bridge.ApplyEventBatch(Batch(sessionId, sessionId + "-r1",
                    1, 9, GadgetEvent("gadget-s-r1-9-1", "gadget.used")));
                Assert.That(bridge.DrainRaceEvents().Count, Is.EqualTo(0),
                    "a stale-race batch never re-admits old events");
            }
            finally
            {
                bridge.Dispose();
            }
        }
    }
}
