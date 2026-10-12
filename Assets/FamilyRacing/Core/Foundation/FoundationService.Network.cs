using System;
using System.Collections.Generic;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Foundation
{
    // LAN half of the foundation service: attaches the Unity-free session
    // service, routes lan.* (and the network meaning of pause.toggle)
    // through it, keeps the identity cache honest across the async command
    // lifecycle, and gates solo-only commands while a session is active.
    public sealed partial class FoundationService
    {
        private static readonly IReadOnlyList<CommandResult> NoNetworkOutcomes =
            new CommandResult[0];

        private LanSessionService lanSession;
        // Task 5: the latest host-confirmed race state — the coordinator's
        // snapshot on the host, the last delivered snapshot on a client.
        // Null until a network race exists; never aliased onto `driving`.
        private NetworkRaceState networkRaceState;
        // Task 6: local prediction facts (predicted motor, ack horizon,
        // pending buffer, corrections) and remote interpolation facts.
        // Stored by reference; Snapshot() deep-copies so emitted states
        // stay independent.
        private NetworkPredictionState networkPrediction;
        private RemoteInterpolationState networkRemotePresentation;

        // The session service Tasks 3+ drive (race coordinator hooks,
        // transport callbacks); null until the runtime attaches one.
        public LanSessionService Network
        {
            get { return lanSession; }
        }

        public void SetLanSession(LanSessionService session)
        {
            lanSession = session;
            if (lanSession != null) lanSession.SetContent(drivingContent);
        }

        // True while a LAN session is in progress (connecting, lobby,
        // countdown, racing, results, reconnecting or recovery). Passive
        // discovery does not count — browsing hosts is not a session.
        private bool LanActive()
        {
            return lanSession != null && lanSession.InSession;
        }

        // Async lifecycle: transport/message work produces terminal
        // outcomes; draining hands them to the caller for boundary
        // emission while the identity cache still holds the accepted
        // placeholder until CompleteNetworkCommand lands.
        public IReadOnlyList<CommandResult> DrainNetworkOutcomes()
        {
            if (lanSession == null) return NoNetworkOutcomes;
            var drained = lanSession.DrainOutcomes();
            foreach (var outcome in drained) outcome.tick = tick;
            return drained;
        }

        // Exactly-once completion: an accepted cache entry moves to the
        // terminal outcome; terminal entries and unknown ids are ignored.
        public bool CompleteNetworkCommand(CommandResult outcome)
        {
            if (outcome == null || string.IsNullOrEmpty(outcome.commandId)) return false;
            if (outcome.status != CommandResult.Applied
                && outcome.status != CommandResult.Rejected
                && outcome.status != CommandResult.Failed) return false;
            CachedResult cached;
            if (!results.TryGetValue(outcome.commandId, out cached)) return false;
            if (cached.Result.status != CommandResult.Accepted) return false;
            var terminal = outcome.Copy();
            terminal.tick = tick;
            terminal.duplicate = false;
            cached.Result = terminal;
            return true;
        }

        // Monotonic wall time for join/request expiry inside the session
        // service; safe to call every frame and while paused.
        public void AdvanceNetworkWallTime(double monotonicSeconds)
        {
            if (lanSession != null) lanSession.AdvanceWallTime(monotonicSeconds);
        }

        // Task 5: publish the host-confirmed race state delivered by the
        // replication bridge. Stored by reference; Snapshot() deep-copies
        // so emitted states stay independent of later mutation.
        public void SetNetworkRaceState(NetworkRaceState state)
        {
            networkRaceState = state;
        }

        // Task 5: authoritative network race events join the same bounded
        // driving-event stream, with the durable drop counter recording
        // every eviction exactly like the solo path.
        public void PublishNetworkRaceEvents(IReadOnlyList<DrivingEvent> events)
        {
            if (events == null) return;
            AppendDrivingEvents(events);
        }

        // Task 6: publish the local prediction facts the replication
        // bridge maintains — null outside a LAN race or on the host,
        // where the local racer is already authoritative.
        public void SetPredictionState(NetworkPredictionState state)
        {
            networkPrediction = state;
        }

        // Task 6: publish the remote-racer interpolation facts the
        // replication bridge maintains.
        public void SetRemotePresentationState(RemoteInterpolationState state)
        {
            networkRemotePresentation = state;
        }

        private CommandResult NetworkExecute(GameCommand command)
        {
            if (lanSession == null)
                return Result(command, CommandResult.Rejected, "unavailable");
            SyncNetworkContext();
            var result = lanSession.Execute(command);
            // Results are stamped with the service observation tick so the
            // trace correlates them with the boundary that produced them.
            if (result != null) result.tick = tick;
            return result;
        }

        private void SyncNetworkContext()
        {
            if (lanSession == null) return;
            lanSession.SetLocalContext(
                savedProfile != null ? savedProfile.id : "",
                savedProfile != null ? savedProfile.avatar : "",
                savedProfile != null ? savedProfile.colorIndex : 0,
                activeSlot >= 0,
                WritableSelected());
            // Task 8: finalized results apply their authored completion
            // reward to the selected profile through the atomic store —
            // exactly once per immutable result, provable by receipt.
            lanSession.SetRewardApplier(ApplyNetworkResult);
        }

        // The session service hands each finalized result here. Coins and
        // the receipt persist together inside one atomic save; the slot
        // caches and the settings draft are refreshed only after the
        // write lands, and the draft's reward fields track the live
        // profile so a pending settings save can never roll coins or
        // receipts back.
        private RewardApplyResult ApplyNetworkResult(RaceResult result)
        {
            if (savedProfile == null)
                return RewardApplyResult.Ineligible("noProfile");
            var applied = RaceRewardService.Apply(
                result, savedProfile.id, savedProfile, store);
            if (applied.status != RaceRewardService.StatusApplied
                && applied.status != RaceRewardService.StatusAlreadyApplied)
                return applied;
            if (activeSlot >= 0)
                slots[activeSlot].Profile = CopyProfile(savedProfile);
            if (draftProfile != null)
            {
                draftProfile.coins = savedProfile.coins;
                draftProfile.appliedResults =
                    CopyReceipts(savedProfile.appliedResults);
            }
            return applied;
        }

        private NetworkSessionState NetworkSnapshot()
        {
            return lanSession != null ? lanSession.Snapshot() : new NetworkSessionState();
        }
    }
}
