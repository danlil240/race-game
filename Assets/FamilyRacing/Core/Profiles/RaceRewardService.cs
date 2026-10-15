using System;
using System.Collections.Generic;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Profiles
{
    // Terminal outcome of attempting to apply one immutable finalized
    // result to the local profile. `awardedCoins` echoes the authored
    // reward the attempt recognized — including on alreadyApplied, where
    // no coins moved but the original award is reported.
    //
    // M5 Task 7: the result also carries the persisted award breakdown
    // (completion / placement / challenge / bounty / rival coins), the
    // mode the verdict was evaluated under, and the progression facts the
    // transaction committed (new star bits, finale/world facts, unlocks,
    // rival reward identity, bound-bounty divergence and the time-trial
    // record verdict). On alreadyApplied the breakdown echoes from the
    // stored receipt — a redelivery reports what persisted, never a
    // re-evaluation.
    public sealed class RewardApplyResult
    {
        public string status = "";
        public string code = "";
        public int awardedCoins;
        public string fingerprint = "";
        // True when this verdict's facts are durably committed in the
        // profile — every applied/alreadyApplied award AND a committed
        // non-award adjudication such as a bound DNF (receipt + context
        // retirement landed, status stays ineligible). Cache refreshers
        // key on this, never on status alone: a stale slot cache would
        // resurrect a consumed context and drop the receipt on reselect.
        public bool committed;

        // The mode vocabulary id the verdict ran under ("campaign" |
        // "quickRace" | "lan" | "timeTrial"); "" for the legacy path.
        public string mode = "";
        // Persisted coin breakdown — sums to awardedCoins on a commit.
        public int completionCoins;
        public int placementCoins;
        public int challengeCoins;
        public int bountyCoins;
        public int rivalCoins;
        // Newly earned objective bits this transaction added to the
        // stored event mask (re-earned bits are not "new").
        public int newStarBits;
        // Finale/world facts committed by this transaction.
        public bool finaleCompleted;
        public bool finaleVictorious;
        public bool rivalDefeated;
        public string rivalCosmeticId = "";
        public List<string> rivalBountyIds = new List<string>();
        public bool worldCompleted;
        public List<string> unlockedVehicleIds = new List<string>();
        public List<string> completedBountyIds = new List<string>();
        // Bound assignment ids whose live slot no longer held the bound
        // bounty — their facts were skipped, never folded elsewhere.
        public List<string> divergedAssignmentIds = new List<string>();
        // Time-trial record verdict: whether a new personal best was
        // committed and the standing best ticks afterwards.
        public bool recordImproved;
        public long recordBestTicks;
        public string recordCourseId = "";
        public string recordClassId = "";

        public static RewardApplyResult Applied(int coins, string fingerprint)
        {
            return new RewardApplyResult
            {
                status = RaceRewardService.StatusApplied,
                awardedCoins = coins, fingerprint = fingerprint ?? "",
                committed = true
            };
        }

        public static RewardApplyResult AlreadyApplied(int coins, string fingerprint)
        {
            return new RewardApplyResult
            {
                status = RaceRewardService.StatusAlreadyApplied,
                awardedCoins = coins, fingerprint = fingerprint ?? "",
                committed = true
            };
        }

        public static RewardApplyResult Failed(string code, string fingerprint = "")
        {
            return new RewardApplyResult
            {
                status = RaceRewardService.StatusFailed,
                code = code ?? "", fingerprint = fingerprint ?? ""
            };
        }

        public static RewardApplyResult Ineligible(string code, string fingerprint = "")
        {
            return new RewardApplyResult
            {
                status = RaceRewardService.StatusIneligible,
                code = code ?? "", fingerprint = fingerprint ?? ""
            };
        }
    }

    // Exactly-once application of finalized race results to the selected
    // profile. Two deliberately different lanes share one entry surface:
    //
    //   - Apply(result, pid, profile, store) is the LEGACY
    //     completion-only lane (M3): authored completion coins for a
    //     finished row — the shape pre-Task-7 LAN results still use.
    //
    //   - Apply(result, pid, profile, store, catalog, context) is the
    //     Task-7 atomic progression transaction: coins + campaign
    //     stars + bound bounties + rival/finale/world facts + unlocks +
    //     time-trial records + the receipt + consumption of the durable
    //     race-start reward context — all inside ONE ProfileStore.Save.
    //
    // Persistence is all-or-nothing in both lanes:
    //   - a failed save leaves memory and disk untouched
    //   - a replay of the identical result returns alreadyApplied
    //   - a replay of the same resultId with conflicting immutable facts
    //     is rejected by fingerprint comparison — durable across restarts
    //   - the local profile identity binds the award: a result meant for
    //     another participant can never be claimed here
    //   - a progression result without its durable bound context is
    //     refused (noContext/contextMismatch) — assignments are never
    //     guessed from whatever happens to be active on arrival
    public static class RaceRewardService
    {
        public const string StatusApplied = "applied";
        public const string StatusAlreadyApplied = "alreadyApplied";
        public const string StatusFailed = "failed";
        public const string StatusIneligible = "ineligible";

        // --- the legacy completion-only lane (pre-Task-7 results) ------

        public static RewardApplyResult Apply(RaceResult result,
            string participantId, ProfileSave savedProfile, ProfileStore store)
        {
            var fingerprint = result != null ? result.Fingerprint() : "";
            if (result == null || !result.finalized)
                return RewardApplyResult.Ineligible("noResult", fingerprint);
            if (savedProfile == null || store == null)
                return RewardApplyResult.Ineligible("noProfile", fingerprint);
            // The reward is bound to the intended local profile: only the
            // result row that names this profile's identity can pay out.
            if (string.IsNullOrEmpty(participantId)
                || participantId != savedProfile.id)
                return RewardApplyResult.Ineligible("foreignProfile", fingerprint);
            RaceResultEntry entry = null;
            if (result.racers != null)
                foreach (var racer in result.racers)
                    if (racer != null && racer.participantId == participantId)
                        entry = racer;
            if (entry == null)
                return RewardApplyResult.Ineligible("notInResult", fingerprint);
            if (entry.outcome != "finished")
                return RewardApplyResult.Ineligible("dnf", fingerprint);

            var receipt = FindReceipt(savedProfile, result.resultId);
            if (receipt != null)
            {
                if (receipt.fingerprint == fingerprint)
                    return RewardApplyResult.AlreadyApplied(
                        receipt.awardedCoins, fingerprint);
                // Same result id, different immutable facts: the stored
                // fingerprint is the proof — never pay or overwrite.
                return RewardApplyResult.Failed("conflict", fingerprint);
            }

            // The authored completion reward comes solely from finalized
            // host facts — never from client-supplied claims.
            var awarded = result.completionCoins;
            if (awarded < 0)
                return RewardApplyResult.Ineligible("invalidResult", fingerprint);
            if ((long)savedProfile.coins + awarded > int.MaxValue)
                return RewardApplyResult.Failed("overflow", fingerprint);

            // Mutate a candidate copy: the live profile and the disk both
            // stay exactly as they were unless the atomic write commits.
            var candidate = ProfileCopy.DeepCopy(savedProfile);
            candidate.coins += awarded;
            candidate.appliedResults.Add(new AppliedResultReceipt
            {
                resultId = result.resultId,
                participantId = participantId,
                awardedCoins = awarded,
                fingerprint = fingerprint
            });
            try
            {
                store.Save(candidate);
            }
            catch (Exception)
            {
                return RewardApplyResult.Failed("saveFailed", fingerprint);
            }
            // Commit to the live profile only after the write lands so a
            // retry sees the pre-award state the disk actually holds.
            savedProfile.coins = candidate.coins;
            savedProfile.appliedResults = candidate.appliedResults;
            return RewardApplyResult.Applied(awarded, fingerprint);
        }

        // --- the Task-7 atomic progression transaction -----------------

        // One finalized result against the reward context durably bound
        // at race start. `context` is the caller's copy of the bound row
        // (the LAN preparer hands it through); the row persisted inside
        // the profile is the authority — a supplied context that was
        // never durably bound, or that disagrees with the stored row, is
        // a mismatch, never a substitute.
        public static RewardApplyResult Apply(RaceResult result,
            string participantId, ProfileSave savedProfile,
            ProfileStore store, GameplayCatalog catalog,
            RaceRewardContext context)
        {
            var fingerprint = result != null ? result.Fingerprint() : "";
            if (result == null || !result.finalized)
                return RewardApplyResult.Ineligible("noResult", fingerprint);
            if (savedProfile == null || store == null)
                return RewardApplyResult.Ineligible("noProfile", fingerprint);
            if (string.IsNullOrEmpty(participantId)
                || participantId != savedProfile.id)
                return RewardApplyResult.Ineligible("foreignProfile", fingerprint);
            var entry = FindEntry(result, participantId);
            if (entry == null)
                return RewardApplyResult.Ineligible("notInResult", fingerprint);

            // Receipt dedup precedes every context rule: a redelivery of
            // an already-committed transaction must answer alreadyApplied
            // even though its context row was consumed by that commit.
            var receipt = FindReceipt(savedProfile, result.resultId);
            if (receipt != null)
            {
                if (receipt.fingerprint == fingerprint)
                    return EchoReceipt(receipt, fingerprint);
                return RewardApplyResult.Failed("conflict", fingerprint);
            }

            var bound = FindContext(savedProfile, result.raceId);
            if (context != null)
            {
                // A supplied context is only trusted when it IS the
                // stored row's identity: same race, owner, mode and
                // rules. Anything else is a conflicting claim.
                if (bound == null
                    || context.raceId != bound.raceId
                    || (!string.IsNullOrEmpty(context.profileId)
                        && context.profileId != bound.profileId)
                    || (!string.IsNullOrEmpty(context.mode)
                        && context.mode != bound.mode)
                    || (!string.IsNullOrEmpty(context.rulesFingerprint)
                        && context.rulesFingerprint
                            != bound.rulesFingerprint))
                    return RewardApplyResult.Ineligible(
                        "contextMismatch", fingerprint);
            }
            if (bound != null)
            {
                // The bound row owns the race's rules identity; a result
                // minted under a different mode or rules edition can
                // never fold into this context.
                if (!string.IsNullOrEmpty(result.mode)
                    && !string.IsNullOrEmpty(bound.mode)
                    && result.mode != bound.mode)
                    return RewardApplyResult.Ineligible(
                        "contextMismatch", fingerprint);
                if (!string.IsNullOrEmpty(result.rulesFingerprint)
                    && !string.IsNullOrEmpty(bound.rulesFingerprint)
                    && result.rulesFingerprint != bound.rulesFingerprint)
                    return RewardApplyResult.Ineligible(
                        "rulesMismatch", fingerprint);
            }

            // Mode resolution: the format-2 result's own identity wins;
            // legacy format-1 rows (LAN) take the bound context's mode.
            // A result with NEITHER is a completion-only legacy result
            // and runs the explicitly limited legacy lane.
            var modeId = !string.IsNullOrEmpty(result.mode)
                ? result.mode
                : (bound != null ? bound.mode : "");
            if (modeId == "")
                return Apply(result, participantId, savedProfile, store);
            RaceMode mode;
            if (!RaceModeRules.TryParse(modeId, out mode))
                return RewardApplyResult.Ineligible(
                    "invalidMode", fingerprint);
            if (mode == RaceMode.TimeTrial)
                return ApplyTrial(result, participantId, savedProfile,
                    store, fingerprint);
            if (!RaceModeRules.IsProgression(mode))
                return RewardApplyResult.Ineligible(
                    "modeNotProgression", fingerprint);
            if (bound == null)
                // A progression-mode result must always find the context
                // its race was bound under — never evaluate against
                // whatever assignments happen to be active now.
                return RewardApplyResult.Ineligible(
                    "noContext", fingerprint);
            if (entry.outcome != "finished")
                return ApplyBoundNonFinish(result, participantId,
                    savedProfile, store, bound, modeId, fingerprint);
            return ApplyProgression(result, participantId, savedProfile,
                store, catalog, bound, modeId, mode, fingerprint);
        }

        // A bound progression race whose local row did not finish still
        // closes the transaction durably: the context retires and a
        // zero-award receipt lands in one save so a redelivery dedupes
        // instead of re-opening adjudication. No coins, no facts.
        private static RewardApplyResult ApplyBoundNonFinish(
            RaceResult result, string participantId,
            ProfileSave savedProfile, ProfileStore store,
            RaceRewardContext bound, string modeId, string fingerprint)
        {
            var candidate = ProfileCopy.DeepCopy(savedProfile);
            RemoveContext(candidate, bound.raceId);
            candidate.appliedResults.Add(new AppliedResultReceipt
            {
                resultId = result.resultId,
                participantId = participantId,
                awardedCoins = 0,
                fingerprint = fingerprint,
                rulesFingerprint = bound.rulesFingerprint ?? "",
                mode = modeId
            });
            try
            {
                store.Save(candidate);
            }
            catch (Exception)
            {
                return RewardApplyResult.Failed("saveFailed", fingerprint);
            }
            ProfileCopy.CopyProgression(candidate, savedProfile);
            var verdict = RewardApplyResult.Ineligible("dnf", fingerprint);
            verdict.mode = modeId;
            // The zero-award adjudication COMMITTED — receipt and context
            // retirement are durable even though nothing was earned.
            verdict.committed = true;
            return verdict;
        }

        // Time Trial: zero coins, no campaign facts, no bound context —
        // only the grouped personal-best row plus the receipt commit
        // atomically. Equal/slower runs still commit their receipt so a
        // redelivery dedupes; they just never replace the best.
        private static RewardApplyResult ApplyTrial(RaceResult result,
            string participantId, ProfileSave savedProfile,
            ProfileStore store, string fingerprint)
        {
            var update = TimeTrialRecordRules.Evaluate(result,
                participantId, savedProfile);
            if (!update.eligible)
                return RewardApplyResult.Ineligible(
                    update.reason, fingerprint);

            var candidate = ProfileCopy.DeepCopy(savedProfile);
            var recordCount = 0;
            if (update.isBest)
            {
                var replaced = false;
                for (var i = 0; i < candidate.timeTrialRecords.Count; i++)
                {
                    var row = candidate.timeTrialRecords[i];
                    if (row != null && row.courseId == update.courseId
                        && row.classId == update.classId)
                    {
                        candidate.timeTrialRecords[i] = update.record;
                        replaced = true;
                        break;
                    }
                }
                if (!replaced)
                    candidate.timeTrialRecords.Add(update.record);
                recordCount = 1;
            }
            var standingBest = update.isBest
                ? update.elapsedTicks : update.previousBestTicks;
            candidate.appliedResults.Add(new AppliedResultReceipt
            {
                resultId = result.resultId,
                participantId = participantId,
                awardedCoins = 0,
                fingerprint = fingerprint,
                rulesFingerprint = result.rulesFingerprint ?? "",
                mode = RaceModeRules.ModeId(RaceMode.TimeTrial),
                recordImproved = update.isBest,
                recordBestTicks = standingBest,
                updatedTimeTrialRecordCount = recordCount
            });
            try
            {
                store.Save(candidate);
            }
            catch (Exception)
            {
                return RewardApplyResult.Failed("saveFailed", fingerprint);
            }
            ProfileCopy.CopyProgression(candidate, savedProfile);
            var applied = RewardApplyResult.Applied(0, fingerprint);
            applied.mode = RaceModeRules.ModeId(RaceMode.TimeTrial);
            applied.recordImproved = update.isBest;
            applied.recordBestTicks = standingBest;
            applied.recordCourseId = update.courseId;
            applied.recordClassId = update.classId;
            return applied;
        }

        // The full progression commit: every amount comes from authored
        // validated content facts (event completion reward, economy
        // placement/challenge/rival tables, bound bounty definitions) —
        // never from a client-claimed coinsAwarded. One candidate copy
        // carries ALL of it: coins, event mask, finale/world rows,
        // unlocks, cosmetic, bounty tray + cursor, receipt, and the
        // removal of the durable context — so the write either commits
        // the whole transaction or leaves everything untouched.
        private static RewardApplyResult ApplyProgression(
            RaceResult result, string participantId,
            ProfileSave savedProfile, ProfileStore store,
            GameplayCatalog catalog, RaceRewardContext bound,
            string modeId, RaceMode mode, string fingerprint)
        {
            var economy = catalog != null ? catalog.Economy : null;
            var evt = FindEvent(catalog, result.eventId);

            // Authored completion reward: the catalog event's authored
            // value wins, then the economy's generic completion, then —
            // only when no catalog exists at all (a LAN endpoint that
            // never loaded gameplay content) — the finalized host-stamped
            // fact on the result itself.
            var completion = evt != null ? evt.completionCoins
                : economy != null ? economy.completionCoins
                : result.completionCoins;
            if (completion < 0)
                return RewardApplyResult.Ineligible(
                    "invalidResult", fingerprint);
            // The finalized row's position indexes the authored
            // placement table — a position beyond the table earns no
            // bonus rather than clamping onto someone else's rank.
            var position = PlacementOf(result, participantId);
            var placement = economy != null
                && economy.placementBonusCoins != null
                && position >= 1
                && position <= economy.placementBonusCoins.Count
                ? economy.placementBonusCoins[position - 1] : 0;

            var candidate = ProfileCopy.DeepCopy(savedProfile);

            // --- campaign facts --------------------------------------
            var challenge = 0;
            var newStarBits = 0;
            var finaleCompleted = false;
            var finaleVictorious = false;
            // Ownership resolves from the immutable result.eventId —
            // never the current menu/world selection (M6 Task 2): a
            // redrock finale commits against the redrock campaign.
            var campaign = catalog != null
                ? catalog.FindCampaignForEvent(result.eventId) : null;
            var isRegularEvent = campaign != null
                && campaign.regularEventIds != null
                && campaign.regularEventIds.Contains(result.eventId);
            FinaleDefinition finale = null;
            if (campaign != null && campaign.finales != null)
                foreach (var candidateFinale in campaign.finales)
                    if (candidateFinale != null
                        && candidateFinale.eventId == result.eventId)
                        finale = candidateFinale;
            if (mode == RaceMode.Campaign && isRegularEvent && evt != null)
            {
                var earned = ObjectiveEvaluator.EvaluateMask(evt, result,
                    participantId);
                var row = FindEventRow(candidate, result.eventId);
                var storedMask = row != null ? row.objectiveMask : 0;
                var merged = ObjectiveEvaluator.MergeMask(storedMask,
                    earned);
                newStarBits = merged & ~storedMask
                    & ObjectiveEvaluator.FullMask;
                if (economy != null)
                {
                    if ((newStarBits
                            & ObjectiveEvaluator.IntroductoryBit) != 0)
                        challenge += economy.challengeIntroductoryCoins;
                    if ((newStarBits
                            & ObjectiveEvaluator.MasteryBit) != 0)
                        challenge += economy.challengeMasteryCoins;
                }
                if (row == null)
                {
                    row = new EventProgress { eventId = result.eventId };
                    candidate.eventProgress.Add(row);
                }
                row.completed = true;
                row.objectiveMask = merged;
            }

            // --- bound bounty fold (tray + rival facts) ---------------
            // Evaluated against the candidate BEFORE the finale's
            // victorious bit lands: rivalRewardDue is defined on the
            // pre-result state — a one-time fact, provably never twice.
            var eval = BountyService.Evaluate(candidate, result,
                participantId, bound, catalog);
            var bountyCoins = 0;
            var rivalCoins = 0;
            var rivalCosmeticId = "";
            var rivalBountyIds = new List<string>();
            var completedBountyIds = new List<string>();
            var diverged = new List<string>();
            var rivalDefeated = false;
            if (eval.eligible)
            {
                candidate.activeBounties = eval.activeBounties;
                candidate.bountyRotationCursor = eval.bountyRotationCursor;
                bountyCoins = eval.totalRewardCoins;
                rivalDefeated = eval.rivalDefeated;
                foreach (var done in eval.completions)
                    if (done != null)
                        completedBountyIds.Add(done.bountyId);
                if (eval.divergedAssignmentIds != null)
                    diverged.AddRange(eval.divergedAssignmentIds);
                if (eval.rivalRewardDue)
                {
                    rivalCoins = eval.rivalRewardCoins;
                    rivalCosmeticId = "livery-" + eval.rivalId;
                    if (eval.rivalBountyIds != null)
                        rivalBountyIds.AddRange(eval.rivalBountyIds);
                    if (!string.IsNullOrEmpty(rivalCosmeticId)
                        && candidate.ownedCosmetics != null
                        && !candidate.ownedCosmetics
                            .Contains(rivalCosmeticId))
                        candidate.ownedCosmetics.Add(rivalCosmeticId);
                }
            }

            // --- finale + world facts ----------------------------------
            var worldCompleted = false;
            var unlockedVehicleIds = new List<string>();
            if (mode == RaceMode.Campaign && finale != null)
            {
                var frow = FindFinaleRow(candidate, result.eventId);
                var wasCompleted = frow != null && frow.completed;
                var wasVictorious = frow != null && frow.victorious;
                if (frow == null)
                {
                    frow = new FinaleProgress { eventId = result.eventId };
                    candidate.finaleProgress.Add(frow);
                }
                frow.completed = true;
                if (rivalDefeated) frow.victorious = true;
                finaleCompleted = !wasCompleted;
                finaleVictorious = !wasVictorious && frow.victorious;

                var access = CampaignRules.Evaluate(campaign, candidate);
                if (access.worldComplete
                    && !WorldMarkedComplete(candidate, campaign.worldId))
                {
                    worldCompleted = true;
                    var wrow = FindWorldRow(candidate, campaign.worldId);
                    if (wrow == null)
                    {
                        wrow = new WorldProgress
                        { worldId = campaign.worldId };
                        candidate.worldProgress.Add(wrow);
                    }
                    wrow.completed = true;
                    foreach (var vehicleId in access.worldRewardVehicleIds)
                        if (!string.IsNullOrEmpty(vehicleId)
                            && !candidate.unlockedVehicles
                                .Contains(vehicleId))
                        {
                            candidate.unlockedVehicles.Add(vehicleId);
                            unlockedVehicleIds.Add(vehicleId);
                        }
                }
            }

            var total = completion + placement + challenge
                + bountyCoins + rivalCoins;
            if ((long)candidate.coins + total > int.MaxValue)
                // The whole award refuses BEFORE any write — the context
                // stays pending and the verdict stays honestly failed.
                return RewardApplyResult.Failed("overflow", fingerprint);

            candidate.coins += total;
            RemoveContext(candidate, bound.raceId);
            candidate.appliedResults.Add(new AppliedResultReceipt
            {
                resultId = result.resultId,
                participantId = participantId,
                awardedCoins = total,
                fingerprint = fingerprint,
                // The receipt binds BOTH identities: the immutable result
                // fingerprint and the rules edition the context bound.
                rulesFingerprint = !string.IsNullOrEmpty(
                        bound.rulesFingerprint)
                    ? bound.rulesFingerprint
                    : result.rulesFingerprint ?? "",
                mode = modeId,
                completionCoins = completion,
                placementCoins = placement,
                challengeCoins = challenge,
                bountyCoins = bountyCoins,
                rivalCoins = rivalCoins,
                completedBountyIds = completedBountyIds,
                updatedVehicleCount = unlockedVehicleIds.Count
            });
            try
            {
                store.Save(candidate);
            }
            catch (Exception)
            {
                return RewardApplyResult.Failed("saveFailed", fingerprint);
            }
            // Publish only after the write lands — one progression slice
            // carries every committed fact onto the live profile.
            ProfileCopy.CopyProgression(candidate, savedProfile);

            var applied = RewardApplyResult.Applied(total, fingerprint);
            applied.mode = modeId;
            applied.completionCoins = completion;
            applied.placementCoins = placement;
            applied.challengeCoins = challenge;
            applied.bountyCoins = bountyCoins;
            applied.rivalCoins = rivalCoins;
            applied.newStarBits = newStarBits;
            applied.finaleCompleted = finaleCompleted;
            applied.finaleVictorious = finaleVictorious;
            applied.rivalDefeated = rivalDefeated;
            applied.rivalCosmeticId = rivalCosmeticId;
            applied.rivalBountyIds = rivalBountyIds;
            applied.worldCompleted = worldCompleted;
            applied.unlockedVehicleIds = unlockedVehicleIds;
            applied.completedBountyIds = completedBountyIds;
            applied.divergedAssignmentIds = diverged;
            return applied;
        }

        // --- shared internals ------------------------------------------

        private static RaceResultEntry FindEntry(RaceResult result,
            string participantId)
        {
            if (result.racers == null) return null;
            RaceResultEntry entry = null;
            foreach (var racer in result.racers)
                if (racer != null && racer.participantId == participantId)
                    entry = racer;
            return entry;
        }

        private static int PlacementOf(RaceResult result,
            string participantId)
        {
            var entry = FindEntry(result, participantId);
            return entry != null ? entry.position : 0;
        }

        private static AppliedResultReceipt FindReceipt(ProfileSave profile,
            string resultId)
        {
            if (profile.appliedResults == null || resultId == null) return null;
            foreach (var receipt in profile.appliedResults)
                if (receipt != null && receipt.resultId == resultId)
                    return receipt;
            return null;
        }

        private static RaceRewardContext FindContext(ProfileSave profile,
            string raceId)
        {
            if (profile.pendingRaceContexts == null || raceId == null)
                return null;
            foreach (var row in profile.pendingRaceContexts)
                if (row != null && row.raceId == raceId) return row;
            return null;
        }

        private static void RemoveContext(ProfileSave profile, string raceId)
        {
            if (profile.pendingRaceContexts == null || raceId == null)
                return;
            profile.pendingRaceContexts.RemoveAll(
                row => row != null && row.raceId == raceId);
        }

        private static EventDefinition FindEvent(GameplayCatalog catalog,
            string eventId)
        {
            if (catalog == null || catalog.IsLegacy
                || catalog.Driving == null
                || catalog.Driving.Events == null || eventId == null)
                return null;
            EventDefinition evt;
            return catalog.Driving.Events.TryGetValue(eventId, out evt)
                ? evt : null;
        }

        private static EventProgress FindEventRow(ProfileSave profile,
            string eventId)
        {
            if (profile.eventProgress == null) return null;
            foreach (var row in profile.eventProgress)
                if (row != null && row.eventId == eventId) return row;
            return null;
        }

        private static FinaleProgress FindFinaleRow(ProfileSave profile,
            string eventId)
        {
            if (profile.finaleProgress == null) return null;
            foreach (var row in profile.finaleProgress)
                if (row != null && row.eventId == eventId) return row;
            return null;
        }

        private static WorldProgress FindWorldRow(ProfileSave profile,
            string worldId)
        {
            if (profile.worldProgress == null) return null;
            foreach (var row in profile.worldProgress)
                if (row != null && row.worldId == worldId) return row;
            return null;
        }

        private static bool WorldMarkedComplete(ProfileSave profile,
            string worldId)
        {
            var row = FindWorldRow(profile, worldId);
            return row != null && row.completed;
        }

        // A redelivery echoes the persisted breakdown — the receipt IS
        // the record of what committed, so replay reporting can never
        // drift from the facts that were actually saved.
        private static RewardApplyResult EchoReceipt(
            AppliedResultReceipt receipt, string fingerprint)
        {
            var echo = RewardApplyResult.AlreadyApplied(
                receipt.awardedCoins, fingerprint);
            echo.mode = receipt.mode ?? "";
            echo.completionCoins = receipt.completionCoins;
            echo.placementCoins = receipt.placementCoins;
            echo.challengeCoins = receipt.challengeCoins;
            echo.bountyCoins = receipt.bountyCoins;
            echo.rivalCoins = receipt.rivalCoins;
            echo.recordImproved = receipt.recordImproved;
            echo.recordBestTicks = receipt.recordBestTicks;
            if (receipt.completedBountyIds != null)
                echo.completedBountyIds =
                    new List<string>(receipt.completedBountyIds);
            return echo;
        }

        // Candidate copies come from ProfileCopy.DeepCopy — the single
        // lossless copy, so every progression collection rides along and
        // no draft of the award shares mutable rows with the live profile.
    }
}
