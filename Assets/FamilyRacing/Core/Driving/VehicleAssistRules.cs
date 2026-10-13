using System;

namespace FamilyRacing.Core.Driving
{
    // Unity-free bounded driver assistance for the physical backend.
    // These functions return targets only — rate limiting, PD torque, and
    // contact-aware eligibility live in Runtime (Task 3). Assistance can
    // never author heading or attitude: every output is a bounded scalar
    // demand, finite for any input.
    public static class VehicleAssistRules
    {
        // Road-wheel steering target in radians. The [-1,1] input scales
        // linearly into an authority that tapers from steerMaxRadians at
        // rest down to the steerHighSpeedRadians floor at
        // steerReferenceSpeed and beyond, using speed magnitude so
        // reversing keeps the same cap. The runtime then rate-limits
        // toward this target at steerRateRadiansPerSecond.
        public static float SteeringTarget(float input, float speed,
            VehiclePhysicsTuning tuning)
        {
            if (tuning == null || !Finite(input)) return 0f;
            var referenceSpeed = tuning.steerReferenceSpeed;
            var lowSpeed = tuning.steerMaxRadians;
            var highSpeed = tuning.steerHighSpeedRadians;
            if (!Finite(referenceSpeed) || !Finite(lowSpeed) || !Finite(highSpeed))
            {
                return 0f;
            }
            var absSpeed = Finite(speed) ? Math.Abs(speed) : Math.Abs(referenceSpeed);
            var fraction = referenceSpeed > 0f
                ? absSpeed / referenceSpeed
                : 1f;
            var t = Math.Clamp(fraction, 0f, 1f);
            var floor = Math.Min(highSpeed, lowSpeed);
            var authority = lowSpeed + (floor - lowSpeed) * t;
            var target = Math.Clamp(input, -1f, 1f) * authority;
            return Finite(target) ? target : 0f;
        }

        // Bounded motorcycle lean target in radians. A steady turn needs
        // the gravity component of lean to supply centripetal demand:
        // tan(lean) = v*yawRate/g, so the target is atan of that demand and
        // its sign follows the turn direction. The result clamps to
        // +-maxLeanRadians (45 degrees in the authored presets); the PD
        // torque controller that follows this target lives in Runtime.
        public static float BikeLeanTarget(float speed, float yawRate,
            float maxLeanRadians)
        {
            if (!Finite(speed) || !Finite(yawRate) || !Finite(maxLeanRadians)
                || maxLeanRadians <= 0f)
            {
                return 0f;
            }
            var demand = (double)speed * yawRate
                / VehiclePhysicsTuning.StandardGravity;
            var target = (float)Math.Atan(demand);
            return Math.Clamp(target, -maxLeanRadians, maxLeanRadians);
        }

        // Ground-assist eligibility: bounded help (steering authority,
        // anti-roll coupling, roll damping) applies only while the
        // vehicle is contact-supported and not past the physical
        // "clearly fallen" limit — governed by contacts and the body's
        // own up axis, never by steering-triggered flip logic. The
        // up-axis test is analytic over the stored attitude quaternion:
        // up.y of a unit quaternion's rotated (0,1,0) is 1 - 2(x² + z²).
        // An overturned vehicle fails this and the recovery rules own
        // it; assists must never fight gravity to hold a fallen pose.
        public static bool CanApplyGroundAssist(VehiclePhysicsState state)
        {
            if (state == null || !state.grounded) return false;
            var upDot = 1f - 2f * (state.attitudeX * state.attitudeX
                + state.attitudeZ * state.attitudeZ);
            return Finite(upDot) && upDot > 0f;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
