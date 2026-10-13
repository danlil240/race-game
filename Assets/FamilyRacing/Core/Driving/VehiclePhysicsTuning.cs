using System;

namespace FamilyRacing.Core.Driving
{
    // Authored suspension-mount geometry for one wheel ray, in the vehicle
    // body frame (+X right, +Y up, +Z forward) and SI units.
    [Serializable]
    public sealed class VehicleWheelSpec
    {
        public float mountX;    // suspension ray origin offset from body origin (m)
        public float mountY;
        public float mountZ;
        public float radius;    // wheel radius (m)
        public bool steerable;  // road-wheel steering applies to this wheel
        public bool driven;     // this wheel shares engine and brake demand

        public VehicleWheelSpec Copy()
        {
            return (VehicleWheelSpec)MemberwiseClone();
        }
    }

    // Validated physical vehicle parameters in SI units (metres, seconds,
    // kilograms, newtons, newton-metres, radians) for the raycast-wheel
    // backend. Validate() is the single authority: a NaN or out-of-range
    // asset can never partially mutate simulation state. Numeric defaults
    // and the Car01/Motorcycle01 presets are the spec's suggested first
    // tuning pass — editable starting values, not measured results.
    [Serializable]
    public sealed class VehiclePhysicsTuning
    {
        // Standard gravity for this backend (m/s^2). Deliberately not the
        // arcade motor's 22 m/s^2.
        public const float StandardGravity = 9.81f;

        // --- chassis body --------------------------------------------------
        public float massKg = 1200f;            // total vehicle mass (kg)
        public float comOffsetX;                // center of mass offset from
        public float comOffsetY = -0.15f;       //   the body origin (m)
        public float comOffsetZ;
        public float chassisLength = 3.2f;      // collision box extents (m)
        public float chassisWidth = 1.8f;
        public float chassisHeight = 1.1f;
        // Positive multipliers on the collider-derived inertia tensor.
        public float inertiaMultiplierX = 1f;
        public float inertiaMultiplierY = 1f;
        public float inertiaMultiplierZ = 1f;

        // --- wheels and suspension ------------------------------------------
        // Supported layouts: exactly two inline contacts (motorcycle) or
        // exactly four wheels (car). Never null inside valid tuning.
        public VehicleWheelSpec[] wheels = new VehicleWheelSpec[0];
        public float suspensionRestLength = 0.35f;   // ray travel at rest (m)
        public float suspensionExtraDroop = 0.10f;   // droop beyond rest (m)
        public float springRatePerWheel = 40000f;    // N/m
        public float damperPerWheel = 3500f;         // N·s/m
        public float maxNormalLoadPerWheel = 20000f; // authored support cap (N)

        // --- tires, drive and braking ---------------------------------------
        public float frictionCoefficient = 1.1f;     // one combined grip budget
        public float peakDriveForce = 7200f;         // N, split over driven wheels
        public float peakBrakeForce = 12000f;        // N total
        public float cruiseSpeed = 18f;              // m/s engine taper target
        public float boostSpeed = 26f;               // m/s while boosting
        public float dragCoefficient = 0.4f;         // quadratic drag, N/(m/s)^2
        public float rollingResistance = 0.01f;      // fraction of normal load

        // --- steering assistance curve --------------------------------------
        public float steerMaxRadians = 0.5236f;         // low-speed road-wheel max
        public float steerHighSpeedRadians = 0.12f;     // authority floor at speed
        public float steerReferenceSpeed = 20f;         // m/s where the floor applies
        public float steerRateRadiansPerSecond = 4f;    // steering change rate limit

        // --- assistance (bounded; never authors attitude) -------------------
        public float antiRollRate;                      // anti-roll spring (N/m), 0 off
        public float rollDamping;                       // roll damping (N·m·s/rad), 0 off
        public float assistMaxLeanRadians = 0.7854f;    // bike lean bound (45 deg)
        public float assistFallAngleRadians = 1.2217f;  // assist cutout (70 deg)
        public float balanceGain;                       // bike balance P torque (N·m/rad)
        public float balanceDamping;                    // bike balance D torque (N·m·s/rad)

