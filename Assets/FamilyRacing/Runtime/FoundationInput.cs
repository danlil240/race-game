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
        private bool touchLeft, touchRight, touchDrift, touchBoost, touchBrake;
        private bool pendingGadget, pendingRecover;
        private DriverInput lastSent = new DriverInput(0f, false, false, false, false, false, false);
        private bool sentNeutral = true;
        private double nextRefresh;
        private bool pausedSeen;
        private bool rearmRequired;
        private readonly Dictionary<string, string> parameters =
            new Dictionary<string, string>(System.StringComparer.Ordinal);
        private static readonly IReadOnlyDictionary<string, string> Empty =
            new Dictionary<string, string>(0);

        public void Bind(FoundationApp owner) { app = owner; }

        public bool PhysicalActive =>
            touchLeft || touchRight || touchDrift || touchBoost || touchBrake
            || !sentNeutral;

        // Touch button hooks called by FoundationShell pointer handlers.
        public void TouchLeft(bool held) { touchLeft = held; }
        public void TouchRight(bool held) { touchRight = held; }
        public void TouchDrift(bool held) { touchDrift = held; }
        public void TouchBoost(bool held) { touchBoost = held; }
        public void TouchBrake(bool held) { touchBrake = held; }
        public void TouchGadget() { pendingGadget = true; }
        public void TouchRecover() { pendingRecover = true; }

        public void ResetPhysical()
        {
            touchLeft = touchRight = touchDrift = touchBoost = touchBrake = false;
            pendingGadget = pendingRecover = false;
        }

        // Focus loss / disable cleanup: a key still physically held must be
        // released before any held action is dispatched again — the 500 ms
        // hold refresh must not reassert it on its own.
        public void RequireRearm()
        {
            ResetPhysical();
            MarkStale();
            rearmRequired = true;
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
            var service = app != null ? app.Service : null;
            var paused = service != null && service.Paused;
            if (agentOwned)
            {
                // The authenticated controller owns driving input; suppress
                // physical driving without dispatching neutrals that would
                // overwrite its holds. Marking stale means a still-held key
                // re-dispatches when ownership returns — unless a pause
                // happened in between, which requires rearm like a resume.
                pausedSeen = paused;
                ResetPhysical();
                MarkStale();
                return;
            }
            // The LAN race screen drives the same input path as the solo
            // race: the sample reaches the host through NetworkRaceBridge,
            // and a network pause suppresses held input via RequireRearm.
            if (screen != "sandbox" && screen != "driving" && screen != "lan-race")
            {
                ResetPhysical();
                MarkStale();
                return;
            }

            var keyboard = Keyboard.current;
            float keySteer = 0f;
            bool keyDrift = false, keyBoost = false, keyBrake = false;
            bool keyGadget = false, keyRecover = false;
            if (keyboard != null)
            {
                if (keyboard.leftArrowKey.isPressed || keyboard.aKey.isPressed) keySteer -= 1f;
                if (keyboard.rightArrowKey.isPressed || keyboard.dKey.isPressed) keySteer += 1f;
                keyDrift = keyboard.spaceKey.isPressed;
                keyBoost = keyboard.shiftKey.isPressed || keyboard.leftShiftKey.isPressed;
                // S / Down is the held brake demand — the same control the
                // touch brake button drives, composed by max.
                keyBrake = keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed;
                keyGadget = keyboard.eKey.wasPressedThisFrame;
                keyRecover = keyboard.rKey.wasPressedThisFrame;
                // Camera and pause stay immediate application actions,
                // including while the game is paused.
                if (keyboard.cKey.wasPressedThisFrame) app.Dispatch("camera.toggle", Empty);
                if (keyboard.escapeKey.wasPressedThisFrame) app.Dispatch("pause.toggle", Empty);
            }
            paused = service != null && service.Paused;
            if (paused)
            {
                // While paused, held driving input is suppressed and its
                // dispatched state forgotten — resuming requires rearm.
                pausedSeen = true;
                ResetPhysical();
                MarkStale();
                return;
            }
            if (pausedSeen)
            {
                // Resume/focus regain — including a pause that just ended on
                // this Pump's pause.toggle dispatch: physical controls must
                // release and rearm before submitting another held action.
                pausedSeen = false;
                rearmRequired = true;
            }
            var keyInput = new DriverInput(keySteer, keyDrift, keyBoost,
                keyGadget, keyRecover, false, false, null, keyBrake ? 1f : 0f);
            // Opposing touches cancel rather than preferring one side.
            var touchInput = new DriverInput(
                (touchRight ? 1f : 0f) - (touchLeft ? 1f : 0f),
                touchDrift, touchBoost, pendingGadget, pendingRecover,
                false, false, null, touchBrake ? 1f : 0f);
            pendingGadget = pendingRecover = false;
            var composed = DriverInputComposer.Compose(keyInput, touchInput);

            if (rearmRequired)
            {
                var neutral = keySteer == 0f && !keyDrift && !keyBoost
                    && !keyBrake && !keyGadget && !keyRecover
                    && !touchLeft && !touchRight && !touchDrift && !touchBoost
                    && !touchBrake && composed.Steering == 0f
                    && composed.Brake == 0f;
                if (!neutral) return; // swallow until every control releases
                rearmRequired = false;
            }

            var now = Time.unscaledTimeAsDouble * 1000.0;
            var unchanged = composed.Equals(lastSent);
            if (unchanged && (sentNeutral || now < nextRefresh)) return;
            DispatchInput(composed);
            lastSent = composed;
            var neutralSent = composed.Steering == 0f && !composed.Drift
                && !composed.Boost && composed.Brake == 0f
                && !composed.HasThrottleOverride;
            sentNeutral = neutralSent;
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
            parameters["brake"] = input.Brake.ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture);
            // An absent throttle key keeps automatic throttle — only an
            // explicit override is serialized, and the stale key from an
            // earlier dispatch must be removed so it cannot linger.
            if (input.HasThrottleOverride)
                parameters["throttle"] = input.Throttle.ToString("0.###",
                    System.Globalization.CultureInfo.InvariantCulture);
            else
                parameters.Remove("throttle");
            parameters["durationTicks"] = "30";
            app.Dispatch("input.set", parameters);
        }
    }
}
