using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Foundation;
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
    // M5 Task 8: durable vehicle-upgrade purchases and cosmetic
    // equipment. One purchase commits — inside ONE atomic profile save —
    // the exact authored coin debit, the category level on the per-
    // vehicle row and the durable receipt that owns the purchaseId
    // forever. Identical replays are idempotent; conflicting tuples
    // reject; every failure leaves live profile and disk unchanged.
    public sealed class UpgradeTests
    {
        private const string RunId = "upgrade-test";
        private string directory;
        private IProfileSerializer serializer;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "family-racing-upgrade-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            serializer = new TestJsonSerializer();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        private ProfileStore Store()
        {
            return new ProfileStore(directory, serializer);
        }

        private string Primary(int slot)
        {
            return Path.Combine(directory, "profile-" + slot + ".json");
        }

        private static ProfileSave Profile()
        {
            return CampaignTestFixture.Profile();
        }

        private static GameplayCatalog Catalog()
        {
            return CampaignTestFixture.Catalog();
        }

        // The shared fixture maps every category's tuningFields to
        // "acceleration"; this variant carries the authored conventions —
        // acceleration -> [acceleration], handling -> [steeringRate,grip],
        // boost -> [boostAcceleration,boostMaxSpeed].
        private static GameplayCatalog MappedCatalog()
        {
            var baseCatalog = Catalog();
            var economy = baseCatalog.Economy.Copy();
            economy.upgradeCategories.Clear();
            economy.upgradeCategories.Add(Category("acceleration",
                "acceleration"));
            economy.upgradeCategories.Add(Category("handling",
                "steeringRate", "grip"));
            economy.upgradeCategories.Add(Category("boost",
                "boostAcceleration", "boostMaxSpeed"));
            var ok = GameplayCatalog.TryCreate(baseCatalog.Driving,
                baseCatalog.Campaign, baseCatalog.Bounties, economy,
                baseCatalog.Gadgets, baseCatalog.Difficulties,
                out var catalog, out var error);
            Assert.That(ok, Is.True, "mapped catalog: " + error);
            return catalog;
        }

        private static UpgradeCategoryDefinition Category(string id,
            params string[] fields)
        {
            var category = new UpgradeCategoryDefinition
            {
                id = id, maxLevel = 3
            };
            category.prices.Add(60);
            category.prices.Add(120);
            category.prices.Add(180);
            category.tuningFields.AddRange(fields);
            return category;
        }

        private static VehicleUpgrade UpgradeRow(ProfileSave profile,
            string vehicleId)
        {
            if (profile.vehicleUpgrades == null) return null;
            foreach (var row in profile.vehicleUpgrades)
                if (row != null && row.vehicleId == vehicleId) return row;
            return null;
        }

        private static PurchaseReceipt Receipt(ProfileSave profile,
            string purchaseId)
        {
            if (profile.purchaseReceipts == null) return null;
            foreach (var row in profile.purchaseReceipts)
                if (row != null && row.purchaseId == purchaseId) return row;
            return null;
        }

        // Field-exact progression snapshot for "nothing mutated" checks.
        private static string ProgressionSnapshot(ProfileSave p)
        {
            var b = new System.Text.StringBuilder(256);
            b.Append(p.coins).Append('|');
            if (p.vehicleUpgrades != null)
                foreach (var row in p.vehicleUpgrades)
                    b.Append(row == null ? "n;" : row.vehicleId + ":"
                        + row.acceleration + ":" + row.handling + ":"
                        + row.boost + ";");
            b.Append('#');
            if (p.purchaseReceipts != null)
                foreach (var row in p.purchaseReceipts)
                    b.Append(row == null ? "n;" : row.purchaseId + ":"
                        + row.kind + ":" + row.vehicleId + ":"
                        + row.category + ":" + row.level + ":"
                        + row.costCoins + ";");
            b.Append('#');
            if (p.ownedCosmetics != null)
                b.Append(string.Join(",", p.ownedCosmetics));
            b.Append('#');
            if (p.equippedCosmetics != null)
                b.Append(string.Join(",", p.equippedCosmetics));
            return b.ToString();
        }

        // --- exact authored prices and the level cap ----------------------

        [Test]
        public void PriceBoundariesChargeTheExactAuthoredCost()
        {
            var catalog = Catalog();
            var store = Store();
            var profile = Profile();

            // One coin short of level 1: an honest refusal that mutates
            // nothing.
            profile.coins = 59;
            var denied = UpgradeService.Purchase("p-short", "car-01",
                "acceleration", 1, profile, store, catalog);
            Assert.That(denied.status,
                Is.EqualTo(UpgradeService.StatusRejected));
            Assert.That(denied.code, Is.EqualTo("insufficientCoins"));
            Assert.That(denied.committed, Is.False);
            Assert.That(profile.coins, Is.EqualTo(59));
            Assert.That(UpgradeRow(profile, "car-01"), Is.Null);
            Assert.That(Receipt(profile, "p-short"), Is.Null);

            // Exactly the authored level-1 price buys the level and
            // leaves the wallet empty.
            profile.coins = 60;
            var bought = UpgradeService.Purchase("p-1", "car-01",
                "acceleration", 1, profile, store, catalog);
            Assert.That(bought.status,
                Is.EqualTo(UpgradeService.StatusApplied));
            Assert.That(bought.committed, Is.True);
            Assert.That(bought.level, Is.EqualTo(1));
            Assert.That(bought.costCoins, Is.EqualTo(60));
            Assert.That(profile.coins, Is.EqualTo(0));
            Assert.That(UpgradeRow(profile, "car-01").acceleration,
                Is.EqualTo(1));

            // Levels 2 and 3 charge their own authored prices.
            profile.coins = 300;
            var second = UpgradeService.Purchase("p-2", "car-01",
                "acceleration", 2, profile, store, catalog);
            Assert.That(second.status,
                Is.EqualTo(UpgradeService.StatusApplied));
            Assert.That(second.costCoins, Is.EqualTo(120));
            Assert.That(profile.coins, Is.EqualTo(180));
            var third = UpgradeService.Purchase("p-3", "car-01",
                "acceleration", 3, profile, store, catalog);
            Assert.That(third.status,
                Is.EqualTo(UpgradeService.StatusApplied));
            Assert.That(third.costCoins, Is.EqualTo(180));
            Assert.That(profile.coins, Is.EqualTo(0));
            Assert.That(UpgradeRow(profile, "car-01").acceleration,
                Is.EqualTo(3));

            // A fourth level is beyond the authored cap and refuses.
            profile.coins = 1000;
            var capped = UpgradeService.Purchase("p-4", "car-01",
                "acceleration", 4, profile, store, catalog);
            Assert.That(capped.status,
                Is.EqualTo(UpgradeService.StatusRejected));
            Assert.That(capped.code, Is.EqualTo("levelCap"));
            Assert.That(profile.coins, Is.EqualTo(1000));
            Assert.That(UpgradeRow(profile, "car-01").acceleration,
                Is.EqualTo(3));
        }

        [Test]
        public void MaxLevelMultiplierScalesOnlyAuthoredFields()
        {
            var catalog = MappedCatalog();
            var store = Store();
            var profile = Profile();
            profile.coins = 2000;
            for (var level = 1; level <= 3; level++)
                Assert.That(UpgradeService.Purchase("p-a" + level,
                        "car-01", "acceleration", level, profile, store,
                        catalog).status,
                    Is.EqualTo(UpgradeService.StatusApplied));
            var baseline = catalog.Driving.Vehicles["car-01"];
            var resolved = VehicleTuningResolver.Resolve(RaceMode.Campaign,
                "car-01", profile, catalog);
            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved, Is.Not.SameAs(baseline),
                "the resolved tune must be a fresh object");
            var multiplier = catalog.Economy.Category("acceleration")
                .MultiplierForLevel(3);
            Assert.That(multiplier, Is.EqualTo(1.15f).Within(0.0001f),
                "level-3 multiplier must reach the authored 1.15");
            Assert.That(resolved.acceleration,
                Is.EqualTo(baseline.acceleration * multiplier)
                    .Within(0.001f));
            // Untouched by the acceleration category: handling/boost
            // fields, base top speed, boost economy and geometry stay
            // exactly the authored baseline.
            Assert.That(resolved.steeringRate,
                Is.EqualTo(baseline.steeringRate));
            Assert.That(resolved.grip, Is.EqualTo(baseline.grip));
            Assert.That(resolved.boostAcceleration,
                Is.EqualTo(baseline.boostAcceleration));
            Assert.That(resolved.boostMaxSpeed,
                Is.EqualTo(baseline.boostMaxSpeed));
            Assert.That(resolved.maxSpeed, Is.EqualTo(baseline.maxSpeed),
                "base top speed must never upgrade");
            Assert.That(resolved.boostDurationSeconds,
                Is.EqualTo(baseline.boostDurationSeconds));
            Assert.That(resolved.boostChargeSeconds,
                Is.EqualTo(baseline.boostChargeSeconds));
            Assert.That(resolved.halfWidth,
                Is.EqualTo(baseline.halfWidth),
                "collision geometry must never upgrade");
            Assert.That(resolved.halfLength,
                Is.EqualTo(baseline.halfLength));
        }

        [Test]
        public void CategoryLevelsComposeOnTheirOwnFields()
        {
            var catalog = MappedCatalog();
            var store = Store();
            var profile = Profile();
            profile.coins = 2000;
            Assert.That(UpgradeService.Purchase("p-h1", "car-01",
                    "handling", 1, profile, store, catalog).status,
                Is.EqualTo(UpgradeService.StatusApplied));
            Assert.That(UpgradeService.Purchase("p-b1", "car-01",
                    "boost", 1, profile, store, catalog).status,
                Is.EqualTo(UpgradeService.StatusApplied));
            var baseline = catalog.Driving.Vehicles["car-01"];
            var resolved = VehicleTuningResolver.Resolve(RaceMode.QuickRace,
                "car-01", profile, catalog);
            var step = 0.05f;
            Assert.That(resolved.acceleration,
                Is.EqualTo(baseline.acceleration),
                "an unbought category contributes nothing");
            Assert.That(resolved.steeringRate,
                Is.EqualTo(baseline.steeringRate * (1f + step))
                    .Within(0.001f));
            Assert.That(resolved.grip,
                Is.EqualTo(baseline.grip * (1f + step)).Within(0.001f));
            Assert.That(resolved.boostAcceleration,
                Is.EqualTo(baseline.boostAcceleration * (1f + step))
                    .Within(0.001f));
            Assert.That(resolved.boostMaxSpeed,
                Is.EqualTo(baseline.boostMaxSpeed * (1f + step))
                    .Within(0.001f));
        }

        // --- durable purchase receipts -------------------------------------

        [Test]
        public void DuplicatePurchaseIdNeverChargesTwice()
        {
            var catalog = Catalog();
            var store = Store();
            var profile = Profile();
            profile.coins = 60;
            Assert.That(UpgradeService.Purchase("p-dup", "car-01",
                    "acceleration", 1, profile, store, catalog).status,
                Is.EqualTo(UpgradeService.StatusApplied));
            Assert.That(profile.coins, Is.EqualTo(0));

            // The identical replay is an idempotent no-op: same level,
            // no second charge, still one receipt.
            var replay = UpgradeService.Purchase("p-dup", "car-01",
                "acceleration", 1, profile, store, catalog);
            Assert.That(replay.status,
                Is.EqualTo(UpgradeService.StatusAlreadyApplied));
            Assert.That(replay.committed, Is.True);
            Assert.That(replay.level, Is.EqualTo(1));
            Assert.That(replay.costCoins, Is.EqualTo(60));
            Assert.That(profile.coins, Is.EqualTo(0));
            Assert.That(UpgradeRow(profile, "car-01").acceleration,
                Is.EqualTo(1));
            var receipts = 0;
            foreach (var row in profile.purchaseReceipts)
                if (row != null && row.purchaseId == "p-dup") receipts++;
            Assert.That(receipts, Is.EqualTo(1));
        }

        [Test]
        public void ConflictingPurchaseIdRejects()
        {
            var catalog = Catalog();
            var store = Store();
            var profile = Profile();
            profile.coins = 2000;
            Assert.That(UpgradeService.Purchase("p-own", "car-01",
                    "acceleration", 1, profile, store, catalog).status,
                Is.EqualTo(UpgradeService.StatusApplied));
            var coinsAfter = profile.coins;

            // Same purchaseId, different tuple: a conflicting replay is
            // rejected even though the money is there.
            var otherCategory = UpgradeService.Purchase("p-own",
                "car-01", "handling", 1, profile, store, catalog);
            Assert.That(otherCategory.status,
                Is.EqualTo(UpgradeService.StatusRejected));
            Assert.That(otherCategory.code, Is.EqualTo("conflict"));
            var otherLevel = UpgradeService.Purchase("p-own",
                "car-01", "acceleration", 2, profile, store, catalog);
            Assert.That(otherLevel.status,
                Is.EqualTo(UpgradeService.StatusRejected));
            Assert.That(otherLevel.code, Is.EqualTo("conflict"));
            var otherVehicle = UpgradeService.Purchase("p-own",
                "motorcycle-01", "acceleration", 1, profile, store,
                catalog);
            Assert.That(otherVehicle.status,
                Is.EqualTo(UpgradeService.StatusRejected));
            Assert.That(otherVehicle.code, Is.EqualTo("conflict"));
            Assert.That(profile.coins, Is.EqualTo(coinsAfter));
            Assert.That(UpgradeRow(profile, "car-01").acceleration,
                Is.EqualTo(1));
            Assert.That(UpgradeRow(profile, "motorcycle-01"), Is.Null);
        }

        [Test]
        public void StaleExpectedLevelCannotBuyNextLevelOnRetry()
        {
            var catalog = Catalog();
            var store = Store();
            var profile = Profile();
            profile.coins = 2000;
            Assert.That(UpgradeService.Purchase("p-1", "car-01",
                    "acceleration", 1, profile, store, catalog).status,
                Is.EqualTo(UpgradeService.StatusApplied));

            // A retry that still believes the wallet is at level 0
            // cannot re-buy level 1 under a NEW purchaseId — the level
            // progression itself refuses, independent of receipts.
            var stale = UpgradeService.Purchase("p-2", "car-01",
                "acceleration", 1, profile, store, catalog);
            Assert.That(stale.status,
                Is.EqualTo(UpgradeService.StatusRejected));
            Assert.That(stale.code, Is.EqualTo("staleExpectedLevel"));
            // Skipping a level refuses too: only the exact next level
            // may be purchased.
            var skip = UpgradeService.Purchase("p-3", "car-01",
                "acceleration", 3, profile, store, catalog);
            Assert.That(skip.status,
                Is.EqualTo(UpgradeService.StatusRejected));
            Assert.That(skip.code, Is.EqualTo("staleExpectedLevel"));
            Assert.That(UpgradeRow(profile, "car-01").acceleration,
                Is.EqualTo(1));
        }

        [Test]
        public void PurchaseSurvivesRestart()
        {
            var catalog = Catalog();
            var profile = Profile();
            profile.coins = 120;
            var store = Store();
            Assert.That(UpgradeService.Purchase("p-rest", "motorcycle-01",
                    "boost", 1, profile, store, catalog).status,
                Is.EqualTo(UpgradeService.StatusApplied));
            Assert.That(profile.coins, Is.EqualTo(60));

            // A cold reload sees debit + level + receipt as one unit —
            // then the same purchaseId is idempotent against the
            // durable ledger, never a second charge.
            var reloaded = new ProfileStore(directory, serializer)
                .Load(profile.slot).Profile;
            Assert.That(reloaded, Is.Not.Null);
            Assert.That(reloaded.coins, Is.EqualTo(60));
            Assert.That(UpgradeRow(reloaded, "motorcycle-01").boost,
                Is.EqualTo(1));
            var receipt = Receipt(reloaded, "p-rest");
            Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.kind, Is.EqualTo("upgrade"));
            Assert.That(receipt.vehicleId, Is.EqualTo("motorcycle-01"));
            Assert.That(receipt.category, Is.EqualTo("boost"));
            Assert.That(receipt.level, Is.EqualTo(1));
            Assert.That(receipt.costCoins, Is.EqualTo(60));
            var replay = UpgradeService.Purchase("p-rest",
                "motorcycle-01", "boost", 1, reloaded, store, catalog);
            Assert.That(replay.status,
                Is.EqualTo(UpgradeService.StatusAlreadyApplied));
            Assert.That(reloaded.coins, Is.EqualTo(60));
        }

        [Test]
        public void FailedSaveMutatesNeitherProfileNorDisk()
        {
            var catalog = Catalog();
            var toggling = new TogglingSerializer();
            var store = new ProfileStore(directory, toggling);
            var profile = Profile();
            profile.coins = 60;
            store.Save(profile);
            var disk = File.ReadAllBytes(Primary(0));
            var before = ProgressionSnapshot(profile);

            toggling.failing = true;
            var failed = UpgradeService.Purchase("p-fail", "car-01",
                "acceleration", 1, profile, store, catalog);
            Assert.That(failed.status,
                Is.EqualTo(UpgradeService.StatusFailed));
            Assert.That(failed.code, Is.EqualTo("saveFailed"));
            Assert.That(failed.committed, Is.False);
            Assert.That(ProgressionSnapshot(profile), Is.EqualTo(before),
                "a failed save must leave the live profile untouched");
            Assert.That(File.ReadAllBytes(Primary(0)), Is.EqualTo(disk),
                "a failed save must leave disk byte-identical");

            // The same purchaseId remains usable — the failed attempt
            // never wrote a receipt that would own it.
            toggling.failing = false;
            var retried = UpgradeService.Purchase("p-fail", "car-01",
                "acceleration", 1, profile, store, catalog);
            Assert.That(retried.status,
                Is.EqualTo(UpgradeService.StatusApplied));
            Assert.That(profile.coins, Is.EqualTo(0));
        }

        // --- purchase eligibility -------------------------------------------

        [Test]
        public void LockedUnknownAndMalformedPurchasesReject()
        {
            var catalog = Catalog();
            var store = Store();
            var profile = Profile();
            profile.coins = 2000;
            var before = ProgressionSnapshot(profile);

            // car-02 is not in the factory profile's unlockedVehicles.
            var locked = UpgradeService.Purchase("p-l", "car-02",
                "acceleration", 1, profile, store, catalog);
            Assert.That(locked.status,
                Is.EqualTo(UpgradeService.StatusRejected));
            Assert.That(locked.code, Is.EqualTo("vehicleLocked"));

            var unknownCategory = UpgradeService.Purchase("p-c",
                "car-01", "wings", 1, profile, store, catalog);
            Assert.That(unknownCategory.status,
                Is.EqualTo(UpgradeService.StatusRejected));
            Assert.That(unknownCategory.code, Is.EqualTo("unknownCategory"));

            var unknownVehicle = UpgradeService.Purchase("p-v",
                "car-99", "acceleration", 1, profile, store, catalog);
            Assert.That(unknownVehicle.status,
                Is.EqualTo(UpgradeService.StatusRejected));
            Assert.That(unknownVehicle.code, Is.EqualTo("unknownVehicle"));

            var emptyId = UpgradeService.Purchase("", "car-01",
                "acceleration", 1, profile, store, catalog);
            Assert.That(emptyId.status,
                Is.EqualTo(UpgradeService.StatusRejected));

            var nullProfile = UpgradeService.Purchase("p-np", "car-01",
                "acceleration", 1, null, store, catalog);
            Assert.That(nullProfile.status,
                Is.EqualTo(UpgradeService.StatusRejected));
            Assert.That(nullProfile.code,
                Is.EqualTo("requiresSelectedProfile"));

            Assert.That(ProgressionSnapshot(profile), Is.EqualTo(before),
                "every rejection above must leave the profile untouched");
            Assert.That(profile.purchaseReceipts.Count, Is.EqualTo(0));
        }

        // --- service-level commands ------------------------------------------

        private FoundationService ServiceWithProfile(ProfileSave seeded,
            GameplayCatalog catalog)
        {
            var store = Store();
            if (seeded != null) store.Save(seeded);
            var service = new FoundationService(store, "en", RunId);
            service.SetDrivingContent(catalog.Driving);
            service.SetGameplayCatalog(catalog);
            Assert.That(service.Execute(Cmd("profile.select",
                P("slot", "0"))).status,
                Is.EqualTo(CommandResult.Applied));
            return service;
        }

        [Test]
        public void PurchaseCommandCommitsAndRefreshesCaches()
        {
            var catalog = Catalog();
            var profile = Profile();
            profile.coins = 120;
            var service = ServiceWithProfile(profile, catalog);

            var bought = service.Execute(Cmd("upgrade.purchase",
                P("purchaseId", "cmd-1", "vehicleId", "car-01",
                    "category", "acceleration", "expectedLevel", "1")));
            Assert.That(bought.status, Is.EqualTo(CommandResult.Applied),
                "upgrade.purchase failed: " + bought.code);
            var state = service.Snapshot();
            Assert.That(state.savedProfile.coins, Is.EqualTo(60));
            Assert.That(state.slots[0].profile.coins, Is.EqualTo(60),
                "the slot cache must track the committed debit");
            Assert.That(state.draftProfile.coins, Is.EqualTo(60),
                "the settings draft must track the committed debit");
            Assert.That(state.persistenceStatus, Is.EqualTo("saved"));

            // The identical command replays idempotently through the
            // durable receipt — no second charge even under a fresh
            // command id.
            var replay = service.Execute(Cmd("upgrade.purchase",
                P("purchaseId", "cmd-1", "vehicleId", "car-01",
                    "category", "acceleration", "expectedLevel", "1")));
            Assert.That(replay.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(service.Snapshot().savedProfile.coins,
                Is.EqualTo(60));

            // Rejection codes reach the command surface unchanged.
            var insufficient = service.Execute(Cmd("upgrade.purchase",
                P("purchaseId", "cmd-2", "vehicleId", "car-01",
                    "category", "acceleration", "expectedLevel", "2")));
            Assert.That(insufficient.status,
                Is.EqualTo(CommandResult.Rejected));
            Assert.That(insufficient.code,
                Is.EqualTo("insufficientCoins"));
        }

        [Test]
        public void PurchaseDuringRaceRejectsRaceActive()
        {
            var catalog = Catalog();
            var profile = Profile();
            profile.coins = 200;
            var service = ServiceWithProfile(profile, catalog);
            Assert.That(service.Execute(Cmd("solo.start",
                P("mode", "campaign",
                    "eventId", "harbor-hop-car-circuit",
                    "vehicleId", "car-01"))).status,
                Is.EqualTo(CommandResult.Applied));
            var during = service.Execute(Cmd("upgrade.purchase",
                P("purchaseId", "cmd-r", "vehicleId", "car-01",
                    "category", "acceleration", "expectedLevel", "1")));
            Assert.That(during.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(during.code, Is.EqualTo("raceActive"));
            var equip = service.Execute(Cmd("cosmetic.equip",
                P("slot", "livery", "cosmeticId", "livery-rival-car",
                    "equipped", "true")));
            Assert.That(equip.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(equip.code, Is.EqualTo("raceActive"));
            Assert.That(service.Execute(Cmd("race.exit", null)).status,
                Is.EqualTo(CommandResult.Applied));
        }

        [Test]
        public void PurchaseDuringLanSessionRejectsNetworkActive()
        {
            var catalog = Catalog();
            var profile = Profile();
            profile.coins = 200;
            var service = ServiceWithProfile(profile, catalog);
            var link = new LanSessionTests.FakeLanLink();
            var lan = LanSessionTests.StandaloneService(link,
                new LanSessionTests.FakeLanProbe(),
                service.Snapshot().savedProfile.id);
            service.SetLanSession(lan);
            Assert.That(service.Execute(Cmd("lan.host",
                P("port", "47777"))).status,
                Is.EqualTo(CommandResult.Applied));
            var during = service.Execute(Cmd("upgrade.purchase",
                P("purchaseId", "cmd-lan", "vehicleId", "car-01",
                    "category", "acceleration", "expectedLevel", "1")));
            Assert.That(during.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(during.code, Is.EqualTo("networkActive"));
            var equip = service.Execute(Cmd("cosmetic.equip",
                P("slot", "livery", "cosmeticId", "livery-rival-car",
                    "equipped", "true")));
            Assert.That(equip.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(equip.code, Is.EqualTo("networkActive"));
        }

        [Test]
        public void UnresolvedRewardBlocksPurchaseUntilRetry()
        {
            var catalog = Catalog();
            var toggling = new TogglingSerializer();
            var store = new ProfileStore(directory, toggling);
            // Seed a profile that can afford the level-1 purchase after
            // the pending reward lands — the gate must release, and the
            // purchase then runs on its own merits.
            var seeded = Profile();
            seeded.coins = 200;
            store.Save(seeded);
            var service = new FoundationService(store, "en", RunId);
            service.SetDrivingContent(catalog.Driving);
            service.SetGameplayCatalog(catalog);
            Assert.That(service.Execute(Cmd("profile.select",
                P("slot", "0"))).status,
                Is.EqualTo(CommandResult.Applied));

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

            toggling.failing = true;
            Assert.That(lan.PublishResult(
                    ProgressionLanResult(raceId,
                        service.Snapshot().savedProfile.id)),
                Is.True);
            Assert.That(service.RewardUnresolved, Is.True);
            Assert.That(lan.Execute(Cmd("lan.leave", null)).status,
                Is.EqualTo(CommandResult.Applied));

            var blocked = service.Execute(Cmd("upgrade.purchase",
                P("purchaseId", "cmd-gated", "vehicleId", "car-01",
                    "category", "acceleration", "expectedLevel", "1")));
            Assert.That(blocked.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(blocked.code, Is.EqualTo("rewardUnresolved"));

            toggling.failing = false;
            Assert.That(service.RetryUnresolvedRewards().status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            var profile = service.Snapshot().savedProfile;
            Assert.That(profile.coins, Is.EqualTo(225),
                "200 seeded + 25 earned coins");
            var bought = service.Execute(Cmd("upgrade.purchase",
                P("purchaseId", "cmd-gated", "vehicleId", "car-01",
                    "category", "acceleration", "expectedLevel", "1")));
            Assert.That(bought.status, Is.EqualTo(CommandResult.Applied),
                "the resolved gate must free the purchase: "
                + bought.code);
            Assert.That(service.Snapshot().savedProfile.coins,
                Is.EqualTo(165), "225 - 60 level-1 price");
        }

        // A finished LAN result identical to the Task-7 reward-gate
        // fixture: 20 completion + 5 first-place bonus = 25 coins.
        private static RaceResult ProgressionLanResult(string raceId,
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

        [Test]
        public void DiscoveryReportsPurchaseAvailability()
        {
            var catalog = Catalog();
            var store = Store();
            var service = new FoundationService(store, "en", RunId);
            service.SetDrivingContent(catalog.Driving);
            service.SetGameplayCatalog(catalog);
            var before = Availability(service, "upgrade.purchase");
            Assert.That(before.available, Is.False);
            Assert.That(before.reason,
                Is.EqualTo("requiresSelectedProfile"));
            var equipBefore = Availability(service, "cosmetic.equip");
            Assert.That(equipBefore.available, Is.False);
            Assert.That(equipBefore.reason,
                Is.EqualTo("requiresSelectedProfile"));

            // Slot caches bind at construction — a fresh service sees
            // the seeded profile.
            var profile = Profile();
            profile.coins = 100;
            store.Save(profile);
            service = new FoundationService(store, "en", RunId);
            service.SetDrivingContent(catalog.Driving);
            service.SetGameplayCatalog(catalog);
            Assert.That(service.Execute(Cmd("profile.select",
                P("slot", "0"))).status,
                Is.EqualTo(CommandResult.Applied));
            var after = Availability(service, "upgrade.purchase");
            Assert.That(after.available, Is.True,
                "upgrade.purchase must be available with a profile: "
                + after.reason);
            var equipAfter = Availability(service, "cosmetic.equip");
            Assert.That(equipAfter.available, Is.True);

            // Legacy content still answers honestly — the purchase
            // surface reports contentUnavailable before any profile
            // check, exactly like solo.start.
            var legacy = new FoundationService(store, "en", "legacy-run");
            legacy.SetGameplayCatalog(
                GameplayCatalog.CreateLegacy(catalog.Driving));
            var unavailable = Availability(legacy, "upgrade.purchase");
            Assert.That(unavailable.available, Is.False);
            Assert.That(unavailable.reason,
                Is.EqualTo("contentUnavailable"));
        }

        private static (bool available, string reason) Availability(
            FoundationService service, string action)
        {
            foreach (var descriptor in service.Discover())
                if (descriptor.action == action)
                    return (descriptor.available, descriptor.reason);
            Assert.Fail("action not discovered: " + action);
            return (false, "");
        }

        // --- cosmetics --------------------------------------------------------

        [Test]
        public void CosmeticEquipChecksOwnershipAndSwapsWithinSlot()
        {
            var catalog = Catalog();
            var store = Store();
            var profile = Profile();
            profile.ownedCosmetics.Add("paint-sunrise");
            profile.ownedCosmetics.Add("paint-dusk");
            profile.ownedCosmetics.Add("accessory-flag");

            // An unowned cosmetic can never equip — cosmetics never
            // bypass the ownership check.
            var locked = UpgradeService.SetCosmeticEquipped(profile,
                "paint-neon", true, store);
            Assert.That(locked.status,
                Is.EqualTo(UpgradeService.StatusRejected));
            Assert.That(locked.code, Is.EqualTo("cosmeticLocked"));
            Assert.That(profile.equippedCosmetics.Count, Is.EqualTo(0));

            // Two different slots coexist; a second paint replaces the
            // first inside its slot.
            Assert.That(UpgradeService.SetCosmeticEquipped(profile,
                    "paint-sunrise", true, store).status,
                Is.EqualTo(UpgradeService.StatusApplied));
            Assert.That(UpgradeService.SetCosmeticEquipped(profile,
                    "accessory-flag", true, store).status,
                Is.EqualTo(UpgradeService.StatusApplied));
            Assert.That(profile.equippedCosmetics,
                Is.EquivalentTo(new[] { "paint-sunrise", "accessory-flag" }));
            Assert.That(UpgradeService.SetCosmeticEquipped(profile,
                    "paint-dusk", true, store).status,
                Is.EqualTo(UpgradeService.StatusApplied));
            Assert.That(profile.equippedCosmetics,
                Is.EquivalentTo(new[] { "paint-dusk", "accessory-flag" }),
                "a same-slot equip replaces, never stacks");

            // Unequip removes only that item, and the whole equipment
            // survives a cold reload.
            Assert.That(UpgradeService.SetCosmeticEquipped(profile,
                    "accessory-flag", false, store).status,
                Is.EqualTo(UpgradeService.StatusApplied));
            Assert.That(profile.equippedCosmetics,
                Is.EquivalentTo(new[] { "paint-dusk" }));
            var reloaded = new ProfileStore(directory, serializer)
                .Load(profile.slot).Profile;
            Assert.That(reloaded.equippedCosmetics,
                Is.EquivalentTo(new[] { "paint-dusk" }),
                "equipped cosmetics must persist across restart");
        }

        [Test]
        public void CosmeticEquipCommandPersistsAtomically()
        {
            var catalog = Catalog();
            var profile = Profile();
            profile.ownedCosmetics.Add("livery-rival-car");
            var service = ServiceWithProfile(profile, catalog);
            var locked = service.Execute(Cmd("cosmetic.equip",
                P("slot", "livery", "cosmeticId", "livery-rival-motorcycle",
                    "equipped", "true")));
            Assert.That(locked.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(locked.code, Is.EqualTo("cosmeticLocked"));
            var equipped = service.Execute(Cmd("cosmetic.equip",
                P("slot", "livery", "cosmeticId", "livery-rival-car",
                    "equipped", "true")));
            Assert.That(equipped.status,
                Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot();
            Assert.That(state.savedProfile.equippedCosmetics,
                Is.EquivalentTo(new[] { "livery-rival-car" }));
            Assert.That(state.draftProfile.equippedCosmetics,
                Is.EquivalentTo(new[] { "livery-rival-car" }));
            // A restart sees the same equipment — the equip wrote the
            // same profile file purchases write.
            var fresh = new FoundationService(Store(), "en", RunId);
            fresh.SetGameplayCatalog(catalog);
            Assert.That(fresh.Execute(Cmd("profile.select",
                P("slot", "0"))).status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(fresh.Snapshot().savedProfile.equippedCosmetics,
                Is.EquivalentTo(new[] { "livery-rival-car" }));
        }

        // --- tuning resolution -------------------------------------------------

        [Test]
        public void ResolverReturnsFreshObjectsAndNeverMutatesCatalog()
        {
            var catalog = MappedCatalog();
            var store = Store();
            var profile = Profile();
            profile.coins = 2000;
            for (var level = 1; level <= 3; level++)
            {
                Assert.That(UpgradeService.Purchase("p-a" + level,
                        "car-01", "acceleration", level, profile, store,
                        catalog).status,
                    Is.EqualTo(UpgradeService.StatusApplied));
                Assert.That(UpgradeService.Purchase("p-h" + level,
                        "car-01", "handling", level, profile, store,
                        catalog).status,
                    Is.EqualTo(UpgradeService.StatusApplied));
            }
            var fingerprint = catalog.CompatibilityFingerprint;
            var drivingFingerprint =
                catalog.Driving.CompatibilityFingerprint;
            var baselineAccel = catalog.Driving.Vehicles["car-01"].acceleration;

            var first = VehicleTuningResolver.Resolve(RaceMode.Campaign,
                "car-01", profile, catalog);
            var second = VehicleTuningResolver.Resolve(RaceMode.Campaign,
                "car-01", profile, catalog);
            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Not.Null);
            Assert.That(first, Is.Not.SameAs(second),
                "every resolve hands back its own object");
            Assert.That(first, Is.Not.SameAs(
                catalog.Driving.Vehicles["car-01"]));
            first.acceleration = -999f;
            Assert.That(second.acceleration,
                Is.GreaterThan(baselineAccel),
                "mutating a resolved tune must not touch another resolve");
            Assert.That(catalog.Driving.Vehicles["car-01"].acceleration,
                Is.EqualTo(baselineAccel),
                "resolve must never mutate the catalog tuning");
            Assert.That(catalog.CompatibilityFingerprint,
                Is.EqualTo(fingerprint),
                "resolve must never disturb the compatibility fingerprint");
            Assert.That(catalog.Driving.CompatibilityFingerprint,
                Is.EqualTo(drivingFingerprint));
        }

        [Test]
        public void LanAndTimeTrialResolveTheClassBaseline()
        {
            var catalog = MappedCatalog();
            var store = Store();
            var profile = Profile();
            profile.coins = 2000;
            for (var level = 1; level <= 3; level++)
            {
                UpgradeService.Purchase("p-a" + level, "car-01",
                    "acceleration", level, profile, store, catalog);
                UpgradeService.Purchase("p-h" + level, "car-01",
                    "handling", level, profile, store, catalog);
                UpgradeService.Purchase("p-b" + level, "car-01",
                    "boost", level, profile, store, catalog);
            }
            var baseline = catalog.Driving.Vehicles["car-01"];
            foreach (var mode in new[]
                { RaceMode.Lan, RaceMode.TimeTrial, RaceMode.Practice })
            {
                var resolved = VehicleTuningResolver.Resolve(mode,
                    "car-01", profile, catalog);
                Assert.That(resolved, Is.Not.Null, "mode " + mode);
                Assert.That(resolved.acceleration,
                    Is.EqualTo(baseline.acceleration),
                    "mode " + mode + " must normalize acceleration");
                Assert.That(resolved.steeringRate,
                    Is.EqualTo(baseline.steeringRate), "mode " + mode);
                Assert.That(resolved.grip, Is.EqualTo(baseline.grip),
                    "mode " + mode);
                Assert.That(resolved.boostAcceleration,
                    Is.EqualTo(baseline.boostAcceleration), "mode " + mode);
                Assert.That(resolved.boostMaxSpeed,
                    Is.EqualTo(baseline.boostMaxSpeed), "mode " + mode);
                Assert.That(resolved, Is.Not.SameAs(baseline));
            }
            // The same maxed profile still gets its purchased tune in
            // the progression modes.
            var campaign = VehicleTuningResolver.Resolve(
                RaceMode.Campaign, "car-01", profile, catalog);
            Assert.That(campaign.acceleration,
                Is.GreaterThan(baseline.acceleration));
        }

        // --- helpers -----------------------------------------------------------

        private static GameCommand Cmd(string action,
            Dictionary<string, string> parameters)
        {
            return new GameCommand(1, RunId,
                Guid.NewGuid().ToString("N"), "local", action,
                parameters);
        }

        private static Dictionary<string, string> P(
            params string[] keyValues)
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
                return JsonSerializer.Deserialize<ProfileSave>(json,
                    Options);
            }
#endif
        }

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
