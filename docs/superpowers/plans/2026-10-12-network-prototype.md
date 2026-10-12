# Milestone 3: Network Prototype Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. This document is a proposed plan, not authorization to begin implementation.

**Goal:** A family can host on either a tablet or PC, join over offline home Wi-Fi, race with four total racers, pause together, reconnect to the same racer, receive results once, and rematch.

**Architecture:** Keep authoritative race rules, session policy, AI input, prediction buffers, and reward application in Unity-free Core code. Add an NGO/Unity Transport adapter for LAN traffic and a separate UDP discovery adapter. Reuse the existing arcade motor and command/state-stream path; clients predict their own movement and render remote racers from host snapshots.

**Tech Stack:** Unity 6000.3.26f1; C# 9 / .NET Standard 2.1-compatible Core; existing URP, Input System, UI Toolkit, and NUnit setup; Netcode for GameObjects 2.7.0 and Unity Transport 2.6.0.

**Spec:** [GAME_PLAN.md](../../../GAME_PLAN.md), especially sections 3 (local multiplayer, commands/state, persistence) and 4 (M3 acceptance). `IMPLEMENTATION_STATUS.md` explicitly treats that approved plan as the design specification. No new product scope is assumed approved here.

## Global constraints

- Platforms: **Android tablets and Windows PC**.
- Required tablet: **Samsung Galaxy Tab A7 Lite SM-T220; assume the weaker 3 GB configuration**.
- Multiplayer: **Two to four people over home Wi-Fi; four racers total**.
- Connectivity: **All gameplay works offline after installation**.
- **Any supported tablet or PC can host. Tablet-to-tablet racing must work without a PC.**
- **Require matching gameplay/content versions.**
- **Normalize vehicle performance within the selected class, including upgrades. Keep cosmetics.**
- **The host owns checkpoints, pickups, gadget effects, standings, and final results.** Only implemented systems are included in this slice; gadgets/pickups remain explicitly unavailable.
- **Start with 30 Hz simulation and 15 Hz snapshots.**
- **A disconnected player retains their slot for the remainder of the race.**
- **AI actions during absence do not earn human skill-bounty credit.**
- **Closing the host ends the session.** There is no host migration.
- **Aborted, unfinished races do not award completion rewards.**
- All new controls use shared commands and complete reconstructable state streams. Validation sockets remain token-protected and loopback-only, with ADB forwarding on Android; ordinary builds exclude them.
- Preserve Hebrew/English UI, camera preferences, profile safety, bounded input, and the existing solo tutorial/practice flow.
- Quality gates determine completion; there is no fixed deadline. Existing US$100 budget is a cap, not purchase authorization. This milestone proposes no purchases.

## Review focus

1. Old or duplicated input after reconnect must never control a reclaimed racer or repeat a recovery pulse — Tasks 2, 5, 7.
2. Pause must freeze countdown/race/AI/finish timers while discovery, heartbeat, command outcomes, and state sequences continue — Tasks 3, 7, 9.
3. A correction at a jump, wall contact, or recovery must not duplicate checkpoint, landing, boost, or reward events — Tasks 4, 6.
4. A save failure, lost result acknowledgment, restart, or duplicated result must not double coins or claim persistence early — Task 8.
5. Blocked discovery, denied permission, a second network adapter, or Android backgrounding must yield a usable diagnostic or recovery flow — Tasks 1, 7, 9, 10.

## Starting point and scope

Baseline inspected: commit `e006fff`, branch `fix/driving-playtest-1`, clean working tree on 2026-10-12. Planning did not rerun the game or certify the old binaries.

The latest **recorded** M2 closeout gate is Core 362/362, EditMode 393 passed plus one explicit voice-clip Ignore, PlayMode 121/121, Python 71/71, verify-tooling 12/12, and a fresh Windows development player with DrivingScenario 28 and AgentScenario 21 checks. See [M2 closeout report](../../tasks/m2-closeout-report.md). Earlier README counts and the earlier Android APK are not evidence for this later revision.

Relevant implementation facts:

- `FoundationService` owns one `DrivingSession`; `DrivingWorld` owns one visible vehicle. Neither represents a multiplayer race.
- `ArcadeMotor.Step` already accepts an optional `IRacerContactSource`; `DrivingSession.Step` does not yet pass one through.
- `FoundationService` accepts only the local participant, and `CommandResult` currently has terminal statuses only. Joining and host acknowledgments require an asynchronous command lifecycle.
- State schema is 2. Profile schema is 1 and has coins but no applied-result identities.
- NGO and Transport are absent from `Packages/manifest.json`.
- Android minimum SDK is 25; target SDK is automatic (`0`). Read the actual exported target before deciding the permission flow.
- `Tools/CoreTests/FamilyRacing.CoreTests.csproj` lists Core folders explicitly; new folders must be included. The offline runner already scans Core recursively.

**Included:** one graybox course; separate car/motorcycle LAN event configurations using the two existing vehicles; 1–4 human connections with AI filling all other race slots; a lobby, ready/start, four-racer simulation, prediction, collisions, standings, pause, reconnect, results, a minimal completion coin transaction, and rematch. One local human profile per process/device.

