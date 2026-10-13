using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FamilyRacing.Tests
{
    // Task-8 reproducible-content proof: VehiclePhysicsSetup.Create()
    // is the established setup entry point's physical step — it must be
    // idempotent (same assets, same GUIDs, same semantics on re-run) and
    // the generated content must place valid hulls over real suspension
    // rays: every authored spawn slot, safe pose, gate respawn candidate
    // and ramp pose must support both physical vehicle classes without
    // the chassis box clipping course geometry.
    public sealed class VehiclePhysicsSetupTests
    {
        private const string PhysicsDir =
            "Assets/FamilyRacing/Generated/Physics";
        private const string DrivingDir =
            "Assets/FamilyRacing/Generated/Driving";
        private const string RaceScenePath =
            PhysicsDir + "/PhysicsRace.unity";
        private const float MinDrivableNormalY = 0.5f;

        private static Dictionary<string, string> GuidSnapshot()
        {
            var map = new Dictionary<string, string>();
            foreach (var guid in AssetDatabase.FindAssets(
                "", new[] { PhysicsDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(path)) continue;
                map[path] = guid;
            }
            return map;
        }

        // The same generator run twice must produce the same asset set
        // with the same GUIDs and the same semantic content — generated
        // assets are rewritten deterministically, never appended to.
        [Test]
        public void SetupIsIdempotentWithStableGuidsAndSemantics()
        {
            VehiclePhysicsSetup.Create();
            var first = GuidSnapshot();
            Assert.That(first.Count, Is.GreaterThan(4),
                "the physics folder holds the generated lab/race content");

            var carVehicle =
                AssetDatabase.LoadAssetAtPath<VehicleDefinitionAsset>(
                    PhysicsDir + "/car-physics-01-vehicle.asset");
            Assert.That(carVehicle, Is.Not.Null);
            var backend = carVehicle.simulationBackend;
            var tuning = carVehicle.physicsTuning;

            VehiclePhysicsSetup.Create();
            var second = GuidSnapshot();
            Assert.That(second.Count, Is.EqualTo(first.Count),
                "a re-run must not grow the generated asset set");
            foreach (var pair in first)
            {
                string guid;
                Assert.That(second.TryGetValue(pair.Key, out guid),
                    Is.True, "second run lost generated asset " + pair.Key);
                Assert.That(guid, Is.EqualTo(pair.Value),
                    "GUID drift on " + pair.Key
                    + " — generated assets must keep their identities");
            }
            var carAgain =
                AssetDatabase.LoadAssetAtPath<VehicleDefinitionAsset>(
                    PhysicsDir + "/car-physics-01-vehicle.asset");
            Assert.That(carAgain.simulationBackend, Is.EqualTo(backend),
                "the backend selection survives a re-run");
            Assert.That(carAgain.physicsTuning, Is.EqualTo(tuning),
                "the tuning binding survives a re-run");
        }

        // Generated definitions must validate into the same Core shapes
        // the sim consumes, and every physical vehicle must carry the
        // backend declaration plus a non-null physics tuning — the
        // contract the solo/LAN seating guards enforce.
        [Test]
        public void GeneratedPhysicalContentValidatesAndBindsBackend()
        {
            VehiclePhysicsSetup.Create();
            foreach (var path in new[]
            {
                PhysicsDir + "/car-physics-01-vehicle.asset",
                PhysicsDir + "/motorcycle-physics-01-vehicle.asset"
            })
            {
                var vehicle =
                    AssetDatabase.LoadAssetAtPath<VehicleDefinitionAsset>(
                        path);
                Assert.That(vehicle, Is.Not.Null, path + " must exist");
                Assert.That(vehicle.simulationBackend, Is.EqualTo(
                        VehicleSimulationBackend.PhysicalAssisted),
                    path + " must declare the physical backend");
                Assert.That(vehicle.physicsTuning, Is.Not.Null,
                    path + " must reference a physics tuning");
                string error;
                var tuning = vehicle.ToTuning(out error);
                Assert.That(tuning, Is.Not.Null,
                    path + " failed validation: " + error);
                Assert.That(tuning.physics, Is.Not.Null,
                    "validated tuning carries the physics block");
                Assert.That(vehicle.leanDegrees, Is.EqualTo(0f),
                    "physical assets never stack cosmetic lean — the "
                    + "chassis roll IS the lean");
            }

            var evt = AssetDatabase.LoadAssetAtPath<EventDefinitionAsset>(
                PhysicsDir + "/physics-01.asset");
            Assert.That(evt, Is.Not.Null,
                "the physical event must be generated");
            string evtError;
            var def = evt.ToEvent(out evtError);
            Assert.That(def, Is.Not.Null,
                "physics-01 failed validation: " + evtError);
            CollectionAssert.AreEquivalent(
                new[] { "car-physics-01", "motorcycle-physics-01" },
                def.vehicleIds,
                "only physical-declared vehicles may enter");
        }

        // Geometric contract between generated content and the shared
        // graybox course: at every spawn slot, safe pose and gate respawn
        // candidate, each suspension ray must find drivable ground within
        // reach while the chassis box stays clear of solid geometry —
        // for BOTH physical classes. Ramp poses add the incline check:
        // rays ride the sloped face and the pitched hull stays clear.
        [Test]
        public void HullsAndRaysClearSpawnsRespawnsAndRamp()
        {
            VehiclePhysicsSetup.Create();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                DrivingDir + "/GrayboxCourse.prefab");
            var trackAsset =
                AssetDatabase.LoadAssetAtPath<TrackDefinitionAsset>(
                    DrivingDir + "/graybox-01.asset");
            Assert.That(prefab, Is.Not.Null,
                "GrayboxCourse prefab must exist");
            Assert.That(trackAsset, Is.Not.Null,
                "graybox-01 must exist");
            string courseError;
            var course = trackAsset.ToCourse(out courseError);
            Assert.That(course, Is.Not.Null,
                "the shared course must validate: " + courseError);
            Assert.That(course.spawnSlots.Count, Is.EqualTo(4),
                "the authored four-slot LAN grid is present");

            var carTuning =
                AssetDatabase.LoadAssetAtPath<VehiclePhysicsTuningAsset>(
                    PhysicsDir + "/car-physics-01.asset")
                    .ToTuning(out _);
            var bikeTuning =
                AssetDatabase.LoadAssetAtPath<VehiclePhysicsTuningAsset>(
                    PhysicsDir + "/motorcycle-physics-01.asset")
                    .ToTuning(out _);
            Assert.That(carTuning, Is.Not.Null);
            Assert.That(bikeTuning, Is.Not.Null);

            var instance = Object.Instantiate(prefab);
            try
            {
                Physics.SyncTransforms();
                foreach (var tuning in new[] { carTuning, bikeTuning })
                {
                    // Solo spawn, the four LAN grid slots, every authored
                    // safe pose, and every gate's respawn candidate.
                    var poses = new List<MotorPose>
                    {
                        new MotorPose
                        {
                            x = course.spawnX, y = course.spawnY,
                            z = course.spawnZ, heading = course.spawnHeading
                        }
                    };
                    poses.AddRange(course.spawnSlots);
                    poses.AddRange(course.safePoses);
                    foreach (var gate in course.checkpoints)
                        poses.Add(new MotorPose
                        {
                            x = gate.recoverX, y = gate.recoverY,
                            z = gate.recoverZ,
                            heading = gate.recoverHeading
                        });
                    foreach (var pose in poses)
                        AssertPoseClear(tuning, pose, "spawn/respawn");
                    AssertRampPosesClear(tuning);
                }
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
        }

        // One surface-height pose: the body origin lifts by the tuning's
        // own SettledLift, rays are cast exactly like WheelContactSolver
        // (mount offset, -bodyUp, rest+droop+radius reach, drivable
        // normal), and the chassis OverlapBox must return empty.
        private static void AssertPoseClear(VehiclePhysicsTuning tuning,
            MotorPose pose, string label)
        {
            var yaw = Quaternion.Euler(0f,
                pose.heading * Mathf.Rad2Deg, 0f);
            var origin = new Vector3(pose.x,
                pose.y + tuning.SettledLift(), pose.z);
            var context = label + " pose (" + pose.x + "," + pose.z + ")"
                + " mass=" + tuning.massKg;
            var reach = tuning.suspensionRestLength
                + tuning.suspensionExtraDroop;
            foreach (var wheel in tuning.wheels)
            {
                var mount = origin + yaw * new Vector3(
                    wheel.mountX, wheel.mountY, wheel.mountZ);
                RaycastHit hit;
                var grounded = Physics.Raycast(mount, Vector3.down,
                    out hit, reach + wheel.radius,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);
                Assert.That(grounded, Is.True,
                    context + ": wheel at mount " + wheel.mountX + ","
                    + wheel.mountZ + " finds no drivable surface");
                Assert.That(hit.normal.y,
                    Is.GreaterThanOrEqualTo(MinDrivableNormalY),
                    context + ": suspension ray lands on a drivable "
                    + "normal, not a wall");
            }
            var half = new Vector3(tuning.chassisWidth * 0.5f,
                tuning.chassisHeight * 0.5f, tuning.chassisLength * 0.5f);
            var overlaps = Physics.OverlapBox(origin, half, yaw,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            Assert.That(overlaps.Length, Is.EqualTo(0),
                context + ": the chassis box overlaps "
                + (overlaps.Length > 0 ? overlaps[0].name : "?"));
        }

        // Poses on the landmark ramp incline (x=-60, the wedge climbs
        // north): the pitched hull still clears and the rays still find
        // the sloped surface — the 14-degree face passes the drivable
        // normal bar.
        private static void AssertRampPosesClear(
            VehiclePhysicsTuning tuning)
        {
            foreach (var z in new[] { 28f, 31f, 34f })
            {
                // Find the real sloped surface under the sampling column.
                RaycastHit surface;
                Assert.That(Physics.Raycast(
                        new Vector3(-60f, 10f, z), Vector3.down,
                        out surface, 20f, Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore),
                    Is.True, "ramp sample at z=" + z + " finds surface");
                var normal = surface.normal.normalized;
                // The body pitches to the slope: origin rides the surface
                // normal, rays run along -bodyUp exactly like the solver.
                var attitude = Quaternion.FromToRotation(
                    Vector3.up, normal);
                var origin = surface.point + normal * tuning.SettledLift();
                var context = "ramp z=" + z + " mass=" + tuning.massKg;
                var reach = tuning.suspensionRestLength
                    + tuning.suspensionExtraDroop;
                var down = attitude * Vector3.down;
                foreach (var wheel in tuning.wheels)
                {
                    var mount = origin + attitude * new Vector3(
                        wheel.mountX, wheel.mountY, wheel.mountZ);
                    RaycastHit hit;
                    var grounded = Physics.Raycast(mount, down, out hit,
                        reach + wheel.radius,
                        Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore);
                    Assert.That(grounded, Is.True,
                        context + ": wheel ray off the incline misses "
                        + "drivable ground within reach");
                    Assert.That(hit.normal.y,
                        Is.GreaterThanOrEqualTo(MinDrivableNormalY),
                        context + ": the incline face stays drivable");
                }
                var half = new Vector3(tuning.chassisWidth * 0.5f,
                    tuning.chassisHeight * 0.5f,
                    tuning.chassisLength * 0.5f);
                var overlaps = Physics.OverlapBox(origin, half, attitude,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore);
                Assert.That(overlaps.Length, Is.EqualTo(0),
                    context + ": the pitched hull overlaps "
                    + (overlaps.Length > 0 ? overlaps[0].name : "?"));
            }
        }

        // The generated physics race scene is a real self-bootstrapping
        // composition: the world's serialized backend selects
        // PhysicalAssisted and every content reference resolves to a
        // generated asset — the catalog it would build is validated
        // here without loading the scene's world.
        [Test]
        public void PhysicsRaceSceneWiresThePhysicalCatalog()
        {
            VehiclePhysicsSetup.Create();
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(
                    RaceScenePath), Is.Not.Null,
                "PhysicsRace.unity must be generated");

            var scene = EditorSceneManager.OpenScene(
                RaceScenePath, OpenSceneMode.Additive);
            // The setup's scene generation may leave this as the ONLY
            // loaded scene — closing the last scene is unsupported, so
            // close only when something else is still open.
            var canClose = SceneManager.sceneCount > 1;
            try
            {
                DrivingWorld world = null;
                FoundationApp app = null;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (app == null)
                        app = root.GetComponentInChildren<FoundationApp>(
                            true);
                    if (world == null)
                        world = root.GetComponentInChildren<DrivingWorld>(
                            true);
                }
                Assert.That(app, Is.Not.Null,
                    "the scene carries the app composition");
                Assert.That(world, Is.Not.Null,
                    "the scene carries a DrivingWorld");

                var serialized = new SerializedObject(world);
                var backend = serialized.FindProperty("simulationBackend");
                Assert.That(backend, Is.Not.Null,
                    "the world's developer-facing backend field exists");
                Assert.That(backend.enumValueIndex, Is.EqualTo(
                        (int)VehicleSimulationBackend.PhysicalAssisted),
                    "the scene's backend selects PhysicalAssisted");

                // Resolve the serialized content references the way
                // Load() would and validate the resulting catalog —
                // without touching the scene's objects.
                var vehicles = serialized.FindProperty("vehicles");
                var events = serialized.FindProperty("events");
                var track = serialized.FindProperty("track");
                var coursePrefab =
                    serialized.FindProperty("coursePrefab");
                Assert.That(vehicles.arraySize, Is.EqualTo(2));
                Assert.That(events.arraySize, Is.EqualTo(1));
                var tunings = new List<VehicleTuning>();
                for (var i = 0; i < vehicles.arraySize; i++)
                {
                    var vehicle = vehicles.GetArrayElementAtIndex(i)
                        .objectReferenceValue as VehicleDefinitionAsset;
                    Assert.That(vehicle, Is.Not.Null,
                        "vehicle slot " + i + " resolves");
                    string error;
                    var tuning = vehicle.ToTuning(out error);
                    Assert.That(tuning, Is.Not.Null, error);
                    tunings.Add(tuning);
                }
                var trackAsset = track.objectReferenceValue
                    as TrackDefinitionAsset;
                Assert.That(trackAsset, Is.Not.Null);
                string courseError;
                var course = trackAsset.ToCourse(out courseError);
                Assert.That(course, Is.Not.Null, courseError);
                var defs = new List<Core.Racing.EventDefinition>();
                for (var i = 0; i < events.arraySize; i++)
                {
                    var evt = events.GetArrayElementAtIndex(i)
                        .objectReferenceValue as EventDefinitionAsset;
                    string error;
                    var def = evt.ToEvent(out error);
                    Assert.That(def, Is.Not.Null, error);
                    defs.Add(def);
                }
                var catalog = Core.Racing.DrivingContentCatalog.Create(
                    "physics-0.1", tunings, new[] { course }, defs,
                    VehicleSimulationBackend.PhysicalAssisted);
                Assert.That(catalog.SimulationBackend, Is.EqualTo(
                        VehicleSimulationBackend.PhysicalAssisted));
                Assert.That(coursePrefab.objectReferenceValue,
                    Is.Not.Null, "the collision course prefab is wired");
            }
            finally
            {
                if (canClose && scene.IsValid() && scene.isLoaded)
                    EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
