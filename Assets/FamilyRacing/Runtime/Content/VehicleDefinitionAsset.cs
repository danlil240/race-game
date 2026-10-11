using FamilyRacing.Core.Driving;
using UnityEngine;

namespace FamilyRacing
{
    // Authored vehicle parameters converted into a validated Core
    // VehicleTuning. Stable vehicleId travels with race evidence. The
    // generated vehicle assets are the runtime authority; the Core
    // VehicleTuning presets (Car01/Motorcycle01) are the authored source —
    // DrivingPrototypeSetup writes them via ApplyPreset and the parity
    // test keeps the two identical.
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
        public float driftChargePerSecond = 0.3f;
        public float boostMaxSpeed = 26f;
        public float boostAcceleration = 12f;
        public float boostDurationSeconds = 2.5f;
        public float boostDrainPerSecond = 0.4f;
        public float boostMinCharge = 0.1f;
        public float boostChargeSeconds = 6f;
        public float cleanLandingCredit = 0.2f;
        public float landingMinAirSeconds = 0.25f;
        public float gravity = 22f;
        public float terminalFallSpeed = 30f;
        public float groundProbe = 0.8f;
        public float rideHeight = 0.55f;
        public float halfWidth = 0.9f;
        public float halfLength = 1.6f;
        public float height = 1.1f;
        public float wallSpeedRetention = 0.15f;
        [Header("Presentation")]
        // Which graybox silhouette VehiclePresentation builds; collision
        // always uses the tuning box above, never the visual shape.
        public string silhouette = "car"; // "car" | "motorcycle"
        // Cosmetic body roll at full steering lock; purely visual.
        public float leanDegrees = 4f;
        public Vector3 bodyScale = new Vector3(1.8f, 1.1f, 3.2f);
        public Color bodyColor = new Color(0.91f, 0.51f, 0.42f);

        // Write every tuning field from a Core preset so the authored
        // source and the generated asset can never drift apart.
        public void ApplyPreset(VehicleTuning preset)
        {
            if (preset == null) return;
            vehicleId = preset.vehicleId;
            maxSpeed = preset.maxSpeed;
            acceleration = preset.acceleration;
            steeringRate = preset.steeringRate;
            steeringReferenceSpeed = preset.steeringReferenceSpeed;
            grip = preset.grip;
            driftMinSpeed = preset.driftMinSpeed;
            driftSteeringFactor = preset.driftSteeringFactor;
            driftLateralAcceleration = preset.driftLateralAcceleration;
            driftChargePerSecond = preset.driftChargePerSecond;
            boostMaxSpeed = preset.boostMaxSpeed;
            boostAcceleration = preset.boostAcceleration;
            boostDurationSeconds = preset.boostDurationSeconds;
            boostDrainPerSecond = preset.boostDrainPerSecond;
            boostMinCharge = preset.boostMinCharge;
            boostChargeSeconds = preset.boostChargeSeconds;
            cleanLandingCredit = preset.cleanLandingCredit;
            landingMinAirSeconds = preset.landingMinAirSeconds;
            gravity = preset.gravity;
            terminalFallSpeed = preset.terminalFallSpeed;
            groundProbe = preset.groundProbe;
            rideHeight = preset.rideHeight;
            halfWidth = preset.halfWidth;
            halfLength = preset.halfLength;
            height = preset.height;
            wallSpeedRetention = preset.wallSpeedRetention;
        }

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
                driftChargePerSecond = driftChargePerSecond,
                boostMaxSpeed = boostMaxSpeed,
                boostAcceleration = boostAcceleration,
                boostDurationSeconds = boostDurationSeconds,
                boostDrainPerSecond = boostDrainPerSecond,
                boostMinCharge = boostMinCharge,
                boostChargeSeconds = boostChargeSeconds,
                cleanLandingCredit = cleanLandingCredit,
                landingMinAirSeconds = landingMinAirSeconds,
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
