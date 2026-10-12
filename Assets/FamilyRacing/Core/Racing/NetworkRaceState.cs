using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;

namespace FamilyRacing.Core.Racing
{
    // One racer the coordinator may seat: roster slot identity, chosen
    // vehicle, and who produces its inputs. `controlSource` accepts the
    // ParticipantState vocabulary ("local" | "remote"); the race
    // normalizes it to "human" | "ai" attribution downstream.
    //
    // `requestedTuning` is where a profile-sourced (upgraded) tune would
    // arrive; per the milestone rule it is deliberately ignored — within
    // a selected class every racer runs the identical catalog baseline.
    // Cosmetic and model identity survive through vehicleId.
    [Serializable]
    public sealed class NetworkRacerSpec
    {
        public string participantId = "";
        public int slot = -1;
        public string vehicleId = "";
        public string controlSource = "ai"; // ParticipantState: local | remote | ai
        public VehicleTuning requestedTuning; // ignored: races normalize tuning per class
    }

    // Observable per-racer race facts: immutable identity + attribution
    // for the session, live finish/DNF facts, current standing position,
    // and the underlying driving snapshot Task 5 snapshots forward.
    [Serializable]
    public sealed class NetworkRacerState
    {
        public string participantId = "";
        public int slot;
        public string vehicleId = "";
        public string attribution = "ai";   // "human" | "ai"
        public int position;                // 1-based standing
        public bool finished;
        public bool dnf;
        public long finishTicks;            // elapsed race ticks at finish; 0 otherwise
        public DrivingSessionState session;

        public NetworkRacerState Copy()
        {
            var copy = (NetworkRacerState)MemberwiseClone();
            copy.session = session != null ? session.Copy() : null;
            return copy;
        }
    }

    // Authoritative race snapshot: one host clock, one shared countdown,
    // one shared elapsed race clock, one pause, per-racer session states,
    // standings, and the single finalized result. `events` carries only
    // the events produced by the Step() that returned this state;
    // Snapshot() reports an empty list.
    [Serializable]
    public sealed class NetworkRaceState
    {
        public string sessionId = "";
        public string raceId = "";
        public string phase = "countdown";  // countdown | running | finished | aborted
        public long hostTick;               // simulated 30 Hz boundaries
        public int countdownTicksRemaining;
        public long elapsedTicks;           // unpaused running-phase ticks (race clock)
        public bool paused;
        public long firstFinishElapsedTicks;     // 0 until the first finisher
        public long finishDeadlineElapsedTicks;  // grace deadline; 0 until first finisher
        public List<NetworkRacerState> racers = new List<NetworkRacerState>();
        public List<int> standings = new List<int>(); // slot indices in rank order
        public List<DrivingEvent> events = new List<DrivingEvent>();
        public RaceResult result;           // assigned once at finalization

        // Deep copy so observation/state-stream consumers can never alias
        // the coordinator's or the wire decoder's live objects.
        public NetworkRaceState Copy()
        {
            var copy = new NetworkRaceState
            {
                sessionId = sessionId,
                raceId = raceId,
                phase = phase,
                hostTick = hostTick,
                countdownTicksRemaining = countdownTicksRemaining,
                elapsedTicks = elapsedTicks,
                paused = paused,
                firstFinishElapsedTicks = firstFinishElapsedTicks,
                finishDeadlineElapsedTicks = finishDeadlineElapsedTicks,
                result = result != null ? result.Copy() : null
            };
            copy.standings.AddRange(standings);
            foreach (var racer in racers)
                copy.racers.Add(racer != null ? racer.Copy() : null);
            foreach (var ev in events)
                copy.events.Add(ev != null ? ev.Copy() : null);
            return copy;
        }
    }
}