**Deferred:** new worlds/art/animals, eight-vehicle production content, gadgets, full difficulty selection, campaign stars, bounties, upgrades, Quick Race/Time Trial menus, public internet services, accounts, relay, split-screen, controllers, and host migration. LAN exposes every model/course actually shipped in the content catalog; it does not manufacture future content. Do not report the overall progression or gadget feature as implemented.

The minimum AI and coin transaction are supporting parts of the network prototype: AI proves vacant-slot/disconnect behavior; a real persisted coin transaction proves duplicate result delivery. Broader gameplay remains M5.

## Carry-forward gates and execution order

M1 and M2 remain open. Obtain the A7 Lite and second tablet early; complete offline launch, OS touch/keyboard checks, owner driving-feel review, human Hebrew review, child onboarding, and licensed voice-clip generation/listening. The existing TTS tool can be used when the required credentials/assets are available; do not substitute fake clips or remove the Ignore without real assets.

Planning and Windows development can proceed while access is arranged, but do not mark prior milestones accepted. Before the M3 closeout, rebuild Android from the M3 revision and run the full physical matrix. A fourth supported device must be borrowed for four-human physical qualification; four local Windows processes are useful automation evidence, not that qualification.

Work in dependency order:

`1 transport smoke → 2 session/commands → 3 multi-racer rules → 4 AI/contacts → 5 replication → 6 prediction → 7 lifecycle → 8 results → 9 UI/stream → 10 qualification`

Tasks 1–2 establish the seams; Tasks 3–8 deliver independently testable behavior; Task 9 completes the family-facing flow and coverage; Task 10 is the release gate. Command/state coverage is added with each task, not postponed until Task 9.

## Proposed implementation decisions

These are implementation defaults for review, not measurements or previously approved tuning. Change them in one shared settings definition and its tests if qualification shows a problem.

| Decision | Initial value / policy |
|---|---|
| Gameplay/discovery ports | UDP 47777 / 47778; validated configurable ports for multi-process tests |
| Discovery | Bounded IPv4 UDP broadcast query/response on active LAN adapters; manual IPv4:port fallback; 1-second queries, entries expire after 3 seconds |
| Join and peer timeout | 5 seconds of monotonic wall time; permission denial is reported immediately |
| Input stream | One sample per local 30 Hz tick; packet carries up to 4 recent samples; duplicate sequences ignored; host consumes at most one new sample per racer per tick |
| Stale input | After 15 host ticks without usable samples, clear held commands/steering; connection timeout then changes control to AI |
| Replay bound | 120 input samples; overflow explicitly resynchronizes and clears pending predictions |
| Remote interpolation | Start 100 ms behind host; extrapolate at most 100 ms, then hold the last pose and expose stale state |
| Corrections | Correct simulation immediately; visually blend ordinary errors over 100 ms; snap on ownership change, recovery, or errors above 3 m |
| Finish policy | All four finish, or 60 unpaused race seconds after the first finisher; remaining racers become DNF; stable slot index breaks same-tick ties |
| Minimal LAN reward | Authored 20 completion coins for finishing prescribed laps in a finalized race; no placement bonus, stars, or bounties in M3 |
| Result retention | Keep immutable results for the lifetime of the host session, including across rematches; initial cap 128 races, then refuse new starts with a clear session-full diagnostic; never silently evict |
| Compatibility | LAN protocol 1 plus exact gameplay/content fingerprints; observation schema 3 and profile schema 2 are separate versions |

Before committing the defaults, confirm in the implementation review that 60-second finish grace and 20 coins suit the prototype. They remain data, not scattered literals.

## Shared interfaces and file ownership

All paths below are relative to the repository root. New public names are proposed contracts, not claims about existing APIs. Create Unity `.meta` files with every new asset/script.

| Area | Location and responsibility |
|---|---|
| LAN policy and DTOs | `Core/Networking/`: session/participant identities, lobby transitions, permission/connection state, command operations, typed input/snapshot envelopes, pause/reconnect policy |
| Four-racer rules | `Core/Racing/NetworkRace.cs`, `NetworkRaceState.cs`, `RaceResult.cs`: one clock and result, per-racer progress, standings and attribution |
| AI and contacts | `Core/Racing/AiDriver.cs`, `RacerContacts.cs`; authored spawn grid and route in course definitions |
| Prediction | `Core/Networking/ClientPrediction.cs`, `RemoteInterpolation.cs`: bounded local replay and remote presentation samples |
| Persistence | `Core/Profiles/`: schema migration, applied-result identities, atomic coin application using the existing store |
| Unity networking | `Runtime/Networking/`: NGO lifetime, connection approval, typed serialization, UDP discovery, Android permissions, safe main-thread delivery |
| Application integration | Partial `Core/Foundation/FoundationService.Network.cs`; retain common validation/deduplication in `FoundationService.cs`; `Runtime/FoundationApp.cs` routes the active solo/LAN mode |
| Presentation | `Runtime/FoundationShell.Network.cs`, existing `DrivingWorld`, `DrivingCamera`, `VehiclePresentation`; camera follows the locally owned racer |
| Evidence | Existing bridge/stream plus `Tools/NetworkScenario.py`, `TestNetworkScenario.py`, `NetworkSoak.py`, and a physical runbook |

