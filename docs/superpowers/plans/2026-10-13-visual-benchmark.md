# Milestone 4: Visual Benchmark Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans, or superpowers:subagent-driven-development if the owner chooses delegation, to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. This is a proposed implementation plan; no implementation was performed while writing it.

**Goal:** Deliver one attractive, readable, fully offline representative course with animal racers, both vehicle classes, lighting, effects and audio, then demonstrate sustained four-racer play on the required tablet with either camera.

**Architecture:** Build a separate visual-benchmark scene around the existing course, command service, solo/LAN simulation and presentation seams. Authored visuals consume simulation state; they cannot change collision, checkpoints, authority or rewards. Add platform rendering profiles and bounded performance evidence, then qualify the same content in development and ordinary builds.

**Tech Stack:** Pinned Unity 6000.3.26f1; URP 17.3.0; C# 9 / .NET Standard 2.1-compatible Core; existing Input System, UI Toolkit, NGO 2.7.0, Transport 2.6.0, Unity Test Framework, Python and PowerShell tooling. No editor/package upgrade is planned.

**Spec:** [GAME_PLAN.md](../../../GAME_PLAN.md), sections 2–5, especially graphics/audio, agent testability, performance and M4 acceptance. [IMPLEMENTATION_STATUS.md](../../../IMPLEMENTATION_STATUS.md) records that the approved game plan serves as the design specification. Course treatment and implementation defaults below are proposals within that scope.

## Global constraints

- **Platforms: Android tablets and Windows PC.**
- **Required tablet: Samsung Galaxy Tab A7 Lite SM-T220; assume the weaker 3 GB configuration.**
- **Multiplayer: Two to four people over home Wi-Fi; four racers total.**
- **Connectivity: All gameplay works offline after installation.**
- **Performance: Target stable 30 fps on the A7 Lite; 60 fps on the reference PC.**
- **Tablet: 30 fps target, 95th-percentile steady-state frame time at or below 40 ms, without recurring stalls above 100 ms.**
- **Start with an 800 MB process-memory budget on the 3 GB tablet; investigate growth across repeated races.**
- **PC: 60 fps at 1080p on the available reference PC.**
- **Start at 0.75 internal render scale; keep the interface sharp.** Tablet profile uses OpenGL ES 3, baked environment lighting, simple vehicle shadows and restrained transparency.
- **All game controls and all implemented features must be executable through documented commands**, with complete reconstructable logical state and honest capture-health reporting. Ordinary builds exclude validation control endpoints.
- Preserve Hebrew/English, offline fonts/voice assets, large touch targets, reduced camera shake, both cameras and cosmetic-only character identity.
- Preserve four-racer capacity and identical collision/gameplay visibility between platform profiles. Physics remains opt-in; promoting `PhysicalAssisted` is a separate qualification decision.
- **The budget is a planning cap, not authorization to purchase.** Recorded spending is US$0 of US$100; propose free/original assets first and record every license and cost. No fixed deadline.

## Review focus

1. Art accidentally changes route collision, legal shortcuts or safe recovery, particularly while jumping — Task 3.
2. Reconnect, local prediction or repeated snapshots replay sounds/effects, swap animal identity or leak objects across rematches — Tasks 4–5.
3. A good average hides thermal deterioration, paused time, absent GPU data or missing samples — Tasks 2 and 7.
4. Setup/build regeneration overwrites hand-authored art or packages a different scene/profile from the one measured — Tasks 1 and 3.
5. Water, scenery, effects, Hebrew layout or the overhead camera hide the road, racers or touch controls — Tasks 3, 5 and 7.

## Starting point and milestone choice

Inspected on 2026-10-13: branch `fix/driving-playtest-1`, HEAD `e67fde7`, with existing uncommitted physics tooling, reports, project settings and generated assets. Preserve those changes; record the actual source/content state before implementation. This planning pass did not run Unity, tests, builds or device qualification.

