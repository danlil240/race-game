using System.Collections.Generic;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using NUnit.Framework;

namespace FamilyRacing.Tests
{
    // M5 Task 6: bounty evaluation is a pure fold of finalized,
    // human-attributed facts onto the three assignments bound in the
    // durable RaceRewardContext. Nothing here saves or mutates the
    // profile: the evaluation only proposes the post-award bounty rows,
    // rotation cursor and earned rewards for Task 7's atomic commit.
    public sealed class BountyTests
    {
        private const string P = "profile-p";

        private static BountyDefinition Def(GameplayCatalog catalog,
            string bountyId)
        {
            foreach (var def in catalog.Bounties)
                if (def != null && def.id == bountyId) return def;
            Assert.Fail("fixture catalog lacks bounty " + bountyId);
            return null;
        }

        private static BountyAssignment Assignment(string id, string bountyId,
            long progress = 0)
        {
            return new BountyAssignment
            {
                assignmentId = id, bountyId = bountyId, progress = progress
            };
        }

        // The shape FoundationService.BindRewardContext writes at race
        // start: the active assignment ids plus their catalog
        // definitions, snapshotted in parallel.
        private static RaceRewardContext BoundContext(ProfileSave profile,
            GameplayCatalog catalog, string raceId, string mode)
        {
            var context = new RaceRewardContext
            {
                raceId = raceId,
                profileId = profile.id,
                mode = mode,
                rulesFingerprint = new string('b', 64)
            };
            foreach (var row in profile.activeBounties)
            {
                var def = Def(catalog, row.bountyId);
                context.bountyAssignmentIds.Add(row.assignmentId);
                context.bountyDefinitions.Add(def.Copy());
            }
            return context;
        }

        private static ProfileSave ProfileWithBounties(
            params BountyAssignment[] rows)
        {
            var profile = CampaignTestFixture.Profile();
            profile.id = P;
            profile.activeBounties = new List<BountyAssignment>(rows);
            return profile;
        }

        private static BountyEvaluation Eval(ProfileSave profile,
            RaceResult result, RaceRewardContext context,
            GameplayCatalog catalog)
        {
            return BountyService.Evaluate(profile, result, P, context,
                catalog);
        }

        private static BountyAssignment Row(BountyEvaluation eval,
            string assignmentId)
        {
            foreach (var row in eval.activeBounties)
                if (row != null && row.assignmentId == assignmentId)
                    return row;
            return null;
        }

        // --- metric folding ------------------------------------------------

        [Test]
        public void SumAggregationAddsWholeSecondsAcrossRaces()
        {
            var catalog = CampaignTestFixture.Catalog();
            var profile = ProfileWithBounties(
                Assignment("asg-1", "drift-10s", progress: 4));
            var context = BoundContext(profile, catalog, "race-a", "quickRace");
            // 180 ticks = 6 s of human drifting: 4 + 6 = 10 s -> the
            // authored threshold completes and rotates immediately.
            var result = CampaignTestFixture.Result("quickRace",
                "harbor-hop-car-circuit", P, "finished", 2,
                new RaceMetrics { driftTicks = 180 }, raceId: "race-a");

            var eval = Eval(profile, result, context, catalog);

            Assert.That(eval.eligible, Is.True);
            Assert.That(eval.completions, Has.Count.EqualTo(1));
            Assert.That(eval.completions[0].bountyId, Is.EqualTo("drift-10s"));
            Assert.That(eval.completions[0].rewardCoins, Is.EqualTo(10));
            Assert.That(eval.totalRewardCoins, Is.EqualTo(10));
            // The completed slot rotated to the next repeatable catalog
            // entry — drift-30s — at zero progress.
            var rotated = eval.activeBounties[0];
            Assert.That(rotated.bountyId, Is.EqualTo("drift-30s"));
            Assert.That(rotated.progress, Is.EqualTo(0));
            Assert.That(rotated.assignmentId, Is.Not.EqualTo("asg-1"),
                "the replacement is a provably new assignment identity");
            Assert.That(eval.bountyRotationCursor, Is.EqualTo(2),
                "cursor moved past the picked index");
        }

