using System;

namespace FamilyRacing.Core.Racing
{
    // One ordered, transient driving event: stable per-race id, the
    // simulation tick it was emitted on, the kind, the correlated command
    // identity ("" when none), and a short detail token. Events are
    // surfaced through DrivingSessionState drivingEvents boundaries so an
    // observer can reconstruct consumed/cancelled/phase transitions.
    // Solo emissions leave participantId/attribution empty; the network
    // race coordinator stamps the owning participantId plus "human"|"ai"
    // at emission time so attribution never has to be re-derived later.
    [Serializable]
    public sealed class DrivingEvent
    {
        public string id;
        public long tick;
        public string kind;
        public string commandId;
        public string detail;
        public string participantId = "";
        public string attribution = "";     // "" solo | "human" | "ai"

        public DrivingEvent Copy()
        {
            return new DrivingEvent
            {
                id = id,
                tick = tick,
                kind = kind,
                commandId = commandId,
                detail = detail,
                participantId = participantId,
                attribution = attribution
            };
        }
    }
}
