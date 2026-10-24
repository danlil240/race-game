using FamilyRacing.Core.Driving;
using UnityEngine;

namespace FamilyRacing
{
    // Per-substep raycast-wheel force application, following the spec's
    // six steps verbatim:
    //   1. cast each authored mount down the suspension axis (self /
    //      triggers excluded by the vehicle layer; wall-like normals
    //      rejected)
    //   2. nonnegative support from compression + compression velocity,
    //      history invalidated on contact loss and seeded on first touch
    //   3. contact-point velocity -> drive split / lateral arrest /
    //      no-reverse brake / bounded slope hold
    //   4. one combined mu*N friction budget via LimitTireForces
    //   5. support and tire forces at physically consistent points via
    //      ForceMode.Force, then drag/rolling resistance and bounded
    //      assistance — no velocity writes, no canned flip torque
    //   6. the caller (PhysicsVehicleSimulation) applies every racer's
    //      forces, then simulates the shared scene once per substep.
    // All per-wheel state rides preallocated scratch arrays on the body;
    // nothing here allocates after warmup.
    public static class WheelContactSolver
    {
        // Drivable contact normals must point mostly up: a side-on wall or
        // ceiling is never a tire contact (curb tops still count).
        private const float MinDrivableNormalY = 0.5f;
        // Below this longitudinal contact speed the brake hold may cancel
        // gravity along the slope within the grip budget.
        private const float BrakeHoldMaxSpeed = 0.6f;
        // Drift progressively unloads rear lateral grip — a demand scale
        // that ramps in over DriftRampSeconds, never a sideways velocity
        // kick. Release ramps the grip back the same way.
        private const float DriftRearLateralScale = 0.35f;
        private const float DriftRampSeconds = 0.5f;
        // Rolling resistance never acts below this speed: a stopped car
        // must not creep.
        private const float RollingResistanceMinSpeed = 0.4f;

