using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Localization;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;
using FamilyRacing.Core.Tutorial;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // Onboarding slice: ordered steering -> drift -> boost -> jump ->
    // recovery lessons that advance only from genuine driving events,
    // cue replay without progression, indefinite retry, phase-aware
    // tutorial.* commands, and graduation that unlocks and then completes
    // only on the real one-lap introductory finish. No tutorial.complete
    // or checkpoint-teleport command exists, and no setup/fixture command
    // can manufacture lesson progress.
    public sealed class TutorialTests
    {
        private const string RunId = "tutorial-test";

        private string directory;
        private FoundationService service;
        private DropWorld world;

        // Flat ground with a raised plateau at z in [30, 55]: driving up
        // the step is a climb, driving off the far edge is a real
        // becameAirborne -> landed episode for the jump lesson.
        private sealed class DropWorld : IMotorWorld
        {
            public GroundHit ProbeGround(MotorPose pose, float probeDistance)
            {
                var top = pose.z >= 30f && pose.z <= 55f ? 1f : 0f;
                var hit = pose.y <= top + probeDistance;
                return new GroundHit
                {
                    hit = hit,
                    x = pose.x,
                    y = top,
                    z = pose.z,
                    normalY = 1f,
                    distance = pose.y - top,
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
                "family-racing-tutorial-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            service = new FoundationService(
                new ProfileStore(directory, new TestJsonSerializer()), "en", RunId);
            service.SetDrivingContent(TestCatalog());
            service.SetTutorialDefinition(TestTutorial());
            world = new DropWorld();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        // Small loop: the start/finish plane accepts only -z crossings so
        // the introductory trial needs one real out-and-back lap; the
        // plateau edge supplies the jump the lessons need.
        private static CourseDefinition TestCourse()
        {
            return new CourseDefinition
            {
                id = "course-test",
                spawnZ = -10f,
                checkpoints = new List<CheckpointGate>
                {
                    new CheckpointGate { id = "start", x = 0f, z = 0f,
                        halfWidth = 8f, dirX = 0f, dirZ = -1f,
                        recoverX = 0f, recoverZ = 3f, recoverHeading = (float)Math.PI },
                    new CheckpointGate { id = "gate-1", x = 0f, z = 40f,
                        halfWidth = 8f, dirX = 0f, dirZ = 1f,
                        recoverX = 0f, recoverZ = 37f }
                }
            };
        }

        private static EventDefinition FreeEvent()
        {
            return new EventDefinition
            {
                id = "tutorial-test",
                courseId = "course-test",
                laps = 99,
                countdownTicks = 2,
                vehicleIds = new List<string> { "car-01", "motorcycle-01" }
            };
        }

        private static EventDefinition IntroEvent(bool motorcycleOnly = false)
        {
            return new EventDefinition
            {
                id = "intro-test",
                courseId = "course-test",
                laps = 1,
                countdownTicks = 2,
                vehicleIds = motorcycleOnly
                    ? new List<string> { "motorcycle-01" }
                    : new List<string> { "car-01", "motorcycle-01" }
            };
        }

        private static EventDefinition PracticeEvent()
        {
            return new EventDefinition
            {
                id = "practice-test",
                courseId = "course-test",
                laps = 2,
                countdownTicks = 2,
                vehicleIds = new List<string> { "car-01", "motorcycle-01" }
            };
        }

        private static DrivingContentCatalog CatalogWith(
            params EventDefinition[] events)
        {
            return DrivingContentCatalog.Create("test-content-1",
                new[] { VehicleTuning.Car01(), VehicleTuning.Motorcycle01() },
                new[] { TestCourse() }, events);
        }

        private static DrivingContentCatalog TestCatalog()
        {
            return CatalogWith(FreeEvent(), IntroEvent(), PracticeEvent());
        }

        // The free-drive stage is present but the trial the lessons
        // unlock cannot be supplied — the failure-path fixtures.
        private static DrivingContentCatalog CatalogWithoutTrial()
        {
            return CatalogWith(FreeEvent(), PracticeEvent());
        }

        private static DrivingContentCatalog CatalogWithoutFreeDrive()
        {
            return CatalogWith(IntroEvent(), PracticeEvent());
        }

        private static DrivingContentCatalog CatalogWithMotoOnlyTrial()
        {
            return CatalogWith(FreeEvent(), IntroEvent(motorcycleOnly: true),
                PracticeEvent());
        }

        private static TutorialDefinition TestTutorial()
        {
            return new TutorialDefinition
            {
                tutorialId = "tutorial-test-def",
                freeDriveEventId = "tutorial-test",
                trialEventId = "intro-test",
                lessons = new List<TutorialLesson>
                {
                    new TutorialLesson
                    {
                        lessonId = "steering",
                        rule = "steerTicks",
                        requiredSteerTicks = 20,
                        minSteering = 0.25f,
                        cue = Cue("tutorial.cue.steering", "<>", "steer-en", "steer-he")
                    },
                    new TutorialLesson
                    {
                        lessonId = "brake",
                        rule = "brakeTicks",
                        requiredBrakeTicks = 15,
                        minBrake = 0.5f,
                        cue = Cue("tutorial.cue.brake", "!", "brake-en", "brake-he")
                    },
                    new TutorialLesson
                    {
                        lessonId = "drift",
                        rule = "event",
                        eventKind = "motor.driftStart",
                        cue = Cue("tutorial.cue.drift", "~", "drift-en", "drift-he")
                    },
                    new TutorialLesson
                    {
                        lessonId = "boost",
                        rule = "event",
                        eventKind = "motor.boostStart",
                        cue = Cue("tutorial.cue.boost", ">>", "boost-en", "boost-he")
                    },
                    new TutorialLesson
                    {
                        lessonId = "jump",
                        rule = "event",
                        eventKind = "motor.landed",
                        cue = Cue("tutorial.cue.jump", "^", "jump-en", "jump-he")
                    },
                    new TutorialLesson
                    {
                        lessonId = "recovery",
                        rule = "event",
                        eventKind = "race.recovered",
                        cue = Cue("tutorial.cue.recovery", "o", "rec-en", "rec-he")
                    }
                },
                trialCue = Cue("tutorial.cue.trial", "*", "trial-en", "trial-he"),
                finishedCue = Cue("tutorial.done", "*", "done-en", "done-he")
            };
        }

        private static TutorialCue Cue(string key, string icon, string en, string he)
        {
            return new TutorialCue
            {
                textKey = key,
                icon = icon,
                audioEn = en,
                audioHe = he
            };
        }

        private void CreateProfile(string language = "en")
        {
            var result = service.Execute(Cmd("profile.create",
                P("slot", "0", "language", language)));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied), "setup create failed");
        }

        private CommandResult StartTutorial(string vehicle = "car-01")
        {
            return service.Execute(Cmd("tutorial.start", P("vehicleId", vehicle)));
        }

        private void Boundary(int steps = 1)
        {
            for (var i = 0; i < steps; i++)
            {
                service.StepDriving(world);
                service.AdvanceTick();
            }
        }

        // tutorial.start through the shared command path plus the
        // countdown, leaving the session in the running phase.
        private void StartTutorialRunning(string vehicle = "car-01")
        {
            var started = StartTutorial(vehicle);
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied),
                "tutorial.start setup failed: " + started.code);
            Boundary(2);
            var state = service.Snapshot().driving;
            Assert.That(state.phase, Is.EqualTo("running"), "setup: countdown must end");
        }

        private CommandResult Drive(float steering, bool drift = false,
            bool boost = false, bool recover = false, int ticks = 5,
            float brake = 0f)
        {
            var result = service.Execute(Cmd("input.set",
                P("steering", steering.ToString("0.###", CultureInfo.InvariantCulture),
                    "drift", drift ? "true" : "false",
                    "boost", boost ? "true" : "false",
                    "gadget", "false",
                    "recover", recover ? "true" : "false",
                    "changeCamera", "false", "pause", "false",
                    "brake", brake.ToString("0.###", CultureInfo.InvariantCulture),
                    "durationTicks", ticks.ToString(CultureInfo.InvariantCulture))));
            if (result.status == CommandResult.Applied) Boundary(ticks);
            return result;
        }

        // Pursue a waypoint through the real input.set command path —
        // the same loop the PlayMode traversal uses against real physics.
        private void Pursue(float targetX, float targetZ, int maxTicks = 4000)
        {
            var state = service.Snapshot().driving;
            for (var t = 0; t < maxTicks;)
            {
                var dx = targetX - state.motor.x;
                var dz = targetZ - state.motor.z;
                if (dx * dx + dz * dz < 9f) return;
                var desired = (float)Math.Atan2(dx, dz);
                var delta = WrapAngle(desired - state.motor.heading);
                var steer = Math.Clamp(delta * 3f, -1f, 1f);
                Drive(steer, ticks: 5);
                t += 5;
                state = service.Snapshot().driving;
                if (state.phase == "finished") return;
            }
            Assert.Fail("pursuit did not reach " + targetX + "," + targetZ
                + " (stopped at " + state.motor.x + "," + state.motor.z + ")");
        }

        private static float WrapAngle(float radians)
        {
            while (radians > Math.PI) radians -= (float)(Math.PI * 2);
            while (radians <= -Math.PI) radians += (float)(Math.PI * 2);
            return radians;
        }

        // Drives onto the raised plateau (climbing whichever edge the
        // approach hits) then off the far edge: a real airborne landing
        // wherever the vehicle happened to be when the jump lesson began.
        private void CompleteJumpLesson()
        {
            Pursue(0f, 45f);   // climb onto the plateau
            Pursue(0f, 72f);   // off the far edge -> becameAirborne -> landed
            Drive(0f, ticks: 15); // settle: the landing must actually commit
        }

        // Drives one genuine lap of the test course after the gate-1
        // crossing: out past the plateau, a U-turn, back across the
        // start/finish plane travelling -z.
        private void FinishIntroLap()
        {
            Pursue(0f, 48f);   // through gate-1 (+z)
            Pursue(0f, 70f);   // off the plateau, room to turn
            Pursue(12f, 40f);  // swing wide so the line crossing stays -z
            Pursue(0f, -6f);   // cross the start plane going -z -> finish
        }

        private TutorialState Tutorial()
        {
            var driving = service.Snapshot().driving;
            return driving != null ? driving.tutorial : null;
        }

        private static List<DrivingEvent> Events(FoundationState state, string kind)
        {
            var found = new List<DrivingEvent>();
            foreach (var ev in state.drivingEvents)
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

        // --- command surface -------------------------------------------

        [Test]
        public void TutorialStartRequiresProfileContentAndValidVehicle()
        {
            var result = StartTutorial();
            Assert.That(result.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(result.code, Is.EqualTo("unavailable"),
                "a selected profile is required");

            var bare = new FoundationService(
                new ProfileStore(directory + "-bare", new TestJsonSerializer()), "en", RunId);
            bare.SetDrivingContent(TestCatalog());
            bare.Execute(Cmd("profile.create", P("slot", "0", "language", "en")));
            var noDef = bare.Execute(Cmd("tutorial.start", P("vehicleId", "car-01")));
            Assert.That(noDef.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(noDef.code, Is.EqualTo("contentUnavailable"),
                "without a validated tutorial definition nothing starts");
            Assert.That(Descriptor(bare, "tutorial.start").reason,
                Is.EqualTo("contentUnavailable"));

            CreateProfile();
            Assert.That(service.Execute(Cmd("tutorial.start", null)).code,
                Is.EqualTo("missingParameter"));
            Assert.That(service.Execute(Cmd("tutorial.start",
                P("vehicleId", "spaceship"))).code, Is.EqualTo("unknownVehicle"));
        }

        [Test]
        public void TutorialStartAppliesAndPublishesFirstCue()
        {
            CreateProfile();
            var started = StartTutorial();
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot();
            Assert.That(state.screen, Is.EqualTo("driving"));
            Assert.That(state.driving, Is.Not.Null);
            Assert.That(state.driving.eventId, Is.EqualTo("tutorial-test"),
                "lessons run inside the free-drive tutorial event");
            var tutorial = state.driving.tutorial;
            Assert.That(tutorial, Is.Not.Null);
            Assert.That(tutorial.phase, Is.EqualTo("lessons"));
            Assert.That(tutorial.lessonId, Is.EqualTo("steering"));
            Assert.That(tutorial.lessonIndex, Is.EqualTo(0));
            Assert.That(tutorial.lessonTotal, Is.EqualTo(6));
            Assert.That(tutorial.objective, Is.EqualTo("tutorial.cue.steering"));
            Assert.That(tutorial.cueId, Is.EqualTo("steering"));
            Assert.That(tutorial.graduated, Is.False);
            Assert.That(tutorial.trialEventId, Is.EqualTo("intro-test"));
            var begun = Events(state, "tutorial.started");
            Assert.That(begun.Count, Is.EqualTo(1));
            Assert.That(begun[0].commandId, Is.EqualTo(started.commandId));
        }

        [Test]
        public void RaceCommandsStayMutuallyExclusiveWithTutorial()
        {
            CreateProfile();
            StartTutorialRunning();
            Assert.That(service.Execute(Cmd("race.start",
                P("vehicleId", "car-01", "eventId", "intro-test"))).code,
                Is.EqualTo("raceActive"));
            Assert.That(service.Execute(Cmd("tutorial.start",
                P("vehicleId", "car-01"))).code, Is.EqualTo("raceActive"));

            service.Execute(Cmd("tutorial.exit", null));
            var race = service.Execute(Cmd("race.start",
                P("vehicleId", "car-01", "eventId", "intro-test")));
            Assert.That(race.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(service.Execute(Cmd("tutorial.start",
                P("vehicleId", "car-01"))).code, Is.EqualTo("raceActive"));
        }

        // --- ordered lessons ---------------------------------------------

        [TestCase("car-01")]
        [TestCase("motorcycle-01")]
        public void OrderedLessonsAdvanceOnlyFromGenuineDrivingEvents(string vehicle)
        {
            CreateProfile();
            StartTutorialRunning(vehicle);
            Assert.That(Tutorial().lessonId, Is.EqualTo("steering"));

            // Steering: sustained held steering for the required ticks.
            Drive(0.8f, ticks: 10);
            Assert.That(Tutorial().lessonId, Is.EqualTo("steering"),
                "partial steering does not complete the lesson");
            Drive(0.8f, ticks: 10);
            Assert.That(Tutorial().lessonId, Is.EqualTo("brake"));
            Assert.That(Tutorial().completedLessons,
                Is.EquivalentTo(new[] { "steering" }));

            // Braking: sustained real brake demand for the required
            // ticks — the same held control a child holds on touch or S.
            Drive(0f, brake: 0.8f, ticks: 10);
            Assert.That(Tutorial().lessonId, Is.EqualTo("brake"),
                "a partial brake hold does not complete the lesson");
            Drive(0f, brake: 0.8f, ticks: 5);
            Assert.That(Tutorial().lessonId, Is.EqualTo("drift"));

            // Drift: a real driftStart at speed. The brake lesson left
            // the car slow, so build speed again before leaning in.
            Drive(0f, ticks: 40);
            Drive(0.9f, drift: true, ticks: 30);
            Assert.That(Tutorial().lessonId, Is.EqualTo("boost"),
                "drift lesson must advance on a genuine driftStart");

            // Boost: a real boostStart.
            Drive(0f, boost: true, ticks: 10);
            Assert.That(Tutorial().lessonId, Is.EqualTo("jump"));

            // Jump: drive over the plateau edge for a real airborne
            // landing — teleporting or fixture state cannot produce it.
            CompleteJumpLesson();
            Assert.That(Tutorial().lessonId, Is.EqualTo("recovery"),
                "landing off the plateau edge must complete the jump lesson");

            // Recovery: the real recovery pending action resolves on a
            // simulation boundary into race.recovered.
            var recovered = service.Execute(Cmd("input.set",
                P("steering", "0", "drift", "false", "boost", "false",
                    "gadget", "false", "recover", "true", "changeCamera", "false",
                    "pause", "false", "durationTicks", "5")));
            Assert.That(recovered.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(Tutorial().lessonId, Is.EqualTo("recovery"),
                "the latched command alone must not complete the lesson");
            Boundary(1);
            Assert.That(Tutorial().phase, Is.EqualTo("trial"),
                "all lessons complete -> the one-lap trial unlocks");

            // The trial is a new race identity on the introductory event.
            var trial = service.Snapshot().driving;
            Assert.That(trial.eventId, Is.EqualTo("intro-test"));
            Assert.That(trial.phase, Is.EqualTo("countdown"));
            var tutorialState = trial.tutorial;
            Assert.That(tutorialState.objective, Is.EqualTo("tutorial.cue.trial"));
            Boundary(2);
            Assert.That(service.Snapshot().driving.phase, Is.EqualTo("running"));

            FinishIntroLap();
            var done = Tutorial();
            Assert.That(service.Snapshot().driving.phase, Is.EqualTo("finished"));
            Assert.That(done.phase, Is.EqualTo("graduated"));
            Assert.That(done.graduated, Is.True);
            Assert.That(done.objective, Is.EqualTo("tutorial.done"));
            Assert.That(Events(service.Snapshot(), "tutorial.graduated").Count,
                Is.EqualTo(1));
        }

        [Test]
        public void OutOfOrderEventsDoNotBankForLaterLessons()
        {
            CreateProfile();
            StartTutorialRunning();
            // Drift and boost before the steering lesson completes: the
            // events are genuine but must not be banked for later lessons.
            // Build speed neutrally first so the brief drift actually
            // engages while the steering count stays incomplete.
            Drive(0f, ticks: 40);
            Drive(0.9f, drift: true, ticks: 10);
            Drive(0f, boost: true, ticks: 10);
            Drive(0f, brake: 0.8f, ticks: 10);
            Assert.That(Tutorial().lessonId, Is.EqualTo("steering"),
                "drift/boost/brake must not skip the ordered steering lesson");
            Drive(0.8f, ticks: 10);
            Assert.That(Tutorial().lessonId, Is.EqualTo("brake"),
                "steering completes on its own merits");
            // The earlier brake hold was ignored too: tick progress does
            // not bank, so 14 ticks short of the requirement stays
            // incomplete and only the full fresh hold advances.
            Drive(0f, brake: 0.8f, ticks: 14);
            Assert.That(Tutorial().lessonId, Is.EqualTo("brake"),
                "early brake ticks must not bank for the brake lesson");
            Drive(0f, brake: 0.8f, ticks: 1);
            Assert.That(Tutorial().lessonId, Is.EqualTo("drift"));
            // The earlier driftStart was ignored: a fresh one is required.
            // The brake lesson left the car slow, so rebuild speed first.
            Drive(0f, ticks: 40);
            Drive(0.9f, drift: true, ticks: 30);
            Assert.That(Tutorial().lessonId, Is.EqualTo("boost"));
            // The earlier boostStart was ignored too.
            Drive(0f, boost: true, ticks: 10);
            Assert.That(Tutorial().lessonId, Is.EqualTo("jump"));
        }

        [Test]
        public void ReplayRepeatsCueWithoutAdvancing()
        {
            CreateProfile();
            StartTutorialRunning();
            var before = Tutorial();
            var sequence = before.cueSequence;
            for (var i = 0; i < 3; i++)
            {
                var replay = service.Execute(Cmd("tutorial.replay", null));
                Assert.That(replay.status, Is.EqualTo(CommandResult.Applied));
            }
            var after = Tutorial();
            Assert.That(after.cueSequence, Is.EqualTo(sequence + 3),
                "each replay re-issues the same cue");
            Assert.That(after.lessonId, Is.EqualTo(before.lessonId));
            Assert.That(after.lessonIndex, Is.EqualTo(before.lessonIndex));
            Assert.That(after.phase, Is.EqualTo(before.phase));
            var cues = Events(service.Snapshot(), "tutorial.cue");
            Assert.That(cues.Count, Is.EqualTo(3));
            // The lesson still completes normally afterwards.
            Drive(0.8f, ticks: 20);
            Assert.That(Tutorial().lessonId, Is.EqualTo("brake"));
        }

        [Test]
        public void RestartRetriesIndefinitelyWithoutLosingProgress()
        {
            CreateProfile();
            StartTutorialRunning();
            Drive(0.8f, ticks: 20); // steering done
            Assert.That(Tutorial().lessonId, Is.EqualTo("brake"));

            var firstRace = service.Snapshot().driving.raceId;
            var restarted = service.Execute(Cmd("race.restart", null));
            Assert.That(restarted.status, Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot().driving;
            Assert.That(state.raceId, Is.Not.EqualTo(firstRace));
            Assert.That(state.eventId, Is.EqualTo("tutorial-test"),
                "retry stays inside the tutorial free-drive");
            Assert.That(state.tutorial, Is.Not.Null);
            Assert.That(state.tutorial.lessonId, Is.EqualTo("brake"),
                "completed lessons are kept across retry");
            Assert.That(state.tutorial.completedLessons,
                Is.EquivalentTo(new[] { "steering" }));

            // Indefinite: another retry keeps the same guarantee.
            var again = service.Execute(Cmd("race.restart", null));
            Assert.That(again.status, Is.EqualTo(CommandResult.Applied));
            Assert.That(Tutorial().lessonId, Is.EqualTo("brake"));
        }

        [Test]
        public void RestartDuringTrialRerunsTheIntroductoryLap()
        {
            CreateProfile();
            StartTutorialRunning();
            Drive(0.8f, ticks: 20);
            Drive(0f, brake: 0.8f, ticks: 15);
            Drive(0f, ticks: 40); // rebuild speed after braking
            Drive(0.9f, drift: true, ticks: 30);
            Drive(0f, boost: true, ticks: 10);
            CompleteJumpLesson();
            var recover = service.Execute(Cmd("input.set",
                P("steering", "0", "drift", "false", "boost", "false",
                    "gadget", "false", "recover", "true", "changeCamera", "false",
                    "pause", "false", "durationTicks", "5")));
            Assert.That(recover.status, Is.EqualTo(CommandResult.Applied));
            Boundary(1);
            Assert.That(Tutorial().phase, Is.EqualTo("trial"));
            Assert.That(service.Snapshot().driving.eventId, Is.EqualTo("intro-test"));

            var restarted = service.Execute(Cmd("race.restart", null));
            Assert.That(restarted.status, Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot().driving;
            Assert.That(state.eventId, Is.EqualTo("intro-test"));
            Assert.That(state.tutorial.phase, Is.EqualTo("trial"),
                "retry during the trial re-runs the one-lap event");
        }

        [Test]
        public void TutorialExitEmitsDistinctReason()
        {
            CreateProfile();
            StartTutorialRunning();
            var exited = service.Execute(Cmd("tutorial.exit", null));
            Assert.That(exited.status, Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot();
            var tutorialExited = false;
            var raceExited = false;
            var raceEnded = false;
            foreach (var ev in state.drivingEvents)
            {
                if (ev.kind == "tutorial.exited")
                {
                    tutorialExited = true;
                    Assert.That(ev.detail, Is.EqualTo("tutorialExit"),
                        "an operator tutorial.exit must carry its own "
                        + "reason token, not the generic raceExit");
                }
                if (ev.kind == "race.exited") raceExited = true;
                if (ev.kind == "race.ended") raceEnded = true;
            }
            Assert.That(tutorialExited, Is.True);
            Assert.That(raceExited, Is.True,
                "the operator exit still ends the session as race.exited");
            Assert.That(raceEnded, Is.False,
                "an operator exit is not a lifecycle race.ended");
        }

        [Test]
        public void TutorialExitAndRaceExitEndTheSession()
        {
            CreateProfile();
            StartTutorialRunning();
            var exited = service.Execute(Cmd("tutorial.exit", null));
            Assert.That(exited.status, Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot();
            Assert.That(state.screen, Is.EqualTo("practice"));
            Assert.That(state.driving, Is.Null);
            Assert.That(Events(state, "tutorial.exited").Count, Is.EqualTo(1));
            Assert.That(Descriptor(service, "tutorial.replay").available, Is.False);
            Assert.That(Descriptor(service, "tutorial.exit").reason,
                Is.EqualTo("noActiveTutorial"));

            // race.exit during a tutorial ends it the same honest way.
            StartTutorialRunning();
            var raceExit = service.Execute(Cmd("race.exit", null));
            Assert.That(raceExit.status, Is.EqualTo(CommandResult.Applied));
            var after = service.Snapshot();
            Assert.That(after.driving, Is.Null);
            Assert.That(after.screen, Is.EqualTo("practice"));
            Assert.That(Descriptor(service, "tutorial.replay").available, Is.False);
        }

        [Test]
        public void ProfileSwitchDuringTutorialEndsItSessionLocally()
        {
            CreateProfile();
            service.Execute(Cmd("profile.create", P("slot", "1", "language", "he")));
            StartTutorialRunning();
            var selected = service.Execute(Cmd("profile.select", P("slot", "1")));
            Assert.That(selected.status, Is.EqualTo(CommandResult.Applied));
            var state = service.Snapshot();
            Assert.That(state.driving, Is.Null, "profile switch ends the tutorial");
            Assert.That(state.screen, Is.EqualTo("practice"));
        }

        // --- nothing but genuine driving may advance --------------------

        [Test]
        public void NoFixtureCommandCanAdvanceOrCompleteLessons()
        {
            CreateProfile();
            StartTutorialRunning();
            // There is no tutorial.complete command and no checkpoint
            // teleport: both are rejected as unknown actions.
            Assert.That(service.Execute(Cmd("tutorial.complete", null)).code,
                Is.EqualTo("unknownAction"));
            Assert.That(service.Execute(Cmd("tutorial.teleport",
                P("checkpoint", "gate-1"))).code, Is.EqualTo("unknownAction"));

            // Setup/fixture commands change nothing about the lesson.
            service.Execute(Cmd("settings.update",
                P("language", "en", "camera", "overhead", "reducedCameraShake", "false",
                    "musicVolume", "0.5", "effectsVolume", "0.5", "voiceVolume", "0.5")));
            service.Execute(Cmd("camera.toggle", null));
            service.Execute(Cmd("input.clear", null));
            service.Execute(Cmd("pause.toggle", null));
            service.Execute(Cmd("pause.toggle", null));
            Assert.That(Tutorial().lessonId, Is.EqualTo("steering"));
            Assert.That(Tutorial().lessonIndex, Is.EqualTo(0));

            // Crossing a real checkpoint cannot satisfy a lesson either:
            // drive through gate-1 while the steering lesson is current.
            Pursue(0f, 48f);
            Assert.That(Tutorial().lessonId, Is.EqualTo("steering"),
                "checkpoint crossings never manufacture lesson progress");
        }

        [Test]
        public void TrialGraduatesOnlyOnGenuineFinish()
        {
            CreateProfile();
            StartTutorialRunning();
            Drive(0.8f, ticks: 20);
            Drive(0f, brake: 0.8f, ticks: 15);
            Drive(0f, ticks: 40); // rebuild speed after braking
            Drive(0.9f, drift: true, ticks: 30);
            Drive(0f, boost: true, ticks: 10);
            CompleteJumpLesson();
            service.Execute(Cmd("input.set",
                P("steering", "0", "drift", "false", "boost", "false",
                    "gadget", "false", "recover", "true", "changeCamera", "false",
                    "pause", "false", "durationTicks", "5")));
            Boundary(1);
            Assert.That(Tutorial().phase, Is.EqualTo("trial"));
            Boundary(2); // trial countdown

            // Crossing gate-1 alone is not graduation.
            Pursue(0f, 50f);
            Assert.That(Tutorial().phase, Is.EqualTo("trial"),
                "a checkpoint crossing must not graduate the trial");
            FinishIntroLap();
            Assert.That(Tutorial().phase, Is.EqualTo("graduated"));
        }

        // --- snapshots / locales ----------------------------------------

        [Test]
        public void TutorialSnapshotIsAnIndependentCopy()
        {
            CreateProfile();
            StartTutorialRunning();
            var snapshot = service.Snapshot();
            snapshot.driving.tutorial.phase = "tampered";
            snapshot.driving.tutorial.lessonId = "tampered";
            snapshot.driving.tutorial.completedLessons.Clear();
            snapshot.driving.tutorial.cueSequence = 999;
            var fresh = Tutorial();
            Assert.That(fresh.phase, Is.EqualTo("lessons"));
            Assert.That(fresh.lessonId, Is.EqualTo("steering"));
            Assert.That(fresh.completedLessons, Is.Empty);
            Assert.That(fresh.cueSequence, Is.LessThan(999));
        }

        [Test]
        public void HebrewTutorialUsesSameSemanticsInLogicalOrder()
        {
            CreateProfile("he");
            var started = StartTutorial();
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied));
            var tutorial = Tutorial();
            Assert.That(tutorial.objective, Is.EqualTo("tutorial.cue.steering"));
            // Every cue resolves in both locales; Hebrew stays logical-order.
            foreach (var key in new[]
                { "tutorial.cue.steering", "tutorial.cue.brake",
                  "tutorial.cue.drift", "tutorial.cue.boost",
                  "tutorial.cue.jump", "tutorial.cue.recovery", "tutorial.cue.trial",
                  "tutorial.done" })
            {
                Assert.That(ShellText.Get(key, "en"), Is.Not.Empty);
                Assert.That(ShellText.Get(key, "he"), Is.Not.Empty);
            }
            Boundary(2);
            Drive(0.8f, ticks: 20);
            Assert.That(Tutorial().lessonId, Is.EqualTo("brake"),
                "lessons advance identically under Hebrew");
        }

        [Test]
        public void BrakeTicksCountsHeldBrakePerBoundary()
        {
            // Unit-level rule check: brakeTicks accumulates only on fresh
            // simulated boundaries while running, only when the published
            // input holds brake at or above minBrake — a stale tick or a
            // feather touch never advances the lesson.
            var def = TestTutorial();
            var steering = def.lessons[0];
            var brake = def.lessons[1];
            var tutorial = new DrivingTutorial(def);
            var state = new DrivingSessionState
            {
                phase = "running",
                simulationTick = 1,
                input = new InputState { brake = 0.8f }
            };
            // Lesson 0 is steering: brake input does not count for it.
            var advance = tutorial.Advance(state, null);
            Assert.That(advance.completedLessonId, Is.Null);

            // Complete steering, then exercise the brake lesson directly.
            state.input.steering = 0.8f;
            state.input.brake = 0f;
            for (var t = 0; t < steering.requiredSteerTicks; t++)
            {
                state.simulationTick++;
                advance = tutorial.Advance(state, null);
            }
            Assert.That(advance.completedLessonId, Is.EqualTo("steering"));
            Assert.That(tutorial.CurrentLessonId, Is.EqualTo("brake"));

            state.input.steering = 0f;
            // Below the threshold does not count.
            state.input.brake = brake.minBrake - 0.01f;
            for (var t = 0; t < brake.requiredBrakeTicks; t++)
            {
                state.simulationTick++;
                advance = tutorial.Advance(state, null);
                Assert.That(advance.completedLessonId, Is.Null);
            }
            // A re-presented boundary (same tick) counts at most once:
            // the first advance on a fresh tick counts, the repeat does not.
            state.input.brake = brake.minBrake;
            state.simulationTick++;
            advance = tutorial.Advance(state, null);
            advance = tutorial.Advance(state, null);
            Assert.That(advance.completedLessonId, Is.Null,
                "re-advancing the same tick must not double-count");
            for (var t = 1; t < brake.requiredBrakeTicks; t++)
            {
                state.simulationTick++;
                advance = tutorial.Advance(state, null);
            }
            Assert.That(advance.completedLessonId, Is.EqualTo("brake"));
            Assert.That(tutorial.CurrentLessonId, Is.EqualTo("drift"));
        }

        [Test]
        public void DefinitionValidationRejectsMalformedLessons()
        {
            var def = TestTutorial();
            Assert.That(def.Validate(out _), Is.True);

            var duplicate = TestTutorial();
            duplicate.lessons[1].lessonId = "steering";
            Assert.That(duplicate.Validate(out var dup), Is.False);
            Assert.That(dup, Does.Contain("duplicate"));

            var unknown = TestTutorial();
            unknown.lessons[0].cue.textKey = "tutorial.cue.missing";
            Assert.That(unknown.Validate(out var key), Is.False);
            Assert.That(key, Does.Contain("textKey"));

            var rule = TestTutorial();
            rule.lessons[0].rule = "magic";
            Assert.That(rule.Validate(out var bad), Is.False);

            // brakeTicks obeys the same fail-closed validation as
            // steerTicks: a non-positive requirement or an out-of-unit
            // threshold can never author a lesson.
            var noTicks = TestTutorial();
            noTicks.lessons[1].requiredBrakeTicks = 0;
            Assert.That(noTicks.lessons[1].rule, Is.EqualTo("brakeTicks"));
            Assert.That(noTicks.Validate(out var ticks), Is.False);
            Assert.That(ticks, Does.Contain("requiredBrakeTicks"));
            var badMin = TestTutorial();
            badMin.lessons[1].minBrake = 1.5f;
            Assert.That(badMin.Validate(out var min), Is.False);
            Assert.That(min, Does.Contain("minBrake"));

            var empty = TestTutorial();
            empty.lessons.Clear();
            Assert.That(empty.Validate(out _), Is.False);
        }

        [Test]
        public void AvailabilityIsPhaseAware()
        {
            Assert.That(Descriptor(service, "tutorial.start").reason,
                Is.EqualTo("requiresSelectedProfile"));
            Assert.That(Descriptor(service, "tutorial.replay").available, Is.False);
            Assert.That(Descriptor(service, "tutorial.replay").reason,
                Is.EqualTo("noActiveTutorial"));
            Assert.That(Descriptor(service, "tutorial.exit").reason,
                Is.EqualTo("noActiveTutorial"));

            CreateProfile();
            Assert.That(Descriptor(service, "tutorial.start").available, Is.True);
            StartTutorialRunning();
            Assert.That(Descriptor(service, "tutorial.replay").available, Is.True);
            Assert.That(Descriptor(service, "tutorial.exit").available, Is.True);
            Assert.That(Descriptor(service, "tutorial.start").available, Is.False);
            Assert.That(Descriptor(service, "tutorial.start").reason,
                Is.EqualTo("raceActive"));

            // The driving controls list advertises the semantic tutorial
            // actions while a tutorial is active.
            var controls = service.Snapshot().driving.controls;
            Assert.That(controls, Does.Contain("tutorial.replay"));
            Assert.That(controls, Does.Contain("tutorial.exit"));
        }

        // --- fail-closed tutorial content -------------------------------

        [Test]
        public void TutorialStartFailsClosedWhenTrialContentCannotRun()
        {
            // Missing trial event: the graduation lap the lessons unlock
            // cannot be entered, so the tutorial must not start.
            var noTrial = new FoundationService(
                new ProfileStore(directory + "-notrial", new TestJsonSerializer()),
                "en", RunId);
            noTrial.SetDrivingContent(CatalogWithoutTrial());
            noTrial.SetTutorialDefinition(TestTutorial());
            noTrial.Execute(Cmd("profile.create", P("slot", "0", "language", "en")));
            var missing = noTrial.Execute(
                Cmd("tutorial.start", P("vehicleId", "car-01")));
            Assert.That(missing.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(missing.code, Is.EqualTo("unknownEvent"));
            Assert.That(noTrial.Snapshot().driving, Is.Null,
                "a rejected start leaves no session behind");
            Assert.That(Descriptor(noTrial, "tutorial.start").reason,
                Is.EqualTo("contentUnavailable"));

            // Trial exists but rejects the chosen vehicle: same honest
            // rejection before any lesson runs.
            var blocked = new FoundationService(
                new ProfileStore(directory + "-blocked", new TestJsonSerializer()),
                "en", RunId);
            blocked.SetDrivingContent(CatalogWithMotoOnlyTrial());
            blocked.SetTutorialDefinition(TestTutorial());
            blocked.Execute(Cmd("profile.create", P("slot", "0", "language", "en")));
            var wrongVehicle = blocked.Execute(
                Cmd("tutorial.start", P("vehicleId", "car-01")));
            Assert.That(wrongVehicle.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(wrongVehicle.code, Is.EqualTo("vehicleNotForEvent"));
            Assert.That(blocked.Snapshot().driving, Is.Null);
            // The vehicle the trial does allow still starts.
            var allowed = blocked.Execute(
                Cmd("tutorial.start", P("vehicleId", "motorcycle-01")));
            Assert.That(allowed.status, Is.EqualTo(CommandResult.Applied));
        }

        [Test]
        public void RaceStartCannotOpenTheInternalFreeDriveStage()
        {
            CreateProfile();
            var blocked = service.Execute(Cmd("race.start",
                P("vehicleId", "car-01", "eventId", "tutorial-test")));
            Assert.That(blocked.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(blocked.code, Is.EqualTo("unavailable"),
                "the internal lesson stage is not a raceable event");
            Assert.That(service.Snapshot().driving, Is.Null);

            // Ordinary events still start: the filter covers only the
            // tutorial's own free-drive stage.
            var normal = service.Execute(Cmd("race.start",
                P("vehicleId", "car-01", "eventId", "intro-test")));
            Assert.That(normal.status, Is.EqualTo(CommandResult.Applied));
        }

        [Test]
        public void MissingTrialContentEndsTheTutorialHonestlyMidRun()
        {
            CreateProfile();
            StartTutorialRunning();
            Drive(0.8f, ticks: 20);
            Drive(0f, brake: 0.8f, ticks: 15);
            Drive(0f, ticks: 40); // rebuild speed after braking
            Drive(0.9f, drift: true, ticks: 30);
            Drive(0f, boost: true, ticks: 10);
            CompleteJumpLesson();
            // The catalog can no longer supply the trial the last lesson
            // unlocks: the transition must end the tutorial honestly and
            // never throw out of the step path.
            service.SetDrivingContent(CatalogWithoutTrial());
            service.Execute(Cmd("input.set",
                P("steering", "0", "drift", "false", "boost", "false",
                    "gadget", "false", "recover", "true", "changeCamera", "false",
                    "pause", "false", "durationTicks", "5")));
            Assert.DoesNotThrow(() => Boundary(1));
            var state = service.Snapshot();
            Assert.That(state.driving, Is.Null);
            Assert.That(state.screen, Is.EqualTo("practice"));
            Assert.That(Events(state, "tutorial.exited").Count, Is.EqualTo(1));
        }

        [Test]
        public void RaceRestartRejectsWhenTutorialContentDisappears()
        {
            CreateProfile();
            StartTutorialRunning();
            // The current stage's event is gone: retry cannot rebuild the
            // session and must reject honestly, leaving the run intact.
            service.SetDrivingContent(CatalogWithoutFreeDrive());
            var restarted = service.Execute(Cmd("race.restart", null));
            Assert.That(restarted.status, Is.EqualTo(CommandResult.Rejected));
            Assert.That(restarted.code, Is.EqualTo("contentUnavailable"));
            Assert.That(service.Snapshot().driving, Is.Not.Null,
                "a rejected retry leaves the running session untouched");
            Assert.That(service.Snapshot().driving.tutorial, Is.Not.Null);
        }
    }
}
