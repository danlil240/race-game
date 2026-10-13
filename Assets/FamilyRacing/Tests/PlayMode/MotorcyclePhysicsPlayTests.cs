using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
using UnityEngine;

namespace FamilyRacing.Tests
{
    // Motorcycle qualification for the physical backend: two inline
    // raycast contacts on a combined bike/rider body plus the bounded
    // PD lean/balance torque controller. Every assertion reads the real
    // rigidbody attitude read back into MotorState.physics — a cosmetic
    // lean or a locked rotation constraint cannot satisfy these tests
    // (the balance test additionally pins RigidbodyConstraints.None).
    public sealed class MotorcyclePhysicsPlayTests
    {
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
            physicsTuning = null;
        }

        private PhysicsVehicleSimulation NewBike(string name,
            VehiclePhysicsTuning physics, MotorState initialState,
            string participantId = "p1")
        {
            world = PhysicsCourseWorld.Create(name);
            sim = new PhysicsVehicleSimulation(world);
            sim.Initialize(new List<VehicleSpawn>
            {
                new VehicleSpawn
                {
                    participantId = participantId,
                    tuning = PhysicsTestWorld.PhysicalTuning(
                        "motorcycle-01", physics),
                    initialState = initialState
                }
            });
            return sim;
        }

        private void Step(DriverInput input, int ticks)
        {
            for (var i = 0; i < ticks; i++)
                sim.Step(new List<VehicleTickRequest>
                { PhysicsTestWorld.Request("p1", input) });
        }

        // Signed lean (roll) angle of the body against the world-up
        // support frame: positive is a lean to the vehicle's right —
        // the same convention MotorcycleBalanceController measures.
        private static float SignedLean(MotorState state)
        {
            var p = state.physics;
            var q = new Quaternion(p.attitudeX, p.attitudeY,
                p.attitudeZ, p.attitudeW);
            var up = q * Vector3.up;
            var fwd = q * Vector3.forward;
            var flatForward = Vector3.ProjectOnPlane(fwd, Vector3.up);
            if (flatForward.sqrMagnitude < 1e-6f) return 0f;
            flatForward.Normalize();
            var right = Vector3.Cross(Vector3.up, flatForward);
            return Mathf.Atan2(Vector3.Dot(up, right),
                Vector3.Dot(up, Vector3.up));
        }

        // Attitude rolled `leanRadians` to the right about the forward
        // axis, written straight into the state's attitude quaternion.
        private static void SetLean(VehiclePhysicsState physics,
            float leanRadians)
        {
            var half = -leanRadians * 0.5f;
            physics.TrySetAttitude(0f, 0f,
                Mathf.Sin(half), Mathf.Cos(half));
        }

        // --- rest and low-speed balance -------------------------------

