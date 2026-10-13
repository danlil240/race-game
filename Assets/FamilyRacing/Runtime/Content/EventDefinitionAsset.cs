using System.Collections.Generic;
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
                completionCoins = completionCoins
            };
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
