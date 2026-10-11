using System;

namespace FamilyRacing.Core.Driving
{
    // Immutable one-frame input sample shared by keyboard and touch sources.
    // Steering is clamped to [-1, 1]; non-finite values become neutral.
    public readonly struct DriverInput
    {
        public float Steering { get; }
        public bool Drift { get; }
        public bool Boost { get; }
        public bool Gadget { get; }
        public bool Recover { get; }
        public bool ChangeCamera { get; }
        public bool Pause { get; }

        public DriverInput(float steering, bool drift, bool boost, bool gadget,
            bool recover, bool changeCamera, bool pause)
        {
            Steering = float.IsNaN(steering) || float.IsInfinity(steering)
                ? 0f
                : Math.Clamp(steering, -1f, 1f);
            Drift = drift;
            Boost = boost;
            Gadget = gadget;
            Recover = recover;
            ChangeCamera = changeCamera;
            Pause = pause;
        }
    }
}
