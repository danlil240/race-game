using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
#if UNITY_5_3_OR_NEWER
using UnityEngine;
#else
using System.Text.Json;
#endif

namespace FamilyRacing.Tests
{
    // M5 Task 3: schema-3 progression persistence — ordered migration,
    // seeded challenge assignments, lossless deep copies, and validation
    // of every new collection. Same real temp-file ProfileStore and
    // dual-path serializer as the base profile suite.
    public sealed class ProgressionProfileTests
    {
        private const string RunId = "progression-profile-test";
        private string directory;
        private IProfileSerializer serializer;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "family-racing-progression-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            serializer = new TestJsonSerializer();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        // --- migration --------------------------------------------------

        [Test]
        public void SchemaOneAndTwoMigrateWithoutLosingSettingsCoinsOrReceipts()
        {
            // A schema-1 file: every settings field set, no receipt or
            // progression keys at all.
            File.WriteAllText(Primary(0),
                "{\"schemaVersion\":1,\"id\":\"11111111-1111-4111-8111-111111111111\"," +
                "\"slot\":0,\"avatar\":\"raccoon\",\"colorIndex\":3,\"language\":\"he\"," +
                "\"camera\":\"overhead\",\"reducedCameraShake\":true,\"musicVolume\":0.1," +
                "\"effectsVolume\":0.2,\"voiceVolume\":0.3,\"coins\":41," +
                "\"unlockedVehicles\":[\"car-01\",\"motorcycle-01\",\"car-02\"]}");

            // A schema-2 file with populated applied-result receipts. The
            // second receipt's fingerprint is a real result fingerprint,
            // so it can prove the replay barrier after migration.
            const string id2 = "22222222-2222-4222-8222-222222222222";
            var replay = FinishedResult("s-9", "r2", id2, "finished", "p-b", "dnf");
            File.WriteAllText(Primary(1),
                "{\"schemaVersion\":2,\"id\":\"" + id2 + "\"," +
                "\"slot\":1,\"avatar\":\"panda\",\"colorIndex\":2,\"language\":\"en\"," +
                "\"camera\":\"chase\",\"reducedCameraShake\":false,\"musicVolume\":0.9," +
                "\"effectsVolume\":0.8,\"voiceVolume\":0.7,\"coins\":55," +
                "\"unlockedVehicles\":[\"car-01\"]," +
                "\"appliedResults\":[" +
                "{\"resultId\":\"r1/result\",\"participantId\":\"" + id2 + "\"," +
                "\"awardedCoins\":20,\"fingerprint\":\"" + new string('a', 64) + "\"}," +
                "{\"resultId\":\"" + replay.resultId + "\",\"participantId\":\"" + id2 + "\"," +
                "\"awardedCoins\":15,\"fingerprint\":\"" + replay.Fingerprint() + "\"}]}");

            var rawOne = File.ReadAllText(Primary(0));
            var rawTwo = File.ReadAllText(Primary(1));

            var one = Store().Load(0);
            Assert.That(one.Status, Is.EqualTo(ProfileLoadStatus.Loaded));
            Assert.That(one.Profile.schemaVersion, Is.EqualTo(3),
                "a schema-1 file migrates 1 -> 2 -> 3 in memory");
            Assert.That(one.Profile.id,
                Is.EqualTo("11111111-1111-4111-8111-111111111111"));
            Assert.That(one.Profile.avatar, Is.EqualTo("raccoon"));
            Assert.That(one.Profile.colorIndex, Is.EqualTo(3));
            Assert.That(one.Profile.language, Is.EqualTo("he"));
            Assert.That(one.Profile.camera, Is.EqualTo("overhead"));
            Assert.That(one.Profile.reducedCameraShake, Is.True);
            Assert.That(one.Profile.musicVolume, Is.EqualTo(0.1f));
            Assert.That(one.Profile.effectsVolume, Is.EqualTo(0.2f));
            Assert.That(one.Profile.voiceVolume, Is.EqualTo(0.3f));
            Assert.That(one.Profile.coins, Is.EqualTo(41));
            Assert.That(one.Profile.unlockedVehicles,
                Is.EquivalentTo(new[] { "car-01", "motorcycle-01", "car-02" }));
            Assert.That(one.Profile.appliedResults, Is.Not.Null);
            Assert.That(one.Profile.appliedResults.Count, Is.EqualTo(0));
            AssertSeededProgression(one.Profile);

            var two = Store().Load(1);
            Assert.That(two.Status, Is.EqualTo(ProfileLoadStatus.Loaded));
            Assert.That(two.Profile.schemaVersion, Is.EqualTo(3),
                "a schema-2 file migrates to 3 in memory");
            Assert.That(two.Profile.language, Is.EqualTo("en"));
            Assert.That(two.Profile.coins, Is.EqualTo(55));
            // Every schema-2 receipt survives with its original
            // fingerprint — they remain replay barriers after restart.
            Assert.That(two.Profile.appliedResults.Count, Is.EqualTo(2));
            Assert.That(two.Profile.appliedResults[0].resultId,
                Is.EqualTo("r1/result"));
            Assert.That(two.Profile.appliedResults[0].awardedCoins,
                Is.EqualTo(20));
            Assert.That(two.Profile.appliedResults[0].fingerprint,
                Is.EqualTo(new string('a', 64)));
            Assert.That(two.Profile.appliedResults[1].fingerprint,
                Is.EqualTo(replay.Fingerprint()));
            AssertSeededProgression(two.Profile);

            // Migration is in-memory only: bytes stay exactly as stored
            // until an explicit successful save rewrites them.
            Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo(rawOne));
            Assert.That(File.ReadAllText(Primary(1)), Is.EqualTo(rawTwo));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(2),
                "loading must never create or rewrite files");

