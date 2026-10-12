using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Networking
{
    // One lobby/race participant. The participantId is stable (the local
    // profile identity) and survives reconnects; transport connection ids
    // are only ever used to deliver messages, never stored here. Ownership
    // epoch increments on every control transfer so stale connections
    // cannot reclaim a slot.
    [Serializable]
    public sealed class ParticipantState
    {
        public string participantId = "";
        public int slot = -1;
        public bool occupied;
        public string avatar = "";
        public int colorIndex;
        public string vehicleId = "";
        public bool connected;
        public bool ready;
        // local | remote | ai | none — who currently produces this
        // participant's inputs. A disconnected racer in a live race is
        // reserved and AI-driven per the milestone decision.
        public string controlSource = "none";
        public long ownershipEpoch;

        public ParticipantState Copy()
        {
            return (ParticipantState)MemberwiseClone();
        }
    }

    // One in-flight asynchronous command exposed to observers: enough to
    // correlate it, never enough to replay it.
    [Serializable]
    public sealed class PendingOperationState
    {
        public string commandId = "";
        public string action = "";
        public double startedSeconds;

        public PendingOperationState Copy()
        {
            return (PendingOperationState)MemberwiseClone();
        }
    }

    // One host advertisement currently visible through bounded discovery.
    [Serializable]
    public sealed class LanDiscoveredHost
    {
        public string address = "";
        public int port;
        public string hostName = "";

        public LanDiscoveredHost Copy()
        {
            return (LanDiscoveredHost)MemberwiseClone();
        }
    }

    // Observable network-session slice of FoundationState. Every Snapshot
    // is an independent deep copy; reconnect credentials and transport
    // identities never appear here.
    [Serializable]
    public sealed class NetworkSessionState
    {
        public string role = LanSessionService.RoleNone;
        public string phase = LanSessionService.PhaseIdle;
        public string localParticipantId = "";
        public SessionDescriptor descriptor;
        public List<ParticipantState> participants = new List<ParticipantState>();
        // Shared host-controlled pause plus the participants currently
        // requesting one. Wall-clock work (timeouts, streams, commands)
        // keeps running while paused.
        public bool paused;
        public List<string> pauseRequests = new List<string>();
        public string lastError = "";
        public List<PendingOperationState> pendingOperations =
            new List<PendingOperationState>();
        public List<LanDiscoveredHost> discovered = new List<LanDiscoveredHost>();
    }
}
