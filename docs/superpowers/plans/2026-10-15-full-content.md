# Milestone 6: Full Content Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. This request authorizes planning only; execution method has not been selected.

**Goal:** Deliver the full offline family adventure: three worlds, six courses, four rally cars and four motorcycles, 72 regular events plus six rival finales, final Hebrew/English prompts, and measured campaign pacing.

**Architecture:** Extend the existing Core gameplay catalog from one campaign world to an ordered collection; reuse the shared simulation, objective evaluation, atomic profile transactions, and semantic command pipeline. Finish M5's gated first-world authoring and course loader before expanding production content, then use that pipeline for Redrock Canyon and Snowy Peaks. Content validation, external scenarios, and physical playtests each supply distinct acceptance evidence.

**Tech Stack:** Pinned Unity 6000.3.26f1, C# 9 / .NET Standard 2.1-compatible Core, URP 17.3.0, Input System 1.20.0, UI Toolkit, NGO 2.7.0 / UTP 2.6.0, Unity Test Framework, existing Python/PowerShell tools. No new runtime package is planned.

**Spec:** [GAME_PLAN.md](../../../GAME_PLAN.md), especially sections 2–4; [IMPLEMENTATION_STATUS.md](../../../IMPLEMENTATION_STATUS.md) identifies it as the approved design specification. Inherited work: [M5 plan](2026-10-14-complete-gameplay-loop.md), [M5 report](../../tasks/gameplay-loop-report.md), and [M4 report](../../tasks/visual-benchmark-report.md). The implementation choices below are proposed defaults, not previously approved scope changes.

## Global constraints

- "Samsung Galaxy Tab A7 Lite SM-T220; assume the weaker 3 GB configuration"; Android tablets and Windows PC.
- "All gameplay works offline after installation"; "Two to four people over home Wi-Fi; four racers total".
- "Four rally cars and four dirt motorcycles; separate race classes"; four existing animal characters remain cosmetic.
- "Three worlds, six shared courses, one combined campaign"; "Build **78 authored event configurations**".
- "Complete the world's 24 regular events and earn 30 regional stars to unlock its two finales." Completing both finales advances the adventure; rival victories are optional.
- "Campaign and Quick Race respect profile unlocks. Local multiplayer exposes every course and vehicle model." LAN and Time Trial remain normalized; Time Trial grants records only.
- "Load one course at a time." Preserve class-identical collision/gameplay across tablet and PC render profiles.
- "Every game control and feature is command-accessible; complete logical game state is streamed to a structured sink for end-to-end agent testing". Missing history invalidates evidence; fixture progress is never production-play evidence.
- "The 6–10 hour duration is a playtest target." No waiting timers, compulsory purchases, mandatory wins, or manufactured grind.
- "The budget is a planning cap, not authorization to purchase." Recorded spending is US$0 of US$100; use existing/original assets and maintain attribution.
- Arcade remains the family-build backend; PhysicalAssisted remains opt-in. Signed release distribution, update installation qualification, and backup instructions remain M7.

## Review focus

1. A raw event ID enters a locked world, or another world's stars open its finales — Task 2 pins world ownership and regional isolation.
2. Replaying a finale, retrying a failed save, or upgrading from an M5 profile loses/doubles progress — Task 2 pins atomicity, existing receipts, and lossless schema-3 loading.
3. Course transitions retain old collision, effects, lightmaps, audio, or loading state — Tasks 4 and 10 test repeated switching and failed loads before simulation starts.
4. A catalog has 78 rows but duplicates a class/type, contains an impossible objective, or ships silent Hebrew prompts — Tasks 4–9 separate structural checks from actual driving and listening evidence.
5. An external driver assumes graybox coordinates or reports a partial/fixture run as full completion — Task 9 adds independent coverage reconciliation and corruption tests.

## Starting point and scope

Inspected on 2026-10-15 at source revision `ade14bd`. The checkout moved from `feat/m5-gameplay-loop` to `main` through a fast-forward during inspection; the final source revision was rechecked. Existing material/generated-asset/project-setting changes were already present and are outside this planning change. No tests, builds, purchases, voice generation, or device qualification were run for this plan.

