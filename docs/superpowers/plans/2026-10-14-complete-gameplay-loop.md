# Milestone 5: Complete Gameplay Loop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Execution method has not been selected; this request authorizes planning only.

**Goal:** Deliver a complete Sunny Island adventure: 24 regular events, two rival finales, persistent rewards, three active challenge bounties, upgrades and cosmetics, Quick Race, Time Trial, and compatible LAN play.

**Architecture:** Extend the existing Unity-free Core rules and atomic profile store. Reuse the four-racer `NetworkRace` simulation for Campaign/Quick Race through a local adapter, and `DrivingSession` for Time Trial; share authoritative metrics, result evaluation, and progression transactions. Unity content assets, presentation, UI, and agent commands consume these rules through the existing application service.

**Tech Stack:** Pinned Unity 6000.3.26f1, C# 9 / .NET Standard 2.1-compatible Core, URP, Input System, UI Toolkit, NGO 2.7.0 / UTP 2.6.0, Unity Test Framework, existing PowerShell/Python validation tools. No new runtime package is planned.

**Spec:** [GAME_PLAN.md](../../../GAME_PLAN.md), sections 1–5. [IMPLEMENTATION_STATUS.md](../../../IMPLEMENTATION_STATUS.md) explicitly identifies that approved plan as the design specification. The owner selected “M5 gameplay loop, with M4 dependencies” on 2026-10-14. New tuning and implementation choices below are proposals for review, not additional previously approved requirements.

## Global constraints

- “Android tablets and Windows PC”; required tablet: “Samsung Galaxy Tab A7 Lite SM-T220; assume the weaker 3 GB configuration”.
- “All gameplay works offline after installation”.
- “Two to four people over home Wi-Fi; four racers total”. Cars and motorcycles race in separate classes.
- “Normalize vehicle performance within the selected class, including upgrades. Keep cosmetics.”
- “Local races award coins and eligible challenge bounties. Campaign stars and rival unlocks remain solo. Time Trial uses separate normalized records.”
- “AI actions during absence do not earn human skill-bounty credit.”
- “Aborted, unfinished races do not award completion rewards.”
- “Each course/class pairing has six events: circuit race, boost challenge, stunt challenge, time challenge, gadget race, and clean-driving challenge.”
- “Complete the world’s 24 regular events and earn 30 regional stars to unlock its two finales.” Completing both finales advances the adventure; victory is optional for advancement.
- “Initial tuning: each level adds 5% to its category; costs are 60, 120, and 180 coins.” Three categories, three levels each; values remain authorable data.
- “Time Trial uses normalized vehicle performance and stores personal bests by course and class. It awards records rather than campaign progress.”
- “Every game control and feature is command-accessible; complete logical game state is streamed to a structured sink for end-to-end agent testing”. Preserve shared validation, command deduplication, bounded input, full reconstruction, and explicit capture failure.
- “The budget is a planning cap, not authorization to purchase.” Recorded spending is US$0 / US$100. Use existing/original assets; record licenses and costs.
- Arcade remains the family-build backend. PhysicalAssisted remains opt-in under its separate qualification gates.

## Review focus

1. A save failure, restart, duplicate result, or old profile clone loses progress or grants it twice — Tasks 3, 7, 11.
2. An upgraded/locked vehicle leaks into LAN or Time Trial, or a raw command bypasses the solo unlock check — Tasks 4, 8, 10.
3. Recovery, retransmission, or AI takeover invents pickups, objective progress, or human bounty credit — Tasks 5–7, 11.
4. Completion is confused with victory, replay stars accumulate, or a three-slot bounty rotation applies one result repeatedly — Tasks 2, 6–7, 11.
5. Course changes, a long frame, pause/rejoin, or missing memory samples produce misleading acceptance evidence — Tasks 1, 4–5, 9, 11–12.

## Starting point and scope

Inspected on 2026-10-14: `main` at `9c5281b`, clean working tree before this planning change. This pass inspected sources and evidence documents; it did not run tests, Unity, builds, or device qualification.

Existing reusable pieces:

- `DrivingContentCatalog` lives in `Core/Racing/DrivingSession.cs`; it validates vehicle/course/event references and calculates compatibility fingerprints.
- `NetworkRace` is already a transport-free four-racer coordinator over per-racer `DrivingSession` objects. It supplies AI, contacts, ordered checkpoints, finish/DNF policy, and finalized results.
- `RaceRewardService.Apply` already saves coins and result receipts together, refuses conflicting result identities, and leaves memory unchanged on write failure. Today it only applies LAN completion coins.
- Profile schema is **2**, observation schema **5**, LAN protocol **2**, gameplay identity **gameplay-2**. The validation-command protocol is a separate version; do not confuse it with the LAN protocol.
- `FoundationService` and `RaceRewardService` each contain manual profile-copy code. Both must preserve every new collection.
- `input.set` already carries `gadget`, but gameplay rejects it and the race HUD disables its button. No campaign, upgrade, bounty, or Time Trial implementation exists.
- M4 dresses `graybox-01` as a Sunny Island benchmark. It does **not** supply two finished courses or 26 campaign events.

Included: Harbor Hop and Lighthouse Loop; the six event types for both classes; one rival finale per class; starter `car-01` / `motorcycle-01` plus earned `car-02` / `motorcycle-02`; three gadgets; three upgrade categories; earned cosmetics; visible AI difficulty; complete bilingual menu/result/replay paths; persistence and agent/device evidence.

Deferred to M6/M7: Redrock Canyon, Snowy Peaks, the remaining four courses/four vehicles/52 events, full-campaign duration tuning, final release signing/distribution. No internet services, daily timers, compulsory currency grind, ghost replay system, or physics-default switch.

### Approach decision

| Approach | Assessment |
|---|---|
| Build the complete first-world loop with shared rules and an M4 content gate | **Recommended and selected scope.** Gives one coherent family-play milestone and exercises economy, modes, networking, and persistence together. |
| Deliver only a campaign menu plus a few races | Useful intermediate checkpoint, but does not satisfy M5 bounties/upgrades/modes or the approved first-world count. |
| Close all historical acceptance work before any M5 systems work | Reduces concurrent work, but hardware access would unnecessarily stop pure Core development. Keep the actual expansion and acceptance gates below. |

