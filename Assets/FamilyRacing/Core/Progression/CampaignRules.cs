using System;
using System.Collections.Generic;
using FamilyRacing.Core.Profiles;

namespace FamilyRacing.Core.Progression
{
    // What one campaign world currently grants a profile (M5 Task 6): a
    // pure read of the profile's durable campaign facts — per-event
    // objective masks, finale rows and world completion. Nothing here
    // mutates the profile; Task 7's award transaction diffs access
    // before/after to persist newly earned unlocks.
    public sealed class CampaignAccess
    {
        // Event ids the profile may enter: every regular event is
        // available from the start; finale ids append once the gate
        // opens. Authored order is preserved.
        public List<string> availableEventIds = new List<string>();
        public List<string> availableFinaleIds = new List<string>();
        // Finale facts by event id — `completed` advances the world,
        // `victorious` is the one-time rival-defeat record.
        public List<string> completedFinaleIds = new List<string>();
        public List<string> victoriousFinaleIds = new List<string>();
        // Vehicles the world awards on completion (one per finale).
        public List<string> worldRewardVehicleIds = new List<string>();
        // Distinct regular events completed (the completion gate is a
        // count, not a row presence).
        public int completedEvents;
        // Unique regional stars: the popcount union of the three
        // objective bits across the world's regular events.
        public int regionalStars;
        public int requiredEventCompletions;
        public int requiredRegionalStars;
        public bool finalesAvailable;
        public bool worldComplete;
    }

    public static class CampaignRules
    {
        public static CampaignAccess Evaluate(CampaignDefinition definition,
            ProfileSave profile)
        {
            var access = new CampaignAccess();
            if (definition == null) return access;
            access.requiredEventCompletions = definition.requiredEventCompletions;
            access.requiredRegionalStars = definition.requiredRegionalStars;

            // Regular events: always enterable. Completion and stars
            // derive from the stored row — the completion bit of the
            // mask and the row's completed flag are the same fact seen
            // twice; unioning either one keeps the count honest.
            var stars = 0;
            var completed = 0;
            if (definition.regularEventIds != null)
            {
                foreach (var eventId in definition.regularEventIds)
                {
                    if (eventId == null) continue;
                    access.availableEventIds.Add(eventId);
                    var row = FindEvent(profile, eventId);
                    if (row == null) continue;
                    var mask = row.objectiveMask
                        & ObjectiveEvaluator.FullMask;
                    if (row.completed) mask |= ObjectiveEvaluator.CompletionBit;
                    // Star bits only exist on completed events — bits
                    // without the completion bit are corrupt data and
                    // count neither a completion nor a star.
                    if ((mask & ObjectiveEvaluator.CompletionBit) == 0)
                        continue;
                    stars += PopCount(mask);
                    completed++;
                }
            }
            access.completedEvents = completed;
            access.regionalStars = stars;
            access.finalesAvailable =
                completed >= definition.requiredEventCompletions
                && stars >= definition.requiredRegionalStars;

            var allFinalesComplete = definition.finales != null
                && definition.finales.Count > 0;
            if (definition.finales != null)
                foreach (var finale in definition.finales)
                {
                    if (finale == null) continue;
                    if (!string.IsNullOrEmpty(finale.rewardVehicleId))
                        access.worldRewardVehicleIds.Add(
                            finale.rewardVehicleId);
                    var row = FindFinale(profile, finale.eventId);
                    if (row == null || !row.completed)
                    {
                        allFinalesComplete = false;
                        continue;
                    }
                    access.completedFinaleIds.Add(finale.eventId);
                    if (row.victorious)
                        access.victoriousFinaleIds.Add(finale.eventId);
                }
            if (access.finalesAvailable && definition.finales != null)
                foreach (var finale in definition.finales)
                    if (finale != null && finale.eventId != null)
                    {
                        access.availableFinaleIds.Add(finale.eventId);
                        access.availableEventIds.Add(finale.eventId);
                    }

            // World completion is a derived fact: every finale completed
            // (victory optional) — or the durably marked world row, kept
            // as an OR so a recorded completion can never be revoked by
            // a partial view of finale rows.
            access.worldComplete = allFinalesComplete
                || WorldMarkedComplete(definition, profile);
            return access;
        }

        private static int PopCount(int mask)
        {
            var count = 0;
            while (mask != 0) { count += mask & 1; mask >>= 1; }
            return count;
        }

        private static EventProgress FindEvent(ProfileSave profile,
            string eventId)
        {
            if (profile == null || profile.eventProgress == null)
                return null;
            foreach (var row in profile.eventProgress)
                if (row != null && row.eventId == eventId) return row;
            return null;
        }

        private static FinaleProgress FindFinale(ProfileSave profile,
            string eventId)
        {
            if (profile == null || profile.finaleProgress == null)
                return null;
            foreach (var row in profile.finaleProgress)
                if (row != null && row.eventId == eventId) return row;
            return null;
        }

        private static bool WorldMarkedComplete(CampaignDefinition definition,
            ProfileSave profile)
        {
            if (profile == null || profile.worldProgress == null)
                return false;
            foreach (var row in profile.worldProgress)
                if (row != null && row.worldId == definition.worldId
                    && row.completed) return true;
            return false;
        }
    }
}
