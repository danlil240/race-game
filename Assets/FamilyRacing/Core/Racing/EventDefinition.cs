using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Progression;

namespace FamilyRacing.Core.Racing
{
    // Closed set of gameplay event types for the campaign slice. The
    // six regular types appear once per course/class pairing; `finale`
    // marks the rival finale events. Empty string on EventDefinition
    // keeps pre-M5 (practice/LAN) events valid through legacy defaults.
    // Named GameplayEventType (not EventType) to avoid colliding with
    // UnityEngine.EventType in runtime/UI code.
    public static class GameplayEventType
    {
        public const string Circuit = "circuit";
        public const string Boost = "boost";
        public const string Stunt = "stunt";
        public const string Time = "time";
        public const string Gadget = "gadget";
        public const string CleanDriving = "clean-driving";
        public const string Finale = "finale";

        public static bool IsValid(string eventType)
        {
            switch (eventType)
            {
                case Circuit:
                case Boost:
                case Stunt:
                case Time:
                case Gadget:
                case CleanDriving:
                case Finale:
                    return true;
                default:
                    return false;
            }
        }
    }

    // One playable driving event: which course, how many laps, the
    // countdown length (simulation ticks that do not charge race elapsed
    // time), which vehicles may enter, and the data-driven recovery
    // thresholds.
    public sealed class EventDefinition
    {
        public string id = "";
        public string courseId = "";
        public int laps = 1;
        public int countdownTicks = 90;
        public List<string> vehicleIds = new List<string>();
        public RecoverySettings recovery = new RecoverySettings();
        // Authored completion reward (coins) applied to racers that finish
        // the prescribed laps in a finalized race. Zero outside events
        // that carry a reward — the reward rule itself lives in the race
        // coordinator, not in presentation.
        public int completionCoins;
        // --- M5 gameplay slice (optional; empty eventType = legacy event) ---
        // One of EventType; when set, `vehicleClass` must be the class
        // shared by every allowed vehicle id (ids carry their class as
        // the segment before the first '-' — VehicleTuning.ClassIdOf).
        public string eventType = "";
        public string vehicleClass = "";
        // Authored par time for time challenges (unpaused 30 Hz ticks);
        // must be positive when eventType is "time". Unused elsewhere.
        public long parTicks;
        // Introductory + mastery star objectives. Regular typed events
        // carry exactly this pair; completion is always the first bit.
        public List<ObjectiveDefinition> objectives;
        public bool isFinale;

        public bool Validate(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(id)) { error = "id"; return false; }
            if (string.IsNullOrEmpty(courseId)) { error = "courseId"; return false; }
            if (laps < 1) { error = "laps"; return false; }
            if (countdownTicks < 0) { error = "countdownTicks"; return false; }
            if (vehicleIds == null || vehicleIds.Count == 0) { error = "vehicleIds"; return false; }
            foreach (var vehicleId in vehicleIds)
                if (string.IsNullOrEmpty(vehicleId)) { error = "vehicleIds"; return false; }
            if (recovery == null) { error = "recovery"; return false; }
            if (!recovery.Validate(out error)) return false;
            if (completionCoins < 0) { error = "completionCoins"; return false; }
            if (parTicks < 0) { error = "parTicks"; return false; }
            if (vehicleClass != "")
                foreach (var vehicleId in vehicleIds)
                    if (VehicleTuning.ClassIdOf(vehicleId) != vehicleClass)
                    { error = "vehicleClass"; return false; }
            if (eventType != "")
            {
                if (!GameplayEventType.IsValid(eventType)) { error = "eventType"; return false; }
                if (string.IsNullOrEmpty(vehicleClass)) { error = "vehicleClass"; return false; }
                if (eventType == GameplayEventType.Time && parTicks <= 0)
                { error = "parTicks"; return false; }
                if (eventType == GameplayEventType.Finale && !isFinale)
                { error = "isFinale"; return false; }
                if (eventType != GameplayEventType.Finale && isFinale)
                { error = "isFinale"; return false; }
                if (eventType != GameplayEventType.Finale)
                    if (!ValidateObjectives(out error)) return false;
            }
            else if (isFinale || (objectives != null && objectives.Count > 0))
            {
                // Gameplay facts without a type are an authoring error.
                error = "eventType";
                return false;
            }
            return true;
        }

        // Regular typed events carry exactly the introductory+mastery
        // objective pair; a single-metric pair must never make the
        // introductory leg stricter than mastery — that template could
        // only award both stars together or neither.
        private bool ValidateObjectives(out string error)
        {
            error = null;
            if (objectives == null || objectives.Count != 2)
            { error = "objectives"; return false; }
            if (objectives[0] == null || objectives[1] == null)
            { error = "objectives"; return false; }
            if (!objectives[0].Validate(out error)
                || !objectives[1].Validate(out error)) return false;
            if (objectives[0].kind != ObjectiveKind.Introductory
                || objectives[1].kind != ObjectiveKind.Mastery)
            { error = "objectives.kind"; return false; }
            var intro = objectives[0].alternatives;
            var mastery = objectives[1].alternatives;
            if (intro.Count == 1 && mastery.Count == 1
                && (intro[0].alsoRequires == null || intro[0].alsoRequires.Count == 0)
                && (mastery[0].alsoRequires == null || mastery[0].alsoRequires.Count == 0)
                && intro[0].metric == mastery[0].metric
                && intro[0].comparator == mastery[0].comparator)
            {
                var stricter = intro[0].comparator == ObjectiveComparator.AtLeast
                    ? intro[0].threshold > mastery[0].threshold
                    : intro[0].threshold < mastery[0].threshold;
                if (stricter) { error = "objectives.threshold"; return false; }
            }
            return true;
        }

        public bool AllowsVehicle(string vehicleId)
        {
            return vehicleIds != null && vehicleIds.Contains(vehicleId);
        }

        public EventDefinition Copy()
        {
            var copy = new EventDefinition
            {
                id = id,
                courseId = courseId,
                laps = laps,
                countdownTicks = countdownTicks,
                vehicleIds = vehicleIds == null ? null : new List<string>(vehicleIds),
                recovery = recovery == null ? null : recovery.Copy(),
                completionCoins = completionCoins,
                eventType = eventType,
                vehicleClass = vehicleClass,
                parTicks = parTicks,
                isFinale = isFinale
            };
            if (objectives != null)
            {
                copy.objectives = new List<ObjectiveDefinition>();
                foreach (var objective in objectives)
                    copy.objectives.Add(objective == null ? null : objective.Copy());
            }
            return copy;
        }
    }
}