- M5 systems exist, but its Task 9 was never dispatched: no bound production gameplay catalog, no Harbor Hop/Lighthouse Loop campaign, and no GameplayLoop build flavors. Progression commands currently reject `contentUnavailable` in the packaged player.
- `GameplayCatalog.Campaign`, `GameplayCatalogAsset.campaign`, reward/bounty evaluation, eligibility, and the campaign screen currently assume one world. This is a real M6 code change, not just adding assets.
- `DrivingWorld` still binds one `track` and `coursePrefab`; M5's planned `CourseLoader` does not yet exist. Do not treat that planned API as shipped code.
- Profile schema **3** already stores event/world/finale lists and vehicle IDs. Observation schema is **6**, LAN wire protocol **3**, gameplay rules identity **gameplay-3**; these are different contracts.
- M5's report supersedes stale M4 defect rows: the credentialed-rejoin input stall and known font/impaired-link test defects were closed in M5. Do not redo them without a fresh failure. Device/human acceptance, ordinary-build performance, voice clips, and generation-2 memory evidence remain open.
- M1–M5 acceptance remains open. Historical test counts in reports are evidence for their recorded revisions, not fresh results for M6.

**Included:** inherited first-world completion as a prerequisite; ordered world progression; four additional courses and four additional vehicle models beyond M5; 52 additional campaign events; all six rival identities/liveries; final bilingual prompts; full-campaign external coverage, pacing, and device qualification.

**Excluded:** new game modes, new gadgets, extra animal characters, ghosts, controller support, online services, asset purchases, a physics-default switch, and M7 signing/distribution work.

### Approach

| Option | Tradeoff |
|---|---|
| **Extend the shared catalog and expand one world at a time — recommended** | Keeps one progression ledger and one set of rules; exposes performance/content problems before all four new courses are produced. |
| Separate executable/scene with copied gameplay systems per world | Initially easy to author, but duplicates saves, commands, mode selection, and network compatibility; reject. |
| Produce all remaining art/events before qualification | Defers the known tablet and first-world risks across the whole content set; conflicts with the existing expansion gate. |

## Dependencies and stop conditions

Execution order: **0 inherited closeout → 1 catalog → 2 progression → 3 UI/state → 4 production pipeline → 5 vehicles → 6 Redrock → 7 Snowy → 8 audio → 9 campaign evidence → 10 qualification**.

Tasks 1–3 and the test-only parts of Tasks 4/9 may proceed with clearly labeled fixtures while hardware is unavailable. Production work in Tasks 4–8 requires Task 0's M4 gate and a playable, verified M5 first world. Redrock must pass its world checks before Snowy production begins. M6 acceptance requires all inherited mandatory gates, not just code completion.

| Gate | Required evidence / owner |
|---|---|
| M4 tablet production gate | Real lighting bake; owner visual approval on SM-T220; both classes/cameras; four racers with tablet hosting; sustained 30-minute rendered performance and memory evidence. Hardware owner supplies devices; implementer runs capture and fixes measured regressions. |
| M5 production foundation | Execute the existing M5 Task 9 after that gate: two authored courses, 26 events, four vehicles, course loader, and four GameplayLoop build flavors. Re-run M5 smoke/campaign/LAN evidence with authored content, including previously unavailable rows. |
| Inherited acceptance | Carry open M1–M5 rows with status, owner, and artifact references. Recheck the fresh-process memory sampler, ordinary/development matched workloads, physical LAN matrix, offline operation, touch/keyboard parity, and human reviews. |
| Four physical players | Two tablets plus PC are recorded as available; a fourth supported device must be supplied before this row can pass. Loopback or AI-filled runs are development evidence only. |
| Final speech | Owner-provided authoring credentials or licensed recordings; package assets for offline playback. Never commit credentials or count silence/placeholder clips as final audio. |

Unavailable hardware/credentials leave the relevant row `notVerified`; they do not block unrelated fixture-based Core work. A failed tablet performance gate stops further production expansion until measured causes are corrected.

## Content and behavior decisions

### Production inventory

| World ID | Courses | Regular / finales | Completion unlocks |
|---|---|---:|---|
| `sunny-island` | `harbor-hop`, `lighthouse-loop` | 24 / 2 | `car-02`, `motorcycle-02` |
| `redrock-canyon` | `canyon-dash`, `mesa-skyway` | 24 / 2 | `car-03`, `motorcycle-03` |
| `snowy-peaks` | `snowpine-circuit`, `aurora-pass` | 24 / 2 | `car-04`, `motorcycle-04` |

Starter vehicles remain `car-01` and `motorcycle-01`. Each course/class pairing has exactly one circuit, boost, stunt, time, gadget, and clean-driving event: **6 × 2 × 6 = 72 regular events**, plus six finales, **216 possible regular stars**. Tutorial/practice/Time Trial helper definitions are not part of the 78 count.