        public bool Validate(out string error)
        {
            error = null;
            if (!Positive(massKg)) { error = "massKg"; return false; }
            if (!Finite(comOffsetX) || !Finite(comOffsetY) || !Finite(comOffsetZ))
            { error = "comOffset"; return false; }
            if (!Positive(chassisLength) || !Positive(chassisWidth)
                || !Positive(chassisHeight))
            { error = "chassis"; return false; }
            if (!Positive(inertiaMultiplierX) || !Positive(inertiaMultiplierY)
                || !Positive(inertiaMultiplierZ))
            { error = "inertiaMultiplier"; return false; }
            if (!ValidateWheels(ref error)) return false;
            if (!Positive(suspensionRestLength)) { error = "suspensionRestLength"; return false; }
            if (!NonNegative(suspensionExtraDroop)) { error = "suspensionExtraDroop"; return false; }
            if (!Positive(springRatePerWheel)) { error = "springRatePerWheel"; return false; }
            if (!NonNegative(damperPerWheel)) { error = "damperPerWheel"; return false; }
            if (!Positive(maxNormalLoadPerWheel)) { error = "maxNormalLoadPerWheel"; return false; }
            if (!Positive(frictionCoefficient)) { error = "frictionCoefficient"; return false; }
            if (!Positive(peakDriveForce)) { error = "peakDriveForce"; return false; }
            if (!Positive(peakBrakeForce)) { error = "peakBrakeForce"; return false; }
            if (!Positive(cruiseSpeed)) { error = "cruiseSpeed"; return false; }
            if (!Positive(boostSpeed) || boostSpeed < cruiseSpeed)
            { error = "boostSpeed"; return false; }
            if (!NonNegative(dragCoefficient)) { error = "dragCoefficient"; return false; }
            if (!NonNegative(rollingResistance)) { error = "rollingResistance"; return false; }
            if (!Positive(steerMaxRadians) || steerMaxRadians > (float)(Math.PI / 2.0))
            { error = "steerMaxRadians"; return false; }
            if (!Positive(steerHighSpeedRadians) || steerHighSpeedRadians > steerMaxRadians)
            { error = "steerHighSpeedRadians"; return false; }
            if (!Positive(steerReferenceSpeed)) { error = "steerReferenceSpeed"; return false; }
            if (!Positive(steerRateRadiansPerSecond))
            { error = "steerRateRadiansPerSecond"; return false; }
            if (!NonNegative(antiRollRate)) { error = "antiRollRate"; return false; }
            if (!NonNegative(rollDamping)) { error = "rollDamping"; return false; }
            if (!Positive(assistMaxLeanRadians)
                || assistMaxLeanRadians > (float)(Math.PI / 2.0))
            { error = "assistMaxLeanRadians"; return false; }
            if (!Positive(assistFallAngleRadians)
                || assistFallAngleRadians <= assistMaxLeanRadians
                || assistFallAngleRadians > (float)Math.PI)
            { error = "assistFallAngleRadians"; return false; }
            if (!NonNegative(balanceGain)) { error = "balanceGain"; return false; }
            if (!NonNegative(balanceDamping)) { error = "balanceDamping"; return false; }
            return true;
        }

        // Supported layouts are exactly two inline contacts (a motorcycle
        // whose wheels share the longitudinal axis) or four wheels with a
        // real track width and wheelbase.
        private bool ValidateWheels(ref string error)
        {
            if (wheels == null) { error = "wheels"; return false; }
            if (wheels.Length != 2 && wheels.Length != 4)
            { error = "wheels"; return false; }
            var anySteerable = false;
            var anyDriven = false;
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (var i = 0; i < wheels.Length; i++)
            {
                var wheel = wheels[i];
                if (wheel == null) { error = "wheels"; return false; }
                if (!Finite(wheel.mountX) || !Finite(wheel.mountY) || !Finite(wheel.mountZ))
                { error = "wheels.mount"; return false; }
                if (!Positive(wheel.radius)) { error = "wheels.radius"; return false; }
                anySteerable |= wheel.steerable;
                anyDriven |= wheel.driven;
                if (wheel.mountX < minX) minX = wheel.mountX;
                if (wheel.mountX > maxX) maxX = wheel.mountX;
                if (wheel.mountZ < minZ) minZ = wheel.mountZ;
                if (wheel.mountZ > maxZ) maxZ = wheel.mountZ;
            }
            if (!anySteerable) { error = "wheels.steerable"; return false; }
            if (!anyDriven) { error = "wheels.driven"; return false; }
            const float layoutEpsilon = 0.01f;
            if (wheels.Length == 2)
            {
                // Two inline contact points: no lateral offset, real wheelbase.
                if (maxX - minX > layoutEpsilon)
                { error = "wheels.layout"; return false; }
                if (maxZ - minZ < layoutEpsilon)
                { error = "wheels.layout"; return false; }
            }
            else
            {
                if (maxX - minX < layoutEpsilon || maxZ - minZ < layoutEpsilon)
                { error = "wheels.layout"; return false; }
            }
            return true;
        }

        // Body-origin height above the contact surface when the vehicle
        // rests settled on its suspension: wheel radius + rest travel -
        // static load compression - mount depth, plus a small margin so
        // struts settle down onto the road instead of starting
        // pre-loaded. Authored spawn/recover/safe poses carry SURFACE
        // height; the physical backend adds this per-vehicle lift so one
        // authored pose serves every vehicle class.
        public float SettledLift()
        {
            var wheelCount = wheels != null ? wheels.Length : 0;
            var lowestMount = 0f;
            var radius = 0f;
            if (wheels != null)
                foreach (var wheel in wheels)
                {
                    if (wheel == null) continue;
                    if (wheel.mountY < lowestMount)
                        lowestMount = wheel.mountY;
                    if (wheel.radius > radius) radius = wheel.radius;
                }
            var staticCompression = wheelCount > 0
                && springRatePerWheel > 0f
                ? massKg * StandardGravity
                    / (wheelCount * springRatePerWheel)
                : 0f;
            return radius + suspensionRestLength - staticCompression
                - lowestMount + 0.05f;
        }

