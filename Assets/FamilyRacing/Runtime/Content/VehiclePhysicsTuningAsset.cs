using System;
using FamilyRacing.Core.Driving;
using UnityEngine;

namespace FamilyRacing
{
    // Authored physical vehicle parameters in SI units, converted into a
    // validated Core VehiclePhysicsTuning. Conversion is atomic: ToTuning
    // builds a candidate, validates it as a whole, and returns null — with
    // nothing mutated — when any field is invalid. A VehicleDefinitionAsset
    // only consumes this asset when it deliberately selects the physical
    // backend; legacy vehicles keep the arcade motor.
    [CreateAssetMenu(menuName = "FamilyRacing/Vehicle Physics Tuning",
        fileName = "vehicle-physics-tuning")]
    public sealed class VehiclePhysicsTuningAsset : ScriptableObject
    {
        // Unity-serializable mirror of Core's VehicleWheelSpec.
        [Serializable]
        public sealed class WheelSpec
        {
            [Tooltip("Suspension ray origin offset from the body origin, metres " +
                "(+X right, +Y up, +Z forward).")]
            public Vector3 mount;
            [Tooltip("Wheel radius, metres.")]
            public float radius = 0.32f;
            [Tooltip("Road-wheel steering applies to this wheel.")]
            public bool steerable;
            [Tooltip("This wheel shares engine and brake demand.")]
            public bool driven = true;

            public WheelSpec Copy()
            {
                return (WheelSpec)MemberwiseClone();
            }
        }

        [Header("Chassis")]
        [Tooltip("Total vehicle mass, kilograms.")]
        public float massKg = 1200f;
        [Tooltip("Center of mass offset from the body origin, metres.")]
        public Vector3 comOffset = new Vector3(0f, -0.15f, 0f);
        [Tooltip("Collision box extents, metres (x width, y height, z length).")]
        public Vector3 chassisSize = new Vector3(1.8f, 1.1f, 3.2f);
        [Tooltip("Positive multipliers on the collider-derived inertia tensor (per axis).")]
        public Vector3 inertiaMultipliers = Vector3.one;

        [Header("Wheels and suspension")]
        [Tooltip("Authored wheel rays: two inline contacts for a motorcycle, four for a car.")]
        public WheelSpec[] wheels =
        {
            new WheelSpec { mount = new Vector3(-0.8f, -0.1f, 1.2f), steerable = true, driven = true },
            new WheelSpec { mount = new Vector3(0.8f, -0.1f, 1.2f), steerable = true, driven = true },
            new WheelSpec { mount = new Vector3(-0.8f, -0.1f, -1.2f), steerable = false, driven = true },
            new WheelSpec { mount = new Vector3(0.8f, -0.1f, -1.2f), steerable = false, driven = true },
        };
        [Tooltip("Suspension rest length, metres.")]
        public float suspensionRestLength = 0.35f;
        [Tooltip("Added suspension droop beyond rest, metres.")]
        public float suspensionExtraDroop = 0.10f;
        [Tooltip("Suspension spring rate per wheel, newtons per metre.")]
        public float springRatePerWheel = 40000f;
        [Tooltip("Suspension damper per wheel, newton-seconds per metre.")]
        public float damperPerWheel = 3500f;
        [Tooltip("Maximum authored normal support per wheel, newtons.")]
        public float maxNormalLoadPerWheel = 20000f;

        [Header("Tires, drive and braking")]
        [Tooltip("Road friction coefficient; one combined grip budget per wheel.")]
        public float frictionCoefficient = 1.1f;
        [Tooltip("Peak total engine force split across driven wheels, newtons.")]
        public float peakDriveForce = 7200f;
        [Tooltip("Peak total brake force, newtons.")]
        public float peakBrakeForce = 12000f;
        [Tooltip("Soft cruise speed where engine force tapers, metres per second.")]
        public float cruiseSpeed = 18f;
        [Tooltip("Boost speed target, metres per second.")]
        public float boostSpeed = 26f;
        [Tooltip("Quadratic aero drag coefficient, newtons per (m/s)^2.")]
        public float dragCoefficient = 0.4f;
        [Tooltip("Rolling resistance as a fraction of normal load.")]
        public float rollingResistance = 0.01f;

        [Header("Steering assistance")]
        [Tooltip("Maximum low-speed road-wheel steer, radians.")]
        public float steerMaxRadians = 0.5236f;
        [Tooltip("Steering authority floor at reference speed, radians.")]
        public float steerHighSpeedRadians = 0.12f;
        [Tooltip("Speed where the steering floor applies, metres per second.")]
        public float steerReferenceSpeed = 20f;
        [Tooltip("Steering change rate limit, radians per second.")]
        public float steerRateRadiansPerSecond = 4f;

        [Header("Assistance")]
        [Tooltip("Optional anti-roll spring rate, newtons per metre; 0 disables.")]
        public float antiRollRate;
        [Tooltip("Bounded roll damping torque, newton-metre-seconds per radian; 0 disables.")]
        public float rollDamping = 1500f;
        [Tooltip("Motorcycle lean target bound, radians (45 deg = 0.7854).")]
        public float assistMaxLeanRadians = 0.7854f;
        [Tooltip("Balance assist cutout past this fall angle, radians (70 deg = 1.2217).")]
        public float assistFallAngleRadians = 1.2217f;
        [Tooltip("Bike balance proportional torque, newton-metres per radian; 0 disables.")]
        public float balanceGain;
        [Tooltip("Bike balance derivative torque, newton-metre-seconds per radian.")]
        public float balanceDamping;