M4 is the next unstarted development milestone. M1/M2 acceptance and M3 physical/human acceptance remain open. The physical backend is implemented but its rollout remains gated. The [physics report](../../tasks/vehicle-physics-report.md) records two failing impaired-link PlayMode tests, an unresolved final 30-minute-soak entry, and several device/handling gaps; these must not be silently represented as passing. Its rejoin row is already closed, despite older overview wording. Reconcile evidence by run identity rather than copying summary counts.

Existing integration points:

- `DrivingWorld` already owns solo and four-racer visuals, with `ApplyRaceFrame` accepting presentation pose overrides. `VehiclePresentation.SetVehicle` and `ApplyPose` support arcade and physical poses, currently using generated primitive silhouettes.
- `ParticipantState` already carries `avatar` and `colorIndex`; profiles have the four fox/rabbit/panda/raccoon identities. Reuse these rather than introducing character progression.
- `DrivingCamera` already owns chase/overhead framing. `TutorialPresenter` and the voice-generation pipeline exist, but packaged voice clips remain an acceptance dependency.
- `PresentationState` exposes camera/font/input facts, and `ValidationBridge` already implements screenshots. Current observation schema is 4; LAN protocol is 2.
- `FoundationProjectSetup` runs during builds. New authoring must survive repeated setup. The Android offline path currently produces a development APK; ordinary Android needs a repeatable path.
- `NetworkSoak.py` measures observed simulation cadence, not rendered frames. Headless/loopback evidence cannot prove visuals, tablet performance or physical LAN acceptance.

## Proposed scope and approach

**Recommended treatment:** a Sunny Island / Harbor Hop benchmark over the proven graybox layout: turquoise water, warm village buildings, a boardwalk/harbor, bold route markings and a landmark coastal jump. Dress the complete loop, its two shortcut branches and recovery areas; one attractive screenshot corner is insufficient. This is a representative art treatment, not a claim that the final Harbor Hop course or its campaign is finished.

Use the existing `graybox-01` course, event IDs, gate order, route and tuning for this benchmark, with a distinct benchmark content version (`visual-1`) and presentation asset manifest. That keeps driving tools reusable while preventing the benchmark from joining an incompatible content build. Preserve the current graybox and physics scenes as developer comparison builds.

| Approach | Trade-off |
|---|---|
| Dress the proven course in a separate benchmark scene — recommended | Concentrates effort on M4 quality and measurement; retains existing gameplay evidence and regression routes. |
| Build the final Harbor Hop layout immediately | More representative final content, but expands into route design, AI and checkpoint qualification before the art budget is proven. |
| Add only graphics settings and minor graybox polish | Cheapest, but does not meet the milestone's intended-quality course, animals and audiovisual acceptance. |

Included: one course treatment; one rally-car model and one motorcycle model; four animals on a compatible shared rig; four distinguishable racers in each separate class; drive/lean/landing reactions; platform lighting/detail profiles; drift/boost/landing/impact effects and audio; music transitions; existing bilingual tutorial voice completion; readable HUD/cameras; render/performance evidence.

Deferred: additional courses/worlds/vehicle models, new course selection systems, gadgets, campaign/progression, bounties, upgrades, rival finales, Quick Race/Time Trial features, final release signing and a physics-default switch. Gadget audio/effects are not claimed before gadget gameplay exists.

## Dependencies and execution order

`1 repeatable benchmark builds → 2 rendering profiles and measurement → 3 complete course treatment → 4 vehicles/animals → 5 audiovisual and camera integration → 6 agent scenario → 7 device optimization and acceptance`

Obtain the A7 Lite early, not after art production. First test a representative art section with four racers using Task 2's measurement path, then finish the course. Missing device access permits desktop development but leaves the tablet review gate open. Do not expand into the other five courses before M4 acceptance.

Two tablets and a PC are recorded as available to the owner; a fourth supported physical device is needed for four-human acceptance. Four AI/local-process racers provide useful load tests but do not close that gate. Schedule inherited M1–M3 device, offline-LAN, touch, Hebrew and family checks alongside M4 sessions; keep their outcomes separate.

