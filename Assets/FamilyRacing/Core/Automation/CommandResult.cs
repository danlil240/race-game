using System;

namespace FamilyRacing.Core.Automation
{
    // Terminal command outcome. Codes are stable short tokens; they never carry
    // raw exception text, save contents, or filesystem paths.
    [Serializable]
    public sealed class CommandResult
    {
        public const string Applied = "applied";
        public const string Rejected = "rejected";
        public const string Failed = "failed";
        // Non-terminal: the request is in flight and completes later via a
        // terminal outcome (applied/rejected/failed). Accepted results are
        // cached under their command identity and completed exactly once.
        public const string Accepted = "accepted";

        public string commandId;
        public string action;
        public string status;
        public string code;
        public long tick;
        public bool duplicate;

        internal CommandResult Copy()
        {
            return new CommandResult
            {
                commandId = commandId,
                action = action,
                status = status,
                code = code,
                tick = tick,
                duplicate = duplicate
            };
        }
    }
}
