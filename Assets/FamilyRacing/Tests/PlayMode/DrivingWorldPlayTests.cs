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
        // The root carries the production course name: DrivingWorld keeps
        // the source prefab's root name on the instance (that name is the
        // published courseVisualId), so tests find it by that name.
        private GameObject BuildCourseSource()
        {
            var root = new GameObject("GrayboxCourse");
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

        // --- Task 8: physical presentation + backend content ----------

        // A committed physical motor sample: the legacy pose fields plus
        // the VehiclePhysicsState the physical backend publishes —
        // attitude quaternion, wheel states, recovery epoch.
        private static MotorState PhysicalMotor(Vector3 pos,
            Quaternion attitude, long tick, int wheelCount = 4,
            long epoch = 0)
        {
            var physics = new VehiclePhysicsState
            {
                wheels = new WheelPhysicsState[wheelCount],
                grounded = true,
                recoveryEpoch = epoch
            };
            for (var i = 0; i < wheelCount; i++)
                physics.wheels[i] = new WheelPhysicsState { index = i };
            physics.TrySetAttitude(attitude.x, attitude.y, attitude.z,
                attitude.w);
            return new MotorState
            {
                x = pos.x, y = pos.y, z = pos.z,
                grounded = true, tick = tick, physics = physics
            };
        }

        private VehicleDefinitionAsset PhysicalCarAsset()
        {
            var tuning = Asset<VehiclePhysicsTuningAsset>(a =>
                a.ApplyPreset(VehiclePhysicsTuning.Car01()));
            return Asset<VehicleDefinitionAsset>(a =>
            {
                a.ApplyPreset(VehicleTuning.Car01());
                a.vehicleId = "car-physics-01";
                a.simulationBackend = VehicleSimulationBackend.PhysicalAssisted;
                a.physicsTuning = tuning;
                a.silhouette = "car";
                a.leanDegrees = 0f; // physical roll is real — no cosmetic lean
                a.bodyScale = new Vector3(1.8f, 1.1f, 3.2f);
            });
        }

        private VehicleDefinitionAsset PhysicalBikeAsset()
        {
            var tuning = Asset<VehiclePhysicsTuningAsset>(a =>
                a.ApplyPreset(VehiclePhysicsTuning.Motorcycle01()));
            return Asset<VehicleDefinitionAsset>(a =>
            {
                a.ApplyPreset(VehicleTuning.Motorcycle01());
                a.vehicleId = "motorcycle-physics-01";
                a.simulationBackend = VehicleSimulationBackend.PhysicalAssisted;
                a.physicsTuning = tuning;
                a.silhouette = "motorcycle";
                a.leanDegrees = 0f;
                a.bodyScale = new Vector3(0.5f, 0.9f, 2.2f);
            });
        }

        private VehiclePresentation BuildPresentation(VehicleDefinitionAsset asset,
            out GameObject root)
        {
            root = new GameObject("physical-presentation");
            var presentation = root.AddComponent<VehiclePresentation>();
            presentation.SetVehicle(asset);
            return presentation;
        }

        // The committed attitude — including roll — must reach the render
        // root. Before Task 8 the presentation wrote yaw-only Euler into
        // the root, forcing the chassis upright even when the physics
        // body was rolled (the documented gap this test pins closed).
        [Test]
        public void PhysicalAttitudeDrivesTheWholeVisualRoot()
        {
            GameObject root;
            var presentation = BuildPresentation(PhysicalCarAsset(), out root);
            try
            {
                var attitude = Quaternion.Euler(0f, 30f, 60f);
                presentation.ApplyPose(
                    PhysicalMotor(new Vector3(2f, 0.75f, 3f), attitude, 1L),
                    0f, 1f);
                Assert.That(Quaternion.Angle(root.transform.rotation, attitude),
                    Is.LessThan(0.5f),
                    "the committed physical attitude must reach the root; "
                    + "yaw-only presentation forces the chassis upright");
                Assert.That(root.transform.position.x,
                    Is.EqualTo(2f).Within(0.001f));
                Assert.That(root.transform.position.y,
                    Is.EqualTo(0.75f).Within(0.001f));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // Committed attitudes interpolate through the +/-180-degree yaw
        // wrap along the shortest arc: crossing 170 -> 190 degrees passes
        // through 180 (facing -z), never snaps back through 0.
        [Test]
        public void AttitudeInterpolationCrossesThe180Wrap()
        {
            GameObject root;
            var presentation = BuildPresentation(PhysicalCarAsset(), out root);
            try
            {
                var prev = Quaternion.Euler(0f, 170f, 0f);
                var next = Quaternion.Euler(0f, 190f, 0f); // == -170 deg
                presentation.ApplyPose(
                    PhysicalMotor(Vector3.zero, prev, 1L), 0f, 1f);
                presentation.ApplyPose(
                    PhysicalMotor(Vector3.zero, next, 2L), 0f, 0.5f);
                Assert.That(
                    Vector3.Dot(root.transform.forward, Vector3.back),
                    Is.GreaterThan(0.99f),
                    "slerp through the wrap must pass through 180 degrees, "
                    + "never swing back through 0 (forward="
                    + root.transform.forward + ")");
                // A fully committed render reaches the boundary itself.
                presentation.ApplyPose(
                    PhysicalMotor(Vector3.zero, next, 2L), 0f, 1f);
                Assert.That(Quaternion.Angle(root.transform.rotation, next),
                    Is.LessThan(0.5f));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // Visible wheels read the committed wheel state: compression
        // shortens the strut, the road-wheel steering angle yaws the
        // steerable front wheels only, and spin derives from travel.
        [Test]
        public void WheelsFollowSuspensionSteeringAndSpin()
        {
            GameObject root;
            var presentation = BuildPresentation(PhysicalCarAsset(), out root);
            try
            {
                var motor = PhysicalMotor(new Vector3(0f, 0.75f, 0f),
                    Quaternion.identity, 1L);
                motor.physics.wheels[0].grounded = true;   // FL
                motor.physics.wheels[0].compression = 0.25f;
                motor.physics.wheels[0].steering = 0.4f;
                motor.physics.wheels[0].spin = 3f;
                motor.physics.wheels[2].grounded = true;   // RL: no steering
                motor.physics.wheels[2].compression = 0.05f;
                motor.physics.wheels[2].spin = 1f;
                // wheels[1] and wheels[3] stay ungrounded: full droop.
                presentation.ApplyPose(motor, 0f, 1f);

                var tuning = VehiclePhysicsTuning.Car01();
                var fl = FindNamed(root, "WheelFL").transform;
                var spec = tuning.wheels[0];
                // The solver's springLength = rest - compression is the
                // mount→wheel-centre distance (the radius lives inside
                // the ray's hit distance, not the visual offset).
                var expectedY = spec.mountY
                    - (tuning.suspensionRestLength - 0.25f);
                Assert.That(fl.localPosition.y, Is.EqualTo(expectedY)
                    .Within(0.005f),
                    "a compressed strut pulls the wheel up against travel");
                Assert.That(fl.localPosition.x, Is.EqualTo(spec.mountX)
                    .Within(0.001f));
                Assert.That(fl.localPosition.z, Is.EqualTo(spec.mountZ)
                    .Within(0.001f));
                var expectedFl = Quaternion.Euler(0f,
                        0.4f * Mathf.Rad2Deg, 0f)
                    * Quaternion.Euler(0f, 0f, 90f)
                    * Quaternion.Euler(0f, 3f * Mathf.Rad2Deg, 0f);
                Assert.That(Quaternion.Angle(fl.localRotation, expectedFl),
                    Is.LessThan(0.5f),
                    "steerable wheels yaw by the committed road-wheel "
                    + "angle and roll by the travel-derived spin");

                var rl = FindNamed(root, "WheelRL").transform;
                var expectedRl = Quaternion.Euler(0f, 0f, 90f)
                    * Quaternion.Euler(0f, 1f * Mathf.Rad2Deg, 0f);
                Assert.That(Quaternion.Angle(rl.localRotation, expectedRl),
                    Is.LessThan(0.5f),
                    "a non-steerable wheel never picks up the steer angle");

                var fr = FindNamed(root, "WheelFR").transform;
                var droopY = tuning.wheels[1].mountY
                    - (tuning.suspensionRestLength
                        + tuning.suspensionExtraDroop);
                Assert.That(fr.localPosition.y, Is.EqualTo(droopY)
                    .Within(0.005f),
                    "an ungrounded wheel hangs at full droop");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // The physical chassis roll IS the motorcycle's lean: steering
        // input must not add cosmetic lean on top — a second application
        // would double the roll the physics already owns.
        [Test]
        public void MotorcycleLeanAppliesOnceNotTwice()
        {
            GameObject root;
            var presentation = BuildPresentation(PhysicalBikeAsset(), out root);
            try
            {
                var attitude = Quaternion.Euler(0f, 0f, 30f);
                // Full steering hold: arcade presentation would add up to
                // leanDegrees of cosmetic roll — physical must add none.
                presentation.ApplyPose(
                    PhysicalMotor(new Vector3(0f, 0.69f, 0f), attitude, 1L,
                        wheelCount: 2),
                    1f, 1f);
                Assert.That(Quaternion.Angle(root.transform.rotation, attitude),
                    Is.LessThan(0.5f),
                    "the bike's physical lean reaches the root exactly once");
                var leanPivot = root.transform.Find("LeanPivot");
                Assert.That(leanPivot, Is.Not.Null);
                Assert.That(Quaternion.Angle(leanPivot.localRotation,
                        Quaternion.identity), Is.LessThan(0.01f),
                    "the cosmetic lean pivot stays identity in physical mode");
                Assert.That(presentation.LeanRoll, Is.EqualTo(0f),
                    "cosmetic lean is fully suppressed for a physical motor");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // A car rolled onto its side renders the roll — the root must not
        // be forced upright while its wheels hang at full droop.
        [Test]
        public void FullCarRollRendersNotUpright()
        {
            GameObject root;
            var presentation = BuildPresentation(PhysicalCarAsset(), out root);
            try
            {
                var attitude = Quaternion.Euler(0f, 0f, 90f);
                var motor = PhysicalMotor(new Vector3(0f, 0.95f, 0f),
                    attitude, 1L);
                foreach (var wheel in motor.physics.wheels)
                    wheel.grounded = false;
                presentation.ApplyPose(motor, 0f, 1f);
                Assert.That(Vector3.Dot(root.transform.up, Vector3.up),
                    Is.LessThan(0.1f),
                    "a 90-degree physical roll renders rolled — the root "
                    + "must not snap the chassis back upright");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // A recovery teleport bumps the recovery epoch: interpolation must
        // snap to the new commit — never sweep across the respawn, even
        // when the pose jump is below the distance threshold.
        [Test]
        public void RecoveryEpochChangeSnapsTheVisualPose()
        {
            GameObject root;
            var presentation = BuildPresentation(PhysicalCarAsset(), out root);
            try
            {
                presentation.ApplyPose(
                    PhysicalMotor(new Vector3(0f, 0.75f, 0f),
                        Quaternion.identity, 1L, epoch: 1),
                    0f, 1f);
                // A small move (< TeleportSnapDistance) but a new epoch:
                // the visual must land exactly on the recovery commit.
                var respawn = Quaternion.Euler(0f, 90f, 0f);
                presentation.ApplyPose(
                    PhysicalMotor(new Vector3(1f, 0.75f, 1f), respawn, 2L,
                        epoch: 2),
                    0f, 0.2f);
                Assert.That(root.transform.position.x,
                    Is.EqualTo(1f).Within(0.001f));
                Assert.That(Quaternion.Angle(root.transform.rotation, respawn),
                    Is.LessThan(0.5f),
                    "an epoch change snaps the render pose — no partial "
                    + "slerp across a recovery teleport");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // --- Task 8: backend selection + seating guard -----------------

        private void WirePhysicalWorld(bool includeArcadeVehicle = false)
        {
            var car = PhysicalCarAsset();
            var track = Asset<TrackDefinitionAsset>(a =>
            {
                a.courseId = "phys-course";
                a.spawnPosition = new Vector3(0f, 0f, 0f);
                a.spawnHeadingDegrees = 0f;
                a.gates = new[]
                {
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
                        direction = new Vector2(0f, 1f),
                        recoverPosition = new Vector3(0f, 0f, 17f),
                        recoverHeadingDegrees = 0f
                    }
                };
            });
            var vehicleIds = includeArcadeVehicle
                ? new[] { "car-physics-01", "car-01" }
                : new[] { "car-physics-01" };
            var evt = Asset<EventDefinitionAsset>(a =>
            {
                a.eventId = "phys-01";
                a.track = track;
                a.laps = 1;
                a.countdownTicks = 3;
                a.vehicleIds = vehicleIds;
            });
            var vehicles = includeArcadeVehicle
                ? new[]
                {
                    car,
                    Asset<VehicleDefinitionAsset>(a =>
                    {
                        a.ApplyPreset(VehicleTuning.Car01());
                        a.vehicleId = "car-01";
                    })
                }
                : new[] { car };
            Inject(world, "vehicles", vehicles);
            Inject(world, "track", track);
            Inject(world, "events", new[] { evt });
            Inject(world, "coursePrefab", courseSource);
            Inject(world, "contentVersion", "physics-1");
            Inject(world, "simulationBackend",
                VehicleSimulationBackend.PhysicalAssisted);
        }

        // The world's serialized backend field is the developer-facing
        // selection: it lands on the catalog and maps to the concrete
        // simulation — never a player-facing toggle.
        [Test]
        public void PhysicalBackendSelectsPhysicalSimulation()
        {
            WirePhysicalWorld();
            var catalog = world.Load();
            Assert.That(catalog, Is.Not.Null,
                "the physical content must validate");
            Assert.That(catalog.SimulationBackend,
                Is.EqualTo(VehicleSimulationBackend.PhysicalAssisted));
            Assert.That(catalog.CompatibilityVersion,
                Does.Contain("sim="),
                "a non-arcade backend folds into the compatibility token");

            IVehicleSimulation physical = null;
            try
            {
                physical = world.CreateSimulation(catalog);
                Assert.That(physical, Is.InstanceOf<PhysicsVehicleSimulation>(),
                    "a physical catalog must map to the physical world");
            }
            finally
            {
                if (physical != null) physical.Dispose();
            }
        }

        // The seating contract NetworkRace.TryCreate already enforces:
        // a physical race can only seat physical-capable vehicles —
        // race.start must reject the arcade-tuned vehicle at the command
        // boundary instead of throwing inside the first stepped tick.
        [Test]
        public void PhysicalCatalogRejectsArcadeVehicleAtStart()
        {
            WirePhysicalWorld(includeArcadeVehicle: true);
            service.SetDrivingContent(world.Load());
            Assert.That(service.Snapshot().driving, Is.Null);
            service.Execute(Command("profile.create",
                P("slot", "0", "language", "en")));

            var bad = service.Execute(Command("race.start",
                P("vehicleId", "car-01", "eventId", "phys-01")));
            Assert.That(bad.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(bad.code, Is.EqualTo("invalidVehicle"),
                "an arcade-only tuning cannot seat in a physical race");

            // The physical vehicle seats fine and steps without throwing.
            var good = service.Execute(Command("race.start",
                P("vehicleId", "car-physics-01", "eventId", "phys-01")));
            Assert.That(good.status, Is.EqualTo(CommandResult.Applied),
                "the physical vehicle seats: " + good.code);
        }

        // Mirrors the LAN contract exactly: only the physical direction
        // is guarded — a physical-declared tuning may still seat in an
        // arcade race (its arcade fields drive that sim), so solo keeps
        // the same seating semantics NetworkRace enforces.
        [Test]
        public void ArcadeCatalogStillSeatsPhysicalDeclaredVehicle()
        {
            var declared = PhysicalCarAsset();
            var track = Asset<TrackDefinitionAsset>(a =>
            {
                a.courseId = "arcade-course";
                a.spawnPosition = new Vector3(0f, 0f, 0f);
                a.spawnHeadingDegrees = 0f;
                a.gates = new[]
                {
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
                a.eventId = "arcade-01";
                a.track = track;
                a.laps = 1;
                a.countdownTicks = 3;
                a.vehicleIds = new[] { "car-physics-01" };
            });
            Inject(world, "vehicles", new[] { declared });
            Inject(world, "track", track);
            Inject(world, "events", new[] { evt });
            Inject(world, "coursePrefab", courseSource);
            Inject(world, "contentVersion", "arcade-content-1");
            var catalog = world.Load();
            Assert.That(catalog.SimulationBackend,
                Is.EqualTo(VehicleSimulationBackend.Arcade));
            service.SetDrivingContent(catalog);
            service.Execute(Command("profile.create",
                P("slot", "0", "language", "en")));
            var seated = service.Execute(Command("race.start",
                P("vehicleId", "car-physics-01", "eventId", "arcade-01")));
            Assert.That(seated.status, Is.EqualTo(CommandResult.Applied),
                "LAN parity: an arcade race seats the declared vehicle: "
                + seated.code);
        }
    }
}
