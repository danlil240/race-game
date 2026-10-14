using System;
using System.Collections.Generic;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using UnityEngine;

namespace FamilyRacing
{
    // Root authored asset that binds every gameplay-loop section into
    // one validated Core GameplayCatalog over a DrivingContentCatalog.
    // Conversion is atomic: any bad section fails the whole ToCatalog —
    // a partially converted catalog is never returned.
    [CreateAssetMenu(menuName = "FamilyRacing/Gameplay Catalog",
        fileName = "gameplay-catalog")]
    public sealed class GameplayCatalogAsset : ScriptableObject
    {
        [Serializable]
        public sealed class DifficultyRow
        {
            public string difficultyId = ""; // easy | medium | hard
            public float brakeMarginScale = 1f;
            public float boostUseMinCharge = 0.9f;
            public float gadgetUseWillingness = 0.5f;
            public float shortcutWillingness = 0.5f;
        }

        public CampaignDefinitionAsset campaign;
        public BountyCatalogAsset bounties;
        public EconomyDefinitionAsset economy;
        public GadgetCatalogAsset gadgets;
        public DifficultyRow[] difficulties = new DifficultyRow[0];

        public GameplayCatalog ToCatalog(DrivingContentCatalog driving,
            out string error)
        {
            if (driving == null)
            {
                error = name + ": missing driving catalog";
                return null;
            }
            if (campaign == null || bounties == null || economy == null
                || gadgets == null)
            {
                error = name + ": missing catalog section reference";
                return null;
            }
            string sectionError;
            var campaignDef = campaign.ToDefinition(out sectionError);
            if (campaignDef == null)
            {
                error = name + ": " + sectionError;
                return null;
            }
            var bountyDefs = bounties.ToDefinitions(out sectionError);
            if (bountyDefs == null)
            {
                error = name + ": " + sectionError;
                return null;
            }
            var economyDef = economy.ToDefinition(out sectionError);
            if (economyDef == null)
            {
                error = name + ": " + sectionError;
                return null;
            }
            var gadgetDefs = gadgets.ToCatalog(out sectionError);
            if (gadgetDefs == null)
            {
                error = name + ": " + sectionError;
                return null;
            }
            var difficultyDefs = new List<AiDifficultyDefinition>();
            if (difficulties != null)
                foreach (var row in difficulties)
                {
                    if (row == null)
                    {
                        error = name + ": null difficulty row";
                        return null;
                    }
                    var difficulty = new AiDifficultyDefinition
                    {
                        id = row.difficultyId,
                        brakeMarginScale = row.brakeMarginScale,
                        boostUseMinCharge = row.boostUseMinCharge,
                        gadgetUseWillingness = row.gadgetUseWillingness,
                        shortcutWillingness = row.shortcutWillingness
                    };
                    if (!difficulty.Validate(out sectionError))
                    {
                        error = name + ": invalid difficulty "
                            + row.difficultyId + " field " + sectionError;
                        return null;
                    }
                    difficultyDefs.Add(difficulty);
                }
            if (!GameplayCatalog.TryCreate(driving, campaignDef, bountyDefs,
                    economyDef, gadgetDefs, difficultyDefs,
                    out var catalog, out sectionError))
            {
                error = name + ": " + sectionError;
                return null;
            }
            error = null;
            return catalog;
        }
    }
}
