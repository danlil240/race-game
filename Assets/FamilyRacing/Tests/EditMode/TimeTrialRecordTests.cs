using System;
using System.Collections.Generic;
using System.IO;
using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Progression;
using FamilyRacing.Core.Racing;
using NUnit.Framework;
#if UNITY_5_3_OR_NEWER
using UnityEngine;
#else
using System.Text.Json;
#endif

namespace FamilyRacing.Tests
{
    // M5 Task 7: personal-best time-trial records. A finalized trial run
    // proposes a lower-is-better record per (courseId, classId) pair —
    // grouped, never across pairs — and the proposal lands inside the
    // same atomic profile save as the result receipt. Coins and every
    // campaign fact stay untouched; equal or slower runs never replace
    // a best.
    public sealed class TimeTrialRecordTests
    {
        private string directory;
        private IProfileSerializer serializer;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(),
                "family-racing-tt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            serializer = new TestJsonSerializer();
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        // --- the pure evaluator ---------------------------------------

        [Test]
        public void FirstRunProposesRecordForCourseClassPair()
        {
            var profile = CampaignTestFixture.Profile();
            var result = TrialResult(profile.id, "harbor-hop", "car", 3000);

            var update = TimeTrialRecordRules.Evaluate(result,
                profile.id, profile);
            Assert.That(update.eligible, Is.True);
            Assert.That(update.courseId, Is.EqualTo("harbor-hop"));
            Assert.That(update.classId, Is.EqualTo("car"));
            Assert.That(update.elapsedTicks, Is.EqualTo(3000));
            Assert.That(update.previousBestTicks, Is.EqualTo(0));
            Assert.That(update.isBest, Is.True);
            Assert.That(update.record, Is.Not.Null);
            Assert.That(update.record.bestTicks, Is.EqualTo(3000));
        }

        [Test]
        public void EqualOrSlowerRunNeverProposesReplacement()
        {
            var profile = CampaignTestFixture.Profile();
            profile.timeTrialRecords.Add(new TimeTrialRecord
            {
                courseId = "harbor-hop", classId = "car",
                bestTicks = 3000
            });

            var equal = TimeTrialRecordRules.Evaluate(
                TrialResult(profile.id, "harbor-hop", "car", 3000),
                profile.id, profile);
            Assert.That(equal.eligible, Is.True);
            Assert.That(equal.isBest, Is.False,
                "an equal time is never a new best");
            Assert.That(equal.previousBestTicks, Is.EqualTo(3000));
            Assert.That(equal.record, Is.Null);

            var slower = TimeTrialRecordRules.Evaluate(
                TrialResult(profile.id, "harbor-hop", "car", 3200),
                profile.id, profile);
            Assert.That(slower.eligible, Is.True);
            Assert.That(slower.isBest, Is.False);
            Assert.That(slower.record, Is.Null);
        }

        [Test]
        public void FasterRunProposesReplacement()
        {
            var profile = CampaignTestFixture.Profile();
            profile.timeTrialRecords.Add(new TimeTrialRecord
            {
                courseId = "harbor-hop", classId = "car",
                bestTicks = 3000
            });
            var update = TimeTrialRecordRules.Evaluate(
                TrialResult(profile.id, "harbor-hop", "car", 2800),
                profile.id, profile);
            Assert.That(update.eligible, Is.True);
            Assert.That(update.isBest, Is.True);
            Assert.That(update.record.bestTicks, Is.EqualTo(2800));
            Assert.That(update.previousBestTicks, Is.EqualTo(3000));
        }

        [Test]
        public void RecordsGroupByCourseAndClassIndependently()
        {
            var profile = CampaignTestFixture.Profile();
            profile.timeTrialRecords.Add(new TimeTrialRecord
            {
                courseId = "harbor-hop", classId = "car",
                bestTicks = 3000
            });
            // Same time on a different class is a different group —
            // a NEW best there, not a comparison against car's row.
            var moto = TimeTrialRecordRules.Evaluate(
                TrialResult(profile.id, "harbor-hop", "motorcycle", 3200),
                profile.id, profile);
            Assert.That(moto.isBest, Is.True);
            var otherCourse = TimeTrialRecordRules.Evaluate(
                TrialResult(profile.id, "lighthouse-loop", "car", 3100),
                profile.id, profile);
            Assert.That(otherCourse.isBest, Is.True);
        }