## File boundaries and shared contracts

Paths beginning `Core/`, `Runtime/`, `Editor/`, `Tests/`, `Art/`, `Audio/` or `Generated/` below are under `Assets/FamilyRacing/`. Create and commit Unity `.meta` files with new assets. New names below are proposed interfaces, not existing APIs.

| Area | Files / responsibility |
|---|---|
| Benchmark authoring | `Editor/VisualBenchmarkSetup.cs`; `Generated/VisualBenchmark/VisualBenchmark.unity`; bind existing service/world to dedicated visual content without overwriting authored files |
| Art sources | `Art/VisualBenchmark/Environment/`, `Vehicles/`, `Characters/`, `Effects/`; models, shared rig, materials, atlases, prefabs and source provenance |
| Presentation data | `Runtime/Content/VisualBenchmarkDefinitionAsset.cs`, `VehicleVisualDefinitionAsset.cs`, `CharacterDefinitionAsset.cs`; references and mount/bone bindings, separate from motor tuning |
| Graphics / evidence | `Runtime/Presentation/RenderProfileController.cs`, `RenderPerformanceRecorder.cs`; `Core/Foundation/RenderPerformanceState.cs`; target profile and bounded frame statistics |
| Racer presentation | Existing `Runtime/Driving/VehiclePresentation.cs`, `DrivingWorld.cs`; new `Runtime/Presentation/RacerVisualRig.cs`; consume authoritative/predicted/interpolated sources already selected by the app |
| Effects / sound | `Runtime/Presentation/RaceEffectsPresenter.cs`, `RaceAudioPresenter.cs`; pooled effects and audio, driven by real race events/state |
| Automation / reports | Existing stream/screenshot APIs; `Tools/VisualBenchmarkScenario.py`, `TestVisualBenchmarkScenario.py`; `docs/VISUAL_BENCHMARK_RUNBOOK.md`, `docs/tasks/visual-benchmark-report.md` |

Presentation observation adds bounded, deep-copied fields under `PresentationState`: `renderProfileId`, `presentationVersion`, `courseVisualId`, `racers`, `effects`, `audio`, `performance`. Racer facts include participant/vehicle/character/color identity and pose-source label; effect/audio facts include actual active identities, counts, missing-asset reasons and event cursors. They contain no Unity objects or private paths.

Version the additive observation contract as schema 5 in Task 2 and update live tooling/readers together. Keep LAN protocol 2 and profile-save schema unchanged unless implementation discovers an actual gameplay-wire or persistence change. Presentation details stay out of gameplay snapshots; existing participant cosmetics already supply identity.

## Task 1: Make benchmark content reproducible on both platforms

**Files:** Create `Editor/VisualBenchmarkSetup.cs`, `Runtime/Content/VisualBenchmarkDefinitionAsset.cs`, `Tests/EditModeAdapter/VisualBenchmarkSetupTests.cs`, benchmark scene/definition, and `docs/VISUAL_BENCHMARK_RUNBOOK.md`. Modify `Editor/FoundationBuild.cs`, `Tools/Verify.ps1`, `Tools/Build-AndroidOffline.ps1`, `Tools/Test-VerifyTooling.ps1`, `Tools/Test-AndroidOffline.ps1`.

**Interfaces:** `VisualBenchmarkSetup.Create() -> void`; initially, `VisualBenchmarkDefinitionAsset` carries `string presentationVersion = "visual-1"`, `GameObject courseVisualPrefab` and `TrackDefinitionAsset sourceTrack`. Tasks 4–5 add typed catalog references when those types exist. Add Verify modes `VisualBenchmarkSetup`, `WindowsVisualBenchmark`, `WindowsVisualBenchmarkOrdinary`, `AndroidVisualBenchmark`, `AndroidVisualBenchmarkOrdinary`. Map setup/Windows modes to matching `FoundationBuild` methods; Android modes call the offline wrapper, which selects a matching export entry point. The wrapper accepts `-Presentation Graybox|VisualBenchmark` and `-Configuration Development|Ordinary`, preserving current defaults.

