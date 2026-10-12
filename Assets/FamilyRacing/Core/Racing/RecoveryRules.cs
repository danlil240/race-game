using System;
using FamilyRacing.Core.Driving;

namespace FamilyRacing.Core.Racing
{
    // Data-driven recovery thresholds carried by the event definition, so
    // tuning lives in content rather than literals in the rules code.
    public sealed class RecoverySettings
    {
        public float offCourseSeconds = 1.5f; // airborne/no-ground time before auto-recovery
        public float minCourseY = -5f;        // y below this is always off-course
        public float stuckSpeed = 0.75f;      // |speed| under this counts as stopped
        public float stuckSeconds = 3f;       // wall-pinned stopped time before auto-recovery
        public float beachedSeconds = 8f;     // stopped-on-ground time before auto-recovery

        public bool Validate(out string error)
        {
            error = null;
            if (!Positive(offCourseSeconds)) { error = "recovery.offCourseSeconds"; return false; }
            if (!Finite(minCourseY)) { error = "recovery.minCourseY"; return false; }
            if (!Positive(stuckSpeed)) { error = "recovery.stuckSpeed"; return false; }
            if (!Positive(stuckSeconds)) { error = "recovery.stuckSeconds"; return false; }
            if (!Positive(beachedSeconds)) { error = "recovery.beachedSeconds"; return false; }
            return true;
        }

        public RecoverySettings Copy()
        {
            return new RecoverySettings
            {
                offCourseSeconds = offCourseSeconds,
                minCourseY = minCourseY,
                stuckSpeed = stuckSpeed,
                stuckSeconds = stuckSeconds,
                beachedSeconds = beachedSeconds
            };
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool Positive(float value)
        {
            return Finite(value) && value > 0f;
        }
    }

    // Per-session automatic recovery evaluator. Called once per running
    // step after the motor commits (never on a recovery boundary — that
    // movement is flagged and excluded). Returns a trigger reason or null:
    // "invalidPose" for a non-finite motor state, "offCourse" when the
    // vehicle leaves the course volume (fell below minCourseY or stayed
    // airborne past offCourseSeconds), "stuck" when it grinds wall-pinned
    // below stuckSpeed for stuckSeconds, "beached" when it sits stopped on
    // open ground for the longer beachedSeconds window — the fallback for
    // obstacles a sweep never reports as wall contact. Manual recovery is
    // the session's pending-action path, not this evaluator.
    public sealed class RecoveryRules
    {
        private int stuckTicks;
        private int beachedTicks;

        public string Evaluate(MotorState motor, RecoverySettings settings, float deltaSeconds)
        {
            if (motor == null || settings == null) return null;
            if (!UsablePose(motor)) return "invalidPose";
            if (motor.y < settings.minCourseY) return "offCourse";
            if (!motor.grounded && motor.airSeconds >= settings.offCourseSeconds)
                return "offCourse";
            var stopped = motor.grounded
                && Math.Abs(motor.speed) < settings.stuckSpeed;
            if (stopped && motor.lastContact == "wall")
            {
                stuckTicks++;
                if (stuckTicks * deltaSeconds >= settings.stuckSeconds)
                {
                    stuckTicks = 0;
                    return "stuck";
                }
            }
            else
            {
                stuckTicks = 0;
            }
            // Beached: stopped on open ground (lastContact "ground") — the
            // stuck rule can't see it because no wall registered. The
            // recovery flag excludes respawn boundaries, whose signature
            // (grounded, stopped, ground contact) is identical.
            if (stopped && !motor.recovering && motor.lastContact == "ground")
            {
                beachedTicks++;
                if (beachedTicks * deltaSeconds >= settings.beachedSeconds)
                {
                    beachedTicks = 0;
                    return "beached";
                }
            }
            else
            {
                beachedTicks = 0;
            }
            return null;
        }

        public void Reset()
        {
            stuckTicks = 0;
            beachedTicks = 0;
        }

        private static bool UsablePose(MotorState motor)
        {
            return Finite(motor.x) && Finite(motor.y) && Finite(motor.z)
                && Finite(motor.heading) && Finite(motor.speed)
                && Finite(motor.lateralSpeed) && Finite(motor.verticalSpeed);
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
