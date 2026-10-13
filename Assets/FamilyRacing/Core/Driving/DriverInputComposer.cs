using System;

namespace FamilyRacing.Core.Driving
{
    public static class DriverInputComposer
    {
        // Merges one keyboard sample and one touch sample: steering sums and
        // stays inside [-1, 1] (opposing inputs cancel), commands combine OR.
        // Brake combines by max so either surface can demand full braking.
        // A throttle override survives an absent partner source; when both
        // sources override, the lower request wins.
        public static DriverInput Compose(DriverInput keyboard, DriverInput touch)
        {
            var steering = keyboard.Steering + touch.Steering;
            if (float.IsNaN(steering) || float.IsInfinity(steering)) steering = 0f;
            var brake = Math.Max(keyboard.Brake, touch.Brake);
            float? throttle = null;
            if (keyboard.HasThrottleOverride && touch.HasThrottleOverride)
                throttle = Math.Min(keyboard.Throttle, touch.Throttle);
            else if (keyboard.HasThrottleOverride) throttle = keyboard.Throttle;
            else if (touch.HasThrottleOverride) throttle = touch.Throttle;
            return new DriverInput(
                Math.Clamp(steering, -1f, 1f),
                keyboard.Drift || touch.Drift,
                keyboard.Boost || touch.Boost,
                keyboard.Gadget || touch.Gadget,
                keyboard.Recover || touch.Recover,
                keyboard.ChangeCamera || touch.ChangeCamera,
                keyboard.Pause || touch.Pause,
                throttle, brake);
        }

        public static DriverInput FromButtons(bool left, bool right, bool drift,
            bool boost, bool gadget, bool brake = false)
        {
            var steering = (right ? 1f : 0f) - (left ? 1f : 0f);
            return new DriverInput(steering, drift, boost, gadget, false,
                false, false, null, brake ? 1f : 0f);
        }
    }
}
