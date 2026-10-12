using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Driving
{
    // A bounded velocity kick another racer applies to this vehicle during
    // a step. World-space impulse is expressed as a speed change in m/s;
    // the motor clamps the applied amount per step so a nudge can never
    // launch or teleport the body (bounded collision response invariant).
    public struct RacerNudge
    {
        public float impulseX;
        public float impulseZ;
        public string racerId;
    }

    // Future seam (post-M2): a contact source other racers implement to
    // push this vehicle. M2 ships no AI racers, so sessions always pass
    // null; the seam exists now so adding racers later changes neither the
    // step signature nor the command/observation contract.
    public interface IRacerContactSource
    {
        // Bounded nudges applying to `self` this step; implementations
        // return an empty list when no contact occurs.
        List<RacerNudge> NudgesFor(MotorState self);
    }

    // Data-driven drift, boost and landing rules for the arcade motor.
    // Charge is normalized to [0,1]: drifting and clean landings fill it,
    // a held boost drains it. Recovery-flagged movement is excluded from
    // every charge gain, so respawns and contact jitter can never mint
    // meter. All methods are pure: the motor integrates them into the
    // state copy it is already building for the step.
    public static class DriftBoostRules
    {
        // Per-step cap on the applied racer-nudge speed change (m/s).
        public const float NudgeMaxDeltaSpeed = 4f;

        // Drift engagement: held input, steering intent, grounded and at
        // least the tuning minimum speed — and not recovery-flagged, so
        // a respawn boundary can never emit a phantom driftStart. The
        // steering deflection required is authored in tuning so tapping
        // drift with neutral steering never engages.
        public static bool DriftEligible(DriverInput input, MotorState state,
            VehicleTuning tuning)
        {
            return input.Drift
                && state.grounded
                && !state.recovering
                && Math.Abs(input.Steering) > tuning.driftSteeringThreshold
                && state.speed >= tuning.driftMinSpeed;
        }

        // A press may start a boost when the latch is rearmed, the charge
        // clears the tuning minimum and the step is not recovery-flagged.
        public static bool CanStartBoost(MotorState state, DriverInput input,
            VehicleTuning tuning)
        {
            return input.Boost
                && !state.boostActive
                && !state.boostLatched
                && !state.recovering
                && state.boostCharge >= tuning.boostMinCharge;
        }

        // A running boost ends on release, empty charge, or its bounded
        // duration — whichever comes first.
        public static bool MustEndBoost(MotorState state, DriverInput input)
        {
            return !input.Boost
                || state.boostCharge <= 0f
                || state.boostSecondsRemaining <= 0f;
        }

        // Charge gained while not boosting: passive recharge plus the
        // drift charge rate. Recovery-flagged steps gain nothing so a
        // respawn boundary cannot be farmed.
        public static float ChargeGained(MotorState state, VehicleTuning tuning,
            float deltaSeconds)
        {
            if (state.recovering) return 0f;
            var gain = deltaSeconds / tuning.boostChargeSeconds;
            if (state.drifting) gain += tuning.driftChargePerSecond * deltaSeconds;
            return gain;
        }

        // A landing is creditable when the airborne episode was real:
        // longer than the tuning minimum so ground-contact jitter never
        // counts, it actually descended past the authored drop so a
        // sustained probe outage reads as no jump at all, it touched no
        // barrier in flight, and it is not recovery-flagged movement.
        public static bool LandingIsClean(MotorState state, bool episodeWallContact,
            VehicleTuning tuning, float landingGroundY)
        {
            return !state.recovering
                && !episodeWallContact
                && state.airSeconds >= tuning.landingMinAirSeconds
                && state.airPeakY - landingGroundY >= tuning.landingMinDrop;
        }
    }
}
