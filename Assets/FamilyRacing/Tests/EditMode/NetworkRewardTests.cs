using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
#if UNITY_5_3_OR_NEWER
using UnityEngine;
#else
using System.Text.Json;
#endif

namespace FamilyRacing.Tests
{
    // Task 8: immutable results + once-only completion coins. The reward
    // service, schema migration, and delivery/ack policy are proven at the
    // Unity-free Core level: real temp-file ProfileStores stand in for
    // process restarts (a new store instance on the same directory is a
    // restart at the persistence level), and the fake link pair proves the
    // wire redelivery policy payload-for-payload.
    public sealed class NetworkRewardTests
    {
        private const string HostPid = "33333333-3333-4333-8333-333333333333";
        private const string GuestPid = "44444444-4444-4444-8444-444444444444";

        private string directory;
        private IProfileSerializer serializer;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "family-racing-reward-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            serializer = new TestJsonSerializer();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        // --- required: once-only reward across restarts ---------------------

        [Test]
        public void DuplicateResultAfterRestartAwardsOnce()
        {
            var store = Store();
            var profile = ValidProfile(0, 7);
            store.Save(profile);
            var loaded = store.Load(0).Profile;
            var result = FinishedResult("s-1", "s-1-r1",
                loaded.id, "finished", "other-p", "dnf");

            var first = RaceRewardService.Apply(result, loaded.id, loaded, store);
            Assert.That(first.status, Is.EqualTo(RaceRewardService.StatusApplied));
            Assert.That(first.awardedCoins, Is.EqualTo(20));
            Assert.That(loaded.coins, Is.EqualTo(27));
            Assert.That(loaded.appliedResults.Count, Is.EqualTo(1));
            var receipt = loaded.appliedResults[0];
            Assert.That(receipt.resultId, Is.EqualTo(result.resultId));
            Assert.That(receipt.participantId, Is.EqualTo(loaded.id));
            Assert.That(receipt.awardedCoins, Is.EqualTo(20));
            Assert.That(receipt.fingerprint, Is.EqualTo(result.Fingerprint()));

            // Process restart: a brand-new store on the same directory
            // reloads the persisted receipt. Redelivery must never award
            // twice — not in memory, not on disk.
            var restartedStore = Store();
            var reloaded = restartedStore.Load(0).Profile;
            Assert.That(reloaded.coins, Is.EqualTo(27));
            Assert.That(reloaded.appliedResults.Count, Is.EqualTo(1));
            Assert.That(reloaded.schemaVersion, Is.EqualTo(2));

            var again = RaceRewardService.Apply(
                result, reloaded.id, reloaded, restartedStore);
            Assert.That(again.status,
                Is.EqualTo(RaceRewardService.StatusAlreadyApplied));
            Assert.That(again.awardedCoins, Is.EqualTo(20));
            Assert.That(reloaded.coins, Is.EqualTo(27));
            Assert.That(reloaded.appliedResults.Count, Is.EqualTo(1));

            var third = RaceRewardService.Apply(
                result, reloaded.id, reloaded, restartedStore);
            Assert.That(third.status,
                Is.EqualTo(RaceRewardService.StatusAlreadyApplied));
            Assert.That(Store().Load(0).Profile.coins, Is.EqualTo(27));
            Assert.That(Store().Load(0).Profile.appliedResults.Count,
                Is.EqualTo(1), "exactly one receipt survives every replay");
        }