            Store().Save(one.Profile);
            Store().Save(two.Profile);
            var written = File.ReadAllText(Primary(0));
            Assert.That(written, Does.Contain("\"schemaVersion\":3"));
            foreach (var key in new[]
            {
                "eventProgress", "worldProgress", "finaleProgress",
                "vehicleUpgrades", "ownedCosmetics", "equippedCosmetics",
                "activeBounties", "bountyRotationCursor", "timeTrialRecords",
                "purchaseReceipts", "pendingRaceContexts"
            })
            {
                Assert.That(written, Does.Contain("\"" + key + "\""),
                    "saved schema-3 file must carry " + key);
            }

            // Restart: the migrated receipt still blocks a replayed award.
            var restarted = Store().Load(1).Profile;
            var applied = RaceRewardService.Apply(
                replay, restarted.id, restarted, Store());
            Assert.That(applied.status,
                Is.EqualTo(RaceRewardService.StatusAlreadyApplied));
            Assert.That(restarted.coins, Is.EqualTo(55),
                "the old receipt must still bar a second award");
        }

        [Test]
        public void UnknownFutureSchemaRemainsUntouched()
        {
            var future = PopulatedProfile(0);
            future.schemaVersion = 4;
            File.WriteAllText(Primary(0), serializer.Serialize(future));
            var raw = File.ReadAllText(Primary(0));

            var load = Store().Load(0);
            Assert.That(load.Status, Is.EqualTo(ProfileLoadStatus.Blocked));
            Assert.That(load.Profile, Is.Null);
            Assert.That(load.Diagnostic, Does.Contain("newer"));
            Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo(raw));

            // A save attempt cannot displace the newer file either.
            Assert.Throws<InvalidOperationException>(
                () => Store().Save(PopulatedProfile(0)));
            Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo(raw));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));

            // The service surfaces the blocked slot without touching it.
            var service = new FoundationService(Store(), "en", RunId);
            Assert.That(service.Snapshot().slots[0].status,
                Is.EqualTo("Blocked"));
        }

        // --- deep copies -------------------------------------------------

        [Test]
        public void SnapshotAndDraftDeepCopyAllProgress()
        {
            Store().Save(PopulatedProfile(0));
            var service = new FoundationService(Store(), "en", RunId);
            Assert.That(service.Execute(Cmd("profile.select", P("slot", "0")))
                .status, Is.EqualTo(CommandResult.Applied));

            var first = service.Snapshot();
            var second = service.Snapshot();
            AssertIndependent(first.savedProfile, second.savedProfile);
            AssertIndependent(first.draftProfile, second.draftProfile);
            AssertIndependent(first.savedProfile, first.draftProfile);
            AssertIndependent(first.slots[0].profile, first.savedProfile);

            // Mutating one snapshot's rows can never reach the next.
            first.savedProfile.eventProgress[0].objectiveMask = 7;
            first.savedProfile.appliedResults[0].awardedCoins = -5;
            first.savedProfile.activeBounties[0].progress = 999;
            first.draftProfile.pendingRaceContexts[0]
                .bountyAssignmentIds.Add("mutated");
            var third = service.Snapshot();
            Assert.That(third.savedProfile.eventProgress[0].objectiveMask,
                Is.EqualTo(5));
            Assert.That(third.savedProfile.appliedResults[0].awardedCoins,
                Is.EqualTo(15));
            Assert.That(third.savedProfile.activeBounties[0].progress,
                Is.EqualTo(7));
            Assert.That(third.draftProfile.pendingRaceContexts[0]
                .bountyAssignmentIds.Count, Is.EqualTo(3));
        }

        [Test]
        public void DeepCopyReturnsIndependentNestedCollections()
        {
            var source = PopulatedProfile(0);
            var copy = ProfileCopy.DeepCopy(source);
            AssertProgressEqual(source, copy);
            AssertIndependent(source, copy);

            copy.eventProgress[0].objectiveMask = 7;
            copy.vehicleUpgrades[0].boost = 3;
            copy.timeTrialRecords[0].bestTicks = 1;
            copy.pendingRaceContexts[0].bountyDefinitions[0].threshold = 99;
            Assert.That(source.eventProgress[0].objectiveMask, Is.EqualTo(5));
            Assert.That(source.vehicleUpgrades[0].boost, Is.EqualTo(0));
            Assert.That(source.timeTrialRecords[0].bestTicks, Is.EqualTo(12345));
            Assert.That(source.pendingRaceContexts[0]
                .bountyDefinitions[0].threshold, Is.EqualTo(10));
            Assert.That(ProfileCopy.DeepCopy(null), Is.Null);
        }

        [Test]
        public void SettingsSliceCopiesWithoutProgression()
        {
            var live = PopulatedProfile(0);
            var draft = ProfileCopy.DeepCopy(live);
            draft.language = "he";
            draft.camera = "overhead";
            // The draft's progression went stale while pending.
            draft.coins = 0;
            draft.eventProgress.Clear();
            draft.appliedResults.Clear();

            var candidate = ProfileCopy.DeepCopy(live);
            ProfileCopy.CopySettings(draft, candidate);
            Assert.That(candidate.language, Is.EqualTo("he"));
            Assert.That(candidate.camera, Is.EqualTo("overhead"));
            Assert.That(candidate.coins, Is.EqualTo(live.coins),
                "progression comes from the live profile, not the draft");
            Assert.That(candidate.eventProgress.Count, Is.EqualTo(2));
            Assert.That(candidate.appliedResults.Count, Is.EqualTo(1));
        }

        [Test]
        public void RewardCandidateCopyKeepsEveryProgressionCollection()
        {
            // RaceRewardService mutates a deep-copied candidate before the
            // atomic save; schema-3 collections must ride that copy so an
            // applied award can never drop unrelated progress.
            var store = Store();
            var profile = PopulatedProfile(0);
            store.Save(profile);
            var loaded = store.Load(0).Profile;
            var result = FinishedResult("s-1", "r-new",
                loaded.id, "finished", "other-p", "dnf");

            var applied = RaceRewardService.Apply(
                result, loaded.id, loaded, store);
            Assert.That(applied.status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            var written = store.Load(0).Profile;
            Assert.That(written.coins, Is.EqualTo(profile.coins + 20));
            Assert.That(written.appliedResults.Count, Is.EqualTo(2));
            AssertProgressEqual(loaded, written);
        }

        // --- seeding ------------------------------------------------------

        [Test]
        public void NewAndMigratedProfilesSeedInitialBountiesWithUniqueIds()
        {
            var created = ProfileFactory.Create(0, "en");
            AssertInitialBounties(created.activeBounties);

            // Assignment ids are unique across profiles too — a row can
            // never be confused with another profile's assignment.
            var other = ProfileFactory.Create(0, "en");
            foreach (var a in created.activeBounties)
                foreach (var b in other.activeBounties)
                    Assert.That(a.assignmentId, Is.Not.EqualTo(b.assignmentId));

            // Migration seeds identically.
            File.WriteAllText(Primary(0),
                "{\"schemaVersion\":2,\"id\":\"33333333-3333-4333-8333-333333333333\"," +
                "\"slot\":0,\"avatar\":\"fox\",\"colorIndex\":0,\"language\":\"en\"," +
                "\"camera\":\"chase\",\"reducedCameraShake\":false,\"musicVolume\":0.5," +
                "\"effectsVolume\":0.5,\"voiceVolume\":0.5,\"coins\":0," +
                "\"unlockedVehicles\":[\"car-01\"],\"appliedResults\":[]}");
            var migrated = Store().Load(0).Profile;
            AssertInitialBounties(migrated.activeBounties);
            foreach (var a in created.activeBounties)
                foreach (var b in migrated.activeBounties)
                    Assert.That(a.assignmentId, Is.Not.EqualTo(b.assignmentId));
        }

        // --- multi-slot / backup -----------------------------------------

        [Test]
        public void FourSlotsPersistProgressionIndependently()
        {
            var profiles = new ProfileSave[4];
            for (var slot = 0; slot < 4; slot++)
            {
                profiles[slot] = PopulatedProfile(slot);
                profiles[slot].coins = 100 + slot;
                profiles[slot].bountyRotationCursor = slot;
                Store().Save(profiles[slot]);
            }
            for (var slot = 0; slot < 4; slot++)
            {
                var loaded = Store().Load(slot);
                Assert.That(loaded.Status, Is.EqualTo(ProfileLoadStatus.Loaded));
                Assert.That(loaded.Profile.id, Is.EqualTo(profiles[slot].id));
                Assert.That(loaded.Profile.coins, Is.EqualTo(100 + slot));
                Assert.That(loaded.Profile.bountyRotationCursor, Is.EqualTo(slot));
                AssertProgressEqual(profiles[slot], loaded.Profile);
            }
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(4));
        }

        [Test]
        public void BackupRecoveryMigratesSchemaTwoIntoProgression()
        {
            File.WriteAllText(Primary(0), "private corrupt content");
            File.WriteAllText(Backup(0),
                "{\"schemaVersion\":2,\"id\":\"44444444-4444-4444-8444-444444444444\"," +
                "\"slot\":0,\"avatar\":\"rabbit\",\"colorIndex\":1,\"language\":\"he\"," +
                "\"camera\":\"overhead\",\"reducedCameraShake\":true,\"musicVolume\":0.4," +
                "\"effectsVolume\":0.6,\"voiceVolume\":0.8,\"coins\":77," +
                "\"unlockedVehicles\":[\"car-01\"],\"appliedResults\":[]}");
            var result = Store().Load(0);
            Assert.That(result.Status,
                Is.EqualTo(ProfileLoadStatus.RecoveredFromBackup));
            Assert.That(result.Profile.schemaVersion, Is.EqualTo(3));
            Assert.That(result.Profile.coins, Is.EqualTo(77));
            AssertSeededProgression(result.Profile);
            // Recovery never rewrites either file.
            Assert.That(File.ReadAllText(Primary(0)),
                Is.EqualTo("private corrupt content"));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(2));
        }

        // --- settings merge -----------------------------------------------

        [Test]
        public void SettingsSaveMergesOntoLiveProgression()
        {
            Store().Save(PopulatedProfile(0));
            var service = new FoundationService(Store(), "en", RunId);
            Assert.That(service.Execute(Cmd("profile.select", P("slot", "0")))
                .status, Is.EqualTo(CommandResult.Applied));
            Assert.That(service.Execute(Cmd("settings.update",
                P("language", "he", "camera", "overhead",
                    "reducedCameraShake", "true", "musicVolume", "0.1",
                    "effectsVolume", "0.2", "voiceVolume", "0.3"))).status,
                Is.EqualTo(CommandResult.Applied));

            // Live progression moves under the pending draft: a finalized
            // LAN result lands on the host and awards coins + a receipt.
            var link = new LanSessionTests.FakeLanLink();
            var session = new LanSessionService(
                link, new LanSessionTests.FakeLanProbe(), "gameplay-test");
            service.SetLanSession(session);
            Assert.That(service.Execute(Cmd("lan.host", P("port", "47777")))
                .status, Is.EqualTo(CommandResult.Applied));
            var profileId = service.Snapshot().savedProfile.id;
            Assert.That(session.PublishResult(
                FinishedResult("s-merge", "merge-race",
                    profileId, "finished", "other-p", "dnf")), Is.True);
            var live = service.Snapshot().savedProfile;
            Assert.That(live.coins, Is.EqualTo(61), "41 + 20 award");
            Assert.That(live.appliedResults.Count, Is.EqualTo(2));

            // The pending draft must not roll the award — or any other
            // progression — back. Only the settings fields may change.
            Assert.That(service.Execute(Cmd("settings.save", null))
                .status, Is.EqualTo(CommandResult.Applied));
            var written = Store().Load(0).Profile;
            Assert.That(written.language, Is.EqualTo("he"));
            Assert.That(written.camera, Is.EqualTo("overhead"));
            Assert.That(written.reducedCameraShake, Is.True);
            Assert.That(written.coins, Is.EqualTo(61),
                "the live award survives a settings save");
            Assert.That(written.appliedResults.Count, Is.EqualTo(2));
            Assert.That(written.eventProgress.Count, Is.EqualTo(2));
            Assert.That(written.vehicleUpgrades.Count, Is.EqualTo(1));
            Assert.That(written.activeBounties.Count, Is.EqualTo(3));
            Assert.That(written.timeTrialRecords.Count, Is.EqualTo(1));
            Assert.That(written.purchaseReceipts.Count, Is.EqualTo(1));
            Assert.That(written.pendingRaceContexts.Count, Is.EqualTo(1));
        }

        // --- schema-3 validation -------------------------------------------

        [TestCase("nullEventProgress")]
        [TestCase("nullActiveBounties")]
        [TestCase("nullPendingContexts")]
        [TestCase("nullEventRow")]
        [TestCase("emptyEventId")]
        [TestCase("duplicateEventId")]
        [TestCase("objectiveMaskNegative")]
        [TestCase("objectiveMaskHigh")]
        [TestCase("duplicateWorldId")]
        [TestCase("duplicateFinaleId")]
        [TestCase("upgradeLevelNegative")]
        [TestCase("upgradeLevelHigh")]
        [TestCase("duplicateUpgradeVehicle")]
        [TestCase("duplicateAssignmentId")]
        [TestCase("duplicateBountyId")]
        [TestCase("tooManyBounties")]
        [TestCase("negativeBountyProgress")]
        [TestCase("negativeCursor")]
        [TestCase("duplicateRecordPair")]
        [TestCase("zeroBestTicks")]
        [TestCase("duplicatePurchaseId")]
        [TestCase("negativePurchaseCoins")]
        [TestCase("negativePurchaseLevel")]
        [TestCase("duplicateContextRaceId")]
        [TestCase("foreignContext")]
        [TestCase("badContextFingerprint")]
        [TestCase("contextAssignmentOverflow")]
        [TestCase("contextMismatchedDefinitions")]
        [TestCase("duplicateOwnedCosmetic")]
        [TestCase("equippedNotOwned")]
        public void SchemaThreeProgressionViolationsReject(string invariant)
        {
            var good = PopulatedProfile(0);
            Store().Save(good);
            var before = File.ReadAllText(Primary(0));

            var broken = PopulatedProfile(0);
            broken.id = good.id; // same identity — only the invariant fails
            ApplyViolation(broken, invariant);
            Assert.Throws<ArgumentException>(() => Store().Save(broken));
            Assert.That(File.ReadAllText(Primary(0)), Is.EqualTo(before));

            // The same violation arriving from disk blocks loading —
            // it is corrupt data, never silently adopted or evicted.
            // Null-collection payloads are written literally: JsonUtility
            // serializes a null list as [], which would hide the corrupt
            // shape the strict reader is meant to reject.
            string field = null;
            if (invariant == "nullEventProgress") field = "eventProgress";
            if (invariant == "nullActiveBounties") field = "activeBounties";
            if (invariant == "nullPendingContexts") field = "pendingRaceContexts";
            string payload;
            if (field != null)
            {
                var clean = PopulatedProfile(0);
                clean.id = good.id;
                clean.slot = 1;
                // Emit the collection as [] first so the serializer's
                // exact token can be rewritten to a literal null.
                if (field == "eventProgress") clean.eventProgress.Clear();
                if (field == "activeBounties") clean.activeBounties.Clear();
                if (field == "pendingRaceContexts")
                    clean.pendingRaceContexts.Clear();
                payload = serializer.Serialize(clean).Replace(
                    "\"" + field + "\":[]", "\"" + field + "\":null");
                Assert.That(payload, Does.Contain("\"" + field + "\":null"),
                    "payload rewrite for " + invariant);
            }
            else
            {
                broken.slot = 1;
                payload = serializer.Serialize(broken);
            }
            File.WriteAllText(Primary(1), payload);
            Assert.That(Store().Load(1).Status,
                Is.EqualTo(ProfileLoadStatus.Blocked), invariant);
        }

        [Test]
        public void SerializedRoundTripPreservesAllProgress()
        {
            var profile = PopulatedProfile(2);
            var json = serializer.Serialize(profile);
            var back = serializer.Deserialize(json);
            AssertProgressEqual(profile, back);
        }

        // --- helpers ------------------------------------------------------

        private static void ApplyViolation(ProfileSave profile, string invariant)
        {
            switch (invariant)
            {
                case "nullEventProgress": profile.eventProgress = null; break;
                case "nullActiveBounties": profile.activeBounties = null; break;
                case "nullPendingContexts": profile.pendingRaceContexts = null; break;
                case "nullEventRow": profile.eventProgress.Add(null); break;
                case "emptyEventId": profile.eventProgress[0].eventId = ""; break;
                case "duplicateEventId":
                    profile.eventProgress.Add(new EventProgress
                    {
                        eventId = profile.eventProgress[0].eventId
                    });
                    break;
                case "objectiveMaskNegative":
                    profile.eventProgress[0].objectiveMask = -1; break;
                case "objectiveMaskHigh":
                    profile.eventProgress[0].objectiveMask = 8; break;
                case "duplicateWorldId":
                    profile.worldProgress.Add(new WorldProgress
                    {
                        worldId = profile.worldProgress[0].worldId
                    });
                    break;
                case "duplicateFinaleId":
                    profile.finaleProgress.Add(new FinaleProgress
                    {
                        eventId = profile.finaleProgress[0].eventId
                    });
                    break;
                case "upgradeLevelNegative":
                    profile.vehicleUpgrades[0].acceleration = -1; break;
                case "upgradeLevelHigh":
                    profile.vehicleUpgrades[0].boost = 4; break;
                case "duplicateUpgradeVehicle":
                    profile.vehicleUpgrades.Add(new VehicleUpgrade
                    {
                        vehicleId = profile.vehicleUpgrades[0].vehicleId
                    });
                    break;
                case "duplicateAssignmentId":
                    profile.activeBounties[1].assignmentId =
                        profile.activeBounties[0].assignmentId;
                    break;
                case "duplicateBountyId":
                    profile.activeBounties[1].bountyId =
                        profile.activeBounties[0].bountyId;
                    break;
                case "tooManyBounties":
                    profile.activeBounties.Add(new BountyAssignment
                    {
                        assignmentId = "asg-4", bountyId = "streak-30s"
                    });
                    break;
                case "negativeBountyProgress":
                    profile.activeBounties[0].progress = -1; break;
                case "negativeCursor": profile.bountyRotationCursor = -1; break;
                case "duplicateRecordPair":
                    profile.timeTrialRecords.Add(new TimeTrialRecord
                    {
                        courseId = profile.timeTrialRecords[0].courseId,
                        classId = profile.timeTrialRecords[0].classId,
                        bestTicks = 5
                    });
                    break;
                case "zeroBestTicks":
                    profile.timeTrialRecords[0].bestTicks = 0; break;
                case "duplicatePurchaseId":
                    profile.purchaseReceipts.Add(new PurchaseReceipt
                    {
                        purchaseId = profile.purchaseReceipts[0].purchaseId
                    });
                    break;
                case "negativePurchaseCoins":
                    profile.purchaseReceipts[0].costCoins = -1; break;
                case "negativePurchaseLevel":
                    profile.purchaseReceipts[0].level = -1; break;
                case "duplicateContextRaceId":
                    profile.pendingRaceContexts.Add(
                        Context(profile, "race-1"));
                    break;
                case "foreignContext":
                    profile.pendingRaceContexts[0].profileId =
                        Guid.NewGuid().ToString("D");
                    break;
                case "badContextFingerprint":
                    profile.pendingRaceContexts[0].rulesFingerprint = "short";
                    break;
                case "contextAssignmentOverflow":
                    profile.pendingRaceContexts[0].bountyAssignmentIds.Add("asg-4");
                    break;
                case "contextMismatchedDefinitions":
                    profile.pendingRaceContexts[0].bountyDefinitions.RemoveAt(0);
                    break;
                case "duplicateOwnedCosmetic":
                    profile.ownedCosmetics.Add(profile.ownedCosmetics[0]); break;
                case "equippedNotOwned":
                    profile.equippedCosmetics.Add("paint-gold"); break;
                default: throw new ArgumentException("Unknown invariant.");
            }
        }

        private static ProfileSave PopulatedProfile(int slot)
        {
            var profile = ProfileFactory.Create(slot, "en");
            // Defensive init keeps pre-implementation failures honest:
            // the factory must seed these itself, but a null list must
            // fail an assertion, not throw an NRE inside the fixture.
            profile.appliedResults = profile.appliedResults
                ?? new List<AppliedResultReceipt>();
            profile.eventProgress = profile.eventProgress
                ?? new List<EventProgress>();
            profile.worldProgress = profile.worldProgress
                ?? new List<WorldProgress>();
            profile.finaleProgress = profile.finaleProgress
                ?? new List<FinaleProgress>();
            profile.vehicleUpgrades = profile.vehicleUpgrades
                ?? new List<VehicleUpgrade>();
            profile.ownedCosmetics = profile.ownedCosmetics
                ?? new List<string>();
            profile.equippedCosmetics = profile.equippedCosmetics
                ?? new List<string>();
            profile.timeTrialRecords = profile.timeTrialRecords
                ?? new List<TimeTrialRecord>();
            profile.purchaseReceipts = profile.purchaseReceipts
                ?? new List<PurchaseReceipt>();
            profile.pendingRaceContexts = profile.pendingRaceContexts
                ?? new List<RaceRewardContext>();
            profile.schemaVersion = 3;
            profile.coins = 41;
            profile.unlockedVehicles.Add("car-02");
            profile.appliedResults.Add(new AppliedResultReceipt
            {
                resultId = "r-old/result", participantId = profile.id,
                awardedCoins = 15, fingerprint = new string('a', 64)
            });
            profile.eventProgress.Add(new EventProgress
            {
                eventId = "harbor-hop-car-circuit",
                completed = true, objectiveMask = 5
            });
            profile.eventProgress.Add(new EventProgress
            {
                eventId = "lighthouse-loop-car-time",
                completed = false, objectiveMask = 1
            });
            profile.worldProgress.Add(new WorldProgress
            {
                worldId = "sunny-island", completed = false
            });
            profile.finaleProgress.Add(new FinaleProgress
            {
                eventId = "sunny-island-car-finale",
                completed = true, victorious = false
            });
            profile.vehicleUpgrades.Add(new VehicleUpgrade
            {
                vehicleId = "car-01", acceleration = 1, handling = 2, boost = 0
            });
            profile.ownedCosmetics.Add("paint-red");
            profile.ownedCosmetics.Add("horn-duck");
            profile.equippedCosmetics.Add("paint-red");
            profile.activeBounties = new List<BountyAssignment>
            {
                new BountyAssignment
                {
                    assignmentId = "asg-1", bountyId = "drift-10s", progress = 7
                },
                new BountyAssignment
                {
                    assignmentId = "asg-2", bountyId = "boost-3", progress = 0
                },
                new BountyAssignment
                {
                    assignmentId = "asg-3", bountyId = "landing-2", progress = 1
                }
            };
            profile.bountyRotationCursor = 2;
            profile.timeTrialRecords.Add(new TimeTrialRecord
            {
                courseId = "harbor-hop", classId = "car", bestTicks = 12345
            });
            profile.purchaseReceipts.Add(new PurchaseReceipt
            {
                purchaseId = "purchase-1", kind = "upgrade",
                vehicleId = "car-01", category = "acceleration",
                level = 1, costCoins = 60
            });
            profile.pendingRaceContexts.Add(Context(profile, "race-1"));
            return profile;
        }

        private static BountyDefinition Bounty(string id)
        {
            var metric = id.StartsWith("drift")
                ? BountyMetric.DriftSeconds
                : id.StartsWith("boost")
                    ? BountyMetric.HumanBoostActivations
                    : BountyMetric.CleanLandings;
            return new BountyDefinition
            {
                id = id, family = id.Split('-')[0], metric = metric,
                threshold = 10, unit = "count", rewardCoins = 10,
                aggregation = BountyAggregation.Sum
            };
        }

        private static RaceRewardContext Context(ProfileSave profile, string raceId)
        {
            var context = new RaceRewardContext
            {
                raceId = raceId,
                profileId = profile.id,
                mode = "campaign",
                rulesFingerprint = new string('b', 64),
                bountyAssignmentIds = new List<string>
                { "asg-1", "asg-2", "asg-3" },
                bountyDefinitions = new List<BountyDefinition>
                { Bounty("drift-10s"), Bounty("boost-3"), Bounty("landing-2") }
            };
            return context;
        }

        // A finalized immutable result naming each participant/outcome
        // pair, same shape the race coordinator produces.
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

        private static void AssertSeededProgression(ProfileSave profile)
        {
            Assert.That(profile.eventProgress, Is.Not.Null);
            Assert.That(profile.worldProgress, Is.Not.Null);
            Assert.That(profile.finaleProgress, Is.Not.Null);
            Assert.That(profile.vehicleUpgrades, Is.Not.Null);
            Assert.That(profile.ownedCosmetics, Is.Not.Null);
            Assert.That(profile.equippedCosmetics, Is.Not.Null);
            Assert.That(profile.timeTrialRecords, Is.Not.Null);
            Assert.That(profile.purchaseReceipts, Is.Not.Null);
            Assert.That(profile.pendingRaceContexts, Is.Not.Null);
            Assert.That(profile.bountyRotationCursor, Is.EqualTo(0));
            AssertInitialBounties(profile.activeBounties);
        }

        private static void AssertInitialBounties(List<BountyAssignment> bounties)
        {
            Assert.That(bounties, Is.Not.Null);
            Assert.That(bounties.Count, Is.EqualTo(3),
                "new/migrated profiles seed the three initial challenges");
            var ids = new List<string>();
            var assignments = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bounty in bounties)
            {
                ids.Add(bounty.bountyId);
                Assert.That(bounty.assignmentId, Is.Not.Null.And.Not.Empty);
                Assert.That(assignments.Add(bounty.assignmentId), Is.True,
                    "assignment ids must be unique");
                Assert.That(bounty.progress, Is.EqualTo(0),
                    "fresh assignments start at zero progress");
            }
            Assert.That(ids,
                Is.EqualTo(new[] { "drift-10s", "boost-3", "landing-2" }));
        }

        // Every nested collection must be an independent instance, and so
        // must every row inside it.
        private static void AssertIndependent(ProfileSave a, ProfileSave b)
        {
            Assert.That(b, Is.Not.Null);
            Assert.AreNotSame(a, b);
            Assert.AreNotSame(a.unlockedVehicles, b.unlockedVehicles);
            Assert.AreNotSame(a.appliedResults, b.appliedResults);
            Assert.AreNotSame(a.eventProgress, b.eventProgress);
            Assert.AreNotSame(a.worldProgress, b.worldProgress);
            Assert.AreNotSame(a.finaleProgress, b.finaleProgress);
            Assert.AreNotSame(a.vehicleUpgrades, b.vehicleUpgrades);
            Assert.AreNotSame(a.ownedCosmetics, b.ownedCosmetics);
            Assert.AreNotSame(a.equippedCosmetics, b.equippedCosmetics);
            Assert.AreNotSame(a.activeBounties, b.activeBounties);
            Assert.AreNotSame(a.timeTrialRecords, b.timeTrialRecords);
            Assert.AreNotSame(a.purchaseReceipts, b.purchaseReceipts);
            Assert.AreNotSame(a.pendingRaceContexts, b.pendingRaceContexts);
            AssertRowsIndependent(a.appliedResults, b.appliedResults);
            AssertRowsIndependent(a.eventProgress, b.eventProgress);
            AssertRowsIndependent(a.worldProgress, b.worldProgress);
            AssertRowsIndependent(a.finaleProgress, b.finaleProgress);
            AssertRowsIndependent(a.vehicleUpgrades, b.vehicleUpgrades);
            AssertRowsIndependent(a.activeBounties, b.activeBounties);
            AssertRowsIndependent(a.timeTrialRecords, b.timeTrialRecords);
            AssertRowsIndependent(a.purchaseReceipts, b.purchaseReceipts);
            AssertRowsIndependent(a.pendingRaceContexts, b.pendingRaceContexts);
            if (a.pendingRaceContexts != null && b.pendingRaceContexts != null)
                for (var i = 0; i < a.pendingRaceContexts.Count; i++)
                {
                    Assert.AreNotSame(a.pendingRaceContexts[i].bountyAssignmentIds,
                        b.pendingRaceContexts[i].bountyAssignmentIds);
                    AssertRowsIndependent(a.pendingRaceContexts[i].bountyDefinitions,
                        b.pendingRaceContexts[i].bountyDefinitions);
                }
        }

        private static void AssertRowsIndependent<T>(List<T> a, List<T> b)
            where T : class
        {
            if (a == null || b == null) return;
            Assert.That(b.Count, Is.EqualTo(a.Count));
            for (var i = 0; i < a.Count; i++)
                Assert.AreNotSame(a[i], b[i]);
        }

        private static void AssertProgressEqual(ProfileSave expected, ProfileSave actual)
        {
            Assert.That(actual, Is.Not.Null);
            Assert.That(actual.id, Is.EqualTo(expected.id));
            Assert.That(actual.slot, Is.EqualTo(expected.slot));
            Assert.That(actual.schemaVersion, Is.EqualTo(expected.schemaVersion));
            Assert.That(actual.coins, Is.EqualTo(expected.coins));
            Assert.That(actual.bountyRotationCursor,
                Is.EqualTo(expected.bountyRotationCursor));
            Assert.That(actual.unlockedVehicles,
                Is.EqualTo(expected.unlockedVehicles));
            Assert.That(actual.ownedCosmetics,
                Is.EqualTo(expected.ownedCosmetics));
            Assert.That(actual.equippedCosmetics,
                Is.EqualTo(expected.equippedCosmetics));
            Assert.That(actual.appliedResults.Count,
                Is.EqualTo(expected.appliedResults.Count));
            for (var i = 0; i < expected.appliedResults.Count; i++)
            {
                Assert.That(actual.appliedResults[i].resultId,
                    Is.EqualTo(expected.appliedResults[i].resultId));
                Assert.That(actual.appliedResults[i].fingerprint,
                    Is.EqualTo(expected.appliedResults[i].fingerprint));
            }
            Assert.That(actual.eventProgress.Count,
                Is.EqualTo(expected.eventProgress.Count));
            for (var i = 0; i < expected.eventProgress.Count; i++)
            {
                Assert.That(actual.eventProgress[i].eventId,
                    Is.EqualTo(expected.eventProgress[i].eventId));
                Assert.That(actual.eventProgress[i].completed,
                    Is.EqualTo(expected.eventProgress[i].completed));
                Assert.That(actual.eventProgress[i].objectiveMask,
                    Is.EqualTo(expected.eventProgress[i].objectiveMask));
            }
            Assert.That(actual.worldProgress.Count,
                Is.EqualTo(expected.worldProgress.Count));
            Assert.That(actual.finaleProgress.Count,
                Is.EqualTo(expected.finaleProgress.Count));
            Assert.That(actual.vehicleUpgrades.Count,
                Is.EqualTo(expected.vehicleUpgrades.Count));
            for (var i = 0; i < expected.vehicleUpgrades.Count; i++)
            {
                Assert.That(actual.vehicleUpgrades[i].vehicleId,
                    Is.EqualTo(expected.vehicleUpgrades[i].vehicleId));
                Assert.That(actual.vehicleUpgrades[i].acceleration,
                    Is.EqualTo(expected.vehicleUpgrades[i].acceleration));
                Assert.That(actual.vehicleUpgrades[i].handling,
                    Is.EqualTo(expected.vehicleUpgrades[i].handling));
                Assert.That(actual.vehicleUpgrades[i].boost,
                    Is.EqualTo(expected.vehicleUpgrades[i].boost));
            }
            Assert.That(actual.activeBounties.Count,
                Is.EqualTo(expected.activeBounties.Count));
            for (var i = 0; i < expected.activeBounties.Count; i++)
            {
                Assert.That(actual.activeBounties[i].assignmentId,
                    Is.EqualTo(expected.activeBounties[i].assignmentId));
                Assert.That(actual.activeBounties[i].bountyId,
                    Is.EqualTo(expected.activeBounties[i].bountyId));
                Assert.That(actual.activeBounties[i].progress,
                    Is.EqualTo(expected.activeBounties[i].progress));
            }
            Assert.That(actual.timeTrialRecords.Count,
                Is.EqualTo(expected.timeTrialRecords.Count));
            for (var i = 0; i < expected.timeTrialRecords.Count; i++)
            {
                Assert.That(actual.timeTrialRecords[i].courseId,
                    Is.EqualTo(expected.timeTrialRecords[i].courseId));
                Assert.That(actual.timeTrialRecords[i].classId,
                    Is.EqualTo(expected.timeTrialRecords[i].classId));
                Assert.That(actual.timeTrialRecords[i].bestTicks,
                    Is.EqualTo(expected.timeTrialRecords[i].bestTicks));
            }
            Assert.That(actual.purchaseReceipts.Count,
                Is.EqualTo(expected.purchaseReceipts.Count));
            for (var i = 0; i < expected.purchaseReceipts.Count; i++)
            {
                Assert.That(actual.purchaseReceipts[i].purchaseId,
                    Is.EqualTo(expected.purchaseReceipts[i].purchaseId));
                Assert.That(actual.purchaseReceipts[i].costCoins,
                    Is.EqualTo(expected.purchaseReceipts[i].costCoins));
            }
            Assert.That(actual.pendingRaceContexts.Count,
                Is.EqualTo(expected.pendingRaceContexts.Count));
            for (var i = 0; i < expected.pendingRaceContexts.Count; i++)
            {
                Assert.That(actual.pendingRaceContexts[i].raceId,
                    Is.EqualTo(expected.pendingRaceContexts[i].raceId));
                Assert.That(actual.pendingRaceContexts[i].profileId,
                    Is.EqualTo(expected.pendingRaceContexts[i].profileId));
                Assert.That(actual.pendingRaceContexts[i].rulesFingerprint,
                    Is.EqualTo(expected.pendingRaceContexts[i].rulesFingerprint));
                Assert.That(actual.pendingRaceContexts[i].bountyAssignmentIds,
                    Is.EqualTo(expected.pendingRaceContexts[i].bountyAssignmentIds));
                Assert.That(actual.pendingRaceContexts[i].bountyDefinitions.Count,
                    Is.EqualTo(expected.pendingRaceContexts[i].bountyDefinitions.Count));
            }
        }

        private ProfileStore Store()
        {
            return new ProfileStore(directory, serializer);
        }

        private string Primary(int slot)
        {
            return Path.Combine(directory, "profile-" + slot + ".json");
        }

        private string Backup(int slot)
        {
            return Primary(slot) + ".bak";
        }

        private static GameCommand Cmd(string action,
            Dictionary<string, string> parameters)
        {
            // Commands must carry THIS fixture's run id — Execute
            // rejects commands from another run.
            return new GameCommand(1, RunId, Guid.NewGuid().ToString("N"),
                "local", action, parameters);
        }

        private static Dictionary<string, string> P(params string[] keyValues)
        {
            return LanSessionTests.P(keyValues);
        }

        private sealed class TestJsonSerializer : IProfileSerializer
        {
#if UNITY_5_3_OR_NEWER
            // Mirror UnityProfileSerializer: JsonUtility for the file
            // shape, strict Newtonsoft reads.
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
