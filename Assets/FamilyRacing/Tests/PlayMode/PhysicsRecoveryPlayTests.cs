using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
using UnityEngine;

namespace FamilyRacing.Tests
{
    // Integrated recovery qualification for the physical backend: a real
    // DrivingSession bound to a real PhysicsVehicleSimulation through the
    // same prepare -> pending reset -> batch -> complete -> rule-reset
    // boundary the coordinators run. Every failure mode is produced by
    // real PhysX contact episodes, not scripted state.
    public sealed class PhysicsRecoveryPlayTests
    {
        private const float Dt = 1f / 30f;

        private PhysicsCourseWorld world;
        private PhysicsVehicleSimulation sim;
        private DrivingSession session;
        private VehiclePhysicsTuning physicsTuning;

        [TearDown]
        public void TearDown()
        {
            if (sim != null) sim.Dispose();
            sim = null;
            world = null;
            session = null;
        }

        private static CourseDefinition Lane(float spawnZ = -10f)
        {
            // Flat straight: start line behind the spawn, one mid gate,
            // one far gate — enough for progress + safe-pose bookkeeping.
            return new CourseDefinition
            {
                id = "phys-lane",
                spawnX = 0f,
                spawnY = 0f,
                spawnZ = spawnZ,
                spawnHeading = 0f,
                checkpoints = new List<CheckpointGate>
                {
                    new CheckpointGate
                    {
                        id = "start", x = 0f, z = spawnZ, dirX = 0f,
                        dirZ = 1f, halfWidth = 10f,
                        recoverX = 0f, recoverZ = spawnZ - 3f,
                        recoverY = 0f
                    },
                    new CheckpointGate
                    {
                        id = "g1", x = 0f, z = 10f, dirX = 0f, dirZ = 1f,
                        halfWidth = 10f, recoverX = 0f, recoverZ = 7f,
                        recoverY = 0f
                    },
                    new CheckpointGate
                    {
                        id = "g2", x = 0f, z = 40f, dirX = 0f, dirZ = 1f,
                        halfWidth = 10f, recoverX = 0f, recoverZ = 37f,
                        recoverY = 0f
                    }
                }
            };
        }

        private static EventDefinition LaneEvent(CourseDefinition course,
            RecoverySettings recovery)
        {
            return new EventDefinition
            {
                id = "e",
                courseId = course.id,
                laps = 1,
                countdownTicks = 0,
                vehicleIds = new List<string> { "car-01" },
                recovery = recovery ?? new RecoverySettings()
            };
        }

        // Session + real physics world on one flat drivable pad; the
        // spawn height is the settled suspension height so the first
        // ticks are honest ground contact, not a drop.
        private void NewRace(CourseDefinition course,
            RecoverySettings recovery = null)
        {
            physicsTuning = VehiclePhysicsTuning.Car01();
            var tuning = PhysicsTestWorld.PhysicalTuning("car-01",
                physicsTuning);
            world = PhysicsCourseWorld.Create("phys-recovery");
            sim = new PhysicsVehicleSimulation(world);
            var spawnY = PhysicsTestWorld.SpawnY(physicsTuning);
            var spawn = new MotorPose
            {
                x = course.spawnX,
                y = spawnY,
                z = course.spawnZ,
                heading = course.spawnHeading
            };
            session = new DrivingSession("race", tuning, course,
                LaneEvent(course, recovery), "v", "start-cmd", spawn);
            PhysicsTestWorld.FlatGround(world, 0f, 0f, 400f, 400f);
            sim.Initialize(new List<VehicleSpawn>
            {
                new VehicleSpawn
                {
                    participantId = session.ParticipantId,
                    tuning = tuning,
                    initialState = session.Snapshot().motor
                }
            });
        }

        // The exact boundary order the coordinators run.
        private DrivingStepResult Tick(DriverInput input)
        {
            var request = session.PrepareStep(input);
            if (request == null) return session.CompleteStep(null);
            if (request.reset != null)
                sim.Restore(request.reset.participantId,
                    request.reset.state);
            var results = sim.Step(new[] { request });
            var result = session.CompleteStep(
                results != null && results.Count > 0 ? results[0] : null);
            if (result != null && result.reset != null)
                sim.Restore(result.reset.participantId,
                    result.reset.state);
            return result;
        }

        private static int CountEvents(DrivingStepResult result,
            string kind)
        {
            var count = 0;
            if (result != null && result.events != null)
                foreach (var ev in result.events)
                    if (ev != null && ev.kind == kind) count++;
            return count;
        }

        private static DriverInput Coast()
        {
            return PhysicsTestWorld.Drive(throttleOverride: 0f);
        }

