using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FamilyRacing.Tests
{
    // M6 Task 4 (slice A) PlayMode pin for the full-content course
    // contract the slice owns: authored optional shortcut lanes ride
    // the inherited authored-asset -> DrivingWorld.Load() path and
    // reach the runtime CourseDefinition unchanged; malformed lanes
    // fail the load honestly (null catalog -> race.start reports
    // contentUnavailable, never a race against half-bound content);
    // an empty lane set stays a legal generic catalog — "two legal
    // shortcuts per course" is the production validator's EditMode
    // contract, not the runtime's. The heavy alternating-course loader
    // expansion is the deferred slice; nothing here constructs a
    // second loader.
    public sealed class FullContentCoursePlayTests
    {
        private GameObject host;
        private DrivingWorld world;
        private readonly List<Object> assets = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("full-content-course-host");
            host.SetActive(false);
            world = host.AddComponent<DrivingWorld>();
            host.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null) Object.Destroy(host);
            foreach (var asset in assets)
                if (asset != null) Object.Destroy(asset);
        }

        private T Asset<T>(System.Action<T> configure)
            where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>();
            configure(asset);
            assets.Add(asset);
            return asset;
        }

        private static void Inject(object target, string field,
            object value)
        {
            target.GetType().GetField(field,
                BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(target, value);
        }

        private static TrackDefinitionAsset.Gate Gate(string id,
            float x, float z, float dirX, float dirZ)
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

        private static TrackDefinitionAsset.ShortcutLane Lane(
            string id, float x, float z, float dirX = 0f,
            float dirZ = 1f)
        {
            return new TrackDefinitionAsset.ShortcutLane
            {
                id = id,
                center = new Vector3(x, 0f, z),
                halfWidth = 5f,
                direction = new Vector2(dirX, dirZ)
            };
        }

        // A minimal valid world: one vehicle, one two-gate course,
        // one event — the same shape the graybox slice loads.
        private void WireWorld(
            TrackDefinitionAsset.ShortcutLane[] lanes)
        {
            var car = Asset<VehicleDefinitionAsset>(a =>
            {
                a.vehicleId = "car-01";
                a.bodyScale = new Vector3(1.8f, 1.1f, 3.2f);
                a.bodyColor = Color.red;
            });
            var track = Asset<TrackDefinitionAsset>(a =>
            {
                a.courseId = "harbor-hop";
                a.spawnPosition = new Vector3(0f, 0f, -60f);
                a.spawnHeadingDegrees = 0f;
                a.gates = new[]
                {
                    Gate("start", 0f, -60f, 0f, 1f),
                    Gate("gate-1", 0f, 25f, 0f, 1f)
                };
                a.shortcutLanes = lanes;
            });
            var evt = Asset<EventDefinitionAsset>(a =>
            {
                a.eventId = "harbor-hop-car-circuit";
                a.track = track;
                a.laps = 1;
                a.countdownTicks = 3;
                a.vehicleIds = new[] { "car-01" };
            });
            Inject(world, "vehicles", new[] { car });
            Inject(world, "track", track);
            Inject(world, "events", new[] { evt });
            Inject(world, "contentVersion", "full-content-play");
        }

        [Test]
        public void AuthoredShortcutLanesReachTheLoadedCourseUnchanged()
        {
            WireWorld(new[]
            {
                Lane("harbor-shortcut-east", 44f, 10f),
                Lane("harbor-shortcut-west", -30f, -10f)
            });

            var catalog = world.Load();
            Assert.That(catalog, Is.Not.Null,
                "a course with two legal shortcut lanes must load");
            var course = catalog.Courses["harbor-hop"];
            Assert.That(course.shortcutZones, Has.Count.EqualTo(2),
                "every authored lane reaches the runtime course");
            Assert.That(course.shortcutZones[0].id,
                Is.EqualTo("harbor-shortcut-east"));
            Assert.That(course.shortcutZones[0].x, Is.EqualTo(44f));
            Assert.That(course.shortcutZones[0].dirZ, Is.EqualTo(1f));
            Assert.That(course.shortcutZones[1].id,
                Is.EqualTo("harbor-shortcut-west"));
            // The zones are traversal detectors, not gates: the
            // mandatory checkpoint order stays exactly the authored
            // two gates.
            Assert.That(course.checkpoints, Has.Count.EqualTo(2));
            Assert.That(course.checkpoints[0].id, Is.EqualTo("start"));
            Assert.That(course.checkpoints[1].id,
                Is.EqualTo("gate-1"));
        }

        [Test]
        public void CourseWithoutShortcutLanesLoadsWithAnEmptyZoneSet()
        {
            WireWorld(null);

            var catalog = world.Load();
            Assert.That(catalog, Is.Not.Null,
                "generic catalogs may author no shortcuts — the "
                + "two-per-course rule is production validation's");
            Assert.That(catalog.Courses["harbor-hop"].shortcutZones,
                Is.Empty);
        }

        [Test]
        public void MalformedShortcutLaneFailsTheLoadHonestly()
        {
            WireWorld(new[] { Lane("", 44f, 10f) });
            LogAssert.Expect(LogType.Error, new Regex(
                "DrivingWorld: .*shortcut\\.id"));

            Assert.That(world.Load(), Is.Null,
                "an empty lane id must fail conversion, not load a "
                + "course with an anonymous detector");
        }

        [Test]
        public void DuplicateShortcutLaneIdFailsTheLoadHonestly()
        {
            WireWorld(new[]
            {
                Lane("harbor-shortcut-east", 44f, 10f),
                Lane("harbor-shortcut-east", -30f, -10f)
            });
            LogAssert.Expect(LogType.Error, new Regex(
                "DrivingWorld: .*shortcut\\.duplicate"));

            Assert.That(world.Load(), Is.Null,
                "two lanes sharing a metric id would double-credit");
        }
    }
}