- [ ] Record the starting commit, dirty-file hashes, existing gate outcomes and known failures in the runbook. Check that source edits from the physics qualification are retained; do not reset or auto-commit unrelated changes.
- [ ] Add `SetupTwicePreservesAuthoredAssets` (authored GUIDs/content unchanged), `BenchmarkSceneUsesExistingRules` (same course/gates/tuning, distinct content version), and build-tool fixtures that assert ordinary flags/output paths and offline failure behavior. Run EditMode/tooling tests and observe missing setup/mode failures.
- [ ] Implement a repeatable separate benchmark scene and build paths. Setup creates missing scaffolding and validates references; it does not regenerate authored meshes, materials or baked lighting. Output to `Builds/Windows-VisualBenchmark`, `Builds/Windows-VisualBenchmark-Ordinary`, `Builds/Android-VisualBenchmark` and `Builds/Android-VisualBenchmark-Ordinary`.
- [ ] Build all four flavors, recording source manifest, scene, content/profile IDs, managed/content hashes and APK hash. For ordinary builds, assert validation code/endpoints remain absent; launching with validation arguments must not create a connection descriptor or listener. Preserve application identity/save compatibility and use isolated test profiles for agent runs.
- [ ] Run `Tools\Verify.ps1 -Mode EditMode`, `Tools\Test-VerifyTooling.ps1` and `Tools\Test-AndroidOffline.ps1`; require the new focused tests to pass and document unrelated baseline failures. Commit only this slice after verification.

## Task 2: Establish tablet/PC rendering profiles and truthful measurements

**Files:** Create `Runtime/Presentation/RenderProfileController.cs`, `RenderPerformanceRecorder.cs`, `Core/Foundation/RenderPerformanceState.cs`, `Tests/EditMode/RenderPerformanceTests.cs`, `Tests/PlayMode/RenderProfilePlayTests.cs`, and two URP profile assets beneath `Generated/VisualBenchmark/`. Modify `Core/Foundation/FoundationState.cs`, `Core/Networking/NetworkSettings.cs`, `Runtime/FoundationApp.cs`, `Runtime/Automation/ValidationJson.cs`, `StateStream.cs`, `Editor/VisualBenchmarkSetup.cs`, `Tools/AgentScenario.py`, `Tools/NetworkScenario.py`, `Tools/TestAgentScenario.py`, `Tools/TestNetworkScenario.py`, and schema checks in dependent driving/physics tooling.

**Interfaces:** `RenderProfileController.Apply(bool tablet) -> void`; `RenderPerformanceRecorder.RecordFrame(double frameMs, string phase, string cameraMode, string vehicleClass) -> void`; `Snapshot() -> RenderPerformanceState`. Phase is `countdown|racing|paused|loading|results|menu`; the recorder separately tags warm-up by elapsed capture time. State includes frame/sample count, observed seconds, median/p95/max frame interval, >100 ms stall count, warm-up/paused/loading seconds, class/camera/profile/build identity, unavailable-metric reasons and report-write health.

