using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FamilyRacing.Core.Racing
{
    // One ordered row in a finalized race result: finishers first (finish
    // tick, then stable slot), then DNFs in standings order. `attribution`
    // is "human" | "ai"; `coinsAwarded` is the authored completion reward
    // applied to finishers only — DNFs and aborted races award nothing.
    [Serializable]
    public sealed class RaceResultEntry
    {
        public int position;
        public int slot;
        public string participantId = "";
        public string vehicleId = "";
        public string attribution = "";
        public string outcome = "";         // "finished" | "dnf"
        public long finishTicks;            // elapsed race ticks; 0 for DNF
        public int lapsCompleted;
        public int coinsAwarded;
        // --- format-2 additive facts (M5 Task 4) ------------------------
        // The visible AI difficulty preset this seat ran under ("easy" |
        // "medium" | "hard"); "" on human seats and pre-format-2 results.
        public string difficultyId = "";
        // Authoritative per-racer metrics counted at completed simulation
        // boundaries; null on legacy (format < 2) results.
        public RaceMetrics metrics;

        public RaceResultEntry Copy()
        {
            var copy = (RaceResultEntry)MemberwiseClone();
            copy.metrics = metrics != null ? metrics.Copy() : null;
            return copy;
        }
    }

    // Delivery metadata for one participant of a retained result — never
    // part of the immutable facts the fingerprint covers. `state` is
    // pending | failed | persisted; `code` carries the last ack failure
    // reason; `lastOfferedAt` is the host's resend clock.
    [Serializable]
    public sealed class RaceResultDelivery
    {
        public string participantId = "";
        public string state = "pending";
        public string code = "";
        public double lastOfferedAt = double.NegativeInfinity;

        public RaceResultDelivery Copy()
        {
            return (RaceResultDelivery)MemberwiseClone();
        }
    }

    // The single authoritative outcome of one network race. Created
    // exactly once at finalization and never mutated afterwards; aborted
    // races produce no result. `completionCoins` is the authored reward
    // fact (Task 8 applies it to eligible finished racers).
    //
    // `deliveryStatus` and `deliveries` are transport metadata owned by
    // the session service: they ride alongside the immutable facts but
    // never enter the fingerprint, the canonical encoding, or the wire
    // form.
    [Serializable]
    public sealed class RaceResult
    {
        // Result format version: absent (deserialized 0) or 1 = the
        // legacy completion-only wire/fingerprint form; 2 (M5 Task 4)
        // adds mode + rules identity and per-racer authoritative
        // metrics. New results always write 2; legacy rows keep the
        // fr-result-fp-1 canonical form so old receipts stay valid.
        public int formatVersion = 1;
        public string resultId = "";
        public string sessionId = "";
        public string raceId = "";
        public string eventId = "";
        public string contentVersion = "";
        // Format-2 identity facts: the race mode vocabulary id
        // ("campaign" | "quickRace" | "timeTrial" | "lan") and the
        // 64-hex canonical rules fingerprint the race ran under.
        public string mode = "";
        public string rulesFingerprint = "";
        public bool finalized;
        public long finalizedHostTick;
        public long finalizedElapsedTicks;
        public int completionCoins;
        public List<RaceResultEntry> racers = new List<RaceResultEntry>();

        // Delivery bookkeeping: aggregate status for observers plus the
        // per-participant ledger. Excluded from Fingerprint() by design.
        // `deliveryCode` is the client-side record of the last apply
        // outcome reason ("" when persisted/pending) so an observer can
        // tell a permanent ineligible verdict from a transient save
        // failure without decoding the wire ack.
        public string deliveryStatus = "";
        public string deliveryCode = "";
        public List<RaceResultDelivery> deliveries =
            new List<RaceResultDelivery>();

        public RaceResult Copy()
        {
            var copy = (RaceResult)MemberwiseClone();
            copy.racers = new List<RaceResultEntry>();
            foreach (var entry in racers)
                copy.racers.Add(entry != null ? entry.Copy() : null);
            copy.deliveries = new List<RaceResultDelivery>();
            foreach (var delivery in deliveries)
                copy.deliveries.Add(delivery != null ? delivery.Copy() : null);
            return copy;
        }

        // Versioned canonical fingerprint of the immutable result facts.
        // Every field is length-prefixed ("<chars>:<text>") in a fixed
        // order, numbers render InvariantCulture, strings are compared
        // ordinally, and racer order is a fact — so the SHA-256 is
        // identical on Android and Windows regardless of locale, JSON
        // property order, or how the result was delivered. Delivery
        // metadata is deliberately excluded.
        //
        // Format 1 (legacy rows, deserialized results with no
        // formatVersion) keeps the exact "fr-result-fp-1" canonical form
        // so previously persisted receipts keep matching. Format 2 uses
        // a distinct "fr-result-fp-2" domain prefix and additionally
        // binds mode + rules identity and every authoritative metric
        // counter, so two runs differing in any recorded fact produce
        // different fingerprints.
        public string Fingerprint()
        {
            var builder = new StringBuilder(256);
            var v2 = formatVersion >= 2;
            builder.Append(v2 ? "fr-result-fp-2" : "fr-result-fp-1");
            Field(builder, resultId);
            Field(builder, sessionId);
            Field(builder, raceId);
            Field(builder, eventId);
            Field(builder, contentVersion);
            if (v2)
            {
                Field(builder, mode);
                Field(builder, rulesFingerprint);
            }
            Field(builder, finalized ? "1" : "0");
            Field(builder, finalizedHostTick.ToString(
                CultureInfo.InvariantCulture));
            Field(builder, finalizedElapsedTicks.ToString(
                CultureInfo.InvariantCulture));
            Field(builder, completionCoins.ToString(
                CultureInfo.InvariantCulture));
            Field(builder, (racers != null ? racers.Count : 0).ToString(
                CultureInfo.InvariantCulture));
            if (racers != null)
            {
                foreach (var racer in racers)
                {
                    if (racer == null)
                    {
                        Field(builder, "-");
                        continue;
                    }
                    Field(builder, racer.position.ToString(
                        CultureInfo.InvariantCulture));
                    Field(builder, racer.slot.ToString(
                        CultureInfo.InvariantCulture));
                    Field(builder, racer.participantId);
                    Field(builder, racer.vehicleId);
                    Field(builder, racer.attribution);
                    Field(builder, racer.outcome);
                    Field(builder, racer.finishTicks.ToString(
                        CultureInfo.InvariantCulture));
                    Field(builder, racer.lapsCompleted.ToString(
                        CultureInfo.InvariantCulture));
                    Field(builder, racer.coinsAwarded.ToString(
                        CultureInfo.InvariantCulture));
                    if (!v2) continue;
                    Field(builder, racer.difficultyId);
                    var m = racer.metrics;
                    if (m == null)
                    {
                        Field(builder, "-");
                        continue;
                    }
                    Field(builder, m.elapsedTicks.ToString(
                        CultureInfo.InvariantCulture));
                    Field(builder, m.finishTicks.ToString(
                        CultureInfo.InvariantCulture));
                    Field(builder, m.position.ToString(
                        CultureInfo.InvariantCulture));
                    Field(builder, m.outcome);
                    Field(builder, m.driftTicks.ToString(
                        CultureInfo.InvariantCulture));
                    Field(builder, m.boostActivations.ToString(
                        CultureInfo.InvariantCulture));
                    Field(builder, m.cleanLandings.ToString(
                        CultureInfo.InvariantCulture));
                    Field(builder, m.longestCleanStreakTicks.ToString(
                        CultureInfo.InvariantCulture));
                    Field(builder, m.acceptedShortcuts.ToString(
                        CultureInfo.InvariantCulture));
                    Field(builder, m.distinctShortcuts.ToString(
                        CultureInfo.InvariantCulture));
                    // M5 Task 6 additive facts: human-attributed
                    // shortcut counters ride the same fp-2 row so two
                    // results differing in AI-vs-human traversal credit
                    // never share a fingerprint.
                    Field(builder, m.humanAcceptedShortcuts.ToString(
                        CultureInfo.InvariantCulture));
                    Field(builder, m.humanDistinctShortcuts.ToString(
                        CultureInfo.InvariantCulture));
                    Field(builder, m.gadgetUses.ToString(
                        CultureInfo.InvariantCulture));
                    Field(builder, m.distinctGadgetTypes.ToString(
                        CultureInfo.InvariantCulture));
                    Field(builder, m.recoveries.ToString(
                        CultureInfo.InvariantCulture));
                    Field(builder, m.impacts.ToString(
                        CultureInfo.InvariantCulture));
                    Field(builder, m.rivalDefeated ? "1" : "0");
                }
            }
            var canonical = Encoding.UTF8.GetBytes(builder.ToString());
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(canonical);
                var hex = new StringBuilder(64);
                foreach (var b in hash)
                    hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }

        private static void Field(StringBuilder builder, string value)
        {
            value = value ?? "";
            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(value);
        }
    }
}
