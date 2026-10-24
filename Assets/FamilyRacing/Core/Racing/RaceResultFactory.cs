using System;
using System.Collections.Generic;
using FamilyRacing.Core.Networking;

namespace FamilyRacing.Core.Racing
{
    // The shared result-shaping rule every local mode uses: a finalized
    // result is format 2 and carries the mode/rules identity plus the
    // authoritative per-racer metrics counted at completed boundaries.
    // The four-racer coordinator calls Bind() on its in-place result
    // before first publication; the lone time-trial session calls
    // FromTrial() to build its single-row result. LAN results never
    // pass through here — they keep format 1 and the fp-1 canonical
    // fingerprint so old receipts stay valid.
    public static class RaceResultFactory
    {
        // Binds format-2 identity + metric facts onto a freshly built
        // coordinator result BEFORE it is published/fingerprinted. No-op
        // on legacy paths (null result). `metrics` carries one row per
        // participant from the race's accumulator snapshot;
        // `difficultyId` is stamped on AI seats (human seats keep "");
        // `rivalParticipantId` names the authored rival seat so the
        // local human's metric can carry the honest rivalDefeated fact.
        public static void Bind(RaceResult result, string mode,
            string rulesFingerprint, IReadOnlyList<RaceMetrics> metrics,
            string difficultyId, string rivalParticipantId)
        {
            if (result == null || result.formatVersion >= 2) return;
            result.formatVersion = 2;
            result.mode = mode ?? "";
            result.rulesFingerprint = rulesFingerprint ?? "";
            var byParticipant = new Dictionary<string, RaceMetrics>(
                StringComparer.Ordinal);
            if (metrics != null)
                foreach (var metric in metrics)
                    if (metric != null && metric.participantId != null
                        && !byParticipant.ContainsKey(metric.participantId))
                        byParticipant[metric.participantId] = metric;

            RaceResultEntry rival = null;
            RaceResultEntry human = null;
            foreach (var entry in result.racers)
            {
                if (entry == null) continue;
                if (rivalParticipantId != null
                    && entry.participantId == rivalParticipantId)
                    rival = entry;
                if (entry.attribution == "human" && human == null)
                    human = entry;
                RaceMetrics snapshot;
                var row = byParticipant.TryGetValue(entry.participantId,
                        out snapshot)
                    ? snapshot.Copy()
                    : new RaceMetrics { participantId = entry.participantId };
                // Authoritative placement facts refresh the counters at
                // the final boundary — the accumulator's own position
                // and outcome agree by construction.
                row.position = entry.position;
                row.outcome = entry.outcome;
                row.finishTicks = entry.finishTicks;
                row.elapsedTicks = result.finalizedElapsedTicks;
                row.attribution = entry.attribution;
                entry.metrics = row;
                entry.difficultyId = entry.attribution == "ai"
                    ? (difficultyId ?? "") : "";
            }
            // Rival-finale fact: the local human beat the authored rival
            // seat — finished ahead of it or the rival DNF'd. Anything
            // else (no rival, human DNF, rival ahead) is not a victory.
            if (rival != null && human != null && human.metrics != null)
                human.metrics.rivalDefeated = human.outcome == "finished"
                    && human.position < rival.position;
        }

        // The lone-racer trial result: one finalized row for the human
        // seat, mode/rules identity bound, and the session's own
        // accumulated metrics. `elapsedTicks` is the authoritative race
        // clock at finalize; a finished trial's row records the session
        // finish tick. Never called for unfinished/invalid sessions —
        // aborts produce no result at all.
        public static RaceResult FromTrial(string sessionId, string raceId,
            string eventId, string contentVersion, string mode,
            string rulesFingerprint, string participantId,
            string vehicleId, long hostTick, long elapsedTicks,
            bool finished, long finishTicks, int lapsCompleted,
            RaceMetrics metrics, string courseId = "", string classId = "")
        {
            var row = metrics != null ? metrics.Copy()
                : new RaceMetrics();
            row.participantId = participantId ?? "";
            row.attribution = "human";
            row.position = 1;
            row.outcome = finished ? "finished" : "dnf";
            row.finishTicks = finished ? finishTicks : 0;
            row.elapsedTicks = elapsedTicks;
            var result = new RaceResult
            {
                formatVersion = 2,
                resultId = (raceId ?? "") + "/result",
                sessionId = sessionId ?? "",
                raceId = raceId ?? "",
                eventId = eventId ?? "",
                contentVersion = contentVersion ?? "",
                mode = mode ?? "",
                rulesFingerprint = rulesFingerprint ?? "",
                // Task 7 record-grouping facts: the trial's course and
                // vehicle class key its personal-best row.
                courseId = courseId ?? "",
                classId = classId ?? "",
                finalized = true,
                finalizedHostTick = hostTick,
                finalizedElapsedTicks = elapsedTicks,
                // Trials award nothing: completion coins live on authored
                // race events only (economy completion rule is Task 7's).
                completionCoins = 0
            };
            result.racers.Add(new RaceResultEntry
            {
                position = 1,
                slot = 0,
                participantId = participantId ?? "",
                vehicleId = vehicleId ?? "",
                attribution = "human",
                outcome = finished ? "finished" : "dnf",
                finishTicks = finished ? finishTicks : 0,
                lapsCompleted = lapsCompleted,
                coinsAwarded = 0,
                metrics = row
            });
            return result;
        }
    }
}