## Dependencies and stage gates

Execution order: **1 inherited risk work → 2 content contracts → 3 saves → 4 local modes → 5 gadgets → 6 objectives/bounties → 7 transactions → 8 upgrades/selection → 9 authored world → 10 family UI → 11 external scenarios → 12 qualification**.

Task 1's hardware work may remain pending while Tasks 2–8 and UI development use the existing benchmark or clearly labeled test fixtures. **Do not begin Task 9 production course expansion until the M4 A7 Lite visual/performance gate is evidenced.** M5 cannot be accepted while required inherited device/human gates remain open. Missing hardware is `notVerified`, never a desktop pass.

| Dependency | Required disposition |
|---|---|
| M4 A7 Lite visual acceptance and sustained performance | Before Task 9: real lighting bake, owner approval on SM-T220, both cameras/classes, four racers with tablet hosting, 30-minute rendered measurement, memory and stall review. Retain the full M4 physical-device matrix. |
| Credentialed-rejoin input stall | Investigate the reported ~110–130 s `input.set` stall; regression and live repro must close it before M5 LAN qualification. Do not guess a fix from the report. |
| Permanently departed result recipient | Proposed policy: explicit `lan.leave` makes outstanding delivery rows terminal `failed/undeliverable`; temporary disconnect keeps them eligible for rejoin. Retain immutable result facts; delivery metadata must not change reward fingerprints. |
| Memory sampler after player relaunch | `9c5281b` already fixes dead-PID honesty and zero-valid-sample verdicts. Remaining work is sampling the new process generation; do not redo or claim the missing gen-2 evidence already exists. |
| Known baseline failures | Reproduce and classify the carried font-fixture failure, missing voice-clip Ignore, and two impaired-link PlayMode failures. Fix code/tooling causes or supply the required environment; no blanket ignore and no “all green” claim while failures remain. |
| Ordinary-build overhead/device evidence | Matched ordinary/development workload, same source/content/device; instrumented PC qualification alone does not prove ordinary or tablet performance. |
| Voices, Hebrew, child/device review | Complete packaged cues, Hebrew text/pronunciation review, physical touch/keyboard checks, onboarding and first-world family playtests. No credentials in code/builds. |
| Four-human physical LAN | Owner currently has two tablets and a PC; a fourth supported device is still needed for this acceptance row. AI fill and local processes remain explicitly labeled substitutes. |

## Proposed behavior decisions

These defaults make the implementation reviewable. Content tuning remains editable and must be checked through play; changing a gate from the approved spec requires an explicit scope change.

### Modes and progression

| Mode | Selection and racers | Persistent outcome |
|---|---|---|
| Campaign | First world's two courses and 24 regular events available from the start; profile-owned vehicles; one human plus three visible AI, same class | Per-event completion, best three-bit objective mask, coins, eligible challenge bounties; solo finale completion/victory rewards |
| Quick Race | Profile-accessible courses and owned vehicles; selected class and Easy/Medium/Hard; one human plus three AI | Coins and eligible challenge bounties; no campaign stars or rival unlocks |
| LAN | Every **implemented** course/model regardless of local unlocks; four racers including AI fill; normalized class tuning | Coins and eligible human-attributed challenge bounties; no campaign stars/rival unlocks |
| Time Trial | Both implemented courses; class selection with its normalized starter vehicle; one human, no AI/gadgets | Best elapsed ticks by course/class only; no coins, stars, bounties, or unlocks |
| Practice/tutorial | Existing paths preserved; gadget lesson added | Tutorial state only; no economy rewards |

- Normalized modes ignore model-specific stats and purchased upgrades, retaining visual identity where selectable. Solo upgrades never mutate the shared catalog or the LAN compatibility fingerprint.
- Every regular event has completion, introductory, and mastery objective bits. Persist their union across replays; regional stars are the sum of unique bits. DNF/abort grants none. A missed target keeps the completion star.
- All 24 completions **and** at least 30 regional stars are necessary for finales. Author a non-victory introductory objective in every regular event, so six extra stars are obtainable without winning.
- Completing both finales unlocks `car-02` and `motorcycle-02` exactly once and marks Sunny Island complete. Show “more worlds coming” until M6 content exists. Beating each named rival separately awards a one-time cosmetic and named-rival bounty; losing does not stop advancement.
- Three repeatable challenge slots rotate immediately from a fixed offline catalog. Named-rival victory bounties appear on finale cards as two separate one-time story rewards; they do not evict a partially completed challenge slot. This is a proposed presentation interpretation of the approved bounty scope.
- Default completion reward: **20 coins** for Campaign/Quick/LAN; placement bonuses **5/3/1/0** for places 1–4. Challenge rewards: **10** for introductory thresholds, **20** for mastery thresholds; one-time rival victory bounty: **30**. Time Trial/practice/tutorial pay **0**. No upgrade is required to finish or advance.
- Upgrade multiplier is additive by level: `1 + 0.05 * level`, maximum **1.15**, per vehicle/category. Acceleration changes `acceleration`; handling changes `steeringRate` and `grip`; boost changes `boostAcceleration` and `boostMaxSpeed`. Base top speed, boost charge/duration, checkpoint rules and AI catch-up behavior do not change.
- Use visible difficulty presets affecting AI decisions (braking safety margin, boost/gadget timing and shortcut choice), not secret speed multipliers. Easy is the default. Changing difficulty never removes earned progress.

### Event objectives and bounty facts

Use typed, authoritative per-participant metrics: elapsed/finish ticks, placement, human drift ticks, human boost activations, validated clean landings, accepted shortcut traversals, gadget uses, longest clean-driving streak, recovery/impact counts, human finish and rival-defeat facts. Count at simulation boundaries before bounded presentation-event eviction. Store attribution at the time of each action; an AI takeover cannot retroactively become human credit.