        [Test]
        public void EvaluateRejectsNonTrialsAndUnfinishedRows()
        {
            var profile = CampaignTestFixture.Profile();
            // A campaign result is never a record candidate.
            var campaign = TrialResult(profile.id, "harbor-hop", "car",
                3000);
            campaign.mode = "campaign";
            var badMode = TimeTrialRecordRules.Evaluate(campaign,
                profile.id, profile);
            Assert.That(badMode.eligible, Is.False);
            Assert.That(badMode.reason, Is.EqualTo("invalidMode"));

            var dnf = TrialResult(profile.id, "harbor-hop", "car", 0);
            dnf.racers[0].outcome = "dnf";
            dnf.racers[0].metrics.outcome = "dnf";
            var unfinished = TimeTrialRecordRules.Evaluate(dnf,
                profile.id, profile);
            Assert.That(unfinished.eligible, Is.False);
            Assert.That(unfinished.reason, Is.EqualTo("dnf"));

            var missing = TimeTrialRecordRules.Evaluate(
                TrialResult("someone-else", "harbor-hop", "car", 3000),
                profile.id, profile);
            Assert.That(missing.eligible, Is.False);
            Assert.That(missing.reason, Is.EqualTo("notInResult"));

            var absent = TimeTrialRecordRules.Evaluate(null,
                profile.id, profile);
            Assert.That(absent.eligible, Is.False);
            Assert.That(absent.reason, Is.EqualTo("noResult"));
        }

        // --- the atomic persist path -----------------------------------

        [Test]
        public void FirstTrialResultPersistsRecordAndReceiptAtomically()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = CampaignTestFixture.Profile();
            store.Save(profile);
            var loaded = store.Load(0).Profile;
            var result = TrialResult(loaded.id, "harbor-hop", "car", 3000);
            result.rulesFingerprint = catalog.CompatibilityFingerprint;

            var applied = RaceRewardService.Apply(result, loaded.id,
                loaded, store, catalog, null);
            Assert.That(applied.status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            Assert.That(applied.awardedCoins, Is.EqualTo(0),
                "trials never pay coins");
            Assert.That(applied.recordImproved, Is.True);
            Assert.That(applied.recordBestTicks, Is.EqualTo(3000));
            Assert.That(applied.recordCourseId, Is.EqualTo("harbor-hop"));
            Assert.That(applied.recordClassId, Is.EqualTo("car"));
            Assert.That(applied.mode, Is.EqualTo("timeTrial"));

            var record = Record(loaded, "harbor-hop", "car");
            Assert.That(record, Is.Not.Null);
            Assert.That(record.bestTicks, Is.EqualTo(3000));
            // No coins, no campaign facts, no context consumed.
            Assert.That(loaded.coins, Is.EqualTo(0));
            Assert.That(loaded.eventProgress.Count, Is.EqualTo(0));
            Assert.That(loaded.finaleProgress.Count, Is.EqualTo(0));
            var receipt = loaded.appliedResults[0];
            Assert.That(receipt.resultId, Is.EqualTo(result.resultId));
            Assert.That(receipt.rulesFingerprint,
                Is.EqualTo(catalog.CompatibilityFingerprint));
            Assert.That(receipt.mode, Is.EqualTo("timeTrial"));
            Assert.That(receipt.recordImproved, Is.True);

            // Restart + redelivery: the receipt dedupes and the record
            // stays the persisted best.
            var reloaded = Store().Load(0).Profile;
            var replay = RaceRewardService.Apply(result, reloaded.id,
                reloaded, Store(), catalog, null);
            Assert.That(replay.status,
                Is.EqualTo(RaceRewardService.StatusAlreadyApplied));
            Assert.That(replay.recordImproved, Is.True,
                "the persisted breakdown echoes on redelivery");
            Assert.That(reloaded.timeTrialRecords.Count, Is.EqualTo(1));
        }

        [Test]
        public void SlowerAndEqualTrialsStillCommitReceipts()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = CampaignTestFixture.Profile();
            profile.timeTrialRecords.Add(new TimeTrialRecord
            {
                courseId = "harbor-hop", classId = "car",
                bestTicks = 3000
            });
            store.Save(profile);
            var loaded = store.Load(0).Profile;

            var slower = TrialResult(loaded.id, "harbor-hop", "car", 3200,
                "tt-slower");
            var applied = RaceRewardService.Apply(slower, loaded.id,
                loaded, store, catalog, null);
            Assert.That(applied.status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            Assert.That(applied.recordImproved, Is.False);
            Assert.That(applied.recordBestTicks, Is.EqualTo(3000),
                "the standing best echoes, not the run");
            Assert.That(Record(loaded, "harbor-hop", "car").bestTicks,
                Is.EqualTo(3000));
            Assert.That(ReceiptFor(loaded, "tt-slower/result"),
                Is.Not.Null,
                "a non-record run is still durably adjudicated");

            var faster = TrialResult(loaded.id, "harbor-hop", "car", 2800,
                "tt-faster");
            var best = RaceRewardService.Apply(faster, loaded.id,
                loaded, store, catalog, null);
            Assert.That(best.status,
                Is.EqualTo(RaceRewardService.StatusApplied));
            Assert.That(best.recordImproved, Is.True);
            Assert.That(best.recordBestTicks, Is.EqualTo(2800));
            Assert.That(Record(loaded, "harbor-hop", "car").bestTicks,
                Is.EqualTo(2800));
            Assert.That(loaded.coins, Is.EqualTo(0));

            // The persisted truth survives a restart.
            var reloaded = Store().Load(0).Profile;
            Assert.That(Record(reloaded, "harbor-hop", "car").bestTicks,
                Is.EqualTo(2800));
            Assert.That(reloaded.appliedResults.Count, Is.EqualTo(2));
        }

