using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Progression
{
    // Typed objective metrics consumed by the Task-6 evaluator from
    // authoritative race facts. The set is closed: asset conversion and
    // catalog validation reject unknown kinds rather than guessing.
    public static class ObjectiveMetric
    {
        public const string CleanLandings = "cleanLandings";
        public const string FinishPlace = "finishPlace";
        public const string DistinctShortcuts = "distinctShortcuts";
        public const string HumanBoostActivations = "humanBoostActivations";
        public const string FinishWithinTicks = "finishWithinTicks";
        public const string GadgetsCollectedAndUsed = "gadgetsCollectedAndUsed";
        public const string DistinctGadgetTypesUsed = "distinctGadgetTypesUsed";
        public const string CleanStreakSeconds = "cleanStreakSeconds";
        public const string Recoveries = "recoveries";

        public static bool IsValid(string metric)
        {
            switch (metric)
            {
                case CleanLandings:
                case FinishPlace:
                case DistinctShortcuts:
                case HumanBoostActivations:
                case FinishWithinTicks:
                case GadgetsCollectedAndUsed:
                case DistinctGadgetTypesUsed:
                case CleanStreakSeconds:
                case Recoveries:
                    return true;
                default:
                    return false;
            }
        }
    }

    public static class ObjectiveComparator
    {
        public const string AtLeast = "atLeast";
        public const string AtMost = "atMost";

        public static bool IsValid(string comparator)
        {
            return comparator == AtLeast || comparator == AtMost;
        }
    }

    public static class ObjectiveKind
    {
        public const string Introductory = "introductory";
        public const string Mastery = "mastery";

        public static bool IsValid(string kind)
        {
            return kind == Introductory || kind == Mastery;
        }
    }

    // One leg of an objective. The objective is met when the metric
    // satisfies (comparator, threshold); conditions listed in
    // `alsoRequires` must hold in the same race (AND inside a leg).
    public sealed class ObjectiveCondition
    {
        public string metric = "";
        public string comparator = ObjectiveComparator.AtLeast;
        public long threshold = 1;
        public List<ObjectiveCondition> alsoRequires = new List<ObjectiveCondition>();

        public bool Validate(out string error)
        {
            error = null;
            if (!ObjectiveMetric.IsValid(metric)) { error = "metric"; return false; }
            if (!ObjectiveComparator.IsValid(comparator)) { error = "comparator"; return false; }
            // An atLeast zero objective is trivially satisfied — not a
            // real template; atMost zero is meaningful (zero recoveries).
            if (threshold < 0 || (comparator == ObjectiveComparator.AtLeast && threshold == 0))
            { error = "threshold"; return false; }
            if (alsoRequires != null)
                foreach (var conjunct in alsoRequires)
                {
                    if (conjunct == null || !conjunct.Validate(out error))
                    { error = "alsoRequires." + error; return false; }
                    if (conjunct.alsoRequires != null && conjunct.alsoRequires.Count > 0)
                    { error = "alsoRequires.nested"; return false; }
                }
            return true;
        }

        public ObjectiveCondition Copy()
        {
            var copy = new ObjectiveCondition
            {
                metric = metric,
                comparator = comparator,
                threshold = threshold
            };
            if (alsoRequires != null)
                foreach (var conjunct in alsoRequires)
                    copy.alsoRequires.Add(conjunct == null ? null : conjunct.Copy());
            return copy;
        }
    }

    // One star bit on a regular event: `alternatives` is an OR list — the
    // objective is satisfied when any one leg holds. This is how the
    // circuit mastery "finish first OR validate both shortcuts" is
    // authored without code.
    public sealed class ObjectiveDefinition
    {
        public string id = "";
        public string kind = ObjectiveKind.Introductory;
        public List<ObjectiveCondition> alternatives = new List<ObjectiveCondition>();

        public bool Validate(out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(id)) { error = "id"; return false; }
            if (!ObjectiveKind.IsValid(kind)) { error = "kind"; return false; }
            if (alternatives == null || alternatives.Count == 0)
            { error = "alternatives"; return false; }
            if (alternatives != null)
                foreach (var condition in alternatives)
                {
                    if (condition == null || !condition.Validate(out error))
                    { error = "alternatives." + error; return false; }
                }
            return true;
        }

        public ObjectiveDefinition Copy()
        {
            var copy = new ObjectiveDefinition { id = id, kind = kind };
            if (alternatives != null)
                foreach (var condition in alternatives)
                    copy.alternatives.Add(condition == null ? null : condition.Copy());
            return copy;
        }
    }
}
