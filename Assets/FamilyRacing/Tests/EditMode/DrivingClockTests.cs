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
    // Simulation-boundary contract: pending one-shot actions latch with
    // their command identities until a boundary consumes them, ordered and
    // bounded; pause freezes the simulation while the observation tick and
    // input leases keep running; lifecycle transitions cancel queued work;
    // duplicate start/restart commands preserve the original race identity.
    public sealed class DrivingClockTests
    {
        private const string RunId = "clock-test-run";

        private string directory;
        private FoundationService service;
        private FlatWorld world;

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
                    normalX = 0f,
                    normalY = 1f,
                    normalZ = 0f,
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

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "family-racing-clock-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            service = new FoundationService(
                new ProfileStore(directory, new TestJsonSerializer()), "en", RunId);
            service.SetDrivingContent(TestCatalog());
            world = new FlatWorld();
            var created = service.Execute(Cmd("profile.create",
                P("slot", "0", "language", "en")));
            Assert.That(created.status, Is.EqualTo(CommandResult.Applied), "setup create failed");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        // --- fixture helpers -------------------------------------------------

        private static DrivingContentCatalog TestCatalog()
        {
            var course = new CourseDefinition
            {
                id = "course-test",
                spawnX = 0f,
                spawnY = 0f,
                spawnZ = 0f,
                spawnHeading = 0f,
                checkpoints = new List<CheckpointGate>()
            };
            var evt = new EventDefinition
            {
                id = "intro-test",
                courseId = "course-test",
                laps = 1,
                countdownTicks = 3,
                vehicleIds = new List<string> { "car-01" }
            };
            return DrivingContentCatalog.Create("test-content-1",
                new[] { VehicleTuning.Car01() },
                new[] { course },
                new[] { evt });
        }

        private CommandResult StartRace(string commandId = null)
        {
            var result = service.Execute(Cmd("race.start",
                P("vehicleId", "car-01", "eventId", "intro-test"), commandId));
            return result;
        }

        private void StartRunning()
        {
            var started = StartRace();
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied), "race.start setup failed");
            Boundary(3); // countdownTicks = 3 -> running
            Assert.That(service.Snapshot().driving.phase, Is.EqualTo("running"));
        }

        // One fixed-step boundary: sample input and consume pending actions
        // through the simulation step, then advance the observation tick.
        private void Boundary(int steps = 1)
        {
            for (var i = 0; i < steps; i++)
            {
                service.StepDriving(world);
                service.AdvanceTick();
            }
        }

        private CommandResult SetInput(string steering = "0", string duration = "5",
            string drift = "false", string boost = "false", string gadget = "false",
            string recover = "false", string camera = "false", string pause = "false",
            string commandId = null)
        {
            return service.Execute(Cmd("input.set",
                P("steering", steering, "drift", drift, "boost", boost,
                    "gadget", gadget, "recover", recover, "changeCamera", camera,
                    "pause", pause, "durationTicks", duration), commandId));
        }

        private static List<DrivingEvent> Events(FoundationService svc, string kind)
        {
            var found = new List<DrivingEvent>();
            foreach (var ev in svc.Snapshot().drivingEvents)
                if (ev.kind == kind) found.Add(ev);
            return found;
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

        // --- countdown / boundary contract ------------------------------------

        [Test]
        public void CountdownAdvancesSimulationButNotElapsedRaceTime()
        {
            var started = StartRace();
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot().driving;
            Assert.That(state.phase, Is.EqualTo("countdown"));
            Assert.That(state.countdownTicksRemaining, Is.EqualTo(3));

            Boundary(3);
            state = service.Snapshot().driving;
            Assert.That(state.phase, Is.EqualTo("running"));
            Assert.That(state.simulationTick, Is.EqualTo(3));
            Assert.That(state.elapsedTicks, Is.EqualTo(0),
                "countdown boundaries must not charge race elapsed time");

            Boundary(2);
            state = service.Snapshot().driving;
            Assert.That(state.elapsedTicks, Is.EqualTo(2));
            Assert.That(state.simulationTick, Is.EqualTo(5));
        }

        // --- pending-action queue contract -------------------------------------

        [Test]
        public void RecoveryThenNeutralBeforeBoundaryPreservesOneAction()
        {
            StartRunning();
            var queued = SetInput(recover: "true", commandId: "recover-1");
            Assert.That(queued.status, Is.EqualTo(CommandResult.Applied));
            var neutral = SetInput(recover: "false", steering: "0.5");
            Assert.That(neutral.status, Is.EqualTo(CommandResult.Applied),
                "a neutral input sample after a recovery press is still applied");
            Assert.That(service.Snapshot().driving.pendingActions.Count, Is.EqualTo(1),
                "the neutral sample must not erase the latched recovery");

            Boundary(1);
            var consumed = Events(service, "action.consumed");
            Assert.That(consumed.Count, Is.EqualTo(1));
            Assert.That(consumed[0].commandId, Is.EqualTo("recover-1"));
            Assert.That(consumed[0].detail, Is.EqualTo("recover"));
            Assert.That(service.Snapshot().driving.pendingActions.Count, Is.Zero);
        }

        [Test]
        public void MultiplePendingActionsRetainOrderAndCorrelation()
        {
            StartRunning();
            Assert.That(SetInput(recover: "true", commandId: "recover-a").status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(SetInput(recover: "true", commandId: "recover-b").status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(SetInput(recover: "true", commandId: "recover-c").status,
                Is.EqualTo(CommandResult.Applied));

            Boundary(1);
            var consumed = Events(service, "action.consumed");
            Assert.That(consumed.Count, Is.EqualTo(1),
                "at most one pending action is consumed per step");
            Assert.That(consumed[0].commandId, Is.EqualTo("recover-a"));

            Boundary(2);
            consumed = Events(service, "action.consumed");
            Assert.That(consumed.Count, Is.EqualTo(3));
            Assert.That(consumed[0].commandId, Is.EqualTo("recover-a"));
            Assert.That(consumed[1].commandId, Is.EqualTo("recover-b"));
            Assert.That(consumed[2].commandId, Is.EqualTo("recover-c"));
        }

        [Test]
        public void PulseReachesSessionOnceAcrossCatchUpSteps()
        {
            StartRunning();
            Assert.That(SetInput(recover: "true", commandId: "recover-once").status,
                Is.EqualTo(CommandResult.Applied));
            Boundary(3); // three catch-up simulation steps after one pulse
            var consumed = Events(service, "action.consumed");
            Assert.That(consumed.Count, Is.EqualTo(1),
                "a single pulse must reach the session exactly once");
            Assert.That(consumed[0].commandId, Is.EqualTo("recover-once"));
        }

        [Test]
        public void QueueOverflowRejectsWithoutPartialMutation()
        {
            StartRunning();
            Assert.That(SetInput(steering: "0.5", duration: "30").status,
                Is.EqualTo(CommandResult.Applied));
            for (var i = 0; i < DrivingSession.PendingCapacity; i++)
            {
                Assert.That(SetInput(recover: "true", commandId: "fill-" + i).status,
                    Is.EqualTo(CommandResult.Applied), "fill-" + i);
            }
            var before = service.Snapshot().input;
            var overflow = SetInput(recover: "true", steering: "-1", commandId: "overflow-1");
            Assert.That(overflow.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(overflow.code, Is.EqualTo("pendingQueueFull"));
            var after = service.Snapshot();
            Assert.That(after.input.steering, Is.EqualTo(before.steering),
                "rejected overflow must not mutate held input");
            Assert.That(after.driving.pendingActions.Count,
                Is.EqualTo(DrivingSession.PendingCapacity),
                "rejected overflow must not partially enqueue");
        }

        [Test]
        public void PausedDrivingFreezesWhileObservationAndLeasesAdvance()
        {
            StartRunning();
            Assert.That(SetInput(steering: "1", duration: "2").status,
                Is.EqualTo(CommandResult.Applied));
            var running = service.Snapshot().driving;
            var simBefore = running.simulationTick;
            var elapsedBefore = running.elapsedTicks;
            var tickBefore = service.Snapshot().tick;

            var paused = service.Execute(Cmd("pause.toggle", null));
            Assert.That(paused.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(service.Snapshot().paused, Is.True);

            Boundary(5);
            var state = service.Snapshot();
            Assert.That(state.paused, Is.True);
            Assert.That(state.tick, Is.EqualTo(tickBefore + 5),
                "the observation tick keeps advancing while paused");
            Assert.That(state.driving.simulationTick, Is.EqualTo(simBefore),
                "simulation time freezes while paused");
            Assert.That(state.driving.elapsedTicks, Is.EqualTo(elapsedBefore));
            Assert.That(state.input.steering, Is.EqualTo(0f),
                "held input leases still expire while paused");

            var resumed = service.Execute(Cmd("pause.toggle", null));
            Assert.That(resumed.status, Is.EqualTo(CommandResult.Applied));
            Boundary(1);
            Assert.That(service.Snapshot().driving.simulationTick,
                Is.EqualTo(simBefore + 1), "simulation resumes after unpause");
        }

        [Test]
        public void PausingCancelsQueuedActions()
        {
            StartRunning();
            Assert.That(SetInput(recover: "true", commandId: "recover-p").status,
                Is.EqualTo(CommandResult.Applied));
            Assert.That(service.Snapshot().driving.pendingActions.Count, Is.EqualTo(1));
            var paused = service.Execute(Cmd("pause.toggle", null));
            Assert.That(paused.status, Is.EqualTo(CommandResult.Applied));
            var cancelled = Events(service, "action.cancelled");
            Assert.That(cancelled.Count, Is.EqualTo(1));
            Assert.That(cancelled[0].commandId, Is.EqualTo("recover-p"));
            Assert.That(cancelled[0].detail, Is.EqualTo("paused"));
            Assert.That(service.Snapshot().driving.pendingActions.Count, Is.Zero);
            Boundary(2);
            Assert.That(Events(service, "action.consumed").Count, Is.Zero,
                "a cancelled action must never reach the session");
        }

        [Test]
        public void LifecycleTransitionsCancelQueuedActions()
        {
            StartRunning();
            Assert.That(SetInput(recover: "true", commandId: "recover-x").status,
                Is.EqualTo(CommandResult.Applied));
            var exit = service.Execute(Cmd("race.exit", null));
            Assert.That(exit.status, Is.EqualTo(CommandResult.Applied));
            var cancelled = Events(service, "action.cancelled");
            Assert.That(cancelled.Count, Is.EqualTo(1));
            Assert.That(cancelled[0].commandId, Is.EqualTo("recover-x"));
            Assert.That(service.Snapshot().driving, Is.Null);
            Boundary(2);
            Assert.That(Events(service, "action.consumed").Count, Is.Zero);
        }

        [Test]
        public void InputClearCancelsQueuedActions()
        {
            StartRunning();
            Assert.That(SetInput(recover: "true", commandId: "recover-c").status,
                Is.EqualTo(CommandResult.Applied));
            var cleared = service.Execute(Cmd("input.clear", null));
            Assert.That(cleared.status, Is.EqualTo(CommandResult.Applied));
            var cancelled = Events(service, "action.cancelled");
            Assert.That(cancelled.Count, Is.EqualTo(1));
            Assert.That(cancelled[0].commandId, Is.EqualTo("recover-c"));
            Assert.That(service.Snapshot().driving.pendingActions.Count, Is.Zero);
        }

        // --- identity contract ---------------------------------------------------

        [Test]
        public void DuplicateStartPreservesOriginalRaceIdentity()
        {
            var first = StartRace("start-dup");
            Assert.That(first.status, Is.EqualTo(CommandResult.Applied));
            var raceId = service.Snapshot().driving.raceId;
            var again = service.Execute(Cmd("race.start",
                P("vehicleId", "car-01", "eventId", "intro-test"), "start-dup"));
            Assert.That(again.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(again.duplicate, Is.True);
            Assert.That(again.tick, Is.EqualTo(first.tick));
            Assert.That(service.Snapshot().driving.raceId, Is.EqualTo(raceId),
                "duplicate delivery must not replace the running race");
            Assert.That(Events(service, "race.started").Count, Is.EqualTo(1));
        }

        [Test]
        public void DuplicateRestartPreservesOriginalOutcome()
        {
            StartRunning();
            var first = service.Execute(Cmd("race.restart", null, "restart-dup"));
            Assert.That(first.status, Is.EqualTo(CommandResult.Applied));
            var raceId = service.Snapshot().driving.raceId;
            var again = service.Execute(Cmd("race.restart", null, "restart-dup"));
            Assert.That(again.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(again.duplicate, Is.True);
            Assert.That(service.Snapshot().driving.raceId, Is.EqualTo(raceId),
                "duplicate restart keeps the identity created by the first delivery");
            Assert.That(Events(service, "race.started").Count, Is.EqualTo(2),
                "exactly two starts: initial race.start plus one restart");
        }

        [Test]
        public void SecondStartWhileRacingRejected()
        {
            StartRace();
            var second = StartRace();
            Assert.That(second.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(second.code, Is.EqualTo("raceActive"));
        }
    }
}
