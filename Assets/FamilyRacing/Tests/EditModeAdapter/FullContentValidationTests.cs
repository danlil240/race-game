using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using FamilyRacing.EditorTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace FamilyRacing.Tests
{
    // M6 Task 4 slice A: structural production validation of the full
    // content catalog plus the packaging gates around it. These tests pin
    // the fixed production contract — 3 worlds / 6 courses / 8 vehicle
    // models / 72 regular + 6 finale events / 6 unique rivals / bilingual
    // names / four spawn slots, recovery poses, pickups and two legal
    // shortcuts per course / final bilingual voice cues — and that the
    // four final build flavors can never package an incomplete inventory
    // while the development-only preview paths can (generic validation
    // only, contentStage=partial, never a candidate).
    //
    // The fixtures are synthetic in-memory content; they are NOT the
    // authored shipping content (Tasks 5–8 own that). A passing fixture
    // means structurally valid, not playtested.
    public sealed class FullContentValidationTests
    {
        private static readonly string[] WorldIds =
            { "sunny-island", "redrock-canyon", "snowy-peaks" };
        private static readonly string[][] WorldCourses =
        {
            new[] { "harbor-hop", "lighthouse-loop" },
            new[] { "canyon-dash", "mesa-skyway" },
            new[] { "snowpine-circuit", "aurora-pass" }
        };
        private static readonly string[] WorldNamesEn =
            { "Sunny Island", "Redrock Canyon", "Snowy Peaks" };
        private static readonly string[] WorldNamesHe =
            { "האי השמשי", "קניון הסלע האדום", "פסגות מושלגות" };
        private static readonly string[] ClassIds = { "car", "motorcycle" };
        private static readonly string[] TypeSuffixes =
            { "circuit", "boost", "stunt", "time", "gadget", "clean" };

        private readonly List<UnityEngine.Object> ownedAssets =
            new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var asset in ownedAssets)
                if (asset != null) UnityEngine.Object.DestroyImmediate(asset);
            ownedAssets.Clear();
        }

        private T New<T>() where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>();
            ownedAssets.Add(asset);
            return asset;
        }

        // ---- fixture builders -------------------------------------------

        private static string RegularEventId(string courseId, string classId,
            string suffix)
        {
            return courseId + "-" + classId + "-" + suffix;
        }

        private static string FinaleId(string worldId, string classId)
        {
            return worldId + "-" + classId + "-finale";
        }

        private static string RivalId(string worldId, string classId)
        {
            return worldId + "-" + classId + "-rival";
        }

        // World completion rewards (plan Tasks 5–7): sunny -> *-02,
        // redrock -> *-03, snowy -> *-04.
        private static string RewardVehicleId(int worldIndex, string classId)
        {
            return classId + "-0" + (worldIndex + 2);
        }

        private static string EventTypeOfSuffix(string suffix)
        {
            return suffix == "clean" ? GameplayEventType.CleanDriving : suffix;
        }

        private static VehicleTuning Vehicle(string id)
        {
            var tuning = id.StartsWith("car", StringComparison.Ordinal)
                ? VehicleTuning.Car01() : VehicleTuning.Motorcycle01();
            tuning.vehicleId = id;
            return tuning;
        }

        private static CourseDefinition Course(string id, int lane)
        {
            var offsetZ = lane * 400f;
            var course = new CourseDefinition
            {
                id = id,
                spawnX = 0f,
                spawnY = 0f,
                spawnZ = -60f + offsetZ,
                spawnHeading = 0f
            };
            course.checkpoints.Add(new CheckpointGate
            {
                id = "start", x = 0f, z = offsetZ, halfWidth = 6f,
                dirX = 0f, dirZ = 1f,
                recoverX = 0f, recoverZ = offsetZ - 3f
            });
            for (var gate = 1; gate <= 3; gate++)
                course.checkpoints.Add(new CheckpointGate
                {
                    id = "gate-" + gate, x = 40f * gate, z = offsetZ + 60f,
                    halfWidth = 6f, dirX = 0f, dirZ = 1f,
                    recoverX = 40f * gate, recoverZ = offsetZ + 57f
                });
            for (var slot = 0; slot < 4; slot++)
                course.spawnSlots.Add(new MotorPose
                {
                    x = (slot - 1.5f) * 5f,
                    z = offsetZ - 70f - (slot % 2) * 5f
                });
            course.safePoses.Add(new MotorPose
                { x = 0f, z = offsetZ - 4f });
            course.safePoses.Add(new MotorPose
                { x = -6f, z = offsetZ - 4f });
            course.pickupSites.Add(new GadgetPickupSite
            {
                id = id + "-pickup-1", x = 3f, z = offsetZ + 20f
            });
            // Two distinct legal optional lanes away from every gate.
            course.shortcutZones.Add(new ShortcutZone
            {
                id = id + "-shortcut-1", x = -30f, z = offsetZ + 30f,
                halfWidth = 5f, dirX = 1f, dirZ = 0f
            });
            course.shortcutZones.Add(new ShortcutZone
            {
                id = id + "-shortcut-2", x = 30f, z = offsetZ + 30f,
                halfWidth = 5f, dirX = -1f, dirZ = 0f
            });
            return course;
        }

        private static List<string> ClassVehicleIds(string classId,
            int models)
        {
            var ids = new List<string>();
            for (var i = 1; i <= models; i++)
                ids.Add(classId + "-0" + i);
            return ids;
        }

        private static EventDefinition RegularEvent(string courseId,
            string classId, string suffix, int models = 4)
        {
            var eventType = EventTypeOfSuffix(suffix);
            var id = RegularEventId(courseId, classId, suffix);
            var evt = new EventDefinition
            {
                id = id,
                courseId = courseId,
                laps = eventType == GameplayEventType.Time ? 1 : 2,
                countdownTicks = 90,
                vehicleIds = ClassVehicleIds(classId, models),
                recovery = new RecoverySettings(),
                eventType = eventType,
                vehicleClass = classId,
                parTicks = eventType == GameplayEventType.Time ? 3600 : 0
            };
            // Introductory + mastery legs on different metrics so the
            // generic strictness check stays uninvolved.
            evt.objectives = new List<ObjectiveDefinition>
            {
                new ObjectiveDefinition
                {
                    id = id + "-intro", kind = ObjectiveKind.Introductory,
                    alternatives =
                    {
                        new ObjectiveCondition
                        {
                            metric = ObjectiveMetric.CleanLandings,
                            threshold = 1
                        }
                    }
                },
                new ObjectiveDefinition
                {
                    id = id + "-mastery", kind = ObjectiveKind.Mastery,
                    alternatives =
                    {
                        new ObjectiveCondition
                        {
                            metric = ObjectiveMetric.FinishPlace,
                            comparator = ObjectiveComparator.AtMost,
                            threshold = 1
                        }
                    }
                }
            };
            return evt;
        }

        private static EventDefinition Finale(string worldId, int worldIndex,
            string classId, int models = 4)
        {
            return new EventDefinition
            {
                id = FinaleId(worldId, classId),
                courseId = WorldCourses[worldIndex][0],
                laps = 3,
                countdownTicks = 90,
                vehicleIds = ClassVehicleIds(classId, models),
                recovery = new RecoverySettings(),
                eventType = GameplayEventType.Finale,
                vehicleClass = classId,
                isFinale = true
            };
        }

        private static List<BountyCatalogAsset.BountyRow> BountyRows()
        {
            var rows = new List<BountyCatalogAsset.BountyRow>
            {
                new BountyCatalogAsset.BountyRow
                {
                    bountyId = "drift-10s", family = "drift",
                    metric = BountyMetric.DriftSeconds, threshold = 10,
                    unit = "seconds", rewardCoins = 10
                },
                new BountyCatalogAsset.BountyRow
                {
                    bountyId = "finish-2", family = "human-finish",
                    metric = BountyMetric.HumanFinishes, threshold = 2,
                    unit = "count", rewardCoins = 10
                }
            };
            foreach (var worldId in WorldIds)
                foreach (var classId in ClassIds)
                {
                    var rivalId = RivalId(worldId, classId);
                    rows.Add(new BountyCatalogAsset.BountyRow
                    {
                        bountyId = rivalId, family = "rival",
                        metric = BountyMetric.HumanFinishes, threshold = 1,
                        unit = "count", rewardCoins = 0,
                        repeatable = false, rivalId = rivalId
                    });
                }
            return rows;
        }

        private EconomyDefinitionAsset EconomyAsset()
        {
            var asset = New<EconomyDefinitionAsset>();
            asset.upgradeCategories = new[]
            {
                new EconomyDefinitionAsset.CategoryRow
                {
                    categoryId = "acceleration", maxLevel = 3,
                    prices = new[] { 60, 120, 180 },
                    tuningFields = new[] { "acceleration" }
                }
            };
            return asset;
        }

        private GadgetCatalogAsset GadgetsAsset()
        {
            var asset = New<GadgetCatalogAsset>();
            asset.gadgets = new[]
            {
                new GadgetCatalogAsset.GadgetRow
                {
                    gadgetId = "turbo-01", kind = "turbo",
                    durationTicks = 60, accelerationMultiplier = 1.25f,
                    speedCapMultiplier = 1.10f
                }
            };
            return asset;
        }

        private VoicePromptCatalogAsset VoiceAsset()
        {
            var asset = New<VoicePromptCatalogAsset>();
            var rows = new List<VoicePromptCatalogAsset.CueRow>();
            foreach (var cueId in FullContentValidation.RequiredCueIds())
                rows.Add(new VoicePromptCatalogAsset.CueRow
                {
                    cueId = cueId,
                    english = AudioClip.Create(cueId + "-en", 4410, 1, 44100, false),
                    hebrew = AudioClip.Create(cueId + "-he", 4410, 1, 44100, false)
                });
            asset.cues = rows.ToArray();
            return asset;
        }

        // The complete synthetic production inventory: 3 ordered worlds,
        // 6 courses, 8 models, 72 regular events exactly covering every
        // class x course x type cell, 6 rival finales with the plan's
        // reward mapping, one-time rival bounties and the full bilingual
        // voice cue set.
        private sealed class Fixture
        {
            public List<VehicleTuning> vehicles = new List<VehicleTuning>();
            public List<CourseDefinition> courses = new List<CourseDefinition>();
            public List<EventDefinition> events = new List<EventDefinition>();
            public CampaignDefinitionAsset[] campaigns;
            public BountyCatalogAsset bounties;
            public EconomyDefinitionAsset economy;
            public GadgetCatalogAsset gadgets;
            public VoicePromptCatalogAsset voices;

            public DrivingContentCatalog driving;
            public GameplayCatalogAsset catalog;

            public void Bake(FullContentValidationTests owner)
            {
                driving = DrivingContentCatalog.Create("full-content-1",
                    vehicles, courses, events);
                catalog = owner.New<GameplayCatalogAsset>();
                catalog.campaigns = campaigns;
                catalog.bounties = bounties;
                catalog.economy = economy;
                catalog.gadgets = gadgets;
                catalog.difficulties = new[]
                {
                    new GameplayCatalogAsset.DifficultyRow { difficultyId = "easy" },
                    new GameplayCatalogAsset.DifficultyRow { difficultyId = "medium" },
                    new GameplayCatalogAsset.DifficultyRow { difficultyId = "hard" }
                };
                catalog.voicePrompts = voices;
            }
        }

        private Fixture CompleteFixture()
        {
            var fixture = new Fixture();
            foreach (var classId in ClassIds)
                for (var model = 1; model <= 4; model++)
                    fixture.vehicles.Add(Vehicle(classId + "-0" + model));
            for (var w = 0; w < WorldIds.Length; w++)
                foreach (var courseId in WorldCourses[w])
                    fixture.courses.Add(Course(courseId, w * 2 +
                        Array.IndexOf(WorldCourses[w], courseId)));
            for (var w = 0; w < WorldIds.Length; w++)
            {
                foreach (var courseId in WorldCourses[w])
                    foreach (var classId in ClassIds)
                        foreach (var suffix in TypeSuffixes)
                            fixture.events.Add(
                                RegularEvent(courseId, classId, suffix));
                foreach (var classId in ClassIds)
                    fixture.events.Add(Finale(WorldIds[w], w, classId));
            }
            var campaigns = new List<CampaignDefinitionAsset>();
            for (var w = 0; w < WorldIds.Length; w++)
            {
                var campaign = New<CampaignDefinitionAsset>();
                campaign.worldId = WorldIds[w];
                campaign.nameEn = WorldNamesEn[w];
                campaign.nameHe = WorldNamesHe[w];
                campaign.courseIds = (string[])WorldCourses[w].Clone();
                var regular = new List<string>();
                foreach (var courseId in WorldCourses[w])
                    foreach (var classId in ClassIds)
                        foreach (var suffix in TypeSuffixes)
                            regular.Add(RegularEventId(courseId, classId, suffix));
                campaign.regularEventIds = regular.ToArray();
                campaign.requiredEventCompletions = regular.Count;
                campaign.requiredRegionalStars = 30;
                campaign.finales = new[]
                {
                    new CampaignDefinitionAsset.FinaleRow
                    {
                        eventId = FinaleId(WorldIds[w], "car"),
                        classId = "car",
                        rivalId = RivalId(WorldIds[w], "car"),
                        rewardVehicleId = RewardVehicleId(w, "car")
                    },
                    new CampaignDefinitionAsset.FinaleRow
                    {
                        eventId = FinaleId(WorldIds[w], "motorcycle"),
                        classId = "motorcycle",
                        rivalId = RivalId(WorldIds[w], "motorcycle"),
                        rewardVehicleId = RewardVehicleId(w, "motorcycle")
                    }
                };
                campaigns.Add(campaign);
            }
            fixture.campaigns = campaigns.ToArray();
            var bounties = New<BountyCatalogAsset>();
            bounties.bounties = BountyRows().ToArray();
            fixture.bounties = bounties;
            fixture.economy = EconomyAsset();
            fixture.gadgets = GadgetsAsset();
            fixture.voices = VoiceAsset();
            fixture.Bake(this);
            return fixture;
        }

        private static string Joined(IReadOnlyList<string> reasons)
        {
            return reasons == null ? "" : string.Join("\n", reasons);
        }

        private static void AssertHasReason(IReadOnlyList<string> reasons,
            string fragment, string defect)
        {
            Assert.That(reasons, Is.Not.Null.And.Not.Empty,
                defect + " must produce at least one named reason");
            Assert.That(Joined(reasons), Does.Contain(fragment),
                defect + " must name '" + fragment + "'; got:\n"
                + Joined(reasons));
        }

        // ---- the contract -----------------------------------------------

        [Test]
        public void CompleteFixtureIsGenericallyConvertible()
        {
            var fixture = CompleteFixture();
            var catalog = fixture.catalog.ToCatalog(fixture.driving,
                out var error);
            Assert.That(error, Is.Null);
            Assert.That(catalog, Is.Not.Null);
            Assert.That(catalog.Campaigns.Count, Is.EqualTo(3));
            Assert.That(fixture.driving.Events.Count, Is.EqualTo(78));
        }

        [Test]
        public void CompleteFixturePassesProductionValidation()
        {
            var fixture = CompleteFixture();
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            Assert.That(reasons, Is.Not.Null);
            Assert.That(Joined(reasons), Is.EqualTo(""),
                "the complete 78-row fixture must be structurally valid");
        }

        // Tiny M5-shaped catalogs stay valid generic catalogs but are not
        // the production inventory — the two validation levels are
        // deliberately distinct.
        [Test]
        public void TinyFixtureIsGenericValidButFailsProduction()
        {
            var fixture = new Fixture();
            foreach (var classId in ClassIds)
                for (var model = 1; model <= 2; model++)
                    fixture.vehicles.Add(Vehicle(classId + "-0" + model));
            foreach (var courseId in WorldCourses[0])
                fixture.courses.Add(Course(courseId, 0));
            foreach (var courseId in WorldCourses[0])
                foreach (var classId in ClassIds)
                    foreach (var suffix in TypeSuffixes)
                        fixture.events.Add(
                            RegularEvent(courseId, classId, suffix,
                                models: 2));
            foreach (var classId in ClassIds)
                fixture.events.Add(Finale(WorldIds[0], 0, classId,
                    models: 2));
            var campaign = New<CampaignDefinitionAsset>();
            campaign.worldId = WorldIds[0];
            campaign.nameEn = WorldNamesEn[0];
            campaign.nameHe = WorldNamesHe[0];
            campaign.courseIds = (string[])WorldCourses[0].Clone();
            var regular = new List<string>();
            foreach (var courseId in WorldCourses[0])
                foreach (var classId in ClassIds)
                    foreach (var suffix in TypeSuffixes)
                        regular.Add(RegularEventId(courseId, classId, suffix));
            campaign.regularEventIds = regular.ToArray();
            campaign.requiredEventCompletions = regular.Count;
            campaign.requiredRegionalStars = 30;
            campaign.finales = new[]
            {
                new CampaignDefinitionAsset.FinaleRow
                {
                    eventId = FinaleId(WorldIds[0], "car"), classId = "car",
                    rivalId = RivalId(WorldIds[0], "car"),
                    rewardVehicleId = RewardVehicleId(0, "car")
                },
                new CampaignDefinitionAsset.FinaleRow
                {
                    eventId = FinaleId(WorldIds[0], "motorcycle"),
                    classId = "motorcycle",
                    rivalId = RivalId(WorldIds[0], "motorcycle"),
                    rewardVehicleId = RewardVehicleId(0, "motorcycle")
                }
            };
            fixture.campaigns = new[] { campaign };
            var bounties = New<BountyCatalogAsset>();
            bounties.bounties = BountyRows().ToArray();
            fixture.bounties = bounties;
            fixture.economy = EconomyAsset();
            fixture.gadgets = GadgetsAsset();
            fixture.voices = VoiceAsset();
            fixture.Bake(this);

            Assert.That(fixture.catalog.ToCatalog(fixture.driving,
                out var genericError), Is.Not.Null,
                "the tiny fixture is a valid generic catalog: "
                + genericError);
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            Assert.That(reasons, Is.Not.Empty,
                "a tiny catalog is not the production inventory");
            AssertHasReason(reasons, "worlds", "tiny fixture");
        }

        [Test]
        public void WrongWorldCountFails()
        {
            var fixture = CompleteFixture();
            fixture.catalog.campaigns = new[]
            {
                fixture.catalog.campaigns[0], fixture.catalog.campaigns[1]
            };
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            // Ownership of the now-homeless world's events fails generic
            // conversion; the inventory count must still be named.
            AssertHasReason(reasons, "world", "dropped world");
        }

        [Test]
        public void WrongWorldOrderFails()
        {
            var fixture = CompleteFixture();
            var swapped = fixture.catalog.campaigns[0];
            fixture.catalog.campaigns[0] = fixture.catalog.campaigns[1];
            fixture.catalog.campaigns[1] = swapped;
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "world",
                "world order is progression content");
            AssertHasReason(reasons, "sunny-island", "world order");
        }

        [Test]
        public void MissingCourseFails()
        {
            var fixture = CompleteFixture();
            fixture.courses.RemoveAll(c => c.id == "lighthouse-loop");
            // Keep the catalog constructible: the orphan events go too.
            fixture.events.RemoveAll(e => e.courseId == "lighthouse-loop");
            foreach (var campaign in fixture.catalog.campaigns)
                if (campaign.worldId == "sunny-island")
                {
                    var kept = new List<string>(campaign.regularEventIds);
                    kept.RemoveAll(id => id.StartsWith("lighthouse-loop",
                        StringComparison.Ordinal));
                    campaign.regularEventIds = kept.ToArray();
                    campaign.requiredEventCompletions = kept.Count;
                    campaign.courseIds = new[] { "harbor-hop" };
                }
            fixture.Bake(this);
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "lighthouse-loop", "dropped course");
        }

        [Test]
        public void ExtraCourseFails()
        {
            var fixture = CompleteFixture();
            fixture.courses.Add(Course("bonus-lagoon", 7));
            fixture.Bake(this);
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "bonus-lagoon", "unexpected course");
        }

        [Test]
        public void MissingCellEventFails()
        {
            var fixture = CompleteFixture();
            fixture.events.RemoveAll(e => e.id == "harbor-hop-car-stunt");
            foreach (var campaign in fixture.catalog.campaigns)
            {
                var kept = new List<string>(campaign.regularEventIds);
                if (kept.Remove("harbor-hop-car-stunt"))
                {
                    campaign.regularEventIds = kept.ToArray();
                    campaign.requiredEventCompletions = kept.Count;
                }
            }
            fixture.Bake(this);
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "harbor-hop-car-stunt",
                "empty class x course x type cell");
        }

        [Test]
        public void DuplicateCellEventFails()
        {
            var fixture = CompleteFixture();
            var extra = RegularEvent("harbor-hop", "car", "stunt");
            extra.id = "harbor-hop-car-stunt-alt";
            fixture.events.Add(extra);
            fixture.Bake(this);
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "harbor-hop-car-stunt-alt",
                "a second event in the same cell is not extra content");
        }

        [Test]
        public void StrayUntypedEventFails()
        {
            var fixture = CompleteFixture();
            // A bonus race nobody claims: untyped, unreferenced by any
            // campaign. Generic conversion never requires unclaimed
            // events to be typed, so only the production gate can catch
            // this inventory drift — the shipped set is exactly 78.
            fixture.events.Add(new EventDefinition
            {
                id = "harbor-hop-car-bonus",
                courseId = "harbor-hop",
                laps = 1,
                countdownTicks = 90,
                vehicleIds = ClassVehicleIds("car", 4),
                recovery = new RecoverySettings()
            });
            fixture.Bake(this);
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "harbor-hop-car-bonus",
                "a stray untyped event must be named, not skipped");
            AssertHasReason(reasons, "found 79",
                "the authored inventory is exactly 78 event definitions");
        }

        [Test]
        public void CleanEventMustUseShortSuffix()
        {
            var fixture = CompleteFixture();
            var evt = fixture.events.Find(
                e => e.id == "harbor-hop-car-clean");
            evt.id = "harbor-hop-car-clean-driving";
            foreach (var campaign in fixture.catalog.campaigns)
                for (var i = 0; i < campaign.regularEventIds.Length; i++)
                    if (campaign.regularEventIds[i] == "harbor-hop-car-clean")
                        campaign.regularEventIds[i] = evt.id;
            fixture.Bake(this);
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "harbor-hop-car-clean",
                "the production suffix is 'clean' even though the "
                + "runtime type is 'clean-driving'");
        }

        [Test]
        public void MissingFinaleFails()
        {
            var fixture = CompleteFixture();
            fixture.events.RemoveAll(
                e => e.id == "snowy-peaks-motorcycle-finale");
            fixture.Bake(this);
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "snowy-peaks-motorcycle-finale",
                "missing finale");
        }

        [Test]
        public void WrongFinaleRewardFails()
        {
            var fixture = CompleteFixture();
            foreach (var campaign in fixture.catalog.campaigns)
                foreach (var finale in campaign.finales)
                    if (finale.eventId == "redrock-canyon-car-finale")
                        finale.rewardVehicleId = "car-02";
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "redrock-canyon-car-finale",
                "wrong world-completion reward");
            AssertHasReason(reasons, "car-03",
                "redrock car completion grants car-03");
        }

        [Test]
        public void WrongRivalIdFails()
        {
            var fixture = CompleteFixture();
            foreach (var campaign in fixture.catalog.campaigns)
                foreach (var finale in campaign.finales)
                    if (finale.eventId == "sunny-island-car-finale")
                        finale.rivalId = "custom-rival";
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "rival",
                "rival ids follow <worldId>-<classId>-rival");
            AssertHasReason(reasons, "sunny-island-car-rival",
                "rival convention");
        }

        [Test]
        public void MissingRivalBountyFails()
        {
            var fixture = CompleteFixture();
            var rows = new List<BountyCatalogAsset.BountyRow>(
                fixture.bounties.bounties);
            rows.RemoveAll(r =>
                r.rivalId == "redrock-canyon-motorcycle-rival");
            fixture.bounties.bounties = rows.ToArray();
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "redrock-canyon-motorcycle-rival",
                "each rival needs its one-time victory bounty");
        }

        [Test]
        public void EnglishOnlyHebrewNameFails()
        {
            var fixture = CompleteFixture();
            foreach (var campaign in fixture.catalog.campaigns)
                if (campaign.worldId == "redrock-canyon")
                    campaign.nameHe = "Redrock Canyon";
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "nameHe",
                "a pasted English string is not a Hebrew display name");
        }

        [Test]
        public void ThreeSpawnSlotsFail()
        {
            var fixture = CompleteFixture();
            fixture.driving.Courses["harbor-hop"].spawnSlots.RemoveAt(0);
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "spawnSlots",
                "production courses seat a four-racer grid");
        }

        [Test]
        public void MissingRecoveryPosesFail()
        {
            var fixture = CompleteFixture();
            fixture.driving.Courses["mesa-skyway"].safePoses.Clear();
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "safePoses",
                "every production course authors recovery poses");
        }

        [Test]
        public void MissingPickupsFail()
        {
            var fixture = CompleteFixture();
            fixture.driving.Courses["canyon-dash"].pickupSites.Clear();
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "pickup",
                "every world hosts gadget events — sites are required");
        }

        [Test]
        public void OneShortcutFails()
        {
            var fixture = CompleteFixture();
            fixture.driving.Courses["aurora-pass"].shortcutZones
                .RemoveAt(0);
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "shortcut",
                "every production course authors two legal shortcuts");
        }

        [Test]
        public void ShortcutOverlappingGateFails()
        {
            var fixture = CompleteFixture();
            var course = fixture.driving.Courses["snowpine-circuit"];
            var gate = course.checkpoints[1];
            course.shortcutZones[0].x = gate.x;
            course.shortcutZones[0].z = gate.z;
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "shortcut",
                "a lane sharing a mandatory gate plane is not a shortcut");
        }

        // The old point-in-circle check tested only the lane's centre
        // against the gate radius, so a lane whose body reached into
        // the crossing region while its centre cleared passed
        // unchecked. Detectors are directed segments (centre ± tangent
        // × halfWidth, per CheckpointProgress.TryCross) — a segment
        // meeting the gate's segment fails even when the centres are
        // farther apart than either half-width.
        [Test]
        public void ShortcutBodyOverlappingGateFails()
        {
            var fixture = CompleteFixture();
            var course = fixture.driving.Courses["snowpine-circuit"];
            var gate = course.checkpoints[1];
            var zone = course.shortcutZones[0];
            // Collinear with the gate: centre 3 m past the gate's own
            // half-width, but the zone's extent reaches back into the
            // crossing region.
            zone.x = gate.x + gate.halfWidth + 3f;
            zone.z = gate.z;
            zone.dirX = gate.dirX;
            zone.dirZ = gate.dirZ;
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "shortcut",
                "a lane body overlapping the gate plane is not legal");
            AssertHasReason(reasons, gate.id,
                "the reason must name the overlapped mandatory gate");
        }

        // A legitimate lane rejoining near a gate: parallel to the gate
        // plane and 3 m past it. The centres sit inside the old radius
        // test, but the segments never meet — the hardened check must
        // not flag it.
        [Test]
        public void ShortcutParallelBeyondGatePlanePasses()
        {
            var fixture = CompleteFixture();
            var course = fixture.driving.Courses["harbor-hop"];
            var gate = course.checkpoints[1];
            var zone = course.shortcutZones[0];
            zone.x = gate.x;
            zone.z = gate.z + 3f;
            zone.dirX = gate.dirX;
            zone.dirZ = gate.dirZ;
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            Assert.That(Joined(reasons), Is.EqualTo(""),
                "a lane 3 m past a gate plane overlaps nothing; got:\n"
                + Joined(reasons));
        }

        // Two lanes sharing a ground-plane segment at the same height
        // are one shortcut authored twice — the dedup compares segment
        // extents (a 0.5 m offset of coincident lanes still flags), not
        // only centre distance.
        [Test]
        public void SamePlaneShortcutLanesFail()
        {
            var fixture = CompleteFixture();
            var course = fixture.driving.Courses["canyon-dash"];
            var lower = course.shortcutZones[0];
            var twin = course.shortcutZones[1];
            twin.x = lower.x + 0.5f;
            twin.z = lower.z;
            twin.y = lower.y;
            twin.dirX = lower.dirX;
            twin.dirZ = lower.dirZ;
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "same plane",
                "coincident lanes at one height are one shortcut");
        }

        // Stacked lanes on elevated routes (Mesa Skyway / Aurora Pass)
        // share ground-plane extent while legally separated in height —
        // they are distinct shortcuts, not a duplicate plane.
        [Test]
        public void StackedShortcutLanesAtDifferentHeightsPass()
        {
            var fixture = CompleteFixture();
            var course = fixture.driving.Courses["mesa-skyway"];
            var lower = course.shortcutZones[0];
            var upper = course.shortcutZones[1];
            upper.x = lower.x;
            upper.z = lower.z;
            upper.dirX = lower.dirX;
            upper.dirZ = lower.dirZ;
            upper.y = lower.y + 8f;
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            Assert.That(Joined(reasons), Is.EqualTo(""),
                "vertically separated stacked lanes are two legal "
                + "shortcuts; got:\n" + Joined(reasons));
        }

        [Test]
        public void ExtraVehicleFails()
        {
            var fixture = CompleteFixture();
            fixture.vehicles.Add(Vehicle("car-05"));
            fixture.Bake(this);
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "car-05",
                "the production roster is exactly the authored eight");
        }

        [Test]
        public void MissingVehicleFails()
        {
            var fixture = CompleteFixture();
            fixture.vehicles.RemoveAll(v => v.vehicleId == "motorcycle-04");
            foreach (var evt in fixture.events)
                evt.vehicleIds.Remove("motorcycle-04");
            foreach (var campaign in fixture.catalog.campaigns)
                foreach (var finale in campaign.finales)
                    if (finale.rewardVehicleId == "motorcycle-04")
                        finale.rewardVehicleId = "motorcycle-03";
            fixture.Bake(this);
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "motorcycle-04", "missing model");
        }

        [Test]
        public void MissingHebrewVoiceClipFails()
        {
            var fixture = CompleteFixture();
            foreach (var row in fixture.voices.cues)
                if (row.cueId == "rival.intro.snowy-peaks-car-rival")
                    row.hebrew = null;
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "voice",
                "missing final clips fail final-content validation");
            AssertHasReason(reasons, "rival.intro.snowy-peaks-car-rival",
                "missing clip names its cue");
        }

        [Test]
        public void MissingVoiceCatalogFails()
        {
            var fixture = CompleteFixture();
            fixture.catalog.voicePrompts = null;
            var reasons = FullContentValidation.Validate(fixture.catalog,
                fixture.driving);
            AssertHasReason(reasons, "voice",
                "an unbound voice catalog is not final content");
        }

        // A shortcut lane that overlaps a mandatory gate cannot shorten
        // the required route — generic validation must still accept a
        // well-formed lane, and Copy() must carry it.
        [Test]
        public void ShortcutZonesValidateAndCopy()
        {
            var course = Course("unit-course", 0);
            Assert.That(course.Validate(out var error), Is.True,
                "fixture course: " + error);
            var copy = course.Copy();
            Assert.That(copy.shortcutZones.Count, Is.EqualTo(2));
            Assert.That(copy.shortcutZones[0].id,
                Is.EqualTo("unit-course-shortcut-1"));
            copy.shortcutZones[0].id = "rewritten";
            Assert.That(course.shortcutZones[0].id,
                Is.EqualTo("unit-course-shortcut-1"),
                "Copy() must deep-copy the zones");

            course.shortcutZones[1].id = course.shortcutZones[0].id;
            Assert.That(course.Validate(out error), Is.False);
            Assert.That(error, Does.Contain("shortcut"));
        }

        [Test]
        public void ShortcutZonesMoveTheGameplayFingerprint()
        {
            var first = CompleteFixture();
            var catalog = first.catalog.ToCatalog(first.driving,
                out var error);
            Assert.That(catalog, Is.Not.Null, error);
            var baseline = catalog.CompatibilityFingerprint;

            var second = CompleteFixture();
            second.driving.Courses["aurora-pass"].shortcutZones[0].x += 1f;
            var changed = second.catalog.ToCatalog(second.driving,
                out error);
            Assert.That(changed, Is.Not.Null, error);
            Assert.That(changed.CompatibilityFingerprint,
                Is.Not.EqualTo(baseline),
                "authored shortcut lanes are gameplay content");
        }

        [Test]
        public void TrackAssetMapsShortcutLanes()
        {
            var track = New<TrackDefinitionAsset>();
            track.courseId = "unit-course";
            track.gates = new[]
            {
                new TrackDefinitionAsset.Gate
                {
                    id = "start", halfWidth = 6f,
                    direction = new Vector2(0f, 1f),
                    recoverPosition = new Vector3(0f, 0f, -3f)
                },
                new TrackDefinitionAsset.Gate
                {
                    id = "gate-1", halfWidth = 6f,
                    center = new Vector3(0f, 0f, 16f),
                    direction = new Vector2(0f, 1f),
                    recoverPosition = new Vector3(0f, 0f, 13f)
                }
            };
            track.shortcutLanes = new[]
            {
                new TrackDefinitionAsset.ShortcutLane
                {
                    id = "cut-1", center = new Vector3(-8f, 0f, 8f),
                    halfWidth = 4f, direction = new Vector2(1f, 0f)
                }
            };
            var course = track.ToCourse(out var error);
            Assert.That(course, Is.Not.Null, error);
            Assert.That(course.shortcutZones.Count, Is.EqualTo(1));
            Assert.That(course.shortcutZones[0].id, Is.EqualTo("cut-1"));
            Assert.That(course.shortcutZones[0].dirX, Is.EqualTo(1f));
        }

        [Test]
        public void VoiceCatalogResolvesBoundClips()
        {
            var voice = New<VoicePromptCatalogAsset>();
            var clip = AudioClip.Create("cue-en", 4410, 1, 44100, false);
            voice.cues = new[]
            {
                new VoicePromptCatalogAsset.CueRow
                {
                    cueId = "world.intro.sunny-island", english = clip
                }
            };
            Assert.That(voice.Resolve("world.intro.sunny-island", "en"),
                Is.SameAs(clip));
            Assert.That(voice.Resolve("world.intro.sunny-island", "he"),
                Is.Null, "unbundled locales resolve null, never a substitute");
            Assert.That(voice.Resolve("unknown-cue", "en"), Is.Null);
        }

        private static void WithoutAuthoredProduction(Action assertion)
        {
            var root = FullContentSetup.ContentDir;
            var hidden = root + "-gate-test-" + Guid.NewGuid().ToString("N");
            var exists = UnityEditor.AssetDatabase.IsValidFolder(root);
            if (exists)
            {
                UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                    UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);
                var error = UnityEditor.AssetDatabase.MoveAsset(root, hidden);
                Assert.That(error, Is.Empty, "Cannot isolate production inventory");
            }
            try { assertion(); }
            finally
            {
                if (exists)
                    Assert.That(UnityEditor.AssetDatabase.MoveAsset(hidden, root), Is.Empty,
                        "Must restore authored production inventory");
            }
        }

        // ---- packaging gates ----------------------------------------------

        // Missing-content tests isolate the authored inventory temporarily.
        // Their result must not depend on how much production content exists.
        [Test]
        public void ProductionSetupRejectsIncompleteInventory()
        {
            WithoutAuthoredProduction(() =>
            {
                var ex = Assert.Throws<InvalidDataException>(
                    () => FullContentSetup.Create());
                Assert.That(ex.Message, Does.Contain("production"),
                    "the production gate must name its validation level");

            });
        }

        [Test]
        public void PreviewSetupRejectsUnauthoredContent()
        {
            WithoutAuthoredProduction(() =>
            {
                // Preview binds only authored content; with none authored it
                // fails instead of silently producing an empty player.
                var ex = Assert.Throws<InvalidDataException>(
                    () => FullContentSetup.CreatePreview());
                Assert.That(ex.Message, Does.Contain("preview"),
                    "the preview gate names its stage");

            });
        }

        [Test]
        public void ManifestMarksPartialStageAndNeverACandidate()
        {
            var root = Path.Combine(Path.GetTempPath(),
                "fc-manifest-" + Guid.NewGuid().ToString("N"));
            try
            {
                FullContentSetup.WriteManifest(root, "Development",
                    "partial");
                var manifest = JObject.Parse(File.ReadAllText(
                    Path.Combine(root, "manifest.json")));
                Assert.That((string)manifest["contentStage"],
                    Is.EqualTo("partial"));
                Assert.That((string)manifest["presentation"],
                    Is.EqualTo("FullContent"));
                Assert.That((bool)manifest["candidate"], Is.False,
                    "a preview may never claim an accepted candidate");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        [Test]
        public void ManifestMarksCompleteStageAsCandidate()
        {
            var root = Path.Combine(Path.GetTempPath(),
                "fc-manifest-" + Guid.NewGuid().ToString("N"));
            try
            {
                FullContentSetup.WriteManifest(root, "Ordinary",
                    "complete");
                var manifest = JObject.Parse(File.ReadAllText(
                    Path.Combine(root, "manifest.json")));
                Assert.That((string)manifest["contentStage"],
                    Is.EqualTo("complete"));
                Assert.That((bool)manifest["candidate"], Is.True);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        [Test]
        public void ManifestRejectsUnknownStage()
        {
            var root = Path.Combine(Path.GetTempPath(),
                "fc-manifest-" + Guid.NewGuid().ToString("N"));
            Assert.Throws<ArgumentException>(
                () => FullContentSetup.WriteManifest(root,
                    "Development", "bogus"));
        }

        // The final four flavors and both preview entry points all run
        // the content gate first, so on today's incomplete inventory
        // every one of them throws before touching a build — this is
        // the C# pin behind the tooling dispatch checks.
        [Test]
        public void FinalFlavorsRejectIncompleteContent()
        {
            WithoutAuthoredProduction(() =>
            {
                foreach (var entry in new Action[]
                {
                    FoundationBuild.FullContentSetup,
                    FoundationBuild.WindowsFullContent,
                    FoundationBuild.WindowsFullContentOrdinary,
                    FoundationBuild.ExportAndroidFullContent,
                    FoundationBuild.ExportAndroidFullContentOrdinary
                })
                    Assert.Throws<InvalidDataException>(() => entry(),
                        entry.Method.Name + " must run the production "
                        + "validation gate before any packaging");

            });
        }

        [Test]
        public void PreviewFlavorsRejectUnauthoredContent()
        {
            WithoutAuthoredProduction(() =>
            {
                foreach (var entry in new Action[]
                {
                    FoundationBuild.WindowsFullContentPreview,
                    FoundationBuild.ExportAndroidFullContentPreview
                })
                    Assert.Throws<InvalidDataException>(() => entry(),
                        entry.Method.Name + " must run the preview gate "
                        + "before any packaging");

            });
        }
    }
}
