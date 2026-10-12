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