        [Test]
        public void ConflictingReplayAfterRestartIsRejected()
        {
            var store = Store();
            var profile = ValidProfile(0, 7);
            store.Save(profile);
            var loaded = store.Load(0).Profile;
            var result = FinishedResult("s-1", "s-1-r1",
                loaded.id, "finished", "other-p", "dnf");
            Assert.That(RaceRewardService.Apply(
                result, loaded.id, loaded, store).status,
                Is.EqualTo(RaceRewardService.StatusApplied));

            var restartedStore = Store();
            var reloaded = restartedStore.Load(0).Profile;
            var conflicting = FinishedResult("s-1", "s-1-r1",
                loaded.id, "finished", "other-p", "dnf");
            // Same result id, altered immutable fact: the stored fingerprint
            // proves this is not the result that was persisted.
            conflicting.completionCoins = 40;
            conflicting.racers[0].coinsAwarded = 40;

            var replay = RaceRewardService.Apply(
                conflicting, reloaded.id, reloaded, restartedStore);
            Assert.That(replay.status, Is.EqualTo(RaceRewardService.StatusFailed));
            Assert.That(replay.code, Is.EqualTo("conflict"));
            Assert.That(reloaded.coins, Is.EqualTo(27),
                "a conflicting replay must never move coins");
            Assert.That(reloaded.appliedResults.Count, Is.EqualTo(1));
            Assert.That(reloaded.appliedResults[0].fingerprint,
                Is.EqualTo(result.Fingerprint()),
                "the original receipt must not be overwritten");

            // The conflict is durable: reloading again and replaying the
            // same tampered payload fails the same way.
            var fresh = restartedStore.Load(0).Profile;
            var again = RaceRewardService.Apply(
                conflicting, fresh.id, fresh, restartedStore);
            Assert.That(again.status, Is.EqualTo(RaceRewardService.StatusFailed));
            Assert.That(Store().Load(0).Profile.coins, Is.EqualTo(27));
        }

        [Test]
        public void SaveFailureLeavesCoinsAndReceiptUnchanged()
        {
            var store = Store();
            var profile = ValidProfile(0, 7);
            store.Save(profile);
            var onDisk = File.ReadAllText(Primary(0));
            var loaded = store.Load(0).Profile;
            var result = FinishedResult("s-1", "s-1-r1",
                loaded.id, "finished", "other-p", "dnf");

            // A real file lock makes the atomic replace fail mid-save.
            using (new FileStream(Primary(0), FileMode.Open, FileAccess.Read,
                FileShare.Read))
            {
                var failed = RaceRewardService.Apply(
                    result, loaded.id, loaded, store);
                Assert.That(failed.status, Is.EqualTo(RaceRewardService.StatusFailed));
                Assert.That(loaded.coins, Is.EqualTo(7),
                    "a failed save must not report coins it never wrote");
                Assert.That(loaded.appliedResults.Count, Is.EqualTo(0),
                    "a failed save must not report a receipt it never wrote");
                Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo(onDisk));
            }

            // Retry applies the identical immutable result once the write
            // path recovers.
            var retried = RaceRewardService.Apply(
                result, loaded.id, loaded, store);
            Assert.That(retried.status, Is.EqualTo(RaceRewardService.StatusApplied));
            Assert.That(loaded.coins, Is.EqualTo(27));
            Assert.That(loaded.appliedResults.Count, Is.EqualTo(1));
        }

