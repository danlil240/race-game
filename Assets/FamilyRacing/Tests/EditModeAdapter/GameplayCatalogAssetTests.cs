using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
using UnityEngine;

namespace FamilyRacing.Tests
{
    // M5 Task 2: ScriptableObject -> Core definition conversion for the
    // gameplay-loop content assets. Conversion is atomic: an unknown
    // metric kind, a nonpositive par time, or any invalid row fails the
    // whole asset — nothing partially loads.
    public sealed class GameplayCatalogAssetTests
    {
        private static T Asset<T>() where T : ScriptableObject
        {
            return ScriptableObject.CreateInstance<T>();
        }

        private static void Destroy(params Object[] objects)
        {
            foreach (var obj in objects)
                if (obj != null) Object.DestroyImmediate(obj);
        }

        private static EventDefinitionAsset EventAsset(
            string eventType, string classId, int parTicks = 0,
            EventDefinitionAsset.ObjectiveRow[] objectives = null)
        {
            var asset = Asset<EventDefinitionAsset>();
            asset.eventId = "evt-" + eventType + "-" + classId;
            asset.track = null; // track bound below
            asset.eventType = eventType;
            asset.vehicleClass = classId;
            asset.parTicks = parTicks;
            asset.isFinale = eventType == GameplayEventType.Finale;
            asset.vehicleIds = new[] { classId + "-01" };
            asset.objectives = objectives ?? new EventDefinitionAsset.ObjectiveRow[0];
            return asset;
        }

        private static TrackDefinitionAsset TrackAsset(string courseId)
        {
            var asset = Asset<TrackDefinitionAsset>();
            asset.courseId = courseId;
            asset.gates = new[]
            {
                new TrackDefinitionAsset.Gate { id = "start", halfWidth = 6f,
                    direction = new Vector2(0f, 1f), recoverPosition = new Vector3(0f, 0f, -3f) },
                new TrackDefinitionAsset.Gate { id = "gate-1", halfWidth = 6f,
                    direction = new Vector2(0f, 1f),
                    center = new Vector3(0f, 0f, 16f),
                    recoverPosition = new Vector3(0f, 0f, 13f) }
            };
            return asset;
        }

        private static EventDefinitionAsset.ObjectiveRow Objective(
            string id, string kind, string metric, long threshold,
            string comparator = "atLeast")
        {
            return new EventDefinitionAsset.ObjectiveRow
            {
                objectiveId = id,
                kind = kind,
                alternatives = new[]
                {
                    new EventDefinitionAsset.ObjectiveConditionRow
                    {
                        metric = metric,
                        comparator = comparator,
                        threshold = threshold
                    }
                }
            };
        }

        private static EventDefinitionAsset.ObjectiveRow[] ObjectivePair(
            string eventId, long introThreshold, long masteryThreshold,
            string metric, string comparator = "atLeast")
        {
            return new[]
            {
                Objective(eventId + "-intro", "introductory", metric,
                    introThreshold, comparator),
                Objective(eventId + "-mastery", "mastery", metric,
                    masteryThreshold, comparator)
            };
        }

        [Test]
        public void EventAssetRejectsUnknownMetricKind()
        {
            var track = TrackAsset("harbor-hop");
            var asset = EventAsset(GameplayEventType.Boost, "car", 0,
                ObjectivePair("harbor-hop-car-boost", 3, 8, "teleportation"));
            asset.track = track;
            try
            {
                string error;
                Assert.That(asset.ToEvent(out error), Is.Null);
                Assert.That(error, Does.Contain("metric"),
                    "unknown metric kinds must be named in the error");
            }
            finally
            {
                Destroy(asset, track);
            }
        }

        [Test]
        public void EventAssetRejectsNonpositiveParTime()
        {
            var track = TrackAsset("harbor-hop");
            var objectives = new[]
            {
                Objective("t-intro", "introductory",
                    ObjectiveMetric.FinishWithinTicks, 5400, "atMost"),
                Objective("t-mastery", "mastery",
                    ObjectiveMetric.FinishWithinTicks, 3600, "atMost")
            };
            var zeroPar = EventAsset(GameplayEventType.Time, "car", 0, objectives);
            zeroPar.track = track;
            var negativePar = EventAsset(GameplayEventType.Time, "car", -30, objectives);
            negativePar.track = track;
            try
            {
                string error;
                Assert.That(zeroPar.ToEvent(out error), Is.Null);
                Assert.That(error, Does.Contain("parTicks"),
                    "nonpositive par times must not partially load");
                Assert.That(negativePar.ToEvent(out error), Is.Null);
            }
            finally
            {
                Destroy(zeroPar, negativePar, track);
            }
        }

