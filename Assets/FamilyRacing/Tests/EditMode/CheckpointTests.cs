using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // Ordered directional gate crossings: the swept previous->current pose
    // segment, evaluated in travel order, is the only thing that advances
    // progress. Reverse crossings, gate re-entry, out-of-order gates,
    // start-line crossings before the cycle, and respawn teleports must
    // never manufacture a checkpoint, a lap, or a finish. Exactly one lap
    // per valid gate cycle and one terminal finish.
    public sealed class CheckpointTests
    {
        private const float Pi = (float)Math.PI;

        private sealed class FlatWorld : IMotorWorld
        {
            public GroundHit ProbeGround(MotorPose pose, float probeDistance)
            {
                return new GroundHit
                {
                    hit = pose.y <= probeDistance,
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
                return new SweepHit { hit = false, fraction = 1f };
            }
        }

        private static CheckpointGate Gate(string id, float x, float z,
            float dirX, float dirZ, float halfWidth = 8f)
        {
            return new CheckpointGate
            {
                id = id,
                x = x,
                z = z,
                dirX = dirX,
                dirZ = dirZ,
                halfWidth = halfWidth,
                // Safe respawn sits just behind the plane along the travel
                // direction, facing the way the lap flows.
                recoverX = x - dirX * 3f,
                recoverY = 0f,
                recoverZ = z - dirZ * 3f,
                recoverHeading = (float)Math.Atan2(dirX, dirZ)
            };
        }

        // Counter-clockwise circular course: start line at (0,-R) crossed
        // heading +x; gates on the circle tangent to the travel direction.
        private static CourseDefinition CircleCourse(float radius = 40f, int gateCount = 4)
        {
            var names = new[] { "start", "gate-1", "gate-2", "gate-3", "gate-4", "gate-5" };
            var course = new CourseDefinition
            {
                id = "circle-test",
                spawnX = 0f,
                spawnY = 0f,
                spawnZ = -radius,
                spawnHeading = Pi / 2f,
                checkpoints = new List<CheckpointGate>()
            };
            for (var k = 0; k < gateCount; k++)
            {
                var t = k * 2f * Pi / gateCount;
                course.checkpoints.Add(Gate(names[k],
                    radius * (float)Math.Sin(t), -radius * (float)Math.Cos(t),
                    (float)Math.Cos(t), (float)Math.Sin(t)));
            }
            return course;
        }

        private static CheckpointState FreshState(CourseDefinition course, int laps = 1)
        {
            return CheckpointProgress.Create(course, laps);
        }

        private static CheckpointResult Advance(CourseDefinition course, CheckpointState state,
            float x0, float z0, float x1, float z1)
        {
            return CheckpointProgress.Advance(course, state,
                new MotorPose { x = x0, z = z0 },
                new MotorPose { x = x1, z = z1 });
        }

        private static List<string> Kinds(CheckpointResult result)
        {
            var kinds = new List<string>();
            foreach (var ev in result.events) kinds.Add(ev.kind);
            return kinds;
        }

        // --- swept directional crossing contract --------------------------

        [Test]
        public void ForwardCrossingOfExpectedGateAdvances()
        {
            var course = CircleCourse();
            var state = FreshState(course);
            Assert.That(state.expectedCheckpoint, Is.EqualTo(1),
                "the start line counts as pre-crossed: the first required gate is gate-1");
            var result = Advance(course, state, 38f, -6f, 42f, 6f);
            Assert.That(state.expectedCheckpoint, Is.EqualTo(2));
            Assert.That(Kinds(result), Does.Contain("checkpoint"));
            Assert.That(result.events[0].gateId, Is.EqualTo("gate-1"));
            Assert.That(state.marks[1].crossed, Is.True);
        }

        [Test]
        public void ReverseCrossingOfExpectedGateIgnored()
        {
            var course = CircleCourse();
            var state = FreshState(course);
            var result = Advance(course, state, 42f, 6f, 38f, -6f);
            Assert.That(result.events, Is.Empty,
                "crossing the plane against its direction must not count");
            Assert.That(state.expectedCheckpoint, Is.EqualTo(1));
            Assert.That(state.marks[1].crossed, Is.False);
        }

        [Test]
        public void GateReentryDoesNotDoubleCount()
        {
            var course = CircleCourse();
            var state = FreshState(course);
            Advance(course, state, 38f, -6f, 42f, 6f); // accepted gate-1
            // Back through the plane, then forward again: neither is a
            // second forward crossing of a still-expected gate.
            var backwards = Advance(course, state, 42f, 6f, 38f, -6f);
            var forwards = Advance(course, state, 38f, -6f, 42f, 6f);
            Assert.That(backwards.events, Is.Empty);
            Assert.That(forwards.events, Is.Empty,
                "re-crossing an already-accepted gate must not recount");
            Assert.That(state.expectedCheckpoint, Is.EqualTo(2));
        }

        [Test]
        public void OutOfOrderGateIgnoredUntilExpected()
        {
            var course = CircleCourse();
            var state = FreshState(course);
            // Cross gate-2's plane (x=0 near z=40 heading -x) while gate-1
            // is still expected: it cannot count early or be banked.
            var skipped = Advance(course, state, 4f, 40f, -4f, 40f);
            Assert.That(skipped.events, Is.Empty);
            Assert.That(state.expectedCheckpoint, Is.EqualTo(1));
            var first = Advance(course, state, 38f, -6f, 42f, 6f);
            Assert.That(first.events[0].gateId, Is.EqualTo("gate-1"));
            Assert.That(state.expectedCheckpoint, Is.EqualTo(2),
                "gate-2 must be crossed again after gate-1 to count");
        }

        [Test]
        public void StartLineBeforeFullCycleDoesNotLap()
        {
            var course = CircleCourse();
            var state = FreshState(course, laps: 3);
            // The line is pre-crossed by the spawn; crossing it again
            // before every gate has been taken must not award a lap.
            var result = Advance(course, state, -4f, -40f, 4f, -40f);
            Assert.That(result.events, Is.Empty);
            Assert.That(state.lap, Is.EqualTo(0));
            Assert.That(state.expectedCheckpoint, Is.EqualTo(1));
        }

        [Test]
        public void TwoValidGatesInOneFastStepCountInTravelOrder()
        {
            var course = CircleCourse();
            var state = FreshState(course, laps: 2);
            // One long sweep crosses gate-1's plane (z=0 near x=40) then
            // gate-2's plane (x=0 near z=40); both must register.
            var result = Advance(course, state, 40f, -5f, -5f, 42f);
            Assert.That(result.events.Count, Is.EqualTo(2));
            Assert.That(result.events[0].kind, Is.EqualTo("checkpoint"));
            Assert.That(result.events[0].gateId, Is.EqualTo("gate-1"));
            Assert.That(result.events[1].gateId, Is.EqualTo("gate-2"));
            Assert.That(state.expectedCheckpoint, Is.EqualTo(3));
            Assert.That(state.marks[1].crossed && state.marks[2].crossed, Is.True);
        }

        [Test]
        public void CrossingOutsideGateWidthIgnored()
        {
            var course = CircleCourse();
            var state = FreshState(course);
            // Cross gate-1's plane at x=20, outside the 8m half-width.
            var result = Advance(course, state, 20f, -6f, 20f, 6f);
            Assert.That(result.events, Is.Empty);
            Assert.That(state.expectedCheckpoint, Is.EqualTo(1));
        }

        [Test]
        public void NonFinitePoseNeverAdvances()
        {
            var course = CircleCourse();
            var state = FreshState(course);
            var nan = CheckpointProgress.Advance(course, state,
                new MotorPose { x = float.NaN, z = -6f },
                new MotorPose { x = 42f, z = 6f });
            var inf = CheckpointProgress.Advance(course, state,
                new MotorPose { x = 38f, z = -6f },
                new MotorPose { x = float.PositiveInfinity, z = 6f });
            Assert.That(nan.events, Is.Empty);
            Assert.That(inf.events, Is.Empty);
            Assert.That(state.expectedCheckpoint, Is.EqualTo(1));
        }

        // --- lap / finish contract -----------------------------------------

        private static void CrossAllGates(CourseDefinition course, CheckpointState state)
        {
            // gate-1: z=0 near x=40 heading +z; gate-2: x=0 near z=40
            // heading -x; gate-3: z=0 near x=-40 heading -z; start: x=0
            // near z=-40 heading +x.
            Advance(course, state, 38f, -6f, 42f, 6f);
            Advance(course, state, 6f, 38f, -6f, 42f);
            Advance(course, state, -38f, 6f, -42f, -6f);
            Advance(course, state, -4f, -44f, 4f, -36f);
        }

        [Test]
        public void FullGateCycleIncrementsLapExactlyOnce()
        {
            var course = CircleCourse();
            var state = FreshState(course, laps: 2);
            var events = new List<CheckpointEvent>();
            var r1 = Advance(course, state, 38f, -6f, 42f, 6f);
            var r2 = Advance(course, state, 6f, 38f, -6f, 42f);
            var r3 = Advance(course, state, -38f, 6f, -42f, -6f);
            var r4 = Advance(course, state, -4f, -44f, 4f, -36f);
            events.AddRange(r1.events);
            events.AddRange(r2.events);
            events.AddRange(r3.events);
            events.AddRange(r4.events);
            Assert.That(state.lap, Is.EqualTo(1), "one lap per valid gate cycle");
            Assert.That(state.expectedCheckpoint, Is.EqualTo(1),
                "the next lap expects gate-1 again");
            var lapEvents = 0;
            var checkpoints = 0;
            foreach (var ev in events)
            {
                if (ev.kind == "lap")
                {
                    lapEvents++;
                    Assert.That(ev.lap, Is.EqualTo(1));
                }
                if (ev.kind == "checkpoint") checkpoints++;
            }
            Assert.That(lapEvents, Is.EqualTo(1));
            Assert.That(checkpoints, Is.EqualTo(4));
            Assert.That(state.marks[0].crossed, Is.True, "the new lap starts on the line");
            Assert.That(state.marks[1].crossed, Is.False, "gate marks reset for the new lap");
        }

        [Test]
        public void FinishIsTerminalAndDuplicateSafe()
        {
            var course = CircleCourse();
            var state = FreshState(course, laps: 1);
            CrossAllGates(course, state);
            Assert.That(state.finished, Is.True);
            Assert.That(state.lap, Is.EqualTo(1));
            // Any further travel can never produce another finish.
            var extra = CrossAllAndCollect(course, state);
            Assert.That(extra, Is.Empty, "a finished tracker must stay inert");
            Assert.That(state.lap, Is.EqualTo(1));
        }

        private static List<CheckpointEvent> CrossAllAndCollect(
            CourseDefinition course, CheckpointState state)
        {
            var collected = new List<CheckpointEvent>();
            var segments = new[]
            {
                new[] { 38f, -6f, 42f, 6f }, new[] { 6f, 38f, -6f, 42f },
                new[] { -38f, 6f, -42f, -6f }, new[] { -4f, -44f, 4f, -36f }
            };
            foreach (var s in segments)
                collected.AddRange(Advance(course, state, s[0], s[1], s[2], s[3]).events);
            return collected;
        }

        // --- shortcut / recovery-pose contract ------------------------------

        [Test]
        public void BothLegalShortcutRoutesCountProgress()
        {
            // Two different branches between gate-1 and gate-2 that both
            // rejoin before the mandatory gate. Neither may award extra
            // progress; the mandatory crossing is what counts.
            var routes = new[]
            {
                new[] // north branch: wide around the outside
                {
                    new[] { 42f, 6f, 36f, 24f }, new[] { 36f, 24f, 18f, 38f },
                    new[] { 18f, 38f, -6f, 42f }
                },
                new[] // inside branch: tight to the corner
                {
                    new[] { 42f, 6f, 44f, 20f }, new[] { 44f, 20f, 30f, 34f },
                    new[] { 30f, 34f, -6f, 41f }
                }
            };
            foreach (var route in routes)
            {
                var course = CircleCourse();
                var state = FreshState(course, laps: 2);
                Advance(course, state, 38f, -6f, 42f, 6f); // gate-1
                foreach (var segment in route)
                    Advance(course, state, segment[0], segment[1], segment[2], segment[3]);
                Assert.That(state.expectedCheckpoint, Is.EqualTo(3),
                    "a legal branch rejoins before gate-2 and counts it exactly once");
                Assert.That(state.lap, Is.EqualTo(0));
            }
        }

        [Test]
        public void BranchThatSkipsMandatoryGateGrantsNothing()
        {
            var course = CircleCourse();
            var state = FreshState(course, laps: 2);
            Advance(course, state, 38f, -6f, 42f, 6f); // gate-1 accepted
            // Cut far outside gate-2 (cross x=0 at z=60, beyond half-width),
            // then cross gate-3's plane: nothing may be banked.
            Advance(course, state, 6f, 58f, -6f, 62f);
            var skipped = Advance(course, state, -38f, 6f, -42f, -6f);
            Assert.That(skipped.events, Is.Empty);
            Assert.That(state.expectedCheckpoint, Is.EqualTo(2));
            Assert.That(state.lap, Is.EqualTo(0));
        }

        [Test]
        public void AcceptedCrossingUpdatesRecoveryPose()
        {
            var course = CircleCourse();
            var state = FreshState(course, laps: 2);
            var first = Advance(course, state, 38f, -6f, 42f, 6f);
            Assert.That(first.recoveryPoseUpdated, Is.True);
            var gate1 = course.checkpoints[1];
            Assert.That(first.recoveryPose.x, Is.EqualTo(gate1.recoverX).Within(0.001f));
            Assert.That(first.recoveryPose.z, Is.EqualTo(gate1.recoverZ).Within(0.001f));
            var second = Advance(course, state, 6f, 38f, -6f, 42f);
            var gate2 = course.checkpoints[2];
            Assert.That(second.recoveryPose.x, Is.EqualTo(gate2.recoverX).Within(0.001f),
                "the latest accepted gate wins the safe pose");
        }

        // --- authoring validation -------------------------------------------

        [Test]
        public void ValidationRejectsDuplicateGateIds()
        {
            var course = CircleCourse();
            course.checkpoints.Add(Gate("gate-1", 0f, 0f, 0f, 1f));
            string error;
            Assert.That(course.Validate(out error), Is.False);
            Assert.That(error, Is.EqualTo("checkpoint.duplicate"));
        }

        [Test]
        public void ValidationRejectsZeroGateDirection()
        {
            var course = CircleCourse();
            course.checkpoints[1].dirX = 0f;
            course.checkpoints[1].dirZ = 0f;
            string error;
            Assert.That(course.Validate(out error), Is.False);
            Assert.That(error, Is.EqualTo("checkpoint.direction"));
        }

        [Test]
        public void ValidationRejectsNonFiniteRecoveryPose()
        {
            var course = CircleCourse();
            course.checkpoints[1].recoverX = float.NaN;
            string error;
            Assert.That(course.Validate(out error), Is.False);
            Assert.That(error, Is.EqualTo("checkpoint.recover"));
        }

        [Test]
        public void ValidationRejectsBadEventAndRecoverySettings()
        {
            var evt = new EventDefinition
            {
                id = "e",
                courseId = "c",
                laps = 0,
                countdownTicks = 0,
                vehicleIds = new List<string> { "car-01" }
            };
            string error;
            Assert.That(evt.Validate(out error), Is.False);
            Assert.That(error, Is.EqualTo("laps"));

            evt.laps = 1;
            evt.recovery.offCourseSeconds = float.NaN;
            Assert.That(evt.Validate(out error), Is.False);
            Assert.That(error, Is.EqualTo("recovery.offCourseSeconds"));
        }

        [Test]
        public void CatalogRejectsMalformedCourseBeforeStart()
        {
            var badCourse = CircleCourse();
            badCourse.checkpoints[1].halfWidth = -2f;
            var evt = new EventDefinition
            {
                id = "e",
                courseId = badCourse.id,
                laps = 1,
                vehicleIds = new List<string> { "car-01" }
            };
            Assert.Throws<ArgumentException>(() => DrivingContentCatalog.Create(
                "v", new[] { VehicleTuning.Car01() }, new[] { badCourse }, new[] { evt }),
                "a malformed course must never reach race.start availability");
        }

        // --- session integration ----------------------------------------------

        private static float WrapAngle(float radians)
        {
            while (radians > Pi) radians -= 2f * Pi;
            while (radians <= -Pi) radians += 2f * Pi;
            return radians;
        }

        // Pursue each expected gate center (aimed slightly past the plane so
        // the sweep crosses cleanly) through the session's own nextCheckpoint.
        private static List<DrivingEvent> DriveToFinish(
            DrivingSession session, IMotorWorld world, CourseDefinition course, int maxTicks)
        {
            var events = new List<DrivingEvent>();
            for (var t = 0; t < maxTicks; t++)
            {
                var snapshot = session.Snapshot();
                if (snapshot.phase == "finished") break;
                var gate = course.checkpoints[snapshot.nextCheckpoint];
                var tx = gate.x + gate.dirX * 6f;
                var tz = gate.z + gate.dirZ * 6f;
                var desired = (float)Math.Atan2(tx - snapshot.motor.x, tz - snapshot.motor.z);
                var steer = Math.Clamp(WrapAngle(desired - snapshot.motor.heading) * 3f, -1f, 1f);
                var result = session.Step(
                    new DriverInput(steer, false, false, false, false, false, false), world);
                events.AddRange(result.events);
            }
            return events;
        }

        [Test]
        public void SessionCompletesTwoLapsWithOrderedEventsAndTerminalFinish()
        {
            var course = CircleCourse();
            var evt = new EventDefinition
            {
                id = "e",
                courseId = course.id,
                laps = 2,
                countdownTicks = 0,
                vehicleIds = new List<string> { "car-01" }
            };
            var session = new DrivingSession("race-1", VehicleTuning.Car01(),
                course, evt, "v", "start-cmd");
            var events = DriveToFinish(session, new FlatWorld(), course, 6000);
            var state = session.Snapshot();
            Assert.That(state.phase, Is.EqualTo("finished"),
                "the pursuit must complete two laps; last pose "
                + state.motor.x + "," + state.motor.z + " next=" + state.nextCheckpoint);
            Assert.That(state.finished, Is.True);
            Assert.That(state.lap, Is.EqualTo(2));
            Assert.That(state.finishTicks, Is.GreaterThan(0));
            Assert.That(state.finishTicks, Is.EqualTo(state.elapsedTicks),
                "the finish clock freezes at the terminal boundary");

            var checkpoints = 0;
            var laps = 0;
            var finishes = 0;
            foreach (var ev in events)
            {
                if (ev.kind == "checkpoint.crossed") checkpoints++;
                if (ev.kind == "race.lap") laps++;
                if (ev.kind == "race.finished") finishes++;
            }
            Assert.That(checkpoints, Is.EqualTo(8), "four gates per lap, two laps");
            Assert.That(laps, Is.EqualTo(2));
            Assert.That(finishes, Is.EqualTo(1), "exactly one terminal finish");

            // After the finish the session is inert: no further progress.
            var extra = session.Step(
                new DriverInput(0f, false, false, false, false, false, false),
                new FlatWorld());
            Assert.That(extra.events, Is.Empty);
            Assert.That(session.Snapshot().lap, Is.EqualTo(2));
        }

        [Test]
        public void SessionSnapshotReportsProgress()
        {
            var course = CircleCourse();
            var evt = new EventDefinition
            {
                id = "e",
                courseId = course.id,
                laps = 2,
                countdownTicks = 0,
                vehicleIds = new List<string> { "car-01" }
            };
            var session = new DrivingSession("race-1", VehicleTuning.Car01(),
                course, evt, "v", "start-cmd");
            var world = new FlatWorld();
            var state = session.Snapshot();
            Assert.That(state.nextCheckpoint, Is.EqualTo(1));
            Assert.That(state.lap, Is.EqualTo(0));
            Assert.That(state.checkpoints.Count, Is.EqualTo(4));

            // Drive until the first gate registers, then confirm the
            // snapshot reports the ordered progress.
            for (var t = 0; t < 900 && state.nextCheckpoint == 1; t++)
            {
                var motor = session.Snapshot().motor;
                var gate = course.checkpoints[1];
                var desired = (float)Math.Atan2(gate.x + 4f - motor.x, gate.z + 4f - motor.z);
                var steer = Math.Clamp(WrapAngle(desired - motor.heading) * 3f, -1f, 1f);
                session.Step(new DriverInput(steer, false, false, false, false, false, false),
                    world);
                state = session.Snapshot();
            }
            Assert.That(state.nextCheckpoint, Is.EqualTo(2),
                "crossing gate-1 must advance the observed expected checkpoint");
            Assert.That(state.checkpoints[1].crossed, Is.True);
            Assert.That(state.checkpoints[1].id, Is.EqualTo("gate-1"));
        }
    }
}
