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
        // Physical-backend thresholds: continuous overturned contact
        // support and continuous time spent outside every authored
        // recovery volume before the auto-recovery fires. Permitted
        // airspace inside a volume is not a failure.
        public float overturnedSeconds = 2f;
        public float outsideBoundsSeconds = 1.5f;

        public bool Validate(out string error)
        {
            error = null;
            if (!Positive(offCourseSeconds)) { error = "recovery.offCourseSeconds"; return false; }
            if (!Finite(minCourseY)) { error = "recovery.minCourseY"; return false; }
            if (!Positive(stuckSpeed)) { error = "recovery.stuckSpeed"; return false; }
            if (!Positive(stuckSeconds)) { error = "recovery.stuckSeconds"; return false; }
            if (!Positive(beachedSeconds)) { error = "recovery.beachedSeconds"; return false; }
            if (!Positive(overturnedSeconds)) { error = "recovery.overturnedSeconds"; return false; }
            if (!Positive(outsideBoundsSeconds)) { error = "recovery.outsideBoundsSeconds"; return false; }
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
                beachedSeconds = beachedSeconds,
                overturnedSeconds = overturnedSeconds,
                outsideBoundsSeconds = outsideBoundsSeconds
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
        private int outsideTicks;

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

        // Physical-backend evaluator: failure conditions come from real
        // contact episodes and driver demand, never from raw airtime or a
        // stopped pose alone.
        //   "invalidPose"   — non-finite motor state (unchanged).
        //   "offCourse"     — the kill height is an immediate failure
        //                     regardless of bounds volumes.
        //   "overturned"    — physics.overturnedSeconds of continuous
        //                     inverted contact support crosses
        //                     settings.overturnedSeconds (2 s authored).
        //   "outsideBounds" — the pose sits outside every authored
        //                     recoveryBounds volume for longer than
        //                     settings.outsideBoundsSeconds (1.5 s
        //                     authored); a permitted jump volume is not a
        //                     failure however long the flight. When no
        //                     bounds are authored the legacy airtime
        //                     window stays the fallback so an off-map
        //                     flight still terminates.
        //   "stuck"/"beached" — demand-based: drive must be requested
        //                     (effective throttle > 0 without a brake
        //                     hold) while progress is blocked. A held
        //                     brake or an explicit throttle-zero coast
        //                     parks deliberately and never recovers.
        // Manual recovery is the session's pending-action path, not this
        // evaluator.
        public string Evaluate(MotorState motor, DriverInput input,
            CourseDefinition course, RecoverySettings settings,
            float deltaSeconds)
        {
            if (motor == null || settings == null) return null;
            if (!UsablePose(motor)) return "invalidPose";
            if (motor.y < settings.minCourseY) return "offCourse";
            var physics = motor.physics;
            if (physics != null
                && physics.overturnedSeconds >= settings.overturnedSeconds)
                return "overturned";
            var bounds = course != null ? course.recoveryBounds : null;
            if (bounds != null && bounds.Count > 0)
            {
                var inside = false;
                for (var i = 0; i < bounds.Count; i++)
                {
                    var volume = bounds[i];
                    if (volume != null
                        && volume.Contains(motor.x, motor.y, motor.z))
                    {
                        inside = true;
                        break;
                    }
                }
                if (!inside)
                {
                    outsideTicks++;
                    if (outsideTicks * deltaSeconds
                            >= settings.outsideBoundsSeconds)
                    {
                        outsideTicks = 0;
                        return "outsideBounds";
                    }
                }
                else
                {
                    outsideTicks = 0;
                }
            }
            else if (!motor.grounded
                && motor.airSeconds >= settings.offCourseSeconds)
            {
                return "offCourse";
            }
            var driveRequested = input.Throttle > 0f && input.Brake <= 0f;
            var stopped = motor.grounded
                && Math.Abs(motor.speed) < settings.stuckSpeed
                && !motor.recovering;
            if (stopped && driveRequested
                && IsBlockedContact(motor.lastContact))
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
            if (stopped && driveRequested && motor.lastContact == "ground")
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

        // A wall, an obstacle or another racer counts as a blocking
        // contact for the demand-based stuck rule; "ground" is open
        // support handled by the beached fallback.
        private static bool IsBlockedContact(string contact)
        {
            return contact == "wall" || contact == "obstacle"
                || contact == "vehicle";
        }

        public void Reset()
        {
            stuckTicks = 0;
            beachedTicks = 0;
            outsideTicks = 0;
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
