using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

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
        // Host -> client.
        public const string MsgJoinAccept = "join.accept";
        public const string MsgJoinReject = "join.reject";
        public const string MsgSessionState = "session.state";
        public const string MsgSessionClosed = "session.closed";
        public const string MsgOpResult = "op.result";

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
        // its first '-' (car-01 -> car, motorcycle-01 -> motorcycle). Ids
        // without a separator are their own class. Class-pure selection is
        // enforced by lan.configure/lan.vehicle on top of this rule.
        public static string VehicleClass(string vehicleId)
        {
            if (string.IsNullOrEmpty(vehicleId)) return "";
            var dash = vehicleId.IndexOf('-');
            return dash < 0 ? vehicleId : vehicleId.Substring(0, dash);
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
