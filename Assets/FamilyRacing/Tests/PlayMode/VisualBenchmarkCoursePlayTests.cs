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
using Object = UnityEngine.Object;

namespace FamilyRacing.Tests
{
    // M4 Task-3 dressed-course proof on real physics: the actual
    // benchmark content — the visual-benchmark definition's
    // sunny-island course visual, the shared graybox-01 track and the
    // generated vehicle/event assets — is driven through the shared
    // command path. The motor's probes/sweeps see the nested
    // GrayboxCourse colliders exactly, so both shortcut branches, the
    // landmark ramp jump and the recovery poses must behave identically
    // to the undressed course.
    public sealed class VisualBenchmarkCoursePlayTests
    {
        private const string RunId = "sunny-island-play";
        private const string DrivingDir =
            "Assets/FamilyRacing/Generated/Driving";
        private const string DefinitionPath =
            "Assets/FamilyRacing/Generated/VisualBenchmark/" +
            "visual-benchmark-01.asset";

        private string directory;
        private FoundationService service;
        private GameObject host;
        private DrivingWorld world;
        private GameObject cameraObject;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "family-racing-sunny-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            service = new FoundationService(
                new ProfileStore(directory, new UnityProfileSerializer()),
                "en", RunId);
            host = new GameObject("sunny-island-host");
            host.SetActive(false);
            world = host.AddComponent<DrivingWorld>();
            cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.AddComponent<Camera>();
            host.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null) Object.Destroy(host);
            if (cameraObject != null) Object.Destroy(cameraObject);
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, true);
            }
            catch (Exception) { }
        }

        private static void Inject(object target, string field,
            object value)
        {
            target.GetType().GetField(field,
                BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(target, value);
        }

        // The real benchmark contract: the definition's own
        // courseVisualPrefab (the dressed sunny-island root), its shared
        // graybox-01 track and the generated class/event assets — the
        // same wiring the VisualBenchmark scene carries.
        private void WireBenchmarkCourse()
        {
            VisualBenchmarkDefinitionAsset definition = null;
            EventDefinitionAsset evt = null;
            VehicleDefinitionAsset car = null;
            VehicleDefinitionAsset moto = null;
#if UNITY_EDITOR
            definition = UnityEditor.AssetDatabase
                .LoadAssetAtPath<VisualBenchmarkDefinitionAsset>(
                    DefinitionPath);
            evt = UnityEditor.AssetDatabase
                .LoadAssetAtPath<EventDefinitionAsset>(
                    DrivingDir + "/practice-02.asset");
            car = UnityEditor.AssetDatabase
                .LoadAssetAtPath<VehicleDefinitionAsset>(
                    DrivingDir + "/car-01.asset");
            moto = UnityEditor.AssetDatabase
                .LoadAssetAtPath<VehicleDefinitionAsset>(
                    DrivingDir + "/motorcycle-01.asset");
#endif
            Assert.That(definition, Is.Not.Null,
                "the benchmark definition must exist — run "
                + "VisualBenchmarkSetup");
            string error;
            Assert.That(definition.Validate(out error), Is.True, error);
            Assert.That(definition.courseVisualPrefab, Is.Not.Null);
            Assert.That(definition.courseVisualPrefab.name,
                Is.EqualTo("sunny-island-benchmark-01"),
                "the world must load the dressed sunny-island visual "
                + "root");
            Assert.That(evt, Is.Not.Null, "practice-02 must exist");
            Inject(world, "vehicles", new[] { car, moto });
            Inject(world, "track", definition.sourceTrack);
            Inject(world, "events", new[] { evt });
            Inject(world, "coursePrefab", definition.courseVisualPrefab);
            Inject(world, "contentVersion", definition.presentationVersion);
        }

        private void LoadAndStart(string vehicleId = "car-01")
        {
            var catalog = world.Load();
            Assert.That(catalog, Is.Not.Null,
                "benchmark definitions must validate");
            Assert.That(catalog.Version, Is.EqualTo("visual-1"));
            Assert.That(world.CourseVisualId,
                Is.EqualTo("sunny-island-benchmark-01"),
                "the published course-visual identity is the dressed "
                + "root's stable id");
            service.SetDrivingContent(catalog);
            service.Execute(Command("profile.create",
                P("slot", "0", "language", "en")));
            var started = service.Execute(Command("race.start",
                P("vehicleId", vehicleId, "eventId", "practice-02")));
            Assert.That(started.status,
                Is.EqualTo(CommandResult.Applied),
                "race.start failed: " + started.code);
            Step(90); // authored countdown on practice-02
            Assert.That(service.Snapshot().driving.phase,
                Is.EqualTo("running"));
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

        private GameCommand Command(string action,
            Dictionary<string, string> parameters, string commandId = null)
        {
            return new GameCommand(1, RunId,
                commandId ?? Guid.NewGuid().ToString("N"),
                "local", action, parameters);
        }

        private static Dictionary<string, string> P(
            params string[] keyValues)
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i + 1 < keyValues.Length; i += 2)
                map[keyValues[i]] = keyValues[i + 1];
            return map;
        }

        private CommandResult Drive(float steering, bool drift = false,
            bool boost = false, int durationTicks = 5)
        {
            return service.Execute(Command("input.set",
                P("steering", steering.ToString("0.###",
                        System.Globalization.CultureInfo.InvariantCulture),
                    "drift", drift ? "true" : "false",
                    "boost", boost ? "true" : "false", "gadget", "false",
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

        // Waypoint pursuit through the real command path (same shape as
        // VehicleClassPlayTests): observed position only, held input.set
        // bounds, a bounded player-like recovery when truly wall-pinned.
        // Returns the count of reached waypoints.
        private int Pursue(List<Vector2> route, int maxTicks = 9000)
        {
            var wp = 0;
            var stuckTicks = 0;
            var recoveries = 0;
            var state = service.Snapshot().driving;
            var lastX = state.motor.x;
            var lastZ = state.motor.z;
            for (var t = 0; t < maxTicks && wp < route.Count;)
            {
                var motor = state.motor;
                var dx = route[wp].x - motor.x;
                var dz = route[wp].y - motor.z;
                if (dx * dx + dz * dz < 49f) { wp++; continue; }
                if (stuckTicks >= 90 && recoveries < 3)
                {
                    recoveries++;
                    stuckTicks = 0;
                    var unstick = service.Execute(Command("input.set",
                        P("steering", "0", "drift", "false", "boost",
                            "false", "gadget", "false", "recover", "true",
                            "changeCamera", "false", "pause", "false",
                            "durationTicks", "5"), "unstick-" + recoveries));
                    Assert.That(unstick.status,
                        Is.EqualTo(CommandResult.Applied),
                        "a stuck vehicle may press R like a player");
                    Step(3);
                    t += 3;
                    state = service.Snapshot().driving;
                    lastX = state.motor.x;
                    lastZ = state.motor.z;
                    continue;
                }
                var desired = Mathf.Atan2(dx, dz);
                var steer = Mathf.Clamp(
                    WrapAngle(desired - motor.heading) * 3f, -1f, 1f);
                var applied = Drive(steer, durationTicks: 5);
                Assert.That(applied.status,
                    Is.EqualTo(CommandResult.Applied),
                    "driving input must apply on the dressed course");
                for (var i = 0; i < 5; i++)
                {
                    service.StepDriving(world.MotorWorld);
                    service.AdvanceTick();
                    var now = service.Snapshot().driving.motor;
                    var step = Mathf.Sqrt(
                        (now.x - lastX) * (now.x - lastX)
                        + (now.z - lastZ) * (now.z - lastZ));
                    var teleport = step >= 1.4f
                        && !now.recovering && !state.motor.recovering;
                    Assert.That(teleport, Is.False,
                        "the vehicle moves by simulation; only recovery "
                        + "respawns may jump the pose (step=" + step + ")");
                    if (step < 0.05f && now.grounded) stuckTicks++;
                    else stuckTicks = 0;
                    lastX = now.x;
                    lastZ = now.z;
                }
                t += 5;
                state = service.Snapshot().driving;
                if (state.phase == "finished") break;
            }
            return wp;
        }

        private static List<DrivingEvent> Events(FoundationState state,
            string kind)
        {
            var found = new List<DrivingEvent>();
            foreach (var ev in state.drivingEvents)
                if (ev.kind == kind) found.Add(ev);
            return found;
        }

        // One lap that takes both optional shortcut branches — inside
        // (south of) IslandNorthEast on leg two and inside (west of)
        // IslandSouthEast on leg four — through the real dressed prefab.
        // If dressing wrongly blocked a lane, the pursuit stalls and the
        // waypoint count exposes it.
        [Test]
        public void BothShortcutsRemainTraversable()
        {
            WireBenchmarkCourse();
            LoadAndStart("car-01");
            var route = new List<Vector2>
            {
                new Vector2(0f, 33f),     // gate-1
                new Vector2(48f, 30f),    // shortcut south of IslandNE
                new Vector2(58f, 52f),    // rejoin before gate-2's plane
                new Vector2(63f, 55f),    // gate-2
                new Vector2(95f, -8f),    // gate-3
                new Vector2(55f, -18f),   // shortcut west of IslandSE
                new Vector2(60f, -50f),   // rejoin below the island
                new Vector2(42f, -55f),   // gate-4
                new Vector2(-10f, -63f),  // swing south of the line
                new Vector2(0f, -45f)     // cross the line -> lap one done
            };
            var reached = Pursue(route);
            Assert.That(reached, Is.EqualTo(route.Count),
                "the pursuit stalled before every shortcut waypoint was "
                + "reached (reached " + reached + "/" + route.Count + ")");
            var state = service.Snapshot();
            Assert.That(Events(state, "race.lap").Count,
                Is.GreaterThanOrEqualTo(1),
                "the shortcut lap must complete through the dressed "
                + "course");
            Assert.That(state.driving.nextCheckpoint, Is.EqualTo(1),
                "lap one accepted every mandatory gate in order");
        }

        // The landmark coastal jump over the boardwalk ramp, then a
        // manual recovery that must respawn on the authored gate-3 safe
        // pose — plus a static guarantee that every authored recovery
        // pose still sits on open ground (no dressing may occupy them).
        [Test]
        public void LandmarkJumpAndRecoveryRemainClear()
        {
            WireBenchmarkCourse();
            LoadAndStart("car-01");

            // Every authored recovery pose (gate recover poses + the
            // deterministic fallback poses) probes clean ground on the
            // dressed course — the same shape the baseline reports.
            var track = world.Catalog.Courses["graybox-01"];
            Assert.That(track, Is.Not.Null);
            foreach (var gate in track.checkpoints)
            {
                var hit = world.MotorWorld.ProbeGround(
                    new MotorPose { x = gate.recoverX, y = 0.5f,
                        z = gate.recoverZ }, 2f);
                Assert.That(hit.hit, Is.True,
                    "gate " + gate.id + " recovery pose lost its ground");
            }
            foreach (var pose in track.safePoses)
            {
                var hit = world.MotorWorld.ProbeGround(
                    new MotorPose { x = pose.x, y = 0.5f, z = pose.z },
                    2f);
                Assert.That(hit.hit, Is.True,
                    "safe pose (" + pose.x + "," + pose.z
                    + ") lost its ground to dressing");
            }

            // Drive up the west leg and off the ramp: becameAirborne and
            // landed must both fire on shipped dressing.
            Pursue(new List<Vector2>
                {
                    new Vector2(-60f, 10f),
                    new Vector2(-60f, 60f)
                }, maxTicks: 6000);
            var state = service.Snapshot();
            Assert.That(Events(state, "motor.becameAirborne").Count,
                Is.GreaterThanOrEqualTo(1),
                "the dressed ramp must still launch the vehicle");
            Assert.That(Events(state, "motor.landed").Count,
                Is.GreaterThanOrEqualTo(1),
                "the vehicle must land after the dressed ramp");
            Assert.That(state.driving.motor.grounded, Is.True,
                "the vehicle settled after the jump");

            // Gates accept in authored order and direction, so from the
            // ramp landing the route swings south of gate-1, crosses it
            // northbound, threads the NE shortcut lane to gate-2 and
            // runs the east leg through gate-3 — then press R like a
            // player: the respawn lands on the authored (95,3) safe
            // pose, heading south.
            Pursue(new List<Vector2>
                {
                    new Vector2(-30f, 10f),
                    new Vector2(0f, 33f),     // gate-1
                    new Vector2(48f, 30f),    // NE shortcut lane
                    new Vector2(58f, 52f),
                    new Vector2(63f, 55f),    // gate-2
                    new Vector2(95f, -8f)     // gate-3
                }, maxTicks: 9000);
            Assert.That(service.Snapshot().driving.nextCheckpoint,
                Is.EqualTo(4), "gate-3 must be accepted before recovery");
            var applied = service.Execute(Command("input.set",
                P("steering", "0", "drift", "false", "boost", "false",
                    "gadget", "false", "recover", "true",
                    "changeCamera", "false", "pause", "false",
                    "durationTicks", "5"), "sunny-recover"));
            Assert.That(applied.status, Is.EqualTo(CommandResult.Applied));
            Step(1);
            state = service.Snapshot();
            var motor = state.driving.motor;
            Assert.That(motor.recovering, Is.True);
            Assert.That(motor.x, Is.EqualTo(95f).Within(1f),
                "respawn x must be the authored gate-3 safe pose");
            Assert.That(motor.z, Is.EqualTo(3f).Within(1f),
                "respawn z must be the authored gate-3 safe pose");
            Assert.That(Events(state, "race.recovered").Count,
                Is.EqualTo(1));
            Assert.That(state.driving.nextCheckpoint, Is.EqualTo(4),
                "recovery never manufactures progress");
        }
    }
}