| Event type | Introductory objective | Mastery objective |
|---|---|---|
| Circuit | Finish with at least one clean landing | Finish first **or** validate both distinct shortcuts |
| Boost | Three human boost activations | Eight human boost activations |
| Stunt | Two validated clean landings | Five validated clean landings |
| Time | Finish within `ceil(1.5 * parTicks)` | Finish within `parTicks` |
| Gadget | Collect and use two gadgets | Use all three gadget types during the event |
| Clean driving | A 30 s clean-driving streak | A 60 s clean-driving streak and zero recoveries |

Completion is always the first star. All counters exclude countdown, pause and recovery movement. A clean streak requires forward driving, resets on wall/racer impact or recovery, and excludes AI driving. Each authored course/class event must have enough laps and opportunities to satisfy its two objectives. Task 9 records measured unupgraded, human-driven par times and commits positive integer `parTicks`; do not invent measurement evidence or rely on an AI/fixture completion as the par.

The repeatable challenge catalog contains two thresholds for each of six families: drift **10/30 s**, boost **3/8 activations**, clean landings **2/5**, clean streak **30/90 s**, accepted shortcuts **2/5**, human finishes **2/5**. Add finalized human metrics across eligible races except clean streak, which uses the maximum qualifying streak. Persist exactly three distinct active assignments and a deterministic rotation cursor. Replacements start at zero and receive no facts from the result that completed their predecessor. Named-rival bounties are one-time facts outside this repeatable cursor.

### Gadget defaults

One held slot; a full slot leaves a pickup unconsumed. Pickup generation IDs and seeded choices are host-owned and independent of standings. All times below are unpaused 30 Hz ticks and live in content assets.

| Item | Initial behavior |
|---|---|
| Turbo | 60 ticks; acceleration ×1.25 and current speed cap ×1.10; repeat activations refresh duration without multiplying modifiers |
| Bubble shield | One charge, lasts until it blocks a rival foam hit or the race ends; activation consumes the slot |
| Foam trap | Ground patch radius 3 m, lifetime 180 ticks; opponents slow to ×0.70 drive acceleration/speed cap for 45 ticks, followed by 60 ticks immunity; owner unaffected; no stacking or immobilization |
| Pickup | Respawn after 300 ticks; deterministic slot-order tie-break if two eligible racers collect on the same boundary |

Use swept, vertically bounded trigger checks against actual traveled segments; recovery/teleport segments are ineligible. Recovering never resets pickup timers or reissues the same generation. Shield blocks one contact; the blocked patch cannot immediately consume the protection again. Pause freezes pickup/effect clocks. AI obeys the same item, cooldown and effect rules. Predicted clients show requests as pending until confirmed; only the host grants items/effects/rewards.

## File boundaries and contracts

Paths starting with `Core/`, `Runtime/`, `Editor/`, `Tests/`, or `Generated/` are relative to `Assets/FamilyRacing/`. New Unity files require committed `.meta` files. Names below are **proposed APIs**, not claims that they already exist.

| Area | Files and responsibility |
|---|---|
| Gameplay data | New `Core/Progression/GameplayCatalog.cs`, `CampaignDefinition.cs`, `ObjectiveDefinition.cs`, `BountyDefinition.cs`, `EconomyDefinition.cs`; new `Core/Racing/GadgetDefinition.cs`, `AiDifficultyDefinition.cs`; extend `Core/Racing/EventDefinition.cs` and `ContentFingerprint.cs` |
| Assets/authoring | New `Runtime/Content/CampaignDefinitionAsset.cs`, `BountyCatalogAsset.cs`, `EconomyDefinitionAsset.cs`, `GadgetCatalogAsset.cs`, `GameplayCatalogAsset.cs`; extend event/track/vehicle assets; new `Editor/GameplayLoopSetup.cs`, `SunnyIslandCampaignAuthoring.cs` |
| Profile data | Extend `Core/Profiles/ProfileSave.cs`, `ProfileMigration.cs`, `ProfileValidation.cs`, `ProfileFactory.cs`, `AppliedResultReceipt.cs`; new `ProfileProgress.cs`, `ProfileCopy.cs`, `PurchaseReceipt.cs`, `RaceRewardContext.cs` |
| Local modes | New `Core/Racing/SoloRaceSession.cs`, `SoloRaceState.cs`, `RaceModeRules.cs`, `RaceMetrics.cs`, `RaceMetricsAccumulator.cs`, `RaceResultFactory.cs`; extend `NetworkRace.cs`, `DrivingSession.cs`, `RaceResult.cs`, `AiDriver.cs` |
| Progression | New `Core/Progression/CampaignRules.cs`, `ObjectiveEvaluator.cs`, `BountyService.cs`, `UpgradeService.cs`, `VehicleTuningResolver.cs`, `TimeTrialRecordRules.cs`; extend `Core/Profiles/RaceRewardService.cs` |
| Gadgets | New `Core/Racing/GadgetRules.cs`, `GadgetState.cs`; integrate `Core/Driving/ArcadeMotor.cs`, existing simulation seam and prediction path; new `Runtime/Presentation/GadgetPresenter.cs` |
| Application/UI | New partials `Core/Foundation/FoundationService.Gameplay.cs`, `FoundationService.Progression.cs`, `Runtime/FoundationShell.Gameplay.cs`, `FoundationShell.Progression.cs`; extend existing service/state/app/input/serialization and `Core/Localization/ShellText.cs` |
| Course lifetime | New `Runtime/Driving/CourseLoader.cs`; extend `DrivingWorld.cs` and `DrivingCamera.cs` for one loaded authored course and local multi-racer presentation |
| Evidence | New `Tools/GameplayLoopScenario.py`, `TestGameplayLoopScenario.py`, `docs/GAMEPLAY_LOOP_RUNBOOK.md`, `docs/tasks/gameplay-loop-report.md`; extend independent stream validator and existing regression harnesses |

Keep profile schema, observation schema, LAN wire version, gameplay identity, and content identity independent. Proposed M5 values: **profile 3**, **state 6**, **LAN 3**, **gameplay-3**, **gameplay-loop-1** content. Apply each version change when its first changed contract lands, not after incompatible intermediate builds have been produced; Task 10 verifies the integrated negotiation. Command-envelope protocol stays unchanged unless its actual contract changes. Negotiate/reject mismatches before allocating seats. Fingerprint every new gameplay-affecting catalog value, including objectives, difficulty, economy and gadgets; exclude local purchased tuning and delivery metadata.

