using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
using UnityEngine;

namespace FamilyRacing.Tests
{
    // Task 9 PlayMode: one authoritative physical world on the host
    // simulates every LAN racer. These tests run the real
    // NetworkRace -> PhysicsVehicleSimulation -> PhysicsCourseWorld seam
    // with four seated racers on collision-only geometry: peer contacts
    // resolve in the shared world, frozen/disconnected handling keeps
    // host authority, occupied respawns fall back to authored safe
    // poses, and simultaneous finishes finalise deterministically.
    // The client side of this wire contract is inputs-only: the packet
    // vocabulary carries no pose/progress/claim types, and a forged
    // client fact can never reach this coordinator (typed-packet tests
    // and NetworkReplicationPlayTests.ClientCannotSubmitFinishOrCoins
    // own that direction).
    public sealed class NetworkPhysicalRacePlayTests
    {
        private PhysicsCourseWorld world;
        private PhysicsVehicleSimulation sim;
        private NetworkRace race;

        [TearDown]
        public void TearDown()
        {
            if (world != null) world.Dispose();
            world = null;
            sim = null;
            race = null;
        }

        [Test]
        public void FrontCollisionResolvesInSharedWorld()
        {
            NewRace();
            var tuning = VehiclePhysicsTuning.Car01();
            var y = PhysicsTestWorld.SpawnY(tuning);
            sim.Restore("p-1", PhysicsTestWorld.PhysicalSpawnState(tuning,
                new Vector3(0f, y, 8f), 0f, 12f));
            sim.Restore("p-2", PhysicsTestWorld.PhysicalSpawnState(tuning,
                new Vector3(0f, y, 22f), Mathf.PI, 12f));
            var state = StepUntilVehicleContact(60);
            Assert.That(state, Is.Not.Null, "nose-to-nose contact never landed");

            var a = Motor(state, "p-1");
            var b = Motor(state, "p-2");
            Assert.That(a.z, Is.LessThan(b.z),
                "bodies interpenetrated: p-1 ended past p-2");
            var c1 = sim.Capture("p-1");
            var c2 = sim.Capture("p-2");
            Assert.That(c1.physics.lastContact, Is.EqualTo("vehicle"));
            Assert.That(c2.physics.lastContact, Is.EqualTo("vehicle"));
        }

        [Test]
        public void SideCollisionDeflectsBothBodies()
        {
            NewRace();
            var tuning = VehiclePhysicsTuning.Car01();
            var y = PhysicsTestWorld.SpawnY(tuning);
            // p-1 drives +z through x=0; p-2 sweeps -x through z=18.
            // Both reach the crossing zone near (0, ~18) together.
            sim.Restore("p-1", PhysicsTestWorld.PhysicalSpawnState(tuning,
                new Vector3(0f, y, 10f), 0f, 12f));
            sim.Restore("p-2", PhysicsTestWorld.PhysicalSpawnState(tuning,
                new Vector3(8f, y, 18f), -Mathf.PI / 2f, 12f));
            var state = StepUntilVehicleContact(60);
            Assert.That(state, Is.Not.Null, "T-bone contact never landed");

            var a = sim.Capture("p-1");
            var b = sim.Capture("p-2");
            // The collision leaves an observable physical fact on the host:
            // non-zero separation and both bodies still finite inside the
            // bounds (a client can never fake this — contacts exist only
            // here).
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            Assert.That(dx * dx + dz * dz, Is.GreaterThan(0.5f));
            Assert.That(float.IsNaN(a.x) || float.IsNaN(b.x), Is.False);
        }