- [ ] Add `KnownSamplesProduceExpectedPercentiles` (nearest-rank p95 of 1..100 is 95), `PausedFramesDoNotCountAsRacing`, `MissingSamplesCannotPass`, `SampleRetentionIsBounded`, `TabletAndPcProfilesKeepCollisionEqual`, and `SchemaFiveRoundTripIncludesPresentation`. Run Core/EditMode/PlayMode and relevant Python tests to observe the missing behavior.
- [ ] Apply Android tablet/Windows PC profiles automatically: tablet 30 fps cap, 0.75 render scale, baked environment lighting and simple vehicle shadows; PC 60 fps target at 1080p with richer shadows/detail. Keep UI at display resolution. No real-time reflections, motion blur or expensive full-screen effects. No profile change may change collision, checkpoints or route visibility.
- [ ] Record actual rendered-player frame intervals, with a bounded maximum of 120,000 samples per capture; overflow invalidates that capture. Mark the initial 60 seconds as warm-up and retain them in reports separately. Segment samples by camera, race/loading/pause state and five-minute window. Distinguish frame intervals from GPU timing and socket cadence; unavailable GPU/process-memory values stay unavailable, never zero.
- [ ] Write compact local performance reports in both development and ordinary benchmark builds without enabling an ordinary control socket. Keep file writes off the race thread, bounded, and failure-visible. Sample Android total process PSS and Windows process working set externally once per second, naming the metric/source/units in reports; never substitute Unity heap allocations. Compare each device against its own baseline rather than comparing the two metrics directly. Record capture overhead with matched runs; ordinary builds get performance summaries, not the validation state stream.
- [ ] Add schema-5 presentation fields and deep-copy handling; extend independent snapshot/delta reconstruction and all active schema guards. Publish new logical presentation decisions at existing boundaries, including paused/menu changes; do not put frame-sized buffers in each record or enlarge the gameplay wire DTO.
- [ ] Run focused tests plus a rendered Windows sample to prove nonempty data and correct metadata. A headless sample must report rendering as unavailable. Commit the profiles/measurement/observation slice; tablet qualification remains pending actual hardware.

## Task 3: Author the complete Sunny Island benchmark course

**Files:** Create `docs/ART_GUIDE.md`, `docs/ASSET_INVENTORY.md`, environment source/prefab/material/lighting assets under `Art/VisualBenchmark/Environment/`, `Tests/EditModeAdapter/VisualBenchmarkContentTests.cs`, and `Tests/PlayMode/VisualBenchmarkCoursePlayTests.cs`. Modify the benchmark definition, scene and setup only where references require it.

**Interfaces:** Course visual root has stable ID `sunny-island-benchmark-01` and a visual-only hierarchy. The existing track/collision prefab remains the gameplay authority; lighting data belongs to the benchmark scene. A presentation manifest records asset GUID/hash, license, source, cost and role.

- [ ] Write the art guide: warm/turquoise palette, rounded proportions, animal silhouettes, matte materials, route/shortcut language, lighting and restrained effects. Inventory original/free assets with actual license evidence and US$0 costs; record asset needs before any proposed purchase. Do not select paid assets on the assumption the budget authorizes them.
- [ ] Add `DressingPreservesGameplayGeometry` (collider dimensions/transforms/layers and authored track data match baseline), `BothShortcutsRemainTraversable`, `LandmarkJumpAndRecoveryRemainClear`, and `AllReferencedAssetsHaveInventoryEntries`. Run the focused EditMode/PlayMode tests to establish missing content or violated invariants.
- [ ] Dress one representative stretch first: road, harbor/building cluster, water, shortcut entrance, jump approach and landing. Use opaque/simple water, shared materials/atlases, LODs and baked lighting; keep camera-only occluders on a layer excluded from driving/physics probes. Measure with four racers on A7 Lite when available before repeating the treatment.
- [ ] Extend the approved treatment around the full loop, both shortcuts and recovery poses. Decorative barriers must visually match the real collision boundary; do not suggest drivable water, blocked shortcuts or false ramps. Preserve visibility in overhead view and at low internal resolution.
- [ ] Re-run setup twice and verify authored art/lightmaps survive. Run route tests and the existing driving scenario on the benchmark development player for both classes; capture chase/overhead views of start, turns, shortcuts, jump and recovery. Review images for clipping, missing shaders and unreadable routes. Commit art, sources, licenses and evidence references together.

## Task 4: Replace silhouettes with recognizable vehicles and animal racers

