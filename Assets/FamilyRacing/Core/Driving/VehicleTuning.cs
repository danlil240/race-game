using System;

namespace FamilyRacing.Core.Driving
{
    // Editable arcade vehicle parameters. Every value must be finite;
    // Validate() is the single authority the motor consults so a NaN or
    // out-of-range asset can never partially mutate state.
    public sealed class VehicleTuning
    {
        public string vehicleId = "";
        public float maxSpeed = 18f;            // m/s forward limit without boost
        public float acceleration = 6f;         // m/s^2 auto-acceleration
        public float steeringRate = 2.6f;       // rad/s at full lock and full authority
        public float steeringReferenceSpeed = 6f; // speed where steering reaches full authority
        public float grip = 4f;                 // lateral speed decay per second
        public float driftMinSpeed = 6f;        // minimum speed to hold a drift
        public float driftSteeringFactor = 1.5f; // steering multiplier while drifting
        public float driftLateralAcceleration = 6f; // sideways kick while drifting
        public float driftChargePerSecond = 0.3f; // charge gained per drift second
        public float boostMaxSpeed = 26f;       // m/s limit while boost is active
        public float boostAcceleration = 12f;   // m/s^2 while boosting
        public float boostDurationSeconds = 2.5f;
        public float boostDrainPerSecond = 0.4f; // charge spent per boost second
        public float boostMinCharge = 0.1f;     // minimum charge that can fire a boost
        public float boostChargeSeconds = 6f;   // time to refill one boost
        public float cleanLandingCredit = 0.2f; // charge granted per clean landing
        public float landingMinAirSeconds = 0.25f; // min air time for a creditable jump
        public float landingMinDrop = 0.4f;     // metres the flight must descend to credit
        public float driftSteeringThreshold = 0.15f; // steering past this engages drift
        public float gravity = 22f;             // downward acceleration while airborne
        public float terminalFallSpeed = 30f;
        public float groundProbe = 0.8f;        // probe reach below the body origin
        public float rideHeight = 0.55f;        // visual lift of the body above ground
        public float halfWidth = 0.9f;          // swept body half extents (metres)
        public float halfLength = 1.6f;
        public float height = 1.1f;
        public float wallSpeedRetention = 0.15f; // fraction of into-wall speed kept on contact

        // Physical backend is opt-in: legacy content stays Arcade. `physics`
        // carries the validated SI tuning the physical backend consumes — it
        // is required when simulationBackend is PhysicalAssisted and is
        // validated whenever present, even under Arcade. The
        // VehicleSimulationBackend enum lives in IVehicleSimulation.cs.
        public VehicleSimulationBackend simulationBackend = VehicleSimulationBackend.Arcade;
        public VehiclePhysicsTuning physics;

