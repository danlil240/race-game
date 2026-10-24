using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Progression
{
    // One class finale: a named rival race whose completion counts toward
    // world completion and whose win pays a one-time rival bounty plus
    // the world-completion vehicle reward.
    public sealed class FinaleDefinition
    {
        public string eventId = "";
        public string classId = "";
        public string rivalId = "";
        public string rewardVehicleId = "";

        public bool Validate(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(eventId)) { error = "eventId"; return false; }
            if (string.IsNullOrEmpty(classId)) { error = "classId"; return false; }
            if (string.IsNullOrEmpty(rivalId)) { error = "rivalId"; return false; }
            if (string.IsNullOrEmpty(rewardVehicleId)) { error = "rewardVehicleId"; return false; }
            return true;
        }

        public FinaleDefinition Copy()
        {
            return (FinaleDefinition)MemberwiseClone();
        }
    }

    // One campaign world: which courses and regular events belong to it,
    // and the regional gate that unlocks the class finales. Display names
    // are bilingual (English/Hebrew, Hebrew stored in logical order).
    public sealed class CampaignDefinition
    {
        public const int ObjectiveBitsPerEvent = 3; // completion + introductory + mastery

        public string worldId = "";
        public string nameEn = "";
        public string nameHe = "";
        // Regional gate: ALL regular events completed AND at least this
        // many unique regional stars (sunny-island: 24 and 30).
        public int requiredEventCompletions;
        public int requiredRegionalStars;
        public List<string> courseIds = new List<string>();
        public List<string> regularEventIds = new List<string>();
        public List<FinaleDefinition> finales = new List<FinaleDefinition>();

        public bool Validate(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(worldId)) { error = "worldId"; return false; }
            if (string.IsNullOrEmpty(nameEn)) { error = "nameEn"; return false; }
            if (string.IsNullOrEmpty(nameHe)) { error = "nameHe"; return false; }
            if (requiredEventCompletions <= 0) { error = "requiredEventCompletions"; return false; }
            if (requiredRegionalStars <= 0) { error = "requiredRegionalStars"; return false; }
            if (courseIds == null || courseIds.Count == 0) { error = "courseIds"; return false; }
            if (regularEventIds == null || regularEventIds.Count == 0)
            { error = "regularEventIds"; return false; }
            if (finales == null || finales.Count == 0) { error = "finales"; return false; }
            if (courseIds != null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var courseId in courseIds)
                {
                    if (string.IsNullOrEmpty(courseId)) { error = "courseIds"; return false; }
                    if (!seen.Add(courseId)) { error = "courseIds.duplicate"; return false; }
                }
            }
            if (regularEventIds != null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var eventId in regularEventIds)
                {
                    if (string.IsNullOrEmpty(eventId)) { error = "regularEventIds"; return false; }
                    if (!seen.Add(eventId)) { error = "regularEventIds.duplicate"; return false; }
                }
            }
            if (finales != null)
            {
                var seenEvents = new HashSet<string>(StringComparer.Ordinal);
                var seenClasses = new HashSet<string>(StringComparer.Ordinal);
                foreach (var finale in finales)
                {
                    if (finale == null || !finale.Validate(out error))
                    { error = "finales." + error; return false; }
                    if (!seenEvents.Add(finale.eventId))
                    { error = "finales.eventId.duplicate"; return false; }
                    if (!seenClasses.Add(finale.classId))
                    { error = "finales.classId.duplicate"; return false; }
                }
            }
            return true;
        }

        public CampaignDefinition Copy()
        {
            var copy = new CampaignDefinition
            {
                worldId = worldId,
                nameEn = nameEn,
                nameHe = nameHe,
                requiredEventCompletions = requiredEventCompletions,
                requiredRegionalStars = requiredRegionalStars,
                courseIds = new List<string>(),
                regularEventIds = new List<string>(),
                finales = new List<FinaleDefinition>()
            };
            if (courseIds != null) copy.courseIds.AddRange(courseIds);
            if (regularEventIds != null) copy.regularEventIds.AddRange(regularEventIds);
            if (finales != null)
                foreach (var finale in finales)
                    copy.finales.Add(finale == null ? null : finale.Copy());
            return copy;
        }
    }
}