## Task 1: Resolve inherited blockers and prepare the M4 gate

**Files:** inspect/modify `Core/Foundation/FoundationService.Network.cs`, `Core/Networking/LanSessionService.cs`, `Runtime/Networking/NetworkRaceBridge.cs`, `Tools/VisualBenchmarkScenario.py`, `Tools/PerfSampler.py`; tests in `Tests/EditMode/NetworkLifecycleTests.cs`, `Tests/PlayMode/NetworkLifecyclePlayTests.cs`, `Tools/TestPerfSampler.py`, `Tools/TestVisualBenchmarkScenario.py`; update the existing M4 report/runbook. Investigate before choosing the stall's edit location.

**Interfaces:** keep the current command/result contracts. Proposed sampler orchestration associates each process generation with its own PID, start identity, evidence interval and sampler; a new launch explicitly creates a new sampler.

- [ ] Reproduce rejoin input stall with bounded command latency diagnostics. Add `RejoinedHumanInputAppliesWithinCommandDeadline` with the existing documented command deadline; a stuck input must fail, never disappear behind retries.
- [ ] Add `ExplicitLeaveTerminatesPendingDeliveryButDisconnectDoesNot`: assert `failed/undeliverable` after permanent leave, `pending` after transient disconnect, unchanged result fingerprint, and successful later redelivery after credentialed rejoin.
- [ ] Add Python sampler tests asserting the relaunched PID receives valid records, the dead generation stays `unavailable`, PID reuse is rejected, and zero valid samples cannot pass. Run `python -m unittest discover -s Tools -p 'Test*Sampler.py'` and the network lifecycle suites before/after fixes.
- [ ] Implement only root-cause fixes and the explicit delivery policy. Preserve previously fixed sampler honesty and immutable rewards. Run the relevant Core/EditMode/PlayMode and tooling suites; report inherited failures individually.
- [ ] Bake benchmark lighting and collect the required M4 device/ordinary-build/owner evidence. Record source/build hashes and distinguish historical `vb-qual-5/6` evidence from the final build. Leave missing hardware rows open; Tasks 2–8 may proceed, Task 9 may not.
- [ ] Commit completed fixes/evidence in focused changes, e.g. `fix: close gameplay-loop prerequisite lifecycle gaps`. Do not commit a claimed M4 pass without its evidence.

## Task 2: Define and validate the first-world content model

**Files:** new progression/catalog definitions and asset adapters from the file map; extend `EventDefinition.cs`, `ContentFingerprint.cs`; new `Tests/EditMode/GameplayCatalogTests.cs`, `Tests/EditModeAdapter/GameplayCatalogAssetTests.cs`.

**Interfaces:** `GameplayCatalog.TryCreate(DrivingContentCatalog driving, CampaignDefinition campaign, IReadOnlyList<BountyDefinition> bounties, EconomyDefinition economy, GadgetDefinitionCatalog gadgets, IReadOnlyList<AiDifficultyDefinition> difficulties, out GameplayCatalog catalog, out string error)` creates one fully validated catalog. `GadgetDefinition.cs` defines the catalog/data types needed by Task 5; it does not implement gadget behavior. IDs use `sunny-island`, `harbor-hop`, `lighthouse-loop`; event IDs are `<course>-<car|motorcycle>-<type>` plus `sunny-island-<class>-finale`. Difficulty IDs are `easy`, `medium`, `hard`.

- [ ] Write catalog tests: `TwoCoursesProduceTwentyFourRegularEventsAndTwoFinales`, `WrongClassMissingReferenceAndImpossibleThresholdReject`, `EveryGameplayValueChangesCompatibilityFingerprint`, `FinaleDefinitionRequiresTwentyFourCompletionsAndThirtyStars`.
- [ ] Pin catalog assertions: 24 regular events, 2 finales, 72 possible regular stars, 3 upgrade levels with prices `[60,120,180]`, and initial challenge IDs `drift-10s`, `boost-3`, `landing-2`. Runtime progression evaluation belongs to Task 6, after the profile model exists. Run `Tools\Verify.ps1 -Mode CoreOffline`; expect the new tests to fail before implementation.
- [ ] Implement definitions, complete cross-reference validation, and fingerprint coverage. Keep fixture catalogs separate from authored shipping content. Add asset conversion validation; reject unknown metric kinds and nonpositive par times without partially loading content.
- [ ] Run CoreOffline and EditMode; all new tests pass. Verify old catalogs still load through explicit legacy defaults and that M4 evidence identities remain distinguishable. Commit `feat: define first-world gameplay content contracts`.

## Task 3: Migrate profiles and make all copies lossless

**Files:** profile files listed above; existing `FoundationService.cs`, `RaceRewardService.cs`, `Runtime/UnityProfileSerializer.cs`; new `Tests/EditMode/ProgressionProfileTests.cs`; extend `ProfileTests.cs`.

**Interfaces:** `ProfileCopy.DeepCopy(ProfileSave source) -> ProfileSave` replaces both manual copying sites. Schema 3 adds `eventProgress`, `worldProgress`, `finaleProgress`, `vehicleUpgrades`, `ownedCosmetics`, `equippedCosmetics`, `activeBounties`, `bountyRotationCursor`, `timeTrialRecords`, `purchaseReceipts`, and `pendingRaceContexts`. Typed records live in `ProfileProgress.cs`: event rows carry `eventId/completed/objectiveMask`; upgrade rows carry `vehicleId/acceleration/handling/boost`; bounty rows carry `assignmentId/bountyId/progress`; records carry `courseId/classId/bestTicks`. `RaceRewardContext` binds `raceId/profileId/mode/rulesFingerprint` to the three original assignment IDs and their applicable definitions. Retain all schema-2 `appliedResults` and their original fingerprints.

