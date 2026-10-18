using System;
using System.Collections.Generic;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Progression
{
    public sealed partial class GameplayCatalog
    {
        public bool QuickRaceOnly { get; private set; }
        public bool GrantsRewards => !IsLegacy && !QuickRaceOnly && Economy != null;

        public bool SupportsMode(RaceMode mode)
        {
            return !IsLegacy && Driving != null && (!QuickRaceOnly
                || mode == RaceMode.QuickRace || mode == RaceMode.Lan);
        }

        public static bool TryCreateQuickRace(DrivingContentCatalog driving,
            GadgetDefinitionCatalog gadgets,
            IReadOnlyList<AiDifficultyDefinition> difficulties,
            out GameplayCatalog catalog, out string error)
        {
            catalog = null;
            error = null;
            if (driving == null) { error = "driving"; return false; }
            try
            {
                driving = DrivingContentCatalog.Create(driving.Version, driving.Vehicles.Values,
                    driving.Courses.Values, driving.Events.Values, driving.SimulationBackend);
            }
            catch (ArgumentException invalid)
            { error = "driving." + invalid.Message; return false; }
            if (gadgets == null || !gadgets.Validate(out error))
            { error = "gadgets." + (error ?? "missing"); return false; }
            if (difficulties == null || difficulties.Count == 0)
            { error = "difficulties.empty"; return false; }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var difficulty in difficulties)
            {
                if (difficulty == null || !difficulty.Validate(out error))
                { error = "difficulties." + (error ?? "missing"); return false; }
                if (!ids.Add(difficulty.id))
                { error = "difficulties.duplicate"; return false; }
            }
            var circuit = false;
            foreach (var evt in driving.Events.Values)
                if (evt.eventType == GameplayEventType.Circuit && !evt.isFinale)
                {
                    if (!evt.Validate(out error)) return false;
                    circuit = true;
                }
            if (!circuit) { error = "quickRace.noCircuit"; return false; }
            catalog = new GameplayCatalog
            {
                Version = "playful-chaos-1", QuickRaceOnly = true,
                Driving = driving, Gadgets = gadgets,
                Campaigns = new List<CampaignDefinition>(),
                Bounties = new List<BountyDefinition>(),
                Difficulties = new List<AiDifficultyDefinition>(difficulties)
            };
            return true;
        }
    }
}
