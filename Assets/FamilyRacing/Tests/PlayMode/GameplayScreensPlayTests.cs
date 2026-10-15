using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
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
            Press(button);
            Refresh();
            yield return null;
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
                yield return WaitForWidget<Button>(button);
                Press(Widget<Button>(button));
                Assert.That(Snapshot().screen, Is.EqualTo(screen),
                    "the " + button + " button must dispatch its gated "
                        + "*.open command");
                Refresh();
                yield return WaitForWidget<VisualElement>(title);
                yield return WaitForWidget<Button>("loop.back");
                Press(Widget<Button>("loop.back"));
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
            yield return WaitForWidget<Button>("campaign.start");
            Press(Widget<Button>("campaign.start"));

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
            yield return WaitForWidget<Button>("quick.start");
            Press(Widget<Button>("quick.start"));

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
            Press(Widget<Button>("trial.start"));
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

            yield return WaitForWidget<Button>("results.retry");
            Press(Widget<Button>("results.retry"));
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

            yield return WaitForWidget<Button>("results.continue");
            Press(Widget<Button>("results.continue"));
            snapshot = Snapshot();
            Assert.That(snapshot.soloRace, Is.Null,
                "continue retires the finished session");
            Assert.That(snapshot.screen, Is.EqualTo("campaign"),
                "a campaign result continues to the campaign map");
            Refresh();
            yield return WaitForWidget<Label>("campaign.title");
        }
    }
}