- [ ] Add `SchemaOneAndTwoMigrateWithoutLosingSettingsCoinsOrReceipts`, `SnapshotAndDraftDeepCopyAllProgress`, `UnknownFutureSchemaRemainsUntouched`, and invalid-level/duplicate-key/negative-counter rejection tests. Assert migration never writes the file until explicit save.
- [ ] Run CoreOffline and observe the new missing-field/migration failures. Include populated schema-2 receipts, four slots, backup recovery, and serialized round trips through the Unity adapter.
- [ ] Implement ordered migrations `1 → 2 → 3`, profile validation/factory defaults, and complete copy helpers. New/migrated profiles seed the three initial challenge definitions from Task 2 with unique assignment IDs and zero progress. Keep `ProfileStore` atomic replacement and unreadable/newer-save preservation. Settings save must merge against live progression rather than overwriting it with a stale draft.
- [ ] Run CoreOffline and EditMode. Verify every nested collection is independent (`Assert.AreNotSame`) and old receipts remain replay barriers after restart. Commit `feat: persist and migrate first-world progression`.

## Task 4: Run Campaign, Quick Race and Time Trial through shared simulation

**Files:** new local-mode/metrics/result files in the map; existing `NetworkRace.cs`, `DrivingSession.cs`, `AiDriver.cs`, `FoundationService.Gameplay.cs`, `FoundationApp.cs`, `DrivingWorld.cs`; new `Tests/EditMode/SoloRaceTests.cs`, `RaceMetricsTests.cs`, `Tests/PlayMode/SoloRacePlayTests.cs`.

**Interfaces:** `RaceMode` contains `Practice`, `Campaign`, `QuickRace`, `TimeTrial`, `Lan`. `SoloRaceSession.TryCreate(SoloRaceRequest request, ProfileSave profile, GameplayCatalog catalog, out SoloRaceSession session, out string error)` validates selection and creates local state. `Step(DriverInput input, IVehicleSimulation simulation) -> SoloRaceState`, `SetPaused(bool)`, `Abort(string)`, and `Snapshot() -> SoloRaceState` own one local clock. `SoloRaceState` has mode, a four-racer `NetworkRaceState race` for Campaign/Quick Race **or** a `DrivingSessionState trial` for Time Trial, and a finalized `RaceResult result`; the unused branch is null.

- [ ] Add tests `QuickRaceSeatsOneHumanAndThreeSameClassAiWithoutTransport`, `CampaignAndQuickShareCheckpointAndFinishRules`, `TimeTrialHasOneRacerNoItemsAndNoRewards`, `PauseExcludesElapsedAndObjectiveTicks`, `AbortProducesNoRewardableResult`.
- [ ] Run CoreOffline for RED. Pin `RaceMetricsAccumulator` tests to authoritative completed steps and action-time attribution; event-list eviction, repeated snapshots and AI takeover must not alter totals.
- [ ] Add an internal local creation seam to `NetworkRace` for validated per-racer tuning/difficulty. Its existing public LAN factory always uses normalized class tuning and ignores requested upgrades. Keep the network coordinator/DTO names and LAN entry points; avoid an unrelated rename/refactor.
- [ ] Implement the adapter, local rendering/input integration, and shared `RaceResultFactory`. Time Trial reuses the single-racer `DrivingSession`. Results include mode, rules/content identity and per-racer metrics. New results use `formatVersion = 2` and canonical prefix `fr-result-fp-2`; legacy absent/version-1 fields retain the existing `fr-result-fp-1` computation so old receipts remain valid.
- [ ] Bind and durably store local reward context when a progression race starts. For LAN, complete this local preparation against the host's allocated race identity before acknowledging start readiness; a preparation save failure reports failure and keeps that participant from starting. This uses normal save/command paths and does not disclose the profile's bounty definitions to peers. Abort retires the unused context; Task 7 atomically retires a finalized context with its reward receipt.
- [ ] Run CoreOffline/PlayMode: both classes, all three local modes, pause/retry/exit, AI fill, finish/DNF, result immutability and legacy LAN tests. Add visible Easy/Medium/Hard preset facts without position-dependent speed. Commit `feat: add shared local race modes and authoritative metrics`.

## Task 5: Implement authoritative gadgets end to end

**Files:** gadget files, event/track assets, `NetworkRace.cs`, `DrivingSession.cs`, `ArcadeMotor.cs`, `FoundationService.cs`, `Runtime/Networking/NetworkWireCodec.cs`, `NetworkRaceBridge.cs`, `Runtime/Presentation/GadgetPresenter.cs`, tutorial definition/presenter; new `Tests/EditMode/GadgetRulesTests.cs`, `Tests/PlayMode/GadgetPlayTests.cs`, `GadgetNetworkPlayTests.cs`.

**Interfaces:** `GadgetRules.Step(GadgetStepInput input, GadgetWorldState previous, GadgetDefinitionCatalog catalog) -> GadgetStepResult` produces authoritative pickup/item/effect state, transient events and per-racer bounded modifiers. `GadgetStepInput` includes actual traveled segments, recovery flags, control attribution, tick and edge-triggered gadget requests. Consumers apply modifiers through simulation; presentation cannot grant items. `input.set.gadget` remains the shared control; holding consumes at most one item until release.

- [ ] Add tests for one slot, full-slot refusal, equal seeded distribution independent of standings, same-tick collection tie, shield block, turbo refresh, foam immunity/expiry, pause, recovery and bounded cleanup. Pin the exact durations/multipliers in the defaults table.
- [ ] Add `RecoveryCannotCollectOrRespawnPickup`, `DuplicateInputDoesNotUseTwoItems`, `SnapshotReplayDoesNotReplayEffect`, `AiTakeoverCannotEarnHumanGadgetCredit`. Run CoreOffline/PlayMode and observe failures.
- [ ] Implement pure rules, shared motor modifiers, host snapshot/wire validation, and prediction reconciliation. Stale/unknown item generations cannot mutate the host. Enforce bounded world effects and the existing 32 KiB snapshot ceiling with maximum four-racer gadget load; fail honestly if exceeded.
- [ ] Add pooled item/effect/audio presentation, the real touch/keyboard gadget button, and an interactive gadget tutorial lesson using the same pickup/use path. Author English/Hebrew text and packaged cue slots; missing clips remain explicit pending evidence.
- [ ] Run CoreOffline/EditMode/PlayMode, including LAN wire overflow/rejection and rejoin state. Confirm different-height roads do not collect each other's pickups/traps. Commit `feat: add host-authoritative racing gadgets`.

