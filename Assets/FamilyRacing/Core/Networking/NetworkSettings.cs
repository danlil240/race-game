namespace FamilyRacing.Core.Networking
{
    // Single definition of the milestone LAN constants shared by Core
    // policy and the Runtime transport adapter. Change them here and in
    // the tests that pin them — never scatter literals.
    public static class NetworkSettings
    {
        // LAN wire protocol spoken by NetworkProtocol messages. Exact
        // equality is required to share a session.
        public const int LanProtocolVersion = 1;

        // Observation schema emitted by FoundationState once the network
        // slice exists. Profile schema (1 -> 2 in a later task) is a
        // separate version and stays in Core/Profiles.
        public const int StateSchemaVersion = 3;

        // Compatibility fingerprint for gameplay rules: peers must report
        // the exact same value before a slot is allocated. Content version
        // comes from the DrivingContentCatalog and is checked separately.
        public const string GameplayVersion = "gameplay-1";

        // Four racers total; AI fills slots without a human (Task 4).
        public const int ParticipantCapacity = 4;

        // In-flight asynchronous commands are bounded; excess is refused
        // without mutation.
        public const int PendingOperationCapacity = 128;

        // Binding milestone timing budget: a failed join and a silent peer
        // must each resolve within 5 s of monotonic wall time. The Runtime
        // transport derives its connect-attempt and disconnect budgets
        // from these; Core derives join/request expiry from them.
        public const int JoinTimeoutMilliseconds = 5000;
        public const int PeerTimeoutMilliseconds = 5000;
        public const int ConnectAttemptIntervalMilliseconds = 500;

        // Wall-clock budgets inside the session service: a join that never
        // reaches accept/reject, or a client request the host never
        // acknowledges, resolves as a failure instead of hanging.
        public const double JoinTimeoutSeconds = JoinTimeoutMilliseconds / 1000.0;
        public const double RequestTimeoutSeconds = PeerTimeoutMilliseconds / 1000.0;

        // Default UDP ports; configurable per start call so several
        // processes can share one machine.
        public const ushort DefaultGameplayPort = 47777;
        public const ushort DefaultDiscoveryPort = 47778;

        // Hard ceiling for a single encoded session-protocol message.
        public const int MaxWireMessageChars = 2048;
    }
}
