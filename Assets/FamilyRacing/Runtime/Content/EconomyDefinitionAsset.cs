using System;
using FamilyRacing.Core.Progression;
using UnityEngine;

namespace FamilyRacing
{
    // Authored economy table converted into a validated Core
    // EconomyDefinition: completion/placement rewards, challenge bounty
    // payouts, the one-time rival victory bounty and the upgrade price
    // table.
    [CreateAssetMenu(menuName = "FamilyRacing/Economy Definition",
        fileName = "economy-definition")]
    public sealed class EconomyDefinitionAsset : ScriptableObject
    {
        [Serializable]
        public sealed class CategoryRow
        {
            public string categoryId = ""; // acceleration|handling|boost
            public int maxLevel = 3;
            public int[] prices = { 60, 120, 180 };
            public float levelMultiplierStep = 0.05f;
            // VehicleTuning fields this category's multiplier scales.
            public string[] tuningFields = new string[0];
        }

        public int completionCoins = 20;
        // Placement bonus by finishing place, index 0 = first place.
        public int[] placementBonusCoins = { 5, 3, 1, 0 };
        public int challengeIntroductoryCoins = 10;
        public int challengeMasteryCoins = 20;
        public int rivalVictoryCoins = 30;
        public CategoryRow[] upgradeCategories = new CategoryRow[0];

        public EconomyDefinition ToDefinition(out string error)
        {
            var economy = new EconomyDefinition
            {
                completionCoins = completionCoins,
                challengeIntroductoryCoins = challengeIntroductoryCoins,
                challengeMasteryCoins = challengeMasteryCoins,
                rivalVictoryCoins = rivalVictoryCoins
            };
            if (placementBonusCoins != null)
                economy.placementBonusCoins.AddRange(placementBonusCoins);
            if (upgradeCategories != null)
                foreach (var row in upgradeCategories)
                {
                    if (row == null)
                    {
                        error = name + ": null upgrade category row";
                        return null;
                    }
                    var category = new UpgradeCategoryDefinition
                    {
                        id = row.categoryId,
                        maxLevel = row.maxLevel,
                        levelMultiplierStep = row.levelMultiplierStep
                    };
                    if (row.prices != null) category.prices.AddRange(row.prices);
                    if (row.tuningFields != null)
                        category.tuningFields.AddRange(row.tuningFields);
                    economy.upgradeCategories.Add(category);
                }
            if (!economy.Validate(out error))
            {
                error = name + ": invalid economy field " + error;
                return null;
            }
            error = null;
            return economy;
        }
    }
}
