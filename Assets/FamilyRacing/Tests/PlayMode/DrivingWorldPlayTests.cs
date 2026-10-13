using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FamilyRacing.Tests
{
    // End-to-end PlayMode proof for the driving slice: the real
    // UnityMotorWorld probes and sweeps actual physics colliders, a real
    // DrivingWorld turns generated definition shapes into a validated
    // catalog, and the shared command path moves a physical car on a real
    // graybox course — position, heading and wall containment included.
    // Everything is built per-test on inactive hosts so no scene asset is
    // required and FoundationApp.Awake never runs.
    public sealed class DrivingWorldPlayTests
    {
        private const string RunId = "driving-play-test";

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
                "family-racing-driveplay-" + Guid.NewGuid().ToString("N"));
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

        // A small real course: flat ground plus a solid wall the car must
        // not penetrate. It stays inactive in the scene so only the cloned
        // instance the DrivingWorld activates participates in physics.
        private GameObject BuildCourseSource()
        {
            var root = new GameObject("test-course");
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.SetParent(root.transform, false);
            ground.transform.localPosition = new Vector3(0f, -0.5f, 0f);
            ground.transform.localScale = new Vector3(120f, 1f, 120f);
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Wall";
            wall.transform.SetParent(root.transform, false);
            wall.transform.localPosition = new Vector3(0f, 1.5f, 40f);
            wall.transform.localScale = new Vector3(60f, 3f, 1f);
            root.SetActive(false);
            return root;
        }

        private void WireWorld()
        {
            var car = Asset<VehicleDefinitionAsset>(a =>
            {
                a.vehicleId = "car-01";
                a.bodyScale = new Vector3(1.8f, 1.1f, 3.2f);
                a.bodyColor = Color.red;
            });
            var track = Asset<TrackDefinitionAsset>(a =>
            {
                a.courseId = "play-course";
                a.spawnPosition = new Vector3(0f, 0f, 0f);
                a.spawnHeadingDegrees = 0f;
                a.gates = new[]
                {
                    // Gate 0 is the start/finish line on the spawn.
                    new TrackDefinitionAsset.Gate
                    {
                        id = "start",
                        center = new Vector3(0f, 0f, 0f),
                        halfWidth = 8f,
                        direction = new Vector2(0f, 1f)
                    },
                    new TrackDefinitionAsset.Gate
                    {
                        id = "gate-1",
                        center = new Vector3(0f, 0f, 20f),
                        halfWidth = 8f,
                        direction = new Vector2(0f, 1f)
                    }
                };
            });
            var evt = Asset<EventDefinitionAsset>(a =>
            {
                a.eventId = "practice-02";
                a.track = track;
                a.laps = 1;
                a.countdownTicks = 3;
                a.vehicleIds = new[] { "car-01" };
            });
            Inject(world, "vehicles", new[] { car });
            Inject(world, "track", track);
            Inject(world, "events", new[] { evt });
            Inject(world, "coursePrefab", courseSource);
            Inject(world, "contentVersion", "play-content-1");
        }

        private void StartRace()
        {
            service.Execute(Command("profile.create",
                P("slot", "0", "language", "en")));
            var started = service.Execute(Command("race.start",
                P("vehicleId", "car-01", "eventId", "practice-02")));
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied),
                "race.start setup failed: " + started.code);
            Step(3); // countdown
            Assert.That(service.Snapshot().driving.phase, Is.EqualTo("running"));
        }

        private void Step(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                service.StepDriving(world.MotorWorld);
                service.AdvanceTick();
            }
        }

        private GameCommand Command(string action, Dictionary<string, string> parameters)
        {
            return new GameCommand(1, RunId, Guid.NewGuid().ToString("N"),
                "local", action, parameters);
        }

        private static Dictionary<string, string> P(params string[] keyValues)
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i + 1 < keyValues.Length; i += 2)
                map[keyValues[i]] = keyValues[i + 1];
            return map;
        }

        // --- UnityMotorWorld against real physics -------------------------

        [Test]
        public void ProbeGroundHitsRealCollider()
        {
            var motorWorld = new UnityMotorWorld();
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                ground.transform.position = new Vector3(0f, -0.5f, 0f);
                ground.transform.localScale = new Vector3(50f, 1f, 50f);
                Physics.SyncTransforms();
                var pose = new MotorPose { x = 0f, y = 3f, z = 0f, heading = 0f };
                var hit = motorWorld.ProbeGround(pose, 6f);
                Assert.That(hit.hit, Is.True, "the probe must see the real ground");
                Assert.That(hit.y, Is.EqualTo(0f).Within(0.05f));
                var miss = motorWorld.ProbeGround(
                    new MotorPose { x = 200f, y = 3f, z = 200f, heading = 0f }, 6f);
                Assert.That(miss.hit, Is.False, "probes off the course miss");
            }
            finally
            {
                Object.Destroy(ground);
            }
        }

        [Test]
        public void SweepBodyHitsRealWall()
        {
            var motorWorld = new UnityMotorWorld();
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                wall.transform.position = new Vector3(0f, 1f, 8f);
                wall.transform.localScale = new Vector3(20f, 2f, 1f);
                Physics.SyncTransforms();
                var pose = new MotorPose { x = 0f, y = 0f, z = 0f, heading = 0f };
                var sweep = motorWorld.SweepBody(pose, 0f, 0f, 10f, 0.9f, 1.6f, 1.1f);
                Assert.That(sweep.hit, Is.True, "the body sweep must see the wall");
                Assert.That(sweep.fraction, Is.LessThan(1f));
                Assert.That(sweep.normalZ, Is.LessThan(-0.5f),
                    "the wall normal pushes back toward -z");
            }
            finally
            {
                Object.Destroy(wall);
            }
        }

        // --- DrivingWorld + real course ------------------------------------

        [Test]
        public void CatalogLoadsAndCarDrivesOnRealCourse()
        {
            WireWorld();
            var catalog = world.Load();
            Assert.That(catalog, Is.Not.Null, "generated definitions must validate");
            Assert.That(catalog.Version, Is.EqualTo("play-content-1"));
            service.SetDrivingContent(catalog);
            StartRace();
            // Activate the course instance and sync physics before probing:
            // the app does this every frame via ApplyFrame.
            world.ApplyFrame(service.Snapshot());
            Physics.SyncTransforms();

            var spawn = service.Snapshot().driving.motor;
            var held = service.Execute(Command("input.set",
                P("steering", "0", "drift", "false", "boost", "false",
                    "gadget", "false", "recover", "false", "changeCamera", "false",
                    "pause", "false", "durationTicks", "150")));
            Assert.That(held.status, Is.EqualTo(CommandResult.Applied));
            Step(60);
            var moved = service.Snapshot().driving.motor;
            Assert.That(moved.z, Is.GreaterThan(spawn.z + 5f),
                "the car must move along the real course under auto-acceleration");
            Assert.That(moved.y, Is.EqualTo(0f).Within(0.1f),
                "the ground probe keeps the car on the slab");

            var steered = service.Execute(Command("input.set",
                P("steering", "1", "drift", "false", "boost", "false",
                    "gadget", "false", "recover", "false", "changeCamera", "false",
                    "pause", "false", "durationTicks", "150")));
            Assert.That(steered.status, Is.EqualTo(CommandResult.Applied));
            Step(20);
            var turned = service.Snapshot().driving.motor;
            var headingDelta = Mathf.DeltaAngle(moved.heading * Mathf.Rad2Deg,
                turned.heading * Mathf.Rad2Deg);
            Assert.That(headingDelta, Is.GreaterThan(5f),
                "steering through the command path changes physical heading");
        }

        [Test]
        public void WallSweepBoundsPenetrationOnRealCourse()
        {
            WireWorld();
            service.SetDrivingContent(world.Load());
            StartRace();
            world.ApplyFrame(service.Snapshot());
            Physics.SyncTransforms();
            // Drive straight at the wall (face at z = 39.5) well past it:
            // auto-acceleration alone carries the car into the barrier.
            Step(400);
            var motor = service.Snapshot().driving.motor;
            Assert.That(motor.z, Is.LessThan(41.5f),
                "the swept body bounds penetration into the real wall");
            Assert.That(motor.y, Is.EqualTo(0f).Within(0.1f));
        }

        [Test]
        public void FrameAppliesMotorPoseAndOverviewCamera()
        {
            WireWorld();
            service.SetDrivingContent(world.Load());
            StartRace();
            Step(10);
            world.ApplyFrame(service.Snapshot());

            var view = FindNamed(host, "DrivingVehicle");
            Assert.That(view, Is.Not.Null, "the vehicle visual must exist");
            Assert.That(view.activeSelf, Is.True);
            var motor = service.Snapshot().driving.motor;
            Assert.That(view.transform.position.x, Is.EqualTo(motor.x).Within(0.01f));
            Assert.That(view.transform.position.z, Is.EqualTo(motor.z).Within(0.01f));
            Assert.That(world.CameraFacts.mode, Is.EqualTo("overview"));

            // Exiting the race hides the world and clears the camera role.
            service.Execute(Command("race.exit", null));
            world.ApplyFrame(service.Snapshot());
            Assert.That(view.activeSelf, Is.False);
            Assert.That(world.CameraFacts.mode, Is.EqualTo("none"));
        }

        [Test]
        public void EmptyRaceFrameCannotHideSoloCourse()
        {
            // Regression for the task-10 gate finding: FoundationApp.Update
            // runs ApplyFrame (solo path) then ApplyRaceFrame(null) on every
            // non-LAN frame. Before the fix the second call owned
            // courseInstance.SetActive and always won, so an active solo
            // race drove with NO ground colliders — the motor fell into an
            // offCourse recovery loop on every real player build.
            WireWorld();
            service.SetDrivingContent(world.Load());
            StartRace();
            // The exact frame order FoundationApp.Update executes.
            world.ApplyFrame(service.Snapshot());
            world.ApplyRaceFrame(null);
            Physics.SyncTransforms();

            var course = FindNamed(host, "GrayboxCourse");
            Assert.That(course, Is.Not.Null, "course instance must exist");
            Assert.That(course.activeSelf, Is.True,
                "a null LAN race frame must not hide the solo course");
            var motor = service.Snapshot().driving.motor;
            var hit = world.MotorWorld.ProbeGround(
                new MotorPose { x = motor.x, y = motor.y, z = motor.z,
                                heading = motor.heading }, 0.8f);
            Assert.That(hit.hit, Is.True,
                "the ground probe must see the course under a solo race");

            // Stepping after the empty race frame keeps the car on the slab —
            // the failure mode fell through to minCourseY and recovered.
            Step(30);
            var stepped = service.Snapshot().driving.motor;
            Assert.That(stepped.y, Is.EqualTo(0f).Within(0.1f));
            Assert.That(stepped.grounded, Is.True);
            Assert.That(stepped.recovering, Is.False);
        }

        [Test]
        public void RaceFrameAfterSoloKeepsCourseForLan()
        {
            // The reverse ordering must also hold: a LAN race frame keeps
            // the course active even though the solo slice is empty — the
            // course may never be visible only to one frame path.
            WireWorld();
            service.SetDrivingContent(world.Load());
            StartRace();
            world.ApplyRaceFrame(new FamilyRacing.Core.Racing.NetworkRaceState
            {
                racers = new System.Collections.Generic.List<
                    FamilyRacing.Core.Racing.NetworkRacerState>
                {
                    new FamilyRacing.Core.Racing.NetworkRacerState
                    {
                        participantId = "p-1",
                        session = new FamilyRacing.Core.Racing.DrivingSessionState
                        {
                            motor = new MotorState { x = 0f, y = 0f, z = 0f },
                        },
                    },
                },
            });
            Physics.SyncTransforms();
            var course = FindNamed(host, "GrayboxCourse");
            Assert.That(course.activeSelf, Is.True,
                "a LAN race frame must keep the course active");
        }

        private static GameObject FindNamed(GameObject root, string name)
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                if (transform.name == name) return transform.gameObject;
            return null;
        }
    }
}
