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

        public RaceResultEntry Copy()
        {
            return (RaceResultEntry)MemberwiseClone();
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
        public string resultId = "";
        public string sessionId = "";
        public string raceId = "";
        public string eventId = "";
        public string contentVersion = "";
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
        public string Fingerprint()
        {
            var builder = new StringBuilder(256);
            builder.Append("fr-result-fp-1");
            Field(builder, resultId);
            Field(builder, sessionId);
            Field(builder, raceId);
            Field(builder, eventId);
            Field(builder, contentVersion);
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
