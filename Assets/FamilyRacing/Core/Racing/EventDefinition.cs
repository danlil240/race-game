using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Racing
{
    // One playable driving event: which course, how many laps, the
    // countdown length (simulation ticks that do not charge race elapsed
    // time), which vehicles may enter, and the data-driven recovery
    // thresholds.
    public sealed class EventDefinition
    {
        public string id = "";
        public string courseId = "";
        public int laps = 1;
        public int countdownTicks = 90;
        public List<string> vehicleIds = new List<string>();
        public RecoverySettings recovery = new RecoverySettings();

        public bool Validate(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(id)) { error = "id"; return false; }
            if (string.IsNullOrEmpty(courseId)) { error = "courseId"; return false; }
            if (laps < 1) { error = "laps"; return false; }
            if (countdownTicks < 0) { error = "countdownTicks"; return false; }
            if (vehicleIds == null || vehicleIds.Count == 0) { error = "vehicleIds"; return false; }
            foreach (var vehicleId in vehicleIds)
                if (string.IsNullOrEmpty(vehicleId)) { error = "vehicleIds"; return false; }
            if (recovery == null) { error = "recovery"; return false; }
            if (!recovery.Validate(out error)) return false;
            return true;
        }

        public bool AllowsVehicle(string vehicleId)
        {
            return vehicleIds != null && vehicleIds.Contains(vehicleId);
        }

        public EventDefinition Copy()
        {
            return new EventDefinition
            {
                id = id,
                courseId = courseId,
                laps = laps,
                countdownTicks = countdownTicks,
                vehicleIds = vehicleIds == null ? null : new List<string>(vehicleIds),
                recovery = recovery == null ? null : recovery.Copy()
            };
        }
    }
}