        [Test]
        public void EventAssetConvertsTypedEventWithObjectives()
        {
            var track = TrackAsset("harbor-hop");
            var asset = EventAsset(GameplayEventType.Stunt, "motorcycle", 0,
                ObjectivePair("evt", 2, 5, ObjectiveMetric.CleanLandings));
            asset.track = track;
            try
            {
                string error;
                var evt = asset.ToEvent(out error);
                Assert.That(evt, Is.Not.Null, error);
                Assert.That(evt.eventType, Is.EqualTo(GameplayEventType.Stunt));
                Assert.That(evt.vehicleClass, Is.EqualTo("motorcycle"));
                Assert.That(evt.objectives, Has.Count.EqualTo(2));
            }
            finally
            {
                Destroy(asset, track);
            }
        }

        [Test]
        public void LegacyEventAssetStillConvertsWithoutGameplayFields()
        {
            var track = TrackAsset("graybox-01");
            var asset = Asset<EventDefinitionAsset>();
            asset.track = track;
            asset.eventId = "practice-02";
            try
            {
                string error;
                var evt = asset.ToEvent(out error);
                Assert.That(evt, Is.Not.Null, error);
                Assert.That(evt.eventType, Is.EqualTo(""),
                    "legacy events keep the empty type");
                Assert.That(evt.objectives, Is.Null);
            }
            finally
            {
                Destroy(asset, track);
            }
        }

        [Test]
        public void BountyAssetRejectsUnknownMetricKind()
        {
            var asset = Asset<BountyCatalogAsset>();
            asset.bounties = new[]
            {
                new BountyCatalogAsset.BountyRow
                {
                    bountyId = "drift-10s", family = "drift",
                    metric = "driftSeconds", threshold = 10,
                    unit = "seconds", rewardCoins = 10
                },
                new BountyCatalogAsset.BountyRow
                {
                    bountyId = "bad-1", family = "drift",
                    metric = "wallHacks", threshold = 10,
                    unit = "seconds", rewardCoins = 10
                }
            };
            try
            {
                string error;
                Assert.That(asset.ToDefinitions(out error), Is.Null,
                    "one bad row fails the whole catalog — no partial load");
                Assert.That(error, Does.Contain("metric"));
            }
            finally
            {
                Destroy(asset);
            }
        }

        [Test]
        public void GameplayCatalogAssetConvertsAtomically()
        {
            var campaign = Asset<CampaignDefinitionAsset>();
            var bounties = Asset<BountyCatalogAsset>();
            var economy = Asset<EconomyDefinitionAsset>();
            var gadgets = Asset<GadgetCatalogAsset>();
            var root = Asset<GameplayCatalogAsset>();
            try
            {
                campaign.worldId = "sunny-island";
                campaign.nameHe = "האי השמשי";
                campaign.requiredEventCompletions = 6;
                campaign.requiredRegionalStars = 18;
                campaign.courseIds = new[] { "harbor-hop" };
                campaign.regularEventIds = new[]
                {
                    "harbor-hop-car-circuit", "harbor-hop-car-boost",
                    "harbor-hop-car-stunt", "harbor-hop-car-time",
                    "harbor-hop-car-gadget", "harbor-hop-car-clean-driving"
                };
                campaign.finales = new[]
                {
                    new CampaignDefinitionAsset.FinaleRow
                    {
                        eventId = "sunny-island-car-finale", classId = "car",
                        rivalId = "rival-car", rewardVehicleId = "car-01"
                    }
                };
                bounties.bounties = new[]
                {
                    new BountyCatalogAsset.BountyRow
                    {
                        bountyId = "drift-10s", family = "drift",
                        metric = BountyMetric.DriftSeconds, threshold = 10,
                        unit = "seconds", rewardCoins = 10
                    }
                };
                economy.upgradeCategories = new[]
                {
                    new EconomyDefinitionAsset.CategoryRow
                    {
                        categoryId = "acceleration",
                        prices = new[] { 60, 120, 180 },
                        tuningFields = new[] { "acceleration" }
                    }
                };
                gadgets.gadgets = new[]
                {
                    new GadgetCatalogAsset.GadgetRow
                    {
                        gadgetId = "turbo", kind = GadgetKind.Turbo,
                        durationTicks = 60, accelerationMultiplier = 1.25f,
                        speedCapMultiplier = 1.10f
                    }
                };
                root.campaign = campaign;
                root.bounties = bounties;
                root.economy = economy;
                root.gadgets = gadgets;
                root.difficulties = new[]
                {
                    new GameplayCatalogAsset.DifficultyRow { difficultyId = "easy" }
                };

                var driving = BuildDriving();
                string error;
                var catalog = root.ToCatalog(driving, out error);
                Assert.That(catalog, Is.Not.Null, error);
                Assert.That(catalog.Version, Is.EqualTo("full-content-1"));
                Assert.That(catalog.Campaigns.Count, Is.EqualTo(1),
                    "the legacy single-campaign field converts as one "
                    + "world");

                // Corrupt one gadget row -> the whole conversion fails.
                gadgets.gadgets[0].kind = "warp-drive";
                Assert.That(root.ToCatalog(driving, out error), Is.Null);
                Assert.That(error, Does.Contain("gadget"));
            }
            finally
            {
                Destroy(campaign, bounties, economy, gadgets, root);
            }
        }

