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
    // Command surface for the driving slice: race.start/restart/exit
    // schemas, phase-specific availability, deep-copy driving state in the
    // shared snapshot, gadget unavailability, and driving input over the
    // same Execute path every caller uses.
    public sealed class DrivingCommandTests
    {
        private const string RunId = "driving-command-test";

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

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "family-racing-driving-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            service = new FoundationService(
                new ProfileStore(directory, new TestJsonSerializer()), "en", RunId);
            service.SetDrivingContent(TestCatalog());
            world = new FlatWorld();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        private static DrivingContentCatalog TestCatalog()
        {
            var course = new CourseDefinition
            {
                id = "course-test",
                checkpoints = new List<CheckpointGate>
                {
                    // Gate 0 is the start/finish line; the second gate sits
                    // far away so command tests never trigger progress.
                    new CheckpointGate { id = "start", x = 0f, y = 0f, z = 0f,
                        halfWidth = 6f, dirX = 0f, dirZ = 1f,
                        recoverX = 0f, recoverZ = -3f },
                    new CheckpointGate { id = "gate-1", x = 0f, y = 0f, z = 2000f,
                        halfWidth = 6f, dirX = 0f, dirZ = 1f,
                        recoverX = 0f, recoverZ = 1997f }
                }
            };
            var intro = new EventDefinition
            {
                id = "intro-test",
                courseId = "course-test",
                laps = 1,
                countdownTicks = 2,
                vehicleIds = new List<string> { "car-01", "motorcycle-01" }
            };
            var practice = new EventDefinition
            {
                id = "practice-test",
                courseId = "course-test",
                laps = 2,
                countdownTicks = 2,
                vehicleIds = new List<string> { "car-01", "motorcycle-01" }
            };
            return DrivingContentCatalog.Create("test-content-1",
                new[] { VehicleTuning.Car01(), VehicleTuning.Motorcycle01() },
                new[] { course },
                new[] { intro, practice });
        }

        private void CreateProfile()
        {
            var result = service.Execute(Cmd("profile.create",
                P("slot", "0", "language", "en")));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied), "setup create failed");
        }

        private CommandResult StartRace(string vehicle = "car-01", string evt = "intro-test",
            string commandId = null)
        {
            return service.Execute(Cmd("race.start",
                P("vehicleId", vehicle, "eventId", evt), commandId));
        }

        private void Boundary(int steps = 1)
        {
            for (var i = 0; i < steps; i++)
            {
                service.StepDriving(world);
                service.AdvanceTick();
            }
        }

        private void StartRunning(string vehicle = "car-01")
        {
            var started = StartRace(vehicle);
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied), "race.start setup failed");
            Boundary(2);
            Assert.That(service.Snapshot().driving.phase, Is.EqualTo("running"));
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

        private static CommandDescriptor Descriptor(FoundationService svc, string action)
        {
            foreach (var descriptor in svc.Discover())
                if (descriptor.action == action) return descriptor;
            Assert.Fail("No descriptor for " + action);
            return null;
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

        // --- schema and availability --------------------------------------------

        [Test]
        public void RaceStartValidatesParameters()
        {
            CreateProfile();
            Assert.That(service.Execute(Cmd("race.start", null)).status,
                Is.EqualTo(CommandResult.Rejected), "missing parameters");
            Assert.That(service.Execute(Cmd("race.start",
                P("vehicleId", "car-01"))).status,
                Is.EqualTo(CommandResult.Rejected), "missing eventId");
            Assert.That(service.Execute(Cmd("race.start",
                P("vehicleId", "car-01", "eventId", "intro-test", "extra", "x"))).status,
                Is.EqualTo(CommandResult.Rejected), "unknown parameter");
            Assert.That(service.Execute(Cmd("race.start",
                P("vehicleId", "spaceship", "eventId", "intro-test"))).status,
                Is.EqualTo(CommandResult.Rejected));
            Assert.That(service.Execute(Cmd("race.start",
                P("vehicleId", "spaceship", "eventId", "intro-test"))).code,
                Is.EqualTo("unknownVehicle"));
            Assert.That(service.Execute(Cmd("race.start",
                P("vehicleId", "car-01", "eventId", "missing-event"))).code,
                Is.EqualTo("unknownEvent"));
        }

        [Test]
        public void RaceStartRequiresSelectedProfile()
        {
            var result = StartRace();
            Assert.That(result.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(result.code, Is.EqualTo("unavailable"));
        }

        [Test]
        public void RaceStartRequiresContent()
        {
            var bare = new FoundationService(
                new ProfileStore(directory + "-bare", new TestJsonSerializer()), "en", RunId);
            bare.Execute(Cmd("profile.create", P("slot", "0", "language", "en")));
            var result = bare.Execute(Cmd("race.start",
                P("vehicleId", "car-01", "eventId", "intro-test")));
            Assert.That(result.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(result.code, Is.EqualTo("contentUnavailable"));
            Assert.That(Descriptor(bare, "race.start").available, Is.False);
            Assert.That(Descriptor(bare, "race.start").reason, Is.EqualTo("contentUnavailable"));
        }

        [Test]
        public void RaceStartAppliesAndExposesDrivingState()
        {
            CreateProfile();
            var started = StartRace();
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot();
            Assert.That(state.screen, Is.EqualTo("driving"));
            Assert.That(state.stateSchemaVersion, Is.EqualTo(4));
            Assert.That(state.driving, Is.Not.Null);
            Assert.That(state.driving.vehicleId, Is.EqualTo("car-01"));
            Assert.That(state.driving.eventId, Is.EqualTo("intro-test"));
            Assert.That(state.driving.courseId, Is.EqualTo("course-test"));
            Assert.That(state.driving.phase, Is.EqualTo("countdown"));
            Assert.That(state.driving.lapsRequired, Is.EqualTo(1));
            Assert.That(state.driving.raceId, Is.Not.Null.And.Not.Empty);
            Assert.That(state.driving.motor, Is.Not.Null);
            Assert.That(state.driving.pendingActions, Is.Not.Null);
            Assert.That(state.driving.checkpoints.Count, Is.EqualTo(2));
            Assert.That(state.drivingEvents.Count, Is.GreaterThanOrEqualTo(1));
            Assert.That(state.drivingEvents[0].kind, Is.EqualTo("race.started"));
            Assert.That(state.drivingEvents[0].commandId, Is.EqualTo(started.commandId));
            Assert.That(state.featureCoverage.drivingSimulation, Is.EqualTo("implemented"));
        }

        [Test]
        public void RaceRestartCreatesNewRaceIdentity()
        {
            CreateProfile();
            StartRunning();
            var first = service.Snapshot().driving.raceId;
            var restarted = service.Execute(Cmd("race.restart", null));
            Assert.That(restarted.status, Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot().driving;
            Assert.That(state.raceId, Is.Not.EqualTo(first),
                "retry must create a new race identity inside the same run");
            Assert.That(state.vehicleId, Is.EqualTo("car-01"));
            Assert.That(state.eventId, Is.EqualTo("intro-test"));
            Assert.That(state.phase, Is.EqualTo("countdown"));
            Assert.That(state.simulationTick, Is.EqualTo(0));
        }

        [Test]
        public void RaceExitLeavesDrivingAndReturnsToPractice()
        {
            CreateProfile();
            StartRunning();
            var exit = service.Execute(Cmd("race.exit", null));
            Assert.That(exit.status, Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot();
            Assert.That(state.driving, Is.Null);
            Assert.That(state.screen, Is.EqualTo("practice"));
            var exited = false;
            foreach (var ev in state.drivingEvents)
                if (ev.kind == "race.exited") exited = true;
            Assert.That(exited, Is.True, "race.exit must emit a correlated race.exited event");
        }

        [Test]
        public void RestartAndExitRejectWithoutActiveRace()
        {
            CreateProfile();
            Assert.That(service.Execute(Cmd("race.restart", null)).code,
                Is.EqualTo("unavailable"));
            Assert.That(service.Execute(Cmd("race.exit", null)).code,
                Is.EqualTo("unavailable"));
        }

        [Test]
        public void DiscoveryReportsPhaseSpecificAvailability()
        {
            var start = Descriptor(service, "race.start");
            Assert.That(start.available, Is.False);
            Assert.That(start.reason, Is.EqualTo("requiresSelectedProfile"));
            Assert.That(Descriptor(service, "race.restart").reason, Is.EqualTo("noActiveRace"));
            Assert.That(Descriptor(service, "race.exit").reason, Is.EqualTo("noActiveRace"));

            CreateProfile();
            Assert.That(Descriptor(service, "race.start").available, Is.True);
            Assert.That(Descriptor(service, "input.set").available, Is.False,
                "input stays sandbox/driving-scoped on menu screens");

            StartRunning();
            Assert.That(Descriptor(service, "race.start").available, Is.False);
            Assert.That(Descriptor(service, "race.start").reason, Is.EqualTo("raceActive"));
            Assert.That(Descriptor(service, "race.restart").available, Is.True);
            Assert.That(Descriptor(service, "race.exit").available, Is.True);
            Assert.That(Descriptor(service, "input.set").available, Is.True);
            Assert.That(Descriptor(service, "pause.toggle").available, Is.True);
        }

        [Test]
        public void RaceStartParametersAdvertised()
        {
            var descriptor = Descriptor(service, "race.start");
            var names = new List<string>();
            foreach (var parameter in descriptor.parameters)
                if (parameter.required) names.Add(parameter.name);
            Assert.That(names, Is.EquivalentTo(new[] { "vehicleId", "eventId" }));
        }

        // --- driving input --------------------------------------------------------

        [Test]
        public void InputSetAllowedDuringDrivingAndGadgetRejected()
        {
            CreateProfile();
            StartRunning();
            var held = service.Execute(Cmd("input.set",
                P("steering", "1", "drift", "true", "boost", "false",
                    "gadget", "false", "recover", "false", "changeCamera", "false",
                    "pause", "false", "durationTicks", "10")));
            Assert.That(held.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(service.Snapshot().input.steering, Is.EqualTo(1f));

            var gadget = service.Execute(Cmd("input.set",
                P("steering", "0", "drift", "false", "boost", "false",
                    "gadget", "true", "recover", "false", "changeCamera", "false",
                    "pause", "false", "durationTicks", "10")));
            Assert.That(gadget.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(gadget.code, Is.EqualTo("unavailable"),
                "gadget actions are explicitly unavailable while driving");
            Assert.That(service.Snapshot().input.steering, Is.EqualTo(1f),
                "the rejected gadget command must not clobber held input");
        }

        [Test]
        public void DrivingSessionMovesVehicleThroughCommands()
        {
            CreateProfile();
            StartRunning();
            var spawn = service.Snapshot().driving.motor;
            service.Execute(Cmd("input.set",
                P("steering", "0", "drift", "false", "boost", "false",
                    "gadget", "false", "recover", "false", "changeCamera", "false",
                    "pause", "false", "durationTicks", "150")));
            Boundary(60);
            var moved = service.Snapshot().driving.motor;
            Assert.That(moved.z, Is.GreaterThan(spawn.z + 5f),
                "auto-acceleration through input leases must move the car");
            Assert.That(moved.speed, Is.GreaterThan(0f));
            service.Execute(Cmd("input.set",
                P("steering", "1", "drift", "false", "boost", "false",
                    "gadget", "false", "recover", "false", "changeCamera", "false",
                    "pause", "false", "durationTicks", "150")));
            Boundary(15);
            var turned = service.Snapshot().driving.motor;
            Assert.That(turned.heading, Is.GreaterThan(moved.heading + 0.5f),
                "steering commands must change the observed heading");
            Assert.That(turned.x, Is.GreaterThan(moved.x + 1f),
                "a right turn must move the car right of its start line");
        }

        [Test]
        public void DrivingSnapshotIsIndependentCopy()
        {
            CreateProfile();
            StartRunning();
            var snapshot = service.Snapshot();
            var raceId = snapshot.driving.raceId;
            snapshot.driving.raceId = "tampered";
            snapshot.driving.motor.x = 999f;
            snapshot.driving.motor.speed = -3f;
            snapshot.driving.phase = "finished";
            snapshot.driving.pendingActions.Add(new PendingActionState { kind = "recover" });
            snapshot.driving.checkpoints.Clear();
            snapshot.drivingEvents.Clear();
            var fresh = service.Snapshot();
            Assert.That(fresh.driving.raceId, Is.EqualTo(raceId));
            Assert.That(fresh.driving.motor.x, Is.EqualTo(0f).Within(0.001f));
            Assert.That(fresh.driving.motor.speed, Is.EqualTo(0f));
            Assert.That(fresh.driving.phase, Is.EqualTo("running"));
            Assert.That(fresh.driving.checkpoints.Count, Is.EqualTo(2));
            Assert.That(fresh.drivingEvents.Count, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void LeavingDrivingRequiresRaceExit()
        {
            CreateProfile();
            StartRunning();
            var nav = service.Execute(Cmd("ui.navigate", P("screen", "settings")));
            Assert.That(nav.status, Is.EqualTo(CommandResult.Rejected),
                "navigation cannot silently abandon a running race");
            Assert.That(service.Snapshot().driving, Is.Not.Null);
            service.Execute(Cmd("race.exit", null));
            var nav2 = service.Execute(Cmd("ui.navigate", P("screen", "settings")));
            Assert.That(nav2.status, Is.EqualTo(CommandResult.Applied));
        }

        [Test]
        public void ProfileSwitchDuringRaceEndsSession()
        {
            CreateProfile();
            service.Execute(Cmd("profile.create", P("slot", "1", "language", "he")));
            StartRunning();
            var selected = service.Execute(Cmd("profile.select", P("slot", "1")));
            Assert.That(selected.status, Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot();
            Assert.That(state.driving, Is.Null,
                "switching profiles ends the running race");
            Assert.That(state.screen, Is.EqualTo("practice"));
            Assert.That(state.activeSlot, Is.EqualTo(1));
        }

        [Test]
        public void MotorcycleEventValidationRejectsWrongVehicle()
        {
            CreateProfile();
            var motoOnly = new EventDefinition
            {
                id = "moto-only",
                courseId = "course-test",
                laps = 1,
                countdownTicks = 1,
                vehicleIds = new List<string> { "motorcycle-01" }
            };
            service.SetDrivingContent(DrivingContentCatalog.Create("v2",
                new[] { VehicleTuning.Car01(), VehicleTuning.Motorcycle01() },
                new[]
                {
                    new CourseDefinition { id = "course-test",
                        checkpoints = new List<CheckpointGate>
                        {
                            new CheckpointGate { id = "start", x = 0f, z = 0f,
                                halfWidth = 6f, dirX = 0f, dirZ = 1f },
                            new CheckpointGate { id = "gate-1", x = 0f, z = 2000f,
                                halfWidth = 6f, dirX = 0f, dirZ = 1f }
                        } }
                },
                new[] { motoOnly }));
            var result = service.Execute(Cmd("race.start",
                P("vehicleId", "car-01", "eventId", "moto-only")));
            Assert.That(result.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(result.code, Is.EqualTo("vehicleNotForEvent"));
            var ok = service.Execute(Cmd("race.start",
                P("vehicleId", "motorcycle-01", "eventId", "moto-only")));
            Assert.That(ok.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(service.Snapshot().driving.vehicleId, Is.EqualTo("motorcycle-01"));
        }

        // Pause is a race affordance: it must never leak past a session
        // boundary into screens that offer no way back out of it.
        [Test]
        public void RaceLifecycleClearsPause()
        {
            CreateProfile();
            StartRunning();
            service.Execute(Cmd("pause.toggle", null));
            Assert.That(service.Snapshot().paused, Is.True, "setup: paused mid-race");

            var exit = service.Execute(Cmd("race.exit", null));
            Assert.That(exit.status, Is.EqualTo(CommandResult.Applied));
            var afterExit = service.Snapshot();
            Assert.That(afterExit.paused, Is.False,
                "race.exit while paused must clear the pause state");
            Assert.That(afterExit.screen, Is.EqualTo("practice"));

            var started = StartRace();
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied));
            Boundary(2);
            var running = service.Snapshot();
            Assert.That(running.driving.phase, Is.EqualTo("running"));
            Assert.That(running.paused, Is.False);
            var sim = running.driving.simulationTick;
            Boundary(3);
            var moved = service.Snapshot().driving;
            Assert.That(moved.simulationTick, Is.GreaterThan(sim),
                "a race started after a paused exit must actually simulate");
            Assert.That(moved.motor.speed, Is.GreaterThan(0f));

            service.Execute(Cmd("pause.toggle", null));
            Assert.That(service.Snapshot().paused, Is.True);
            var restart = service.Execute(Cmd("race.restart", null));
            Assert.That(restart.status, Is.EqualTo(CommandResult.Applied));
            var fresh = service.Snapshot();
            Assert.That(fresh.paused, Is.False,
                "race.restart while paused must clear the pause state");
            Assert.That(fresh.driving.phase, Is.EqualTo("countdown"));
            Boundary(3);
            Assert.That(service.Snapshot().driving.simulationTick,
                Is.GreaterThan(0L), "the restarted race must not be frozen");
        }

        // Known behaviour (documented in COMMANDS.md): input.set still
        // applies during pause — a recover sample latches into
        // pendingActions while the sim is frozen and is consumed at the
        // first post-resume boundary, never silently dropped. Resume via
        // an input.set pause pulse (which does not neutralize the queue)
        // is the consume path; pause.toggle resume cancels instead.
        [Test]
        public void InputSetDuringPauseQueuesAndConsumesAtResume()
        {
            CreateProfile();
            StartRunning();
            service.Execute(Cmd("pause.toggle", null));
            Assert.That(service.Snapshot().paused, Is.True, "setup: paused mid-race");

            var set = service.Execute(Cmd("input.set",
                P("steering", "0", "drift", "false", "boost", "false",
                  "gadget", "false", "recover", "true", "changeCamera", "false",
                  "pause", "false", "durationTicks", "1"), "pause-recover-1"));
            Assert.That(set.status, Is.EqualTo(CommandResult.Applied),
                "input.set applies during pause");
            var paused = service.Snapshot();
            Assert.That(paused.driving.pendingActions.Count, Is.EqualTo(1),
                "the recover press latches into pendingActions");
            Assert.That(paused.driving.pendingActions[0].kind, Is.EqualTo("recover"));
            Assert.That(paused.driving.pendingActions[0].commandId,
                Is.EqualTo("pause-recover-1"));
            var simBefore = paused.driving.simulationTick;

            Boundary(2);
            var still = service.Snapshot();
            Assert.That(still.paused, Is.True);
            Assert.That(still.driving.simulationTick, Is.EqualTo(simBefore),
                "the sim stays frozen while paused");
            Assert.That(still.driving.pendingActions.Count, Is.EqualTo(1),
                "the latch survives unconsumed while paused");

            // Resume with an input.set pause pulse: it toggles paused off
            // without running ClearInput, so the latch is consumed, not
            // cancelled, at the next simulated boundary.
            var resume = service.Execute(Cmd("input.set",
                P("steering", "0", "drift", "false", "boost", "false",
                  "gadget", "false", "recover", "false", "changeCamera", "false",
                  "pause", "true", "durationTicks", "1")));
            Assert.That(resume.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(service.Snapshot().paused, Is.False);
            Boundary(1);
            var after = service.Snapshot();
            Assert.That(after.driving.pendingActions.Count, Is.EqualTo(0),
                "the first post-resume boundary consumes the queued action");
            var recovered = false;
            foreach (var ev in after.drivingEvents)
                if (ev.kind == "race.recovered" && ev.commandId == "pause-recover-1")
                    recovered = true;
            Assert.That(recovered, Is.True,
                "the consumed recover emits a correlated race.recovered event");
        }

        // The sibling resume path: pause.toggle ends with ClearInput,
        // which cancels queued actions (observable as action.cancelled)
        // rather than consuming them — the queue never drives a hidden
        // recovery after a toggle resume.
        [Test]
        public void PauseToggleResumeCancelsQueuedActions()
        {
            CreateProfile();
            StartRunning();
            service.Execute(Cmd("pause.toggle", null));
            service.Execute(Cmd("input.set",
                P("steering", "0", "drift", "false", "boost", "false",
                  "gadget", "false", "recover", "true", "changeCamera", "false",
                  "pause", "false", "durationTicks", "1"), "pause-recover-2"));
            Assert.That(service.Snapshot().driving.pendingActions.Count, Is.EqualTo(1));

            service.Execute(Cmd("pause.toggle", null));
            var after = service.Snapshot();
            Assert.That(after.paused, Is.False);
            Assert.That(after.driving.pendingActions.Count, Is.EqualTo(0),
                "toggle resume cancels the queued action");
            var cancelled = false;
            var recovered = false;
            foreach (var ev in after.drivingEvents)
            {
                if (ev.kind == "action.cancelled" && ev.commandId == "pause-recover-2")
                    cancelled = true;
                if (ev.kind == "race.recovered" && ev.commandId == "pause-recover-2")
                    recovered = true;
            }
            Assert.That(cancelled, Is.True,
                "the cancelled latch emits a correlated action.cancelled event");
            Assert.That(recovered, Is.False,
                "a cancelled queue must never recover on resume");
        }

        // --- optional brake / throttle override (vehicle-physics Task 7) ------

        [Test]
        public void InputSetCarriesOptionalBrakeAndThrottle()
        {
            CreateProfile();
            StartRunning();
            var set = service.Execute(Cmd("input.set",
                P("steering", "0", "drift", "false", "boost", "false",
                  "gadget", "false", "recover", "false", "changeCamera", "false",
                  "pause", "false", "durationTicks", "30",
                  "brake", "0.6", "throttle", "0.4")));
            Assert.That(set.status, Is.EqualTo(CommandResult.Applied), set.code);
            var input = service.Snapshot().input;
            Assert.That(input.brake, Is.EqualTo(0.6f).Within(0.001f));
            Assert.That(input.hasThrottleOverride, Is.True);
            Assert.That(input.throttle, Is.EqualTo(0.4f).Within(0.001f));

            // The held lease keeps carrying the new controls across
            // boundaries until it expires — they must not silently drop.
            Boundary(3);
            var held = service.Snapshot().input;
            Assert.That(held.brake, Is.EqualTo(0.6f).Within(0.001f));
            Assert.That(held.hasThrottleOverride, Is.True);
            Assert.That(held.throttle, Is.EqualTo(0.4f).Within(0.001f));

            for (var i = 0; i < 30; i++) Boundary(1);
            var expired = service.Snapshot().input;
            Assert.That(expired.brake, Is.EqualTo(0f));
            Assert.That(expired.hasThrottleOverride, Is.False,
                "an expired hold restores automatic throttle");
            Assert.That(expired.throttle, Is.EqualTo(1f));
        }

        [Test]
        public void InputSetAbsentThrottleMeansAutomatic()
        {
            CreateProfile();
            StartRunning();
            var set = service.Execute(Cmd("input.set",
                P("steering", "0.2", "drift", "false", "boost", "false",
                  "gadget", "false", "recover", "false", "changeCamera", "false",
                  "pause", "false", "durationTicks", "10")));
            Assert.That(set.status, Is.EqualTo(CommandResult.Applied), set.code);
            var input = service.Snapshot().input;
            Assert.That(input.hasThrottleOverride, Is.False,
                "a missing throttle must stay automatic, not coast");
            Assert.That(input.throttle, Is.EqualTo(1f));
            Assert.That(input.brake, Is.EqualTo(0f));
        }

        [Test]
        public void InputSetExplicitZeroThrottleCoasts()
        {
            CreateProfile();
            StartRunning();
            var set = service.Execute(Cmd("input.set",
                P("steering", "0", "drift", "false", "boost", "false",
                  "gadget", "false", "recover", "false", "changeCamera", "false",
                  "pause", "false", "durationTicks", "20",
                  "throttle", "0")));
            Assert.That(set.status, Is.EqualTo(CommandResult.Applied), set.code);
            var input = service.Snapshot().input;
            Assert.That(input.hasThrottleOverride, Is.True,
                "explicit 0 is a deliberate coast, distinct from absent");
            Assert.That(input.throttle, Is.EqualTo(0f));
        }

        [Test]
        public void InputClearRestoresAutomaticThrottleAndZeroBrake()
        {
            CreateProfile();
            StartRunning();
            service.Execute(Cmd("input.set",
                P("steering", "0", "drift", "false", "boost", "false",
                  "gadget", "false", "recover", "false", "changeCamera", "false",
                  "pause", "false", "durationTicks", "30",
                  "brake", "1", "throttle", "0")));
            var cleared = service.Execute(Cmd("input.clear", null));
            Assert.That(cleared.status, Is.EqualTo(CommandResult.Applied));
            var input = service.Snapshot().input;
            Assert.That(input.brake, Is.EqualTo(0f));
            Assert.That(input.hasThrottleOverride, Is.False);
            Assert.That(input.throttle, Is.EqualTo(1f));
        }

        [Test]
        public void AppFocusLostClearsHeldInputAndInvokesRearmHook()
        {
            CreateProfile();
            StartRunning();
            service.Execute(Cmd("input.set",
                P("steering", "0.8", "drift", "true", "boost", "false",
                  "gadget", "false", "recover", "false", "changeCamera", "false",
                  "pause", "false", "durationTicks", "30",
                  "brake", "0.5", "throttle", "0")));
            // The runtime wires FocusCleanupHook to the physical input
            // pump's RequireRearm; Core proves the hook fires through
            // the same path OnApplicationFocus dispatches.
            var rearmed = 0;
            service.FocusCleanupHook = () => rearmed++;
            var result = service.Execute(Cmd("app.focusLost", null));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied));
            var input = service.Snapshot().input;
            Assert.That(input.steering, Is.EqualTo(0f));
            Assert.That(input.drift, Is.False);
            Assert.That(input.brake, Is.EqualTo(0f));
            Assert.That(input.hasThrottleOverride, Is.False);
            Assert.That(input.throttle, Is.EqualTo(1f));
            Assert.That(rearmed, Is.EqualTo(1));
        }

        [Test]
        public void InputSetRejectsMalformedBrakeThrottleAtomically()
        {
            CreateProfile();
            StartRunning();
            // Marker hold the rejected commands must never clobber.
            var marker = service.Execute(Cmd("input.set",
                P("steering", "0.5", "drift", "false", "boost", "false",
                  "gadget", "false", "recover", "false", "changeCamera", "false",
                  "pause", "false", "durationTicks", "30")));
            Assert.That(marker.status, Is.EqualTo(CommandResult.Applied));

            var recoverablePause = service.Snapshot().paused;
            foreach (var bad in new[] { "-0.2", "1.5", "NaN", "Infinity" })
            {
                var brake = service.Execute(Cmd("input.set",
                    P("steering", "-1", "drift", "true", "boost", "false",
                      "gadget", "false", "recover", "false", "changeCamera",
                      "false", "pause", "false", "durationTicks", "5",
                      "brake", bad)));
                Assert.That(brake.status, Is.EqualTo(CommandResult.Rejected),
                    "brake=" + bad);
                Assert.That(brake.code, Is.EqualTo("invalidParameter"));
                var throttle = service.Execute(Cmd("input.set",
                    P("steering", "-1", "drift", "true", "boost", "false",
                      "gadget", "false", "recover", "false", "changeCamera",
                      "false", "pause", "false", "durationTicks", "5",
                      "throttle", bad)));
                Assert.That(throttle.status, Is.EqualTo(CommandResult.Rejected),
                    "throttle=" + bad);
                Assert.That(throttle.code, Is.EqualTo("invalidParameter"));
            }
            var input = service.Snapshot().input;
            Assert.That(input.steering, Is.EqualTo(0.5f).Within(0.001f),
                "a rejected sample must not partially mutate held input");
            Assert.That(input.drift, Is.False);
            Assert.That(input.brake, Is.EqualTo(0f));
            Assert.That(input.hasThrottleOverride, Is.False);
            Assert.That(service.Snapshot().paused, Is.EqualTo(recoverablePause),
                "rejected input.set must not toggle pause state");
            Assert.That(service.Snapshot().driving.pendingActions, Is.Empty,
                "rejected input.set must not queue a recovery");
        }

        [Test]
        public void InputSetDiscoveryAdvertisesOptionalBrakeThrottle()
        {
            var descriptor = Descriptor(service, "input.set");
            foreach (var parameter in descriptor.parameters)
            {
                if (parameter.name == "brake" || parameter.name == "throttle")
                {
                    Assert.That(parameter.type, Is.EqualTo("decimal"));
                    Assert.That(parameter.required, Is.False,
                        parameter.name + " must be optional — absent means "
                        + "no brake / automatic throttle");
                }
                else
                {
                    Assert.That(parameter.required, Is.True,
                        parameter.name + " stays a required legacy parameter");
                }
            }
            var names = new List<string>();
            foreach (var parameter in descriptor.parameters)
                names.Add(parameter.name);
            Assert.That(names, Does.Contain("brake"));
            Assert.That(names, Does.Contain("throttle"));
        }
    }
}
