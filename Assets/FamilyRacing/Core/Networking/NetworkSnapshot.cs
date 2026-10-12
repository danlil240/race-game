using System;
using System.Collections.Generic;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Networking
{
    // Per-racer processed-input acknowledgment inside a NetworkSnapshot.
    // The host only advances these fields when it actually consumes a
    // sample for the racer on a simulated boundary — a received but
    // unconsumed sample is never acknowledged, so a client's prediction
    // replay key (Task 6) is exactly the set of inputs newer than this.
    [Serializable]
    public sealed class NetworkInputAck
    {
        public string participantId = "";
        public long inputSequence;   // last input sequence the host consumed
        public long clientTick;      // that frame's client tick
        public long hostTick;        // host tick it was consumed on — the authoritative boundary it fed

        public NetworkInputAck Copy()
        {
            return (NetworkInputAck)MemberwiseClone();
        }
    }

    // One reliable-ordered batch of host transient events. Events travel
    // on the reliable ordered channel so events between snapshots stay
    // visible; `id` identities let consumers apply each exactly once even
    // when a batch is redelivered (prediction/replay never creates a
    // second authoritative event).
    [Serializable]
    public sealed class NetworkEventBatch
    {
        public string sessionId = "";
        public string raceId = "";
        public int raceOrdinal;      // monotonic per session; fences stale-race batches
        public long batchSequence;   // +1 per batch within a race
        public List<DrivingEvent> events = new List<DrivingEvent>();
    }

    // Full four-racer authoritative race state for one 15 Hz boundary
    // (every second host tick of the 30 Hz simulation). The wire codec
    // enforces a hard SnapshotCeilingBytes ceiling and fails explicitly on
    // overflow rather than truncating racer state.
    //
    // `state.events` is always empty on the wire: transient events ride
    // the reliable ordered NetworkEventBatch channel so each event is
    // delivered exactly once with its identity.
    [Serializable]
    public sealed class NetworkSnapshot
    {
        public string sessionId = "";
        public string raceId = "";
        public int raceOrdinal;        // monotonic per session; fences stale-race snapshots
        public long snapshotSequence;  // +1 per published snapshot within a race
        public NetworkRaceState state;
        public List<NetworkInputAck> acks = new List<NetworkInputAck>();
    }
}
