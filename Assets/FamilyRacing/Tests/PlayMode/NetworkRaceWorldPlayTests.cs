using System;
using System.Collections.Generic;
using System.Reflection;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FamilyRacing.Tests
{
    // Task 4 end-to-end proof on real physics: a four-AI network race
    // runs the real graybox course — walls, barriers, islands and the
    // landmark ramp — through UnityMotorWorld probes/sweeps and finishes
    // under the normal checkpoint/recovery rules, and DrivingWorld shows
    // one participant-keyed vehicle view per racer with deterministic
    // cleanup when the roster shrinks or clears.
    public sealed class NetworkRaceWorldPlayTests
    {
        private GameObject host;
        private DrivingWorld world;
        private GameObject courseSource;
        private readonly List<Object> assets = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            host = new GameObject("network-race-world-host");
            host.SetActive(false);
            world = host.AddComponent<DrivingWorld>();
            courseSource = BuildCourseSource();
            host.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null) Object.Destroy(host);
            if (courseSource != null) Object.Destroy(courseSource);
            foreach (var asset in assets)
                if (asset != null) Object.Destroy(asset);
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

        // The generated graybox layout verbatim: ground slab, perimeter
        // walls, both chicane barriers, the landmark ramp, and both island
        // blocks that split the route into main/shortcut branches.
        private GameObject BuildCourseSource()
        {
            var root = new GameObject("test-course");
            Box(root, "Ground", new Vector3(0f, -0.5f, 0f),
                new Vector3(240f, 1f, 160f), Quaternion.identity);
            Box(root, "WallWest", new Vector3(-119.5f, 1.5f, 0f),
                new Vector3(1f, 3f, 160f), Quaternion.identity);
            Box(root, "WallEast", new Vector3(119.5f, 1.5f, 0f),
                new Vector3(1f, 3f, 160f), Quaternion.identity);
            Box(root, "WallNorth", new Vector3(0f, 1.5f, 79.5f),
                new Vector3(240f, 3f, 1f), Quaternion.identity);
            Box(root, "WallSouth", new Vector3(0f, 1.5f, -79.5f),
                new Vector3(240f, 3f, 1f), Quaternion.identity);
            Box(root, "BarrierA", new Vector3(25f, 1f, -25f),
                new Vector3(6f, 2f, 30f), Quaternion.identity);
            Box(root, "BarrierB", new Vector3(-30f, 1f, 10f),
                new Vector3(30f, 2f, 6f), Quaternion.identity);
            Box(root, "Ramp", new Vector3(-60f, 1.2f, 30f),
                new Vector3(12f, 0.6f, 16f), Quaternion.Euler(-14f, 0f, 0f));
            Box(root, "IslandNorthEast", new Vector3(32f, 1f, 44f),
                new Vector3(22f, 2f, 14f), Quaternion.identity);
            Box(root, "IslandSouthEast", new Vector3(72f, 1f, -32f),
                new Vector3(20f, 2f, 16f), Quaternion.identity);
            root.SetActive(false);
            return root;
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

        // The authored AI route for the real course: north to gate-1,
        // west onto the ramp toe, over the crest, north around
        // IslandNorthEast to gate-2, down the east edge to gate-3, under
        // IslandSouthEast to gate-4, then south of the line and back
        // across start/finish.
        private static TrackDefinitionAsset.RoutePoint[] TestRoute()
        {
            return new[]
            {
                Rp(0f, -30f), Rp(0f, 10f), Rp(0f, 30f), Rp(-30f, 24f), Rp(-54f, 14f),
                Rp(-60f, 30f), Rp(-60f, 46f), Rp(-40f, 58f), Rp(-10f, 62f),
                Rp(20f, 62f), Rp(48f, 60f), Rp(62f, 55f), Rp(82f, 42f),
                Rp(95f, 18f), Rp(95f, -14f), Rp(99f, -34f), Rp(92f, -48f),
                Rp(68f, -60f), Rp(38f, -56f), Rp(10f, -62f), Rp(0f, -66f),
                Rp(0f, -48f)
            };
        }

        private static TrackDefinitionAsset.RoutePoint Rp(float x, float z)
        {
            return new TrackDefinitionAsset.RoutePoint { x = x, z = z, radius = 0f };
        }

        private void WireWorld()
        {
            var car = Asset<VehicleDefinitionAsset>(a =>
            {
                a.vehicleId = "car-01";
                a.bodyScale = new Vector3(1.8f, 1.1f, 3.2f);
                a.bodyColor = Color.red;
            });
            var moto = Asset<VehicleDefinitionAsset>(a =>
            {
                a.vehicleId = "motorcycle-01";
                a.bodyScale = new Vector3(0.9f, 1.4f, 2.2f);
                a.bodyColor = Color.green;
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
                a.spawnSlots = new[]
                {
                    Spawn(-5f, -57f), Spawn(5f, -57f),
                    Spawn(-5f, -62f), Spawn(5f, -62f)
                };
                a.aiRoute = TestRoute();
            });
            var carEvent = Asset<EventDefinitionAsset>(a =>
            {
                a.eventId = "lan-car-01";
                a.track = track;
                a.laps = 1;
                a.countdownTicks = 3;
                a.vehicleIds = new[] { "car-01" };
            });
            var motoEvent = Asset<EventDefinitionAsset>(a =>
            {
                a.eventId = "lan-motorcycle-01";
                a.track = track;
                a.laps = 1;
                a.countdownTicks = 3;
                a.vehicleIds = new[] { "motorcycle-01" };
            });
            Inject(world, "vehicles", new[] { car, moto });
            Inject(world, "track", track);
            Inject(world, "events", new[] { carEvent, motoEvent });
            Inject(world, "coursePrefab", courseSource);
            Inject(world, "contentVersion", "play-net-1");
        }

        private static TrackDefinitionAsset.SpawnSlot Spawn(float x, float z)
        {
            return new TrackDefinitionAsset.SpawnSlot
            {
                position = new Vector3(x, 0f, z),
                headingDegrees = 0f
            };
        }

        private static List<NetworkRacerSpec> AiRoster(string vehicleId)
        {
            var roster = new List<NetworkRacerSpec>();
            for (var i = 0; i < 4; i++)
                roster.Add(new NetworkRacerSpec
                {
                    participantId = "ai-" + i,
                    slot = i,
                    vehicleId = vehicleId,
                    controlSource = "ai"
                });
            return roster;
        }

        private static List<DrivingEvent> EventsOf(List<DrivingEvent> events,
            string kind)
        {
            var found = new List<DrivingEvent>();
            foreach (var ev in events)
                if (ev.kind == kind) found.Add(ev);
            return found;
        }

        // Runs one four-AI race on the real course to completion (or the
        // bound) and returns every emitted event.
        private NetworkRaceState RunRace(string eventId, string classId,
            string vehicleId, int maxTicks, List<DrivingEvent> events)
        {
            var catalog = world.Load();
            Assert.That(catalog, Is.Not.Null, "generated definitions must validate");
            var ok = NetworkRace.TryCreate("sess-1", "race-1", catalog,
                eventId, classId, AiRoster(vehicleId), out var race, out var error);
            Assert.That(ok, Is.True, "TryCreate failed: " + error);

            // Activate the course exactly like a race frame does, then
            // sync physics so probes/sweeps see the real colliders.
            var state = race.Snapshot();
            world.ApplyRaceFrame(state);
            Physics.SyncTransforms();
            for (var t = 0; t < maxTicks && state.phase != "finished"; t++)
            {
                state = race.Step(null, world.MotorWorld);
                events.AddRange(state.events);
                world.ApplyRaceFrame(state);
            }
            return state;
        }

        private GameObject FindRacerView(string participantId)
        {
            foreach (var transform in host.GetComponentsInChildren<Transform>(true))
                if (transform.name == "RacerVehicle-" + participantId)
                    return transform.gameObject;
            return null;
        }

        [Test]
        public void AiCarsFinishRealCourseOverTheRamp()
        {
            WireWorld();
            var events = new List<DrivingEvent>();
            var state = RunRace("lan-car-01", "car", "car-01", 3000, events);

            Assert.That(state.phase, Is.EqualTo("finished"),
                "four AI cars must complete the real course inside the bound");
            foreach (var racer in state.racers)
            {
                Assert.That(racer.finished, Is.True,
                    "AI racer " + racer.participantId + " must finish, not DNF");
                Assert.That(racer.dnf, Is.False);
            }
            Assert.That(state.result, Is.Not.Null);
            Assert.That(state.result.racers.Count, Is.EqualTo(4));
            foreach (var entry in state.result.racers)
                Assert.That(entry.attribution, Is.EqualTo("ai"));

            // The ramp leg is real: someone actually left the ground near
            // it, and no recovery cheated a checkpoint — every gate event
            // stays in strict travel order per racer.
            Assert.That(EventsOf(events, "motor.becameAirborne").Count,
                Is.GreaterThan(0),
                "the authored route must put the AI over the landmark ramp");
            foreach (var racer in state.racers)
            {
                var order = new List<string>();
                foreach (var ev in events)
                    if (ev.kind == "checkpoint.crossed"
                        && ev.participantId == racer.participantId)
                        order.Add(ev.detail);
                Assert.That(order, Is.EqualTo(new[]
                    { "gate-1", "gate-2", "gate-3", "gate-4", "start" }),
                    racer.participantId + " crossed gates out of order: "
                    + string.Join(",", order));
            }
        }

        [Test]
        public void AiMotorcyclesFinishRealCourseOverTheRamp()
        {
            WireWorld();
            var events = new List<DrivingEvent>();
            var state = RunRace("lan-motorcycle-01", "motorcycle",
                "motorcycle-01", 3000, events);

            Assert.That(state.phase, Is.EqualTo("finished"),
                "four AI motorcycles must complete the real course");
            foreach (var racer in state.racers)
                Assert.That(racer.finished, Is.True,
                    "AI racer " + racer.participantId + " must finish, not DNF");
            Assert.That(EventsOf(events, "motor.becameAirborne").Count,
                Is.GreaterThan(0),
                "the ramp path must launch the motorcycles too");
        }

        [Test]
        public void RacerViewsTrackParticipantsWithDeterministicCleanup()
        {
            WireWorld();
            world.Load();

            // Fabricated race state: three live participants.
            var race = new NetworkRaceState();
            for (var i = 0; i < 3; i++)
                race.racers.Add(new NetworkRacerState
                {
                    participantId = "p" + i,
                    slot = i,
                    vehicleId = i == 2 ? "motorcycle-01" : "car-01",
                    session = new DrivingSessionState
                    {
                        motor = new MotorState
                        {
                            x = i * 4f, y = 0f, z = -50f + i,
                            heading = 0f, grounded = true
                        },
                        input = new InputState()
                    }
                });
            world.ApplyRaceFrame(race);
            for (var i = 0; i < 3; i++)
            {
                var view = FindRacerView("p" + i);
                Assert.That(view, Is.Not.Null, "view for p" + i + " must exist");
                Assert.That(view.activeSelf, Is.True);
                Assert.That(view.transform.position.x,
                    Is.EqualTo(i * 4f).Within(0.01f));
                Assert.That(view.transform.position.z,
                    Is.EqualTo(-50f + i).Within(0.01f));
            }

            // A roster of five exceeds the participant capacity: only the
            // first four keyed views are ever built.
            race.racers.Add(new NetworkRacerState
            {
                participantId = "p4",
                slot = 4,
                vehicleId = "car-01",
                session = new DrivingSessionState
                {
                    motor = new MotorState { x = 0f, z = -40f, grounded = true },
                    input = new InputState()
                }
            });
            race.racers.Add(new NetworkRacerState
            {
                participantId = "p5",
                slot = 5,
                vehicleId = "car-01",
                session = new DrivingSessionState
                {
                    motor = new MotorState { x = 2f, z = -40f, grounded = true },
                    input = new InputState()
                }
            });
            world.ApplyRaceFrame(race);
            var viewCount = 0;
            foreach (var transform in host.GetComponentsInChildren<Transform>(true))
                if (transform.name.StartsWith("RacerVehicle-",
                    StringComparison.Ordinal)) viewCount++;
            Assert.That(viewCount,
                Is.LessThanOrEqualTo(NetworkSettings.ParticipantCapacity),
                "at most four participant views may exist");

            // Roster shrink: stale participant views are torn down
            // deterministically — only the still-present ids keep views.
            var shrunk = new NetworkRaceState();
            shrunk.racers.Add(race.racers[1]);
            shrunk.racers.Add(race.racers[2]);
            world.ApplyRaceFrame(shrunk);
            Assert.That(FindRacerView("p0"), Is.Null);
            Assert.That(FindRacerView("p1"), Is.Not.Null);
            Assert.That(FindRacerView("p2"), Is.Not.Null);
            Assert.That(FindRacerView("p3"), Is.Null);
            Assert.That(FindRacerView("p4"), Is.Null);

            // No race clears every view and the course.
            world.ApplyRaceFrame(null);
            foreach (var transform in host.GetComponentsInChildren<Transform>(true))
                Assert.That(transform.name.StartsWith("RacerVehicle-",
                        StringComparison.Ordinal), Is.False,
                    "a cleared race leaves no racer views behind");
        }
    }
}
