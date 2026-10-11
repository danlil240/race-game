using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Driving
{
    // Unity-free serializable motor state. Positions are world metres on a
    // flat-y course; heading is yaw in radians where 0 faces +z and
    // positive turns toward +x. `speed` is signed forward speed along the
    // heading; `lateralSpeed` slides along the right vector (drift).
    // Every observation copy is independent — Snapshot()/Copy() never
    // alias live state.
    [Serializable]
    public sealed class MotorState
    {
        public float x;
        public float y;
        public float z;
        public float heading;
        public float speed;
        public float lateralSpeed;
        public float verticalSpeed;
        public bool grounded;
        public float airSeconds;
        public bool drifting;
        public int driftSign;
        public float boostCharge = 1f;
        public bool boostActive;
        public float boostSecondsRemaining;
        public bool recovering;
        public string lastContact = "none"; // "none" | "ground" | "wall" | "obstacle"
        public long tick;

        public MotorPose Pose
        {
            get { return new MotorPose { x = x, y = y, z = z, heading = heading }; }
        }

        public MotorState Copy()
        {
            return new MotorState
            {
                x = x,
                y = y,
                z = z,
                heading = heading,
                speed = speed,
                lateralSpeed = lateralSpeed,
                verticalSpeed = verticalSpeed,
                grounded = grounded,
                airSeconds = airSeconds,
                drifting = drifting,
                driftSign = driftSign,
                boostCharge = boostCharge,
                boostActive = boostActive,
                boostSecondsRemaining = boostSecondsRemaining,
                recovering = recovering,
                lastContact = lastContact,
                tick = tick
            };
        }
    }

    // Position + yaw sample used by probes, sweeps and (Task 2) gate
    // crossing tests. Deliberately a plain value struct.
    [Serializable]
    public struct MotorPose
    {
        public float x;
        public float y;
        public float z;
        public float heading;
    }

    // One ordered transient motor event inside a step. `value` carries a
    // kind-specific magnitude (e.g. wall impact speed); it is 0 when the
    // kind has none.
    public sealed class MotorEvent
    {
        public string kind;
        public float value;

        public MotorEvent(string kind, float value = 0f)
        {
            this.kind = kind;
            this.value = value;
        }
    }

    // Result of one ArcadeMotor.Step. A rejected step returns the previous
    // state (copied) unchanged and never advances the motor tick.
    public sealed class MotorStepResult
    {
        public MotorState state;
        public bool rejected;
        public string error = "";
        public List<MotorEvent> events = new List<MotorEvent>();

        public static MotorStepResult Rejected(MotorState previous, string error)
        {
            return new MotorStepResult
            {
                state = previous == null ? new MotorState() : previous.Copy(),
                rejected = true,
                error = error
            };
        }
    }
}
