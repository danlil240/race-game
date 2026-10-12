using System;
using System.Collections.Generic;
using FamilyRacing.Core.Driving;

namespace FamilyRacing.Core.Networking
{
    // One client input sample on the wire (Task 5). Identity fields are
    // verified against the authenticated connection on the host —
    // `participantId` here is informational and never grants ownership:
    // the host derives allowed ownership from the connection that
    // delivered the frame.
    //
    // `steering` deliberately carries the raw wire value. DriverInput
    // clamps out-of-range input and neutralizes non-finite input on
    // construction, but the host validation path must see (and reject)
    // non-finite data instead of silently accepting a neutralized sample.
    // `input` is the bounded DriverInput a validated frame contributes.
    [Serializable]
    public sealed class NetworkInputFrame
    {
        public string sessionId = "";
        public string raceId = "";
        public string participantId = "";
        public long ownershipEpoch;
        // Per-participant monotonic sequence, +1 per local 30 Hz tick.
        // A packet resends up to InputPacketSampleCapacity recent samples;
        // duplicates are ignored by the host.
        public long inputSequence;
        public long clientTick;
        public float steering;   // raw wire value; finiteness is a host gate
        public bool drift;
        public bool boost;
        public bool gadget;
        public bool recover;
        public bool changeCamera;
        public bool pause;
        // One-shot action identities awaiting host acknowledgment (applied
        // or cancelled). Clients retry them across packets and across
        // reconnect; the host deduplicates them independently of held
        // input so a resent id can never fire twice.
        public List<string> actionIds = new List<string>();

        // The bounded DriverInput a validated sample contributes.
        public DriverInput input
        {
            get
            {
                return new DriverInput(steering, drift, boost, gadget,
                    recover, changeCamera, pause);
            }
        }

        public NetworkInputFrame Copy()
        {
            var copy = (NetworkInputFrame)MemberwiseClone();
            copy.actionIds = new List<string>(actionIds);
            return copy;
        }
    }
}
