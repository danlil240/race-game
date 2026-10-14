using System;
using System.Collections.Generic;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // Task 7 lifecycle policy, proven at the Unity-free Core level:
    // host-approved shared pause, reserved slots across disconnects,
    // credentialed rejoin with epoch fencing, credential persistence for
    // client-process restart, and host-loss recovery. Transport stays
    // faked through the ILanSessionLink seam; two services are wired
    // payload-for-payload wherever both ends matter.
    public sealed class NetworkLifecycleTests
    {
        private const ulong GuestConn = 9;   // host-side connection id for the guest
        private const ulong HostConn = 1;    // guest-side connection id for its server
        private const ulong GuestConn2 = 20; // replacement connection after rejoin

        private static readonly DriverInput Neutral =
            new DriverInput(0f, false, false, false, false, false, false);

        private LanSessionTests.FakeLanLink link;
        private LanSessionTests.FakeLanProbe probe;
        private LanSessionService host;

        // In-memory credential store standing in for the private Runtime
        // store — identical contract, no files.
        private sealed class MemoryCredentialStore : IReconnectCredentialStore
        {
            public ReconnectMaterial Stored;
            public int SaveCalls;
            public int ClearCalls;

            public void Save(ReconnectMaterial material)
            {
                SaveCalls++;
                Stored = material;
            }

            public bool TryLoad(string participantId, out ReconnectMaterial material)
            {
                material = Stored != null
                    && Stored.participantId == participantId ? Stored : null;
                return material != null;
            }

            public void Clear()
            {
                ClearCalls++;
                Stored = null;
            }
        }

        [SetUp]
        public void SetUp()
        {
            link = new LanSessionTests.FakeLanLink();
            probe = new LanSessionTests.FakeLanProbe();
            host = LanSessionTests.StandaloneHost(link, probe);
        }

        private static LanSessionService NewGuest(
            LanSessionTests.FakeLanLink guestLink, string participantId,
            IReconnectCredentialStore store = null)
        {
            var guest = LanSessionTests.StandaloneService(guestLink,
                new LanSessionTests.FakeLanProbe(), participantId);
            if (store != null) guest.SetReconnectStore(store);
            return guest;
        }

        private static GameCommand Cmd(string action,
            Dictionary<string, string> parameters, string commandId = null)
        {
            return LanSessionTests.Cmd(action, parameters, commandId);
        }

        private static Dictionary<string, string> P(params string[] keyValues)
        {
            return LanSessionTests.P(keyValues);
        }

        // Delivers every queued outbound payload both ways: messages the
        // guest sent arrive at the host as `guestConn`, messages the host
        // sent arrive at the guest as its server connection `hostConn`.
        private static void PumpWire(
            LanSessionTests.FakeLanLink hostLink,
            LanSessionTests.FakeLanLink guestLink,
            LanSessionService host, LanSessionService guest,
            ulong guestConn, ulong hostConn)
        {
            guestLink.DeliverTo(host, guestConn);
            hostLink.DeliverTo(guest, hostConn);
        }

        private static void JoinGuest(LanSessionService host,
            LanSessionTests.FakeLanLink hostLink,
            LanSessionService guest, LanSessionTests.FakeLanLink guestLink,
            ulong guestConn, ulong hostConn)
        {
            var join = guest.Execute(Cmd("lan.join",
                P("address", "192.168.7.7", "port", "47777")));
            Assert.That(join.status, Is.EqualTo(CommandResult.Accepted),
                "lan.join rejected: " + join.code);
            host.OnLinkConnected(guestConn);
            guest.OnLinkConnected(hostConn);
            PumpWire(hostLink, guestLink, host, guest, guestConn, hostConn);
            Assert.That(guest.Snapshot().phase,
                Is.EqualTo(LanSessionService.PhaseLobby),
                "guest never reached the lobby");
        }

        private static void GuestReady(LanSessionService host, ulong conn, string tag)
        {
            host.OnLinkMessage(conn, NetworkProtocol.Encode(
                NetworkProtocol.MsgReadySet, "cid", "rdy-" + tag, "rdy", "1"));
        }

        // lan.configure must precede readiness (configure clears the
        // flags), and every connected human — including the guest — must
        // be ready before lan.start.
        private static void ConfigureCar(LanSessionService host)
        {
            Assert.That(host.Execute(Cmd("lan.configure",
                P("eventId", "lan-mixed", "classId", "car"))).status,
                Is.EqualTo(CommandResult.Applied));
        }

        private static void ReadyAndStart(LanSessionService host,
            ulong guestConn = 0)
        {
            if (guestConn != 0) GuestReady(host, guestConn, "1");
            Assert.That(host.Execute(Cmd("lan.ready", P("ready", "true"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(host.Execute(Cmd("lan.start", null)).status,
                Is.EqualTo(CommandResult.Applied));
        }

        private static RaceResult FinishedResult(string sessionId, string raceId,
            params string[] participantOutcomes)
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
                completionCoins = 20
            };
            var position = 0;
            for (var i = 0; i + 1 < participantOutcomes.Length; i += 2)
            {
                var outcome = participantOutcomes[i + 1];
                var finished = outcome == "finished";
                result.racers.Add(new RaceResultEntry
                {
                    position = ++position,
                    slot = i / 2,
                    participantId = participantOutcomes[i],
                    vehicleId = "car-01",
                    attribution = "human",
                    outcome = outcome,
                    finishTicks = finished ? 210 : 0,
                    lapsCompleted = finished ? 1 : 0,
                    coinsAwarded = finished ? 20 : 0
                });
            }
            return result;
        }

        private static RaceResult Retained(LanSessionService host,
            string resultId)
        {
            foreach (var result in host.RetainedResults())
                if (result.resultId == resultId) return result;
            return null;
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

        private static string RejectCode(LanSessionTests.FakeLanLink sent,
            string wantedType = NetworkProtocol.MsgJoinReject)
        {
            string code = null;
            foreach (var payload in sent.DrainSent())
            {
                string type;
                Dictionary<string, string> fields;
                if (NetworkProtocol.TryDecode(payload, out type, out fields)
                    && type == wantedType)
                    code = fields["code"];
            }
            return code;
        }

        private static string CaptureCredential(LanSessionTests.FakeLanLink hostLink,
            ulong conn)
        {
            string credential = null;
            foreach (var pair in hostLink.DrainPairs())
            {
                string type;
                Dictionary<string, string> fields;
                if (pair.Key != conn) continue;
                if (!NetworkProtocol.TryDecode(pair.Value, out type, out fields))
                    continue;
                if (type == NetworkProtocol.MsgJoinAccept)
                    fields.TryGetValue("cred", out credential);
            }
            return credential;
        }

        private static NetworkInputFrame Frame(string participantId, long sequence,
            float steering = 0f, long epoch = 1,
            string sessionId = "s-1", string raceId = "s-1-r1")
        {
            return new NetworkInputFrame
            {
                sessionId = sessionId,
                raceId = raceId,
                participantId = participantId,
                ownershipEpoch = epoch,
                inputSequence = sequence,
                clientTick = sequence,
                steering = steering
            };
        }

        // --- ReconnectRegistry: the credential/reservation record ---------

        [Test]
        public void ReclaimRequiresMatchingSessionAndCurrentCredential()
        {
            var registry = new ReconnectRegistry();
            registry.BeginSession("s-1");
            var credential = registry.Issue("p-1");
            string pid;
            long epoch;
            Assert.That(registry.TryReclaim(credential, "s-1",
                out pid, out epoch), Is.True);
            Assert.That(pid, Is.EqualTo("p-1"));
            Assert.That(epoch, Is.EqualTo(2), "reclaim must bump the ownership epoch");

            Assert.That(registry.TryReclaim(credential, "s-2",
                out pid, out epoch), Is.False, "credentials are scoped to the session");
            Assert.That(registry.TryReclaim("forged", "s-1",
                out pid, out epoch), Is.False);
            Assert.That(registry.TryReclaim("", "s-1",
                out pid, out epoch), Is.False);
            Assert.That(registry.TryReclaim(null, "s-1",
                out pid, out epoch), Is.False);
        }

        [Test]
        public void ReclaimRotatesCredentialAndRetiresThePresentedOne()
        {
            var registry = new ReconnectRegistry();
            registry.BeginSession("s-1");
            var first = registry.Issue("p-1");
            string pid;
            long epoch;
            Assert.That(registry.TryReclaim(first, "s-1", out pid, out epoch), Is.True);
            var rotated = registry.Rotate("p-1");
            Assert.That(rotated, Is.Not.Null.And.Not.Empty);
            Assert.That(rotated, Is.Not.EqualTo(first));

            // The retired credential can never reclaim the slot again…
            Assert.That(registry.TryReclaim(first, "s-1", out pid, out epoch),
                Is.False, "a superseded credential must not reclaim twice");
            // …but it keeps result-retrieval rights for that participant
            // (the Task-8 seam), which never bump the epoch.
            Assert.That(registry.TryAuthenticate(first, "s-1", out pid), Is.True);
            Assert.That(pid, Is.EqualTo("p-1"));

            Assert.That(registry.TryReclaim(rotated, "s-1", out pid, out epoch),
                Is.True);
            Assert.That(epoch, Is.EqualTo(3));
        }

        [Test]
        public void RevokeRemovesEveryIssuedCredential()
        {
            var registry = new ReconnectRegistry();
            registry.BeginSession("s-1");
            var credA = registry.Issue("p-1");
            var credB = registry.Issue("p-2");
            registry.Rotate("p-1");
            registry.Revoke("p-1");
            string pid;
            long epoch;
            Assert.That(registry.TryReclaim(credA, "s-1", out pid, out epoch), Is.False);
            Assert.That(registry.TryAuthenticate(credA, "s-1", out pid), Is.False);
            Assert.That(registry.TryReclaim(credB, "s-1", out pid, out epoch),
                Is.True, "other participants are unaffected");
            Assert.That(pid, Is.EqualTo("p-2"));
        }

        // --- disconnect per phase -----------------------------------------

        [Test]
        public void DisconnectInCountdownRetainsSlotAsAi()
        {
            host.OnLinkConnected(GuestConn);
            host.OnLinkMessage(GuestConn, LanSessionTests.JoinRequest("p-1"));
            ConfigureCar(host);
            ReadyAndStart(host, GuestConn);
            // lan.start was issued but BeginRace has not run yet: the
            // session still sits in countdown.
            Assert.That(host.Phase, Is.EqualTo(LanSessionService.PhaseCountdown));

            host.OnLinkDisconnected(GuestConn, "lost");

            var guest = Find(host.Snapshot(), "p-1");
            Assert.That(guest, Is.Not.Null, "slot freed during countdown");
            Assert.That(guest.connected, Is.False);
            Assert.That(guest.controlSource, Is.EqualTo("ai"),
                "reserved slot falls to AI for the remainder of the race");
        }

        [Test]
        public void DisconnectWhilePausedKeepsReservation()
        {
            host.OnLinkConnected(GuestConn);
            host.OnLinkMessage(GuestConn, LanSessionTests.JoinRequest("p-1"));
            ConfigureCar(host);
            ReadyAndStart(host, GuestConn);
            Assert.That(host.BeginRace(), Is.True);

            Assert.That(host.Execute(Cmd("lan.pause.set",
                P("paused", "true"))).status, Is.EqualTo(CommandResult.Applied));
            host.OnLinkDisconnected(GuestConn, "lost");

            var snap = host.Snapshot();
            var guest = Find(snap, "p-1");
            Assert.That(guest, Is.Not.Null, "slot must stay reserved while paused");
            Assert.That(guest.connected, Is.False);
            Assert.That(guest.controlSource, Is.EqualTo("ai"));
            Assert.That(snap.paused, Is.True, "the shared pause survives the drop");
            Assert.That(snap.pauseRequests, Does.Not.Contain("p-1"),
                "a dead peer's pause request must not linger");

            // Resuming does not hand the reserved slot back to a human.
            Assert.That(host.Execute(Cmd("lan.pause.set",
                P("paused", "false"))).status, Is.EqualTo(CommandResult.Applied));
            guest = Find(host.Snapshot(), "p-1");
            Assert.That(guest.controlSource, Is.EqualTo("ai"));
            Assert.That(guest.connected, Is.False);
        }

        [Test]
        public void DisconnectInResultsHoldsSlotUntilRematch()
        {
            host.OnLinkConnected(GuestConn);
            host.OnLinkMessage(GuestConn, LanSessionTests.JoinRequest("p-1"));
            var credential = CaptureCredential(link, GuestConn);
            ConfigureCar(host);
            ReadyAndStart(host, GuestConn);
            Assert.That(host.BeginRace(), Is.True);
            Assert.That(host.FinishRace(), Is.True);
            Assert.That(host.Phase, Is.EqualTo(LanSessionService.PhaseResults));

            // A disconnect during results still holds the slot so the
            // participant can rejoin for result retrieval.
            host.OnLinkDisconnected(GuestConn, "lost");
            var guest = Find(host.Snapshot(), "p-1");
            Assert.That(guest, Is.Not.Null,
                "results-phase disconnect must keep the slot for retrieval");
            Assert.That(guest.connected, Is.False);

            // The race-slot reservation dies with the race lifecycle: the
            // rematch back to lobby frees it for a fresh participant.
            Assert.That(host.Execute(Cmd("lan.rematch", null)).status,
                Is.EqualTo(CommandResult.Applied));
            guest = Find(host.Snapshot(), "p-1");
            Assert.That(guest, Is.Null,
                "rematch must release a disconnected player's reservation");

            // The credential keeps authenticating result retrieval but the
            // released slot is gone — a credentialed return is an ordinary
            // fresh lobby join now (epoch restarts at 1).
            host.OnLinkConnected(GuestConn2);
            host.OnLinkMessage(GuestConn2,
                LanSessionTests.JoinRequest("p-1", credential: credential));
            var acceptEpoch = (string)null;
            var reject = (string)null;
            foreach (var pair in link.DrainPairs())
            {
                string type;
                Dictionary<string, string> fields;
                if (!NetworkProtocol.TryDecode(pair.Value, out type, out fields))
                    continue;
                if (type == NetworkProtocol.MsgJoinAccept)
                    acceptEpoch = fields["ep"];
                if (type == NetworkProtocol.MsgJoinReject)
                    reject = fields["code"];
            }
            Assert.That(reject, Is.Null, "a released slot rejoins as a fresh participant");
            Assert.That(acceptEpoch, Is.EqualTo("1"),
                "a released reservation must not keep its old epoch");
            guest = Find(host.Snapshot(), "p-1");
            Assert.That(guest, Is.Not.Null);
            Assert.That(guest.connected, Is.True);
        }

        // --- result delivery lifecycle policy ---------------------------------

        [Test]
        public void ExplicitLeaveTerminatesPendingDeliveryButDisconnectDoesNot()
        {
            var guestLink = new LanSessionTests.FakeLanLink();
            var guest = NewGuest(guestLink, "p-guest");
            host.SetRewardApplier(result =>
                RewardApplyResult.Applied(20, result.Fingerprint()));
            guest.SetRewardApplier(result =>
                RewardApplyResult.Applied(20, result.Fingerprint()));
            JoinGuest(host, link, guest, guestLink, GuestConn, HostConn);
            ConfigureCar(host);
            ReadyAndStart(host, GuestConn);
            Assert.That(host.BeginRace(), Is.True);

            var result1 = FinishedResult("s-1", "s-1-r1",
                "p-host", "dnf", "p-guest", "finished");
            Assert.That(host.PublishResult(result1), Is.True);
            Assert.That(DeliveryOf(host.Snapshot(),
                result1.resultId, "p-guest").state, Is.EqualTo("pending"));
            var fingerprint1 = Retained(host, result1.resultId).Fingerprint();

            // A transient link drop leaves the row pending — the slot is
            // reserved and the guest may credentialed rejoin to receive it.
            host.OnLinkDisconnected(GuestConn, "lost");
            link.DrainSent();
            Assert.That(DeliveryOf(host.Snapshot(),
                result1.resultId, "p-guest").state, Is.EqualTo("pending"),
                "a disconnect must not settle the pending delivery");

            guest.OnLinkDisconnected(HostConn, "lost");
            Assert.That(guest.Execute(Cmd("lan.reconnect", null)).status,
                Is.EqualTo(CommandResult.Accepted));
            host.OnLinkConnected(GuestConn2);
            guest.OnLinkConnected(HostConn);
            for (var i = 0; i < 4; i++)
                PumpWire(link, guestLink, host, guest, GuestConn2, HostConn);

            Assert.That(DeliveryOf(host.Snapshot(),
                result1.resultId, "p-guest").state, Is.EqualTo("persisted"),
                "the pending row must redeliver across a credentialed rejoin");
            Assert.That(Retained(host, result1.resultId).Fingerprint(),
                Is.EqualTo(fingerprint1),
                "delivery lifecycle must never rewrite the immutable result");

            // A second result while the guest is still connected; its
            // offers are deliberately not delivered so the row is still
            // pending when the participant leaves for good.
            var result2 = FinishedResult("s-1", "s-1-r2",
                "p-host", "finished", "p-guest", "finished");
            Assert.That(host.PublishResult(result2), Is.True);
            link.DrainSent();
            guestLink.DrainSent();
            Assert.That(DeliveryOf(host.Snapshot(),
                result2.resultId, "p-guest").state, Is.EqualTo("pending"));
            var fingerprint2 = Retained(host, result2.resultId).Fingerprint();

            Assert.That(guest.Execute(Cmd("lan.leave", null)).status,
                Is.EqualTo(CommandResult.Applied));
            guestLink.DeliverTo(host, GuestConn2);

            var settled = DeliveryOf(host.Snapshot(),
                result2.resultId, "p-guest");
            Assert.That(settled.state, Is.EqualTo("failed"),
                "explicit leave settles outstanding delivery as terminal");
            Assert.That(settled.code, Is.EqualTo("undeliverable"));
            Assert.That(Retained(host, result2.resultId).deliveryStatus,
                Is.EqualTo("failed"));
            Assert.That(DeliveryOf(host.Snapshot(),
                result1.resultId, "p-guest").state, Is.EqualTo("persisted"),
                "persisted history must not be rewritten by the departure");
            Assert.That(Retained(host, result2.resultId).Fingerprint(),
                Is.EqualTo(fingerprint2),
                "the immutable result fingerprint survives the verdict");

            // Terminal means terminal: no redelivery cadence revives it.
            host.AdvanceWallTime(
                NetworkSettings.ResultRedeliverySeconds + 1.0);
            foreach (var pair in link.DrainPairs())
            {
                string type;
                Dictionary<string, string> fields;
                Assert.That(
                    !NetworkProtocol.TryDecode(pair.Value, out type, out fields)
                    || type != NetworkProtocol.MsgResultOffer, Is.True,
                    "an undeliverable row must never be re-offered");
            }
        }

        [Test]
        public void ReconnectKeepsSameRacerAndProgress()
        {
            var guestLink = new LanSessionTests.FakeLanLink();
            var guest = NewGuest(guestLink, "p-guest");
            JoinGuest(host, link, guest, guestLink, GuestConn, HostConn);
            ConfigureCar(host);
            ReadyAndStart(host, GuestConn);
            Assert.That(host.BeginRace(), Is.True);
            link.DrainSent();
            var slotBefore = -1;
            var before = Find(host.Snapshot(), "p-guest");
            Assert.That(before, Is.Not.Null);
            slotBefore = before.slot;

            // The connection drops mid-race; the slot is reserved.
            host.OnLinkDisconnected(GuestConn, "lost");
            var reserved = Find(host.Snapshot(), "p-guest");
            Assert.That(reserved.connected, Is.False);
            Assert.That(reserved.controlSource, Is.EqualTo("ai"));

            // Same process: the client noticed its link died, enters
            // recovery, and reconnects on the stored credential.
            guest.OnLinkDisconnected(HostConn, "lost");
            Assert.That(guest.Snapshot().phase,
                Is.EqualTo(LanSessionService.PhaseRecovery));
            var rejoin = guest.Execute(Cmd("lan.reconnect", null));
            Assert.That(rejoin.status, Is.EqualTo(CommandResult.Accepted),
                "lan.reconnect rejected: " + rejoin.code);
            host.OnLinkConnected(GuestConn2);
            guest.OnLinkConnected(HostConn);
            PumpWire(link, guestLink, host, guest, GuestConn2, HostConn);

            var after = Find(host.Snapshot(), "p-guest");
            Assert.That(after, Is.Not.Null);
            Assert.That(after.slot, Is.EqualTo(slotBefore),
                "rejoin must return the same reserved slot");
            Assert.That(after.connected, Is.True);
            Assert.That(after.controlSource, Is.EqualTo("remote"),
                "control hands back to the participant at the boundary");
            Assert.That(after.ownershipEpoch, Is.EqualTo(2));
            Assert.That(guest.Snapshot().phase,
                Is.EqualTo(LanSessionService.PhaseRacing));

            // Session state broadcasts never carry the credential.
            link.DrainSent();
            Assert.That(host.Execute(Cmd("lan.pause.set",
                P("paused", "true"))).status, Is.EqualTo(CommandResult.Applied));
            foreach (var pair in link.DrainPairs())
            {
                string type;
                Dictionary<string, string> fields;
                if (!NetworkProtocol.TryDecode(pair.Value, out type, out fields))
                    continue;
                if (type == NetworkProtocol.MsgSessionState)
                    Assert.That(fields.ContainsKey("cred"), Is.False,
                        "reconnect credentials must never ride session state");
            }
        }

        // Coordinator-level half of the same contract: a human->AI->human
        // handover keeps the racer's session and progress — it is never
        // rebuilt from spawn.
        [Test]
        public void ControlHandoverPreservesRacerProgress()
        {
            var race = CreateLifecycleRace();
            var inputs = new Dictionary<string, DriverInput>(StringComparer.Ordinal);
            var state = race.Snapshot();
            foreach (var r in state.racers) inputs[r.participantId] = Neutral;
            // Run past the countdown and some running ticks with steering.
            for (var i = 0; i < 40; i++)
            {
                inputs["p-guest"] = new DriverInput(0.6f, false, false,
                    false, false, false, false);
                state = race.Step(inputs, new FlatWorld());
            }
            var racer = RacerOf(state, "p-guest");
            Assert.That(racer.session.phase, Is.EqualTo("running"));
            var tickBefore = racer.session.simulationTick;
            var distBefore = DistanceFromSpawn(racer, 1);

            // Disconnect: control passes to the AI driver.
            Assert.That(race.SetControlSource("p-guest", "ai"), Is.True);
            for (var i = 0; i < 30; i++)
                state = race.Step(inputs, new FlatWorld());
            racer = RacerOf(state, "p-guest");
            Assert.That(racer.attribution, Is.EqualTo("ai"));
            Assert.That(racer.session.simulationTick,
                Is.GreaterThan(tickBefore),
                "the reserved racer's session keeps advancing under AI");

            // Rejoin: control returns at a host tick boundary; progress is
            // the same session's, never a respawn.
            Assert.That(race.SetControlSource("p-guest", "remote"), Is.True);
            for (var i = 0; i < 10; i++)
                state = race.Step(inputs, new FlatWorld());
            racer = RacerOf(state, "p-guest");
            Assert.That(racer.attribution, Is.EqualTo("human"));
            Assert.That(racer.participantId, Is.EqualTo("p-guest"));
            Assert.That(racer.session.simulationTick,
                Is.GreaterThan(tickBefore),
                "a reclaimed racer keeps its accumulated session progress");
            Assert.That(DistanceFromSpawn(racer, 1),
                Is.GreaterThanOrEqualTo(distBefore * 0.5f),
                "reclaim must never respawn the racer at the grid");
        }

        [Test]
        public void OldEpochCannotControlReclaimedSlot()
        {
            var buffer = new HostInputBuffer();
            buffer.BeginRace("s-1", "s-1-r1");
            buffer.SeatParticipant("p-1", 1);
            string error;
            Assert.That(buffer.TryEnqueue(
                Frame("p-1", 1, steering: 1f, epoch: 1), "p-1", out error),
                Is.True, error);
            var held = buffer.Consume("p-1", 1);
            Assert.That(held.Steering, Is.EqualTo(1f).Within(0.0001f));

            // Credentialed rejoin: the host fences the lane to epoch 2 —
            // queue, held input, and the sequence baseline purge.
            buffer.SetOwnershipEpoch("p-1", 2);
            Assert.That(buffer.TryEnqueue(
                Frame("p-1", 2, steering: 1f, epoch: 1), "p-1", out error),
                Is.False);
            Assert.That(error, Is.EqualTo("staleEpoch"),
                "old-epoch input must never steer a reclaimed racer");
            held = buffer.Consume("p-1", 2);
            Assert.That(held.Steering, Is.EqualTo(0f).Within(0.0001f),
                "pre-reclaim held input must not leak across the epoch fence");

            // The rejoined process restarted its stream at sequence 1:
            // fresh-epoch input under a low sequence is valid again.
            Assert.That(buffer.TryEnqueue(
                Frame("p-1", 1, steering: -1f, epoch: 2), "p-1", out error),
                Is.True, error);
            held = buffer.Consume("p-1", 3);
            Assert.That(held.Steering, Is.EqualTo(-1f).Within(0.0001f));
        }

        [Test]
        public void RejoinBeforeTimeoutSupersedesOldConnection()
        {
            host.OnLinkConnected(GuestConn);
            host.OnLinkMessage(GuestConn, LanSessionTests.JoinRequest("p-1"));
            var credential = CaptureCredential(link, GuestConn);
            Assert.That(credential, Is.Not.Null.And.Not.Empty);
            ConfigureCar(host);
            ReadyAndStart(host, GuestConn);
            Assert.That(host.BeginRace(), Is.True);
            Assert.That(host.ParticipantIdFor(GuestConn), Is.EqualTo("p-1"));

            // The client's replacement process reconnects while its old
            // socket is still open (before any timeout): the new connection
            // takes the slot and the old one is fenced off.
            host.OnLinkConnected(GuestConn2);
            host.OnLinkMessage(GuestConn2,
                LanSessionTests.JoinRequest("p-1", credential: credential));

            Assert.That(host.ParticipantIdFor(GuestConn2), Is.EqualTo("p-1"),
                "the new connection must own the reclaimed participant");
            Assert.That(host.ParticipantIdFor(GuestConn), Is.Null,
                "the superseded connection must lose ownership entirely");

            var closedToOld = false;
            string acceptEpoch = null;
            foreach (var pair in link.DrainPairs())
            {
                string type;
                Dictionary<string, string> fields;
                if (!NetworkProtocol.TryDecode(pair.Value, out type, out fields))
                    continue;
                if (pair.Key == GuestConn && type == NetworkProtocol.MsgSessionClosed
                    && fields.TryGetValue("rsn", out var rsn) && rsn == "superseded")
                    closedToOld = true;
                if (type == NetworkProtocol.MsgJoinAccept)
                    acceptEpoch = fields["ep"];
            }
            Assert.That(closedToOld, Is.True,
                "the fenced connection must be told the session closed for it");
            Assert.That(acceptEpoch, Is.EqualTo("2"));
            var roster = host.Snapshot();
            Assert.That(Find(roster, "p-1").ownershipEpoch, Is.EqualTo(2));
            Assert.That(Find(roster, "p-1").connected, Is.True);
        }

        // --- shared pause authority ------------------------------------------

        [Test]
        public void ClientCannotResumeHostPause()
        {
            var guestLink = new LanSessionTests.FakeLanLink();
            var guest = NewGuest(guestLink, "p-guest");
            JoinGuest(host, link, guest, guestLink, GuestConn, HostConn);
            ConfigureCar(host);
            ReadyAndStart(host, GuestConn);
            Assert.That(host.BeginRace(), Is.True);

            Assert.That(host.Execute(Cmd("lan.pause.set",
                P("paused", "true"))).status, Is.EqualTo(CommandResult.Applied));
            PumpWire(link, guestLink, host, guest, GuestConn, HostConn);
            Assert.That(guest.Snapshot().paused, Is.True,
                "the shared pause must reach the client");

            // A client cannot set the shared pause state at all.
            var set = guest.Execute(Cmd("lan.pause.set", P("paused", "false")));
            Assert.That(set.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(set.code, Is.EqualTo("hostOnly"));

            // pause.toggle on a client is only ever a request the host sees.
            var toggle = guest.Execute(Cmd("pause.toggle", null));
            Assert.That(toggle.status, Is.EqualTo(CommandResult.Accepted));
            PumpWire(link, guestLink, host, guest, GuestConn, HostConn);
            Assert.That(host.Snapshot().pauseRequests,
                Does.Contain("p-guest"),
                "the client's request must be visible to the host");
            Assert.That(host.Snapshot().paused, Is.True,
                "a client request can never resume a host pause");
            PumpWire(link, guestLink, host, guest, GuestConn, HostConn);
            Assert.That(guest.Snapshot().paused, Is.True,
                "the client stays paused until the host decides otherwise");

            Assert.That(host.Execute(Cmd("lan.pause.set",
                P("paused", "false"))).status, Is.EqualTo(CommandResult.Applied));
            PumpWire(link, guestLink, host, guest, GuestConn, HostConn);
            Assert.That(guest.Snapshot().paused, Is.False);
        }

        // --- host loss --------------------------------------------------------

        [Test]
        public void HostLossNeverProducesCompletionReward()
        {
            var guestLink = new LanSessionTests.FakeLanLink();
            var guest = NewGuest(guestLink, "p-guest");
            JoinGuest(host, link, guest, guestLink, GuestConn, HostConn);
            ConfigureCar(host);
            ReadyAndStart(host, GuestConn);
            Assert.That(host.BeginRace(), Is.True);

            // The host closes the session (or dies): the client lands in
            // the localized recovery state and no path can mint a result.
            Assert.That(host.Execute(Cmd("lan.leave", null)).status,
                Is.EqualTo(CommandResult.Applied));
            PumpWire(link, guestLink, host, guest, GuestConn, HostConn);

            var snap = guest.Snapshot();
            Assert.That(snap.phase, Is.EqualTo(LanSessionService.PhaseRecovery),
                "host loss must put the client in recovery, not results");
            Assert.That(snap.lastError, Is.EqualTo("hostClosed"));
            Assert.That(snap.descriptor.joinable, Is.False);
            foreach (var p in snap.participants)
                Assert.That(p.connected, Is.False,
                    "no participant stays connected after host loss");

            // Nothing on the client can produce or fetch a result: no
            // migration and no inferred final result exists.
            Assert.That(guest.Execute(Cmd("lan.results.request",
                P("raceId", "any"))).status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(guest.Execute(Cmd("lan.results.retrySave", null)).status,
                Is.EqualTo(CommandResult.Rejected));
            Assert.That(snap.phase,
                Is.Not.EqualTo(LanSessionService.PhaseResults));
        }

        // --- reservation theft -----------------------------------------------

        [Test]
        public void NewParticipantCannotStealInRaceReservation()
        {
            host.OnLinkConnected(GuestConn);
            host.OnLinkMessage(GuestConn, LanSessionTests.JoinRequest("p-1"));
            ConfigureCar(host);
            ReadyAndStart(host, GuestConn);
            Assert.That(host.BeginRace(), Is.True);
            host.OnLinkDisconnected(GuestConn, "lost");
            link.DrainSent();

            // A stranger presenting the same participant/profile id — with
            // no credential or a forged one — never takes the reserved slot.
            host.OnLinkConnected(GuestConn2);
            host.OnLinkMessage(GuestConn2, LanSessionTests.JoinRequest("p-1"));
            Assert.That(RejectCode(link), Is.EqualTo("raceInProgress"));

            host.OnLinkConnected(21);
            host.OnLinkMessage(21,
                LanSessionTests.JoinRequest("p-1", credential: "forged"));
            Assert.That(RejectCode(link), Is.EqualTo("raceInProgress"));

            var guest = Find(host.Snapshot(), "p-1");
            Assert.That(guest, Is.Not.Null);
            Assert.That(guest.connected, Is.False,
                "the reservation stays intact for its owner");
            Assert.That(guest.controlSource, Is.EqualTo("ai"));
            Assert.That(guest.ownershipEpoch, Is.EqualTo(1),
                "failed steals must not bump the epoch");
        }

        // --- credential store seam ---------------------------------------------

        [Test]
        public void ExplicitLeaveRemovesStoredCredential()
        {
            var guestLink = new LanSessionTests.FakeLanLink();
            var store = new MemoryCredentialStore();
            var guest = NewGuest(guestLink, "p-guest", store);
            JoinGuest(host, link, guest, guestLink, GuestConn, HostConn);

            Assert.That(store.SaveCalls, Is.GreaterThanOrEqualTo(1),
                "the credential must persist privately after join");
            Assert.That(store.Stored, Is.Not.Null);
            Assert.That(store.Stored.participantId, Is.EqualTo("p-guest"));
            Assert.That(store.Stored.credential, Is.Not.Null.And.Not.Empty);

            Assert.That(guest.Execute(Cmd("lan.leave", null)).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(store.ClearCalls, Is.GreaterThanOrEqualTo(1),
                "explicit leave removes the local credential");
            Assert.That(store.Stored, Is.Null);
            Assert.That(guest.Execute(Cmd("lan.reconnect", null)).status,
                Is.EqualTo(CommandResult.Rejected));
        }

        [Test]
        public void RestartedClientReconnectsFromCredentialStore()
        {
            var store = new MemoryCredentialStore();
            var guestLink = new LanSessionTests.FakeLanLink();
            var guest = NewGuest(guestLink, "p-guest", store);
            JoinGuest(host, link, guest, guestLink, GuestConn, HostConn);
            ConfigureCar(host);
            ReadyAndStart(host, GuestConn);
            Assert.That(host.BeginRace(), Is.True);

            // The process dies mid-race: the host notices the drop and
            // reserves the slot; the fresh process shares only the store.
            host.OnLinkDisconnected(GuestConn, "processLost");
            var guestLink2 = new LanSessionTests.FakeLanLink();
            var restarted = NewGuest(guestLink2, "p-guest", store);

            Assert.That(restarted.Execute(Cmd("lan.reconnect", null)).status,
                Is.EqualTo(CommandResult.Accepted),
                "a stored credential must survive process restart");
            host.OnLinkConnected(GuestConn2);
            restarted.OnLinkConnected(HostConn);
            PumpWire(link, guestLink2, host, restarted, GuestConn2, HostConn);

            var snap = restarted.Snapshot();
            Assert.That(snap.phase, Is.EqualTo(LanSessionService.PhaseRacing),
                "rejoin during a live race lands back in the race");
            var after = Find(host.Snapshot(), "p-guest");
            Assert.That(after.connected, Is.True);
            Assert.That(after.controlSource, Is.EqualTo("remote"));
            Assert.That(after.ownershipEpoch, Is.EqualTo(2));
            Assert.That(store.SaveCalls, Is.GreaterThanOrEqualTo(2),
                "the rotated credential must be re-persisted on reclaim");
        }

        [Test]
        public void ReconnectUnavailableWithoutProfileOrCredential()
        {
            var guestLink = new LanSessionTests.FakeLanLink();
            var guest = LanSessionTests.StandaloneService(guestLink,
                new LanSessionTests.FakeLanProbe(), "p-guest");
            bool available;
            string reason;
            guest.Availability("lan.reconnect", out available, out reason);
            Assert.That(available, Is.False);
            Assert.That(reason, Is.EqualTo("noSession"));
            Assert.That(guest.Execute(Cmd("lan.reconnect", null)).status,
                Is.EqualTo(CommandResult.Rejected));
        }

        // --- HostInputBuffer pause/control-loss clearing -----------------------

        [Test]
        public void HeldInputClearsOnPauseAndResumeNeedsFreshInput()
        {
            var buffer = new HostInputBuffer();
            buffer.BeginRace("s-1", "s-1-r1");
            buffer.SeatParticipant("p-1", 1);
            string error;
            Assert.That(buffer.TryEnqueue(
                Frame("p-1", 1, steering: 1f), "p-1", out error), Is.True);
            var held = buffer.Consume("p-1", 1);
            Assert.That(held.Steering, Is.EqualTo(1f).Within(0.0001f));

            buffer.SetPaused(true);
            // Held input neutralizes on pause entry — the steering the
            // player was holding must not seep into the resume tick.
            held = buffer.Consume("p-1", 2);
            Assert.That(held.Steering, Is.EqualTo(0f).Within(0.0001f),
                "held input must clear the moment the race pauses");

            // Input arriving during pause keeps the stream sequence
            // advancing but can never become held input.
            Assert.That(buffer.TryEnqueue(
                Frame("p-1", 2, steering: -1f), "p-1", out error), Is.True);
            held = buffer.Consume("p-1", 3);
            Assert.That(held.Steering, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(buffer.AckFor("p-1").inputSequence, Is.EqualTo(2),
                "paused consumption still acknowledges the stream");

            buffer.SetPaused(false);
            // Resume requires fresh input: nothing paused-era may be held.
            held = buffer.Consume("p-1", 4);
            Assert.That(held.Steering, Is.EqualTo(0f).Within(0.0001f));
            Assert.That(buffer.TryEnqueue(
                Frame("p-1", 3, steering: -0.5f), "p-1", out error), Is.True);
            held = buffer.Consume("p-1", 5);
            Assert.That(held.Steering, Is.EqualTo(-0.5f).Within(0.0001f));
        }

        [Test]
        public void ControlLossReleasesLaneAndReturnsPendingActions()
        {
            var buffer = new HostInputBuffer();
            buffer.BeginRace("s-1", "s-1-r1");
            buffer.SeatParticipant("p-1", 1);
            string error;
            var frame = Frame("p-1", 1, steering: 1f);
            frame.actionIds.Add("act-1");
            frame.actionIds.Add("act-2");
            Assert.That(buffer.TryEnqueue(frame, "p-1", out error), Is.True);
            buffer.Consume("p-1", 1);

            var released = buffer.ReleaseLane("p-1");
            Assert.That(released, Is.EquivalentTo(new[] { "act-1", "act-2" }),
                "queued one-shots must come back for explicit cancellation");
            Assert.That(buffer.QueueDepth("p-1"), Is.EqualTo(0));
            Assert.That(buffer.Consume("p-1", 2).Steering,
                Is.EqualTo(0f).Within(0.0001f));
        }

        // --- terminal-phase pause guard (Task 3 deferred) ---------------------

        [Test]
        public void PauseOnTerminalRaceEmitsNoLifecycleEvents()
        {
            var race = CreateLifecycleRace();
            var inputs = new Dictionary<string, DriverInput>(StringComparer.Ordinal);
            var state = race.Snapshot();
            foreach (var r in state.racers) inputs[r.participantId] = Neutral;
            for (var i = 0; i < 10; i++)
                state = race.Step(inputs, new FlatWorld());

            var events = new List<DrivingEvent>();
            race.SetPaused(true);
            state = race.Step(inputs, new FlatWorld());
            events.AddRange(state.events);
            Assert.That(events.Exists(e => e.kind == "race.paused"), Is.True,
                "a live race must still announce its pause");

            // Once aborted, pause/resume flips are presentation no-ops and
            // must not mint lifecycle events on a dead race.
            race.Abort("test");
            state = race.Step(inputs, new FlatWorld());
            events.Clear();
            events.AddRange(state.events);
            race.SetPaused(false);
            race.SetPaused(true);
            state = race.Step(inputs, new FlatWorld());
            events.AddRange(state.events);
            Assert.That(events.Exists(e => e.kind == "race.paused"), Is.False,
                "a terminal race must not emit pause events");
            Assert.That(events.Exists(e => e.kind == "race.resumed"), Is.False,
                "a terminal race must not emit resume events");
        }

        // The published `prediction` slice is contract-gated to a live
        // client LAN race (FoundationState.cs): null on the host and
        // outside a race. A live benchmark scenario (2026-10-14) caught a
        // stale slice on a `lan.leave` teardown boundary — the bridge
        // clears its own facts on its next pump, one boundary after the
        // session transition — so Snapshot() gates the slice on the
        // record's own role/race facts, never on push timing.
        [Test]
        public void PredictionSliceNeverPublishesOutsideClientRace()
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "family-racing-pred-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            try
            {
                var guestLink = new LanSessionTests.FakeLanLink();
                var guest = NewGuest(guestLink, "p-guest");
                var guestService =
                    new FamilyRacing.Core.Foundation.FoundationService(
                        new ProfileStore(directory,
                            new LanSessionTests.LanTestJsonSerializer()),
                        "en", "lan-session-test");
                guestService.SetDrivingContent(LanSessionTests.LanCatalog());
                guestService.SetLanSession(guest);
                JoinGuest(host, link, guest, guestLink, GuestConn, HostConn);

                var facts = new NetworkPredictionState
                {
                    participantId = "p-guest",
                    active = true
                };
                // In-lobby client leg: facts pushed, no race yet — the
                // slice must stay unpublished.
                guestService.SetPredictionState(facts);
                var lobby = guestService.Snapshot();
                Assert.That(lobby.network.role,
                    Is.EqualTo(LanSessionService.RoleClient));
                Assert.That(lobby.prediction, Is.Null,
                    "prediction leaked while client had no race");

                // Client + pushed race state: the slice publishes.
                guestService.SetNetworkRaceState(new NetworkRaceState
                {
                    sessionId = "s", raceId = "r1", phase = "running"
                });
                Assert.That(guestService.Snapshot().prediction,
                    Is.Not.Null, "client in a race must publish prediction");

                // The regressed boundary: lan.leave clears the role
                // immediately while the pushed facts stay stale — the
                // record must still never claim prediction.
                Assert.That(guest.Execute(Cmd("lan.leave", null)).status,
                    Is.EqualTo(CommandResult.Applied));
                var left = guestService.Snapshot();
                Assert.That(left.network.role,
                    Is.EqualTo(LanSessionService.RoleNone));
                Assert.That(left.prediction, Is.Null,
                    "teardown boundary published a stale prediction slice");

                // Host leg: role=host never predicts, even with both
                // facts pushed.
                var hostService =
                    new FamilyRacing.Core.Foundation.FoundationService(
                        new ProfileStore(directory,
                            new LanSessionTests.LanTestJsonSerializer()),
                        "en", "lan-session-test");
                hostService.SetDrivingContent(LanSessionTests.LanCatalog());
                hostService.SetLanSession(host);
                hostService.SetPredictionState(facts);
                hostService.SetNetworkRaceState(new NetworkRaceState
                {
                    sessionId = "s", raceId = "r1", phase = "running"
                });
                var hostSnap = hostService.Snapshot();
                Assert.That(hostSnap.network.role,
                    Is.EqualTo(LanSessionService.RoleHost));
                Assert.That(hostSnap.prediction, Is.Null,
                    "the host must never publish a prediction slice");
            }
            finally
            {
                if (System.IO.Directory.Exists(directory))
                    System.IO.Directory.Delete(directory, true);
            }
        }

        // --- solo-blocked lifecycle ----------------------------------------------

        [Test]
        public void RaceExitRejectedDuringLanSession()
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "family-racing-life-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            try
            {
                // runId must match the stamp LanSessionTests.Cmd puts on
                // commands — a foreign runId is rejected before dispatch.
                var service = new FamilyRacing.Core.Foundation.FoundationService(
                    new ProfileStore(directory,
                        new LanSessionTests.LanTestJsonSerializer()),
                    "en", "lan-session-test");
                service.SetDrivingContent(LanSessionTests.LanCatalog());
                var lanLink = new LanSessionTests.FakeLanLink();
                var lan = new LanSessionService(lanLink,
                    new LanSessionTests.FakeLanProbe(), "gameplay-test");
                service.SetLanSession(lan);
                Assert.That(service.Execute(Cmd("profile.create",
                    P("slot", "0", "language", "en"))).status,
                    Is.EqualTo(CommandResult.Applied));
                Assert.That(service.Execute(Cmd("lan.host",
                    P("port", "47777"))).status,
                    Is.EqualTo(CommandResult.Applied));

                var exit = service.Execute(Cmd("race.exit", null));
                Assert.That(exit.status, Is.EqualTo(CommandResult.Rejected));
                Assert.That(exit.code, Is.EqualTo("networkActive"),
                    "solo lifecycle commands must not run inside a LAN session");
            }
            finally
            {
                if (System.IO.Directory.Exists(directory))
                    System.IO.Directory.Delete(directory, true);
            }
        }

        // --- tiny race fixture --------------------------------------------------

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

        private static DrivingContentCatalog LifecycleCatalog()
        {
            var course = new CourseDefinition
            {
                id = "course-life",
                checkpoints = new List<CheckpointGate>
                {
                    new CheckpointGate
                    {
                        id = "start", x = 0f, y = 0f, z = 0f,
                        halfWidth = 12f, dirX = 0f, dirZ = 1f,
                        recoverX = 0f, recoverZ = -3f
                    },
                    new CheckpointGate
                    {
                        id = "gate-1", x = 0f, y = 0f, z = 2000f,
                        halfWidth = 12f, dirX = 0f, dirZ = 1f,
                        recoverX = 0f, recoverZ = 1997f
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
            var evt = new EventDefinition
            {
                id = "lan-car-life",
                courseId = "course-life",
                laps = 1,
                countdownTicks = 10,
                vehicleIds = new List<string> { "car-01", "motorcycle-01" }
            };
            return DrivingContentCatalog.Create("lan-life-1",
                new[] { VehicleTuning.Car01(), VehicleTuning.Motorcycle01() },
                new[] { course }, new[] { evt });
        }

        private static NetworkRace CreateLifecycleRace()
        {
            var specs = new List<NetworkRacerSpec>
            {
                new NetworkRacerSpec
                {
                    slot = 0, participantId = "p-host",
                    vehicleId = "car-01", controlSource = "local"
                },
                new NetworkRacerSpec
                {
                    slot = 1, participantId = "p-guest",
                    vehicleId = "car-01", controlSource = "remote"
                },
                new NetworkRacerSpec
                {
                    slot = 2, participantId = "ai-2",
                    vehicleId = "car-01", controlSource = "ai"
                },
                new NetworkRacerSpec
                {
                    slot = 3, participantId = "ai-3",
                    vehicleId = "car-01", controlSource = "ai"
                }
            };
            NetworkRace race;
            string error;
            Assert.That(NetworkRace.TryCreate("s-1", "s-1-r1",
                LifecycleCatalog(), "lan-car-life", "car", specs,
                out race, out error), Is.True, "TryCreate: " + error);
            return race;
        }

        private static NetworkRacerState RacerOf(NetworkRaceState state,
            string participantId)
        {
            foreach (var racer in state.racers)
                if (racer.participantId == participantId) return racer;
            return null;
        }

        private static float DistanceFromSpawn(NetworkRacerState racer, int slot)
        {
            var spawnX = -6f + slot * 4f; // spawnSlots x values above
            var dx = racer.session.motor.x - spawnX;
            var dz = racer.session.motor.z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }
    }
}
