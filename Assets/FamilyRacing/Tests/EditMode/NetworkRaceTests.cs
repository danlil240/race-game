using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // Task 3: one authoritative 30 Hz clock drives four per-slot
    // DrivingSessions — shared countdown, shared elapsed race clock,
    // shared pause that freezes every race timer, class-normalized tuning
    // (profile upgrades never reach the sim), authored non-overlapping
    // spawn slots, host standings, and a once-only finalized result for
    // the shared finish policy: all four finish, or 60 unpaused race
    // seconds after the first finisher, remaining racers DNF in stable
    // slot order.
    public sealed class NetworkRaceTests
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

        private struct Wp
        {
            internal float x;
            internal float z;

            internal Wp(float x, float z) { this.x = x; this.z = z; }
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

        // Same loop as the shipped graybox-01 course plus a four-slot
        // starting grid behind/on the start line: two staggered rows.
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
            return course;
        }

        private static MotorPose Spawn(float x, float z)
        {
            return new MotorPose { x = x, y = 0f, z = z, heading = 0f };
        }

        private static EventDefinition LanEvent(string id, string vehicleId)
        {
            return new EventDefinition
            {
                id = id,
                courseId = "graybox-net",
                laps = 1,
                countdownTicks = 30,
                vehicleIds = new List<string> { vehicleId },
                completionCoins = 20
            };
        }

        private static DrivingContentCatalog TestCatalog()
        {
            return DrivingContentCatalog.Create("net-content-1",
                new[] { VehicleTuning.Car01(), VehicleTuning.Motorcycle01() },
                new[] { TestCourse() },
                new[]
                {
                    LanEvent("lan-car-01", "car-01"),
                    LanEvent("lan-motorcycle-01", "motorcycle-01")
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

        // Two humans (local + remote) and two AI fills — the full four-slot
        // roster a lobby hands the coordinator.
        private static List<NetworkRacerSpec> Roster(string vehicleId = "car-01")
        {
            return new List<NetworkRacerSpec>
            {
                Spec(0, "p-host", vehicleId, "local"),
                Spec(1, "p-guest", vehicleId, "remote"),
                Spec(2, "ai-2", vehicleId, "ai"),
                Spec(3, "ai-3", vehicleId, "ai")
            };
        }

        // All-human variant: tests that need slots to hold neutral input
        // (single finisher, DNF ordering, identical-input lockstep) seat
        // humans everywhere — AI-attributed slots genuinely drive since
        // Task 4, so they cannot play the inert role here.
        private static List<NetworkRacerSpec> HumanRoster(string vehicleId = "car-01")
        {
            return new List<NetworkRacerSpec>
            {
                Spec(0, "p-host", vehicleId, "local"),
                Spec(1, "p-guest", vehicleId, "remote"),
                Spec(2, "p-2", vehicleId, "remote"),
                Spec(3, "p-3", vehicleId, "local")
            };
        }

        private static NetworkRace CreateRace(DrivingContentCatalog catalog,
            string eventId = "lan-car-01", string classId = "car",
            List<NetworkRacerSpec> roster = null)
        {
            var ok = NetworkRace.TryCreate("sess-1", "race-1", catalog,
                eventId, classId, roster ?? Roster(), out var race, out var error);
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
            int ticks, Func<NetworkRaceState, Dictionary<string, DriverInput>> build = null,
            List<DrivingEvent> collect = null)
        {
            var state = race.Snapshot();
            for (var i = 0; i < ticks; i++)
            {
                state = race.Step(build == null ? NeutralInputs(state) : build(state), world);
                if (collect != null) collect.AddRange(state.events);
            }
            return state;
        }

        private static float WrapAngle(float radians)
        {
            while (radians > Pi) radians -= 2f * Pi;
            while (radians <= -Pi) radians += 2f * Pi;
            return radians;
        }

        // Chase a waypoint list for one slot while every other racer holds
        // neutral input. Returns the state after the route completes, the
        // racer finishes, or maxTicks run out.
        private static NetworkRaceState DriveRoute(NetworkRace race, IMotorWorld world,
            int slot, IList<Wp> route, NetworkRaceState state, int maxTicks,
            List<DrivingEvent> collect = null)
        {
            var index = 0;
            for (var t = 0; t < maxTicks && index < route.Count
                && !state.racers[slot].finished; t++)
            {
                var motor = state.racers[slot].session.motor;
                var dx = route[index].x - motor.x;
                var dz = route[index].z - motor.z;
                if (dx * dx + dz * dz < 36f) { index++; continue; }
                var desired = (float)Math.Atan2(dx, dz);
                var steer = Math.Clamp(WrapAngle(desired - motor.heading) * 3f, -1f, 1f);
                var map = NeutralInputs(state);
                map[state.racers[slot].participantId] =
                    new DriverInput(steer, false, false, false, false, false, false);
                state = race.Step(map, world);
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

        private static int EventsOfKind(List<DrivingEvent> events, string kind)
        {
            var count = 0;
            foreach (var ev in events) if (ev.kind == kind) count++;
            return count;
        }

        // Legal one-lap route on the test course: gate-1 north, gate-2 east,
        // gate-3 south, gate-4 west, then dip south of the line and cross
        // the start/finish plane heading +z.
        private static List<Wp> FinishRoute()
        {
            return new List<Wp>
            {
                new Wp(0f, 33f),
                new Wp(63f, 55f),
                new Wp(95f, -8f),
                new Wp(44f, -55f),
                new Wp(0f, -70f),
                new Wp(0f, -50f)
            };
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

        // --- the seven binding tests -----------------------------------------

        [Test]
        public void FourDistinctSpawnsShareCountdown()
        {
            var course = TestCourse();
            var race = CreateRace(TestCatalog());
            var world = new FlatWorld();

            var state = race.Snapshot();
            Assert.That(state.phase, Is.EqualTo("countdown"));
            Assert.That(state.countdownTicksRemaining, Is.EqualTo(30));
            Assert.That(state.racers.Count, Is.EqualTo(4));
            Assert.That(state.result, Is.Null);

            // Each racer occupies its authored spawn slot; all four are
            // pairwise non-overlapping.
            for (var i = 0; i < 4; i++)
            {
                var racer = state.racers[i];
                Assert.That(racer.slot, Is.EqualTo(i));
                Assert.That(racer.session.motor.x,
                    Is.EqualTo(course.spawnSlots[i].x).Within(0.001f));
                Assert.That(racer.session.motor.z,
                    Is.EqualTo(course.spawnSlots[i].z).Within(0.001f));
                Assert.That(racer.session.motor.heading,
                    Is.EqualTo(course.spawnSlots[i].heading).Within(0.001f));
                Assert.That(racer.session.phase, Is.EqualTo("countdown"));
                Assert.That(racer.session.simulationTick, Is.EqualTo(0));
            }
            for (var i = 0; i < 4; i++)
                for (var j = i + 1; j < 4; j++)
                {
                    var dx = state.racers[i].session.motor.x
                        - state.racers[j].session.motor.x;
                    var dz = state.racers[i].session.motor.z
                        - state.racers[j].session.motor.z;
                    Assert.That(dx * dx + dz * dz, Is.GreaterThan(9f),
                        "spawn slots " + i + " and " + j + " overlap");
                }

            // First boundary: all four sessions emit race.started stamped
            // with participantId and human|ai attribution.
            var stepped = race.Step(NeutralInputs(state), world);
            var started = stepped.events.FindAll(e => e.kind == "race.started");
            Assert.That(started.Count, Is.EqualTo(4));
            foreach (var ev in started)
            {
                var expected = ev.participantId == "ai-2" || ev.participantId == "ai-3"
                    ? "ai" : "human";
                Assert.That(ev.attribution, Is.EqualTo(expected),
                    "race.started for " + ev.participantId);
            }

            // Remaining countdown: all four share one tick and one clock.
            state = StepAll(race, world, 29);
            Assert.That(state.phase, Is.EqualTo("running"));
            Assert.That(state.hostTick, Is.EqualTo(30));
            Assert.That(state.elapsedTicks, Is.EqualTo(0),
                "countdown boundaries never charge race time");
            foreach (var racer in state.racers)
            {
                Assert.That(racer.session.simulationTick, Is.EqualTo(30),
                    "racer " + racer.participantId + " must share the host tick");
                Assert.That(racer.session.phase, Is.EqualTo("running"));
                Assert.That(racer.session.elapsedTicks, Is.EqualTo(0));
            }

            // 30 steps at 30 Hz = one running second for every racer.
            state = StepAll(race, world, 30);
            Assert.That(state.hostTick, Is.EqualTo(60));
            Assert.That(state.elapsedTicks, Is.EqualTo(30));
            foreach (var racer in state.racers)
            {
                Assert.That(racer.session.elapsedTicks, Is.EqualTo(30));
                Assert.That(racer.session.simulationTick, Is.EqualTo(60));
                Assert.That(racer.session.motor.z,
                    Is.GreaterThan(course.spawnSlots[racer.slot].z),
                    "racer " + racer.participantId + " must actually drive");
            }
        }

        [Test]
        public void WrongClassRejectedAtomically()
        {
            var catalog = TestCatalog();
            var roster = Roster();
            roster[1].vehicleId = "motorcycle-01"; // wrong class for lan-car-01
            var ok = NetworkRace.TryCreate("sess-1", "race-1", catalog,
                "lan-car-01", "car", roster, out var race, out var error);
            Assert.That(ok, Is.False);
            Assert.That(race, Is.Null,
                "a rejected roster must leave no partially built race");
            Assert.That(error, Is.EqualTo("invalidVehicle"));

            // A class the event does not offer is rejected before racers build.
            ok = NetworkRace.TryCreate("sess-1", "race-2", catalog,
                "lan-car-01", "motorcycle", Roster(), out race, out error);
            Assert.That(ok, Is.False);
            Assert.That(race, Is.Null);
            Assert.That(error, Is.EqualTo("invalidClass"));

            // The motorcycle-class event rejects cars the same way.
            ok = NetworkRace.TryCreate("sess-1", "race-3", catalog,
                "lan-motorcycle-01", "motorcycle", Roster(), out race, out error);
            Assert.That(ok, Is.False);
            Assert.That(race, Is.Null);
            Assert.That(error, Is.EqualTo("invalidVehicle"));

            // A corrected roster creates cleanly after the rejections —
            // no half-built state leaked into later creation.
            ok = NetworkRace.TryCreate("sess-1", "race-4", catalog,
                "lan-car-01", "car", Roster(), out race, out error);
            Assert.That(ok, Is.True, error);
            Assert.That(race, Is.Not.Null);
            ok = NetworkRace.TryCreate("sess-1", "race-5", catalog,
                "lan-motorcycle-01", "motorcycle", Roster("motorcycle-01"),
                out race, out error);
            Assert.That(ok, Is.True, error);
        }

        [Test]
        public void NormalizationIgnoresProfileUpgrades()
        {
            var catalog = TestCatalog();
            var roster = HumanRoster();
            // A profile-supplied "upgraded" tune must never reach the sim:
            // within a class every racer runs the identical catalog baseline.
            var boosted = VehicleTuning.Car01();
            boosted.maxSpeed = 99f;
            boosted.boostMaxSpeed = 140f;
            boosted.acceleration = 40f;
            roster[2].requestedTuning = boosted;
            roster[3].requestedTuning = VehicleTuning.Motorcycle01();
            var race = CreateRace(catalog, roster: roster);
            var world = new FlatWorld();

            var boost = new DriverInput(0f, false, true, false, false, false, false);
            var state = StepAll(race, world, 30); // countdown
            state = StepAll(race, world, 240, s =>
            {
                var map = new Dictionary<string, DriverInput>(StringComparer.Ordinal);
                foreach (var racer in s.racers) map[racer.participantId] = boost;
                return map;
            });

            var baseline = VehicleTuning.Car01();
            var reference = state.racers[0].session.motor;
            foreach (var racer in state.racers)
            {
                var motor = racer.session.motor;
                Assert.That(racer.session.vehicleId, Is.EqualTo("car-01"),
                    "model identity is preserved under normalization");
                Assert.That(motor.speed, Is.EqualTo(reference.speed).Within(0.0001f),
                    "racer " + racer.participantId
                    + " must run the identical baseline tuning");
                Assert.That(motor.z - SpawnZ(racer.slot),
                    Is.EqualTo(reference.z - SpawnZ(0)).Within(0.0001f),
                    "identical inputs on identical tuning must stay in lockstep");
                Assert.That(motor.speed,
                    Is.LessThanOrEqualTo(baseline.boostMaxSpeed + 0.001f),
                    "an upgraded 140 m/s boost tune must never be reachable");
            }
            // Cosmetic/spawn identity survives: distinct slots never occupy
            // the same pose even though performance is identical.
            for (var i = 0; i < 4; i++)
                for (var j = i + 1; j < 4; j++)
                {
                    var dx = state.racers[i].session.motor.x
                        - state.racers[j].session.motor.x;
                    var dz = state.racers[i].session.motor.z
                        - state.racers[j].session.motor.z;
                    Assert.That(dx * dx + dz * dz, Is.GreaterThan(1f),
                        "racer slots " + i + "/" + j + " must stay distinct");
                }
        }

        private static float SpawnZ(int slot)
        {
            return slot < 2 ? -57f : -62f;
        }

        [Test]
        public void PauseFreezesAllRaceTimers()
        {
            var race = CreateRace(TestCatalog());
            var world = new FlatWorld();
            var events = new List<DrivingEvent>();

            var state = StepAll(race, world, 30, null, events);
            // Finish slot 0 so the 60-second grace deadline is armed.
            state = DriveRoute(race, world, 0, FinishRoute(), state, 4000, events);
            Assert.That(state.racers[0].finished, Is.True,
                "route must finish slot 0 to arm the finish grace");
            Assert.That(state.finishDeadlineElapsedTicks, Is.GreaterThan(0));

            var before = RacerFacts(state);
            var tickBefore = state.hostTick;
            var elapsedBefore = state.elapsedTicks;
            var deadlineBefore = state.finishDeadlineElapsedTicks;
            var standingsBefore = string.Join(",", state.standings.ConvertAll(s => s.ToString()));

            race.SetPaused(true);
            // Paused steps must not move any clock, motor, progress, or
            // finish deadline — even with live-looking inputs.
            var aggressive = new DriverInput(1f, true, true, false, true, false, false);
            var paused = StepAll(race, world, 45, s =>
            {
                var map = new Dictionary<string, DriverInput>(StringComparer.Ordinal);
                foreach (var racer in s.racers) map[racer.participantId] = aggressive;
                return map;
            }, events);
            Assert.That(paused.paused, Is.True);
            Assert.That(paused.phase, Is.EqualTo("running"));
            Assert.That(paused.hostTick, Is.EqualTo(tickBefore),
                "the host clock freezes while paused");
            Assert.That(paused.elapsedTicks, Is.EqualTo(elapsedBefore));
            Assert.That(paused.finishDeadlineElapsedTicks, Is.EqualTo(deadlineBefore),
                "the finish grace deadline must not move while paused");
            Assert.That(paused.result, Is.Null);
            Assert.That(RacerFacts(paused), Is.EqualTo(before),
                "no racer's motor/progress/finish facts may change while paused");
            var standingsAfter = string.Join(",", paused.standings.ConvertAll(s => s.ToString()));
            Assert.That(standingsAfter, Is.EqualTo(standingsBefore));

            race.SetPaused(false);
            var resumed = StepAll(race, world, 5);
            Assert.That(resumed.paused, Is.False);
            Assert.That(resumed.hostTick, Is.EqualTo(tickBefore + 5));
            Assert.That(resumed.elapsedTicks, Is.EqualTo(elapsedBefore + 5),
                "the shared race clock resumes from where it froze");
        }

        [Test]
        public void SkippedCheckpointCannotFinish()
        {
            var race = CreateRace(TestCatalog());
            var world = new FlatWorld();
            var events = new List<DrivingEvent>();

            var state = StepAll(race, world, 30, null, events);
            // Leg A: drive straight at gate-2's plane while gate-1 is still
            // required — a forward crossing of a non-expected gate banks
            // nothing.
            state = DriveRoute(race, world, 0, new List<Wp> { new Wp(62f, 56f) },
                state, 600, events);
            var s0 = state.racers[0].session;
            Assert.That(s0.nextCheckpoint, Is.EqualTo(1),
                "crossing gate-2 early must not bank it");
            Assert.That(s0.lap, Is.EqualTo(0));
            Assert.That(s0.finished, Is.False);
            Assert.That(EventsOf(events, "checkpoint.crossed", "p-host"), Is.Empty,
                "an out-of-order crossing emits no checkpoint event");

            // Leg B: dip south then cross the start/finish plane forward
            // while gate-1 is still expected — no lap, no finish.
            state = DriveRoute(race, world, 0,
                new List<Wp> { new Wp(0f, -70f), new Wp(0f, -50f) },
                state, 900, events);
            s0 = state.racers[0].session;
            Assert.That(s0.nextCheckpoint, Is.EqualTo(1));
            Assert.That(s0.lap, Is.EqualTo(0),
                "crossing the line before the gate cycle grants no lap");
            Assert.That(s0.finished, Is.False);
            Assert.That(EventsOf(events, "race.lap", "p-host"), Is.Empty);
            Assert.That(EventsOf(events, "race.finished", "p-host"), Is.Empty);

            // Leg C: the legal full lap is the only path to finish.
            state = DriveRoute(race, world, 0, FinishRoute(), state, 3000, events);
            Assert.That(state.racers[0].finished, Is.True,
                "the legal gate sequence must complete the lap");
            Assert.That(EventsOf(events, "race.finished", "p-host").Count,
                Is.EqualTo(1), "exactly one finish may be attributed");
        }

        [Test]
        public void FirstFinishDoesNotEndOtherRacers()
        {
            var race = CreateRace(TestCatalog(), roster: HumanRoster());
            var world = new FlatWorld();
            var events = new List<DrivingEvent>();

            var state = StepAll(race, world, 30, null, events);
            state = DriveRoute(race, world, 0, FinishRoute(), state, 4000, events);
            var racer0 = state.racers[0];
            Assert.That(racer0.finished, Is.True);
            Assert.That(racer0.finishTicks, Is.GreaterThan(0));
            Assert.That(racer0.position, Is.EqualTo(1));

            // One finisher does not finalize the race or its result.
            Assert.That(state.phase, Is.EqualTo("running"));
            Assert.That(state.result, Is.Null,
                "per-racer finish is not global result completion");
            Assert.That(state.firstFinishElapsedTicks, Is.EqualTo(racer0.finishTicks));
            Assert.That(state.finishDeadlineElapsedTicks,
                Is.EqualTo(racer0.finishTicks + NetworkSettings.FinishGraceTicks));
            Assert.That(state.standings[0], Is.EqualTo(0));

            var tick0 = racer0.session.simulationTick;
            var elapsed1 = state.racers[1].session.elapsedTicks;
            var z1 = state.racers[1].session.motor.z;

            state = StepAll(race, world, 30, null, events);
            Assert.That(state.result, Is.Null);
            Assert.That(state.phase, Is.EqualTo("running"));
            Assert.That(state.racers[0].session.simulationTick, Is.EqualTo(tick0),
                "a finished racer parks; it no longer simulates");
            Assert.That(state.racers[1].session.elapsedTicks,
                Is.EqualTo(elapsed1 + 30), "unfinished racers keep racing");
            Assert.That(state.racers[1].session.motor.z, Is.GreaterThan(z1));

            var finishes = events.FindAll(e => e.kind == "race.finished");
            Assert.That(finishes.Count, Is.EqualTo(1));
            Assert.That(finishes[0].participantId, Is.EqualTo("p-host"));
            Assert.That(finishes[0].attribution, Is.EqualTo("human"));
            Assert.That(EventsOfKind(events, "race.finalized"), Is.EqualTo(0));
        }

        [Test]
        public void FinishGraceProducesStableDnfOrder()
        {
            var race = CreateRace(TestCatalog(), roster: HumanRoster());
            var world = new FlatWorld();
            var events = new List<DrivingEvent>();

            var state = StepAll(race, world, 30, null, events);
            state = DriveRoute(race, world, 0, FinishRoute(), state, 4000, events);
            Assert.That(state.racers[0].finished, Is.True);
            Assert.That(state.result, Is.Null);

            // The remaining racers never progress past gate-1's segment
            // (they run off north): at firstFinish + 1800 ticks they DNF.
            var remaining = (int)(state.finishDeadlineElapsedTicks - state.elapsedTicks);
            state = StepAll(race, world, remaining, null, events);
            Assert.That(state.phase, Is.EqualTo("finished"));
            Assert.That(state.result, Is.Not.Null, "grace expiry finalizes the race");
            var result = state.result;
            Assert.That(result.finalized, Is.True);
            Assert.That(result.sessionId, Is.EqualTo("sess-1"));
            Assert.That(result.raceId, Is.EqualTo("race-1"));
            Assert.That(result.eventId, Is.EqualTo("lan-car-01"));
            Assert.That(result.contentVersion, Is.EqualTo("net-content-1"));
            Assert.That(result.completionCoins, Is.EqualTo(20));
            Assert.That(result.racers.Count, Is.EqualTo(4));

            // The finisher leads; identical-progress DNFs order by slot.
            Assert.That(result.racers[0].participantId, Is.EqualTo("p-host"));
            Assert.That(result.racers[0].outcome, Is.EqualTo("finished"));
            Assert.That(result.racers[0].position, Is.EqualTo(1));
            Assert.That(result.racers[0].coinsAwarded, Is.EqualTo(20));
            Assert.That(result.racers[0].attribution, Is.EqualTo("human"));
            Assert.That(result.racers[0].finishTicks, Is.GreaterThan(0));
            for (var i = 1; i < 4; i++)
            {
                Assert.That(result.racers[i].outcome, Is.EqualTo("dnf"),
                    "unfinished racers DNF at grace expiry");
                Assert.That(result.racers[i].slot, Is.EqualTo(i),
                    "equal progress breaks ties by stable slot index");
                Assert.That(result.racers[i].position, Is.EqualTo(i + 1));
                Assert.That(result.racers[i].coinsAwarded, Is.EqualTo(0));
                Assert.That(result.racers[i].attribution, Is.EqualTo("human"));
            }
            Assert.That(EventsOfKind(events, "race.finalized"), Is.EqualTo(1));

            // The finalized result is created once and is immutable in
            // place: later steps never rebuild or reorder it.
            var snapshot = result;
            var later = StepAll(race, world, 30, null, events);
            Assert.That(later.result, Is.SameAs(snapshot));
            Assert.That(EventsOfKind(events, "race.finalized"), Is.EqualTo(1));
        }

        // --- task-level invariants beyond the seven named tests ---------------

        [Test]
        public void AbortedRaceProducesNoRewardResult()
        {
            var race = CreateRace(TestCatalog());
            var world = new FlatWorld();
            var events = new List<DrivingEvent>();
            var state = StepAll(race, world, 60, null, events);

            race.Abort("hostClosed");
            var aborted = StepAll(race, world, 30, null, events);
            Assert.That(aborted.phase, Is.EqualTo("aborted"));
            Assert.That(aborted.result, Is.Null,
                "an aborted race must never carry a reward-bearing result");
            Assert.That(EventsOfKind(events, "race.finalized"), Is.EqualTo(0));
            var tick = aborted.hostTick;
            aborted = StepAll(race, world, 30, null, events);
            Assert.That(aborted.hostTick, Is.EqualTo(tick),
                "an aborted race stops simulating");
        }

        [Test]
        public void RecoveryNeverAdvancesProgressOrFinishes()
        {
            var race = CreateRace(TestCatalog());
            var world = new FlatWorld();
            var events = new List<DrivingEvent>();
            var state = StepAll(race, world, 30, null, events);

            // Cross gate-1 legally, then recover: the respawn returns to
            // gate-1's safe pose and progress must not move.
            state = DriveRoute(race, world, 0, new List<Wp> { new Wp(0f, 33f) },
                state, 900, events);
            Assert.That(state.racers[0].session.nextCheckpoint, Is.EqualTo(2));
            var lapsBefore = state.racers[0].session.lap;

            string error;
            Assert.That(race.QueuePending("p-host", "recover", "rc-1", out error),
                Is.True, error);
            state = StepAll(race, world, 2, null, events);
            var s0 = state.racers[0].session;
            var recovered = EventsOf(events, "race.recovered", "p-host");
            Assert.That(recovered.Count, Is.EqualTo(1));
            Assert.That(s0.nextCheckpoint, Is.EqualTo(2),
                "recovery must not advance the expected gate");
            Assert.That(s0.lap, Is.EqualTo(lapsBefore));
            Assert.That(s0.finished, Is.False);
            Assert.That(s0.motor.z, Is.LessThan(24f),
                "the respawn lands behind gate-1's plane, not past it");
            Assert.That(EventsOf(events, "race.finished", "p-host"), Is.Empty,
                "a recovery can never manufacture a finish");
        }
    }
}
