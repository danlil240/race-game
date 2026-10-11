using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FamilyRacing.Automation
{
    // Strict wire parsing for the loopback validation protocol. Every
    // inbound frame must be a single, complete, bounded JSON object;
    // oversize, deep, duplicate-keyed, trailing-content or malformed frames
    // are rejected before dispatch.
    internal static class ValidationJson
    {
        internal const int MaxFrameChars = 16 * 1024;
        internal const int MaxDepth = 16;

        internal static bool TryParseFrame(string line, out JObject frame, out string error)
        {
            frame = null;
            error = null;
            if (string.IsNullOrEmpty(line) || line.Length > MaxFrameChars)
            {
                error = "frameTooLarge";
                return false;
            }
            try
            {
                using (var reader = new JsonTextReader(new StringReader(line)))
                {
                    reader.MaxDepth = MaxDepth;
                    reader.DateParseHandling = DateParseHandling.None;
                    reader.FloatParseHandling = FloatParseHandling.Double;
                    frame = JObject.Load(reader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                    });
                    // Reject anything after the closing object: the protocol is
                    // exactly one frame per line.
                    if (reader.Read())
                    {
                        error = "trailingContent";
                        frame = null;
                        return false;
                    }
                }
            }
            catch (JsonException) { error = "invalidJson"; frame = null; return false; }
            catch (InvalidCastException) { error = "invalidJson"; frame = null; return false; }
            catch (FormatException) { error = "invalidJson"; frame = null; return false; }
            if (frame == null || frame.Type != JTokenType.Object || Depth(frame) > MaxDepth)
            {
                error = "frameTooDeep";
                frame = null;
                return false;
            }
            return true;
        }

        internal static int Depth(JToken token)
        {
            if (token == null) return 0;
            var depth = 1;
            foreach (var child in token.Children())
                depth = Math.Max(depth, 1 + Depth(child));
            return depth;
        }

        internal static string Text(JObject obj, string name)
        {
            var token = obj?[name];
            return token != null && token.Type == JTokenType.String ? (string)token : null;
        }

        // Integer fields that overflow Int32 are treated as absent rather
        // than throwing on the game thread.
        internal static int? Int(JObject obj, string name)
        {
            var token = obj?[name];
            if (token == null || token.Type != JTokenType.Integer) return null;
            try
            {
                var value = token.Value<long>();
                return value >= int.MinValue && value <= int.MaxValue ? (int?)value : null;
            }
            catch (Exception) { return null; }
        }

        internal static string Line(JObject record)
        {
            return record.ToString(Formatting.None);
        }
    }
}
