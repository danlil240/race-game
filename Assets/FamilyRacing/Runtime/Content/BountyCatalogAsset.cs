using System;
using System.Collections.Generic;
using FamilyRacing.Core.Progression;
using UnityEngine;

namespace FamilyRacing
{
    // Authored repeatable-challenge catalog converted into validated Core
    // BountyDefinition rows. The catalog is fixed and offline; the three
    // active assignments rotate from this list (rotation cursor lives in
    // the profile, not in content).
    [CreateAssetMenu(menuName = "FamilyRacing/Bounty Catalog",
        fileName = "bounty-catalog")]
    public sealed class BountyCatalogAsset : ScriptableObject
    {
        [Serializable]
        public sealed class BountyRow
        {
            public string bountyId = "";
            public string family = "";
            // One of BountyMetric; unknown kinds reject the whole
            // conversion — a bad row never partially loads.
            public string metric = "";
            public long threshold = 1;
            public string unit = "count"; // "count" | "seconds" | "ticks"
            public int rewardCoins;
            public string aggregation = "sum"; // "sum" | "max"
            public bool repeatable = true;
            // Named-rival victory bounties bind a rival; they are
            // one-time facts outside the repeatable rotation cursor.
            public string rivalId = "";
        }

        public BountyRow[] bounties = new BountyRow[0];

        public List<BountyDefinition> ToDefinitions(out string error)
        {
            var list = new List<BountyDefinition>();
            if (bounties != null)
                foreach (var row in bounties)
                {
                    if (row == null)
                    {
                        error = name + ": null bounty row";
                        return null;
                    }
                    var bounty = new BountyDefinition
                    {
                        id = row.bountyId,
                        family = row.family,
                        metric = row.metric,
                        threshold = row.threshold,
                        unit = row.unit,
                        rewardCoins = row.rewardCoins,
                        aggregation = row.aggregation,
                        repeatable = row.repeatable,
                        rivalId = row.rivalId
                    };
                    if (!bounty.Validate(out error))
                    {
                        error = name + ": invalid bounty " + row.bountyId
                            + " field " + error;
                        return null;
                    }
                    list.Add(bounty);
                }
            error = null;
            return list;
        }
    }
}
