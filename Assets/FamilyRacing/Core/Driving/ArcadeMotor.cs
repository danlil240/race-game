using System;

namespace FamilyRacing.Core.Driving
{
    // Kinematic arcade motor: automatic acceleration, speed-scaled
    // steering, drift lateral slip, timed boost, ground probing with
    // controlled airborne states and swept wall contact with one slide
    // iteration. All collision information arrives through IMotorWorld so
    // the same step runs against a fake plane world in tests and Unity
    // physics in the player. A rejected step never partially mutates: the
    // previous state is returned copied and unchanged.
    public static class ArcadeMotor
    {
        private const float SweepBackoffFraction = 0.02f;
        private const float DriftSteeringThreshold = 0.15f;
        private const float TwoPi = (float)(Math.PI * 2.0);

        public static MotorStepResult Step(MotorState previous, DriverInput input,
            VehicleTuning tuning, IMotorWorld world, float deltaSeconds)
        {
            if (tuning == null || !tuning.Validate(out _))
                return MotorStepResult.Rejected(previous, "invalidTuning");
            if (world == null)
                return MotorStepResult.Rejected(previous, "missingWorld");
            if (float.IsNaN(deltaSeconds) || float.IsInfinity(deltaSeconds) || deltaSeconds <= 0f)
                return MotorStepResult.Rejected(previous, "invalidDelta");
            if (previous == null)
                return MotorStepResult.Rejected(null, "missingState");

            var result = new MotorStepResult();
            var state = previous.Copy();
            var dt = deltaSeconds;

            // --- steering + drift ----------------------------------------
            var steer = input.Steering;
            var authority = Clamp01(Math.Abs(state.speed) / tuning.steeringReferenceSpeed);
            var wantDrift = input.Drift && state.grounded
                && Math.Abs(steer) > DriftSteeringThreshold
                && state.speed >= tuning.driftMinSpeed;
            if (wantDrift && !state.drifting)
            {
                state.drifting = true;
                state.driftSign = steer >= 0f ? 1 : -1;
                result.events.Add(new MotorEvent("driftStart"));
            }
            else if (!wantDrift && state.drifting)
            {
                state.drifting = false;
                state.driftSign = 0;
                result.events.Add(new MotorEvent("driftEnd"));
            }
            var rate = tuning.steeringRate * authority
                * (state.drifting ? tuning.driftSteeringFactor : 1f);
            state.heading = WrapAngle(state.heading + steer * rate * dt);

            // --- boost -----------------------------------------------------
            if (input.Boost && !state.boostActive && state.boostCharge >= 1f)
            {
                state.boostActive = true;
                state.boostSecondsRemaining = tuning.boostDurationSeconds;
                state.boostCharge = 0f;
                result.events.Add(new MotorEvent("boostStart"));
            }
            if (state.boostActive)
            {
                state.boostSecondsRemaining -= dt;
                if (state.boostSecondsRemaining <= 0f)
                {
                    state.boostActive = false;
                    state.boostSecondsRemaining = 0f;
                    result.events.Add(new MotorEvent("boostEnd"));
                }
            }
            else if (state.boostCharge < 1f)
            {
                state.boostCharge = Clamp01(state.boostCharge + dt / tuning.boostChargeSeconds);
            }

            // --- forward speed (auto-acceleration, traction requires ground)
            var speedLimit = state.boostActive ? tuning.boostMaxSpeed : tuning.maxSpeed;
            var accel = state.boostActive ? tuning.boostAcceleration : tuning.acceleration;
            if (state.grounded)
                state.speed = MoveToward(state.speed, speedLimit, accel * dt);
            // Airborne: no traction; momentum carries.

            // --- lateral slip ---------------------------------------------
            if (state.drifting)
            {
                state.lateralSpeed += state.driftSign * tuning.driftLateralAcceleration * dt;
                var cap = Math.Max(1f, Math.Abs(state.speed) * 0.8f);
                state.lateralSpeed = Math.Clamp(state.lateralSpeed, -cap, cap);
            }
            else
            {
                state.lateralSpeed = state.lateralSpeed * Math.Max(0f, 1f - tuning.grip * dt);
                if (Math.Abs(state.lateralSpeed) < 0.001f) state.lateralSpeed = 0f;
            }

            // --- horizontal displacement with swept contact -----------------
            var fx = (float)Math.Sin(state.heading);
            var fz = (float)Math.Cos(state.heading);
            var rx = fz;
            var rz = -fx;
            var dx = (fx * state.speed + rx * state.lateralSpeed) * dt;
            var dz = (fz * state.speed + rz * state.lateralSpeed) * dt;

            var pose = new MotorPose { x = state.x, y = state.y, z = state.z, heading = state.heading };
            var sweep = world.SweepBody(pose, dx, 0f, dz,
                tuning.halfWidth, tuning.halfLength, tuning.height);
            var contacted = false;
            float nx = 0f, nz = 0f;
            if (sweep.hit)
            {
                contacted = true;
                nx = sweep.normalX;
                nz = sweep.normalZ;
                var allowed = Math.Max(0f, sweep.fraction - SweepBackoffFraction);
                state.x += dx * allowed;
                state.z += dz * allowed;
                // Slide the remaining motion along the contact plane once.
                var remX = dx * (1f - allowed);
                var remZ = dz * (1f - allowed);
                var dot = remX * nx + remZ * nz;
                var slideX = remX - nx * dot;
                var slideZ = remZ - nz * dot;
                if (slideX != 0f || slideZ != 0f)
                {
                    var slidePose = new MotorPose
                        { x = state.x, y = state.y, z = state.z, heading = state.heading };
                    var slide = world.SweepBody(slidePose, slideX, 0f, slideZ,
                        tuning.halfWidth, tuning.halfLength, tuning.height);
                    var slideAllowed = slide.hit
                        ? Math.Max(0f, slide.fraction - SweepBackoffFraction)
                        : 1f;
                    state.x += slideX * slideAllowed;
                    state.z += slideZ * slideAllowed;
                }
                // Remove the into-wall component of velocity (keep retention).
                var vx = fx * state.speed + rx * state.lateralSpeed;
                var vz = fz * state.speed + rz * state.lateralSpeed;
                var intoWall = vx * nx + vz * nz;
                if (intoWall > 0f)
                {
                    vx -= nx * intoWall * (1f - tuning.wallSpeedRetention);
                    vz -= nz * intoWall * (1f - tuning.wallSpeedRetention);
                    state.speed = vx * fx + vz * fz;
                    state.lateralSpeed = vx * rx + vz * rz;
                    result.events.Add(new MotorEvent("wallHit", intoWall));
                }
            }
            else
            {
                state.x += dx;
                state.z += dz;
            }

            // --- vertical: ground follow / airborne fall ---------------------
            pose.x = state.x;
            pose.y = state.y;
            pose.z = state.z;
            var ground = world.ProbeGround(pose, tuning.groundProbe);
            if (state.grounded)
            {
                if (ground.hit && state.y - ground.y <= tuning.groundProbe)
                {
                    state.y = ground.y;
                    state.verticalSpeed = 0f;
                    state.airSeconds = 0f;
                    state.lastContact = contacted ? "wall" : "ground";
                }
                else
                {
                    state.grounded = false;
                    state.airSeconds = 0f;
                    result.events.Add(new MotorEvent("becameAirborne"));
                }
            }
            if (!state.grounded)
            {
                state.verticalSpeed = Math.Max(state.verticalSpeed - tuning.gravity * dt,
                    -tuning.terminalFallSpeed);
                var newY = state.y + state.verticalSpeed * dt;
                state.airSeconds += dt;
                if (ground.hit && newY <= ground.y)
                {
                    state.y = ground.y;
                    state.grounded = true;
                    state.verticalSpeed = 0f;
                    state.airSeconds = 0f;
                    result.events.Add(new MotorEvent("landed"));
                    state.lastContact = contacted ? "wall" : "ground";
                }
                else
                {
                    state.y = newY;
                    state.lastContact = contacted ? "wall" : "none";
                }
            }

            state.tick = previous.tick + 1;
            result.state = state;
            return result;
        }

        private static float Clamp01(float value)
        {
            if (float.IsNaN(value)) return 0f;
            return value < 0f ? 0f : (value > 1f ? 1f : value);
        }

        private static float MoveToward(float current, float target, float maxDelta)
        {
            if (current < target) return Math.Min(current + maxDelta, target);
            if (current > target) return Math.Max(current - maxDelta, target);
            return current;
        }

        private static float WrapAngle(float radians)
        {
            if (float.IsNaN(radians) || float.IsInfinity(radians)) return 0f;
            while (radians > Math.PI) radians -= TwoPi;
            while (radians <= -Math.PI) radians += TwoPi;
            return radians;
        }
    }
}
