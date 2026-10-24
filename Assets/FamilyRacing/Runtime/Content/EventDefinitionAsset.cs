using System;
using System.Collections.Generic;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using UnityEngine;

namespace FamilyRacing
{
    // Authored event data converted into a validated Core EventDefinition:
    // which course, laps, countdown length, allowed vehicles, and the
    // data-driven recovery thresholds.
    [CreateAssetMenu(menuName = "FamilyRacing/Event Definition",
        fileName = "event-definition")]
    public sealed class EventDefinitionAsset : ScriptableObject
    {
        public string eventId = "practice-02";
        public TrackDefinitionAsset track;
        public int laps = 2;
        public int countdownTicks = 90;
        public int finishGraceSeconds = 60;
        public string[] vehicleIds = { "car-01", "motorcycle-01" };
        // Coins a finalized race awards each finisher; zero keeps the
        // event reward-free (solo events award nothing here — rewards
        // flow through the profile/economy slice instead).
        public int completionCoins;
        [Header("Recovery")]
        public float offCourseSeconds = 1.5f;
        public float minCourseY = -5f;
        public float stuckSpeed = 0.75f;
        public float stuckSeconds = 3f;
        public float beachedSeconds = 8f;
        [Header("Recovery (physical backend)")]
        public float overturnedSeconds = 2f;
        public float outsideBoundsSeconds = 1.5f;
        [Header("Gameplay (optional — empty eventType keeps a legacy event)")]
        // One of EventType: circuit|boost|stunt|time|gadget|clean-driving|
        // finale. Typed events need vehicleClass and the objective pair.
        public string eventType = "";
        public string vehicleClass = ""; // "car" | "motorcycle"
        // Authored par time for time challenges (30 Hz ticks); must be
        // positive when eventType is "time" — nonpositive par fails the
        // whole conversion, never partially loads.
        public int parTicks;
        public bool isFinale;

        // One ANDed conjunct inside a leg — a leaf on purpose: Core
        // validation rejects nested alsoRequires, so the serialized
        // row stops at one level and Unity never walks a
        // self-referencing field to its serialization depth limit.
        [Serializable]
        public sealed class ObjectiveConditionLeaf
        {
            public string metric = "";
            public string comparator = "atLeast"; // "atLeast" | "atMost"
            public long threshold = 1;
        }

        [Serializable]
        public sealed class ObjectiveConditionRow
        {
            public string metric = "";
            public string comparator = "atLeast"; // "atLeast" | "atMost"
            public long threshold = 1;
            // ANDed conditions inside this leg (e.g. "60 s streak AND
            // zero recoveries"); OR lives across `alternatives`.
            public ObjectiveConditionLeaf[] alsoRequires = new ObjectiveConditionLeaf[0];
        }

        [Serializable]
        public sealed class ObjectiveRow
        {
            public string objectiveId = "";
            public string kind = "introductory"; // "introductory" | "mastery"
            public ObjectiveConditionRow[] alternatives = new ObjectiveConditionRow[0];
        }

        public ObjectiveRow[] objectives = new ObjectiveRow[0];

        private ObjectiveCondition ToCondition(ObjectiveConditionRow row,
            out string error)
        {
            if (row == null)
            {
                error = name + ": null objective condition";
                return null;
            }
            var condition = new ObjectiveCondition
            {
                metric = row.metric,
                comparator = row.comparator,
                threshold = row.threshold
            };
            if (row.alsoRequires != null)
                foreach (var conjunctRow in row.alsoRequires)
                {
                    var conjunct = ToCondition(conjunctRow, out error);
                    if (conjunct == null) return null;
                    condition.alsoRequires.Add(conjunct);
                }
            if (!condition.Validate(out error))
            {
                error = name + ": invalid objective condition " + error;
                return null;
            }
            error = null;
            return condition;
        }

        // Leaf conjuncts need no Validate of their own — the leg's
        // Validate above walks `alsoRequires` and names the failing
        // field (e.g. "alsoRequires.metric").
        private ObjectiveCondition ToCondition(ObjectiveConditionLeaf leaf,
            out string error)
        {
            if (leaf == null)
            {
                error = name + ": null objective condition";
                return null;
            }
            error = null;
            return new ObjectiveCondition
            {
                metric = leaf.metric,
                comparator = leaf.comparator,
                threshold = leaf.threshold
            };
        }

        public EventDefinition ToEvent(out string error)
        {
            if (track == null)
            {
                error = name + ": missing track reference";
                return null;
            }
            var evt = new EventDefinition
            {
                id = eventId,
                courseId = track.courseId,
                laps = laps,
                countdownTicks = countdownTicks,
                finishGraceSeconds = finishGraceSeconds,
                vehicleIds = new List<string>(vehicleIds ?? new string[0]),
                recovery = new RecoverySettings
                {
                    offCourseSeconds = offCourseSeconds,
                    minCourseY = minCourseY,
                    stuckSpeed = stuckSpeed,
                    stuckSeconds = stuckSeconds,
                    beachedSeconds = beachedSeconds,
                    overturnedSeconds = overturnedSeconds,
                    outsideBoundsSeconds = outsideBoundsSeconds
                },
                completionCoins = completionCoins,
                eventType = eventType,
                vehicleClass = vehicleClass,
                parTicks = parTicks,
                isFinale = isFinale
            };
            if (objectives != null && objectives.Length > 0)
            {
                evt.objectives = new List<ObjectiveDefinition>();
                foreach (var row in objectives)
                {
                    if (row == null)
                    {
                        error = name + ": null objective row";
                        return null;
                    }
                    var objective = new ObjectiveDefinition
                    {
                        id = row.objectiveId,
                        kind = row.kind
                    };
                    if (row.alternatives != null)
                        foreach (var conditionRow in row.alternatives)
                        {
                            var condition = ToCondition(conditionRow, out error);
                            if (condition == null) return null;
                            objective.alternatives.Add(condition);
                        }
                    evt.objectives.Add(objective);
                }
            }
            if (!evt.Validate(out error))
            {
                error = name + ": invalid event field " + error;
                return null;
            }
            error = null;
            return evt;
        }
    }
}