**Files:** Create `Runtime/Content/VehicleVisualDefinitionAsset.cs`, `CharacterDefinitionAsset.cs`, `Runtime/Presentation/RacerVisualRig.cs`, `Tests/PlayMode/RacerVisualRigPlayTests.cs`, `Art/VisualBenchmark/Vehicles/` and `Characters/`. Modify `Runtime/Content/VehicleDefinitionAsset.cs`, `Runtime/Driving/VehiclePresentation.cs`, `DrivingWorld.cs`, `Runtime/FoundationApp.cs` and benchmark catalog references.

**Interfaces:** `RacerVisualRig.Bind(VehicleVisualDefinitionAsset vehicle, CharacterDefinitionAsset character, int colorIndex) -> void`; `ApplyPose(MotorState motor, float steering, float alpha) -> void`; `ResetVisuals() -> void`. Vehicle assets specify chassis/wheel/seat/handlebar mounts; all four characters share a compatible skeleton and named mounting conventions. Keep the existing `VehiclePresentation` API as the integration point and primitive fallback.

- [ ] Add `FourRacersKeepDistinctCosmetics`, `RejoinKeepsCharacterAndLocalCameraTarget`, `PhysicalLeanIsNotAppliedTwice`, `RematchDoesNotAccumulateRigsOrMaterials`, and `MissingVisualReportsFallback`. Assert correct identities, no gameplay colliders on rigs, bounded object/material counts, and correct physical wheel/recovery-epoch behavior. Observe RED in PlayMode.
- [ ] Author one car and motorcycle plus fox/rabbit/panda/raccoon, readable at tablet chase and overhead distances. Reuse one character rig with seated car and motorcycle poses; no separate character-selection/unlock system. Resolve solo cosmetics from the selected profile, human LAN cosmetics from existing participants, and AI cosmetics from a stable slot mapping.
- [ ] Implement wheel motion, steering, rider lean and small landing/celebration reactions using visual mounts. Preserve interpolation, remote/local pose sources, physical full attitude and recovery snapping. Animation must not steer, grant boosts or alter physics/checkpoints.
- [ ] Use shared materials/property overrides and bounded rig reuse. Populate streamed racer presentation facts with actual asset/fallback identities; repeated snapshots/rejoins must not instantiate extra rigs. Review all four animals with both classes and cameras, including low-resolution silhouette/color distinction.
- [ ] Run new rig tests and existing `DrivingPresentationPlayTests`, `VehicleClassPlayTests`, `NetworkPredictionPlayTests` and relevant physical-presentation tests; compare known impaired-link failures separately. Commit the visual slice with inventory updates.

## Task 5: Integrate effects, audio, camera readability and bilingual presentation

**Files:** Create `Runtime/Presentation/RaceEffectsPresenter.cs`, `RaceAudioPresenter.cs`, `Runtime/Content/RacePresentationCatalogAsset.cs`, `Tests/PlayMode/RacePresentationPlayTests.cs`, effect/audio assets under `Art/VisualBenchmark/Effects/` and `Audio/VisualBenchmark/`. Modify `Runtime/Presentation/DrivingCamera.cs`, `TutorialPresenter.cs`, `Runtime/FoundationApp.cs`, `FoundationShell.Driving.cs`, `FoundationShell.Network.cs`, `Core/Localization/ShellText.cs`, and tutorial audio references.

**Interfaces:** Both presenters expose `ApplyFrame(FoundationState state, float deltaSeconds) -> void` and `Reset(string raceId) -> void`. Consume existing event identities/cursors for one-shots and live motor state for continuous loops. The catalog maps stable cue/effect IDs to licensed assets, pooling limits and volume categories.