- Preserve all M5 shipped IDs. For new regular events use `<courseId>-<classId>-<type>`, with `classId` `car|motorcycle` and `type` `circuit|boost|stunt|time|gadget|clean`. New finale IDs are `<worldId>-<classId>-finale`; rival IDs are `<worldId>-<classId>-rival`, with `livery-<rivalId>` cosmetics.
- World order is Sunny Island → Redrock Canyon → Snowy Peaks. World one is open initially; completing the immediately previous world opens the next. Both finales completed is sufficient, regardless of victory. Previously completed worlds stay open.
- Each available world exposes its 24 regular events immediately. Its own 24 completions **and** 30 regional stars open its finales. Stars from other worlds never count. Replays union objective bits; final-world completion displays an adventure-complete state and preserves replay access.
- Quick Race respects course-world and vehicle unlocks. LAN exposes all six courses/eight models regardless of profile progress, with class-normalized tuning. Proposed Time Trial policy: expose all six courses from the start, using normalized starter vehicles and independent course/class records; this extends M5's all-implemented-course policy.
- Completing each world's two finales atomically grants its two vehicles once. Beating a rival separately grants its named bounty and cosmetic once. The fourth vehicle in each class remains useful in replay/Quick Race/LAN after the adventure ends.
- Keep M5 objective semantics and initial economy (20 completion coins; placement bonuses 5/3/1/0; upgrade costs 60/120/180, +5% per level, three levels). Change tuning only from recorded play evidence, preserving non-victory advancement and unupgraded completability.
- Every course needs a readable main route, two distinct legal optional shortcuts, a landmark jump, four separated spawn slots, validated recovery poses, and enough pickups/landings/drift opportunities for both classes. Snow/ice accents are visual in this milestone; no new traction/surface simulation is assumed.
- Target 2–4 minute event attempts, then measure family sessions including learning/retries against the 6–10 hour target. Do not present scripted-agent completion time as family pacing.

### Shared interfaces and version policy

Paths below are relative to `Assets/FamilyRacing/` unless prefixed with `Tools/` or `docs/`. New interfaces are proposals to implement, not current APIs.

| Boundary | Proposed contract / responsibility |
|---|---|
| Core catalog | `GameplayCatalog.Campaigns : IReadOnlyList<CampaignDefinition>` in authored order; `FindCampaign(string worldId)` and `FindCampaignForEvent(string eventId)` return a definition or null. Add a multiworld `TryCreate` overload with the existing remaining arguments. Retain the single-world overload and `Campaign` first-world alias for old fixtures, but migrate production consumers. |
| Core adventure access | New `Progression/AdventureRules.cs`: `Evaluate(GameplayCatalog catalog, ProfileSave profile) -> AdventureAccess`; `CanAccessCourse(GameplayCatalog catalog, ProfileSave profile, string courseId) -> bool`. `AdventureAccess` has `List<WorldAccess> worlds`, `bool complete`; each `WorldAccess` has `worldId`, `available`, `lockedReason`, and `CampaignAccess campaign`. Regional math stays in `CampaignRules.Evaluate`. |
| Asset conversion | `GameplayCatalogAsset.campaigns : CampaignDefinitionAsset[]`; retain legacy `campaign` fallback only when the array is empty. Reject both populated rather than silently choosing. Generic catalogs can contain smaller fixtures; production exact counts belong to the production validator. |
| Campaign selection | New `campaign.selectWorld(worldId)` semantic command. Selection is transient per active profile; on profile switch choose the first accessible unfinished world, or the last world if all are complete. Existing `campaign.start(eventId, vehicleId, difficulty)` resolves the event's world and checks access independently of selected UI state. |
| Observation | Add `progression.worlds` with per-world availability, safe locked reason, regional counts, finale availability/completion, plus `adventureComplete`. Existing `campaignWorldId`/`campaignEvents` describe the selected world. Deep-copy all new rows and preserve full snapshot/delta reconstruction. |
| Production validation | New `Editor/FullContentValidation.cs`: `Validate(GameplayCatalogAsset asset, DrivingContentCatalog driving) -> IReadOnlyList<string>`; empty means structurally valid, not playtested. New `Editor/FullContentSetup.cs`: `Create() -> void`, throws/fails the build on invalid production content. |
| Evidence tool | New `Tools/FullContentScenario.py`, reusing the existing connection/capture helpers: `--launch <exe>` or `--connections <connection.json> ...`, `--scope catalog|campaign|modes|lan`, required `--evidence <new-dir>`, optional `--fixture`. Fixture results can never pass production acceptance. |

Keep profile schema 3: the existing ID-keyed progress collections already express this expansion, and selected world need not be persisted. Move full-content identity to `full-content-1`, gameplay rules identity to `gameplay-4`, and observation schema to **7**. Keep LAN wire protocol 3 unless a wire DTO actually changes; content/rules compatibility must reject old or different catalogs before seating. Fingerprint every world's membership/order/gates/rewards and all gameplay-affecting content; cosmetic text/audio changes alone do not change gameplay fingerprints.

