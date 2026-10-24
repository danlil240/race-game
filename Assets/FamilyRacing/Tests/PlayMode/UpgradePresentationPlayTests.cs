using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
    // M5 Task 8 presentation proof: the garage shows exact authored
    // level/cost/effect per vehicle and category, a buy button commits
    // the same purchase the command does, installed-part indicators and
    // equipped cosmetics bind onto the racer's visual without touching
    // stats or collision, and profile/vehicle swaps re-scope the whole
    // surface honestly.
    [UnityPlatform(RuntimePlatform.WindowsEditor, RuntimePlatform.OSXEditor,
        RuntimePlatform.LinuxEditor)]
    public sealed class UpgradePresentationPlayTests
    {
        private const string RunId = "upgrade-play";
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
                "fr-upgrade-ui-" + Guid.NewGuid().ToString("N"));
            store = new ProfileStore(directory, new UnityProfileSerializer());
            service = new FoundationService(store, "en", RunId);

            hostGo = new GameObject("upgrade-host");
            hostGo.SetActive(false);
            app = hostGo.AddComponent<FoundationApp>();
            Inject(app, "service", service);
            Inject(app, "runId", RunId);

            courseSource = BuildCourseSource();
            worldGo = new GameObject("upgrade-world");
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

            shellGo = new GameObject("upgrade-shell");
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

        // --- fixture -----------------------------------------------------------

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
            var root = new GameObject("upgrade-course");
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
                { circuit, motoCircuit, finale, motoFinale });
            Inject(world, "coursePrefab", courseSource);
            Inject(world, "contentVersion", "upgrade-play-content-1");
        }

        // Two regular events (car + motorcycle) and both class finales —
        // the smallest campaign structure GameplayCatalog validates.
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
            var categories = new[]
            {
                new KeyValuePair<string, string[]>("acceleration",
                    new[] { "acceleration" }),
                new KeyValuePair<string, string[]>("handling",
                    new[] { "steeringRate", "grip" }),
                new KeyValuePair<string, string[]>("boost",
                    new[] { "boostAcceleration", "boostMaxSpeed" })
            };
            foreach (var entry in categories)
            {
                var category = new UpgradeCategoryDefinition
                {
                    id = entry.Key, maxLevel = 3
                };
                category.prices.Add(60);
                category.prices.Add(120);
                category.prices.Add(180);
                category.tuningFields.AddRange(entry.Value);
                economy.upgradeCategories.Add(category);
            }
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

        // The multi-frame press a real pointer performs: down, frames of
        // refresh ticks, then up. Pointer capture must survive — that is
        // exactly the promise the signature-gated garage rebuild makes.
        private static void PointerDown(Button button, int fingerId = 0)
        {
            var touch = new Touch
            {
                fingerId = fingerId,
                position = button.worldBound.center,
                phase = TouchPhase.Began
            };
            using (var down = PointerDownEvent.GetPooled(touch,
                EventModifiers.None))
            {
                down.target = button;
                button.SendEvent(down);
            }
        }

        private static void PointerUp(Button button, int fingerId = 0)
        {
            var touch = new Touch
            {
                fingerId = fingerId,
                position = button.worldBound.center,
                phase = TouchPhase.Ended
            };
            using (var up = PointerUpEvent.GetPooled(touch,
                EventModifiers.None))
            {
                up.target = button;
                button.SendEvent(up);
            }
        }

        // Seeds a profile straight to disk (slot caches bind at service
        // construction, so the service is rebuilt around it) and lands
        // on the garage's home screen.
        private IEnumerator SeedProfileAndGarage(int coins,
            params string[] ownedCosmetics)
        {
            var profile = ProfileFactory.Create(0, "en");
            profile.coins = coins;
            foreach (var cosmetic in ownedCosmetics)
                profile.ownedCosmetics.Add(cosmetic);
            yield return SeedProfileAndGarage(profile);
        }

        private IEnumerator SeedProfileAndGarage(ProfileSave profile)
        {
            store.Save(profile);
            service = new FoundationService(store, "en", RunId);
            Inject(app, "service", service);
            service.SetDrivingContent(world.Catalog);
            service.SetGameplayCatalog(Gameplay(world.Catalog));
            service.SetDrivingSimulationFactory(world.CreateSimulation);
            Assert.That(app.Dispatch("profile.select",
                Params(("slot", "0"))).status, Is.EqualTo("applied"));
            Assert.That(app.Dispatch("ui.navigate",
                Params(("screen", "practice"))).status,
                Is.EqualTo("applied"));
            Refresh();
            yield return WaitForWidget<VisualElement>("garage.coins");
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

        // --- tests -------------------------------------------------------------

        [UnityTest]
        public IEnumerator GarageRowsShowExactLevelCostAndEffect()
        {
            yield return SeedProfileAndGarage(120);

            var coins = Widget<Label>("garage.coins");
            Assert.That(coins.text, Does.Contain("120"));

            var level = Widget<Label>(
                "garage.car-01.acceleration.level");
            Assert.That(level, Is.Not.Null,
                "the garage must publish a level row per vehicle/category");
            Assert.That(level.text, Does.Contain("0"));
            var cost = Widget<Label>("garage.car-01.acceleration.cost");
            Assert.That(cost.text, Does.Contain("60"),
                "level 1 must quote the authored 60-coin price");
            var effect = Widget<Label>(
                "garage.car-01.acceleration.effect");
            Assert.That(effect.text, Does.Contain("5"),
                "the authored 0.05 step must read as a +5% effect");
            var installed = Widget<VisualElement>(
                "garage.car-01.acceleration.installed");
            Assert.That(installed, Is.Not.Null);

            // Locked tiers are marked, not silently shown as ownable.
            var lockedTag = Widget<Label>("garage.car-02.locked");
            Assert.That(lockedTag, Is.Not.Null,
                "a locked vehicle must be marked in the garage");
            Assert.That(lockedTag.style.display.value,
                Is.EqualTo(DisplayStyle.Flex));

            yield return WaitForWidget<Button>(
                "garage.car-01.acceleration.buy");
            Press(Widget<Button>("garage.car-01.acceleration.buy"));
            var state = Snapshot();
            Assert.That(state.savedProfile.coins, Is.EqualTo(60),
                "the buy button must run the same purchase the command does");
            Assert.That(state.savedProfile.vehicleUpgrades.Count,
                Is.EqualTo(1));
            Refresh();
            Assert.That(Widget<Label>("garage.car-01.acceleration.level")
                .text, Does.Contain("1"));
            Assert.That(Widget<Label>("garage.car-01.acceleration.cost")
                .text, Does.Contain("120"),
                "the next level quotes its own authored price");
            Assert.That(Widget<Label>("garage.coins").text,
                Does.Contain("60"));
        }

        [UnityTest]
        public IEnumerator PurchasedLevelsRenderInstalledPartsInRace()
        {
            yield return SeedProfileAndGarage(500);
            Assert.That(app.Dispatch("upgrade.purchase", Params(
                ("purchaseId", "play-1"), ("vehicleId", "car-01"),
                ("category", "acceleration"), ("expectedLevel", "1")))
                .status, Is.EqualTo("applied"));
            Assert.That(app.Dispatch("upgrade.purchase", Params(
                ("purchaseId", "play-2"), ("vehicleId", "car-01"),
                ("category", "boost"), ("expectedLevel", "1")))
                .status, Is.EqualTo("applied"));

            var started = app.Dispatch("solo.start", Params(
                ("mode", "campaign"), ("eventId", "harbor-car-circuit"),
                ("vehicleId", "car-01")));
            Assert.That(started.status, Is.EqualTo("applied"),
                "solo.start failed: " + started.code);
            var snapshot = Snapshot();
            world.ApplyFrame(snapshot);
            RenderSoloRace(snapshot);
            var view = world.RacerPresentation(
                snapshot.soloRace.participantId);
            Assert.That(view, Is.Not.Null,
                "the local racer must own a presentation view");
            Assert.That(view.UpgradeLevelShown("acceleration"),
                Is.EqualTo(1),
                "the purchased level must bind an installed part");
            Assert.That(view.UpgradeLevelShown("boost"), Is.EqualTo(1));
            Assert.That(view.UpgradeLevelShown("handling"), Is.EqualTo(0),
                "unbought categories show no part");
            Assert.That(
                view.transform.Find("LeanPivot/upgrade.acceleration.1"),
                Is.Not.Null,
                "the installed part must be a named child visual");
            Assert.That(
                view.transform.Find("LeanPivot/upgrade.boost.1"),
                Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator EquippedCosmeticsBindVisuallyWithoutStats()
        {
            yield return SeedProfileAndGarage(500, "livery-rival-car",
                "accessory-flag");
            Assert.That(app.Dispatch("cosmetic.equip", Params(
                ("slot", "accessory"), ("cosmeticId", "accessory-flag"),
                ("equipped", "true")))
                .status, Is.EqualTo("applied"));
            // The unowned cosmetic can never equip — cosmetics never
            // bypass ownership.
            Assert.That(app.Dispatch("cosmetic.equip", Params(
                ("slot", "livery"), ("cosmeticId", "livery-rival-motorcycle"),
                ("equipped", "true"))).status, Is.EqualTo("rejected"));

            var started = app.Dispatch("solo.start", Params(
                ("mode", "campaign"), ("eventId", "harbor-car-circuit"),
                ("vehicleId", "car-01")));
            Assert.That(started.status, Is.EqualTo("applied"));
            var snapshot = Snapshot();
            world.ApplyFrame(snapshot);
            RenderSoloRace(snapshot);
            var view = world.RacerPresentation(
                snapshot.soloRace.participantId);
            Assert.That(view.CosmeticShown("accessory-flag"), Is.True,
                "the equipped cosmetic must bind a named visual");
            Assert.That(view.CosmeticShown("livery-rival-car"), Is.False,
                "an unequipped cosmetic stays off the visual");
            Assert.That(
                view.transform.Find("LeanPivot/cosmetic.accessory-flag"),
                Is.Not.Null);
            // Cosmetics never touch collision or stats: the visual
            // carries no collider anywhere in its hierarchy.
            Assert.That(view.GetComponentInChildren<Collider>(),
                Is.Null,
                "cosmetic visuals must never add collision");
        }

        [UnityTest]
        public IEnumerator ProfileAndVehicleSwapsRescopeTheSurface()
        {
            // Slot 0 rich, slot 1 fresh — the garage must follow the
            // selected profile exactly.
            var rich = ProfileFactory.Create(0, "en");
            rich.coins = 500;
            rich.vehicleUpgrades.Add(new VehicleUpgrade
            {
                vehicleId = "car-01", acceleration = 2
            });
            var fresh = ProfileFactory.Create(1, "en");
            store.Save(fresh);
            yield return SeedProfileAndGarage(rich);
            yield return WaitForWidget<Label>(
                "garage.car-01.acceleration.level");
            Assert.That(Widget<Label>(
                    "garage.car-01.acceleration.level").text,
                Does.Contain("2"));
            Assert.That(Widget<Label>(
                    "garage.motorcycle-01.acceleration.level").text,
                Does.Contain("0"),
                "a different vehicle's row is scoped to its own levels");

            Assert.That(app.Dispatch("profile.select",
                Params(("slot", "1"))).status, Is.EqualTo("applied"));
            Assert.That(app.Dispatch("ui.navigate",
                Params(("screen", "practice"))).status,
                Is.EqualTo("applied"));
            Refresh();
            yield return WaitForWidget<Label>(
                "garage.car-01.acceleration.level");
            Assert.That(Widget<Label>(
                    "garage.car-01.acceleration.level").text,
                Does.Contain("0"),
                "a profile swap must re-scope the garage rows");
            Assert.That(Widget<Label>("garage.coins").text,
                Does.Contain("0"));
        }

        [UnityTest]
        public IEnumerator IdenticalFactsKeepGarageWidgetsAlive()
        {
            yield return SeedProfileAndGarage(500);
            yield return WaitForWidget<Button>(
                "garage.car-01.acceleration.buy");
            var buy = Widget<Button>(
                "garage.car-01.acceleration.buy");
            var installed = Widget<VisualElement>(
                "garage.car-01.acceleration.installed");

            // A click spans frames: PointerDown on frame N, PointerUp on
            // frame N+k. A rebuild-per-refresh destroys the widget
            // mid-click and drops pointer capture — identical facts
            // must keep the SAME elements alive.
            Refresh();
            yield return null;
            Refresh();
            yield return null;
            Assert.That(
                Widget<Button>("garage.car-01.acceleration.buy"),
                Is.SameAs(buy),
                "unchanged facts must not rebuild the garage rows");
            Assert.That(
                Widget<VisualElement>(
                    "garage.car-01.acceleration.installed"),
                Is.SameAs(installed));

            // The press a real finger performs commits the purchase —
            // the buy button runs the same upgrade.purchase command.
            Press(buy);
            yield return null;
            Assert.That(Snapshot().savedProfile
                    .vehicleUpgrades[0].acceleration,
                Is.EqualTo(1),
                "the buy button must commit the level-1 purchase");

            // A committed purchase is a facts change: the row rebuilds
            // exactly once and shows the new level.
            Refresh();
            yield return WaitForWidget<Button>(
                "garage.car-01.acceleration.buy");
            var rebuilt = Widget<Button>(
                "garage.car-01.acceleration.buy");
            Assert.That(rebuilt, Is.Not.Null);
            Assert.That(rebuilt, Is.Not.SameAs(buy),
                "a changed fact must rebuild the affected garage tree");
            Assert.That(Widget<Label>(
                    "garage.car-01.acceleration.level").text,
                Does.Contain("1"));

            // Publication keeps ticking after the purchase (save status
            // and friends), so resolve the CURRENT widget once facts
            // settle: the same instance must survive refresh ticks and
            // sit under its own bounds before a real press can land
            // (flex wrap + advanced text settle over a few layouts).
            const string buyName = "garage.car-01.acceleration.buy";
            Button live = null;
            var diag = "not polled";
            var settledUntil = Time.unscaledTime + 8f;
            var settled = false;
            while (Time.unscaledTime < settledUntil && !settled)
            {
                var w = Widget<Button>(buyName);
                if (w == null || w.panel == null
                    || w.worldBound.width <= 0f)
                {
                    live = null;
                    diag = "widget=" + (w == null ? "null" : "panel="
                        + (w.panel != null) + " bound=" + w.worldBound);
                    yield return null;
                    continue;
                }
                var hit = w.panel.Pick(w.worldBound.center);
                var inside = false;
                for (var p = hit; p != null; p = p.hierarchy.parent)
                    if (ReferenceEquals(p, w)) { inside = true; break; }
                var same = ReferenceEquals(w, live);
                diag = "bound=" + w.worldBound + " picked="
                    + (hit == null ? "null" : hit.name)
                    + " inside=" + inside + " sameInstance=" + same;
                if (!inside) live = null;
                else if (!same) live = w;
                else settled = true;
                yield return null;
            }
            Assert.That(settled, Is.True,
                "a settled, pickable buy button; " + diag);
            Assert.That(live, Is.Not.SameAs(buy),
                "a changed fact must rebuild the affected garage tree");
            Assert.That(Widget<Label>(
                    "garage.car-01.acceleration.level").text,
                Does.Contain("1"));

            // The settled widget still commits the next purchase — the
            // multi-frame press holds pointer capture only because
            // nothing rebuilds while facts stay identical.
            var clicks = 0;
            live.clicked += () => clicks++;
            PointerDown(live);
            yield return null;
            Assert.That(Widget<Button>(buyName), Is.SameAs(live),
                "a held pointer must keep the same widget across frames");
            PointerUp(live);
            yield return null;
            Assert.That(clicks, Is.EqualTo(1),
                "the pointer press never reached the rebuilt button");
            Assert.That(Snapshot().savedProfile
                    .vehicleUpgrades[0].acceleration,
                Is.EqualTo(2),
                "the rebuilt buy button still commits the purchase");
        }
    }
}