- [ ] Add `RepeatedSnapshotsDoNotRepeatLandingCue`, `CorrectionReplayDoesNotReplayEffects`, `ReconnectDoesNotPlayHistoricalOneShots`, `PauseAndExitStopLoops`, `ThreeVolumeControlsAffectTheirOwnBus`, `ReducedShakeAndCameraOcclusionRemainUsable`, and `MissingClipDoesNotClaimPlaying`. Observe RED before implementing presenters.
- [ ] Add drift trails/dust, boost feedback, landing/impact cues, car/motorcycle engine loops and race/results music transitions. Deduplicate by race/racer/event identity; initial observer/rejoin snapshots establish a cursor without replaying historical one-shots. Bound pools and audio voices in authored data. Pool exhaustion is observable and may reduce decoration, never gameplay feedback required to understand the race.
- [ ] Complete the existing English/Hebrew tutorial voice pipeline when licensed clips/credentials are available; wire real recordings and inspect every cue. Keep the existing missing-audio gate open if assets are unavailable. Provide no runtime speech service or credentials, and never manufacture placeholder speech as acceptance evidence.
- [ ] Fit camera framing/occlusion handling to the dressed course, both vehicles and physical rollovers. Keep the local racer and next route visible through jumps/recovery; reduced-shake suppresses added shake. Check overhead scenery density, touch-control contrast, Hebrew RTL layouts, LTR timers/IPs and existing volume/replay controls. Preserve semantic IDs and command/UI parity.
- [ ] Publish actual active audio/effect state and safe missing-asset reasons in schema 5; clear it on pause/exit/scene teardown as appropriate. Run PlayMode tests, inspect screenshots, and listen to packaged audio in a rendered player. Update asset inventory/coverage and commit. Human Hebrew pronunciation remains a distinct review.

## Task 6: Prove the benchmark through the existing command path

**Files:** Create `Tools/VisualBenchmarkScenario.py`, `Tools/TestVisualBenchmarkScenario.py`; modify `docs/COMMANDS.md`, `docs/FEATURE_COVERAGE.md`, `docs/VISUAL_BENCHMARK_RUNBOOK.md`, and existing stream tests where schema-5 coverage needs assertions.

**Interfaces:** `python Tools\VisualBenchmarkScenario.py --connections <host-connection.json> <client-connection.json> [additional peers] --evidence <new-dir> --minutes 30`. First connection is host; require 2–4 connected endpoints, with four total racers through normal AI fill. Accept ADB-forwarded descriptors. Use existing commands, screenshot requests and scenario validators; no new gameplay bypass or teleport path.

- [ ] Add tooling tests `RejectsMissingRenderedSamples`, `RejectsIncompleteStream`, `DoesNotCountPausedTime`, `RequiresBothClassesAndCameras`, `LabelsLoopbackAndAiFillHonestly`, and `RejectsMismatchedBuildManifest`. Corrupt one required evidence dimension per fixture and require a failing verdict.
- [ ] Before hosting, use a solo tutorial phase for voice replay/volume controls and locale checks, then exit it through normal commands. Drive profile/lobby/ready/race/results/rematch through real commands. Exercise both classes, both cameras, drift/boost/jump/recovery, shared pause and reconnect. Verify avatar/color continuity, immutable results, bounded pools and cleanup after race exit. Reuse existing driving/network helpers where possible; do not duplicate their protocol client.
- [ ] Capture a labeled image set at course landmarks with four visible racers where feasible; correlate request/completion to race/tick/camera identities. Save command outcomes, full reconstructable stream, presentation/performance reports, assertions and hashes. Screenshot timeouts or missing rendered frames are failures, not successful captures.
- [ ] Keep the 30-minute performance segment separate from screenshot/locale/pause exercises: at least 1,800 observed active-racing seconds after the 60-second warm-up, with each class/camera combination represented for at least five minutes. Report all excluded time; slow transitions cannot be hidden in a good aggregate. Disk/queue limits must fit the measured trace rate; overflow invalidates evidence.
- [ ] Run `python -m unittest discover -s Tools -p 'Test*.py'` and the rendered benchmark scenario on freshly built Windows development players. Treat this as desktop automation evidence only. Document the separate ordinary-build procedure, which cannot use validation connections. Commit tooling and coverage mappings.

## Task 7: Optimize on devices and close the acceptance report

