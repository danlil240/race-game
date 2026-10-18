using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Progression
{
    // The fully validated gameplay content model: driving content
    // (vehicles/courses/events) plus the ordered campaign worlds,
    // challenge bounties, economy, gadgets and AI difficulty presets.
    // TryCreate is the only way to obtain a non-legacy catalog — it runs
    // every cross-reference so a consumer can never observe partially
    // bound content. The CompatibilityFingerprint binds every
    // gameplay-affecting value (see ContentFingerprint); the authored
    // identity string is the full-content version, independent of the
    // driving content version, profile schema and LAN protocol.
    public sealed partial class GameplayCatalog
    {
        // Full-content identity (M6 Task 1): the ordered multiworld
        // campaign contract replaces the M5 first-world epoch. Bumped
        // alongside the first changed gameplay contract. Kept
        // independent of DrivingContentCatalog.Version
        // ("physics-1"/"driving-0.1"), NetworkSettings.GameplayVersion,
        // and profile/state schemas.
        public const string ContentVersion = "full-content-1";
        public const string LegacyContentVersion = "legacy-1";

        public string Version { get; private set; }
        public bool IsLegacy { get; private set; }
        public DrivingContentCatalog Driving { get; private set; }
        // The campaign worlds in authored progression order. Never null
        // on a constructed catalog; the explicit legacy catalog carries
        // an empty list.
        public IReadOnlyList<CampaignDefinition> Campaigns
        { get; private set; }
        // M5 compatibility alias: the first world. Production consumers
        // should prefer Campaigns plus the lookups below; the alias is
        // retained so single-world fixtures and not-yet-migrated
        // consumers keep working.
        public CampaignDefinition Campaign
        {
            get
            {
                return Campaigns != null && Campaigns.Count > 0
                    ? Campaigns[0] : null;
            }
        }
        public IReadOnlyList<BountyDefinition> Bounties { get; private set; }
        public EconomyDefinition Economy { get; private set; }
        public GadgetDefinitionCatalog Gadgets { get; private set; }
        public IReadOnlyList<AiDifficultyDefinition> Difficulties { get; private set; }

        // Unique regular stars the adventure's regular events can award
        // across every world: completion + introductory + mastery per
        // event.
        public int PossibleRegularStars
        {
            get
            {
                var total = 0;
                if (Campaigns != null)
                    foreach (var campaign in Campaigns)
                        if (campaign != null
                            && campaign.regularEventIds != null)
                            total += campaign.regularEventIds.Count
                                * CampaignDefinition.ObjectiveBitsPerEvent;
                return total;
            }
        }

        // World lookup by authored worldId; null when no world claims it
        // (or the id is empty — an id is never optional content).
        public CampaignDefinition FindCampaign(string worldId)
        {
            if (string.IsNullOrEmpty(worldId) || Campaigns == null)
                return null;
            foreach (var campaign in Campaigns)
                if (campaign != null && campaign.worldId == worldId)
                    return campaign;
            return null;
        }

        // World lookup by owned event: regular and finale event ids are
        // owned by exactly one world (TryCreate enforces it), so the
        // owning world is unambiguous. Null when no world claims the id.
        public CampaignDefinition FindCampaignForEvent(string eventId)
        {
            if (string.IsNullOrEmpty(eventId) || Campaigns == null)
                return null;
            foreach (var campaign in Campaigns)
            {
                if (campaign == null) continue;
                if (campaign.regularEventIds != null
                    && campaign.regularEventIds.Contains(eventId))
                    return campaign;
                if (campaign.finales != null)
                    foreach (var finale in campaign.finales)
                        if (finale != null && finale.eventId == eventId)
                            return campaign;
            }
            return null;
        }

        // Same shape as DrivingContentCatalog.CompatibilityFingerprint,
        // computed on read so post-load edits stay bound.
        public string CompatibilityFingerprint
        {
            get { return ContentFingerprint.Compute(this); }
        }

        // Human-readable identity: the gameplay content version plus the
        // driving compatibility token (which folds the sim backend).
        public string CompatibilityVersion
        {
            get
            {
                return Driving == null
                    ? Version
                    : Version + "|" + Driving.CompatibilityVersion;
            }
        }

        // Retained M5 single-world entry point: identical to the
        // multiworld overload with a one-element campaign list.
        public static bool TryCreate(
            DrivingContentCatalog driving,
            CampaignDefinition campaign,
            IReadOnlyList<BountyDefinition> bounties,
            EconomyDefinition economy,
            GadgetDefinitionCatalog gadgets,
            IReadOnlyList<AiDifficultyDefinition> difficulties,
            out GameplayCatalog catalog,
            out string error)
        {
            if (driving == null)
            { catalog = null; error = "driving"; return false; }
            if (campaign == null)
            { catalog = null; error = "campaign"; return false; }
            return TryCreate(driving,
                new List<CampaignDefinition> { campaign },
                bounties, economy, gadgets, difficulties,
                out catalog, out error);
        }

        // M6 Task 1: ordered multiworld construction. Every world is
        // validated against the driving catalog, and each worldId,
        // courseId and eventId (regular or finale) must be owned by
        // exactly one world so progression and reward resolution can
        // never be ambiguous.
        public static bool TryCreate(
            DrivingContentCatalog driving,
            IReadOnlyList<CampaignDefinition> campaigns,
            IReadOnlyList<BountyDefinition> bounties,
            EconomyDefinition economy,
            GadgetDefinitionCatalog gadgets,
            IReadOnlyList<AiDifficultyDefinition> difficulties,
            out GameplayCatalog catalog,
            out string error)
        {
            catalog = null;
            error = null;
            if (driving == null) { error = "driving"; return false; }
            if (campaigns == null) { error = "campaigns"; return false; }
            if (campaigns.Count == 0) { error = "campaigns.empty"; return false; }
            if (bounties == null) { error = "bounties"; return false; }
            if (economy == null) { error = "economy"; return false; }
            if (gadgets == null) { error = "gadgets"; return false; }
            if (difficulties == null) { error = "difficulties"; return false; }

            // Structural pass: every world must be self-consistent.
            for (var i = 0; i < campaigns.Count; i++)
            {
                var campaign = campaigns[i];
                if (campaign == null)
                { error = "campaigns[" + i + "]"; return false; }
                if (!campaign.Validate(out error))
                { error = "campaigns[" + i + "]." + error; return false; }
            }
            if (!economy.Validate(out error))
            { error = "economy." + error; return false; }
            if (!gadgets.Validate(out error))
            { error = "gadgets." + error; return false; }

            var bountyIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bounty in bounties)
            {
                if (bounty == null || !bounty.Validate(out error))
                { error = "bounties." + error; return false; }
                if (!bountyIds.Add(bounty.id))
                { error = "bounties.duplicate"; return false; }
            }
            var difficultyIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var difficulty in difficulties)
            {
                if (difficulty == null || !difficulty.Validate(out error))
                { error = "difficulties." + error; return false; }
                if (!difficultyIds.Add(difficulty.id))
                { error = "difficulties.duplicate"; return false; }
            }

            // Ownership + cross-reference pass: each id is claimed by
            // exactly one world before its references are checked.
            var worldIds = new HashSet<string>(StringComparer.Ordinal);
            var courseOwners = new HashSet<string>(StringComparer.Ordinal);
            var eventOwners = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < campaigns.Count; i++)
            {
                var campaign = campaigns[i];
                if (!worldIds.Add(campaign.worldId))
                {
                    error = "campaigns[" + i + "].worldId.duplicate "
                        + campaign.worldId;
                    return false;
                }
                foreach (var courseId in campaign.courseIds)
                    if (!courseOwners.Add(courseId))
                    {
                        error = "campaigns[" + i + "].course.duplicate "
                            + courseId;
                        return false;
                    }
                foreach (var eventId in campaign.regularEventIds)
                    if (!eventOwners.Add(eventId))
                    {
                        error = "campaigns[" + i + "].event.duplicate "
                            + eventId;
                        return false;
                    }
                foreach (var finale in campaign.finales)
                {
                    // An event cannot be both a regular event and a
                    // finale of the same world — distinct from another
                    // world claiming the id, so it names the overlap.
                    if (campaign.regularEventIds.Contains(finale.eventId))
                    {
                        error = "campaigns[" + i + "].event.overlap "
                            + finale.eventId;
                        return false;
                    }
                    if (!eventOwners.Add(finale.eventId))
                    {
                        error = "campaigns[" + i + "].event.duplicate "
                            + finale.eventId;
                        return false;
                    }
                }
                if (!ValidateWorldContent(driving, campaign, gadgets,
                        out error))
                {
                    error = "campaigns[" + i + "]." + error;
                    return false;
                }
            }

            catalog = new GameplayCatalog
            {
                Version = ContentVersion,
                IsLegacy = false,
                Driving = driving,
                Campaigns = new List<CampaignDefinition>(campaigns),
                Bounties = new List<BountyDefinition>(bounties),
                Economy = economy,
                Gadgets = gadgets,
                Difficulties = new List<AiDifficultyDefinition>(difficulties)
            };
            return true;
        }

        // Per-world cross references into the driving catalog — the M5
        // first-world rules applied to every world: courses exist,
        // regular events are typed non-finale events on owned courses,
        // the gate is reachable, each finale is a real finale whose
        // course the world claims and whose reward vehicle exists and
        // matches the finale class, and every class with regular events
        // ships its finale.
        private static bool ValidateWorldContent(
            DrivingContentCatalog driving,
            CampaignDefinition campaign,
            GadgetDefinitionCatalog gadgets,
            out string error)
        {
            error = null;
            foreach (var courseId in campaign.courseIds)
                if (!driving.Courses.ContainsKey(courseId))
                { error = "campaign.course " + courseId; return false; }

            var gadgetEventCount = 0;
            var regularClasses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var eventId in campaign.regularEventIds)
            {
                EventDefinition evt;
                if (!driving.Events.TryGetValue(eventId, out evt))
                { error = "campaign.event " + eventId; return false; }
                if (evt.isFinale || evt.eventType == GameplayEventType.Finale)
                { error = "campaign.event " + eventId + " finale"; return false; }
                // Re-validate: post-load edits could have broken a rule
                // the driving catalog checked at build time.
                if (!evt.Validate(out error))
                { error = "campaign.event " + eventId + " " + error; return false; }
                // A regular event must be typed: an untyped (legacy) event
                // carries no objectives, so its three star bits could never
                // be earned.
                if (evt.eventType == "")
                { error = "campaign.event " + eventId + " untyped"; return false; }
                regularClasses.Add(evt.vehicleClass);
                if (!campaign.courseIds.Contains(evt.courseId))
                { error = "campaign.event " + eventId + " course"; return false; }
                if (evt.eventType == GameplayEventType.Gadget) gadgetEventCount++;
            }

            // The finale gate requires completing every authored regular
            // event; a star gate must stay reachable.
            if (campaign.requiredEventCompletions != campaign.regularEventIds.Count)
            { error = "campaign.requiredEventCompletions"; return false; }
            var possibleStars = campaign.regularEventIds.Count
                * CampaignDefinition.ObjectiveBitsPerEvent;
            if (campaign.requiredRegionalStars > possibleStars)
            { error = "campaign.requiredRegionalStars"; return false; }

            var finaleClasses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var finale in campaign.finales)
            {
                EventDefinition evt;
                if (!driving.Events.TryGetValue(finale.eventId, out evt))
                { error = "campaign.finale " + finale.eventId; return false; }
                if (!evt.isFinale && evt.eventType != GameplayEventType.Finale)
                { error = "campaign.finale " + finale.eventId + " notFinale"; return false; }
                if (!evt.Validate(out error))
                { error = "campaign.finale " + finale.eventId + " " + error; return false; }
                if (evt.vehicleClass != finale.classId)
                { error = "campaign.finale " + finale.eventId + " class"; return false; }
                // The finale event must be hosted on a course the world
                // owns — a finale on foreign soil would leak progress
                // across the world boundary.
                if (!campaign.courseIds.Contains(evt.courseId))
                { error = "campaign.finale " + finale.eventId + " course"; return false; }
                if (!driving.Vehicles.ContainsKey(finale.rewardVehicleId))
                { error = "campaign.finale " + finale.eventId + " rewardVehicle"; return false; }
                // The world-completion reward is a member of the finale's
                // class: a car finale can never grant a motorcycle.
                if (VehicleTuning.ClassIdOf(finale.rewardVehicleId)
                        != finale.classId)
                { error = "campaign.finale " + finale.eventId + " rewardClass"; return false; }
                finaleClasses.Add(finale.classId);
            }
            // One rival finale per class: every class that has regular
            // events must ship its finale.
            foreach (var classId in regularClasses)
                if (!finaleClasses.Contains(classId))
                { error = "campaign.finale missing class " + classId; return false; }
            if (gadgetEventCount > 0
                && (gadgets.gadgets == null || gadgets.gadgets.Count == 0))
            { error = "gadgets.empty"; return false; }
            return true;
        }

        // Explicit legacy path: a pre-M5 driving catalog (no gameplay
        // fields) still loads — with empty gameplay sections and its own
        // identity so evidence streams can never confuse it with the
        // full-content catalog.
        public static GameplayCatalog CreateLegacy(DrivingContentCatalog driving)
        {
            if (driving == null)
                throw new ArgumentNullException("driving");
            return new GameplayCatalog
            {
                Version = LegacyContentVersion,
                IsLegacy = true,
                Driving = driving,
                Campaigns = new List<CampaignDefinition>(),
                Bounties = new List<BountyDefinition>(),
                Economy = null,
                Gadgets = null,
                Difficulties = new List<AiDifficultyDefinition>()
            };
        }
    }
}
