using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FamilyRacing.Core.Racing;

namespace FamilyRacing.Core.Networking
{
    // LAN session wire vocabulary (protocol 1) and its bounded codec.
    //
    // A message is a single line:  FRN1|<type>|<key>=<value>|...
    // Values escape four characters with a backslash prefix so keys and
    // subfields can never be forged by crafted ids:
    //   \ -> \\   | -> \p   = -> \e   , -> \c
    // TryDecode refuses anything above MaxWireMessageChars, missing the
    // magic, or carrying a malformed field. Transport ids (connection
    // handles) never appear inside payloads; the host derives ownership
    // from the authenticated connection, not the message.
    public static class NetworkProtocol
    {
        public const int LanProtocolVersion = NetworkSettings.LanProtocolVersion;

        private const string Magic = "FRN1";

        // --- message types -------------------------------------------------
        // Client -> host.
        public const string MsgJoinRequest = "join.request";
        public const string MsgReadySet = "ready.set";
        public const string MsgVehicleSet = "vehicle.set";
        public const string MsgPauseRequest = "pause.request";
        public const string MsgLeave = "leave";
        // Client -> host, Task 8 result flow.
        public const string MsgResultRequest = "result.request";
        public const string MsgResultAck = "result.ack";
        // Host -> client.
        public const string MsgJoinAccept = "join.accept";
        public const string MsgJoinReject = "join.reject";
        public const string MsgSessionState = "session.state";
        public const string MsgSessionClosed = "session.closed";
        public const string MsgOpResult = "op.result";
        public const string MsgResultOffer = "result.offer";

        // --- field keys -----------------------------------------------------
        // join.request: v, gv, cv, pid, av, ci, cid, cred
        // join.accept:  sid, ph, pid, slot, ev, cl, ep, cred, cid
        // join.reject:  code, cid
        // session.state: sid, ph, ev, cl, pd, pr, p0..p3
        //   (each pN is the comma-joined tuple
        //    pid,slot,avatar,colorIndex,vehicle,connected,ready,control,epoch;
        //    empty means the slot is free)
        // session.closed: rsn
        // ready.set: cid, rdy | vehicle.set: cid, veh | pause.request: cid
        // op.result: cid, st, code | leave: pid
        // result.request: cid, race
        // result.offer:  rid, sid, race, ev, cv, fin, fht, fet, cc, rc, r0..rN
        //   (each rN is the comma-joined racer tuple
        //    pos,slot,pid,veh,attr,out,ft,laps,coins — immutable facts only;
        //    delivery metadata never crosses the wire)
        // result.ack: rid, st, code

        public static string Encode(string type, params string[] keyValues)
        {
            if (string.IsNullOrEmpty(type)) throw new ArgumentException("type");
            if (keyValues == null || keyValues.Length % 2 != 0)
                throw new ArgumentException("keyValues must be key/value pairs");
            var builder = new StringBuilder(Magic.Length + type.Length + 64);
            builder.Append(Magic).Append('|').Append(type);
            for (var i = 0; i + 1 < keyValues.Length; i += 2)
            {
                builder.Append('|').Append(keyValues[i]).Append('=')
                    .Append(Escape(keyValues[i + 1] ?? ""));
            }
            var encoded = builder.ToString();
            if (encoded.Length > NetworkSettings.MaxWireMessageChars)
                throw new ArgumentException("Encoded message exceeds the wire bound.");
            return encoded;
        }

        public static bool TryDecode(string payload, out string type,
            out Dictionary<string, string> fields)
        {
            type = null;
            fields = null;
            if (string.IsNullOrEmpty(payload)
                || payload.Length > NetworkSettings.MaxWireMessageChars
                || !payload.StartsWith(Magic + "|", StringComparison.Ordinal))
                return false;
            var body = payload.Substring(Magic.Length + 1);
            var tokens = SplitUnescaped(body, '|');
            if (tokens.Count < 1 || tokens[0].Length == 0) return false;
            type = tokens[0];
            fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 1; i < tokens.Count; i++)
            {
                var eq = IndexOfUnescaped(tokens[i], '=');
                if (eq <= 0) return false;
                var key = tokens[i].Substring(0, eq);
                var value = Unescape(tokens[i].Substring(eq + 1));
                if (fields.ContainsKey(key)) return false;
                fields[key] = value;
            }
            return true;
        }

        // M3 content convention: a vehicle id's class is the segment before
        // its first '-' (car-01 -> car, motorcycle-01 -> motorcycle). The
        // single implementation lives on VehicleTuning so the content layer
        // and the race coordinator share it; class-pure selection is
        // enforced by lan.configure/lan.vehicle on top of this rule.
        public static string VehicleClass(string vehicleId)
        {
            return FamilyRacing.Core.Driving.VehicleTuning.ClassIdOf(vehicleId);
        }

