using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace FamilyRacing.Core.Automation
{
    // Immutable wire command: routing headers, action, and a copied ordinal
    // parameter map wrapped read-only so neither later caller mutation nor a
    // cast back to a writable dictionary can change a submitted command.
    public sealed class GameCommand
    {
        public int ProtocolVersion { get; }
        public string RunId { get; }
        public string CommandId { get; }
        public string ParticipantId { get; }
        public string Action { get; }
        public IReadOnlyDictionary<string, string> Parameters { get; }

        public GameCommand(int protocolVersion, string runId, string commandId,
            string participantId, string action, IReadOnlyDictionary<string, string> parameters)
        {
            ProtocolVersion = protocolVersion;
            RunId = runId;
            CommandId = commandId;
            ParticipantId = participantId;
            Action = action;
            var copy = new Dictionary<string, string>(StringComparer.Ordinal);
            if (parameters != null)
                foreach (var entry in parameters) copy[entry.Key] = entry.Value;
            Parameters = new ReadOnlyDictionary<string, string>(copy);
        }
    }
}