## Task 0: Close the production prerequisites without rewriting history

**Files:** Existing M4/M5 reports/runbooks; M5 Task 9's proposed `Editor/GameplayLoopSetup.cs`, `Editor/SunnyIslandCampaignAuthoring.cs`, `Runtime/Driving/CourseLoader.cs`, generated content, build tools, and tests. Update `IMPLEMENTATION_STATUS.md` only with actual evidence.

**Interfaces:** Produces the M5 plan's `CourseLoader.Load(string courseId) -> CourseLoadResult`, a bound two-course gameplay catalog, four GameplayLoop build flavors, and an evidence table identifying remaining inherited gates.

- [ ] Reconcile reports against current source and artifact identities; list each open dependency, owner, and next check. Reproduce only unresolved or newly failing defects.
- [ ] Execute the M4 tablet/visual gate and record owner acceptance; if unavailable, retain `notVerified` and continue only allowed fixture work.
- [ ] Execute M5 Task 9 and its content/loader/build tests exactly under its gate; finish M5 authored-content scenarios and family first-world review before expanding production.
- [ ] Run `Tools\Verify.ps1 -Mode CoreOffline`, `EditMode`, and `PlayMode` separately; run M5 smoke/campaign/LAN using `Tools\GameplayLoopScenario.py`. Require real 24+2-event evidence; no `contentUnavailable` row can satisfy an authored-content gate.
- [ ] Commit only scoped closeout changes with their measured report. Do not relabel an earlier milestone accepted while its mandatory rows remain open.

## Task 1: Support ordered multiworld content and compatibility

**Files:** Modify `Core/Progression/GameplayCatalog.cs`, `Core/Racing/ContentFingerprint.cs`, `Core/Networking/NetworkSettings.cs`, `Runtime/Content/GameplayCatalogAsset.cs`; extend `Tests/EditMode/GameplayCatalogTests.cs`, `ContentFingerprintTests.cs`, and `Tests/EditModeAdapter/GameplayCatalogAssetTests.cs`.

**Interfaces:** Produces the catalog/asset contracts above. Existing single-world constructors remain usable by M5/legacy tests; production uses `Campaigns` and lookup methods.

- [ ] Add failing fixtures for three distinct worlds, duplicate world/event/course ownership, missing finale course/reward references, wrong-class reward vehicles, regular/finale overlap, conflicting legacy/new asset fields, and complete lookup coverage. Pin assertions such as `Assert.That(catalog.Campaigns.Count, Is.EqualTo(3));` and `Assert.That(catalog.FindCampaignForEvent(redrockEvent).worldId, Is.EqualTo("redrock-canyon"));`.
- [ ] Run `Tools\Verify.ps1 -Mode CoreOffline` and `EditMode`; confirm new failures concern the missing contract/validation before implementing.
- [ ] Implement the overload/lookups and per-world cross-reference validation; fingerprint ordered world progression and reward associations. A change to a later world's gate or course pickup data must change the fingerprint; translated names must not. Apply the content/rules identity policy above.
- [ ] Re-run the suites and existing admission tests; same full catalog accepts, different/old catalogs reject before allocating a slot. Commit `feat: support ordered multiworld gameplay catalogs`.

## Task 2: Enforce world unlocks and atomic rewards across all modes

**Files:** Create `Core/Progression/AdventureRules.cs`, `Tests/EditMode/AdventureRulesTests.cs`; modify `Core/Racing/RaceModeRules.cs`, `Core/Profiles/RaceRewardService.cs`, `Core/Progression/BountyService.cs`; extend `ModeEligibilityTests.cs`, `ProgressionRewardTests.cs`, `ProgressionProfileTests.cs`, and `BountyTests.cs`.

**Interfaces:** Consumes Task 1 lookup contracts; produces `AdventureAccess`/`WorldAccess` and course access checks. Reward processing resolves the owning world from the immutable result's `eventId`, never the current menu selection. Existing atomic reward APIs and receipt identities stay intact.