Contract vocabulary:

- `SessionDescriptor`: sessionId, host endpoint, LAN/gameplay/content versions, selected event/class, occupancy/capacity, joinability; no reconnect secrets.
- `ParticipantState`: stable participantId, slot, cosmetic identity, vehicleId, connected/ready, control source, ownership epoch. NGO connection IDs are transport identities only.
- `NetworkSessionState`: role, phase, localParticipantId, descriptor, four participants, shared pause/request state, last safe error, and pending operations.
- `NetworkRaceState`: sessionId/raceId, host tick, elapsed/countdown ticks, paused, four racer states, standings, immutable final result if present. Per-racer events include participantId and `human|ai` attribution.
- `NetworkInputFrame`: sessionId/raceId, participantId, ownership epoch, input sequence, client tick, `DriverInput`, and one-shot action IDs. Host derives allowed ownership from the authenticated connection, not the payload.
- `NetworkSnapshot`: snapshot sequence, full authoritative race state, and per-racer processed-input acknowledgment. Typed host-to-client gameplay data excludes local saves, command tokens, and reconnect credentials.
- `RaceResult`: unique resultId, sessionId/raceId, event/content version, finalized flag, ordered racer finish/DNF facts, human/AI attribution, and authored reward facts.

The validation state adds `network`, `networkRace` (host-confirmed state), `prediction` (local predicted motor/ack/buffer/corrections), and `remotePresentation` (interpolation facts). Keep `driving` for solo behavior; do not silently make predicted data appear authoritative through that field.

## Task 1: Prove the pinned LAN transport on the existing build pipeline

**Files:** Modify `Packages/manifest.json`, `Packages/packages-lock.json`, `Runtime/FamilyRacing.Runtime.asmdef`, `Editor/FoundationProjectSetup.cs`, `Editor/FoundationBuild.cs`; create `Runtime/Networking/LanTransport.cs`, `LanDiscovery.cs`, `LanNetworkAccess.cs`, `Editor/NetworkPrototypeSetup.cs`, `Tests/PlayMode/LanTransportPlayTests.cs`, and `docs/NETWORK_TEST_RUNBOOK.md`. Paths under Runtime/Editor/Tests are beneath `Assets/FamilyRacing/` throughout the tasks.

**Interfaces:** `LanTransport.StartHost(ushort port)`, `StartClient(string address, ushort port)`, `Stop()`; connected/disconnected/message callbacks dispatched on the Unity thread. `LanNetworkAccess` reports `available|requesting|denied|unsupported` before opening LAN sockets.

- [ ] Add smoke tests: `HostClientExchangeUsesRealTransport` requires a real handshake and payload round-trip; `StopReleasesSockets` permits restart on the same port; `DeniedAccessDoesNotOpenSockets` observes denial without a pending infinite join.
- [ ] Pin NGO 2.7.0 / UTP 2.6.0, resolve/commit the package lock, and add the actual package assembly references. Run `Tools\Verify.ps1 -Mode Setup`, then `-Mode PlayMode`; failures must identify missing package/transport behavior before implementing the adapter.
- [ ] Implement the adapter and discovery, bounded parsing (maximum discovery datagram 1024 bytes), cancellation, and cleanup. Bind gameplay for LAN use while leaving validation strictly on loopback. Do not add Relay, Unity Services authentication, or cloud lobby dependencies.
- [ ] Inspect the exported Android target SDK and merged manifest. Implement applicable INTERNET/local-network runtime permission handling for the actual OS/target combination; permission denial and blocked broadcast must leave manual join accessible when unicast itself is allowed. Record Windows private-network firewall setup without disabling the firewall.
- [ ] Build Windows and Android with the existing verification commands. Record same-machine Windows transport exchange and, when devices are available, tablet-to-tablet and mixed-platform handshakes with internet disconnected. Missing hardware remains an explicit gate.
- [ ] Commit the transport/build slice after its tests pass. Do not upgrade the editor or unrelated packages to address an uninvestigated failure.

## Task 2: Establish lobby policy, ownership, and asynchronous commands

**Files:** Create `Core/Networking/SessionDescriptor.cs`, `NetworkSessionState.cs`, `LanSessionService.cs`, `NetworkProtocol.cs`, `NetworkSettings.cs`, `Core/Foundation/FoundationService.Network.cs`, `Tests/EditMode/LanSessionTests.cs`, `Tests/EditMode/NetworkCommandTests.cs`; modify `FoundationService.cs`, `FoundationState.cs`, `Core/Automation/CommandResult.cs`, `Runtime/FoundationApp.cs`, `Runtime/Automation/ValidationBridge.cs`, `StateStream.cs`, and `Tools/CoreTests/FamilyRacing.CoreTests.csproj`.

**Interfaces:** `LanSessionService.Execute(GameCommand command) -> CommandResult`; `AdvanceWallTime(double monotonicSeconds)`; `Snapshot() -> NetworkSessionState`; `DrainOutcomes() -> IReadOnlyList<CommandResult>`. Add `CommandResult.Accepted = "accepted"`; `FoundationService.CompleteNetworkCommand(CommandResult outcome)` updates the existing identity cache to a terminal outcome exactly once.

