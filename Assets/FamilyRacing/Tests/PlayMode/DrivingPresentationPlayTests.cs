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
            // Let the smoothed climb converge, then assert the elevated
            // framing actually arrived.
            for (var i = 0; i < 12; i++) Frame(0.1f);
            state = Snapshot();
            Assert.That(renderCamera.transform.position.y, Is.GreaterThan(25f),
                "the overhead camera is elevated above the course; py="
                + renderCamera.transform.position.y
                + " blocker=" + (drivingCamera.LastObstacleName ?? "none"));
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

        // Corner occlusion flickers on/off the target→camera ray every
        // other frame; an instantaneous pulled/ideal flip pumped the
        // framing (the reported "camera jumps near corners"). The pull
        // depth is smoothed instead: pull-in stays quick, release eases.
        [Test]
        public void CameraPullDepthIsSmoothUnderFlickeringOcclusion()
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            loose.Add(wall);
            wall.transform.SetPositionAndRotation(
                new Vector3(0f, 4f, -6f), Quaternion.identity);
            wall.transform.localScale = new Vector3(20f, 8f, 1f);
            Physics.SyncTransforms();

            var state = FakeDriving(0f, 0f, 0f, 0f);
            var minZ = float.MaxValue;
            var maxZ = float.MinValue;
            for (var i = 0; i < 16; i++)
            {
                // An occluder flapping on/off the ray — what a wall corner
                // does to a frame-by-frame raycast while driving past it.
                wall.SetActive(i % 2 == 0);
                Physics.SyncTransforms();
                drivingCamera.ApplyFrame(state, 0.033f);
                var z = renderCamera.transform.position.z;
                minZ = Mathf.Min(minZ, z);
                maxZ = Mathf.Max(maxZ, z);
            }
            Assert.That(maxZ - minZ, Is.LessThan(1.2f),
                "flickering occlusion must hold a steady depth, not pump "
                + "between pulled and ideal framing (swing="
                + (maxZ - minZ).ToString("F2") + "m)");

            // With the occluder gone for good the pull releases gradually
            // and still converges on the chase standoff.
            wall.SetActive(false);
            Physics.SyncTransforms();
            var worstStep = 0f;
            var prev = renderCamera.transform.position.z;
            for (var i = 0; i < 90; i++)
            {
                drivingCamera.ApplyFrame(state, 0.033f);
                var step = Mathf.Abs(renderCamera.transform.position.z - prev);
                prev = renderCamera.transform.position.z;
                worstStep = Mathf.Max(worstStep, step);
            }
            Assert.That(worstStep, Is.LessThan(0.9f),
                "release eases out — no single-frame jump back to ideal "
                + "(worstStep=" + worstStep.ToString("F2") + "m)");
            Assert.That(renderCamera.transform.position.z, Is.LessThan(-6.5f),
                "and it does converge back to the chase standoff");
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
        public IEnumerator DrivingHudShowsKeyboardLegendAndDriftFeedback()
        {
            yield return ToRunning();
            var keys = Widget<Label>("hud.keys");
            Assert.That(keys, Is.Not.Null,
                "the driving HUD must carry a keyboard legend");
            Assert.That(keys.text, Does.Contain("Space"));
            Assert.That(keys.text, Does.Contain("Shift"));
            var drift = Widget<Label>("hud.drift");
            Assert.That(drift, Is.Not.Null, "drift engagement feedback exists");
            Assert.That(drift.text, Is.EqualTo(""),
                "no drift feedback before a real drift");

            // Engage a real drift through the shared command path: speed,
            // steering, held drift — then the HUD answers visibly.
            var held = app.Dispatch("input.set", Params(
                ("steering", "0.7"), ("drift", "true"), ("boost", "false"),
                ("gadget", "false"), ("recover", "false"),
                ("changeCamera", "false"), ("pause", "false"),
                ("durationTicks", "90")));
            Assert.That(held.status, Is.EqualTo("applied"));
            Step(90);
            Assert.That(Snapshot().driving.motor.drifting, Is.True,
                "setup: the held drift input must engage a real drift");
            Frame(0.1f);
            Assert.That(Widget<Label>("hud.drift").text, Does.Contain("Drift"),
                "an engaged drift shows visible feedback");
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
        public IEnumerator AppFocusLostThroughCommandPathClearsHeldInput()
        {
            yield return ToRunning();
            // The exact cleanup OnApplicationFocus/OnApplicationPause
            // dispatch, exercised through the command surface — headless
            // and test runs can never lose OS focus on their own.
            var held = app.Dispatch("input.set", Params(
                ("steering", "0.8"), ("drift", "false"), ("boost", "false"),
                ("gadget", "false"), ("recover", "false"),
                ("changeCamera", "false"), ("pause", "false"),
                ("durationTicks", "90")));
            Assert.That(held.status, Is.EqualTo("applied"));
            Assert.That(Snapshot().input.steering,
                Is.EqualTo(0.8f).Within(0.001f));

            var lost = app.Dispatch("app.focusLost",
                new Dictionary<string, string>(0));
            Assert.That(lost.status, Is.EqualTo("applied"));
            var input = Snapshot().input;
            Assert.That(input.steering, Is.EqualTo(0f).Within(0.001f),
                "focus-loss cleanup neutralizes held steering");
            Assert.That(input.drift, Is.False);
            Assert.That(input.brake, Is.EqualTo(0f).Within(0.001f));
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
                overlay.Q<Button>("driving.pauseRetry") != null
                && overlay.Q<Button>("driving.pauseRetry").worldBound.width > 0f,
                "retry widget");
            Press(overlay.Q<Button>("driving.pauseRetry"));
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

        [UnityTest]
        public IEnumerator DisengageRestoresMenuPoseEveryTime()
        {
            // The saved menu pose must survive every disengage — a second
            // race leaving must put the camera back just like the first.
            world.BindPresentationCamera();
            var menuPos = renderCamera.transform.position;
            var menuRot = renderCamera.transform.rotation;
            var created = app.Dispatch("profile.create", Params(
                ("slot", "0"), ("language", "en")));
            Assert.That(created.status, Is.EqualTo("applied"),
                "profile.create: " + created.code);
            for (var cycle = 0; cycle < 2; cycle++)
            {
                var started = app.Dispatch("race.start", Params(
                    ("vehicleId", "car-01"), ("eventId", "practice-02")));
                Assert.That(started.status, Is.EqualTo("applied"),
                    "race.start: " + started.code);
                Step(90);
                Assert.That(Snapshot().driving.phase, Is.EqualTo("running"));
                for (var i = 0; i < 12; i++) Frame(0.1f);
                Assert.That(Vector3.Distance(
                        renderCamera.transform.position, menuPos),
                    Is.GreaterThan(0.5f),
                    "cycle " + cycle + ": engaged chase framing must leave "
                    + "the menu pose");
                app.Dispatch("race.exit", new Dictionary<string, string>(0));
                Frame(0.1f);
                Assert.That(Vector3.Distance(
                        renderCamera.transform.position, menuPos),
                    Is.LessThan(0.001f),
                    "cycle " + cycle + ": disengage must restore the saved "
                    + "menu pose every time");
                Assert.That(Quaternion.Angle(
                        renderCamera.transform.rotation, menuRot),
                    Is.LessThan(0.5f));
            }
            yield break;
        }

        [UnityTest]
        public IEnumerator NoneModeFactsCarryNoPose()
        {
            yield return ToRunning();
            Frame(0.1f);
            Assert.That(drivingCamera.Facts.mode, Is.EqualTo("chase"),
                "setup: an engaged camera reports its live mode");
            app.Dispatch("race.exit", new Dictionary<string, string>(0));
            Frame(0.1f);
            var facts = drivingCamera.Facts;
            Assert.That(facts.mode, Is.EqualTo("none"));
            Assert.That(facts.px, Is.EqualTo(0f));
            Assert.That(facts.py, Is.EqualTo(0f));
            Assert.That(facts.pz, Is.EqualTo(0f));
            Assert.That(facts.tx, Is.EqualTo(0f));
            Assert.That(facts.ty, Is.EqualTo(0f));
            Assert.That(facts.tz, Is.EqualTo(0f));
        }

        [UnityTest]
        public IEnumerator UnknownVehicleIdShowsFallbackSilhouette()
        {
            yield return ToRunning();
            Frame(0.1f);
            var presentation = world.transform.Find("DrivingVehicle")
                .GetComponent<VehiclePresentation>();
            Assert.That(presentation.ShownKey, Does.Contain("car-01"),
                "setup: the known vehicle shows its real silhouette");
            // Session vehicleIds always validate against the catalog, so
            // exercise the world's asset lookup directly: a missing id
            // must swap to the generic fallback, never keep the old shape.
            var applyVisual = typeof(DrivingWorld).GetMethod(
                "ApplyVehicleVisual",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(applyVisual, Is.Not.Null);
            applyVisual.Invoke(world, new object[]
                { "ghost-01", Snapshot().driving.motor, 0f, 1f,
                    Snapshot() });
            // ShownKey is silhouette|vehicleId|visualId — the generic
            // fallback has no silhouette and no authored visual ("-").
            Assert.That(presentation.ShownKey, Is.EqualTo("|ghost-01|-"),
                "an unknown vehicle id must fall back to the generic cube");
            Assert.That(
                world.transform.Find("DrivingVehicle/LeanPivot/Cabin"),
                Is.Null, "the previous car silhouette must not linger");
            Assert.That(
                world.transform.Find("DrivingVehicle/LeanPivot/Body"),
                Is.Not.Null, "the fallback body part must exist");
            yield break;
        }

        [UnityTest]
        public IEnumerator DrivingButtonsHaveUniqueSemanticNames()
        {
            yield return ToRunning();
            var names = new HashSet<string>();
            var duplicates = new List<string>();
            foreach (var button in document.rootVisualElement
                .Query<Button>().ToList())
                if (!names.Add(button.name)) duplicates.Add(button.name);
            Assert.That(duplicates, Is.Empty,
                "every driving button needs a unique semantic name: "
                + string.Join(", ", duplicates));
        }

        // --- generated-course camera coverage -------------------------------
        // Camera behaviour must hold on the real graybox course, not only
        // on the flat fixture slab: the ramp crest/landing, both shortcut
        // islands, and overhead occluders. Two shapes — motor states
        // placed against the real colliders, and a driven section of the
        // real generated event through the shared command path.

        private GameObject SpawnRealCourse()
        {
            var prefab =
#if UNITY_EDITOR
                UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/FamilyRacing/Generated/Driving/GrayboxCourse.prefab");
#else
                null;
#endif
            Assert.That(prefab, Is.Not.Null,
                "the generated GrayboxCourse prefab must exist");
            var instance = Object.Instantiate(prefab);
            loose.Add(instance);
            Physics.SyncTransforms();
            return instance;
        }

        private static float WrapAngle(float radians)
        {
            while (radians > Mathf.PI) radians -= 2f * Mathf.PI;
            while (radians <= -Mathf.PI) radians += 2f * Mathf.PI;
            return radians;
        }

        [Test]
        public void ChaseCameraPullsInBesideBothRealShortcutIslands()
        {
            SpawnRealCourse();

            // IslandNorthEast's north face sits at z=51: a vehicle heading
            // north with its tail at the face wants a chase spot inside
            // the island — the real collider must pull the camera out.
            var state = FakeDriving(32f, 0f, 51.9f, 0f);
            for (var i = 0; i < 4; i++)
                drivingCamera.ApplyFrame(state, 0.1f);
            Assert.That(drivingCamera.Facts.mode, Is.EqualTo("chase"));
            Assert.That(drivingCamera.Facts.pz, Is.GreaterThan(51f)
                .And.LessThan(51.9f),
                "IslandNE must push the chase spot out of its face; pz="
                + drivingCamera.Facts.pz);
            Assert.That(drivingCamera.Facts.py, Is.LessThan(2f),
                "the pull-in hugs the island face, py=" + drivingCamera.Facts.py);

            // IslandSouthEast's south face sits at z=-40: the same check
            // heading south proves both shortcut blockers occlude the view.
            state = FakeDriving(72f, 0f, -40.9f, Mathf.PI);
            for (var i = 0; i < 4; i++)
                drivingCamera.ApplyFrame(state, 0.1f);
            Assert.That(drivingCamera.Facts.pz, Is.GreaterThan(-40.9f)
                .And.LessThan(-39.4f),
                "IslandSE must pull the chase spot out of its face; pz="
                + drivingCamera.Facts.pz);
            Assert.That(drivingCamera.Facts.py, Is.LessThan(2f));
        }

        [Test]
        public void OverheadCameraStaysElevatedAndDropsBelowRealCanopy()
        {
            SpawnRealCourse();

            // Clear sky on the real course: the elevated follower keeps
            // its height above the vehicle.
            var state = FakeDriving(0f, 0f, -30f, 0f, camera: "overhead");
            for (var i = 0; i < 4; i++)
                drivingCamera.ApplyFrame(state, 0.1f);
            Assert.That(drivingCamera.Facts.mode, Is.EqualTo("overhead"));
            Assert.That(drivingCamera.Facts.py, Is.GreaterThan(25f),
                "unobstructed overhead must stay elevated, py="
                + drivingCamera.Facts.py);

            // A solid canopy over the vehicle occludes the downward view;
            // the same linecast pull-in the chase mode uses must drop the
            // overhead camera in front of the blocker.
            var canopy = GameObject.CreatePrimitive(PrimitiveType.Cube);
            loose.Add(canopy);
            canopy.transform.SetPositionAndRotation(
                new Vector3(0f, 20f, -30f), Quaternion.identity);
            canopy.transform.localScale = new Vector3(30f, 1f, 30f);
            Physics.SyncTransforms();

            for (var i = 0; i < 14; i++)
                drivingCamera.ApplyFrame(state, 0.1f);
            Assert.That(drivingCamera.Facts.py, Is.LessThan(19.6f),
                "the canopy must pull the overhead camera below its face; py="
                + drivingCamera.Facts.py);
            Assert.That(drivingCamera.Facts.py, Is.GreaterThan(1.3f),
                "the camera still stays above the vehicle, py="
                + drivingCamera.Facts.py);
        }

        [Test]
        public void DrivenRealCourseCameraTracksRampLandingAndShortcuts()
        {
            // Wire a DrivingWorld straight onto the generated assets —
            // the real track/event/vehicle definitions and course prefab,
            // not fixture mirrors.
            var realGo = new GameObject("real-course-world");
            loose.Add(realGo);
            var realWorld = realGo.AddComponent<DrivingWorld>();
            VehicleDefinitionAsset car = null;
            VehicleDefinitionAsset moto = null;
            TrackDefinitionAsset track = null;
            EventDefinitionAsset evt = null;
            GameObject prefab = null;
#if UNITY_EDITOR
            car = UnityEditor.AssetDatabase.LoadAssetAtPath<VehicleDefinitionAsset>(
                "Assets/FamilyRacing/Generated/Driving/car-01.asset");
            moto = UnityEditor.AssetDatabase.LoadAssetAtPath<VehicleDefinitionAsset>(
                "Assets/FamilyRacing/Generated/Driving/motorcycle-01.asset");
            track = UnityEditor.AssetDatabase.LoadAssetAtPath<TrackDefinitionAsset>(
                "Assets/FamilyRacing/Generated/Driving/graybox-01.asset");
            evt = UnityEditor.AssetDatabase.LoadAssetAtPath<EventDefinitionAsset>(
                "Assets/FamilyRacing/Generated/Driving/practice-02.asset");
            prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/FamilyRacing/Generated/Driving/GrayboxCourse.prefab");
#endif
            Assert.That(car, Is.Not.Null);
            Assert.That(moto, Is.Not.Null,
                "practice-02 allows both vehicles; the catalog needs both");
            Assert.That(track, Is.Not.Null);
            Assert.That(evt, Is.Not.Null);
            Assert.That(prefab, Is.Not.Null);
            Inject(realWorld, "vehicles", new[] { car, moto });
            Inject(realWorld, "track", track);
            Inject(realWorld, "events", new[] { evt });
            Inject(realWorld, "coursePrefab", prefab);
            Inject(realWorld, "contentVersion", "generated-course-under-test");
            var catalog = realWorld.Load();
            Assert.That(catalog, Is.Not.Null, "generated content must validate");
            service.SetDrivingContent(catalog);

            StartRace();
            for (var i = 0; i < 90; i++)
            {
                service.StepDriving(realWorld.MotorWorld);
                service.AdvanceTick();
            }
            var session = Snapshot().driving;
            Assert.That(session.phase, Is.EqualTo("running"));
            // Activate the real course colliders before the motor sweeps.
            realWorld.ApplyFrame(Snapshot());
            Physics.SyncTransforms();

            // Real graybox route: up the start straight, west past the
            // landmark ramp's flank into its flat landing apron (the
            // current motor sweep cannot step onto the ramp's leading
            // lip — the placed test below covers the climb/landing
            // elevation track), then the inside shortcut legs beside
            // both islands — south of IslandNorthEast and west/south of
            // IslandSouthEast.
            var route = new List<Vector2>
            {
                new Vector2(0f, -30f),
                new Vector2(-50f, -5f),
                new Vector2(-70f, 20f),   // skirt the ramp's west flank
                new Vector2(-72f, 46f),   // stay wide: the slab's west
                                          // face (x≈-66, z 22-38) is
                                          // solid — a straight cut to the
                                          // apron clips its NW corner
                new Vector2(-62f, 52f),   // ramp landing apron, north side
                new Vector2(-30f, 55f),
                new Vector2(30f, 28f),    // inside shortcut south of IslandNE
                new Vector2(70f, 32f),
                new Vector2(97f, -12f),   // down the east side
                new Vector2(55f, -18f),   // inside shortcut west of IslandSE
                new Vector2(58f, -52f)    // rejoin south of the island
            };
            var wp = 0;
            var facts = drivingCamera.Facts;
            for (var t = 0; t < 4000 && wp < route.Count; t += 5)
            {
                var motor = session.motor;
                var dx = route[wp].x - motor.x;
                var dz = route[wp].y - motor.z;
                if (dx * dx + dz * dz < 49f) { wp++; continue; }
                var desired = Mathf.Atan2(dx, dz);
                var steer = Mathf.Clamp(
                    WrapAngle(desired - motor.heading) * 3f, -1f, 1f);
                var applied = app.Dispatch("input.set", Params(
                    ("steering", steer.ToString("0.###",
                        System.Globalization.CultureInfo.InvariantCulture)),
                    ("drift", "false"), ("boost", "false"), ("gadget", "false"),
                    ("recover", "false"), ("changeCamera", "false"),
                    ("pause", "false"), ("durationTicks", "5")));
                Assert.That(applied.status, Is.EqualTo("applied"),
                    "steering input must apply while driving the real course");
                for (var i = 0; i < 5; i++)
                {
                    service.StepDriving(realWorld.MotorWorld);
                    service.AdvanceTick();
                }
                // Same order as FoundationApp.Update: world visuals, then
                // the presentation camera, then published facts.
                realWorld.ApplyFrame(Snapshot());
                Physics.SyncTransforms();
                drivingCamera.ApplyFrame(Snapshot(), 0.05f);
                service.SetDrivingCameraFacts(facts);
                session = Snapshot().driving;
                if (session == null || session.motor == null) break;
                Assert.That(facts.mode, Is.EqualTo("chase"));
                Assert.That(facts.px,
                    Is.EqualTo(renderCamera.transform.position.x).Within(0.001f),
                    "published facts must describe the actual render-camera pose");
                Assert.That(facts.pz,
                    Is.EqualTo(renderCamera.transform.position.z).Within(0.001f));
                Assert.That(Mathf.Abs(facts.tx - session.motor.x), Is.LessThan(15f),
                    "the camera target must keep tracking the driven vehicle");
            }
            Assert.That(session, Is.Not.Null);
            Assert.That(session.motor, Is.Not.Null);
            Assert.That(wp, Is.EqualTo(route.Count),
                "the drive must reach every waypoint; stopped at index " + wp
                + " near " + (session.motor != null
                    ? session.motor.x + "," + session.motor.z : "?"));
            Assert.That(session.motor.grounded, Is.True,
                "the whole driven route stays on the real course surface");
            Assert.That(facts.ty, Is.EqualTo(session.motor.y + 1.2f).Within(1.5f),
                "the camera target tracks the vehicle height end to end");
        }

        [Test]
        public void ChaseCameraFollowsVehicleUpRealRampAndDownOnLanding()
        {
            SpawnRealCourse();

            // Ride the real ramp's surface line (x=-60): the 14-degree
            // wedge emerges from the ground near z=24 and crests near
            // z=38 around y=3.4, then the landing apron drops back to the
            // flat. Placed motor states — the current motor sweep cannot
            // step onto the lip, recorded as a Task-4 finding.
            var maxFactsPy = -999f;
            for (var i = 0; i <= 40; i++)
            {
                var z = 20f + i * 0.6f;                  // z 20 -> 44
                var surfaceAt = z < 22f || z > 38f
                    ? 0f
                    : Mathf.Max(0f, 1.51f + 0.242f * (z - 30f));
                var state = FakeDriving(-60f, surfaceAt, z, 0f);
                drivingCamera.ApplyFrame(state, 0.1f);
                Assert.That(drivingCamera.Facts.mode, Is.EqualTo("chase"));
                maxFactsPy = Mathf.Max(maxFactsPy, drivingCamera.Facts.py);
            }
            Assert.That(maxFactsPy, Is.GreaterThan(7f),
                "the chase camera must climb with the vehicle over the "
                + "real ramp crest (peak py " + maxFactsPy + ")");

            // Landing: the motor returns to the flat past the crest and
            // the camera settles back to ground-level framing.
            for (var i = 0; i < 15; i++)
                drivingCamera.ApplyFrame(FakeDriving(-60f, 0f, 46f + i, 0f), 0.1f);
            Assert.That(drivingCamera.Facts.py, Is.LessThan(6.5f),
                "after the landing the camera settles back down, py="
                + drivingCamera.Facts.py);
            Assert.That(drivingCamera.Facts.ty, Is.EqualTo(1.2f).Within(0.6f),
                "the smoothed target re-converges to the landed vehicle");
        }

        [Test]
        public void VehicleVisualInterpolatesBetweenCommittedTicks()
        {
            // The sim commits at 30 Hz but ApplyPose runs per rendered
            // frame: the visual must interpolate between the last two
            // commits (alpha = elapsed fraction of the current tick), not
            // snap — stepping is the reported constant vibration.
            var go = new GameObject("interp-car");
            loose.Add(go);
            var presentation = go.AddComponent<VehiclePresentation>();
            var car = Asset<VehicleDefinitionAsset>(a =>
            {
                a.vehicleId = "car-01";
                a.silhouette = "car";
                a.leanDegrees = 4f;
                a.bodyScale = new Vector3(1.8f, 1.1f, 3.2f);
                a.bodyColor = Color.white;
            });
            presentation.SetVehicle(car);
            var first = new MotorState { x = 0f, y = 0f, z = 0f, heading = 0f, tick = 10 };
            var second = new MotorState { x = 0f, y = 0f, z = 0.6f, heading = 0f, tick = 11 };
            presentation.ApplyPose(first, 0f, 1f);
            presentation.ApplyPose(second, 0f, 0.5f);
            Assert.That(go.transform.position.z, Is.EqualTo(0.3f).Within(0.001f),
                "mid-tick the visual sits halfway between commits");
            presentation.ApplyPose(second, 0f, 1f);
            Assert.That(go.transform.position.z, Is.EqualTo(0.6f).Within(0.001f),
                "alpha 1 lands exactly on the latest committed pose");
            // Recovery-scale jump: snap, never sweep across the course.
            var respawn = new MotorState { x = 40f, y = 0f, z = 40f, heading = 0f, tick = 12 };
            presentation.ApplyPose(respawn, 0f, 0f);
            Assert.That(go.transform.position.x, Is.EqualTo(40f).Within(0.001f),
                "a respawn-scale jump must snap instead of sweeping");
        }

        // --- Task 8: horizon-stable camera + bounded physical feedback ----

        // A committed physical sample for camera tests: the legacy
        // envelope plus the VehiclePhysicsState the physical backend
        // publishes. The camera reads the envelope — its orientation
        // must never couple to the chassis attitude.
        private static MotorState PhysicalMotor(Vector3 pos, float heading,
            Quaternion attitude, bool grounded, int wheelCount = 4)
        {
            var physics = new VehiclePhysicsState
            {
                wheels = new WheelPhysicsState[wheelCount],
                grounded = grounded
            };
            for (var i = 0; i < wheelCount; i++)
                physics.wheels[i] = new WheelPhysicsState { index = i };
            physics.TrySetAttitude(attitude.x, attitude.y, attitude.z,
                attitude.w);
            return new MotorState
            {
                x = pos.x, y = pos.y, z = pos.z, heading = heading,
                grounded = grounded, physics = physics
            };
        }

        private static FoundationState FakePhysical(MotorState motor,
            string camera = "chase", bool reducedShake = false,
            float effectsVolume = 0.8f)
        {
            return new FoundationState
            {
                language = "en",
                screen = "driving",
                draftProfile = new ProfileSave
                {
                    camera = camera,
                    reducedCameraShake = reducedShake,
                    effectsVolume = effectsVolume
                },
                driving = new DrivingSessionState
                {
                    phase = "running",
                    motor = motor
                }
            };
        }

        // The chassis may be fully rolled — the camera still frames the
        // horizon. Camera roll is always zero: the look rotation is built
        // from world up, never from the body attitude.
        [Test]
        public void ChaseCameraStaysHorizonStableWhenVehicleRolls()
        {
            var rolled = FakePhysical(PhysicalMotor(
                new Vector3(0f, 0.95f, 0f), 0f,
                Quaternion.Euler(0f, 0f, 70f), grounded: true));
            for (var i = 0; i < 10; i++)
                drivingCamera.ApplyFrame(rolled, 0.1f);

            var roll = renderCamera.transform.rotation.eulerAngles.z;
            if (roll > 180f) roll -= 360f;
            Assert.That(Mathf.Abs(roll), Is.LessThan(0.5f),
                "a 70-degree chassis roll must not tilt the camera's "
                + "horizon (camera roll " + roll + " deg)");

            var overhead = FakePhysical(PhysicalMotor(
                new Vector3(0f, 0.95f, 0f), 0f,
                Quaternion.Euler(0f, 0f, -60f), grounded: true),
                camera: "overhead");
            for (var i = 0; i < 10; i++)
                drivingCamera.ApplyFrame(overhead, 0.1f);
            roll = renderCamera.transform.rotation.eulerAngles.z;
            if (roll > 180f) roll -= 360f;
            Assert.That(Mathf.Abs(roll), Is.LessThan(0.5f),
                "the overhead mode keeps the horizon level under roll too "
                + "(camera roll " + roll + " deg)");
        }

        // Touchdown dips the camera a bounded amount and plays a thump
        // scaled by the effects volume; "reduce camera shake" shrinks the
        // dip instead of deleting it. The dip decays back to the clean
        // chase framing instead of leaving a permanent offset.
        [Test]
        public void LandingFeedbackIsBoundedAndHonoursPreferences()
        {
            // Converge on settled framing first.
            var settled = FakePhysical(PhysicalMotor(
                new Vector3(0f, 0.75f, 0f), 0f,
                Quaternion.identity, grounded: true));
            for (var i = 0; i < 10; i++)
                drivingCamera.ApplyFrame(settled, 0.1f);
            Assert.That(drivingCamera.FeedbackDip, Is.EqualTo(0f)
                .Within(0.001f), "no feedback on flat settled driving");

            // Airborne with a real downward speed, then touchdown.
            var airborne = FakePhysical(PhysicalMotor(
                new Vector3(0f, 2.5f, 3f), 0f,
                Quaternion.identity, grounded: false));
            airborne.driving.motor.airSeconds = 0.6f;
            airborne.driving.motor.verticalSpeed = -8f;
            drivingCamera.ApplyFrame(airborne, 0.1f);
            var landed = FakePhysical(PhysicalMotor(
                new Vector3(0f, 0.75f, 4f), 0f,
                Quaternion.identity, grounded: true));
            drivingCamera.ApplyFrame(landed, 0.1f);

            Assert.That(drivingCamera.FeedbackDip, Is.GreaterThan(0.02f),
                "touchdown feeds a bounded dip into the camera");
            Assert.That(drivingCamera.FeedbackDip,
                Is.LessThanOrEqualTo(drivingCamera.MaxFeedbackDip + 0.001f),
                "the dip is bounded by the authored cap");
            Assert.That(drivingCamera.LastThumpVolume, Is.GreaterThan(0.01f),
                "a real landing thumps at the profile's effects volume");

            var peak = drivingCamera.FeedbackDip;
            for (var i = 0; i < 60; i++)
            {
                drivingCamera.ApplyFrame(landed, 0.05f);
                Assert.That(drivingCamera.FeedbackDip,
                    Is.LessThanOrEqualTo(peak + 0.001f),
                    "the dip only decays after touchdown");
            }
            Assert.That(drivingCamera.FeedbackDip, Is.LessThan(0.01f),
                "the feedback decays back to clean framing");

            // Volume zero silences the thump but the dip still answers.
            var quietGo = new GameObject("presenter-quiet");
            loose.Add(quietGo);
            var quiet = quietGo.AddComponent<DrivingCamera>();
            var quietSettled = FakePhysical(PhysicalMotor(
                new Vector3(0f, 0.75f, 0f), 0f,
                Quaternion.identity, grounded: true),
                effectsVolume: 0f);
            for (var i = 0; i < 6; i++)
                quiet.ApplyFrame(quietSettled, 0.1f);
            var quietAir = FakePhysical(PhysicalMotor(
                new Vector3(0f, 2.5f, 3f), 0f,
                Quaternion.identity, grounded: false),
                effectsVolume: 0f);
            quietAir.driving.motor.airSeconds = 0.6f;
            quietAir.driving.motor.verticalSpeed = -8f;
            quiet.ApplyFrame(quietAir, 0.1f);
            quiet.ApplyFrame(FakePhysical(PhysicalMotor(
                new Vector3(0f, 0.75f, 4f), 0f,
                Quaternion.identity, grounded: true),
                effectsVolume: 0f), 0.1f);
            Assert.That(quiet.FeedbackDip, Is.GreaterThan(0.02f),
                "the camera still answers a landing with volume muted");
            Assert.That(quiet.LastThumpVolume, Is.EqualTo(0f),
                "effectsVolume=0 must silence the touchdown thump");

            // Reduced shake shrinks the same landing's dip measurably.
            var reducedGo = new GameObject("presenter-reduced-fb");
            loose.Add(reducedGo);
            var reduced = reducedGo.AddComponent<DrivingCamera>();
            var reducedSettled = FakePhysical(PhysicalMotor(
                new Vector3(0f, 0.75f, 0f), 0f,
                Quaternion.identity, grounded: true),
                reducedShake: true);
            for (var i = 0; i < 6; i++)
                reduced.ApplyFrame(reducedSettled, 0.1f);
            var reducedAir = FakePhysical(PhysicalMotor(
                new Vector3(0f, 2.5f, 3f), 0f,
                Quaternion.identity, grounded: false),
                reducedShake: true);
            reducedAir.driving.motor.airSeconds = 0.6f;
            reducedAir.driving.motor.verticalSpeed = -8f;
            reduced.ApplyFrame(reducedAir, 0.1f);
            reduced.ApplyFrame(FakePhysical(PhysicalMotor(
                new Vector3(0f, 0.75f, 4f), 0f,
                Quaternion.identity, grounded: true),
                reducedShake: true), 0.1f);
            Assert.That(reduced.FeedbackDip, Is.GreaterThan(0.001f),
                "reduced shake softens the dip, it does not delete it");
            Assert.That(reduced.FeedbackDip,
                Is.LessThan(peak * 0.5f),
                "reduced shake shrinks the landing dip: reduced="
                + reduced.FeedbackDip + " full=" + peak);
        }

        // Committed suspension compression feeds a small, bounded dip —
        // grounded wheels riding deep in their travel register, airborne
        // or full-droop wheels register nothing.
        [Test]
        public void SuspensionCompressionFeedbackIsBoundedAndShakeAware()
        {
            var riding = FakePhysical(PhysicalMotor(
                new Vector3(0f, 0.6f, 0f), 0f,
                Quaternion.identity, grounded: true));
            foreach (var wheel in riding.driving.motor.physics.wheels)
            {
                wheel.grounded = true;
                wheel.compression = 0.25f;
            }
            for (var i = 0; i < 30; i++)
                drivingCamera.ApplyFrame(riding, 0.05f);

            Assert.That(drivingCamera.FeedbackDip, Is.GreaterThan(0.01f),
                "sustained compression registers a bounded camera dip");
            var sustained = drivingCamera.FeedbackDip;
            Assert.That(sustained,
                Is.LessThanOrEqualTo(drivingCamera.MaxFeedbackDip + 0.001f));

            // Wheels at full droop (ungrounded) contribute nothing.
            var droop = FakePhysical(PhysicalMotor(
                new Vector3(0f, 2.5f, 0f), 0f,
                Quaternion.identity, grounded: false));
            droop.driving.motor.airSeconds = 0.2f;
            for (var i = 0; i < 60; i++)
                drivingCamera.ApplyFrame(droop, 0.05f);
            Assert.That(drivingCamera.FeedbackDip, Is.LessThan(sustained * 0.5f),
                "droop releases the compression dip");

            var reducedGo = new GameObject("presenter-reduced-susp");
            loose.Add(reducedGo);
            var reduced = reducedGo.AddComponent<DrivingCamera>();
            var reducedRide = FakePhysical(PhysicalMotor(
                new Vector3(0f, 0.6f, 0f), 0f,
                Quaternion.identity, grounded: true),
                reducedShake: true);
            foreach (var wheel in reducedRide.driving.motor.physics.wheels)
            {
                wheel.grounded = true;
                wheel.compression = 0.25f;
            }
            for (var i = 0; i < 30; i++)
                reduced.ApplyFrame(reducedRide, 0.05f);
            Assert.That(reduced.FeedbackDip,
                Is.LessThan(sustained * 0.5f),
                "reduced shake shrinks the sustained suspension dip too");
        }
    }
}
