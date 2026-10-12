using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;
using FamilyRacing.Core.Tutorial;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace FamilyRacing.Tests
{
    // PlayMode proof for the onboarding slice: the ordered lessons are
    // traversed through real input commands on a real physics course
    // (auto-acceleration, a real drift, a real ramp launch and landing,
    // a real recovery pending action), the unlocked one-lap trial is
    // finished for graduation, and the presenter announces cues from the
    // bundled clips while honestly reporting missing recordings.
    public sealed class DrivingTutorialPlayTests
    {
        private const string RunId = "tutorial-play-test";

        private string directory;
        private FoundationService service;
        private GameObject host;
        private DrivingWorld world;
        private TutorialDefinitionAsset tutorialAsset;
        private GameObject courseSource;
        private GameObject cameraObject;
        private readonly List<Object> assets = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "family-racing-tutplay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            service = new FoundationService(
                new ProfileStore(directory, new UnityProfileSerializer()), "en", RunId);

            host = new GameObject("tutorial-world-host");
            host.SetActive(false);
            world = host.AddComponent<DrivingWorld>();
            courseSource = BuildCourseSource();
            cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.AddComponent<Camera>();
            host.SetActive(true);
        }

        [TearDown]
        public void TearDown()
        {
            if (host != null) Object.Destroy(host);
            if (courseSource != null) Object.Destroy(courseSource);
            if (cameraObject != null) Object.Destroy(cameraObject);
            foreach (var asset in assets)
                if (asset != null) Object.Destroy(asset);
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (Exception) { }
        }

        private T Asset<T>(Action<T> configure) where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>();
            configure(asset);
            assets.Add(asset);
            return asset;
        }

        private static void Inject(object target, string field, object value)
        {
            target.GetType().GetField(field,
                BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
        }

        // A flat slab plus the same style of sloped ramp the generated
        // graybox uses: drive +z to climb and launch, with a wide flat
        // return leg at x=12 for the finish crossing.
        private GameObject BuildCourseSource()
        {
            var root = new GameObject("tutorial-course");
            Box(root, "Ground", new Vector3(0f, -0.5f, 0f),
                new Vector3(120f, 1f, 160f), Quaternion.identity);
            Box(root, "Ramp", new Vector3(0f, 1.2f, 30f),
                new Vector3(12f, 0.6f, 16f), Quaternion.Euler(-14f, 0f, 0f));
            root.SetActive(false);
            return root;
        }

        private static void Box(GameObject parent, string name,
            Vector3 position, Vector3 scale, Quaternion rotation)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = name;
            box.transform.SetParent(parent.transform, false);
            box.transform.localPosition = position;
            box.transform.localRotation = rotation;
            box.transform.localScale = scale;
        }

        private static TrackDefinitionAsset.Gate Gate(string id, float x, float z,
            float dirX, float dirZ)
        {
            return new TrackDefinitionAsset.Gate
            {
                id = id,
                center = new Vector3(x, 0f, z),
                halfWidth = 12f,
                direction = new Vector2(dirX, dirZ),
                recoverPosition = new Vector3(x - dirX * 3f, 0f, z - dirZ * 3f),
                recoverHeadingDegrees = Mathf.Atan2(dirX, dirZ) * Mathf.Rad2Deg
            };
        }

        private void WireWorld()
        {
            var car = Asset<VehicleDefinitionAsset>(a =>
            {
                a.vehicleId = "car-01";
                a.bodyScale = new Vector3(1.8f, 1.1f, 3.2f);
                a.bodyColor = Color.red;
            });
            var track = Asset<TrackDefinitionAsset>(a =>
            {
                a.courseId = "play-course";
                a.spawnPosition = new Vector3(0f, 0f, -10f);
                a.spawnHeadingDegrees = 0f;
                a.gates = new[]
                {
                    // Start/finish accepts -z crossings only: the trial is
                    // a real out-and-back lap over the ramp.
                    Gate("start", 0f, 0f, 0f, -1f),
                    Gate("gate-1", 0f, 50f, 0f, 1f)
                };
            });
            var free = Asset<EventDefinitionAsset>(a =>
            {
                a.eventId = "tutorial-01";
                a.track = track;
                a.laps = 99;
                a.countdownTicks = 3;
                a.vehicleIds = new[] { "car-01" };
            });
            var intro = Asset<EventDefinitionAsset>(a =>
            {
                a.eventId = "intro-01";
                a.track = track;
                a.laps = 1;
                a.countdownTicks = 3;
                a.vehicleIds = new[] { "car-01" };
            });
            tutorialAsset = Asset<TutorialDefinitionAsset>(a =>
            {
                a.tutorialId = "tutorial-play";
                a.freeDriveEventId = "tutorial-01";
                a.trialEventId = "intro-01";
                a.lessons = new[]
                {
                    Lesson("steering", "steerTicks", "", 25, 0.25f,
                        "tutorial.cue.steering", "<>"),
                    Lesson("drift", "event", "motor.driftStart", 0, 0f,
                        "tutorial.cue.drift", "~"),
                    Lesson("boost", "event", "motor.boostStart", 0, 0f,
                        "tutorial.cue.boost", ">>"),
                    Lesson("jump", "event", "motor.landed", 0, 0f,
                        "tutorial.cue.jump", "^"),
                    Lesson("recovery", "event", "race.recovered", 0, 0f,
                        "tutorial.cue.recovery", "o")
                };
                a.trialCue = Cue("tutorial.cue.trial", "*");
                a.finishedCue = Cue("tutorial.done", "*");
            });
            Inject(world, "vehicles", new[] { car });
            Inject(world, "track", track);
            Inject(world, "events", new EventDefinitionAsset[] { free, intro });
            Inject(world, "tutorial", tutorialAsset);
            Inject(world, "coursePrefab", courseSource);
            Inject(world, "contentVersion", "play-tutorial-1");
        }

        private static TutorialDefinitionAsset.Lesson Lesson(string id,
            string rule, string eventKind, int steerTicks, float minSteering,
            string textKey, string icon)
        {
            return new TutorialDefinitionAsset.Lesson
            {
                lessonId = id,
                rule = rule,
                eventKind = eventKind,
                requiredSteerTicks = steerTicks,
                minSteering = minSteering,
                cue = Cue(textKey, icon)
            };
        }

        private static TutorialDefinitionAsset.Cue Cue(string textKey, string icon)
        {
            return new TutorialDefinitionAsset.Cue { textKey = textKey, icon = icon };
        }

        private void LoadAndStartTutorial()
        {
            var catalog = world.Load();
            Assert.That(catalog, Is.Not.Null, "definitions must validate");
            Assert.That(world.Tutorial, Is.Not.Null, "tutorial must validate");
            service.SetDrivingContent(catalog);
            service.SetTutorialDefinition(world.Tutorial);
            service.Execute(Command("profile.create", P("slot", "0", "language", "en")));
            var started = service.Execute(Command("tutorial.start",
                P("vehicleId", "car-01")));
            Assert.That(started.status, Is.EqualTo(CommandResult.Applied),
                "tutorial.start failed: " + started.code);
            // Activate the course colliders like the app does each frame,
            // before any simulated boundary probes the world.
            world.ApplyFrame(service.Snapshot());
            Physics.SyncTransforms();
            Step(3); // countdown
            Assert.That(service.Snapshot().driving.phase, Is.EqualTo("running"));
        }

        private void Step(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                service.StepDriving(world.MotorWorld);
                service.AdvanceTick();
            }
        }

        private GameCommand Command(string action, Dictionary<string, string> parameters)
        {
            return new GameCommand(1, RunId, Guid.NewGuid().ToString("N"),
                "local", action, parameters);
        }

        private static Dictionary<string, string> P(params string[] keyValues)
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i + 1 < keyValues.Length; i += 2)
                map[keyValues[i]] = keyValues[i + 1];
            return map;
        }

        private CommandResult Drive(float steering, bool drift = false,
            bool boost = false, bool recover = false, int durationTicks = 5)
        {
            var result = service.Execute(Command("input.set",
                P("steering", steering.ToString("0.###",
                        System.Globalization.CultureInfo.InvariantCulture),
                    "drift", drift ? "true" : "false",
                    "boost", boost ? "true" : "false",
                    "gadget", "false",
                    "recover", recover ? "true" : "false",
                    "changeCamera", "false", "pause", "false",
                    "durationTicks", durationTicks.ToString(
                        System.Globalization.CultureInfo.InvariantCulture))));
            Assert.That(result.status, Is.EqualTo(CommandResult.Applied),
                "input must apply during the tutorial");
            Step(durationTicks);
            return result;
        }

        private static float WrapAngle(float radians)
        {
            while (radians > Mathf.PI) radians -= 2f * Mathf.PI;
            while (radians <= -Mathf.PI) radians += 2f * Mathf.PI;
            return radians;
        }

        private DrivingSessionState Pursue(Vector2 waypoint, int maxTicks = 4000)
        {
            return Pursue(new List<Vector2> { waypoint }, maxTicks);
        }

        private DrivingSessionState Pursue(List<Vector2> route, int maxTicks = 4000)
        {
            var wp = 0;
            var state = service.Snapshot().driving;
            for (var t = 0; t < maxTicks && wp < route.Count;)
            {
                var motor = state.motor;
                var dx = route[wp].x - motor.x;
                var dz = route[wp].y - motor.z;
                if (dx * dx + dz * dz < 16f) { wp++; continue; }
                var desired = Mathf.Atan2(dx, dz);
                var steer = Mathf.Clamp(
                    WrapAngle(desired - motor.heading) * 3f, -1f, 1f);
                Drive(steer, durationTicks: 5);
                t += 5;
                state = service.Snapshot().driving;
                if (state.phase == "finished") break;
            }
            Assert.That(wp == route.Count || state.phase == "finished",
                "pursuit must complete the route (or finish the race)");
            return state;
        }

        private TutorialState Tutorial()
        {
            var driving = service.Snapshot().driving;
            return driving != null ? driving.tutorial : null;
        }

        [Test]
        public void TutorialTraversalThroughRealControlsGraduates()
        {
            WireWorld();
            LoadAndStartTutorial();
            Assert.That(Tutorial().lessonId, Is.EqualTo("steering"));

            // Steering: real held steering through the command path.
            Drive(0.8f, durationTicks: 25);
            Assert.That(Tutorial().lessonId, Is.EqualTo("drift"),
                "held steering must satisfy the first lesson");

            // Drift: a real drift engagement at speed.
            Drive(0f, durationTicks: 40); // straighten and reach drift speed
            Drive(0.9f, drift: true, durationTicks: 30);
            Assert.That(Tutorial().lessonId, Is.EqualTo("boost"));

            // Boost: a real boost firing.
            Drive(0f, boost: true, durationTicks: 10);
            Assert.That(Tutorial().lessonId, Is.EqualTo("jump"));

            // Jump: align with the ramp centre first so the approach
            // climbs the slope instead of clipping its side wall, then
            // launch, go airborne, and land.
            Pursue(new List<Vector2>
            {
                new Vector2(0f, 10f),
                new Vector2(0f, 60f)
            });
            Step(15); // settle: the landing must actually commit
            Assert.That(Tutorial().lessonId, Is.EqualTo("recovery"),
                "a real landing off the ramp must complete the jump lesson");

            // Recovery: the real pending action resolves on a boundary.
            Drive(0f, recover: true, durationTicks: 1);
            Assert.That(Tutorial().phase, Is.EqualTo("trial"),
                "completing all lessons unlocks the one-lap trial");
            Assert.That(service.Snapshot().driving.eventId, Is.EqualTo("intro-01"));
            Step(3); // trial countdown

            // The trial: gate-1 over the ramp, swing east off the ramp's
            // footprint, then a flat return leg that crosses the -z
            // start/finish plane inside its half-width.
            Pursue(new List<Vector2>
            {
                new Vector2(0f, 55f),
                new Vector2(14f, 55f),
                new Vector2(0f, -8f)
            });
            var tutorial = Tutorial();
            Assert.That(service.Snapshot().driving.phase, Is.EqualTo("finished"),
                "the introductory lap must finish through real crossings");
            Assert.That(tutorial.phase, Is.EqualTo("graduated"));
            Assert.That(tutorial.graduated, Is.True);
        }

        [Test]
        public void PresenterAnnouncesBundledClipAndReportsMissing()
        {
            WireWorld();
            var go = new GameObject("tutorial-presenter");
            try
            {
                var presenter = go.AddComponent<TutorialPresenter>();
                presenter.Bind(tutorialAsset);

                // No clips are bundled yet: the announcement is honest —
                // a recorded missing cue, one logged warning, no throw.
                LogAssert.Expect(LogType.Warning,
                    new System.Text.RegularExpressions.Regex("no en recording"));
                presenter.ApplyFrame(TutorialStateFrame("steering", 1, "en"), null);
                Assert.That(presenter.PlayingCue, Is.Null,
                    "a missing clip plays nothing — PlayingCue stays honest");
                Assert.That(presenter.LastMissingCue, Is.EqualTo("steering:en"));

                // Replay re-announces the same cue (and does not re-warn).
                presenter.ApplyFrame(TutorialStateFrame("steering", 2, "en"), null);
                Assert.That(presenter.PlayingCue, Is.Null);

                // A bundled clip plays through the presenter's source.
                var clip = AudioClip.Create("cue-steering-en", 4410, 1, 44100, false);
                assets.Add(clip);
                tutorialAsset.FindCue("steering").english = clip;
                presenter.ApplyFrame(TutorialStateFrame("steering", 3, "en"), null);
                Assert.That(presenter.PlayingCue, Is.EqualTo("steering"),
                    "a resolved clip reports itself as playing");
                Assert.That(presenter.LastMissingCue, Is.EqualTo("steering:en"),
                    "the earlier miss is remembered, not replayed");

                // Clearing the session stops the announcement cleanly.
                presenter.ApplyFrame(new FoundationState(), null);
                Assert.That(presenter.PlayingCue, Is.Null);
            }
            finally
            {
                Object.Destroy(go);
            }
        }

        private static FoundationState TutorialStateFrame(string cueId,
            int cueSequence, string language)
        {
            return new FoundationState
            {
                language = language,
                driving = new DrivingSessionState
                {
                    tutorial = new TutorialState
                    {
                        tutorialId = "tutorial-play",
                        phase = "lessons",
                        lessonId = cueId,
                        cueId = cueId,
                        cueSequence = cueSequence,
                        objective = "tutorial.cue.steering"
                    }
                },
                draftProfile = new ProfileSave { voiceVolume = 0.5f }
            };
        }
    }
}
