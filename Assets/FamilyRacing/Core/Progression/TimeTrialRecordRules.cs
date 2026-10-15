using FamilyRacing.Core.Profiles;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Progression
{
    // The proposed outcome of evaluating one finalized time-trial result
    // against a profile's stored personal bests (M5 Task 7). Pure: the
    // `record` is a NEW TimeTrialRecord to persist — the profile is
    // never mutated here and nothing is saved. `isBest` is true only
    // when `record` is non-null and must replace/create the stored row.
    public sealed class TimeTrialRecordUpdate
    {
        // Facts were eligible for record evaluation. False with `reason`
        // set for no result, unfinalized, absent participant row, DNF,
        // non-trial mode, or a non-positive/missing elapsed time —
        // exactly the cases the receipt path treats as ineligible.
        public bool eligible;
        public string reason = "";
        public string courseId = "";
        public string classId = "";
        // The run's own elapsed finish ticks — the measured time.
        public long elapsedTicks;
        // The stored best for the pair before this run (0 = none yet).
        public long previousBestTicks;
        public bool isBest;
        // The replacement row to persist when isBest; null otherwise.
        public TimeTrialRecord record;
    }

    // Personal-best rule for time trials (M5 Task 7): records group by
    // (courseId, classId) and only a strictly lower positive elapsed
    // tick count ever replaces a standing best — equal and slower runs
    // are adjudicated (receipt committed) but never overwrite. Trials
    // carry no coins, campaign facts or bounty folds; the record is the
    // only progression a trial can move.
    public static class TimeTrialRecordRules
    {
        public static TimeTrialRecordUpdate Evaluate(RaceResult result,
            string participantId, ProfileSave profile)
        {
            var update = new TimeTrialRecordUpdate();
            if (result == null || !result.finalized)
            { update.reason = "noResult"; return update; }
            if (result.mode != RaceModeRules.ModeId(RaceMode.TimeTrial))
            { update.reason = "invalidMode"; return update; }
            RaceResultEntry entry = null;
            if (result.racers != null)
                foreach (var racer in result.racers)
                    if (racer != null
                        && racer.participantId == participantId)
                    { entry = racer; break; }
            if (entry == null)
            { update.reason = "notInResult"; return update; }
            if (entry.outcome != "finished")
            { update.reason = "dnf"; return update; }
            // The recordable time is the finished row's own finish tick
            // count — positive only; a zero/negative row can never be a
            // best because validation rejects non-positive records.
            if (entry.finishTicks <= 0)
            { update.reason = "invalidResult"; return update; }
            // The grouping facts bind at finalize (format-2 fingerprint);
            // a result without them has no honest bucket to record into.
            if (string.IsNullOrEmpty(result.courseId)
                || string.IsNullOrEmpty(result.classId))
            { update.reason = "invalidResult"; return update; }

            update.eligible = true;
            update.reason = "ok";
            update.courseId = result.courseId;
            update.classId = result.classId;
            update.elapsedTicks = entry.finishTicks;

            TimeTrialRecord standing = null;
            if (profile != null && profile.timeTrialRecords != null)
                foreach (var row in profile.timeTrialRecords)
                    if (row != null && row.courseId == result.courseId
                        && row.classId == result.classId)
                    { standing = row; break; }
            update.previousBestTicks =
                standing != null ? standing.bestTicks : 0;
            update.isBest = standing == null
                || entry.finishTicks < standing.bestTicks;
            if (update.isBest)
                update.record = new TimeTrialRecord
                {
                    courseId = result.courseId,
                    classId = result.classId,
                    bestTicks = entry.finishTicks
                };
            return update;
        }
    }
}
