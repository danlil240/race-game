using System;
using System.Collections.Generic;
using FamilyRacing.Core.Profiles;

namespace FamilyRacing.Core.Racing
{
    // Everything a local race-mode request needs: the mode, the content
    // identity it runs (event for campaign/quick/practice, course+class
    // for time trial), the chosen class vehicle and AI difficulty, and
    // the local participant + race identity the reward context binds.
    public sealed class SoloRaceRequest
    {
        public string mode = "";        // campaign | quickRace | timeTrial | practice
        public string eventId = "";     // campaign | quickRace | practice
        public string courseId = "";    // timeTrial
        public string classId = "";     // timeTrial (normalized starter class)
        public string vehicleId = "";   // campaign | quickRace | practice
        public string difficultyId = "";// "" resolves to easy
        public string participantId = "";// local human identity
        public string raceId = "";      // "" generates one
        public string commandId = "";   // start-correlation for race.started

        public SoloRaceRequest Copy()
        {
            return (SoloRaceRequest)MemberwiseClone();
        }
    }

    // Published state of one local race session. Exactly one of `race`
    // (four-racer modes: campaign, quickRace) or `trial` (single-racer
    // modes: timeTrial, practice) is populated; the unused branch stays
    // null. `result` is the finalized immutable result or null —
    // aborted sessions never carry one. `events` holds only the events
    // the most recent boundary produced (like NetworkRaceState.events).
    [Serializable]
    public sealed class SoloRaceState
    {
        public string mode = "";
        public string sessionId = "";
        public string raceId = "";
        public string participantId = "";
        public string phase = "";       // countdown | running | finished | aborted
        public bool paused;
        public string difficultyId = "";
        // Where the local human seat's tune came from: "purchased" when
        // the profile's durable upgrades resolved onto the catalog
        // baseline (campaign | quickRace), "normalized" when the mode
        // seats the authored starter (timeTrial). AI seats always run
        // the catalog baseline; this label describes the human seat.
        public string tuningSource = "";
        public NetworkRaceState race;   // campaign | quickRace only
        public DrivingSessionState trial;// timeTrial | practice only
        public RaceResult result;
        public List<RaceMetrics> metrics = new List<RaceMetrics>();
        public List<DrivingEvent> events = new List<DrivingEvent>();
        public string abortReason = "";

        public SoloRaceState Copy()
        {
            var copy = new SoloRaceState
            {
                mode = mode,
                sessionId = sessionId,
                raceId = raceId,
                participantId = participantId,
                phase = phase,
                paused = paused,
                difficultyId = difficultyId,
                tuningSource = tuningSource,
                race = race != null ? race.Copy() : null,
                trial = trial != null ? trial.Copy() : null,
                result = result != null ? result.Copy() : null,
                abortReason = abortReason
            };
            foreach (var metric in metrics)
                copy.metrics.Add(metric != null ? metric.Copy() : null);
            foreach (var ev in events)
                copy.events.Add(ev != null ? ev.Copy() : null);
            return copy;
        }
    }
}
