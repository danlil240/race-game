using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FamilyRacing
{
    // Physical keyboard + touch control adapter. Composed DriverInput values
    // are translated into input.set commands through the same Execute path
    // agents use; commands dispatch on value changes (plus a bounded hold
    // refresh before the 150-tick lease expires), never per frame while held
    // unchanged. While the agent controller owns driving input, physical
    // driving is suppressed without clearing the agent's holds.
    public sealed class FoundationInput : MonoBehaviour
    {
        private const int HoldRefreshSecondsNumerator = 500; // ms between hold refresh dispatches

        private FoundationApp app;
        private bool touchLeft, touchRight, touchDrift, touchBoost;
        private bool pendingGadget, pendingRecover;
        private DriverInput lastSent = new DriverInput(0f, false, false, false, false, false, false);
        private bool sentNeutral = true;
        private double nextRefresh;
        private readonly Dictionary<string, string> parameters =
            new Dictionary<string, string>(System.StringComparer.Ordinal);
        private static readonly IReadOnlyDictionary<string, string> Empty =
            new Dictionary<string, string>(0);

        public void Bind(FoundationApp owner) { app = owner; }

        public bool PhysicalActive =>
            touchLeft || touchRight || touchDrift || touchBoost || !sentNeutral;

        // Touch button hooks called by FoundationShell pointer handlers.
        public void TouchLeft(bool held) { touchLeft = held; }
        public void TouchRight(bool held) { touchRight = held; }
        public void TouchDrift(bool held) { touchDrift = held; }
        public void TouchBoost(bool held) { touchBoost = held; }
        public void TouchGadget() { pendingGadget = true; }
        public void TouchRecover() { pendingRecover = true; }

        public void ResetPhysical()
        {
            touchLeft = touchRight = touchDrift = touchBoost = false;
            pendingGadget = pendingRecover = false;
        }

        // Forget the last dispatched value so re-entry into the sandbox (or
        // ownership returning) re-dispatches the current held state instead
        // of leaking or suppressing a hold across the transition.
        private void MarkStale()
        {
            lastSent = new DriverInput(0f, false, false, false, false, false, false);
            sentNeutral = true;
            nextRefresh = 0;
        }

        private void OnDisable()
        {
            // Service-side held input must not outlive the component: clear
            // through the shared command path so the trace shows a boundary.
            if (app != null && app.Service != null)
                app.Dispatch("input.clear", Empty);
            ResetPhysical();
            MarkStale();
        }

        public void Pump(bool agentOwned, string screen)
        {
            if (agentOwned)
            {
                // The authenticated controller owns driving input; suppress
                // physical driving without dispatching neutrals that would
                // overwrite its holds. Marking stale means a still-held key
                // re-dispatches when ownership returns.
                ResetPhysical();
                MarkStale();
                return;
            }
            if (screen != "sandbox")
            {
                ResetPhysical();
                MarkStale();
                return;
            }

            var keyboard = Keyboard.current;
            float keySteer = 0f;
            bool keyDrift = false, keyBoost = false;
            bool keyGadget = false, keyRecover = false;
            if (keyboard != null)
            {
                if (keyboard.leftArrowKey.isPressed || keyboard.aKey.isPressed) keySteer -= 1f;
                if (keyboard.rightArrowKey.isPressed || keyboard.dKey.isPressed) keySteer += 1f;
                keyDrift = keyboard.spaceKey.isPressed;
                keyBoost = keyboard.shiftKey.isPressed || keyboard.leftShiftKey.isPressed;
                keyGadget = keyboard.eKey.wasPressedThisFrame;
                keyRecover = keyboard.rKey.wasPressedThisFrame;
                if (keyboard.cKey.wasPressedThisFrame) app.Dispatch("camera.toggle", Empty);
                if (keyboard.escapeKey.wasPressedThisFrame) app.Dispatch("pause.toggle", Empty);
            }
            var keyInput = new DriverInput(keySteer, keyDrift, keyBoost, keyGadget, keyRecover, false, false);
            // Opposing touches cancel rather than preferring one side.
            var touchInput = new DriverInput(
                (touchRight ? 1f : 0f) - (touchLeft ? 1f : 0f),
                touchDrift, touchBoost, pendingGadget, pendingRecover, false, false);
            pendingGadget = pendingRecover = false;
            var composed = DriverInputComposer.Compose(keyInput, touchInput);

            var now = Time.unscaledTimeAsDouble * 1000.0;
            var unchanged = composed.Equals(lastSent);
            if (unchanged && (sentNeutral || now < nextRefresh)) return;
            DispatchInput(composed);
            lastSent = composed;
            var neutral = composed.Steering == 0f && !composed.Drift && !composed.Boost;
            sentNeutral = neutral;
            nextRefresh = now + HoldRefreshSecondsNumerator;
        }

        private void DispatchInput(DriverInput input)
        {
            parameters["steering"] = input.Steering.ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture);
            parameters["drift"] = input.Drift ? "true" : "false";
            parameters["boost"] = input.Boost ? "true" : "false";
            parameters["gadget"] = input.Gadget ? "true" : "false";
            parameters["recover"] = input.Recover ? "true" : "false";
            parameters["changeCamera"] = "false";
            parameters["pause"] = "false";
            parameters["durationTicks"] = "30";
            app.Dispatch("input.set", parameters);
        }
    }
}
