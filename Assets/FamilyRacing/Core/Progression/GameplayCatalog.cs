using System;
using System.Collections.Generic;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Progression
{
    // The fully validated first-world gameplay content model: driving
    // content (vehicles/courses/events) plus campaign structure,
    // challenge bounties, economy, gadgets and AI difficulty presets.
    // TryCreate is the only way to obtain a non-legacy catalog — it runs
    // every cross-reference so a consumer can never observe partially
    // bound content. The CompatibilityFingerprint binds every
    // gameplay-affecting value (see ContentFingerprint); the authored
    // identity string is the gameplay-loop content version, independent
    // of the driving content version, profile schema and LAN protocol.
    public sealed class GameplayCatalog
    {
        // First-world content identity (M5): bumped alongside the first
        // changed gameplay contract. Kept independent of
        // DrivingContentCatalog.Version ("physics-1"/"driving-0.1"),
        // NetworkSettings.GameplayVersion, and profile/state schemas.
        public const string ContentVersion = "gameplay-loop-1";
        public const string LegacyContentVersion = "legacy-1";

        public string Version { get; private set; }
        public bool IsLegacy { get; private set; }
        public DrivingContentCatalog Driving { get; private set; }
        public CampaignDefinition Campaign { get; private set; }
        public IReadOnlyList<BountyDefinition> Bounties { get; private set; }
        public EconomyDefinition Economy { get; private set; }
        public GadgetDefinitionCatalog Gadgets { get; private set; }
        public IReadOnlyList<AiDifficultyDefinition> Difficulties { get; private set; }

        // Unique regional stars the world's regular events can award:
        // completion + introductory + mastery per event.
        public int PossibleRegularStars
        {
            get
            {
                return Campaign == null || Campaign.regularEventIds == null
                    ? 0
                    : Campaign.regularEventIds.Count
                        * CampaignDefinition.ObjectiveBitsPerEvent;
            }
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
            catalog = null;
            error = null;
            if (driving == null) { error = "driving"; return false; }
            if (campaign == null) { error = "campaign"; return false; }
            if (bounties == null) { error = "bounties"; return false; }
            if (economy == null) { error = "economy"; return false; }
            if (gadgets == null) { error = "gadgets"; return false; }
            if (difficulties == null) { error = "difficulties"; return false; }

            if (!campaign.Validate(out error))
            { error = "campaign." + error; return false; }
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

            // --- cross references into the driving catalog -------------
            foreach (var courseId in campaign.courseIds)
                if (!driving.Courses.ContainsKey(courseId))
                { error = "campaign.course " + courseId; return false; }

            var gadgetEventCount = 0;
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
                if (!driving.Vehicles.ContainsKey(finale.rewardVehicleId))
                { error = "campaign.finale " + finale.eventId + " rewardVehicle"; return false; }
                finaleClasses.Add(finale.classId);
            }
            if (gadgetEventCount > 0
                && (gadgets.gadgets == null || gadgets.gadgets.Count == 0))
            { error = "gadgets.empty"; return false; }

            catalog = new GameplayCatalog
            {
                Version = ContentVersion,
                IsLegacy = false,
                Driving = driving,
                Campaign = campaign,
                Bounties = new List<BountyDefinition>(bounties),
                Economy = economy,
                Gadgets = gadgets,
                Difficulties = new List<AiDifficultyDefinition>(difficulties)
            };
            return true;
        }

        // Explicit legacy path: a pre-M5 driving catalog (no gameplay
        // fields) still loads — with empty gameplay sections and its own
        // identity so evidence streams can never confuse it with the
        // gameplay-loop content.
        public static GameplayCatalog CreateLegacy(DrivingContentCatalog driving)
        {
            if (driving == null)
                throw new ArgumentNullException("driving");
            return new GameplayCatalog
            {
                Version = LegacyContentVersion,
                IsLegacy = true,
                Driving = driving,
                Campaign = null,
                Bounties = new List<BountyDefinition>(),
                Economy = null,
                Gadgets = null,
                Difficulties = new List<AiDifficultyDefinition>()
            };
        }
    }
}