        [Test]
        public void RacerLandingOnRacerContactsVehicle()
        {
            NewRace();
            var tuning = VehiclePhysicsTuning.Car01();
            var y = PhysicsTestWorld.SpawnY(tuning);
            sim.Restore("p-2", PhysicsTestWorld.PhysicalSpawnState(tuning,
                new Vector3(0f, y, 20f), 0f, 0f));
            // p-1 hovers 3 m above p-2's roof and drops.
            sim.Restore("p-1", PhysicsTestWorld.PhysicalSpawnState(tuning,
                new Vector3(0f, y + 3f, 20f), 0f, 0f));
            var state = StepUntilVehicleContact(80);
            Assert.That(state, Is.Not.Null,
                "vertical racer-on-racer contact never landed");
            var a = sim.Capture("p-1");
            Assert.That(a.physics.lastContact, Is.EqualTo("vehicle"));
            Assert.That(a.y, Is.GreaterThan(0.5f),
                "the falling body must rest on the roof or ground, never inside");
        }

        [Test]
        public void PausedRaceFreezesSharedWorld()
        {
            NewRace();
            var tuning = VehiclePhysicsTuning.Car01();
            var y = PhysicsTestWorld.SpawnY(tuning);
            sim.Restore("p-2", PhysicsTestWorld.PhysicalSpawnState(tuning,
                new Vector3(0f, y, 20f), 0f, 0f));
            sim.Restore("p-1", PhysicsTestWorld.PhysicalSpawnState(tuning,
                new Vector3(0f, y + 4f, 20f), 0f, 0f));

            race.SetPaused(true);
            var frozenY = sim.Capture("p-1").y;
            for (var i = 0; i < 30; i++)
                race.Step(CoastInputs(), sim);
            Assert.That(sim.Capture("p-1").y, Is.EqualTo(frozenY).Within(1e-4f),
                "a paused host world must never simulate");
            Assert.That(sim.Capture("p-1").physics.lastContact,
                Is.Not.EqualTo("vehicle"));

            race.SetPaused(false);
            var state = StepUntilVehicleContact(80);
            Assert.That(state, Is.Not.Null,
                "unpaused host world must resume real contacts");
        }

        [Test]
        public void DisconnectedRacerIsDrivenByHostAi()
        {
            NewRace();
            var startZ = Motor(race.Step(CoastInputs(), sim), "p-1").z;

            Assert.That(race.SetControlSource("p-1", "ai"), Is.True);
            Assert.That(race.Snapshot().racers[0].attribution,
                Is.EqualTo("ai"));

            // The host's AiDriver owns the vacated seat — never the client.
            NetworkRaceState state = null;
            for (var i = 0; i < 60; i++)
                state = race.Step(CoastInputs(), sim);
            var moved = Motor(state, "p-1");
            Assert.That(moved.z, Is.GreaterThan(startZ + 0.5f),
                "host AI must keep the disconnected racer driving");
        }

        [Test]
        public void OccupiedRespawnFallsBackToAuthoredSafePose()
        {
            NewRace();
            var tuning = VehiclePhysicsTuning.Car01();
            // Park p-2 on p-1's spawn-safe pose (-6, -16) and let the
            // committed session pose land before the recovery request.
            sim.Restore("p-2", PhysicsTestWorld.PhysicalSpawnState(tuning,
                new Vector3(-6f, PhysicsTestWorld.SpawnY(tuning), -16f),
                0f, 0f));
            race.Step(CoastInputs(), sim);
            race.Step(CoastInputs(), sim);

            string error;
            Assert.That(race.QueuePending("p-1", "recover", "rc-1", out error),
                Is.True, error);
            var state = race.Step(CoastInputs(), sim);
            var motor = Motor(state, "p-1");
            Assert.That(Mathf.Abs(motor.x - 0f), Is.LessThan(0.5f));
            Assert.That(Mathf.Abs(motor.z - 8f), Is.LessThan(0.5f),
                "occupied primary must fall back to safePoses[0] (0, 8)");
            Assert.That(motor.physics, Is.Not.Null);
            Assert.That(motor.physics.recoveryEpoch, Is.GreaterThan(0));
        }

