using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // Shared first-world fixture for the Task-6 evaluator tests: the same
    // 2 courses x 2 classes x 6 event types + 2 rival finales catalog
    // shape GameplayCatalogTests pins, plus the named-rival one-time
    // bounty rows. Builders are internal so BountyTests reuses them —
    // this fixture is test content, never the authored shipping catalog.
    // M6 Task 2: a parallel three-world fixture (sunny/redrock/snowy,
    // six courses, eight vehicles, six rivals) lives beside it so world
    // progression, mode gating and reward tests share one shape.
    internal static class CampaignTestFixture
    {
        internal static readonly string[] CourseIds = { "harbor-hop", "lighthouse-loop" };
        internal static readonly string[] ClassIds = { "car", "motorcycle" };
        internal static readonly string[] EventTypes =
        {
            GameplayEventType.Circuit, GameplayEventType.Boost,
            GameplayEventType.Stunt, GameplayEventType.Time,
            GameplayEventType.Gadget, GameplayEventType.CleanDriving
        };
        internal const long ParTicks = 3600; // 120 s at 30 Hz
        internal const int TicksPerSecond = 30;

        // The authored world order and per-world course sets (M6 plan
        // inventory): every world owns two courses, 24 regular events
        // and two class finales paying the world's unlock pair.
        internal static readonly string[] WorldIds =
            { "sunny-island", "redrock-canyon", "snowy-peaks" };
        internal static readonly string[] RedrockCourseIds =
            { "canyon-dash", "mesa-skyway" };
        internal static readonly string[] SnowyCourseIds =
            { "snowpine-circuit", "aurora-pass" };

        internal static string RegularEventId(string courseId, string classId,
            string eventType)
        {
            return courseId + "-" + classId + "-" + eventType;
        }

        internal static string FinaleEventId(string classId)
        {
            return FinaleEventId("sunny-island", classId);
        }

        internal static string FinaleEventId(string worldId, string classId)
        {
            return worldId + "-" + classId + "-finale";
        }

        // The authored rival/livery/bounty naming convention for the
        // multiworld fixture: <worldId>-<classId>-rival with a one-time
        // bounty named after it.
        internal static string RivalId(string worldId, string classId)
        {
            return worldId + "-" + classId + "-rival";
        }

        internal static string RivalBountyId(string worldId, string classId)
        {
            return "bounty-" + RivalId(worldId, classId);
        }

        internal static string[] CoursesForWorld(string worldId)
        {
            if (worldId == "sunny-island") return CourseIds;
            if (worldId == "redrock-canyon") return RedrockCourseIds;
            if (worldId == "snowy-peaks") return SnowyCourseIds;
            return null;
        }

        // The world's completion unlock pair: sunny grants the -02
        // tier, redrock -03, snowy -04 (starters are -01).
        internal static int RewardOrdinalFor(string worldId)
        {
            if (worldId == "sunny-island") return 2;
            if (worldId == "redrock-canyon") return 3;
            if (worldId == "snowy-peaks") return 4;
            return 0;
        }

        internal static List<string> RegularEventsFor(string worldId)
        {
            var ids = new List<string>();
            var courses = CoursesForWorld(worldId);
            if (courses == null) return ids;
            foreach (var courseId in courses)
                foreach (var classId in ClassIds)
                    foreach (var eventType in EventTypes)
                        ids.Add(RegularEventId(courseId, classId,
                            eventType));
            return ids;
        }

        // Writes durable rows onto `profile`: the first `completedEvents`
        // regular events of the world completed, with `extraStars`
        // additional introductory/mastery bits spread two per event —
        // so the world's regional star count is completedEvents +
        // extraStars exactly.
        internal static void CompleteWorldEvents(ProfileSave profile,
            string worldId, int completedEvents, int extraStars)
        {
            var index = 0;
            var starsLeft = extraStars;
            foreach (var eventId in RegularEventsFor(worldId))
            {
                if (index >= completedEvents) break;
                var mask = ObjectiveEvaluator.CompletionBit;
                if (starsLeft >= 2)
                {
                    mask = ObjectiveEvaluator.FullMask;
                    starsLeft -= 2;
                }
                else if (starsLeft == 1)
                {
                    mask = ObjectiveEvaluator.CompletionBit
                        | ObjectiveEvaluator.IntroductoryBit;
                    starsLeft -= 1;
                }
                profile.eventProgress.Add(new EventProgress
                {
                    eventId = eventId, completed = true,
                    objectiveMask = mask
                });
                index++;
            }
        }

        // Durable finale rows for every class finale of the world —
        // `victorious` models the rival-defeat fact; world completion
        // is wins-optional.
        internal static void CompleteWorldFinales(ProfileSave profile,
            string worldId, bool victorious)
        {
            foreach (var classId in ClassIds)
                profile.finaleProgress.Add(new FinaleProgress
                {
                    eventId = FinaleEventId(worldId, classId),
                    completed = true, victorious = victorious
                });
        }

        // Objective templates per the plan's authored table: completion
        // is bit 0 (implicit), introductory bit 1, mastery bit 2.
        internal static List<ObjectiveDefinition> ObjectivesFor(
            string eventType, string eventId)
        {
            var intro = new ObjectiveCondition { threshold = 1 };
            var mastery = new ObjectiveCondition { threshold = 1 };
            switch (eventType)
            {
                case GameplayEventType.Circuit:
                    intro.metric = ObjectiveMetric.CleanLandings;
                    // OR legs: finish first OR both distinct shortcuts.
                    var m = new ObjectiveDefinition
                    {
                        id = eventId + "-mastery", kind = ObjectiveKind.Mastery
                    };
                    m.alternatives.Add(new ObjectiveCondition
                    {
                        metric = ObjectiveMetric.FinishPlace,
                        comparator = ObjectiveComparator.AtMost, threshold = 1
                    });
                    m.alternatives.Add(new ObjectiveCondition
                    {
                        metric = ObjectiveMetric.DistinctShortcuts, threshold = 2
                    });
                    return new List<ObjectiveDefinition>
                    {
                        new ObjectiveDefinition
                        {
                            id = eventId + "-intro",
                            kind = ObjectiveKind.Introductory,
                            alternatives = { intro }
                        },
                        m
                    };
                case GameplayEventType.Boost:
                    intro.metric = ObjectiveMetric.HumanBoostActivations;
                    intro.threshold = 3;
                    mastery.metric = ObjectiveMetric.HumanBoostActivations;
                    mastery.threshold = 8;
                    break;
                case GameplayEventType.Stunt:
                    intro.metric = ObjectiveMetric.CleanLandings;
                    intro.threshold = 2;
                    mastery.metric = ObjectiveMetric.CleanLandings;
                    mastery.threshold = 5;
                    break;
                case GameplayEventType.Time:
                    intro.metric = ObjectiveMetric.FinishWithinTicks;
                    intro.comparator = ObjectiveComparator.AtMost;
                    intro.threshold = (ParTicks + 1) / 2 + ParTicks; // ceil(1.5*par)
                    mastery.metric = ObjectiveMetric.FinishWithinTicks;
                    mastery.comparator = ObjectiveComparator.AtMost;
                    mastery.threshold = ParTicks;
                    break;
                case GameplayEventType.Gadget:
                    intro.metric = ObjectiveMetric.GadgetsCollectedAndUsed;
                    intro.threshold = 2;
                    mastery.metric = ObjectiveMetric.DistinctGadgetTypesUsed;
                    mastery.threshold = 3;
                    break;
                default: // CleanDriving
                    intro.metric = ObjectiveMetric.CleanStreakSeconds;
                    intro.threshold = 30;
                    mastery.metric = ObjectiveMetric.CleanStreakSeconds;
                    mastery.threshold = 60;
                    // AND inside one leg: the 60 s streak must come with
                    // zero recoveries.
                    mastery.alsoRequires = new List<ObjectiveCondition>
                    {
                        new ObjectiveCondition
                        {
                            metric = ObjectiveMetric.Recoveries,
                            comparator = ObjectiveComparator.AtMost,
                            threshold = 0
                        }
                    };
                    break;
            }
            var masteryDef = new ObjectiveDefinition
            {
                id = eventId + "-mastery", kind = ObjectiveKind.Mastery
            };
            masteryDef.alternatives.Add(mastery);
            return new List<ObjectiveDefinition>
            {
                new ObjectiveDefinition
                {
                    id = eventId + "-intro",
                    kind = ObjectiveKind.Introductory,
                    alternatives = { intro }
                },
                masteryDef
            };
        }

        private static FamilyRacing.Core.Driving.VehicleTuning Vehicle(string id)
        {
            var tuning = id.StartsWith("car")
                ? FamilyRacing.Core.Driving.VehicleTuning.Car01()
                : FamilyRacing.Core.Driving.VehicleTuning.Motorcycle01();
            tuning.vehicleId = id;
            return tuning;
        }

        private static CourseDefinition Course(string id)
        {
            var course = new CourseDefinition
            {
                id = id, spawnX = 0f, spawnY = 0f, spawnZ = -12f,
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
            // A four-slot start grid (Task 7 service-level tests drive
            // solo.start end-to-end through NetworkRace seat validation).
            course.spawnSlots.Add(new MotorPose { x = -5f, z = -57f });
            course.spawnSlots.Add(new MotorPose { x = 5f, z = -57f });
            course.spawnSlots.Add(new MotorPose { x = -5f, z = -62f });
            course.spawnSlots.Add(new MotorPose { x = 5f, z = -62f });
            return course;
        }

        private static EventDefinition RegularEvent(string courseId,
            string classId, string eventType)
        {
            var id = RegularEventId(courseId, classId, eventType);
            return new EventDefinition
            {
                id = id,
                courseId = courseId,
                laps = eventType == GameplayEventType.Time ? 1 : 2,
                countdownTicks = 90,
                vehicleIds = new List<string> { classId + "-01", classId + "-02" },
                recovery = new RecoverySettings(),
                completionCoins = 20,
                eventType = eventType,
                vehicleClass = classId,
                parTicks = eventType == GameplayEventType.Time ? ParTicks : 0,
                objectives = ObjectivesFor(eventType, id)
            };
        }

        private static DrivingContentCatalog Driving()
        {
            var events = new List<EventDefinition>();
            foreach (var courseId in CourseIds)
                foreach (var classId in ClassIds)
                    foreach (var eventType in EventTypes)
                        events.Add(RegularEvent(courseId, classId, eventType));
            foreach (var classId in ClassIds)
                events.Add(new EventDefinition
                {
                    id = FinaleEventId(classId),
                    courseId = CourseIds[0],
                    laps = 3,
                    countdownTicks = 90,
                    vehicleIds = new List<string>
                        { classId + "-01", classId + "-02" },
                    recovery = new RecoverySettings(),
                    completionCoins = 20,
                    eventType = GameplayEventType.Finale,
                    vehicleClass = classId,
                    isFinale = true
                });
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

        internal static CampaignDefinition Campaign()
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
                eventId = FinaleEventId("car"), classId = "car",
                rivalId = "rival-car", rewardVehicleId = "car-02"
            });
            campaign.finales.Add(new FinaleDefinition
            {
                eventId = FinaleEventId("motorcycle"), classId = "motorcycle",
                rivalId = "rival-motorcycle", rewardVehicleId = "motorcycle-02"
            });
            return campaign;
        }

        // The six repeatable families at their two authored thresholds,
        // plus the two one-time named-rival bounties that live OUTSIDE
        // the repeatable rotation cursor.
        internal static List<BountyDefinition> Bounties()
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
            // One-time rival victories: repeatable = false, rivalId set.
            list.Add(new BountyDefinition
            {
                id = "bounty-rival-car", family = "rival",
                metric = BountyMetric.HumanFinishes, threshold = 1,
                unit = "count", rewardCoins = 30,
                aggregation = BountyAggregation.Max,
                repeatable = false, rivalId = "rival-car"
            });
            list.Add(new BountyDefinition
            {
                id = "bounty-rival-motorcycle", family = "rival",
                metric = BountyMetric.HumanFinishes, threshold = 1,
                unit = "count", rewardCoins = 30,
                aggregation = BountyAggregation.Max,
                repeatable = false, rivalId = "rival-motorcycle"
            });
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
                cat.tuningFields.Add("acceleration");
                economy.upgradeCategories.Add(cat);
            }
            return economy;
        }

        private static GadgetDefinitionCatalog Gadgets()
        {
            var catalog = new GadgetDefinitionCatalog
            {
                pickupRespawnTicks = 300, heldSlotCapacity = 1
            };
            catalog.gadgets.Add(new GadgetDefinition
            {
                id = "turbo", kind = GadgetKind.Turbo, durationTicks = 60,
                accelerationMultiplier = 1.25f, speedCapMultiplier = 1.10f
            });
            catalog.gadgets.Add(new GadgetDefinition
            {
                id = "bubble-shield", kind = GadgetKind.BubbleShield, charges = 1
            });
            catalog.gadgets.Add(new GadgetDefinition
            {
                id = "foam-trap", kind = GadgetKind.FoamTrap,
                patchRadiusMeters = 3f, patchLifetimeTicks = 180,
                slowMultiplier = 0.70f, slowDurationTicks = 45,
                immunityTicks = 60
            });
            return catalog;
        }

        internal static GameplayCatalog Catalog()
        {
            var difficulties = new List<AiDifficultyDefinition>
            {
                new AiDifficultyDefinition { id = "easy" },
                new AiDifficultyDefinition { id = "medium" },
                new AiDifficultyDefinition { id = "hard" }
            };
            var ok = GameplayCatalog.TryCreate(
                Driving(), Campaign(), Bounties(), Economy(), Gadgets(),
                difficulties, out var catalog, out var error);
            Assert.That(ok, Is.True, "fixture catalog must validate: " + error);
            return catalog;
        }

        // --- the three-world fixture (M6 Task 2) -------------------------
        // Six courses / eight vehicles / 72 regular events / six rival
        // finales in authored order. Every regular event and finale seats
        // all four class tiers so Quick Race and LAN can prove access for
        // models the profile has not unlocked.

        private static readonly string[] VehicleTiers =
            { "-01", "-02", "-03", "-04" };

        private static CampaignDefinition MultiWorld(string worldId,
            string nameEn, string nameHe)
        {
            var courses = CoursesForWorld(worldId);
            var campaign = new CampaignDefinition
            {
                worldId = worldId,
                nameEn = nameEn,
                nameHe = nameHe,
                requiredEventCompletions =
                    courses.Length * ClassIds.Length * EventTypes.Length,
                requiredRegionalStars = 30
            };
            foreach (var courseId in courses)
                campaign.courseIds.Add(courseId);
            foreach (var eventId in RegularEventsFor(worldId))
                campaign.regularEventIds.Add(eventId);
            foreach (var classId in ClassIds)
                campaign.finales.Add(new FinaleDefinition
                {
                    eventId = FinaleEventId(worldId, classId),
                    classId = classId,
                    rivalId = RivalId(worldId, classId),
                    rewardVehicleId = classId + "-0"
                        + RewardOrdinalFor(worldId)
                });
            return campaign;
        }

        internal static List<CampaignDefinition> Worlds()
        {
            return new List<CampaignDefinition>
            {
                MultiWorld("sunny-island", "Sunny Island", "האי השמשי"),
                MultiWorld("redrock-canyon", "Redrock Canyon",
                    "קניון רדרוק"),
                MultiWorld("snowy-peaks", "Snowy Peaks", "הפסגות המושלגות")
            };
        }

        private static List<string> AllClassTiers(string classId)
        {
            var ids = new List<string>();
            foreach (var tier in VehicleTiers)
                ids.Add(classId + tier);
            return ids;
        }

        private static DrivingContentCatalog MultiDriving()
        {
            var courses = new List<CourseDefinition>();
            var events = new List<EventDefinition>();
            foreach (var worldId in WorldIds)
            {
                var worldCourses = CoursesForWorld(worldId);
                foreach (var courseId in worldCourses)
                {
                    courses.Add(Course(courseId));
                    foreach (var classId in ClassIds)
                        foreach (var eventType in EventTypes)
                        {
                            var evt = RegularEvent(courseId, classId,
                                eventType);
                            evt.vehicleIds = AllClassTiers(classId);
                            events.Add(evt);
                        }
                }
                foreach (var classId in ClassIds)
                    events.Add(new EventDefinition
                    {
                        id = FinaleEventId(worldId, classId),
                        courseId = worldCourses[0],
                        laps = 3,
                        countdownTicks = 90,
                        vehicleIds = AllClassTiers(classId),
                        recovery = new RecoverySettings(),
                        completionCoins = 20,
                        eventType = GameplayEventType.Finale,
                        vehicleClass = classId,
                        isFinale = true
                    });
            }
            return DrivingContentCatalog.Create(
                "full-content-1",
                new[]
                {
                    Vehicle("car-01"), Vehicle("car-02"),
                    Vehicle("car-03"), Vehicle("car-04"),
                    Vehicle("motorcycle-01"), Vehicle("motorcycle-02"),
                    Vehicle("motorcycle-03"), Vehicle("motorcycle-04")
                },
                courses, events);
        }

        // The twelve repeatable challenges plus one one-time bounty per
        // authored rival — six independent rival rewards.
        private static List<BountyDefinition> MultiBounties()
        {
            var list = Bounties();
            list.RemoveAll(b => b == null || !b.repeatable);
            foreach (var worldId in WorldIds)
                foreach (var classId in ClassIds)
                    list.Add(new BountyDefinition
                    {
                        id = RivalBountyId(worldId, classId),
                        family = "rival",
                        metric = BountyMetric.HumanFinishes,
                        threshold = 1,
                        unit = "count", rewardCoins = 30,
                        aggregation = BountyAggregation.Max,
                        repeatable = false,
                        rivalId = RivalId(worldId, classId)
                    });
            return list;
        }

        internal static GameplayCatalog MultiCatalog()
        {
            var difficulties = new List<AiDifficultyDefinition>
            {
                new AiDifficultyDefinition { id = "easy" },
                new AiDifficultyDefinition { id = "medium" },
                new AiDifficultyDefinition { id = "hard" }
            };
            var ok = GameplayCatalog.TryCreate(
                MultiDriving(), Worlds(), MultiBounties(), Economy(),
                Gadgets(), difficulties, out var catalog, out var error);
            Assert.That(ok, Is.True,
                "multiworld fixture catalog must validate: " + error);
            return catalog;
        }

        internal static ProfileSave Profile()
        {
            return ProfileFactory.Create(0, "en");
        }

        // A finalized format-2 result with one human row carrying the
        // supplied metrics — the shape RaceResultFactory.Bind produces.
        internal static RaceResult Result(string mode, string eventId,
            string participantId, string outcome, int position,
            RaceMetrics metrics, string attribution = "human",
            string raceId = "race-t1")
        {
            var result = new RaceResult
            {
                formatVersion = 2,
                resultId = raceId + "/result",
                sessionId = "solo:" + raceId,
                raceId = raceId,
                eventId = eventId,
                contentVersion = "gameplay-loop-1",
                mode = mode,
                rulesFingerprint = new string('b', 64),
                finalized = true,
                finalizedHostTick = 300,
                finalizedElapsedTicks = 4000,
                completionCoins = 20
            };
            var finished = outcome == "finished";
            metrics = metrics != null ? metrics : new RaceMetrics();
            metrics.participantId = participantId;
            metrics.attribution = attribution;
            metrics.position = position;
            metrics.outcome = outcome;
            if (metrics.finishTicks == 0 && finished) metrics.finishTicks = 3800;
            metrics.elapsedTicks = 4000;
            result.racers.Add(new RaceResultEntry
            {
                position = position,
                slot = 0,
                participantId = participantId,
                vehicleId = "car-01",
                attribution = attribution,
                outcome = outcome,
                finishTicks = finished ? metrics.finishTicks : 0,
                lapsCompleted = finished ? 2 : 0,
                coinsAwarded = finished ? 20 : 0,
                metrics = metrics
            });
            return result;
        }

        // Field-exact snapshot for the "inputs unmutated" assertions:
        // covers every schema-3 progression collection plus the fields
        // the evaluators read.
        internal static string ProfileSnapshot(ProfileSave p)
        {
            var b = new System.Text.StringBuilder(512);
            b.Append(p.id).Append('|').Append(p.coins).Append('|')
                .Append(p.bountyRotationCursor).Append('|')
                .Append(p.localRaceOrdinal).Append('|');
            if (p.unlockedVehicles != null)
                b.Append(string.Join(",", p.unlockedVehicles));
            b.Append('#');
            if (p.eventProgress != null)
                foreach (var r in p.eventProgress)
                    b.Append(r == null ? "n;" : r.eventId + ":" + r.completed
                        + ":" + r.objectiveMask + ";");
            b.Append('#');
            if (p.worldProgress != null)
                foreach (var r in p.worldProgress)
                    b.Append(r == null ? "n;" : r.worldId + ":" + r.completed + ";");
            b.Append('#');
            if (p.finaleProgress != null)
                foreach (var r in p.finaleProgress)
                    b.Append(r == null ? "n;" : r.eventId + ":" + r.completed
                        + ":" + r.victorious + ";");
            b.Append('#');
            if (p.activeBounties != null)
                foreach (var r in p.activeBounties)
                    b.Append(r == null ? "n;" : r.assignmentId + ":"
                        + r.bountyId + ":" + r.progress + ";");
            b.Append('#');
            if (p.ownedCosmetics != null)
                b.Append(string.Join(",", p.ownedCosmetics));
            b.Append('#');
            if (p.pendingRaceContexts != null)
                foreach (var r in p.pendingRaceContexts)
                    b.Append(r == null ? "n;" : r.raceId + ":" + r.mode + ";");
            return b.ToString();
        }
    }

    // M5 Task 6: objective/star evaluation reads only finalized facts.
    // Objective bits (completion + introductory + mastery) union across
    // replays; regional stars are the sum of unique per-event bits; the
    // finale gate needs every completion AND the authored star count.
    public sealed class CampaignObjectiveTests
    {
        private const string P = "profile-p";

        private static EventDefinition Event(GameplayCatalog catalog,
            string eventType, string classId = "car")
        {
            return catalog.Driving.Events[CampaignTestFixture.RegularEventId(
                "harbor-hop", classId, eventType)];
        }

        private static bool Eval(ObjectiveDefinition definition,
            RaceResult result)
        {
            return ObjectiveEvaluator.Evaluate(definition, result, P);
        }

        // --- the six event-objective templates --------------------------

        [Test]
        public void CircuitTemplateEvaluatesLandingsPlaceAndShortcuts()
        {
            var catalog = CampaignTestFixture.Catalog();
            var evt = Event(catalog, GameplayEventType.Circuit);
            var intro = evt.objectives[0];
            var mastery = evt.objectives[1];

            // 2nd place, one clean landing, only one distinct shortcut:
            // introductory met, mastery not.
            var result = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 2, new RaceMetrics
                {
                    cleanLandings = 1, humanDistinctShortcuts = 1
                });
            Assert.That(Eval(intro, result), Is.True);
            Assert.That(Eval(mastery, result), Is.False);

            // Finish first OR both distinct shortcuts: either leg pays.
            var won = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 1, new RaceMetrics { cleanLandings = 1 });
            Assert.That(Eval(mastery, won), Is.True,
                "finish-first leg is an OR alternative");
            var shortcuts = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 3, new RaceMetrics
                {
                    cleanLandings = 0, humanDistinctShortcuts = 2
                });
            Assert.That(Eval(mastery, shortcuts), Is.True,
                "both distinct shortcuts satisfy mastery without winning");
            Assert.That(Eval(intro, shortcuts), Is.False);
        }

        [Test]
        public void BoostTemplateCountsHumanActivations()
        {
            var catalog = CampaignTestFixture.Catalog();
            var evt = Event(catalog, GameplayEventType.Boost);
            var low = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 2, new RaceMetrics { boostActivations = 3 });
            Assert.That(Eval(evt.objectives[0], low), Is.True);
            Assert.That(Eval(evt.objectives[1], low), Is.False);
            var high = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 1, new RaceMetrics { boostActivations = 8 });
            Assert.That(Eval(evt.objectives[1], high), Is.True);
        }

        [Test]
        public void StuntTemplateCountsCleanLandings()
        {
            var catalog = CampaignTestFixture.Catalog();
            var evt = Event(catalog, GameplayEventType.Stunt);
            var two = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 2, new RaceMetrics { cleanLandings = 2 });
            Assert.That(Eval(evt.objectives[0], two), Is.True);
            Assert.That(Eval(evt.objectives[1], two), Is.False);
            var five = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 1, new RaceMetrics { cleanLandings = 5 });
            Assert.That(Eval(evt.objectives[1], five), Is.True);
        }

        [Test]
        public void TimeTemplateBoundsFinishTicksInclusively()
        {
            var catalog = CampaignTestFixture.Catalog();
            var evt = Event(catalog, GameplayEventType.Time);
            var par = CampaignTestFixture.ParTicks;
            var introLimit = (par + 1) / 2 + par; // ceil(1.5 * par) = 5400

            // Equality on both authored boundaries earns the star.
            var atPar = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 1, new RaceMetrics { finishTicks = par });
            Assert.That(Eval(evt.objectives[0], atPar), Is.True);
            Assert.That(Eval(evt.objectives[1], atPar), Is.True,
                "finishTicks == parTicks meets the mastery bound");

            var overPar = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 1, new RaceMetrics { finishTicks = par + 1 });
            Assert.That(Eval(evt.objectives[0], overPar), Is.True);
            Assert.That(Eval(evt.objectives[1], overPar), Is.False);

            var atIntro = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 1, new RaceMetrics { finishTicks = introLimit });
            Assert.That(Eval(evt.objectives[0], atIntro), Is.True,
                "finishTicks == ceil(1.5*par) meets the introductory bound");
            var overIntro = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 1, new RaceMetrics { finishTicks = introLimit + 1 });
            Assert.That(Eval(evt.objectives[0], overIntro), Is.False);
        }

        [Test]
        public void GadgetTemplateCountsUsesAndDistinctTypes()
        {
            var catalog = CampaignTestFixture.Catalog();
            var evt = Event(catalog, GameplayEventType.Gadget);
            var two = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 2, new RaceMetrics
                {
                    gadgetUses = 2, distinctGadgetTypes = 2
                });
            Assert.That(Eval(evt.objectives[0], two), Is.True);
            Assert.That(Eval(evt.objectives[1], two), Is.False);
            var all = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 1, new RaceMetrics
                {
                    gadgetUses = 4, distinctGadgetTypes = 3
                });
            Assert.That(Eval(evt.objectives[1], all), Is.True);
        }

        [Test]
        public void CleanDrivingTemplateReadsStreakSecondsAndRecoveries()
        {
            var catalog = CampaignTestFixture.Catalog();
            var evt = Event(catalog, GameplayEventType.CleanDriving);
            var tps = CampaignTestFixture.TicksPerSecond;

            // 30 s streak -> introductory only; 60 s requires zero
            // recoveries inside the same leg (AND).
            var intro = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 1, new RaceMetrics
                {
                    longestCleanStreakTicks = 30 * tps, recoveries = 0
                });
            Assert.That(Eval(evt.objectives[0], intro), Is.True);
            Assert.That(Eval(evt.objectives[1], intro), Is.False);

            var justUnder = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 1, new RaceMetrics
                {
                    longestCleanStreakTicks = 30 * tps - 1, recoveries = 0
                });
            Assert.That(Eval(evt.objectives[0], justUnder), Is.False,
                "899 ticks is 29.9s — the 30s streak boundary is exact");

            var masteryButRecovery = CampaignTestFixture.Result(
                "campaign", evt.id, P, "finished", 1, new RaceMetrics
                {
                    longestCleanStreakTicks = 60 * tps, recoveries = 1
                });
            Assert.That(Eval(evt.objectives[1], masteryButRecovery), Is.False,
                "the zero-recoveries conjunct fails the whole leg");

            var mastery = CampaignTestFixture.Result(
                "campaign", evt.id, P, "finished", 1, new RaceMetrics
                {
                    longestCleanStreakTicks = 60 * tps, recoveries = 0
                });
            Assert.That(Eval(evt.objectives[1], mastery), Is.True);
        }

        // --- mask union / eligibility -------------------------------------

        [Test]
        public void ObjectiveMaskUnionsAcrossReplays()
        {
            var catalog = CampaignTestFixture.Catalog();
            var evt = Event(catalog, GameplayEventType.Boost);
            // First replay: finish + introductory only.
            var first = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 2, new RaceMetrics { boostActivations = 3 });
            var mask1 = ObjectiveEvaluator.EvaluateMask(evt, first, P);
            Assert.That(mask1, Is.EqualTo(0b011),
                "completion + introductory bits");
            // Second replay: mastery too; union keeps the earned bits.
            var second = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 1, new RaceMetrics { boostActivations = 9 });
            var mask2 = ObjectiveEvaluator.EvaluateMask(evt, second, P);
            Assert.That(mask2, Is.EqualTo(0b111));
            var merged = ObjectiveEvaluator.MergeMask(mask1, mask2);
            Assert.That(merged, Is.EqualTo(0b111));
            // Replaying a completed three-star event can never add bits:
            // the union of equal masks is the same mask.
            var third = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 1, new RaceMetrics { boostActivations = 9 });
            Assert.That(ObjectiveEvaluator.MergeMask(merged,
                ObjectiveEvaluator.EvaluateMask(evt, third, P)),
                Is.EqualTo(0b111));
        }

        [Test]
        public void DnfAbortAndForeignSeatsGrantNoBits()
        {
            var catalog = CampaignTestFixture.Catalog();
            var evt = Event(catalog, GameplayEventType.Boost);
            var dnf = CampaignTestFixture.Result("campaign", evt.id, P,
                "dnf", 4, new RaceMetrics { boostActivations = 9 });
            Assert.That(ObjectiveEvaluator.EvaluateMask(evt, dnf, P),
                Is.EqualTo(0), "DNF grants no objective bits");
            var unfinalized = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 1, new RaceMetrics { boostActivations = 9 });
            unfinalized.finalized = false;
            Assert.That(ObjectiveEvaluator.EvaluateMask(evt, unfinalized, P),
                Is.EqualTo(0), "an unfinalized result awards nothing");
            // A seat that finished under AI attribution earns no stars.
            var ai = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 1, new RaceMetrics { boostActivations = 9 },
                attribution: "ai");
            Assert.That(ObjectiveEvaluator.EvaluateMask(evt, ai, P),
                Is.EqualTo(0), "AI-attributed finishes earn no campaign stars");
            // A result for a different event never satisfies this event.
            var other = CampaignTestFixture.Result("campaign",
                Event(catalog, GameplayEventType.Stunt).id, P,
                "finished", 1, new RaceMetrics { boostActivations = 9 });
            Assert.That(ObjectiveEvaluator.EvaluateMask(evt, other, P),
                Is.EqualTo(0));
            // Campaign stars are solo: the same event under quick race
            // earns no mask at all.
            var quick = CampaignTestFixture.Result("quickRace", evt.id, P,
                "finished", 1, new RaceMetrics { boostActivations = 9 });
            Assert.That(ObjectiveEvaluator.EvaluateMask(evt, quick, P),
                Is.EqualTo(0));
        }

        [Test]
        public void CleanStreakFactsResetAndSkipNonHumanCredit()
        {
            // The accumulator already encodes the streak rules; pin the
            // facts the evaluator consumes: an impact/recovery boundary
            // does not grow the streak, a recovering boundary counts no
            // clean tick, and AI-attributed shortcuts earn no human
            // shortcut credit while still recording shared counters.
            var metrics = new RaceMetricsAccumulator();
            var state = new DrivingSessionState();
            state.motor = new FamilyRacing.Core.Driving.MotorState();
            state.motor.grounded = true;
            state.motor.speed = 10f;
            for (long t = 1; t <= 5; t++)
                metrics.RecordBoundary(t, P, "human", true, state, null);
            // Recovery boundary: no clean tick, breaks the streak.
            var recovering = new DrivingSessionState();
            recovering.motor = new FamilyRacing.Core.Driving.MotorState();
            recovering.motor.grounded = true;
            recovering.motor.speed = 10f;
            recovering.motor.recovering = true;
            metrics.RecordBoundary(6, P, "human", true, recovering,
                new List<DrivingEvent>
                {
                    new DrivingEvent { kind = "race.recovered", attribution = "human" }
                });
            for (long t = 7; t <= 9; t++)
                metrics.RecordBoundary(t, P, "human", true, state, null);
            // An AI-attributed shortcut: shared fact, no human credit.
            metrics.RecordBoundary(10, P, "ai", true, state,
                new List<DrivingEvent>
                {
                    new DrivingEvent
                    {
                        kind = "shortcut.accepted", detail = "cut-1",
                        attribution = "ai"
                    }
                });
            var row = metrics.Snapshot()[0];
            Assert.That(row.longestCleanStreakTicks, Is.EqualTo(5),
                "recovery boundary must not grow the streak");
            Assert.That(row.recoveries, Is.EqualTo(1));
            Assert.That(row.acceptedShortcuts, Is.EqualTo(1),
                "shared counters still record the traversal");
            Assert.That(row.humanAcceptedShortcuts, Is.EqualTo(0),
                "AI-attributed segments earn no human shortcut credit");
            Assert.That(row.humanDistinctShortcuts, Is.EqualTo(0));
        }

        // --- finale gate / world completion --------------------------------

        private static ProfileSave ProfileWithProgress(
            GameplayCatalog catalog, int completedEvents, int extraStars,
            bool carFinaleDone = false, bool carFinaleWon = false,
            bool motoFinaleDone = false, bool motoFinaleWon = false)
        {
            var profile = CampaignTestFixture.Profile();
            var index = 0;
            var starsLeft = extraStars;
            foreach (var eventId in catalog.Campaign.regularEventIds)
            {
                if (index >= completedEvents) break;
                var mask = 1; // completion bit
                // Spend extra stars on top of completions, two per event
                // at most (introductory + mastery bits).
                if (starsLeft >= 2) { mask = 0b111; starsLeft -= 2; }
                else if (starsLeft == 1) { mask = 0b011; starsLeft -= 1; }
                profile.eventProgress.Add(new EventProgress
                {
                    eventId = eventId, completed = true, objectiveMask = mask
                });
                index++;
            }
            profile.finaleProgress.Add(new FinaleProgress
            {
                eventId = CampaignTestFixture.FinaleEventId("car"),
                completed = carFinaleDone, victorious = carFinaleWon
            });
            profile.finaleProgress.Add(new FinaleProgress
            {
                eventId = CampaignTestFixture.FinaleEventId("motorcycle"),
                completed = motoFinaleDone, victorious = motoFinaleWon
            });
            return profile;
        }

        [Test]
        public void TwentyFourCompletionsAndSixNonVictoryObjectivesUnlockFinales()
        {
            var catalog = CampaignTestFixture.Catalog();
            // 24 completions + six extra (non-victory) objective bits =
            // exactly 30 regional stars — the authored gate.
            var profile = ProfileWithProgress(catalog, 24, 6);
            var access = CampaignRules.Evaluate(catalog.Campaign, profile);

            Assert.That(access.completedEvents, Is.EqualTo(24));
            Assert.That(access.regionalStars, Is.EqualTo(30));
            Assert.That(access.finalesAvailable, Is.True);
            Assert.That(access.availableFinaleIds,
                Is.EquivalentTo(new[]
                {
                    CampaignTestFixture.FinaleEventId("car"),
                    CampaignTestFixture.FinaleEventId("motorcycle")
                }));
            Assert.That(access.availableEventIds,
                Is.SupersetOf(catalog.Campaign.regularEventIds));
            Assert.That(access.availableEventIds,
                Does.Contain(CampaignTestFixture.FinaleEventId("car")));
            Assert.That(access.worldComplete, Is.False,
                "no finale has been completed yet");
        }

        [Test]
        public void FinaleGateBoundariesStayLocked()
        {
            var catalog = CampaignTestFixture.Catalog();
            // 23 completions + 30 stars: the star count alone never opens
            // the gate — every regular event must be completed.
            var almost = ProfileWithProgress(catalog, 23, 7);
            var access = CampaignRules.Evaluate(catalog.Campaign, almost);
            Assert.That(access.completedEvents, Is.EqualTo(23));
            Assert.That(access.regionalStars, Is.EqualTo(30));
            Assert.That(access.finalesAvailable, Is.False,
                "23 completions + 30 stars stays locked");
            Assert.That(access.availableFinaleIds, Is.Empty);

            // 24 completions + 29 stars: one star short.
            var shortStars = ProfileWithProgress(catalog, 24, 5);
            access = CampaignRules.Evaluate(catalog.Campaign, shortStars);
            Assert.That(access.regionalStars, Is.EqualTo(29));
            Assert.That(access.finalesAvailable, Is.False,
                "24 completions + 29 stars stays locked");
        }

        [Test]
        public void ReplayingThreeStarEventKeepsThreeUniqueStars()
        {
            var catalog = CampaignTestFixture.Catalog();
            var profile = ProfileWithProgress(catalog, 24, 6);
            var before = CampaignRules.Evaluate(catalog.Campaign, profile);

            // Replaying an already three-starred event earns the same
            // mask; the union can never mint a fourth bit.
            var evtId = catalog.Campaign.regularEventIds[0];
            var evt = catalog.Driving.Events[evtId];
            var replay = CampaignTestFixture.Result("campaign", evtId, P,
                "finished", 1, new RaceMetrics
                {
                    cleanLandings = 9, humanDistinctShortcuts = 2
                });
            var earned = ObjectiveEvaluator.EvaluateMask(evt, replay, P);
            var row = profile.eventProgress[0];
            row.objectiveMask = ObjectiveEvaluator.MergeMask(
                row.objectiveMask, earned);
            var after = CampaignRules.Evaluate(catalog.Campaign, profile);

            Assert.That(row.objectiveMask, Is.EqualTo(0b111),
                "a replayed three-star event still holds exactly 3 stars");
            Assert.That(after.regionalStars,
                Is.EqualTo(before.regionalStars));
            Assert.That(after.finalesAvailable,
                Is.EqualTo(before.finalesAvailable));
        }

        [Test]
        public void LosingBothFinalesStillCompletesWorld()
        {
            var catalog = CampaignTestFixture.Catalog();
            // Both finales completed, neither won: the world advances
            // regardless of victory.
            var profile = ProfileWithProgress(catalog, 24, 6,
                carFinaleDone: true, carFinaleWon: false,
                motoFinaleDone: true, motoFinaleWon: false);
            var access = CampaignRules.Evaluate(catalog.Campaign, profile);
            Assert.That(access.worldComplete, Is.True,
                "completing both finales completes the world even on losses");
            Assert.That(access.worldRewardVehicleIds,
                Is.EquivalentTo(new[] { "car-02", "motorcycle-02" }));
            Assert.That(access.victoriousFinaleIds, Is.Empty);

            // One finale short of world completion.
            var half = ProfileWithProgress(catalog, 24, 6,
                carFinaleDone: true, motoFinaleDone: false);
            Assert.That(CampaignRules.Evaluate(catalog.Campaign, half)
                .worldComplete, Is.False);
        }

        [Test]
        public void EvaluationIsDeterministicAndLeavesInputsUntouched()
        {
            var catalog = CampaignTestFixture.Catalog();
            var evt = Event(catalog, GameplayEventType.Circuit);
            var profile = ProfileWithProgress(catalog, 24, 6);
            var result = CampaignTestFixture.Result("campaign", evt.id, P,
                "finished", 2, new RaceMetrics
                {
                    cleanLandings = 1, humanDistinctShortcuts = 2
                });
            var profileBefore = CampaignTestFixture.ProfileSnapshot(profile);
            var resultFingerprint = result.Fingerprint();

            var a = CampaignRules.Evaluate(catalog.Campaign, profile);
            var b = CampaignRules.Evaluate(catalog.Campaign, profile);
            Assert.That(a.regionalStars, Is.EqualTo(b.regionalStars));
            Assert.That(a.availableEventIds, Is.EqualTo(b.availableEventIds));
            Assert.That(a.finalesAvailable, Is.EqualTo(b.finalesAvailable));
            Assert.That(ObjectiveEvaluator.EvaluateMask(evt, result, P),
                Is.EqualTo(ObjectiveEvaluator.EvaluateMask(evt, result, P)));

            Assert.That(CampaignTestFixture.ProfileSnapshot(profile),
                Is.EqualTo(profileBefore),
                "evaluation must not mutate the source profile");
            Assert.That(result.Fingerprint(), Is.EqualTo(resultFingerprint),
                "evaluation must not mutate the source result");
        }
    }
}
