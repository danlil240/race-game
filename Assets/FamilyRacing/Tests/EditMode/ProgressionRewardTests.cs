using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
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
    // M5 Task 7: the complete race-progression transaction. One finalized
    // result against its durable race-start context commits — inside ONE
    // atomic profile save — coins (completion + placement + challenge +
    // bounty + rival), campaign objective masks and completion, finale
    // and world facts, vehicle unlocks and the rival cosmetic, bounty
    // tray progress/rotation, the award receipt bound to result AND
    // rules identity, and the removal of the pending context. A failed
    // write leaves live profile and disk byte-identical and retryable.
    public sealed class ProgressionRewardTests
    {
        private string directory;
        private IProfileSerializer serializer;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "family-racing-prog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            serializer = new TestJsonSerializer();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        // --- the atomic transaction -----------------------------------

        [Test]
        public void SingleResultFoldsCoinsStarsAndBoundBountyAtomically()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = SeededProfile(catalog,
                Assignment("asg-finish", "finish-2", progress: 1),
                Assignment("asg-boost", "boost-3", progress: 0),
                Assignment("asg-land", "landing-2", progress: 0));
            store.Save(profile);
            var loaded = store.Load(0).Profile;
            var context = BoundContext(loaded, catalog, "race-a", "campaign");
            PersistContext(store, loaded, context);

            // harbor-hop-car-circuit: intro needs 1 clean landing,
            // mastery a first-place finish OR 2 distinct shortcuts.
            var metrics = new RaceMetrics
            {
                cleanLandings = 1, position = 1, finishTicks = 3800
            };
            var result = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-circuit", loaded.id, "finished", 1,
                metrics, raceId: "race-a");
            result.rulesFingerprint = catalog.CompatibilityFingerprint;

            var applied = RaceRewardService.Apply(result, loaded.id,
                loaded, store, catalog, context);

            Assert.That(applied.status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            // 20 completion + 5 first-place + 10 introductory + 20
            // mastery + 10 bounty = 65, all from authored facts.
            Assert.That(applied.completionCoins, Is.EqualTo(20));
            Assert.That(applied.placementCoins, Is.EqualTo(5));
            Assert.That(applied.challengeCoins, Is.EqualTo(30));
            Assert.That(applied.bountyCoins, Is.EqualTo(10));
            Assert.That(applied.rivalCoins, Is.EqualTo(0));
            Assert.That(applied.awardedCoins, Is.EqualTo(65));
            Assert.That(applied.newStarBits,
                Is.EqualTo(ObjectiveEvaluator.FullMask));
            Assert.That(loaded.coins, Is.EqualTo(65));

            // Campaign facts landed on the candidate, not before save.
            var evt = ProgressionRow(loaded, "harbor-hop-car-circuit");
            Assert.That(evt, Is.Not.Null);
            Assert.That(evt.completed, Is.True);
            Assert.That(evt.objectiveMask,
                Is.EqualTo(ObjectiveEvaluator.FullMask));

            // The bound finish-2 assignment completed and rotated out;
            // its replacement is a fresh identity at zero progress.
            var gone = BountyRow(loaded, "asg-finish");
            Assert.That(gone, Is.Null,
                "a completed bound slot must rotate out");
            Assert.That(loaded.activeBounties.Count, Is.EqualTo(3));
            foreach (var row in loaded.activeBounties)
            {
                Assert.That(row.assignmentId,
                    Is.Not.EqualTo("asg-finish"));
            }

            // The durable context retired inside the same save.
            Assert.That(PendingContext(loaded, "race-a"), Is.Null);

            // The receipt binds result identity AND rules identity and
            // carries the persisted breakdown.
            var receipt = ReceiptFor(loaded, result.resultId);
            Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.fingerprint,
                Is.EqualTo(result.Fingerprint()));
            Assert.That(receipt.rulesFingerprint,
                Is.EqualTo(catalog.CompatibilityFingerprint));
            Assert.That(receipt.mode, Is.EqualTo("campaign"));
            Assert.That(receipt.awardedCoins, Is.EqualTo(65));
            Assert.That(receipt.completionCoins, Is.EqualTo(20));
            Assert.That(receipt.placementCoins, Is.EqualTo(5));
            Assert.That(receipt.challengeCoins, Is.EqualTo(30));
            Assert.That(receipt.bountyCoins, Is.EqualTo(10));
            Assert.That(receipt.completedBountyIds,
                Is.EquivalentTo(new[] { "finish-2" }));

            // A fresh store on the same directory is a process restart:
            // the committed file must carry every fact.
            var reloaded = Store().Load(0).Profile;
            Assert.That(reloaded.coins, Is.EqualTo(65));
            Assert.That(PendingContext(reloaded, "race-a"), Is.Null);
            Assert.That(ReceiptFor(reloaded, result.resultId),
                Is.Not.Null);
            var persistedEvt = ProgressionRow(reloaded,
                "harbor-hop-car-circuit");
            Assert.That(persistedEvt.objectiveMask,
                Is.EqualTo(ObjectiveEvaluator.FullMask));
        }

        [Test]
        public void RivalFinaleVictoryUnlocksWorldVehiclesAndCosmetic()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = WorldAlmostDone(catalog);
            profile.activeBounties = new List<BountyAssignment>
            {
                Assignment("asg-finish", "finish-2", progress: 1)
            };
            store.Save(profile);
            var loaded = store.Load(0).Profile;
            var context = BoundContext(loaded, catalog,
                "race-finale", "campaign");
            PersistContext(store, loaded, context);

            var result = CampaignTestFixture.Result("campaign",
                "sunny-island-car-finale", loaded.id, "finished", 1,
                new RaceMetrics
                {
                    position = 1, finishTicks = 3800, rivalDefeated = true
                },
                raceId: "race-finale");
            result.rulesFingerprint = catalog.CompatibilityFingerprint;

            var applied = RaceRewardService.Apply(result, loaded.id,
                loaded, store, catalog, context);

            Assert.That(applied.status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            // 20 completion + 5 placement + 30 rival victory + 10
            // completing bound bounty = 65.
            Assert.That(applied.awardedCoins, Is.EqualTo(65));
            Assert.That(applied.rivalCoins, Is.EqualTo(30));
            Assert.That(applied.bountyCoins, Is.EqualTo(10));
            Assert.That(loaded.coins, Is.EqualTo(65));

            // One-time rival facts: finale completed AND victorious,
            // rival bounty ids recorded, cosmetic granted.
            var finale = FinaleRow(loaded, "sunny-island-car-finale");
            Assert.That(finale, Is.Not.Null);
            Assert.That(finale.completed, Is.True);
            Assert.That(finale.victorious, Is.True);
            Assert.That(applied.finaleCompleted, Is.True);
            Assert.That(applied.finaleVictorious, Is.True);
            Assert.That(applied.rivalDefeated, Is.True);
            Assert.That(applied.rivalCosmeticId,
                Is.EqualTo("livery-rival-car"));
            Assert.That(loaded.ownedCosmetics,
                Does.Contain("livery-rival-car"));
            Assert.That(applied.rivalBountyIds,
                Is.EquivalentTo(new[] { "bounty-rival-car" }));

            // Both finales done -> the world marks complete and BOTH
            // authored reward vehicles unlock inside this save.
            Assert.That(applied.worldCompleted, Is.True);
            var world = WorldRow(loaded, "sunny-island");
            Assert.That(world, Is.Not.Null);
            Assert.That(world.completed, Is.True);
            Assert.That(loaded.unlockedVehicles, Does.Contain("car-02"));
            Assert.That(loaded.unlockedVehicles,
                Does.Contain("motorcycle-02"));
            Assert.That(applied.unlockedVehicleIds,
                Is.EquivalentTo(new[] { "car-02", "motorcycle-02" }));
            Assert.That(PendingContext(loaded, "race-finale"), Is.Null);

            // Replay of the identical result is a provable no-op even
            // though the context is already consumed.
            var replay = RaceRewardService.Apply(result, loaded.id,
                loaded, store, catalog, context);
            Assert.That(replay.status,
                Is.EqualTo(RaceRewardService.StatusAlreadyApplied));
            Assert.That(replay.awardedCoins, Is.EqualTo(65),
                "the replay reports the persisted award, nothing new");
            Assert.That(loaded.coins, Is.EqualTo(65));
            Assert.That(loaded.ownedCosmetics.FindAll(
                c => c == "livery-rival-car").Count, Is.EqualTo(1));

            // Restart-level proof.
            var reloaded = Store().Load(0).Profile;
            Assert.That(reloaded.coins, Is.EqualTo(65));
            Assert.That(FinaleRow(reloaded, "sunny-island-car-finale")
                .victorious, Is.True);
            Assert.That(reloaded.unlockedVehicles,
                Does.Contain("car-02").And.Contain("motorcycle-02"));
        }

        [Test]
        public void FinaleLossStillCompletesWorldWithoutRivalRewards()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = WorldAlmostDone(catalog);
            store.Save(profile);
            var loaded = store.Load(0).Profile;
            var context = BoundContext(loaded, catalog,
                "race-finale", "campaign");
            PersistContext(store, loaded, context);

            var result = CampaignTestFixture.Result("campaign",
                "sunny-island-car-finale", loaded.id, "finished", 2,
                new RaceMetrics { position = 2, finishTicks = 3900 },
                raceId: "race-finale");
            result.rulesFingerprint = catalog.CompatibilityFingerprint;

            var applied = RaceRewardService.Apply(result, loaded.id,
                loaded, store, catalog, context);

            Assert.That(applied.status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            // Second place: 20 completion + 3 placement, no rival facts.
            Assert.That(applied.awardedCoins, Is.EqualTo(23));
            Assert.That(applied.rivalCoins, Is.EqualTo(0));
            Assert.That(applied.finaleCompleted, Is.True);
            Assert.That(applied.finaleVictorious, Is.False);
            var finale = FinaleRow(loaded, "sunny-island-car-finale");
            Assert.That(finale.completed, Is.True);
            Assert.That(finale.victorious, Is.False);
            // Losing still completes the world (victory is optional) —
            // but grants no cosmetic.
            Assert.That(applied.worldCompleted, Is.True);
            Assert.That(loaded.unlockedVehicles, Does.Contain("car-02"));
            Assert.That(loaded.ownedCosmetics,
                Does.Not.Contain("livery-rival-car"));
        }

        [Test]
        public void RivalVictoryPaysOnceAcrossDistinctRaces()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = WorldAlmostDone(catalog);
            store.Save(profile);
            var loaded = store.Load(0).Profile;

            var first = FinaleVictoryResult(loaded, catalog, "race-f1");
            PersistContext(store, loaded,
                BoundContext(loaded, catalog, "race-f1", "campaign"));
            Assert.That(RaceRewardService.Apply(first, loaded.id,
                loaded, store, catalog, null).status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            var coinsAfterFirst = loaded.coins;

            // A NEW race id on the same finale: the victory was already
            // recorded, so no second rival reward or cosmetic.
            var second = FinaleVictoryResult(loaded, catalog, "race-f2");
            PersistContext(store, loaded,
                BoundContext(loaded, catalog, "race-f2", "campaign"));
            var applied = RaceRewardService.Apply(second, loaded.id,
                loaded, store, catalog, null);
            Assert.That(applied.status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            Assert.That(applied.rivalCoins, Is.EqualTo(0));
            Assert.That(applied.rivalCosmeticId, Is.EqualTo(""));
            Assert.That(applied.finaleVictorious, Is.False,
                "the one-time fact was already durable");
            Assert.That(loaded.coins, Is.LessThan(coinsAfterFirst + 30));
            Assert.That(loaded.ownedCosmetics.FindAll(
                c => c == "livery-rival-car").Count, Is.EqualTo(1));
        }

        // --- atomicity under write failure -----------------------------

        [Test]
        public void WriteFailureLeavesLiveProfileAndFileHashesUnchanged()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = WorldAlmostDone(catalog);
            profile.activeBounties = new List<BountyAssignment>
            {
                Assignment("asg-finish", "finish-2", progress: 1)
            };
            store.Save(profile);
            var loaded = store.Load(0).Profile;
            var context = BoundContext(loaded, catalog,
                "race-finale", "campaign");
            PersistContext(store, loaded, context);
            var onDisk = File.ReadAllBytes(Primary(0));
            var liveBefore = CampaignTestFixture.ProfileSnapshot(loaded);

            var result = FinaleVictoryResult(loaded, catalog, "race-finale");

            RewardApplyResult failed;
            using (new FileStream(Primary(0), FileMode.Open,
                FileAccess.Read, FileShare.Read))
            {
                failed = RaceRewardService.Apply(result, loaded.id,
                    loaded, store, catalog, context);
            }
            Assert.That(failed.status,
                Is.EqualTo(RaceRewardService.StatusFailed));
            Assert.That(failed.code, Is.EqualTo("saveFailed"));

            // Memory is untouched — same snapshot — and the file hash
            // is byte-identical.
            Assert.That(CampaignTestFixture.ProfileSnapshot(loaded),
                Is.EqualTo(liveBefore));
            Assert.That(File.ReadAllBytes(Primary(0)),
                Is.EqualTo(onDisk));
            Assert.That(PendingContext(loaded, "race-finale"),
                Is.Not.Null, "the durable context stays pending");

            // The retry is against the ORIGINAL bound context and
            // completes the same transaction.
            var retried = RaceRewardService.Apply(result, loaded.id,
                loaded, store, catalog, context);
            Assert.That(retried.status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            Assert.That(retried.awardedCoins, Is.EqualTo(65));
            Assert.That(PendingContext(loaded, "race-finale"), Is.Null);
        }

        // --- replay / conflict / eligibility ----------------------------

        [Test]
        public void RestartAndRedeliveryDoNotRepayAnyAward()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = SeededProfile(catalog,
                Assignment("asg-finish", "finish-2", progress: 1));
            store.Save(profile);
            var loaded = store.Load(0).Profile;
            var context = BoundContext(loaded, catalog, "race-a", "campaign");
            PersistContext(store, loaded, context);
            var result = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-circuit", loaded.id, "finished", 1,
                new RaceMetrics
                { cleanLandings = 1, position = 1, finishTicks = 3800 },
                raceId: "race-a");
            result.rulesFingerprint = catalog.CompatibilityFingerprint;

            Assert.That(RaceRewardService.Apply(result, loaded.id,
                loaded, store, catalog, context).status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            Assert.That(loaded.coins, Is.EqualTo(65));

            // Process restart: fresh store + reloaded profile, and NO
            // supplied context (the durable row was consumed) — the
            // receipt alone dedupes the redelivery.
            var restartedStore = Store();
            var reloaded = restartedStore.Load(0).Profile;
            var snapshot = CampaignTestFixture.ProfileSnapshot(reloaded);
            var again = RaceRewardService.Apply(result, reloaded.id,
                reloaded, restartedStore, catalog, null);
            Assert.That(again.status,
                Is.EqualTo(RaceRewardService.StatusAlreadyApplied));
            Assert.That(again.awardedCoins, Is.EqualTo(65));
            Assert.That(again.completionCoins, Is.EqualTo(20));
            Assert.That(again.bountyCoins, Is.EqualTo(10),
                "the persisted breakdown echoes on redelivery");
            Assert.That(
                CampaignTestFixture.ProfileSnapshot(reloaded),
                Is.EqualTo(snapshot));
            var third = RaceRewardService.Apply(result, reloaded.id,
                reloaded, restartedStore, catalog, null);
            Assert.That(third.status,
                Is.EqualTo(RaceRewardService.StatusAlreadyApplied));
            Assert.That(Store().Load(0).Profile.appliedResults.Count,
                Is.EqualTo(1));
        }

        [Test]
        public void ConflictingResultIdentityRejectsAfterRestart()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = SeededProfile(catalog,
                Assignment("asg-finish", "finish-2", progress: 0));
            store.Save(profile);
            var loaded = store.Load(0).Profile;
            var context = BoundContext(loaded, catalog, "race-a", "campaign");
            PersistContext(store, loaded, context);
            var result = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-circuit", loaded.id, "finished", 1,
                new RaceMetrics
                { cleanLandings = 1, position = 1, finishTicks = 3800 },
                raceId: "race-a");
            result.rulesFingerprint = catalog.CompatibilityFingerprint;
            Assert.That(RaceRewardService.Apply(result, loaded.id,
                loaded, store, catalog, context).status,
                Is.EqualTo(RaceRewardService.StatusApplied));

            var restartedStore = Store();
            var reloaded = restartedStore.Load(0).Profile;
            var coins = reloaded.coins;
            var conflicting = result.Copy();
            conflicting.racers[0].finishTicks++;
            conflicting.racers[0].metrics.finishTicks++;
            var replay = RaceRewardService.Apply(conflicting,
                reloaded.id, reloaded, restartedStore, catalog, null);
            Assert.That(replay.status,
                Is.EqualTo(RaceRewardService.StatusFailed));
            Assert.That(replay.code, Is.EqualTo("conflict"));
            Assert.That(reloaded.coins, Is.EqualTo(coins));

            // A conflicting rules identity is equally durable proof:
            // the same result id under a different rules fingerprint
            // is never this result.
            var foreignRules = result.Copy();
            foreignRules.rulesFingerprint = new string('c', 64);
            var mismatch = RaceRewardService.Apply(foreignRules,
                reloaded.id, reloaded, restartedStore, catalog, null);
            Assert.That(mismatch.status,
                Is.EqualTo(RaceRewardService.StatusFailed));
            Assert.That(mismatch.code, Is.EqualTo("conflict"));
        }

        [Test]
        public void WrongParticipantAndInvalidModeReject()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = SeededProfile(catalog,
                Assignment("asg-finish", "finish-2", progress: 0));
            store.Save(profile);
            var loaded = store.Load(0).Profile;
            var context = BoundContext(loaded, catalog, "race-a", "campaign");
            PersistContext(store, loaded, context);
            var result = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-circuit", loaded.id, "finished", 1,
                new RaceMetrics { position = 1, finishTicks = 3800 },
                raceId: "race-a");
            result.rulesFingerprint = catalog.CompatibilityFingerprint;

            // A foreign participant id can never claim this profile.
            var foreign = RaceRewardService.Apply(result, "other-pid",
                loaded, store, catalog, context);
            Assert.That(foreign.status,
                Is.EqualTo(RaceRewardService.StatusIneligible));
            Assert.That(foreign.code, Is.EqualTo("foreignProfile"));

            // A result whose row names someone else never applies here.
            var otherRow = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-circuit", "someone-else", "finished", 1,
                new RaceMetrics { position = 1, finishTicks = 3800 },
                raceId: "race-b");
            var missing = RaceRewardService.Apply(otherRow, loaded.id,
                loaded, store, catalog, context);
            Assert.That(missing.status,
                Is.EqualTo(RaceRewardService.StatusIneligible));
            Assert.That(missing.code, Is.EqualTo("notInResult"));

            // An unrecognized mode is rejected, never silently treated
            // as legacy.
            var bogus = CampaignTestFixture.Result("bogusMode",
                "harbor-hop-car-circuit", loaded.id, "finished", 1,
                new RaceMetrics { position = 1, finishTicks = 3800 },
                raceId: "race-c");
            bogus.rulesFingerprint = catalog.CompatibilityFingerprint;
            var badMode = RaceRewardService.Apply(bogus, loaded.id,
                loaded, store, catalog, null);
            Assert.That(badMode.status,
                Is.EqualTo(RaceRewardService.StatusIneligible));
            Assert.That(badMode.code, Is.EqualTo("invalidMode"));

            // Practice is a real mode but never pays progression.
            var practice = CampaignTestFixture.Result("practice",
                "harbor-hop-car-circuit", loaded.id, "finished", 1,
                new RaceMetrics { position = 1, finishTicks = 3800 },
                raceId: "race-d");
            var noPay = RaceRewardService.Apply(practice, loaded.id,
                loaded, store, catalog, null);
            Assert.That(noPay.status,
                Is.EqualTo(RaceRewardService.StatusIneligible));
            Assert.That(noPay.code, Is.EqualTo("modeNotProgression"));

            // Nothing moved anywhere.
            Assert.That(loaded.coins, Is.EqualTo(0));
            Assert.That(loaded.appliedResults.Count, Is.EqualTo(0));
            Assert.That(PendingContext(loaded, "race-a"), Is.Not.Null);
        }

        [Test]
        public void NewRaceCanAwardCoinsButNotDuplicateStars()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            // finish-5 never completes inside two races, so the coin
            // assertions isolate completion+placement+challenge only.
            var profile = SeededProfile(catalog,
                Assignment("asg-finish", "finish-5", progress: 0));
            store.Save(profile);
            var loaded = store.Load(0).Profile;

            var first = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-circuit", loaded.id, "finished", 1,
                new RaceMetrics
                { cleanLandings = 1, position = 1, finishTicks = 3800 },
                raceId: "race-1");
            first.rulesFingerprint = catalog.CompatibilityFingerprint;
            PersistContext(store, loaded,
                BoundContext(loaded, catalog, "race-1", "campaign"));
            var one = RaceRewardService.Apply(first, loaded.id,
                loaded, store, catalog, null);
            Assert.That(one.status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            Assert.That(one.challengeCoins, Is.EqualTo(30));
            Assert.That(loaded.coins, Is.EqualTo(55)); // 20+5+30

            // A fresh race on the same event: the same finish earns
            // coins again but the already-earned stars never repay.
            var second = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-circuit", loaded.id, "finished", 1,
                new RaceMetrics
                { cleanLandings = 1, position = 1, finishTicks = 3700 },
                raceId: "race-2");
            second.rulesFingerprint = catalog.CompatibilityFingerprint;
            PersistContext(store, loaded,
                BoundContext(loaded, catalog, "race-2", "campaign"));
            var two = RaceRewardService.Apply(second, loaded.id,
                loaded, store, catalog, null);
            Assert.That(two.status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            Assert.That(two.completionCoins, Is.EqualTo(20));
            Assert.That(two.placementCoins, Is.EqualTo(5));
            Assert.That(two.awardedCoins, Is.EqualTo(25));
            Assert.That(two.challengeCoins, Is.EqualTo(0));
            Assert.That(two.newStarBits, Is.EqualTo(0));
            Assert.That(loaded.coins, Is.EqualTo(80));
            Assert.That(ProgressionRow(loaded,
                "harbor-hop-car-circuit").objectiveMask,
                Is.EqualTo(ObjectiveEvaluator.FullMask));
            Assert.That(loaded.appliedResults.Count, Is.EqualTo(2));
        }

        // --- validation of the bound context ----------------------------

        [Test]
        public void MissingContextRejectsProgressionResult()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = SeededProfile(catalog,
                Assignment("asg-finish", "finish-2", progress: 0));
            store.Save(profile);
            var loaded = store.Load(0).Profile;

            var result = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-circuit", loaded.id, "finished", 1,
                new RaceMetrics { position = 1, finishTicks = 3800 },
                raceId: "race-unbound");
            result.rulesFingerprint = catalog.CompatibilityFingerprint;

            // No durable row, no supplied context: never guess.
            var missing = RaceRewardService.Apply(result, loaded.id,
                loaded, store, catalog, null);
            Assert.That(missing.status,
                Is.EqualTo(RaceRewardService.StatusIneligible));
            Assert.That(missing.code, Is.EqualTo("noContext"));

            // A supplied context that was never durably bound is not a
            // substitute — only the stored row is trusted.
            var supplied = BoundContext(loaded, catalog,
                "race-unbound", "campaign");
            var unbound = RaceRewardService.Apply(result, loaded.id,
                loaded, store, catalog, supplied);
            Assert.That(unbound.status,
                Is.EqualTo(RaceRewardService.StatusIneligible));
            Assert.That(unbound.code, Is.EqualTo("contextMismatch"));

            Assert.That(loaded.coins, Is.EqualTo(0));
            Assert.That(loaded.appliedResults.Count, Is.EqualTo(0));
        }

        [Test]
        public void ConflictingContextAndRulesIdentityReject()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = SeededProfile(catalog,
                Assignment("asg-finish", "finish-2", progress: 0));
            store.Save(profile);
            var loaded = store.Load(0).Profile;
            var durable = BoundContext(loaded, catalog, "race-a", "campaign");
            PersistContext(store, loaded, durable);

            var result = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-circuit", loaded.id, "finished", 1,
                new RaceMetrics { position = 1, finishTicks = 3800 },
                raceId: "race-a");
            result.rulesFingerprint = catalog.CompatibilityFingerprint;

            // A supplied context bound to a different mode is a
            // conflict, never an override.
            var other = BoundContext(loaded, catalog, "race-a", "quickRace");
            var clash = RaceRewardService.Apply(result, loaded.id,
                loaded, store, catalog, other);
            Assert.That(clash.status,
                Is.EqualTo(RaceRewardService.StatusIneligible));
            Assert.That(clash.code, Is.EqualTo("contextMismatch"));

            // A result minted under different rules than the bound
            // context can never fold into them.
            var wrongRules = result.Copy();
            wrongRules.rulesFingerprint = new string('c', 64);
            var rules = RaceRewardService.Apply(wrongRules, loaded.id,
                loaded, store, catalog, durable);
            Assert.That(rules.status,
                Is.EqualTo(RaceRewardService.StatusIneligible));
            Assert.That(rules.code, Is.EqualTo("rulesMismatch"));

            Assert.That(loaded.coins, Is.EqualTo(0));
            Assert.That(PendingContext(loaded, "race-a"), Is.Not.Null);
        }

        [Test]
        public void BoundLanResultFoldsBoundBountiesAndPlacement()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = SeededProfile(catalog,
                Assignment("asg-finish", "finish-2", progress: 0));
            store.Save(profile);
            var loaded = store.Load(0).Profile;
            // LAN results are format 1 on the wire — no mode/rules fields —
            // but the durable bound context carries mode "lan" and the
            // rules fingerprint the race allocated under.
            var context = BoundContext(loaded, catalog, "lan-r1", "lan");
            PersistContext(store, loaded, context);

            var result = new RaceResult
            {
                formatVersion = 1,
                resultId = "lan-r1/result",
                sessionId = "s-1",
                raceId = "lan-r1",
                eventId = "lan-mixed",
                contentVersion = "lan-content-1",
                finalized = true,
                finalizedHostTick = 300,
                finalizedElapsedTicks = 4000,
                completionCoins = 20
            };
            result.racers.Add(new RaceResultEntry
            {
                position = 1, slot = 0, participantId = loaded.id,
                vehicleId = "car-01", attribution = "human",
                outcome = "finished", finishTicks = 3900,
                lapsCompleted = 2, coinsAwarded = 20
            });

            var applied = RaceRewardService.Apply(result, loaded.id,
                loaded, store, catalog, context);

            Assert.That(applied.status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            Assert.That(applied.mode, Is.EqualTo("lan"));
            // 20 authored completion + 5 first-place + one bound
            // human-finish fact (no bounty completion yet at 1/2).
            Assert.That(applied.awardedCoins, Is.EqualTo(25));
            var row = BountyRow(loaded, "asg-finish");
            Assert.That(row.progress, Is.EqualTo(1));
            Assert.That(PendingContext(loaded, "lan-r1"), Is.Null);
            Assert.That(ReceiptFor(loaded, "lan-r1/result")
                .rulesFingerprint,
                Is.EqualTo(catalog.CompatibilityFingerprint));
            // LAN never earns campaign stars.
            Assert.That(loaded.eventProgress.Count, Is.EqualTo(0));
        }

        [Test]
        public void BoundDnfConsumesContextWithoutAward()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = SeededProfile(catalog,
                Assignment("asg-finish", "finish-2", progress: 0));
            store.Save(profile);
            var loaded = store.Load(0).Profile;
            var context = BoundContext(loaded, catalog, "race-dnf", "campaign");
            PersistContext(store, loaded, context);

            var result = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-circuit", loaded.id, "dnf", 4,
                new RaceMetrics(), raceId: "race-dnf");
            result.rulesFingerprint = catalog.CompatibilityFingerprint;

            var applied = RaceRewardService.Apply(result, loaded.id,
                loaded, store, catalog, context);
            Assert.That(applied.status,
                Is.EqualTo(RaceRewardService.StatusIneligible));
            Assert.That(applied.code, Is.EqualTo("dnf"));
            Assert.That(loaded.coins, Is.EqualTo(0));
            // The race is over — its bound context is retired inside a
            // durable zero-award adjudication, not left pending.
            Assert.That(PendingContext(loaded, "race-dnf"), Is.Null);
            var receipt = ReceiptFor(loaded, result.resultId);
            Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.awardedCoins, Is.EqualTo(0));
            Assert.That(receipt.mode, Is.EqualTo("campaign"));

            var replay = RaceRewardService.Apply(result, loaded.id,
                loaded, store, catalog, null);
            Assert.That(replay.status,
                Is.EqualTo(RaceRewardService.StatusAlreadyApplied));
            Assert.That(replay.awardedCoins, Is.EqualTo(0));
        }

        [Test]
        public void RetriedResultUsesOriginalBoundAssignmentsOnly()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = SeededProfile(catalog,
                Assignment("asg-finish", "finish-2", progress: 1),
                Assignment("asg-boost", "boost-3", progress: 0),
                Assignment("asg-land", "landing-2", progress: 0));
            store.Save(profile);
            var loaded = store.Load(0).Profile;

            // Two races bind the same tray before either result lands.
            var contextA = BoundContext(loaded, catalog, "race-a", "quickRace");
            var contextB = BoundContext(loaded, catalog, "race-b", "quickRace");
            PersistContext(store, loaded, contextA);
            PersistContext(store, loaded, contextB);

            // Race A finishes: asg-finish completes and rotates out.
            var metrics = new RaceMetrics
            { position = 1, finishTicks = 3800, boostActivations = 1 };
            var resultA = CampaignTestFixture.Result("quickRace",
                "harbor-hop-car-circuit", loaded.id, "finished", 1,
                metrics, raceId: "race-a");
            resultA.rulesFingerprint = catalog.CompatibilityFingerprint;
            Assert.That(RaceRewardService.Apply(resultA, loaded.id,
                loaded, store, catalog, contextA).status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            var rotatedId = "";
            foreach (var row in loaded.activeBounties)
                if (row.bountyId != "boost-3" && row.bountyId != "landing-2")
                    rotatedId = row.assignmentId;
            Assert.That(rotatedId, Is.Not.EqualTo(""));

            // Race B's result lands late. Its bound ids still name the
            // ORIGINAL assignments — asg-finish is gone and its facts
            // have no landing place (it can never pay twice); the other
            // bound slots still fold normally.
            // +1 boost keeps asg-boost at 2/3 — folding works but no
            // second completion can hide inside this result.
            var resultB = CampaignTestFixture.Result("quickRace",
                "harbor-hop-car-circuit", loaded.id, "finished", 1,
                new RaceMetrics
                { position = 1, finishTicks = 3900, boostActivations = 1 },
                raceId: "race-b");
            resultB.rulesFingerprint = catalog.CompatibilityFingerprint;
            var appliedB = RaceRewardService.Apply(resultB, loaded.id,
                loaded, store, catalog, contextB);
            Assert.That(appliedB.status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            // finish-2 cannot complete again — its replacement never
            // received B's facts.
            var rotated = BountyRow(loaded, rotatedId);
            Assert.That(rotated, Is.Not.Null);
            Assert.That(rotated.progress, Is.EqualTo(0),
                "the rotated-in row was never fed the late result");
            Assert.That(BountyRow(loaded, "asg-boost").progress,
                Is.EqualTo(2));
            Assert.That(PendingContext(loaded, "race-b"), Is.Null);
        }

        [Test]
        public void DivergedBoundSlotPaysNothingAndRecordsDivergence()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = SeededProfile(catalog,
                Assignment("asg-finish", "finish-2", progress: 1),
                Assignment("asg-boost", "boost-3", progress: 0));
            store.Save(profile);
            var loaded = store.Load(0).Profile;
            var context = BoundContext(loaded, catalog, "race-a", "quickRace");
            PersistContext(store, loaded, context);
            // The slot was reassigned between bind and result: it no
            // longer holds the bounty the context bound.
            loaded.activeBounties[0].bountyId = "drift-10s";

            var result = CampaignTestFixture.Result("quickRace",
                "harbor-hop-car-circuit", loaded.id, "finished", 1,
                new RaceMetrics { position = 1, finishTicks = 3800 },
                raceId: "race-a");
            result.rulesFingerprint = catalog.CompatibilityFingerprint;
            var applied = RaceRewardService.Apply(result, loaded.id,
                loaded, store, catalog, context);
            Assert.That(applied.status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            Assert.That(applied.divergedAssignmentIds,
                Is.EquivalentTo(new[] { "asg-finish" }));
            Assert.That(applied.bountyCoins, Is.EqualTo(0),
                "a diverged slot can never pay a mismatched reward");
            Assert.That(BountyRow(loaded, "asg-finish").progress,
                Is.EqualTo(1), "facts never folded onto drift-10s");
        }

        // --- honesty of amounts -----------------------------------------

        [Test]
        public void ClientClaimedCoinsNeverPay()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            // finish-2 at 1/2 completes on this finish — the authored
            // 10-coin bounty joins the authored 20+5+30 the claims
            // cannot inflate.
            var profile = SeededProfile(catalog,
                Assignment("asg-finish", "finish-2", progress: 1));
            store.Save(profile);
            var loaded = store.Load(0).Profile;
            var context = BoundContext(loaded, catalog, "race-a", "campaign");
            PersistContext(store, loaded, context);

            var result = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-circuit", loaded.id, "finished", 1,
                new RaceMetrics
                { cleanLandings = 1, position = 1, finishTicks = 3800 },
                raceId: "race-a");
            result.rulesFingerprint = catalog.CompatibilityFingerprint;
            // A crafted payload claims an absurd reward — the authored
            // event/economy facts are what pay.
            result.completionCoins = 9999;
            result.racers[0].coinsAwarded = 9999;

            var applied = RaceRewardService.Apply(result, loaded.id,
                loaded, store, catalog, context);
            Assert.That(applied.status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            Assert.That(applied.awardedCoins, Is.EqualTo(65));
            Assert.That(loaded.coins, Is.EqualTo(65));
        }

        [Test]
        public void PlacementBonusComesFromAuthoredTable()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = SeededProfile(catalog,
                Assignment("asg-finish", "finish-2", progress: 0));
            store.Save(profile);
            var loaded = store.Load(0).Profile;

            var positions = new[] { 1, 2, 3, 4 };
            var expected = new[] { 5, 3, 1, 0 };
            for (var i = 0; i < positions.Length; i++)
            {
                var raceId = "race-p" + i;
                PersistContext(store, loaded,
                    BoundContext(loaded, catalog, raceId, "quickRace"));
                var result = CampaignTestFixture.Result("quickRace",
                    "harbor-hop-car-circuit", loaded.id, "finished",
                    positions[i],
                    new RaceMetrics
                    { position = positions[i], finishTicks = 3800 + i },
                    raceId: raceId);
                result.rulesFingerprint = catalog.CompatibilityFingerprint;
                var applied = RaceRewardService.Apply(result, loaded.id,
                    loaded, store, catalog, null);
                Assert.That(applied.status,
                    Is.EqualTo(RaceRewardService.StatusApplied));
                Assert.That(applied.placementCoins, Is.EqualTo(expected[i]),
                    "position " + positions[i]);
            }
        }

        [Test]
        public void OverflowRejectsWholeAwardBeforeAnyWrite()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = SeededProfile(catalog,
                Assignment("asg-finish", "finish-2", progress: 1));
            profile.coins = int.MaxValue - 10;
            store.Save(profile);
            var onDisk = File.ReadAllBytes(Primary(0));
            var loaded = store.Load(0).Profile;
            var context = BoundContext(loaded, catalog, "race-a", "campaign");
            PersistContext(store, loaded, context);
            onDisk = File.ReadAllBytes(Primary(0));
            var liveBefore = CampaignTestFixture.ProfileSnapshot(loaded);

            var result = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-circuit", loaded.id, "finished", 1,
                new RaceMetrics
                { cleanLandings = 1, position = 1, finishTicks = 3800 },
                raceId: "race-a");
            result.rulesFingerprint = catalog.CompatibilityFingerprint;
            var applied = RaceRewardService.Apply(result, loaded.id,
                loaded, store, catalog, context);

            Assert.That(applied.status,
                Is.EqualTo(RaceRewardService.StatusFailed));
            Assert.That(applied.code, Is.EqualTo("overflow"));
            Assert.That(loaded.coins, Is.EqualTo(int.MaxValue - 10));
            Assert.That(loaded.appliedResults.Count, Is.EqualTo(0));
            Assert.That(PendingContext(loaded, "race-a"), Is.Not.Null,
                "the context stays pending for an honest verdict");
            Assert.That(
                CampaignTestFixture.ProfileSnapshot(loaded),
                Is.EqualTo(liveBefore));
            Assert.That(File.ReadAllBytes(Primary(0)),
                Is.EqualTo(onDisk));
        }

        // --- unresolved-transaction gating (service level) --------------

        [Test]
        public void UnresolvedRewardBlocksNextProgressionRaceUntilRetry()
        {
            var catalog = CampaignTestFixture.Catalog();
            var serializer = new TogglingSerializer();
            var failingStore = new ProfileStore(directory, serializer);
            var service = new FoundationService(failingStore, "en",
                "prog-test");
            service.SetDrivingContent(catalog.Driving);
            service.SetGameplayCatalog(catalog);
            Assert.That(service.Execute(Cmd("profile.create",
                P("slot", "0", "language", "en"))).status,
                Is.EqualTo(CommandResult.Applied));

            // A LAN session wired to the service: the host binds its
            // context and publishes a finished result for itself.
            var link = new LanSessionTests.FakeLanLink();
            var lan = LanSessionTests.StandaloneService(link,
                new LanSessionTests.FakeLanProbe(),
                service.Snapshot().savedProfile.id);
            service.SetLanSession(lan);
            Assert.That(service.Execute(Cmd("lan.host",
                P("port", "47777"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(lan.Execute(Cmd("lan.configure",
                P("eventId", "harbor-hop-car-circuit",
                    "classId", "car"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(lan.Execute(Cmd("lan.ready",
                P("ready", "true"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(lan.Execute(Cmd("lan.start", null)).status,
                Is.EqualTo(CommandResult.Applied));
            var raceId = lan.Snapshot().currentRaceId;
            Assert.That(raceId, Is.Not.EqualTo(""));

            // The finalize fails to persist: the transaction is now
            // unresolved. Leaving the LAN session keeps it unresolved —
            // the retained result survives the session for retry — and
            // a new progression race must refuse.
            serializer.failing = true;
            var result = LanResult(raceId,
                service.Snapshot().savedProfile.id);
            Assert.That(lan.PublishResult(result), Is.True);
            Assert.That(service.RewardUnresolved, Is.True);
            Assert.That(lan.Execute(Cmd("lan.leave", null)).status,
                Is.EqualTo(CommandResult.Applied));
            var blocked = service.Execute(Cmd("solo.start",
                P("mode", "campaign",
                    "eventId", "harbor-hop-car-circuit",
                    "vehicleId", "car-01")));
            Assert.That(blocked.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(blocked.code, Is.EqualTo("rewardUnresolved"));

            // The retry resolves against the ORIGINAL bound context —
            // exactly once.
            serializer.failing = false;
            var retried = service.RetryUnresolvedRewards();
            Assert.That(retried.status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            Assert.That(service.RewardUnresolved, Is.False);
            Assert.That(service.Snapshot().savedProfile.coins,
                Is.EqualTo(25));
            var started = service.Execute(Cmd("solo.start",
                P("mode", "campaign",
                    "eventId", "harbor-hop-car-circuit",
                    "vehicleId", "car-01")));
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied),
                "the resolved transaction frees the start: " + started.code);
        }

        // Review fix round 1: a conflicting redelivery is a TERMINAL
        // failure — the durable receipt owns that resultId forever, so
        // it must never latch the unresolved gate. Only saveFailed is
        // retryable.
        [Test]
        public void ConflictingRedeliveryNeverLatchesUnresolvedGate()
        {
            var catalog = CampaignTestFixture.Catalog();
            var service = new FoundationService(Store(), "en", "prog-test");
            service.SetDrivingContent(catalog.Driving);
            service.SetGameplayCatalog(catalog);
            Assert.That(service.Execute(Cmd("profile.create",
                P("slot", "0", "language", "en"))).status,
                Is.EqualTo(CommandResult.Applied));
            var participantId = service.Snapshot().savedProfile.id;

            // Session 1 binds and publishes a finished result — the
            // receipt and context consumption commit together.
            var link = new LanSessionTests.FakeLanLink();
            var lan = LanSessionTests.StandaloneService(link,
                new LanSessionTests.FakeLanProbe(), participantId);
            service.SetLanSession(lan);
            Assert.That(service.Execute(Cmd("lan.host",
                P("port", "47777"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(lan.Execute(Cmd("lan.configure",
                P("eventId", "harbor-hop-car-circuit",
                    "classId", "car"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(lan.Execute(Cmd("lan.ready",
                P("ready", "true"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(lan.Execute(Cmd("lan.start", null)).status,
                Is.EqualTo(CommandResult.Applied));
            var raceId = lan.Snapshot().currentRaceId;
            Assert.That(lan.PublishResult(LanResult(raceId,
                participantId)), Is.True);
            Assert.That(service.Snapshot().savedProfile.coins,
                Is.EqualTo(25));
            Assert.That(lan.Execute(Cmd("lan.leave", null)).status,
                Is.EqualTo(CommandResult.Applied));

            // A second session (fresh retention ledger) delivers a
            // CONFLICTING result under the same resultId: the durable
            // receipt rejects it before any context evaluation. The
            // terminal failure must leave the gate free.
            var link2 = new LanSessionTests.FakeLanLink();
            var lan2 = LanSessionTests.StandaloneService(link2,
                new LanSessionTests.FakeLanProbe(), participantId);
            service.SetLanSession(lan2);
            Assert.That(service.Execute(Cmd("lan.host",
                P("port", "47778"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(lan2.Execute(Cmd("lan.configure",
                P("eventId", "harbor-hop-car-circuit",
                    "classId", "car"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(lan2.Execute(Cmd("lan.ready",
                P("ready", "true"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(lan2.Execute(Cmd("lan.start", null)).status,
                Is.EqualTo(CommandResult.Applied));
            var conflict = LanResult(raceId, participantId);
            // Same resultId, different immutable facts → different
            // fingerprint → the stored receipt rejects the replay.
            conflict.finalizedHostTick = 301;
            Assert.That(lan2.PublishResult(conflict), Is.True);
            Assert.That(service.RewardUnresolved, Is.False,
                "a terminal conflict must never latch the retry gate");
            Assert.That(lan2.Execute(Cmd("lan.leave", null)).status,
                Is.EqualTo(CommandResult.Applied));

            // A legitimate progression race starts immediately after.
            var started = service.Execute(Cmd("solo.start",
                P("mode", "campaign",
                    "eventId", "harbor-hop-car-circuit",
                    "vehicleId", "car-01")));
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied),
                "conflicting redelivery wedged progression: "
                    + started.code);
        }

        // Review fix round 1: a bound DNF COMMITS (receipt + context
        // retirement land inside one save) even though the verdict is
        // ineligible. The slot cache must observe the commit, or a
        // profile reselect resurrects the consumed context and drops
        // the receipt.
        [Test]
        public void BoundDnfCommitSurvivesProfileReselect()
        {
            var catalog = CampaignTestFixture.Catalog();
            var service = new FoundationService(Store(), "en", "prog-test");
            service.SetDrivingContent(catalog.Driving);
            service.SetGameplayCatalog(catalog);
            Assert.That(service.Execute(Cmd("profile.create",
                P("slot", "0", "language", "en"))).status,
                Is.EqualTo(CommandResult.Applied));
            var participantId = service.Snapshot().savedProfile.id;

            var link = new LanSessionTests.FakeLanLink();
            var lan = LanSessionTests.StandaloneService(link,
                new LanSessionTests.FakeLanProbe(), participantId);
            service.SetLanSession(lan);
            Assert.That(service.Execute(Cmd("lan.host",
                P("port", "47777"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(lan.Execute(Cmd("lan.configure",
                P("eventId", "harbor-hop-car-circuit",
                    "classId", "car"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(lan.Execute(Cmd("lan.ready",
                P("ready", "true"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(lan.Execute(Cmd("lan.start", null)).status,
                Is.EqualTo(CommandResult.Applied));
            var raceId = lan.Snapshot().currentRaceId;

            // The bound race ends in a DNF: a zero-award adjudication
            // commits the receipt and retires the context atomically.
            var dnf = LanResult(raceId, participantId);
            dnf.racers[0].outcome = "dnf";
            dnf.racers[0].position = 4;
            dnf.racers[0].finishTicks = 0;
            dnf.racers[0].coinsAwarded = 0;
            Assert.That(lan.PublishResult(dnf), Is.True);
            var live = service.Snapshot().savedProfile;
            Assert.That(live.coins, Is.EqualTo(0));
            Assert.That(PendingContext(live, raceId), Is.Null);
            Assert.That(ReceiptFor(live, raceId + "/result"),
                Is.Not.Null);
            Assert.That(lan.Execute(Cmd("lan.leave", null)).status,
                Is.EqualTo(CommandResult.Applied));

            // Reselect reloads from the slot cache: it must observe the
            // committed adjudication, not the pre-award snapshot.
            Assert.That(service.Execute(Cmd("profile.select",
                P("slot", "0"))).status,
                Is.EqualTo(CommandResult.Applied));
            var reloaded = service.Snapshot().savedProfile;
            Assert.That(PendingContext(reloaded, raceId), Is.Null,
                "the consumed context resurrected through the slot cache");
            var receipt = ReceiptFor(reloaded, raceId + "/result");
            Assert.That(receipt, Is.Not.Null,
                "the committed DNF receipt vanished on reselect");
            Assert.That(receipt.awardedCoins, Is.EqualTo(0));
            Assert.That(receipt.mode, Is.EqualTo("lan"));
        }

        // --- helpers ----------------------------------------------------

        private ProfileStore Store()
        {
            return new ProfileStore(directory, serializer);
        }

        private string Primary(int slot)
        {
            return Path.Combine(directory, "profile-" + slot + ".json");
        }

        private static BountyAssignment Assignment(string assignmentId,
            string bountyId, long progress)
        {
            return new BountyAssignment
            {
                assignmentId = assignmentId, bountyId = bountyId,
                progress = progress
            };
        }

        // A valid profile with the given tray — assignment ids here are
        // fixed so tests can bind contexts deterministically.
        private static ProfileSave SeededProfile(GameplayCatalog catalog,
            params BountyAssignment[] rows)
        {
            var profile = CampaignTestFixture.Profile();
            profile.activeBounties = new List<BountyAssignment>(rows);
            return profile;
        }

        // A profile one finale away from completing Sunny Island: all
        // 24 regular events at full marks and the motorcycle finale
        // completed (not victorious).
        private static ProfileSave WorldAlmostDone(GameplayCatalog catalog)
        {
            var profile = CampaignTestFixture.Profile();
            foreach (var eventId in catalog.Campaign.regularEventIds)
                profile.eventProgress.Add(new EventProgress
                {
                    eventId = eventId, completed = true,
                    objectiveMask = ObjectiveEvaluator.FullMask
                });
            profile.finaleProgress.Add(new FinaleProgress
            {
                eventId = "sunny-island-motorcycle-finale",
                completed = true, victorious = false
            });
            return profile;
        }

        private static RaceResult FinaleVictoryResult(ProfileSave profile,
            GameplayCatalog catalog, string raceId)
        {
            var result = CampaignTestFixture.Result("campaign",
                "sunny-island-car-finale", profile.id, "finished", 1,
                new RaceMetrics
                { position = 1, finishTicks = 3800, rivalDefeated = true },
                raceId: raceId);
            result.rulesFingerprint = catalog.CompatibilityFingerprint;
            return result;
        }

        private static RaceResult LanResult(string raceId,
            string participantId)
        {
            var result = new RaceResult
            {
                formatVersion = 1,
                resultId = raceId + "/result",
                sessionId = "lan-s", raceId = raceId,
                eventId = "harbor-hop-car-circuit",
                contentVersion = "gameplay-loop-1",
                finalized = true, finalizedHostTick = 300,
                finalizedElapsedTicks = 4000, completionCoins = 20
            };
            result.racers.Add(new RaceResultEntry
            {
                position = 1, slot = 0, participantId = participantId,
                vehicleId = "car-01", attribution = "human",
                outcome = "finished", finishTicks = 3900,
                lapsCompleted = 2, coinsAwarded = 20
            });
            return result;
        }

        // Builds the context exactly as the Task-4 bind does — the
        // profile's current tray ids plus their catalog definitions —
        // under the catalog's own rules fingerprint.
        private static RaceRewardContext BoundContext(ProfileSave profile,
            GameplayCatalog catalog, string raceId, string mode)
        {
            var context = new RaceRewardContext
            {
                raceId = raceId,
                profileId = profile.id,
                mode = mode,
                rulesFingerprint = catalog.CompatibilityFingerprint
            };
            foreach (var row in profile.activeBounties)
            {
                BountyDefinition def = null;
                foreach (var candidate in catalog.Bounties)
                    if (candidate != null && candidate.id == row.bountyId)
                        def = candidate;
                Assert.That(def, Is.Not.Null,
                    "fixture bounty must exist: " + row.bountyId);
                context.bountyAssignmentIds.Add(row.assignmentId);
                context.bountyDefinitions.Add(def.Copy());
            }
            return context;
        }

        // The durable half of the bind: the row rides inside one profile
        // save exactly like FoundationService.BindRewardContext writes it.
        private void PersistContext(ProfileStore store, ProfileSave profile,
            RaceRewardContext context)
        {
            profile.pendingRaceContexts.Add(context.Copy());
            store.Save(profile);
        }

        private static EventProgress ProgressionRow(ProfileSave profile,
            string eventId)
        {
            foreach (var row in profile.eventProgress)
                if (row.eventId == eventId) return row;
            return null;
        }

        private static FinaleProgress FinaleRow(ProfileSave profile,
            string eventId)
        {
            foreach (var row in profile.finaleProgress)
                if (row.eventId == eventId) return row;
            return null;
        }

        private static WorldProgress WorldRow(ProfileSave profile,
            string worldId)
        {
            foreach (var row in profile.worldProgress)
                if (row.worldId == worldId) return row;
            return null;
        }

        private static BountyAssignment BountyRow(ProfileSave profile,
            string assignmentId)
        {
            foreach (var row in profile.activeBounties)
                if (row.assignmentId == assignmentId) return row;
            return null;
        }

        private static RaceRewardContext PendingContext(ProfileSave profile,
            string raceId)
        {
            foreach (var row in profile.pendingRaceContexts)
                if (row != null && row.raceId == raceId) return row;
            return null;
        }

        private static AppliedResultReceipt ReceiptFor(ProfileSave profile,
            string resultId)
        {
            foreach (var row in profile.appliedResults)
                if (row != null && row.resultId == resultId) return row;
            return null;
        }

        private static GameCommand Cmd(string action,
            Dictionary<string, string> parameters)
        {
            return new GameCommand(1, "prog-test",
                Guid.NewGuid().ToString("N"), "local", action, parameters);
        }

        private static Dictionary<string, string> P(params string[] keyValues)
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i + 1 < keyValues.Length; i += 2)
                map[keyValues[i]] = keyValues[i + 1];
            return map;
        }

        private class TestJsonSerializer : IProfileSerializer
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

        // A serializer that can be switched into a failing mode to prove
        // retryable transactions — a save failure at the write path is
        // indistinguishable from a locked file.
        private sealed class TogglingSerializer : IProfileSerializer
        {
            internal bool failing;
            private readonly TestJsonSerializer inner =
                new TestJsonSerializer();

            public string Serialize(ProfileSave profile)
            {
                if (failing) throw new InvalidOperationException("down");
                return inner.Serialize(profile);
            }

            public ProfileSave Deserialize(string json)
            {
                return inner.Deserialize(json);
            }
        }
    }
}
