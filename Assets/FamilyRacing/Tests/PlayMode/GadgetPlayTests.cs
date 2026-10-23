using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FamilyRacing.Tests
{
    // M5 Task 5 PlayMode proof for the gadget loop on the real driving
    // stack: the authored GadgetCatalogAsset converts through
    // DrivingWorld, the service's session-owned gadget world rides
    // driving.gadgets on every snapshot, a real drive over a real
    // course collects the pickup, the same input.set gadget bit uses it
    // (edge-latched — a held press never spends twice), and the pooled
    // GadgetPresenter mirrors exactly what the authoritative world
    // publishes and retires honestly when the race ends.
    public sealed class GadgetPlayTests
    {
        private const string RunId = "gadget-play-test";

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
                "family-racing-gadget-play-" + Guid.NewGuid().ToString("N"));
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

        private T Asset<T>(Action<T> configure = null)
            where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>();
            if (configure != null) configure(asset);
            assets.Add(asset);
            return asset;
        }

        private static void Inject(object target, string field, object value)
        {
            target.GetType().GetField(field,
                BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(target, value);
        }

        // A flat slab with perimeter walls so the arcade sim's probes
        // and sweeps land on real colliders.
        private GameObject BuildCourseSource()
        {
            var root = new GameObject("test-course");
            Box(root, "Ground", new Vector3(0f, -0.5f, 0f),
                new Vector3(240f, 1f, 160f));
            Box(root, "WallWest", new Vector3(-119.5f, 1.5f, 0f),
                new Vector3(1f, 3f, 160f));
            Box(root, "WallEast", new Vector3(119.5f, 1.5f, 0f),
                new Vector3(1f, 3f, 160f));
            Box(root, "WallNorth", new Vector3(0f, 1.5f, 79.5f),
                new Vector3(240f, 3f, 1f));
            Box(root, "WallSouth", new Vector3(0f, 1.5f, -79.5f),
                new Vector3(240f, 3f, 1f));
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

        // The pinned authored numbers the rules tests lock in — the
        // asset row mirrors them verbatim.
        private GadgetCatalogAsset AuthoredGadgets()
        {
            return Asset<GadgetCatalogAsset>(a =>
            {
                a.pickupRespawnTicks = 300;
                a.heldSlotCapacity = 1;
                a.gadgets = new[]
                {
                    new GadgetCatalogAsset.GadgetRow
                    {
                        gadgetId = "turbo-01", kind = GadgetKind.Turbo,
                        durationTicks = 60,
                        accelerationMultiplier = 1.25f,
                        speedCapMultiplier = 1.10f
                    },
                    new GadgetCatalogAsset.GadgetRow
                    {
                        gadgetId = "shield-01", kind = GadgetKind.BubbleShield,
                        charges = 1
                    },
                    new GadgetCatalogAsset.GadgetRow
                    {
                        gadgetId = "foam-01", kind = GadgetKind.FoamTrap,
                        patchRadiusMeters = 3f, patchLifetimeTicks = 180,
                        slowMultiplier = 0.70f, slowDurationTicks = 45,
                        immunityTicks = 60
                    }
                };
            });
        }

        // The practice course: spawn faces +z, one pickup site dead
        // ahead on the drive line (auto-acceleration carries the car
        // through it), gates far enough out that a finish can never
        // interleave the gadget assertions.
        private void WireWorld(GadgetCatalogAsset gadgets)
        {
            var car = Asset<VehicleDefinitionAsset>(a =>
            {
                a.vehicleId = "car-01";
                a.bodyScale = new Vector3(1.8f, 1.1f, 3.2f);
                a.bodyColor = Color.red;
            });
            var track = Asset<TrackDefinitionAsset>(a =>
            {
                a.courseId = "gadget-course";
                a.spawnPosition = new Vector3(0f, 0f, -60f);
                a.spawnHeadingDegrees = 0f;
                a.gates = new[]
                {
                    new TrackDefinitionAsset.Gate
                    {
                        id = "start",
                        center = new Vector3(0f, 0f, -60f),
                        halfWidth = 10f,
                        direction = new Vector2(0f, 1f),
                        recoverPosition = new Vector3(0f, 0f, -63f),
                        recoverHeadingDegrees = 0f
                    },
                    new TrackDefinitionAsset.Gate
                    {
                        id = "gate-1",
                        center = new Vector3(0f, 0f, 60f),
                        halfWidth = 10f,
                        direction = new Vector2(0f, 1f),
                        recoverPosition = new Vector3(0f, 0f, 57f),
                        recoverHeadingDegrees = 0f
                    }
                };
                a.pickupSites = new[]
                {
                    new TrackDefinitionAsset.PickupSite
                    {
                        id = "site-ahead",
                        center = new Vector3(0f, 0f, -20f),
                        radiusMeters = 3f,
                        verticalRangeMeters = 2.5f
                    }
                };
            });
            var evt = Asset<EventDefinitionAsset>(a =>
            {
                a.eventId = "gadget-practice";
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
            if (gadgets != null) Inject(world, "gadgetCatalog", gadgets);
        }

        private void StartRace()
        {
            service.Execute(Command("profile.create",
                P("slot", "0", "language", "en")));
            var started = service.Execute(Command("race.start",
                P("vehicleId", "car-01", "eventId", "gadget-practice")));
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied),
                "race.start failed: " + started.code);
            Step(3); // countdown
            Assert.That(service.Snapshot().driving.phase,
                Is.EqualTo("running"));
        }

        private void Step(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                service.StepDriving(world.MotorWorld);
                service.AdvanceTick();
            }
        }

        private void PressGadget()
        {
            var applied = service.Execute(Command("input.set",
                P("steering", "0", "drift", "false", "boost", "false",
                    "gadget", "true", "recover", "false",
                    "changeCamera", "false", "pause", "false",
                    "durationTicks", "1")));
            Assert.That(applied.status, Is.EqualTo(CommandResult.Applied),
                "input.set gadget=true rejected: " + applied.code);
        }

        private void ReleaseGadget()
        {
            service.Execute(Command("input.set",
                P("steering", "0", "drift", "false", "boost", "false",
                    "gadget", "false", "recover", "false",
                    "changeCamera", "false", "pause", "false",
                    "durationTicks", "150")));
        }

        private GameCommand Command(string action,
            Dictionary<string, string> parameters)
        {
            return new GameCommand(1, RunId, Guid.NewGuid().ToString("N"),
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

        private GadgetRacerState LocalRow()
        {
            var gadgets = service.Snapshot().driving.gadgets;
            if (gadgets == null || gadgets.racers.Count == 0) return null;
            return gadgets.racers[0];
        }

        // --- content binding ------------------------------------------

        [Test]
        public void AuthoredGadgetCatalogConvertsWithPinnedDefaults()
        {
            WireWorld(AuthoredGadgets());
            world.Load();
            var catalog = world.Gadgets;
            Assert.That(catalog, Is.Not.Null,
                "the gadget section converts into a validated catalog");
            Assert.That(catalog.pickupRespawnTicks, Is.EqualTo(300));
            Assert.That(catalog.heldSlotCapacity, Is.EqualTo(1));
            var turbo = catalog.Gadget("turbo-01");
            Assert.That(turbo.durationTicks, Is.EqualTo(60));
            Assert.That(turbo.accelerationMultiplier,
                Is.EqualTo(1.25f).Within(1e-6f));
            Assert.That(turbo.speedCapMultiplier,
                Is.EqualTo(1.10f).Within(1e-6f));
            var foam = catalog.Gadget("foam-01");
            Assert.That(foam.patchRadiusMeters,
                Is.EqualTo(3f).Within(1e-6f));
            Assert.That(foam.patchLifetimeTicks, Is.EqualTo(180));
            Assert.That(foam.slowMultiplier,
                Is.EqualTo(0.70f).Within(1e-6f));
            Assert.That(foam.slowDurationTicks, Is.EqualTo(45));
            Assert.That(foam.immunityTicks, Is.EqualTo(60));
        }

        [Test]
        public void InvalidGadgetRowFailsTheWholeConversion()
        {
            var bad = AuthoredGadgets();
            bad.gadgets[0].kind = "warp-drive";
            WireWorld(bad);
            LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex(
                    "invalid gadget"));
            world.Load();
            Assert.That(world.Gadgets, Is.Null,
                "a malformed gadget section resolves to absent content");
        }

        // --- session flow over the real course -------------------------

        [TestCase(false)]
        [TestCase(true)]
        public void DriveThroughCollectsAndGadgetInputUsesTheItem(bool releaseBeforeSimulation)
        {
            WireWorld(AuthoredGadgets());
            service.SetDrivingContent(world.Load());
            service.SetGadgetCatalog(world.Gadgets);
            Assert.That(world.Gadgets, Is.Not.Null);
            StartRace();
            world.ApplyFrame(service.Snapshot());
            Physics.SyncTransforms();

            // The session publishes a gadget world with the authored
            // pickup column on the drive line.
            var snapshot = service.Snapshot();
            Assert.That(snapshot.driving.gadgets, Is.Not.Null,
                "the session owns a gadget world when content binds");
            Assert.That(snapshot.driving.gadgets.pickups.Count,
                Is.EqualTo(1));
            Assert.That(snapshot.driving.gadgets.pickups[0].siteId,
                Is.EqualTo("site-ahead"));

            // Auto-acceleration sweeps the car through the site column:
            // ~40 m from spawn, reached well inside 200 ticks.
            var collected = false;
            for (var i = 0; i < 240 && !collected; i++)
            {
                Step(1);
                var row = LocalRow();
                collected = row != null
                    && !string.IsNullOrEmpty(row.heldItemId);
            }
            Assert.That(collected,
                "driving through the pickup column grants one item");
            var state = service.Snapshot();
            Assert.That(state.driving.gadgets.pickups[0].active,
                Is.False, "the collected site sits on its respawn clock");
            var granted = LocalRow().heldItemId;
            Assert.That(LocalRow().heldKind, Is.Not.Empty,
                "the kind tag rides the grant for HUD/presentation");

            // The same input.set gadget bit the touch button and the E
            // key emit uses the held item — edge-latched, attributed to
            // the human seat.
            PressGadget();
            if (releaseBeforeSimulation) ReleaseGadget();
            Step(1);
            var events = service.Snapshot().drivingEvents;
            var used = false;
            foreach (var ev in events)
                if (ev.kind == "gadget.used" && ev.detail == granted
                    && (ev.attribution == "" || ev.attribution == "human"))
                    used = true;
            Assert.That(used,
                "gadget.used publishes with the granted item id");
            Assert.That(LocalRow().heldItemId, Is.Empty,
                "the single slot empties on use");
            ReleaseGadget();
        }

        [Test]
        public void PressOnEmptySlotMintsNothingAndOnePressSpendsOneItem()
        {
            WireWorld(AuthoredGadgets());
            service.SetDrivingContent(world.Load());
            service.SetGadgetCatalog(world.Gadgets);
            Assert.That(world.Gadgets, Is.Not.Null);
            StartRace();
            world.ApplyFrame(service.Snapshot());
            Physics.SyncTransforms();

            // A press on an empty slot is a no-op: the service-level
            // pulse rides one tick, rules see an empty slot, no event.
            PressGadget();
            Step(1);
            foreach (var ev in service.Snapshot().drivingEvents)
                Assert.That(ev.kind, Is.Not.EqualTo("gadget.used"),
                    "an empty slot can never mint a use fact");
            Assert.That(LocalRow().heldItemId, Is.Empty);

            // Drive through the site, then one press spends the item.
            var collected = false;
            for (var i = 0; i < 240 && !collected; i++)
            {
                Step(1);
                var row = LocalRow();
                collected = row != null
                    && !string.IsNullOrEmpty(row.heldItemId);
            }
            Assert.That(collected, "the drive-over grant lands");
            PressGadget();
            Step(1);
            var uses = 0;
            foreach (var ev in service.Snapshot().drivingEvents)
                if (ev.kind == "gadget.used") uses++;
            Assert.That(uses, Is.EqualTo(1),
                "one press spends the one held item — the edge latch "
                + "can never double-spend");
            Assert.That(LocalRow().heldItemId, Is.Empty);

            // A second press with the slot empty again mints nothing.
            PressGadget();
            Step(1);
            uses = 0;
            foreach (var ev in service.Snapshot().drivingEvents)
                if (ev.kind == "gadget.used") uses++;
            Assert.That(uses, Is.EqualTo(1),
                "no further press fabricates another item");
        }

        [Test]
        public void GadgetFreeContentYieldsNoGadgetWorld()
        {
            WireWorld(null); // no gadget catalog bound
            service.SetDrivingContent(world.Load());
            service.SetGadgetCatalog(null);
            StartRace();
            Assert.That(service.Snapshot().driving.gadgets, Is.Null,
                "absent authored content means a gadget-free session, "
                + "never a fabricated catalog");
        }

        // --- presentation ----------------------------------------------

        [Test]
        public void PresenterMirrorsWorldThenRetiresWithRace()
        {
            WireWorld(AuthoredGadgets());
            service.SetDrivingContent(world.Load());
            service.SetGadgetCatalog(world.Gadgets);
            StartRace();
            var presenter = host.AddComponent<GadgetPresenter>();

            presenter.ApplyFrame(service.Snapshot(), 1f / 60f);
            Assert.That(presenter.ActivePickupCount, Is.EqualTo(1),
                "one authored site -> one pooled pickup view");

            Step(240); // drive through the site
            presenter.ApplyFrame(service.Snapshot(), 1f / 60f);
            // The collected pickup stays a dim ghost on its respawn
            // clock — the view still tracks the site, not a live item.
            Assert.That(presenter.ActivePickupCount, Is.EqualTo(1));

            service.Execute(Command("race.exit", null));
            presenter.ApplyFrame(service.Snapshot(), 1f / 60f);
            Assert.That(presenter.ActivePickupCount, Is.EqualTo(0),
                "ending the race retires every gadget view");
            Assert.That(presenter.ActivePatchCount, Is.EqualTo(0));
            Assert.That(presenter.ActiveShieldCount, Is.EqualTo(0));
        }
    }
}
