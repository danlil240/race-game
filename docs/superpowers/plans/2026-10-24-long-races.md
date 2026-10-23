# Long Races Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Deliver a Windows player with fast 5–8 minute races, a large circuit, two journeys and richer scenery.
**Architecture:** Add a backwards-compatible open-route contract to Core. A separate editor authoring pipeline clones the current asset-trial scene and produces versioned long courses, vehicles and campaign assets, leaving old trial builds reproducible. Reuse the current authoritative racing and UI.
**Tech Stack:** Unity 6000.3.26f1, C# 9, URP, NUnit/.NET 10, Python validation bridge.
**Spec:** docs/superpowers/specs/2026-10-24-long-races-design.md (approved by user: “implement”).

## Global Constraints

- Ordinary races approximately 5–8 minutes; higher speeds with approachable handling.
- One 3–4 km coastal circuit (two laps), two 7–10 km open journeys.
- Preserve profiles, coins, upgrades and event identities; use versioned course IDs for new records.
- Existing PC/tablet profiles and English/Hebrew support; no purchases/new dependencies.
- Keep existing working-tree changes. Work in place on the existing non-main branch because the uncommitted asset-trial content is the foundation of this upgrade.

## Review Focus

- Open routes must finish only after every directional gate; no wrapping or reverse finish.
- AI must stay on open endpoints and remain capable at higher speeds.
- Recovery at the last journey gate and multi-racer finish deadlines must not strand a human.
- All race entry modes and HUDs must agree about course topology and required distance.
- Long-world rendering must avoid loading visible scenery from unrelated places and preserve a continuous drivable road.

### Task 1: Core route topology

Files: Core/Racing/{CourseDefinition,CheckpointProgress,AiDriver,DrivingSession,DrivingSessionState,ContentFingerprint}.cs; Runtime/Content/TrackDefinitionAsset.cs; Tests/EditMode/LongRouteTests.cs.
Interfaces: `CourseDefinition.isOpenRoute` and `TrackDefinitionAsset.isOpenRoute` (false by default); `DrivingSessionState.isOpenRoute` and `routeProgress` (0–1).
- [ ] Write tests for gate-order open finish, reverse rejection, circuit regression, topology copy/hash, AI endpoint and open event lap validation; run and observe failure.
- [ ] Implement topology, bounded AI indexing and lookahead, final-gate completion, progress observations and compatibility fingerprint.
- [ ] Run `dotnet test Tools/CoreTests/FamilyRacing.CoreTests.csproj --no-restore --verbosity quiet`; expect all tests passing.

### Task 2: Long-world authoring and content

Files: Editor/LongRaceAuthoring.cs, Editor/LongRaceRoads.cs, Editor/LongRaceScenery.cs; Editor/AssetTrialCampaign.cs (parameterized binding); Generated/LongRaces/*.
Interfaces: `LongRaceRoads.Build()` returns course prefab, `LongRaceRoads.TrackPath(int)` returns versioned tracks, `LongRaceAuthoring.CreateAndBuildBoth()` builds development/ordinary players.
- [ ] Author a continuous 3–4 km irregular coastal loop and 7–10 km forest/canyon journeys with endpoint-safe road, grid, checkpoints, pickups, recovery and collider checks.
- [ ] Add route-adapted, batched landscapes/foliage/buildings/rocks and richer lighting/materials. Reuse licensed imported assets, use chunk bounds and distance culling.
- [ ] Bind separate fast car/motorcycle assets, versioned courses, preserved campaign event IDs, retuned pars, two-lap coastal regular races and one-pass journeys. Default Quick Race must inherit correct lap count.
- [ ] Run Unity batch authoring validation and build; expect zero compile/build errors and measured route distances within target.

### Task 3: Player route presentation and activation

Files: Runtime/FoundationShell.Player.cs, Runtime/FoundationShell.Driving.cs, Runtime/Driving/DrivingWorld.cs, Runtime/Presentation/{AssetTrialHud,DrivingCamera}.cs, Core/Localization/ShellText.cs.
Interfaces: HUD consumes Task 1 route progress; course selection consumes versioned IDs/previews and route metadata.
- [ ] Show journey progress/distance and expected race duration, circuit lap counts, legible km/h and camera framing for faster travel.
- [ ] Activate only the selected course, retain collision fidelity and keep render-profile behavior.
- [ ] Verify both English/Hebrew and both vehicles with actual gameplay screenshots, pause/retry/recovery/results.

### Task 4: Qualification and delivery

Files: Tools/LongRaceScenario.py, docs/LONG_RACES.md, docs/qa/long-races-2026-10-24/*.
- [ ] Build standalone development and ordinary Windows players in separate LongRaces folders.
- [ ] Drive full races on all three courses with real input. Save elapsed time, speed, gate progression, finish/reward and gameplay captures; tune route length/speed from measurements.
- [ ] Exercise two LAN endpoints on an open course, relevant existing Core/Unity checks and sustained PC frame samples. Record physical-tablet qualification as unverified.
- [ ] Obtain one fresh code review; address material findings and verify fixes. Document runnable build paths and observed limits.

## Execution ledger

Baseline: 846 Core tests passed. Existing branch: fix/pcqa-session-02; significant existing uncommitted asset-trial work preserved. No new worktree because it would omit the very content being upgraded. User authorized inline implementation; no additional planning approval gate.