        [Test]
        public void LostAckResendsSameResult()
        {
            var hostDir = Path.Combine(directory, "host");
            var guestDir = Path.Combine(directory, "guest");
            var hostStore = Store(hostDir);
            var guestStore = Store(guestDir);
            var hostProfile = ValidProfile(0, 7, HostPid);
            var guestProfile = ValidProfile(0, 7, GuestPid);
            hostStore.Save(hostProfile);
            guestStore.Save(guestProfile);
            var hostSaved = hostStore.Load(0).Profile;
            var guestSaved = guestStore.Load(0).Profile;

            var hostLink = new LanSessionTests.FakeLanLink();
            var guestLink = new LanSessionTests.FakeLanLink();
            var host = LanSessionTests.StandaloneService(
                hostLink, new LanSessionTests.FakeLanProbe(), HostPid);
            Assert.That(host.Execute(Cmd("lan.host",
                P("port", "47777"))).status, Is.EqualTo("applied"));
            var guest = LanSessionTests.StandaloneService(
                guestLink, new LanSessionTests.FakeLanProbe(), GuestPid);
            host.SetRewardApplier(r =>
                RaceRewardService.Apply(r, HostPid, hostSaved, hostStore));
            guest.SetRewardApplier(r =>
                RaceRewardService.Apply(r, GuestPid, guestSaved, guestStore));
            JoinGuest(host, hostLink, guest, guestLink, 9, 1);

            var result = FinishedResult("s-1", "s-1-r1",
                HostPid, "finished", GuestPid, "finished",
                "ai-slot-2", "finished", "ai-slot-3", "dnf");
            Assert.That(host.PublishResult(result), Is.True);
            var firstOffer = DeliverOffers(hostLink, guest, 1, 9);
            Assert.That(firstOffer.Count, Is.EqualTo(1),
                "publish must offer the result to the guest");
            Assert.That(guestSaved.coins, Is.EqualTo(27),
                "delivery applies the authored reward exactly once");
            Assert.That(guestSaved.appliedResults.Count, Is.EqualTo(1));
            // The host's own reward applied locally at publish time.
            Assert.That(hostSaved.coins, Is.EqualTo(27));

            // Lose the guest's ack: it queued one, but nothing it sent
            // reaches the host.
            Assert.That(ContainsType(guestLink.Sent,
                NetworkProtocol.MsgResultAck), Is.True,
                "the client must acknowledge persistence");
            guestLink.DrainSent();

            // The host resends the identical offer — never a mutated copy.
            host.AdvanceWallTime(
                NetworkSettings.ResultRedeliverySeconds + 0.5);
            var resent = DeliverOffers(hostLink, guest, 1, 9);
            Assert.That(resent.Count, Is.EqualTo(1),
                "an unacknowledged offer must be retransmitted");
            Assert.That(resent[0], Is.EqualTo(firstOffer[0]));

            // Redelivery replays the receipt: no second award, fresh ack.
            Assert.That(guestSaved.coins, Is.EqualTo(27));
            Assert.That(guestSaved.appliedResults.Count, Is.EqualTo(1));
            Assert.That(ContainsType(guestLink.Sent,
                NetworkProtocol.MsgResultAck), Is.True,
                "the redelivery must be re-acknowledged");
            guestLink.DeliverTo(host, 9);

            var delivery = DeliveryOf(host.Snapshot(), result.resultId, GuestPid);
            Assert.That(delivery, Is.Not.Null);
            Assert.That(delivery.state, Is.EqualTo("persisted"));
            Assert.That(guestStore.Load(0).Profile.coins, Is.EqualTo(27));
        }

        [Test]
        public void LocalIneligibleDeliveryCarriesTheVerdictPrefix()
        {
            var hostStore = Store();
            var hostProfile = ValidProfile(0, 7, HostPid);
            hostStore.Save(hostProfile);
            var hostSaved = hostStore.Load(0).Profile;

            var hostLink = new LanSessionTests.FakeLanLink();
            var host = LanSessionTests.StandaloneService(
                hostLink, new LanSessionTests.FakeLanProbe(), HostPid);
            Assert.That(host.Execute(Cmd("lan.host",
                P("port", "47777"))).status, Is.EqualTo("applied"));
            host.SetRewardApplier(r =>
                RaceRewardService.Apply(r, HostPid, hostSaved, hostStore));

            // The host's own racer DNFs: the reward applier answers
            // ineligible, and the local delivery row must record the same
            // permanent verdict the remote ack path reports —
            // "ineligible:dnf", never the bare outcome — so endpoint
            // observations of one delivery agree byte-for-byte.
            var result = FinishedResult("s-1", "s-1-r1",
                HostPid, "dnf", "ai-slot-1", "finished");
            Assert.That(host.PublishResult(result), Is.True);

            var delivery = DeliveryOf(host.Snapshot(), result.resultId, HostPid);
            Assert.That(delivery, Is.Not.Null);
            Assert.That(delivery.state, Is.EqualTo("failed"));
            Assert.That(delivery.code, Is.EqualTo("ineligible:dnf"));
            Assert.That(hostSaved.coins, Is.EqualTo(7));
            Assert.That(hostSaved.appliedResults.Count, Is.EqualTo(0));
        }