        private static DrivingContentCatalog BuildDriving()
        {
            var events = CarEvents("harbor-hop");
            events.Add(CarFinale("sunny-island", "harbor-hop"));
            return DrivingContentCatalog.Create(
                "gameplay-loop-1",
                new[] { VehicleTuning.Car01() },
                new[] { TestCourse("harbor-hop") },
                events);
        }

        // M6 Task 1: a second world (canyon-dash) beside the first so the
        // campaigns array and the world lookups have real content to
        // bind. Both worlds keep one class (car) and six event types.
        private static DrivingContentCatalog BuildTwoWorldDriving()
        {
            var events = CarEvents("harbor-hop");
            events.Add(CarFinale("sunny-island", "harbor-hop"));
            events.AddRange(CarEvents("canyon-dash"));
            events.Add(CarFinale("redrock-canyon", "canyon-dash"));
            return DrivingContentCatalog.Create(
                "full-content-1",
                new[] { VehicleTuning.Car01() },
                new[] { TestCourse("harbor-hop"), TestCourse("canyon-dash") },
                events);
        }

        private static CourseDefinition TestCourse(string courseId)
        {
            var course = new CourseDefinition
            {
                id = courseId, spawnX = 0f, spawnY = 0f, spawnZ = -12f
            };
            course.checkpoints.Add(new CheckpointGate
            {
                id = "start", x = 0f, z = 0f, halfWidth = 6f,
                dirX = 0f, dirZ = 1f, recoverX = 0f, recoverZ = -3f
            });
            course.checkpoints.Add(new CheckpointGate
            {
                id = "gate-1", x = 0f, z = 16f, halfWidth = 6f,
                dirX = 0f, dirZ = 1f, recoverX = 0f, recoverZ = 13f
            });
            return course;
        }

        private static List<EventDefinition> CarEvents(string courseId)
        {
            var events = new List<EventDefinition>();
            var types = new[]
            {
                GameplayEventType.Circuit, GameplayEventType.Boost, GameplayEventType.Stunt,
                GameplayEventType.Time, GameplayEventType.Gadget, GameplayEventType.CleanDriving
            };
            foreach (var eventType in types)
            {
                var id = courseId + "-car-" + eventType;
                var evt = new EventDefinition
                {
                    id = id, courseId = courseId, laps = 1,
                    countdownTicks = 90,
                    vehicleIds = new List<string> { "car-01" },
                    recovery = new RecoverySettings(),
                    eventType = eventType, vehicleClass = "car",
                    parTicks = eventType == GameplayEventType.Time ? 3600 : 0,
                    objectives = new List<ObjectiveDefinition>
                    {
                        new ObjectiveDefinition
                        {
                            id = id + "-intro", kind = ObjectiveKind.Introductory,
                            alternatives =
                            {
                                new ObjectiveCondition
                                {
                                    metric = eventType == GameplayEventType.Time
                                        ? ObjectiveMetric.FinishWithinTicks
                                        : ObjectiveMetric.CleanLandings,
                                    comparator = eventType == GameplayEventType.Time
                                        ? ObjectiveComparator.AtMost
                                        : ObjectiveComparator.AtLeast,
                                    threshold = eventType == GameplayEventType.Time ? 5400 : 1
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
                                    metric = eventType == GameplayEventType.Time
                                        ? ObjectiveMetric.FinishWithinTicks
                                        : ObjectiveMetric.CleanLandings,
                                    comparator = eventType == GameplayEventType.Time
                                        ? ObjectiveComparator.AtMost
                                        : ObjectiveComparator.AtLeast,
                                    threshold = eventType == GameplayEventType.Time ? 3600 : 5
                                }
                            }
                        }
                    }
                };
                events.Add(evt);
            }
            return events;
        }

