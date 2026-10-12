using System;

namespace FamilyRacing.Core.Networking
{
    // Public description of one LAN session: who hosts it, which versions
    // it speaks, what is configured, and whether a new participant may
    // join. It intentionally carries no reconnect secrets — credentials
    // travel only on the private join-accept path.
    [Serializable]
    public sealed class SessionDescriptor
    {
        public string sessionId = "";
        public string hostAddress = "";
        public int hostPort;
        public int lanProtocol = NetworkSettings.LanProtocolVersion;
        public string gameplayVersion = "";
        public string contentVersion = "";
        public string eventId = "";
        public string classId = "";
        public int occupancy;
        public int capacity = NetworkSettings.ParticipantCapacity;
        public bool joinable;

        public SessionDescriptor Copy()
        {
            return (SessionDescriptor)MemberwiseClone();
        }
    }
}
