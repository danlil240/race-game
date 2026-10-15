using System;
using System.Collections.Generic;
using FamilyRacing.Core.Networking;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Progression
{
    // One bound assignment that reached its threshold during this
    // evaluation: which assignment/bounty completed, its authored coin
    // reward, and the rotated-in replacement row (null when the
    // repeatable catalog cannot supply a distinct pick).
    public sealed class BountyCompletion
    {
        public string assignmentId = "";
        public string bountyId = "";
        public int rewardCoins;
        public BountyAssignment replacement;
    }

    // The proposed outcome of folding one finalized result into the
    // bounty tray (M5 Task 6). Pure evaluation: `activeBounties` and
    // `bountyRotationCursor` are the proposed post-award rows/cursor as
    // NEW objects — the source profile and result are never mutated and
    // nothing is saved here. `completions` records repeatable-catalog
    // payouts for the award receipt; the rival-victory fields carry the
    // one-time named-rival facts that live outside the rotation cursor.
    public sealed class BountyEvaluation
    {
        // Facts were eligible to feed the bound assignments. False with
        // `reason` set for no result, unfinalized, missing/mismatched
        // context, non-progression mode, absent participant, or DNF —
        // the proposal then mirrors the current tray unchanged.
        public bool eligible;
        public string reason = "";
        public string mode = "";
        public string raceId = "";
        public List<BountyAssignment> activeBounties =
            new List<BountyAssignment>();
        public int bountyRotationCursor;
        public List<BountyCompletion> completions =
            new List<BountyCompletion>();
        // Sum of the repeatable completions' authored coin rewards.
        public int totalRewardCoins;

        // The finalized result's rival-defeat fact (finished ahead of
        // the authored rival) — reported whenever it appears on the
        // human row, regardless of mode.
        public bool rivalDefeated;
        // A NEW solo victory: due only when the defeat landed in a
        // campaign result and the profile's finale row is not already
        // victorious. Reward identity + authored amount follow.
        public bool rivalRewardDue;
        public string rivalId = "";
        public string rivalEventId = "";
        public List<string> rivalBountyIds = new List<string>();
        public int rivalRewardCoins;

        // Bound assignment ids whose profile slot no longer holds the
        // bounty id bound at race start — corrupt or reassigned state.
        // Such slots are skipped (their facts have no honest landing
        // place) and recorded here for diagnostics; they can neither
        // accumulate mismatched facts nor pay a mismatched reward.
        public List<string> divergedAssignmentIds =
            new List<string>();
    }

    // Folds finalized human-attributed metrics into the three challenge
    // assignments the race was started under — the parallel lists bound
    // inside the durable RaceRewardContext (Task 4), never whatever
    // happens to be active when a delayed result arrives. A completed
    // assignment rotates immediately: the replacement comes from the
    // fixed offline repeatable catalog at the deterministic cursor,
    // starts at zero progress, and receives NO facts from the result
    // that completed its predecessor. Named-rival bounties are one-time
    // facts outside the repeatable cursor.
    public static class BountyService
    {
        public static BountyEvaluation Evaluate(ProfileSave profile,
            RaceResult result, string participantId,
            RaceRewardContext context, GameplayCatalog catalog)
        {
            var eval = new BountyEvaluation();
            eval.raceId = result != null ? result.raceId : "";
            // The proposal always mirrors the current tray as fresh
            // rows — an ineligible evaluation is a no-op proposal the
            // award path can still apply blindly.
            if (profile != null && profile.activeBounties != null)
                foreach (var row in profile.activeBounties)
                    eval.activeBounties.Add(row != null ? row.Copy() : null);
            eval.bountyRotationCursor =
                profile != null ? profile.bountyRotationCursor : 0;

            if (result == null || !result.finalized)
            { eval.reason = "noResult"; return eval; }
            if (profile == null)
            { eval.reason = "noProfile"; return eval; }
            if (context == null)
            { eval.reason = "noContext"; return eval; }
            // A context bound to a different race or profile is a
            // conflict, never a source of facts for this result.
            if (!string.IsNullOrEmpty(context.raceId)
                && !string.IsNullOrEmpty(result.raceId)
                && context.raceId != result.raceId)
            { eval.reason = "contextMismatch"; return eval; }
            if (!string.IsNullOrEmpty(context.profileId)
                && !string.IsNullOrEmpty(profile.id)
                && context.profileId != profile.id)
            { eval.reason = "contextMismatch"; return eval; }

            eval.mode = !string.IsNullOrEmpty(context.mode)
                ? context.mode : result.mode ?? "";
            RaceMode mode;
            if (!RaceModeRules.TryParse(eval.mode, out mode)
                || !RaceModeRules.IsProgression(mode))
            { eval.reason = "modeNotProgression"; return eval; }
            // Campaign/Quick/LAN human facts qualify; time trial,
            // practice and tutorial modes bind no context and carry no
            // progression mode id, so they can never reach the fold.

            RaceResultEntry entry = null;
            if (result.racers != null)
                foreach (var racer in result.racers)
                    if (racer != null
                        && racer.participantId == participantId)
                    { entry = racer; break; }
            if (entry == null)
            { eval.reason = "notInResult"; return eval; }
            // A finished race is required for any fact to land: DNF
            // grants nothing (and aborted races never produce a result
            // at all).
            if (entry.outcome != "finished")
            { eval.reason = "dnf"; return eval; }
            eval.eligible = true;
            eval.reason = "ok";

            FoldBoundAssignments(eval, profile, result, context, entry,
                catalog);
            EvaluateRivalVictory(eval, profile, result, entry, mode,
                catalog);
            return eval;
        }

        // Feeds this result's facts to exactly the assignments bound at
        // race start. Each bound id resolves to the profile row it still
        // occupies; a bound row that has since retired has nowhere to
        // land and gets nothing, and an active-but-unbound row is never
        // fed. Bound rows are visited in context order so multiple
        // completions rotate deterministically off the shared cursor.
        private static void FoldBoundAssignments(BountyEvaluation eval,
            ProfileSave profile, RaceResult result,
            RaceRewardContext context, RaceResultEntry entry,
            GameplayCatalog catalog)
        {
            var boundCount = context.bountyAssignmentIds == null ? 0
                : context.bountyAssignmentIds.Count;
            var defCount = context.bountyDefinitions == null ? 0
                : context.bountyDefinitions.Count;
            if (defCount < boundCount) boundCount = defCount;
            if (boundCount == 0) return;

            // assignmentId -> slot inside the proposed tray (first row
            // wins on duplicates; profile validation forbids them).
            var slotByAssignment =
                new Dictionary<string, int>(StringComparer.Ordinal);
            var compacted = false;
            for (var i = 0; i < eval.activeBounties.Count; i++)
            {
                var row = eval.activeBounties[i];
                if (row != null && !string.IsNullOrEmpty(row.assignmentId)
                    && !slotByAssignment.ContainsKey(row.assignmentId))
                    slotByAssignment[row.assignmentId] = i;
            }

            for (var i = 0; i < boundCount; i++)
            {
                var assignmentId = context.bountyAssignmentIds[i];
                var definition = context.bountyDefinitions[i];
                if (string.IsNullOrEmpty(assignmentId)
                    || definition == null) continue;
                int slot;
                if (!slotByAssignment.TryGetValue(assignmentId, out slot))
                    continue; // bound assignment already retired
                var row = eval.activeBounties[slot];
                // The slot must still hold the bounty bound at race
                // start — a reassigned or corrupt row would fold this
                // definition's metric facts onto a different bounty and
                // pay a mismatched reward. Skip it and record the
                // divergence instead.
                if (row == null || row.bountyId != definition.id)
                { eval.divergedAssignmentIds.Add(assignmentId); continue; }
                var amount = InDefinitionUnits(definition,
                    MetricAmount(definition, entry));
                // A corrupt row must never mint negative progress.
                if (amount < 0) amount = 0;
                row.progress = definition.aggregation
                        == BountyAggregation.Max
                    ? Math.Max(row.progress, amount)
                    : row.progress + amount;
                if (row.progress < definition.threshold) continue;

                var completion = new BountyCompletion
                {
                    assignmentId = assignmentId,
                    bountyId = row.bountyId,
                    rewardCoins = definition.rewardCoins
                };
                eval.completions.Add(completion);
                eval.totalRewardCoins += definition.rewardCoins;

                var replacement = Rotate(eval, catalog, row.bountyId,
                    result.raceId, slot);
                completion.replacement = replacement;
                eval.activeBounties[slot] = replacement;
                compacted |= replacement == null;
            }
            // A catalog that cannot supply a distinct replacement leaves
            // the slot empty rather than emitting a null row (profile
            // validation rejects null rows); untouched rows keep their
            // slots, so compact only what rotation produced.
            if (compacted)
                eval.activeBounties.RemoveAll(row => row == null);
        }

        // Deterministic rotation over the fixed offline catalog's
        // repeatable list in authored order: scan forward from the
        // cursor for the first bounty that is neither the completed id
        // nor currently active; if none exists, fall back to the first
        // not-currently-active repeatable (which may re-offer the just
        // completed id — the only way to keep three distinct slots
        // filled when the catalog is exhausted). The cursor stores the
        // index AFTER the pick so repeated races walk the catalog.
        private static BountyAssignment Rotate(BountyEvaluation eval,
            GameplayCatalog catalog, string completedBountyId,
            string raceId, int slot)
        {
            var repeatable = RepeatableCatalog(catalog);
            if (repeatable.Count == 0) return null;
            var active = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in eval.activeBounties)
                if (row != null && !string.IsNullOrEmpty(row.bountyId))
                    active.Add(row.bountyId);
            // The completing row is leaving — its id is only barred from
            // the first pass, not the "keep three filled" fallback.
            active.Remove(completedBountyId);

            var start = eval.bountyRotationCursor < 0
                ? 0
                : eval.bountyRotationCursor % repeatable.Count;
            var picked = -1;
            for (var k = 0; k < repeatable.Count && picked < 0; k++)
            {
                var index = (start + k) % repeatable.Count;
                var candidate = repeatable[index];
                if (candidate.id == completedBountyId
                    || active.Contains(candidate.id)) continue;
                picked = index;
            }
            for (var k = 0; k < repeatable.Count && picked < 0; k++)
            {
                var index = (start + k) % repeatable.Count;
                if (!active.Contains(repeatable[index].id)) picked = index;
            }
            if (picked < 0) return null;
            eval.bountyRotationCursor = (picked + 1) % repeatable.Count;
            // Replacement identities are minted deterministically from
            // the result's race identity + tray slot: re-evaluating the
            // same result proposes the identical row, so a redelivery
            // can never mint a divergent assignment.
            return new BountyAssignment
            {
                assignmentId = "asg-" + (raceId ?? "") + "-" +
                    slot.ToString(System.Globalization.CultureInfo
                        .InvariantCulture),
                bountyId = repeatable[picked].id,
                progress = 0
            };
        }

        private static List<BountyDefinition> RepeatableCatalog(
            GameplayCatalog catalog)
        {
            var list = new List<BountyDefinition>();
            if (catalog != null && catalog.Bounties != null)
                foreach (var definition in catalog.Bounties)
                    if (definition != null && definition.repeatable)
                        list.Add(definition);
            return list;
        }

        // The per-race metric amount in raw units: human-attributed
        // counters only — the accumulator already withholds AI-segment
        // credit, and humanAcceptedShortcuts counts only
        // human-attributed traversals. A null metrics row (legacy
        // format-1 result) contributes the seat-level finish fact only.
        private static long MetricAmount(BountyDefinition definition,
            RaceResultEntry entry)
        {
            var m = entry.metrics;
            switch (definition.metric)
            {
                case BountyMetric.DriftSeconds:
                    return m != null ? m.driftTicks : 0;
                case BountyMetric.HumanBoostActivations:
                    return m != null ? m.boostActivations : 0;
                case BountyMetric.CleanLandings:
                    return m != null ? m.cleanLandings : 0;
                case BountyMetric.CleanStreakSeconds:
                    return m != null ? m.longestCleanStreakTicks : 0;
                case BountyMetric.AcceptedShortcuts:
                    return m != null ? m.humanAcceptedShortcuts : 0;
                case BountyMetric.HumanFinishes:
                    // The finish itself is a seat fact: only a human-
                    // attributed finished row is a human finish — an AI
                    // takeover ending the race earns none.
                    return entry.attribution == "human"
                        && entry.outcome == "finished" ? 1 : 0;
                default:
                    return 0;
            }
        }

        // Progress persists in the definition's own unit (the schema
        // contract on BountyAssignment.progress): count metrics fold
        // directly, and the tick-counted time metrics convert to whole
        // authored units — a "seconds" bounty therefore receives
        // floor(ticks / 30) per race, never crediting a sub-unit
        // remainder it cannot represent.
        private static long InDefinitionUnits(BountyDefinition definition,
            long amount)
        {
            var tickMetric = definition.metric == BountyMetric.DriftSeconds
                || definition.metric == BountyMetric.CleanStreakSeconds;
            if (tickMetric && definition.unit == "seconds")
                return amount / NetworkSettings.SimulationTicksPerSecond;
            return amount;
        }

        // Named-rival victories are one-time story facts outside the
        // repeatable cursor: a defeated rival pays the authored rival
        // victory reward once — keyed off the durable finale row — and
        // only from a solo campaign result; shared modes (LAN/quick
        // race) can never unlock rival rewards. Losing the finale does
        // not stop advancement and proposes nothing here.
        private static void EvaluateRivalVictory(BountyEvaluation eval,
            ProfileSave profile, RaceResult result,
            RaceResultEntry entry, RaceMode mode, GameplayCatalog catalog)
        {
            if (entry.attribution != "human" || entry.metrics == null
                || !entry.metrics.rivalDefeated) return;
            eval.rivalDefeated = true;
            if (mode != RaceMode.Campaign) return;
            var finale = FindFinale(catalog, result.eventId);
            if (finale == null) return;
            eval.rivalId = finale.rivalId ?? "";
            eval.rivalEventId = finale.eventId ?? "";
            if (AlreadyVictorious(profile, finale.eventId)) return;
            eval.rivalRewardDue = true;
            eval.rivalRewardCoins = catalog != null
                && catalog.Economy != null
                    ? catalog.Economy.rivalVictoryCoins : 0;
            if (catalog != null && catalog.Bounties != null)
                foreach (var definition in catalog.Bounties)
                    if (definition != null && !definition.repeatable
                        && definition.rivalId == finale.rivalId)
                        eval.rivalBountyIds.Add(definition.id);
        }

        private static FinaleDefinition FindFinale(GameplayCatalog catalog,
            string eventId)
        {
            if (catalog == null || catalog.Campaign == null
                || catalog.Campaign.finales == null || eventId == null)
                return null;
            foreach (var finale in catalog.Campaign.finales)
                if (finale != null && finale.eventId == eventId)
                    return finale;
            return null;
        }

        private static bool AlreadyVictorious(ProfileSave profile,
            string finaleEventId)
        {
            if (profile.finaleProgress == null) return false;
            foreach (var row in profile.finaleProgress)
                if (row != null && row.eventId == finaleEventId
                    && row.victorious) return true;
            return false;
        }
    }
}