- [ ] Write failing tests for fresh profile access `[true,false,false]`; 24 completions with 29 stars stays gated; 23 completions with 30 stars stays gated; 24/30 opens only that world's finales; completing both without wins opens the next world and grants exactly two vehicles. Assert cross-world stars and raw `solo.start`/`campaign.start` IDs cannot bypass access.
- [ ] Add tests for Quick Race locked-course rejection, all-content LAN access, normalized Time Trial without rewards, final adventure completion, and six independent rival rewards. Cover second-finale save failure: no changed in-memory profile/unlock/receipt until retry commits, then duplicate delivery changes nothing.
- [ ] Add a schema-3 M5 profile round-trip with upgrades, receipts, bounties, records, and Sunny completion intact; expect Redrock open, Snowy locked. Unknown retained IDs are preserved. Run CoreOffline to establish red tests.
- [ ] Implement the pure rules and migrate campaign consumers in eligibility, rewards, and bounty rival lookup. Preserve blocked/read-only profile behavior, result rules-fingerprint checks, and no rewards on DNF/abort/AI skill actions.
- [ ] Run CoreOffline and EditMode green, including existing transaction regressions. Commit `feat: advance the adventure with regional once-only rewards`.

## Task 3: Expose worlds through the bilingual UI and state stream

**Files:** Modify `Core/Foundation/FoundationService.cs`, `FoundationService.Progression.cs`, `FoundationState.cs`, `Core/Localization/ShellText.cs`, `Core/Networking/NetworkSettings.cs`, `Runtime/FoundationShell.Progression.cs`, `Runtime/Automation/ValidationJson.cs`/`StateStream.cs`, `Tools/AgentScenario.py`; extend `GameplayCommandTests.cs`, `GameplayScreensPlayTests.cs`, `GameplayStreamPlayTests.cs`; update `docs/COMMANDS.md` and `docs/FEATURE_COVERAGE.md`.

**Interfaces:** Consumes Tasks 1–2; produces `campaign.selectWorld`, schema-7 world rows, selected-world event rows, accessible course pickers, and final adventure completion presentation.

- [ ] Add failing tests for UI/command parity, unknown/locked world rejection with no partial selection mutation, command deduplication, selection reset on profile switch, selected-world regional totals, and results returning to the correct world. Assert `worlds.Count == 3` and streamed/UI availability equals `AdventureRules.Evaluate`.
- [ ] Add stream reconstruction/deep-copy tests through selection while paused, unlock after reward commit, failed-save retry, profile switch, and reconnect. Use synthetic UI tests for English/Hebrew world cards, longer names, portrait/color/icons and scrolling; physical layout remains a human check.
- [ ] Run CoreOffline/PlayMode red, then implement shared commands and UI callbacks, world-aware Quick Race/Time Trial pickers, localized locks and adventure-complete copy. Bump schema to 7 and update independent reconstruction; do not change the command protocol unnecessarily.
- [ ] Run CoreOffline/EditMode/PlayMode and `python -m unittest discover -s Tools -p 'Test*.py'` green. Commit `feat: expose world progression through commands and bilingual screens`.

## Task 4: Validate, load, and package the full content set

**Gate:** Production portion requires Task 0. Adapt the actual inherited loader rather than implementing a second one.

**Files:** Create `Editor/FullContentSetup.cs`, `Editor/FullContentValidation.cs`, `Tests/EditModeAdapter/FullContentValidationTests.cs`, `Tests/PlayMode/FullContentCoursePlayTests.cs`; extend M5 `Runtime/Driving/CourseLoader.cs`, `DrivingWorld.cs`, `Runtime/FoundationApp.cs`, `Editor/FoundationBuild.cs`, `Tools/Verify.ps1`, `Tools/Build-AndroidOffline.ps1`, and both PowerShell tooling test suites. Create `docs/FULL_CONTENT_RUNBOOK.md` and `docs/CONTENT_MATRIX.md`.

**Interfaces:** Produces validation/setup contracts above and Verify modes `FullContentSetup`, `WindowsFullContent`, `WindowsFullContentOrdinary`, `AndroidFullContent`, `AndroidFullContentOrdinary`. Android uses `-Presentation FullContent`; outputs are separate `Builds/<Platform>-FullContent[-Ordinary]` roots. `CONTENT_MATRIX.md` is the reviewable inventory, linked to evidence, not a second runtime catalog. For intermediate world/device checks, add `WindowsFullContentPreview` and `AndroidFullContentPreview`: development-only builds using generic catalog validation, separate `Builds/<Platform>-FullContent-Preview` roots, and a manifest with `contentStage=partial`. Preview setup binds only the available authored content and may never produce an ordinary or accepted full-content candidate.

