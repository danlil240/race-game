using System;

namespace FamilyRacing.Core.Driving
{
    // Per-wheel physical history for one authored suspension ray. All
    // values are SI (metres, seconds, newtons, radians) in the vehicle's
    // world frame. `compression`/`previousCompression` are metres of
    // suspension travel used; `contactValid` marks previousCompression as
    // trustworthy damper history — it is cleared when contact is lost or
    // the vehicle spawns/recovers, and preserved by an authoritative
    // prediction restore.
    [Serializable]
    public sealed class WheelPhysicsState
    {
        public int index;               // authored wheel slot, 0..wheels.Length-1
        public bool grounded;           // suspension ray rests on drivable contact
        public float contactX;          // world contact point (m)
        public float contactY;
        public float contactZ;
        public float normalX;           // world contact normal (unit when grounded)
        public float normalY = 1f;
        public float normalZ;
        public float compression;           // current suspension compression (m)
        public float previousCompression;   // previous substep compression (m)
        public bool contactValid;           // previousCompression is usable history
        public float steering;              // applied road-wheel steering angle (rad)
        public float spin;                  // visual wheel-spin accumulator (rad)
        // Diagnostics from the last force evaluation — observation only,
        // never a force authority.
        public float normalLoad;            // normal support force applied (N)
        public float slipLongitudinal;      // contact-point longitudinal slip (m/s)
        public float slipLateral;           // contact-point lateral slip (m/s)

        public WheelPhysicsState Copy()
        {
            return (WheelPhysicsState)MemberwiseClone();
        }
    }

    // Optional full physical state carried by MotorState for the physical
    // backend. All values are SI: metres, seconds, kilograms, radians.
    // Vehicle axes are +X right, +Y up, +Z forward; attitude is a scalar
    // quaternion (x, y, z, w) where identity (0,0,0,1) faces +Z upright.
    // TrySetAttitude is the only normalizing mutation path — it normalizes
    // usable quaternions and rejects zero/non-finite input before touching
    // state. Copy() is a deep copy: wheel/controller histories never alias.
    [Serializable]
    public sealed class VehiclePhysicsState
    {
        public float attitudeX;
        public float attitudeY;
        public float attitudeZ;
        public float attitudeW = 1f;

        public float velocityX;             // world linear velocity (m/s)
        public float velocityY;
        public float velocityZ;
        public float angularVelocityX;      // world angular velocity (rad/s)
        public float angularVelocityY;
        public float angularVelocityZ;

        // Per-wheel history in authored-wheel order; length equals the
        // tuning's wheel count. Never null inside a valid state.
        public WheelPhysicsState[] wheels = new WheelPhysicsState[0];

        // Controller histories that feed force application and must be
        // copied/restored for replay.
        public float filteredSteering;      // rate-limited steering applied (rad)
        public float filteredRollRate;      // damped roll-rate estimate (rad/s)
        public float balancePrevError;      // bike balance controller's last error (rad)
        // Slew-limited lean target the bike balance PD actually tracks
        // (rad). The raw atan(v*yawRate/g) demand bangs with the weave
        // mode's yaw wobble; the persisted slew limiter keeps the PD's
        // demand smooth AND makes the lag replayable.
        public float filteredLeanTarget;
        // Progressive drift engagement in [0,1]: ramps up while drift
        // demand is held and back down on release, so rear lateral grip
        // eases off progressively instead of stepping.
        public float driftAmount;

        // Airborne/contact episode bookkeeping for recovery and events.
        public bool grounded;               // any wheel or body contact supports us
        public float airSeconds;            // continuous airborne time (s)
        public float airPeakY;              // highest body y this airborne episode
        public bool airWallContact;         // airborne episode clipped a barrier
        public float overturnedSeconds;     // continuous inverted-support time (s)
        // Continuous time without ANY contact support (s) — the
        // grounded->airborne debounce window. Restored so a replayed
        // boundary cannot mint or swallow a takeoff the original run
        // did not see.
        public float contactLossSeconds;
        public string lastContact = "none"; // "none"|"ground"|"wall"|"obstacle"|"vehicle"
        // Bumped on every recovery; restore/prediction key on it so stale
        // replicas can never undo a recovery.
        public long recoveryEpoch;

