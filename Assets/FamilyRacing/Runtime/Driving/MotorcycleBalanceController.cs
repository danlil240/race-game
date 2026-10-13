using FamilyRacing.Core.Driving;
using UnityEngine;

namespace FamilyRacing
{
    // Bounded lean/balance torque for the two-inline-contact motorcycle.
    //
    // A bike on two inline contact points has no lateral stance: without
    // assistance it is an inverted pendulum and tips over even parked.
    // This controller supplies the real PD torque the spec calls for —
    // contact-aware, capped, and never a velocity or attitude write:
    //
    //   * Supported (>=1 wheel ray on a drivable normal): a PD torque
    //     about the flattened forward axis follows the Core lean target
    //     atan(v*yawRate/g) bounded to assistMaxLeanRadians. The raw
    //     demand is slew-limited into the persisted filteredLeanTarget:
    //     a bike's coupled weave mode wobbles yawRate fast enough that
    //     chasing it raw pumps the roll loop into a limit cycle — the
    //     lagged demand preserves the steady lean while breaking that
    //     feedback. At low or zero speed the target is ~0, which is
    //     exactly the low-speed balance assistance — the same
    //     controller holds a parked bike upright.
    //   * The support frame is the averaged grounded-ray contact
    //     normal. The wheel solver already rejects normals with y < 0.5,
    //     so a wall-side chassis contact is never a balance reference —
    //     a bike lying on its side has no measured frame at all and is
    //     left to gravity (and the shared recovery rules).
    //   * Past assistFallAngleRadians measured against that support
    //     frame the assist is CUT until ordinary recovery — the
    //     proportional term can never hold a fallen pose.
    //   * Airborne there is no support frame: inertia plus bounded
    //     roll-rate and pitch-rate dampers only. No world-upright
    //     spring — a tossed bike keeps its tilt; the dampers only
    //     bleed rotation rates (the pitch damper stops ramp-lip
    //     endos, it can never right anything).
    //   * Torque is bounded by construction (P capped at the widest
    //     angle it may act over, D capped at a fixed error-rate): a
    //     hard lateral hit whose torque exceeds the cap still knocks
    //     the bike over.
    //
    // All history lives in VehiclePhysicsState — balancePrevError feeds
    // the derivative term, filteredLeanTarget carries the slew-limited
    // demand, and filteredRollRate feeds the airborne damper — so
    // capture/restore replays the controller exactly.
    // `input` is part of the mandated signature for steer-coupled
    // feed-forward; the current model's demand is the measured turn
    // rate, so it is deliberately unused.
    public static class MotorcycleBalanceController
    {
        // The support frame only counts while its averaged normal is
        // mostly up — the same bound the wheel solver uses to accept a
        // drivable contact. A wall (horizontal normal) is never a
        // balance reference.
        private const float MinSupportNormalY = 0.5f;
        // The derivative term is capped at this error rate: beyond it
        // the assist has already surrendered. Bounds the total torque
        // alongside the P cap so assistance is finite by construction.
        private const float MaxBalanceErrorRate = 4f; // rad/s
        // Slew limit on the lean demand the PD tracks. atan(v*w/g)
        // saturates near the 45-degree bound for almost any sustained
        // turn, so the demand itself is near-bang-bang; tracking it raw
        // lets the weave-mode yaw wobble (+-1.5 rad/s observed) pump
        // the roll loop. 2 rad/s reaches full lean in ~0.4 s — slow
        // enough to reject the wobble, fast enough for a family game.
        private const float MaxLeanTargetRate = 2f; // rad/s
        // The error derivative is only trusted once the persisted
        // contact-loss clock proves support was continuous across the
        // last readback — a fresh touchdown (or even a debounced ray
        // flicker) re-seeds the error history instead of kicking the
        // derivative off a stale value.
        public static void Apply(PhysicsVehicleBody vehicle,
            DriverInput input, VehiclePhysicsTuning tuning,
            float stepSeconds)
        {
            if (vehicle == null || tuning == null
                || tuning.wheels == null || tuning.wheels.Length != 2)
            {
                return;
            }
            var gain = tuning.balanceGain;
            var damping = tuning.balanceDamping;
            if (gain <= 0f && damping <= 0f) return;
            var dt = stepSeconds > 0f ? stepSeconds : 1f / 60f;
            _ = input; // measured turn rate is the demand today.

            var body = vehicle.body;
            var physics = vehicle.physics;
            var transform = body.transform;
            var bodyUp = transform.up;
            var bodyForward = transform.forward;

            // --- support frame ------------------------------------------
            // Average the normals of the grounded suspension rays; the
            // solver only marks a ray hit when normal.y >= 0.5, so walls
            // and ceilings can never enter the frame.
            var supportUp = Vector3.zero;
            var supported = 0;
            for (var i = 0; i < vehicle.wheelHit.Length; i++)
            {
                if (!vehicle.wheelHit[i]) continue;
                supportUp += vehicle.wheelNormal[i];
                supported++;
            }
            if (supported > 0)
            {
                supportUp.Normalize();
                if (supportUp.y < MinSupportNormalY) supported = 0;
            }

            // --- airborne: inertia + damping only ------------------------
            if (supported == 0)
            {
                // No measurable ground frame — the error history and
                // the lean demand are both meaningless until the next
                // touchdown re-seeds them.
                physics.balancePrevError = 0f;
                physics.filteredLeanTarget = 0f;
                if (damping > 0f)
                {
                    var dampCap = damping * MaxBalanceErrorRate;
                    // Roll damper on the persisted filtered estimate —
                    // same authority as before, replayable.
                    var rollDamp = Mathf.Clamp(
                        -physics.filteredRollRate * damping,
                        -dampCap, dampCap);
                    if (rollDamp != 0f)
                        body.AddTorque(bodyForward * rollDamp,
                            ForceMode.Force);
                    // Pitch damper on the raw body rate: leaving a ramp
                    // lip unloads the front wheel while the rear strut
                    // still pushes, so the bike exits with a nose-down
                    // rate that a roll-only damper cannot touch — the
                    // landing then endos. This is still "inertia and
                    // damping only": it slows the rotation, it can
                    // never rotate the bike toward upright.
                    var pitchAxis = transform.right;
                    var pitchRate = Vector3.Dot(body.angularVelocity,
                        pitchAxis);
                    var pitchDamp = Mathf.Clamp(
                        -pitchRate * damping, -dampCap, dampCap);
                    if (pitchDamp != 0f)
                        body.AddTorque(pitchAxis * pitchDamp,
                            ForceMode.Force);
                }
                return;
            }

            // --- supported: PD lean control -------------------------------
            // Flattened forward inside the support frame defines the
            // roll axis; a degenerate projection (bike standing on its
            // nose) has no measurable lean and gets no torque.
            var flatForward = Vector3.ProjectOnPlane(bodyForward,
                supportUp);
            if (flatForward.sqrMagnitude < 1e-6f)
            {
                physics.balancePrevError = 0f;
                physics.filteredLeanTarget = 0f;
                return;
            }
            flatForward.Normalize();
            var right = Vector3.Cross(supportUp, flatForward);
            var upness = Vector3.Dot(bodyUp, supportUp);
            var roll = Mathf.Atan2(Vector3.Dot(bodyUp, right), upness);

            // Past the authored fall angle the assist is cut until
            // ordinary recovery: the proportional term can never hold a
            // fallen pose — but the error history still tracks the real
            // lean so replay stays honest.
            var eligible = VehicleAssistRules.CanApplyGroundAssist(physics)
                && upness >= Mathf.Cos(tuning.assistFallAngleRadians);
            if (eligible)
            {
                var flatSpeed = Vector3.Dot(body.linearVelocity,
                    flatForward);
                var yawRate = Vector3.Dot(body.angularVelocity,
                    supportUp);
                var rawTarget = VehicleAssistRules.BikeLeanTarget(
                    flatSpeed, yawRate, tuning.assistMaxLeanRadians);
                // Slew-limit the demand into persisted history: the PD
                // tracks the lagged target, so the weave mode's yaw
                // wobble cannot pump the roll loop, and the lag itself
                // survives capture/restore.
                var targetStep = MaxLeanTargetRate * dt;
                var target = Mathf.Clamp(rawTarget,
                    physics.filteredLeanTarget - targetStep,
                    physics.filteredLeanTarget + targetStep);
                physics.filteredLeanTarget = target;
                var error = roll - target;
                // The derivative only derives while support is proven
                // continuous: contactLossSeconds stays 0 across the last
                // readback exactly when no ray gap (not even a debounced
                // one) intervened. After a touchdown or a flicker the
                // airborne branch re-seeded prevError to 0 — deriving
                // off it anyway would inject a capped kick, so the
                // first supported substep starts the derivative at
                // rest instead.
                var errorRate = physics.contactLossSeconds <= 0f
                    && dt > 0f
                    ? (error - physics.balancePrevError) / dt
                    : 0f;
                var pCap = gain * tuning.assistFallAngleRadians;
                var dCap = damping * MaxBalanceErrorRate;
                var torque = Mathf.Clamp(gain * error, -pCap, pCap)
                    + Mathf.Clamp(damping * errorRate, -dCap, dCap);
                if (torque != 0f)
                    body.AddTorque(flatForward * torque,
                        ForceMode.Force);
                physics.balancePrevError = error;
            }
            else
            {
                // Beyond the fall angle: keep the measured lean fresh so
                // an eligible substep never derives off a stale error,
                // and drop the stale lean demand with it.
                physics.balancePrevError = roll;
                physics.filteredLeanTarget = 0f;
            }
        }
    }
}
