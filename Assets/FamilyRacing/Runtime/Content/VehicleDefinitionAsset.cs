using FamilyRacing.Core.Driving;
using UnityEngine;

namespace FamilyRacing
{
    // Authored vehicle parameters converted into a validated Core
    // VehicleTuning. Stable vehicleId travels with race evidence.
    [CreateAssetMenu(menuName = "FamilyRacing/Vehicle Definition",
        fileName = "vehicle-definition")]
    public sealed class VehicleDefinitionAsset : ScriptableObject
    {
        public string vehicleId = "car-01";
        public float maxSpeed = 18f;
        public float acceleration = 6f;
        public float steeringRate = 2.6f;
        public float steeringReferenceSpeed = 6f;
        public float grip = 4f;
        public float driftMinSpeed = 6f;
        public float driftSteeringFactor = 1.5f;
        public float driftLateralAcceleration = 6f;
        public float boostMaxSpeed = 26f;
        public float boostAcceleration = 12f;
        public float boostDurationSeconds = 2.5f;
        public float boostChargeSeconds = 6f;
        public float gravity = 22f;
        public float terminalFallSpeed = 30f;
        public float groundProbe = 0.8f;
        public float rideHeight = 0.55f;
        public float halfWidth = 0.9f;
        public float halfLength = 1.6f;
        public float height = 1.1f;
        public float wallSpeedRetention = 0.15f;
        public Vector3 bodyScale = new Vector3(1.8f, 1.1f, 3.2f);
        public Color bodyColor = new Color(0.91f, 0.51f, 0.42f);

        public VehicleTuning ToTuning(out string error)
        {
            var tuning = new VehicleTuning
            {
                vehicleId = vehicleId,
                maxSpeed = maxSpeed,
                acceleration = acceleration,
                steeringRate = steeringRate,
                steeringReferenceSpeed = steeringReferenceSpeed,
                grip = grip,
                driftMinSpeed = driftMinSpeed,
                driftSteeringFactor = driftSteeringFactor,
                driftLateralAcceleration = driftLateralAcceleration,
                boostMaxSpeed = boostMaxSpeed,
                boostAcceleration = boostAcceleration,
                boostDurationSeconds = boostDurationSeconds,
                boostChargeSeconds = boostChargeSeconds,
                gravity = gravity,
                terminalFallSpeed = terminalFallSpeed,
                groundProbe = groundProbe,
                rideHeight = rideHeight,
                halfWidth = halfWidth,
                halfLength = halfLength,
                height = height,
                wallSpeedRetention = wallSpeedRetention
            };
            if (!tuning.Validate(out error))
            {
                error = name + ": invalid tuning field " + error;
                return null;
            }
            error = null;
            return tuning;
        }
    }
}
