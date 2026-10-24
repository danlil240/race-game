using System;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Progression
{
    // Pure evaluation of authored objective templates against one
    // finalized race result (M5 Task 6). Reads only the authoritative
    // per-racer metrics row — the accumulator already encoded
    // attribution, clean-streak resets and boundary scoping, so this
    // evaluator compares final values and never re-simulates.
    //
    // Eligibility: only a finalized result's finished, human-attributed
    // row can earn objective bits — DNF rows, unfinalized results, and
    // seats the AI was driving at finalize grant nothing. Evaluate()
    // returns the raw fact for one objective; EvaluateMask() adds the
    // campaign-mode scope (stars are solo-only) and the implicit
    // completion bit. Neither mutates the result nor the definition.
    public static class ObjectiveEvaluator
    {
        // The three star bits persisted per regular event in
        // EventProgress.objectiveMask: completion + introductory +
        // mastery (CampaignDefinition.ObjectiveBitsPerEvent).
        public const int CompletionBit = 0x1;
        public const int IntroductoryBit = 0x2;
        public const int MasteryBit = 0x4;
        public const int FullMask =
            CompletionBit | IntroductoryBit | MasteryBit;

        // One objective against one participant's finalized facts. An
        // objective is satisfied when ANY alternative leg holds; a leg
        // holds when its condition and every `alsoRequires` conjunct
        // hold (OR across legs, AND inside a leg).
        public static bool Evaluate(ObjectiveDefinition definition,
            RaceResult result, string participantId)
        {
            var metrics = EligibleMetrics(result, participantId);
            if (metrics == null || definition == null
                || definition.alternatives == null) return false;
            foreach (var leg in definition.alternatives)
                if (leg != null && LegHolds(leg, metrics)) return true;
            return false;
        }

        // The star bits this result earns for one campaign event: the
        // completion bit for any finished human row plus one bit per
        // satisfied objective by kind. Only campaign-mode results earn
        // stars — quick race, LAN, time trial, practice and foreign
        // event ids can never mint campaign progress.
        public static int EvaluateMask(EventDefinition evt,
            RaceResult result, string participantId)
        {
            if (evt == null || result == null || result.eventId != evt.id
                || result.mode != RaceModeRules.ModeId(RaceMode.Campaign))
                return 0;
            if (EligibleMetrics(result, participantId) == null) return 0;
            var mask = CompletionBit;
            if (evt.objectives != null)
                foreach (var objective in evt.objectives)
                {
                    if (objective == null
                        || !Evaluate(objective, result, participantId))
                        continue;
                    if (objective.kind == ObjectiveKind.Introductory)
                        mask |= IntroductoryBit;
                    else if (objective.kind == ObjectiveKind.Mastery)
                        mask |= MasteryBit;
                }
            return mask;
        }

        // Objective bits union across replays: a re-earned bit is
        // idempotent and no bit is ever lost, so regional stars are
        // always the popcount of the merged mask.
        public static int MergeMask(int storedMask, int earnedMask)
        {
            return (storedMask | earnedMask) & FullMask;
        }

        // The finalized, finished, human-attributed metrics row for one
        // participant — null for DNFs, unfinalized results, absent
        // participants, AI-ended seats, and legacy (metric-less) rows.
        private static RaceMetrics EligibleMetrics(RaceResult result,
            string participantId)
        {
            if (result == null || !result.finalized || result.racers == null
                || string.IsNullOrEmpty(participantId)) return null;
            foreach (var entry in result.racers)
            {
                if (entry == null || entry.participantId != participantId)
                    continue;
                if (entry.attribution != "human"
                    || entry.outcome != "finished" || entry.metrics == null)
                    return null;
                return entry.metrics;
            }
            return null;
        }

        private static bool LegHolds(ObjectiveCondition leg,
            RaceMetrics metrics)
        {
            if (!Holds(leg, metrics)) return false;
            if (leg.alsoRequires != null)
                foreach (var conjunct in leg.alsoRequires)
                    if (conjunct == null || !Holds(conjunct, metrics))
                        return false;
            return true;
        }

        private static bool Holds(ObjectiveCondition condition,
            RaceMetrics metrics)
        {
            long value;
            if (condition == null || !MetricValue(condition.metric,
                    metrics, out value))
                return false;
            if (condition.comparator == ObjectiveComparator.AtLeast)
                return value >= condition.threshold;
            if (condition.comparator == ObjectiveComparator.AtMost)
                return value <= condition.threshold;
            return false;
        }

        // Metric values in the metric's own unit: the "*Seconds" metrics
        // fold 30 Hz tick counts into whole seconds (floor — a 29.9 s
        // streak never rounds into a 30 s requirement); every other
        // metric reads its counter directly.
        private static bool MetricValue(string metric, RaceMetrics m,
            out long value)
        {
            switch (metric)
            {
                case ObjectiveMetric.CleanLandings:
                    value = m.cleanLandings; return true;
                case ObjectiveMetric.FinishPlace:
                    value = m.position; return true;
                case ObjectiveMetric.DistinctShortcuts:
                    value = m.humanDistinctShortcuts; return true;
                case ObjectiveMetric.HumanBoostActivations:
                    value = m.boostActivations; return true;
                case ObjectiveMetric.FinishWithinTicks:
                    value = m.finishTicks; return true;
                case ObjectiveMetric.GadgetsCollectedAndUsed:
                    value = m.gadgetUses; return true;
                case ObjectiveMetric.DistinctGadgetTypesUsed:
                    value = m.distinctGadgetTypes; return true;
                case ObjectiveMetric.CleanStreakSeconds:
                    value = m.longestCleanStreakTicks
                        / NetworkSettings.SimulationTicksPerSecond;
                    return true;
                case ObjectiveMetric.Recoveries:
                    value = m.recoveries; return true;
                default:
                    value = 0; return false;
            }
        }
    }
}