        [Test]
        public void DnfAndAbortAwardZero()
        {
            var store = Store();
            var profile = ValidProfile(0, 7);
            store.Save(profile);
            var loaded = store.Load(0).Profile;

            // A DNF racer earns nothing and records no receipt.
            var dnf = FinishedResult("s-1", "s-1-r1",
                loaded.id, "dnf", "other-p", "finished");
            var applied = RaceRewardService.Apply(dnf, loaded.id, loaded, store);
            Assert.That(applied.status,
                Is.EqualTo(RaceRewardService.StatusIneligible));
            Assert.That(loaded.coins, Is.EqualTo(7));
            Assert.That(loaded.appliedResults.Count, Is.EqualTo(0));

            // Aborted/unfinished races produce no reward-bearing result at
            // all: null and unfinalized outcomes are both ineligible.
            Assert.That(RaceRewardService.Apply(
                null, loaded.id, loaded, store).status,
                Is.EqualTo(RaceRewardService.StatusIneligible));
            var unfinished = FinishedResult("s-1", "s-1-r1",
                loaded.id, "finished");
            unfinished.finalized = false;
            Assert.That(RaceRewardService.Apply(
                unfinished, loaded.id, loaded, store).status,
                Is.EqualTo(RaceRewardService.StatusIneligible));
            Assert.That(loaded.coins, Is.EqualTo(7));
            Assert.That(loaded.appliedResults.Count, Is.EqualTo(0));
        }

        [Test]
        public void ResultForOtherProfileRejected()
        {
            var store = Store();
            var profile = ValidProfile(0, 7);
            store.Save(profile);
            var loaded = store.Load(0).Profile;

            // The result carries no row for this profile's identity.
            var foreign = FinishedResult("s-1", "s-1-r1",
                "someone-else", "finished", "another-p", "dnf");
            var applied = RaceRewardService.Apply(
                foreign, loaded.id, loaded, store);
            Assert.That(applied.status,
                Is.EqualTo(RaceRewardService.StatusIneligible));
            Assert.That(loaded.coins, Is.EqualTo(7));
            Assert.That(loaded.appliedResults.Count, Is.EqualTo(0));

            // Claiming another participant's row for this profile is bound
            // to the local identity and refused the same way.
            var stolen = FinishedResult("s-1", "s-1-r2",
                "someone-else", "finished");
            var claim = RaceRewardService.Apply(
                stolen, "someone-else", loaded, store);
            Assert.That(claim.status,
                Is.EqualTo(RaceRewardService.StatusIneligible));
            Assert.That(loaded.coins, Is.EqualTo(7));
            Assert.That(loaded.appliedResults.Count, Is.EqualTo(0));
            Assert.That(Store().Load(0).Profile.coins, Is.EqualTo(7));
        }

        [Test]
        public void SchemaOneMigratesWithoutLosingSettings()
        {
            // A hand-authored schema-1 file: every settings field set, no
            // appliedResults key at all.
            File.WriteAllText(Primary(0),
                "{\"schemaVersion\":1,\"id\":\"11111111-1111-4111-8111-111111111111\"," +
                "\"slot\":0,\"avatar\":\"raccoon\",\"colorIndex\":3,\"language\":\"he\"," +
                "\"camera\":\"overhead\",\"reducedCameraShake\":true,\"musicVolume\":0.1," +
                "\"effectsVolume\":0.2,\"voiceVolume\":0.3,\"coins\":41," +
                "\"unlockedVehicles\":[\"car-01\",\"motorcycle-01\",\"car-02\"]}");
            var raw = File.ReadAllText(Primary(0));

            var loaded = Store().Load(0);
            Assert.That(loaded.Status, Is.EqualTo(ProfileLoadStatus.Loaded));
            var profile = loaded.Profile;
            Assert.That(profile.schemaVersion, Is.EqualTo(2),
                "a schema-1 file migrates in memory on read");
            Assert.That(profile.language, Is.EqualTo("he"));
            Assert.That(profile.camera, Is.EqualTo("overhead"));
            Assert.That(profile.reducedCameraShake, Is.True);
            Assert.That(profile.musicVolume, Is.EqualTo(0.1f));
            Assert.That(profile.effectsVolume, Is.EqualTo(0.2f));
            Assert.That(profile.voiceVolume, Is.EqualTo(0.3f));
            Assert.That(profile.coins, Is.EqualTo(41));
            Assert.That(profile.unlockedVehicles,
                Is.EquivalentTo(new[] { "car-01", "motorcycle-01", "car-02" }));
            Assert.That(profile.appliedResults, Is.Not.Null);
            Assert.That(profile.appliedResults.Count, Is.EqualTo(0));

            // Loading never rewrites the file: the bytes stay schema 1
            // until an explicit successful save.
            Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo(raw));
            Assert.That(File.ReadAllText(Primary(0)),
                Does.Contain("\"schemaVersion\":1"));

