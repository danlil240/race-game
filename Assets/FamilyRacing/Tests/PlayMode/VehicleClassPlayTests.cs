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
    // Task-3 vehicle-class proof on real physics: both the rally car and
    // the motorcycle complete the graybox course through the shared
    // input.set command path — never teleported — taking both shortcut
    // branches, jumping the landmark ramp, charging boost in the drift
    // zone, and surviving a recovery. The motorcycle needs no balance
    // input; its lean is a cosmetic pivot over the same stable swept
    // collision body. Response-metric comparisons between the two tuning
    // sets are recorded here for the task report's feel-review evidence.
    public sealed class VehicleClassPlayTests
    {
        private const string RunId = "vehicle-class-play";

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
                "family-racing-vclass-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            service = new FoundationService(
                new ProfileStore(directory, new UnityProfileSerializer()), "en", RunId);
            host = new GameObject("vehicle-class-host");
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

        private static void Box(GameObject parent, string name,
            Vector3 position, Vector3 scale)
        {
            Box(parent, name, position, scale, Quaternion.identity);
        }

        private static void Box(GameObject parent, string name,
            Vector3 position, Vector3 scale, Quaternion rotation)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent.transform, false);
            box.transform.localPosition = position;
            box.transform.localRotation = rotation;
            box.transform.localScale = scale;
        }

        // Mirrors the generated graybox: ground slab, perimeter walls,
        // the landmark ramp west of the loop, two island blocks splitting
        // the route into main and shortcut branches, and the drift pad.
        private GameObject BuildCourseSource()
        {
            var root = new GameObject("test-course");
            Box(root, "Ground", new Vector3(0f, -0.5f, 0f), new Vector3(240f, 1f, 160f));
            Box(root, "WallWest", new Vector3(-119.5f, 1.5f, 0f), new Vector3(1f, 3f, 160f));
            Box(root, "WallEast", new Vector3(119.5f, 1.5f, 0f), new Vector3(1f, 3f, 160f));
            Box(root, "WallNorth", new Vector3(0f, 1.5f, 79.5f), new Vector3(240f, 3f, 1f));
            Box(root, "WallSouth", new Vector3(0f, 1.5f, -79.5f), new Vector3(240f, 3f, 1f));
            Box(root, "Ramp", new Vector3(-60f, 1.2f, 30f), new Vector3(12f, 0.6f, 16f),
                Quaternion.Euler(-14f, 0f, 0f));
            Box(root, "IslandNorthEast", new Vector3(32f, 1f, 44f), new Vector3(22f, 2f, 14f));
            Box(root, "IslandSouthEast", new Vector3(72f, 1f, -32f), new Vector3(20f, 2f, 16f));
            // A small ski-jump wedge with a clean drop: the flight lands
            // on open ground with no barrier anywhere near it.
            Box(root, "JumpWedge", new Vector3(-40f, 0.7f, -30f),
                new Vector3(10f, 0.5f, 10f), Quaternion.Euler(-14f, 0f, 0f));
            root.SetActive(false);
            return root;
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

        private VehicleDefinitionAsset carAsset;
        private VehicleDefinitionAsset motoAsset;

        private void WireWorld(int laps = 1)
        {
            carAsset = Asset<VehicleDefinitionAsset>(a =>
            {
                a.ApplyPreset(VehicleTuning.Car01());
                a.silhouette = "car";
                a.leanDegrees = 4f;
                a.bodyScale = new Vector3(1.8f, 1.1f, 3.2f);
                a.bodyColor = new Color(0.91f, 0.51f, 0.42f);
            });
            motoAsset = Asset<VehicleDefinitionAsset>(a =>
            {
                a.ApplyPreset(VehicleTuning.Motorcycle01());
                a.silhouette = "motorcycle";
                a.leanDegrees = 24f;
                a.bodyScale = new Vector3(0.9f, 1.4f, 2.2f);
                a.bodyColor = new Color(0.50f, 0.78f, 0.70f);
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
            });
            var evt = Asset<EventDefinitionAsset>(a =>
            {
                a.eventId = "intro-01";
                a.track = track;
                a.laps = laps;
                a.countdownTicks = 3;
                a.vehicleIds = new[] { "car-01", "motorcycle-01" };
            });
            Inject(world, "vehicles", new[] { carAsset, motoAsset });
            Inject(world, "track", track);
            Inject(world, "events", new[] { evt });
            Inject(world, "coursePrefab", courseSource);
            Inject(world, "contentVersion", "vclass-content-1");
        }

        private void LoadAndStart(string vehicleId)
        {
            var catalog = world.Load();
            Assert.That(catalog, Is.Not.Null, "generated definitions must validate");
            service.SetDrivingContent(catalog);
            service.Execute(Command("profile.create", P("slot", "0", "language", "en")));
            var started = service.Execute(Command("race.start",
                P("vehicleId", vehicleId, "eventId", "intro-01")));
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied),
                "race.start failed: " + started.code);
            Step(3);
            Assert.That(service.Snapshot().driving.phase, Is.EqualTo("running"));
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

        private CommandResult Drive(float steering, bool drift = false,
            bool boost = false, int durationTicks = 5)
        {
            return service.Execute(Command("input.set",
                P("steering", steering.ToString("0.###",
                        System.Globalization.CultureInfo.InvariantCulture),
                    "drift", drift ? "true" : "false",
                    "boost", boost ? "true" : "false", "gadget", "false",
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

        // Waypoint pursuit through the real command path: every control
        // cycle reads the snapshot, steers at the current waypoint and
        // posts a held input.set. `boostWhenCharged` fires boost on legs
        // where the meter has charge, exercising the drain model in-lap.
        // Returns the terminal snapshot; never teleports the vehicle.
        private DrivingSessionState Pursue(List<Vector2> route,
            bool driftInZones = false, bool boostWhenCharged = false,
            int maxTicks = 9000)
        {
            var wp = 0;
            var state = service.Snapshot().driving;
            var lastX = state.motor.x;
            var lastZ = state.motor.z;
            for (var t = 0; t < maxTicks && wp < route.Count;)
            {
                var motor = state.motor;
                var dx = route[wp].x - motor.x;
                var dz = route[wp].y - motor.z;
                if (dx * dx + dz * dz < 49f) { wp++; continue; }
                var desired = Mathf.Atan2(dx, dz);
                var steer = Mathf.Clamp(WrapAngle(desired - motor.heading) * 3f, -1f, 1f);
                var drift = driftInZones
                    && InDriftZone(motor.x, motor.z)
                    && Mathf.Abs(steer) > 0.2f;
                var boost = boostWhenCharged
                    && !motor.boostActive
                    && motor.boostCharge > 0.5f
                    && Mathf.Abs(steer) < 0.3f;
                var applied = Drive(steer, drift, boost, 5);
                Assert.That(applied.status, Is.EqualTo(CommandResult.Applied),
                    "driving input must apply during the trial");
                for (var i = 0; i < 5; i++)
                {
                    service.StepDriving(world.MotorWorld);
                    service.AdvanceTick();
                    var now = service.Snapshot().driving.motor;
                    var step = Mathf.Sqrt(
                        (now.x - lastX) * (now.x - lastX)
                        + (now.z - lastZ) * (now.z - lastZ));
                    Assert.That(step, Is.LessThan(1.4f),
                        "the vehicle moves by simulation, never teleports");
                    lastX = now.x;
                    lastZ = now.z;
                }
                t += 5;
                state = service.Snapshot().driving;
                if (state.phase == "finished") break;
            }
            return state;
        }

        private static bool InDriftZone(float x, float z)
        {
            // The flat drift pad region: (45,66) half extents (20,11).
            return Mathf.Abs(x - 45f) < 18f && Mathf.Abs(z - 66f) < 9f;
        }

        private static List<DrivingEvent> Events(FoundationState state, string kind)
        {
            var found = new List<DrivingEvent>();
            foreach (var ev in state.drivingEvents)
                if (ev.kind == kind) found.Add(ev);
            return found;
        }

        // One lap through both optional shortcut branches: inside
        // IslandNorthEast on leg two and inside IslandSouthEast on leg
        // four, rejoining before each next mandatory gate.
        private static List<Vector2> ShortcutLapRoute()
        {
            return new List<Vector2>
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
                new Vector2(0f, -45f)     // cross start heading north -> finish
            };
        }

        // --- both classes complete the course --------------------------------

        [TestCase("car-01")]
        [TestCase("motorcycle-01")]
        public void VehicleCompletesLapThroughBothShortcuts(string vehicleId)
        {
            WireWorld(laps: 1);
            LoadAndStart(vehicleId);
            var terminal = Pursue(ShortcutLapRoute(), boostWhenCharged: true);
            Assert.That(terminal.phase, Is.EqualTo("finished"),
                vehicleId + " must finish the lap; pose "
                + terminal.motor.x + "," + terminal.motor.z
                + " next=" + terminal.nextCheckpoint + " lap=" + terminal.lap);
            Assert.That(terminal.lap, Is.EqualTo(1));
            var snapshot = service.Snapshot();
            Assert.That(Events(snapshot, "race.lap").Count, Is.EqualTo(1));
            Assert.That(Events(snapshot, "checkpoint.crossed").Count,
                Is.GreaterThanOrEqualTo(5));
        }

        // --- ramp + landing ---------------------------------------------------

        [TestCase("car-01")]
        [TestCase("motorcycle-01")]
        public void RampJumpProducesAirborneAndLanding(string vehicleId)
        {
            WireWorld(laps: 1);
            LoadAndStart(vehicleId);
            // Line up south of the ramp and drive north through it; the
            // route continues to gate-1 afterwards so the pursuit ends.
            var route = new List<Vector2>
            {
                new Vector2(-60f, 12f),
                new Vector2(-60f, 50f),  // over the ramp crest
                new Vector2(-30f, 30f),
                new Vector2(0f, 33f)     // gate-1
            };
            Pursue(route, maxTicks: 4000);
            var snapshot = service.Snapshot();
            Assert.That(Events(snapshot, "motor.becameAirborne").Count,
                Is.GreaterThanOrEqualTo(1),
                vehicleId + " must actually leave the ground on the ramp");
            Assert.That(Events(snapshot, "motor.landed").Count,
                Is.GreaterThanOrEqualTo(1),
                vehicleId + " must land after the ramp jump");
        }

        [TestCase("car-01")]
        [TestCase("motorcycle-01")]
        public void CleanJumpAwardsLandingCredit(string vehicleId)
        {
            WireWorld(laps: 1);
            LoadAndStart(vehicleId);
            // The ski-jump wedge drops the car onto open ground with no
            // barrier near the flight path — a creditable landing.
            var route = new List<Vector2>
            {
                new Vector2(-40f, -45f),
                new Vector2(-40f, -12f),  // over the wedge crest
                new Vector2(0f, 33f)      // gate-1
            };
            Pursue(route, maxTicks: 4000);
            var snapshot = service.Snapshot();
            Assert.That(Events(snapshot, "motor.becameAirborne").Count,
                Is.GreaterThanOrEqualTo(1),
                vehicleId + " must leave the ground on the jump wedge");
            Assert.That(Events(snapshot, "motor.cleanLanding").Count,
                Is.GreaterThanOrEqualTo(1),
                vehicleId + " must bank a clean-landing credit on the wedge");
        }

        // --- drift and boost --------------------------------------------------

        [TestCase("car-01")]
        [TestCase("motorcycle-01")]
        public void DriftChargesBoostBeyondPassiveRecharge(string vehicleId)
        {
            WireWorld(laps: 1);
            LoadAndStart(vehicleId);
            var tuning = vehicleId == "car-01"
                ? VehicleTuning.Car01()
                : VehicleTuning.Motorcycle01();
            // Cruise into the open south half of the course, then burn
            // the charge down so the drift gain has headroom to show.
            Pursue(new List<Vector2> { new Vector2(-20f, -30f) }, maxTicks: 3000);
            Drive(0f, boost: true, durationTicks: 60);
            Step(60);
            var drained = service.Snapshot().driving.motor;
            Assert.That(drained.boostCharge, Is.LessThan(0.8f),
                "setup: boost drain must leave headroom below the cap");
            var before = drained.boostCharge;
            // A held drift turn on open ground: real drift engagement,
            // charge rising faster than passive recharge alone.
            const int driftTicks = 60;
            Drive(0.6f, drift: true, durationTicks: driftTicks);
            Step(driftTicks);
            var motor = service.Snapshot().driving.motor;
            var engaged = motor.drifting || CountKind("motor.driftStart") > 0;
            Assert.That(engaged, Is.True, vehicleId + " must engage a real drift");
            var passiveOnly = driftTicks * DrivingSession.StepSeconds
                / tuning.boostChargeSeconds;
            var driftTotal = tuning.driftChargePerSecond
                * driftTicks * DrivingSession.StepSeconds;
            var gained = motor.boostCharge - before;
            Assert.That(gained, Is.GreaterThan(passiveOnly + driftTotal * 0.5f),
                vehicleId + " drift must charge faster than passive recharge"
                + " (before=" + before + " after=" + motor.boostCharge + ")");
        }

        private int CountKind(string kind)
        {
            return Events(service.Snapshot(), kind).Count;
        }

        private static GameObject FindNamed(GameObject root, string name)
        {
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                if (transform.name == name) return transform.gameObject;
            return null;
        }

        [TestCase("car-01")]
        [TestCase("motorcycle-01")]
        public void BoostDrainsChangesSpeedAndEndsOnRelease(string vehicleId)
        {
            WireWorld(laps: 1);
            LoadAndStart(vehicleId);
            var tuning = vehicleId == "car-01"
                ? VehicleTuning.Car01()
                : VehicleTuning.Motorcycle01();
            // Build speed on the south straight.
            Pursue(new List<Vector2> { new Vector2(0f, 0f) }, maxTicks: 3000);
            Drive(0f, boost: true, durationTicks: 45);
            Step(45);
            var boosted = service.Snapshot().driving.motor;
            Assert.That(boosted.speed, Is.GreaterThan(tuning.maxSpeed + 0.5f),
                "the boost speed modifier changes real motion");
            Assert.That(boosted.boostCharge, Is.LessThan(1f),
                "boost drains the normalized charge");
            var chargeBeforeRelease = boosted.boostCharge;
            Drive(0f, boost: false, durationTicks: 5); // release
            Step(5);
            var released = service.Snapshot().driving.motor;
            Assert.That(released.boostActive, Is.False,
                "releasing the input ends the boost early");
            Assert.That(released.boostCharge, Is.GreaterThan(0.1f),
                "an early release keeps the unspent charge (left "
                + chargeBeforeRelease + ")");
        }

        [TestCase("car-01")]
        [TestCase("motorcycle-01")]
        public void RecoveryDuringBoostClearsBoostAndKeepsCharge(string vehicleId)
        {
            WireWorld(laps: 1);
            LoadAndStart(vehicleId);
            Pursue(new List<Vector2> { new Vector2(0f, 33f) }, maxTicks: 3000);
            Assert.That(service.Snapshot().driving.nextCheckpoint, Is.EqualTo(2),
                "setup: gate-1 must be accepted");
            Drive(0f, boost: true, durationTicks: 10);
            Step(5);
            var boosting = service.Snapshot().driving.motor;
            Assert.That(boosting.boostActive, Is.True, "setup: boost engaged");
            var chargeLeft = boosting.boostCharge;

            var recovered = service.Execute(Command("input.set",
                P("steering", "0", "drift", "false", "boost", "false", "gadget", "false",
                    "recover", "true", "changeCamera", "false", "pause", "false",
                    "durationTicks", "5"), "recover-mid-boost"));
            Assert.That(recovered.status, Is.EqualTo(CommandResult.Applied));
            Step(1);
            var state = service.Snapshot();
            var motor = state.driving.motor;
            Assert.That(motor.recovering, Is.True);
            Assert.That(motor.boostActive, Is.False,
                "recovery ends the active boost");
            Assert.That(motor.boostCharge, Is.EqualTo(chargeLeft).Within(0.01f),
                "recovery keeps the unspent charge — never refills it");
            Assert.That(Events(state, "motor.cleanLanding").Count, Is.EqualTo(0),
                "a respawn must not mint a landing credit");
            Assert.That(state.driving.nextCheckpoint, Is.EqualTo(2),
                "the respawn never advances progress");
        }

        // --- motorcycle identity ----------------------------------------------

        [Test]
        public void SilhouettesAreVisiblyDistinct()
        {
            WireWorld(laps: 1);
            LoadAndStart("car-01");
            var presentation = FindNamed(host, "DrivingVehicle")
                .GetComponent<VehiclePresentation>();
            Assert.That(presentation, Is.Not.Null);
            world.ApplyFrame(service.Snapshot());
            var carParts = PartNames();
            service.Execute(Command("race.exit", null));
            service.Execute(Command("race.start",
                P("vehicleId", "motorcycle-01", "eventId", "intro-01")));
            Step(4);
            world.ApplyFrame(service.Snapshot());
            var motoParts = PartNames();

            Assert.That(carParts, Does.Contain("Cabin"),
                "the car silhouette carries a cabin");
            Assert.That(carParts, Does.Contain("WheelFL"));
            Assert.That(motoParts, Does.Contain("Rider")
                .And.Contain("WheelF").And.Contain("WheelR"),
                "the motorcycle silhouette carries a rider and two wheels");
            Assert.That(motoParts, Does.Not.Contain("Cabin"),
                "the two classes must not share an identical silhouette");
            Assert.That(presentation.ShownKey, Does.Contain("motorcycle"));

            List<string> PartNames()
            {
                var view = FindNamed(host, "DrivingVehicle");
                var names = new List<string>();
                foreach (Transform child in view
                    .GetComponentInChildren<Transform>(true))
                    names.Add(child.name);
                return names;
            }
        }

        [Test]
        public void LeanIsCosmeticAndCollisionBodyStaysStable()
        {
            WireWorld(laps: 1);
            LoadAndStart("motorcycle-01");
            Pursue(new List<Vector2> { new Vector2(0f, 0f) }, maxTicks: 3000);
            var presentation = FindNamed(host, "DrivingVehicle")
                .GetComponent<VehiclePresentation>();
            Assert.That(presentation, Is.Not.Null);

            // Hold a hard right turn: the lean pivot rolls but the motor
            // body's swept footprint (halfWidth) is untouched by it.
            Drive(1f, durationTicks: 60);
            for (var i = 0; i < 30; i++)
            {
                service.StepDriving(world.MotorWorld);
                service.AdvanceTick();
                world.ApplyFrame(service.Snapshot());
            }
            Assert.That(Mathf.Abs(presentation.LeanRoll), Is.GreaterThan(4f),
                "the motorcycle must lean visibly while turning");
            var motor = service.Snapshot().driving.motor;
            Assert.That(motor.grounded, Is.True);
            Assert.That(motor.y, Is.EqualTo(0f).Within(0.05f),
                "lean is cosmetic: the stable body stays planted");
            var view = FindNamed(host, "DrivingVehicle");
            var yaw = view.transform.rotation.eulerAngles.y;
            Assert.That(Mathf.DeltaAngle(motor.heading * Mathf.Rad2Deg, yaw),
                Is.EqualTo(0f).Within(0.5f),
                "the root only yaws; roll lives on the lean pivot alone");
        }

        // --- tuning comparison ------------------------------------------------

        [Test]
        public void ResponseMetricsCompareBothTuningSets()
        {
            WireWorld(laps: 1);
            var results = new Dictionary<string, Dictionary<string, float>>();
            foreach (var vehicleId in new[] { "car-01", "motorcycle-01" })
            {
                if (service.Snapshot().driving == null)
                    LoadAndStart(vehicleId);
                else
                {
                    service.Execute(Command("race.exit", null));
                    var started = service.Execute(Command("race.start",
                        P("vehicleId", vehicleId, "eventId", "intro-01")));
                    Assert.That(started.status, Is.EqualTo(CommandResult.Applied));
                    Step(3);
                }
                var tuning = vehicleId == "car-01"
                    ? VehicleTuning.Car01()
                    : VehicleTuning.Motorcycle01();

                // Metric 1: ticks from spawn to 80% of max speed.
                var ticksTo80 = 0;
                while (ticksTo80 < 3000
                    && service.Snapshot().driving.motor.speed < tuning.maxSpeed * 0.8f)
                {
                    Step(1);
                    ticksTo80++;
                }
                // Metric 2: heading change over 30 steps at full lock
                // from cruise — the steering-response number a feel
                // review compares between classes.
                var h0 = service.Snapshot().driving.motor.heading;
                Drive(1f, durationTicks: 30);
                Step(30);
                var headingDelta = Mathf.Abs(Mathf.DeltaAngle(h0 * Mathf.Rad2Deg,
                    service.Snapshot().driving.motor.heading * Mathf.Rad2Deg));
                // Metric 3: peak speed under a held boost.
                Drive(0f, boost: true, durationTicks: 60);
                var peak = 0f;
                for (var i = 0; i < 60; i++)
                {
                    Step(1);
                    peak = Mathf.Max(peak, service.Snapshot().driving.motor.speed);
                }
                results[vehicleId] = new Dictionary<string, float>
                {
                    { "ticksTo80pctMaxSpeed", ticksTo80 },
                    { "headingDelta30StepsFullLockDeg", headingDelta },
                    { "boostPeakSpeed", peak }
                };
                TestContext.Out.WriteLine(
                    "metric " + vehicleId
                    + " ticksTo80pct=" + ticksTo80
                    + " headingDeltaDeg=" + headingDelta.ToString("0.#")
                    + " boostPeak=" + peak.ToString("0.#"));
            }

            Assert.That(results["motorcycle-01"]["headingDelta30StepsFullLockDeg"],
                Is.GreaterThan(results["car-01"]["headingDelta30StepsFullLockDeg"]),
                "the motorcycle steers measurably quicker");
            Assert.That(results["motorcycle-01"]["boostPeakSpeed"],
                Is.GreaterThan(results["car-01"]["boostPeakSpeed"]),
                "the motorcycle's boost speed modifier is higher");
        }
    }
}