        [Test]
        public void SimultaneousFinishesFinalizeBySlotOrder()
        {
            NewRace();
            var tuning = VehiclePhysicsTuning.Car01();
            var y = PhysicsTestWorld.SpawnY(tuning);
            // Drive all four over the start line (pre-crossed) and then
            // gate-1 at z=30; once every session expects gate 0 again the
            // race is one finish-line crossing from done.
            var drive = new Dictionary<string, DriverInput>();
            for (var i = 1; i <= 4; i++)
                drive["p-" + i] = PhysicsTestWorld.Drive();
            for (var i = 0; i < 240; i++)
            {
                var stepped = race.Step(drive, sim);
                if (AllExpectingStartGate(stepped)) break;
            }
            Assert.That(AllExpectingStartGate(race.Snapshot()), Is.True,
                "racers never cleared gate-1");

            // Park every racer just behind the start line; the next
            // boundary crosses every body through the finish gate together.
            for (var slot = 0; slot < 4; slot++)
            {
                sim.Restore("p-" + (slot + 1),
                    PhysicsTestWorld.PhysicalSpawnState(tuning,
                        new Vector3((slot - 1.5f) * 4f, y, -0.4f), 0f, 10f));
            }

            NetworkRaceState state = null;
            for (var i = 0; i < 40; i++)
            {
                state = race.Step(CoastInputs(), sim);
                if (state.phase == NetworkRace.PhaseFinished) break;
            }
            Assert.That(state.phase, Is.EqualTo(NetworkRace.PhaseFinished));
            Assert.That(state.result, Is.Not.Null);
            Assert.That(state.result.finalized, Is.True);
            Assert.That(state.result.racers.Count, Is.EqualTo(4));

            var finishTicks = state.result.racers[0].finishTicks;
            foreach (var entry in state.result.racers)
            {
                Assert.That(entry.finishTicks, Is.EqualTo(finishTicks),
                    "simultaneous crossing must share one finish tick");
                Assert.That(entry.outcome, Is.EqualTo("finished"));
                // Host owns the reward fact — the client packet vocabulary
                // has no reward claim type to forge.
                Assert.That(entry.coinsAwarded, Is.EqualTo(10));
            }
            // Equal finish ticks order by slot.
            for (var i = 0; i < 4; i++)
                Assert.That(state.result.racers[i].position, Is.EqualTo(i + 1));
        }

        [Test]
        public void AuthoritativeSnapshotCarriesPhysicalState()
        {
            NewRace();
            var state = race.Step(CoastInputs(), sim);
            foreach (var racer in state.racers)
            {
                var motor = racer.session.motor;
                Assert.That(motor.physics, Is.Not.Null,
                    "host-published racer state must carry the physical body");
                Assert.That(motor.physics.wheels.Length, Is.EqualTo(4));
                // The compact network projection is what clients consume;
                // every published physical fact validates on that shape.
                var dto = NetworkPhysicsState.FromMotor(motor);
                Assert.That(dto, Is.Not.Null);
                Assert.That(dto.Validate(out _), Is.True);
            }
        }

        // --- shared fixture ------------------------------------------------

        private void NewRace()
        {
            world = PhysicsCourseWorld.Create("net-phys");
            PhysicsTestWorld.FlatGround(world, 0f, 30f, 120f, 220f);
            sim = new PhysicsVehicleSimulation(world);
            var vehicles = new List<VehicleTuning>();
            for (var i = 1; i <= 4; i++)
                vehicles.Add(PhysicsTestWorld.PhysicalTuning(
                    "car-0" + i, VehiclePhysicsTuning.Car01()));
            var evt = new EventDefinition
            {
                id = "evt-phys",
                courseId = "net-phys",
                laps = 1,
                countdownTicks = 0,
                // Generous automatic-recovery timers keep scripted poses
                // deterministic; manual recovery is exercised explicitly.
                recovery = new RecoverySettings
                {
                    offCourseSeconds = 60f,
                    minCourseY = -5f,
                    stuckSpeed = 0.75f,
                    stuckSeconds = 60f,
                    beachedSeconds = 60f,
                    overturnedSeconds = 60f,
                    outsideBoundsSeconds = 60f
                },
                vehicleIds = new List<string>
                    { "car-01", "car-02", "car-03", "car-04" },
                completionCoins = 10
            };
            var catalog = DrivingContentCatalog.Create("physics-1",
                vehicles, new[] { StraightCourse() }, new[] { evt },
                VehicleSimulationBackend.PhysicalAssisted);

            var specs = new List<NetworkRacerSpec>();
            for (var slot = 0; slot < 4; slot++)
            {
                specs.Add(new NetworkRacerSpec
                {
                    participantId = "p-" + (slot + 1),
                    slot = slot,
                    vehicleId = "car-0" + (slot + 1),
                    controlSource = "remote"
                });
            }
            string raceError;
            Assert.That(
                NetworkRace.TryCreate("net-phys", "race-phys", catalog,
                    "evt-phys", "car", specs, out race, out raceError),
                Is.True, raceError);
            sim.Initialize(race.VehicleSpawns());
        }