        [Test]
        public void TrialReceiptBindsFingerprintAndRules()
        {
            var catalog = CampaignTestFixture.Catalog();
            var store = Store();
            var profile = CampaignTestFixture.Profile();
            store.Save(profile);
            var loaded = store.Load(0).Profile;
            var result = TrialResult(loaded.id, "harbor-hop", "car", 3000);
            result.rulesFingerprint = catalog.CompatibilityFingerprint;
            Assert.That(RaceRewardService.Apply(result, loaded.id,
                loaded, store, catalog, null).status,
                Is.EqualTo(RaceRewardService.StatusApplied));

            // Same result id under a different rules edition is a
            // conflict — not a redelivery.
            var foreign = result.Copy();
            foreign.rulesFingerprint = new string('c', 64);
            foreign.racers[0].finishTicks = 2900;
            var conflict = RaceRewardService.Apply(foreign, loaded.id,
                loaded, store, catalog, null);
            Assert.That(conflict.status,
                Is.EqualTo(RaceRewardService.StatusFailed));
            Assert.That(conflict.code, Is.EqualTo("conflict"));
            Assert.That(Record(loaded, "harbor-hop", "car").bestTicks,
                Is.EqualTo(3000));
        }

        // --- helpers ----------------------------------------------------

        private ProfileStore Store()
        {
            return new ProfileStore(directory, serializer);
        }

        // A finalized format-2 trial result: the shape
        // RaceResultFactory.FromTrial produces — mode, rules identity,
        // the record grouping facts and one human metrics row.
        private static RaceResult TrialResult(string participantId,
            string courseId, string classId, long finishTicks,
            string raceId = "tt-r1")
        {
            var metrics = new RaceMetrics
            {
                participantId = participantId,
                attribution = "human",
                position = 1,
                outcome = "finished",
                finishTicks = finishTicks,
                elapsedTicks = finishTicks + 100
            };
            var result = new RaceResult
            {
                formatVersion = 2,
                resultId = raceId + "/result",
                sessionId = "solo:" + raceId,
                raceId = raceId,
                eventId = courseId + "-" + classId + "-time",
                contentVersion = "gameplay-loop-1",
                mode = "timeTrial",
                rulesFingerprint = new string('b', 64),
                courseId = courseId,
                classId = classId,
                finalized = true,
                finalizedHostTick = finishTicks + 100,
                finalizedElapsedTicks = finishTicks + 100,
                completionCoins = 0
            };
            result.racers.Add(new RaceResultEntry
            {
                position = 1, slot = 0, participantId = participantId,
                vehicleId = classId + "-01", attribution = "human",
                outcome = "finished", finishTicks = finishTicks,
                lapsCompleted = 1, coinsAwarded = 0, metrics = metrics
            });
            return result;
        }

        private static TimeTrialRecord Record(ProfileSave profile,
            string courseId, string classId)
        {
            foreach (var row in profile.timeTrialRecords)
                if (row != null && row.courseId == courseId
                    && row.classId == classId) return row;
            return null;
        }

        private static AppliedResultReceipt ReceiptFor(ProfileSave profile,
            string resultId)
        {
            foreach (var row in profile.appliedResults)
                if (row != null && row.resultId == resultId) return row;
            return null;
        }

        private sealed class TestJsonSerializer : IProfileSerializer
        {
#if UNITY_5_3_OR_NEWER
            public string Serialize(ProfileSave profile)
            {
                return JsonUtility.ToJson(profile);
            }
            public ProfileSave Deserialize(string json)
            {
                return Newtonsoft.Json.JsonConvert
                    .DeserializeObject<ProfileSave>(json);
            }
#else
            private static readonly JsonSerializerOptions Options =
                new JsonSerializerOptions { IncludeFields = true };
            public string Serialize(ProfileSave profile)
            {
                return JsonSerializer.Serialize(profile, Options);
            }
            public ProfileSave Deserialize(string json)
            {
                return JsonSerializer.Deserialize<ProfileSave>(json, Options);
            }
#endif
        }
    }
}
