using System;
using System.Collections.Generic;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
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
        // Task 11: replication-link packet/loss counters published by
        // the bridge — the starvation-vs-rejection evidence.
        private NetworkLinkState networkLinkState;

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
            // A completed join/reconnect lands the user on the lobby on the
            // same boundary it completes; a failed one returns the browser.
            SyncLanScreen();
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

        // Task 11: publish the replication bridge's own packet/loss
        // counters — the facts that distinguish "no snapshots arrived"
        // from "snapshots arrived and were rejected". Null where no
        // replication bridge exists.
        public void SetNetworkLinkState(NetworkLinkState state)
        {
            networkLinkState = state;
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
            // A command that changes phase/role updates the screen on its
            // own boundary: lan.host lands on the lobby, lan.leave returns
            // to the browser, a disconnected client lands on recovery.
            SyncLanScreen();
            return result;
        }

        // --- LAN screen mapping (Task 9) ----------------------------------
        // The screen a session phase renders on. "lan-browser" is the only
        // user-navigable LAN screen; every other screen name is a pure
        // projection of the session phase so the UI can never show a lobby
        // for a session that no longer exists.

        public static bool IsLanScreen(string value)
        {
            return value == ScreenLanBrowser || value == ScreenLanLobby
                || value == ScreenLanRace || value == ScreenLanResults
                || value == ScreenLanRecovery;
        }

        public static string LanScreenForPhase(string phase)
        {
            switch (phase)
            {
                case LanSessionService.PhaseLobby: return ScreenLanLobby;
                case LanSessionService.PhaseCountdown:
                case LanSessionService.PhaseRacing: return ScreenLanRace;
                case LanSessionService.PhaseResults: return ScreenLanResults;
                case LanSessionService.PhaseReconnecting:
                case LanSessionService.PhaseRecovery: return ScreenLanRecovery;
                default: return ScreenLanBrowser;
            }
        }

        // Screen reconciliation: while a session is active the phase owns
        // the screen (the UI follows lobby -> countdown -> racing ->
        // results -> recovery without any navigation command); without a
        // session only the browser may remain — a stranded phase screen is
        // walked back to it. Solo screens are never touched.
        private void SyncLanScreen()
        {
            if (!LanActive())
            {
                if (IsLanScreen(screen) && screen != ScreenLanBrowser)
                    screen = ScreenLanBrowser;
                return;
            }
            var target = LanScreenForPhase(lanSession.Phase);
            if (screen != target) screen = target;
        }

        // --- lobby pickers -------------------------------------------------
        // LAN-configurable content exposed to the shell: only events
        // authored for LAN play (the "lan-" prefix convention) appear, so
        // the picker never offers a solo-only practice/tutorial event the
        // session would reject anyway. Ordering is ordinal-deterministic.

        public IReadOnlyList<string> LanEventIds()
        {
            var ids = new List<string>();
            if (drivingContent == null || drivingContent.Events == null)
                return ids;
            foreach (var id in drivingContent.Events.Keys)
                if (id != null && id.StartsWith("lan-", StringComparison.Ordinal))
                    ids.Add(id);
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }

        public IReadOnlyList<string> LanClassIds(string eventId)
        {
            var classes = new List<string>();
            EventDefinition evt;
            if (drivingContent == null || drivingContent.Events == null
                || eventId == null
                || !drivingContent.Events.TryGetValue(eventId, out evt)
                || evt == null || evt.vehicleIds == null)
                return classes;
            foreach (var vehicleId in evt.vehicleIds)
            {
                var classId = VehicleTuning.ClassIdOf(vehicleId);
                if (!string.IsNullOrEmpty(classId) && !classes.Contains(classId))
                    classes.Add(classId);
            }
            classes.Sort(StringComparer.Ordinal);
            return classes;
        }

        public IReadOnlyList<string> LanVehicleIds(string eventId, string classId)
        {
            var ids = new List<string>();
            EventDefinition evt;
            if (drivingContent == null || drivingContent.Events == null
                || eventId == null
                || !drivingContent.Events.TryGetValue(eventId, out evt)
                || evt == null || evt.vehicleIds == null)
                return ids;
            foreach (var vehicleId in evt.vehicleIds)
                if (VehicleTuning.ClassIdOf(vehicleId) == (classId ?? ""))
                    ids.Add(vehicleId);
            ids.Sort(StringComparer.Ordinal);
            return ids;
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
            // M5 Task 4: the allocated race identity binds a durable
            // reward context before the race clock may start — the
            // preparer reports an error instead of ever starting
            // unbound.
            lanSession.SetRacePreparer(PrepareLanRewardContext);
        }

        // The session service hands each finalized result here. Task 7
        // resolves the durable reward context the race was bound under
        // and runs the full atomic transaction — coins, progression
        // facts, record, receipt and context consumption in one save.
        // A saveFailed outcome retains the immutable result with its
        // exact bound context so RetryUnresolvedRewards replays the
        // identical transaction; the pair also gates every new
        // progression race. Terminal failures (conflict, overflow,
        // invalid) can NEVER commit on retry — latching them would wedge
        // every later progression start, so they never set the gate and
        // they release any retained pair for the same result identity.
        // The durable context row stays pending in the profile either
        // way — the honest forensic record of what was bound.
        private RewardApplyResult ApplyNetworkResult(RaceResult result)
        {
            if (savedProfile == null)
                return RewardApplyResult.Ineligible("noProfile");
            var context = FindRewardContext(
                result != null ? result.raceId : null);
            var applied = RaceRewardService.Apply(
                result, savedProfile.id, savedProfile, store,
                gameplayCatalog, context);
            if (applied.status == RaceRewardService.StatusFailed)
            {
                if (applied.code == "saveFailed")
                {
                    unresolvedResult =
                        result != null ? result.Copy() : null;
                    unresolvedContext =
                        context != null ? context.Copy() : null;
                }
                else if (unresolvedResult != null && result != null
                    && unresolvedResult.resultId == result.resultId)
                {
                    // A terminal verdict on a retained result ends its
                    // retry — it can never commit, so the gate frees.
                    unresolvedResult = null;
                    unresolvedContext = null;
                }
                return applied;
            }
            // Terminal verdicts (applied, alreadyApplied, ineligible —
            // including a committed bound DNF) resolve any retained
            // retry for this result identity.
            if (unresolvedResult != null && result != null
                && unresolvedResult.resultId == result.resultId)
            {
                unresolvedResult = null;
                unresolvedContext = null;
            }
            // Refresh whenever facts durably committed — awards AND the
            // committed non-award adjudications (bound DNF) alike. An
            // ineligible-but-committed verdict must still update the
            // slot cache, or a profile reselect resurrects the consumed
            // context and drops the receipt.
            if (applied.committed)
                RefreshProgressionCaches();
            return applied;
        }

        private NetworkSessionState NetworkSnapshot()
        {
            return lanSession != null ? lanSession.Snapshot() : new NetworkSessionState();
        }
    }
}
