using System.Collections.Generic;
using System.Diagnostics;
using FamilyRacing.Core.Driving;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace FamilyRacing.Tests
{
    // PlayMode qualification of the PhysX-backed IVehicleSimulation: the
    // mass/torque oracles are deliberately run in an EMPTY world (no
    // drivable colliders) with drag/rolling resistance zeroed, so the only
    // force acting is the injected test force — gravity affects only the
    // unmeasured y-axis. Grounded handling fixtures build collision-only
    // geometry inside the sim's private scene.
    public sealed class PhysicsVehiclePlayTests
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

        private PhysicsVehicleSimulation NewSim(string name,
            VehiclePhysicsTuning physics, MotorState initialState,
            string participantId = "p1", GameObject template = null)
        {
            world = template == null
                ? PhysicsCourseWorld.Create(name)
                : PhysicsCourseWorld.Create(name, template);
            sim = new PhysicsVehicleSimulation(world);
            sim.Initialize(new List<VehicleSpawn>
            {
                new VehicleSpawn
                {
                    participantId = participantId,
                    tuning = PhysicsTestWorld.PhysicalTuning("car-01", physics),
                    initialState = initialState
                }
            });
            return sim;
        }

        private static VehiclePhysicsTuning BareCar(float mass)
        {
            // Oracle tuning: no drag/rolling resistance, no assists — the
            // only forces left are the injected debug force and gravity.
            var t = VehiclePhysicsTuning.Car01();
            t.massKg = mass;
            t.dragCoefficient = 0f;
            t.rollingResistance = 0f;
            t.rollDamping = 0f;
            t.peakDriveForce = 0.0001f; // throttle off anyway; keep valid
            return t;
        }

        // --- the pinned mass oracle ----------------------------------------

        [Test]
        public void EqualForceRespectsMass()
        {
            var light = BareCar(1000f);
            var heavy = BareCar(2000f);
            world = PhysicsCourseWorld.Create("mass-oracle"); // empty: no ground
            sim = new PhysicsVehicleSimulation(world);
            sim.Initialize(new List<VehicleSpawn>
            {
                new VehicleSpawn
                {
                    participantId = "light",
                    tuning = PhysicsTestWorld.PhysicalTuning("car-01", light),
                    initialState = PhysicsTestWorld.PhysicalSpawnState(light,
                        new Vector3(-3f, 20f, 0f), 0f)
                },
                new VehicleSpawn
                {
                    participantId = "heavy",
                    tuning = PhysicsTestWorld.PhysicalTuning("car-01", heavy),
                    initialState = PhysicsTestWorld.PhysicalSpawnState(heavy,
                        new Vector3(3f, 20f, 0f), 0f)
                }
            });

            sim.DebugSetForce("light", new Vector3(0f, 0f, 3000f));
            sim.DebugSetForce("heavy", new Vector3(0f, 0f, 3000f));
            // 0.1 s = three logical ticks = six substeps.
            for (var i = 0; i < 3; i++)
                sim.Step(new List<VehicleTickRequest>
                {
                    PhysicsTestWorld.Request("light", PhysicsTestWorld.Drive()),
                    PhysicsTestWorld.Request("heavy", PhysicsTestWorld.Drive())
                });

            var lightDeltaVelocity = sim.Capture("light").physics.velocityZ;
            var heavyDeltaVelocity = sim.Capture("heavy").physics.velocityZ;
            Assert.That(lightDeltaVelocity, Is.EqualTo(0.3f).Within(0.005)); // 3000 N, 1000 kg, 0.1 s
            Assert.That(heavyDeltaVelocity, Is.EqualTo(0.15f).Within(0.005)); // same force, 2000 kg
        }

        // --- torque oracle: off-center force rotates the chassis ------------

        [Test]
        public void OffCenterForceChangesAngularVelocity()
        {
            var physics = BareCar(1200f);
            NewSim("torque-oracle", physics,
                PhysicsTestWorld.PhysicalSpawnState(physics,
                    new Vector3(0f, 20f, 0f), 0f));

            // +z force applied 0.5 m right of the COM: r x F gives a
            // negative-y torque on the chassis.
            sim.DebugSetForceAtOffset("p1", new Vector3(0f, 0f, 2400f),
                new Vector3(0.5f, 0f, 0f));
            for (var i = 0; i < 3; i++)
                sim.Step(new List<VehicleTickRequest>
                { PhysicsTestWorld.Request("p1", PhysicsTestWorld.Drive()) });

            var state = sim.Capture("p1").physics;
            Assert.That(state.velocityZ, Is.EqualTo(0.2f).Within(0.01),
                "the same force still translates the COM (2400 N / 1200 kg)");
            Assert.That(state.angularVelocityY, Is.LessThan(-0.005f),
                "an off-center force must produce torque: r x F about -y "
                + "(got " + state.angularVelocityY + ")");
        }

        // --- shared world cadence -------------------------------------------

        [Test]
        public void FourBodiesAdvanceOnlyTwoSubsteps()
        {
            var physics = VehiclePhysicsTuning.Car01();
            world = PhysicsCourseWorld.Create("four-body");
            PhysicsTestWorld.FlatGround(world, 0f, 50f, 80f, 200f);
            sim = new PhysicsVehicleSimulation(world);
            var spawns = new List<VehicleSpawn>();
            var requests = new List<VehicleTickRequest>();
            for (var i = 0; i < 4; i++)
            {
                var id = "p" + i;
                spawns.Add(new VehicleSpawn
                {
                    participantId = id,
                    tuning = PhysicsTestWorld.PhysicalTuning("car-01", physics),
                    initialState = PhysicsTestWorld.PhysicalSpawnState(physics,
                        new Vector3(i * 6f - 9f,
                            PhysicsTestWorld.SpawnY(physics), 0f), 0f)
                });
                requests.Add(PhysicsTestWorld.Request(id, PhysicsTestWorld.Drive()));
            }
            sim.Initialize(spawns);

            // Settle onto the suspension first so all four are grounded.
            for (var i = 0; i < 60; i++) sim.Step(requests);
            var before = sim.SubstepsSimulated;
            var results = sim.Step(requests);
            var substepsForOneLogicalTickWithFourRacers = sim.SubstepsSimulated - before;
            Assert.That(substepsForOneLogicalTickWithFourRacers, Is.EqualTo(2));
            Assert.That(results.Count, Is.EqualTo(4));
            foreach (var result in results)
            {
                Assert.That(result.step.rejected, Is.False);
                Assert.That(result.path.Length, Is.EqualTo(3));
                Assert.That(result.path[0].offsetSeconds, Is.EqualTo(0f).Within(1e-6f));
                Assert.That(result.path[1].offsetSeconds, Is.EqualTo(1f / 60f).Within(1e-6f));
                Assert.That(result.path[2].offsetSeconds, Is.EqualTo(1f / 30f).Within(1e-6f));
                Assert.That(result.step.state.z,
                    Is.GreaterThan(result.path[0].pose.z - 0.001f),
                    "every active body advances inside the shared substeps");
            }
        }

        // --- lifecycle --------------------------------------------------------

        [Test]
        public void SceneLifecycleDoesNotLeak()
        {
            var before = SceneManager.sceneCount;
            Scene scene;
            using (var local = new PhysicsVehicleSimulation(
                PhysicsCourseWorld.Create("leak-check")))
            {
                scene = local.World.Scene;
                Assert.That(scene.IsValid(), Is.True);
                Assert.That(scene.isLoaded, Is.True);
            }
            Assert.That(scene.isLoaded, Is.False,
                "disposing the simulation must unload its local scene");
            Assert.That(SceneManager.sceneCount, Is.EqualTo(before),
                "the scene list must return to its previous count");
            // A second create/dispose cycle is just as clean.
            using (var second = new PhysicsVehicleSimulation(
                PhysicsCourseWorld.Create("leak-check-2")))
            {
                Assert.That(second.World.Scene.isLoaded, Is.True);
            }
            Assert.That(SceneManager.sceneCount, Is.EqualTo(before));
        }

        // --- braking ----------------------------------------------------------

        [Test]
        public void BrakeStopsWithoutReversing()
        {
            var physics = VehiclePhysicsTuning.Car01();
            NewSim("brake-lane", physics,
                PhysicsTestWorld.PhysicalSpawnState(physics,
                    new Vector3(0f, PhysicsTestWorld.SpawnY(physics), 0f), 0f));
            PhysicsTestWorld.FlatGround(world, 0f, 100f, 60f, 300f);

            // Accelerate to cruise under automatic throttle.
            for (var i = 0; i < 120; i++)
                sim.Step(new List<VehicleTickRequest>
                { PhysicsTestWorld.Request("p1", PhysicsTestWorld.Drive()) });
            var speedAtBrake = PhysicsTestWorld.ForwardSpeed(sim.Capture("p1"));
            Assert.That(speedAtBrake, Is.GreaterThan(8f),
                "the car must reach speed before braking");

            var worstReverse = 0f;
            for (var i = 0; i < 180; i++)
            {
                sim.Step(new List<VehicleTickRequest>
                { PhysicsTestWorld.Request("p1", PhysicsTestWorld.Drive(brake: 1f)) });
                var forward = PhysicsTestWorld.ForwardSpeed(sim.Capture("p1"));
                if (forward < worstReverse) worstReverse = forward;
            }
            var stopped = PhysicsTestWorld.ForwardSpeed(sim.Capture("p1"));
            Assert.That(stopped, Is.LessThan(0.25f),
                "a held brake must stop the car (speed " + stopped + ")");
            Assert.That(worstReverse, Is.GreaterThan(-0.1f),
                "braking must never reverse travel (worst " + worstReverse + ")");
        }

        // --- rollover fixtures -------------------------------------------------

        [Test]
        public void StockCarTripsOnAggressiveCurb()
        {
            // Tripped rollover: the car slides SIDEWAYS into a knee-high
            // curb — the classic curb trip. The left side skirt digs
            // into the curb face low on the chassis while tire grip
            // keeps the wheels planted, so the body pivots over the
            // curb line (no canned flip torque anywhere).
            // Reproducible geometry: ground top y=0; curb 0.35 m tall,
            // 1.0 m wide, running along z at x=-1.0.
            var physics = VehiclePhysicsTuning.Car01();
            physics.cruiseSpeed = 30f;
            physics.boostSpeed = 30f;
            physics.peakDriveForce = 9000f;
            var tuning = physics;
            var spawn = PhysicsTestWorld.PhysicalSpawnState(tuning,
                new Vector3(7f, PhysicsTestWorld.SpawnY(tuning), -10f),
                0f, forwardSpeed: 6f);
            // Facing +z but sliding -x: a lateral slide into the curb.
            spawn.physics.velocityX = -22f;
            NewSim("curb-trip", tuning, spawn);
            PhysicsTestWorld.FlatGround(world, 0f, 0f, 300f, 300f);
            PhysicsTestWorld.Box(world, "TripCurb", new Vector3(-1f, 0.175f, 0f),
                new Vector3(1f, 0.35f, 120f));

            var flippedAt = -1;
            var minDot = 1f;
            for (var i = 0; i < 300; i++)
            {
                // Straight throttle — the authored slide does the work.
                sim.Step(new List<VehicleTickRequest>
                { PhysicsTestWorld.Request("p1", PhysicsTestWorld.Drive()) });
                var dot = PhysicsTestWorld.BodyUpDot(sim.Capture("p1"));
                if (dot < minDot) minDot = dot;
                if (dot < 0f)
                {
                    flippedAt = i;
                    break;
                }
            }
            Assert.That(flippedAt, Is.GreaterThanOrEqualTo(0),
                "the stock car must trip on the aggressive curb fixture "
                + "within 10 s of contact driving (minDot " + minDot + ")");
        }

        [Test]
        public void HighComPresetRollsUntripped()
        {
            // Untripped roll: the labeled high-COM preset on flat ground,
            // full lock at speed — no curb, no wall, pure grip rollover.
            var physics = PhysicsTestWorld.HighComCar();
            NewSim("high-com-roll", physics,
                PhysicsTestWorld.PhysicalSpawnState(physics,
                    new Vector3(0f, PhysicsTestWorld.SpawnY(physics) + 0.6f, 0f), 0f,
                    forwardSpeed: 16f));
            PhysicsTestWorld.FlatGround(world, 0f, 0f, 200f, 200f);

            var flippedAt = -1;
            for (var i = 0; i < 300; i++)
            {
                sim.Step(new List<VehicleTickRequest>
                { PhysicsTestWorld.Request("p1", PhysicsTestWorld.Drive(steering: 1f)) });
                if (PhysicsTestWorld.BodyUpDot(sim.Capture("p1")) < 0f)
                {
                    flippedAt = i;
                    break;
                }
            }
            Assert.That(flippedAt, Is.GreaterThanOrEqualTo(0),
                "the labeled high-COM preset must roll untripped on a flat "
                + "full-lock turn — dot(bodyUp, worldUp) < 0");
        }

        // --- controller history and state seam ---------------------------------

        [Test]
        public void SteeringIsRateLimitedAndFiltered()
        {
            var physics = VehiclePhysicsTuning.Car01();
            NewSim("steer-rate", physics,
                PhysicsTestWorld.PhysicalSpawnState(physics,
                    new Vector3(0f, PhysicsTestWorld.SpawnY(physics), 0f), 0f));
            PhysicsTestWorld.FlatGround(world, 0f, 100f, 60f, 300f);

            sim.Step(new List<VehicleTickRequest>
            { PhysicsTestWorld.Request("p1", PhysicsTestWorld.Drive(steering: 1f)) });
            var afterOneTick = sim.Capture("p1").physics.filteredSteering;
            // Rate limit: steerRateRadiansPerSecond over two 1/60 substeps.
            var bound = physics.steerRateRadiansPerSecond * (2f / 60f) + 1e-4f;
            Assert.That(Mathf.Abs(afterOneTick), Is.LessThanOrEqualTo(bound),
                "one tick of full input cannot exceed the rate limit "
                + "(" + afterOneTick + " vs " + bound + ")");

            for (var i = 0; i < 120; i++)
                sim.Step(new List<VehicleTickRequest>
                { PhysicsTestWorld.Request("p1", PhysicsTestWorld.Drive(steering: 1f)) });
            var settled = Mathf.Abs(sim.Capture("p1").physics.filteredSteering);
            Assert.That(settled, Is.GreaterThan(0.05f),
                "the filter still converges toward the steering target");
            Assert.That(settled, Is.LessThanOrEqualTo(physics.steerMaxRadians + 1e-4f));
        }

        [Test]
        public void RestoreAndCaptureRoundTripControllerHistoryAndEpoch()
        {
            var physics = VehiclePhysicsTuning.Car01();
            NewSim("restore", physics,
                PhysicsTestWorld.PhysicalSpawnState(physics,
                    new Vector3(0f, PhysicsTestWorld.SpawnY(physics), 0f), 0f));
            PhysicsTestWorld.FlatGround(world, 0f, 100f, 60f, 300f);

            for (var i = 0; i < 30; i++)
                sim.Step(new List<VehicleTickRequest>
                { PhysicsTestWorld.Request("p1", PhysicsTestWorld.Drive(steering: 0.5f)) });
            var saved = sim.Capture("p1");
            saved.physics.recoveryEpoch = 9;
            sim.Restore("p1", saved);
            var restored = sim.Capture("p1");

            Assert.That(restored.physics.recoveryEpoch, Is.EqualTo(9));
            Assert.That(restored.physics.filteredSteering,
                Is.EqualTo(saved.physics.filteredSteering).Within(1e-6f),
                "controller history must survive a restore");
            Assert.That(restored.x, Is.EqualTo(saved.x).Within(1e-4f));
            Assert.That(PhysicsTestWorld.BodyUpDot(restored),
                Is.EqualTo(PhysicsTestWorld.BodyUpDot(saved)).Within(1e-3f));

            // Capture independence: mutating the returned state must not
            // touch the live body.
            restored.physics.wheels[0].compression = 0.9f;
            restored.physics.filteredSteering = -0.4f;
            var again = sim.Capture("p1");
            Assert.That(again.physics.wheels[0].compression,
                Is.Not.EqualTo(0.9f));
            Assert.That(again.physics.filteredSteering,
                Is.Not.EqualTo(-0.4f));
        }

        [Test]
        public void FrozenBodyDoesNotMoveOrInterfere()
        {
            var physics = VehiclePhysicsTuning.Car01();
            world = PhysicsCourseWorld.Create("frozen");
            PhysicsTestWorld.FlatGround(world, 0f, 100f, 60f, 300f);
            sim = new PhysicsVehicleSimulation(world);
            var spawnY = PhysicsTestWorld.SpawnY(physics);
            sim.Initialize(new List<VehicleSpawn>
            {
                new VehicleSpawn
                {
                    participantId = "active",
                    tuning = PhysicsTestWorld.PhysicalTuning("car-01", physics),
                    initialState = PhysicsTestWorld.PhysicalSpawnState(physics,
                        new Vector3(0f, spawnY, 0f), 0f)
                },
                new VehicleSpawn
                {
                    participantId = "disabled",
                    tuning = PhysicsTestWorld.PhysicalTuning("car-01", physics),
                    initialState = PhysicsTestWorld.PhysicalSpawnState(physics,
                        new Vector3(0f, spawnY, 15f), 0f) // parked in the lane
                }
            });
            var frozenSpawn = sim.Capture("disabled");

            for (var i = 0; i < 240; i++)
            {
                sim.Step(new List<VehicleTickRequest>
                {
                    PhysicsTestWorld.Request("active", PhysicsTestWorld.Drive()),
                    PhysicsTestWorld.Request("disabled", PhysicsTestWorld.Drive(),
                        simulateMotion: false)
                });
            }
            var active = sim.Capture("active");
            var frozen = sim.Capture("disabled");
            Assert.That(active.z, Is.GreaterThan(25f),
                "the active car must drive through the frozen body's slot "
                + "— a disabled body is non-interfering (z=" + active.z + ")");
            Assert.That(frozen.z, Is.EqualTo(frozenSpawn.z).Within(0.01f));
            Assert.That(frozen.x, Is.EqualTo(frozenSpawn.x).Within(0.01f));
        }

        // --- measurements quoted by the prototype report -----------------------

        [Test]
        public void SettledComHeightAndFourBodyLoadAreRecorded()
        {
            var physics = VehiclePhysicsTuning.Car01();
            world = PhysicsCourseWorld.Create("load");
            PhysicsTestWorld.FlatGround(world, 0f, 50f, 80f, 200f);
            sim = new PhysicsVehicleSimulation(world);
            var spawns = new List<VehicleSpawn>();
            var requests = new List<VehicleTickRequest>();
            var spawnY = PhysicsTestWorld.SpawnY(physics);
            for (var i = 0; i < 4; i++)
            {
                var id = "p" + i;
                spawns.Add(new VehicleSpawn
                {
                    participantId = id,
                    tuning = PhysicsTestWorld.PhysicalTuning("car-01", physics),
                    initialState = PhysicsTestWorld.PhysicalSpawnState(physics,
                        new Vector3(i * 6f - 9f, spawnY, i * 3f), 0f)
                });
                requests.Add(PhysicsTestWorld.Request(id, PhysicsTestWorld.Drive()));
            }
            sim.Initialize(spawns);

            for (var i = 0; i < 120; i++) sim.Step(requests);
            var body = sim.DebugRigidbody("p0");
            var comHeight = body.worldCenterOfMass.y;
            UnityEngine.Debug.Log(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "PHYSICS-LAB settled COM height: {0:F3} m (body origin y {1:F3})",
                comHeight, body.position.y));
            Assert.That(comHeight, Is.GreaterThan(0.2f));
            Assert.That(comHeight, Is.LessThan(0.9f));

            var watch = Stopwatch.StartNew();
            const int ticks = 300;
            for (var i = 0; i < ticks; i++) sim.Step(requests);
            watch.Stop();
            var perTickMs = watch.Elapsed.TotalMilliseconds / ticks;
            UnityEngine.Debug.Log(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "PHYSICS-LAB four-body load: {0:F2} ms/tick ({1:F2} ms/substep) "
                + "over {2} ticks x 4 bodies",
                perTickMs, perTickMs / 2.0, ticks));
            // Generous upper bound: 30 Hz budget is 33 ms; anything near it
            // already fails the product. This guards gross regressions only.
            Assert.That(perTickMs, Is.LessThan(30.0),
                "four-body stepping must stay far inside the tick budget");
        }
    }
}
