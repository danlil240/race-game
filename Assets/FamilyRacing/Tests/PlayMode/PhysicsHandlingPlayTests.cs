using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FamilyRacing.Tests
{
    // Handling qualification for the physical backend: progressive
    // rear-grip drift, bounded boost demand, airborne traction rules and
    // one end-to-end physical lap on the authored graybox layout —
    // geometry and course data mirrored from DrivingPrototypeSetup so
    // the check is reproducible in test code.
    public sealed class PhysicsHandlingPlayTests
    {
        private PhysicsCourseWorld world;
        private PhysicsVehicleSimulation sim;

        [TearDown]
        public void TearDown()
        {
            if (sim != null) sim.Dispose();
            sim = null;
            world = null;
        }

        private PhysicsVehicleSimulation NewCar(string name,
            VehiclePhysicsTuning physics, MotorState initialState,
            MotorState rulesBefore = null)
        {
            world = PhysicsCourseWorld.Create(name);
            sim = new PhysicsVehicleSimulation(world);
            sim.Initialize(new List<VehicleSpawn>
            {
                new VehicleSpawn
                {
                    participantId = "p1",
                    tuning = PhysicsTestWorld.PhysicalTuning("car-01",
                        physics),
                    initialState = initialState
                }
            });
            return sim;
        }

        private static float HorizontalSpeed(MotorState state)
        {
            var p = state.physics;
            return Mathf.Sqrt(p.velocityX * p.velocityX
                + p.velocityZ * p.velocityZ);
        }

        // --- airborne traction: no drive, no brake, no boost -------------

        [Test]
        public void AirborneWheelsCannotDriveOrBrake()
        {
            var physics = VehiclePhysicsTuning.Car01();
            // Remove drag/rolling resistance so the only possible
            // horizontal force is tire demand — any change in horizontal
            // speed while airborne can only be tire force.
            physics.dragCoefficient = 0f;
            physics.rollingResistance = 0f;
            // Dropped from 8 m at 8 m/s with a FULL brake held: airborne
            // wheels must apply neither drive nor brake force, so the
            // horizontal speed stays at the ballistic value while the
            // car falls with gravity.
            var spawn = PhysicsTestWorld.PhysicalSpawnState(physics,
                new Vector3(0f, 8f, 0f), 0f, forwardSpeed: 8f);
            spawn.physics.grounded = false;
            spawn.grounded = false;
            // Claim an active boost through the rule envelope: boost
            // traction must also be zero in the air.
            var rules = new MotorState { boostActive = true };
            NewCar("air-traction", physics, spawn);
            PhysicsTestWorld.FlatGround(world, 0f, 0f, 400f, 400f);

            var minHorizontal = float.MaxValue;
            var maxHorizontal = 0f;
            var airTicks = 0;
            for (var i = 0; i < 60; i++)
            {
                sim.Step(new List<VehicleTickRequest>
                {
                    PhysicsTestWorld.Request("p1",
                        PhysicsTestWorld.Drive(brake: 1f),
                        rulesBefore: rules)
                });
                var state = sim.Capture("p1");
                if (!state.grounded)
                {
                    airTicks++;
                    var h = HorizontalSpeed(state);
                    if (h < minHorizontal) minHorizontal = h;
                    if (h > maxHorizontal) maxHorizontal = h;
                }
            }
            Assert.That(airTicks, Is.GreaterThan(10),
                "setup: the car must actually be airborne for a "
                + "meaningful window");
            Assert.That(minHorizontal, Is.GreaterThan(7.6f),
                "a full brake held mid-flight must not decelerate the "
                + "car (min " + minHorizontal + ")");
            Assert.That(maxHorizontal, Is.LessThan(8.4f),
                "an active boost mid-flight must not accelerate the car "
                + "(max " + maxHorizontal + ")");
        }

        // --- boost: bounded drive demand --------------------------------

        [Test]
        public void BoostReachesBoostTargetButNotWhileBraking()
        {
            var physics = VehiclePhysicsTuning.Car01();
            var spawn = PhysicsTestWorld.PhysicalSpawnState(physics,
                new Vector3(0f, PhysicsTestWorld.SpawnY(physics), 0f),
                0f);
            var rules = new MotorState { boostActive = true };
            NewCar("boost-drive", physics, spawn);
            PhysicsTestWorld.FlatGround(world, 0f, 200f, 60f, 500f);

            for (var i = 0; i < 300; i++)
                sim.Step(new List<VehicleTickRequest>
                {
                    PhysicsTestWorld.Request("p1",
                        PhysicsTestWorld.Drive(), rulesBefore: rules)
                });
            var boosted = PhysicsTestWorld.ForwardSpeed(sim.Capture("p1"));
            Assert.That(boosted, Is.GreaterThan(physics.cruiseSpeed + 1f),
                "an active boost must drive past the cruise target "
                + "(speed " + boosted + " vs cruise "
                + physics.cruiseSpeed + ")");
            Assert.That(boosted, Is.LessThanOrEqualTo(
                    physics.boostSpeed + 0.5f),
                "boost is a bounded demand: it tapers at boostSpeed, "
                + "never beyond");

            // Held brake while boost is still claimed active: drive
            // force is suppressed and the car must actually shed speed.
            for (var i = 0; i < 30; i++)
                sim.Step(new List<VehicleTickRequest>
                {
                    PhysicsTestWorld.Request("p1",
                        PhysicsTestWorld.Drive(brake: 1f),
                        rulesBefore: rules)
                });
            var braking = PhysicsTestWorld.ForwardSpeed(sim.Capture("p1"));
            Assert.That(braking, Is.LessThan(boosted - 1f),
                "brake suppresses boost drive force (boosted " + boosted
                + " -> " + braking + " while braking)");
        }

        // --- drift: progressive rear lateral-grip relief -----------------

        [Test]
        public void DriftBuildsProgressivelyAndSlidesRear()
        {
            var physics = VehiclePhysicsTuning.Car01();
            var spawn = PhysicsTestWorld.PhysicalSpawnState(physics,
                new Vector3(0f, PhysicsTestWorld.SpawnY(physics), 0f),
                0f, forwardSpeed: 14f);
            NewCar("drift-ramp", physics, spawn);
            PhysicsTestWorld.FlatGround(world, 0f, 0f, 400f, 400f);
            // Settle onto the suspension before demanding the drift.
            for (var i = 0; i < 30; i++)
                sim.Step(new List<VehicleTickRequest>
                { PhysicsTestWorld.Request("p1", PhysicsTestWorld.Drive()) });

            var driftAt3Ticks = -1f;
            var driftAt20Ticks = -1f;
            var slipEarly = 0f;
            var slipLate = 0f;
            for (var i = 0; i < 60; i++)
            {
                sim.Step(new List<VehicleTickRequest>
                {
                    PhysicsTestWorld.Request("p1",
                        PhysicsTestWorld.Drive(steering: 0.8f,
                            drift: true))
                });
                var state = sim.Capture("p1").physics;
                if (i == 2)
                {
                    driftAt3Ticks = state.driftAmount;
                    slipEarly = Mathf.Abs(state.wheels[2].slipLateral)
                        + Mathf.Abs(state.wheels[3].slipLateral);
                }
                if (i == 19)
                {
                    driftAt20Ticks = state.driftAmount;
                    slipLate = Mathf.Abs(state.wheels[2].slipLateral)
                        + Mathf.Abs(state.wheels[3].slipLateral);
                }
            }
            Assert.That(driftAt3Ticks, Is.LessThan(0.4f).And.GreaterThan(0f),
                "drift must ramp in, not step: only a fraction of the "
                + "rear grip relief applies after ~0.1 s (got "
                + driftAt3Ticks + ")");
            Assert.That(driftAt20Ticks, Is.GreaterThan(0.9f),
                "by ~0.65 s the drift demand is fully engaged (got "
                + driftAt20Ticks + ")");
            Assert.That(slipLate, Is.GreaterThan(slipEarly + 0.5f),
                "rear slip must build as grip eases off (early "
                + slipEarly + " vs late " + slipLate + ")");
            // The recovery/restore contract: driftAmount rides the
            // persisted state so a restore clears it.
            var clean = PhysicsTestWorld.PhysicalSpawnState(physics,
                new Vector3(0f, PhysicsTestWorld.SpawnY(physics), 40f),
                0f);
            sim.Restore("p1", clean);
            Assert.That(sim.Capture("p1").physics.driftAmount,
                Is.EqualTo(0f),
                "a full controller reset clears drift engagement");
        }

        // --- end-to-end: one physical lap on the authored graybox -------

        private static CourseDefinition GrayboxCourse()
        {
            // Mirrors the generated graybox-01 TrackDefinitionAsset:
            // same spawn, gates, AI route, recovery bounds and safe
            // poses — the check is the authored layout, not the loader.
            var course = new CourseDefinition
            {
                id = "graybox-01",
                spawnX = 0f,
                spawnY = 0f,
                spawnZ = -60f,
                spawnHeading = 0f
            };
            course.checkpoints.AddRange(new[]
            {
                Gate("start", 0f, -60f, 0f, 1f),
                Gate("gate-1", 0f, 25f, 0f, 1f),
                Gate("gate-2", 55f, 55f, 1f, 0f),
                Gate("gate-3", 95f, 0f, 0f, -1f),
                Gate("gate-4", 50f, -55f, -1f, 0f)
            });
            // The authored AI route loop (from graybox-01.asset).
            float[,] route =
            {
                { 0f, -30f }, { 0f, 10f }, { 0f, 30f }, { -30f, 24f },
                { -54f, 14f }, { -60f, 30f }, { -60f, 46f }, { -40f, 58f },
                { -10f, 62f }, { 20f, 62f }, { 48f, 60f }, { 62f, 55f },
                { 82f, 42f }, { 95f, 18f }, { 95f, -14f }, { 99f, -34f },
                { 92f, -48f }, { 68f, -60f }, { 38f, -56f }, { 10f, -62f },
                { 0f, -66f }, { 0f, -48f }
            };
            for (var i = 0; i < route.GetLength(0); i++)
                course.route.Add(new RouteWaypoint
                {
                    x = route[i, 0], z = route[i, 1]
                });
            course.recoveryBounds.Add(new RecoveryVolume
            {
                minX = -125f, minY = -2f, minZ = -85f,
                maxX = 125f, maxY = 30f, maxZ = 85f
            });
            course.safePoses.AddRange(new[]
            {
                new MotorPose { x = 4.5f, y = 0f, z = -63f },
                new MotorPose { x = -4.5f, y = 0f, z = -63f },
                new MotorPose { x = 5f, y = 0f, z = 22f },
                new MotorPose
                    { x = 52f, y = 0f, z = 50.5f, heading = 1.5708f },
                new MotorPose
                    { x = 90.5f, y = 0f, z = -5f, heading = 3.1416f },
                new MotorPose
                    { x = 55f, y = 0f, z = -52f, heading = -1.5708f }
            });
            return course;
        }

        private static CheckpointGate Gate(string id, float x, float z,
            float dirX, float dirZ)
        {
            return new CheckpointGate
            {
                id = id, x = x, z = z, dirX = dirX, dirZ = dirZ,
                halfWidth = 12f,
                recoverX = x - dirX * 3f,
                recoverZ = z - dirZ * 3f,
                recoverY = 0f,
                recoverHeading = Mathf.Atan2(dirX, dirZ)
            };
        }

        // The graybox collision world: every solid collider from
        // EnsureCoursePrefab, built collision-only in the local scene.
        private void BuildGrayboxWorld(PhysicsCourseWorld w)
        {
            PhysicsTestWorld.Box(w, "Ground", new Vector3(0f, -0.5f, 0f),
                new Vector3(240f, 1f, 160f));
            PhysicsTestWorld.Box(w, "WallWest",
                new Vector3(-119.5f, 1.5f, 0f),
                new Vector3(1f, 3f, 160f));
            PhysicsTestWorld.Box(w, "WallEast",
                new Vector3(119.5f, 1.5f, 0f),
                new Vector3(1f, 3f, 160f));
            PhysicsTestWorld.Box(w, "WallNorth",
                new Vector3(0f, 1.5f, 79.5f),
                new Vector3(240f, 3f, 1f));
            PhysicsTestWorld.Box(w, "WallSouth",
                new Vector3(0f, 1.5f, -79.5f),
                new Vector3(240f, 3f, 1f));
            PhysicsTestWorld.Box(w, "BarrierA",
                new Vector3(25f, 1f, -25f),
                new Vector3(6f, 2f, 30f));
            PhysicsTestWorld.Box(w, "BarrierB",
                new Vector3(-30f, 1f, 10f),
                new Vector3(30f, 2f, 6f));
            PhysicsTestWorld.Box(w, "Ramp",
                new Vector3(-60f, 1.2f, 30f),
                new Vector3(12f, 0.6f, 16f),
                Quaternion.Euler(-14f, 0f, 0f));
            PhysicsTestWorld.Box(w, "IslandNorthEast",
                new Vector3(32f, 1f, 44f),
                new Vector3(22f, 2f, 14f));
            PhysicsTestWorld.Box(w, "IslandSouthEast",
                new Vector3(72f, 1f, -32f),
                new Vector3(20f, 2f, 16f));
        }

        [Test]
        [Timeout(300000)]
        public void PhysicalCarCompletesAuthoredCourseEndToEnd()
        {
            var physics = VehiclePhysicsTuning.Car01();
            // E2E fixture tune: a milder cruise keeps the honest waypoint
            // follower inside its speed-tapered steering radius on the
            // authored route — still a fully validated VehiclePhysicsTuning.
            physics.cruiseSpeed = 12f;
            physics.boostSpeed = 16f;
            var tuning = PhysicsTestWorld.PhysicalTuning("car-01", physics);
            var course = GrayboxCourse();
            var evt = new EventDefinition
            {
                id = "e2e",
                courseId = course.id,
                laps = 1,
                countdownTicks = 0,
                vehicleIds = new List<string> { "car-01" },
                recovery = new RecoverySettings()
            };
            // Surface-height spawn convention: y=0 is the slab top; the
            // session adds SettledLift exactly once.
            var session = new DrivingSession("e2e", tuning, course, evt,
                "v", "start", new MotorPose
                {
                    x = 0f, y = 0f, z = -60f, heading = 0f
                });
            world = PhysicsCourseWorld.Create("e2e");
            BuildGrayboxWorld(world);
            sim = new PhysicsVehicleSimulation(world);
            sim.Initialize(new List<VehicleSpawn>
            {
                new VehicleSpawn
                {
                    participantId = session.ParticipantId,
                    tuning = tuning,
                    initialState = session.Snapshot().motor
                }
            });

            var ai = new AiDriver(0f);
            var recoveries = 0;
            var gatesCrossed = 0;
            var finished = false;
            var sawAirborne = false;
            var sawLanded = false;
            var trail = new System.Text.StringBuilder();
            for (var i = 0; i < 5400 && !finished; i++) // up to 3 min sim
            {
                if (i % 150 == 0)
                {
                    var m = session.Snapshot().motor;
                    trail.Append(' ').Append(i).Append(":(")
                        .Append(m.x.ToString("F0")).Append(',')
                        .Append(m.y.ToString("F1")).Append(',')
                        .Append(m.z.ToString("F0")).Append(",v")
                        .Append(m.speed.ToString("F1")).Append(",cp")
                        .Append(session.Snapshot().nextCheckpoint).Append(')');
                }
                var request = session.PrepareStep(
                    ai.Sample(session.Snapshot(), course));
                if (request == null) break;
                if (request.reset != null)
                    sim.Restore(request.reset.participantId,
                        request.reset.state);
                var results = sim.Step(new[] { request });
                var result = session.CompleteStep(
                    results.Count > 0 ? results[0] : null);
                if (result.reset != null)
                    sim.Restore(result.reset.participantId,
                        result.reset.state);
                foreach (var ev in result.events)
                {
                    if (ev.kind == "race.recovered")
                    {
                        recoveries++;
                        var rm = session.Snapshot().motor;
                        trail.Append(" REC[").Append(ev.detail).Append("]@(")
                            .Append(rm.x.ToString("F0")).Append(',')
                            .Append(rm.z.ToString("F0")).Append(')');
                    }
                    else if (ev.kind == "checkpoint.crossed")
                        gatesCrossed++;
                    else if (ev.kind == "motor.becameAirborne")
                    {
                        sawAirborne = true;
                        var am = session.Snapshot().motor;
                        trail.Append(" AIR@(")
                            .Append(am.x.ToString("F0")).Append(',')
                            .Append(am.z.ToString("F0")).Append(')');
                    }
                    else if (ev.kind == "motor.landed")
                    {
                        sawLanded = true;
                        var lm = session.Snapshot().motor;
                        trail.Append(" LAND@(")
                            .Append(lm.x.ToString("F0")).Append(',')
                            .Append(lm.z.ToString("F0")).Append(",")
                            .Append(ev.detail).Append(')');
                    }
                    else if (ev.kind == "race.finished") finished = true;
                }
            }
            var snapshot = session.Snapshot();
            Assert.That(finished, Is.True,
                "the physical car must complete the authored course: "
                + "crossed " + gatesCrossed + " gates, recovered "
                + recoveries + " times, last checkpoint "
                + snapshot.nextCheckpoint + ", lap " + snapshot.lap
                + " | trail" + trail);
            Assert.That(gatesCrossed, Is.GreaterThanOrEqualTo(5),
                "a finished lap crossed every authored gate");
            // The authored ramp is a real rollover trap at fixture pace:
            // honest outcomes are a clean airborne->landed flight OR an
            // overturned wipeout the recovery rules resolve — either way
            // the lap must still finish within bounded recovery work.
            // The bound covers the wedge-escalation budget: each respawn
            // advances the pursuit aim one route vertex, and walking the
            // aim past this trap's landing zone takes up to five cycles —
            // observed live lines land 3-5 recoveries. A driver that
            // needed more than six would be failing to escalate, not
            // surviving a trap.
            Assert.That(recoveries, Is.LessThanOrEqualTo(6),
                "recovery is a safety net, not a crutch: " + recoveries
                + " recoveries on one lap (air=" + sawAirborne
                + ", landed=" + sawLanded + ") | trail" + trail);
        }
    }
}