- [ ] Write tests: `DuplicatePendingJoinStartsOneConnection`, `IdentityConflictDoesNotMutateLobby`, `FifthParticipantRejected`, `IncompatibleContentRejectedBeforeSpawn`, `NonHostCannotStartOrConfigure`, `ClassChangeClearsReady`, and `JoinTimesOutOnce`. Assert pending lookup returns accepted, successful completion returns applied, failure returns failed/rejected, and duplicate terminal delivery returns the same result.
- [ ] Run `Tools\Verify.ps1 -Mode Core`; expected RED includes the missing APIs. Include `Core/Networking/*.cs` in the Core test project so tests actually compile/discover.
- [ ] Implement strict transitions `idle → discovering/connecting → lobby → countdown → racing → results`, plus paused/reconnecting/recovery states. Bind the local command target to the local participant; remote players are controlled through their own connection. Enforce exact compatibility before allocation, four slots, same-class selection, and all connected humans ready before start. Start freezes the roster; reject new participants mid-race while permitting authenticated rejoin.
- [ ] Add the command surface below and publish availability reasons. Duplicate pending commands must not resubmit transport work. A network request becomes applied only after the relevant host/transport result, not merely after queuing an RPC. Bound pending operations at 128 and refuse excess without mutation.
- [ ] Emit command outcomes and schema-3 state for each async transition while paused as well as running. Keep protocol-1 command envelopes; update clients to understand accepted separately from terminal outcomes. Run Core/EditMode/PlayMode regressions, then commit.

| Command | Parameters / authority |
|---|---|
| `lan.host` | `port`; selected writable local profile, idle |
| `lan.discover` / `lan.discovery.stop` | No parameters; start/stop ongoing bounded discovery |
| `lan.join` | `address`, `port`; manual or discovered endpoint uses the same action |
| `lan.configure` | `eventId`, `classId`; host in lobby |
| `lan.vehicle` | `vehicleId`; local participant, valid for selected class |
| `lan.ready` | `ready`; local participant in lobby |
| `lan.start` | No parameters; host, compatible ready humans |
| `lan.pause.request` | No parameters; any connected participant |
| `lan.pause.set` | `paused`; host only |
| `lan.reconnect` | No parameters; uses privately stored session credential |
| `lan.results.request` | `raceId`; own result from current host session |
| `lan.results.retrySave` | No parameters; retries the exact pending local result |
| `lan.rematch` | No parameters; host results → lobby, clears readiness |
| `lan.leave` | No parameters; client leaves; host closes session |

Existing `input.set`, `input.clear`, `camera.toggle`, and physical controls route through the active local racer. `pause.toggle` becomes a request on clients and a shared toggle on the host. Solo-only restart/start/tutorial commands reject while in a LAN session.

## Task 3: Run four racers under one authoritative clock

**Files:** Create `Core/Racing/NetworkRace.cs`, `NetworkRaceState.cs`, `RaceResult.cs`, `Tests/EditMode/NetworkRaceTests.cs`; modify `DrivingSession.cs`, `DrivingSessionState.cs`, `DrivingEvent.cs`, `CourseDefinition.cs`, `EventDefinition.cs`, `Runtime/Content/TrackDefinitionAsset.cs`, `EventDefinitionAsset.cs`, and `Editor/NetworkPrototypeSetup.cs`.

**Interfaces:** `NetworkRace.Step(IReadOnlyDictionary<string, DriverInput> inputs, IMotorWorld world) -> NetworkRaceState`; `Snapshot() -> NetworkRaceState`; `SetPaused(bool paused)`. Extend `DrivingSession.Step(DriverInput input, IMotorWorld world, IRacerContactSource racers = null)` and support an authored starting pose without changing existing solo callers.

- [ ] Write `FourDistinctSpawnsShareCountdown`, `WrongClassRejectedAtomically`, `NormalizationIgnoresProfileUpgrades`, `PauseFreezesAllRaceTimers`, `SkippedCheckpointCannotFinish`, `FirstFinishDoesNotEndOtherRacers`, and `FinishGraceProducesStableDnfOrder`. Assert 30 steps equal one running second, all four share the tick, and pause changes none of their motors/progress/finish deadlines.
- [ ] Run Core tests and observe RED, then implement one coordinator around four racer sessions with one race ID and lifecycle. Author four non-overlapping spawns and `lan-car-01` / `lan-motorcycle-01` events on the existing graybox course. Keep the solo content IDs and behavior intact.
- [ ] Use identical baseline tuning within a selected class and preserve cosmetic/model identity. Rank finished racers by finish tick, then stable slot; rank running racers by accepted lap/checkpoint and clamped progress on the current legal segment. A recovery must not advance progress or create a false finish.
- [ ] Implement the finish policy from the shared settings. A global finalized result is created once; per-racer finish events are not global result completion. Abort produces no reward-bearing result.
- [ ] Run Core plus course/progress PlayMode tests; commit with the new content assets and metadata.

## Task 4: Fill empty slots with honest AI and bounded racer contact

