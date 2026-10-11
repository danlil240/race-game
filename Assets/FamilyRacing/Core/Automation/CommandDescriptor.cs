using System;
using System.Collections.Generic;

namespace FamilyRacing.Core.Automation
{
    [Serializable]
    public sealed class CommandParameterDescriptor
    {
        public string name;
        public string type;
        public bool required;
        public List<string> allowedValues;
    }

    // One discoverable action. `available` and `reason` reflect the current
    // selection, screen, and save state at discovery time.
    [Serializable]
    public sealed class CommandDescriptor
    {
        public string action;
        public bool available;
        public string reason;
        public List<CommandParameterDescriptor> parameters;
    }
}
