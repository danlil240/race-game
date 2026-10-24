using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Racing
{
    // Typed authoritative per-racer race metrics (M5 Task 4). Counted
    // only at completed simulation boundaries: human-attributed skill
    // facts (drift, boost, clean landings, clean streak) credit whoever
    // owned the input at the action tick — a later AI takeover never
    // erases or inherits earlier human facts. Shared counters
    // (impacts, recoveries, gadget/shortcut uses) record for every
    // racer regardless of attribution.
    //
    // `position`, `outcome`, `finishTicks` and `elapsedTicks` are
    // filled from the authoritative standings/result facts (they are
    // authoritative placement, not counters); `rivalDefeated` is the
    // rival-finale fact the result factory derives.
    [Serializable]
    public sealed class RaceMetrics
    {
        public string participantId = "";
        public string attribution = "";
        // Authoritative race-clock facts at finalize (0 while racing).
        public long elapsedTicks;
        public long finishTicks;
        public int position;
        public string outcome = "";        // "" | "finished" | "dnf"
        // Human-attributed counters (0 under AI attribution).
        public int driftTicks;
        public int boostActivations;
        public int cleanLandings;
        public int longestCleanStreakTicks;
        // Human-attributed shortcut facts (M5 Task 6): the shared
        // acceptedShortcuts/distinctShortcuts counters below record
        // every traversal regardless of who drove; these variants count
        // only human-attributed traversals so bounty/objective credit
        // can never come from an AI-driven segment.
        public int humanAcceptedShortcuts;
        public int humanDistinctShortcuts;
        // Shared counters for every racer.
        public int acceptedShortcuts;
        public int distinctShortcuts;
        public int gadgetUses;
        public int distinctGadgetTypes;
        public int recoveries;
        public int impacts;
        // Finale fact: the local human finished ahead of the rival
        // (or the rival DNF'd). False outside a rival race.
        public bool rivalDefeated;

        public RaceMetrics Copy()
        {
            return (RaceMetrics)MemberwiseClone();
        }
    }
}