## Task 6: Evaluate objectives, stars, and bounties from final facts

**Files:** `ObjectiveEvaluator.cs`, `CampaignRules.cs`, `BountyService.cs`, `RaceMetricsAccumulator.cs`, `RaceResult.cs`; new `Tests/EditMode/CampaignObjectiveTests.cs`, `BountyTests.cs`.

**Interfaces:** `ObjectiveEvaluator.Evaluate(ObjectiveDefinition definition, RaceResult result, string participantId) -> bool`; `BountyService.Evaluate(ProfileSave profile, RaceResult result, string participantId, RaceRewardContext context, GameplayCatalog catalog) -> BountyEvaluation`; `CampaignRules.Evaluate(CampaignDefinition definition, ProfileSave profile) -> CampaignAccess`, containing available event IDs, unique regional stars, finale availability and world completion. Evaluators return proposed changes without saving or mutating inputs. The award path in Task 7 owns persistence.

- [ ] Write tests for all six objective templates, OR-based non-victory mastery, unique objective-mask union, time equality boundaries, clean streak resets and no recovery landing/shortcut credit.
- [ ] Add `TwentyFourCompletionsAndSixNonVictoryObjectivesUnlockFinales`, `LosingBothFinalesStillCompletesWorld`, `VictoryRewardsOnlyOnce`, `OnlyActiveBountiesAtRaceStartReceiveFacts`, `RotationDoesNotFeedReplacementFromSameResult`. Pin gate boundaries: 23 completions + 30 stars → locked; 24 + 29 → locked; 24 + 30 → both finales available; replaying one three-star event → still 3 stars.
- [ ] Pin eligibility in tests: Campaign/Quick/LAN human facts qualify; Time Trial/practice/tutorial/DNF/abort do not; AI-attributed segments never qualify. Use the three assignments bound in the local `RaceRewardContext` from Task 4, not whatever assignments happen to be active when a delayed result arrives.
- [ ] Run CoreOffline for RED, then implement pure evaluators and deterministic eligible-catalog rotation. Complete a maximum of the three assignments bound to the result; replacements begin at zero. Preserve one-time finale rewards separately from repeatable bounties.
- [ ] Run CoreOffline; assert equal results across repeated evaluation and unchanged source profile/result objects. Commit `feat: evaluate campaign objectives and offline bounties`.

## Task 7: Commit rewards, unlocks and records atomically

**Files:** `RaceRewardService.cs`, `AppliedResultReceipt.cs`, `FoundationService.Progression.cs`, profile data and result fingerprint code; new `Tests/EditMode/ProgressionRewardTests.cs`, `TimeTrialRecordTests.cs`; extend `NetworkRewardTests.cs`.

**Interfaces:** new `RaceRewardService.Apply(RaceResult result, string participantId, ProfileSave savedProfile, ProfileStore store, GameplayCatalog catalog, RaceRewardContext context) -> RewardApplyResult`. Preserve the existing overload for legacy completion-only results. `RewardApplyResult` adds persisted award breakdown and changed progress/record facts; each receipt binds result fingerprint and rules identity. `TimeTrialRecordRules.Evaluate(RaceResult result, string participantId, ProfileSave profile) -> TimeTrialRecordUpdate` returns a proposed best record using lower positive elapsed ticks only, grouped by course/class.

- [ ] Test a single result that completes an objective, a bounty, a world unlock and a cosmetic: either all changes and the receipt persist, or none do. Inject write failure; assert live profile and file hashes unchanged.
- [ ] Add `RestartAndRedeliveryDoNotRepayAnyAward`, `ConflictingResultIdentityRejectsAfterRestart`, `WrongParticipantAndInvalidModeReject`, `NewRaceCanAwardCoinsButNotDuplicateStars`, and overflow tests. Time Trial equality/slower time does not replace the best; a faster normalized run does; coins and campaign state stay unchanged.
- [ ] Run CoreOffline for RED; extend the candidate-copy → atomic save → publish pattern. Bind reward amounts to finalized validated content facts, not UI/client claims. Expose pending/persisted/failed status and a retryable failed transaction; prevent starting another progression race or purchasing while a final reward remains unresolved.
- [ ] Consume the durable local reward context from Tasks 3–4; remove it only in the same successful save as the award receipt. Reject missing/conflicting context for new progression results rather than guessing. A retained result can be retried against its original assignment/rules context after reconnect/restart; never against newly rotated assignments. Older completion-only results keep their explicitly limited legacy path.
- [ ] Run CoreOffline/EditMode and legacy reward tests, including old receipt deduplication. Commit `feat: atomically persist complete race progression`.

## Task 8: Add upgrades, cosmetics, and mode-specific selection rules

**Files:** `UpgradeService.cs`, `VehicleTuningResolver.cs`, `RaceModeRules.cs`, profile purchase receipts, gameplay service partials, vehicle visual bindings; new `Tests/EditMode/UpgradeTests.cs`, `ModeEligibilityTests.cs`, `Tests/PlayMode/UpgradePresentationPlayTests.cs`.

**Interfaces:** `UpgradeService.Purchase(string purchaseId, string vehicleId, string category, int expectedLevel, ProfileSave profile, ProfileStore store, GameplayCatalog catalog) -> PurchaseResult`; `VehicleTuningResolver.Resolve(RaceMode mode, string vehicleId, ProfileSave profile, GameplayCatalog catalog) -> VehicleTuning`; `RaceModeRules.CanEnter(RaceMode mode, string courseId, string eventId, string vehicleId, ProfileSave profile, GameplayCatalog catalog) -> EntryDecision` is shared by discovery, UI and start commands.

