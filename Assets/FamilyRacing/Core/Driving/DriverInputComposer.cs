using System;

namespace FamilyRacing.Core.Driving
{
    public static class DriverInputComposer
    {
        // Merges one keyboard sample and one touch sample: steering sums and
        // stays inside [-1, 1] (opposing inputs cancel), commands combine OR.
        public static DriverInput Compose(DriverInput keyboard, DriverInput touch)
        {
            var steering = keyboard.Steering + touch.Steering;
            if (float.IsNaN(steering) || float.IsInfinity(steering)) steering = 0f;
            return new DriverInput(
                Math.Clamp(steering, -1f, 1f),
                keyboard.Drift || touch.Drift,
                keyboard.Boost || touch.Boost,
                keyboard.Gadget || touch.Gadget,
                keyboard.Recover || touch.Recover,
                keyboard.ChangeCamera || touch.ChangeCamera,
                keyboard.Pause || touch.Pause);
        }

        public static DriverInput FromButtons(bool left, bool right, bool drift, bool boost, bool gadget)
        {
            var steering = (right ? 1f : 0f) - (left ? 1f : 0f);
            return new DriverInput(steering, drift, boost, gadget, false, false, false);
        }
    }
}
