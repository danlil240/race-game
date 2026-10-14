using System;
using System.Collections.Generic;
using FamilyRacing.Core.Progression;
using UnityEngine;

namespace FamilyRacing
{
    // Authored campaign-world data converted into a validated Core
    // CampaignDefinition: world identity, bilingual display name, the
    // course/event membership and the regional finale gate.
    [CreateAssetMenu(menuName = "FamilyRacing/Campaign Definition",
        fileName = "campaign-definition")]
    public sealed class CampaignDefinitionAsset : ScriptableObject
    {
        [Serializable]
        public sealed class FinaleRow
        {
            public string eventId = "";
            public string classId = ""; // "car" | "motorcycle"
            public string rivalId = "";
            public string rewardVehicleId = "";
        }

        public string worldId = "sunny-island";
        public string nameEn = "Sunny Island";
        // Hebrew display name in normal logical order; right-to-left
        // layout is the caller's responsibility.
        public string nameHe = "";
        // Regional gate: ALL regular events completed AND at least this
        // many unique regional stars (sunny-island: 24 and 30).
        public int requiredEventCompletions = 24;
        public int requiredRegionalStars = 30;
        public string[] courseIds = { "harbor-hop", "lighthouse-loop" };
        public string[] regularEventIds = new string[0];
        public FinaleRow[] finales = new FinaleRow[0];

        public CampaignDefinition ToDefinition(out string error)
        {
            var campaign = new CampaignDefinition
            {
                worldId = worldId,
                nameEn = nameEn,
                nameHe = nameHe,
                requiredEventCompletions = requiredEventCompletions,
                requiredRegionalStars = requiredRegionalStars
            };
            if (courseIds != null) campaign.courseIds.AddRange(courseIds);
            if (regularEventIds != null) campaign.regularEventIds.AddRange(regularEventIds);
            if (finales != null)
                foreach (var row in finales)
                {
                    if (row == null)
                    {
                        error = name + ": null finale row";
                        return null;
                    }
                    campaign.finales.Add(new FinaleDefinition
                    {
                        eventId = row.eventId,
                        classId = row.classId,
                        rivalId = row.rivalId,
                        rewardVehicleId = row.rewardVehicleId
                    });
                }
            if (!campaign.Validate(out error))
            {
                error = name + ": invalid campaign field " + error;
                return null;
            }
            error = null;
            return campaign;
        }
    }
}
