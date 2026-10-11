using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace FamilyRacing.Tests
{
    // UI/command parity on the screens the settings fixture does not cover:
    // profile cards, the profiles-screen language toggle, navigation/back
    // buttons, and the sandbox touch controls. Every rendered control must
    // drive the same service state the equivalent command produces — pointer
    // down/up/cancel on hold buttons maps to Touch*(bool), click buttons map
    // to TouchGadget()/TouchRecover() or direct Dispatch calls. These are
    // synthetic UI Toolkit pointer events in the editor — not OS/physical
    // touch.
    // Editor-only fixture: the generated PanelSettings loads via
    // AssetDatabase, so standalone player/device runs must exclude it.
    [UnityPlatform(RuntimePlatform.WindowsEditor, RuntimePlatform.OSXEditor,
        RuntimePlatform.LinuxEditor)]
    public sealed class FoundationScreensPlayTests
    {
        private const string PanelSettingsPath =
            "Assets/FamilyRacing/Generated/FoundationPanelSettings.asset";

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

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            profilesDirectory = Path.Combine(Path.GetTempPath(),
                "fr-screens-ui-" + Guid.NewGuid().ToString("N"));
            store = new ProfileStore(profilesDirectory, new UnityProfileSerializer());
            var runId = "run-" + Guid.NewGuid().ToString("N");
            service = new FoundationService(store, "en", runId);

            // The host stays inactive so FoundationApp.Awake never runs and
            // never opens the real Application.persistentDataPath store;
            // Dispatch is a plain method over the injected composition root.
            hostGo = new GameObject("screens-parity-host");
            hostGo.SetActive(false);
            app = hostGo.AddComponent<FoundationApp>();
            Inject(app, "service", service);
            Inject(app, "runId", runId);

            // The sandbox hold buttons call app.Input.Touch*; the adapter
            // field must be injected too. It lives on its own GameObject —
            // FoundationInput has no Awake — and Pump is invoked manually.
            inputGo = new GameObject("screens-input-adapter");
            driverInput = inputGo.AddComponent<FoundationInput>();
            driverInput.Bind(app);
            Inject(app, "driverInput", driverInput);

#if UNITY_EDITOR
            panelSettings = UnityEngine.Object.Instantiate(
                UnityEditor.AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath));
#endif
            Assert.That(panelSettings, Is.Not.Null,
                "The generated PanelSettings asset must load; the clone is destroyed in teardown.");

            shellGo = new GameObject("screens-parity-shell");
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
            if (shellGo != null) UnityEngine.Object.Destroy(shellGo);
            if (inputGo != null) UnityEngine.Object.Destroy(inputGo);
            if (hostGo != null) UnityEngine.Object.Destroy(hostGo);
            if (panelSettings != null) UnityEngine.Object.DestroyImmediate(panelSettings);
            try
            {
                if (Directory.Exists(profilesDirectory))
                    Directory.Delete(profilesDirectory, true);
            }
            catch (Exception) { }
        }

        // --- fixture helpers ------------------------------------------------

        private static void Inject(FoundationApp target, string field, object value)
        {
            typeof(FoundationApp).GetField(field,
                BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
        }

        private FoundationState Snapshot()
        {
            return app.Service.Snapshot();
        }

        private void Refresh()
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

        // The same pointer down/up pair a real touch produces — the path
        // Clickable.ProcessDown/UpEvent actually consumes.
        private static void Press(Button button)
        {
            var center = button.worldBound.center;
            var touch = new Touch
            {
                fingerId = 0,
                position = center,
                phase = TouchPhase.Began
            };
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

        // The primary touch path a real finger produces: GetPooled(Touch)
        // stamps pointerId = fingerId + touchPointerIdBase and button = 0
        // (left). Hold buttons receive these in the TrickleDown phase —
        // AddHoldButton registers there specifically because Button's
        // built-in Clickable stops immediate propagation of left/touch
        // downs in the bubble phase (see FoundationShell.AddHoldButton).
        private static Touch TouchAt(VisualElement element, TouchPhase phase)
        {
            return new Touch
            {
                fingerId = 0,
                position = element.worldBound.center,
                phase = phase
            };
        }

        // Hold buttons track pointer IDs across down/up/cancel, so each edge
        // is dispatched separately with the same fingerId.
        private static void PointerDownOn(VisualElement element)
        {
            using (var down = PointerDownEvent.GetPooled(
                TouchAt(element, TouchPhase.Began), EventModifiers.None))
            {
                down.target = element;
                element.SendEvent(down);
            }
        }

        private static void PointerUpOn(VisualElement element)
        {
            using (var up = PointerUpEvent.GetPooled(
                TouchAt(element, TouchPhase.Ended), EventModifiers.None))
            {
                up.target = element;
                element.SendEvent(up);
            }
        }

        private static void PointerCancelOn(VisualElement element)
        {
            using (var cancel = PointerCancelEvent.GetPooled(
                TouchAt(element, TouchPhase.Canceled), EventModifiers.None))
            {
                cancel.target = element;
                element.SendEvent(cancel);
            }
        }

        private void PumpSandbox()
        {
            driverInput.Pump(false, "sandbox");
        }

        private IEnumerator GoToSandbox()
        {
            var created = app.Dispatch("profile.create",
                Params(("slot", "0"), ("language", "en")));
            Assert.That(created.status, Is.EqualTo("applied"));
            var nav = app.Dispatch("ui.navigate", Params(("screen", "sandbox")));
            Assert.That(nav.status, Is.EqualTo("applied"));
            Refresh();
            yield return WaitForWidget<Button>("input.left");
        }

        // --- tests ------------------------------------------------------------

        [UnityTest]
        public IEnumerator EmptySlotCardPressCreatesProfileAndOpensSettings()
        {
            Refresh();
            yield return WaitForWidget<Button>("profile.0.select");
            Assert.That(Snapshot().slots[0].status, Is.EqualTo("Missing"));

            Press(Widget<Button>("profile.0.select"));
            var state = Snapshot();
            Assert.That(state.slots[0].status, Is.EqualTo("Loaded"),
                "The card press must dispatch profile.create on an empty slot.");
            Assert.That(state.activeSlot, Is.EqualTo(0));
            Assert.That(state.savedProfile, Is.Not.Null);
            Assert.That(state.savedProfile.slot, Is.EqualTo(0));
            Assert.That(state.persistenceStatus, Is.EqualTo("saved"),
                "Card creation persists the new profile like the command does.");
            Assert.That(state.screen, Is.EqualTo("settings"),
                "profile.create from a card lands on settings, exactly like the command.");
        }

        [UnityTest]
        public IEnumerator LoadedSlotCardPressSelectsProfileAndEntersSandbox()
        {
            var created = app.Dispatch("profile.create",
                Params(("slot", "1"), ("language", "en")));
            Assert.That(created.status, Is.EqualTo("applied"));
            var back = app.Dispatch("ui.navigate", Params(("screen", "profiles")));
            Assert.That(back.status, Is.EqualTo("applied"));

            Refresh();
            yield return WaitForWidget<Button>("profile.1.select");
            Assert.That(Snapshot().slots[1].status, Is.EqualTo("Loaded"));

            Press(Widget<Button>("profile.1.select"));
            var state = Snapshot();
            Assert.That(state.activeSlot, Is.EqualTo(1),
                "The card press must dispatch profile.select on a loaded slot.");
            Assert.That(state.screen, Is.EqualTo("sandbox"),
                "Selecting a loaded profile enters the sandbox, same as commands.");
            Assert.That(state.savedProfile.slot, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ProfilesLanguageToggleMatchesUiLanguageCommand()
        {
            Refresh();
            yield return WaitForWidget<Button>("settings.language.he");

            Press(Widget<Button>("settings.language.he"));
            var state = Snapshot();
            Assert.That(state.language, Is.EqualTo("he"),
                "The profiles language button must dispatch ui.language.");
            Assert.That(state.presentation.textDirection, Is.EqualTo("rtl"),
                "The command boundary must already carry RTL presentation.");
            Refresh();
            Assert.That(document.rootVisualElement.languageDirection,
                Is.EqualTo(LanguageDirection.RTL),
                "The Hebrew switch must rebuild the root RTL.");

            // The locale change rebuilt the tree; re-query before pressing.
            yield return WaitForWidget<Button>("settings.language.en");
            Press(Widget<Button>("settings.language.en"));
            state = Snapshot();
            Assert.That(state.language, Is.EqualTo("en"));
            Assert.That(state.presentation.textDirection, Is.EqualTo("ltr"));
            Refresh();
            Assert.That(document.rootVisualElement.languageDirection,
                Is.EqualTo(LanguageDirection.LTR));
        }

        [UnityTest]
        public IEnumerator BackButtonsNavigateLikeUiNavigate()
        {
            var created = app.Dispatch("profile.create",
                Params(("slot", "0"), ("language", "en")));
            Assert.That(created.status, Is.EqualTo("applied"));
            Assert.That(Snapshot().screen, Is.EqualTo("settings"));

            Refresh();
            yield return WaitForWidget<Button>("action.back");
            Press(Widget<Button>("action.back"));
            Assert.That(Snapshot().screen, Is.EqualTo("profiles"),
                "The settings back button must produce the same state as "
                + "ui.navigate to profiles.");

            // The loaded card enters the sandbox; its back button must also
            // produce the ui.navigate profiles state.
            Refresh();
            yield return WaitForWidget<Button>("profile.0.select");
            Press(Widget<Button>("profile.0.select"));
            Assert.That(Snapshot().screen, Is.EqualTo("sandbox"));

            Refresh();
            yield return WaitForWidget<Button>("action.back");
            Press(Widget<Button>("action.back"));
            Assert.That(Snapshot().screen, Is.EqualTo("profiles"));
        }

        [UnityTest]
        public IEnumerator SandboxHoldButtonDownHoldsAndUpReleases()
        {
            yield return GoToSandbox();
            var left = Widget<Button>("input.left");

            PointerDownOn(left);
            PumpSandbox();
            var held = Snapshot();
            Assert.That(held.input.steering, Is.EqualTo(-1f).Within(0.001f),
                "Pointer down on input.left must hold steering -1 through the "
                + "same input.set the command produces.");
            Assert.That(held.input.drift, Is.False);
            Assert.That(driverInput.PhysicalActive, Is.True);

            PointerUpOn(left);
            PumpSandbox();
            Assert.That(Snapshot().input.steering, Is.EqualTo(0f).Within(0.001f),
                "Pointer up must release the hold — the next input.set is neutral.");
            Assert.That(driverInput.PhysicalActive, Is.False);

            // Parity with the command path: an explicit input.set produces
            // the same service state the pointer hold did.
            var viaCommand = app.Dispatch("input.set", Params(
                ("steering", "-1"), ("drift", "false"), ("boost", "false"),
                ("gadget", "false"), ("recover", "false"),
                ("changeCamera", "false"), ("pause", "false"),
                ("durationTicks", "30")));
            Assert.That(viaCommand.status, Is.EqualTo("applied"));
            var commanded = Snapshot().input;
            Assert.That(commanded.steering, Is.EqualTo(-1f).Within(0.001f));
            Assert.That(commanded.drift, Is.False);
            app.Dispatch("input.clear", new Dictionary<string, string>(0));
        }

        [UnityTest]
        public IEnumerator SandboxHoldButtonCancelReleasesHold()
        {
            yield return GoToSandbox();
            yield return WaitForWidget<Button>("input.drift");
            var drift = Widget<Button>("input.drift");

            PointerDownOn(drift);
            PumpSandbox();
            Assert.That(Snapshot().input.drift, Is.True,
                "Pointer down on input.drift must hold drift:true.");

            PointerCancelOn(drift);
            PumpSandbox();
            Assert.That(Snapshot().input.drift, Is.False,
                "PointerCancelEvent must release the hold the same as pointer up.");
            Assert.That(driverInput.PhysicalActive, Is.False);
        }

        [UnityTest]
        public IEnumerator SandboxGadgetButtonPressPulsesOnce()
        {
            yield return GoToSandbox();
            yield return WaitForWidget<Button>("input.gadget");
            Press(Widget<Button>("input.gadget"));
            PumpSandbox();
            Assert.That(Snapshot().input.gadget, Is.True,
                "The gadget button press must carry gadget:true once, like E.");

            PumpSandbox();
            Assert.That(Snapshot().input.gadget, Is.False,
                "The pending touch flag is consumed — the next dispatch drops it.");
        }

        [UnityTest]
        public IEnumerator SandboxActionButtonsDispatchToggleCommands()
        {
            yield return GoToSandbox();
            Assert.That(Snapshot().draftProfile.camera, Is.EqualTo("chase"));

            yield return WaitForWidget<Button>("action.camera");
            Press(Widget<Button>("action.camera"));
            var state = Snapshot();
            Assert.That(state.draftProfile.camera, Is.EqualTo("overhead"),
                "The camera button must dispatch camera.toggle.");
            Assert.That(state.pendingSettings, Is.True);

            yield return WaitForWidget<Button>("action.pause");
            Press(Widget<Button>("action.pause"));
            Assert.That(Snapshot().paused, Is.True,
                "The pause button must dispatch pause.toggle.");
            Refresh();
            // The inline style the shell wrote — resolvedStyle lags a
            // style-resolution pass and would read stale this frame.
            Assert.That(Widget<VisualElement>("overlay.pause").style.display
                .value, Is.EqualTo(DisplayStyle.Flex),
                "A paused snapshot must surface the overlay on Refresh.");

            yield return WaitForWidget<Button>("action.pause");
            Press(Widget<Button>("action.pause"));
            Assert.That(Snapshot().paused, Is.False);
            Refresh();
            Assert.That(Widget<VisualElement>("overlay.pause").style.display
                .value, Is.EqualTo(DisplayStyle.None));
        }
    }
}
