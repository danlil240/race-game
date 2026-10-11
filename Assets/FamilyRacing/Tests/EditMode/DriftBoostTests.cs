using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // Drift/boost/air rules contract (Task 3). The charge meter is
    // normalized [0,1]: drifting and clean landings fill it, boosting
    // drains it under a bounded hold/release model. One airborne episode
    // earns at most one landing credit; recovery-flagged movement and
    // ground-contact jitter earn nothing. Boost-speed or backwards gate
    // crossings must not tunnel or reorder progress. Everything here is
    // Unity-free so the same tests run in the dotnet Core harness.
    public sealed class DriftBoostTests
    {
        private const float Dt = 1f / 30f;

        // A flat ground plane plus oriented half-space walls (same model
        // as MotorTests). Extent bounds the ground; Jitter makes the probe
        // alternate hit/miss to simulate contact flicker.
        private sealed class TestWorld : IMotorWorld
        {
            internal float GroundY = 0f;
            internal float Extent = 500f;
            internal bool Jitter;
            private int probeCalls;

            internal struct Wall
            {
                internal float nx, nz, d;
            }

            internal readonly List<Wall> Walls = new List<Wall>();

            public GroundHit ProbeGround(MotorPose pose, float probeDistance)
            {
                probeCalls++;
                var inside = Math.Abs(pose.x) <= Extent && Math.Abs(pose.z) <= Extent;
                var hit = inside && pose.y - GroundY <= probeDistance;
                if (Jitter && probeCalls % 2 == 1) hit = false;
                return new GroundHit
                {
                    hit = hit,
                    x = pose.x,
                    y = GroundY,
                    z = pose.z,
                    normalY = 1f,
                    distance = pose.y - GroundY,
                    surfaceId = "ground"
                };
            }

            public SweepHit SweepBody(MotorPose pose, float dx, float dy, float dz,
                float halfWidth, float halfLength, float height)
            {
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
                        normalY = 0f,
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

        private static DriverInput Neutral()
        {
            return new DriverInput(0f, false, false, false, false, false, false);
        }

        private static DriverInput Input(float steering, bool drift, bool boost)
        {
            return new DriverInput(steering, drift, boost, false, false, false, false);
        }

        private static MotorState Spawn()
        {
            return new MotorState { grounded = true };
        }

        private static MotorState StepN(MotorState state, DriverInput input,
            VehicleTuning tuning, IMotorWorld world, int steps)
        {
            for (var i = 0; i < steps; i++)
            {
                var result = ArcadeMotor.Step(state, input, tuning, world, Dt);
                Assert.That(result.rejected, Is.False, "step unexpectedly rejected");
                state = result.state;
            }
            return state;
        }

        private static bool HasEvent(MotorStepResult result, string kind)
        {
            foreach (var ev in result.events)
                if (ev.kind == kind) return true;
            return false;
        }

        private static int CountEvent(List<DrivingEvent> events, string kind)
        {
            var count = 0;
            foreach (var ev in events)
                if (ev.kind == kind) count++;
            return count;
        }

        // Reach a steady cruise speed on flat ground.
        private static MotorState Cruising(VehicleTuning tuning, IMotorWorld world)
        {
            return StepN(Spawn(), Neutral(), tuning, world, 240);
        }

        // --- drift eligibility ------------------------------------------------

        [Test]
        public void DriftRequiresMinimumSpeed()
        {
            var world = new TestWorld();
            var tuning = VehicleTuning.Car01();
            // Hold drift + steering from a standstill: no drift while the
            // car is below driftMinSpeed.
            var state = Spawn();
            for (var i = 0; i < 30; i++)
            {
                var result = ArcadeMotor.Step(state, Input(1f, true, false), tuning, world, Dt);
                state = result.state;
                if (state.speed < tuning.driftMinSpeed)
                    Assert.That(state.drifting, Is.False,
                        "drift must not engage below the tuning minimum speed");
            }
            state = StepN(state, Input(1f, true, false), tuning, world, 240);
            Assert.That(state.drifting, Is.True,
                "drift must engage once speed, steering and ground qualify");
            Assert.That(state.driftSign, Is.EqualTo(1));
        }

        [Test]
        public void DriftRequiresSteeringAndGround()
        {
            var world = new TestWorld();
            var tuning = VehicleTuning.Car01();
            var state = Cruising(tuning, world);
            // Drift held but steering neutral: no engagement.
            state = StepN(state, Input(0f, true, false), tuning, world, 30);
            Assert.That(state.drifting, Is.False,
                "drift requires a real steering input, not just the drift button");
            // Airborne drift is impossible: a falling car cannot start a drift.
            var air = state.Copy();
            air.grounded = false;
            air.verticalSpeed = -1f;
            air.y = 3f;
            air = StepN(air, Input(1f, true, false), tuning, world, 5);
            Assert.That(air.drifting, Is.False, "an airborne car must not drift");
        }

        [Test]
        public void DriftStartAndEndEmitOrderedEvents()
        {
            var world = new TestWorld();
            var tuning = VehicleTuning.Car01();
            var state = Cruising(tuning, world);
            var result = ArcadeMotor.Step(state, Input(1f, true, false), tuning, world, Dt);
            Assert.That(HasEvent(result, "driftStart"), Is.True);
            state = StepN(result.state, Input(1f, true, false), tuning, world, 10);
            result = ArcadeMotor.Step(state, Neutral(), tuning, world, Dt);
            Assert.That(HasEvent(result, "driftEnd"), Is.True);
            Assert.That(result.state.drifting, Is.False);
        }

        // --- charge model -----------------------------------------------------

        [Test]
        public void DriftChargesBoostAndCapsAtOne()
        {
            var world = new TestWorld();
            var tuning = VehicleTuning.Car01();
            var state = Cruising(tuning, world);
            state.boostCharge = 0.2f;
            var before = state.boostCharge;
            state = StepN(state, Input(1f, true, false), tuning, world, 60);
            Assert.That(state.drifting, Is.True);
            Assert.That(state.boostCharge, Is.GreaterThan(before + 0.1f),
                "holding a real drift must charge the boost meter");
            // Long drift can never push the normalized charge past 1.
            state = StepN(state, Input(1f, true, false), tuning, world, 600);
            Assert.That(state.boostCharge, Is.EqualTo(1f).Within(0.001f),
                "charge is normalized to [0,1] and must cap at full");
        }

        [Test]
        public void BoostRequiresPositiveCharge()
        {
            var world = new TestWorld();
            var tuning = VehicleTuning.Car01();
            var state = Cruising(tuning, world);
            state.boostCharge = 0f;
            var result = ArcadeMotor.Step(state, Input(0f, false, true), tuning, world, Dt);
            Assert.That(result.state.boostActive, Is.False,
                "an empty charge must not start a boost");
            Assert.That(HasEvent(result, "boostStart"), Is.False);
        }

        [Test]
        public void BoostActivatesOnPartialCharge()
        {
            var world = new TestWorld();
            var tuning = VehicleTuning.Car01();
            var state = Cruising(tuning, world);
            state.boostCharge = 0.5f;
            var result = ArcadeMotor.Step(state, Input(0f, false, true), tuning, world, Dt);
            Assert.That(result.state.boostActive, Is.True,
                "boost is a drain model: any charge above the minimum may fire");
            Assert.That(HasEvent(result, "boostStart"), Is.True);
        }

        [Test]
        public void BoostReleaseEndsEarlyAndKeepsRemainingCharge()
        {
            var world = new TestWorld();
            var tuning = VehicleTuning.Car01();
            var state = Cruising(tuning, world);
            state = StepN(state, Input(0f, false, true), tuning, world, 10);
            Assert.That(state.boostActive, Is.True);
            var result = ArcadeMotor.Step(state, Neutral(), tuning, world, Dt);
            Assert.That(result.state.boostActive, Is.False,
                "releasing the boost input must end the boost on that step");
            Assert.That(HasEvent(result, "boostEnd"), Is.True);
            Assert.That(result.state.boostCharge, Is.GreaterThan(0f)
                .And.LessThan(1f), "an early release keeps the unspent charge");
            // A fresh press may boost again with what remains; releasing
            // must have rearmed the one-shot boost latch.
            state = result.state;
            result = ArcadeMotor.Step(state, Input(0f, false, true), tuning, world, Dt);
            Assert.That(result.state.boostActive, Is.True,
                "a release-and-press may spend the remaining charge");
        }

        [Test]
        public void BoostEmptiesChargeAndStops()
        {
            var world = new TestWorld();
            var tuning = VehicleTuning.Car01();
            var state = Cruising(tuning, world);
            state.boostCharge = 0.15f;
            var ended = false;
            for (var i = 0; i < 300 && !ended; i++)
            {
                var result = ArcadeMotor.Step(state, Input(0f, false, true), tuning, world, Dt);
                state = result.state;
                if (HasEvent(result, "boostEnd")) ended = true;
            }
            Assert.That(ended, Is.True, "a held boost must end when the charge empties");
            Assert.That(state.boostActive, Is.False);
            Assert.That(state.boostCharge, Is.EqualTo(0f).Within(0.001f));
        }

        [Test]
        public void BoostDurationIsBounded()
        {
            var world = new TestWorld();
            // Slow drain: the duration bound must end the boost long before
            // the charge could empty.
            var tuning = VehicleTuning.Car01();
            tuning.boostDrainPerSecond = 0.02f;
            tuning.boostDurationSeconds = 0.3f;
            var state = Cruising(tuning, world);
            var steps = 0;
            var ended = false;
            for (var i = 0; i < 300 && !ended; i++)
            {
                var result = ArcadeMotor.Step(state, Input(0f, false, true), tuning, world, Dt);
                state = result.state;
                steps++;
                if (HasEvent(result, "boostEnd")) ended = true;
            }
            Assert.That(ended, Is.True);
            Assert.That(steps, Is.LessThanOrEqualTo((int)(tuning.boostDurationSeconds / Dt) + 2),
                "boost must not outlive its bounded duration even with charge left");
            Assert.That(state.boostCharge, Is.GreaterThan(0.5f),
                "the duration bound, not the drain, ended this boost");
        }

        [Test]
        public void HeldBoostCannotMachineGunAfterEmptying()
        {
            var world = new TestWorld();
            var tuning = VehicleTuning.Car01();
            var state = Cruising(tuning, world);
            state.boostCharge = 0.12f;
            var starts = 0;
            var lastEnded = -100;
            for (var i = 0; i < 300; i++)
            {
                var result = ArcadeMotor.Step(state, Input(0f, false, true), tuning, world, Dt);
                state = result.state;
                if (HasEvent(result, "boostStart"))
                {
                    starts++;
                    Assert.That(i - lastEnded, Is.GreaterThan(1),
                        "a held boost must not restart every tick after emptying");
                }
                if (HasEvent(result, "boostEnd")) lastEnded = i;
            }
            Assert.That(starts, Is.EqualTo(1),
                "one press spends the charge once; rearming needs a release");
        }

        [Test]
        public void BoostSpeedModifierChangesRealMotion()
        {
            var world = new TestWorld();
            var tuning = VehicleTuning.Car01();
            var state = Cruising(tuning, world);
            Assert.That(state.speed, Is.LessThanOrEqualTo(tuning.maxSpeed + 0.001f));
            state = StepN(state, Input(0f, false, true), tuning, world, 60);
            Assert.That(state.speed, Is.GreaterThan(tuning.maxSpeed + 0.5f),
                "the boost speed modifier must move the car faster than its base limit");
            Assert.That(state.speed, Is.LessThanOrEqualTo(tuning.boostMaxSpeed + 0.001f));
        }

        // --- landings ---------------------------------------------------------

        [Test]
        public void CleanLandingGrantsExactlyOneCredit()
        {
            var world = new TestWorld();
            var tuning = VehicleTuning.Car01();
            tuning.boostChargeSeconds = 1000000f; // isolate the credit from passive recharge
            var state = new MotorState
            {
                y = 4f,
                grounded = false,
                speed = 10f,
                verticalSpeed = -1f,
                airSeconds = 0.5f,
                boostCharge = 0.4f
            };
            var credits = 0;
            var landed = false;
            var before = state.boostCharge;
            for (var i = 0; i < 300 && !landed; i++)
            {
                var result = ArcadeMotor.Step(state, Neutral(), tuning, world, Dt);
                state = result.state;
                if (HasEvent(result, "cleanLanding")) credits++;
                if (HasEvent(result, "landed")) landed = true;
            }
            Assert.That(landed, Is.True);
            Assert.That(credits, Is.EqualTo(1),
                "an airborne episode earns at most one landing credit");
            Assert.That(state.boostCharge, Is.EqualTo(
                Math.Min(1f, before + tuning.cleanLandingCredit)).Within(0.02f),
                "the clean landing credits the authored charge amount");
        }

        [Test]
        public void ShortHopLandingGrantsNoCredit()
        {
            var world = new TestWorld();
            var tuning = VehicleTuning.Car01();
            var state = new MotorState
            {
                y = 0.05f,
                grounded = false,
                speed = 10f,
                verticalSpeed = -0.5f,
                airSeconds = 0.05f,
                boostCharge = 1f
            };
            var sawClean = false;
            var landed = false;
            for (var i = 0; i < 30 && !landed; i++)
            {
                var result = ArcadeMotor.Step(state, Neutral(), tuning, world, Dt);
                state = result.state;
                sawClean |= HasEvent(result, "cleanLanding");
                landed |= HasEvent(result, "landed");
            }
            Assert.That(landed, Is.True);
            Assert.That(sawClean, Is.False,
                "a hop shorter than the tuning minimum air time is not a jump");
        }

        [Test]
        public void WallContactDuringAirMakesLandingDirty()
        {
            var world = new TestWorld();
            world.Walls.Add(new TestWorld.Wall { nx = 0f, nz = 1f, d = 4f });
            var tuning = VehicleTuning.Car01();
            var state = new MotorState
            {
                y = 2.5f,
                grounded = false,
                speed = 12f,
                verticalSpeed = -0.5f,
                airSeconds = 0.4f,
                boostCharge = 0.4f
            };
            var sawClean = false;
            var landed = false;
            for (var i = 0; i < 300 && !landed; i++)
            {
                var result = ArcadeMotor.Step(state, Neutral(), tuning, world, Dt);
                state = result.state;
                sawClean |= HasEvent(result, "cleanLanding");
                landed |= HasEvent(result, "landed");
            }
            Assert.That(landed, Is.True);
            Assert.That(sawClean, Is.False,
                "clipping a barrier mid-flight invalidates the landing credit");
        }

        [Test]
        public void RecoveryFlaggedStepGrantsNoLandingCreditOrCharge()
        {
            var world = new TestWorld();
            var tuning = VehicleTuning.Car01();
            // The session flags the respawn boundary; the next committed
            // motor step still carries `recovering` and must be excluded
            // from charge gains and landing credit.
            var state = new MotorState
            {
                y = 0.4f,
                grounded = false,
                speed = 10f,
                verticalSpeed = -3f,
                airSeconds = 0.6f,
                boostCharge = 0.4f,
                recovering = true
            };
            var landed = false;
            for (var i = 0; i < 30 && !landed; i++)
            {
                var result = ArcadeMotor.Step(state, Neutral(), tuning, world, Dt);
                state = result.state;
                Assert.That(HasEvent(result, "cleanLanding"), Is.False,
                    "recovery movement must never earn a landing credit");
                Assert.That(state.boostCharge, Is.EqualTo(0.4f).Within(0.001f),
                    "the recovery-flagged step must not grow the charge");
                landed = HasEvent(result, "landed");
            }
            Assert.That(landed, Is.True,
                "the flagged steps still resolve ground contact");
        }

        [Test]
        public void GroundContactJitterCannotFarmLandingCredit()
        {
            var world = new TestWorld { Jitter = true };
            var tuning = VehicleTuning.Car01();
            var state = Spawn();
            var credits = 0;
            for (var i = 0; i < 900; i++)
            {
                var result = ArcadeMotor.Step(state, Neutral(), tuning, world, Dt);
                state = result.state;
                if (HasEvent(result, "cleanLanding")) credits++;
                Assert.That(state.boostCharge, Is.LessThanOrEqualTo(1f + 0.001f),
                    "charge stays normalized through contact flicker");
            }
            Assert.That(credits, Is.EqualTo(0),
                "probe flicker must never mint clean-landing charge");
        }

        // --- racer nudge seam -------------------------------------------------

        private sealed class FakeNudges : IRacerContactSource
        {
            internal readonly List<RacerNudge> Nudges = new List<RacerNudge>();
            public List<RacerNudge> NudgesFor(MotorState self)
            {
                return Nudges;
            }
        }

        [Test]
        public void RacerNudgeSeamAppliesBoundedImpulse()
        {
            var world = new TestWorld();
            var tuning = VehicleTuning.Car01();
            var state = Cruising(tuning, world);
            var nudges = new FakeNudges();
            // +z is the facing direction at heading 0: a forward shove.
            nudges.Nudges.Add(new RacerNudge
            {
                impulseX = 0f,
                impulseZ = 30f,
                racerId = "racer-x"
            });
            var before = state.speed;
            var result = ArcadeMotor.Step(state, Neutral(), tuning, world, Dt, nudges);
            var delta = result.state.speed - before;
            Assert.That(delta, Is.GreaterThan(0f).And.LessThan(10f),
                "a nudge applies a bounded speed change, never the raw impulse");
            Assert.That(HasEvent(result, "racerNudge"), Is.True,
                "the nudge is an observable motor event");
        }

        [Test]
        public void NullNudgeSourceChangesNothing()
        {
            var world = new TestWorld();
            var tuning = VehicleTuning.Car01();
            var state = Cruising(tuning, world);
            var plain = ArcadeMotor.Step(state, Neutral(), tuning, world, Dt);
            Assert.That(HasEvent(plain, "racerNudge"), Is.False);
        }

        // --- session-level recovery and progress ------------------------------

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
            VehicleTuning tuning = null)
        {
            var evt = new EventDefinition
            {
                id = "e",
                courseId = course.id,
                laps = 1,
                countdownTicks = 0,
                vehicleIds = new List<string> { (tuning ?? VehicleTuning.Car01()).vehicleId },
                recovery = new RecoverySettings()
            };
            return new DrivingSession("race-1", tuning ?? VehicleTuning.Car01(),
                course, evt, "v", "start-cmd");
        }

        private static List<DrivingEvent> StepSession(DrivingSession session,
            DriverInput input, IMotorWorld world, int steps)
        {
            var events = new List<DrivingEvent>();
            for (var i = 0; i < steps; i++)
                events.AddRange(session.Step(input, world).events);
            return events;
        }

        [Test]
        public void RecoveryDuringDriftClearsDriftAndGrantsNothing()
        {
            var course = StraightCourse();
            var world = new TestWorld();
            var session = NewSession(course);
            var state = session.Snapshot();
            // Build speed through the session, then hold drift.
            StepSession(session, Neutral(), world, 240);
            var drifted = StepSession(session, Input(1f, true, false), world, 30);
            Assert.That(session.Snapshot().motor.drifting, Is.True,
                "setup: a held drift must be engaged before recovery");
            var chargeBefore = session.Snapshot().motor.boostCharge;

            session.QueuePending("recover", "cmd-rec", out _);
            var step = session.Step(Neutral(), world);
            Assert.That(CountEvent(step.events, "race.recovered"), Is.EqualTo(1));
            var motor = session.Snapshot().motor;
            Assert.That(motor.drifting, Is.False);
            Assert.That(motor.recovering, Is.True);
            Assert.That(motor.boostCharge, Is.EqualTo(chargeBefore).Within(0.001f),
                "recovery preserves charge; it must not create it");
            Assert.That(CountEvent(step.events, "motor.cleanLanding"), Is.EqualTo(0));
            Assert.That(CountEvent(step.events, "checkpoint.crossed"), Is.EqualTo(0));
        }

        [Test]
        public void RecoveryDuringBoostPreservesRemainingCharge()
        {
            var course = StraightCourse();
            var world = new TestWorld();
            var session = NewSession(course);
            StepSession(session, Neutral(), world, 240);
            StepSession(session, Input(0f, false, true), world, 20);
            var boosting = session.Snapshot().motor;
            Assert.That(boosting.boostActive, Is.True, "setup: boost must be engaged");
            var chargeLeft = boosting.boostCharge;
            Assert.That(chargeLeft, Is.LessThan(1f).And.GreaterThan(0f));

            session.QueuePending("recover", "cmd-rec", out _);
            session.Step(Neutral(), world);
            var motor = session.Snapshot().motor;
            Assert.That(motor.boostActive, Is.False);
            Assert.That(motor.boostCharge, Is.EqualTo(chargeLeft).Within(0.001f),
                "recovery keeps the unspent charge; it never refills it");
        }

        [Test]
        public void RecoveryWhileAirborneRespawnsWithoutLandingCredit()
        {
            var course = StraightCourse();
            var world = new TestWorld { Extent = 60f };
            var session = NewSession(course);
            // Drive past gate-1's safe pose then off the bounded ground.
            var events = StepSession(session, Neutral(), world, 600);
            var sawAir = false;
            var motor = session.Snapshot().motor;
            for (var t = 0; t < 900 && !sawAir; t++)
            {
                session.Step(Neutral(), world);
                motor = session.Snapshot().motor;
                sawAir = !motor.grounded;
            }
            Assert.That(sawAir, Is.True, "setup: the car must be airborne off the edge");

            session.QueuePending("recover", "cmd-rec", out _);
            var step = session.Step(Neutral(), world);
            Assert.That(CountEvent(step.events, "race.recovered"), Is.EqualTo(1));
            Assert.That(CountEvent(step.events, "motor.landed"), Is.EqualTo(0),
                "a respawn is not a landing");
            Assert.That(CountEvent(step.events, "motor.cleanLanding"), Is.EqualTo(0));
            var after = session.Snapshot().motor;
            Assert.That(after.grounded, Is.True);
            Assert.That(after.recovering, Is.True);
        }

        [Test]
        public void FlaggedRecoveryStepCannotCrossGates()
        {
            // gate-1's safe pose (z=9.5) sits less than a step behind
            // gate-2's plane (z=10.2): the first committed step after the
            // respawn physically sweeps the plane while still flagged.
            var course = new CourseDefinition
            {
                id = "flag-test",
                spawnX = 0f,
                spawnY = 0f,
                spawnZ = -10f,
                spawnHeading = 0f,
                checkpoints = new List<CheckpointGate>
                {
                    new CheckpointGate
                    {
                        id = "start", x = 0f, z = -10f, dirX = 0f, dirZ = 1f,
                        halfWidth = 8f, recoverX = 0f, recoverZ = -13f
                    },
                    new CheckpointGate
                    {
                        id = "g1", x = 0f, z = 10f, dirX = 0f, dirZ = 1f,
                        halfWidth = 8f, recoverX = 0f, recoverZ = 9.5f
                    },
                    new CheckpointGate
                    {
                        id = "g2", x = 0f, z = 10.2f, dirX = 0f, dirZ = 1f,
                        halfWidth = 8f, recoverX = 0f, recoverZ = 9.9f
                    }
                }
            };
            var world = new TestWorld();
            var session = NewSession(course);
            var crossed = false;
            for (var t = 0; t < 1500 && !crossed; t++)
            {
                session.Step(Neutral(), world);
                crossed = session.Snapshot().nextCheckpoint == 2;
            }
            Assert.That(crossed, Is.True, "setup: gate g1 must be accepted");

            session.QueuePending("recover", "cmd-rec", out _);
            session.Step(Neutral(), world);
            Assert.That(session.Snapshot().motor.recovering, Is.True);

            // The flagged boundary evaluates no progress; evaluation
            // resumes unharmed afterwards — the next real crossing of g2
            // is awarded exactly once, not banked early or double-counted.
            var flagged = session.Step(Neutral(), world);
            Assert.That(CountEvent(flagged.events, "checkpoint.crossed"), Is.EqualTo(0),
                "a recovery-flagged boundary must never award a gate");
            Assert.That(session.Snapshot().nextCheckpoint, Is.EqualTo(2));
            var after = StepSession(session, Neutral(), world, 240);
            Assert.That(CountEvent(after, "checkpoint.crossed"), Is.EqualTo(1),
                "the genuine g2 crossing after the flag is awarded once");
            Assert.That(session.Snapshot().nextCheckpoint, Is.EqualTo(0));
        }

        [Test]
        public void BoostSpeedCrossingsStayOrderedWithoutTunneling()
        {
            // Gates packed 0.6m apart: a boosted car crosses several
            // planes inside single steps. Progress must accept them in
            // travel order — never skipping the expected gate.
            var course = new CourseDefinition
            {
                id = "dense-test",
                spawnX = 0f,
                spawnY = 0f,
                spawnZ = -10f,
                spawnHeading = 0f,
                checkpoints = new List<CheckpointGate>
                {
                    Gate("start", -10f), Gate("g1", 2.0f), Gate("g2", 2.6f),
                    Gate("g3", 3.2f), Gate("g4", 3.8f), Gate("g5", 30f)
                }
            };
            var world = new TestWorld();
            var session = NewSession(course);
            var events = StepSession(session, Input(0f, false, true), world, 300);
            var expected = new[] { "g1", "g2", "g3", "g4", "g5" };
            var got = new List<string>();
            foreach (var ev in events)
                if (ev.kind == "checkpoint.crossed") got.Add(ev.detail);
            Assert.That(got, Is.EqualTo(expected),
                "every gate must be accepted exactly once in authored order");
        }

        private static CheckpointGate Gate(string id, float z)
        {
            return new CheckpointGate
            {
                id = id,
                x = 0f,
                z = z,
                dirX = 0f,
                dirZ = 1f,
                halfWidth = 8f,
                recoverX = 0f,
                recoverZ = z - 3f,
                recoverHeading = 0f
            };
        }

        [Test]
        public void BackwardsCrossingsGrantNoProgress()
        {
            var course = new CourseDefinition
            {
                id = "dense-test",
                spawnX = 0f,
                spawnY = 0f,
                spawnZ = -10f,
                spawnHeading = 0f,
                checkpoints = new List<CheckpointGate>
                {
                    Gate("start", -10f), Gate("g1", 2.0f), Gate("g2", 2.6f),
                    Gate("g3", 3.2f), Gate("g4", 3.8f), Gate("g5", 30f)
                }
            };
            var world = new TestWorld();
            var session = NewSession(course);
            StepSession(session, Neutral(), world, 300); // through g1..g5
            var before = session.Snapshot().nextCheckpoint;
            // Reverse: steer back toward the gates and re-cross their
            // planes against the authored direction. Stop before the car
            // could loop around for a legitimate forward crossing.
            var events = new List<DrivingEvent>();
            for (var i = 0; i < 400; i++)
            {
                var motor = session.Snapshot().motor;
                if (motor.z < -4f) break; // past g1, still well before the line
                var desired = (float)Math.Atan2(0f - motor.x, -6f - motor.z);
                var steer = Math.Clamp(WrapAngle(desired - motor.heading) * 3f, -1f, 1f);
                events.AddRange(session.Step(Input(steer, false, false), world).events);
            }
            Assert.That(CountEvent(events, "checkpoint.crossed"), Is.EqualTo(0),
                "re-crossing planes backwards must not recount progress");
            Assert.That(session.Snapshot().nextCheckpoint, Is.EqualTo(before));
        }

        private static float WrapAngle(float radians)
        {
            while (radians > Math.PI) radians -= 2f * (float)Math.PI;
            while (radians <= -Math.PI) radians += 2f * (float)Math.PI;
            return radians;
        }

        // --- invariants ---------------------------------------------------------

        [Test]
        public void MotorStateStaysFiniteAndBoundedUnderChaos()
        {
            var world = new TestWorld();
            world.Walls.Add(new TestWorld.Wall { nx = 1f, nz = 0f, d = 40f });
            world.Walls.Add(new TestWorld.Wall { nx = -1f, nz = 0f, d = 40f });
            world.Walls.Add(new TestWorld.Wall { nx = 0f, nz = 1f, d = 40f });
            world.Walls.Add(new TestWorld.Wall { nx = 0f, nz = -1f, d = 40f });
            var tuning = VehicleTuning.Motorcycle01();
            var random = new Random(20261011);
            var state = Spawn();
            for (var i = 0; i < 3000; i++)
            {
                var input = Input(
                    (float)(random.NextDouble() * 2.0 - 1.0),
                    random.NextDouble() < 0.4,
                    random.NextDouble() < 0.3);
                var result = ArcadeMotor.Step(state, input, tuning, world, Dt);
                Assert.That(result.rejected, Is.False);
                state = result.state;
                AssertFinite(state.x, "x");
                AssertFinite(state.y, "y");
                AssertFinite(state.z, "z");
                AssertFinite(state.heading, "heading");
                AssertFinite(state.speed, "speed");
                AssertFinite(state.lateralSpeed, "lateralSpeed");
                AssertFinite(state.verticalSpeed, "verticalSpeed");
                Assert.That(Math.Abs(state.x), Is.LessThan(45f), "walls bound x");
                Assert.That(Math.Abs(state.z), Is.LessThan(45f), "walls bound z");
                Assert.That(state.boostCharge, Is.GreaterThanOrEqualTo(0f)
                    .And.LessThanOrEqualTo(1f), "charge stays normalized");
                Assert.That(Math.Abs(state.speed), Is.LessThanOrEqualTo(
                    tuning.boostMaxSpeed + 10f), "speed stays bounded");
            }
        }

        private static void AssertFinite(float value, string name)
        {
            Assert.That(float.IsNaN(value) || float.IsInfinity(value), Is.False,
                name + " must stay finite");
        }

        // --- tuning source reconciliation -------------------------------------

        // The generated assets are the runtime authority; the Core presets
        // are the authored source the setup writes. This test reads the
        // committed YAML so both sources are proven identical — no Unity
        // dependency needed.
        [TestCase("car-01", "car")]
        [TestCase("motorcycle-01", "motorcycle")]
        public void GeneratedVehicleAssetMatchesCorePreset(string vehicleId, string kind)
        {
            var path = FindGeneratedAsset(vehicleId + ".asset");
            var fields = ParseScalars(path);
            var tuning = kind == "car"
                ? VehicleTuning.Car01()
                : VehicleTuning.Motorcycle01();
            AssertField(fields, tuning, "vehicleId", tuning.vehicleId);
            AssertField(fields, tuning, "maxSpeed", tuning.maxSpeed);
            AssertField(fields, tuning, "acceleration", tuning.acceleration);
            AssertField(fields, tuning, "steeringRate", tuning.steeringRate);
            AssertField(fields, tuning, "steeringReferenceSpeed", tuning.steeringReferenceSpeed);
            AssertField(fields, tuning, "grip", tuning.grip);
            AssertField(fields, tuning, "driftMinSpeed", tuning.driftMinSpeed);
            AssertField(fields, tuning, "driftSteeringFactor", tuning.driftSteeringFactor);
            AssertField(fields, tuning, "driftLateralAcceleration", tuning.driftLateralAcceleration);
            AssertField(fields, tuning, "driftChargePerSecond", tuning.driftChargePerSecond);
            AssertField(fields, tuning, "boostMaxSpeed", tuning.boostMaxSpeed);
            AssertField(fields, tuning, "boostAcceleration", tuning.boostAcceleration);
            AssertField(fields, tuning, "boostDurationSeconds", tuning.boostDurationSeconds);
            AssertField(fields, tuning, "boostDrainPerSecond", tuning.boostDrainPerSecond);
            AssertField(fields, tuning, "boostMinCharge", tuning.boostMinCharge);
            AssertField(fields, tuning, "boostChargeSeconds", tuning.boostChargeSeconds);
            AssertField(fields, tuning, "cleanLandingCredit", tuning.cleanLandingCredit);
            AssertField(fields, tuning, "landingMinAirSeconds", tuning.landingMinAirSeconds);
            AssertField(fields, tuning, "gravity", tuning.gravity);
            AssertField(fields, tuning, "terminalFallSpeed", tuning.terminalFallSpeed);
            AssertField(fields, tuning, "groundProbe", tuning.groundProbe);
            AssertField(fields, tuning, "rideHeight", tuning.rideHeight);
            AssertField(fields, tuning, "halfWidth", tuning.halfWidth);
            AssertField(fields, tuning, "halfLength", tuning.halfLength);
            AssertField(fields, tuning, "height", tuning.height);
            AssertField(fields, tuning, "wallSpeedRetention", tuning.wallSpeedRetention);
        }

        private static void AssertField(Dictionary<string, string> fields,
            VehicleTuning tuning, string name, float expected)
        {
            string raw;
            Assert.That(fields.TryGetValue(name, out raw), Is.True,
                "generated asset is missing tuning field " + name);
            var value = float.Parse(raw,
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.That(value, Is.EqualTo(expected).Within(0.0001f),
                "asset field " + name + " disagrees with the Core preset");
        }

        private static void AssertField(Dictionary<string, string> fields,
            VehicleTuning tuning, string name, string expected)
        {
            string raw;
            Assert.That(fields.TryGetValue(name, out raw), Is.True,
                "generated asset is missing " + name);
            Assert.That(raw, Is.EqualTo(expected));
        }

        private static Dictionary<string, string> ParseScalars(string path)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                var colon = trimmed.IndexOf(':');
                if (colon <= 0) continue;
                var key = trimmed.Substring(0, colon).Trim();
                var value = trimmed.Substring(colon + 1).Trim();
                if (!fields.ContainsKey(key)) fields[key] = value;
            }
            return fields;
        }

        private static string FindGeneratedAsset(string file, [CallerFilePath] string from = "")
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(from));
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName,
                    "Assets", "FamilyRacing", "Generated", "Driving", file);
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }
            Assert.Fail("generated asset not found from " + from);
            return null;
        }

        // --- vehicle class parity ----------------------------------------------

        [Test]
        public void VehicleTuningsStayValidAndDistinct()
        {
            var car = VehicleTuning.Car01();
            var moto = VehicleTuning.Motorcycle01();
            string error;
            Assert.That(car.Validate(out error), Is.True, error);
            Assert.That(moto.Validate(out error), Is.True, error);
            Assert.That(moto.steeringRate, Is.GreaterThan(car.steeringRate));
            Assert.That(moto.halfWidth, Is.LessThan(car.halfWidth),
                "the motorcycle collision body is visibly narrower");
            Assert.That(moto.driftChargePerSecond, Is.Not.EqualTo(car.driftChargePerSecond),
                "the classes must differ in the charge economy too");
        }
    }
}
