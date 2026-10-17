using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Localization;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FamilyRacing.Tests
{
    // M5 Task 10 screen proof: the loop screens are driven by real
    // pointer events through the same FoundationService.Execute path an
    // agent uses. The sandbox loop buttons dispatch the gated *.open
    // commands; the campaign map renders authored rows, honest lock
    // labels and LTR-starred counters in both languages; the quick-race
    // and time-trial pickers walk course→class→vehicle and dispatch the
    // real *.start commands; the bounty board lists the three resolved
    // assignments; and the driving results overlay carries one-action
    // replay/continue through results.*.
    [UnityPlatform(RuntimePlatform.WindowsEditor, RuntimePlatform.OSXEditor,
        RuntimePlatform.LinuxEditor)]
    public sealed class GameplayScreensPlayTests
    {
        private const string RunId = "gameplay-screens-play";
        private const string PanelSettingsPath =
            "Assets/FamilyRacing/Generated/FoundationPanelSettings.asset";

        private string directory;
        private ProfileStore store;
        private FoundationService service;
        private GameObject hostGo;
        private FoundationApp app;
        private GameObject worldGo;
        private DrivingWorld world;
        private GameObject courseSource;
        private GameObject shellGo;
        private UIDocument document;
        private PanelSettings panelSettings;
        private FoundationShell shell;
        private readonly List<Object> assets = new List<Object>();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "fr-loop-ui-" + Guid.NewGuid().ToString("N"));
            store = new ProfileStore(directory, new UnityProfileSerializer());
            service = new FoundationService(store, "en", RunId);

            hostGo = new GameObject("loop-host");
            hostGo.SetActive(false);
            app = hostGo.AddComponent<FoundationApp>();
            Inject(app, "service", service);
            Inject(app, "runId", RunId);

            courseSource = BuildCourseSource();
            worldGo = new GameObject("loop-world");
            worldGo.SetActive(false);
            world = worldGo.AddComponent<DrivingWorld>();
            WireWorld();
            worldGo.SetActive(true);
            Inject(app, "drivingWorld", world);
            var catalog = world.Load();
            Assert.That(catalog, Is.Not.Null,
                "generated definitions must validate");
            service.SetDrivingContent(catalog);
            service.SetGameplayCatalog(Gameplay(catalog));
            service.SetDrivingSimulationFactory(world.CreateSimulation);

#if UNITY_EDITOR
            panelSettings = Object.Instantiate(
                UnityEditor.AssetDatabase.LoadAssetAtPath<PanelSettings>(
                    PanelSettingsPath));
#endif
            Assert.That(panelSettings, Is.Not.Null,
                "The generated PanelSettings asset must load.");

            shellGo = new GameObject("loop-shell");
            shellGo.SetActive(false);
            document = shellGo.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            shell = shellGo.AddComponent<FoundationShell>();
            shellGo.SetActive(true);
            shell.Bind(app);
            yield return WaitFor(() => document.rootVisualElement != null
                && document.rootVisualElement.panel != null, "panel attach");
        }

        [TearDown]
        public void TearDown()
        {
            if (shellGo != null) Object.Destroy(shellGo);
            if (worldGo != null) Object.Destroy(worldGo);
            if (courseSource != null) Object.Destroy(courseSource);
            if (hostGo != null) Object.Destroy(hostGo);
            if (panelSettings != null) Object.DestroyImmediate(panelSettings);
            foreach (var asset in assets)
                if (asset != null) Object.Destroy(asset);
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch (Exception) { }
        }

        // --- fixture -----------------------------------------------------

        private static void Inject(object target, string field, object value)
        {
            target.GetType().GetField(field,
                BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(target, value);
        }

        private T Asset<T>(Action<T> configure) where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>();
            configure(asset);
            assets.Add(asset);
            return asset;
        }

        private static GameObject BuildCourseSource()
        {
            var root = new GameObject("loop-course");
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.SetParent(root.transform, false);
            ground.transform.localPosition = new Vector3(0f, -0.5f, 0f);
            ground.transform.localScale = new Vector3(240f, 1f, 160f);
            root.SetActive(false);
            return root;
        }

        private static TrackDefinitionAsset.Gate Gate(string id, float x,
            float z, float dirX, float dirZ)
        {
            return new TrackDefinitionAsset.Gate
            {
                id = id,
                center = new Vector3(x, 0f, z),
                halfWidth = 12f,
                direction = new Vector2(dirX, dirZ),
                recoverPosition = new Vector3(x - dirX * 3f, 0f,
                    z - dirZ * 3f),
                recoverHeadingDegrees =
                    Mathf.Atan2(dirX, dirZ) * Mathf.Rad2Deg
            };
        }

        private static TrackDefinitionAsset.RoutePoint Route(float x,
            float z)
        {
            return new TrackDefinitionAsset.RoutePoint
            {
                x = x, z = z, radius = 7f
            };
        }

        private static EventDefinitionAsset.ObjectiveRow Objective(
            string id, string kind, string metric, long threshold)
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
                        comparator = "atLeast",
                        threshold = threshold
                    }
                }
            };
        }

        // Two regular circuits (car + motorcycle), one time event and
        // both class finales — the smallest world the gameplay catalog
        // validates that still exercises every loop screen.
        private void WireWorld()
        {
            var car1 = Asset<VehicleDefinitionAsset>(a =>
            {
                a.vehicleId = "car-01";
                a.bodyScale = new Vector3(1.8f, 1.1f, 3.2f);
                a.bodyColor = Color.red;
            });
            var car2 = Asset<VehicleDefinitionAsset>(a =>
            {
                a.vehicleId = "car-02";
                a.bodyScale = new Vector3(1.8f, 1.1f, 3.2f);
                a.bodyColor = Color.blue;
            });
            var moto1 = Asset<VehicleDefinitionAsset>(a =>
            {
                a.vehicleId = "motorcycle-01";
                a.silhouette = "motorcycle";
                a.bodyScale = new Vector3(0.9f, 1.2f, 2.4f);
                a.bodyColor = Color.green;
            });
            var track = Asset<TrackDefinitionAsset>(a =>
            {
                a.courseId = "harbor-hop";
                a.spawnPosition = new Vector3(0f, 0f, -60f);
                a.spawnHeadingDegrees = 0f;
                a.gates = new[]
                {
                    Gate("start", 0f, -60f, 0f, 1f),
                    Gate("gate-1", 0f, 25f, 0f, 1f),
                    Gate("gate-2", 55f, 55f, 1f, 0f),
                    Gate("gate-3", 95f, 0f, 0f, -1f),
                    Gate("gate-4", 50f, -55f, -1f, 0f)
                };
                a.spawnSlots = new[]
                {
                    new TrackDefinitionAsset.SpawnSlot
                    {
                        position = new Vector3(-5f, 0f, -57f),
                        headingDegrees = 0f
                    },
                    new TrackDefinitionAsset.SpawnSlot
                    {
                        position = new Vector3(5f, 0f, -57f),
                        headingDegrees = 0f
                    },
                    new TrackDefinitionAsset.SpawnSlot
                    {
                        position = new Vector3(-5f, 0f, -62f),
                        headingDegrees = 0f
                    },
                    new TrackDefinitionAsset.SpawnSlot
                    {
                        position = new Vector3(5f, 0f, -62f),
                        headingDegrees = 0f
                    }
                };
                a.aiRoute = new[]
                {
                    Route(0f, 33f), Route(63f, 55f), Route(95f, -8f),
                    Route(44f, -55f), Route(0f, -70f), Route(0f, -50f)
                };
            });
            var circuit = Asset<EventDefinitionAsset>(a =>
            {
                a.eventId = "harbor-car-circuit";
                a.track = track;
                a.laps = 1;
                a.countdownTicks = 3;
                a.vehicleIds = new[] { "car-01", "car-02" };
                a.completionCoins = 20;
                a.eventType = "circuit";
                a.vehicleClass = "car";
                a.objectives = new[]
                {
                    Objective("intro", "introductory", "cleanLandings", 1),
                    Objective("mastery", "mastery", "cleanLandings", 3)
                };
            });
            var motoCircuit = Asset<EventDefinitionAsset>(a =>
            {
                a.eventId = "harbor-moto-circuit";
                a.track = track;
                a.laps = 1;
                a.countdownTicks = 3;
                a.vehicleIds = new[] { "motorcycle-01" };
                a.completionCoins = 20;
                a.eventType = "circuit";
                a.vehicleClass = "motorcycle";
                a.objectives = new[]
                {
                    Objective("intro", "introductory", "cleanLandings", 1),
                    Objective("mastery", "mastery", "cleanLandings", 3)
                };
            });
            var time = Asset<EventDefinitionAsset>(a =>
            {
                a.eventId = "harbor-car-time";
                a.track = track;
                a.laps = 1;
                a.countdownTicks = 3;
                a.vehicleIds = new[] { "car-01", "car-02" };
                a.eventType = "time";
                a.vehicleClass = "car";
                a.parTicks = 3600;
                a.objectives = new[]
                {
                    Objective("intro", "introductory", "cleanLandings", 1),
                    Objective("mastery", "mastery", "cleanLandings", 3)
                };
            });
            var finale = Asset<EventDefinitionAsset>(a =>
            {
                a.eventId = "sunny-car-finale";
                a.track = track;
                a.laps = 1;
                a.countdownTicks = 3;
                a.vehicleIds = new[] { "car-01", "car-02" };
                a.eventType = "finale";
                a.vehicleClass = "car";
                a.isFinale = true;
                a.objectives = new[]
                {
                    Objective("intro", "introductory", "cleanLandings", 1),
                    Objective("mastery", "mastery", "cleanLandings", 3)
                };
            });
            var motoFinale = Asset<EventDefinitionAsset>(a =>
            {
                a.eventId = "sunny-moto-finale";
                a.track = track;
                a.laps = 1;
                a.countdownTicks = 3;
                a.vehicleIds = new[] { "motorcycle-01" };
                a.eventType = "finale";
                a.vehicleClass = "motorcycle";
                a.isFinale = true;
                a.objectives = new[]
                {
                    Objective("intro", "introductory", "cleanLandings", 1),
                    Objective("mastery", "mastery", "cleanLandings", 3)
                };
            });
            Inject(world, "vehicles", new[] { car1, car2, moto1 });
            Inject(world, "track", track);
            Inject(world, "events", new[]
                { circuit, motoCircuit, time, finale, motoFinale });
            Inject(world, "coursePrefab", courseSource);
            Inject(world, "contentVersion", "loop-play-content-1");
        }

        private GameplayCatalog Gameplay(
            FamilyRacing.Core.Racing.DrivingContentCatalog driving)
        {
            var campaign = new CampaignDefinition
            {
                worldId = "sunny-island",
                nameEn = "Sunny Island",
                nameHe = "האי השמשי",
                requiredEventCompletions = 2,
                requiredRegionalStars = 1
            };
            campaign.courseIds.Add("harbor-hop");
            campaign.regularEventIds.Add("harbor-car-circuit");
            campaign.regularEventIds.Add("harbor-moto-circuit");
            campaign.finales.Add(new FinaleDefinition
            {
                eventId = "sunny-car-finale", classId = "car",
                rivalId = "rival-car", rewardVehicleId = "car-02"
            });
            campaign.finales.Add(new FinaleDefinition
            {
                eventId = "sunny-moto-finale", classId = "motorcycle",
                rivalId = "rival-motorcycle",
                rewardVehicleId = "motorcycle-01"
            });
            var bounties = new List<BountyDefinition>
            {
                new BountyDefinition
                {
                    id = "drift-10s", family = "drift",
                    metric = BountyMetric.DriftSeconds, threshold = 10,
                    unit = "seconds", rewardCoins = 10,
                    aggregation = BountyAggregation.Sum
                },
                new BountyDefinition
                {
                    id = "boost-3", family = "boost",
                    metric = BountyMetric.HumanBoostActivations,
                    threshold = 3, unit = "count", rewardCoins = 10,
                    aggregation = BountyAggregation.Sum
                },
                new BountyDefinition
                {
                    id = "landing-2", family = "landing",
                    metric = BountyMetric.CleanLandings, threshold = 2,
                    unit = "count", rewardCoins = 10,
                    aggregation = BountyAggregation.Sum
                }
            };
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
            var category = new UpgradeCategoryDefinition
            {
                id = "acceleration", maxLevel = 3
            };
            category.prices.Add(60);
            category.prices.Add(120);
            category.prices.Add(180);
            category.tuningFields.Add("acceleration");
            economy.upgradeCategories.Add(category);
            var ok = GameplayCatalog.TryCreate(driving, campaign,
                bounties, economy, new GadgetDefinitionCatalog(),
                new List<AiDifficultyDefinition>
                {
                    new AiDifficultyDefinition { id = "easy" },
                    new AiDifficultyDefinition { id = "medium" },
                    new AiDifficultyDefinition { id = "hard" }
                }, out var gameplay, out var error);
            Assert.That(ok, Is.True, "gameplay catalog: " + error);
            return gameplay;
        }

        // --- three-world fixture (M6 Task 3) ------------------------------
        // The pickers and world cards read only the catalog, so worlds
        // two and three ride synthetic Core courses/events next to the
        // generated harbor-hop content — no extra physics worlds. Sunny
        // keeps the authored fixture events so a real race still finishes.

        private static CourseDefinition MultiCourse(string id)
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
            course.spawnSlots.Add(new MotorPose { x = -5f, z = -57f });
            course.spawnSlots.Add(new MotorPose { x = 5f, z = -57f });
            course.spawnSlots.Add(new MotorPose { x = -5f, z = -62f });
            course.spawnSlots.Add(new MotorPose { x = 5f, z = -62f });
            return course;
        }

        private static List<ObjectiveDefinition> MultiObjectives(string id)
        {
            var intro = new ObjectiveDefinition
            {
                id = id + "-intro", kind = ObjectiveKind.Introductory
            };
            intro.alternatives.Add(new ObjectiveCondition
            {
                metric = ObjectiveMetric.CleanLandings, threshold = 1
            });
            var mastery = new ObjectiveDefinition
            {
                id = id + "-mastery", kind = ObjectiveKind.Mastery
            };
            mastery.alternatives.Add(new ObjectiveCondition
            {
                metric = ObjectiveMetric.CleanLandings, threshold = 3
            });
            return new List<ObjectiveDefinition> { intro, mastery };
        }

        private static EventDefinition MultiEvent(string id,
            string courseId, string classId, string eventType)
        {
            return new EventDefinition
            {
                id = id, courseId = courseId, laps = 1,
                countdownTicks = 3,
                vehicleIds = new List<string>
                    { classId + "-01", classId + "-02" },
                recovery = new RecoverySettings(), completionCoins = 20,
                eventType = eventType, vehicleClass = classId,
                parTicks = eventType == GameplayEventType.Time
                    ? 3600 : 0,
                objectives = MultiObjectives(id)
            };
        }

        private static EventDefinition MultiFinale(string id,
            string courseId, string classId)
        {
            return new EventDefinition
            {
                id = id, courseId = courseId, laps = 1,
                countdownTicks = 3,
                vehicleIds = new List<string>
                    { classId + "-01", classId + "-02" },
                recovery = new RecoverySettings(), completionCoins = 20,
                eventType = GameplayEventType.Finale,
                vehicleClass = classId, isFinale = true
            };
        }

        private static List<EventDefinition> MultiWorldEvents(
            string worldId, string courseId)
        {
            var events = new List<EventDefinition>();
            var prefix = courseId.Substring(0, courseId.IndexOf('-'));
            events.Add(MultiEvent(prefix + "-car-circuit", courseId,
                "car", GameplayEventType.Circuit));
            events.Add(MultiEvent(prefix + "-moto-circuit", courseId,
                "motorcycle", GameplayEventType.Circuit));
            events.Add(MultiEvent(prefix + "-car-time", courseId,
                "car", GameplayEventType.Time));
            events.Add(MultiEvent(prefix + "-moto-time", courseId,
                "motorcycle", GameplayEventType.Time));
            events.Add(MultiFinale(worldId + "-car-finale", courseId,
                "car"));
            events.Add(MultiFinale(worldId + "-moto-finale", courseId,
                "motorcycle"));
            return events;
        }

        private static CampaignDefinition MultiWorldDef(string worldId,
            string nameEn, string nameHe, string courseId,
            string regularPrefix)
        {
            var campaign = new CampaignDefinition
            {
                worldId = worldId, nameEn = nameEn, nameHe = nameHe,
                requiredEventCompletions = 4, requiredRegionalStars = 1
            };
            campaign.courseIds.Add(courseId);
            campaign.regularEventIds.Add(regularPrefix + "-car-circuit");
            campaign.regularEventIds.Add(regularPrefix + "-moto-circuit");
            campaign.regularEventIds.Add(regularPrefix + "-car-time");
            campaign.regularEventIds.Add(regularPrefix + "-moto-time");
            campaign.finales.Add(new FinaleDefinition
            {
                eventId = worldId + "-car-finale", classId = "car",
                rivalId = worldId + "-car-rival",
                rewardVehicleId = "car-02"
            });
            campaign.finales.Add(new FinaleDefinition
            {
                eventId = worldId + "-moto-finale", classId = "motorcycle",
                rivalId = worldId + "-moto-rival",
                rewardVehicleId = "motorcycle-01"
            });
            return campaign;
        }

        // The generated world's catalog extended with the two later
        // worlds' courses and events — harbor-hop and its events carry
        // over verbatim so real campaign races still finish.
        private DrivingContentCatalog MultiWorldDriving()
        {
            var source = world.Catalog;
            var vehicles = new List<VehicleTuning>(
                source.Vehicles.Values);
            vehicles.Add(VehicleTuning.Motorcycle01());
            vehicles[vehicles.Count - 1].vehicleId = "motorcycle-02";
            var courses = new List<CourseDefinition>(
                source.Courses.Values);
            courses.Add(MultiCourse("canyon-dash"));
            courses.Add(MultiCourse("aurora-pass"));
            var events = new List<EventDefinition>(
                source.Events.Values);
            // Sunny's authored trio plus a motorcycle time event so the
            // fixture worlds share one shape.
            events.Add(MultiEvent("harbor-moto-time", "harbor-hop",
                "motorcycle", GameplayEventType.Time));
            events.AddRange(MultiWorldEvents("redrock-canyon",
                "canyon-dash"));
            events.AddRange(MultiWorldEvents("snowy-peaks",
                "aurora-pass"));
            return DrivingContentCatalog.Create("loop-play-content-1",
                vehicles, courses, events);
        }

        private GameplayCatalog MultiWorldGameplay(
            DrivingContentCatalog driving, bool longNames = false)
        {
            var sunny = new CampaignDefinition
            {
                worldId = "sunny-island", nameEn = "Sunny Island",
                nameHe = "האי השמשי",
                requiredEventCompletions = 4, requiredRegionalStars = 1
            };
            sunny.courseIds.Add("harbor-hop");
            sunny.regularEventIds.Add("harbor-car-circuit");
            sunny.regularEventIds.Add("harbor-moto-circuit");
            sunny.regularEventIds.Add("harbor-car-time");
            sunny.regularEventIds.Add("harbor-moto-time");
            sunny.finales.Add(new FinaleDefinition
            {
                eventId = "sunny-car-finale", classId = "car",
                rivalId = "rival-car", rewardVehicleId = "car-02"
            });
            sunny.finales.Add(new FinaleDefinition
            {
                eventId = "sunny-moto-finale", classId = "motorcycle",
                rivalId = "rival-motorcycle",
                rewardVehicleId = "motorcycle-01"
            });
            var worlds = new List<CampaignDefinition>
            {
                sunny,
                MultiWorldDef("redrock-canyon",
                    longNames
                        ? "Redrock Canyon of the Long Winding Road"
                        : "Redrock Canyon",
                    longNames
                        ? "קניון רדרוק של הדרך המפותלת הארוכה מאוד"
                        : "קניון רדרוק",
                    "canyon-dash", "canyon"),
                MultiWorldDef("snowy-peaks", "Snowy Peaks",
                    "הפסגות המושלגות", "aurora-pass", "aurora")
            };
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
            var category = new UpgradeCategoryDefinition
            {
                id = "acceleration", maxLevel = 3
            };
            category.prices.Add(60);
            category.prices.Add(120);
            category.prices.Add(180);
            category.tuningFields.Add("acceleration");
            economy.upgradeCategories.Add(category);
            var bounties = new List<BountyDefinition>
            {
                new BountyDefinition
                {
                    id = "drift-10s", family = "drift",
                    metric = BountyMetric.DriftSeconds, threshold = 10,
                    unit = "seconds", rewardCoins = 10,
                    aggregation = BountyAggregation.Sum
                }
            };
            var ok = GameplayCatalog.TryCreate(driving, worlds, bounties,
                economy, new GadgetDefinitionCatalog(),
                new List<AiDifficultyDefinition>
                {
                    new AiDifficultyDefinition { id = "easy" },
                    new AiDifficultyDefinition { id = "medium" },
                    new AiDifficultyDefinition { id = "hard" }
                }, out var gameplay, out var error);
            Assert.That(ok, Is.True, "multiworld catalog: " + error);
            return gameplay;
        }

        // Completes a fixture world's authored rows on the profile — the
        // durable facts AdventureRules reads to open the next world.
        private static void CompleteFixtureWorld(ProfileSave profile,
            string[] regularEventIds, string[] finaleEventIds)
        {
            foreach (var eventId in regularEventIds)
                profile.eventProgress.Add(new EventProgress
                {
                    eventId = eventId, completed = true,
                    objectiveMask = ObjectiveEvaluator.FullMask
                });
            foreach (var eventId in finaleEventIds)
                profile.finaleProgress.Add(new FinaleProgress
                {
                    eventId = eventId, completed = true,
                    victorious = true
                });
        }

        private static readonly string[] SunnyRegulars =
        {
            "harbor-car-circuit", "harbor-moto-circuit",
            "harbor-car-time", "harbor-moto-time"
        };
        private static readonly string[] SunnyFinales =
            { "sunny-car-finale", "sunny-moto-finale" };

        // Rebinds the service to the three-world catalog around a seeded
        // profile, then lands on the sandbox — the same seam
        // SeedProfile uses, pointed at the ordered adventure.
        private IEnumerator SeedMultiProfile(string language,
            ProfileSave profile = null, bool longNames = false)
        {
            store.Save(profile != null
                ? profile : ProfileFactory.Create(0, language));
            service = new FoundationService(store, language, RunId);
            Inject(app, "service", service);
            var driving = MultiWorldDriving();
            service.SetDrivingContent(driving);
            service.SetGameplayCatalog(
                MultiWorldGameplay(driving, longNames));
            service.SetDrivingSimulationFactory(world.CreateSimulation);
            Assert.That(app.Dispatch("profile.select",
                Params(("slot", "0"))).status, Is.EqualTo("applied"));
            Assert.That(app.Dispatch("ui.navigate",
                Params(("screen", "sandbox"))).status,
                Is.EqualTo("applied"));
            Refresh();
            yield return WaitForWidget<Button>("loop.campaign");
        }

        // --- helpers -------------------------------------------------------

        private FoundationState Snapshot()
        {
            return app.Service.Snapshot();
        }

        private void Refresh()
        {
            shell.Refresh(Snapshot());
        }

        private T Widget<T>(string name) where T : VisualElement
        {
            var root = document.rootVisualElement;
            return root == null ? null : root.Q<T>(name);
        }

        private List<VisualElement> WidgetsNamed(string prefix)
        {
            var found = new List<VisualElement>();
            var root = document.rootVisualElement;
            if (root == null) return found;
            foreach (var element in root.Query<VisualElement>().ToList())
                if (element.name != null
                    && element.name.StartsWith(prefix,
                        StringComparison.Ordinal))
                    found.Add(element);
            return found;
        }

        private IEnumerator WaitFor(Func<bool> done, string what,
            float timeout = 5f)
        {
            var until = Time.unscaledTime + timeout;
            while (Time.unscaledTime < until && !done())
                yield return null;
            Assert.That(done(), Is.True,
                "Timed out waiting for " + what + ".");
        }

        private IEnumerator WaitForWidget<T>(string name)
            where T : VisualElement
        {
            yield return WaitFor(() =>
            {
                var widget = Widget<T>(name);
                return widget != null && widget.worldBound.width > 0f
                    && widget.worldBound.height > 0f;
            }, "widget " + name);
        }

        private static Dictionary<string, string> Params(
            params (string name, string value)[] entries)
        {
            var map = new Dictionary<string, string>();
            foreach (var (name, value) in entries) map[name] = value;
            return map;
        }

        private static void Press(Button button)
        {
            var center = button.worldBound.center;
            var touch = new Touch
            {
                fingerId = 0, position = center, phase = TouchPhase.Began
            };
            using (var down = PointerDownEvent.GetPooled(touch,
                EventModifiers.None))
            {
                down.target = button;
                button.SendEvent(down);
            }
            touch.phase = TouchPhase.Ended;
            using (var up = PointerUpEvent.GetPooled(touch,
                EventModifiers.None))
            {
                up.target = button;
                button.SendEvent(up);
            }
        }

        // Seeds a profile straight to disk (slot caches bind at service
        // construction, so the service is rebuilt around it) and lands
        // on the sandbox screen the loop buttons live on.
        private IEnumerator SeedProfile(string language)
        {
            store.Save(ProfileFactory.Create(0, language));
            service = new FoundationService(store, language, RunId);
            Inject(app, "service", service);
            service.SetDrivingContent(world.Catalog);
            service.SetGameplayCatalog(Gameplay(world.Catalog));
            service.SetDrivingSimulationFactory(world.CreateSimulation);
            Assert.That(app.Dispatch("profile.select",
                Params(("slot", "0"))).status, Is.EqualTo("applied"));
            Assert.That(app.Dispatch("ui.navigate",
                Params(("screen", "sandbox"))).status,
                Is.EqualTo("applied"));
            Refresh();
            yield return WaitForWidget<Button>("loop.campaign");
        }

        // Rebuilds the shell after a picker tap — the picker fields are
        // shell-side facts, so the LoopSignature change repaints once.
        private IEnumerator Tap(string widgetName)
        {
            yield return WaitForWidget<Button>(widgetName);
            var button = Widget<Button>(widgetName);
            Assert.That(button.enabledSelf, Is.True,
                widgetName + " must be pressable");
            yield return Reveal(button);
            Press(button);
            Refresh();
            yield return null;
        }

        // A synthesized press must land on a point the panel can pick:
        // a button below a scroll viewport is clipped, its center is
        // unpickable, and the down/up never activates Clickable. Scroll
        // the nearest ScrollView ancestor first — the same gesture a
        // real touch user performs — then press.
        private IEnumerator Reveal(VisualElement element)
        {
            if (element == null) yield break;
            var scroll = element.GetFirstAncestorOfType<ScrollView>();
            if (scroll == null) yield break;
            scroll.ScrollTo(element);
            yield return null;
        }

        private IEnumerator PressWidget(string widgetName)
        {
            yield return WaitForWidget<Button>(widgetName);
            var button = Widget<Button>(widgetName);
            yield return Reveal(button);
            Press(button);
        }

        private void Step(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                service.StepSolo(world.MotorWorld);
                service.AdvanceTick();
            }
        }

        private CommandResult Drive(float steering, int durationTicks = 5)
        {
            return app.Dispatch("input.set", Params(
                ("steering", steering.ToString("0.###",
                    System.Globalization.CultureInfo.InvariantCulture)),
                ("drift", "false"), ("boost", "false"),
                ("gadget", "false"), ("recover", "false"),
                ("changeCamera", "false"), ("pause", "false"),
                ("durationTicks", durationTicks.ToString(
                    System.Globalization.CultureInfo
                        .InvariantCulture))));
        }

        private static float WrapAngle(float radians)
        {
            while (radians > Mathf.PI) radians -= 2f * Mathf.PI;
            while (radians <= -Mathf.PI) radians += 2f * Mathf.PI;
            return radians;
        }

        private static MotorState HumanMotor(FoundationState snapshot)
        {
            if (snapshot.soloRace == null) return null;
            if (snapshot.soloRace.trial != null)
                return snapshot.soloRace.trial.motor;
            foreach (var racer in snapshot.soloRace.race.racers)
                if (racer.participantId == snapshot.soloRace.participantId)
                    return racer.session != null ? racer.session.motor : null;
            return null;
        }

        private static bool HumanFinished(FoundationState snapshot)
        {
            if (snapshot.soloRace == null) return false;
            if (snapshot.soloRace.trial != null)
                return snapshot.soloRace.trial.finished;
            foreach (var racer in snapshot.soloRace.race.racers)
                if (racer.participantId == snapshot.soloRace.participantId)
                    return racer.finished;
            return false;
        }

        private void RenderSoloRace(FoundationState snapshot)
        {
            if (snapshot.soloRace == null || snapshot.soloRace.race == null)
            {
                world.ApplyRaceFrame(null);
                return;
            }
            var roster = new List<ParticipantState>(1);
            if (snapshot.draftProfile != null)
                roster.Add(new ParticipantState
                {
                    participantId = snapshot.soloRace.participantId,
                    slot = 0, occupied = true, connected = true,
                    controlSource = "local",
                    avatar = snapshot.draftProfile.avatar ?? "",
                    colorIndex = snapshot.draftProfile.colorIndex
                });
            world.SetLocalProfile(snapshot.savedProfile);
            world.ApplyRaceFrame(snapshot.soloRace.race, 1f, null, roster);
        }

        private static readonly Vector2[] DriveRoute =
        {
            new Vector2(0f, 33f), new Vector2(63f, 55f),
            new Vector2(95f, -8f), new Vector2(44f, -55f),
            new Vector2(0f, -70f), new Vector2(0f, -50f)
        };

        private FoundationState Pursue(FoundationState state, int maxTicks)
        {
            var wp = 0;
            for (var t = 0; t < maxTicks && !HumanFinished(state);)
            {
                var motor = HumanMotor(state);
                if (motor == null) break;
                var target = DriveRoute[wp % DriveRoute.Length];
                var dx = target.x - motor.x;
                var dz = target.y - motor.z;
                if (dx * dx + dz * dz < 49f) { wp++; continue; }
                var desired = Mathf.Atan2(dx, dz);
                var steer = Mathf.Clamp(
                    WrapAngle(desired - motor.heading) * 3f, -1f, 1f);
                var applied = Drive(steer, 5);
                Assert.That(applied.status, Is.EqualTo("applied"),
                    "steering input must apply mid-race: " + applied.code);
                Step(5);
                t += 5;
                state = Snapshot();
            }
            return state;
        }

        // Starts the authored campaign circuit through the command path
        // and drives the local seat to a finalized result — the same
        // honest drive SoloRacePlayTests proves.
        private FoundationState FinishCampaignRace()
        {
            var started = app.Dispatch("campaign.start", Params(
                ("eventId", "harbor-car-circuit"),
                ("vehicleId", "car-01"), ("difficulty", "easy")));
            Assert.That(started.status, Is.EqualTo("applied"),
                "campaign.start: " + started.code);
            Step(3); // authored countdown
            var snapshot = Snapshot();
            world.ApplyFrame(snapshot);
            RenderSoloRace(snapshot);
            Physics.SyncTransforms();
            snapshot = Pursue(snapshot, 6000);
            var guard = 0;
            while ((snapshot.soloRace == null
                    || snapshot.soloRace.result == null) && guard++ < 4000)
            {
                Step(1);
                snapshot = Snapshot();
            }
            Assert.That(snapshot.soloRace.result, Is.Not.Null,
                "the campaign race never finalized");
            Assert.That(HumanFinished(snapshot), Is.True,
                "the scripted driver must finish the lap");
            return snapshot;
        }

        // --- tests ---------------------------------------------------------

        [UnityTest]
        public IEnumerator SandboxLoopButtonsLandOnGatedScreens()
        {
            yield return SeedProfile("en");

            var screens = new (string button, string screen, string title)[]
            {
                ("loop.campaign", "campaign", "campaign.title"),
                ("loop.quickRace", "quickRace", "quick.title"),
                ("loop.timeTrial", "timeTrial", "trial.title"),
                ("loop.garage", "garage", "garage.coins"),
                ("loop.bounties", "bounties", "bounties.title")
            };
            foreach (var (button, screen, title) in screens)
            {
                yield return PressWidget(button);
                Assert.That(Snapshot().screen, Is.EqualTo(screen),
                    "the " + button + " button must dispatch its gated "
                        + "*.open command");
                Refresh();
                yield return WaitForWidget<VisualElement>(title);
                yield return PressWidget("loop.back");
                Assert.That(Snapshot().screen, Is.EqualTo("sandbox"),
                    "the back button navigates home through ui.navigate");
                Refresh();
            }
        }

        [UnityTest]
        public IEnumerator CampaignMapRendersAuthoredRowsLocksAndStars()
        {
            yield return SeedProfile("en");
            yield return Tap("loop.campaign");
            yield return WaitForWidget<Label>("campaign.stars");

            var snapshot = Snapshot();
            Assert.That(snapshot.progression, Is.Not.Null);
            Assert.That(snapshot.progression.available, Is.True);
            Assert.That(snapshot.progression.campaignEvents.Count,
                Is.EqualTo(4),
                "two regulars plus both class finales, in authored order");

            var regulars = 0;
            var finales = 0;
            foreach (var row in snapshot.progression.campaignEvents)
            {
                var rowEl = Widget<VisualElement>(
                    "campaign.row." + row.eventId);
                Assert.That(rowEl, Is.Not.Null,
                    "every authored event renders a row: " + row.eventId);
                var stars = Widget<Label>(
                    "campaign.event." + row.eventId + ".stars");
                Assert.That(stars, Is.Not.Null);
                Assert.That(stars.text, Does.Contain("‎"),
                    "the star counter carries the LTR isolate");
                Assert.That(stars.text, Does.Contain("0/3"));
                var pick = Widget<Button>(
                    "campaign.event." + row.eventId);
                if (row.finale)
                {
                    finales++;
                    Assert.That(row.locked, Is.True,
                        "a gated finale reports locked");
                    Assert.That(pick.enabledSelf, Is.False,
                        "the locked finale's row stays disabled");
                    Assert.That(Widget<Label>(
                        "campaign.event." + row.eventId + ".lock"),
                        Is.Not.Null,
                        "a locked row names its reason honestly");
                }
                else
                {
                    regulars++;
                    Assert.That(row.locked, Is.False);
                    Assert.That(pick.enabledSelf, Is.True);
                }
            }
            Assert.That(regulars, Is.EqualTo(2));
            Assert.That(finales, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator CampaignStartButtonRunsTheSharedCommand()
        {
            yield return SeedProfile("en");
            yield return Tap("loop.campaign");
            yield return Tap("campaign.event.harbor-car-circuit");
            yield return WaitForWidget<Button>("campaign.vehicle.car-01");
            // The unowned vehicle sits disabled with the same reason the
            // command would reject with.
            var locked = Widget<Button>("campaign.vehicle.car-02");
            Assert.That(locked, Is.Not.Null);
            Assert.That(locked.enabledSelf, Is.False,
                "an unowned vehicle stays disabled in the picker");
            Assert.That(locked.tooltip, Is.Not.Empty,
                "a disabled pick carries the shared verdict reason");

            yield return Tap("campaign.vehicle.car-01");
            yield return Tap("campaign.difficulty.easy");
            yield return PressWidget("campaign.start");

            var snapshot = Snapshot();
            Assert.That(snapshot.screen, Is.EqualTo("driving"),
                "the start button dispatches campaign.start");
            Assert.That(snapshot.soloRace, Is.Not.Null);
            Assert.That(snapshot.soloRace.mode, Is.EqualTo("campaign"));
            Assert.That(snapshot.soloRace.race, Is.Not.Null);
            Assert.That(snapshot.soloRace.tuningSource,
                Is.EqualTo("purchased"),
                "the campaign seats the profile's durable tune");
        }

        [UnityTest]
        public IEnumerator QuickRacePickerStartsTheResolvedCircuit()
        {
            yield return SeedProfile("en");
            yield return Tap("loop.quickRace");
            yield return WaitForWidget<Label>("quick.title");

            yield return Tap("quick.course.harbor-hop");
            yield return WaitForWidget<Button>("quick.class.car");
            yield return Tap("quick.class.car");
            yield return WaitForWidget<Button>("quick.vehicle.car-01");
            yield return Tap("quick.vehicle.car-01");
            yield return PressWidget("quick.start");

            var snapshot = Snapshot();
            Assert.That(snapshot.screen, Is.EqualTo("driving"));
            Assert.That(snapshot.soloRace, Is.Not.Null);
            Assert.That(snapshot.soloRace.mode, Is.EqualTo("quickRace"));
            Assert.That(snapshot.soloRace.race, Is.Not.Null);
            Assert.That(snapshot.soloRace.race.racers[0].session.eventId,
                Is.EqualTo("harbor-car-circuit"),
                "the picker resolved the authored circuit the command "
                    + "path resolves");
        }

        [UnityTest]
        public IEnumerator TimeTrialPickerSeatsTheNormalizedStarter()
        {
            yield return SeedProfile("en");
            yield return Tap("loop.timeTrial");
            yield return WaitForWidget<Label>("trial.title");

            yield return Tap("trial.course.harbor-hop");
            yield return WaitForWidget<Button>("trial.class.car");
            yield return Tap("trial.class.car");
            yield return WaitForWidget<Label>("trial.starter");

            var starter = Widget<Label>("trial.starter");
            Assert.That(starter.text, Is.Not.Empty,
                "the resolved normalized starter is named honestly");
            Assert.That(Widget<Label>("trial.par"), Is.Not.Null,
                "the authored par rides the picker");
            var record = Widget<Label>("trial.record");
            Assert.That(record, Is.Not.Null,
                "the durable record row renders even with no record");

            yield return WaitForWidget<Button>("trial.start");
            yield return PressWidget("trial.start");
            var snapshot = Snapshot();
            Assert.That(snapshot.screen, Is.EqualTo("driving"));
            Assert.That(snapshot.soloRace, Is.Not.Null);
            Assert.That(snapshot.soloRace.mode, Is.EqualTo("timeTrial"));
            Assert.That(snapshot.soloRace.trial, Is.Not.Null);
            Assert.That(snapshot.soloRace.race, Is.Null);
            Assert.That(snapshot.soloRace.tuningSource,
                Is.EqualTo("normalized"),
                "the trial seats the authored normalized starter");
        }

        [UnityTest]
        public IEnumerator BountyBoardListsTheThreeAssignments()
        {
            yield return SeedProfile("en");
            yield return Tap("loop.bounties");
            yield return WaitForWidget<Label>("bounties.title");

            var snapshot = Snapshot();
            Assert.That(snapshot.progression, Is.Not.Null);
            Assert.That(snapshot.progression.bounties.Count,
                Is.EqualTo(3),
                "the tray publishes exactly the three seeded rows");
            foreach (var row in snapshot.progression.bounties)
            {
                Assert.That(
                    Widget<VisualElement>("bounty." + row.assignmentId),
                    Is.Not.Null, "assignment row: " + row.assignmentId);
                var progress = Widget<Label>(
                    "bounty." + row.assignmentId + ".progress");
                Assert.That(progress, Is.Not.Null);
                Assert.That(progress.text,
                    Does.Contain(row.progress + "/" + row.threshold));
                Assert.That(progress.text, Does.Contain("‎"),
                    "the progress counter stays LTR");
                var reward = Widget<Label>(
                    "bounty." + row.assignmentId + ".reward");
                Assert.That(reward.text,
                    Does.Contain(row.rewardCoins.ToString()));
            }
            Assert.That(Widget<Label>("bounties.coins"), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator HebrewLoopScreensKeepNumericsLtr()
        {
            yield return SeedProfile("he");
            yield return Tap("loop.campaign");
            yield return WaitForWidget<Label>("campaign.stars");

            Assert.That(document.rootVisualElement.languageDirection,
                Is.EqualTo(LanguageDirection.RTL),
                "the Hebrew campaign renders RTL");
            var stars = Widget<Label>("campaign.stars");
            Assert.That(stars.text, Does.Contain("‎"),
                "numeric counters carry the LTR isolate inside RTL");
            Assert.That(stars.text, Does.Contain("0/"));

            yield return Tap("loop.back");
            yield return Tap("loop.bounties");
            yield return WaitForWidget<Label>("bounties.title");
            var snapshot = Snapshot();
            foreach (var row in snapshot.progression.bounties)
            {
                var progress = Widget<Label>(
                    "bounty." + row.assignmentId + ".progress");
                Assert.That(progress, Is.Not.Null);
                Assert.That(progress.text, Does.Contain("‎"),
                    "bounty progress stays LTR inside the RTL board");
            }
        }

        [UnityTest]
        public IEnumerator FinishedRaceShowsResultsOverlayAndRetryStartsFresh()
        {
            yield return SeedProfile("en");
            var snapshot = FinishCampaignRace();
            Assert.That(snapshot.soloRace.phase, Is.EqualTo("finished"));
            var firstRaceId = snapshot.soloRace.raceId;

            Refresh();
            yield return WaitForWidget<VisualElement>("overlay.results");
            var overlay = Widget<VisualElement>("overlay.results");
            Assert.That(overlay.style.display.value,
                Is.EqualTo(DisplayStyle.Flex),
                "a finalized solo result surfaces the results overlay");
            Assert.That(Widget<Label>("results.place").text,
                Is.Not.Empty);
            Assert.That(Widget<Label>("results.coins").text,
                Is.Not.Empty,
                "the coin award is named on the results card");
            Assert.That(Widget<Label>("results.breakdown").text,
                Is.Not.Empty,
                "a committed receipt renders its honest breakdown");

            yield return PressWidget("results.retry");
            snapshot = Snapshot();
            Assert.That(snapshot.soloRace, Is.Not.Null);
            Assert.That(snapshot.soloRace.raceId,
                Is.Not.EqualTo(firstRaceId),
                "one-action replay mints a fresh race identity");
            Assert.That(snapshot.soloRace.result, Is.Null,
                "the replay is a new run, not a replayed result");
            Refresh();
            Assert.That(Widget<VisualElement>("overlay.results")
                    .style.display.value, Is.EqualTo(DisplayStyle.None),
                "the overlay hides once a fresh run is live");
        }

        [UnityTest]
        public IEnumerator ResultsContinueReturnsToTheModesSetup()
        {
            yield return SeedProfile("en");
            var snapshot = FinishCampaignRace();
            Refresh();
            yield return WaitForWidget<VisualElement>("overlay.results");

            yield return PressWidget("results.continue");
            snapshot = Snapshot();
            Assert.That(snapshot.soloRace, Is.Null,
                "continue retires the finished session");
            Assert.That(snapshot.screen, Is.EqualTo("campaign"),
                "a campaign result continues to the campaign map");
            Refresh();
            yield return WaitForWidget<Label>("campaign.title");
        }

        // Task 10 review fix: a finalized result whose commit failed
        // must keep the overlay — and its retrySave remedy — mounted
        // while replay/continue reject rewardUnresolved through BOTH
        // the widget availability and the command path.
        [UnityTest]
        public IEnumerator FailedResultSaveKeepsRetrySaveReachable()
        {
            var toggling = new TogglingSerializer();
            store = new ProfileStore(directory, toggling);
            yield return SeedProfile("en");

            var started = app.Dispatch("campaign.start", Params(
                ("eventId", "harbor-car-circuit"),
                ("vehicleId", "car-01"), ("difficulty", "easy")));
            Assert.That(started.status, Is.EqualTo("applied"),
                "campaign.start: " + started.code);
            Step(3);
            var snapshot = Snapshot();
            world.ApplyFrame(snapshot);
            RenderSoloRace(snapshot);
            Physics.SyncTransforms();
            snapshot = Pursue(snapshot, 6000);
            Assert.That(HumanFinished(snapshot), Is.True,
                "the scripted driver must finish the lap");
            // The context bind committed at start; only the result
            // transaction is allowed to fail.
            toggling.failing = true;
            var guard = 0;
            while ((snapshot.soloRace == null
                    || snapshot.soloRace.result == null)
                && guard++ < 4000)
            {
                Step(1);
                snapshot = Snapshot();
            }
            Assert.That(snapshot.soloRace.result, Is.Not.Null,
                "the campaign race never finalized");
            Assert.That(snapshot.progression.rewardUnresolved, Is.True,
                "the failed commit retains the unresolved pair");
            var raceId = snapshot.soloRace.raceId;

            Refresh();
            yield return WaitForWidget<VisualElement>("overlay.results");
            var overlay = Widget<VisualElement>("overlay.results");
            Assert.That(overlay.style.display.value,
                Is.EqualTo(DisplayStyle.Flex),
                "the finished result keeps the overlay mounted");
            var retrySave = Widget<Button>("results.retrySave");
            Assert.That(retrySave, Is.Not.Null);
            Assert.That(retrySave.style.display.value,
                Is.EqualTo(DisplayStyle.Flex),
                "the save-failed remedy renders");
            Assert.That(retrySave.enabledSelf, Is.True,
                "the remedy button must actually press");
            var retry = Widget<Button>("results.retry");
            var cont = Widget<Button>("results.continue");
            Assert.That(retry.enabledSelf, Is.False,
                "replay greys out while the gate holds");
            Assert.That(cont.enabledSelf, Is.False);
            Assert.That(retry.tooltip, Does.Contain("rewardUnresolved"),
                "the disabled button names the shared reason");

            var rejectedRetry = app.Dispatch("results.retry", Params());
            Assert.That(rejectedRetry.status, Is.EqualTo("rejected"));
            Assert.That(rejectedRetry.code, Is.EqualTo("rewardUnresolved"));
            var rejectedContinue = app.Dispatch("results.continue",
                Params());
            Assert.That(rejectedContinue.status, Is.EqualTo("rejected"));
            Assert.That(rejectedContinue.code,
                Is.EqualTo("rewardUnresolved"));
            snapshot = Snapshot();
            Assert.That(snapshot.soloRace, Is.Not.Null,
                "rejected results commands must not tear the session");
            Assert.That(snapshot.soloRace.raceId, Is.EqualTo(raceId));
            Assert.That(snapshot.soloRace.result, Is.Not.Null);
            Assert.That(snapshot.screen, Is.EqualTo("driving"));
            Refresh();
            Assert.That(Widget<VisualElement>("overlay.results")
                    .style.display.value, Is.EqualTo(DisplayStyle.Flex),
                "the overlay stays mounted so retrySave stays reachable");

            toggling.failing = false;
            Press(retrySave);
            snapshot = Snapshot();
            Assert.That(snapshot.progression.rewardUnresolved, Is.False,
                "the retried save commits the retained transaction");
            Refresh();
            yield return WaitForWidget<Button>("results.retry");
            var replay = Widget<Button>("results.retry");
            Assert.That(replay.enabledSelf, Is.True,
                "replay re-enables once the gate clears");
            Press(replay);
            snapshot = Snapshot();
            Assert.That(snapshot.soloRace, Is.Not.Null);
            Assert.That(snapshot.soloRace.raceId,
                Is.Not.EqualTo(raceId),
                "replay mints a fresh race identity");
            Assert.That(snapshot.soloRace.result, Is.Null,
                "the replay is a new run, not a replayed result");
            Refresh();
            Assert.That(Widget<VisualElement>("overlay.results")
                    .style.display.value, Is.EqualTo(DisplayStyle.None),
                "the overlay hides once a fresh run is live");
        }

        // --- multiworld screens (M6 Task 3) -------------------------------
        // The ordered world strip rides the campaign map: bilingual
        // names, per-world swatch/icon/progress, an honest disabled lock
        // reason that mirrors AdventureRules.Evaluate, and selection
        // through the real campaign.selectWorld command.

        [UnityTest]
        public IEnumerator WorldCardsRenderNamesSwatchesAndLocksEnglish()
        {
            yield return SeedMultiProfile("en");
            yield return Tap("loop.campaign");
            yield return WaitForWidget<Label>("campaign.stars");

            var strip = Widget<ScrollView>("campaign.worlds");
            Assert.That(strip, Is.Not.Null,
                "the world strip scrolls inside the campaign screen");

            var snapshot = Snapshot();
            var worlds = snapshot.progression.worlds;
            Assert.That(worlds.Count, Is.EqualTo(3),
                "worlds.Count == 3 — every authored world renders a card");
            var access = AdventureRules.Evaluate(
                app.Service.GameplayContent, snapshot.savedProfile);
            var expected = new[] { "sunny-island", "redrock-canyon",
                "snowy-peaks" };
            var names = new[] { "Sunny Island", "Redrock Canyon",
                "Snowy Peaks" };
            for (var i = 0; i < worlds.Count; i++)
            {
                Assert.That(worlds[i].worldId, Is.EqualTo(expected[i]),
                    "world rows keep authored order");
                var card = Widget<Button>("campaign.world." + expected[i]);
                Assert.That(card, Is.Not.Null,
                    "world card: " + expected[i]);
                // UI availability is the streamed availability — both
                // equal the AdventureRules verdict, never a guess.
                Assert.That(card.enabledSelf,
                    Is.EqualTo(worlds[i].available),
                    "card availability mirrors the streamed row");
                Assert.That(access.worlds[i].available,
                    Is.EqualTo(worlds[i].available),
                    "the streamed row mirrors AdventureRules.Evaluate");
                var name = Widget<Label>(
                    "campaign.world." + expected[i] + ".name");
                Assert.That(name, Is.Not.Null);
                Assert.That(name.text, Is.EqualTo(names[i]));
                Assert.That(Widget<VisualElement>(
                    "campaign.world." + expected[i] + ".swatch"),
                    Is.Not.Null, "every card carries its color swatch");
                var icon = Widget<Label>(
                    "campaign.world." + expected[i] + ".icon");
                Assert.That(icon, Is.Not.Null,
                    "every card carries its icon badge");
                Assert.That(icon.text, Is.Not.Empty);
                var progress = Widget<Label>(
                    "campaign.world." + expected[i] + ".progress");
                Assert.That(progress, Is.Not.Null);
                Assert.That(progress.text, Does.Contain(
                    worlds[i].completedEvents + "/"
                        + worlds[i].requiredEvents));
                if (!worlds[i].available)
                {
                    var lockLabel = Widget<Label>(
                        "campaign.world." + expected[i] + ".lock");
                    Assert.That(lockLabel, Is.Not.Null,
                        "a closed world names its reason");
                    Assert.That(lockLabel.text, Is.Not.Empty);
                    Assert.That(card.tooltip, Is.Not.Empty,
                        "the disabled card carries the same reason");
                }
            }
            // The selected badge sits on the default world only.
            Assert.That(Widget<Label>(
                "campaign.world.sunny-island.selected"), Is.Not.Null);
            Assert.That(Widget<Label>(
                "campaign.world.redrock-canyon.selected"), Is.Null);
            // A disabled card is a real dead end — tapping it mutates
            // nothing.
            yield return PressWidget("campaign.world.snowy-peaks");
            Assert.That(Snapshot().progression.campaignWorldId,
                Is.EqualTo("sunny-island"));
        }

        [UnityTest]
        public IEnumerator WorldCardsRenderHebrewNamesAndReasons()
        {
            yield return SeedMultiProfile("he");
            yield return Tap("loop.campaign");
            yield return WaitForWidget<Label>("campaign.stars");

            Assert.That(document.rootVisualElement.languageDirection,
                Is.EqualTo(LanguageDirection.RTL),
                "the Hebrew campaign map renders RTL");
            var snapshot = Snapshot();
            var expected = new[] { "sunny-island", "redrock-canyon",
                "snowy-peaks" };
            var names = new[] { "האי השמשי", "קניון רדרוק",
                "הפסגות המושלגות" };
            for (var i = 0; i < expected.Length; i++)
            {
                var card = Widget<Button>("campaign.world." + expected[i]);
                Assert.That(card, Is.Not.Null);
                var name = Widget<Label>(
                    "campaign.world." + expected[i] + ".name");
                Assert.That(name.text, Is.EqualTo(names[i]),
                    "the Hebrew world name renders verbatim");
                var progress = Widget<Label>(
                    "campaign.world." + expected[i] + ".progress");
                Assert.That(progress.text, Does.Contain("‎"),
                    "the card counters stay LTR inside RTL");
            }
            var lockLabel = Widget<Label>(
                "campaign.world.redrock-canyon.lock");
            Assert.That(lockLabel, Is.Not.Null);
            Assert.That(lockLabel.text,
                Is.EqualTo(
                    FamilyRacing.Core.Localization.ShellText.Get(
                        "loop.reason.worldLocked", "he")),
                "the lock reason is the localized shared reason");
        }

        [UnityTest]
        public IEnumerator TappingAWorldCardRestacksTheSelectedEvents()
        {
            // Sunny complete -> the default selection is redrock and the
            // map lists canyon events; tapping the sunny card reselects
            // it through campaign.selectWorld.
            var profile = ProfileFactory.Create(0, "en");
            CompleteFixtureWorld(profile, SunnyRegulars, SunnyFinales);
            yield return SeedMultiProfile("en", profile);
            yield return Tap("loop.campaign");
            yield return WaitForWidget<Label>("campaign.stars");

            var snapshot = Snapshot();
            Assert.That(snapshot.progression.campaignWorldId,
                Is.EqualTo("redrock-canyon"),
                "the first accessible unfinished world is selected");
            Assert.That(Widget<VisualElement>(
                "campaign.row.canyon-car-circuit"), Is.Not.Null,
                "the selected world's events render");
            Assert.That(Widget<VisualElement>(
                "campaign.row.harbor-car-circuit"), Is.Null,
                "another world's events stay off the map");

            yield return Tap("campaign.world.sunny-island");
            snapshot = Snapshot();
            Assert.That(snapshot.progression.campaignWorldId,
                Is.EqualTo("sunny-island"),
                "the card tap dispatched campaign.selectWorld");
            Assert.That(snapshot.progression.worlds[0].selected,
                Is.True);
            yield return WaitForWidget<VisualElement>(
                "campaign.row.harbor-car-circuit");
            Assert.That(Widget<VisualElement>(
                "campaign.row.canyon-car-circuit"), Is.Null,
                "the event rows restack onto the selected world");
            // Sunny is complete: the card still carries its done badge.
            Assert.That(Widget<Label>(
                "campaign.world.sunny-island.done"), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator LongWorldNamesWrapInsideTheCard()
        {
            yield return SeedMultiProfile("en", null, longNames: true);
            yield return Tap("loop.campaign");
            yield return WaitForWidget<Label>("campaign.stars");

            var name = Widget<Label>(
                "campaign.world.redrock-canyon.name");
            Assert.That(name, Is.Not.Null);
            Assert.That(name.text, Does.Contain("Long Winding Road"));
            Assert.That(name.style.whiteSpace.value,
                Is.EqualTo(WhiteSpace.Normal),
                "a longer name wraps instead of clipping");
            var card = Widget<Button>("campaign.world.redrock-canyon");
            yield return WaitFor(() =>
                card.worldBound.width > 0f, "world card layout");
            Assert.That(card.worldBound.width,
                Is.LessThanOrEqualTo(560f),
                "a long name never grows the card past its cell");
        }

        [UnityTest]
        public IEnumerator QuickRaceGroupsCoursesByWorldAndHonorsLocks()
        {
            yield return SeedMultiProfile("en");
            yield return Tap("loop.quickRace");
            yield return WaitForWidget<Label>("quick.title");

            var snapshot = Snapshot();
            var gameplay = app.Service.GameplayContent;
            foreach (var pair in new[]
            {
                new { world = "sunny-island", course = "harbor-hop" },
                new { world = "redrock-canyon", course = "canyon-dash" },
                new { world = "snowy-peaks", course = "aurora-pass" }
            })
            {
                var header = Widget<Label>("quick.world." + pair.world);
                Assert.That(header, Is.Not.Null,
                    "each world owns a course group header");
                var button = Widget<Button>(
                    "quick.course." + pair.course);
                Assert.That(button, Is.Not.Null,
                    "the course renders under its world group");
                var allowed = AdventureRules.CanAccessCourse(
                    gameplay, snapshot.savedProfile, pair.course);
                Assert.That(button.enabledSelf, Is.EqualTo(allowed),
                    pair.course + " availability mirrors the rules");
                if (!allowed)
                    Assert.That(button.tooltip, Is.Not.Empty,
                        "a locked course names its reason");
            }
            // The locked pick refuses even when pressed directly.
            yield return PressWidget("quick.course.canyon-dash");
            Assert.That(Snapshot().screen, Is.EqualTo("quickRace"));
        }

        [UnityTest]
        public IEnumerator TimeTrialListsEveryWorldCourseFromStart()
        {
            yield return SeedMultiProfile("en");
            yield return Tap("loop.timeTrial");
            yield return WaitForWidget<Label>("trial.title");

            // Trials are the always-open lane: all six authored courses
            // are enterable on a fresh profile regardless of the world
            // gate (the command surface answers the same verdict).
            foreach (var courseId in new[]
                { "harbor-hop", "canyon-dash", "aurora-pass" })
            {
                var button = Widget<Button>(
                    "trial.course." + courseId);
                Assert.That(button, Is.Not.Null,
                    "trial course: " + courseId);
                Assert.That(button.enabledSelf, Is.True,
                    courseId + " stays open for time trials");
            }
            // The world grouping is honest there too.
            Assert.That(Widget<Label>("trial.world.snowy-peaks"),
                Is.Not.Null);
            yield return Tap("trial.course.canyon-dash");
            yield return WaitForWidget<Button>("trial.class.car");
            yield return Tap("trial.class.car");
            yield return WaitForWidget<Label>("trial.starter");
            var verdict = app.Service.CanEnter(RaceMode.TimeTrial,
                "canyon-dash", "", "car", "");
            Assert.That(verdict.allowed, Is.True,
                "a locked-world course still seats the trial starter");
        }

        [UnityTest]
        public IEnumerator AdventureCompleteBannerRendersOnFullRun()
        {
            var profile = ProfileFactory.Create(0, "en");
            CompleteFixtureWorld(profile, SunnyRegulars, SunnyFinales);
            CompleteFixtureWorld(profile, new[]
            {
                "canyon-car-circuit", "canyon-moto-circuit",
                "canyon-car-time", "canyon-moto-time"
            }, new[] { "redrock-canyon-car-finale",
                "redrock-canyon-moto-finale" });
            CompleteFixtureWorld(profile, new[]
            {
                "aurora-car-circuit", "aurora-moto-circuit",
                "aurora-car-time", "aurora-moto-time"
            }, new[] { "snowy-peaks-car-finale",
                "snowy-peaks-moto-finale" });
            yield return SeedMultiProfile("en", profile);
            yield return Tap("loop.campaign");
            yield return WaitForWidget<Label>("campaign.stars");

            var snapshot = Snapshot();
            Assert.That(snapshot.progression.adventureComplete, Is.True);
            var banner = Widget<Label>("campaign.adventureComplete");
            Assert.That(banner, Is.Not.Null,
                "a finished adventure presents its completion banner");
            Assert.That(banner.text,
                Is.EqualTo(
                    FamilyRacing.Core.Localization.ShellText.Get(
                        "campaign.adventureComplete", "en")));
            // The finished adventure keeps the last world selected and
            // every card marked done.
            Assert.That(snapshot.progression.campaignWorldId,
                Is.EqualTo("snowy-peaks"));
            Assert.That(Widget<Label>(
                "campaign.world.snowy-peaks.done"), Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator ResultsContinueReturnsToTheRacedWorld()
        {
            var profile = ProfileFactory.Create(0, "en");
            CompleteFixtureWorld(profile, SunnyRegulars, SunnyFinales);
            yield return SeedMultiProfile("en", profile);
            // Default selection is redrock; the raced event lives in
            // sunny — continue must return the map to the raced world.
            var snapshot = Snapshot();
            Assert.That(snapshot.progression.campaignWorldId,
                Is.EqualTo("redrock-canyon"));
            var started = app.Dispatch("campaign.start", Params(
                ("eventId", "harbor-car-circuit"),
                ("vehicleId", "car-01"), ("difficulty", "easy")));
            Assert.That(started.status, Is.EqualTo("applied"),
                "campaign.start: " + started.code);
            Step(3);
            snapshot = Snapshot();
            world.ApplyFrame(snapshot);
            RenderSoloRace(snapshot);
            Physics.SyncTransforms();
            snapshot = Pursue(snapshot, 6000);
            var guard = 0;
            while ((snapshot.soloRace == null
                    || snapshot.soloRace.result == null) && guard++ < 4000)
            {
                Step(1);
                snapshot = Snapshot();
            }
            Assert.That(snapshot.soloRace.result, Is.Not.Null,
                "the campaign race never finalized");
            Assert.That(snapshot.soloRace.result.eventId,
                Is.EqualTo("harbor-car-circuit"));
            Refresh();
            yield return PressWidget("results.continue");
            snapshot = Snapshot();
            Assert.That(snapshot.screen, Is.EqualTo("campaign"));
            Assert.That(snapshot.progression.campaignWorldId,
                Is.EqualTo("sunny-island"),
                "continue lands the map on the world the result "
                    + "belongs to");
            Refresh();
            yield return WaitForWidget<Label>(
                "campaign.world.sunny-island.selected");
        }

        [UnityTest]
        public IEnumerator ProfilesDisclaimerTracksCatalogAvailability()
        {
            // SetUp binds a full gameplay catalog: with the gated
            // *.open entries published playable, the welcome copy
            // keeps the all-playable claim.
            Refresh();
            yield return WaitForWidget<Label>("shell.disclaimer");
            Assert.That(Widget<Label>("shell.disclaimer").text,
                Is.EqualTo(ShellText.Get("shell.disclaimer", "en")));

            // Unbinding flips every *.open descriptor to
            // contentUnavailable; the profiles signature picks the
            // verdict up and repaints honestly.
            service.SetGameplayCatalog(null);
            Refresh();
            yield return WaitForWidget<Label>("shell.disclaimer");
            Assert.That(Widget<Label>("shell.disclaimer").text,
                Is.EqualTo(ShellText.Get("shell.disclaimer.unavailable", "en")),
                "Without a catalog the welcome must name the playable modes.");

            // Rebinding restores the claim — the verdict is a live
            // published fact, never a latched build-time string.
            service.SetGameplayCatalog(Gameplay(world.Catalog));
            Refresh();
            yield return WaitForWidget<Label>("shell.disclaimer");
            Assert.That(Widget<Label>("shell.disclaimer").text,
                Is.EqualTo(ShellText.Get("shell.disclaimer", "en")));
        }

        // Fails profile saves on demand — the PlayMode counterpart of
        // the EditMode TogglingSerializer, wrapping the real JSON
        // adapter so committed writes stay byte-identical.
        private sealed class TogglingSerializer : IProfileSerializer
        {
            internal bool failing;
            private readonly UnityProfileSerializer inner =
                new UnityProfileSerializer();
            public string Serialize(ProfileSave profile)
            {
                if (failing)
                    throw new InvalidOperationException("down");
                return inner.Serialize(profile);
            }
            public ProfileSave Deserialize(string json)
            {
                return inner.Deserialize(json);
            }
        }
    }
}
