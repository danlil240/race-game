using System;

namespace FamilyRacing.Core.Driving
{
    // Immutable one-frame input sample shared by keyboard and touch sources.
    // Steering is clamped to [-1, 1]; non-finite values become neutral.
    // Brake is a held demand in [0, 1]; any positive value suppresses
    // engine/boost drive force in the physical backend.
    //
    // Throttle is an explicit override in [0, 1] carried by a nullable
    // backing value, never an ambiguous zero float: a missing override —
    // including default(DriverInput) and every cleared/neutral sample —
    // means automatic throttle (effective Throttle is 1), while an explicit
    // override of 0 is a deliberate coast.
    public readonly struct DriverInput
    {
        public float Steering { get; }
        public bool Drift { get; }
        public bool Boost { get; }
        public bool Gadget { get; }
        public bool Recover { get; }
        public bool ChangeCamera { get; }
        public bool Pause { get; }
        public float Brake { get; }

        // True when a control source explicitly requested a throttle level.
        public bool HasThrottleOverride => throttleOverride.HasValue;

        // Effective throttle demand in [0, 1]: the override when present,
        // else 1 (automatic throttle).
        public float Throttle => throttleOverride ?? 1f;

        private readonly float? throttleOverride;

        // Backward-compatible constructor: automatic throttle and no brake,
        // so every pre-throttle caller keeps its exact meaning.
        public DriverInput(float steering, bool drift, bool boost, bool gadget,
            bool recover, bool changeCamera, bool pause)
            : this(steering, drift, boost, gadget, recover, changeCamera, pause,
                null, 0f)
        {
        }

        // Full control constructor. A null throttleOverride selects automatic
        // throttle; a present value (0 included, meaning coast) is clamped to
        // [0, 1]. Non-finite override/brake inputs are defensive-neutralized
        // the same way non-finite steering becomes neutral.
        public DriverInput(float steering, bool drift, bool boost, bool gadget,
            bool recover, bool changeCamera, bool pause,
            float? throttleOverride, float brake)
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
            Brake = float.IsNaN(brake) || float.IsInfinity(brake)
                ? 0f
                : Math.Clamp(brake, 0f, 1f);
            if (throttleOverride.HasValue)
            {
                var requested = throttleOverride.Value;
                this.throttleOverride = float.IsNaN(requested)
                    || float.IsInfinity(requested)
                    ? (float?)null
                    : Math.Clamp(requested, 0f, 1f);
            }
            else
            {
                this.throttleOverride = null;
            }
        }
    }
}