**Files:** Create `Core/Racing/AiDriver.cs`, `RacerContacts.cs`, `Tests/EditMode/NetworkAiTests.cs`, `Tests/PlayMode/NetworkRaceWorldPlayTests.cs`; modify course/track definitions and `Editor/NetworkPrototypeSetup.cs`, `NetworkRace.cs`, `Runtime/Driving/DrivingWorld.cs`.

**Interfaces:** `AiDriver.Sample(DrivingSessionState racer, CourseDefinition course) -> DriverInput`; `RacerContacts.ForParticipant(string participantId) -> IRacerContactSource`. All contact queries use the same pre-step racer snapshot; iteration order must not change outcomes.

- [ ] Add tests `AiUsesOrderedCourseRoute`, `AiRecoveryCannotSkipGate`, `ContactExcludesSelf`, `ContactMagnitudeIsBounded`, `RacerIterationOrderDoesNotChangeStep`, and `AiEventsCarryNoHumanSkillCredit`. Test both classes and the ramp path; an AI must complete the real course under normal rules.
- [ ] Observe RED; author a route compatible with the existing gates, including ramp approach and safe recovery. Implement a waypoint follower that emits normal steering/drift/boost/recover inputs. Use normalized tuning and no hidden catch-up multiplier or teleports.
- [ ] Pass the existing contact interface through each session and validate bounded nudges, including overlapping spawn/recovery poses and airborne racers. Mark event control source at the tick of the action, not from the driver's later reconnect state.
- [ ] Extend `DrivingWorld` to a participant-keyed set of up to four vehicle views with deterministic cleanup. Keep solo presentation tests passing. Run Core and PlayMode tests, then commit.

## Task 5: Replicate host state and validate all incoming control

**Files:** Create `Core/Networking/NetworkInputFrame.cs`, `NetworkSnapshot.cs`, `HostInputBuffer.cs`, `Runtime/Networking/NetworkRaceBridge.cs`, `NetworkWireCodec.cs`, `Tests/EditMode/HostInputBufferTests.cs`, `Tests/PlayMode/NetworkReplicationPlayTests.cs`; modify `LanTransport.cs`, `LanSessionService.cs`, `FoundationApp.cs`, and the network state DTOs.

**Interfaces:** `HostInputBuffer.TryEnqueue(NetworkInputFrame frame, string connectionParticipantId, out string error) -> bool`; `Consume(string participantId, long hostTick) -> DriverInput`; `NetworkRaceBridge.Pump()` and `Publish(NetworkSnapshot snapshot)` execute at defined Unity boundaries.

- [ ] Write tests for foreign ownership, wrong race/epoch, non-finite values, oversized packets, old/future sequences, duplicate one-shot IDs, stale input neutralization, and bounded queues. `ClientCannotSubmitFinishOrCoins` asserts no client-supplied progress/results mutate authority.
- [ ] Observe RED, then implement host simulation at 30 Hz with full four-racer snapshots every second tick (15 Hz). Send inputs/snapshots over a sequenced unreliable channel; use reliable ordered messages for lobby, lifecycle, ownership, and results. Define strict typed payload limits; begin with a 16 KiB snapshot ceiling and fail explicitly on overflow rather than truncating racer state.
- [ ] Keep the host's local racer on the same validated input path. Acknowledgments identify the last processed input and corresponding authoritative state; do not acknowledge samples merely received. Queue capacity is 120 samples per racer, and bursts cannot grant extra simulation steps. Deliver host transient events in reliable ordered batches with event identities so events between snapshots remain visible and audio/effects consume each event once; prediction/replay does not create a second authoritative event.
- [ ] Deduplicate one-shot action IDs independently of held inputs, retain them through replay/reconnect boundaries, and acknowledge/retry until applied or cancelled. Reject stale race/epoch traffic before it can affect a newly spawned session.
- [ ] Validate real NGO traffic between players, including reordered/lost snapshots and same-tick events. Authority fields agree after delivery; each device's validation stream still emits every local logical boundary, regardless of the lower gameplay snapshot rate. Run Core/PlayMode, then commit.

## Task 6: Predict the local motor and interpolate remote racers

**Files:** Create `Core/Networking/ClientPrediction.cs`, `RemoteInterpolation.cs`, `Tests/EditMode/ClientPredictionTests.cs`, `Tests/PlayMode/NetworkPredictionPlayTests.cs`; modify `NetworkRaceBridge.cs`, `DrivingWorld.cs`, `Runtime/Presentation/DrivingCamera.cs`, and network observation DTOs.

**Interfaces:** `ClientPrediction.Predict(NetworkInputFrame frame, VehicleTuning tuning, IMotorWorld world) -> MotorState`; `Reconcile(NetworkSnapshot snapshot, VehicleTuning tuning, IMotorWorld world) -> MotorState`; `Reset()`; `RemoteInterpolation.Push(NetworkSnapshot snapshot)` and `Sample(string participantId, double renderHostTime) -> MotorState`.

