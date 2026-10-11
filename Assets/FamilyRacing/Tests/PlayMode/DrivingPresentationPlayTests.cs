using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace FamilyRacing.Tests
{
    // PlayMode proof for the Task-4 driving presentation: the real
    // DrivingCamera owns Camera.main (chase/overhead, obstacle pull-in,
    // reduced shake, snap-on-teleport) while the transparent HUD exposes
    // semantic controls that drive the same input.set/camera.toggle/
    // pause.toggle/race.* commands keyboard and agents use. Touch paths are
    // synthetic UI Toolkit pointer events — physical A7 Lite multitouch and
    // human Hebrew review stay open device gates.
    // Editor-only fixture: the generated PanelSettings loads via
    // AssetDatabase, so standalone player/device runs must exclude it.
    [UnityPlatform(RuntimePlatform.WindowsEditor, RuntimePlatform.OSXEditor,
        RuntimePlatform.LinuxEditor)]
    public sealed class DrivingPresentationPlayTests
    {
        private const string PanelSettingsPath =
            "Assets/FamilyRacing/Generated/FoundationPanelSettings.asset";
        private const string RunId = "driving-presentation-test";

        private string profilesDirectory;
        private ProfileStore store;
        private FoundationService service;
        private GameObject hostGo;
        private FoundationApp app;
        private GameObject inputGo;
        private FoundationInput driverInput;
        private GameObject shellGo;
        private UIDocument document;
        private PanelSettings panelSettings;
        private FoundationShell shell;
        private GameObject worldGo;
        private DrivingWorld world;
        private GameObject courseSource;
        private GameObject cameraObject;
        private Camera renderCamera;
        private DrivingCamera drivingCamera;
        private readonly List<Object> assets = new List<Object>();
        private readonly List<Object> loose = new List<Object>();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            profilesDirectory = Path.Combine(Path.GetTempPath(),
                "fr-driving-pres-" + Guid.NewGuid().ToString("N"));
            store = new ProfileStore(profilesDirectory, new UnityProfileSerializer());
            service = new FoundationService(store, "en", RunId);

            // The app host stays inactive so FoundationApp.Awake never runs;
            // the service/run-id/adapter are injected exactly like the other
            // shell fixtures and every boundary is pumped manually.
            hostGo = new GameObject("driving-presentation-host");
            hostGo.SetActive(false);
            app = hostGo.AddComponent<FoundationApp>();
            Inject(app, "service", service);
            Inject(app, "runId", RunId);

            inputGo = new GameObject("driving-input-adapter");
            driverInput = inputGo.AddComponent<FoundationInput>();
            driverInput.Bind(app);
            Inject(app, "driverInput", driverInput);

            // A real DrivingWorld with generated-style definitions so the
            // session/motor content is the real catalog, not a fixture stub.
            worldGo = new GameObject("driving-world-host");
            world = worldGo.AddComponent<DrivingWorld>();
            courseSource = BuildCourseSource();
            WireWorld();
            service.SetDrivingContent(world.Load());
            Assert.That(world.Catalog, Is.Not.Null,
                "the test definitions must validate like the generated course");

            cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetPositionAndRotation(
                new Vector3(0f, 10f, -20f), Quaternion.Euler(25f, 0f, 0f));
            renderCamera = cameraObject.AddComponent<Camera>();
            var camGo = new GameObject("driving-camera-presenter");
            drivingCamera = camGo.AddComponent<DrivingCamera>();

#if UNITY_EDITOR
            panelSettings = Object.Instantiate(
                UnityEditor.AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath));
#endif
            Assert.That(panelSettings, Is.Not.Null,
                "The generated PanelSettings asset must load; the clone is destroyed in teardown.");
            shellGo = new GameObject("driving-presentation-shell");
            shellGo.SetActive(false);
            document = shellGo.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            shell = shellGo.AddComponent<FoundationShell>();
            shellGo.SetActive(true);
            shell.Bind(app);
            yield return WaitFor(() => document.rootVisualElement != null
                && document.rootVisualElement.panel != null, "panel attach");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var extra in loose)
                if (extra != null) Object.Destroy(extra);
            if (drivingCamera != null) Object.Destroy(drivingCamera.gameObject);
            if (cameraObject != null) Object.Destroy(cameraObject);
            if (courseSource != null) Object.Destroy(courseSource);
            if (worldGo != null) Object.Destroy(worldGo);
            if (shellGo != null) Object.Destroy(shellGo);
            if (inputGo != null) Object.Destroy(inputGo);
            if (hostGo != null) Object.Destroy(hostGo);
            if (panelSettings != null) Object.DestroyImmediate(panelSettings);
            foreach (var asset in assets)
                if (asset != null) Object.Destroy(asset);
            try
            {
                if (Directory.Exists(profilesDirectory))
                    Directory.Delete(profilesDirectory, true);
            }
            catch (Exception) { }
        }

        // --- fixture helpers ------------------------------------------------

        private static void Inject(object target, string field, object value)
        {
            target.GetType().GetField(field,
                BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
        }

        private T Asset<T>(Action<T> configure) where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>();
            configure(asset);
            assets.Add(asset);
            return asset;
        }

        // Flat slab only — camera-obstacle tests place their own wall.
        private GameObject BuildCourseSource()
        {
            var root = new GameObject("presentation-test-course");
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Ground";
            ground.transform.SetParent(root.transform, false);
            ground.transform.localPosition = new Vector3(0f, -0.5f, 0f);
            ground.transform.localScale = new Vector3(200f, 1f, 200f);
            root.SetActive(false);
            return root;
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
                a.courseId = "presentation-course";
                a.spawnPosition = new Vector3(0f, 0f, 0f);
                a.spawnHeadingDegrees = 0f;
                a.gates = new[]
                {
                    new TrackDefinitionAsset.Gate
                    {
                        id = "start",
                        center = new Vector3(0f, 0f, 0f),
                        halfWidth = 10f,
                        direction = new Vector2(0f, 1f)
                    },
                    new TrackDefinitionAsset.Gate
                    {
                        id = "gate-1",
                        center = new Vector3(0f, 0f, 60f),
                        halfWidth = 10f,
                        direction = new Vector2(0f, 1f)
                    }
                };
            });
            var evt = Asset<EventDefinitionAsset>(a =>
            {
                a.eventId = "practice-02";
                a.track = track;
                a.laps = 2;
                a.countdownTicks = 90;
                a.vehicleIds = new[] { "car-01" };
            });
            Inject(world, "vehicles", new[] { car });
            Inject(world, "track", track);
            Inject(world, "events", new[] { evt });
            Inject(world, "coursePrefab", courseSource);
            Inject(world, "contentVersion", "presentation-content-1");
        }

        private FoundationState Snapshot()
        {
            return service.Snapshot();
        }

        // One rendered frame, in the same order FoundationApp.Update applies
        // it: world visuals first, then the presentation camera, then the
        // published camera facts and the HUD refresh.
        private FoundationState Frame(float deltaSeconds)
        {
            var state = service.Snapshot();
            world.ApplyFrame(state);
            // Flush collider activation so the next Step's probes/sweeps see
            // the same real course geometry DrivingWorldPlayTests rely on.
            Physics.SyncTransforms();
            drivingCamera.ApplyFrame(state, deltaSeconds);
            service.SetDrivingCameraFacts(drivingCamera.Facts);
            var fresh = service.Snapshot();
            shell.Refresh(fresh);
            return fresh;
        }

        private void Step(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                service.StepDriving(world.MotorWorld);
                service.AdvanceTick();
            }
        }

        private void Pump()
        {
            driverInput.Pump(false, "driving");
        }

        private void StartRace()
        {
            var created = app.Dispatch("profile.create", Params(
                ("slot", "0"), ("language", "en")));
            Assert.That(created.status, Is.EqualTo("applied"), "profile.create: " + created.code);
            var started = app.Dispatch("race.start", Params(
                ("vehicleId", "car-01"), ("eventId", "practice-02")));
            Assert.That(started.status, Is.EqualTo("applied"), "race.start: " + started.code);
        }

        private IEnumerator ToRunning()
        {
            StartRace();
            RefreshDriving();
            yield return WaitForWidget<Button>("driving.steerLeft");
            Step(90);
            Assert.That(Snapshot().driving.phase, Is.EqualTo("running"));
            Frame(0.1f);
        }

        private void RefreshDriving()
        {
            shell.Refresh(Snapshot());
        }

        private T Widget<T>(string name) where T : VisualElement
        {
            var root = document.rootVisualElement;
            return root == null ? null : root.Q<T>(name);
        }

        private IEnumerator WaitFor(Func<bool> done, string what, float timeout = 5f)
        {
            var until = Time.unscaledTime + timeout;
            while (Time.unscaledTime < until && !done())
                yield return null;
            Assert.That(done(), Is.True, "Timed out waiting for " + what + ".");
        }

        private IEnumerator WaitForWidget<T>(string name) where T : VisualElement
        {
            yield return WaitFor(() =>
            {
                var widget = Widget<T>(name);
                return widget != null && widget.worldBound.width > 0f
                    && widget.worldBound.height > 0f;
            }, "widget " + name);
        }

        private static Dictionary<string, string> Params(
            params (string name, string value)[] entries)
        {
            var map = new Dictionary<string, string>();
            foreach (var (name, value) in entries) map[name] = value;
            return map;
        }

        private static void Press(Button button, int fingerId = 0)
        {
            var touch = TouchAt(button, TouchPhase.Began, fingerId);
            using (var down = PointerDownEvent.GetPooled(touch, EventModifiers.None))
            {
                down.target = button;
                button.SendEvent(down);
            }
            touch.phase = TouchPhase.Ended;
            using (var up = PointerUpEvent.GetPooled(touch, EventModifiers.None))
            {
                up.target = button;
                button.SendEvent(up);
            }
        }

        private static Touch TouchAt(VisualElement element, TouchPhase phase, int fingerId = 0)
        {
            return new Touch
            {
                fingerId = fingerId,
                position = element.worldBound.center,
                phase = phase
            };
        }

        private static void PointerDownOn(VisualElement element, int fingerId = 0)
        {
            using (var down = PointerDownEvent.GetPooled(
                TouchAt(element, TouchPhase.Began, fingerId), EventModifiers.None))
            {
                down.target = element;
                element.SendEvent(down);
            }
        }

        private static void PointerUpOn(VisualElement element, int fingerId = 0)
        {
            using (var up = PointerUpEvent.GetPooled(
                TouchAt(element, TouchPhase.Ended, fingerId), EventModifiers.None))
            {
                up.target = element;
                element.SendEvent(up);
            }
        }

        private static void PointerCancelOn(VisualElement element, int fingerId = 0)
        {
            using (var cancel = PointerCancelEvent.GetPooled(
                TouchAt(element, TouchPhase.Canceled, fingerId), EventModifiers.None))
            {
                cancel.target = element;
                element.SendEvent(cancel);
            }
        }

        // A synthetic presentation frame for camera-math tests: no session,
        // just the DTOs the camera actually consumes.
        private static FoundationState FakeDriving(float x, float y, float z,
            float heading, string camera = "chase", bool reducedShake = false)
        {
            return new FoundationState
            {
                language = "en",
                screen = "driving",
                draftProfile = new ProfileSave
                {
                    camera = camera,
                    reducedCameraShake = reducedShake
                },
                driving = new DrivingSessionState
                {
                    phase = "running",
                    motor = new MotorState { x = x, y = y, z = z, heading = heading }
                }
            };
        }

        // --- camera behaviour ------------------------------------------------

        [UnityTest]
        public IEnumerator ChaseCameraFramesVehicleAndRecordsActualFacts()
        {
            yield return ToRunning();
            Step(30);
            var state = Frame(0.1f);

            Assert.That(drivingCamera.Facts.mode, Is.EqualTo("chase"),
                "the default profile camera is chase");
            var motor = state.driving.motor;
            var camPos = renderCamera.transform.position;
            Assert.That(camPos.z, Is.LessThan(motor.z - 3f),
                "a chase camera sits behind the vehicle facing +z");
            Assert.That(camPos.y, Is.GreaterThan(motor.y + 2f),
                "a chase camera sits above the vehicle");
            Assert.That(state.driving.camera.mode, Is.EqualTo("chase"),
                "published camera facts record the actual mode");
            Assert.That(state.driving.camera.px, Is.EqualTo(camPos.x).Within(0.001f));
            Assert.That(state.driving.camera.pz, Is.EqualTo(camPos.z).Within(0.001f));

            // Leaving the race clears the camera role and restores the pose
            // the camera held before the session engaged.
            app.Dispatch("race.exit", new Dictionary<string, string>(0));
            Frame(0.1f);
            Assert.That(drivingCamera.Facts.mode, Is.EqualTo("none"));
            Assert.That(renderCamera.transform.position.z, Is.EqualTo(-20f).Within(0.001f),
                "the pre-race camera pose is restored on exit");
        }

        [UnityTest]
        public IEnumerator CameraToggleSwitchesActualCameraToOverhead()
        {
            yield return ToRunning();
            Frame(0.1f);
            Assert.That(drivingCamera.Facts.mode, Is.EqualTo("chase"));

            var toggled = app.Dispatch("camera.toggle", new Dictionary<string, string>(0));
            Assert.That(toggled.status, Is.EqualTo("applied"));
            var state = Frame(0.1f);
            Assert.That(Snapshot().draftProfile.camera, Is.EqualTo("overhead"));

            Assert.That(drivingCamera.Facts.mode, Is.EqualTo("overhead"),
                "the actual camera follows the toggled draft preference");
            Assert.That(renderCamera.transform.position.y, Is.GreaterThan(20f),
                "the overhead camera is elevated above the course");
            Assert.That(state.driving.camera.mode, Is.EqualTo("overhead"));
        }

        [UnityTest]
        public IEnumerator CameraPreferenceRequiresExplicitSaveAndSurvivesRestart()
        {
            yield return ToRunning();
            app.Dispatch("camera.toggle", new Dictionary<string, string>(0));
            var state = Snapshot();
            Assert.That(state.draftProfile.camera, Is.EqualTo("overhead"));
            Assert.That(state.pendingSettings, Is.True);
            Assert.That(state.savedProfile.camera, Is.EqualTo("chase"),
                "the toggle must not silently commit the preference");
            Assert.That(state.draftProfile.musicVolume,
                Is.EqualTo(state.savedProfile.musicVolume).Within(0.001f),
                "the toggle must not rewrite unrelated draft settings");

            // Restart proof, negative: a fresh service over the same profile
            // directory must NOT see the unsaved toggle.
            var reloaded = new FoundationService(
                new ProfileStore(profilesDirectory, new UnityProfileSerializer()), "en", "run-2");
            Assert.That(reloaded.Snapshot().slots[0].profile.camera,
                Is.EqualTo("chase"),
                "an unsaved camera preference must not persist");

            var saved = app.Dispatch("settings.save", new Dictionary<string, string>(0));
            Assert.That(saved.status, Is.EqualTo("applied"));
            Assert.That(Snapshot().savedProfile.camera, Is.EqualTo("overhead"));

            // Restart proof, positive: the explicitly saved preference loads.
            var reloadedSaved = new FoundationService(
                new ProfileStore(profilesDirectory, new UnityProfileSerializer()), "en", "run-3");
            Assert.That(reloadedSaved.Snapshot().slots[0].profile.camera,
                Is.EqualTo("overhead"),
                "the saved camera preference must persist across a process restart");
        }

        [Test]
        public void ChaseCameraPullsInWhenWallBlocksView()
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            loose.Add(wall);
            wall.transform.SetPositionAndRotation(
                new Vector3(0f, 4f, -6f), Quaternion.identity);
            wall.transform.localScale = new Vector3(20f, 8f, 1f);
            Physics.SyncTransforms();

            var state = FakeDriving(0f, 0f, 0f, 0f);
            for (var i = 0; i < 10; i++)
                drivingCamera.ApplyFrame(state, 0.1f);

            var camZ = renderCamera.transform.position.z;
            Assert.That(camZ, Is.GreaterThan(-5.4f),
                "the chase camera must pull in front of the blocking wall, z=" + camZ);
            Assert.That(camZ, Is.LessThan(0f),
                "the camera still stays behind the vehicle, z=" + camZ);
            Assert.That(drivingCamera.Facts.pz, Is.EqualTo(camZ).Within(0.001f));
        }

        [Test]
        public void TeleportSnapsCameraAndReducedShakeSmoothsSlower()
        {
            var state = FakeDriving(0f, 0f, 0f, 0f);
            drivingCamera.ApplyFrame(state, 0.1f);
            var engaged = renderCamera.transform.position;
            Assert.That(engaged.z, Is.LessThan(-3f),
                "engage snaps straight into chase framing, z=" + engaged.z);

            // A respawn-scale jump must snap, not glide through the world.
            drivingCamera.ApplyFrame(FakeDriving(0f, 0f, 60f, 0f), 0.1f);
            var snapped = renderCamera.transform.position.z;
            Assert.That(snapped, Is.GreaterThan(45f),
                "a teleport-scale target jump snaps the camera behind the "
                + "respawned vehicle; z=" + snapped);

            // Reduced shake, same move: a second presenter on the same render
            // camera converges measurably slower for one equal frame.
            var normalCam = new GameObject("presenter-normal");
            var reducedGo = new GameObject("presenter-reduced");
            loose.Add(normalCam);
            loose.Add(reducedGo);
            var normal = normalCam.AddComponent<DrivingCamera>();
            var reduced = reducedGo.AddComponent<DrivingCamera>();

            normal.ApplyFrame(FakeDriving(0f, 0f, 0f, 0f), 0.1f);
            var normalEngaged = renderCamera.transform.position;
            normal.ApplyFrame(FakeDriving(0f, 0f, 3f, 0f), 0.1f);
            var normalDelta = renderCamera.transform.position.z - normalEngaged.z;

            renderCamera.transform.SetPositionAndRotation(
                new Vector3(0f, 10f, -20f), Quaternion.Euler(25f, 0f, 0f));
            reduced.ApplyFrame(FakeDriving(0f, 0f, 0f, 0f, "chase", true), 0.1f);
            var reducedEngaged = renderCamera.transform.position;
            reduced.ApplyFrame(FakeDriving(0f, 0f, 3f, 0f, "chase", true), 0.1f);
            var reducedDelta = renderCamera.transform.position.z - reducedEngaged.z;

            Assert.That(reducedDelta, Is.GreaterThan(0.01f),
                "the reduced-shake camera still follows, just gently");
            Assert.That(reducedDelta, Is.LessThan(normalDelta * 0.8f),
                "reduced shake converges slower: normal=" + normalDelta
                + " reduced=" + reducedDelta);
        }

        // --- HUD --------------------------------------------------------------

        [UnityTest]
        public IEnumerator HudReadoutsAgreeWithSessionState()
        {
            StartRace();
            RefreshDriving();
            yield return WaitForWidget<VisualElement>("hud.elapsed");

            var state = Frame(0.1f);
            Assert.That(Widget<Label>("hud.phase").text, Is.EqualTo("3"),
                "the countdown shows the remaining seconds");
            Assert.That(Widget<Label>("hud.lap").text, Does.Contain("Lap"),
                "the lap readout is localized English");
            Assert.That(Widget<Label>("hud.lap").text, Does.Contain("1/2"));
            Assert.That(Widget<Label>("hud.gate").text, Does.Contain("Gate"));
            Assert.That(Widget<Label>("hud.gate").text, Does.Contain("1/1"),
                "one non-start gate ahead of the two-gate test course");
            var fill = Widget<VisualElement>("hud.boostFill");
            Assert.That(fill.style.width.value.value, Is.EqualTo(100f).Within(0.5f),
                "boost starts fully charged");

            Step(90); // countdown finishes
            Step(15); // half a second of race time
            state = Frame(0.1f);
            Assert.That(state.driving.phase, Is.EqualTo("running"));
            Assert.That(Widget<Label>("hud.elapsed").text, Does.Contain("0:00.5"),
                "the timer is the session elapsed time, m:ss.d and LTR");

            // The HUD root stays transparent so the 3D world renders through.
            Assert.That(document.rootVisualElement.style.backgroundColor.value.a,
                Is.EqualTo(0f).Within(0.001f),
                "the driving HUD must not paint an opaque root over the course");
        }

        [UnityTest]
        public IEnumerator GadgetButtonIsHonestAboutBeingUnavailable()
        {
            yield return ToRunning();
            var gadget = Widget<Button>("driving.gadget");
            Assert.That(gadget, Is.Not.Null, "a gadget affordance exists in the HUD");
            Assert.That(gadget.enabledSelf, Is.False,
                "unimplemented gadget input must be visibly disabled");
            Assert.That(gadget.text, Does.Contain("Gadget"),
                "the label names the feature honestly rather than promising it works");
        }

        // --- semantic controls ------------------------------------------------

        [UnityTest]
        public IEnumerator SimultaneousHoldsComposeSteerDriftBoost()
        {
            yield return ToRunning();
            // Three simultaneous touches, distinct finger ids — the same
            // multi-touch composition a child produces on the tablet.
            PointerDownOn(Widget<VisualElement>("driving.steerLeft"), fingerId: 0);
            PointerDownOn(Widget<VisualElement>("driving.drift"), fingerId: 1);
            PointerDownOn(Widget<VisualElement>("driving.boost"), fingerId: 2);
            Pump();

            var input = Snapshot().input;
            Assert.That(input.steering, Is.EqualTo(-1f).Within(0.001f));
            Assert.That(input.drift, Is.True);
            Assert.That(input.boost, Is.True);

            // Parity: the identical agent command produces the same state.
            PointerUpOn(Widget<VisualElement>("driving.steerLeft"), fingerId: 0);
            PointerUpOn(Widget<VisualElement>("driving.drift"), fingerId: 1);
            PointerUpOn(Widget<VisualElement>("driving.boost"), fingerId: 2);
            Pump();
            var viaCommand = app.Dispatch("input.set", Params(
                ("steering", "-1"), ("drift", "true"), ("boost", "true"),
                ("gadget", "false"), ("recover", "false"),
                ("changeCamera", "false"), ("pause", "false"),
                ("durationTicks", "30")));
            Assert.That(viaCommand.status, Is.EqualTo("applied"));
            var commanded = Snapshot().input;
            Assert.That(commanded.steering, Is.EqualTo(-1f).Within(0.001f));
            Assert.That(commanded.drift, Is.True);
            Assert.That(commanded.boost, Is.True);
        }

        [UnityTest]
        public IEnumerator HoldReleaseAndCancelEndHeldInput()
        {
            yield return ToRunning();
            var steer = Widget<VisualElement>("driving.steerRight");

            PointerDownOn(steer, fingerId: 0);
            Pump();
            Assert.That(Snapshot().input.steering, Is.EqualTo(1f).Within(0.001f));
            PointerUpOn(steer, fingerId: 0);
            Pump();
            Assert.That(Snapshot().input.steering, Is.EqualTo(0f).Within(0.001f),
                "pointer up ends the hold");

            PointerDownOn(steer, fingerId: 3);
            Pump();
            Assert.That(Snapshot().input.steering, Is.EqualTo(1f).Within(0.001f));
            PointerCancelOn(steer, fingerId: 3);
            Pump();
            Assert.That(Snapshot().input.steering, Is.EqualTo(0f).Within(0.001f),
                "a cancelled touch ends the hold like a release");
        }

        [UnityTest]
        public IEnumerator CameraToggleDuringHeldSteerKeepsTheHold()
        {
            yield return ToRunning();
            PointerDownOn(Widget<VisualElement>("driving.steerLeft"), fingerId: 0);
            Pump();
            Assert.That(Snapshot().input.steering, Is.EqualTo(-1f).Within(0.001f));

            yield return WaitForWidget<Button>("driving.camera");
            // A second finger taps the camera control while the first holds
            // the steer button — the real multitouch shape on the tablet.
            Press(Widget<Button>("driving.camera"), fingerId: 9);
            Assert.That(Snapshot().draftProfile.camera, Is.EqualTo("overhead"));

            Pump();
            Assert.That(Snapshot().input.steering, Is.EqualTo(-1f).Within(0.001f),
                "toggling the camera mid-turn must not drop or stick the hold");
            var state = Frame(0.1f);
            Assert.That(state.driving.camera.mode, Is.EqualTo("overhead"),
                "the actual camera switched while the turn was held");
            PointerUpOn(Widget<VisualElement>("driving.steerLeft"), fingerId: 0);
            Pump();
        }

        [UnityTest]
        public IEnumerator LocaleChangeDuringHeldSteerReleasesAndRebuilds()
        {
            yield return ToRunning();
            PointerDownOn(Widget<VisualElement>("driving.steerLeft"), fingerId: 0);
            Pump();
            Assert.That(Snapshot().input.steering, Is.EqualTo(-1f).Within(0.001f));

            // The language change rebuilds the tree; the detached hold button
            // must release instead of sticking the turn.
            var draft = Snapshot().draftProfile;
            var changed = app.Dispatch("settings.update", Params(
                ("language", "he"), ("camera", draft.camera),
                ("reducedCameraShake", draft.reducedCameraShake ? "true" : "false"),
                ("musicVolume", "0.8"), ("effectsVolume", "0.8"),
                ("voiceVolume", "0.8")));
            Assert.That(changed.status, Is.EqualTo("applied"));
            RefreshDriving();
            Assert.That(document.rootVisualElement.languageDirection,
                Is.EqualTo(LanguageDirection.RTL), "Hebrew rebuilds RTL");

            Pump();
            Assert.That(Snapshot().input.steering, Is.EqualTo(0f).Within(0.001f),
                "a touch whose button was rebuilt away must not keep steering");
            yield return WaitForWidget<Label>("hud.lap");
            Assert.That(Widget<Label>("hud.lap").text, Does.Contain("הקפה"),
                "the rebuilt HUD renders logical-order Hebrew");
            Assert.That(Widget<Label>("hud.elapsed").text, Does.Contain("0:"),
                "the timer digits stay LTR inside the RTL shell");
        }

        [UnityTest]
        public IEnumerator PauseResumeAndRetryThroughHud()
        {
            yield return ToRunning();
            var raceId = Snapshot().driving.raceId;

            yield return WaitForWidget<Button>("driving.pause");
            Press(Widget<Button>("driving.pause"));
            Assert.That(Snapshot().paused, Is.True);
            Frame(0.1f);
            Assert.That(Widget<VisualElement>("overlay.pause").style.display.value,
                Is.EqualTo(DisplayStyle.Flex), "the pause overlay must surface");

            yield return WaitForWidget<Button>("driving.resume");
            Press(Widget<Button>("driving.resume"));
            Assert.That(Snapshot().paused, Is.False);
            Frame(0.1f);
            Assert.That(Widget<VisualElement>("overlay.pause").style.display.value,
                Is.EqualTo(DisplayStyle.None));

            // Pause again and retry: a fresh race identity, still driving,
            // and the retry control went through race.restart.
            Press(Widget<Button>("driving.pause"));
            Assert.That(Snapshot().paused, Is.True);
            Frame(0.1f);
            var overlay = Widget<VisualElement>("overlay.pause");
            yield return WaitFor(() =>
                overlay.Q<Button>("driving.retry") != null
                && overlay.Q<Button>("driving.retry").worldBound.width > 0f,
                "retry widget");
            Press(overlay.Q<Button>("driving.retry"));
            var state = Snapshot();
            Assert.That(state.driving.raceId, Is.Not.EqualTo(raceId),
                "retry creates a new race identity");
            Assert.That(state.paused, Is.False,
                "a restarted race must not inherit the pause");
            Assert.That(state.screen, Is.EqualTo("driving"));
        }

        [UnityTest]
        public IEnumerator RecoverButtonPulsesThroughSharedInputPath()
        {
            yield return ToRunning();
            yield return WaitForWidget<Button>("driving.recover");
            Press(Widget<Button>("driving.recover"));
            Pump();
            var state = Snapshot();
            Assert.That(state.input.recover || state.driving.pendingActions.Count > 0,
                Is.True, "the recover button must reach the same input.set path as R");
        }
    }
}
