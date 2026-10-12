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

        // One authoritative race clock shared by every racer in a session:
        // 30 simulated boundaries per real second, and once the first
        // racer finishes, the rest have 60 unpaused race seconds before
        // they are recorded DNF.
        public const int SimulationTicksPerSecond = 30;
        public const int FinishGraceSeconds = 60;
        public const long FinishGraceTicks =
            (long)FinishGraceSeconds * SimulationTicksPerSecond;

        // Authored completion reward facts for network races: finishing
        // the prescribed laps in a finalized race awards this many coins;
        // DNFs and aborted races award nothing (Task 8 applies them).
        public const int LanCompletionCoins = 20;

        // In-flight asynchronous commands are bounded; excess is refused
        // without mutation.
        public const int PendingOperationCapacity = 128;

        // --- Task 4: honest AI driver + bounded racer contact -----------
        // The AI emits the same bounded DriverInput humans produce on the
        // same normalized tuning — no hidden catch-up multiplier and no
        // teleports. These values are shared policy constants, not secret
        // tuning; tests pin them alongside the rules that consume them.
        //
        // Waypoint following: a route waypoint counts as reached inside
        // this radius; a waypoint left behind the travel direction inside
        // PassedFactor times that radius is skipped instead of orbited.
        public const float AiWaypointReachMeters = 5f;
        public const float AiWaypointPassedFactor = 3f;
        // Heading error -> steering proportional gain (input stays ±1).
        public const float AiSteeringGain = 3f;
        // Hold drift only through real steering demand and real speed;
        // hold boost only on straights with a nearly full meter.
        public const float AiDriftSteerThreshold = 0.55f;
        public const float AiDriftMinSpeed = 5f;
        public const float AiBoostMinCharge = 0.9f;
        public const float AiBoostMaxSteer = 0.2f;
        // A grounded near-stopped streak this long requests the ordinary
        // recover input — the same one-shot a human presses.
        public const float AiRecoverStuckSpeed = 0.75f;
        public const int AiRecoverStuckTicks = 45; // 1.5 s at 30 Hz
        // Per-slot lateral lane spread (metres between lanes): slots aim
        // at (slot - centre) * spacing off the route line so four AI
        // racers fan out instead of tailgating.
        public const float AiLaneSpacingMeters = 2f;
        //
        // Racer contact: bodies inside this ground-plane radius push
        // apart, capped by DriftBoostRules.NudgeMaxDeltaSpeed per applied
        // step. The radius stays below CourseDefinition's
        // MinSpawnSeparationMeters (3.5) so a legal grid never self-nudges
        // on the line; vertically separated bodies (e.g. mid-ramp jump)
        // never contact.
        public const float ContactRadiusMeters = 3f;
        public const float ContactVerticalRangeMeters = 1.25f;
        public const float ContactNudgeImpulsePerMeter = 4f;

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

        // --- Task 5: authoritative replication -------------------------
        // Input stream: one sample per local 30 Hz tick; a packet carries
        // up to this many recent samples for loss tolerance.
        public const int InputPacketSampleCapacity = 4;
        // Replay bound: 120 samples per racer. Overflow explicitly
        // resynchronizes — the backlog clears instead of replaying stale
        // bursts, and bursts never grant extra simulation steps (at most
        // one new sample per racer per host tick).
        public const int InputQueueCapacity = 120;
        // After this many host ticks without a usable sample the held
        // commands/steering clear to neutral; the connection timeout then
        // hands control to AI.
        public const int InputStaleTicks = 15;
        // How far ahead of the newest seen sequence a frame may claim
        // before it is rejected as bogus future traffic.
        public const long InputSequenceWindow = 120;
        // One-shot action ids are bounded per frame and per retained dedup
        // set; the dedup set survives replay/reconnect/rematch boundaries.
        public const int MaxFrameActionIds = 8;
        public const int ActionIdLengthMax = 64;
        public const int ActionIdDedupCapacity = 256;
        // Accepted-but-not-yet-applied one-shot ids per racer; beyond this
        // an id stays recorded-seen (cancelled) rather than queued.
        public const int PendingInboundActions = 16;

        // Full four-racer snapshots publish on every second host tick —
        // 15 Hz against the 30 Hz simulation — with a hard byte ceiling.
        // Overflow fails explicitly rather than truncating racer state.
        public const int SnapshotEveryTicks = 2;
        public const int SnapshotCeilingBytes = 16 * 1024;
        // Typed payload limits for the gameplay wire: client->host input
        // packets and the reliable host->client event batches.
        public const int MaxInputPacketBytes = 4 * 1024;
        public const int MaxEventBatchBytes = 8 * 1024;
        public const int EventBatchCapacity = 64;
        // Bounded exactly-once event consumption on the receiving side.
        public const int SeenEventIdCapacity = 512;
    }
}