- [ ] Add `LocalInputChangesPoseBeforeRoundTrip`, `CorrectionReplaysOnlyUnacknowledgedInputs`, `StaleSnapshotIgnored`, `ReplayDoesNotEmitAuthoritativeEvents`, `ReplayOverflowRequestsFullState`, and `RecoveryOrEpochChangeClearsHistory`. Assert buffer count never exceeds 120 and canceled/replayed recover actions do not fire twice.
- [ ] Observe RED, then implement restore-and-replay using `ArcadeMotor.Step` and the same normalized tuning/static course queries. Host authority remains decisive for dynamic racer contacts, recovery, checkpoints, and landing facts. Reconciliation is not assumed bit-identical across Android/Windows physics.
- [ ] Separate simulation correction from visual smoothing. Retain predicted motor state, last authoritative motor, acknowledged input, replay count, error distance, and interpolation source tick in the stream. Replay must not re-emit authoritative audiovisual events or trigger persistence.
- [ ] Exercise 100 ms RTT, deterministic jitter of up to ±30 ms, and 2% loss through the real transport test path. Record immediate local feedback, correction magnitudes/frequency, stale remote poses, and buffer occupancy. The owner judges recurring visible jumps; do not call average RTT proof of acceptable feel.
- [ ] Run Core/PlayMode and a real two-player drive over turns, ramps, wall contact, pause/resume, and recovery in both cameras. Commit after evidence is recorded.

## Task 7: Shared pause, reserved slots, rejoin, and host loss

**Files:** Create `Core/Networking/ReconnectRegistry.cs`, `Runtime/Networking/ReconnectCredentialStore.cs`, `Tests/EditMode/NetworkLifecycleTests.cs`, `Tests/PlayMode/NetworkLifecyclePlayTests.cs`; modify `LanSessionService.cs`, `NetworkRaceBridge.cs`, `FoundationApp.cs`, and prediction/input lifecycle methods.

**Interfaces:** `ReconnectRegistry.TryReclaim(string credential, string sessionId, out string participantId, out long ownershipEpoch) -> bool`; `LanSessionService.OnDisconnected(string participantId, string reason)`; credential creation/rotation lives behind the transport adapter and never enters observation DTOs.

- [ ] Test disconnect in countdown/racing/pause/finish, `ReconnectKeepsSameRacerAndProgress`, `OldEpochCannotControlReclaimedSlot`, `RejoinBeforeTimeoutSupersedesOldConnection`, `ClientCannotResumeHostPause`, and `HostLossNeverProducesCompletionReward`.
- [ ] Observe RED, then implement host-approved shared pause with requests visible to the host. Race/countdown/AI/grace clocks freeze; wall-clock peer timeouts, result retries, stream sequences, and commands continue. Clear held input and queued one-shots on pause/control loss; resume requires fresh input.
- [ ] On confirmed disconnect, reserve the slot for that race and switch to the Task-4 AI. Rejoin requires the session credential, increments ownership epoch, fences out the old connection, sends full state, clears predictions, and transfers control at a host tick boundary. A full lobby is not a reason to reject its reserved participant.
- [ ] Store reconnect credentials privately for client-process restart, scoped to session and local profile; never log or stream them. Explicit leave removes the local credential. A new participant cannot steal an in-race reservation by presenting a display name/profile ID. Retain result retrieval rights for old participants across rematches; release race-slot reservations when that race is over.
- [ ] Handle focus/background transitions explicitly: neutralize local controls; on client suspension disconnect/rejoin safely; on host loss or suspended-host timeout return clients to a localized recovery screen. No migration and no inferred final result. Already persisted results remain intact.
- [ ] Run Core/PlayMode and real-process disconnect/rejoin/host-kill checks, including a paused host and repeated reconnects. Commit.

## Task 8: Deliver immutable results and apply completion coins once

**Files:** Create `Core/Profiles/RaceRewardService.cs`, `AppliedResultReceipt.cs`, `ProfileMigration.cs`, `Tests/EditMode/NetworkRewardTests.cs`, `Tests/PlayMode/NetworkResultsPlayTests.cs`; modify `ProfileSave.cs`, `ProfileFactory.cs`, `ProfileValidation.cs`, `ProfileStore.cs`, `FoundationService.cs` (profile copying), `RaceResult.cs`, event content, `LanSessionService.cs`, and `NetworkRaceBridge.cs`.

**Interfaces:** `RaceRewardService.Apply(RaceResult result, string participantId, ProfileSave savedProfile, ProfileStore store) -> RewardApplyResult`; result statuses `applied|alreadyApplied|failed|ineligible`. Schema 2 adds `List<AppliedResultReceipt> appliedResults`; each receipt contains resultId, participantId, awardedCoins, and a SHA-256 fingerprint of a versioned canonical encoding of immutable result fields (excluding delivery metadata). Identity is bound to the intended local profile; the stored fingerprint detects conflicting replay even after restart.

