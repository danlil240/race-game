using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Racing
{
    // One ordered row in a finalized race result: finishers first (finish
    // tick, then stable slot), then DNFs in standings order. `attribution`
    // is "human" | "ai"; `coinsAwarded` is the authored completion reward
    // applied to finishers only — DNFs and aborted races award nothing.
    [Serializable]
    public sealed class RaceResultEntry
    {
        public int position;
        public int slot;
        public string participantId = "";
        public string vehicleId = "";
        public string attribution = "";
        public string outcome = "";         // "finished" | "dnf"
        public long finishTicks;            // elapsed race ticks; 0 for DNF
        public int lapsCompleted;
        public int coinsAwarded;
    }

    // The single authoritative outcome of one network race. Created
    // exactly once at finalization and never mutated afterwards; aborted
    // races produce no result. `completionCoins` is the authored reward
    // fact (Task 8 applies it to eligible finished racers).
    [Serializable]
    public sealed class RaceResult
    {
        public string resultId = "";
        public string sessionId = "";
        public string raceId = "";
        public string eventId = "";
        public string contentVersion = "";
        public bool finalized;
        public long finalizedHostTick;
        public long finalizedElapsedTicks;
        public int completionCoins;
        public List<RaceResultEntry> racers = new List<RaceResultEntry>();
    }
}