        // --- Task 8: immutable result offer codec ---------------------------
        // The wire form carries only immutable result facts — the same
        // facts the canonical fingerprint covers — so a redelivered offer
        // always encodes byte-identical payloads. Delivery metadata is
        // stripped by construction. Bounded by MaxWireMessageChars like
        // every other session message.
        public static string EncodeResultOffer(RaceResult result)
        {
            if (result == null) throw new ArgumentNullException("result");
            var racers = result.racers != null
                ? result.racers.Count : 0;
            var fields = new List<string>
            {
                "rid", result.resultId,
                "sid", result.sessionId,
                "race", result.raceId,
                "ev", result.eventId,
                "cv", result.contentVersion,
                "fin", result.finalized ? "1" : "0",
                "fht", result.finalizedHostTick.ToString(
                    CultureInfo.InvariantCulture),
                "fet", result.finalizedElapsedTicks.ToString(
                    CultureInfo.InvariantCulture),
                "cc", result.completionCoins.ToString(
                    CultureInfo.InvariantCulture),
                "rc", racers.ToString(CultureInfo.InvariantCulture)
            };
            for (var i = 0; i < racers; i++)
            {
                var racer = result.racers[i];
                fields.Add("r" + i.ToString(CultureInfo.InvariantCulture));
                fields.Add(racer == null ? "" : string.Join(",", new[]
                {
                    racer.position.ToString(CultureInfo.InvariantCulture),
                    racer.slot.ToString(CultureInfo.InvariantCulture),
                    racer.participantId ?? "",
                    racer.vehicleId ?? "",
                    racer.attribution ?? "",
                    racer.outcome ?? "",
                    racer.finishTicks.ToString(CultureInfo.InvariantCulture),
                    racer.lapsCompleted.ToString(CultureInfo.InvariantCulture),
                    racer.coinsAwarded.ToString(CultureInfo.InvariantCulture)
                }));
            }
            return Encode(MsgResultOffer, fields.ToArray());
        }

        // Decodes the immutable facts of a result.offer. Delivery state
        // and the fingerprint are never parsed from the wire — the client
        // recomputes the fingerprint locally so a forged fingerprint can
        // never launder a tampered result.
        public static bool TryDecodeResultOffer(
            IReadOnlyDictionary<string, string> fields, out RaceResult result)
        {
            result = null;
            if (fields == null) return false;
            string resultId, sessionId, raceId, eventId, contentVersion, fin;
            if (!fields.TryGetValue("rid", out resultId)
                || !fields.TryGetValue("sid", out sessionId)
                || !fields.TryGetValue("race", out raceId)
                || !fields.TryGetValue("ev", out eventId)
                || !fields.TryGetValue("cv", out contentVersion)
                || !fields.TryGetValue("fin", out fin)) return false;
            long hostTick, elapsed;
            int completionCoins, count;
            if (!long.TryParse(Value(fields, "fht"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out hostTick)
                || !long.TryParse(Value(fields, "fet"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out elapsed)
                || !IntField(fields, "cc", out completionCoins)
                || !IntField(fields, "rc", out count)
                || count < 0
                || count > NetworkSettings.ParticipantCapacity) return false;
            var decoded = new RaceResult
            {
                resultId = resultId ?? "",
                sessionId = sessionId ?? "",
                raceId = raceId ?? "",
                eventId = eventId ?? "",
                contentVersion = contentVersion ?? "",
                finalized = fin == "1",
                finalizedHostTick = hostTick,
                finalizedElapsedTicks = elapsed,
                completionCoins = completionCoins
            };
            for (var i = 0; i < count; i++)
            {
                var raw = Value(fields,
                    "r" + i.ToString(CultureInfo.InvariantCulture));
                var parts = raw.Split(',');
                if (parts.Length != 9) return false;
                int position, slot, laps, coins;
                long finishTicks;
                if (!int.TryParse(parts[0], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out position)
                    || !int.TryParse(parts[1], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out slot)
                    || !long.TryParse(parts[6], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out finishTicks)
                    || !int.TryParse(parts[7], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out laps)
                    || !int.TryParse(parts[8], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out coins)) return false;
                decoded.racers.Add(new RaceResultEntry
                {
                    position = position,
                    slot = slot,
                    participantId = parts[2],
                    vehicleId = parts[3],
                    attribution = parts[4],
                    outcome = parts[5],
                    finishTicks = finishTicks,
                    lapsCompleted = laps,
                    coinsAwarded = coins
                });
            }
            result = decoded;
            return true;
        }

        private static string Value(IReadOnlyDictionary<string, string> fields,
            string key)
        {
            string value;
            return fields != null && fields.TryGetValue(key, out value)
                && value != null ? value : "";
        }

        public static bool IntField(IReadOnlyDictionary<string, string> fields,
            string key, out int value)
        {
            value = 0;
            string raw;
            if (fields == null || !fields.TryGetValue(key, out raw)) return false;
            return int.TryParse(raw, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value);
        }

        private static string Escape(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '|': builder.Append("\\p"); break;
                    case '=': builder.Append("\\e"); break;
                    case ',': builder.Append("\\c"); break;
                    default: builder.Append(c); break;
                }
            }
            return builder.ToString();
        }

        private static string Unescape(string value)
        {
            var builder = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == '\\' && i + 1 < value.Length)
                {
                    switch (value[i + 1])
                    {
                        case '\\': builder.Append('\\'); i++; continue;
                        case 'p': builder.Append('|'); i++; continue;
                        case 'e': builder.Append('='); i++; continue;
                        case 'c': builder.Append(','); i++; continue;
                    }
                }
                builder.Append(value[i]);
            }
            return builder.ToString();
        }

        private static List<string> SplitUnescaped(string body, char separator)
        {
            var tokens = new List<string>(8);
            var start = 0;
            for (var i = 0; i < body.Length; i++)
            {
                if (body[i] == '\\') { i++; continue; }
                if (body[i] != separator) continue;
                tokens.Add(body.Substring(start, i - start));
                start = i + 1;
            }
            tokens.Add(body.Substring(start));
            return tokens;
        }

        private static int IndexOfUnescaped(string token, char target)
        {
            for (var i = 0; i < token.Length; i++)
            {
                if (token[i] == '\\') { i++; continue; }
                if (token[i] == target) return i;
            }
            return -1;
        }
    }
}