- [ ] Write `DuplicateResultAfterRestartAwardsOnce`, `ConflictingReplayAfterRestartIsRejected`, `SaveFailureLeavesCoinsAndReceiptUnchanged`, `LostAckResendsSameResult`, `DnfAndAbortAwardZero`, `ResultForOtherProfileRejected`, `SchemaOneMigratesWithoutLosingSettings`, and `NewerOrUnreadableSaveIsPreserved`. With an initial balance of 7 and a finalized completion, assert balance 27 and exactly one receipt after repeated delivery/reload; integer overflow fails with no write. Fingerprinting must give identical results for the same facts on Android and Windows regardless of locale or JSON property order.
- [ ] Observe RED. Add schema migration on read without automatically rewriting a file just because it was loaded. Explicit successful save writes schema 2. Update every profile deep-copy/serializer/validation path; receipts are copied, validated, and never silently evicted.
- [ ] Calculate the authored completion reward solely from finalized host facts. AI takeover may complete the race for completion credit; its action intervals remain ineligible for future human skill bounties. No M3 bounties/stars are granted or claimed supported.
- [ ] Save coins and the applied-result ID together through one existing atomic profile write. Publish pending/failed/persisted state accurately; acknowledge persistence only after success. Retry the immutable same result after save failure. A duplicate with conflicting payload fails; receipt replay never changes coins.
- [ ] Retain results and delivery/ack status while the host session lives, across rematches. At the 128-race retention cap refuse another start and offer closing/rehosting, without losing result retrieval. Host departure before a result has ever reached a client cannot guarantee new delivery; report that limit explicitly, and preserve all previously saved progress.
- [ ] Run Core/EditMode/PlayMode and real client-process restart/redelivery checks using isolated profiles; commit.

## Task 9: Complete bilingual UI, observable state, and agent scenarios

**Files:** Create `Runtime/FoundationShell.Network.cs`, `Tests/PlayMode/NetworkScreensPlayTests.cs`, `NetworkStreamPlayTests.cs`, `Tools/NetworkScenario.py`, `Tools/TestNetworkScenario.py`; modify `FoundationShell.cs`, `FoundationShell.Driving.cs`, `FoundationShell.Screens.cs`, `FoundationInput.cs`, `Core/Localization/ShellText.cs`, `FoundationState.cs`, `ValidationBridge.cs`, `StateStream.cs`, `Tools/AgentScenario.py`, `Tools/DrivingScenario.py`, `docs/COMMANDS.md`, and `docs/FEATURE_COVERAGE.md`.

**Interfaces:** `NetworkScenario.py --connections <host-connection.json> <client-connection.json> ... --evidence <new-directory>` attaches to 2–4 independently launched players. Android connection descriptors use per-device ADB-forwarded loopback endpoints. Existing scenario connection-file CLIs remain supported.

- [ ] Test every actual widget callback against its command: host/discover/manual join/cancel, class/course/vehicle, ready/start, pause request/host resume, reconnect/leave, result-save retry, and rematch. Assert stable semantic IDs, disabled reasons, RTL navigation, and LTR IP/timer fields; steering arrows keep physical meaning.
- [ ] Implement screens `lan-browser`, `lan-lobby`, `lan-race`, `lan-results`, and `lan-recovery`, with a local camera target and readable ownership/disconnect states. Save no hidden settings as a side effect of reconnect or profile switching. Allow profile change only after leaving the active session.
- [ ] Complete schema 3 across discovery, snapshot, deltas, explicit resync, and Python validators. Preserve schema-2 historical fixture support where used; fail clearly on unknown versions. Add immutable-copy tests for nested racer/result/prediction state.
- [ ] Add reconstruction tests at every tick, async command completion, paused transition, reconnect, and result save. Each stream includes device/run/session/participant/build/content identities, host versus predicted state, AI control source, readiness, pause, input acknowledgment, corrections, result delivery, persistence, and safe errors. Never include reconnect or validation secrets. Trace overflow/sink failure remains a failed capture, not sampled success.
- [ ] Implement real-player scenarios that drive ordinary commands through race completion for both classes, normalization, duplicate commands/results, both cameras, pause, AI takeover, rejoin, rematch, and host loss. Use state predicates plus bounded timeouts. Teleporting/forced finishing is not feature evidence; fixtures/fault injection are clearly labeled separate modes.
- [ ] Add validator mutation tests: remove/reorder a delta, mislabel predicted state as authoritative, duplicate a reward, or drop a participant; each altered evidence set must fail. Run Python unit tests and Core/EditMode/PlayMode; commit commands, coverage, and scenarios together.

## Task 10: Qualify real LAN builds and document remaining gates

**Files:** Create `Tools/NetworkSoak.py`, `docs/tasks/network-prototype-report.md`; finalize `docs/NETWORK_TEST_RUNBOOK.md`; update `README.md`, `IMPLEMENTATION_STATUS.md`, `docs/COMMANDS.md`, and `docs/FEATURE_COVERAGE.md`.

**Interfaces:** `NetworkSoak.py --connections <host.json> <client.json> ... --minutes 30 --evidence <new-directory>` drives repeated real races/rematches and records per-device evidence/health. It does not control live LAN by deterministic clock stepping.

