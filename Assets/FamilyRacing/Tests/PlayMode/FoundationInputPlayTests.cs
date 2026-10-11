using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FamilyRacing.Core.Foundation;
using FamilyRacing.Core.Profiles;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FamilyRacing.Tests
{
    // Physical keyboard lifecycle for the FoundationInput adapter against a
    // real FoundationService and a synthetic ProfileStore in a temp dir: a
    // virtual Keyboard device feeds FoundationInput.Pump, which composes
    // through the same input.set/camera.toggle/pause.toggle command path
    // agents drive. Synchronous [Test] methods are deliberate: outside
    // [UnityTest], InputTestFixture.Press/Release run InputSystem.Update()
    // themselves, and an explicit InputSystem.Update() between reads stands
    // in for a player-loop frame so wasPressedThisFrame stays honest.
    // The host stays inactive so FoundationApp.Awake never runs and never
    // opens the real Application.persistentDataPath store; Dispatch is a
    // plain method over the injected composition root.
    public sealed class FoundationInputPlayTests : InputTestFixture
    {
        private string profilesDirectory;
        private FoundationService service;
        private GameObject hostGo;
        private FoundationApp app;
        private GameObject inputGo;
        private FoundationInput driverInput;
        private Keyboard keyboard;

        // Runs after InputTestFixture.Setup() (base-class [SetUp] methods go
        // first), so the reset input system sees exactly the keyboard added
        // here. Devices must not be created in [UnitySetUp]: enumerable
        // setup commands wrap the regular [SetUp] command and would run
        // before the fixture resets the system.
        [SetUp]
        public void CreateFixture()
        {
            keyboard = InputSystem.AddDevice<Keyboard>();
            keyboard.MakeCurrent();
            Assert.That(Keyboard.current, Is.SameAs(keyboard),
                "Pump reads Keyboard.current; the virtual device must be current.");

            profilesDirectory = Path.Combine(Path.GetTempPath(),
                "fr-input-" + Guid.NewGuid().ToString("N"));
            var runId = "run-" + Guid.NewGuid().ToString("N");
            service = new FoundationService(
                new ProfileStore(profilesDirectory, new UnityProfileSerializer()),
                "en", runId);

            hostGo = new GameObject("input-parity-host");
            hostGo.SetActive(false);
            app = hostGo.AddComponent<FoundationApp>();
            Inject(app, "service", service);
            Inject(app, "runId", runId);

            // The adapter sits on its own GameObject so enabled=false fires
            // OnDisable without waking FoundationApp.Awake. It stays active:
            // FoundationInput has no Awake/Update of its own.
            inputGo = new GameObject("input-adapter");
            driverInput = inputGo.AddComponent<FoundationInput>();
            driverInput.Bind(app);
            Inject(app, "driverInput", driverInput);
        }

        // Runs before InputTestFixture.TearDown() (derived-class [TearDown]
        // methods go first) so OnDisable still sees a live service.
        [TearDown]
        public void DestroyFixture()
        {
            if (inputGo != null) UnityEngine.Object.Destroy(inputGo);
            if (hostGo != null) UnityEngine.Object.Destroy(hostGo);
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

        private static Dictionary<string, string> Params(
            params (string name, string value)[] entries)
        {
            var map = new Dictionary<string, string>();
            foreach (var (name, value) in entries) map[name] = value;
            return map;
        }

        // One InputSystem update is one player-loop frame for control state:
        // wasPressedThisFrame reads true only until the next update runs.
        private static void NextFrame()
        {
            InputSystem.Update();
        }

        private void PumpSandbox()
        {
            driverInput.Pump(false, "sandbox");
        }

        private void GoToSandbox()
        {
            var created = app.Dispatch("profile.create",
                Params(("slot", "0"), ("language", "en")));
            Assert.That(created.status, Is.EqualTo("applied"));
            var nav = app.Dispatch("ui.navigate", Params(("screen", "sandbox")));
            Assert.That(nav.status, Is.EqualTo("applied"));
            Assert.That(Snapshot().screen, Is.EqualTo("sandbox"));
        }

        // --- tests ------------------------------------------------------------

        [Test]
        public void SteeringKeysComposeAndReleaseToNeutral()
        {
            GoToSandbox();

            Press(keyboard.leftArrowKey);
            PumpSandbox();
            Assert.That(Snapshot().input.steering, Is.EqualTo(-1f).Within(0.001f),
                "A held left arrow must produce steering -1 on the next Pump.");
            Assert.That(driverInput.PhysicalActive, Is.True);

            Release(keyboard.leftArrowKey);
            PumpSandbox();
            Assert.That(Snapshot().input.steering, Is.EqualTo(0f).Within(0.001f),
                "Releasing the key must dispatch a neutral input.set.");

            Press(keyboard.aKey);
            PumpSandbox();
            Assert.That(Snapshot().input.steering, Is.EqualTo(-1f).Within(0.001f),
                "A is the left-steer alias.");
            Release(keyboard.aKey);
            PumpSandbox();
            Assert.That(Snapshot().input.steering, Is.EqualTo(0f).Within(0.001f));

            Press(keyboard.rightArrowKey);
            PumpSandbox();
            Assert.That(Snapshot().input.steering, Is.EqualTo(1f).Within(0.001f),
                "A held right arrow must produce steering +1 on the next Pump.");
            Release(keyboard.rightArrowKey);
            PumpSandbox();
            Assert.That(Snapshot().input.steering, Is.EqualTo(0f).Within(0.001f));

            Press(keyboard.dKey);
            PumpSandbox();
            Assert.That(Snapshot().input.steering, Is.EqualTo(1f).Within(0.001f),
                "D is the right-steer alias.");
            Release(keyboard.dKey);
            PumpSandbox();
            Assert.That(Snapshot().input.steering, Is.EqualTo(0f).Within(0.001f));
            Assert.That(driverInput.PhysicalActive, Is.False,
                "A neutral dispatch must leave the adapter reporting inactive.");
        }

        [Test]
        public void OpposingArrowsCancelToNeutral()
        {
            GoToSandbox();
            Press(keyboard.leftArrowKey);
            PumpSandbox();
            Assert.That(Snapshot().input.steering, Is.EqualTo(-1f).Within(0.001f));

            // Left stays held; adding right must cancel like
            // DriverInputComposer, not prefer either side.
            Press(keyboard.rightArrowKey);
            PumpSandbox();
            Assert.That(Snapshot().input.steering, Is.EqualTo(0f).Within(0.001f),
                "Left+Right held together must compose steering 0.");
            Release(keyboard.leftArrowKey);
            Release(keyboard.rightArrowKey);
        }

        [Test]
        public void SpaceHoldsDriftAndShiftHoldsBoost()
        {
            GoToSandbox();
            Press(keyboard.spaceKey);
            PumpSandbox();
            Assert.That(Snapshot().input.drift, Is.True,
                "A held Space must produce drift:true.");

            Press(keyboard.leftShiftKey);
            PumpSandbox();
            var held = Snapshot().input;
            Assert.That(held.drift, Is.True);
            Assert.That(held.boost, Is.True,
                "A held Shift must produce boost:true.");

            Release(keyboard.spaceKey);
            Release(keyboard.leftShiftKey);
            PumpSandbox();
            var released = Snapshot().input;
            Assert.That(released.drift, Is.False);
            Assert.That(released.boost, Is.False);
        }

        [Test]
        public void GadgetAndRecoverKeysPulseForOneDispatch()
        {
            GoToSandbox();
            Press(keyboard.eKey);
            PumpSandbox();
            Assert.That(Snapshot().input.gadget, Is.True,
                "The Pump on the frame of an E press must carry gadget:true.");

            // The key is still held but the press edge is gone; the next
            // dispatch must drop the flag — the pulse is not latched.
            NextFrame();
            PumpSandbox();
            Assert.That(Snapshot().input.gadget, Is.False,
                "Holding E must not re-fire the gadget pulse.");
            Release(keyboard.eKey);

            Press(keyboard.rKey);
            PumpSandbox();
            Assert.That(Snapshot().input.recover, Is.True,
                "The Pump on the frame of an R press must carry recover:true.");
            NextFrame();
            PumpSandbox();
            Assert.That(Snapshot().input.recover, Is.False,
                "Holding R must not re-fire the recover pulse.");
            Release(keyboard.rKey);
        }

        [Test]
        public void CameraKeyTogglesDraftCamera()
        {
            GoToSandbox();
            Assert.That(Snapshot().draftProfile.camera, Is.EqualTo("chase"));

            Press(keyboard.cKey);
            PumpSandbox();
            var state = Snapshot();
            Assert.That(state.draftProfile.camera, Is.EqualTo("overhead"),
                "C must dispatch camera.toggle through the shared path.");
            Assert.That(state.pendingSettings, Is.True);
            Release(keyboard.cKey);
        }

        [Test]
        public void EscapeKeyTogglesPauseAndClearsHeldInput()
        {
            GoToSandbox();
            driverInput.TouchLeft(true);
            PumpSandbox();
            Assert.That(Snapshot().input.steering, Is.EqualTo(-1f).Within(0.001f));

            Press(keyboard.escapeKey);
            PumpSandbox();
            var paused = Snapshot();
            Assert.That(paused.paused, Is.True,
                "Esc must dispatch pause.toggle.");
            Assert.That(paused.input.steering, Is.EqualTo(0f).Within(0.001f),
                "pause.toggle clears held input service-side.");
            Release(keyboard.escapeKey);
            NextFrame();

            Press(keyboard.escapeKey);
            PumpSandbox();
            Assert.That(Snapshot().paused, Is.False,
                "A second Esc must toggle pause off.");
            Release(keyboard.escapeKey);
        }

        [Test]
        public void AgentOwnedPumpSuppressesPhysicalDriving()
        {
            GoToSandbox();
            // Prime a controller hold the adapter must not overwrite; the
            // marker value distinguishes suppression from a physical -1
            // dispatch that would otherwise look identical.
            var primed = app.Dispatch("input.set", Params(
                ("steering", "1"), ("drift", "false"), ("boost", "false"),
                ("gadget", "false"), ("recover", "false"),
                ("changeCamera", "false"), ("pause", "false"),
                ("durationTicks", "30")));
            Assert.That(primed.status, Is.EqualTo("applied"));

            Press(keyboard.leftArrowKey);
            driverInput.Pump(true, "sandbox");
            Assert.That(Snapshot().input.steering, Is.EqualTo(1f).Within(0.001f),
                "Agent-owned Pump must suppress physical driving entirely: no "
                + "input.set and no clearing neutral reaches the service.");
            Assert.That(driverInput.PhysicalActive, Is.False,
                "Suppressed Pump must also drop pending physical state.");
            Release(keyboard.leftArrowKey);
        }

        [Test]
        public void NonSandboxPumpSuppressesDrivingAndDropsTouches()
        {
            // Still on the profiles screen — the service starts there.
            Assert.That(Snapshot().screen, Is.EqualTo("profiles"));
            driverInput.TouchLeft(true);
            Assert.That(driverInput.PhysicalActive, Is.True);

            driverInput.Pump(false, "profiles");
            Assert.That(Snapshot().input.steering, Is.EqualTo(0f).Within(0.001f),
                "Off-sandbox Pump must not dispatch driving input.");
            Assert.That(driverInput.PhysicalActive, Is.False,
                "Off-sandbox Pump must drop pending touch flags.");

            Press(keyboard.leftArrowKey);
            driverInput.Pump(false, "profiles");
            Assert.That(Snapshot().input.steering, Is.EqualTo(0f).Within(0.001f),
                "Held keys are suppressed off-sandbox too.");
            Release(keyboard.leftArrowKey);
        }

        [Test]
        public void DisableClearsServiceHeldInput()
        {
            GoToSandbox();
            driverInput.TouchLeft(true);
            PumpSandbox();
            Assert.That(Snapshot().input.steering, Is.EqualTo(-1f).Within(0.001f));
            Assert.That(driverInput.PhysicalActive, Is.True);

            driverInput.enabled = false;
            var state = Snapshot();
            Assert.That(state.input.steering, Is.EqualTo(0f).Within(0.001f),
                "OnDisable must clear service-held input through input.clear.");
            Assert.That(state.input.drift, Is.False);
            Assert.That(driverInput.PhysicalActive, Is.False,
                "No stale hold may survive the component boundary.");
        }

        [Test]
        public void ResetPhysicalDropsPendingTouchFlags()
        {
            GoToSandbox();
            // Marker input: if the Pump below dispatched anything, the marker
            // would move — the adapter's lastSent is still the initial
            // neutral, so a flag-free Pump dispatches nothing at all.
            var marker = app.Dispatch("input.set", Params(
                ("steering", "0.5"), ("drift", "false"), ("boost", "false"),
                ("gadget", "false"), ("recover", "false"),
                ("changeCamera", "false"), ("pause", "false"),
                ("durationTicks", "30")));
            Assert.That(marker.status, Is.EqualTo("applied"));

            driverInput.TouchLeft(true);
            driverInput.TouchGadget();
            Assert.That(driverInput.PhysicalActive, Is.True);
            driverInput.ResetPhysical();
            Assert.That(driverInput.PhysicalActive, Is.False);

            PumpSandbox();
            var state = Snapshot().input;
            Assert.That(state.steering, Is.EqualTo(0.5f).Within(0.001f),
                "ResetPhysical must drop pending touch flags so the next "
                + "Pump composes neutral and dispatches nothing.");
            Assert.That(state.gadget, Is.False,
                "A pending touch-gadget flag must not survive ResetPhysical.");
        }

        [Test]
        public void HeldKeyCannotRearmUntilReleasedAfterPause()
        {
            GoToSandbox();
            Press(keyboard.rightArrowKey);
            PumpSandbox();
            Assert.That(Snapshot().input.steering, Is.EqualTo(1f).Within(0.001f));

            // Pause with the arrow still held: held input neutralizes.
            Press(keyboard.escapeKey);
            PumpSandbox();
            var paused = Snapshot();
            Assert.That(paused.paused, Is.True);
            Assert.That(paused.input.steering, Is.EqualTo(0f).Within(0.001f));
            Release(keyboard.escapeKey);
            NextFrame();

            // Resume while the arrow is STILL held: the hold refresh must
            // not rearm it — no input.set may reach the service.
            Press(keyboard.escapeKey);
            PumpSandbox();
            Assert.That(Snapshot().paused, Is.False);
            Release(keyboard.escapeKey);
            NextFrame();
            for (var i = 0; i < 3; i++)
            {
                PumpSandbox();
                NextFrame();
            }
            Assert.That(Snapshot().input.steering, Is.EqualTo(0f).Within(0.001f),
                "A key held since before the pause must not rearm on resume.");

            // A real release re-arms it; the next press drives again.
            Release(keyboard.rightArrowKey);
            PumpSandbox();
            Press(keyboard.rightArrowKey);
            PumpSandbox();
            Assert.That(Snapshot().input.steering, Is.EqualTo(1f).Within(0.001f),
                "After a real release the key must arm and drive again.");
            Release(keyboard.rightArrowKey);
        }
    }
}