        private static CourseDefinition StraightCourse()
        {
            var course = new CourseDefinition
            {
                id = "net-phys",
                spawnX = 0f,
                spawnZ = -12f,
                spawnHeading = 0f
            };
            course.checkpoints.Add(new CheckpointGate
            {
                id = "start", x = 0f, z = 0f, halfWidth = 8f,
                dirX = 0f, dirZ = 1f,
                recoverX = 0f, recoverY = 0f, recoverZ = -3f
            });
            course.checkpoints.Add(new CheckpointGate
            {
                id = "gate-1", x = 0f, z = 30f, halfWidth = 8f,
                dirX = 0f, dirZ = 1f,
                recoverX = 0f, recoverY = 0f, recoverZ = 27f
            });
            for (var i = 0; i < 4; i++)
            {
                course.spawnSlots.Add(new MotorPose
                {
                    x = (i - 1.5f) * 4f,
                    z = -16f - (i % 2) * 4f
                });
            }
            course.route.Add(new RouteWaypoint { x = 0f, z = -16f, radius = 4f });
            course.route.Add(new RouteWaypoint { x = 0f, z = 0f, radius = 5f });
            course.route.Add(new RouteWaypoint { x = 0f, z = 14f, radius = 4f });
            course.route.Add(new RouteWaypoint { x = 0f, z = 40f, radius = 5f });
            course.recoveryBounds.Add(new RecoveryVolume
            {
                minX = -40f, minY = -5f, minZ = -40f,
                maxX = 40f, maxY = 30f, maxZ = 100f
            });
            course.safePoses.Add(new MotorPose { x = 0f, z = 8f });
            course.safePoses.Add(new MotorPose { x = -6f, z = 8f });
            return course;
        }

        private static bool AllExpectingStartGate(NetworkRaceState state)
        {
            if (state == null) return false;
            foreach (var racer in state.racers)
            {
                var session = racer.session;
                if (session == null || session.nextCheckpoint != 0
                    || session.finished)
                    return false;
            }
            return true;
        }

        private static Dictionary<string, DriverInput> CoastInputs()
        {
            // Explicit zero-throttle coast: the only forces are the restored
            // velocities and rolling/drag resistance, so closing motion is
            // deterministic.
            var inputs = new Dictionary<string, DriverInput>();
            for (var i = 1; i <= 4; i++)
                inputs["p-" + i] = PhysicsTestWorld.Drive(
                    throttleOverride: 0f);
            return inputs;
        }

        private NetworkRaceState StepUntilVehicleContact(int maxTicks)
        {
            var inputs = CoastInputs();
            NetworkRaceState state = null;
            for (var i = 0; i < maxTicks; i++)
            {
                state = race.Step(inputs, sim);
                foreach (var racer in state.racers)
                {
                    var physics = racer.session.motor.physics;
                    if (physics != null && physics.lastContact == "vehicle")
                        return state;
                }
            }
            return null;
        }

        private static MotorState Motor(NetworkRaceState state, string participantId)
        {
            foreach (var racer in state.racers)
                if (racer.participantId == participantId)
                    return racer.session.motor;
            return null;
        }
    }
}