- [ ] Add failing structural tests for exactly 3 worlds/6 courses/8 models/72 regular/6 finale events, each class×course×type cell exactly once, six unique rivals, compatible cosmetics, bilingual strings, valid spawns/checkpoints/recovery/pickups and two legal shortcuts per course. Tiny fixtures remain valid generic catalogs but fail production validation. Missing final clips fail final-content validation.
- [ ] Add loader tests alternating courses through solo, LAN rematch, tutorial and aborted loads: one active collision course; correct course/visual identity before countdown; old pickups/effects/audio cleared; camera rebound; late load completion cannot revive a canceled race. No simulation may start against stale collision after a failed load.
- [ ] Run EditMode/PlayMode and tooling fixtures red; implement validation/build wrappers and extend the inherited loader. Pin tests that incomplete content builds only through the explicit preview modes and that the final four flavors reject it. Repeated setup preserves authored IDs, GUIDs and approved measurements. Load heavy course assets on demand and release unused references/lightmaps/audio; merely hiding five instantiated courses does not satisfy the memory target.
- [ ] Run suites green and setup twice. Before Tasks 5–8 finish, production validation should explicitly reject the incomplete inventory; use fixtures solely for pipeline tests. Commit `feat: validate and package the full content catalog`.

## Task 5: Complete the eight-model vehicle collection

**Files:** Create `Editor/FullContentVehicleAuthoring.cs`; extend `Editor/RacerVisualAuthoring.cs` and Task 4 setup; new assets under `Generated/FullContent/Vehicles/` and `Art/FullContent/Vehicles/`; extend `VehicleClassPlayTests.cs`, `UpgradePresentationPlayTests.cs`, `RacerVisualRigPlayTests.cs`, and production validation tests; update `docs/ASSET_INVENTORY.md`.

**Interfaces:** `FullContentVehicleAuthoring.Create() -> void` creates/validates only its owned assets. Produces `car-03/04` and `motorcycle-03/04` definitions and visual rigs; preserves M5's four models. Existing tuning resolution, visual asset and cosmetic APIs remain authoritative.

- [ ] Add failing tests for eight class-correct usable definitions, all model-specific visual bindings, three visible upgrade categories, and six compatible rival liveries. Assert every model is legal in the relevant LAN circuit, regardless of local ownership, with identical effective class tuning.
- [ ] Author visibly distinct silhouettes/bodywork for the four new models; reuse the existing animal rig. Keep solo model handling differences readable and every event completable without upgrades. Final vehicle unlocks are completion rewards, not new progression gates.
- [ ] Run EditMode/PlayMode green and capture every vehicle in both cameras, including rider lean/landing and upgrade/cosmetic combinations. Review readability on the tablet; record source/license/cost. Commit `feat: complete the four-car and four-motorcycle roster`.

## Task 6: Build and verify Redrock Canyon

**Files:** Create `Editor/RedrockCampaignAuthoring.cs`, `Generated/FullContent/RedrockCanyon/` and `Art/FullContent/RedrockCanyon/`; extend production tests, setup, `docs/CONTENT_MATRIX.md`, `docs/ART_GUIDE.md`, and asset inventory.

**Interfaces:** `RedrockCampaignAuthoring.Create() -> void` produces `redrock-canyon`, `canyon-dash`, `mesa-skyway`, 24 regular events and two finales with the proposed stable IDs. World completion rewards `car-03`/`motorcycle-03` through Task 2.

- [ ] Add failing inventory and route tests for the two courses and their 12 class/type cells each; run EditMode/PlayMode to confirm missing assets fail explicitly.
- [ ] Author Canyon Dash around broad canyon bends/bridges and Mesa Skyway around elevated routes; give each two distinct legal shortcuts and a memorable jump. Author AI routes and recovery for both classes; keep distant scenery and transparency within the qualified tablet treatment.
- [ ] Configure 24+2 events, six non-victory extra stars sufficient for the gate, and separate class par times measured with unupgraded human driving. Record event laps, objective opportunities, actual evidence IDs and measured par ticks in the content matrix; never invent times to pass validation.
- [ ] Verify all 26 completions and introductory/mastery objectives with actual input-driven traversal, both cameras, legal shortcuts and recovery. Run a tablet-host four-racer performance spot check; fix regressions before beginning Snowy production. Commit `feat: add the Redrock Canyon campaign world`.

## Task 7: Build and verify Snowy Peaks

**Files:** Create `Editor/SnowyCampaignAuthoring.cs`, `Generated/FullContent/SnowyPeaks/` and `Art/FullContent/SnowyPeaks/`; extend the same production tests/setup/content matrix/art guide/inventory.

**Interfaces:** `SnowyCampaignAuthoring.Create() -> void` produces `snowy-peaks`, `snowpine-circuit`, `aurora-pass`, 24 regular events and two finales. Completion rewards `car-04`/`motorcycle-04` and completes the adventure.