**Files:** Create `docs/tasks/visual-benchmark-report.md`; update `docs/VISUAL_BENCHMARK_RUNBOOK.md`, `docs/ASSET_INVENTORY.md`, `README.md`, `IMPLEMENTATION_STATUS.md`, `docs/FEATURE_COVERAGE.md`, and only measured hotspot/profile/content files needed to meet the targets.

**Interfaces:** One evidence matrix row per device/build/host/class/camera combination, linked to exact build/content manifests, frame reports, process-memory/thermal observations and human review. Verdicts are `passed`, `failed` or `notVerified`; no missing test can become a pass.

- [ ] Run a short A7 Lite pass before the full soak to identify GPU/CPU/memory/visibility hotspots. Optimize measured causes: scenery density, batching/materials, overdraw, shadow distance, LODs, texture memory, animation/effect/audio budgets. Preserve the required tablet, four racers and readable gameplay. Re-run affected tests after each behavior change.
- [ ] Produce final same-source Windows/Android development and ordinary benchmark builds. Run CoreOffline, EditMode, PlayMode, Python and build-tool suites once at the final revision; rerun further only after changes/failures. Record the exact known impaired-link failures if still present, with evidence and remediation ownership; do not rename the suite green or mark M3 accepted.
- [ ] Run a rendered A7 Lite host with four physical participants over home Wi-Fi without internet, plus tablet–tablet without PC and tablet–PC with each host type. Use both classes/cameras and repeated rematches. Capture at least 30 active racing minutes after warm-up for each required development/ordinary performance qualification; compare overhead explicitly. Do not infer physical-device coverage from AI fill or local Windows processes.
- [ ] Report frame distributions and five-minute trends, stall timestamps, process-memory peak/growth, startup/loading separately, thermal behavior and instrumentation overhead. As a proposed conservative interpretation of “without recurring stalls above 100 ms,” require no more than one such steady-state stall per five-minute window and review every occurrence; never relax this silently. Missing process-memory or frame evidence leaves its gate `notVerified`.
- [ ] Obtain owner review on the actual tablet: accepted art quality, distinct animals/vehicles, visible route/shortcuts/jump, comfortable chase/overhead cameras, helpful audio and understandable feedback. Complete Hebrew text/pronunciation and child onboarding checks with the owner/family, recording findings under the appropriate M1/M2/M4 gates.
- [ ] Publish the report and update the milestone ledger with measured outcomes and remaining gates. If hardware or review is unavailable, label M4 “development complete; acceptance open” only when its development work is actually complete. Otherwise keep “in progress.” Commit the final report/necessary optimization changes; leave M5/content expansion for a separate plan.

## Acceptance checklist

| Gate | Required evidence |
|---|---|
| Representative quality | Complete dressed loop, both shortcuts, landmark jump/recovery, one car, one motorcycle and four recognizable animals; owner acceptance on A7 Lite |
| Offline audiovisual content | All referenced materials/shaders/models/fonts/effects/music/voice clips bundled; license/cost inventory complete; English/Hebrew listening review |
| Both cameras | Usable chase/overhead views during corners, crowding, jumps, recovery and rematch; clear controls/RTL UI on tablet |
| Tablet performance | A7 Lite SM-T220 3 GB, four racers with tablet hosting, 30 fps target, p95 ≤40 ms, stall review, 800 MB process-memory budget, 30-minute thermal/rematch evidence |
| PC performance | Documented reference PC, 1080p, sustained 60 fps target with four racers and both cameras |
| Build separation | Same source/content provenance for development/ordinary builds; ordinary control endpoints absent; capture overhead measured separately |
| Agent coverage | Shared commands, schema-5 reconstruction, bounded presentation facts, real screenshots and fault-sensitive scenario verdicts |
| Regression safety | Original course/physics comparison paths preserved; visual content does not change collision, authority, progress or rewards; inherited open gates retained |

The milestone ends with a playable benchmark, recorded evidence and owner review. The final deliverable is permission to scale a proven visual style and performance budget to later content—not completion of the campaign or an implicit physics rollout.