        [Test]
        [Timeout(120000)]
        public void BikeBalancesAtRestAndLowSpeed()
        {
            var physics = VehiclePhysicsTuning.Motorcycle01();
            physicsTuning = physics;
            var spawn = PhysicsTestWorld.PhysicalSpawnState(physics,
                new Vector3(0f, PhysicsTestWorld.SpawnY(physics), 0f), 0f);
            // A deliberate 4-degree initial lean: a symmetric perfectly-
            // upright rigidbody could pass on numerical luck alone, so
            // the bike starts visibly off-balance and the controller
            // must actively pull it back. Two inline contacts supply no
            // roll support torque — only real assist torque can save it.
            SetLean(spawn.physics, 4f * Mathf.Deg2Rad);
            NewBike("bike-rest", physics, spawn);
            PhysicsTestWorld.FlatGround(world, 0f, 0f, 200f, 200f);
            Assert.That(sim.DebugRigidbody("p1").constraints,
                Is.EqualTo(RigidbodyConstraints.None),
                "rotation is never frozen: the balance must come from "
                + "torque, not a locked constraint");

            var maxLean = 0f;
            var worstUngrounded = 0;
            for (var i = 0; i < 300; i++) // 10 s stationary
            {
                // An explicit throttle-zero coast: the bike is parked,
                // not driving anywhere.
                sim.Step(new List<VehicleTickRequest>
                {
                    PhysicsTestWorld.Request("p1",
                        PhysicsTestWorld.Drive(throttleOverride: 0f))
                });
                var state = sim.Capture("p1");
                var lean = Mathf.Abs(SignedLean(state));
                if (lean > maxLean) maxLean = lean;
                if (!state.grounded) worstUngrounded++;
                Assert.That(lean, Is.LessThan(10f * Mathf.Deg2Rad),
                    "a supported stationary bike must stay within 10 "
                    + "degrees for 10 s (lean " + (lean * Mathf.Rad2Deg)
                    + " deg at tick " + i + ")");
            }
            var final = sim.Capture("p1");
            Assert.That(worstUngrounded, Is.EqualTo(0),
                "the parked bike keeps wheel support the whole window");
            Assert.That(Mathf.Abs(SignedLean(final)),
                Is.LessThan(2f * Mathf.Deg2Rad),
                "the assist converges toward upright, it does not merely "
                + "arrest the fall (final lean "
                + (SignedLean(final) * Mathf.Rad2Deg) + " deg)");
            Assert.That(maxLean, Is.LessThan(8f * Mathf.Deg2Rad),
                "recovery from the seeded 4-degree lean must not "
                + "overshoot past 8 degrees (peak "
                + (maxLean * Mathf.Rad2Deg) + " deg)");

            // Low-speed straight-line riding: gentle throttle for 3 s —
            // the upright target still holds inside 10 degrees.
            for (var i = 0; i < 90; i++)
            {
                sim.Step(new List<VehicleTickRequest>
                {
                    PhysicsTestWorld.Request("p1",
                        PhysicsTestWorld.Drive(throttleOverride: 0.3f))
                });
                var state = sim.Capture("p1");
                Assert.That(Mathf.Abs(SignedLean(state)),
                    Is.LessThan(10f * Mathf.Deg2Rad),
                    "low-speed riding stays inside 10 degrees (tick "
                    + i + ", lean "
                    + (SignedLean(state) * Mathf.Rad2Deg) + " deg)");
            }
            var rolling = sim.Capture("p1");
            Assert.That(PhysicsTestWorld.ForwardSpeed(rolling),
                Is.GreaterThan(1f),
                "setup: the low-speed phase really moved (speed "
                + PhysicsTestWorld.ForwardSpeed(rolling) + ")");
        }

        // --- lean into both turn directions -----------------------------