- [ ] Add price/cap tests with assertions: 59 coins cannot buy level 1; 60 buys level 1 and leaves 0; next levels cost 120/180; level 4 refuses; maximum multiplier is `1.15f` within float tolerance; a failure changes neither money nor tuning.
- [ ] Add `DuplicatePurchaseIdNeverChargesTwice`, `ConflictingPurchaseIdRejects`, `StaleExpectedLevelCannotBuyNextLevelOnRetry`, `PurchaseSurvivesRestart`, and “locked vehicle / unknown category / in-race purchase” rejection. Persist purchase identity and expected level, not only the session command cache.
- [ ] Add selection tests proving locked vehicles reject in Campaign/Quick via both buttons and raw commands; LAN exposes both implemented vehicle tiers; LAN/Time Trial tuning equals the class baseline even for a max-upgraded profile. Purchased tune resolution returns a new object and leaves the catalog fingerprint unchanged.
- [ ] Run CoreOffline for RED, then implement atomic purchases and common eligibility. Resolve tuning at race start; display exact level/cost/effect and a visible installed-part indicator. Earn/equip paints and accessories without changing collision or stats. Cosmetics never bypass ownership checks.
- [ ] Run CoreOffline/PlayMode, including character/vehicle swaps and profile switching. Commit `feat: add durable upgrades cosmetics and selection rules`.

## Task 9: Author and package the complete Sunny Island world

**Gate:** Task 1's M4 tablet visual/performance evidence and owner visual acceptance must be recorded before production course expansion. Test fixtures used by earlier tasks do not satisfy this deliverable.

**Files:** `Editor/GameplayLoopSetup.cs`, `SunnyIslandCampaignAuthoring.cs`, `Runtime/Driving/CourseLoader.cs`, content assets under `Generated/GameplayLoop/`; existing `FoundationBuild.cs`, `Tools/Verify.ps1`, `Build-AndroidOffline.ps1`; new `Tests/EditModeAdapter/GameplayLoopContentTests.cs`, `Tests/PlayMode/GameplayCoursePlayTests.cs`; extend both PowerShell tooling fixtures and asset inventory.

**Interfaces:** `CourseLoader.Load(string courseId) -> CourseLoadResult` loads exactly one authored course, disposes prior colliders/pickups/visuals, and reports actual load status. `GameplayLoopSetup.Create()` validates/regenerates only owned generated assets. New Verify modes: `GameplayLoopSetup`, `WindowsGameplayLoop`, `WindowsGameplayLoopOrdinary`, `AndroidGameplayLoop`, `AndroidGameplayLoopOrdinary`; Android adds `-Presentation GameplayLoop` with separate output/export roots.

- [ ] Add content tests asserting exactly 2 production courses, 24 regular events, 2 finales, 4 usable vehicle models, class-correct references, bilingual names, compatible cosmetics, positive measured par times and reachable objective opportunities.
- [ ] Build Harbor Hop from the qualified treatment without changing the developer benchmark IDs. Author Lighthouse Loop as a distinct route; both require a readable main route, two legal optional shortcuts, a landmark jump, four separated spawn slots, recovery poses and appropriate pickup placement.
- [ ] Tune each event's laps/thresholds through actual unupgraded play. Record per-course/class time measurements used for `parTicks`; verify stunt/boost/gadget opportunities are sufficient on both classes. Author rivals with recognizable existing animals/liveries and honest AI policies. Ship meaningful `car-02`/`motorcycle-02` visuals alongside their normalized LAN stats.
- [ ] Implement one-course-at-a-time loading, camera rebinding, repeatable setup and all four build flavors. Setup twice must preserve authored content and stable gameplay identity. Ordinary builds must expose no validation endpoint.
- [ ] Run EditMode/PlayMode, `Tools\Test-VerifyTooling.ps1`, `Tools\Test-AndroidOffline.ps1`, and all four new builds. Repeat course switching/rematches; assert no old colliders/pickups/effect leaks, no stale event selection and stable memory. Commit `feat: deliver the complete Sunny Island campaign content`.

## Task 10: Expose the complete loop through bilingual UI, commands, and state

**Files:** service/shell partials and state files in the map, `FoundationInput.cs`, `FoundationApp.cs`, `ShellText.cs`, `Runtime/Automation/StateStream.cs`, `ValidationJson.cs`, `NetworkWireCodec.cs`, `Core/Networking/NetworkSettings.cs`, `Tools/AgentScenario.py` (independent reconstruction); new `Tests/PlayMode/GameplayScreensPlayTests.cs`, `GameplayStreamPlayTests.cs`, `Tests/EditMode/GameplayCommandTests.cs`; `docs/COMMANDS.md`, `docs/FEATURE_COVERAGE.md`.

**Interfaces:** reuse `GameCommand`/`CommandResult`. New semantic actions: `campaign.open`, `campaign.start(eventId,vehicleId,difficulty)`, `quickRace.start(courseId,classId,vehicleId,difficulty)`, `timeTrial.start(courseId,classId)`, `garage.open`, `bounties.open`, `upgrade.purchase(purchaseId,vehicleId,category,expectedLevel)`, `cosmetic.equip(slot,cosmeticId)`, `results.retrySave`, `results.retry`, `results.continue`. No command grants coins/stars or bypasses course traversal. Existing `lan.*`, input, pause, camera and tutorial actions remain shared.

- [ ] Add UI/command parity tests for profile → campaign → event → race → reward → next event, garage purchase/equip, bounty progress/rotation, Quick Race, Time Trial and replay. Test locked/unavailable reasons, pending/failed saves, invalid parameters and duplicate command identities.
- [ ] Add schema-6 reconstruction/deep-copy tests: campaign access, per-objective progress, three bounty assignments, wallet/upgrades/cosmetics, normalized tuning source, local race/result, records, pickups/effects, save state and result delivery. Preserve transient same-tick events and source labels (`authoritative`, `predicted`, `interpolated`).
- [ ] Implement screens with icons, large touch targets, clear stars/coin breakdowns, explicit locked reasons and one-action replay/continue. Use complete English/Hebrew strings, logical Hebrew order, LTR numeric/time fields and unchanged steering meaning. Do not show inaccessible M6 content as playable.
- [ ] Verify the version changes introduced with their owning contracts in Tasks 2–5. Complete independent validator, old scenario compatibility and canonical wire/fingerprint tests together; refuse incompatible peers before seat allocation. New fields are available from the first snapshot, including menus and failed saves.
- [ ] Run CoreOffline/EditMode/PlayMode and Python validator tests; prove touch/widget callbacks and commands reach the same service. Capture both languages and both cameras. Commit `feat: expose the complete bilingual gameplay loop`.

