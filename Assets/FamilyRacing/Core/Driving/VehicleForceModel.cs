using System;

namespace FamilyRacing.Core.Driving
{
    // Signed tire force demand at one grounded contact, in newtons along
    // the contact's surface basis: `longitudinal` runs along the (steered)
    // wheel direction on the hit plane, `lateral` across it. The runtime
    // backend maps the pair onto world directions; here they are pure
    // scalars sharing one mu*N friction budget.
    public readonly struct TireForces
    {
        public readonly float longitudinal;
        public readonly float lateral;

        public TireForces(float longitudinal, float lateral)
        {
            this.longitudinal = longitudinal;
            this.lateral = lateral;
        }
    }

    // Unity-free scalar force calculations for the physical vehicle
    // backend. The runtime evaluates these once per wheel per substep, so
    // every function is allocation-free and returns a finite value for any
    // input — degenerate or non-finite arguments collapse to zero rather
    // than producing NaN/Infinity. Validation of tuning values is the
    // VehiclePhysicsTuning.Validate authority; these functions stay
    // defensive without re-running it.
    public static class VehicleForceModel
    {
        // Nonnegative suspension support: N = clamp(k*x + c*xDot, 0,
        // maxLoad). The strut can push the chassis away from the contact
        // but never pull it down, so negative compression (lost contact)
        // and net damper rebound both land on zero. compressionSpeed is
        // the change in compression — positive while compressing — which
        // the caller derives from validated compression history.
        public static float SuspensionForce(float compression,
            float compressionSpeed, float spring, float damper, float maxLoad)
        {
            if (!Finite(compression) || !Finite(compressionSpeed)
                || !Finite(spring) || !Finite(damper) || !Finite(maxLoad)
                || maxLoad <= 0f)
            {
                return 0f;
            }
            var springRate = spring > 0f ? spring : 0f;
            var damperRate = damper > 0f ? damper : 0f;
            // Double intermediate: float products of large finite values
            // cannot overflow into Infinity before the cap is applied.
            var force = (double)springRate * compression
                + (double)damperRate * compressionSpeed;
            if (!(force > 0.0)) return 0f;
            return force >= maxLoad ? maxLoad : (float)force;
        }

        // One combined friction budget: the (longitudinal, lateral) pair
        // must fit inside mu*N. A tire cannot spend full grip on drive,
        // brake, and cornering independently — excess demand is scaled so
        // the result sits exactly on the ellipse while keeping its
        // direction. Zero or negative normal load (no contact) means no
        // traction at all.
        public static TireForces LimitTireForces(float longitudinal,
            float lateral, float normalLoad, float friction)
        {
            if (!Finite(longitudinal) || !Finite(lateral)
                || !Finite(normalLoad) || !Finite(friction)
                || normalLoad <= 0f || friction <= 0f)
            {
                return new TireForces(0f, 0f);
            }
            var limit = (double)friction * normalLoad;
            var magnitude = Math.Sqrt(
                (double)longitudinal * longitudinal
                + (double)lateral * lateral);
            if (!(magnitude > limit))
            {
                return new TireForces(longitudinal, lateral);
            }
            var scale = limit / magnitude;
            return new TireForces(
                (float)(longitudinal * scale), (float)(lateral * scale));
        }

        // Signed longitudinal brake force opposing the direction of
        // contact travel: negative while rolling forward, positive while
        // rolling backward, zero at rest. The magnitude is capped so the
        // applied impulse can at most bring contact speed to zero within
        // the step — |F|*dt/mass <= |contactSpeed| — it can never reverse
        // travel or act as a handbrake-launch. Slope hold near rest is a
        // separate contact demand the caller adds upstream of the combined
        // friction limit.
        public static float BrakeForce(float contactSpeed,
            float requestedForce, float supportedMass, float dt)
        {
            if (!Finite(contactSpeed) || !Finite(requestedForce)
                || !Finite(supportedMass) || !Finite(dt)
                || requestedForce <= 0f || supportedMass <= 0f || dt <= 0f
                || contactSpeed == 0f)
            {
                return 0f;
            }
            var noReverseCap = (double)supportedMass * Math.Abs(contactSpeed) / dt;
            var magnitude = Math.Min(requestedForce, noReverseCap);
            var signed = contactSpeed > 0f ? -magnitude : magnitude;
            return (float)signed;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