        [Test]
        [Timeout(120000)]
        public void BikeLeansIntoBothTurnDirections()
        {
            var physics = VehiclePhysicsTuning.Motorcycle01();
            physicsTuning = physics;
            var spawn = PhysicsTestWorld.PhysicalSpawnState(physics,
                new Vector3(0f, PhysicsTestWorld.SpawnY(physics), 0f), 0f,
                forwardSpeed: 14f);
            NewBike("bike-lean", physics, spawn);
            PhysicsTestWorld.FlatGround(world, 0f, 0f, 800f, 800f);
            Step(PhysicsTestWorld.Drive(), 30); // settle at speed

            // Headings wrap at +-pi, so accumulate the unwrapped delta —
            // a fast circling bike crosses the seam many times. MotorState
            // .heading is already radians (Atan2 convention), so no unit
            // conversion is applied.
            var maxLean = 0f;
            var rightLean = 0f;
            var prevHeading = sim.Capture("p1").heading;
            var turnRight = 0f;
            for (var i = 0; i < 120; i++)
            {
                Step(PhysicsTestWorld.Drive(steering: 1f), 1);
                var state = sim.Capture("p1");
                var lean = SignedLean(state);
                if (Mathf.Abs(lean) > maxLean) maxLean = Mathf.Abs(lean);
                // Turn is measured over the committed cornering window
                // around the lean sample: holding full lock long enough
                // scrubs the bike to a near-standstill spin whose
                // residual rotation is chaotic and must not flip the
                // "leaned INTO the turn" oracle.
                if (i >= 30 && i <= 50)
                    turnRight += Mathf.DeltaAngle(prevHeading,
                        state.heading);
                prevHeading = state.heading;
                if (i == 40) rightLean = lean;
            }
            Assert.That(Mathf.Abs(turnRight), Is.GreaterThan(0.5f),
                "setup: full-lock steering must really turn the bike "
                + "(net turn " + turnRight + " rad)");
            Assert.That(Mathf.Abs(rightLean),
                Is.GreaterThan(3f * Mathf.Deg2Rad),
                "a real lean develops in a sustained turn (lean "
                + (rightLean * Mathf.Rad2Deg) + " deg)");
            Assert.That(rightLean * turnRight, Is.GreaterThan(0f),
                "the lean must be INTO the turn — lean "
                + (rightLean * Mathf.Rad2Deg) + " deg for turn "
                + turnRight + " rad");
            Assert.That(maxLean,
                Is.LessThanOrEqualTo(
                    physics.assistMaxLeanRadians + 0.05f),
                "lean never exceeds the authored 45-degree bound (peak "
                + (maxLean * Mathf.Rad2Deg) + " deg)");

            var leftLean = 0f;
            var turnLeft = 0f;
            for (var i = 0; i < 160; i++)
            {
                Step(PhysicsTestWorld.Drive(steering: -1f), 1);
                var state = sim.Capture("p1");
                var lean = SignedLean(state);
                if (Mathf.Abs(lean) > maxLean) maxLean = Mathf.Abs(lean);
                if (i >= 60 && i <= 80)
                    turnLeft += Mathf.DeltaAngle(prevHeading,
                        state.heading);
                prevHeading = state.heading;
                // Same committed-turn window, later — the reversal
                // needs ~a second to establish out of the right phase's
                // low-speed spin state.
                if (i == 70) leftLean = lean;
            }
            Assert.That(Mathf.Abs(turnLeft), Is.GreaterThan(0.5f),
                "setup: the reversal must really turn the bike the "
                + "other way (net turn " + turnLeft + " rad)");
            Assert.That(Mathf.Abs(leftLean),
                Is.GreaterThan(3f * Mathf.Deg2Rad),
                "a real lean develops in the reversed turn (lean "
                + (leftLean * Mathf.Rad2Deg) + " deg)");
            Assert.That(leftLean * turnLeft, Is.GreaterThan(0f),
                "the lean must be INTO the reversed turn — lean "
                + (leftLean * Mathf.Rad2Deg) + " deg for turn "
                + turnLeft + " rad");
            Assert.That(maxLean,
                Is.LessThanOrEqualTo(
                    physics.assistMaxLeanRadians + 0.05f),
                "lean never exceeds the authored 45-degree bound (peak "
                + (maxLean * Mathf.Rad2Deg) + " deg)");
            var final = sim.Capture("p1");
            Assert.That(final.grounded, Is.True,
                "the bike stays supported through both turn directions");
            Assert.That(PhysicsTestWorld.BodyUpDot(final),
                Is.GreaterThan(0.5f),
                "the bike never fell over while leaning");
        }

        // --- airborne: inertia + damping only ----------------------------

        [Test]
        [Timeout(120000)]
        public void AirborneBalanceDoesNotRightTheBike()
        {
            var physics = VehiclePhysicsTuning.Motorcycle01();
            physicsTuning = physics;
            // 35-degree lean at 8 m up with a small roll rate AWAY from
            // upright. Inertia + damping only: the tilt can never be
            // pulled back toward upright — a world-upright spring would
            // visibly reduce it; the bounded damper only bleeds the
            // roll rate.
            var spawn = PhysicsTestWorld.PhysicalSpawnState(physics,
                new Vector3(0f, 8f, 0f), 0f);
            SetLean(spawn.physics, 35f * Mathf.Deg2Rad);
            spawn.physics.angularVelocityZ = -1.5f; // rolling further over
            spawn.grounded = false;
            spawn.physics.grounded = false;
            NewBike("bike-air", physics, spawn);
            PhysicsTestWorld.FlatGround(world, 0f, 0f, 400f, 400f);

            var airTicks = 0;
            var maxUpDot = float.MinValue;
            var minUpDot = float.MaxValue;
            var startUpDot = 0f;
            var startRollRate = 0f;
            var endRollRate = 0f;
            for (var i = 0; i < 27; i++) // ~0.9 s — still falling
            {
                Step(PhysicsTestWorld.Drive(), 1);
                var state = sim.Capture("p1");
                if (state.grounded) break;
                airTicks++;
                var body = sim.DebugRigidbody("p1");
                var upDot = Vector3.Dot(body.transform.up, Vector3.up);
                var rollRate = Vector3.Dot(body.angularVelocity,
                    body.transform.forward);
                if (airTicks == 1)
                {
                    startUpDot = upDot;
                    startRollRate = Mathf.Abs(rollRate);
                }
                if (upDot > maxUpDot) maxUpDot = upDot;
                if (upDot < minUpDot) minUpDot = upDot;
                endRollRate = Mathf.Abs(rollRate);
            }
            Assert.That(airTicks, Is.GreaterThan(20),
                "setup: the bike must stay airborne the measurement "
                + "window (airTicks " + airTicks + ")");
            Assert.That(startRollRate, Is.GreaterThan(1f),
                "setup: a real initial roll rate is required to observe "
                + "damping (got " + startRollRate + ")");
            Assert.That(maxUpDot, Is.LessThan(startUpDot + 0.02f),
                "an airborne bike can never be rotated toward upright — "
                + "upDot peaked at " + maxUpDot + " from " + startUpDot);
            Assert.That(endRollRate, Is.LessThan(startRollRate * 0.5f),
                "the bounded damper bleeds roll rate in the air ("
                + startRollRate + " -> " + endRollRate + ")");
        }

