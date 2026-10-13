using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // Recovery contract: manual, off-course, invalid-pose and stuck
    // recoveries all return the vehicle to the last accepted safe pose.
    // The respawn is flagged, excluded from progress evaluation, never
    // increases progress, resets velocity and held motion while preserving
    // (never creating) boost charge, and the race clock keeps running.
    public sealed class RecoveryTests
    {
        private const string RunId = "recovery-test-run";
        private const float Dt = 1f / 30f;

        // Bounded ground plane (off-course past Extent), oriented
        // half-space walls like MotorTests, and a corruptible ground probe
        // for invalid-pose injection.
        private sealed class TestWorld : IMotorWorld
        {
            internal float Extent = 120f;
            internal bool CorruptSweep;

            internal struct Wall
            {
                internal float nx, nz, d;
            }

            internal readonly List<Wall> Walls = new List<Wall>();

            public GroundHit ProbeGround(MotorPose pose, float probeDistance)
            {
                var inside = Math.Abs(pose.x) <= Extent && Math.Abs(pose.z) <= Extent;
                var hit = inside && pose.y <= probeDistance;
                return new GroundHit
                {
                    hit = hit,
                    x = pose.x,
                    y = 0f,
                    z = pose.z,
                    normalY = 1f,
                    distance = pose.y,
                    surfaceId = "ground"
                };
            }

            public SweepHit SweepBody(MotorPose pose, float dx, float dy, float dz,
                float halfWidth, float halfLength, float height)
            {
                if (CorruptSweep)
                    return new SweepHit
                    {
                        hit = true,
                        fraction = float.NaN,
                        normalX = 0f,
                        normalZ = 1f,
                        surfaceId = "corrupt"
                    };
                var fx = (float)Math.Sin(pose.heading);
                var fz = (float)Math.Cos(pose.heading);
                var rx = fz;
                var rz = -fx;
                var best = new SweepHit { hit = false, fraction = 1f };
                foreach (var wall in Walls)
                {
                    var approach =
                        Math.Abs(wall.nx * rx + wall.nz * rz) * halfWidth +
                        Math.Abs(wall.nx * fx + wall.nz * fz) * halfLength;
                    var limit = wall.d - approach;
                    var s0 = pose.x * wall.nx + pose.z * wall.nz;
                    var s1 = s0 + dx * wall.nx + dz * wall.nz;
                    if (s1 <= limit) continue;
                    var fraction = s1 == s0 ? 0f : (limit - s0) / (s1 - s0);
                    if (fraction < 0f) fraction = 0f;
                    if (best.hit && fraction >= best.fraction) continue;
                    best = new SweepHit
                    {
                        hit = true,
                        fraction = fraction,
                        normalX = wall.nx,
                        normalZ = wall.nz,
                        pointX = pose.x + dx * fraction,
                        pointY = pose.y,
                        pointZ = pose.z + dz * fraction,
                        surfaceId = "wall"
                    };
                }
                return best;
            }
        }

        // Straight test course: spawn south of a single forward gate. The
        // lap can never complete (the car can't loop back to the line),
        // which keeps every recovery assertion inside the running phase.
        private static CourseDefinition StraightCourse()
        {
            return new CourseDefinition
            {
                id = "straight-test",
                spawnX = 0f,
                spawnY = 0f,
                spawnZ = -50f,
                spawnHeading = 0f,
                checkpoints = new List<CheckpointGate>
                {
                    new CheckpointGate
                    {
                        id = "start", x = 0f, z = -50f, dirX = 0f, dirZ = 1f,
                        halfWidth = 8f, recoverX = 0f, recoverZ = -53f,
                        recoverHeading = 0f
                    },
                    new CheckpointGate
                    {
                        id = "gate-1", x = 0f, z = 50f, dirX = 0f, dirZ = 1f,
                        halfWidth = 8f, recoverX = 0f, recoverZ = 47f,
                        recoverHeading = 0f
                    }
                }
            };
        }

        private static DrivingSession NewSession(CourseDefinition course,
            RecoverySettings recovery = null, int countdown = 0)
        {
            var evt = new EventDefinition
            {
                id = "e",
                courseId = course.id,
                laps = 1,
                countdownTicks = countdown,
                vehicleIds = new List<string> { "car-01" },
                recovery = recovery ?? new RecoverySettings()
            };
            return new DrivingSession("race-1", VehicleTuning.Car01(),
                course, evt, "v", "start-cmd");
        }

        private static DriverInput Neutral()
        {
            return new DriverInput(0f, false, false, false, false, false, false);
        }

        private static List<DrivingEvent> StepN(DrivingSession session,
            IMotorWorld world, int steps)
        {
            var events = new List<DrivingEvent>();
            for (var i = 0; i < steps; i++)
                events.AddRange(session.Step(Neutral(), world).events);
            return events;
        }

        private static DrivingEvent Find(List<DrivingEvent> events, string kind)
        {
            foreach (var ev in events) if (ev.kind == kind) return ev;
            return null;
        }

        private static void DriveUntilCrossed(DrivingSession session, IMotorWorld world,
            int gateIndex, int maxTicks = 1500)
        {
            for (var t = 0; t < maxTicks; t++)
            {
                var snapshot = session.Snapshot();
                if (snapshot.nextCheckpoint != gateIndex) return;
                var def = StraightCourse().checkpoints[gateIndex];
                var desired = (float)Math.Atan2(
                    def.x + def.dirX * 4f - snapshot.motor.x,
                    def.z + def.dirZ * 4f - snapshot.motor.z);
                var steer = Math.Clamp(Wrap(desired - snapshot.motor.heading) * 3f, -1f, 1f);
                session.Step(new DriverInput(steer, false, false, false, false, false, false), world);
            }
            Assert.Fail("never crossed gate " + gateIndex);
        }

        private static float Wrap(float r)
        {
            while (r > Math.PI) r -= 2f * (float)Math.PI;
            while (r <= -Math.PI) r += 2f * (float)Math.PI;
            return r;
        }

        // --- manual recovery ------------------------------------------------

        [Test]
        public void ManualRecoveryReturnsToLastAcceptedSafePose()
        {
            var course = StraightCourse();
            var world = new TestWorld();
            var session = NewSession(course);
            DriveUntilCrossed(session, world, 1); // safe pose now gate-1
            StepN(session, world, 45);            // drive on past the gate
            var far = session.Snapshot().motor;
            Assert.That(far.z, Is.GreaterThan(50f));

            string error;
            Assert.That(session.QueuePending("recover", "cmd-rec-1", out error), Is.True, error);
            var step = session.Step(Neutral(), world);
            var recovered = Find(step.events, "race.recovered");
            Assert.That(recovered, Is.Not.Null, "consuming a recovery must emit race.recovered");
            Assert.That(recovered.commandId, Is.EqualTo("cmd-rec-1"));
            Assert.That(recovered.detail, Is.EqualTo("manual"));

            var motor = session.Snapshot().motor;
            Assert.That(motor.x, Is.EqualTo(0f).Within(0.01f));
            Assert.That(motor.z, Is.EqualTo(47f).Within(0.01f),
                "recovery returns to the last accepted gate's safe pose");
            Assert.That(motor.heading, Is.EqualTo(0f).Within(0.01f));
            Assert.That(motor.recovering, Is.True,
                "the respawn boundary is flagged for observers");
        }

        [Test]
        public void RecoveryBeforeAnyGateReturnsToSpawn()
        {
            var course = StraightCourse();
            var world = new TestWorld();
            var session = NewSession(course);
            StepN(session, world, 60);
            var moved = session.Snapshot().motor;
            Assert.That(moved.z, Is.GreaterThan(-40f));

            session.QueuePending("recover", "cmd-rec", out _);
            session.Step(Neutral(), world);
            var motor = session.Snapshot().motor;
            Assert.That(motor.x, Is.EqualTo(0f).Within(0.01f));
            Assert.That(motor.z, Is.EqualTo(-50f).Within(0.01f),
                "with no accepted gate the safe pose is the spawn");
        }

        [Test]
        public void RecoveryResetsVelocityAndHeldMotionPreservesCharge()
        {
            var course = StraightCourse();
            var world = new TestWorld();
            var session = NewSession(course);
            // Build speed and engage boost + drift so the motion state is dirty.
            for (var i = 0; i < 90; i++)
                session.Step(new DriverInput(0.4f, true, true, false, false, false, false), world);
            var dirty = session.Snapshot().motor;
            Assert.That(dirty.speed, Is.GreaterThan(5f));

            var chargeBefore = dirty.boostCharge;
            session.QueuePending("recover", "cmd-rec", out _);
            session.Step(Neutral(), world);
            var motor = session.Snapshot().motor;
            Assert.That(motor.speed, Is.EqualTo(0f));
            Assert.That(motor.lateralSpeed, Is.EqualTo(0f));
            Assert.That(motor.verticalSpeed, Is.EqualTo(0f));
            Assert.That(motor.drifting, Is.False);
            Assert.That(motor.boostActive, Is.False);
            Assert.That(motor.boostCharge, Is.EqualTo(chargeBefore).Within(0.001f),
                "recovery preserves the existing charge; it must not create charge");
        }

        [Test]
        public void RespawnNeverIncreasesProgress()
        {
            var course = StraightCourse();
            var world = new TestWorld();
            var session = NewSession(course);
            DriveUntilCrossed(session, world, 1);
            StepN(session, world, 30);
            var before = session.Snapshot();

            session.QueuePending("recover", "cmd-rec", out _);
            var step = session.Step(Neutral(), world);
            Assert.That(Find(step.events, "checkpoint.crossed"), Is.Null,
                "the teleport itself is excluded from progress evaluation");
            var after = session.Snapshot();
            Assert.That(after.nextCheckpoint, Is.EqualTo(before.nextCheckpoint));
            Assert.That(after.lap, Is.EqualTo(before.lap));
            Assert.That(after.checkpoints[1].crossed, Is.True);
        }

        [Test]
        public void RaceClockContinuesDuringRecovery()
        {
            var course = StraightCourse();
            var world = new TestWorld();
            var session = NewSession(course);
            StepN(session, world, 30);
            var elapsed = session.Snapshot().elapsedTicks;
            session.QueuePending("recover", "cmd-rec", out _);
            session.Step(Neutral(), world);
            Assert.That(session.Snapshot().elapsedTicks, Is.EqualTo(elapsed + 1),
                "a recovery boundary still charges the race clock");
        }

        [Test]
        public void RepeatedRecoveryCannotCreateFinishOrCharge()
        {
            var course = StraightCourse();
            var world = new TestWorld();
            var session = NewSession(course);
            StepN(session, world, 30);
            session.QueuePending("recover", "cmd-a", out _);
            session.QueuePending("recover", "cmd-b", out _);
            session.QueuePending("recover", "cmd-c", out _);
            var recovered = 0;
            for (var i = 0; i < 3; i++)
            {
                var before = session.Snapshot().motor.boostCharge;
                var step = session.Step(Neutral(), world);
                if (Find(step.events, "race.recovered") != null) recovered++;
                Assert.That(session.Snapshot().motor.boostCharge,
                    Is.EqualTo(before).Within(0.001f),
                    "each recovery preserves rather than creates charge");
            }
            Assert.That(recovered, Is.EqualTo(3), "each queued recovery executes once");
            var state = session.Snapshot();
            Assert.That(state.phase, Is.EqualTo("running"));
            Assert.That(state.lap, Is.EqualTo(0));
            Assert.That(state.finished, Is.False);
        }

        // --- automatic recovery triggers --------------------------------------

        [Test]
        public void OffCourseCarAutoRecoversToSafePose()
        {
            var course = StraightCourse();
            var world = new TestWorld { Extent = 80f };
            var settings = new RecoverySettings
            {
                offCourseSeconds = 0.3f,
                minCourseY = -4f,
                stuckSpeed = 0.5f,
                stuckSeconds = 2f
            };
            var session = NewSession(course, settings);
            DriveUntilCrossed(session, world, 1);
            // Keep driving north past the ground edge (z > 80): the car
            // goes airborne, falls, and must auto-recover.
            string reason = null;
            var motor = session.Snapshot().motor;
            for (var t = 0; t < 600 && reason == null; t++)
            {
                var step = session.Step(Neutral(), world);
                var ev = Find(step.events, "race.recovered");
                if (ev != null) reason = ev.detail;
                motor = session.Snapshot().motor;
            }
            Assert.That(reason, Is.EqualTo("offCourse"));
            Assert.That(motor.x, Is.EqualTo(0f).Within(0.01f));
            Assert.That(motor.z, Is.EqualTo(47f).Within(0.01f),
                "auto-recovery lands on the last accepted gate pose");
            Assert.That(motor.grounded, Is.True);
        }

        [Test]
        public void WallPinnedCarAutoRecoversAsStuck()
        {
            var course = StraightCourse();
            var world = new TestWorld();
            world.Walls.Add(new TestWorld.Wall { nx = 0f, nz = 1f, d = 30f });
            var settings = new RecoverySettings
            {
                offCourseSeconds = 2f,
                minCourseY = -10f,
                stuckSpeed = 0.75f,
                stuckSeconds = 0.4f
            };
            var session = NewSession(course, settings);
            string reason = null;
            for (var t = 0; t < 1200 && reason == null; t++)
            {
                var step = session.Step(Neutral(), world);
                var ev = Find(step.events, "race.recovered");
                if (ev != null) reason = ev.detail;
            }
            Assert.That(reason, Is.EqualTo("stuck"),
                "a wall-pinned car must recover instead of grinding forever");
            var motor = session.Snapshot().motor;
            Assert.That(motor.z, Is.EqualTo(-50f).Within(0.01f));
        }

        // A vehicle stopped on open ground (pinned by something the sweep
        // never reports as a wall — e.g. a shallow obstacle) is beached:
        // lastContact stays "ground", so the wall-pinned "stuck" path can
        // never fire. The beached rule is the fallback for that signature.
        private static MotorState BeachedMotor()
        {
            return new MotorState
            {
                grounded = true,
                speed = 0.1f,
                lastContact = "ground"
            };
        }

        [Test]
        public void BeachedVehicleOnFlatGroundAutoRecovers()
        {
            var rules = new RecoveryRules();
            var settings = new RecoverySettings
            {
                stuckSpeed = 0.75f,
                stuckSeconds = 30f,
                offCourseSeconds = 30f,
                minCourseY = -20f
            };
            var motor = BeachedMotor();
            string reason = null;
            for (var i = 0; i < 300 && reason == null; i++)
                reason = rules.Evaluate(motor, settings, Dt);
            Assert.That(reason, Is.EqualTo("beached"),
                "a stopped vehicle with ground (not wall) contact must still auto-recover");
            var field = typeof(RecoverySettings).GetField("beachedSeconds");
            Assert.That(field, Is.Not.Null,
                "the beached window is editable content, not a code literal");
            Assert.That(field.GetValue(settings), Is.EqualTo(8f));
        }

        [Test]
        public void BeachedDoesNotAccumulateDuringRecovery()
        {
            // A fresh respawn is grounded/stopped/ground-contact — exactly
            // the beached signature; the recovery-flagged boundary must
            // never feed the counter or a respawn chain could re-trigger.
            var rules = new RecoveryRules();
            var settings = new RecoverySettings
            {
                stuckSpeed = 0.75f,
                stuckSeconds = 30f,
                offCourseSeconds = 30f,
                minCourseY = -20f
            };
            var motor = BeachedMotor();
            motor.recovering = true;
            for (var i = 0; i < 300; i++)
                Assert.That(rules.Evaluate(motor, settings, Dt), Is.Null,
                    "a recovery-flagged boundary must not accumulate beached time");
        }

        [Test]
        public void BeachedCounterResetsOnMovement()
        {
            var rules = new RecoveryRules();
            var settings = new RecoverySettings
            {
                stuckSpeed = 0.75f,
                stuckSeconds = 30f,
                offCourseSeconds = 30f,
                minCourseY = -20f
            };
            var motor = BeachedMotor();
            var beachedTicks = (int)Math.Ceiling(8.0 / Dt); // default window
            for (var i = 0; i < beachedTicks / 2; i++)
                Assert.That(rules.Evaluate(motor, settings, Dt), Is.Null);
            motor.speed = 5f; // moving again — the window must restart
            Assert.That(rules.Evaluate(motor, settings, Dt), Is.Null);
            motor.speed = 0.1f;
            for (var i = 0; i < beachedTicks / 2; i++)
                Assert.That(rules.Evaluate(motor, settings, Dt), Is.Null,
                    "half a window after movement must not be enough to trigger");
            string reason = null;
            for (var i = 0; i < beachedTicks && reason == null; i++)
                reason = rules.Evaluate(motor, settings, Dt);
            Assert.That(reason, Is.EqualTo("beached"),
                "a full uninterrupted window after the reset still recovers");
        }

        [Test]
        public void InvalidPoseAutoRecovers()
        {
            var course = StraightCourse();
            var world = new TestWorld();
            var settings = new RecoverySettings
            {
                offCourseSeconds = 5f,
                minCourseY = -20f,
                stuckSpeed = 0.5f,
                stuckSeconds = 5f
            };
            var session = NewSession(course, settings);
            StepN(session, world, 10);
            world.CorruptSweep = true; // sweeps now return a NaN contact
            string reason = null;
            for (var t = 0; t < 30 && reason == null; t++)
            {
                var step = session.Step(Neutral(), world);
                var ev = Find(step.events, "race.recovered");
                if (ev != null) reason = ev.detail;
            }
            world.CorruptSweep = false;
            Assert.That(reason, Is.EqualTo("invalidPose"));
            var motor = session.Snapshot().motor;
            Assert.That(float.IsNaN(motor.x) || float.IsNaN(motor.y) || float.IsNaN(motor.z),
                Is.False, "recovery must restore a finite pose");
            Assert.That(motor.z, Is.EqualTo(-50f).Within(0.01f));
        }

        [Test]
        public void RecoveryDuringCountdownStillApplies()
        {
            var course = StraightCourse();
            var world = new TestWorld();
            var session = NewSession(course, countdown: 30);
            session.QueuePending("recover", "cmd-rec", out _);
            var step = session.Step(Neutral(), world);
            Assert.That(Find(step.events, "race.recovered"), Is.Not.Null);
            Assert.That(session.Snapshot().phase, Is.EqualTo("countdown"));
        }

        // --- physical-mode recovery evaluator ---------------------------------
        // The physical backend evaluates recovery from real contact
        // episodes (wheel/body contacts, bounds volumes, overturned
        // support) and driver demand — never from raw airtime or a
        // stopped pose alone. These rules are Unity-free so they run in
        // the Core harness; the integrated PhysX cases live in
        // PhysicsRecoveryPlayTests.

        private static MotorState PhysicalMotor(bool grounded = true,
            float speed = 0.1f, string lastContact = "ground")
        {
            var motor = new MotorState
            {
                grounded = grounded,
                speed = speed,
                lastContact = lastContact
            };
            var physics = new VehiclePhysicsState
            {
                wheels = new WheelPhysicsState[4]
            };
            for (var i = 0; i < 4; i++)
                physics.wheels[i] = new WheelPhysicsState { index = i };
            physics.grounded = grounded;
            motor.physics = physics;
            return motor;
        }

        private static DriverInput BrakeHeld()
        {
            return new DriverInput(0f, false, false, false, false, false,
                false, null, 1f);
        }

        private static DriverInput Coasting()
        {
            // Explicit throttle override of 0 = deliberate coast.
            return new DriverInput(0f, false, false, false, false, false,
                false, 0f, 0f);
        }

        private static CourseDefinition BoundedCourse()
        {
            var course = StraightCourse();
            course.recoveryBounds.Add(new RecoveryVolume
            {
                minX = -120f, minY = -4f, minZ = -120f,
                maxX = 120f, maxY = 40f, maxZ = 120f
            });
            return course;
        }

        [Test]
        public void RecoverySettingsCarryPhysicalThresholds()
        {
            var settings = new RecoverySettings();
            Assert.That(settings.overturnedSeconds, Is.EqualTo(2f),
                "auto-recovery after 2 s continuous overturned contact");
            Assert.That(settings.outsideBoundsSeconds, Is.EqualTo(1.5f),
                "1.5 s authored-bounds grace");
            string error;
            Assert.That(settings.Validate(out error), Is.True, error);
            var copy = settings.Copy();
            Assert.That(copy.overturnedSeconds, Is.EqualTo(2f));
            Assert.That(copy.outsideBoundsSeconds, Is.EqualTo(1.5f));
        }

        [Test]
        public void PhysicalBrakeHeldForTenSecondsDoesNotRecover()
        {
            var rules = new RecoveryRules();
            var settings = new RecoverySettings();
            var motor = PhysicalMotor();
            var brake = BrakeHeld();
            var recoveries = 0;
            for (var i = 0; i < 300; i++) // 10 s at 30 Hz
            {
                if (rules.Evaluate(motor, brake, StraightCourse(),
                        settings, Dt) != null)
                    recoveries++;
            }
            Assert.That(recoveries, Is.Zero,
                "deliberately braking to a stop must never trigger "
                + "beached recovery");
        }

        [Test]
        public void PhysicalCoastingParkDoesNotRecover()
        {
            var rules = new RecoveryRules();
            var settings = new RecoverySettings();
            var motor = PhysicalMotor();
            var coast = Coasting();
            var recoveries = 0;
            for (var i = 0; i < 300; i++)
            {
                if (rules.Evaluate(motor, coast, StraightCourse(),
                        settings, Dt) != null)
                    recoveries++;
            }
            Assert.That(recoveries, Is.Zero,
                "an explicit throttle-zero coast to a stop is deliberate "
                + "parking, not a beached vehicle");
        }

        [Test]
        public void PhysicalStuckRequiresDriveDemandAndBlockedProgress()
        {
            var rules = new RecoveryRules();
            var settings = new RecoverySettings
            {
                stuckSeconds = 0.5f,
                beachedSeconds = 0.5f
            };
            // Auto-throttle held against open ground: drive is requested
            // but nothing moves — the beached fallback still fires.
            var motor = PhysicalMotor(lastContact: "ground");
            string reason = null;
            for (var i = 0; i < 300 && reason == null; i++)
                reason = rules.Evaluate(motor, Neutral(), StraightCourse(),
                    settings, Dt);
            Assert.That(reason, Is.EqualTo("beached"),
                "drive requested with progress blocked on open ground "
                + "is still a recovery");

            rules.Reset();
            // The same stopped pose against a wall is "stuck".
            motor = PhysicalMotor(lastContact: "wall");
            reason = null;
            for (var i = 0; i < 300 && reason == null; i++)
                reason = rules.Evaluate(motor, Neutral(), StraightCourse(),
                    settings, Dt);
            Assert.That(reason, Is.EqualTo("stuck"));
        }

        [Test]
        public void PhysicalInvertedSupportedForTwoSecondsRecoversOnce()
        {
            var rules = new RecoveryRules();
            var settings = new RecoverySettings();
            var motor = PhysicalMotor();
            var before = 0;
            // 1.9 s of continuous overturned support: no recovery yet.
            for (var i = 0; i < 57; i++)
            {
                motor.physics.overturnedSeconds += Dt;
                if (rules.Evaluate(motor, Neutral(), StraightCourse(),
                        settings, Dt) != null)
                    before++;
            }
            Assert.That(before, Is.Zero,
                "overturned contact under 2 s must not recover");
            motor.physics.overturnedSeconds = 2f;
            Assert.That(rules.Evaluate(motor, Neutral(), StraightCourse(),
                    settings, Dt), Is.EqualTo("overturned"));
        }

        [Test]
        public void PhysicalValidTwoSecondJumpDoesNotRecover()
        {
            var rules = new RecoveryRules();
            var settings = new RecoverySettings();
            var course = BoundedCourse();
            // A real jump inside the permitted volume: 2 s of airtime is
            // longer than the arcade offCourseSeconds window and must
            // still not be a failure.
            var motor = PhysicalMotor(grounded: false, speed: 15f,
                lastContact: "none");
            motor.y = 12f;
            var recoveries = 0;
            for (var i = 0; i < 75; i++) // 2.5 s airborne, inside bounds
            {
                motor.physics.airSeconds += Dt;
                motor.airSeconds = motor.physics.airSeconds;
                if (rules.Evaluate(motor, Neutral(), course, settings,
                        Dt) != null)
                    recoveries++;
            }
            Assert.That(recoveries, Is.Zero,
                "airtime inside the permitted jump volume is not a "
                + "failure");
        }

        [Test]
        public void PhysicalOutsideBoundsGraceThenRecovers()
        {
            var rules = new RecoveryRules();
            var settings = new RecoverySettings();
            var course = BoundedCourse();
            var motor = PhysicalMotor(speed: 15f, lastContact: "ground");
            motor.x = 200f; // outside every authored volume
            var recoveries = 0;
            for (var i = 0; i < 44; i++) // 1.47 s outside: inside grace
            {
                if (rules.Evaluate(motor, Neutral(), course, settings,
                        Dt) != null)
                    recoveries++;
            }
            Assert.That(recoveries, Is.Zero,
                "the authored-bounds grace is 1.5 s, not instant");
            string reason = null;
            for (var i = 0; i < 10 && reason == null; i++)
                reason = rules.Evaluate(motor, Neutral(), course,
                    settings, Dt);
            Assert.That(reason, Is.EqualTo("outsideBounds"));
            // Re-entering the volume clears the episode timer.
            rules.Reset();
            motor.x = 0f;
            Assert.That(rules.Evaluate(motor, Neutral(), course,
                settings, Dt), Is.Null);
            motor.x = 200f;
            for (var i = 0; i < 44; i++)
                Assert.That(rules.Evaluate(motor, Neutral(), course,
                    settings, Dt), Is.Null,
                    "the outside-bounds timer restarts after re-entry");
        }

        [Test]
        public void PhysicalKillHeightStillRecovers()
        {
            var rules = new RecoveryRules();
            var settings = new RecoverySettings { minCourseY = -5f };
            var motor = PhysicalMotor(grounded: false, speed: 10f,
                lastContact: "none");
            motor.y = -6f;
            Assert.That(rules.Evaluate(motor, Neutral(), BoundedCourse(),
                settings, Dt), Is.EqualTo("offCourse"),
                "the kill height remains an immediate recovery even "
                + "inside the horizontal bounds volume");
        }

        [Test]
        public void CourseRecoveryBoundsAndSafePosesValidateAndCopy()
        {
            var course = StraightCourse();
            course.recoveryBounds.Add(new RecoveryVolume
            {
                minX = -10f, minY = -2f, minZ = -10f,
                maxX = 10f, maxY = 5f, maxZ = 10f
            });
            course.safePoses.Add(new MotorPose { x = 3f, z = -50f });
            string error;
            Assert.That(course.Validate(out error), Is.True, error);

            var copy = course.Copy();
            Assert.That(copy.recoveryBounds.Count, Is.EqualTo(1));
            Assert.That(copy.safePoses.Count, Is.EqualTo(1));
            copy.recoveryBounds[0].minX = -999f;
            var moved = copy.safePoses[0];
            moved.x = 42f;
            copy.safePoses[0] = moved;
            Assert.That(course.recoveryBounds[0].minX, Is.EqualTo(-10f),
                "copied bounds must not alias the original");
            Assert.That(course.safePoses[0].x, Is.EqualTo(3f));

            // An inverted volume can never be authored.
            var broken = StraightCourse();
            broken.recoveryBounds.Add(new RecoveryVolume
            {
                minX = 10f, maxX = -10f, minY = -1f, maxY = 1f,
                minZ = -1f, maxZ = 1f
            });
            Assert.That(broken.Validate(out error), Is.False,
                "min > max bounds must fail validation");
            Assert.That(error, Is.EqualTo("recoveryBounds"));
        }

        // A minimal physical IVehicleSimulation for CoreOffline session
        // tests: returns the prepared rulesBefore plus an attached
        // physics envelope, advancing z and the overturned clock when
        // inverted so the session's recovery and economy paths run end
        // to end without Unity.
        private sealed class FakePhysicalSimulation : IVehicleSimulation
        {
            internal float speed = 10f;
            internal bool inverted;
            internal int restoreCalls;

            public void Initialize(IReadOnlyList<VehicleSpawn> vehicles) { }

            public IReadOnlyList<VehicleTickResult> Step(
                IReadOnlyList<VehicleTickRequest> requests)
            {
                var results = new List<VehicleTickResult>();
                if (requests == null) return results;
                foreach (var request in requests)
                {
                    var state = request.rulesBefore != null
                        ? request.rulesBefore.Copy()
                        : new MotorState();
                    if (request.simulateMotion)
                    {
                        if (state.physics == null)
                            state.physics = Physics();
                        state.speed = speed;
                        state.grounded = true;
                        state.physics.grounded = true;
                        state.z += speed * Dt;
                        if (inverted)
                            state.physics.overturnedSeconds += Dt;
                        else
                            state.physics.overturnedSeconds = 0f;
                    }
                    var pose = state.Pose;
                    results.Add(new VehicleTickResult
                    {
                        participantId = request.participantId,
                        step = new MotorStepResult { state = state },
                        path = new[]
                        {
                            new VehiclePathSample
                                { offsetSeconds = 0f, pose = pose },
                            new VehiclePathSample
                                { offsetSeconds = 1f / 60f, pose = pose },
                            new VehiclePathSample
                                { offsetSeconds = 1f / 30f, pose = pose }
                        }
                    });
                }
                return results;
            }

            public void Restore(string participantId, MotorState state)
            {
                restoreCalls++;
                // The body's reset clears the episode clocks a real
                // restore would: an uprighted car is no longer inverted.
                inverted = false;
            }

            public MotorState Capture(string participantId)
            {
                return null;
            }

            public void Dispose() { }

            internal static VehiclePhysicsState Physics()
            {
                var physics = new VehiclePhysicsState
                {
                    wheels = new WheelPhysicsState[4]
                };
                for (var i = 0; i < physics.wheels.Length; i++)
                    physics.wheels[i] = new WheelPhysicsState { index = i };
                return physics;
            }
        }

        private static VehicleTuning PhysicalCar()
        {
            var tuning = VehicleTuning.Car01();
            tuning.simulationBackend = VehicleSimulationBackend.PhysicalAssisted;
            tuning.physics = VehiclePhysicsTuning.Car01();
            return tuning;
        }

        private static DrivingSession NewPhysicalSession(
            CourseDefinition course, RecoverySettings recovery = null)
        {
            var evt = new EventDefinition
            {
                id = "e",
                courseId = course.id,
                laps = 1,
                countdownTicks = 0,
                vehicleIds = new List<string> { "car-01" },
                recovery = recovery ?? new RecoverySettings()
            };
            return new DrivingSession("race-1", PhysicalCar(), course, evt,
                "v", "start-cmd");
        }

        // The coordinator boundary the session + simulation share:
        // prepare -> pending reset -> one batch -> complete ->
        // rule-requested reset — the same shape NetworkRace and the solo
        // service run.
        private static DrivingStepResult Tick(DrivingSession session,
            IVehicleSimulation sim, DriverInput input)
        {
            var request = session.PrepareStep(input);
            if (request == null) return session.CompleteStep(null);
            if (request.reset != null)
                sim.Restore(request.reset.participantId, request.reset.state);
            var results = sim.Step(new[] { request });
            var result = session.CompleteStep(
                results != null && results.Count > 0 ? results[0] : null);
            if (result != null && result.reset != null)
                sim.Restore(result.reset.participantId, result.reset.state);
            return result;
        }

        [Test]
        public void PhysicalSessionInvertedForTwoSecondsRecoversOnce()
        {
            var session = NewPhysicalSession(StraightCourse());
            var sim = new FakePhysicalSimulation { inverted = true };
            var before2Seconds = 0;
            var after = 0;
            for (var i = 0; i < 120; i++) // 4 s at 30 Hz
            {
                var result = Tick(session, sim, Neutral());
                if (Find(result.events, "race.recovered") != null)
                {
                    if (i < 58) before2Seconds++;
                    else after++;
                }
            }
            Assert.That(before2Seconds, Is.Zero,
                "no recovery before 2 s of continuous overturned support");
            Assert.That(after, Is.EqualTo(1),
                "exactly one recovery once the threshold is crossed — "
                + "the reset clears the overturned clock");
            Assert.That(session.Snapshot().motor.physics.recoveryEpoch,
                Is.EqualTo(1), "the recovery bumped the epoch");
            Assert.That(sim.restoreCalls, Is.GreaterThanOrEqualTo(1),
                "the rule-requested reset reached the body");
        }

        [Test]
        public void PhysicalRecoveryClearsMotionAndControllerHistories()
        {
            var session = NewPhysicalSession(StraightCourse());
            var sim = new FakePhysicalSimulation { speed = 12f };
            Tick(session, sim, Neutral());
            var committed = session.Snapshot().motor;
            Assert.That(committed.physics, Is.Not.Null,
                "the physical backend commits a physics envelope");
            var chargeBefore = committed.boostCharge;

            session.QueuePending("recover", "cmd-rec", out _);
            Tick(session, sim, Neutral());
            var motor = session.Snapshot().motor;
            Assert.That(motor.speed, Is.EqualTo(0f));
            Assert.That(motor.lateralSpeed, Is.EqualTo(0f));
            Assert.That(motor.drifting, Is.False);
            Assert.That(motor.boostActive, Is.False);
            Assert.That(motor.boostCharge,
                Is.EqualTo(chargeBefore).Within(0.001f),
                "recovery preserves, never creates, boost charge");
            Assert.That(motor.physics, Is.Not.Null);
            Assert.That(motor.physics.recoveryEpoch, Is.EqualTo(1));
            Assert.That(motor.physics.velocityX, Is.EqualTo(0f));
            Assert.That(motor.physics.velocityY, Is.EqualTo(0f));
            Assert.That(motor.physics.velocityZ, Is.EqualTo(0f));
            Assert.That(motor.physics.angularVelocityX, Is.EqualTo(0f));
            Assert.That(motor.physics.filteredSteering, Is.EqualTo(0f));
            Assert.That(motor.physics.filteredRollRate, Is.EqualTo(0f));
            Assert.That(motor.physics.driftAmount, Is.EqualTo(0f));
            Assert.That(motor.physics.overturnedSeconds, Is.EqualTo(0f));
            foreach (var wheel in motor.physics.wheels)
                Assert.That(wheel.contactValid, Is.False,
                    "damper history is invalidated on recovery");
        }

        [Test]
        public void OccupiedSafePoseUsesClearCandidate()
        {
            var course = StraightCourse();
            // Deterministic authored fallbacks behind the spawn.
            course.safePoses.Add(new MotorPose { x = 4f, z = -50f });
            course.safePoses.Add(new MotorPose { x = 8f, z = -50f });
            var world = new TestWorld();
            var session = NewSession(course);
            // Another racer blocks the spawn and the first candidate —
            // the occupancy probe answers "occupied" for both.
            session.PoseOccupied = pose =>
                (System.Math.Abs(pose.x - 0f) < 2f
                    && System.Math.Abs(pose.z + 50f) < 2f)
                || (System.Math.Abs(pose.x - 4f) < 2f
                    && System.Math.Abs(pose.z + 50f) < 2f);
            StepN(session, world, 10);
            session.QueuePending("recover", "cmd-occ", out _);
            var step = session.Step(Neutral(), world);
            Assert.That(Find(step.events, "race.recovered"), Is.Not.Null);
            var motor = session.Snapshot().motor;
            Assert.That(motor.x, Is.EqualTo(8f).Within(0.01f),
                "the first UNBLOCKED authored candidate wins");
            Assert.That(motor.z, Is.EqualTo(-50f).Within(0.01f));
        }

        [Test]
        public void PhysicalRecoveryTeleportEarnsNoGateOrCharge()
        {
            var session = NewPhysicalSession(StraightCourse());
            var sim = new FakePhysicalSimulation { speed = 12f };
            Tick(session, sim, Neutral());
            var before = session.Snapshot();
            var chargeBefore = before.motor.boostCharge;

            session.QueuePending("recover", "cmd-rec", out _);
            var result = Tick(session, sim, Neutral());
            var checkpointsAddedByTeleport = 0;
            foreach (var ev in result.events)
                if (ev.kind == "checkpoint.crossed")
                    checkpointsAddedByTeleport++;
            Assert.That(checkpointsAddedByTeleport, Is.Zero,
                "the teleport is excluded from checkpoint evaluation");
            var chargeAfterRecovery = session.Snapshot().motor.boostCharge;
            Assert.That(chargeAfterRecovery,
                Is.EqualTo(chargeBefore).Within(0.001f),
                "recovery preserves the existing charge");
        }

        // --- command-path integration -----------------------------------------

        private sealed class TestJsonSerializer : IProfileSerializer
        {
#if UNITY_5_3_OR_NEWER
            public string Serialize(ProfileSave profile) { return UnityEngine.JsonUtility.ToJson(profile); }
            public ProfileSave Deserialize(string json) { return UnityEngine.JsonUtility.FromJson<ProfileSave>(json); }
#else
            private static readonly System.Text.Json.JsonSerializerOptions Options =
                new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
            public string Serialize(ProfileSave profile) { return System.Text.Json.JsonSerializer.Serialize(profile, Options); }
            public ProfileSave Deserialize(string json) { return System.Text.Json.JsonSerializer.Deserialize<ProfileSave>(json, Options); }
#endif
        }

        private static GameCommand Cmd(string action, Dictionary<string, string> parameters,
            string commandId = null)
        {
            return new GameCommand(1, RunId, commandId ?? Guid.NewGuid().ToString("N"),
                "local", action, parameters);
        }

        private static Dictionary<string, string> P(params string[] keyValues)
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i + 1 < keyValues.Length; i += 2)
                map[keyValues[i]] = keyValues[i + 1];
            return map;
        }

        [Test]
        public void RecoveryCommandCorrelationTravelsToRecoveredEvent()
        {
            var directory = Path.Combine(Path.GetTempPath(),
                "family-racing-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var service = new FoundationService(
                    new ProfileStore(directory, new TestJsonSerializer()), "en", RunId);
                var course = StraightCourse();
                var evt = new EventDefinition
                {
                    id = "e",
                    courseId = course.id,
                    laps = 1,
                    countdownTicks = 1,
                    vehicleIds = new List<string> { "car-01" }
                };
                service.SetDrivingContent(DrivingContentCatalog.Create("v",
                    new[] { VehicleTuning.Car01() }, new[] { course }, new[] { evt }));
                var world = new TestWorld();
                Assert.That(service.Execute(Cmd("profile.create",
                    P("slot", "0", "language", "en"))).status,
                    Is.EqualTo(CommandResult.Applied));
                Assert.That(service.Execute(Cmd("race.start",
                    P("vehicleId", "car-01", "eventId", "e"))).status,
                    Is.EqualTo(CommandResult.Applied));
                service.StepDriving(world);
                service.AdvanceTick(); // countdown -> running

                var queued = service.Execute(Cmd("input.set",
                    P("steering", "0", "drift", "false", "boost", "false", "gadget", "false",
                        "recover", "true", "changeCamera", "false", "pause", "false",
                        "durationTicks", "5"), "recover-cmd-1"));
                Assert.That(queued.status, Is.EqualTo(CommandResult.Applied));

                service.StepDriving(world);
                service.AdvanceTick();
                var events = service.Snapshot().drivingEvents;
                DrivingEvent recovered = null;
                foreach (var ev in events)
                    if (ev.kind == "race.recovered") recovered = ev;
                Assert.That(recovered, Is.Not.Null,
                    "the queued recovery must surface as a recovered event");
                Assert.That(recovered.commandId, Is.EqualTo("recover-cmd-1"),
                    "the recovered event correlates the original command identity");
                Assert.That(service.Snapshot().driving.motor.recovering, Is.True);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }
    }
}
