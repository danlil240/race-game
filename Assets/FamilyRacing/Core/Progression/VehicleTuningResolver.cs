using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Progression
{
    // Resolves the VehicleTuning a profile's durable upgrades produce
    // for one vehicle in one mode (M5 Task 8). Progression modes
    // (Campaign, Quick Race) apply each authored upgrade category's
    // level multiplier to exactly the category's authored tuningFields;
    // every normalized mode (LAN, Time Trial, Practice) resolves the
    // catalog baseline untouched — LAN fairness can never depend on a
    // participant's wallet, and trials/practice run the authored tune.
    //
    // Every call returns a FRESH object copied off the catalog baseline:
    // the catalog is never mutated (its compatibility fingerprint stays
    // the proof) and two resolves never share an instance. Any content
    // or resolved-value failure returns null rather than partially
    // applying — an unmappable authored field or an invalid tune is
    // refused, never silently raced.
    public static class VehicleTuningResolver
    {
        public static VehicleTuning Resolve(RaceMode mode, string vehicleId,
            ProfileSave profile, GameplayCatalog catalog)
        {
            if (catalog == null || catalog.Driving == null) return null;
            VehicleTuning baseline;
            if (string.IsNullOrEmpty(vehicleId)
                || !catalog.Driving.Vehicles.TryGetValue(vehicleId,
                    out baseline)
                || baseline == null)
                return null;
            var resolved = baseline.Copy();
            if (!AppliesProfileUpgrades(mode)) return resolved;
            if (profile == null || profile.vehicleUpgrades == null
                || catalog.Economy == null
                || catalog.Economy.upgradeCategories == null)
                return resolved;
            VehicleUpgrade row = null;
            foreach (var candidate in profile.vehicleUpgrades)
                if (candidate != null && candidate.vehicleId == vehicleId)
                    row = candidate;
            if (row == null) return resolved;

            foreach (var category in catalog.Economy.upgradeCategories)
            {
                if (category == null || category.id == null) continue;
                // Only the authored categories map onto the persisted
                // counters; an authored id without a counter is a
                // content/resolver disagreement — refuse, never skip.
                var level = row.LevelFor(category.id);
                if (level < 0) return null;
                if (level == 0) continue;
                var multiplier = category.MultiplierForLevel(level);
                if (category.tuningFields == null) return null;
                foreach (var field in category.tuningFields)
                    if (!ApplyField(resolved, field, multiplier))
                        return null;
            }
            string error;
            return resolved.Validate(out error) ? resolved : null;
        }

        // Progression modes seat the purchased tune; every other mode
        // normalizes to the authored baseline. Unknown mode values
        // normalize too — normalization is the safe default, never the
        // upgraded path.
        private static bool AppliesProfileUpgrades(RaceMode mode)
        {
            return mode == RaceMode.Campaign || mode == RaceMode.QuickRace;
        }

        // The full authored-field vocabulary: every numeric
        // VehicleTuning field the economy may name. An authored name
        // outside this set fails the resolve closed — it is a content
        // bug the race must not paper over.
        private static bool ApplyField(VehicleTuning tuning, string field,
            float multiplier)
        {
            switch (field)
            {
                case "acceleration":
                    tuning.acceleration *= multiplier; return true;
                case "maxSpeed":
                    tuning.maxSpeed *= multiplier; return true;
                case "steeringRate":
                    tuning.steeringRate *= multiplier; return true;
                case "steeringReferenceSpeed":
                    tuning.steeringReferenceSpeed *= multiplier;
                    return true;
                case "grip":
                    tuning.grip *= multiplier; return true;
                case "driftMinSpeed":
                    tuning.driftMinSpeed *= multiplier; return true;
                case "driftSteeringFactor":
                    tuning.driftSteeringFactor *= multiplier; return true;
                case "driftLateralAcceleration":
                    tuning.driftLateralAcceleration *= multiplier;
                    return true;
                case "driftChargePerSecond":
                    tuning.driftChargePerSecond *= multiplier; return true;
                case "boostMaxSpeed":
                    tuning.boostMaxSpeed *= multiplier; return true;
                case "boostAcceleration":
                    tuning.boostAcceleration *= multiplier; return true;
                case "boostDurationSeconds":
                    tuning.boostDurationSeconds *= multiplier; return true;
                case "boostDrainPerSecond":
                    tuning.boostDrainPerSecond *= multiplier; return true;
                case "boostMinCharge":
                    tuning.boostMinCharge *= multiplier; return true;
                case "boostChargeSeconds":
                    tuning.boostChargeSeconds *= multiplier; return true;
                case "cleanLandingCredit":
                    tuning.cleanLandingCredit *= multiplier; return true;
                case "landingMinAirSeconds":
                    tuning.landingMinAirSeconds *= multiplier; return true;
                case "landingMinDrop":
                    tuning.landingMinDrop *= multiplier; return true;
                case "driftSteeringThreshold":
                    tuning.driftSteeringThreshold *= multiplier; return true;
                case "gravity":
                    tuning.gravity *= multiplier; return true;
                case "terminalFallSpeed":
                    tuning.terminalFallSpeed *= multiplier; return true;
                case "groundProbe":
                    tuning.groundProbe *= multiplier; return true;
                case "rideHeight":
                    tuning.rideHeight *= multiplier; return true;
                case "halfWidth":
                    tuning.halfWidth *= multiplier; return true;
                case "halfLength":
                    tuning.halfLength *= multiplier; return true;
                case "height":
                    tuning.height *= multiplier; return true;
                case "wallSpeedRetention":
                    tuning.wallSpeedRetention *= multiplier; return true;
                default: return false;
            }
        }
    }
}
