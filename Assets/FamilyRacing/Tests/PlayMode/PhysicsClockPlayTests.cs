using System.Collections;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FamilyRacing.Tests
{
    // Clock-boundary tests for the physical backend: the simulation's
    // elapsed time is a pure function of Step() calls — never of render
    // cadence, frame deltaTime, or FixedUpdate.
    public sealed class PhysicsClockPlayTests
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

        private void BuildFlatWorld(string name, out List<VehicleTickRequest> requests)
        {
            var physics = VehiclePhysicsTuning.Car01();
            world = PhysicsCourseWorld.Create(name);
            PhysicsTestWorld.FlatGround(world, 0f, 500f, 60f, 2000f);
            sim = new PhysicsVehicleSimulation(world);
            sim.Initialize(new List<VehicleSpawn>
            {
                new VehicleSpawn
                {
                    participantId = "p1",
                    tuning = PhysicsTestWorld.PhysicalTuning("car-01", physics),
                    initialState = PhysicsTestWorld.PhysicalSpawnState(physics,
                        new Vector3(0f, PhysicsTestWorld.SpawnY(physics), 0f), 0f)
                }
            });
            requests = new List<VehicleTickRequest>
            { PhysicsTestWorld.Request("p1", PhysicsTestWorld.Drive()) };
        }

        // Pinned: 300 ticks at 1/30 s must equal exactly 10 simulated
        // seconds whether Step is called from a tight loop or spread
        // across rendered frames of any cadence.
        [UnityTest]
        public IEnumerator RenderCadenceDoesNotChangeElapsedPhysicsTime()
        {
            List<VehicleTickRequest> requests;
            BuildFlatWorld("clock-render", out requests);

            // Spread the 300 ticks across 300 rendered frames: the render
            // loop's cadence is irrelevant to elapsed physics time.
            for (var i = 0; i < 300; i++)
            {
                sim.Step(requests);
                yield return null;
            }
            var simulatedSecondsAfter300Ticks = sim.SimulatedSeconds;
            Assert.That(simulatedSecondsAfter300Ticks, Is.EqualTo(10).Within(0.001));
            Assert.That(sim.TicksSimulated, Is.EqualTo(300));
            var framePaced = sim.Capture("p1");
            var frameSubsteps = sim.SubstepsSimulated;
            Assert.That(frameSubsteps, Is.EqualTo(600),
                "every logical tick is exactly two substeps");

            // Dispose the frame-paced world, then run the identical input
            // sequence in a tight loop: same total time, same endpoint.
            var endpoint = framePaced;
            sim.Dispose();
            sim = null;
            BuildFlatWorld("clock-tight", out requests);
            for (var i = 0; i < 300; i++) sim.Step(requests);
            Assert.That(sim.SimulatedSeconds, Is.EqualTo(10).Within(0.001));
            var tightPaced = sim.Capture("p1");
            Assert.That(tightPaced.z, Is.EqualTo(endpoint.z).Within(0.05f),
                "render cadence must not change the simulated trajectory "
                + "(frame-paced z=" + endpoint.z + " tight z=" + tightPaced.z + ")");
        }

        [Test]
        public void FrozenRequestsStillAdvanceTheSharedClock()
        {
            List<VehicleTickRequest> requests;
            BuildFlatWorld("clock-frozen", out requests);
            // A fully frozen batch still consumes one logical interval:
            // Step is the interval, not the presence of active bodies.
            var frozen = new List<VehicleTickRequest>
            {
                PhysicsTestWorld.Request("p1", PhysicsTestWorld.Drive(),
                    simulateMotion: false)
            };
            for (var i = 0; i < 30; i++) sim.Step(frozen);
            Assert.That(sim.SimulatedSeconds, Is.EqualTo(1).Within(0.001));
            Assert.That(sim.SubstepsSimulated, Is.EqualTo(60));
        }

        // --- Task 4 boundary: NetworkRace over the physical backend ----------

        // A four-racer network course whose lane is a straight sprint:
        // gate "mid" at z=40 is expected first, the start/finish "line"
        // at z=80 completes the single lap. Spawn slots sit behind both.
        private static CourseDefinition PhysicalCourse(float spawnY)
        {
            var course = new CourseDefinition
            {
                id = "phys-lane",
                spawnX = 0f,
                spawnY = spawnY,
                spawnZ = 0f,
                spawnHeading = 0f
            };
            course.checkpoints.Add(new CheckpointGate
            {
                id = "line", x = 0f, z = 80f, dirX = 0f, dirZ = 1f,
                halfWidth = 12f, recoverX = 0f, recoverY = spawnY,
                recoverZ = 76f, recoverHeading = 0f
            });
            course.checkpoints.Add(new CheckpointGate
            {
                id = "mid", x = 0f, z = 40f, dirX = 0f, dirZ = 1f,
                halfWidth = 12f, recoverX = 0f, recoverY = spawnY,
                recoverZ = 36f, recoverHeading = 0f
            });
            course.spawnSlots.Add(new MotorPose
                { x = -6f, y = spawnY, z = 5f, heading = 0f });
            course.spawnSlots.Add(new MotorPose
                { x = 6f, y = spawnY, z = 5f, heading = 0f });
            course.spawnSlots.Add(new MotorPose
                { x = -6f, y = spawnY, z = 10f, heading = 0f });
            course.spawnSlots.Add(new MotorPose
                { x = 6f, y = spawnY, z = 10f, heading = 0f });
            return course;
        }

        private static DrivingContentCatalog PhysicalCatalog(
            VehiclePhysicsTuning physics, int countdownTicks)
        {
            var evt = new EventDefinition
            {
                id = "lan-phys",
                courseId = "phys-lane",
                laps = 1,
                countdownTicks = countdownTicks,
                vehicleIds = new List<string> { "car-01" }
            };
            return DrivingContentCatalog.Create("phys-content-1",
                new[]
                {
                    PhysicsTestWorld.PhysicalTuning("car-01", physics)
                },
                new[] { PhysicalCourse(PhysicsTestWorld.SpawnY(physics)) },
                new[] { evt },
                VehicleSimulationBackend.PhysicalAssisted);
        }

        private static List<NetworkRacerSpec> PhysicalRoster()
        {
            var roster = new List<NetworkRacerSpec>();
            for (var slot = 0; slot < 4; slot++)
                roster.Add(new NetworkRacerSpec
                {
                    participantId = "p" + slot,
                    slot = slot,
                    vehicleId = "car-01",
                    controlSource = slot == 0 ? "local" : "remote"
                });
            return roster;
        }

        private NetworkRace BuildRace(int countdownTicks)
        {
            var physics = VehiclePhysicsTuning.Car01();
            var catalog = PhysicalCatalog(physics, countdownTicks);
            string error;
            NetworkRace race;
            Assert.That(NetworkRace.TryCreate("s1", "r1", catalog,
                    "lan-phys", "car", PhysicalRoster(), out race, out error),
                Is.True, error);
            world = PhysicsCourseWorld.Create("race-phys");
            PhysicsTestWorld.FlatGround(world, 0f, 60f, 80f, 240f);
            sim = new PhysicsVehicleSimulation(world);
            sim.Initialize(race.VehicleSpawns());
            return race;
        }

        private static Dictionary<string, DriverInput> NeutralInputs(
            NetworkRaceState state)
        {
            var map = new Dictionary<string, DriverInput>(
                System.StringComparer.Ordinal);
            foreach (var racer in state.racers)
                map[racer.participantId] = new DriverInput(0f, false, false,
                    false, false, false, false);
            return map;
        }

        // The coordinator drives ONE shared batch per tick: exactly two
        // substeps regardless of roster size, bodies frozen through the
        // countdown, then all four advance from the single batch.
        [Test]
        public void NetworkRaceStepsOneSharedBatchPerTick()
        {
            var race = BuildRace(30);
            var state = race.Snapshot();
            var spawnZ = new float[4];
            for (var i = 0; i < 4; i++)
                spawnZ[i] = state.racers[i].session.motor.z;

            for (var i = 0; i < 30; i++)
                state = race.Step(NeutralInputs(state), sim);
            Assert.That(sim.TicksSimulated, Is.EqualTo(30),
                "one batch per boundary, never per racer");
            Assert.That(sim.SubstepsSimulated, Is.EqualTo(60));
            Assert.That(state.phase, Is.EqualTo("running"));
            Assert.That(state.elapsedTicks, Is.EqualTo(0),
                "countdown boundaries never charge the race clock");
            for (var i = 0; i < 4; i++)
                Assert.That(state.racers[i].session.motor.z,
                    Is.EqualTo(spawnZ[i]).Within(0.05f),
                    "countdown bodies stay frozen at the grid");

            for (var i = 0; i < 60; i++)
                state = race.Step(NeutralInputs(state), sim);
            Assert.That(sim.TicksSimulated, Is.EqualTo(90));
            Assert.That(sim.SubstepsSimulated, Is.EqualTo(180),
                "four racers still share exactly two substeps per tick");
            Assert.That(state.elapsedTicks, Is.EqualTo(60));
            for (var i = 0; i < 4; i++)
                Assert.That(state.racers[i].session.motor.z,
                    Is.GreaterThan(spawnZ[i] + 2f),
                    "racer " + i + " must advance from the shared batch");
        }

        // A shared pause freezes the physical world AND every race timer:
        // no Step call reaches the simulation while paused.
        [Test]
        public void PausedRaceFreezesSharedWorldAndClocks()
        {
            var race = BuildRace(0);
            var state = race.Snapshot();
            for (var i = 0; i < 30; i++)
                state = race.Step(NeutralInputs(state), sim);
            var ticks = sim.TicksSimulated;
            var elapsed = state.elapsedTicks;

            race.SetPaused(true);
            for (var i = 0; i < 10; i++)
            {
                var paused = race.Step(NeutralInputs(state), sim);
                Assert.That(paused.paused, Is.True);
            }
            Assert.That(sim.TicksSimulated, Is.EqualTo(ticks),
                "a paused race must never advance the shared world");
            Assert.That(race.Snapshot().elapsedTicks, Is.EqualTo(elapsed));
            race.SetPaused(false);
            state = race.Step(NeutralInputs(state), sim);
            Assert.That(sim.TicksSimulated, Is.EqualTo(ticks + 1));
        }

        // All four physical racers crossing the sprint lane finish nearly
        // together; the finalized result is immutable and the whole world
        // tears down cleanly on race end (the scene-exit half of the
        // fixture: disposing mid-race must never throw or leak).
        [Test]
        public void SimultaneousFinishFinalizesThenWorldDisposes()
        {
            var race = BuildRace(0);
            var state = race.Snapshot();
            var ticks = 0;
            while (state.phase != "finished" && ticks < 900)
            {
                state = race.Step(NeutralInputs(state), sim);
                ticks++;
            }
            Assert.That(state.phase, Is.EqualTo("finished"),
                "all four cars should cross the sprint lane inside 30 s");
            Assert.That(state.result, Is.Not.Null);
            Assert.That(state.result.finalized, Is.True);
            var spread = 0L;
            foreach (var racer in state.result.racers)
            {
                Assert.That(racer.outcome, Is.EqualTo("finished"),
                    racer.participantId + " should finish, not DNF");
                if (racer.finishTicks > spread) spread = racer.finishTicks;
            }
            var first = long.MaxValue;
            foreach (var racer in state.result.racers)
                if (racer.finishTicks < first) first = racer.finishTicks;
            Assert.That(spread - first, Is.LessThan(90),
                "identical cars on one lane must finish nearly together");
            // Scene exit: the finished race stops stepping and teardown
            // releases the local physics scene without faulting.
            var before = sim.TicksSimulated;
            state = race.Step(NeutralInputs(state), sim);
            Assert.That(sim.TicksSimulated, Is.EqualTo(before),
                "a finalized race never steps the world again");
            sim.Dispose();
            sim = null;
            world.Dispose();
            world = null;
        }
    }
}