- [ ] Add failing completeness and final-world tests; run EditMode/PlayMode red for missing content.
- [ ] Author Snowpine's readable forest/snow-ramp route and Aurora's mountain tunnels/high route, each with two shortcuts and a landmark jump. Use restrained snow effects and baked lighting; keep track edges/gadget patches readable against snow in both cameras.
- [ ] Configure and measure the 24+2 events using the Redrock procedure, including non-victory advancement and every objective's opportunities. Complete both finales without wins, verify both vehicles unlock once, and confirm replay/Quick Race/LAN remain usable after adventure completion.
- [ ] Run production validation for the now-complete course/event/vehicle inventory, PlayMode course switching, all 26 event traversals, and the tablet spot check. Final audio validation remains explicitly open until Task 8. Commit `feat: add Snowy Peaks and adventure completion`.

## Task 8: Finish bilingual names, prompts, and world audio

**Files:** Existing `Tools/Generate-TutorialCues.py`, `Tools/tutorial-cues.json`, `Runtime/Presentation/TutorialPresenter.cs`, `RaceAudioPresenter.cs`, `Runtime/Content/RacePresentationCatalogAsset.cs`, `Core/Localization/ShellText.cs`, `Core/Foundation/FoundationService.cs`, `FoundationService.Progression.cs`, `FoundationState.cs`, `Runtime/FoundationApp.cs`, `FoundationShell.Progression.cs`, `Runtime/Automation/ValidationJson.cs`; create `Tools/gameplay-cues.json`, `Runtime/Content/VoicePromptCatalogAsset.cs`, `Runtime/Presentation/VoicePromptPresenter.cs`, `Tests/PlayMode/VoicePromptPlayTests.cs`, `Tools/TestVoiceCueManifest.py`; audio under `Audio/FullContent/`; extend content validation, inventory, `docs/COMMANDS.md` and `docs/FEATURE_COVERAGE.md`.

**Interfaces:** Reuse the generator with an explicit manifest/output-directory option for both tutorial and gameplay cues. `VoicePromptCatalogAsset.Resolve(string cueId, string language) -> AudioClip` returns null for missing clips; `VoicePromptPresenter.Play(string cueId, string language) -> bool` reports actual playback, respects voice volume, and uses bounded/interruptible playback. Add `voice.replay` for the last gameplay prompt through the same presenter, preserving tutorial replay.

- [ ] Pin required English/Hebrew pairs for all existing tutorial cues; three world introductions; six rival introductions; completion/introductory/mastery objective explanations; world unlock; and adventure completion. Add failing manifest/content tests for missing locale/file, zero-duration clip, unknown cue and mismatched packaged text. Keep instructions short; no 78-event narration project.
- [ ] Implement final localized names/copy and playback bindings, replay command/discovery/state observations and tests for locale/volume changes, race teardown and rapid repeated replay. Produce clips through the existing licensed authoring pipeline; synthesis failure must not overwrite good clips. Keep service credentials out of traces/builds.
- [ ] Run tooling/EditMode/PlayMode green; missing credentials leave clip production `notVerified`, never a silent pass. Package world music/ambient cues with the existing bounded audio presenter, ensuring unloading between worlds.
- [ ] Obtain Hebrew-speaking review of meaning, RTL layout, pronunciation, audible replay and actual tablet playback; record reviewer/date/cue IDs and corrections. Commit only accepted assets and scoped source changes: `feat: finish offline Hebrew and English content audio`.

## Task 9: Prove the complete adventure and measure pacing

**Files:** Create `Tools/FullContentScenario.py`, `Tools/TestFullContentScenario.py`, `docs/tasks/full-content-report.md`; extend `Tools/GameplayLoopScenario.py` shared helpers only where reusable, `Tools/AgentScenario.py`, `docs/FULL_CONTENT_RUNBOOK.md`, `docs/CONTENT_MATRIX.md`, and feature coverage registry.

**Interfaces:** Produces the scenario CLI above and per-event evidence: world/course/class/type, build/content identities, actual command/state trace, completion/objective facts, rewards, durations, capture validity, and explicit human-only gaps. Expected production inventory comes from the validated manifest and the fixed 72+6 contract, not just the observed rows.