        [Test]
        [Timeout(120000)]
        public void BrakeHeldForTenSecondsDoesNotRecover()
        {
            NewRace(Lane());
            for (var i = 0; i < 90; i++)
                Tick(PhysicsTestWorld.Drive());
            var recoveries = 0;
            var stoppedOnGroundTicks = 0;
            for (var i = 0; i < 300; i++) // 10 s held brake at 30 Hz
            {
                recoveries += CountEvents(Tick(
                    PhysicsTestWorld.Drive(brake: 1f)), "race.recovered");
                var motor = session.Snapshot().motor;
                // `grounded` covers wheel-ray support; lastContact only
                // records CHASSIS hits, which a suspended parked car
                // never produces.
                if (Mathf.Abs(motor.speed) < 0.5f && motor.grounded)
                    stoppedOnGroundTicks++;
            }
            var finalSpeed = Mathf.Abs(
                PhysicsTestWorld.ForwardSpeed(session.Snapshot().motor));
            Assert.That(finalSpeed, Is.LessThan(0.5f),
                "setup: a held brake must actually stop the car (speed "
                + finalSpeed + ")");
            Assert.That(stoppedOnGroundTicks, Is.GreaterThan(150),
                "setup: the car sat stopped on open ground most of the "
                + "window, so a pose-based rule WOULD have fired "
                + "(ticks " + stoppedOnGroundTicks + ")");
            Assert.That(recoveries, Is.Zero,
                "deliberately braking to a stop must never recover");
        }

        [Test]
        [Timeout(120000)]
        public void CoastingParkDoesNotRecover()
        {
            NewRace(Lane());
            var recoveries = 0;
            var stoppedOnGroundTicks = 0;
            // Explicit throttle-zero coast from standstill: the car is
            // parked deliberately on open ground the whole window.
            for (var i = 0; i < 300; i++)
            {
                recoveries += CountEvents(Tick(Coast()), "race.recovered");
                var motor = session.Snapshot().motor;
                if (Mathf.Abs(motor.speed) < 0.5f && motor.grounded)
                    stoppedOnGroundTicks++;
            }
            Assert.That(stoppedOnGroundTicks, Is.GreaterThan(200),
                "setup: the car sat parked on open ground "
                + stoppedOnGroundTicks + " ticks");
            Assert.That(recoveries, Is.Zero,
                "coasting to a stop must never trigger beached recovery");
        }

        [Test]
        [Timeout(120000)]
        public void ValidTwoSecondJumpDoesNotRecover()
        {
            var course = Lane();
            // The permitted volume includes the drop's airspace: this is
            // a designed jump, not a fall off the course.
            course.recoveryBounds.Add(new RecoveryVolume
            {
                minX = -60f, minY = -2f, minZ = -60f,
                maxX = 60f, maxY = 40f, maxZ = 60f
            });
            NewRace(course);
            // A ~20 m drop is a ~2 s flight at standard gravity — longer
            // than the arcade off-course airtime window.
            var drop = PhysicsTestWorld.PhysicalSpawnState(physicsTuning,
                new Vector3(0f, 20f, 0f), 0f, forwardSpeed: 4f);
            drop.grounded = false;
            drop.physics.grounded = false;
            sim.Restore(session.ParticipantId, drop);

            var recoveries = 0;
            var maxAir = 0f;
            for (var i = 0; i < 120; i++)
            {
                recoveries += CountEvents(Tick(PhysicsTestWorld.Drive()),
                    "race.recovered");
                var motor = session.Snapshot().motor;
                if (motor.airSeconds > maxAir) maxAir = motor.airSeconds;
            }
            Assert.That(maxAir, Is.GreaterThanOrEqualTo(1.9f),
                "setup: the flight must really span ~2 s (peak "
                + maxAir + " s)");
            Assert.That(session.Snapshot().motor.grounded, Is.True,
                "the car must have landed and driven on");
            Assert.That(recoveries, Is.Zero,
                "a valid jump inside the permitted volume is not a "
                + "failure");
        }

