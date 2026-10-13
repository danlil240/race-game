using System;
using FamilyRacing.Core.Driving;

namespace FamilyRacing.Core.Networking
{
    // Compact wire form of one wheel's replay-relevant history. The array
    // position is the authored wheel index, so no index field travels;
    // diagnostic-only values (contact point, normal, load, slips) are
    // deliberately absent — clients never present them and can never
    // author them.
    [Serializable]
    public sealed class NetworkPhysicsWheel
    {
        public bool grounded;
        public float compression;
        public float previousCompression;
        public bool contactValid;
        public float steering;
        public float spin;

        public NetworkPhysicsWheel Copy()
        {
            return (NetworkPhysicsWheel)MemberwiseClone();
        }
    }

    // Versioned physical-state DTO carried inside each NetworkRacerState
    // (protocol 2 / schema 4). It is a compact projection of the
    // authoritative MotorState: the full rule envelope plus the physical
    // body's pose, motion, controller histories, recovery bookkeeping and
    // wheel histories — everything client prediction/replay needs, and
    // nothing diagnostic-only (contact points, normals, loads, slips
    // never cross the wire).
    //
    // `present` is the explicit wire marker: JsonUtility materializes an
    // absent field as an empty instance, so the flag — never null-vs-
    // instance — is what distinguishes "physical racer" from "arcade
    // racer". Shared episode facts (grounded, airSeconds, airPeakY,
    // airWallContact, lastContact) are carried once, sourced from the
    // physical state and applied to both the envelope and the physics on
    // decode so the two layers can never disagree.
    //
    // The decode contract is atomic: Validate() gates the whole DTO
    // before any motor field is attached, so a hostile or corrupted
    // snapshot can never partially mutate client state.
    [Serializable]
    public sealed class NetworkPhysicsState
    {
        // Every authored vehicle layout stays inside this bound; a longer
        // wheel array is malformed by definition.
        public const int MaxWheels = 4;

        public bool present;

        // --- rule envelope (mirrors MotorState) -------------------------
        public float x;
        public float y;
        public float z;
        public float heading;
        public float speed;
        public float lateralSpeed;
        public float verticalSpeed;
        public bool drifting;
        public int driftSign;
        public float boostCharge;
        public bool boostActive;
        public float boostSecondsRemaining;
        public bool boostLatched;
        public bool recovering;
        public long tick;

        // --- physical body: pose + motion -------------------------------
        public float attitudeX;
        public float attitudeY;
        public float attitudeZ;
        public float attitudeW;
        public float velocityX;
        public float velocityY;
        public float velocityZ;
        public float angularVelocityX;
        public float angularVelocityY;
        public float angularVelocityZ;

        // --- controller histories ---------------------------------------
        public float filteredSteering;
        public float filteredRollRate;
        public float balancePrevError;
        public float filteredLeanTarget;
        public float driftAmount;

        // --- shared episode bookkeeping (single source: the physical
        // state; applied to both layers on decode) ----------------------
        public bool grounded;
        public float airSeconds;
        public float airPeakY;
        public bool airWallContact;
        public float overturnedSeconds;
        public float contactLossSeconds;
        public string lastContact = "none";
        public long recoveryEpoch;

        public NetworkPhysicsWheel[] wheels = new NetworkPhysicsWheel[0];