        public VehiclePhysicsState Copy()
        {
            var copy = new VehiclePhysicsState
            {
                attitudeX = attitudeX,
                attitudeY = attitudeY,
                attitudeZ = attitudeZ,
                attitudeW = attitudeW,
                velocityX = velocityX,
                velocityY = velocityY,
                velocityZ = velocityZ,
                angularVelocityX = angularVelocityX,
                angularVelocityY = angularVelocityY,
                angularVelocityZ = angularVelocityZ,
                filteredSteering = filteredSteering,
                filteredRollRate = filteredRollRate,
                balancePrevError = balancePrevError,
                filteredLeanTarget = filteredLeanTarget,
                driftAmount = driftAmount,
                grounded = grounded,
                airSeconds = airSeconds,
                airPeakY = airPeakY,
                airWallContact = airWallContact,
                overturnedSeconds = overturnedSeconds,
                contactLossSeconds = contactLossSeconds,
                lastContact = lastContact,
                recoveryEpoch = recoveryEpoch
            };
            if (wheels == null)
            {
                copy.wheels = new WheelPhysicsState[0];
            }
            else
            {
                copy.wheels = new WheelPhysicsState[wheels.Length];
                for (var i = 0; i < wheels.Length; i++)
                    copy.wheels[i] = wheels[i] == null ? null : wheels[i].Copy();
            }
            return copy;
        }

        // Assigns a normalized attitude quaternion. Returns false — without
        // mutating any field — when the input is non-finite or zero-length.
        public bool TrySetAttitude(float x, float y, float z, float w)
        {
            if (!Finite(x) || !Finite(y) || !Finite(z) || !Finite(w)) return false;
            var normSq = x * x + y * y + z * z + w * w;
            if (normSq < 1e-12f) return false;
            var inv = 1f / (float)Math.Sqrt(normSq);
            attitudeX = x * inv;
            attitudeY = y * inv;
            attitudeZ = z * inv;
            attitudeW = w * inv;
            return true;
        }

        // Single authority consulted before physical state reaches PhysX:
        // a non-finite or structurally broken sample is refused as a whole.
        public bool Validate(out string error)
        {
            error = null;
            if (!Finite(attitudeX) || !Finite(attitudeY)
                || !Finite(attitudeZ) || !Finite(attitudeW))
            { error = "attitude"; return false; }
            var normSq = attitudeX * attitudeX + attitudeY * attitudeY
                + attitudeZ * attitudeZ + attitudeW * attitudeW;
            // Zero-length and non-unit quaternions are both unusable:
            // TrySetAttitude is the sanitizing mutation path.
            if (normSq < 1e-12f) { error = "attitude"; return false; }
            if (Math.Abs(Math.Sqrt(normSq) - 1.0) > 0.01)
            { error = "attitude"; return false; }
            if (!Finite(velocityX) || !Finite(velocityY) || !Finite(velocityZ))
            { error = "velocity"; return false; }
            if (!Finite(angularVelocityX) || !Finite(angularVelocityY)
                || !Finite(angularVelocityZ))
            { error = "angularVelocity"; return false; }
            if (wheels == null) { error = "wheels"; return false; }
            for (var i = 0; i < wheels.Length; i++)
            {
                var wheel = wheels[i];
                if (wheel == null) { error = "wheels"; return false; }
                if (wheel.index != i) { error = "wheels.index"; return false; }
                if (!Finite(wheel.contactX) || !Finite(wheel.contactY)
                    || !Finite(wheel.contactZ) || !Finite(wheel.normalX)
                    || !Finite(wheel.normalY) || !Finite(wheel.normalZ)
                    || !Finite(wheel.compression) || !Finite(wheel.previousCompression)
                    || !Finite(wheel.steering) || !Finite(wheel.spin)
                    || !Finite(wheel.normalLoad) || !Finite(wheel.slipLongitudinal)
                    || !Finite(wheel.slipLateral))
                { error = "wheels"; return false; }
                if (wheel.compression < 0f || wheel.previousCompression < 0f)
                { error = "wheels.compression"; return false; }
            }
            if (!Finite(filteredSteering) || !Finite(filteredRollRate)
                || !Finite(balancePrevError) || !Finite(filteredLeanTarget)
                || !Finite(driftAmount)
                || driftAmount < 0f || driftAmount > 1f)
            { error = "controller"; return false; }
            if (!Finite(airSeconds) || airSeconds < 0f)
            { error = "airSeconds"; return false; }
            if (!Finite(airPeakY)) { error = "airPeakY"; return false; }
            if (!Finite(overturnedSeconds) || overturnedSeconds < 0f)
            { error = "overturnedSeconds"; return false; }
            if (!Finite(contactLossSeconds) || contactLossSeconds < 0f)
            { error = "contactLossSeconds"; return false; }
            if (lastContact == null) { error = "lastContact"; return false; }
            if (recoveryEpoch < 0) { error = "recoveryEpoch"; return false; }
            return true;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