        [Test]
        [Timeout(120000)]
        public void InvertedSupportedForTwoSecondsRecoversOnce()
        {
            NewRace(Lane());
            // Resting on its roof: attitude rolled 180 degrees about the
            // forward axis, sitting at settle height. The body contacts
            // the ground through the chassis roof, so the overturned
            // episode clock accrues from real contact.
            var inverted = PhysicsTestWorld.PhysicalSpawnState(
                physicsTuning,
                new Vector3(0f,
                    PhysicsTestWorld.SpawnY(physicsTuning) + 0.15f, 0f),
                0f);
            inverted.physics.TrySetAttitude(1f, 0f, 0f, 0f);
            inverted.physics.grounded = false;
            inverted.grounded = false;
            sim.Restore(session.ParticipantId, inverted);

            var recoveriesBeforeThreshold = 0;
            var totalRecoveries = 0;
            var detail = "";
            for (var i = 0; i < 150; i++) // 5 s window
            {
                var step = Tick(PhysicsTestWorld.Drive());
                if (CountEvents(step, "race.recovered") > 0)
                {
                    totalRecoveries++;
                    if (i < 55) recoveriesBeforeThreshold++;
                    foreach (var ev in step.events)
                        if (ev.kind == "race.recovered")
                            detail = ev.detail;
                }
            }
            Assert.That(recoveriesBeforeThreshold, Is.Zero,
                "no recovery before 2 s of continuous overturned "
                + "support");
            Assert.That(totalRecoveries, Is.EqualTo(1),
                "exactly one recovery once the threshold is crossed — "
                + "the reset clears the overturned clock");
            Assert.That(detail, Does.StartWith("overturned"),
                "the recovery reason must name the overturned episode");
            var motor = session.Snapshot().motor;
            Assert.That(motor.physics.recoveryEpoch, Is.EqualTo(1L),
                "the recovery bumped the epoch");
            Assert.That(PhysicsTestWorld.BodyUpDot(motor),
                Is.GreaterThan(0.8f),
                "the recovered body is upright again");
        }

        [Test]
        [Timeout(120000)]
        public void RecoveryDoesNotCrossGateOrChargeBoost()
        {
            NewRace(Lane());
            // Drive past gate-1 (z=10) so the safe pose moves behind it.
            for (var i = 0; i < 150
                && session.Snapshot().nextCheckpoint != 2; i++)
                Tick(PhysicsTestWorld.Drive());
            Assert.That(session.Snapshot().nextCheckpoint, Is.EqualTo(2),
                "setup: gate-1 must be accepted before the recovery");
            var chargeBefore = session.Snapshot().motor.boostCharge;

            session.QueuePending("recover", "rc-1", out _);
            var step = Tick(PhysicsTestWorld.Drive());
            Assert.That(CountEvents(step, "race.recovered"),
                Is.EqualTo(1));
            Assert.That(CountEvents(step, "checkpoint.crossed"),
                Is.EqualTo(0), "a teleport must not award a gate");
            Assert.That(CountEvents(step, "motor.cleanLanding"),
                Is.EqualTo(0), "a respawn mints no landing credit");
            Assert.That(CountEvents(step, "motor.contact"),
                Is.EqualTo(0), "a respawn emits no contact");
            var motor = session.Snapshot().motor;
            Assert.That(motor.boostCharge,
                Is.EqualTo(chargeBefore).Within(0.001f),
                "recovery preserves the existing charge");
            // The flagged boundary after the restore is excluded from
            // gate evaluation too: a few settled ticks must not award
            // gate-2 just because the respawn moved the pose.
            var crossings = 0;
            for (var i = 0; i < 20; i++)
                crossings += CountEvents(Tick(PhysicsTestWorld.Drive()),
                    "checkpoint.crossed");
            Assert.That(session.Snapshot().nextCheckpoint,
                Is.EqualTo(2), "no gate may be banked by the teleport");
            Assert.That(crossings, Is.EqualTo(0),
                "the car respawns far behind gate-2; nothing crossed");
        }

        [Test]
        [Timeout(120000)]
        public void OccupiedSafePoseUsesClearCandidate()
        {
            var course = Lane();
            // One authored fallback beside the gate-1 recover point.
            course.safePoses.Add(new MotorPose
            {
                x = 4.5f, y = 0f, z = 7f, heading = 0f
            });
            NewRace(course);
            for (var i = 0; i < 150
                && session.Snapshot().nextCheckpoint != 2; i++)
                Tick(PhysicsTestWorld.Drive());
            Assert.That(session.Snapshot().nextCheckpoint, Is.EqualTo(2),
                "setup: gate-1 accepted");
            // A peer parked on the primary safe pose: the coordinator's
            // probe reports it blocked, the candidate stays clear.
            session.PoseOccupied = pose =>
                Mathf.Abs(pose.x - 0f) < 3.5f
                    && Mathf.Abs(pose.z - 7f) < 3.5f;

            session.QueuePending("recover", "rc-occ", out _);
            Tick(PhysicsTestWorld.Drive());
            var motor = session.Snapshot().motor;
            Assert.That(motor.x, Is.EqualTo(4.5f).Within(0.05f),
                "the first UNBLOCKED authored candidate wins");
            Assert.That(motor.z, Is.EqualTo(7f).Within(0.05f));
        }
    }
}