        // Write every field from a Core preset so an authored source and the
        // generated asset can never drift apart (same convention as
        // VehicleDefinitionAsset.ApplyPreset).
        public void ApplyPreset(VehiclePhysicsTuning preset)
        {
            if (preset == null) return;
            massKg = preset.massKg;
            comOffset = new Vector3(preset.comOffsetX, preset.comOffsetY, preset.comOffsetZ);
            chassisSize = new Vector3(preset.chassisWidth, preset.chassisHeight, preset.chassisLength);
            inertiaMultipliers = new Vector3(preset.inertiaMultiplierX,
                preset.inertiaMultiplierY, preset.inertiaMultiplierZ);
            var source = preset.wheels ?? new VehicleWheelSpec[0];
            wheels = new WheelSpec[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                var wheel = source[i];
                wheels[i] = wheel == null
                    ? new WheelSpec()
                    : new WheelSpec
                    {
                        mount = new Vector3(wheel.mountX, wheel.mountY, wheel.mountZ),
                        radius = wheel.radius,
                        steerable = wheel.steerable,
                        driven = wheel.driven
                    };
            }
            suspensionRestLength = preset.suspensionRestLength;
            suspensionExtraDroop = preset.suspensionExtraDroop;
            springRatePerWheel = preset.springRatePerWheel;
            damperPerWheel = preset.damperPerWheel;
            maxNormalLoadPerWheel = preset.maxNormalLoadPerWheel;
            frictionCoefficient = preset.frictionCoefficient;
            peakDriveForce = preset.peakDriveForce;
            peakBrakeForce = preset.peakBrakeForce;
            cruiseSpeed = preset.cruiseSpeed;
            boostSpeed = preset.boostSpeed;
            dragCoefficient = preset.dragCoefficient;
            rollingResistance = preset.rollingResistance;
            steerMaxRadians = preset.steerMaxRadians;
            steerHighSpeedRadians = preset.steerHighSpeedRadians;
            steerReferenceSpeed = preset.steerReferenceSpeed;
            steerRateRadiansPerSecond = preset.steerRateRadiansPerSecond;
            antiRollRate = preset.antiRollRate;
            rollDamping = preset.rollDamping;
            assistMaxLeanRadians = preset.assistMaxLeanRadians;
            assistFallAngleRadians = preset.assistFallAngleRadians;
            balanceGain = preset.balanceGain;
            balanceDamping = preset.balanceDamping;
        }

        public VehiclePhysicsTuning ToTuning(out string error)
        {
            var source = wheels ?? new WheelSpec[0];
            var specs = new VehicleWheelSpec[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                var wheel = source[i];
                if (wheel == null)
                {
                    error = name + ": null wheel entry " + i;
                    return null;
                }
                specs[i] = new VehicleWheelSpec
                {
                    mountX = wheel.mount.x,
                    mountY = wheel.mount.y,
                    mountZ = wheel.mount.z,
                    radius = wheel.radius,
                    steerable = wheel.steerable,
                    driven = wheel.driven
                };
            }
            var tuning = new VehiclePhysicsTuning
            {
                massKg = massKg,
                comOffsetX = comOffset.x,
                comOffsetY = comOffset.y,
                comOffsetZ = comOffset.z,
                chassisWidth = chassisSize.x,
                chassisHeight = chassisSize.y,
                chassisLength = chassisSize.z,
                inertiaMultiplierX = inertiaMultipliers.x,
                inertiaMultiplierY = inertiaMultipliers.y,
                inertiaMultiplierZ = inertiaMultipliers.z,
                wheels = specs,
                suspensionRestLength = suspensionRestLength,
                suspensionExtraDroop = suspensionExtraDroop,
                springRatePerWheel = springRatePerWheel,
                damperPerWheel = damperPerWheel,
                maxNormalLoadPerWheel = maxNormalLoadPerWheel,
                frictionCoefficient = frictionCoefficient,
                peakDriveForce = peakDriveForce,
                peakBrakeForce = peakBrakeForce,
                cruiseSpeed = cruiseSpeed,
                boostSpeed = boostSpeed,
                dragCoefficient = dragCoefficient,
                rollingResistance = rollingResistance,
                steerMaxRadians = steerMaxRadians,
                steerHighSpeedRadians = steerHighSpeedRadians,
                steerReferenceSpeed = steerReferenceSpeed,
                steerRateRadiansPerSecond = steerRateRadiansPerSecond,
                antiRollRate = antiRollRate,
                rollDamping = rollDamping,
                assistMaxLeanRadians = assistMaxLeanRadians,
                assistFallAngleRadians = assistFallAngleRadians,
                balanceGain = balanceGain,
                balanceDamping = balanceDamping
            };
            // The candidate is validated as a whole before it leaves: a bad
            // asset can never partially configure a vehicle.
            if (!tuning.Validate(out error))
            {
                error = name + ": invalid physics tuning field " + error;
                return null;
            }
            error = null;
            return tuning;
        }
    }
}
