using System.Collections.Generic;
using FamilyRacing.Core.Profiles;

namespace FamilyRacing.Core.Progression
{
    // What one campaign world currently means to a profile inside the
    // ordered adventure (M6 Task 2): availability plus the machine-safe
    // lock reason the world selector can stream, wrapping the per-world
    // regional `CampaignAccess` math. A locked world still reports its
    // honest regional numbers — progress parked in a closed world is
    // real, it simply cannot be entered or spent.
    public sealed class WorldAccess
    {
        public string worldId = "";
        public bool available;
        // Stable machine code for a closed world ("" while open): the
        // immediately previous world's finales are not both completed.
        // Entry verdicts emit the same code.
        public string lockedReason = "";
        // The regional read for exactly this world: its own
        // completions, its own stars, its own finales.
        public CampaignAccess campaign;
    }

    // The whole adventure view: every authored world in progression
    // order plus the adventure-complete fact. World one is open on a
    // fresh profile; every later world opens the moment the
    // immediately previous world is complete — both finales finished,
    // wins never required. Completion is a durable fact, so completed
    // worlds can never relock and `complete` never revokes replay.
    public sealed class AdventureAccess
    {
        public List<WorldAccess> worlds = new List<WorldAccess>();
        public bool complete;
    }

    // Pure ordered-world progression rules (M6 Task 2). The chain is
    // derived from the catalog's authored campaign order: no world id
    // constants, no scanning, no mutation of the profile. Regional
    // event/star math stays inside `CampaignRules.Evaluate` — this
    // type only sequences the worlds around it.
    public static class AdventureRules
    {
        // Stable machine code a closed world carries — the same code
        // the mode verdicts emit for events/courses behind it.
        public const string LockedWorldReason = "worldLocked";

        public static AdventureAccess Evaluate(GameplayCatalog catalog,
            ProfileSave profile)
        {
            var access = new AdventureAccess();
            if (catalog == null || catalog.Campaigns == null)
                return access;
            var previousComplete = false;
            var allComplete = catalog.Campaigns.Count > 0;
            foreach (var definition in catalog.Campaigns)
            {
                if (definition == null) continue;
                var world = new WorldAccess
                {
                    worldId = definition.worldId ?? "",
                    campaign = CampaignRules.Evaluate(definition, profile)
                };
                // World order decides availability: the first world is
                // always open; every successor waits on the IMMEDIATELY
                // previous world completing.
                var index = access.worlds.Count;
                world.available = index == 0 || previousComplete;
                world.lockedReason = world.available
                    ? "" : LockedWorldReason;
                previousComplete = world.campaign.worldComplete;
                if (!world.campaign.worldComplete) allComplete = false;
                access.worlds.Add(world);
            }
            access.complete = allComplete;
            return access;
        }

        // The world's row inside an evaluated adventure, or null when
        // the id names no authored world.
        public static WorldAccess FindWorld(AdventureAccess access,
            string worldId)
        {
            if (access == null || access.worlds == null
                || worldId == null)
                return null;
            foreach (var world in access.worlds)
                if (world != null && world.worldId == worldId)
                    return world;
            return null;
        }

        // The unlock-gated answer a course picker needs: a course is
        // enterable exactly when the world that owns it is open. A
        // course no authored world claims is not adventure content —
        // no world can open it, so the honest answer is closed.
        public static bool CanAccessCourse(GameplayCatalog catalog,
            ProfileSave profile, string courseId)
        {
            if (catalog == null || courseId == null
                || catalog.Campaigns == null)
                return false;
            CampaignDefinition owner = null;
            foreach (var campaign in catalog.Campaigns)
            {
                if (campaign == null || campaign.courseIds == null)
                    continue;
                if (campaign.courseIds.Contains(courseId))
                {
                    owner = campaign;
                    break;
                }
            }
            if (owner == null) return false;
            var world = FindWorld(Evaluate(catalog, profile),
                owner.worldId);
            return world != null && world.available;
        }
    }
}