        public VehiclePhysicsTuning Copy()
        {
            var copy = (VehiclePhysicsTuning)MemberwiseClone();
            if (wheels == null)
            {
                copy.wheels = new VehicleWheelSpec[0];
            }
            else
            {
                copy.wheels = new VehicleWheelSpec[wheels.Length];
                for (var i = 0; i < wheels.Length; i++)
                    copy.wheels[i] = wheels[i] == null ? null : wheels[i].Copy();
            }
            return copy;
        }

        private static bool Positive(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
        }

        private static bool NonNegative(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        // Spec's first tuning pass for a car: 1200 kg, 2.4 m wheelbase,
        // 1.6 m track, 0.32 m wheels, front steering, all-wheel drive.
        public static VehiclePhysicsTuning Car01()
        {
            return new VehiclePhysicsTuning
            {
                massKg = 1200f,
                comOffsetY = -0.15f,
                chassisLength = 3.2f,
                chassisWidth = 1.8f,
                chassisHeight = 1.1f,
                wheels = new[]
                {
                    new VehicleWheelSpec { mountX = -0.8f, mountY = -0.1f, mountZ = 1.2f,
                        radius = 0.32f, steerable = true, driven = true },
                    new VehicleWheelSpec { mountX = 0.8f, mountY = -0.1f, mountZ = 1.2f,
                        radius = 0.32f, steerable = true, driven = true },
                    new VehicleWheelSpec { mountX = -0.8f, mountY = -0.1f, mountZ = -1.2f,
                        radius = 0.32f, steerable = false, driven = true },
                    new VehicleWheelSpec { mountX = 0.8f, mountY = -0.1f, mountZ = -1.2f,
                        radius = 0.32f, steerable = false, driven = true },
                },
                suspensionRestLength = 0.35f,
                suspensionExtraDroop = 0.10f,
                springRatePerWheel = 40000f,
                damperPerWheel = 3500f,
                maxNormalLoadPerWheel = 20000f,
                frictionCoefficient = 1.1f,
                peakDriveForce = 7200f,
                peakBrakeForce = 12000f,
                cruiseSpeed = 18f,
                boostSpeed = 26f,
                steerMaxRadians = 0.5236f,          // 30 degrees
                steerHighSpeedRadians = 0.12f,
                steerReferenceSpeed = 20f,
                steerRateRadiansPerSecond = 4f,
                rollDamping = 1500f,
                assistMaxLeanRadians = 0.7854f,     // 45 degrees
                assistFallAngleRadians = 1.2217f    // 70 degrees
            };
        }

        // Spec's first tuning pass for a motorcycle including its rider:
        // 220 kg, 1.45 m wheelbase, two inline 0.30 m contacts, front
        // steering, rear drive, automatic balance assistance.
        public static VehiclePhysicsTuning Motorcycle01()
        {
            return new VehiclePhysicsTuning
            {
                massKg = 220f,
                comOffsetY = -0.1f,
                chassisLength = 2.2f,
                chassisWidth = 0.5f,
                chassisHeight = 0.9f,
                wheels = new[]
                {
                    new VehicleWheelSpec { mountX = 0f, mountY = -0.1f, mountZ = 0.725f,
                        radius = 0.30f, steerable = true, driven = false },
                    new VehicleWheelSpec { mountX = 0f, mountY = -0.1f, mountZ = -0.725f,
                        radius = 0.30f, steerable = false, driven = true },
                },
                suspensionRestLength = 0.30f,
                // Deep droop: the rays run along -bodyUp, so a leaned
                // bike needs reach*cos(lean) to still touch — 0.90 m of
                // reach keeps both contacts valid through the authored
                // 45-degree lean bound (cuts out again past ~50 deg, so
                // a real fall is still a fall).
                suspensionExtraDroop = 0.30f,
                springRatePerWheel = 18000f,
                damperPerWheel = 1500f,
                maxNormalLoadPerWheel = 8000f,
                // Sport-tire grip: a max-lean turn demands ~1.2 g of
                // lateral accel, so mu below ~1.3 pins the bike on the
                // traction circle — the yaw saturates, the lean pulses,
                // and the turn stalls in one-second cycles.
                frictionCoefficient = 1.3f,
                peakDriveForce = 1760f,
                peakBrakeForce = 2200f,
                cruiseSpeed = 20f,
                boostSpeed = 28f,
                dragCoefficient = 0.5f,
                steerMaxRadians = 0.4363f,          // 25 degrees
                steerHighSpeedRadians = 0.15f,
                steerReferenceSpeed = 22f,
                steerRateRadiansPerSecond = 5f,
                assistMaxLeanRadians = 0.7854f,     // 45 degrees
                assistFallAngleRadians = 1.2217f,   // 70 degrees
                balanceGain = 2500f,
                balanceDamping = 800f
            };
        }
    }
}
