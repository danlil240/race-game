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
    // `compat` carries the responder's full compatibility token
    // (gameplay | content | fingerprint) so a join can fence an
    // incompatible build before a single datagram is dialed.
    [Serializable]
    public sealed class LanDiscoveredHost
    {
        public string address = "";
        public int port;
        public string hostName = "";
        public string compat = "";

        public LanDiscoveredHost Copy()
        {
            return (LanDiscoveredHost)MemberwiseClone();
        }
    }

    // One result-delivery row exposed to observers. On the host it is one
    // participant's delivery state inside a retained result; on a client
    // it is one locally retained result's persistence state. `state` is
    // pending | failed | persisted; `code` carries the failure reason
    // (empty otherwise) so permanent verdicts like "ineligible:dnf" are
    // distinguishable from retryable failures like "saveFailed".
    [Serializable]
    public sealed class ResultDeliveryState
    {
        public string resultId = "";
        public string raceId = "";
        public string participantId = "";
        public string state = "";
        public string code = "";

        public ResultDeliveryState Copy()
        {
            return (ResultDeliveryState)MemberwiseClone();
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
        // The host-allocated identity of the race being prepared/racing
        // (M5 Task 4): allocated at lan.start and broadcast inside the
        // session state so every participant binds its local reward
        // context against the same identity before the race clock
        // starts. "" outside countdown/racing/results.
        public string currentRaceId = "";
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
        // Task 8: result delivery/persistence rows — the host's retained
        // ledger on a host, the locally retained results on a client.
        public List<ResultDeliveryState> results =
            new List<ResultDeliveryState>();
    }
}