- [ ] Run one full gate after integration: `Tools\Verify.ps1 -Mode Core`, `-Mode EditMode`, `-Mode PlayMode`, `python -B -m unittest discover -s Tools -p 'Test*.py'`, and `Tools\Test-VerifyTooling.ps1`. Record actual pass/fail/Ignore counts, including the unchanged voice gate if still open. Targeted tests run during development; repeat the broad gate only for changes/failures that justify it.
- [ ] Build the same revision using `-Mode Windows`, `-Mode WindowsOrdinary`, and `-Mode Android`. Record executable/APK hashes, package locks, exported Android target SDK, and build settings. Demonstrate ordinary Windows LAN play and that ordinary-build validation access is absent. Document an Android ordinary-build path and compare it with instrumented performance; do not infer ordinary performance from validation builds.
- [ ] Re-run existing solo driving/agent/restart/stream-fault/finish-delivery/normal-profile scenarios where the changed shared paths affect them. `DrivingScenario.py` and `AgentScenario.py` take a launched player's **connection.json**, not an executable path. Start each player with its own validation/evidence directory and synthetic profile store; keep helper windows hidden.
- [ ] Run the following matrix with internet disconnected. A missing device or owner review is **not verified**, never passed by a desktop substitute.

| Gate | Required evidence |
|---|---|
| Two tablets, either hosting | Discovery/manual join, ready/countdown, race/results/rematch, touch, no PC required for gameplay |
| Tablet + PC, either hosting | Same flow, compatibility rejection, Windows firewall and multi-adapter diagnostics |
| Four physical devices, A7 Lite host | Four humans, repeated races; AI takeover/rejoin; 30-minute session |
| Discovery failures | Block broadcast, use manual IP; deny applicable permission; unreachable/full/incompatible lobby has actionable localized response |
| Lifecycle | Disconnect in countdown, running, shared pause, finishing and result delivery; client restart; host close; no duplicate reward |
| Impaired network | Measured/injected 100 ms RTT, ±30 ms jitter, 2% loss; record transport settings and corrections, plus normal-Wi-Fi baseline |
| Performance | Both cameras; tablet target 30 fps, p95 steady frame ≤40 ms, no recurring >100 ms stalls, starting memory budget 800 MB; PC 60 fps at 1080p |
| Instrumentation | Full capture valid on every device; memory/trace/backlog/cadence measured separately from ordinary-build frame performance |
| Human acceptance | Responsive steering, understandable join/pause/recovery/results flow, Hebrew reading and touch usability, prior M2 child/feel/audio gates |

- [ ] Report every result with revision, platform/OS/device, host role, human/AI count, class/camera, seed/fault settings, commands, complete state traces, assertions/timeouts, logs, screenshots, and performance method. A faulty sink invalidates the affected capture even when gameplay succeeds.
- [ ] Fix failures within scope, re-run affected checks, and produce the final milestone report. Mark M3 accepted only when the physical, performance, command/state, and human gates pass. If code is complete first, use **development complete; acceptance open** with named missing evidence. Keep M1/M2 status independent.
- [ ] Commit the report and documentation. Stop before expanding into M4 visual content.

## Risks and explicit boundaries

- **Prediction is the largest technical risk.** NGO supplies networking/anticipation hooks, but the input history, acknowledgment, replay, and correction policy are this project's responsibility. Prove the two-player ramp/contact loop before adding visual complexity. [Unity NGO 2.7 anticipation documentation](https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.7/manual/advanced-topics/client-anticipation.html)
- **Package compatibility must be exercised.** NGO 2.7.0 declares Unity 6000.0 minimum and UTP 2.6.0; that is a dependency fact, not proof this project's Android build works. [Pinned package manifest](https://raw.githubusercontent.com/Unity-Technologies/com.unity.netcode.gameobjects/ngo/2.7.0/com.unity.netcode.gameobjects/package.json)
- **Android LAN access depends on OS and target.** The current official guidance distinguishes Android 16 opt-in restrictions from Android 17/target-37 enforcement. Task 1 records the actual target/OS and implements the applicable path; manual IP does not bypass denied LAN permission. [Android local-network permission](https://developer.android.com/privacy-and-security/local-network-permission)
- **Instrumentation overhead grows with four racers.** Gameplay snapshots at 15 Hz do not relax validation's every-boundary contract. Measure and optimize allocation/serialization; do not hide overload by dropping required records.
- **Result durability has a session limit.** No host migration or cloud recovery is introduced. A finalized result already saved survives host loss; an undelivered result cannot be recovered after its host session is gone. The UI and report must distinguish those cases.
- **Device access determines acceptance.** Starting M3 engineering does not close M2, and Windows multi-process tests do not certify tablet hosting or family usability.

## Self-review and handoff

- Spec coverage: host/discovery/manual join/version checks → 1–2; four racers/classes/normalization/authority → 3–5; prediction → 6; pause/rejoin/host loss/AI attribution → 4 and 7; once-only results/persistence → 8; bilingual commands/full state → every task and 9; physical/performance acceptance → 10.
- Cross-task checks: one set of session/race/participant/epoch identities; 30 Hz authority versus 15 Hz replication; wall time for connectivity versus frozen race time; async accepted versus terminal outcomes; prediction never becomes reward authority.
- Intentionally small scope: AI exists to support network races, and completion coins exist to validate result persistence. No campaign, upgrade, gadget, or art expansion is bundled into M3.
- Recommended execution: **native, sequential implementation** with targeted checks per task and a whole-change review before qualification, because the identity/authority/stream interfaces are tightly coupled. Subagent-driven execution is an alternative if explicitly selected later.
- Review this plan's proposed tuning defaults before execution. This planning pass does not modify product code, resolve packages, run devices, or mark any milestone complete.