## Task 11: Prove the loop externally, including restart and hostile delivery

**Files:** new scenario/test/runbook/report files in the map; extend `Tools/RestartScenario.py`, `NetworkScenario.py` and shared validation helpers only as needed.

**Interfaces:** `GameplayLoopScenario.py` accepts either `--launch <development-player>` or `--connections <descriptor...>`, plus `--evidence <new-directory>` and `--scope smoke|campaign|lan`. Fixtures require an explicit test-only option and are labeled in every trace. Real progression evidence uses normal start/input/result/purchase paths.

- [ ] Write Python harness tests with deliberately corrupted traces: missing delta, silent observer gap, wrong attribution, double award, stale result context, fake normalized tuning, failed save shown as persisted, missing process generation and timeout. Each mutant must produce a failing verdict with its reason.
- [ ] Implement smoke: fresh synthetic profile → tutorial/gadget → finished campaign event → stars/coins → enough legitimate earnings for a 60-coin upgrade → purchase → process restart → preserved progress → Quick Race → Time Trial record → replay without campaign credit.
- [ ] Implement full campaign: drive all 24 events plus both finales, across both classes/courses; demonstrate at least six non-victory introductory stars and finale advancement after losses. Replays must not inflate stars; one-time rewards must stay once-only. Prove every authored objective is achievable, separately from the non-victory advancement route.
- [ ] Implement LAN: uneven unlocks/upgrades but equal class performance, gadget authority, pause, disconnect/AI/rejoin with attribution checks, duplicate/conflicting result delivery, failed-save retry and permanent departure. A fresh process attaches to retained result state without minting rewards again. Run two-client and four-endpoint workloads; label loopback/AI versus physical participants.
- [ ] Run `python -m unittest discover -s Tools -p 'Test*.py'`, then smoke/campaign/LAN on fresh development builds. Save commands, reconstructable NDJSON, assertions, failure reasons, logs, screenshots, source/content/build hashes and fixture declarations. Commit `test: qualify the complete gameplay loop through real players`.

## Task 12: Qualify M5 and publish an honest milestone report

**Files:** `docs/tasks/gameplay-loop-report.md`, `docs/GAMEPLAY_LOOP_RUNBOOK.md`, `docs/FEATURE_COVERAGE.md`, `docs/ASSET_INVENTORY.md`, `IMPLEMENTATION_STATUS.md`, `README.md`; content tuning changes only when evidence requires them.

**Interfaces:** report rows use `passed`, `failed`, or `notVerified`, with exact artifact/run identity and named remaining owner. Development completion and milestone acceptance are separate verdicts.

- [ ] Run final source-consistent checks: `Tools\Verify.ps1 -Mode CoreOffline`, `-Mode EditMode`, `-Mode PlayMode`; Python tooling and both PowerShell tooling suites. Record counts/failures/ignores explicitly. New failures block acceptance; inherited failures cannot be described as a clean pass.
- [ ] Build all four GameplayLoop flavors from that source/content revision. Record hashes. Run smoke, full-campaign and LAN scenarios on the matching development artifacts; verify ordinary Windows/Android endpoints are absent. Any later runtime/content fix requires affected evidence on the rebuilt artifact.
- [ ] Qualify A7 Lite hosting with four racers, both courses/classes/cameras, sustained sessions and repeated mode/course switches. Retain 30 fps target, p95 ≤40 ms, 800 MB process-memory investigation budget, and the M4 stall review rule. Measure PC 60 fps at 1080p and quantify matched capture overhead; a 30 Hz logical clock is not rendered-frame evidence.
- [ ] Run tablet↔tablet and tablet↔PC in both hosting directions, internet-disconnected Wi-Fi, four physical humans, impaired network, rematches/reconnect and ordinary-build play. Preserve each missing row as `notVerified`.
- [ ] Owner/Hebrew-speaker/child checks: a five-year-old understands a reward, chooses the next race, uses a gadget and can finish; losses still progress; upgrade effects and bounty goals are understandable; text/RTL/audio/cameras/touch work. Measure first-world duration/retries and enjoyment, without claiming the full 6–10 h campaign target from one world.
- [ ] Reconcile all M1–M4 dependencies and M5 requirements. M5 acceptance requires two real courses, 26 playable authored events, persisted progression/purchases/records, normalized LAN/Time Trial, complete command/state coverage and the device/human rows. Commit the evidence/report; only then mark M5 complete.

## Checkpoints, risks, and handoff

| Checkpoint | Reviewable result |
|---|---|
| After Task 4 | Campaign/Quick/Time Trial simulations run on fixture content with honest results; saves migrate safely |
| After Task 8 | Gadgets, objectives, bounties, rewards, purchases and normalization work together on existing content |
| After Tasks 9–10 | First world is fully playable through the family UI and agent commands |
| After Tasks 11–12 | Same-build automated, ordinary-build, physical-device and family evidence supports acceptance |

Main risks are inherited rejoin latency, content opportunity/threshold mismatch, duplicated result credit, profile-copy loss, course-loading memory, and unavailable physical hardware. Each has an owning task above. Keep the hardware-dependent course gate visible; do not quietly ship a one-course or no-gadget substitute for M5.

This is a quality-gated plan, not a deadline estimate. The sequence is intentionally dependent; local Core fixtures permit progress while M4 device access is pending. A final self-review should map every spec requirement to a task, check API/type consistency, and ensure all five review-focus cases have explicit tests.

**Planning self-review:** all M5 scope items map to Tasks 2–12; inherited gates map to Task 1/12; first-world counts are 2 × 2 × 6 + 2 = 26; M6 retains 52 events. Required prices, star gates, platform scope and normalization come from the approved spec. Optional economy/gadget/objective defaults are explicitly labeled proposals. No implementation, build, test pass, device approval, or execution-method selection is claimed by this planning document.
