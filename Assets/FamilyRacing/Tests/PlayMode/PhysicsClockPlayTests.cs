using System.Collections;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
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
    }
}
