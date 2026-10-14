using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Progression
{
    // One upgrade category: `maxLevel` purchasable levels at `prices`
    // coins each. The level-N effect is additive: multiplier =
    // 1 + levelMultiplierStep * level (default step 0.05 -> max 1.15
    // at level 3). Which tuning fields each category scales is resolver
    // policy (Task 8), not data here.
    public sealed class UpgradeCategoryDefinition
    {
        public string id = "";
        public int maxLevel = 3;
        public List<int> prices = new List<int>();
        public float levelMultiplierStep = 0.05f;

        public bool Validate(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(id)) { error = "id"; return false; }
            if (maxLevel <= 0) { error = "maxLevel"; return false; }
            if (prices == null || prices.Count != maxLevel) { error = "prices"; return false; }
            if (prices != null)
                for (var i = 0; i < prices.Count; i++)
                {
                    if (prices[i] <= 0) { error = "prices"; return false; }
                    if (i > 0 && prices[i] < prices[i - 1])
                    { error = "prices.order"; return false; }
                }
            if (float.IsNaN(levelMultiplierStep) || float.IsInfinity(levelMultiplierStep)
                || levelMultiplierStep <= 0f)
            { error = "levelMultiplierStep"; return false; }
            return true;
        }

        public float MultiplierForLevel(int level)
        {
            return 1f + levelMultiplierStep * level;
        }

        public UpgradeCategoryDefinition Copy()
        {
            var copy = new UpgradeCategoryDefinition
            {
                id = id,
                maxLevel = maxLevel,
                levelMultiplierStep = levelMultiplierStep,
                prices = new List<int>()
            };
            if (prices != null) copy.prices.AddRange(prices);
            return copy;
        }
    }

    // Authored reward/economy table. Amounts bind to finalized validated
    // content facts — the award path (Task 7) never trusts client claims.
    public sealed class EconomyDefinition
    {
        // Completion reward for Campaign / Quick Race / LAN finishes;
        // Time Trial, practice and tutorial award nothing.
        public int completionCoins = 20;
        // Placement bonus by finishing place, index 0 = first place.
        public List<int> placementBonusCoins = new List<int>();
        public int challengeIntroductoryCoins = 10;
        public int challengeMasteryCoins = 20;
        public int rivalVictoryCoins = 30;
        public List<UpgradeCategoryDefinition> upgradeCategories =
            new List<UpgradeCategoryDefinition>();

        public bool Validate(out string error)
        {
            error = null;
            if (completionCoins < 0) { error = "completionCoins"; return false; }
            if (placementBonusCoins == null) { error = "placementBonusCoins"; return false; }
            if (placementBonusCoins != null)
                foreach (var bonus in placementBonusCoins)
                    if (bonus < 0) { error = "placementBonusCoins"; return false; }
            if (challengeIntroductoryCoins < 0) { error = "challengeIntroductoryCoins"; return false; }
            if (challengeMasteryCoins < 0) { error = "challengeMasteryCoins"; return false; }
            if (rivalVictoryCoins < 0) { error = "rivalVictoryCoins"; return false; }
            if (upgradeCategories == null || upgradeCategories.Count == 0)
            { error = "upgradeCategories"; return false; }
            if (upgradeCategories != null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var category in upgradeCategories)
                {
                    if (category == null || !category.Validate(out error))
                    { error = "upgradeCategories." + error; return false; }
                    if (!seen.Add(category.id))
                    { error = "upgradeCategories.duplicate"; return false; }
                }
            }
            return true;
        }

        public UpgradeCategoryDefinition Category(string id)
        {
            if (upgradeCategories != null)
                foreach (var category in upgradeCategories)
                    if (category != null && category.id == id) return category;
            return null;
        }

        public EconomyDefinition Copy()
        {
            var copy = new EconomyDefinition
            {
                completionCoins = completionCoins,
                challengeIntroductoryCoins = challengeIntroductoryCoins,
                challengeMasteryCoins = challengeMasteryCoins,
                rivalVictoryCoins = rivalVictoryCoins,
                placementBonusCoins = new List<int>(),
                upgradeCategories = new List<UpgradeCategoryDefinition>()
            };
            if (placementBonusCoins != null) copy.placementBonusCoins.AddRange(placementBonusCoins);
            if (upgradeCategories != null)
                foreach (var category in upgradeCategories)
                    copy.upgradeCategories.Add(category == null ? null : category.Copy());
            return copy;
        }
    }
}
