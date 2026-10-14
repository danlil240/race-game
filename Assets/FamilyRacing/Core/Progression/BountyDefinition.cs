using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Progression
{
    // Closed set of repeatable-challenge metrics. Progress accumulates
    // from finalized human-attributed race facts only.
    public static class BountyMetric
    {
        public const string DriftSeconds = "driftSeconds";
        public const string HumanBoostActivations = "humanBoostActivations";
        public const string CleanLandings = "cleanLandings";
        public const string CleanStreakSeconds = "cleanStreakSeconds";
        public const string AcceptedShortcuts = "acceptedShortcuts";
        public const string HumanFinishes = "humanFinishes";

        public static bool IsValid(string metric)
        {
            switch (metric)
            {
                case DriftSeconds:
                case HumanBoostActivations:
                case CleanLandings:
                case CleanStreakSeconds:
                case AcceptedShortcuts:
                case HumanFinishes:
                    return true;
                default:
                    return false;
            }
        }
    }

    // How race facts fold into progress: most challenges add their
    // per-race amount; the clean streak keeps the best qualifying streak.
    public static class BountyAggregation
    {
        public const string Sum = "sum";
        public const string Max = "max";

        public static bool IsValid(string aggregation)
        {
            return aggregation == Sum || aggregation == Max;
        }
    }

    // One repeatable challenge in the fixed offline catalog. Progress
    // rows in the profile reference `id` via their assignment; rotation
    // order is the authored list order (Task 6/7 own the cursor).
    public sealed class BountyDefinition
    {
        public string id = "";
        public string family = "";
        public string metric = "";
        public long threshold;
        public string unit = "count"; // "count" | "seconds" | "ticks"
        public int rewardCoins;
        public string aggregation = BountyAggregation.Sum;
        // Repeatable challenges rotate; named-rival victory bounties are
        // one-time facts outside the repeatable cursor.
        public bool repeatable = true;
        public string rivalId = "";

        public bool Validate(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(id)) { error = "id"; return false; }
            if (string.IsNullOrEmpty(family)) { error = "family"; return false; }
            if (!BountyMetric.IsValid(metric)) { error = "metric"; return false; }
            if (threshold <= 0) { error = "threshold"; return false; }
            if (unit != "count" && unit != "seconds" && unit != "ticks")
            { error = "unit"; return false; }
            if (rewardCoins < 0) { error = "rewardCoins"; return false; }
            if (!BountyAggregation.IsValid(aggregation)) { error = "aggregation"; return false; }
            return true;
        }

        public BountyDefinition Copy()
        {
            return (BountyDefinition)MemberwiseClone();
        }
    }
}
