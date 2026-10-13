using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // Task 4 simulation/rule boundary contract: NetworkRace and
    // DrivingSession drive ONE injected IVehicleSimulation batch per
    // logical tick instead of stepping each racer's motor inline. The
    // binding order is: prepare every request (+ pending resets) ->
    // apply resets -> one shared batch step -> complete every rule ->
    // apply rule-requested recovery resets -> publish one committed
    // boundary. A spy simulation records the calls so the tests can pin
    // ordering, freeze semantics and atomicity.
    public sealed class VehicleSimulationBoundaryTests
    {
        private static readonly DriverInput Neutral =
            new DriverInput(0f, false, false, false, false, false, false);

        // --- spy simulation ---------------------------------------------------

        // Records every boundary call. Capture returns a dummy state so the
        // coordinator's roster check never triggers a late Initialize —
        // the test owns initialization explicitly.
        private sealed class SpySimulation : IVehicleSimulation
        {
            internal int initializeCalls;
            internal int stepCalls;
            internal int restoreCalls;
            internal IReadOnlyList<VehicleTickRequest> lastRequests;
            internal readonly List<string> order = new List<string>();
            // Runs INSIDE Step, before any result is handed back — the
            // tests use it to prove every session was already prepared
            // and none had been completed yet.
            internal Action<IReadOnlyList<VehicleTickRequest>> insideStep;
            // Optional per-request result override (rejection fixtures).
            internal Func<VehicleTickRequest, VehicleTickResult> resultFor;
            internal readonly List<VehicleSpawn> spawned =
                new List<VehicleSpawn>();
            internal readonly List<string> restored = new List<string>();

            public void Initialize(IReadOnlyList<VehicleSpawn> vehicles)
            {
                initializeCalls++;
                order.Add("Initialize");
                spawned.Clear();
                if (vehicles != null) spawned.AddRange(vehicles);
            }

            public IReadOnlyList<VehicleTickResult> Step(
                IReadOnlyList<VehicleTickRequest> requests)
            {
                stepCalls++;
                order.Add("Step");
                lastRequests = requests;
                if (insideStep != null) insideStep(requests);
                var results = new List<VehicleTickResult>();
                if (requests != null)
                    foreach (var request in requests)
                        results.Add(resultFor != null
                            ? resultFor(request)
                            : Advance(request));
                return results;
            }

            public void Restore(string participantId, MotorState state)
            {
                restoreCalls++;
                order.Add("Restore");
                restored.Add(participantId);
            }

            public MotorState Capture(string participantId)
            {
                return new MotorState(); // pretend-initialized
            }

            public void Dispose() { }

            // Default answer: an honest +2 m move along +z with a
            // three-sample path whose second segment is empty.
            private static VehicleTickResult Advance(VehicleTickRequest request)
            {
                var before = request.rulesBefore;
                var moved = before != null ? before.Copy() : new MotorState();
                var from = moved.Pose;
                if (request.simulateMotion) moved.z += 2f;
                var to = moved.Pose;
                return new VehicleTickResult
                {
                    participantId = request.participantId,
                    step = new MotorStepResult { state = moved },
                    path = new[]
                    {
                        new VehiclePathSample { offsetSeconds = 0f, pose = from },
                        new VehiclePathSample { offsetSeconds = 1f / 60f, pose = to },
                        new VehiclePathSample { offsetSeconds = 1f / 30f, pose = to }
                    }
                };
            }
        }

        // --- fixture ---------------------------------------------------------

        private static CheckpointGate Gate(string id, float x, float z,
            float dirX, float dirZ, float halfWidth = 12f)
        {
            return new CheckpointGate
            {
                id = id,
                x = x,
                z = z,
                dirX = dirX,
                dirZ = dirZ,
                halfWidth = halfWidth,
                recoverX = x - dirX * 3f,
                recoverY = 0f,
                recoverZ = z - dirZ * 3f,
                recoverHeading = (float)Math.Atan2(dirX, dirZ)
            };
        }

        private static MotorPose Spawn(float x, float z)
        {
            return new MotorPose { x = x, y = 0f, z = z, heading = 0f };
        }

        private static CourseDefinition TestCourse()
        {
            var course = new CourseDefinition
            {
                id = "boundary-course",
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

        private static DrivingContentCatalog TestCatalog(int countdownTicks = 30)
        {
            var evt = new EventDefinition
            {
                id = "lan-car-01",
                courseId = "boundary-course",
                laps = 1,
                countdownTicks = countdownTicks,
                vehicleIds = new List<string> { "car-01" },
                completionCoins = 20
            };
            return DrivingContentCatalog.Create("boundary-content-1",
                new[] { VehicleTuning.Car01() },
                new[] { TestCourse() },
                new[] { evt });
        }

        private static List<NetworkRacerSpec> HumanRoster()
        {
            return new List<NetworkRacerSpec>
            {
                Spec(0, "p-host", "local"),
                Spec(1, "p-guest", "remote"),
                Spec(2, "p-2", "remote"),
                Spec(3, "p-3", "local")
            };
        }

        private static NetworkRacerSpec Spec(int slot, string participantId,
            string controlSource)
        {
            return new NetworkRacerSpec
            {
                participantId = participantId,
                slot = slot,
                vehicleId = "car-01",
                controlSource = controlSource
            };
        }

        private static NetworkRace CreateRace(
            DrivingContentCatalog catalog = null)
        {
            var ok = NetworkRace.TryCreate("sess-1", "race-1",
                catalog ?? TestCatalog(), "lan-car-01", "car", HumanRoster(),
                out var race, out var error);
            Assert.That(ok, Is.True, "TryCreate failed: " + error);
            return race;
        }

        private static Dictionary<string, DriverInput> NeutralInputs(
            NetworkRaceState state)
        {
            var map = new Dictionary<string, DriverInput>(
                StringComparer.Ordinal);
            foreach (var racer in state.racers)
                map[racer.participantId] = Neutral;
            return map;
        }

        private static List<DrivingEvent> EventsOf(List<DrivingEvent> events,
            string kind, string participantId)
        {
            var found = new List<DrivingEvent>();
            foreach (var ev in events)
                if (ev.kind == kind && ev.participantId == participantId)
                    found.Add(ev);
            return found;
        }

        // --- the binding tests -------------------------------------------------

        // One batch per boundary: every request is fully prepared before
        // the single Step, and no racer's rules run until the batch
        // returns — then all four complete in slot order.
        [Test]
        public void PrepareAllBeforeSimulateBeforeComplete()
        {
            var race = CreateRace();
            var spy = new SpySimulation();
            var state = race.Snapshot();

            // 30 countdown boundaries: one shared Step each, every
            // request prepared first, all bodies frozen.
            var observedPrepared = 0;
            var unfinishedMotors = 0;
            var tickSeen = -1;
            for (var tick = 0; tick < 30; tick++)
            {
                var expected = tick + 1;
                spy.insideStep = requests =>
                {
                    // Inside the batch every session already consumed its
                    // boundary (simulationTick incremented) but none has
                    // committed a result yet (motor still at spawn).
                    var inner = race.Snapshot();
                    observedPrepared = 0;
                    unfinishedMotors = 0;
                    foreach (var racer in inner.racers)
                    {
                        if (racer.session.simulationTick == expected)
                            observedPrepared++;
                        if (Math.Abs(racer.session.motor.z
                                - TestCourse().spawnSlots[racer.slot].z) < 0.001f)
                            unfinishedMotors++;
                    }
                    Assert.That(requests.Count, Is.EqualTo(4),
                        "all four racers must be prepared before Step");
                };
                state = race.Step(NeutralInputs(state), spy);
                tickSeen = tick;
            }
            Assert.That(tickSeen, Is.EqualTo(29));
            Assert.That(observedPrepared, Is.EqualTo(4),
                "prepare x4 must run before the single batch");
            Assert.That(unfinishedMotors, Is.EqualTo(4),
                "no racer may complete before the batch returns");
            Assert.That(spy.stepCalls, Is.EqualTo(30),
                "exactly one batch per boundary");
            Assert.That(state.phase, Is.EqualTo("running"));
            Assert.That(state.elapsedTicks, Is.EqualTo(0),
                "countdown never charges the race clock");

            // Running boundary: the inside-step probe must see all four
            // prepared (simulationTick incremented) and zero completed
            // (motors still parked), then all four results commit.
            var preparedSeen = 0;
            var unparkedSeen = 0;
            spy.insideStep = requests =>
            {
                var inner = race.Snapshot();
                foreach (var racer in inner.racers)
                {
                    if (racer.session.simulationTick == 31) preparedSeen++;
                    if (Math.Abs(racer.session.motor.z
                            - TestCourse().spawnSlots[racer.slot].z) > 0.001f)
                        unparkedSeen++;
                }
            };
            state = race.Step(NeutralInputs(state), spy);
            Assert.That(preparedSeen, Is.EqualTo(4),
                "prepare x4 must run before the single batch");
            Assert.That(unparkedSeen, Is.EqualTo(0),
                "no racer's rules may complete before the batch returns");
            foreach (var racer in state.racers)
            {
                Assert.That(racer.session.motor.z,
                    Is.EqualTo(TestCourse().spawnSlots[racer.slot].z + 2f)
                        .Within(0.001f),
                    "racer " + racer.participantId + " must complete x4");
            }

            // The race clock ticks once per batch, never per racer.
            var elapsedBefore = state.elapsedTicks;
            for (var i = 0; i < 5; i++)
                state = race.Step(NeutralInputs(state), spy);
            Assert.That(state.elapsedTicks, Is.EqualTo(elapsedBefore + 5));
            foreach (var racer in state.racers)
                Assert.That(racer.session.elapsedTicks,
                    Is.EqualTo(elapsedBefore + 5));
        }

        // Countdown requests are frozen; pausing holds every race timer
        // and never touches the simulation.
        [Test]
        public void CountdownAndPauseFreezeBodiesAndTimers()
        {
            var race = CreateRace();
            var spy = new SpySimulation();
            var state = race.Snapshot();

            state = race.Step(NeutralInputs(state), spy);
            Assert.That(spy.lastRequests.Count, Is.EqualTo(4));
            foreach (var request in spy.lastRequests)
                Assert.That(request.simulateMotion, Is.False,
                    "countdown bodies must be frozen requests");
            Assert.That(state.elapsedTicks, Is.EqualTo(0));

            var running = race.Step(NeutralInputs(
                StepAll(race, spy, 29, state)), spy);
            Assert.That(running.phase, Is.EqualTo("running"));

            var frozenHostTick = running.hostTick;
            var frozenSimTicks = new long[4];
            for (var i = 0; i < 4; i++)
                frozenSimTicks[i] = running.racers[i].session.simulationTick;

            race.SetPaused(true);
            var pausedCalls = spy.stepCalls;
            for (var i = 0; i < 5; i++)
            {
                var paused = race.Step(NeutralInputs(running), spy);
                Assert.That(paused.paused, Is.True);
                Assert.That(paused.hostTick, Is.EqualTo(frozenHostTick),
                    "the host clock must freeze under pause");
            }
            Assert.That(spy.stepCalls, Is.EqualTo(pausedCalls),
                "a paused race must never call simulation.Step");
            var held = race.Snapshot();
            Assert.That(held.elapsedTicks, Is.EqualTo(running.elapsedTicks));
            for (var i = 0; i < 4; i++)
                Assert.That(held.racers[i].session.simulationTick,
                    Is.EqualTo(frozenSimTicks[i]));

            race.SetPaused(false);
            var resumed = race.Step(NeutralInputs(running), spy);
            Assert.That(resumed.hostTick, Is.EqualTo(frozenHostTick + 1));
            Assert.That(spy.stepCalls, Is.EqualTo(pausedCalls + 1));
        }

        private static NetworkRaceState StepAll(NetworkRace race,
            SpySimulation spy, int ticks, NetworkRaceState state)
        {
            for (var i = 0; i < ticks; i++)
                state = race.Step(NeutralInputs(state), spy);
            return state;
        }

        // A pending (manual) recovery is applied to the body BEFORE the
        // batch runs, and the published boundary already shows the reset
        // pose — never the pre-reset one.
        [Test]
        public void RecoveryResetPrecedesPublication()
        {
            var race = CreateRace();
            var spy = new SpySimulation();
            var state = StepAll(race, spy, 31, race.Snapshot());
            Assert.That(state.phase, Is.EqualTo("running"));
            // No gate has been accepted, so the safe respawn is the slot.
            var safe = TestCourse().spawnSlots[0];

            // Drive a few boundaries so the committed pose differs from
            // the safe pose, then queue the manual recovery.
            state = StepAll(race, spy, 3, state);
            Assert.That(state.racers[0].session.motor.z,
                Is.GreaterThan(safe.z + 4f), "setup: racer must have moved");

            string queueError;
            Assert.That(race.QueuePending("p-host", "recover", "rc-1",
                    out queueError), Is.True, queueError);
            spy.order.Clear();
            spy.restored.Clear();
            var stepped = race.Step(NeutralInputs(state), spy);

            Assert.That(spy.order, Is.EqualTo(new[] { "Restore", "Step" }),
                "the pending reset must land on the body before physics runs");
            Assert.That(spy.restored, Is.EqualTo(new[] { "p-host" }));
            var published = stepped.racers[0].session.motor;
            Assert.That(published.z, Is.EqualTo(safe.z).Within(0.001f),
                "the committed boundary publishes the reset pose");
            Assert.That(published.recovering, Is.True,
                "the reset boundary stays flagged so it can never "
                + "manufacture a crossing");
        }

        // A rule-requested recovery (auto off-course) is applied AFTER
        // the batch completes but BEFORE the committed boundary is
        // published.
        [Test]
        public void RuleResetAppliedAfterCompleteBeforePublish()
        {
            var race = CreateRace();
            var spy = new SpySimulation();
            var state = StepAll(race, spy, 31, race.Snapshot());
            // No accepted gate yet: the safe respawn is the slot itself.
            var safe = TestCourse().spawnSlots[1];

            // The spy drops p-guest far below the course this tick: the
            // session's recovery rules must fire, and the coordinator must
            // restore the body before the state goes out.
            spy.resultFor = request =>
            {
                var before = request.rulesBefore;
                var moved = before != null ? before.Copy() : new MotorState();
                var from = moved.Pose;
                if (request.simulateMotion)
                {
                    if (request.participantId == "p-guest") moved.y = -20f;
                    else moved.z += 2f;
                }
                var to = moved.Pose;
                return new VehicleTickResult
                {
                    participantId = request.participantId,
                    step = new MotorStepResult { state = moved },
                    path = new[]
                    {
                        new VehiclePathSample { offsetSeconds = 0f, pose = from },
                        new VehiclePathSample { offsetSeconds = 1f / 60f, pose = to },
                        new VehiclePathSample { offsetSeconds = 1f / 30f, pose = to }
                    }
                };
            };
            spy.order.Clear();
            var stepped = race.Step(NeutralInputs(state), spy);

            Assert.That(spy.order, Is.EqualTo(new[] { "Step", "Restore" }),
                "a rule-requested reset is applied after complete, "
                + "before publish");
            Assert.That(spy.restored, Is.EqualTo(new[] { "p-guest" }));
            var published = stepped.racers[1].session.motor;
            Assert.That(published.y, Is.EqualTo(safe.y).Within(0.001f));
            Assert.That(published.z, Is.EqualTo(safe.z).Within(0.001f),
                "published pose is the recovery pose, not the fallen one");
            var recovered = EventsOf(stepped.events, "race.recovered", "p-guest");
            Assert.That(recovered.Count, Is.EqualTo(1));
        }

        // Gate crossings are evaluated on the two chronological path
        // segments, not the end-to-end chord: a path that crosses the
        // gate on the first substep and settles back behind the plane
        // must still accept the gate.
        [Test]
        public void SubstepPathsCrossOrderedGates()
        {
            var course = new CourseDefinition
            {
                id = "substep-course",
                spawnX = 0f,
                spawnY = 0f,
                spawnZ = 45f,
                spawnHeading = 0f,
                checkpoints = new List<CheckpointGate>
                {
                    Gate("start", 0f, 30f, 0f, 1f),
                    Gate("gate-1", 0f, 50f, 0f, 1f)
                }
            };
            var evt = new EventDefinition
            {
                id = "substep-event",
                courseId = "substep-course",
                laps = 1,
                countdownTicks = 0,
                vehicleIds = new List<string> { "car-01" }
            };
            var session = new DrivingSession("race-sub", VehicleTuning.Car01(),
                course, evt, "v1", "cmd-1");
            session.DrainEvents(); // drop race.started/race.running

            var request = session.PrepareStep(Neutral);
            Assert.That(request, Is.Not.Null);
            Assert.That(request.simulateMotion, Is.True);
            var prev = request.rulesBefore.Pose;
            var mid = new MotorPose { x = 0f, y = 0f, z = 55f }; // crossed
            var end = new MotorPose { x = 0f, y = 0f, z = 47f }; // settled back
            var result = new VehicleTickResult
            {
                participantId = request.participantId,
                step = new MotorStepResult
                {
                    state = new MotorState
                    {
                        x = 0f, y = 0f, z = 47f, heading = 0f, grounded = true
                    }
                },
                path = new[]
                {
                    new VehiclePathSample { offsetSeconds = 0f, pose = prev },
                    new VehiclePathSample { offsetSeconds = 1f / 60f, pose = mid },
                    new VehiclePathSample { offsetSeconds = 1f / 30f, pose = end }
                }
            };
            var completed = session.CompleteStep(result);

            Assert.That(completed.state.nextCheckpoint, Is.EqualTo(0),
                "the first chronological segment crossed gate-1 even "
                + "though the end-to-end chord never touches it");
            var crossed = false;
            foreach (var ev in completed.events)
                if (ev.kind == "checkpoint.crossed" && ev.detail == "gate-1")
                    crossed = true;
            Assert.That(crossed, Is.True,
                "gate-1 accepted by the substep path, not the chord");
        }

        // A rejected result leaves that racer's motor untouched and
        // surfaces motor.error — while every other racer in the same
        // batch still advances.
        [Test]
        public void RejectedBatchDoesNotPartiallyAdvance()
        {
            var race = CreateRace();
            var spy = new SpySimulation();
            var state = StepAll(race, spy, 31, race.Snapshot());
            Assert.That(state.phase, Is.EqualTo("running"));
            var before = new float[4];
            for (var i = 0; i < 4; i++)
                before[i] = state.racers[i].session.motor.z;
            var parked = state.racers[1].session.motor.z;

            spy.resultFor = request =>
            {
                if (request.participantId == "p-guest")
                {
                    return new VehicleTickResult
                    {
                        participantId = request.participantId,
                        step = MotorStepResult.Rejected(
                            request.rulesBefore, "invalidRulesBefore"),
                        path = new[]
                        {
                            new VehiclePathSample
                            {
                                offsetSeconds = 0f,
                                pose = request.rulesBefore.Pose
                            },
                            new VehiclePathSample
                            {
                                offsetSeconds = 1f / 60f,
                                pose = request.rulesBefore.Pose
                            },
                            new VehiclePathSample
                            {
                                offsetSeconds = 1f / 30f,
                                pose = request.rulesBefore.Pose
                            }
                        }
                    };
                }
                var before = request.rulesBefore;
                var moved = before.Copy();
                var from = moved.Pose;
                moved.z += 2f;
                var to = moved.Pose;
                return new VehicleTickResult
                {
                    participantId = request.participantId,
                    step = new MotorStepResult { state = moved },
                    path = new[]
                    {
                        new VehiclePathSample { offsetSeconds = 0f, pose = from },
                        new VehiclePathSample { offsetSeconds = 1f / 60f, pose = to },
                        new VehiclePathSample { offsetSeconds = 1f / 30f, pose = to }
                    }
                };
            };
            var stepped = race.Step(NeutralInputs(state), spy);

            Assert.That(spy.stepCalls, Is.EqualTo(32),
                "the rejection happened inside the same single batch");
            var guest = stepped.racers[1];
            Assert.That(guest.session.motor.z, Is.EqualTo(parked).Within(0.0001f),
                "a rejected result can never move the motor");
            var errors = EventsOf(stepped.events, "motor.error", "p-guest");
            Assert.That(errors.Count, Is.EqualTo(1));
            Assert.That(errors[0].detail, Is.EqualTo("invalidRulesBefore"));
            foreach (var racer in stepped.racers)
            {
                if (racer.participantId == "p-guest") continue;
                Assert.That(racer.session.motor.z,
                    Is.EqualTo(before[racer.slot] + 2f).Within(0.001f),
                    "racer " + racer.participantId
                    + " must still advance — no partial batch rollback");
            }
        }
    }
}