        private static EventDefinition CarFinale(string worldId,
            string courseId)
        {
            return new EventDefinition
            {
                id = worldId + "-car-finale", courseId = courseId,
                laps = 3, countdownTicks = 90,
                vehicleIds = new List<string> { "car-01" },
                recovery = new RecoverySettings(),
                eventType = GameplayEventType.Finale, vehicleClass = "car",
                isFinale = true
            };
        }

        // One-car one-course campaign world asset: six regular events on
        // the course plus the class finale, gated at 6 completions /
        // 18 stars (the maximum the six events can award).
        private static void FillWorld(CampaignDefinitionAsset asset,
            string worldId, string nameEn, string nameHe, string courseId)
        {
            asset.worldId = worldId;
            asset.nameEn = nameEn;
            asset.nameHe = nameHe;
            asset.requiredEventCompletions = 6;
            asset.requiredRegionalStars = 18;
            asset.courseIds = new[] { courseId };
            asset.regularEventIds = new[]
            {
                courseId + "-car-circuit", courseId + "-car-boost",
                courseId + "-car-stunt", courseId + "-car-time",
                courseId + "-car-gadget", courseId + "-car-clean-driving"
            };
            asset.finales = new[]
            {
                new CampaignDefinitionAsset.FinaleRow
                {
                    eventId = worldId + "-car-finale", classId = "car",
                    rivalId = worldId + "-car-rival",
                    rewardVehicleId = "car-01"
                }
            };
        }

        private static void FillSections(GameplayCatalogAsset root,
            BountyCatalogAsset bounties, EconomyDefinitionAsset economy,
            GadgetCatalogAsset gadgets)
        {
            bounties.bounties = new[]
            {
                new BountyCatalogAsset.BountyRow
                {
                    bountyId = "drift-10s", family = "drift",
                    metric = BountyMetric.DriftSeconds, threshold = 10,
                    unit = "seconds", rewardCoins = 10
                }
            };
            economy.upgradeCategories = new[]
            {
                new EconomyDefinitionAsset.CategoryRow
                {
                    categoryId = "acceleration",
                    prices = new[] { 60, 120, 180 },
                    tuningFields = new[] { "acceleration" }
                }
            };
            gadgets.gadgets = new[]
            {
                new GadgetCatalogAsset.GadgetRow
                {
                    gadgetId = "turbo", kind = GadgetKind.Turbo,
                    durationTicks = 60, accelerationMultiplier = 1.25f,
                    speedCapMultiplier = 1.10f
                }
            };
            root.bounties = bounties;
            root.economy = economy;
            root.gadgets = gadgets;
            root.difficulties = new[]
            {
                new GameplayCatalogAsset.DifficultyRow
                {
                    difficultyId = "easy"
                }
            };
        }

        // --- M6 Task 1: the campaigns array -----------------------------

