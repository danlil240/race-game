using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // M5 Task 2: the first-world (Sunny Island) gameplay content model.
    // These tests pin the *fixture* catalog contract that every later
    // task consumes: 2 courses x 2 classes x 6 event types = 24 regular
    // events + one rival finale per class, 72 possible regular stars
    // (completion + introductory + mastery bits), three upgrade
    // categories with prices [60,120,180], the initial challenge bounty
    // IDs, and full fingerprint coverage of every gameplay-affecting
    // value. Fixture catalogs are built inline here — they are NOT the
    // authored shipping content (Task 9 owns that).
    public sealed class GameplayCatalogTests
    {
        private static readonly System.Text.RegularExpressions.Regex Hex64 =
            new System.Text.RegularExpressions.Regex("^[0-9a-f]{64}$");

        private static readonly string[] CourseIds = { "harbor-hop", "lighthouse-loop" };
        private static readonly string[] ClassIds = { "car", "motorcycle" };
        private static readonly string[] EventTypes =
        {
            GameplayEventType.Circuit, GameplayEventType.Boost, GameplayEventType.Stunt,
            GameplayEventType.Time, GameplayEventType.Gadget, GameplayEventType.CleanDriving
        };

        // --- fixture builders ----------------------------------------------

        private static VehicleTuning Vehicle(string id)
        {
            var tuning = id.StartsWith("car")
                ? VehicleTuning.Car01() : VehicleTuning.Motorcycle01();
            tuning.vehicleId = id;
            return tuning;
        }

        private static CourseDefinition Course(string id)
        {
            var course = new CourseDefinition
            {
                id = id,
                spawnX = 0f,
                spawnY = 0f,
                spawnZ = -12f,
                spawnHeading = 0f
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

        // Objective templates per the plan table: every regular event has a
        // completion bit plus an introductory and a mastery objective.
        private static List<ObjectiveDefinition> ObjectivesFor(
            string eventType, string eventId, long parTicks)
        {
            ObjectiveCondition Intro()
            {
                switch (eventType)
                {
                    case GameplayEventType.Circuit:
                        return new ObjectiveCondition { metric = ObjectiveMetric.CleanLandings, threshold = 1 };
                    case GameplayEventType.Boost:
                        return new ObjectiveCondition { metric = ObjectiveMetric.HumanBoostActivations, threshold = 3 };
                    case GameplayEventType.Stunt:
                        return new ObjectiveCondition { metric = ObjectiveMetric.CleanLandings, threshold = 2 };
                    case GameplayEventType.Time:
                        return new ObjectiveCondition
                        {
                            metric = ObjectiveMetric.FinishWithinTicks,
                            comparator = ObjectiveComparator.AtMost,
                            threshold = (parTicks + 1) / 2 + parTicks // ceil(1.5 * parTicks)
                        };
                    case GameplayEventType.Gadget:
                        return new ObjectiveCondition { metric = ObjectiveMetric.GadgetsCollectedAndUsed, threshold = 2 };
                    default: // CleanDriving
                        return new ObjectiveCondition { metric = ObjectiveMetric.CleanStreakSeconds, threshold = 30 };
                }
            }

            var mastery = new ObjectiveDefinition { id = eventId + "-mastery", kind = ObjectiveKind.Mastery };
            switch (eventType)
            {
                case GameplayEventType.Circuit:
                    // Finish first OR validate both distinct shortcuts.
                    mastery.alternatives.Add(new ObjectiveCondition
                    {
                        metric = ObjectiveMetric.FinishPlace,
                        comparator = ObjectiveComparator.AtMost, threshold = 1
                    });
                    mastery.alternatives.Add(new ObjectiveCondition
                    {
                        metric = ObjectiveMetric.DistinctShortcuts, threshold = 2
                    });
                    break;
                case GameplayEventType.Boost:
                    mastery.alternatives.Add(new ObjectiveCondition
                        { metric = ObjectiveMetric.HumanBoostActivations, threshold = 8 });
                    break;
                case GameplayEventType.Stunt:
                    mastery.alternatives.Add(new ObjectiveCondition
                        { metric = ObjectiveMetric.CleanLandings, threshold = 5 });
                    break;
                case GameplayEventType.Time:
                    mastery.alternatives.Add(new ObjectiveCondition
                    {
                        metric = ObjectiveMetric.FinishWithinTicks,
                        comparator = ObjectiveComparator.AtMost, threshold = parTicks
                    });
                    break;
                case GameplayEventType.Gadget:
                    mastery.alternatives.Add(new ObjectiveCondition
                        { metric = ObjectiveMetric.DistinctGadgetTypesUsed, threshold = 3 });
                    break;
                default: // CleanDriving
                    // 60 s streak AND zero recoveries -> one alternative pair:
                    // both conditions in one leg must hold (AND inside a leg,
                    // OR across legs).
                    mastery.alternatives.Add(new ObjectiveCondition
                    {
                        metric = ObjectiveMetric.CleanStreakSeconds, threshold = 60,
                        alsoRequires = new List<ObjectiveCondition>
                        {
                            new ObjectiveCondition
                            {
                                metric = ObjectiveMetric.Recoveries,
                                comparator = ObjectiveComparator.AtMost, threshold = 0
                            }
                        }
                    });
                    break;
            }

            return new List<ObjectiveDefinition>
            {
                new ObjectiveDefinition
                {
                    id = eventId + "-intro", kind = ObjectiveKind.Introductory,
                    alternatives = { Intro() }
                },
                mastery
            };
        }

        private static string RegularEventId(string courseId, string classId, string eventType)
        {
            return courseId + "-" + classId + "-" + eventType;
        }

        private static EventDefinition RegularEvent(string courseId, string classId, string eventType)
        {
            var id = RegularEventId(courseId, classId, eventType);
            var evt = new EventDefinition
            {
                id = id,
                courseId = courseId,
                laps = eventType == GameplayEventType.Time ? 1 : 2,
                countdownTicks = 90,
                vehicleIds = new List<string> { classId + "-01", classId + "-02" },
                recovery = new RecoverySettings(),
                completionCoins = 0,
                eventType = eventType,
                vehicleClass = classId,
                parTicks = eventType == GameplayEventType.Time ? 3600 : 0
            };
            evt.objectives = ObjectivesFor(eventType, id, evt.parTicks);
            return evt;
        }

        private static EventDefinition Finale(string classId, string courseId)
        {
            return new EventDefinition
            {
                id = "sunny-island-" + classId + "-finale",
                courseId = courseId,
                laps = 3,
                countdownTicks = 90,
                vehicleIds = new List<string> { classId + "-01", classId + "-02" },
                recovery = new RecoverySettings(),
                completionCoins = 0,
                eventType = GameplayEventType.Finale,
                vehicleClass = classId,
                isFinale = true
            };
        }

        private static DrivingContentCatalog Driving()
        {
            var events = new List<EventDefinition>();
            foreach (var courseId in CourseIds)
                foreach (var classId in ClassIds)
                    foreach (var eventType in EventTypes)
                        events.Add(RegularEvent(courseId, classId, eventType));
            // One rival finale per class for the world (not per course).
            foreach (var classId in ClassIds)
                events.Add(Finale(classId, CourseIds[0]));
            return DrivingContentCatalog.Create(
                "gameplay-loop-1",
                new[]
                {
                    Vehicle("car-01"), Vehicle("car-02"),
                    Vehicle("motorcycle-01"), Vehicle("motorcycle-02")
                },
                new[] { Course("harbor-hop"), Course("lighthouse-loop") },
                events);
        }

        private static CampaignDefinition Campaign()
        {
            var campaign = new CampaignDefinition
            {
                worldId = "sunny-island",
                nameEn = "Sunny Island",
                nameHe = "האי השמשי",
                requiredEventCompletions = 24,
                requiredRegionalStars = 30
            };
            foreach (var courseId in CourseIds) campaign.courseIds.Add(courseId);
            foreach (var courseId in CourseIds)
                foreach (var classId in ClassIds)
                    foreach (var eventType in EventTypes)
                        campaign.regularEventIds.Add(
                            RegularEventId(courseId, classId, eventType));
            campaign.finales.Add(new FinaleDefinition
            {
                eventId = "sunny-island-car-finale",
                classId = "car",
                rivalId = "rival-car",
                rewardVehicleId = "car-02"
            });
            campaign.finales.Add(new FinaleDefinition
            {
                eventId = "sunny-island-motorcycle-finale",
                classId = "motorcycle",
                rivalId = "rival-motorcycle",
                rewardVehicleId = "motorcycle-02"
            });
            return campaign;
        }

        // Two thresholds for each of the six challenge families; the three
        // initial active assignments seed from drift-10s / boost-3 / landing-2.
        private static List<BountyDefinition> Bounties()
        {
            var list = new List<BountyDefinition>();
            void Add(string id, string family, string metric, long threshold,
                string unit, int coins, string aggregation)
            {
                list.Add(new BountyDefinition
                {
                    id = id, family = family, metric = metric,
                    threshold = threshold, unit = unit,
                    rewardCoins = coins, aggregation = aggregation,
                    repeatable = true
                });
            }
            Add("drift-10s", "drift", BountyMetric.DriftSeconds, 10, "seconds", 10, BountyAggregation.Sum);
            Add("drift-30s", "drift", BountyMetric.DriftSeconds, 30, "seconds", 20, BountyAggregation.Sum);
            Add("boost-3", "boost", BountyMetric.HumanBoostActivations, 3, "count", 10, BountyAggregation.Sum);
            Add("boost-8", "boost", BountyMetric.HumanBoostActivations, 8, "count", 20, BountyAggregation.Sum);
            Add("landing-2", "landing", BountyMetric.CleanLandings, 2, "count", 10, BountyAggregation.Sum);
            Add("landing-5", "landing", BountyMetric.CleanLandings, 5, "count", 20, BountyAggregation.Sum);
            Add("streak-30s", "clean-streak", BountyMetric.CleanStreakSeconds, 30, "seconds", 10, BountyAggregation.Max);
            Add("streak-90s", "clean-streak", BountyMetric.CleanStreakSeconds, 90, "seconds", 20, BountyAggregation.Max);
            Add("shortcut-2", "shortcut", BountyMetric.AcceptedShortcuts, 2, "count", 10, BountyAggregation.Sum);
            Add("shortcut-5", "shortcut", BountyMetric.AcceptedShortcuts, 5, "count", 20, BountyAggregation.Sum);
            Add("finish-2", "human-finish", BountyMetric.HumanFinishes, 2, "count", 10, BountyAggregation.Sum);
            Add("finish-5", "human-finish", BountyMetric.HumanFinishes, 5, "count", 20, BountyAggregation.Sum);
            return list;
        }

        private static EconomyDefinition Economy()
        {
            var economy = new EconomyDefinition
            {
                completionCoins = 20,
                challengeIntroductoryCoins = 10,
                challengeMasteryCoins = 20,
                rivalVictoryCoins = 30
            };
            economy.placementBonusCoins.Add(5);
            economy.placementBonusCoins.Add(3);
            economy.placementBonusCoins.Add(1);
            economy.placementBonusCoins.Add(0);
            foreach (var category in new[] { "acceleration", "handling", "boost" })
            {
                var cat = new UpgradeCategoryDefinition { id = category, maxLevel = 3 };
                cat.prices.Add(60);
                cat.prices.Add(120);
                cat.prices.Add(180);
                // Spec mapping: which tuning fields this category scales.
                if (category == "acceleration")
                    cat.tuningFields.Add("acceleration");
                else if (category == "handling")
                {
                    cat.tuningFields.Add("steeringRate");
                    cat.tuningFields.Add("grip");
                }
                else
                {
                    cat.tuningFields.Add("boostAcceleration");
                    cat.tuningFields.Add("boostMaxSpeed");
                }
                economy.upgradeCategories.Add(cat);
            }
            return economy;
        }

        private static GadgetDefinitionCatalog Gadgets()
        {
            var catalog = new GadgetDefinitionCatalog
            {
                pickupRespawnTicks = 300,
                heldSlotCapacity = 1
            };
            catalog.gadgets.Add(new GadgetDefinition
            {
                id = "turbo", kind = GadgetKind.Turbo,
                durationTicks = 60,
                accelerationMultiplier = 1.25f,
                speedCapMultiplier = 1.10f
            });
            catalog.gadgets.Add(new GadgetDefinition
            {
                id = "bubble-shield", kind = GadgetKind.BubbleShield,
                charges = 1
            });
            catalog.gadgets.Add(new GadgetDefinition
            {
                id = "foam-trap", kind = GadgetKind.FoamTrap,
                patchRadiusMeters = 3f,
                patchLifetimeTicks = 180,
                slowMultiplier = 0.70f,
                slowDurationTicks = 45,
                immunityTicks = 60
            });
            return catalog;
        }

        private static List<AiDifficultyDefinition> Difficulties()
        {
            return new List<AiDifficultyDefinition>
            {
                new AiDifficultyDefinition { id = "easy" },
                new AiDifficultyDefinition { id = "medium" },
                new AiDifficultyDefinition { id = "hard" }
            };
        }

        private static GameplayCatalog Catalog()
        {
            var ok = GameplayCatalog.TryCreate(
                Driving(), Campaign(), Bounties(), Economy(), Gadgets(),
                Difficulties(), out var catalog, out var error);
            Assert.That(ok, Is.True, "fixture catalog must validate: " + error);
            return catalog;
        }

        private static void AssertRejected(
            DrivingContentCatalog driving = null,
            CampaignDefinition campaign = null,
            IReadOnlyList<BountyDefinition> bounties = null,
            EconomyDefinition economy = null,
            GadgetDefinitionCatalog gadgets = null,
            IReadOnlyList<AiDifficultyDefinition> difficulties = null,
            string because = "")
        {
            var ok = GameplayCatalog.TryCreate(
                driving ?? Driving(),
                campaign ?? Campaign(),
                bounties ?? Bounties(),
                economy ?? Economy(),
                gadgets ?? Gadgets(),
                difficulties ?? Difficulties(),
                out var catalog, out var error);
            Assert.That(ok, Is.False,
                "catalog must be rejected (" + because + ") but was accepted");
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(catalog, Is.Null);
        }

        // --- binding tests --------------------------------------------------

        [Test]
        public void TwoCoursesProduceTwentyFourRegularEventsAndTwoFinales()
        {
            var catalog = Catalog();
            var campaign = catalog.Campaign;

            Assert.That(campaign.regularEventIds, Has.Count.EqualTo(24),
                "2 courses x 2 classes x 6 event types = 24 regular events");
            Assert.That(campaign.finales, Has.Count.EqualTo(2),
                "one rival finale per class");
            Assert.That(catalog.PossibleRegularStars, Is.EqualTo(72),
                "24 events x 3 objective bits (completion, introductory, mastery)");

            foreach (var courseId in CourseIds)
                foreach (var classId in ClassIds)
                    foreach (var eventType in EventTypes)
                        Assert.That(
                            campaign.regularEventIds,
                            Does.Contain(RegularEventId(courseId, classId, eventType)),
                            "missing event " + courseId + "/" + classId + "/" + eventType);
            Assert.That(campaign.regularEventIds,
                Does.Contain("harbor-hop-car-circuit"));
            Assert.That(campaign.regularEventIds,
                Does.Contain("lighthouse-loop-motorcycle-clean-driving"));

            var finaleIds = new List<string>();
            foreach (var finale in campaign.finales) finaleIds.Add(finale.eventId);
            Assert.That(finaleIds,
                Is.EquivalentTo(new[]
                {
                    "sunny-island-car-finale",
                    "sunny-island-motorcycle-finale"
                }));

            // Every regular event carries exactly the three star bits.
            foreach (var eventId in campaign.regularEventIds)
            {
                var evt = catalog.Driving.Events[eventId];
                Assert.That(evt.objectives, Has.Count.EqualTo(2), eventId);
                Assert.That(evt.objectives[0].kind,
                    Is.EqualTo(ObjectiveKind.Introductory), eventId);
                Assert.That(evt.objectives[1].kind,
                    Is.EqualTo(ObjectiveKind.Mastery), eventId);
            }

            // Difficulty ids and upgrade prices pinned by the plan.
            var difficultyIds = new List<string>();
            foreach (var difficulty in catalog.Difficulties)
                difficultyIds.Add(difficulty.id);
            Assert.That(difficultyIds,
                Is.EquivalentTo(new[] { "easy", "medium", "hard" }));

            Assert.That(catalog.Economy.upgradeCategories, Has.Count.EqualTo(3));
            var expectedFields = new Dictionary<string, string[]>
            {
                ["acceleration"] = new[] { "acceleration" },
                ["handling"] = new[] { "steeringRate", "grip" },
                ["boost"] = new[] { "boostAcceleration", "boostMaxSpeed" }
            };
            foreach (var category in catalog.Economy.upgradeCategories)
            {
                Assert.That(category.maxLevel, Is.EqualTo(3), category.id);
                Assert.That(category.prices,
                    Is.EqualTo(new[] { 60, 120, 180 }), category.id);
                Assert.That(category.tuningFields,
                    Is.EqualTo(expectedFields[category.id]),
                    category.id + " tuning field mapping");
            }

            // Initial challenge IDs the profile seeds its three assignments
            // from (Task 3).
            var bountyIds = new List<string>();
            foreach (var bounty in catalog.Bounties) bountyIds.Add(bounty.id);
            Assert.That(bountyIds,
                Is.SupersetOf(new[] { "drift-10s", "boost-3", "landing-2" }));
        }

        [Test]
        public void FinaleDefinitionRequiresTwentyFourCompletionsAndThirtyStars()
        {
            var catalog = Catalog();
            Assert.That(catalog.Campaign.requiredEventCompletions, Is.EqualTo(24),
                "finales need all 24 regular-event completions");
            Assert.That(catalog.Campaign.requiredRegionalStars, Is.EqualTo(30),
                "finales need at least 30 regional stars");

            // A gate that can never be satisfied must not load.
            var impossible = Campaign();
            impossible.requiredRegionalStars = 73; // only 72 regular stars exist
            AssertRejected(campaign: impossible,
                because: "star requirement above achievable maximum");

            // A gate mismatching the authored regular event count is invalid.
            var shortGate = Campaign();
            shortGate.requiredEventCompletions = 23;
            AssertRejected(campaign: shortGate,
                because: "completion requirement below authored event count");

            // A finale bound to a class with no finale event, or to an
            // unknown event, fails cross-reference validation.
            var missingFinale = Campaign();
            missingFinale.finales[0].eventId = "sunny-island-motorcycle-finale";
            AssertRejected(campaign: missingFinale,
                because: "duplicate/misbound finale event");

            var unknownFinale = Campaign();
            unknownFinale.finales[0].eventId = "sunny-island-car-unknown";
            AssertRejected(campaign: unknownFinale,
                because: "finale references an event that does not exist");
        }

        [Test]
        public void WrongClassMissingReferenceAndImpossibleThresholdReject()
        {
            // Wrong class: a car-class event whose allowed vehicles are all
            // motorcycles is not playable and must be refused.
            Assert.Throws<System.ArgumentException>(
                () => BuildWrongClassDriving(),
                "car event with only motorcycle vehicles");
            // (DrivingContentCatalog.Create throws; GameplayCatalog.TryCreate
            // must never see it — but a class-consistent driving catalog with
            // a mismatched *vehicleClass* field is also refused:)
            var mismatched = Driving();
            mismatched.Events["harbor-hop-car-circuit"].vehicleClass = "motorcycle";
            AssertRejected(driving: mismatched,
                because: "event vehicleClass contradicts its vehicle list");

            // Missing reference: campaign points at an event that is not in
            // the driving catalog.
            var missingRef = Campaign();
            missingRef.regularEventIds[0] = "harbor-hop-car-nope";
            AssertRejected(campaign: missingRef,
                because: "campaign references a nonexistent event");
            // ... or at a course the driving catalog does not carry.
            var missingCourse = Campaign();
            missingCourse.courseIds[0] = "no-such-course";
            AssertRejected(campaign: missingCourse,
                because: "campaign references a nonexistent course");

            // Impossible threshold: an introductory objective stricter than
            // its mastery objective can never represent the template.
            var impossibleIntro = Driving();
            var intro = impossibleIntro.Events["harbor-hop-car-boost"].objectives[0];
            intro.alternatives[0].threshold = 9; // mastery needs only 8
            AssertRejected(driving: impossibleIntro,
                because: "introductory objective harder than mastery");

            // Zero/negative thresholds are meaningless counters.
            var zeroThreshold = Driving();
            zeroThreshold.Events["harbor-hop-car-stunt"].objectives[1]
                .alternatives[0].threshold = 0;
            AssertRejected(driving: zeroThreshold,
                because: "non-positive objective threshold");

            // A bounty threshold that is impossible in one race is content,
            // not evaluation — but a nonpositive threshold is just broken.
            var badBounty = Bounties();
            badBounty[0].threshold = 0;
            AssertRejected(bounties: badBounty,
                because: "non-positive bounty threshold");
        }

        private static DrivingContentCatalog BuildWrongClassDriving()
        {
            var events = new List<EventDefinition>();
            foreach (var courseId in CourseIds)
                foreach (var classId in ClassIds)
                    foreach (var eventType in EventTypes)
                        events.Add(RegularEvent(courseId, classId, eventType));
            foreach (var classId in ClassIds)
                events.Add(Finale(classId, CourseIds[0]));
            // Corrupt one event: car event allowing only motorcycle vehicles.
            var corrupt = events.Find(e => e.id == "harbor-hop-car-circuit");
            corrupt.vehicleIds = new List<string> { "motorcycle-01" };
            return DrivingContentCatalog.Create(
                "gameplay-loop-1",
                new[]
                {
                    Vehicle("car-01"), Vehicle("car-02"),
                    Vehicle("motorcycle-01"), Vehicle("motorcycle-02")
                },
                new[] { Course("harbor-hop"), Course("lighthouse-loop") },
                events);
        }

        [Test]
        public void EveryGameplayValueChangesCompatibilityFingerprint()
        {
            var baseline = Catalog();
            var baselineFingerprint = baseline.CompatibilityFingerprint;
            Assert.That(Hex64.IsMatch(baselineFingerprint), Is.True);

            // The gameplay fingerprint is a superset of the driving one —
            // identical driving content still differs once gameplay rules
            // are bound.
            Assert.That(baselineFingerprint,
                Is.Not.EqualTo(baseline.Driving.CompatibilityFingerprint));

            void Changes(System.Action<GameplayCatalog> mutate, string what)
            {
                var mutated = Catalog();
                mutate(mutated);
                Assert.That(mutated.CompatibilityFingerprint,
                    Is.Not.EqualTo(baselineFingerprint), what);
            }

            // Campaign / gate values.
            Changes(c => c.Campaign.requiredRegionalStars = 31, "star gate");
            Changes(c => c.Campaign.requiredEventCompletions = 25, "completion gate");
            Changes(c => c.Campaign.nameEn = "Sunny Isle", "world name");
            Changes(c => c.Campaign.finales[0].rewardVehicleId = "car-01",
                "finale reward vehicle");

            // Per-event gameplay facts: objectives, par, type, finale flag.
            Changes(c =>
                c.Driving.Events["harbor-hop-car-boost"].objectives[1]
                    .alternatives[0].threshold = 7, "mastery boost threshold");
            Changes(c =>
                c.Driving.Events["harbor-hop-car-time"].parTicks = 3601,
                "par ticks");
            Changes(c =>
                c.Driving.Events["harbor-hop-car-stunt"].objectives[0].kind =
                    ObjectiveKind.Mastery, "objective kind");
            Changes(c =>
                c.Driving.Events["sunny-island-car-finale"].isFinale = false,
                "finale flag");

            // Bounty catalog.
            Changes(c => c.Bounties[0].threshold = 11, "drift threshold");
            Changes(c => c.Bounties[3].rewardCoins = 21, "bounty reward");
            Changes(c => c.Bounties[6].aggregation = BountyAggregation.Sum,
                "streak aggregation");

            // Economy.
            Changes(c => c.Economy.upgradeCategories[0].prices[0] = 61,
                "upgrade price");
            Changes(c => c.Economy.completionCoins = 21, "completion coins");
            Changes(c => c.Economy.placementBonusCoins[1] = 4,
                "placement bonus");
            Changes(c => c.Economy.upgradeCategories[1].levelMultiplierStep = 0.06f,
                "upgrade step");
            Changes(c => c.Economy.upgradeCategories[2].tuningFields[1] = "boostDurationSeconds",
                "upgrade tuning field");

            // Gadgets.
            Changes(c =>
            {
                foreach (var g in c.Gadgets.gadgets)
                    if (g.id == "turbo") g.durationTicks = 61;
            }, "turbo duration");
            Changes(c =>
            {
                foreach (var g in c.Gadgets.gadgets)
                    if (g.id == "foam-trap") g.slowMultiplier = 0.71f;
            }, "foam slow multiplier");
            Changes(c => c.Gadgets.pickupRespawnTicks = 301, "pickup respawn");
            Changes(c => c.Gadgets.heldSlotCapacity = 2, "held slot capacity");

            // Difficulty presets.
            Changes(c => c.Difficulties[0].brakeMarginScale = 1.11f,
                "difficulty brake margin");
            Changes(c => c.Difficulties[1].shortcutWillingness = 0.55f,
                "difficulty shortcut willingness");

            // Driving data remains bound through the same token.
            Changes(c =>
                c.Driving.Events["harbor-hop-car-circuit"].laps = 3,
                "event laps");
        }

        [Test]
        public void PickupSitesBindTheGameplayFingerprintOnly()
        {
            // Authored pickup placement seeds the authoritative gadget
            // world: two catalogs differing only in pickupSites must
            // never share an admission fingerprint — yet the epoch-1
            // driving fingerprint stays byte-identical because pickup
            // sites change no simulation outcome the driving body owns.
            var baseline = Catalog();
            var baselineGameplay = baseline.CompatibilityFingerprint;
            var baselineDriving = baseline.Driving.CompatibilityFingerprint;

            var sited = Catalog();
            sited.Driving.Courses["harbor-hop"].pickupSites.Add(
                new GadgetPickupSite { id = "site-a", x = 4f, z = 8f });
            Assert.That(sited.CompatibilityFingerprint,
                Is.Not.EqualTo(baselineGameplay),
                "an added pickup site is a gameplay-affecting delta");
            Assert.That(sited.Driving.CompatibilityFingerprint,
                Is.EqualTo(baselineDriving),
                "pickup sites never touch the epoch-1 driving fingerprint");

            var moved = Catalog();
            moved.Driving.Courses["harbor-hop"].pickupSites.Add(
                new GadgetPickupSite { id = "site-a", x = 4.5f, z = 8f });
            Assert.That(moved.CompatibilityFingerprint,
                Is.Not.EqualTo(sited.CompatibilityFingerprint),
                "a moved pickup site must be detected");

            // Canonical ordering: the same site set inserted in a
            // different order fingerprints identically.
            GadgetPickupSite Site(string id, float x)
            {
                return new GadgetPickupSite
                {
                    id = id, x = x, z = 8f,
                    radiusMeters = 2f, verticalRangeMeters = 2.5f
                };
            }
            var ordered = Catalog();
            ordered.Driving.Courses["harbor-hop"].pickupSites.Add(
                Site("site-a", 1f));
            ordered.Driving.Courses["harbor-hop"].pickupSites.Add(
                Site("site-b", 2f));
            var reordered = Catalog();
            reordered.Driving.Courses["harbor-hop"].pickupSites.Add(
                Site("site-b", 2f));
            reordered.Driving.Courses["harbor-hop"].pickupSites.Add(
                Site("site-a", 1f));
            Assert.That(reordered.CompatibilityFingerprint,
                Is.EqualTo(ordered.CompatibilityFingerprint),
                "site list order is presentation, not content");
        }

        [Test]
        public void UntypedLegacyEventCannotBeARegularCampaignEvent()
        {
            // An untyped event carries no objectives — as a regular event it
            // would inflate PossibleRegularStars with 3 unearnable stars.
            var untyped = Driving();
            var evt = untyped.Events["harbor-hop-car-circuit"];
            evt.eventType = "";
            evt.vehicleClass = "";
            evt.objectives = null;
            AssertRejected(driving: untyped,
                because: "untyped (legacy) event in regularEventIds");
        }

        [Test]
        public void EveryClassWithRegularEventsRequiresAFinale()
        {
            // The plan's world ships one rival finale per class; a class
            // whose finale is missing must not validate.
            var noMotoFinale = Campaign();
            noMotoFinale.finales.RemoveAt(1);
            AssertRejected(campaign: noMotoFinale,
                because: "motorcycle class has regular events but no finale");
        }

        [Test]
        public void LegacyCatalogLoadsThroughExplicitLegacyDefaults()
        {
            // A pre-M5 driving catalog (no eventType/objectives/par) must
            // still load via explicit legacy defaults — and stays
            // distinguishable from the gameplay-loop content identity.
            var legacyDriving = DrivingContentCatalog.Create(
                "physics-1",
                new[] { Vehicle("car-01") },
                new[] { Course("harbor-hop") },
                new[]
                {
                    new EventDefinition
                    {
                        id = "practice-02",
                        courseId = "harbor-hop",
                        laps = 1,
                        countdownTicks = 90,
                        vehicleIds = new List<string> { "car-01" },
                        recovery = new RecoverySettings()
                    }
                });
            var legacy = GameplayCatalog.CreateLegacy(legacyDriving);
            Assert.That(legacy, Is.Not.Null);
            Assert.That(legacy.IsLegacy, Is.True);
            Assert.That(Hex64.IsMatch(legacy.CompatibilityFingerprint), Is.True,
                "a legacy catalog still produces a stable token");
            Assert.That(legacy.CompatibilityFingerprint,
                Is.Not.EqualTo(Catalog().CompatibilityFingerprint),
                "M4 evidence identity stays distinguishable from gameplay-loop-1");
        }

        [Test]
        public void GameplayIdentityIsVersionThree()
        {
            // M5 Task 2 lands the first changed gameplay contract: peers must
            // agree on objectives/difficulty/economy/gadgets, not just the
            // M4 driving rules, so the gameplay identity is gameplay-3.
            Assert.That(
                FamilyRacing.Core.Networking.NetworkSettings.GameplayVersion,
                Is.EqualTo("gameplay-3"));
            Assert.That(Catalog().Version, Is.EqualTo("gameplay-loop-1"),
                "first-world content identity");
        }
    }
}