        // The authoritative-state projection: null for a non-physical
        // motor (or a null motor), else a complete compact DTO with a
        // normalized attitude — a peer never sees pre-normalization
        // components.
        public static NetworkPhysicsState FromMotor(MotorState motor)
        {
            if (motor == null || motor.physics == null) return null;
            var physics = motor.physics;
            var dto = new NetworkPhysicsState
            {
                present = true,
                x = motor.x,
                y = motor.y,
                z = motor.z,
                heading = motor.heading,
                speed = motor.speed,
                lateralSpeed = motor.lateralSpeed,
                verticalSpeed = motor.verticalSpeed,
                drifting = motor.drifting,
                driftSign = motor.driftSign,
                boostCharge = motor.boostCharge,
                boostActive = motor.boostActive,
                boostSecondsRemaining = motor.boostSecondsRemaining,
                boostLatched = motor.boostLatched,
                recovering = motor.recovering,
                tick = motor.tick,
                velocityX = physics.velocityX,
                velocityY = physics.velocityY,
                velocityZ = physics.velocityZ,
                angularVelocityX = physics.angularVelocityX,
                angularVelocityY = physics.angularVelocityY,
                angularVelocityZ = physics.angularVelocityZ,
                filteredSteering = physics.filteredSteering,
                filteredRollRate = physics.filteredRollRate,
                balancePrevError = physics.balancePrevError,
                filteredLeanTarget = physics.filteredLeanTarget,
                driftAmount = physics.driftAmount,
                grounded = physics.grounded,
                airSeconds = physics.airSeconds,
                airPeakY = physics.airPeakY,
                airWallContact = physics.airWallContact,
                overturnedSeconds = physics.overturnedSeconds,
                contactLossSeconds = physics.contactLossSeconds,
                lastContact = physics.lastContact ?? "none",
                recoveryEpoch = physics.recoveryEpoch
            };
            // Encode normalizes: a usable quaternion crosses the wire
            // unit-length; a broken one crosses raw so Validate() can
            // name the failure.
            var normSq = physics.attitudeX * physics.attitudeX
                + physics.attitudeY * physics.attitudeY
                + physics.attitudeZ * physics.attitudeZ
                + physics.attitudeW * physics.attitudeW;
            if (Finite(physics.attitudeX) && Finite(physics.attitudeY)
                && Finite(physics.attitudeZ) && Finite(physics.attitudeW)
                && normSq >= 1e-12f)
            {
                var inv = 1f / (float)Math.Sqrt(normSq);
                dto.attitudeX = physics.attitudeX * inv;
                dto.attitudeY = physics.attitudeY * inv;
                dto.attitudeZ = physics.attitudeZ * inv;
                dto.attitudeW = physics.attitudeW * inv;
            }
            else
            {
                dto.attitudeX = physics.attitudeX;
                dto.attitudeY = physics.attitudeY;
                dto.attitudeZ = physics.attitudeZ;
                dto.attitudeW = physics.attitudeW;
            }
            var source = physics.wheels;
            if (source == null)
            {
                dto.wheels = new NetworkPhysicsWheel[0];
            }
            else
            {
                dto.wheels = new NetworkPhysicsWheel[source.Length];
                for (var i = 0; i < source.Length; i++)
                {
                    var wheel = source[i];
                    if (wheel == null)
                    {
                        dto.wheels[i] = new NetworkPhysicsWheel();
                        continue;
                    }
                    dto.wheels[i] = new NetworkPhysicsWheel
                    {
                        grounded = wheel.grounded,
                        compression = wheel.compression,
                        previousCompression = wheel.previousCompression,
                        contactValid = wheel.contactValid,
                        steering = wheel.steering,
                        spin = wheel.spin
                    };
                }
            }
            return dto;
        }

        // Rebuilds the authoritative MotorState: rule envelope plus a
        // full VehiclePhysicsState whose diagnostics decode as zero and
        // whose wheel indexes come from array position. The wire
        // quaternion normalizes through the state's single mutation
        // path; a zero/non-finite one leaves the identity attitude —
        // Validate() is the gate callers run first.
        public MotorState ToMotor()
        {
            var motor = new MotorState
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
                boostLatched = boostLatched,
                airWallContact = airWallContact,
                airPeakY = airPeakY,
                recovering = recovering,
                lastContact = lastContact ?? "none",
                tick = tick
            };
            var physics = new VehiclePhysicsState
            {
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
                lastContact = lastContact ?? "none",
                recoveryEpoch = recoveryEpoch
            };
            physics.TrySetAttitude(attitudeX, attitudeY, attitudeZ, attitudeW);
            if (wheels == null)
            {
                physics.wheels = new WheelPhysicsState[0];
            }
            else
            {
                physics.wheels = new WheelPhysicsState[wheels.Length];
                for (var i = 0; i < wheels.Length; i++)
                {
                    var wire = wheels[i];
                    physics.wheels[i] = new WheelPhysicsState
                    {
                        index = i,
                        grounded = wire != null && wire.grounded,
                        compression = wire != null ? wire.compression : 0f,
                        previousCompression = wire != null
                            ? wire.previousCompression : 0f,
                        contactValid = wire != null && wire.contactValid,
                        steering = wire != null ? wire.steering : 0f,
                        spin = wire != null ? wire.spin : 0f
                    };
                }
            }
            motor.physics = physics;
            return motor;
        }