        // --- a hard side impact still knocks the bike over ---------------

        [Test]
        [Timeout(120000)]
        public void HardSideImpactCanKnockBikeOver()
        {
            var physics = VehiclePhysicsTuning.Motorcycle01();
            physicsTuning = physics;
            var spawn = PhysicsTestWorld.PhysicalSpawnState(physics,
                new Vector3(0f, PhysicsTestWorld.SpawnY(physics), 0f), 0f);
            NewBike("bike-impact", physics, spawn);
            PhysicsTestWorld.FlatGround(world, 0f, 0f, 200f, 200f);
            // A long pre-impact window: a perfectly symmetric spawn tips
            // slowly, so a short settle would pass on luck alone. Four
            // seconds parked is long enough that an unassisted bike is
            // measurably falling — and the assisted one is not.
            Step(PhysicsTestWorld.Drive(throttleOverride: 0f), 120);
            Assert.That(PhysicsTestWorld.BodyUpDot(sim.Capture("p1")),
                Is.GreaterThan(0.9f),
                "setup: the bike must be upright BEFORE the hit — this "
                + "precondition is the balance-assist failure without "
                + "the controller");

            // A sustained shoulder-height lateral hit: 20 kN applied 0.6 m
            // above the body origin produces ~12 kN·m of roll torque —
            // far past the authored assistance cap (~4.7 kN·m). The
            // assist is bounded by construction so a hard hit wins.
            sim.DebugSetForceAtOffset("p1",
                new Vector3(-20000f, 0f, 0f), new Vector3(0f, 0.6f, 0f));
            var minUpDot = 1f;
            var fellAt = -1;
            for (var i = 0; i < 150; i++)
            {
                if (i == 15) sim.DebugClearForces("p1");
                Step(PhysicsTestWorld.Drive(throttleOverride: 0f), 1);
                var state = sim.Capture("p1");
                var dot = PhysicsTestWorld.BodyUpDot(state);
                if (dot < minUpDot) minUpDot = dot;
                if (dot < 0.3f)
                {
                    fellAt = i;
                    break;
                }
            }
            Assert.That(fellAt, Is.GreaterThanOrEqualTo(0),
                "a hard lateral impact must still knock the bike over — "
                + "the capped assist cannot hold it (minUpDot "
                + minUpDot + ")");
            Assert.That(sim.Capture("p1").physics.grounded, Is.True,
                "the fallen bike rests on the chassis/ground — ordinary "
                + "contact, ready for the shared recovery path");
        }

        // --- the fallen bike shares the ordinary recovery path -----------

