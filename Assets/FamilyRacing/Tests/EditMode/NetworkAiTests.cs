using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // Task 4: vacant slots are driven by an honest AI — a waypoint
    // follower on the course's authored route that emits ordinary
    // steering/drift/boost/recover inputs under the same normalized
    // tuning and rules as humans — and bounded racer-to-racer contact
    // wired through the pre-step racer snapshot so iteration order can
    // never change outcomes. AI-driven events keep "ai" attribution at
    // the tick of the action, so absence driving earns no human
    // skill-bounty credit.
    public sealed class NetworkAiTests
    {
        private const float Pi = (float)Math.PI;

        [TestCase("homing-missile")]
        [TestCase("mine")]
        [TestCase("emp-pulse")]
        public void NewWeaponsWaitForUsefulOpportunityAndReleaseInheritedLatch(string kind)
        {
            var racer = RacerAt(0, -10, 0, 12);
            var context = new AiGadgetContext { heldItemId = kind, kind = kind, heldTicks = 90, heldGeneration = 9 };
            var ai = new AiDriver();
            Assert.IsFalse(ai.Sample(racer, WideSquareCourse(), context).Gadget, "do not waste weapon without a rival");
            context.rivalAhead = context.rivalNearby = context.rivalNearBehind = true;
            context.pressLatched = true;
            Assert.IsFalse(ai.Sample(racer, WideSquareCourse(), context).Gadget, "release inherited button first");
            context.pressLatched = false;
            Assert.IsTrue(ai.Sample(racer, WideSquareCourse(), context).Gadget, "use when a rival enters the weapon window");
            Assert.IsFalse(ai.Sample(racer, WideSquareCourse(), context).Gadget, "one use per generation");
        }

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

        private static readonly DriverInput Neutral =
            new DriverInput(0f, false, false, false, false, false, false);

        // --- fixture ---------------------------------------------------------

        private static CheckpointGate Gate(string id, float x, float z,
            float dirX, float dirZ)
        {
            return new CheckpointGate
            {
                id = id,
                x = x,
                z = z,
                dirX = dirX,
                dirZ = dirZ,
                halfWidth = 12f,
                recoverX = x - dirX * 3f,
                recoverY = 0f,
                recoverZ = z - dirZ * 3f,
                recoverHeading = (float)Math.Atan2(dirX, dirZ)
            };
        }

        private static RouteWaypoint Wp(float x, float z)
        {
            return new RouteWaypoint { x = x, z = z, radius = 0f };
        }

        // The graybox loop plus the authored AI route: north to gate-1,
        // west onto the landmark ramp, over its crest and north around
        // IslandNorthEast to gate-2, down the east side to gate-3, around
        // IslandSouthEast to gate-4, then south of the line and back
        // across start/finish. Every leg crosses its gate plane inside
        // the authored half-width.
        private static CourseDefinition TestCourse()
        {
            var course = new CourseDefinition
            {
                id = "graybox-net",
                spawnX = 0f,
                spawnY = 0f,
                spawnZ = -60f,
                spawnHeading = 0f,
                checkpoints = new List<CheckpointGate>
                {
                    Gate("start", 0f, -60f, 0f, 1f),
                    Gate("gate-1", 0f, 25f, 0f, 1f),
                    Gate("gate-2", 55f, 55f, 1f, 0f),
                    Gate("gate-3", 95f, 0f, 0f, -1f),
                    Gate("gate-4", 50f, -55f, -1f, 0f)
                }
            };
            course.spawnSlots.Add(Spawn(-5f, -57f));
            course.spawnSlots.Add(Spawn(5f, -57f));
            course.spawnSlots.Add(Spawn(-5f, -62f));
            course.spawnSlots.Add(Spawn(5f, -62f));
            course.route.Add(Wp(0f, -30f));
            course.route.Add(Wp(0f, 10f));    // pre-gate alignment
            course.route.Add(Wp(0f, 30f));    // crosses gate-1 inside half-width
            course.route.Add(Wp(-30f, 24f));
            course.route.Add(Wp(-54f, 14f));
            course.route.Add(Wp(-60f, 30f));  // ramp approach + crest
            course.route.Add(Wp(-60f, 46f));  // landing
            course.route.Add(Wp(-40f, 58f));
            course.route.Add(Wp(-10f, 62f));
            course.route.Add(Wp(20f, 62f));
            course.route.Add(Wp(48f, 60f));
            course.route.Add(Wp(62f, 55f));   // crosses gate-2
            course.route.Add(Wp(82f, 42f));
            course.route.Add(Wp(95f, 18f));
            course.route.Add(Wp(95f, -14f));  // crosses gate-3
            course.route.Add(Wp(99f, -34f));
            course.route.Add(Wp(92f, -48f));
            course.route.Add(Wp(68f, -60f));
            course.route.Add(Wp(38f, -56f));  // crosses gate-4
            course.route.Add(Wp(10f, -62f));
            course.route.Add(Wp(0f, -66f));
            course.route.Add(Wp(0f, -48f));   // crosses start/finish
            return course;
        }

        private static MotorPose Spawn(float x, float z)
        {
            return new MotorPose { x = x, y = 0f, z = z, heading = 0f };
        }

        private static EventDefinition LanEvent(string id, string vehicleId,
            string courseId, int laps)
        {
            return new EventDefinition
            {
                id = id,
                courseId = courseId,
                laps = laps,
                countdownTicks = 30,
                vehicleIds = new List<string> { vehicleId },
                completionCoins = 20
            };
        }

        private static DrivingContentCatalog TestCatalog(int laps = 1)
        {
            var course = TestCourse();
            return DrivingContentCatalog.Create("net-content-1",
                new[] { VehicleTuning.Car01(), VehicleTuning.Motorcycle01() },
                new[] { course },
                new[]
                {
                    LanEvent("lan-car-01", "car-01", course.id, laps),
                    LanEvent("lan-motorcycle-01", "motorcycle-01", course.id, laps)
                });
        }

        private static NetworkRacerSpec Spec(int slot, string participantId,
            string vehicleId, string controlSource)
        {
            return new NetworkRacerSpec
            {
                participantId = participantId,
                slot = slot,
                vehicleId = vehicleId,
                controlSource = controlSource
            };
        }

        private static List<NetworkRacerSpec> AiRoster(string vehicleId = "car-01")
        {
            return new List<NetworkRacerSpec>
            {
                Spec(0, "ai-0", vehicleId, "ai"),
                Spec(1, "ai-1", vehicleId, "ai"),
                Spec(2, "ai-2", vehicleId, "ai"),
                Spec(3, "ai-3", vehicleId, "ai")
            };
        }

        private static List<NetworkRacerSpec> MixedRoster(string vehicleId = "car-01")
        {
            return new List<NetworkRacerSpec>
            {
                Spec(0, "p-host", vehicleId, "local"),
                Spec(1, "p-guest", vehicleId, "remote"),
                Spec(2, "ai-2", vehicleId, "ai"),
                Spec(3, "ai-3", vehicleId, "ai")
            };
        }

        private static NetworkRace CreateRace(DrivingContentCatalog catalog,
            string eventId = "lan-car-01", string classId = "car",
            List<NetworkRacerSpec> roster = null)
        {
            var ok = NetworkRace.TryCreate("sess-1", "race-1", catalog,
                eventId, classId, roster ?? AiRoster(), out var race, out var error);
            Assert.That(ok, Is.True, "TryCreate failed: " + error);
            Assert.That(race, Is.Not.Null);
            return race;
        }

        private static Dictionary<string, DriverInput> NeutralInputs(NetworkRaceState state)
        {
            var map = new Dictionary<string, DriverInput>(StringComparer.Ordinal);
            foreach (var racer in state.racers)
                map[racer.participantId] = Neutral;
            return map;
        }

        private static NetworkRaceState StepAll(NetworkRace race, IMotorWorld world,
            int ticks, List<DrivingEvent> collect = null)
        {
            var state = race.Snapshot();
            for (var i = 0; i < ticks; i++)
            {
                state = race.Step(NeutralInputs(state), world);
                if (collect != null) collect.AddRange(state.events);
            }
            return state;
        }

        private static List<DrivingEvent> EventsOf(List<DrivingEvent> events,
            string kind, string participantId)
        {
            var found = new List<DrivingEvent>();
            foreach (var ev in events)
                if (ev.kind == kind && ev.participantId == participantId) found.Add(ev);
            return found;
        }

        private static NetworkRacerState Racer(NetworkRaceState state, string participantId)
        {
            foreach (var racer in state.racers)
                if (racer.participantId == participantId) return racer;
            return null;
        }

        private static List<string> RacerFacts(NetworkRaceState state)
        {
            var facts = new List<string>();
            foreach (var r in state.racers)
            {
                var m = r.session.motor;
                facts.Add(r.slot + "|" + r.session.phase
                    + "|" + r.session.simulationTick
                    + "|" + r.session.elapsedTicks
                    + "|" + r.session.lap + "|" + r.session.nextCheckpoint
                    + "|" + r.finished + "|" + r.dnf + "|" + r.finishTicks
                    + "|" + m.x.ToString("0.####") + "," + m.y.ToString("0.####")
                    + "," + m.z.ToString("0.####") + "," + m.heading.ToString("0.####")
                    + "," + m.speed.ToString("0.####"));
            }
            return facts;
        }

        private static MotorState MotorAt(float x, float y, float z)
        {
            return new MotorState { x = x, y = y, z = z, grounded = true };
        }

        private static RacerContacts.Entry Body(string participantId, float x, float y, float z)
        {
            return new RacerContacts.Entry
            {
                participantId = participantId,
                motor = MotorAt(x, y, z)
            };
        }

        // --- the six binding tests -----------------------------------------

        // Task 7: cornering lookahead. A tight 10 m square asks the car's
        // 2.6 rad/s steering for 90° turns — followable near ~13 m/s, so a
        // full-speed approach must brake through the ordinary held
        // control before the vertex, not after the heading error grows.
        private static CourseDefinition TightSquareCourse()
        {
            var course = new CourseDefinition { id = "corner-tight" };
            course.route.Add(Wp(0f, 0f));
            course.route.Add(Wp(0f, 10f));
            course.route.Add(Wp(10f, 10f));
            course.route.Add(Wp(10f, 0f));
            return course;
        }

        // The same 90° corners spread over 60 m arcs are followable at
        // full speed — the honest straight-line control case.
        private static CourseDefinition WideSquareCourse()
        {
            var course = new CourseDefinition { id = "corner-wide" };
            course.route.Add(Wp(0f, 0f));
            course.route.Add(Wp(0f, 60f));
            course.route.Add(Wp(60f, 60f));
            course.route.Add(Wp(60f, 0f));
            return course;
        }

        private static DrivingSessionState RacerAt(float x, float z,
            float heading, float speed, float boostCharge = 0f)
        {
            return new DrivingSessionState
            {
                phase = "running",
                vehicleId = "car-01",
                motor = new MotorState
                {
                    x = x, y = 0f, z = z,
                    heading = heading, speed = speed, grounded = true,
                    boostCharge = boostCharge
                }
            };
        }

        [Test]
        public void AiBrakesBeforeTightCorner()
        {
            var ai = new AiDriver();
            var course = TightSquareCourse();
            // Approaching the first 90° vertex flat out: the lookahead
            // covers the corner, so brake demand must be strong while
            // steering is still nearly neutral — the slowdown precedes
            // the turn, it does not react to it.
            var fast = RacerAt(0f, 2f, 0f, 18f, boostCharge: 1f);
            var sample = ai.Sample(fast, course);
            Assert.That(sample.Brake, Is.GreaterThan(0.5f),
                "a corner inside the lookahead must drive held brake");
            Assert.That(Math.Abs(sample.Steering), Is.LessThan(0.3f),
                "braking starts before the steering demand builds");
            Assert.That(sample.Boost, Is.False,
                "boost must never fire while the AI is braking");
            Assert.That(sample.HasThrottleOverride, Is.False,
                "the AI never claims a throttle override — braking is enough");

            // Below the yaw-limited corner speed the same pose demands
            // no brake at all — the control is honest, not a script.
            var slow = new AiDriver();
            var creeping = RacerAt(0f, 2f, 0f, 5f);
            var eased = slow.Sample(creeping, course);
            Assert.That(eased.Brake, Is.EqualTo(0f),
                "a followable approach speed must not manufacture brake");
        }

        // Physical pursuit feasibility: the physical backend's reachable
        // turn radius is real (wheel authority AND the friction ellipse),
        // so a target inside that radius can only be orbited — the
        // honest response is the same brake a human presses to shrink
        // the radius. The arcade kinematic, whose constant-rate heading
        // can reach any bearing, sees no such brake.
        private static DrivingSessionState PhysicalRacerAt(float x, float z,
            float heading, float speed)
        {
            var racer = RacerAt(x, z, heading, speed);
            racer.vehicleId = "car-physics-01";
            racer.simulationBackend = "physicalAssisted";
            return racer;
        }

        [Test]
        public void AiPhysicalBrakeCoversUnreachablePursuitTarget()
        {
            var ai = new AiDriver();
            // Wide-square route: the pursuit target is the next vertex —
            // place the racer 12 m short of it at cruise speed. The
            // physical car's reachable radius at 16 m/s (~30 m) swallows
            // the target — inside the off-axis Dubins disc, not merely
            // near the car: steering alone would orbit, so brake demand
            // must fire — ordinary DriverInput.Brake, not a slowdown.
            // Heading 0.4 rad off the target bearing saturates the
            // steering demand, so drift would engage on a flat gate —
            // the pursuit brake's suppression is what the assert sees.
            var racer = PhysicalRacerAt(0f, 48f, 0.4f, 16f);
            var sample = ai.Sample(racer, WideSquareCourse());
            Assert.That(sample.Brake, Is.GreaterThan(0.15f),
                "a pursuit target inside the reachable turn radius must " +
                "drive held brake on the physical backend");
            Assert.That(sample.Drift, Is.False,
                "the pursuit brake suppresses drift: a shrinking radius " +
                "needs the rear bite a drift surrenders");
            Assert.That(sample.Boost, Is.False,
                "boost stays gated off while the pursuit brake demands");

            // Arcade identical pose: the kinematic heading reaches any
            // bearing at speed, so no pursuit brake may appear — the
            // change is strictly physical-backend aware.
            var arcade = new AiDriver();
            var eased = arcade.Sample(
                RacerAt(0f, 48f, 0f, 16f), WideSquareCourse());
            Assert.That(eased.Brake, Is.EqualTo(0f),
                "the arcade backend's constant-rate heading must not " +
                "manufacture a pursuit brake");

            // Physical, but the target is beyond the reachable radius:
            // steering alone converges and no brake is fabricated.
            var far = new AiDriver();
            var sample2 = far.Sample(
                PhysicalRacerAt(0f, 0f, 0f, 16f), WideSquareCourse());
            Assert.That(sample2.Brake, Is.EqualTo(0f),
                "a pursuit target beyond the reachable radius brakes nothing");
        }

        [Test]
        public void AiPursuitBrakeIgnoresDeadAheadTarget()
        {
            // The Dubins unreachable discs sit BESIDE the heading axis:
            // a pursuit target well inside the reachable radius but
            // nearly dead ahead is reachable by driving straight, and
            // braking for it only slows lines that were never orbiting
            // (live regression: the authored-ramp approach slowed, the
            // shortened jump fell off the edge, and the same overturn
            // cycle repeated until the lap bound broke).
            var ai = new AiDriver();
            var course = WideSquareCourse();
            // The (0,-30) -> (0,30) leg is dead straight: the pursuit
            // target lands ~28 m ahead, inside the ~32 m reachable
            // radius at 16 m/s — the old distance-only test braked
            // ~0.7 here; the Dubins disc test sees the target outside
            // both tangent circles and demands nothing.
            var racer = PhysicalRacerAt(0f, -18f, 0f, 16f);
            var sample = ai.Sample(racer, course);
            Assert.That(sample.Brake, Is.EqualTo(0f),
                "a dead-ahead pursuit target inside the turn radius " +
                "must not brake — it is reachable, not orbited");
        }

        [Test]
        public void AiDisplacementWatchdogSeesThroughSpeedometer()
        {
            var ai = new AiDriver();
            var course = WideSquareCourse();
            // A car ramming a wall reads real speed while its net
            // position never moves — the velocity streak stays blind,
            // so the displacement watchdog is what requests the same
            // ordinary recover one-shot a stalled car presses.
            var ramming = RacerAt(0f, 20f, 0f, 10f);
            var seen = false;
            for (var i = 0; i < NetworkSettings.AiProgressStuckTicks + 4; i++)
                seen |= ai.Sample(ramming, course).Recover;
            Assert.That(seen, Is.True,
                "a grounded racer pinned inside the progress disc must " +
                "request recovery even while the speedometer reads speed");

            // A moving racer re-anchors the disc: an honest slow drive
            // never accumulates the streak.
            var moving = new AiDriver();
            var seenMoving = false;
            for (var i = 0; i < NetworkSettings.AiProgressStuckTicks + 4; i++)
                seenMoving |= moving.Sample(
                    RacerAt(i * 0.5f, 20f, 0f, 3f), course).Recover;
            Assert.That(seenMoving, Is.False,
                "net displacement outside the disc must reset the watchdog");
        }

        [Test]
        public void AiBoostStaysAStraightLineAssist()
        {
            var ai = new AiDriver();
            // Long arcs: followable at full speed, steering neutral —
            // a charged meter may boost like a human on a straight.
            var straight = RacerAt(0f, -10f, 0f, 15f, boostCharge: 1f);
            var boosted = ai.Sample(straight, WideSquareCourse());
            Assert.That(boosted.Brake, Is.EqualTo(0f));
            Assert.That(boosted.Boost, Is.True,
                "a charged straight is where the boost assist belongs");

            // The same speed into the tight corner: brake suppresses the
            // boost the way a human feathers off, and the AI stays off
            // the wall instead of trading speed for control.
            var cornering = new AiDriver();
            var fast = RacerAt(0f, 2f, 0f, 18f, boostCharge: 1f);
            var braked = cornering.Sample(fast, TightSquareCourse());
            Assert.That(braked.Boost, Is.False,
                "boost is gated away while braking into a turn");
        }

        [TestCase(1f)]
        [TestCase(0.9f)]
        [TestCase(0.75f)]
        public void AiSustainsBoostBelowItsStartingCharge(float threshold)
        {
            var ai = new AiDriver(0f, new AiDifficultyDefinition
                { id = "test", boostUseMinCharge = threshold });
            var racer = RacerAt(0f, -10f, 0f, 15f, threshold - 0.02f);
            racer.motor.boostActive = true;
            racer.motor.boostSecondsRemaining = 1f;
            Assert.That(ai.Sample(racer, WideSquareCourse()).Boost, Is.True,
                "a safe active burst must not be cancelled by its own charge drain");
            racer.motor.boostActive = false;
            Assert.That(ai.Sample(racer, WideSquareCourse()).Boost, Is.False,
                "the same charge must not start a fresh burst");
        }

        [Test]
        public void DefaultAiSustainsAFullMotorBoostOnAStraight()
        {
            var course = new CourseDefinition { id = "straight", isOpenRoute = true };
            course.route.Add(Wp(0f, 0f));
            course.route.Add(Wp(0f, 1000f));
            course.route.Add(Wp(0f, 2000f));
            var ai = new AiDriver();
            var racer = RacerAt(0f, 10f, 0f, 18f, 1f);
            var tuning = VehicleTuning.Car01();
            var activeTicks = 0;
            for (var tick = 0; tick < 90; tick++)
            {
                racer.motor = ArcadeMotor.Step(racer.motor, ai.Sample(racer, course),
                    tuning, new FlatWorld(), 1f / 30f).state;
                if (racer.motor.boostActive) activeTicks++;
            }
            Assert.That(activeTicks, Is.GreaterThanOrEqualTo(70),
                "a full meter should deliver nearly the authored 2.5-second burst");
            Assert.That(racer.motor.z, Is.GreaterThan(80f),
                "the sustained burst must produce actual progress");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AiReleasesActiveBoostWhenTheRoadIsUnsafe(bool cornerBrake)
        {
            var racer = cornerBrake
                ? RacerAt(0f, 2f, 0f, 18f, 1f)
                : RacerAt(0f, -10f, 0.5f, 15f, 1f);
            racer.motor.boostActive = true;
            racer.motor.boostSecondsRemaining = 1f;
            Assert.That(new AiDriver().Sample(racer,
                cornerBrake ? TightSquareCourse() : WideSquareCourse()).Boost,
                Is.False, "continuation must still respect braking and steering");
        }

        [TestCase("arcade", 18f)]
        [TestCase("physicalAssisted", 12f)]
        public void HardBrakesLessThanMediumAndEasy(string backend, float speed)
        {
            var racer = RacerAt(3f, 2f, 0f, speed);
            racer.simulationBackend = backend;
            var course = TightSquareCourse();
            var easy = new AiDriver(0f, new AiDifficultyDefinition
                { id = "easy", brakeMarginScale = 1.3f }).Sample(racer, course);
            var medium = new AiDriver(0f, new AiDifficultyDefinition
                { id = "medium", brakeMarginScale = 1f }).Sample(racer, course);
            var hard = new AiDriver(0f, new AiDifficultyDefinition
                { id = "hard", brakeMarginScale = 0.9f }).Sample(racer, course);
            Assert.That(easy.Brake, Is.GreaterThan(medium.Brake),
                "Easy should leave more cornering safety margin");
            Assert.That(medium.Brake, Is.GreaterThan(hard.Brake),
                "Hard should carry more speed through the same corner");
            Assert.That(new AiDriver().Sample(racer, course).Brake,
                Is.EqualTo(medium.Brake), "default/LAN retains the base margin");
        }

        [Test]
        public void AiUsesOrderedCourseRoute()
        {
            var race = CreateRace(TestCatalog());
            var world = new FlatWorld();
            var events = new List<DrivingEvent>();

            // Four AI drivers must complete the full one-lap course under
            // normal rules — ordered gates, the authored route, and no
            // checkpoint may be skipped.
            var state = race.Snapshot();
            for (var t = 0; t < 2600 && state.phase != "finished"; t++)
            {
                state = race.Step(NeutralInputs(state), world);
                events.AddRange(state.events);
            }
            Assert.That(state.phase, Is.EqualTo("finished"),
                "all four AI racers must finish the course inside the bound");
            foreach (var racer in state.racers)
                Assert.That(racer.finished, Is.True,
                    "AI racer " + racer.participantId + " must finish");
            Assert.That(state.result, Is.Not.Null);

            var crossed = EventsOf(events, "checkpoint.crossed", "ai-2");
            var order = new List<string>();
            foreach (var ev in crossed) order.Add(ev.detail);
            Assert.That(order, Is.EqualTo(new[]
                { "gate-1", "gate-2", "gate-3", "gate-4", "start" }),
                "the AI must accept the course gates strictly in route order");
            Assert.That(EventsOf(events, "race.lap", "ai-2").Count, Is.EqualTo(1));
            Assert.That(EventsOf(events, "race.finished", "ai-2").Count, Is.EqualTo(1));
        }

        [Test]
        public void AiRecoveryCannotSkipGate()
        {
            var race = CreateRace(TestCatalog());
            var world = new FlatWorld();
            var events = new List<DrivingEvent>();

            // Run until ai-2 has gate-1 banked (expected index 2).
            var state = StepAll(race, world, 30, events);
            for (var t = 0; t < 900
                && Racer(state, "ai-2").session.nextCheckpoint != 2; t++)
            {
                state = race.Step(NeutralInputs(state), world);
                events.AddRange(state.events);
            }
            Assert.That(Racer(state, "ai-2").session.nextCheckpoint, Is.EqualTo(2),
                "setup: ai-2 must have crossed gate-1");

            // A recovery while gate-2 is pending returns the racer to
            // gate-1's safe pose — the pending gate can never be skipped.
            string error;
            Assert.That(race.QueuePending("ai-2", "recover", "rc-test", out error),
                Is.True, error);
            state = StepAll(race, world, 2, events);
            var recovered = EventsOf(events, "race.recovered", "ai-2");
            Assert.That(recovered.Count, Is.EqualTo(1));
            var s2 = Racer(state, "ai-2").session;
            Assert.That(s2.nextCheckpoint, Is.EqualTo(2),
                "recovery must not advance the expected gate");
            Assert.That(s2.motor.z, Is.LessThan(25f),
                "the respawn lands behind gate-2's segment, not past it");

            // The AI resyncs onto the route and earns gate-2 honestly.
            var crossedBefore = EventsOf(events, "checkpoint.crossed", "ai-2").Count;
            for (var t = 0; t < 1200
                && Racer(state, "ai-2").session.nextCheckpoint != 3; t++)
            {
                state = race.Step(NeutralInputs(state), world);
                events.AddRange(state.events);
            }
            Assert.That(Racer(state, "ai-2").session.nextCheckpoint, Is.EqualTo(3),
                "the AI must re-drive the route and cross gate-2 after recovery");
            var gate2 = EventsOf(events, "checkpoint.crossed", "ai-2")
                .FindAll(e => e.detail == "gate-2");
            Assert.That(gate2.Count, Is.EqualTo(1),
                "exactly one gate-2 crossing may be emitted");

            // Unit level: a grounded, stalled AI emits the same recover
            // input a human presses — never a teleport.
            var ai = new AiDriver();
            var course = TestCourse();
            var stalled = new DrivingSessionState
            {
                phase = "running",
                motor = new MotorState
                {
                    x = -100f, y = 0f, z = -40f,
                    heading = 0f, speed = 0f, grounded = true
                }
            };
            var recoverSeen = false;
            for (var i = 0; i < NetworkSettings.AiRecoverStuckTicks + 4; i++)
                recoverSeen |= ai.Sample(stalled, course).Recover;
            Assert.That(recoverSeen, Is.True,
                "a stalled AI must request a normal recovery input");
        }

        [Test]
        public void ContactExcludesSelf()
        {
            var contacts = new RacerContacts(new List<RacerContacts.Entry>
            {
                Body("a", 0f, 0f, 0f),
                Body("b", 1.5f, 0f, 0f),
                Body("c", -1f, 0f, 1f)
            });
            var nudges = contacts.ForParticipant("a")
                .NudgesFor(MotorAt(0f, 0f, 0f));
            Assert.That(nudges.Count, Is.EqualTo(2),
                "overlapping neighbours push; the self body never does");
            foreach (var nudge in nudges)
            {
                Assert.That(nudge.racerId, Is.Not.EqualTo("a"),
                    "a racer can never nudge itself");
                Assert.That(nudge.racerId == "b" || nudge.racerId == "c", Is.True);
            }
            // The b-push points from b toward a (-x); c pushes a away too.
            var fromB = nudges.Find(n => n.racerId == "b");
            Assert.That(fromB.impulseX, Is.LessThan(0f));

            // A contact source for a participant absent from the snapshot
            // still excludes whatever id it was bound to and reports all
            // remaining bodies.
            var others = contacts.ForParticipant("zzz").NudgesFor(MotorAt(0f, 0f, 0f));
            Assert.That(others.Count, Is.EqualTo(3));
        }

        [Test]
        public void ContactMagnitudeIsBounded()
        {
            // Exact-overlap poses (overlapping spawn/recovery): nudges must
            // stay finite, deterministic and capped — no launch impulses.
            var contacts = new RacerContacts(new List<RacerContacts.Entry>
            {
                Body("a", 2f, 0f, -3f),
                Body("b", 2f, 0f, -3f),
                Body("c", 2f, 0f, -3f),
                Body("d", 2f, 0f, -3f)
            });
            var nudges = contacts.ForParticipant("a")
                .NudgesFor(MotorAt(2f, 0f, -3f));
            Assert.That(nudges.Count, Is.EqualTo(3));
            foreach (var nudge in nudges)
            {
                var magnitude = (float)Math.Sqrt(
                    nudge.impulseX * nudge.impulseX
                    + nudge.impulseZ * nudge.impulseZ);
                Assert.That(float.IsNaN(magnitude) || float.IsInfinity(magnitude),
                    Is.False, "nudge impulses must stay finite");
                Assert.That(magnitude,
                    Is.LessThanOrEqualTo(DriftBoostRules.NudgeMaxDeltaSpeed + 0.0001f),
                    "every contact impulse is individually bounded");
            }

            // An airborne neighbour too high to touch produces no nudge.
            var mixed = new RacerContacts(new List<RacerContacts.Entry>
            {
                Body("a", 0f, 0f, 0f),
                Body("air", 0.5f, 4f, 0f),
                Body("ground", 0.5f, 0f, 0f)
            });
            var low = mixed.ForParticipant("a").NudgesFor(MotorAt(0f, 0f, 0f));
            Assert.That(low.Count, Is.EqualTo(1));
            Assert.That(low[0].racerId, Is.EqualTo("ground"),
                "a racer several metres airborne never contacts the ground layer");

            // Through the motor the applied speed change is still capped.
            var tuning = VehicleTuning.Car01();
            var state = MotorAt(0f, 0f, 0f);
            var before = state.Copy();
            var result = ArcadeMotor.Step(state, Neutral, tuning, new FlatWorld(),
                DrivingSession.StepSeconds, mixed.ForParticipant("a"));
            Assert.That(result.rejected, Is.False);
            var fx = (float)Math.Sin(before.heading);
            var fz = (float)Math.Cos(before.heading);
            var rx = fz;
            var rz = -fx;
            var dvx = result.state.speed * fx + result.state.lateralSpeed * rx
                - (before.speed * fx + before.lateralSpeed * rx);
            var dvz = result.state.speed * fz + result.state.lateralSpeed * rz
                - (before.speed * fz + before.lateralSpeed * rz);
            // Auto-acceleration contributes at most accel*dt; the nudge
            // portion of the delta stays under the shared cap.
            var nudgeDelta = Math.Max(0f,
                (float)Math.Sqrt(dvx * dvx + dvz * dvz)
                - tuning.acceleration * DrivingSession.StepSeconds);
            Assert.That(nudgeDelta,
                Is.LessThanOrEqualTo(DriftBoostRules.NudgeMaxDeltaSpeed + 0.001f),
                "applied contact never exceeds the shared bound");
        }

        [Test]
        public void RacerIterationOrderDoesNotChangeStep()
        {
            // Two identical races stepped in lockstep; only the input map's
            // enumeration order differs. Contact queries read the same
            // pre-step snapshot either way, so every observable fact —
            // including nudge-driven cluster motion on the shared route —
            // must stay identical.
            var raceA = CreateRace(TestCatalog());
            var raceB = CreateRace(TestCatalog());
            var world = new FlatWorld();

            var stateA = raceA.Snapshot();
            var stateB = raceB.Snapshot();
            for (var t = 0; t < 900; t++)
            {
                var forward = NeutralInputs(stateA);
                var backward = new Dictionary<string, DriverInput>(StringComparer.Ordinal);
                for (var i = stateB.racers.Count - 1; i >= 0; i--)
                    backward[stateB.racers[i].participantId] = Neutral;
                stateA = raceA.Step(forward, world);
                stateB = raceB.Step(backward, world);
                Assert.That(RacerFacts(stateB), Is.EqualTo(RacerFacts(stateA)),
                    "input-map order must never change a step outcome (tick " + t + ")");
                var kindsA = stateA.events.ConvertAll(e => e.id + e.kind);
                var kindsB = stateB.events.ConvertAll(e => e.id + e.kind);
                Assert.That(kindsB, Is.EqualTo(kindsA),
                    "event emission must be order-independent (tick " + t + ")");
            }
        }

        [Test]
        public void AiEventsCarryNoHumanSkillCredit()
        {
            var race = CreateRace(TestCatalog(), roster: MixedRoster());
            var world = new FlatWorld();
            var events = new List<DrivingEvent>();

            // Drive until the AI racers have real race events banked.
            var state = StepAll(race, world, 30, events);
            for (var t = 0; t < 900
                && EventsOf(events, "checkpoint.crossed", "ai-2").Count == 0; t++)
            {
                state = race.Step(NeutralInputs(state), world);
                events.AddRange(state.events);
            }
            Assert.That(EventsOf(events, "checkpoint.crossed", "ai-2").Count,
                Is.GreaterThan(0), "setup: ai-2 must produce race events");

            // Every AI-attributed action stays "ai" — no human credit.
            foreach (var ev in events)
            {
                if (ev.participantId == "ai-2" || ev.participantId == "ai-3")
                    Assert.That(ev.attribution, Is.EqualTo("ai"),
                        ev.kind + " must carry ai attribution");
                else if (ev.participantId == "p-host" || ev.participantId == "p-guest")
                    Assert.That(ev.attribution, Is.EqualTo("human"));
            }

            // A human disconnect flips the slot to AI: actions from that
            // tick on are ai-attributed even though the slot was human.
            Assert.That(race.SetControlSource("p-guest", "ai"), Is.True);
            var aiWindowEvents = new List<DrivingEvent>();
            state = StepAll(race, world, 400, aiWindowEvents);
            var guestAiEvents = aiWindowEvents.FindAll(e => e.participantId == "p-guest");
            Assert.That(guestAiEvents.Count, Is.GreaterThan(0),
                "the flipped slot keeps racing under the AI");
            foreach (var ev in guestAiEvents)
                Assert.That(ev.attribution, Is.EqualTo("ai"),
                    "actions after the control flip are ai at the tick they occur");

            // The same tick-marking applies in reverse: a rejoining human
            // stamps "human" from that tick on, and the earlier ai window
            // is never rewritten.
            Assert.That(race.SetControlSource("p-guest", "remote"), Is.True);
            var humanWindowEvents = new List<DrivingEvent>();
            state = StepAll(race, world, 60, humanWindowEvents);
            foreach (var ev in humanWindowEvents)
                if (ev.participantId == "p-guest")
                    Assert.That(ev.attribution, Is.EqualTo("human"));
            foreach (var ev in guestAiEvents)
                Assert.That(ev.attribution, Is.EqualTo("ai"),
                    "emitted events are stamped at their tick, never re-derived");

            // Rejected flips leave attribution untouched.
            Assert.That(race.SetControlSource("nobody", "ai"), Is.False);
            Assert.That(race.SetControlSource("ai-2", "bogus"), Is.False);
        }
    }
}
