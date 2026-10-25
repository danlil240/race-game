using System;
using System.Linq;
using FamilyRacing.EditorTools;
using FamilyRacing.Core.Driving;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FamilyRacing.Tests
{
    public sealed class ProductionCourseAuthoringTests
    {
        private static readonly string[] Expected = {
            "harbor-hop", "lighthouse-loop", "canyon-dash", "mesa-skyway", "snowpine-circuit", "aurora-pass"
        };

        [OneTimeSetUp]
        public void AuthorCourses() => ProductionCourseAuthoring.Create();

        [Test]
        public void SixDistinctClosedCoursesConvertWithCompleteDrivingInventory()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ProductionCourseAuthoring.PrefabPath);
            Assert.That(prefab, Is.Not.Null);
            Assert.That(prefab.transform.Cast<Transform>().Select(t => t.name), Is.EqualTo(Expected));
            var lengths = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < Expected.Length; i++)
            {
                var track = Track(i);
                Assert.That(track.courseId, Is.EqualTo(Expected[i]));
                Assert.That(track.isOpenRoute, Is.False);
                Assert.That(track.ToCourse(out var error), Is.Not.Null, error);
                Assert.That(track.spawnSlots.Length, Is.EqualTo(4));
                Assert.That(track.safePoses.Length, Is.GreaterThanOrEqualTo(4));
                Assert.That(track.pickupSites.Length, Is.GreaterThanOrEqualTo(3));
                Assert.That(track.shortcutLanes.Length, Is.EqualTo(2));
                Assert.That(track.gates.Length, Is.GreaterThanOrEqualTo(4));
                Assert.That(track.aiRoute.Length, Is.GreaterThan(100));
                var route = track.aiRoute.Select(p => new Vector3(p.x, 0, p.z)).ToArray();
                float length = 0;
                for (int n = 0; n < route.Length; n++)
                {
                    float gap = Vector3.Distance(route[n], route[(n + 1) % route.Length]);
                    Assert.That(gap, Is.InRange(1f, 20f), "AI loop must have a drivable closure");
                    length += gap;
                }
                Assert.That(length, Is.GreaterThan(900));
                lengths.Add(Mathf.RoundToInt(length));
            }
            Assert.That(lengths.Count, Is.EqualTo(6), "Routes must have distinct lengths/topologies");
        }

        [Test]
        public void EverySpawnGateRecoveryPickupAndAiPointHasAnActualRoadBelowIt()
        {
            var instance = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ProductionCourseAuthoring.PrefabPath));
            try
            {
                Physics.SyncTransforms();
                for (int i = 0; i < 6; i++)
                {
                    var track = Track(i);
                    var root = instance.transform.Find(Expected[i]);
                    var collider = root.Find("MainRoad").GetComponent<MeshCollider>();
                    Assert.That(collider.sharedMesh, Is.SameAs(root.Find("MainRoad").GetComponent<MeshFilter>().sharedMesh));
                    foreach (var point in track.spawnSlots.Select(s => s.position)
                        .Concat(track.safePoses.Select(s => s.position))
                        .Concat(track.gates.Select(g => g.center))
                        .Concat(track.pickupSites.Select(s => s.center)))
                        AssertRoad(collider, point);
                    foreach (var point in track.aiRoute)
                        AssertRoad(collider, new Vector3(point.x, 30, point.z));
                    Assert.That(root.GetComponentsInChildren<BoxCollider>().Count(c => c.name.StartsWith("Barrier")), Is.GreaterThan(200));
                }
            }
            finally { Object.DestroyImmediate(instance); }
        }

        [Test]
        public void ShortcutRoadsDivergeRejoinBeforeNextGateAndHaveUnobstructedDrivingCenters()
        {
            var instance = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ProductionCourseAuthoring.PrefabPath));
            try
            {
                Physics.SyncTransforms();
                for (int i = 0; i < 6; i++)
                {
                    var track = Track(i);
                    var root = instance.transform.Find(Expected[i]);
                    var main = Centers(root.Find("MainRoad").GetComponent<MeshFilter>().sharedMesh);
                    for (int lane = 0; lane < 2; lane++)
                    {
                        var road = root.Find("Shortcut" + (lane + 1) + "Road");
                        Assert.That(road, Is.Not.Null, "A detector alone is not a shortcut");
                        var points = Centers(road.GetComponent<MeshFilter>().sharedMesh);
                        Assert.That(road.GetComponent<MeshCollider>().sharedMesh, Is.SameAs(road.GetComponent<MeshFilter>().sharedMesh));
                        int entry = Nearest(main, points[0]), exit = Nearest(main, points.Last());
                        Assert.That(Vector3.Distance(main[entry], points[0]), Is.LessThan(.1f));
                        Assert.That(Vector3.Distance(main[exit], points.Last()), Is.LessThan(.1f));
                        Assert.That(exit, Is.GreaterThan(entry));
                        var routeGateIndices = track.gates.Select(g => Nearest(main, g.center)).Where(n => n > entry).ToArray();
                        Assert.That(routeGateIndices.Length, Is.GreaterThan(0));
                        Assert.That(exit, Is.LessThan(routeGateIndices.Min()), "Shortcut must rejoin before next mandatory gate");
                        Assert.That(points.Max(p => Vector3.Distance(p, main[Nearest(main, p)])), Is.GreaterThan(12), "Branch must leave the ordinary road");
                        float branchLength = 0, mainLength = 0;
                        for (int n = 1; n < points.Length; n++) branchLength += Vector3.Distance(points[n - 1], points[n]);
                        for (int n = entry + 1; n <= exit; n++) mainLength += Vector3.Distance(main[n - 1], main[n]);
                        Assert.That(branchLength, Is.LessThan(mainLength * .99f));
                        var detector = track.shortcutLanes[lane];
                        AssertRoad(road.GetComponent<MeshCollider>(), detector.center);
                        Assert.That(Vector3.Distance(detector.center, main[Nearest(main, detector.center)]), Is.GreaterThan(12));
                        foreach (var gate in track.gates)
                            Assert.That(Vector3.Distance(detector.center, gate.center), Is.GreaterThan(detector.halfWidth + gate.halfWidth + 5));
                        foreach (var point in points) AssertRoad(road.GetComponent<MeshCollider>(), point);
                        for (int n = 1; n < points.Length; n++)
                        {
                            var from = points[n - 1] + Vector3.up * .7f;
                            var delta = points[n] - points[n - 1];
                            foreach (var barrier in root.GetComponentsInChildren<BoxCollider>())
                                Assert.That(barrier.Raycast(new Ray(from, delta.normalized), out _, delta.magnitude), Is.False,
                                    Expected[i] + " shortcut driving corridor blocked by " + barrier.name);
                        }
                    }
                }
            }
            finally { Object.DestroyImmediate(instance); }
        }

        [TestCase("car-01")]
        [TestCase("motorcycle-01")]
        public void EveryAuthoredLandmarkJumpProducesRealAirborneAndCleanLanding(string vehicleId)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ProductionCourseAuthoring.PrefabPath);
            var instance = Object.Instantiate(prefab);
            try
            {
                Physics.SyncTransforms();
                var vehicle = AssetDatabase.LoadAssetAtPath<VehicleDefinitionAsset>(ProductionCampaignAuthoring.VehiclePath(vehicleId));
                Assert.That(vehicle, Is.Not.Null, "Production starter tuning must be authored before the physical oracle");
                var tuning = vehicle.ToTuning(out var tuningError);
                Assert.That(tuning, Is.Not.Null, tuningError);
                var world = new UnityMotorWorld();
                for (int course = 0; course < Expected.Length; course++)
                {
                    var jump = instance.transform.Find(Expected[course] + "/LandmarkJump");
                    Assert.That(jump, Is.Not.Null, "Every production course requires an authored landmark jump");
                    var start = jump.Find("ApproachStart").position;
                    var launch = jump.Find("Launch").position;
                    var runout = jump.Find("Runout").position;
                    var direction = runout - start; direction.y = 0; direction.Normalize();
                    var state = new MotorState { x = start.x, y = start.y, z = start.z,
                        heading = Mathf.Atan2(direction.x, direction.z), grounded = true, boostCharge = 0 };
                    int airborne = 0, landed = 0, clean = 0;
                    float flightSeconds = 0;
                    for (int tick = 0; tick < 360; tick++)
                    {
                        var result = ArcadeMotor.Step(state, new DriverInput(0, false, false, false, false, false, false), tuning, world, 1f / 30f);
                        Assert.That(result.rejected, Is.False, result.error);
                        state = result.state;
                        Assert.That(Track(course).recoveryBounds.Any(b =>
                            state.x >= b.min.x && state.x <= b.max.x &&
                            state.y >= b.min.y && state.y <= b.max.y &&
                            state.z >= b.min.z && state.z <= b.max.z), Is.True,
                            Expected[course] + "/" + vehicleId + " jump pose left authored permitted airspace at tick " + tick);
                        airborne += result.events.Count(e => e.kind == "becameAirborne");
                        landed += result.events.Count(e => e.kind == "landed");
                        clean += result.events.Count(e => e.kind == "cleanLanding");
                        flightSeconds = Mathf.Max(flightSeconds, state.airSeconds);
                        float beyondLaunch = Vector3.Dot(new Vector3(state.x, 0, state.z) - new Vector3(launch.x, 0, launch.z), direction);
                        if (clean > 0 && state.grounded && beyondLaunch > 20) break;
                    }
                    string context = Expected[course] + "/" + vehicleId;
                    Assert.That(airborne, Is.GreaterThanOrEqualTo(1), context + " never left the launch deck");
                    Assert.That(landed, Is.GreaterThanOrEqualTo(1), context + " never reached the landing surface");
                    Assert.That(clean, Is.GreaterThanOrEqualTo(1), context + " flight must earn the real motor cleanLanding event");
                    Assert.That(flightSeconds, Is.GreaterThanOrEqualTo(tuning.landingMinAirSeconds), context);
                    Assert.That(state.grounded, Is.True, context + " must settle on the runout");
                    Assert.That(Vector3.Dot(new Vector3(state.x, 0, state.z) - new Vector3(launch.x, 0, launch.z), direction), Is.GreaterThan(20), context);
                    TestContext.Progress.WriteLine(context + ": airborne=" + airborne + ", landed=" + landed + ", clean=" + clean + ", flightSeconds=" + flightSeconds);
                }
            }
            finally { Object.DestroyImmediate(instance); }
        }

        [Test]
        public void LandmarkJumpHasConnectedOptionalSpurRealGapAndWideLanding()
        {
            var instance = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ProductionCourseAuthoring.PrefabPath));
            try
            {
                Physics.SyncTransforms();
                for (int course = 0; course < Expected.Length; course++)
                {
                    var root = instance.transform.Find(Expected[course]);
                    var jump = root.Find("LandmarkJump");
                    Assert.That(jump, Is.Not.Null);
                    foreach (var collider in jump.GetComponentsInChildren<MeshCollider>())
                        foreach (var vertex in collider.sharedMesh.vertices)
                        {
                            var point = collider.transform.TransformPoint(vertex);
                            Assert.That(Track(course).recoveryBounds.Any(b =>
                                point.x >= b.min.x && point.x <= b.max.x && point.y >= b.min.y && point.y <= b.max.y &&
                                point.z >= b.min.z && point.z <= b.max.z), Is.True, "Authored jump road lies outside permitted recovery bounds");
                        }
                    var main = Centers(root.Find("MainRoad").GetComponent<MeshFilter>().sharedMesh);
                    var approach = Centers(jump.Find("EntryRoad").GetComponent<MeshFilter>().sharedMesh);
                    var exit = Centers(jump.Find("ExitRoad").GetComponent<MeshFilter>().sharedMesh);
                    Assert.That(Vector3.Distance(approach[0], main[86]), Is.LessThan(.01f));
                    Assert.That(Vector3.Distance(exit.Last(), main[116]), Is.LessThan(.01f));
                    Assert.That(116, Is.LessThan(Track(course).gates.Select(g => Nearest(main, g.center)).Where(n => n > 86).Min()));
                    var launch = jump.Find("Launch").position;
                    var landing = jump.Find("LandingStart").position;
                    Assert.That(launch.y - landing.y, Is.EqualTo(3).Within(.01));
                    var gap = (launch + landing) * .5f;
                    Assert.That(Physics.Raycast(gap + Vector3.up * 10, Vector3.down, out _, 15), Is.False,
                        "The jump gap must be real airspace, not a detector over ordinary road");
                    AssertRoad(jump.Find("LandingRoad").GetComponent<MeshCollider>(), landing);
                    var v = jump.Find("LandingRoad").GetComponent<MeshFilter>().sharedMesh.vertices;
                    Assert.That(Vector3.Distance(v[0], v[1]), Is.GreaterThanOrEqualTo(11.9f));
                }
            }
            finally { Object.DestroyImmediate(instance); }
        }

        [Test]
        public void CourseSelectionSwitchesVisibleAndSolidHierarchyTogether()
        {
            var instance = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ProductionCourseAuthoring.PrefabPath));
            try
            {
                var selector = instance.GetComponent<LongRaceVisibility>();
                Assert.That(selector, Is.Not.Null);
                foreach (var selected in Expected.Concat(new[] { Expected[0] }))
                {
                    selector.SelectCourse(selected);
                    foreach (Transform child in instance.transform)
                    {
                        Assert.That(child.gameObject.activeSelf, Is.EqualTo(child.name == selected));
                        Assert.That(child.GetComponentsInChildren<MeshCollider>(true)
                            .Any(collider => collider.enabled && collider.gameObject.activeInHierarchy), Is.EqualTo(child.name == selected));
                        Assert.That(child.GetComponentsInChildren<MeshRenderer>(true)
                            .Any(renderer => renderer.enabled && renderer.gameObject.activeInHierarchy), Is.EqualTo(child.name == selected));
                    }
                }
            }
            finally { Object.DestroyImmediate(instance); }
        }

        [Test]
        public void ReauthoringRetainsEveryAssetGuidAndStableCourseData()
        {
            var paths = AssetDatabase.FindAssets("", new[] { FullContentSetup.ContentDir + "/Courses" })
                .Select(AssetDatabase.GUIDToAssetPath).Where(p => !AssetDatabase.IsValidFolder(p)).ToArray();
            var guids = paths.Select(AssetDatabase.AssetPathToGUID).ToArray();
            var before = Enumerable.Range(0, 6).Select(i => EditorJsonUtility.ToJson(Track(i))).ToArray();
            ProductionCourseAuthoring.Create();
            Assert.That(paths.Select(AssetDatabase.AssetPathToGUID), Is.EqualTo(guids));
            Assert.That(Enumerable.Range(0, 6).Select(i => EditorJsonUtility.ToJson(Track(i))), Is.EqualTo(before));
        }

        private static TrackDefinitionAsset Track(int i) => AssetDatabase.LoadAssetAtPath<TrackDefinitionAsset>(ProductionCourseAuthoring.TrackPath(i));
        private static Vector3[] Centers(Mesh mesh)
        {
            var v = mesh.vertices;
            return Enumerable.Range(0, v.Length / 2).Select(n => (v[n * 2] + v[n * 2 + 1]) * .5f).ToArray();
        }
        private static int Nearest(Vector3[] points, Vector3 p)
            => Enumerable.Range(0, points.Length).OrderBy(n => (points[n] - p).sqrMagnitude).First();
        private static void AssertRoad(MeshCollider collider, Vector3 point)
        {
            Assert.That(collider.Raycast(new Ray(new Vector3(point.x, 80, point.z), Vector3.down), out var hit, 120), Is.True,
                "No road under " + point + " on " + collider.name);
            if (point.y < 20) Assert.That(Mathf.Abs(hit.point.y - point.y), Is.LessThan(.4f));
        }
    }
}
