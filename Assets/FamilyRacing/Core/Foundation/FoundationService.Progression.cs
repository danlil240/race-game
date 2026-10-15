using System;
using System.Collections.Generic;
using FamilyRacing.Core.Automation;
using FamilyRacing.Core.Driving;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Foundation
{
    // M5 Task 10 — the complete local gameplay loop exposed through the
    // semantic command surface. The *.open commands are the ONLY route
    // to their screens (gated, honest availability); the *.start
    // commands build a SoloRaceRequest and land on the shared
    // StartSoloSession path solo.start uses; results.* manage the
    // finished session and the unresolved-save gate. Nothing here
    // grants coins, stars or traversal — every durable mutation still
    // flows through a real race result or an authored purchase/equip.
    public sealed partial class FoundationService
    {
        // The screen a finished session's results.continue returns to —
        // each mode returns to its own setup surface.
        private static string LoopReturnScreen(string mode)
        {
            switch (mode)
            {
                case "campaign": return ScreenCampaign;
                case "quickRace": return ScreenQuickRace;
                case "timeTrial": return ScreenTimeTrial;
                default: return ScreenPractice;
            }
        }

        // A gated screen open: non-legacy gameplay content first, then a
        // selected profile — the same order solo.start enforces so
        // discovery, UI buttons and agent commands agree. The screens
        // are unreachable through ui.navigate; this is the only path.
        private CommandResult OpenLoopScreen(GameCommand command,
            string target)
        {
            if (gameplayCatalog == null || gameplayCatalog.IsLegacy
                || gameplayCatalog.Driving == null)
                return Result(command, CommandResult.Rejected,
                    "contentUnavailable");
            // Garage and bounty surfaces need the authored sections they
            // render — a catalog without them is honest unavailability.
            if ((target == ScreenGarage || target == ScreenBounties)
                && gameplayCatalog.Economy == null)
                return Result(command, CommandResult.Rejected,
                    "contentUnavailable");
            if (activeSlot < 0 || savedProfile == null)
                return Result(command, CommandResult.Rejected,
                    "requiresSelectedProfile");
            // A race is live: navigation refuses (the dispatch-level
            // fence already answered raceActive — this is defense in
            // depth for direct calls).
            if (driving != null || soloRace != null)
                return Result(command, CommandResult.Rejected,
                    "raceActive");
            // Leaving the sandbox neutralizes held input exactly like
            // Navigate does; landing on a loop screen clears pause so a
            // paused flag can never strand where pause.toggle is absent.
            if (screen == ScreenSandbox) ClearInput();
            paused = false;
            screen = target;
            return Result(command, CommandResult.Applied, "");
        }

        // campaign.start / quickRace.start / timeTrial.start: build the
        // mode's pinned request from its own parameters and hand it to
        // the shared start path. Field mapping is the only difference
        // between these and solo.start — validation, gating, identity
        // minting and reward-context binding are identical.
        private CommandResult LoopStart(GameCommand command,
            RaceMode mode)
        {
            var request = new SoloRaceRequest
            {
                mode = RaceModeRules.ModeId(mode)
            };
            switch (mode)
            {
                case RaceMode.Campaign:
                    request.eventId = OptionalParam(command, "eventId");
                    request.vehicleId = OptionalParam(command, "vehicleId");
                    request.difficultyId =
                        OptionalParam(command, "difficulty");
                    break;
                case RaceMode.QuickRace:
                    // Course + class select the authored circuit inside
                    // RaceModeRules.Evaluate — the same verdict the
                    // setup screen's pickers consult.
                    request.courseId = OptionalParam(command, "courseId");
                    request.classId = OptionalParam(command, "classId");
                    request.vehicleId = OptionalParam(command, "vehicleId");
                    request.difficultyId =
                        OptionalParam(command, "difficulty");
                    break;
                default: // RaceMode.TimeTrial
                    request.courseId = OptionalParam(command, "courseId");
                    request.classId = OptionalParam(command, "classId");
                    break;
            }
            return StartSoloSession(request, command);
        }

        // --- results lifecycle ------------------------------------------
        // The finished local session's replay/continue and the
        // unresolved-save retry. Retry replays the stored request under
        // a fresh race identity (SoloRestart); continue retires the
        // session back to the mode's setup screen; retrySave replays the
        // retained immutable result+context pair through the shared
        // exactly-once reward seam.

        private CommandResult ResultsRetrySave(GameCommand command)
        {
            if (unresolvedResult == null)
                return Result(command, CommandResult.Rejected,
                    "noPendingResult");
            var applied = RetryUnresolvedRewards();
            if (applied.status == RaceRewardService.StatusFailed)
            {
                persistenceStatus = "saveFailed";
                return Result(command, CommandResult.Failed,
                    applied.code ?? "saveFailed");
            }
            // The save retry did its durable work whenever the replayed
            // transaction COMMITTED — award, deduped award, or the
            // zero-award verdict a bound non-finish lands. Only a
            // verdict that committed nothing (an ineligible result the
            // gate already released) reports rejected.
            if (!applied.committed)
                return Result(command, CommandResult.Rejected,
                    applied.code ?? "unavailable");
            persistenceStatus = "saved";
            return Result(command, CommandResult.Applied, "");
        }

        // One-action replay: available exactly when a finished result
        // exists — a running or aborted session refuses honestly. The
        // replay binds a fresh race identity and its own reward context;
        // the finished result's receipt stays the dedup record. While a
        // finalized result's transaction is still uncommitted the replay
        // refuses BEFORE SoloRestart may tear the session down: this
        // overlay is the only retrySave surface, so a rejected retry
        // leaves the result — and the remedy — mounted.
        private CommandResult ResultsRetry(GameCommand command)
        {
            if (soloRace == null || soloRace.Result == null)
                return Result(command, CommandResult.Rejected,
                    "unavailable");
            if (unresolvedResult != null)
                return Result(command, CommandResult.Rejected,
                    "rewardUnresolved");
            return SoloRestart(command);
        }

        // Continue shares the unresolved-reward gate: retiring the
        // finished session would strand the retrySave affordance this
        // overlay hosts, so the rejection fires before any teardown.
        private CommandResult ResultsContinue(GameCommand command)
        {
            if (soloRace == null || soloRace.Result == null)
                return Result(command, CommandResult.Rejected,
                    "unavailable");
            if (unresolvedResult != null)
                return Result(command, CommandResult.Rejected,
                    "rewardUnresolved");
            var mode = soloRace.Mode;
            EndSoloRace("raceExit", command);
            // EndSoloRace lands on practice; continue returns to the
            // mode's own setup surface so the loop reads as a loop.
            if (screen == ScreenPractice)
                screen = LoopReturnScreen(mode);
            return Result(command, CommandResult.Applied, "");
        }

        // --- progression observation slice (schema 6) ---------------------

        // The resolved campaign/bounty/reward-gate view rebuilt from the
        // live profile + bound catalog on every snapshot. Reads the same
        // CampaignRules seam the commands enforce — the published access
        // rows can never disagree with campaign.start.
        private ProgressionState BuildProgressionState()
        {
            var state = new ProgressionState();
            state.rewardUnresolved = unresolvedResult != null;
            state.rewardUnresolvedRaceId = unresolvedResult != null
                ? unresolvedResult.raceId ?? "" : "";
            if (gameplayCatalog == null || gameplayCatalog.IsLegacy
                || gameplayCatalog.Driving == null)
                return state;
            state.available = true;
            state.contentIdentity =
                gameplayCatalog.CompatibilityVersion ?? "none";
            var campaign = gameplayCatalog.Campaign;
            if (campaign != null)
            {
                state.campaignWorldId = campaign.worldId ?? "";
                var access = CampaignRules.Evaluate(campaign,
                    savedProfile);
                state.campaignCompletedEvents = access.completedEvents;
                state.campaignStars = access.regionalStars;
                state.campaignRequiredEvents =
                    access.requiredEventCompletions;
                state.campaignRequiredStars = access.requiredRegionalStars;
                state.campaignFinalesOpen = access.finalesAvailable;
                state.campaignWorldComplete = access.worldComplete;
                var available = new HashSet<string>(
                    access.availableEventIds, StringComparer.Ordinal);
                if (campaign.regularEventIds != null)
                    foreach (var eventId in campaign.regularEventIds)
                        state.campaignEvents.Add(CampaignEventRow(
                            eventId, null, available));
                if (campaign.finales != null)
                    foreach (var finale in campaign.finales)
                        if (finale != null)
                            state.campaignEvents.Add(CampaignEventRow(
                                finale.eventId, finale, available));
            }
            if (savedProfile != null && savedProfile.activeBounties != null)
                foreach (var assignment in savedProfile.activeBounties)
                    state.bounties.Add(BountyRow(assignment));
            return state;
        }

        // One authored campaign event resolved against the profile's
        // durable rows — the same mask union CampaignRules counts so a
        // corrupt partial mask can never count phantom stars.
        private CampaignEventState CampaignEventRow(string eventId,
            FinaleDefinition finale, HashSet<string> available)
        {
            var row = new CampaignEventState
            {
                eventId = eventId ?? "",
                finale = finale != null,
                rivalId = finale != null ? finale.rivalId ?? "" : "",
                rewardVehicleId = finale != null
                    ? finale.rewardVehicleId ?? "" : ""
            };
            EventDefinition evt;
            if (gameplayCatalog.Driving.Events.TryGetValue(
                    row.eventId, out evt) && evt != null)
            {
                row.courseId = evt.courseId ?? "";
                row.classId = evt.vehicleClass ?? "";
                row.eventType = evt.eventType ?? "";
            }
            if (savedProfile != null && savedProfile.eventProgress != null)
                foreach (var progress in savedProfile.eventProgress)
                    if (progress != null && progress.eventId == row.eventId)
                    {
                        // The same union rule as CampaignRules: the
                        // completed flag counts as the completion bit,
                        // and star bits only count on completed events.
                        var mask = progress.objectiveMask
                            & ObjectiveEvaluator.FullMask;
                        if (progress.completed)
                            mask |= ObjectiveEvaluator.CompletionBit;
                        row.objectiveMask = mask;
                        row.completed =
                            (mask & ObjectiveEvaluator.CompletionBit) != 0;
                        var stars = 0;
                        var bits = row.completed ? mask : 0;
                        while (bits != 0) { stars += bits & 1; bits >>= 1; }
                        row.stars = stars;
                        break;
                    }
            row.locked = !available.Contains(row.eventId);
            row.lockReason = row.locked ? "eventLocked" : "";
            return row;
        }

        // One active assignment resolved against the bound catalog — an
        // assignment whose definition was removed stays on the board
        // with resolved=false rather than silently disappearing.
        private BountyState BountyRow(BountyAssignment assignment)
        {
            var row = new BountyState();
            if (assignment == null)
            {
                row.resolved = false;
                return row;
            }
            row.assignmentId = assignment.assignmentId ?? "";
            row.bountyId = assignment.bountyId ?? "";
            row.progress = assignment.progress;
            var definition = FindBounty(assignment.bountyId);
            row.resolved = definition != null;
            if (definition != null)
            {
                row.family = definition.family ?? "";
                row.metric = definition.metric ?? "";
                row.threshold = definition.threshold;
                row.rewardCoins = definition.rewardCoins;
                row.unit = definition.unit ?? "";
                row.completed = assignment.progress >= definition.threshold;
            }
            return row;
        }
    }
}
