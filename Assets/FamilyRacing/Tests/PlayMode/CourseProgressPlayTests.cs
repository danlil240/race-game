using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FamilyRacing.Tests
{
    // End-to-end course progress proof on real physics: the UnityMotorWorld
    // probes/sweeps actual colliders while the shared command path drives a
    // car through a two-lap trial — the longer main branch on lap one and
    // both optional shortcut branches on lap two. Finish, retry and exit
    // are exercised through the same race.* commands players and agents
    // use; checkpoints, laps and recovery come from the Core rules.
    public sealed class CourseProgressPlayTests
    {
        private const string RunId = "course-progress-play";

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
                "family-racing-progress-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            service = new FoundationService(
                new ProfileStore(directory, new UnityProfileSerializer()), "en", RunId);

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

        // Mirrors the generated graybox layout: a ground slab, perimeter
        // walls and two island blocks that split the route into a longer
        // main branch and an optional shortcut branch per leg. Both
        // branches rejoin before the next mandatory checkpoint.
        private GameObject BuildCourseSource()
        {
            var root = new GameObject("test-course");
            Box(root, "Ground", new Vector3(0f, -0.5f, 0f), new Vector3(240f, 1f, 160f));
            Box(root, "WallWest", new Vector3(-119.5f, 1.5f, 0f), new Vector3(1f, 3f, 160f));
            Box(root, "WallEast", new Vector3(119.5f, 1.5f, 0f), new Vector3(1f, 3f, 160f));
            Box(root, "WallNorth", new Vector3(0f, 1.5f, 79.5f), new Vector3(240f, 3f, 1f));
            Box(root, "WallSouth", new Vector3(0f, 1.5f, -79.5f), new Vector3(240f, 3f, 1f));
            Box(root, "IslandNorthEast", new Vector3(32f, 1f, 44f), new Vector3(22f, 2f, 14f));
            Box(root, "IslandSouthEast", new Vector3(72f, 1f, -32f), new Vector3(20f, 2f, 16f));
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

        private static TrackDefinitionAsset.Gate Gate(string id, float x, float z,
            float dirX, float dirZ)
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

        private void WireWorld(int laps = 2, Action<TrackDefinitionAsset> mutate = null)
        {
            var car = Asset<VehicleDefinitionAsset>(a =>
            {
                a.vehicleId = "car-01";
                a.bodyScale = new Vector3(1.8f, 1.1f, 3.2f);
                a.bodyColor = Color.red;
            });
            var track = Asset<TrackDefinitionAsset>(a =>
            {
                a.courseId = "graybox-01";
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
                if (mutate != null) mutate(a);
            });
            var evt = Asset<EventDefinitionAsset>(a =>
            {
                a.eventId = "practice-02";
                a.track = track;
                a.laps = laps;
                a.countdownTicks = 3;
                a.vehicleIds = new[] { "car-01" };
            });
            Inject(world, "vehicles", new[] { car });
            Inject(world, "track", track);
            Inject(world, "events", new[] { evt });
            Inject(world, "coursePrefab", courseSource);
            Inject(world, "contentVersion", "play-content-2");
        }

        private void LoadAndStart()
        {
            var catalog = world.Load();
            Assert.That(catalog, Is.Not.Null, "generated definitions must validate");
            service.SetDrivingContent(catalog);
            service.Execute(Command("profile.create", P("slot", "0", "language", "en")));
            var started = service.Execute(Command("race.start",
                P("vehicleId", "car-01", "eventId", "practice-02")));
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied),
                "race.start failed: " + started.code);
            Step(3); // countdown
            Assert.That(service.Snapshot().driving.phase, Is.EqualTo("running"));
            // Activate the course colliders like the app does each frame.
            world.ApplyFrame(service.Snapshot());
            Physics.SyncTransforms();
        }

        private void Step(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                service.StepDriving(world.MotorWorld);
                service.AdvanceTick();
            }
        }

        private GameCommand Command(string action, Dictionary<string, string> parameters,
            string commandId = null)
        {
            return new GameCommand(1, RunId, commandId ?? Guid.NewGuid().ToString("N"),
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
                    "recover", "false", "changeCamera", "false", "pause", "false",
                    "durationTicks", durationTicks.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))));
        }

        private static float WrapAngle(float radians)
        {
            while (radians > Mathf.PI) radians -= 2f * Mathf.PI;
            while (radians <= -Mathf.PI) radians += 2f * Mathf.PI;
            return radians;
        }

        // Pursue waypoints through the real command path: every control
        // cycle reads the snapshot, computes steering toward the current
        // waypoint and posts a held input.set — exactly what a player's
        // wheel does. Returns the terminal snapshot.
        private DrivingSessionState Pursue(List<Vector2> route, int maxTicks = 6000)
        {
            var wp = 0;
            var state = service.Snapshot().driving;
            for (var t = 0; t < maxTicks && wp < route.Count;)
            {
                var motor = state.motor;
                var dx = route[wp].x - motor.x;
                var dz = route[wp].y - motor.z;
                if (dx * dx + dz * dz < 49f) { wp++; continue; }
                var desired = Mathf.Atan2(dx, dz);
                var steer = Mathf.Clamp(WrapAngle(desired - motor.heading) * 3f, -1f, 1f);
                var applied = Drive(steer, 5);
                Assert.That(applied.status, Is.EqualTo(CommandResult.Applied),
                    "steering input must apply during the trial");
                Step(5);
                t += 5;
                state = service.Snapshot().driving;
                if (state.phase == "finished") break;
            }
            return state;
        }

        private static List<DrivingEvent> Events(FoundationState state, string kind)
        {
            var found = new List<DrivingEvent>();
            foreach (var ev in state.drivingEvents)
                if (ev.kind == kind) found.Add(ev);
            return found;
        }

        // Gate centres aimed 8m past their planes plus the branch
        // waypoints. Lap 1 takes the longer main branch around both
        // islands; lap 2 takes both inside shortcut branches. Every
        // branch rejoins before the next mandatory checkpoint.
        private static List<Vector2> FullRoute()
        {
            return new List<Vector2>
            {
                // --- lap 1: main branch ---
                new Vector2(0f, 33f),     // gate-1
                new Vector2(14f, 66f),    // main branch north of IslandNE
                new Vector2(63f, 55f),    // gate-2
                new Vector2(95f, -8f),    // gate-3
                new Vector2(97f, -46f),   // main branch east of IslandSE
                new Vector2(42f, -55f),   // gate-4
                new Vector2(-10f, -63f),  // swing south of the line
                new Vector2(0f, 33f),     // start crossing -> lap 1, then gate-1
                // --- lap 2: shortcut branches ---
                new Vector2(48f, 30f),    // shortcut south of IslandNE
                new Vector2(63f, 55f),    // gate-2
                new Vector2(95f, -8f),    // gate-3
                new Vector2(55f, -18f),   // shortcut west of IslandSE
                new Vector2(60f, -50f),   // rejoin below the island
                new Vector2(42f, -55f),   // gate-4
                new Vector2(-10f, -63f),  // swing south of the line
                new Vector2(0f, 33f)      // start crossing -> lap 2 -> finish
            };
        }

        [Test]
        public void TwoLapTrialCompletesThroughRealControls()
        {
            WireWorld(laps: 2);
            LoadAndStart();
            var before = service.Snapshot().savedProfile;

            var terminal = Pursue(FullRoute());
            Assert.That(terminal, Is.Not.Null);
            Assert.That(terminal.phase, Is.EqualTo("finished"),
                "the pursuit must complete both laps; pose "
                + terminal.motor.x + "," + terminal.motor.z
                + " next=" + terminal.nextCheckpoint + " lap=" + terminal.lap);
            Assert.That(terminal.finished, Is.True);
            Assert.That(terminal.lap, Is.EqualTo(2));
            Assert.That(terminal.finishTicks, Is.EqualTo(terminal.elapsedTicks));

            var snapshot = service.Snapshot();
            Assert.That(Events(snapshot, "race.lap").Count, Is.EqualTo(2));
            Assert.That(Events(snapshot, "race.finished").Count, Is.EqualTo(1),
                "exactly one terminal finish may be emitted");
            Assert.That(Events(snapshot, "checkpoint.crossed").Count,
                Is.GreaterThanOrEqualTo(9), "ordered gates across both laps");

            // Retry through race.restart gives a fresh identity inside the
            // same run without replaying side effects.
            var raceId = terminal.raceId;
            var restarted = service.Execute(Command("race.restart", null));
            Assert.That(restarted.status, Is.EqualTo(CommandResult.Applied));
            var fresh = service.Snapshot().driving;
            Assert.That(fresh.raceId, Is.Not.EqualTo(raceId));
            Assert.That(fresh.phase, Is.EqualTo("countdown"));
            Assert.That(fresh.lap, Is.EqualTo(0));
            Assert.That(fresh.finished, Is.False);
            Assert.That(Events(service.Snapshot(), "race.finished").Count,
                Is.EqualTo(1), "retry must not replay the finish");

            // Exit returns to practice; the profile's currency and unlocks
            // are untouched by the whole trial.
            var exited = service.Execute(Command("race.exit", null));
            Assert.That(exited.status, Is.EqualTo(CommandResult.Applied));
            var after = service.Snapshot();
            Assert.That(after.screen, Is.EqualTo("practice"));
            Assert.That(after.savedProfile.coins, Is.EqualTo(before.coins),
                "finishing grants no currency");
            Assert.That(after.savedProfile.unlockedVehicles,
                Is.EquivalentTo(before.unlockedVehicles),
                "finishing grants no unlocks");
        }

        [Test]
        public void ManualRecoveryOnRealCourseKeepsProgressAndClock()
        {
            WireWorld(laps: 2);
            LoadAndStart();
            // Drive to and through gate-1.
            Pursue(new List<Vector2> { new Vector2(0f, 33f) }, 1200);
            var crossed = service.Snapshot().driving;
            Assert.That(crossed.nextCheckpoint, Is.EqualTo(2),
                "gate-1 must be accepted before the recovery check");

            Step(30); // drive on past the gate
            var elapsed = service.Snapshot().driving.elapsedTicks;
            var recovered = service.Execute(Command("input.set",
                P("steering", "0", "drift", "false", "boost", "false", "gadget", "false",
                    "recover", "true", "changeCamera", "false", "pause", "false",
                    "durationTicks", "5"), "recover-cmd"));
            Assert.That(recovered.status, Is.EqualTo(CommandResult.Applied));
            Step(1);
            var state = service.Snapshot();
            var ev = Events(state, "race.recovered");
            Assert.That(ev.Count, Is.EqualTo(1));
            Assert.That(ev[0].commandId, Is.EqualTo("recover-cmd"));
            Assert.That(ev[0].detail, Is.EqualTo("manual"));
            var motor = state.driving.motor;
            Assert.That(motor.x, Is.EqualTo(0f).Within(0.5f));
            Assert.That(motor.z, Is.EqualTo(22f).Within(0.5f),
                "the respawn lands on gate-1's safe pose");
            Assert.That(motor.recovering, Is.True);
            Assert.That(state.driving.nextCheckpoint, Is.EqualTo(2),
                "respawning never advances progress");
            Assert.That(state.driving.elapsedTicks, Is.EqualTo(elapsed + 1),
                "the race clock continues through recovery");
        }

        [Test]
        public void ReverseCrossingOnRealCourseGrantsNoProgress()
        {
            WireWorld(laps: 2);
            LoadAndStart();
            Pursue(new List<Vector2> { new Vector2(0f, 40f) }, 1200);
            var crossed = service.Snapshot().driving;
            Assert.That(crossed.nextCheckpoint, Is.EqualTo(2));

            // Turn around and drive back through gate-1's plane against
            // its direction: the sweep must not award anything.
            var before = Events(service.Snapshot(), "checkpoint.crossed").Count;
            Pursue(new List<Vector2> { new Vector2(0f, 15f) }, 1200);
            var after = service.Snapshot();
            Assert.That(Events(after, "checkpoint.crossed").Count, Is.EqualTo(before),
                "re-crossing backwards must not recount gate-1");
            Assert.That(after.driving.nextCheckpoint, Is.EqualTo(2));
        }

        [Test]
        public void MalformedDefinitionsFailClosedBeforeRaceStart()
        {
            WireWorld(mutate: track =>
            {
                // Duplicate gate ids are a malformed course: the asset
                // must refuse conversion and the catalog must never build.
                track.gates[1].id = "start";
            });
            LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex("checkpoint\\.duplicate"));
            var catalog = world.Load();
            Assert.That(catalog, Is.Null,
                "a malformed course must fail validation inside Load");
            service.Execute(Command("profile.create", P("slot", "0", "language", "en")));
            var started = service.Execute(Command("race.start",
                P("vehicleId", "car-01", "eventId", "practice-02")));
            Assert.That(started.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(started.code, Is.EqualTo("contentUnavailable"),
                "race.start stays unavailable until content validates");
        }
    }
}