        public bool Validate(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(vehicleId)) { error = "vehicleId"; return false; }
            if (!Positive(maxSpeed)) { error = "maxSpeed"; return false; }
            if (!Positive(acceleration)) { error = "acceleration"; return false; }
            if (!Positive(steeringRate)) { error = "steeringRate"; return false; }
            if (!Positive(steeringReferenceSpeed)) { error = "steeringReferenceSpeed"; return false; }
            if (!Positive(grip)) { error = "grip"; return false; }
            if (float.IsNaN(driftMinSpeed) || float.IsInfinity(driftMinSpeed) || driftMinSpeed < 0f)
            { error = "driftMinSpeed"; return false; }
            if (!Positive(driftSteeringFactor)) { error = "driftSteeringFactor"; return false; }
            if (float.IsNaN(driftLateralAcceleration) || float.IsInfinity(driftLateralAcceleration)
                || driftLateralAcceleration < 0f) { error = "driftLateralAcceleration"; return false; }
            if (float.IsNaN(driftChargePerSecond) || float.IsInfinity(driftChargePerSecond)
                || driftChargePerSecond < 0f) { error = "driftChargePerSecond"; return false; }
            if (!Positive(boostMaxSpeed) || boostMaxSpeed < maxSpeed) { error = "boostMaxSpeed"; return false; }
            if (!Positive(boostAcceleration)) { error = "boostAcceleration"; return false; }
            if (!Positive(boostDurationSeconds)) { error = "boostDurationSeconds"; return false; }
            if (!Positive(boostDrainPerSecond)) { error = "boostDrainPerSecond"; return false; }
            if (float.IsNaN(boostMinCharge) || float.IsInfinity(boostMinCharge)
                || boostMinCharge < 0f || boostMinCharge > 1f)
            { error = "boostMinCharge"; return false; }
            if (!Positive(boostChargeSeconds)) { error = "boostChargeSeconds"; return false; }
            if (float.IsNaN(cleanLandingCredit) || float.IsInfinity(cleanLandingCredit)
                || cleanLandingCredit < 0f || cleanLandingCredit > 1f)
            { error = "cleanLandingCredit"; return false; }
            if (!Positive(landingMinAirSeconds)) { error = "landingMinAirSeconds"; return false; }
            if (float.IsNaN(landingMinDrop) || float.IsInfinity(landingMinDrop)
                || landingMinDrop < 0f) { error = "landingMinDrop"; return false; }
            if (float.IsNaN(driftSteeringThreshold)
                || float.IsInfinity(driftSteeringThreshold)
                || driftSteeringThreshold <= 0f || driftSteeringThreshold > 1f)
            { error = "driftSteeringThreshold"; return false; }
            if (!Positive(gravity)) { error = "gravity"; return false; }
            if (!Positive(terminalFallSpeed)) { error = "terminalFallSpeed"; return false; }
            if (!Positive(groundProbe)) { error = "groundProbe"; return false; }
            if (float.IsNaN(rideHeight) || float.IsInfinity(rideHeight) || rideHeight < 0f)
            { error = "rideHeight"; return false; }
            if (!Positive(halfWidth)) { error = "halfWidth"; return false; }
            if (!Positive(halfLength)) { error = "halfLength"; return false; }
            if (!Positive(height)) { error = "height"; return false; }
            if (float.IsNaN(wallSpeedRetention) || float.IsInfinity(wallSpeedRetention)
                || wallSpeedRetention < 0f || wallSpeedRetention > 1f)
            { error = "wallSpeedRetention"; return false; }
            if (!Enum.IsDefined(typeof(VehicleSimulationBackend), simulationBackend))
            { error = "simulationBackend"; return false; }
            if (physics != null && !physics.Validate(out error))
            { error = "physics." + error; return false; }
            if (simulationBackend == VehicleSimulationBackend.PhysicalAssisted
                && physics == null)
            { error = "physics"; return false; }
            return true;
        }

        private static bool Positive(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
        }

        // Content convention shared with the LAN session layer: a vehicle
        // id's class is the segment before its first '-' (car-01 -> car).
        // Ids without a separator are their own class.
        public static string ClassIdOf(string vehicleId)
        {
            if (string.IsNullOrEmpty(vehicleId)) return "";
            var dash = vehicleId.IndexOf('-');
            return dash < 0 ? vehicleId : vehicleId.Substring(0, dash);
        }

        // Rally car: heavier, grippier, calmer steering.
        public static VehicleTuning Car01()
        {
            return new VehicleTuning { vehicleId = "car-01" };
        }

        // Motorcycle: quicker steering and lighter feel; balance is
        // automatic so no balance parameters exist. This preset is the
        // authored source of truth — the generated motorcycle-01 asset is
        // written from it (see Editor/DrivingPrototypeSetup.ApplyPreset)
        // and DriftBoostTests asserts the two stay identical.
        public static VehicleTuning Motorcycle01()
        {
            return new VehicleTuning
            {
                vehicleId = "motorcycle-01",
                maxSpeed = 20f,
                acceleration = 7f,
                steeringRate = 3.4f,
                steeringReferenceSpeed = 5f,
                grip = 5f,
                driftMinSpeed = 5f,
                driftSteeringFactor = 1.35f,
                driftLateralAcceleration = 5f,
                driftChargePerSecond = 0.35f,
                boostMaxSpeed = 28f,
                boostAcceleration = 14f,
                boostDurationSeconds = 2.2f,
                boostDrainPerSecond = 0.45f,
                boostMinCharge = 0.1f,
                boostChargeSeconds = 5.5f,
                cleanLandingCredit = 0.25f,
                landingMinAirSeconds = 0.25f,
                gravity = 24f,
                terminalFallSpeed = 34f,
                rideHeight = 0.6f,
                halfWidth = 0.5f,
                halfLength = 1.1f,
                height = 1.4f,
                wallSpeedRetention = 0.1f
            };
        }
    }
}
