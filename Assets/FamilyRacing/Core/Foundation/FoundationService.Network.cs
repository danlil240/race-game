using System;
using System.Collections.Generic;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Networking;

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
        }

        private NetworkSessionState NetworkSnapshot()
        {
            return lanSession != null ? lanSession.Snapshot() : new NetworkSessionState();
        }
    }
}
