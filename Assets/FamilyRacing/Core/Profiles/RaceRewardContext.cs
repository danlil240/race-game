using System;
using System.Collections.Generic;
using FamilyRacing.Core.Progression;

namespace FamilyRacing.Core.Profiles
{
    // The durable binding between one race and the reward rules it was
    // started under. Task 4 binds and stores a context when a
    // progression race begins; Task 7 consumes it when the finalized
    // result lands and retires it inside the same atomic save as the
    // award receipt. `raceId`/`profileId`/`mode`/`rulesFingerprint` pin
    // which rules edition produced the race; `bountyAssignmentIds` and
    // `bountyDefinitions` are the challenge assignments active at race
    // start together with the definitions they pointed at — a delayed or
    // retried result always evaluates against these, never against
    // whatever assignments happen to be active when it arrives.
    [Serializable]
    public sealed class RaceRewardContext
    {
        public string raceId = "";
        public string profileId = "";
        public string mode = "";
        public string rulesFingerprint = "";
        public List<string> bountyAssignmentIds = new List<string>();
        public List<BountyDefinition> bountyDefinitions = new List<BountyDefinition>();

        public RaceRewardContext Copy()
        {
            var copy = new RaceRewardContext
            {
                raceId = raceId,
                profileId = profileId,
                mode = mode,
                rulesFingerprint = rulesFingerprint,
                bountyAssignmentIds = bountyAssignmentIds == null
                    ? null
                    : new List<string>(bountyAssignmentIds),
                bountyDefinitions = null
            };
            if (bountyDefinitions != null)
            {
                copy.bountyDefinitions = new List<BountyDefinition>(bountyDefinitions.Count);
                foreach (var definition in bountyDefinitions)
                    copy.bountyDefinitions.Add(
                        definition != null ? definition.Copy() : null);
            }
            return copy;
        }
    }
}