        private static CourseDefinition Lane(float spawnZ = -10f)
        {
            return new CourseDefinition
            {
                id = "bike-lane",
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
                    }
                }
            };
        }

        private void NewBikeRace(CourseDefinition course)
        {
            physicsTuning = VehiclePhysicsTuning.Motorcycle01();
            var tuning = PhysicsTestWorld.PhysicalTuning("motorcycle-01",
                physicsTuning);
            world = PhysicsCourseWorld.Create("bike-recovery");
            sim = new PhysicsVehicleSimulation(world);
            var evt = new EventDefinition
            {
                id = "e",
                courseId = course.id,
                laps = 1,
                countdownTicks = 0,
                vehicleIds = new List<string> { "motorcycle-01" },
                recovery = new RecoverySettings()
            };
            session = new DrivingSession("race", tuning, course, evt,
                "v", "start-cmd", new MotorPose
                {
                    x = course.spawnX, y = 0f,
                    z = course.spawnZ, heading = 0f
                });
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

        [Test]
        [Timeout(120000)]
        public void FallenBikeRecoversOnce()
        {
            var course = Lane();
            NewBikeRace(course);
            // Precondition through the real session boundary: an
            // assisted bike stays upright while driven; without the
            // controller this is where the test fails first. The window
            // is long enough that a symmetric spawn still visibly tips.
            for (var i = 0; i < 120; i++)
                Tick(PhysicsTestWorld.Drive());
            Assert.That(
                PhysicsTestWorld.BodyUpDot(session.Snapshot().motor),
                Is.GreaterThan(0.7f),
                "setup: a session-driven bike must still be upright "
                + "before the forced fall");
            // Lying on its side: 100 degrees of roll, body resting at
            // chassis height. The wheel rays point sideways — support
            // comes only through the chassis contact, exactly like a
            // fallen real bike. Overturned support for 2 s recovers
            // through the same mechanism the car uses.
            var fallen = PhysicsTestWorld.PhysicalSpawnState(physicsTuning,
                new Vector3(0f, 0.35f, 0f), 0f);
            SetLean(fallen.physics, 100f * Mathf.Deg2Rad);
            fallen.grounded = false;
            fallen.physics.grounded = false;
            sim.Restore(session.ParticipantId, fallen);

            var totalRecoveries = 0;
            var earlyRecoveries = 0;
            var detail = "";
            for (var i = 0; i < 150; i++) // 5 s window
            {
                var step = Tick(PhysicsTestWorld.Drive());
                foreach (var ev in step.events)
                    if (ev.kind == "race.recovered")
                    {
                        totalRecoveries++;
                        if (i < 55) earlyRecoveries++;
                        detail = ev.detail;
                    }
            }
            Assert.That(earlyRecoveries, Is.EqualTo(0),
                "no recovery before 2 s of continuous overturned "
                + "support");
            Assert.That(totalRecoveries, Is.EqualTo(1),
                "exactly one recovery — the reset clears the overturned "
                + "clock (got " + totalRecoveries + ")");
            Assert.That(detail, Does.StartWith("overturned"),
                "a fallen bike uses the ordinary overturned recovery, "
                + "not a bike-specific path (detail " + detail + ")");
            var motor = session.Snapshot().motor;
            Assert.That(motor.physics.recoveryEpoch, Is.EqualTo(1L));
            Assert.That(PhysicsTestWorld.BodyUpDot(motor),
                Is.GreaterThan(0.8f),
                "the recovered bike is upright again");
            Assert.That(motor.physics.balancePrevError, Is.EqualTo(0f),
                "recovery clears the balance controller history");
            Assert.That(motor.physics.filteredRollRate, Is.EqualTo(0f));
        }

        // --- restore replays controller history ---------------------------

        [Test]
        [Timeout(120000)]
        public void BikeRestoreReplaysControllerHistory()
        {
            var physics = VehiclePhysicsTuning.Motorcycle01();
            physicsTuning = physics;
            var spawn = PhysicsTestWorld.PhysicalSpawnState(physics,
                new Vector3(0f, PhysicsTestWorld.SpawnY(physics), 0f), 0f,
                forwardSpeed: 14f);
            NewBike("bike-replay", physics, spawn);
            PhysicsTestWorld.FlatGround(world, 0f, 0f, 800f, 800f);
            // Lean the bike into a sustained right turn so the PD
            // controller's error history and the filtered roll rate are
            // both non-zero mid-transient.
            Step(PhysicsTestWorld.Drive(), 30);
            Step(PhysicsTestWorld.Drive(steering: 1f), 45);
            var saved = sim.Capture("p1");
            Assert.That(PhysicsTestWorld.BodyUpDot(saved),
                Is.GreaterThan(0.7f),
                "setup: the bike is leaned but upright at capture — "
                + "without balance assist it is already falling");
            Assert.That(Mathf.Abs(SignedLean(saved)),
                Is.GreaterThan(2f * Mathf.Deg2Rad),
                "setup: a real lean, not a cosmetic pose, is captured");
            Assert.That(
                Mathf.Abs(saved.physics.balancePrevError)
                    + Mathf.Abs(saved.physics.filteredRollRate),
                Is.GreaterThan(1e-4f),
                "setup: the capture must carry live controller history "
                + "(prevError " + saved.physics.balancePrevError
                + " rollRate " + saved.physics.filteredRollRate + ")");

            // Reference run: keep stepping from the live state.
            Step(PhysicsTestWorld.Drive(steering: 1f), 40);
            var reference = sim.Capture("p1");

            // Replay: restore the captured state and run the same inputs.
            sim.Restore("p1", saved);
            var restored = sim.Capture("p1");
            Assert.That(restored.physics.balancePrevError,
                Is.EqualTo(saved.physics.balancePrevError).Within(1e-6f),
                "restore must replay balancePrevError, not clear it");
            Assert.That(restored.physics.filteredRollRate,
                Is.EqualTo(saved.physics.filteredRollRate).Within(1e-6f),
                "restore must replay filteredRollRate, not clear it");
            Step(PhysicsTestWorld.Drive(steering: 1f), 40);
            var replayed = sim.Capture("p1");

            // Consistency bound: a faithful restore stays inside the
            // PhysX replay envelope — solver warm-start state that is
            // not part of MotorState means this is a bounded tolerance,
            // never a bit-exact promise. The bike is a marginally
            // stable plant, so its envelope is wider than the car's:
            // ~1.3 m was observed over 40 full-lock ticks. The real
            // teeth are the doctored-history oracles below.
            Assert.That(replayed.x,
                Is.EqualTo(reference.x).Within(2f),
                "replay diverged: x " + replayed.x + " vs " + reference.x);
            Assert.That(replayed.z,
                Is.EqualTo(reference.z).Within(2f),
                "replay diverged: z " + replayed.z + " vs " + reference.z);
            Assert.That(
                PhysicsTestWorld.BodyUpDot(replayed),
                Is.EqualTo(PhysicsTestWorld.BodyUpDot(reference))
                    .Within(0.1f),
                "the replayed attitude stays with the reference");

            // Functional oracle: the persisted fields must feed the
            // torque, not just round-trip. Two restores identical but
            // for balancePrevError produce measurably different roll
            // rates after one tick — a derivative kick is the only
            // difference between them.
            var doctored = saved.Copy();
            doctored.physics.balancePrevError += 1f;
            sim.Restore("p1", saved);
            Step(PhysicsTestWorld.Drive(steering: 1f), 1);
            var rateFaithful = RollRate(sim.Capture("p1"));
            sim.Restore("p1", doctored);
            Step(PhysicsTestWorld.Drive(steering: 1f), 1);
            var rateDoctored = RollRate(sim.Capture("p1"));
            Assert.That(Mathf.Abs(rateDoctored - rateFaithful),
                Is.GreaterThan(0.05f),
                "balancePrevError must feed the PD torque — a doctored "
                + "restore changes the roll response (faithful "
                + rateFaithful + " vs doctored " + rateDoctored + ")");

            // The airborne branch consumes filteredRollRate: two
            // restores identical but for that field damp differently.
            var air = PhysicsTestWorld.PhysicalSpawnState(physics,
                new Vector3(0f, 8f, 0f), 0f);
            SetLean(air.physics, 20f * Mathf.Deg2Rad);
            air.physics.angularVelocityZ = -2f;
            air.grounded = false;
            air.physics.grounded = false;
            air.physics.filteredRollRate = -2f;
            var airDoctored = air.Copy();
            airDoctored.physics.filteredRollRate = 0f;
            sim.Restore("p1", air);
            Step(PhysicsTestWorld.Drive(), 1);
            var rateDamped = RollRate(sim.Capture("p1"));
            sim.Restore("p1", airDoctored);
            Step(PhysicsTestWorld.Drive(), 1);
            var rateUndamped = RollRate(sim.Capture("p1"));
            Assert.That(Mathf.Abs(rateDamped - rateUndamped),
                Is.GreaterThan(0.05f),
                "filteredRollRate must feed the airborne damper — a "
                + "doctored restore changes the roll response ("
                + rateDamped + " vs " + rateUndamped + ")");
        }

        // Roll rate about the body forward axis from the persisted
        // state — the same quantity filteredRollRate estimates.
        private static float RollRate(MotorState state)
        {
            var p = state.physics;
            var q = new Quaternion(p.attitudeX, p.attitudeY,
                p.attitudeZ, p.attitudeW);
            var fwd = q * Vector3.forward;
            return p.angularVelocityX * fwd.x
                + p.angularVelocityY * fwd.y
                + p.angularVelocityZ * fwd.z;
        }

        // --- braking while leaned + low-speed steering reversal ----------

        [Test]
        [Timeout(120000)]
        public void BikeBrakesWhileLeanedAndSteersAtLowSpeed()
        {
            var physics = VehiclePhysicsTuning.Motorcycle01();
            physicsTuning = physics;
            var spawn = PhysicsTestWorld.PhysicalSpawnState(physics,
                new Vector3(0f, PhysicsTestWorld.SpawnY(physics), 0f), 0f,
                forwardSpeed: 15f);
            NewBike("bike-brake-lean", physics, spawn);
            PhysicsTestWorld.FlatGround(world, 0f, 0f, 800f, 800f);
            Step(PhysicsTestWorld.Drive(steering: 0.8f), 60);
            var leanedState = sim.Capture("p1");
            var leaned = SignedLean(leanedState);
            Assert.That(PhysicsTestWorld.BodyUpDot(leanedState),
                Is.GreaterThan(0.7f),
                "setup: the bike is leaned but upright before braking");
            Assert.That(Mathf.Abs(leaned), Is.GreaterThan(2f * Mathf.Deg2Rad),
                "setup: the bike must be leaned before braking (lean "
                + (leaned * Mathf.Rad2Deg) + " deg)");

            // Held brake while leaned: the bike must shed speed AND stay
            // supported — braking must not be blocked by the lean and
            // the capped assist must not crash it either.
            var minUpDot = 1f;
            var maxLeanBraking = 0f;
            for (var i = 0; i < 90; i++)
            {
                Step(PhysicsTestWorld.Drive(steering: 0.5f, brake: 1f), 1);
                var state = sim.Capture("p1");
                var dot = PhysicsTestWorld.BodyUpDot(state);
                var lean = Mathf.Abs(SignedLean(state));
                if (dot < minUpDot) minUpDot = dot;
                if (lean > maxLeanBraking) maxLeanBraking = lean;
            }
            var afterBrake = sim.Capture("p1");
            Assert.That(PhysicsTestWorld.ForwardSpeed(afterBrake),
                Is.LessThan(5f),
                "braking while leaned still slows the bike (speed "
                + PhysicsTestWorld.ForwardSpeed(afterBrake) + ")");
            Assert.That(minUpDot, Is.GreaterThan(0.5f),
                "the bike must not fall while braking leaned (minUpDot "
                + minUpDot + ")");
            Assert.That(maxLeanBraking,
                Is.LessThan(physics.assistMaxLeanRadians + 0.05f));

            // Low-speed steering reversal: alternate full lock — the
            // lean tracks sign smoothly, never past the bound, and the
            // bike stays up through both reversals.
            var reversedSigns = 0;
            var prevSign = 0;
            for (var i = 0; i < 180; i++)
            {
                var steer = (i / 45) % 2 == 0 ? 1f : -1f;
                Step(PhysicsTestWorld.Drive(steering: steer), 1);
                var lean = SignedLean(sim.Capture("p1"));
                Assert.That(Mathf.Abs(lean),
                    Is.LessThan(physics.assistMaxLeanRadians + 0.05f),
                    "low-speed reversal keeps lean bounded (tick " + i
                    + ", lean " + (lean * Mathf.Rad2Deg) + " deg)");
                if (Mathf.Abs(lean) > 1f * Mathf.Deg2Rad)
                {
                    var sign = lean > 0f ? 1 : -1;
                    if (prevSign != 0 && sign != prevSign) reversedSigns++;
                    prevSign = sign;
                }
            }
            var final = sim.Capture("p1");
            Assert.That(PhysicsTestWorld.BodyUpDot(final),
                Is.GreaterThan(0.5f), "the bike survives the reversals");
            Assert.That(reversedSigns, Is.GreaterThanOrEqualTo(2),
                "the lean actually reverses sign with steering "
                + "(reversals " + reversedSigns + ")");
        }

        // --- three consecutive laps on the authored graybox ---------------

        private static CourseDefinition GrayboxCourse()
        {
            // Mirrors the generated graybox-01 layout (same as the car
            // end-to-end fixture).
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
        [Timeout(600000)]
        public void MotorcycleCompletesThreeLapsOnAuthoredCourse()
        {
            var physics = VehiclePhysicsTuning.Motorcycle01();
            physicsTuning = physics;
            // Same fixture pace as the car e2e: a milder cruise keeps the
            // honest waypoint follower inside its steering radius.
            physics.cruiseSpeed = 12f;
            physics.boostSpeed = 16f;
            var tuning = PhysicsTestWorld.PhysicalTuning("motorcycle-01",
                physics);
            var course = GrayboxCourse();
            var evt = new EventDefinition
            {
                id = "e2e-bike",
                courseId = course.id,
                laps = 3,
                countdownTicks = 0,
                vehicleIds = new List<string> { "motorcycle-01" },
                recovery = new RecoverySettings()
            };
            session = new DrivingSession("e2e-bike", tuning, course, evt,
                "v", "start", new MotorPose
                {
                    x = 0f, y = 0f, z = -60f, heading = 0f
                });
            world = PhysicsCourseWorld.Create("e2e-bike");
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
            var minUpDot = 1f;
            var invalidState = "";
            var trail = new System.Text.StringBuilder();
            for (var i = 0; i < 16000 && !finished; i++)
            {
                if (i % 150 == 0)
                {
                    var m = session.Snapshot().motor;
                    trail.Append(' ').Append(i).Append(":(")
                        .Append(m.x.ToString("F0")).Append(',')
                        .Append(m.z.ToString("F0")).Append(",v")
                        .Append(m.speed.ToString("F1")).Append(",cp")
                        .Append(session.Snapshot().nextCheckpoint)
                        .Append(",lap").Append(session.Snapshot().lap)
                        .Append(')');
                    // The session motor carries physics once the first
                    // stepped boundary merges the sim's read-back — the
                    // pre-step snapshot legitimately lacks it.
                    if (m.physics != null)
                    {
                        var upDot = PhysicsTestWorld.BodyUpDot(m);
                        if (upDot < minUpDot) minUpDot = upDot;
                        if (!m.physics.Validate(out var stateError))
                            invalidState = "tick " + i + ": "
                                + stateError;
                    }
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
                        sawAirborne = true;
                    else if (ev.kind == "motor.landed")
                        sawLanded = true;
                    else if (ev.kind == "race.finished") finished = true;
                }
            }
            var snapshot = session.Snapshot();
            Assert.That(invalidState, Is.EqualTo(""),
                "no invalid physical state across three laps ("
                + invalidState + ")");
            Assert.That(finished, Is.True,
                "the physical motorcycle must complete three laps of the "
                + "authored course: crossed " + gatesCrossed
                + " gates, recovered " + recoveries
                + " times, last checkpoint " + snapshot.nextCheckpoint
                + ", lap " + snapshot.lap + " | trail" + trail);
            Assert.That(gatesCrossed, Is.GreaterThanOrEqualTo(15),
                "three finished laps cross every authored gate thrice");
            Assert.That(recoveries, Is.LessThanOrEqualTo(9),
                "recovery is a safety net, not a crutch: " + recoveries
                + " recoveries over three laps (air=" + sawAirborne
                + ", landed=" + sawLanded + ", minUpDot=" + minUpDot
                + ") | trail" + trail);
        }
    }
}