        // The whole-DTO gate run before any decoded field touches client
        // state: every carried float must be finite, the quaternion
        // non-zero (scale is repaired by decode), the wheel array present
        // and inside the authored bound with valid entries, and every
        // semantic range (drift amount, episode clocks, recovery epoch,
        // contact label) respected.
        public bool Validate(out string error)
        {
            error = null;
            if (!Finite(x) || !Finite(y) || !Finite(z) || !Finite(heading)
                || !Finite(speed) || !Finite(lateralSpeed)
                || !Finite(verticalSpeed))
            { error = "pose"; return false; }
            if (!Finite(boostCharge) || !Finite(boostSecondsRemaining))
            { error = "envelope"; return false; }
            if (!Finite(attitudeX) || !Finite(attitudeY)
                || !Finite(attitudeZ) || !Finite(attitudeW))
            { error = "attitude"; return false; }
            var normSq = attitudeX * attitudeX + attitudeY * attitudeY
                + attitudeZ * attitudeZ + attitudeW * attitudeW;
            if (normSq < 1e-12f) { error = "attitude"; return false; }
            // Encode normalizes before transmission, so a wire DTO is
            // unit-length within float round-trip noise — a non-unit
            // quaternion is malformed wire data, never a repairable
            // artifact to normalize silently.
            if (Math.Abs(Math.Sqrt(normSq) - 1.0) > 0.01)
            { error = "attitude"; return false; }
            if (!Finite(velocityX) || !Finite(velocityY) || !Finite(velocityZ))
            { error = "velocity"; return false; }
            if (!Finite(angularVelocityX) || !Finite(angularVelocityY)
                || !Finite(angularVelocityZ))
            { error = "angularVelocity"; return false; }
            if (!Finite(filteredSteering) || !Finite(filteredRollRate)
                || !Finite(balancePrevError) || !Finite(filteredLeanTarget))
            { error = "controller"; return false; }
            if (!Finite(driftAmount) || driftAmount < 0f || driftAmount > 1f)
            { error = "driftAmount"; return false; }
            if (!Finite(airSeconds) || airSeconds < 0f)
            { error = "airSeconds"; return false; }
            if (!Finite(airPeakY)) { error = "airPeakY"; return false; }
            if (!Finite(overturnedSeconds) || overturnedSeconds < 0f)
            { error = "overturnedSeconds"; return false; }
            if (!Finite(contactLossSeconds) || contactLossSeconds < 0f)
            { error = "contactLossSeconds"; return false; }
            if (lastContact == null) { error = "lastContact"; return false; }
            if (recoveryEpoch < 0) { error = "recoveryEpoch"; return false; }
            if (wheels == null) { error = "wheels"; return false; }
            if (wheels.Length > MaxWheels) { error = "wheels"; return false; }
            for (var i = 0; i < wheels.Length; i++)
            {
                var wheel = wheels[i];
                if (wheel == null) { error = "wheels"; return false; }
                if (!Finite(wheel.compression) || wheel.compression < 0f
                    || !Finite(wheel.previousCompression)
                    || wheel.previousCompression < 0f)
                { error = "wheels.compression"; return false; }
                if (!Finite(wheel.steering) || !Finite(wheel.spin))
                { error = "wheels"; return false; }
            }
            return true;
        }

        public NetworkPhysicsState Copy()
        {
            var copy = (NetworkPhysicsState)MemberwiseClone();
            if (wheels != null)
            {
                copy.wheels = new NetworkPhysicsWheel[wheels.Length];
                for (var i = 0; i < wheels.Length; i++)
                    copy.wheels[i] = wheels[i] == null ? null : wheels[i].Copy();
            }
            return copy;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