        [Test]
        public void CampaignsArrayConvertsEveryWorldInOrder()
        {
            var sunny = Asset<CampaignDefinitionAsset>();
            var redrock = Asset<CampaignDefinitionAsset>();
            var bounties = Asset<BountyCatalogAsset>();
            var economy = Asset<EconomyDefinitionAsset>();
            var gadgets = Asset<GadgetCatalogAsset>();
            var root = Asset<GameplayCatalogAsset>();
            try
            {
                FillWorld(sunny, "sunny-island", "Sunny Island",
                    "האי השמשי", "harbor-hop");
                FillWorld(redrock, "redrock-canyon", "Redrock Canyon",
                    "קניון רדרוק", "canyon-dash");
                FillSections(root, bounties, economy, gadgets);
                root.campaigns = new[] { sunny, redrock };

                var catalog = root.ToCatalog(BuildTwoWorldDriving(),
                    out var error);
                Assert.That(catalog, Is.Not.Null, error);
                Assert.That(catalog.Campaigns.Count, Is.EqualTo(2));
                Assert.That(catalog.Campaigns[0].worldId,
                    Is.EqualTo("sunny-island"));
                Assert.That(catalog.Campaigns[1].worldId,
                    Is.EqualTo("redrock-canyon"));
                Assert.That(catalog.Campaign,
                    Is.SameAs(catalog.Campaigns[0]));
                Assert.That(
                    catalog.FindCampaignForEvent("canyon-dash-car-circuit")
                        .worldId,
                    Is.EqualTo("redrock-canyon"));
                Assert.That(catalog.Version,
                    Is.EqualTo("full-content-1"));
            }
            finally
            {
                Destroy(sunny, redrock, bounties, economy, gadgets, root);
            }
        }

        [Test]
        public void ConflictingLegacyAndArrayCampaignFieldsReject()
        {
            // The legacy `campaign` field and the new `campaigns` array
            // must never both be populated — silently choosing one would
            // hide an authoring error.
            var legacy = Asset<CampaignDefinitionAsset>();
            var listed = Asset<CampaignDefinitionAsset>();
            var bounties = Asset<BountyCatalogAsset>();
            var economy = Asset<EconomyDefinitionAsset>();
            var gadgets = Asset<GadgetCatalogAsset>();
            var root = Asset<GameplayCatalogAsset>();
            try
            {
                FillWorld(legacy, "sunny-island", "Sunny Island",
                    "האי השמשי", "harbor-hop");
                FillWorld(listed, "redrock-canyon", "Redrock Canyon",
                    "קניון רדרוק", "canyon-dash");
                FillSections(root, bounties, economy, gadgets);
                root.campaign = legacy;
                root.campaigns = new[] { listed };

                string error;
                Assert.That(root.ToCatalog(BuildTwoWorldDriving(),
                    out error), Is.Null);
                Assert.That(error, Does.Contain("campaign"));
            }
            finally
            {
                Destroy(legacy, listed, bounties, economy, gadgets, root);
            }
        }

        [Test]
        public void EmptyCampaignsArrayFallsBackToLegacyField()
        {
            var campaign = Asset<CampaignDefinitionAsset>();
            var bounties = Asset<BountyCatalogAsset>();
            var economy = Asset<EconomyDefinitionAsset>();
            var gadgets = Asset<GadgetCatalogAsset>();
            var root = Asset<GameplayCatalogAsset>();
            try
            {
                FillWorld(campaign, "sunny-island", "Sunny Island",
                    "האי השמשי", "harbor-hop");
                FillSections(root, bounties, economy, gadgets);
                root.campaign = campaign;
                root.campaigns = new CampaignDefinitionAsset[0];

                string error;
                var catalog = root.ToCatalog(BuildDriving(), out error);
                Assert.That(catalog, Is.Not.Null, error);
                Assert.That(catalog.Campaigns.Count, Is.EqualTo(1));
                Assert.That(catalog.Campaign.worldId,
                    Is.EqualTo("sunny-island"));
            }
            finally
            {
                Destroy(campaign, bounties, economy, gadgets, root);
            }
        }

        [Test]
        public void CampaignsArrayRejectsNullSlot()
        {
            var sunny = Asset<CampaignDefinitionAsset>();
            var bounties = Asset<BountyCatalogAsset>();
            var economy = Asset<EconomyDefinitionAsset>();
            var gadgets = Asset<GadgetCatalogAsset>();
            var root = Asset<GameplayCatalogAsset>();
            try
            {
                FillWorld(sunny, "sunny-island", "Sunny Island",
                    "האי השמשי", "harbor-hop");
                FillSections(root, bounties, economy, gadgets);
                root.campaigns = new CampaignDefinitionAsset[]
                    { sunny, null };

                string error;
                Assert.That(root.ToCatalog(BuildDriving(), out error),
                    Is.Null);
                Assert.That(error, Does.Contain("campaign"));
            }
            finally
            {
                Destroy(sunny, bounties, economy, gadgets, root);
            }
        }
    }
}