        [Test]
        public void CleanStreakKeepsBestQualifyingStreakNotSum()
        {
            var catalog = CampaignTestFixture.Catalog();
            var profile = ProfileWithBounties(
                Assignment("asg-1", "streak-30s", progress: 20));
            var context = BoundContext(profile, catalog, "race-s", "campaign");

            // A 25 s streak lifts the max to 25 — it does not sum to 45.
            var first = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-clean-driving", P, "finished", 1,
                new RaceMetrics { longestCleanStreakTicks = 25 * 30 },
                raceId: "race-s");
            var eval = Eval(profile, first, context, catalog);
            Assert.That(Row(eval, "asg-1").progress, Is.EqualTo(25),
                "max aggregation keeps the best qualifying streak");
            Assert.That(eval.completions, Is.Empty);

            // 899 ticks = 29.97 s never rounds up to the 30 s threshold.
            var justUnder = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-clean-driving", P, "finished", 1,
                new RaceMetrics { longestCleanStreakTicks = 30 * 30 - 1 },
                raceId: "race-s");
            eval = Eval(profile, justUnder, context, catalog);
            Assert.That(Row(eval, "asg-1").progress, Is.EqualTo(29));
            Assert.That(eval.completions, Is.Empty);

            // Exactly 900 ticks is a 30 s streak and completes.
            var exact = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-clean-driving", P, "finished", 1,
                new RaceMetrics { longestCleanStreakTicks = 30 * 30 },
                raceId: "race-s");
            eval = Eval(profile, exact, context, catalog);
            Assert.That(eval.completions, Has.Count.EqualTo(1));
            Assert.That(eval.completions[0].bountyId, Is.EqualTo("streak-30s"));
        }

        [Test]
        public void HumanFinishesCountFinishedHumanSeatsOnly()
        {
            var catalog = CampaignTestFixture.Catalog();
            var profile = ProfileWithBounties(
                Assignment("asg-1", "finish-2", progress: 1));
            var context = BoundContext(profile, catalog, "race-f", "lan");

            var result = CampaignTestFixture.Result("lan", "lan-mixed", P,
                "finished", 3, null, raceId: "race-f");
            var eval = Eval(profile, result, context, catalog);
            Assert.That(eval.completions, Has.Count.EqualTo(1),
                "a finished human LAN seat earns the finish fact");

            // A seat that finished under AI attribution is not a human
            // finish — segment facts never claim the seat outcome.
            var ai = CampaignTestFixture.Result("lan", "lan-mixed", P,
                "finished", 1, new RaceMetrics { driftTicks = 90 },
                attribution: "ai", raceId: "race-f");
            eval = Eval(profile, ai, context, catalog);
            Assert.That(Row(eval, "asg-1").progress, Is.EqualTo(1),
                "AI-attributed finishes earn no human finish credit");
            Assert.That(eval.completions, Is.Empty);
        }

        // --- bound-context scoping -----------------------------------------

        [Test]
        public void OnlyActiveBountiesAtRaceStartReceiveFacts()
        {
            var catalog = CampaignTestFixture.Catalog();
            // At race start the profile held asg-1/asg-2/asg-3. By the
            // time the delayed result lands, asg-1 rotated out to
            // asg-new — the result must still feed the BOUND three, and
            // the rotated-in replacement must never see these facts.
            var profile = ProfileWithBounties(
                Assignment("asg-new", "shortcut-2", progress: 0),
                Assignment("asg-2", "boost-3", progress: 1),
                Assignment("asg-3", "landing-2", progress: 0));
            var context = new RaceRewardContext
            {
                raceId = "race-late",
                profileId = P,
                mode = "campaign",
                rulesFingerprint = new string('b', 64),
                bountyAssignmentIds = new List<string>
                    { "asg-1", "asg-2", "asg-3" },
                bountyDefinitions = new List<BountyDefinition>
                {
                    Def(catalog, "drift-10s").Copy(),
                    Def(catalog, "boost-3").Copy(),
                    Def(catalog, "landing-2").Copy()
                }
            };
            var result = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-stunt", P, "finished", 2, new RaceMetrics
                {
                    boostActivations = 1, cleanLandings = 1, driftTicks = 300
                }, raceId: "race-late");

            var eval = Eval(profile, result, context, catalog);

            Assert.That(eval.eligible, Is.True);
            Assert.That(Row(eval, "asg-new").progress, Is.EqualTo(0),
                "the currently-active unbound slot receives no facts");
            Assert.That(Row(eval, "asg-2").progress, Is.EqualTo(2),
                "the bound assignment accumulates this result's facts");
            Assert.That(Row(eval, "asg-3").progress, Is.EqualTo(1));
            // asg-1 is gone from the profile: its facts have nowhere to
            // land and must not resurrect the retired assignment.
            Assert.That(Row(eval, "asg-1"), Is.Null);
            Assert.That(eval.completions, Is.Empty);
        }

        [Test]
        public void DivergedBoundSlotReceivesNoFacts()
        {
            var catalog = CampaignTestFixture.Catalog();
            // asg-1 was bound to drift-10s at race start, but its
            // profile slot now holds shortcut-2 — corrupt or reassigned
            // state. The bound drift facts must never fold onto the
            // shortcut bounty, and no mismatched reward may pay out.
            var profile = ProfileWithBounties(
                Assignment("asg-1", "shortcut-2", progress: 0),
                Assignment("asg-2", "boost-3", progress: 0));
            var context = new RaceRewardContext
            {
                raceId = "race-div",
                profileId = P,
                mode = "campaign",
                rulesFingerprint = new string('b', 64),
                bountyAssignmentIds = new List<string>
                    { "asg-1", "asg-2" },
                bountyDefinitions = new List<BountyDefinition>
                {
                    Def(catalog, "drift-10s").Copy(),
                    Def(catalog, "boost-3").Copy()
                }
            };
            // 20 s of drift would complete drift-10s outright; three
            // boosts complete boost-3 on its own honest row.
            var result = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-stunt", P, "finished", 1, new RaceMetrics
                {
                    driftTicks = 600, boostActivations = 3
                }, raceId: "race-div");

            var eval = Eval(profile, result, context, catalog);

            Assert.That(eval.eligible, Is.True);
            var diverged = Row(eval, "asg-1");
            Assert.That(diverged, Is.Not.Null,
                "the diverged slot is left in place, not destroyed");
            Assert.That(diverged.bountyId, Is.EqualTo("shortcut-2"));
            Assert.That(diverged.progress, Is.EqualTo(0),
                "facts bound to a different bounty never land here");
            Assert.That(eval.divergedAssignmentIds,
                Is.EqualTo(new[] { "asg-1" }),
                "the divergence is recorded for diagnostics");
            // The honest bound slot still folds and completes on its
            // own definition's metric and reward.
            Assert.That(eval.completions, Has.Count.EqualTo(1));
            Assert.That(eval.completions[0].bountyId,
                Is.EqualTo("boost-3"));
            Assert.That(eval.completions[0].assignmentId,
                Is.EqualTo("asg-2"));
        }

        [Test]
        public void RotationDoesNotFeedReplacementFromSameResult()
        {
            var catalog = CampaignTestFixture.Catalog();
            var profile = ProfileWithBounties(
                Assignment("asg-1", "drift-10s", progress: 9),
                Assignment("asg-2", "boost-3", progress: 0));
            var context = BoundContext(profile, catalog, "race-r",
                "campaign");
            // One more drift second completes drift-10s; the rotated-in
            // drift-30s is the same metric family — the completing
            // result must not pour its facts into the replacement.
            var result = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-circuit", P, "finished", 1,
                new RaceMetrics { driftTicks = 60 }, raceId: "race-r");

            var eval = Eval(profile, result, context, catalog);

            Assert.That(eval.completions, Has.Count.EqualTo(1));
            var replacement = eval.completions[0].replacement;
            Assert.That(replacement, Is.Not.Null);
            Assert.That(replacement.bountyId, Is.EqualTo("drift-30s"));
            Assert.That(replacement.progress, Is.EqualTo(0),
                "replacements begin at zero and receive no facts from "
                + "the result that completed their predecessor");
            Assert.That(Row(eval, replacement.assignmentId).progress,
                Is.EqualTo(0));
            // The untouched bound slot still accumulated its own facts.
            Assert.That(Row(eval, "asg-2").progress, Is.EqualTo(0));
        }

        [Test]
        public void RotationCursorWalksTheRepeatableCatalogDeterministically()
        {
            var catalog = CampaignTestFixture.Catalog();
            var profile = ProfileWithBounties(
                Assignment("asg-1", "drift-10s", progress: 10),
                Assignment("asg-2", "boost-3", progress: 3),
                Assignment("asg-3", "landing-2", progress: 0));
            profile.bountyRotationCursor = 0;
            var context = BoundContext(profile, catalog, "race-c",
                "campaign");
            // One result completes BOTH bound assignments: drift-10s and
            // boost-3. Each rotates in bound order; the two replacements
            // must be distinct picks off the shared cursor.
            var result = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-boost", P, "finished", 1, new RaceMetrics
                {
                    driftTicks = 90, boostActivations = 1
                }, raceId: "race-c");

            var eval = Eval(profile, result, context, catalog);

            Assert.That(eval.completions, Has.Count.EqualTo(2),
                "at most the bound assignments may complete");
            Assert.That(eval.completions[0].bountyId, Is.EqualTo("drift-10s"));
            Assert.That(eval.completions[0].replacement.bountyId,
                Is.EqualTo("drift-30s"));
            Assert.That(eval.completions[1].bountyId, Is.EqualTo("boost-3"));
            Assert.That(eval.completions[1].replacement.bountyId,
                Is.EqualTo("boost-8"));
            Assert.That(eval.bountyRotationCursor, Is.EqualTo(4),
                "the cursor advanced past each deterministic pick");
            // Exactly three distinct active assignments are preserved.
            Assert.That(eval.activeBounties, Has.Count.EqualTo(3));
            var ids = new HashSet<string>();
            foreach (var row in eval.activeBounties)
                Assert.That(ids.Add(row.bountyId), Is.True,
                    "active assignments stay distinct");
            // Repeatable-only: rival one-time bounties are never
            // candidates for rotation picks.
            foreach (var row in eval.activeBounties)
                Assert.That(row.bountyId.StartsWith("bounty-rival"),
                    Is.False);
        }

        // --- eligibility ----------------------------------------------------

        [Test]
        public void NonProgressionModesAndUnfinishedResultsEarnNoFacts()
        {
            var catalog = CampaignTestFixture.Catalog();
            var profile = ProfileWithBounties(
                Assignment("asg-1", "boost-3", progress: 1));
            var metrics = new RaceMetrics { boostActivations = 9 };

            foreach (var mode in new[] { "timeTrial", "practice", "" })
            {
                var context = BoundContext(profile, catalog,
                    "race-" + (mode == "" ? "none" : mode), mode);
                var result = CampaignTestFixture.Result(mode,
                    "harbor-hop-car-time", P, "finished", 1, metrics.Copy(),
                    raceId: context.raceId);
                var eval = Eval(profile, result, context, catalog);
                Assert.That(eval.eligible, Is.False, mode);
                Assert.That(Row(eval, "asg-1").progress, Is.EqualTo(1), mode);
                Assert.That(eval.completions, Is.Empty, mode);
            }

            // DNF under a progression mode feeds nothing either.
            var dnfContext = BoundContext(profile, catalog, "race-dnf",
                "campaign");
            var dnf = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-boost", P, "dnf", 4, metrics.Copy(),
                raceId: "race-dnf");
            var evalDnf = Eval(profile, dnf, dnfContext, catalog);
            Assert.That(evalDnf.eligible, Is.False);
            Assert.That(Row(evalDnf, "asg-1").progress, Is.EqualTo(1));
            Assert.That(evalDnf.completions, Is.Empty);
            // An aborted race produces no finalized result at all.
            var unfinalized = CampaignTestFixture.Result("campaign",
                "harbor-hop-car-boost", P, "finished", 1, metrics.Copy(),
                raceId: "race-dnf");
            unfinalized.finalized = false;
            Assert.That(Eval(profile, unfinalized, dnfContext, catalog)
                .eligible, Is.False);
        }

        [Test]
        public void LanAndQuickRaceHumanFactsQualify()
        {
            var catalog = CampaignTestFixture.Catalog();
            var profile = ProfileWithBounties(
                Assignment("asg-1", "boost-3", progress: 1));
            foreach (var mode in new[] { "lan", "quickRace", "campaign" })
            {
                var context = BoundContext(profile, catalog,
                    "race-" + mode, mode);
                var result = CampaignTestFixture.Result(mode, "evt-x", P,
                    "finished", 2, new RaceMetrics { boostActivations = 1 },
                    raceId: context.raceId);
                var eval = Eval(profile, result, context, catalog);
                Assert.That(eval.eligible, Is.True, mode);
                Assert.That(Row(eval, "asg-1").progress, Is.EqualTo(2), mode);
            }
        }

        // --- one-time named-rival bounties ---------------------------------

        private static RaceResult FinaleResult(GameplayCatalog catalog,
            string classId, bool defeated, string raceId)
        {
            var metrics = new RaceMetrics { rivalDefeated = defeated };
            return CampaignTestFixture.Result("campaign",
                CampaignTestFixture.FinaleEventId(classId), P,
                "finished", defeated ? 1 : 2, metrics, raceId: raceId);
        }

        [Test]
        public void VictoryRewardsOnlyOnce()
        {
            var catalog = CampaignTestFixture.Catalog();
            var profile = ProfileWithBounties(
                Assignment("asg-1", "boost-3", progress: 1));
            // The car finale's victory is already recorded: beating the
            // rival again must not re-propose the one-time reward.
            profile.finaleProgress.Add(new FinaleProgress
            {
                eventId = CampaignTestFixture.FinaleEventId("car"),
                completed = true, victorious = true
            });
            var context = BoundContext(profile, catalog, "race-v",
                "campaign");
            var context2 = BoundContext(profile, catalog, "race-v2",
                "campaign");

            var repeat = Eval(profile,
                FinaleResult(catalog, "car", defeated: true, "race-v"),
                context, catalog);
            Assert.That(repeat.rivalDefeated, Is.True,
                "the defeat fact itself is still reported");
            Assert.That(repeat.rivalRewardDue, Is.False,
                "an already-victorious rival never pays twice");
            Assert.That(repeat.rivalRewardCoins, Is.EqualTo(0));
            Assert.That(repeat.rivalBountyIds, Is.Empty);

            // The motorcycle finale was completed but never won — its
            // first victory still owes the one-time reward.
            profile.finaleProgress.Add(new FinaleProgress
            {
                eventId = CampaignTestFixture.FinaleEventId("motorcycle"),
                completed = true, victorious = false
            });
            var first = Eval(profile,
                FinaleResult(catalog, "motorcycle", defeated: true,
                    "race-v2"),
                context2, catalog);
            Assert.That(first.rivalRewardDue, Is.True);
            Assert.That(first.rivalRewardCoins, Is.EqualTo(30));
            Assert.That(first.rivalBountyIds,
                Is.EqualTo(new[] { "bounty-rival-motorcycle" }));
            Assert.That(first.rivalId, Is.EqualTo("rival-motorcycle"));
        }

        [Test]
        public void RivalBountiesStayOutsideRotationAndSoloOnly()
        {
            var catalog = CampaignTestFixture.Catalog();
            var profile = ProfileWithBounties(
                Assignment("asg-1", "boost-3", progress: 1));
            var context = BoundContext(profile, catalog, "race-f1",
                "campaign");

            var eval = Eval(profile,
                FinaleResult(catalog, "car", defeated: true, "race-f1"),
                context, catalog);

            Assert.That(eval.rivalRewardDue, Is.True);
            Assert.That(eval.rivalRewardCoins, Is.EqualTo(30));
            Assert.That(eval.rivalBountyIds,
                Is.EqualTo(new[] { "bounty-rival-car" }));
            // The repeatable tray is untouched: same three assignments,
            // same progress, same cursor.
            Assert.That(eval.activeBounties, Has.Count.EqualTo(1));
            Assert.That(eval.activeBounties[0].assignmentId,
                Is.EqualTo("asg-1"));
            Assert.That(eval.activeBounties[0].progress, Is.EqualTo(1));
            Assert.That(eval.bountyRotationCursor,
                Is.EqualTo(profile.bountyRotationCursor));
            Assert.That(eval.completions, Is.Empty,
                "rival victories are one-time facts, not tray rotations");

            // A defeat in a shared mode is not a solo rival unlock.
            var lanContext = BoundContext(profile, catalog, "race-lan",
                "lan");
            var lan = Eval(profile,
                CampaignTestFixture.Result("lan",
                    CampaignTestFixture.FinaleEventId("car"), P,
                    "finished", 1,
                    new RaceMetrics { rivalDefeated = true },
                    raceId: "race-lan"),
                lanContext, catalog);
            Assert.That(lan.rivalRewardDue, Is.False,
                "rival unlocks stay solo — LAN defeats pay no bounty");
        }

        [Test]
        public void LostFinaleProposesNoRivalReward()
        {
            var catalog = CampaignTestFixture.Catalog();
            var profile = ProfileWithBounties(
                Assignment("asg-1", "boost-3", progress: 0));
            var context = BoundContext(profile, catalog, "race-l",
                "campaign");
            var eval = Eval(profile,
                FinaleResult(catalog, "car", defeated: false, "race-l"),
                context, catalog);
            Assert.That(eval.rivalDefeated, Is.False);
            Assert.That(eval.rivalRewardDue, Is.False);
            Assert.That(eval.rivalRewardCoins, Is.EqualTo(0));
        }

        // --- determinism / immutability ------------------------------------

        private static string EvaluationSnapshot(BountyEvaluation eval)
        {
            var b = new System.Text.StringBuilder(256);
            b.Append(eval.eligible).Append('|').Append(eval.reason)
                .Append('|').Append(eval.bountyRotationCursor).Append('|')
                .Append(eval.totalRewardCoins).Append('|')
                .Append(eval.rivalDefeated).Append('|')
                .Append(eval.rivalRewardDue).Append('|')
                .Append(eval.rivalRewardCoins).Append('|')
                .Append(eval.rivalId ?? "").Append('#');
            if (eval.activeBounties != null)
                foreach (var r in eval.activeBounties)
                    b.Append(r == null ? "n;" : r.assignmentId + ":"
                        + r.bountyId + ":" + r.progress + ";");
            b.Append('#');
            if (eval.completions != null)
                foreach (var c in eval.completions)
                    b.Append(c == null ? "n;" : c.assignmentId + ":"
                        + c.bountyId + ":" + c.rewardCoins + ":"
                        + (c.replacement == null ? "-" :
                            c.replacement.assignmentId + "/"
                            + c.replacement.bountyId) + ";");
            b.Append('#');
            if (eval.rivalBountyIds != null)
                b.Append(string.Join(",", eval.rivalBountyIds));
            return b.ToString();
        }

        [Test]
        public void EvaluationIsDeterministicAndLeavesInputsUntouched()
        {
            var catalog = CampaignTestFixture.Catalog();
            var profile = ProfileWithBounties(
                Assignment("asg-1", "drift-10s", progress: 9),
                Assignment("asg-2", "boost-3", progress: 0));
            var context = BoundContext(profile, catalog, "race-det",
                "campaign");
            var result = FinaleResult(catalog, "car", defeated: true,
                "race-det");
            result.racers[0].metrics.driftTicks = 90;

            var profileBefore = CampaignTestFixture.ProfileSnapshot(profile);
            var contextBefore = context.raceId + "|" + context.mode
                + "|" + string.Join(",", context.bountyAssignmentIds);
            var fingerprint = result.Fingerprint();

            var a = Eval(profile, result, context, catalog);
            var b = Eval(profile, result, context, catalog);
            Assert.That(EvaluationSnapshot(a),
                Is.EqualTo(EvaluationSnapshot(b)),
                "repeated evaluation of the same facts is identical, "
                + "including minted replacement identities");

            Assert.That(CampaignTestFixture.ProfileSnapshot(profile),
                Is.EqualTo(profileBefore));
            Assert.That(context.raceId + "|" + context.mode
                + "|" + string.Join(",", context.bountyAssignmentIds),
                Is.EqualTo(contextBefore));
            Assert.That(result.Fingerprint(), Is.EqualTo(fingerprint));
            // Proposed rows are independent objects, never the profile's.
            Assert.AreNotSame(profile.activeBounties[0],
                a.activeBounties[0]);
        }
    }
}