- [ ] Add failing harness tests that remove a world/event, duplicate one cell, substitute fixtures, drop stream records, misattribute AI actions, omit an objective proof, or report only 77 completions. Every mutant must prevent a production pass. Preserve `partial`/`notVerified` instead of treating unavailable content as success.
- [ ] Replace graybox-specific waypoint/pickup assumptions for these scenarios with authored per-course route/checkpoint observations. If extra read-only observations are needed, extend schema-7 discovery/state/tests; never teleport, grant stars, or bypass race commands for feature evidence.
- [ ] Run a fresh synthetic profile through all 78 events in order, obtaining at least six non-victory extra stars per region, both finales without required wins, eight final vehicles, six separate rival-victory rewards, and stable progress after process restarts. Additional runs prove every introductory/mastery objective is achievable; finishing an event alone does not prove its objectives.
- [ ] Run all 12 course/class combinations in Quick Race and Time Trial; verify normalized records and no campaign credit in the latter. Exercise LAN with uneven profiles, all eight models, course-changing rematches, duplicate/retained results, failed-save retry, disconnect/rejoin and host loss. Respect genuine Wi-Fi/device execution for the physical rows.
- [ ] Run `python -m unittest discover -s Tools -p 'Test*.py'` green, then all four scenario scopes on the full-content development build. Record that local processes/AI are substitutes where used. Commit `test: verify the full three-world adventure through real players`.
- [ ] Gather child/family playtests across all three worlds: active driving, learning, retries, navigation, completion-star understanding, difficulty, and desire to replay. Report session counts and observed total/range against 6–10 hours; tune measured problems and rerun affected evidence. Small samples remain disclosed; do not claim statistically established pacing.

## Task 10: Qualify the content-complete candidate and publish the handoff

**Files:** Full-content report/runbook/matrix, asset inventory, coverage registry, `IMPLEMENTATION_STATUS.md`, `README.md`; measured content/performance fixes only.

**Interfaces:** Produces one source/content-identified candidate across Windows/Android development/ordinary flavors, a per-gate evidence ledger, and an M7 handoff. No family-release acceptance is implied by a content-complete development build.

- [ ] Run CoreOffline, EditMode, PlayMode, all Python tooling tests, `Tools\Test-VerifyTooling.ps1`, and `Tools\Test-AndroidOffline.ps1`. Record exact counts/skips/failures and revision; final voice-content gaps cannot be waived as successful audio acceptance.
- [ ] Run FullContentSetup and the four new build modes. Record source revision plus any asset diff identity, editor/packages, content fingerprint and artifact SHA-256. Verify ordinary builds expose no validation endpoint; all four contain the same approved gameplay/audio content and launch offline.
- [ ] Execute physical tablet–tablet and tablet–PC with each host type, four physical devices including an A7 Lite host, internet-disconnected Wi-Fi, manual fallback/version mismatch, reconnect/pause/results/rematches, and the existing 100 ms RTT/jitter/2% loss matrix. Reuse prior runbooks; do not replace them with loopback.
- [ ] Run 30-minute sustained representative/worst-course sessions including both classes/cameras and repeated course transitions, then matched ordinary/development workloads. Require A7 Lite 30 fps target, steady p95 <=40 ms, no recurring >100 ms stalls; investigate any breach of the initial 800 MB process-memory budget or growth across rematches. Require reference PC 60 fps at 1080p. Record instrumentation overhead, thermal behavior, storage and fresh-process memory samples separately.
- [ ] Reconcile 78/78 event completion and all objective proofs, all clips/locales, three-world non-victory progression, durable purchases/rewards, family pacing and owner visuals. Publish each acceptance row as passed/failed/notVerified with evidence and owner; mark M6 accepted only when its mandatory and inherited gates pass.
- [ ] Update status/README and commit `docs: publish full-content qualification and M7 handoff`. M7 receives signed-package/update/backup/license-installation work plus any clearly disclosed remaining gates; it does not inherit a fabricated M6 pass.

## Checkpoints and handoff

| Checkpoint | Reviewable result |
|---|---|
| A: Production-ready first world | M4 gate evidence and M5 playable 26-event world, with inherited acceptance honestly tracked. |
| B: Multiworld systems | Three fixture worlds with tested access, rewards, UI and reconstructable schema-7 state. |
| C: Content expansion | Qualified Redrock followed by Snowy; six courses, eight models, 78 authored campaign events. |
| D: Content-complete candidate | Final bilingual audio, all-event/objective evidence, measured pacing, device qualification and report. |

Recommended execution method: **native task-by-task implementation**, because catalog, access, rewards, UI, and authored content share tightly coupled interfaces and assets; keeping that context together reduces coordination. Review at each checkpoint. No completion date is promised: hardware access, first-world acceptance, speech assets, and family playtests determine the critical path.

Planning self-review: mapped the approved M6 counts, world progression, non-victory gates, mode rules, bilingual audio, agent coverage and pacing to tasks; kept M4/M5 prerequisites and M7 scope distinct; checked proposed interface names and test locations against the inspected M5 source. No implementation or milestone acceptance is claimed by this document.
