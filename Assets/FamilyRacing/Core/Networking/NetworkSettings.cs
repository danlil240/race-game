namespace FamilyRacing.Core.Networking
{
    // Single definition of the milestone LAN constants shared by Core
    // policy and the Runtime transport adapter. Change them here and in
    // the tests that pin them — never scatter literals.
    public static class NetworkSettings
    {
        // LAN wire protocol spoken by NetworkProtocol messages. Exact
        // equality is required to share a session. Protocol 2 was the
        // physical replication epoch: typed P1 gameplay packets, compact
        // versioned physical state inside snapshots, and fingerprint-bound
        // admission — a protocol-1 peer is fenced before slot allocation.
        // Protocol 3 (M5 Task 5) adds the authoritative gadget world to
        // the snapshot — pickups, patches and per-racer rows — so a
        // protocol-2 client lacking the gadget contract is fenced before
        // a seat is dealt rather than silently racing without items.
        public const int LanProtocolVersion = 3;

        // Observation schema emitted by FoundationState once the network
        // slice exists. Profile schema (1 -> 2 in a later task) is a
        // separate version and stays in Core/Profiles. Schema 4 adds the
        // physical-state surfaces: racer DTOs may carry the compact
        // NetworkPhysicsState projection and host descriptors the content
        // fingerprint. Schema 5 (M4 Task 2) is additive presentation truth:
        // the applied render profile and a bounded render-performance
        // summary on PresentationState — never frame-sized buffers, and
        // nothing that crosses the gameplay wire DTO. Schema 6 (M5
        // Task 4) is again additive: the local solo-race slice and the
        // network session's current-race identity. Schema 7 (M6 Task 3)
        // is additive once more: the progression surface carries the
        // ordered world rows (`progression.worlds`) and the
        // adventure-complete verdict — pure observation, no wire change.
        public const int StateSchemaVersion = 7;

        // Compatibility fingerprint for gameplay rules: peers must report
        // the exact same value before a slot is allocated. Content version
        // comes from the DrivingContentCatalog and is checked separately.
        // gameplay-3 (M5 Task 2): the gameplay-rules contract now includes
        // the first-world GameplayCatalog — objectives, difficulty presets,
        // economy and gadget definitions — all bound by the catalog
        // compatibility fingerprint, so M4 gameplay-2 peers can never be
        // silently admitted under the new rules set.
        // gameplay-4 (M6 Task 1): the campaign contract is now the ordered
        // multiworld catalog — every world's membership, order, gates and
        // rewards are fingerprinted — so a gameplay-3 peer holding a
        // single-world catalog is fenced before a seat is dealt.
        public const string GameplayVersion = "gameplay-4";

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

        // Task 8: immutable result retention and redelivery. Results are
        // retained for the whole host session — across rematches — up to
        // this many races; at the cap another start is refused with a
        // session-full diagnostic rather than silently evicting history.
        // An unacknowledged offer is re-sent no more often than this.
        public const int ResultRetentionCapacity = 128;
        public const double ResultRedeliverySeconds = 2.0;

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
        // Task 7 cornering lookahead: the AI scans the authored route
        // ahead — this many seconds of travel at the current speed plus
        // a fixed floor — and brakes toward each vertex's yaw-limited
        // cornering speed (a turn of angle θ over arc s is followable
        // at |v| ≤ classSteeringRate·s/θ). The demand is the ordinary
        // held brake a human presses, scaled by overspeed.
        public const float AiBrakeLookaheadSeconds = 1.4f;
        public const float AiBrakeMinLookaheadMeters = 8f;
        // Brake demand ramps linearly across this overspeed above the
        // corner target, so a barely-fast approach feathers instead of
        // slamming.
        public const float AiBrakeOverspeedSpan = 6f;
        // Safety factor on the yaw-limited cornering speed — turning
        // harder than the limit understeers off the route.
        public const float AiCorneringMargin = 0.8f;
        // A grounded near-stopped streak this long requests the ordinary
        // recover input — the same one-shot a human presses.
        public const float AiRecoverStuckSpeed = 0.75f;
        public const int AiRecoverStuckTicks = 45; // 1.5 s at 30 Hz
        // Displacement watchdog, windowed: a grounded racer covering
        // less than this span across one window is stuck even while its
        // speedometer reads motion — wall-ramming and ramp-stall loops
        // move at real speed with no net progress. Under ~0.6 m/s of
        // honest advance the check can never trip a moving car.
        public const float AiProgressStuckMeters = 3f;
        public const int AiProgressStuckTicks = 150; // 5 s at 30 Hz
        // Per-slot lateral lane spread (metres between lanes): slots aim
        // at (slot - centre) * spacing off the route line so four AI
        // racers fan out instead of tailgating.
        public const float AiLaneSpacingMeters = 2f;
        // M5 Task 5 gadget spend policy: the follower waits this many
        // held ticks scaled by (1 - willingness) before pressing, so a
        // fully willing seat fires promptly and a zero-willingness seat
        // never fires. The default applies when no difficulty preset
        // authors a willingness (LAN constant).
        public const float AiGadgetSettleTicks = 90f;
        public const float AiGadgetDefaultWillingness = 0.6f;
        // Past the settle delay plus this patience window a held item is
        // spent even without its preferred window — a held slot can
        // never deadlock an AI seat. Deterministic (heldTicks only).
        public const int AiGadgetPatienceTicks = 120;
        // A rival this close behind makes the foam trap worth spending;
        // a live rival patch this close ahead arms the shield.
        public const float AiGadgetRivalBehindMeters = 15f;
        public const float AiGadgetPatchAheadMeters = 30f;
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

        // Grace between broadcasting session.closed and tearing the host
        // link down: the reliable-ordered pipeline only delivers what the
        // driver still has time to flush, so an immediate Stop() can drop
        // the close notification entirely and every client would fall
        // back to the generic transport "disconnect" reason.
        public const double SessionCloseFlushSeconds = 0.5;

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
        // The 32 KiB bound covers the physical-backend wire: the compact
        // NetworkPhysicsState projection per racer plus the shared race
        // envelope crosses 16 KiB within the first minute of a real race
        // (live evidence: run-physics-t11-s, snapshotOverflow after 45
        // publishes) — arcade snapshots sit far below either bound.
        public const int SnapshotEveryTicks = 2;
        public const int SnapshotCeilingBytes = 32 * 1024;
        // Typed payload limits for the gameplay wire: client->host input
        // packets and the reliable host->client event batches.
        public const int MaxInputPacketBytes = 4 * 1024;
        public const int MaxEventBatchBytes = 8 * 1024;
        public const int EventBatchCapacity = 64;
        // Bounded exactly-once event consumption on the receiving side.
        public const int SeenEventIdCapacity = 512;

        // --- M5 Task 5: authoritative gadgets -------------------------
        // Authored pickup sites per course, live foam patches in the
        // shared world, and the per-racer resolved-patch list are all
        // hard-capped so a four-racer gadget load can never overflow the
        // snapshot ceiling or grow unboundedly under spam.
        public const int GadgetPickupCapacity = 32;
        public const int GadgetPatchCapacity = 16;
        public const int GadgetImmunePatchCapacity = 32;

        // --- Task 6: client prediction + remote presentation -----------
        // Replay bound for the local predicted motor: pending sent-but-
        // unacknowledged input samples never exceed this; overflow
        // explicitly resynchronizes (clears the backlog and requests full
        // state) instead of replaying a stale burst. Matches the host
        // input-queue bound — the two ends bound the same stream.
        public const int PredictionInputCapacity = InputQueueCapacity;
        // Remote pose buffer: ~4.3 s of 15 Hz snapshot cadence retained
        // per remote racer for interpolation.
        public const int InterpolationBufferCapacity = 64;
        // Remote racers render this far behind the newest host tick.
        public const double InterpolationDelaySeconds = 0.1;
        // Past the newest buffered pose, extrapolation runs at most this
        // far, then the last pose holds and reports stale.
        public const double ExtrapolationLimitSeconds = 0.1;
        // Correction policy: the simulated pose corrects immediately on
        // every reconcile; the *visual* blends ordinary errors over
        // CorrectionBlendSeconds, while ownership change, recovery, or an
        // error past CorrectionSnapMeters snaps instantly. Errors under
        // CorrectionEpsilonMeters are noise — reconciliation is not
        // assumed bit-identical across Android/Windows physics.
        public const float CorrectionSnapMeters = 3f;
        public const double CorrectionBlendSeconds = 0.1;
        public const float CorrectionEpsilonMeters = 0.01f;
        // Task 10: the orientation leg of the same policy — full-attitude
        // errors past CorrectionSnapDegrees snap, under
        // CorrectionEpsilonDegrees they are PhysX-approximation noise.
        public const float CorrectionSnapDegrees = 45f;
        public const float CorrectionEpsilonDegrees = 0.5f;
        // Replay work budget per rendered frame: at most this many
        // logical ticks are replayed across EVERY correction the frame
        // processes — a packet burst can never multiply a PhysX replay
        // burst. A correction that would exceed the remaining budget
        // holds the fresh authoritative pose and resynchronizes instead
        // of spreading a partially replayed world across later frames.
        public const int PredictionReplayTicksPerFrame = 12;
    }
}
