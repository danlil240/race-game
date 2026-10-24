using System;
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
using Object = UnityEngine.Object;

namespace FamilyRacing.Tests
{
    // M5 Task 4 PlayMode proof: the local modes run through the real
    // Unity physics path — UnityMotorWorld probes/sweeps actual
    // colliders — while the shared command surface drives the race.
    // Campaign/Quick Race seat the shared four-racer coordinator and
    // render through the same racer-view path LAN uses; Time Trial is
    // the lone DrivingSession on the solo visual; pause freezes the
    // simulated pose and clock.
    public sealed class SoloRacePlayTests
    {
        private const string RunId = "solo-race-play";

        private string directory;
        private FoundationService service;
        private GameObject host;
        private DrivingWorld world;
        private GameObject courseSource;
        private GameObject cameraObject;
        private readonly List<Object> assets = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "family-racing-solo-play-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            service = new FoundationService(
                new ProfileStore(directory, new UnityProfileSerializer()),
                "en", RunId);

            host = new GameObject("driving-world-host");
            host.SetActive(false);
            world = host.AddComponent<DrivingWorld>();
            courseSource = BuildCourseSource();
            cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.AddComponent<Camera>();
            host.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null) Object.Destroy(host);
            if (courseSource != null) Object.Destroy(courseSource);
            if (cameraObject != null) Object.Destroy(cameraObject);
            foreach (var asset in assets)
                if (asset != null) Object.Destroy(asset);
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (Exception) { }
        }

        private T Asset<T>(Action<T> configure) where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>();
            configure(asset);
            assets.Add(asset);
            return asset;
        }

        private static void Inject(object target, string field, object value)
        {
            target.GetType().GetField(field,
                BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
        }

        // A flat course with perimeter walls so the arcade sim's probes
        // and sweeps land on real colliders.
        private GameObject BuildCourseSource()
        {
            var root = new GameObject("test-course");
            Box(root, "Ground", new Vector3(0f, -0.5f, 0f), new Vector3(240f, 1f, 160f));
            Box(root, "WallWest", new Vector3(-119.5f, 1.5f, 0f), new Vector3(1f, 3f, 160f));
            Box(root, "WallEast", new Vector3(119.5f, 1.5f, 0f), new Vector3(1f, 3f, 160f));
            Box(root, "WallNorth", new Vector3(0f, 1.5f, 79.5f), new Vector3(240f, 3f, 1f));
            Box(root, "WallSouth", new Vector3(0f, 1.5f, -79.5f), new Vector3(240f, 3f, 1f));
            root.SetActive(false);
            return root;
        }

        private static void Box(GameObject parent, string name,
            Vector3 position, Vector3 scale)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent.transform, false);
            box.transform.localPosition = position;
            box.transform.localScale = scale;
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
                recoverPosition = new Vector3(x - dirX * 3f, 0f, z - dirZ * 3f),
                recoverHeadingDegrees = Mathf.Atan2(dirX, dirZ) * Mathf.Rad2Deg
            };
        }

        private static TrackDefinitionAsset.RoutePoint Route(float x, float z)
        {
            return new TrackDefinitionAsset.RoutePoint { x = x, z = z, radius = 7f };
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

        // One track, one circuit regular, one class finale and one time
        // event — the minimum campaign structure the gameplay catalog
        // validates, plus the four-slot grid and AI route the shared
        // coordinator needs.
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
                // Same drive-through shape the offline fixture proves:
                // the finish approach swings south of the line, then
                // re-enters through the gate centre.
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
            Inject(world, "vehicles", new[] { car1, car2 });
            Inject(world, "track", track);
            Inject(world, "events", new[] { circuit, time, finale });
            Inject(world, "coursePrefab", courseSource);
            Inject(world, "contentVersion", "solo-play-content-1");
        }

        // The Core gameplay catalog bound over the world's own loaded
        // driving content — same TryCreate the authored asset runs.
        private GameplayCatalog Gameplay(DrivingContentCatalog driving)
        {
            var campaign = new CampaignDefinition
            {
                worldId = "sunny-island",
                nameEn = "Sunny Island",
                nameHe = "האי השמשי",
                requiredEventCompletions = 1,
                requiredRegionalStars = 1
            };
            campaign.courseIds.Add("harbor-hop");
            campaign.regularEventIds.Add("harbor-car-circuit");
            campaign.finales.Add(new FinaleDefinition
            {
                eventId = "sunny-car-finale",
                classId = "car",
                rivalId = "rival-car",
                rewardVehicleId = "car-02"
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
            var category = new UpgradeCategoryDefinition
            {
                id = "acceleration", maxLevel = 3
            };
            category.prices.Add(60);
            category.prices.Add(120);
            category.prices.Add(180);
            category.tuningFields.Add("acceleration");
            economy.upgradeCategories.Add(category);
            var ok = GameplayCatalog.TryCreate(driving, campaign, bounties,
                economy, new GadgetDefinitionCatalog(),
                new List<AiDifficultyDefinition>
                {
                    new AiDifficultyDefinition { id = "easy" },
                    new AiDifficultyDefinition { id = "medium" },
                    new AiDifficultyDefinition { id = "hard" }
                }, out var gameplay, out var error);
            Assert.That(ok, Is.True, "gameplay catalog: " + error);
            return gameplay;
        }

        private void LoadAndStart(string mode, string eventId,
            string vehicleId = "car-01")
        {
            var catalog = world.Load();
            Assert.That(catalog, Is.Not.Null,
                "generated definitions must validate");
            service.SetDrivingContent(catalog);
            service.SetGameplayCatalog(Gameplay(catalog));
            service.SetDrivingSimulationFactory(world.CreateSimulation);
            service.Execute(Command("profile.create",
                P("slot", "0", "language", "en")));
            var started = service.Execute(Command("solo.start",
                P("mode", mode, "eventId", eventId,
                  "vehicleId", vehicleId)));
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied),
                "solo.start failed: " + started.code);
            Step(3); // authored countdown
            var snapshot = service.Snapshot();
            // Course colliders go live like the app's render pass does,
            // before the bodies may move.
            world.ApplyFrame(snapshot);
            RenderSoloRace(snapshot);
            Physics.SyncTransforms();
        }

        // The app's solo-race render path: one roster row for the local
        // human so cosmetics resolve like a LAN local racer.
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
                    slot = 0,
                    occupied = true,
                    connected = true,
                    controlSource = "local",
                    avatar = snapshot.draftProfile.avatar ?? "",
                    colorIndex = snapshot.draftProfile.colorIndex
                });
            world.ApplyRaceFrame(snapshot.soloRace.race, 1f, null, roster);
        }

        private void Step(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                service.StepSolo(world.MotorWorld);
                service.AdvanceTick();
            }
        }

        private GameCommand Command(string action,
            Dictionary<string, string> parameters, string commandId = null)
        {
            return new GameCommand(1, RunId,
                commandId ?? Guid.NewGuid().ToString("N"),
                "local", action, parameters);
        }

        private static Dictionary<string, string> P(params string[] keyValues)
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i + 1 < keyValues.Length; i += 2)
                map[keyValues[i]] = keyValues[i + 1];
            return map;
        }

        private CommandResult Drive(float steering, int durationTicks = 5)
        {
            return service.Execute(Command("input.set",
                P("steering", steering.ToString("0.###",
                        System.Globalization.CultureInfo.InvariantCulture),
                    "drift", "false", "boost", "false", "gadget", "false",
                    "recover", "false", "changeCamera", "false",
                    "pause", "false",
                    "durationTicks", durationTicks.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))));
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

        // The drive-through loop the AI route authors: pursue each
        // waypoint through the shared input.set command path.
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
                Assert.That(applied.status, Is.EqualTo(CommandResult.Applied),
                    "steering input must apply mid-race: " + applied.code);
                Step(5);
                t += 5;
                state = service.Snapshot();
            }
            return state;
        }

        // --- named task tests ------------------------------------------

        [UnityTest]
        public System.Collections.IEnumerator QuickRaceSeatsFourRacersAndRendersThroughSharedWorld()
        {
            WireWorld();
            LoadAndStart("quickRace", "harbor-car-circuit");
            var snapshot = service.Snapshot();
            // The shared coordinator publishes a four-racer race — one
            // human slot plus three AI — and the durable reward context
            // bound on the start boundary.
            Assert.That(snapshot.soloRace, Is.Not.Null);
            Assert.That(snapshot.soloRace.mode, Is.EqualTo("quickRace"));
            Assert.That(snapshot.soloRace.race.racers.Count,
                Is.EqualTo(NetworkSettings.ParticipantCapacity));
            Assert.That(snapshot.savedProfile.pendingRaceContexts.Count,
                Is.EqualTo(1));
            Assert.That(snapshot.savedProfile.pendingRaceContexts[0].raceId,
                Is.EqualTo(snapshot.soloRace.raceId));
            var humans = 0;
            foreach (var racer in snapshot.soloRace.race.racers)
                if (racer.attribution == "human") humans++;
            Assert.That(humans, Is.EqualTo(1));
            // The world renders the local race through the shared
            // racer-view path: one fact per authoritative racer, all
            // marked authoritative (never predicted or interpolated).
            RenderSoloRace(snapshot);
            Assert.That(world.RacerFacts.Count,
                Is.EqualTo(NetworkSettings.ParticipantCapacity));
            foreach (var fact in world.RacerFacts)
                Assert.That(fact.poseSource, Is.EqualTo("authoritative"));

            // Drive the local human to the finish through real colliders
            // and the shared command path, then run the race out: the
            // AI seats finish or the DNF deadline converts them — one
            // immutable format-2 result lands either way.
            snapshot = Pursue(snapshot, 6000);
            var guard = 0;
            while (snapshot.soloRace.result == null && guard++ < 4000)
            {
                Step(1);
                snapshot = service.Snapshot();
            }
            Assert.That(snapshot.soloRace.result, Is.Not.Null,
                "race never finalized");
            var result = snapshot.soloRace.result;
            Assert.That(result.formatVersion, Is.EqualTo(2));
            Assert.That(result.mode, Is.EqualTo("quickRace"));
            Assert.That(result.racers.Count,
                Is.EqualTo(NetworkSettings.ParticipantCapacity));
            Assert.That(HumanFinished(snapshot), Is.True,
                "the scripted driver must finish the lap");
            foreach (var row in result.racers)
                Assert.That(row.metrics, Is.Not.Null,
                    "every result row carries authoritative metrics");
            yield return null;
        }

        [UnityTest]
        public System.Collections.IEnumerator TimeTrialRendersSingleRacerNoItemsNoRewards()
        {
            WireWorld();
            LoadAndStart("timeTrial", "harbor-car-time");
            var snapshot = service.Snapshot();
            Assert.That(snapshot.soloRace.mode, Is.EqualTo("timeTrial"));
            Assert.That(snapshot.soloRace.trial, Is.Not.Null);
            Assert.That(snapshot.soloRace.race, Is.Null);
            // No reward context: a trial is not a progression race.
            Assert.That(snapshot.savedProfile.pendingRaceContexts.Count,
                Is.EqualTo(0));
            // The solo visual renders the lone session — exactly one
            // racer fact, never a four-racer roster.
            world.ApplyFrame(snapshot);
            Assert.That(world.RacerFacts.Count, Is.EqualTo(1));

            snapshot = Pursue(snapshot, 6000);
            Assert.That(snapshot.soloRace.trial.finished, Is.True,
                "the scripted driver must finish the lap");
            Step(1);
            snapshot = service.Snapshot();
            Assert.That(snapshot.soloRace.result, Is.Not.Null);
            Assert.That(snapshot.soloRace.result.formatVersion,
                Is.EqualTo(2));
            Assert.That(snapshot.soloRace.result.mode,
                Is.EqualTo("timeTrial"));
            Assert.That(snapshot.soloRace.result.racers.Count,
                Is.EqualTo(1));
            Assert.That(snapshot.soloRace.result.completionCoins,
                Is.EqualTo(0));
            Assert.That(snapshot.soloRace.result.racers[0].coinsAwarded,
                Is.EqualTo(0));
            yield return null;
        }

        [UnityTest]
        public System.Collections.IEnumerator PausedRaceHoldsSimulatedPoseAndClock()
        {
            WireWorld();
            LoadAndStart("quickRace", "harbor-car-circuit");
            Step(10);
            var snapshot = service.Snapshot();
            Assert.That(snapshot.soloRace.race.phase, Is.EqualTo("running"));
            var motor = HumanMotor(snapshot);
            var x = motor.x;
            var z = motor.z;
            var elapsed = snapshot.soloRace.race.elapsedTicks;

            var paused = service.Execute(Command("pause.toggle", null));
            Assert.That(paused.status, Is.EqualTo(CommandResult.Applied),
                "pause.toggle: " + paused.code);
            Step(20);
            snapshot = service.Snapshot();
            Assert.That(snapshot.soloRace.race.elapsedTicks,
                Is.EqualTo(elapsed),
                "elapsed race clock must not advance while paused");
            motor = HumanMotor(snapshot);
            Assert.That(motor.x, Is.EqualTo(x).Within(0.001f));
            Assert.That(motor.z, Is.EqualTo(z).Within(0.001f),
                "the simulated pose is frozen at the pause boundary");

            var resumed = service.Execute(Command("pause.toggle", null));
            Assert.That(resumed.status, Is.EqualTo(CommandResult.Applied),
                "resume: " + resumed.code);
            Step(1);
            snapshot = service.Snapshot();
            Assert.That(snapshot.soloRace.race.elapsedTicks,
                Is.EqualTo(elapsed + 1));
            yield return null;
        }
    }
}