        // `modifiers` is the authoritative rule layer's per-tick scaling
        // (M5 Task 5 gadgets): the acceleration multiplier scales peak
        // drive force, the cap multiplier scales the tapered cruise/boost
        // target — both inside the ordinary tire-force budget, so the
        // effect can never bypass the shared contact model. Null means
        // neutral.
        public static void ApplySubstepForces(PhysicsVehicleBody vehicle,
            DriverInput input, float dt, PhysicsCourseWorld world,
            MotorModifiers modifiers = null)
        {
            var tuning = vehicle.tuning;
            var body = vehicle.body;
            var physics = vehicle.physics;
            var transform = body.transform;
            var bodyUp = transform.up;
            var bodyForward = transform.forward;

            // --- steering assistance: authority curve + rate limit -------
            // SteeringTarget returns the speed-tapered target; the Runtime
            // side owns the steerRateRadiansPerSecond limiter and the
            // filteredSteering history that capture/restore round-trips.
            var flatForward = Vector3.ProjectOnPlane(bodyForward,
                Vector3.up);
            var flatSpeed = flatForward.sqrMagnitude > 1e-6f
                ? Vector3.Dot(body.linearVelocity, flatForward.normalized)
                : 0f;
            var steerTarget = VehicleAssistRules.SteeringTarget(
                input.Steering, flatSpeed, tuning);
            var maxDelta = tuning.steerRateRadiansPerSecond * dt;
            var filtered = Mathf.Clamp(steerTarget,
                physics.filteredSteering - maxDelta,
                physics.filteredSteering + maxDelta);
            physics.filteredSteering = filtered;

            // --- pass 1: suspension rays -----------------------------------
            var groundedDriven = 0;
            var groundedWheels = 0;
            var specs = tuning.wheels;
            var rayReach = tuning.suspensionRestLength
                + tuning.suspensionExtraDroop;
            for (var i = 0; i < specs.Length; i++)
            {
                var spec = specs[i];
                var wheel = physics.wheels[i];
                var origin = transform.TransformPoint(
                    new Vector3(spec.mountX, spec.mountY, spec.mountZ));
                var reach = rayReach + spec.radius;
                RaycastHit hit;
                var grounded = world.Raycast(origin, -bodyUp, out hit, reach,
                    PhysicsCourseWorld.WheelRaycastMask)
                    && hit.normal.y >= MinDrivableNormalY;
                vehicle.wheelHit[i] = grounded;
                if (!grounded)
                {
                    // Contact loss invalidates damper history.
                    wheel.grounded = false;
                    wheel.contactValid = false;
                    wheel.compression = 0f;
                    wheel.previousCompression = 0f;
                    wheel.normalLoad = 0f;
                    wheel.slipLongitudinal = 0f;
                    wheel.slipLateral = 0f;
                    vehicle.wheelNormalLoad[i] = 0f;
                    continue;
                }

                var springLength = hit.distance - spec.radius;
                if (springLength < 0f) springLength = 0f;
                var compression = Mathf.Clamp(
                    tuning.suspensionRestLength - springLength,
                    0f, tuning.suspensionRestLength + tuning.suspensionExtraDroop);
                // First contact seeds previousCompression = compression so
                // a landing never produces a false damper impulse.
                var compressionSpeed = wheel.contactValid
                    ? (compression - wheel.previousCompression) / dt
                    : 0f;
                // Called only for ray-hit (grounded) wheels: the function
                // is free to return damper support inside its own clamp.
                var normalLoad = VehicleForceModel.SuspensionForce(
                    compression, compressionSpeed, tuning.springRatePerWheel,
                    tuning.damperPerWheel, tuning.maxNormalLoadPerWheel);

                // Steered direction on the contact plane.
                var steer = spec.steerable ? filtered : 0f;
                var steeredForward = steer != 0f
                    ? Quaternion.AngleAxis(steer * Mathf.Rad2Deg, bodyUp)
                        * bodyForward
                    : bodyForward;
                var fwd = Vector3.ProjectOnPlane(steeredForward, hit.normal);
                if (fwd.sqrMagnitude < 1e-8f) fwd = Vector3.ProjectOnPlane(
                    bodyForward, hit.normal);
                fwd.Normalize();
                var right = Vector3.Cross(hit.normal, fwd);
                var contactVelocity = body.GetPointVelocity(hit.point);

                vehicle.wheelPoint[i] = hit.point;
                vehicle.wheelNormal[i] = hit.normal;
                vehicle.wheelForward[i] = fwd;
                vehicle.wheelRight[i] = right;
                vehicle.wheelCompression[i] = compression;
                vehicle.wheelNormalLoad[i] = normalLoad;
                vehicle.wheelLongSpeed[i] = Vector3.Dot(contactVelocity, fwd);
                vehicle.wheelLatSpeed[i] = Vector3.Dot(contactVelocity, right);
                groundedWheels++;
                if (spec.driven) groundedDriven++;

                wheel.grounded = true;
                wheel.contactX = hit.point.x;
                wheel.contactY = hit.point.y;
                wheel.contactZ = hit.point.z;
                wheel.normalX = hit.normal.x;
                wheel.normalY = hit.normal.y;
                wheel.normalZ = hit.normal.z;
                wheel.previousCompression = wheel.contactValid
                    ? wheel.compression
                    : compression;
                wheel.compression = compression;
                wheel.contactValid = true;
                wheel.steering = steer;
                wheel.spin += vehicle.wheelLongSpeed[i]
                    / Mathf.Max(0.01f, spec.radius) * dt;
                wheel.normalLoad = normalLoad;
                wheel.slipLongitudinal = vehicle.wheelLongSpeed[i];
                wheel.slipLateral = vehicle.wheelLatSpeed[i];

                // Step 2 support force at the contact point.
                if (normalLoad > 0f)
                    body.AddForceAtPosition(hit.normal * normalLoad,
                        hit.point, ForceMode.Force);
            }

            // --- pass 2: tire forces within one combined mu*N budget -------
            var braking = input.Brake > 0f;
            var boost = vehicle.live.boostActive;
            var gadgetAccel = modifiers != null
                ? modifiers.accelerationMultiplier : 1f;
            var gadgetCap = modifiers != null
                ? modifiers.speedCapMultiplier : 1f;
            var cruiseTarget = (boost
                ? tuning.boostSpeed : tuning.cruiseSpeed) * gadgetCap;
            // Any positive brake suppresses engine/boost drive force.
            var throttle = braking ? 0f : input.Throttle;
            // Drift engagement is a progressive ramp on the persisted
            // state: the first instants of a held drift keep most of the
            // rear axle's bite, so the slide builds instead of kicking.
            physics.driftAmount = Mathf.Clamp01(physics.driftAmount
                + (input.Drift ? dt : -dt) / DriftRampSeconds);
            var rearLateralScale = Mathf.Lerp(1f, DriftRearLateralScale,
                physics.driftAmount);
            var gravity = -VehiclePhysicsTuning.StandardGravity;
            for (var i = 0; i < specs.Length; i++)
            {
                if (!vehicle.wheelHit[i]) continue;
                var spec = specs[i];
                var normalLoad = vehicle.wheelNormalLoad[i];
                if (normalLoad <= 0f) continue;
                var fwd = vehicle.wheelForward[i];
                var longSpeed = vehicle.wheelLongSpeed[i];
                // The mass this contact supports: the grip and brake caps
                // scale with instantaneous load, not static weight.
                var supportedMass = normalLoad
                    / VehiclePhysicsTuning.StandardGravity;

                var longitudinal = 0f;
                if (throttle > 0f && spec.driven && groundedDriven > 0)
                {
                    // Cruise/boost are tapered controller targets, never
                    // velocity clamps: full demand below 75% of target,
                    // tapering to zero at target, allowing coasting and
                    // downhill overspeed untouched.
                    var taper = Mathf.Clamp01(
                        (cruiseTarget - longSpeed) / (0.25f * cruiseTarget));
                    longitudinal += tuning.peakDriveForce * gadgetAccel
                        * throttle * taper / groundedDriven;
                }
                // Brake demand opposes travel at EVERY grounded wheel —
                // unscoped by drive (a non-driven front wheel still
                // brakes); demand is split across grounded contacts.
                if (braking && groundedWheels > 0)
                {
                    var requested = tuning.peakBrakeForce * input.Brake
                        / groundedWheels;
                    longitudinal += VehicleForceModel.BrakeForce(
                        longSpeed, requested, supportedMass, dt);
                    // Slope hold: near rest a held brake may cancel the
                    // tangential gravity component within the same grip
                    // budget — it can never drive the car.
                    if (Mathf.Abs(longSpeed) < BrakeHoldMaxSpeed)
                    {
                        var gAlong = Vector3.Dot(
                            new Vector3(0f, gravity, 0f), fwd);
                        var hold = -gAlong * supportedMass;
                        longitudinal += Mathf.Clamp(hold,
                            -requested, requested);
                    }
                }
                // Lateral demand arrests contact slide within one step;
                // the friction ellipse below bounds what is delivered.
                var lateral = -vehicle.wheelLatSpeed[i] * supportedMass / dt;
                if (!spec.steerable)
                    lateral *= rearLateralScale;
                var tire = VehicleForceModel.LimitTireForces(
                    longitudinal, lateral, normalLoad,
                    tuning.frictionCoefficient);
                var force = fwd * tire.longitudinal
                    + vehicle.wheelRight[i] * tire.lateral;
                if (force.sqrMagnitude > 0f)
                    body.AddForceAtPosition(force, vehicle.wheelPoint[i],
                        ForceMode.Force);
            }

            // --- drag, rolling resistance, bounded assistance --------------
            var velocity = body.linearVelocity;
            var speedSq = velocity.sqrMagnitude;
            if (tuning.dragCoefficient > 0f && speedSq > 0f)
                body.AddForce(-velocity * (tuning.dragCoefficient
                    * Mathf.Sqrt(speedSq)), ForceMode.Force);
            var flatSpeedMag = new Vector2(velocity.x, velocity.z).magnitude;
            if (tuning.rollingResistance > 0f && flatSpeedMag
                > RollingResistanceMinSpeed)
            {
                var totalNormal = 0f;
                for (var i = 0; i < specs.Length; i++)
                    totalNormal += vehicle.wheelNormalLoad[i];
                if (totalNormal > 0f)
                {
                    var dir = new Vector3(velocity.x, 0f, velocity.z)
                        / flatSpeedMag;
                    body.AddForce(-dir * (tuning.rollingResistance
                        * totalNormal), ForceMode.Force);
                }
            }
            // Ground assists are contact-governed and cut at the authored
            // fall angle: past it the vehicle is falling/rolling and the
            // recovery rules own the outcome — assist torque must never
            // hold a fallen pose.
            var groundAssist = VehicleAssistRules.CanApplyGroundAssist(
                    physics)
                && Vector3.Dot(bodyUp, Vector3.up)
                    >= Mathf.Cos(tuning.assistFallAngleRadians);
            // Optional anti-roll spring coupling left/right compression on
            // each axle — assists never author attitude and stay off
            // unless authored (antiRollRate > 0).
            if (tuning.antiRollRate > 0f && specs.Length == 4
                && groundAssist)
            {
                ApplyAntiRollPair(vehicle, 0, 1);
                ApplyAntiRollPair(vehicle, 2, 3);
            }
            // Bounded roll damping: torque opposing the roll-rate
            // component of angular velocity, capped so it can never
            // prevent every rollover.
            if (tuning.rollDamping > 0f && groundAssist)
            {
                var rollRate = Vector3.Dot(body.angularVelocity, bodyForward);
                var torque = Mathf.Clamp(-rollRate * tuning.rollDamping,
                    -tuning.rollDamping * 3f, tuning.rollDamping * 3f);
                body.AddTorque(bodyForward * torque, ForceMode.Force);
            }
            // The two-inline-contact layout is a motorcycle: the bounded
            // PD lean/balance controller applies torque here — contact-
            // aware, capped, cut past the authored fall angle, and
            // carrying all history on the persisted physics state.
            MotorcycleBalanceController.Apply(vehicle, input, tuning, dt);

            // Injected test forces last so oracles stay pure.
            if (vehicle.debugForceActive)
            {
                if (vehicle.debugForceAtOffset)
                    body.AddForceAtPosition(vehicle.debugForce,
                        transform.TransformPoint(
                            vehicle.debugForceLocalOffset),
                        ForceMode.Force);
                else
                    body.AddForce(vehicle.debugForce, ForceMode.Force);
            }
        }

        private static void ApplyAntiRollPair(PhysicsVehicleBody vehicle,
            int left, int right)
        {
            if (!vehicle.wheelHit[left] || !vehicle.wheelHit[right]) return;
            var difference = vehicle.wheelCompression[left]
                - vehicle.wheelCompression[right];
            if (Mathf.Abs(difference) < 1e-5f) return;
            var force = difference * vehicle.tuning.antiRollRate * 0.5f;
            vehicle.body.AddForceAtPosition(
                -vehicle.wheelNormal[left] * force,
                vehicle.wheelPoint[left], ForceMode.Force);
            vehicle.body.AddForceAtPosition(
                vehicle.wheelNormal[right] * force,
                vehicle.wheelPoint[right], ForceMode.Force);
        }
    }
}