            Store().Save(profile);
            var written = File.ReadAllText(Primary(0));
            Assert.That(written, Does.Contain("\"schemaVersion\":2"));
            Assert.That(written, Does.Contain("\"appliedResults\""));
            var reloaded = Store().Load(0);
            Assert.That(reloaded.Status, Is.EqualTo(ProfileLoadStatus.Loaded));
            Assert.That(reloaded.Profile.coins, Is.EqualTo(41));
        }

        [Test]
        public void NewerOrUnreadableSaveIsPreserved()
        {
            // A file from a newer build is never migrated, recovered over,
            // or overwritten by a load.
            var newer = ValidProfile(0, 9);
            newer.schemaVersion = 3;
            File.WriteAllText(Primary(0), serializer.Serialize(newer));
            var newerRaw = File.ReadAllText(Primary(0));
            var blocked = Store().Load(0);
            Assert.That(blocked.Status, Is.EqualTo(ProfileLoadStatus.Blocked));
            Assert.That(blocked.Profile, Is.Null);
            Assert.That(blocked.Diagnostic, Does.Contain("newer"));
            Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo(newerRaw));

            // An unreadable file is preserved byte-for-byte, too.
            File.WriteAllText(Primary(1), "not a profile at all");
            var unreadable = Store().Load(1);
            Assert.That(unreadable.Status, Is.EqualTo(ProfileLoadStatus.Blocked));
            Assert.That(unreadable.Profile, Is.Null);
            Assert.That(File.ReadAllText(Primary(1)),
                Is.EqualTo("not a profile at all"));
        }

        // --- contract details ------------------------------------------------

        [Test]
        public void FingerprintIsDeterministicAcrossLocaleAndDeliveryState()
        {
            var result = FinishedResult("s-1", "s-1-r1",
                "p-a", "finished", "p-b", "dnf");
            var expected = result.Fingerprint();
            Assert.That(expected, Has.Length.EqualTo(64));

            // Locale must not leak into the canonical encoding.
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                Assert.That(result.Fingerprint(), Is.EqualTo(expected));
                System.Globalization.CultureInfo.CurrentCulture =
                    new System.Globalization.CultureInfo("he-IL");
                Assert.That(result.Fingerprint(), Is.EqualTo(expected));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previous;
            }

            // Delivery metadata is never part of the fingerprint.
            result.deliveryStatus = "persisted";
            result.deliveries.Add(new RaceResultDelivery
            {
                participantId = "p-a", state = "persisted", lastOfferedAt = 12.5
            });
            Assert.That(result.Fingerprint(), Is.EqualTo(expected));

            // Any changed immutable fact changes the fingerprint.
            var tampered = result.Copy();
            tampered.racers[0].finishTicks++;
            Assert.That(tampered.Fingerprint(), Is.Not.EqualTo(expected));
            var reordered = result.Copy();
            reordered.racers.Reverse();
            Assert.That(reordered.Fingerprint(), Is.Not.EqualTo(expected),
                "racer order is a fact — reordering must re-key the result");
        }

        [Test]
        public void IntegerOverflowFailsWithoutWrite()
        {
            var store = Store();
            var profile = ValidProfile(0, int.MaxValue);
            store.Save(profile);
            var onDisk = File.ReadAllText(Primary(0));
            var loaded = store.Load(0).Profile;
            var result = FinishedResult("s-1", "s-1-r1",
                loaded.id, "finished");

            var applied = RaceRewardService.Apply(
                result, loaded.id, loaded, store);
            Assert.That(applied.status, Is.EqualTo(RaceRewardService.StatusFailed));
            Assert.That(applied.code, Is.EqualTo("overflow"));
            Assert.That(loaded.coins, Is.EqualTo(int.MaxValue));
            Assert.That(loaded.appliedResults.Count, Is.EqualTo(0));
            Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo(onDisk),
                "overflow must never reach the write path");
        }

        [Test]
        public void ConflictingDuplicateOfferNeverApplies()
        {
            var guestDir = Path.Combine(directory, "guest");
            var guestStore = Store(guestDir);
            var guestProfile = ValidProfile(0, 7, GuestPid);
            guestStore.Save(guestProfile);
            var guestSaved = guestStore.Load(0).Profile;

            var hostLink = new LanSessionTests.FakeLanLink();
            var guestLink = new LanSessionTests.FakeLanLink();
            var host = LanSessionTests.StandaloneService(
                hostLink, new LanSessionTests.FakeLanProbe(), HostPid);
            Assert.That(host.Execute(Cmd("lan.host",
                P("port", "47777"))).status, Is.EqualTo("applied"));
            var guest = LanSessionTests.StandaloneService(
                guestLink, new LanSessionTests.FakeLanProbe(), GuestPid);
            guest.SetRewardApplier(r =>
                RaceRewardService.Apply(r, GuestPid, guestSaved, guestStore));
            JoinGuest(host, hostLink, guest, guestLink, 9, 1);

            // First the good offer lands and persists.
            var result = FinishedResult("s-1", "s-1-r1",
                GuestPid, "finished", "ai-slot-1", "dnf");
            guest.OnLinkMessage(1, NetworkProtocol.EncodeResultOffer(result));
            Assert.That(guestSaved.coins, Is.EqualTo(27));

            // A conflicting replay of the same result id can never move
            // coins or rewrite the stored facts.
            var tampered = FinishedResult("s-1", "s-1-r1",
                GuestPid, "finished", "ai-slot-1", "dnf");
            tampered.completionCoins = 200;
            guest.OnLinkMessage(1, NetworkProtocol.EncodeResultOffer(tampered));
            Assert.That(guestSaved.coins, Is.EqualTo(27));
            Assert.That(guestSaved.appliedResults.Count, Is.EqualTo(1));
            Assert.That(guestSaved.appliedResults[0].fingerprint,
                Is.EqualTo(result.Fingerprint()));
        }

        [Test]
        public void ResultRetrievalSurvivesRematch()
        {
            var guestDir = Path.Combine(directory, "guest");
            var guestStore = Store(guestDir);
            var guestProfile = ValidProfile(0, 7, GuestPid);
            guestStore.Save(guestProfile);
            var guestSaved = guestStore.Load(0).Profile;

            var hostLink = new LanSessionTests.FakeLanLink();
            var guestLink = new LanSessionTests.FakeLanLink();
            var host = LanSessionTests.StandaloneService(
                hostLink, new LanSessionTests.FakeLanProbe(), HostPid);
            Assert.That(host.Execute(Cmd("lan.host",
                P("port", "47777"))).status, Is.EqualTo("applied"));
            var guest = LanSessionTests.StandaloneService(
                guestLink, new LanSessionTests.FakeLanProbe(), GuestPid);
            guest.SetRewardApplier(r =>
                RaceRewardService.Apply(r, GuestPid, guestSaved, guestStore));
            JoinGuest(host, hostLink, guest, guestLink, 9, 1);
            hostLink.DrainSent();
            guestLink.DrainSent();

            // Drive the session into the results phase so the rematch is
            // real: configure, everyone ready, start, race ends with the
            // published result.
            Assert.That(host.Execute(Cmd("lan.configure",
                P("eventId", "lan-mixed", "classId", "car"))).status,
                Is.EqualTo("applied"));
            host.OnLinkMessage(9, NetworkProtocol.Encode(
                NetworkProtocol.MsgReadySet, "cid", "rdy-1", "rdy", "1"));
            Assert.That(host.Execute(Cmd("lan.ready",
                P("ready", "true"))).status, Is.EqualTo("applied"));
            Assert.That(host.Execute(Cmd("lan.start", null)).status,
                Is.EqualTo("applied"));
            Assert.That(host.BeginRace(), Is.True);

            var result = FinishedResult("s-1", "s-1-r1",
                HostPid, "finished", GuestPid, "finished");
            Assert.That(host.PublishResult(result), Is.True);
            Assert.That(host.FinishRace(), Is.True);
            hostLink.DrainSent();
            guestLink.DrainSent();

            // Rematch must not lose the retained result or its delivery
            // state; the guest can still fetch its own result afterwards.
            Assert.That(host.Execute(Cmd("lan.rematch", null)).status,
                Is.EqualTo("applied"));
            hostLink.DrainSent();

            var request = guest.Execute(Cmd("lan.results.request",
                P("raceId", result.raceId)));
            Assert.That(request.status, Is.EqualTo("accepted"),
                "result retrieval must survive the rematch: " + request.code);
            PumpWire(hostLink, guestLink, host, guest, 9, 1);
            Assert.That(guestSaved.coins, Is.EqualTo(27));
            Assert.That(guestSaved.appliedResults.Count, Is.EqualTo(1));
            var outcomes = guest.DrainOutcomes();
            Assert.That(outcomes.Count, Is.EqualTo(1));
            Assert.That(outcomes[0].status, Is.EqualTo("applied"));
        }

        [Test]
        public void RetentionCapRefusesStartButKeepsResults()
        {
            var hostLink = new LanSessionTests.FakeLanLink();
            var host = LanSessionTests.StandaloneService(
                hostLink, new LanSessionTests.FakeLanProbe(), HostPid);
            Assert.That(host.Execute(Cmd("lan.host",
                P("port", "47777"))).status, Is.EqualTo("applied"));

            for (var i = 0; i < NetworkSettings.ResultRetentionCapacity; i++)
            {
                var result = FinishedResult("s-1", "s-1-r" + i,
                    HostPid, "finished");
                Assert.That(host.PublishResult(result), Is.True,
                    "retention must accept result " + i);
            }
            var overflow = FinishedResult("s-1", "s-1-r999",
                HostPid, "finished");
            Assert.That(host.PublishResult(overflow), Is.False,
                "the 129th result must be refused, never silently evicted");

            Assert.That(host.Execute(Cmd("lan.configure",
                P("eventId", "lan-mixed", "classId", "car"))).status,
                Is.EqualTo("applied"));
            Assert.That(host.Execute(Cmd("lan.ready",
                P("ready", "true"))).status, Is.EqualTo("applied"));
            var started = host.Execute(Cmd("lan.start", null));
            Assert.That(started.status, Is.EqualTo("rejected"));
            Assert.That(started.code, Is.EqualTo("sessionFull"),
                "a full session must say so — closing/rehosting is the answer");
            bool available;
            string reason;
            host.Availability("lan.start", out available, out reason);
            Assert.That(available, Is.False);
            Assert.That(reason, Is.EqualTo("sessionFull"));

            // Retained results stay retrievable at the cap.
            Assert.That(host.Execute(Cmd("lan.results.request",
                P("raceId", "s-1-r0"))).status, Is.EqualTo("applied"));
            Assert.That(host.Snapshot().results.Count,
                Is.EqualTo(NetworkSettings.ResultRetentionCapacity),
                "retained results remain visible — nothing was evicted");
        }

        // --- helpers ----------------------------------------------------------

        private ProfileStore Store()
        {
            return new ProfileStore(directory, serializer);
        }

        private ProfileStore Store(string dir)
        {
            return new ProfileStore(dir, serializer);
        }

        private string Primary(int slot)
        {
            return Path.Combine(directory, "profile-" + slot + ".json");
        }

        private static ProfileSave ValidProfile(int slot, int coins,
            string id = "11111111-1111-4111-8111-111111111111")
        {
            return new ProfileSave
            {
                schemaVersion = 2,
                id = id,
                slot = slot,
                avatar = "fox",
                colorIndex = 0,
                language = "en",
                camera = "chase",
                musicVolume = 0.65f,
                effectsVolume = 0.85f,
                voiceVolume = 1f,
                coins = coins,
                unlockedVehicles = new List<string> { "car-01", "motorcycle-01" },
                appliedResults = new List<AppliedResultReceipt>()
            };
        }

        // A finalized immutable result: ordered finish/DNF rows exactly as
        // the race coordinator produces them.
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

        private static GameCommand Cmd(string action,
            Dictionary<string, string> parameters)
        {
            return LanSessionTests.Cmd(action, parameters);
        }

        private static Dictionary<string, string> P(params string[] keyValues)
        {
            return LanSessionTests.P(keyValues);
        }

        private static void JoinGuest(LanSessionService host,
            LanSessionTests.FakeLanLink hostLink,
            LanSessionService guest, LanSessionTests.FakeLanLink guestLink,
            ulong guestConn, ulong hostConn)
        {
            var join = guest.Execute(Cmd("lan.join",
                P("address", "192.168.7.7", "port", "47777")));
            Assert.That(join.status, Is.EqualTo("accepted"),
                "lan.join rejected: " + join.code);
            host.OnLinkConnected(guestConn);
            guest.OnLinkConnected(hostConn);
            PumpWire(hostLink, guestLink, host, guest, guestConn, hostConn);
            Assert.That(guest.Snapshot().phase, Is.EqualTo("lobby"),
                "guest never reached the lobby");
            // The join completion is a side effect of setup, not of the
            // operation under test — absorb it like NetworkCommandTests
            // does so later DrainOutcomes() counts stay honest.
            guest.DrainOutcomes();
        }

        private static void PumpWire(
            LanSessionTests.FakeLanLink hostLink,
            LanSessionTests.FakeLanLink guestLink,
            LanSessionService host, LanSessionService guest,
            ulong guestConn, ulong hostConn)
        {
            guestLink.DeliverTo(host, guestConn);
            hostLink.DeliverTo(guest, hostConn);
        }

        // Drains the fake link, delivers every payload to `guest` as if it
        // came from its server connection, and returns every result.offer
        // payload that was addressed to `toConn`.
        private static List<string> DeliverOffers(
            LanSessionTests.FakeLanLink sent, LanSessionService guest,
            ulong asConn, ulong toConn)
        {
            var offers = new List<string>();
            foreach (var pair in sent.DrainPairs())
            {
                guest.OnLinkMessage(asConn, pair.Value);
                if (pair.Key != toConn) continue;
                string type;
                Dictionary<string, string> fields;
                if (NetworkProtocol.TryDecode(pair.Value, out type, out fields)
                    && type == NetworkProtocol.MsgResultOffer)
                    offers.Add(pair.Value);
            }
            return offers;
        }

        private static bool ContainsType(
            IEnumerable<KeyValuePair<ulong, string>> sent, string wanted)
        {
            foreach (var pair in sent)
            {
                string type;
                Dictionary<string, string> fields;
                if (NetworkProtocol.TryDecode(pair.Value, out type, out fields)
                    && type == wanted)
                    return true;
            }
            return false;
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

        private sealed class TestJsonSerializer : IProfileSerializer
        {
#if UNITY_5_3_OR_NEWER
            public string Serialize(ProfileSave profile)
            {
                return JsonUtility.ToJson(profile);
            }
            public ProfileSave Deserialize(string json)
            {
                return Newtonsoft.Json.JsonConvert
                    .DeserializeObject<ProfileSave>(json);
            }
#else
            private static readonly JsonSerializerOptions Options =
                new JsonSerializerOptions { IncludeFields = true };
            public string Serialize(ProfileSave profile)
            {
                return JsonSerializer.Serialize(profile, Options);
            }
            public ProfileSave Deserialize(string json)
            {
                return JsonSerializer.Deserialize<ProfileSave>(json, Options);
            }
#endif
        }
    }
}
