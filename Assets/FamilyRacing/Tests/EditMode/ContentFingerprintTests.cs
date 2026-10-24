using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // Task 9: the LAN physical content contract is not the display
    // string alone — the deterministic tuning/course fingerprint must be
    // stable across platforms and must detect any canonical tuning or
    // course delta before a slot is allocated. These tests pin the
    // fingerprint contract: stable, culture-independent, order-free,
    // and sensitive to every replay-relevant value.
    public sealed class ContentFingerprintTests
    {
        private static readonly System.Text.RegularExpressions.Regex Hex64 =
            new System.Text.RegularExpressions.Regex("^[0-9a-f]{64}$");

        private static VehicleTuning PhysicalCar(string id)
        {
            var tuning = VehicleTuning.Car01();
            tuning.vehicleId = id;
            tuning.simulationBackend = VehicleSimulationBackend.PhysicalAssisted;
            tuning.physics = VehiclePhysicsTuning.Car01();
            return tuning;
        }

        private static CourseDefinition TestCourse()
        {
            var course = new CourseDefinition
            {
                id = "course-fp",
                spawnX = 0f,
                spawnY = 0f,
                spawnZ = -12f,
                spawnHeading = 0f
            };
            course.checkpoints.Add(new CheckpointGate
            {
                id = "start", x = 0f, z = 0f, halfWidth = 6f,
                dirX = 0f, dirZ = 1f, recoverX = 0f, recoverZ = -3f
            });
            course.checkpoints.Add(new CheckpointGate
            {
                id = "gate-1", x = 0f, z = 16f, halfWidth = 6f,
                dirX = 0f, dirZ = 1f, recoverX = 0f, recoverZ = 13f
            });
            for (var slot = 0; slot < 4; slot++)
                course.spawnSlots.Add(new MotorPose
                {
                    x = (slot - 1.5f) * 4f,
                    z = -16f - (slot % 2) * 4f
                });
            course.route.Add(new RouteWaypoint { x = 0f, z = -16f, radius = 4f });
            course.route.Add(new RouteWaypoint { x = 0f, z = 0f, radius = 5f });
            course.route.Add(new RouteWaypoint { x = 0f, z = 14f, radius = 4f });
            course.recoveryBounds.Add(new RecoveryVolume
            {
                minX = -30f, minY = -5f, minZ = -30f,
                maxX = 30f, maxY = 20f, maxZ = 40f
            });
            course.safePoses.Add(new MotorPose { x = 0f, z = 8f });
            course.safePoses.Add(new MotorPose { x = -6f, z = 8f });
            return course;
        }

        private static EventDefinition TestEvent(string id)
        {
            return new EventDefinition
            {
                id = id,
                courseId = "course-fp",
                laps = 1,
                countdownTicks = 0,
                vehicleIds = new List<string> { "car-01", "car-02" },
                recovery = new RecoverySettings(),
                completionCoins = 10
            };
        }

        private static DrivingContentCatalog Build(
            string version = "physics-1",
            VehicleSimulationBackend backend =
                VehicleSimulationBackend.PhysicalAssisted)
        {
            return DrivingContentCatalog.Create(
                version,
                new[] { PhysicalCar("car-02"), PhysicalCar("car-01") },
                new[] { TestCourse() },
                new[] { TestEvent("evt-2"), TestEvent("evt-1") },
                backend);
        }

        private static DrivingContentCatalog BuildReordered()
        {
            return DrivingContentCatalog.Create(
                "physics-2",
                new[] { PhysicalCar("car-01"), PhysicalCar("car-02") },
                new[] { TestCourse() },
                new[] { TestEvent("evt-1"), TestEvent("evt-2") },
                VehicleSimulationBackend.PhysicalAssisted);
        }

        [Test]
        public void FingerprintIsDeterministicAndHex64()
        {
            var first = Build().CompatibilityFingerprint;
            var second = Build().CompatibilityFingerprint;
            Assert.That(first, Is.Not.Null.And.Not.Empty);
            Assert.That(Hex64.IsMatch(first), Is.True,
                "the fingerprint is a lowercase 64-hex digest");
            Assert.That(first, Is.EqualTo(second),
                "identical canonical content must fingerprint identically");
        }

        [Test]
        public void FingerprintIgnoresContentVersionAndInsertionOrder()
        {
            // The human-readable compatibility token is admitted
            // separately; the fingerprint binds tuned data only.
            var a = Build("physics-1").CompatibilityFingerprint;
            var b = BuildReordered().CompatibilityFingerprint;
            Assert.That(b, Is.EqualTo(a),
                "version strings and dictionary order are presentation, "
                + "not canonical content");
        }

        [Test]
        public void FingerprintBindsCatalogBackend()
        {
            var arcade = Build("arcade-1",
                VehicleSimulationBackend.Arcade);
            Assert.That(arcade.CompatibilityFingerprint,
                Is.Not.EqualTo(Build().CompatibilityFingerprint),
                "a backend mismatch must fence before any slot is dealt");
        }

        [Test]
        public void FingerprintChangesWithTunedValues()
        {
            var baseline = Build().CompatibilityFingerprint;

            var mass = Build();
            mass.Vehicles["car-01"].physics.massKg += 1f;
            Assert.That(mass.CompatibilityFingerprint,
                Is.Not.EqualTo(baseline), "vehicle mass");

            var wheel = Build();
            wheel.Vehicles["car-02"].physics.wheels[0].mountX += 0.01f;
            Assert.That(wheel.CompatibilityFingerprint,
                Is.Not.EqualTo(baseline), "wheel mount");

            var arcade = Build();
            arcade.Vehicles["car-01"].maxSpeed += 0.5f;
            Assert.That(arcade.CompatibilityFingerprint,
                Is.Not.EqualTo(baseline), "arcade envelope field");

            var checkpoint = Build();
            checkpoint.Courses["course-fp"].checkpoints[0].x += 0.5f;
            Assert.That(checkpoint.CompatibilityFingerprint,
                Is.Not.EqualTo(baseline), "checkpoint position");

            var spawn = Build();
            var slot = spawn.Courses["course-fp"].spawnSlots[2];
            slot.z -= 0.25f;
            spawn.Courses["course-fp"].spawnSlots[2] = slot;
            Assert.That(spawn.CompatibilityFingerprint,
                Is.Not.EqualTo(baseline), "spawn slot");

            var route = Build();
            route.Courses["course-fp"].route[1].x += 0.5f;
            Assert.That(route.CompatibilityFingerprint,
                Is.Not.EqualTo(baseline), "route waypoint");

            var bounds = Build();
            bounds.Courses["course-fp"].recoveryBounds[0].maxZ += 1f;
            Assert.That(bounds.CompatibilityFingerprint,
                Is.Not.EqualTo(baseline), "recovery volume");

            var laps = Build();
            laps.Events["evt-1"].laps = 2;
            Assert.That(laps.CompatibilityFingerprint,
                Is.Not.EqualTo(baseline), "event laps");

            var recovery = Build();
            recovery.Events["evt-2"].recovery.overturnedSeconds += 0.25f;
            Assert.That(recovery.CompatibilityFingerprint,
                Is.Not.EqualTo(baseline), "recovery rule");

            var coins = Build();
            coins.Events["evt-1"].completionCoins = 99;
            Assert.That(coins.CompatibilityFingerprint,
                Is.Not.EqualTo(baseline), "reward table");
        }

        [Test]
        public void FingerprintDetectsSubLsbFloatDelta()
        {
            var baseline = Build().CompatibilityFingerprint;
            var delta = Build();
            delta.Courses["course-fp"].checkpoints[0].halfWidth += 1e-6f;
            Assert.That(delta.CompatibilityFingerprint,
                Is.Not.EqualTo(baseline),
                "canonicalization must not round tuned floats");
        }

        [Test]
        public void PhysicalCatalogCarriesTask9ContentIdentity()
        {
            var catalog = Build();
            Assert.That(catalog.CompatibilityVersion,
                Is.EqualTo("physics-1|sim=1"),
                "the content token binds the physical backend");
        }

        // --- M6 Task 1: ordered multiworld gameplay fingerprints -------

        [Test]
        public void GameplayFingerprintBindsWorldOrderAndEveryWorld()
        {
            var baseline = GameplayCatalogTests.MultiCatalog();
            var baselineFingerprint = baseline.CompatibilityFingerprint;
            Assert.That(Hex64.IsMatch(baselineFingerprint), Is.True);

            // Authored world order is progression content: the same three
            // worlds in a different order are a different adventure.
            var reordered = GameplayCatalogTests.MultiCampaigns();
            var swap = reordered[1];
            reordered[1] = reordered[2];
            reordered[2] = swap;
            var shuffled = GameplayCatalogTests.MultiCatalog(reordered);
            Assert.That(shuffled.CompatibilityFingerprint,
                Is.Not.EqualTo(baselineFingerprint),
                "authored world order is content, not presentation");

            // Event order within a world is bound too.
            var eventsSwapped = GameplayCatalogTests.MultiCatalog();
            var ids = eventsSwapped.Campaigns[0].regularEventIds;
            var first = ids[0];
            ids[0] = ids[1];
            ids[1] = first;
            Assert.That(eventsSwapped.CompatibilityFingerprint,
                Is.Not.EqualTo(baselineFingerprint),
                "event order within a world");

            // A later world's gate is part of the token.
            var gated = GameplayCatalogTests.MultiCatalog();
            gated.Campaigns[2].requiredRegionalStars = 31;
            Assert.That(gated.CompatibilityFingerprint,
                Is.Not.EqualTo(baselineFingerprint),
                "a later world's star gate");
            var completions = GameplayCatalogTests.MultiCatalog();
            completions.Campaigns[1].requiredEventCompletions = 23;
            Assert.That(completions.CompatibilityFingerprint,
                Is.Not.EqualTo(baselineFingerprint),
                "a later world's completion gate");

            // A later world's finale reward association is part of it.
            var rewarded = GameplayCatalogTests.MultiCatalog();
            rewarded.Campaigns[1].finales[0].rewardVehicleId = "car-04";
            Assert.That(rewarded.CompatibilityFingerprint,
                Is.Not.EqualTo(baselineFingerprint),
                "a later world's finale reward vehicle");

            // A later world's authored course pickup data is part of it.
            var sited = GameplayCatalogTests.MultiCatalog();
            sited.Driving.Courses["aurora-pass"].pickupSites.Add(
                new GadgetPickupSite { id = "aurora-site", x = 3f, z = 9f });
            Assert.That(sited.CompatibilityFingerprint,
                Is.Not.EqualTo(baselineFingerprint),
                "a later world's course pickup data");

            // Translated/cosmetic display names never move the token.
            var renamed = GameplayCatalogTests.MultiCatalog();
            renamed.Campaigns[0].nameEn = "Sunny Isle";
            renamed.Campaigns[1].nameEn = "Redrock";
            renamed.Campaigns[1].nameHe = "רדרוק";
            renamed.Campaigns[2].nameHe = "סנואי";
            Assert.That(renamed.CompatibilityFingerprint,
                Is.EqualTo(baselineFingerprint),
                "translated names are presentation, not content");
        }
    }
}
