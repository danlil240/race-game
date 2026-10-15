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

        // Ordered campaign worlds (M6 Task 1). The legacy single
        // `campaign` field is the fallback only when this array is empty;
        // populating both is an authoring error and is rejected rather
        // than silently choosing one.
        public CampaignDefinitionAsset[] campaigns =
            new CampaignDefinitionAsset[0];
        public CampaignDefinitionAsset campaign;
        public BountyCatalogAsset bounties;
        public EconomyDefinitionAsset economy;
        public GadgetCatalogAsset gadgets;
        public DifficultyRow[] difficulties = new DifficultyRow[0];
        // M6 Task 4: the authored bilingual voice-prompt table is
        // production-only content — generic conversion deliberately
        // ignores this slot so fixtures and mid-production trees keep
        // converting; FullContentValidation is the gate that requires
        // the complete clip set. Task 8's presenter resolves clips
        // through this catalog at runtime.
        public VoicePromptCatalogAsset voicePrompts;

        public GameplayCatalog ToCatalog(DrivingContentCatalog driving,
            out string error)
        {
            if (driving == null)
            {
                error = name + ": missing driving catalog";
                return null;
            }
            var hasCampaigns = campaigns != null && campaigns.Length > 0;
            if (hasCampaigns && campaign != null)
            {
                error = name + ": both campaign and campaigns are "
                    + "populated";
                return null;
            }
            if ((!hasCampaigns && campaign == null) || bounties == null
                || economy == null || gadgets == null)
            {
                error = name + ": missing catalog section reference";
                return null;
            }
            string sectionError;
            var campaignDefs = new List<CampaignDefinition>();
            if (hasCampaigns)
            {
                foreach (var campaignAsset in campaigns)
                {
                    if (campaignAsset == null)
                    {
                        error = name + ": null campaign asset";
                        return null;
                    }
                    var def = campaignAsset.ToDefinition(out sectionError);
                    if (def == null)
                    {
                        error = name + ": " + sectionError;
                        return null;
                    }
                    campaignDefs.Add(def);
                }
            }
            else
            {
                var def = campaign.ToDefinition(out sectionError);
                if (def == null)
                {
                    error = name + ": " + sectionError;
                    return null;
                }
                campaignDefs.Add(def);
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
            if (!GameplayCatalog.TryCreate(driving, campaignDefs,
                    bountyDefs, economyDef, gadgetDefs, difficultyDefs,
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
