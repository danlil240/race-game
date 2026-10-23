# Playful Chaos Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task, or superpowers:subagent-driven-development if that execution method is selected. Steps use checkbox (`- [ ]`) syntax for tracking. This document proposes the work; no implementation has started.

**Goal:** Deliver a short, readable, chaotic family race that people voluntarily replay, then expand only after that experience works.

**Architecture:** Reuse the arcade motor, authoritative local/LAN race coordinator, gadgets, semantic commands, and presentation events. Introduce a narrowly scoped Quick Race-only content capability, one explicitly bound harbor scene, better contact/presentation behavior, and one authoritative course hazard. Defer campaign expansion and default physical simulation.

**Tech Stack:** Unity 6000.3.26f1, C#, URP, UI Toolkit, existing Core/EditMode/PlayMode tests and Python scenario tools.

**Spec:** [Playful chaos design](../specs/2026-10-18-playful-chaos-design.md).

## Global constraints

- Family audience, ages five and older; offline Windows PC and Android tablets.
- Samsung Galaxy Tab A7 Lite SM-T220; assume the weaker 3 GB configuration.
- Target stable 30 fps on the A7 Lite; 60 fps on the reference PC.
- Four racers total; two to four human players over home Wi-Fi; separate vehicle classes.
- Hebrew and English; all gameplay-affecting input goes through the existing command path.
- US$100 total for assets and tools; inventory prior spending before any purchase.
- Preserve auto-acceleration, held-boost behavior, bounded contacts, ordered checkpoints, recovery correctness, and authoritative results.
- No hidden catch-up speed or position-dependent item distribution.
- Prototype content does not qualify production release gates or erase existing device/human acceptance gaps.
- Work in an isolated implementation checkout: the inspected workspace already contains modified generated assets, materials, settings, and unrelated QA files. Do not sweep these into task commits.

## Review focus

1. Quick Race-only content accidentally enables campaign/purchases or persists incomplete-catalog rewards: test capability boundaries in Task 1.
2. HUD or decorative geometry obscures the road at a ramp/turn, especially in Hebrew or overhead view: capture and inspect these views in Tasks 2 and 3.
3. Overlapping racers, walls, respawns, or repeated network events create chained hits/reward farming: regression tests in Task 4 and event dedup checks in Task 5.
4. Host/client hazard phases diverge after pause, reconnect, or rematch: deterministic and lifecycle checks in Task 5.
5. AI can finish but human beginners cannot read the race or want to replay: human comparison and replay gate in Task 7.

## Priority roadmap

| Order | Deliverable | Completion evidence |
| --- | --- | --- |
| 1 | Race now → four-racer race → results → Race again | Current identified build completes the loop without a campaign catalog or progression bypass |
| 2 | Clear road view and satisfying car response | Same-course comparison, readable screenshots, first human handling session |
| 3 | Harbor Havoc course | Main route, two shortcuts, jump, and safe recoveries all traversed by humans and AI |
| 4 | Readable playful contact and stunt feedback | Side bumps, wall slides, landings, and recovery remain controllable; no repeated stun chain |
| 5 | One surprise and three rival personalities | Shared fountain timing, actual route choices, useful gadget encounters, stable rematches |
| 6 | Human fun gate and tablet/LAN check | Voluntary replay, comprehension, frustration, real-device performance evidence |
| 7 | Motorcycle pass, one new gadget if needed, three-race mini-cup | Distinct vehicle feel and a reason to return without grinding |
| 8 | Wider content production | Each new course adds a distinct interaction; campaign length follows actual playtesting |

The first playable milestone is Tasks 1–7 below. Tasks 3–6 build on a playable loop; keep it runnable after every task. Reassess at the human checkpoints rather than treating the whole list as an uninterrupted batch.

## Task 1: Make one race accessible without completing the campaign

**Outcome:** Race now starts a local race with three AI and offers a one-action rematch. Use the existing course temporarily so this can be tested before new art exists.

**Files to modify:**
- `Assets/FamilyRacing/Core/Progression/GameplayCatalog.cs`
- `Assets/FamilyRacing/Core/Racing/SoloRaceSession.cs`
- `Assets/FamilyRacing/Core/Racing/RaceModeRules.cs`
- `Assets/FamilyRacing/Core/Racing/ContentFingerprint.cs`
- `Assets/FamilyRacing/Core/Foundation/FoundationService.Gameplay.cs`
- `Assets/FamilyRacing/Core/Foundation/FoundationService.Progression.cs`
- `Assets/FamilyRacing/Core/Foundation/FoundationState.cs`
- `Assets/FamilyRacing/Runtime/Content/GameplayCatalogAsset.cs`
- `Assets/FamilyRacing/Runtime/FoundationShell.Screens.cs`
- `Assets/FamilyRacing/Runtime/FoundationShell.Progression.cs`
- `Assets/FamilyRacing/Runtime/Automation/ValidationJson.cs`
- `Assets/FamilyRacing/Editor/FoundationBuild.cs`, `Tools/Verify.ps1`

**Create:** `Assets/FamilyRacing/Editor/PlayfulChaosSetup.cs`; a dedicated scene and assets under `Assets/FamilyRacing/Generated/PlayfulChaos/`.

**Test owners:** `GameplayCatalogTests.cs`, `ModeEligibilityTests.cs`, `SoloRaceTests.cs`, `GameplayCommandTests.cs` in `Assets/FamilyRacing/Tests/EditMode/`; `GameplayScreensPlayTests.cs` and `GameplayStreamPlayTests.cs` in `Assets/FamilyRacing/Tests/PlayMode/`.

**Interfaces:** Continue using `quickRace.open`, `quickRace.start`, and `results.*`; do not add a competing race command family. Add an explicit catalog capability for Quick Race-only experimental content, used consistently by commands, UI discovery, snapshots, and reward eligibility. Preserve the existing full-catalog construction path and validation. This configuration has no persistent economy/campaign rewards; it must reject those commands with an explicit unavailable reason.

- [ ] Add failing tests: Quick Race-only content allows a four-racer start and rematch; campaign/garage/purchases/rewards remain unavailable; malformed vehicle/course/gadget/difficulty references fail atomically; the production validator still rejects incomplete inventory.
- [ ] Add the capability and route the existing local race coordinator through it. Inspect all economy/campaign dereferences before allowing this catalog; do not fabricate dummy finales or rewards to satisfy validation.
- [ ] Add idempotent `PlayfulChaosSetup.Create()` and a `WindowsPlayfulChaos` development build entry. Bind the actual course prefab, track, vehicles, presentation, gadgets, and catalog in that scene. Publish a distinct content identity and `partial` stage. The proposed build mode does not exist before this task.
- [ ] Isolate prototype profiles from ordinary family profiles. Add the small home screen and immediate rematch route; retain access to practice and settings.
- [ ] Run CoreOffline and affected Unity tests; launch the new build, complete three races, and verify roster, content identity, results, rematch, and unchanged ordinary saves. Record build source/hash. Commit the scoped working loop.

## Task 2: Clear the view and tune driving feel

**Modify:** `Runtime/FoundationShell.Driving.cs`, `Runtime/FoundationInput.cs`, `Runtime/Presentation/DrivingCamera.cs`, `Runtime/Driving/VehiclePresentation.cs`, `Runtime/Presentation/RacerVisualRig.cs`, `Runtime/Presentation/RaceAudioPresenter.cs`, `Core/Driving/VehicleTuning.cs`, `Core/Driving/ArcadeMotor.cs`, `Core/Driving/DriftBoostRules.cs`, `Core/Localization/ShellText.cs` under `Assets/FamilyRacing/`.

**Interfaces:** Input continues producing `DriverInput`; camera and effects read committed state and never influence simulation. Store experimental tuning in the new content assets instead of silently replacing existing presets. Preserve the reduced-motion setting in both cameras.

- [ ] Add regressions to `FoundationInputPlayTests`, `DrivingPresentationPlayTests`, and `DriftBoostTests` for layout switching without stuck input, held-boost behavior, and reduced-motion handling.
- [ ] Implement compact desktop HUD and corner-based touch controls. Prioritize place/lap/gadget/boost; move diagnostic fields out of the family HUD. Keep the spec's central road-view rectangle unobstructed.
- [ ] Create two controlled tuning candidates on the same course. Change acceleration, steering/drift response, and camera feedback in small groups; retain the baseline for comparison. Add continuous engine/boost feedback and clear drift/landing cues using existing presenters.
- [ ] Capture English/Hebrew at 1024×768 and 1920×1080, both cameras, keyboard and touch layouts. Check turns, ramps, pause/resume, and focus loss; perform an actual tablet thumb-reach check when available.
- [ ] Run the relevant tests and a brief human comparison. Record the chosen tune and why; record tablet checks as pending if the device is unavailable. Commit the chosen version.

**Human checkpoint:** A player should enjoy steering and taking the ramp before hazards are added. If they cannot read the road or reliably exit a drift, iterate here.

## Task 3: Author Harbor Havoc as a designed sequence

**Create:** `Assets/FamilyRacing/Editor/HarborHavocAuthoring.cs`; course prefab, track/event definitions, and materials under `Assets/FamilyRacing/Generated/PlayfulChaos/HarborHavoc/`.

**Modify:** `Editor/PlayfulChaosSetup.cs`, `Runtime/Content/TrackDefinitionAsset.cs`, and `Core/Racing/CourseDefinition.cs` only where route metadata requires it. Reuse `Editor/SunnyIslandCourseAuthoring.cs` assets/helpers after checking suitability and licenses. Avoid a general multi-course loader for this one-course milestone.

**Interfaces:** Course ID `fun-harbor-01`; two-lap car event `fun-harbor-car-01`; use existing `CheckpointGate`, `ShortcutZone`, `RecoveryVolume`, route, and safe-pose concepts. Branches rejoin before the next mandatory gate. Content setup preserves authored tuning and IDs on rerun.

- [ ] Add `HarborHavocContentTests.cs` in EditMode and `HarborHavocPlayTests.cs` in PlayMode: legal main/shortcut traversal, missed ramp recovery, valid spawn separation, no recovery shortcut credit, and repeatable setup preserving edits.
- [ ] Build the five course sections from the spec using simple readable geometry. Target 45–70 seconds per lap; measure rather than forcing the time through hidden speed changes.
- [ ] Add the two optional routes and landmark jump. Compare clean shortcut time with the main route; target roughly 1–2 seconds saved, with a recoverable miss. Ensure airborne traversal and camera framing work.
- [ ] Dress the course with existing Sunny Island materials/props, road edges, landmarks, and cheap breakaway crates. Reuse pooled effects; avoid networked debris rigidbodies.
- [ ] Traverse every route through normal input, then run ten AI races. Capture the course from both cameras. If the road is unclear or a shortcut is always optimal at no risk, fix geometry before adding effects. Commit the playable course.

## Task 4: Make bumps and stunt chains expressive

**Modify:** `Core/Driving/RacerContacts.cs`, `Core/Driving/ArcadeMotor.cs`, `Core/Racing/RecoveryRules.cs`, `Core/Racing/RaceMetricsAccumulator.cs`, `Core/Racing/DrivingEvent.cs`, `Runtime/Presentation/RaceEffectsPresenter.cs`, `Runtime/Presentation/RaceAudioPresenter.cs`, `Runtime/Presentation/RacerVisualRig.cs` under `Assets/FamilyRacing/`.

**Interfaces:** Keep physical-backend collision ownership intact. Authoritative contact events carry participant pair and impact intensity. Cosmetic chain/trick events derive from accepted drift/boost/flight/landing events and are deduplicated by race/event identity. No added stunt-charge multiplier in this slice.

- [ ] Add contact regressions for exactly overlapping racers, sustained side contact, wall pinning, airborne separation, and occupied recovery. Assert finite state, bounded velocity, no repeated contact farming, and no lost steering control.
- [ ] Add per-pair contact cooldown/sustained-contact resolution while retaining the existing 4 m/s per-step nudge ceiling. Tune glancing wall slides and recovery against normal driving tests.
- [ ] Add wobble, tire chirp, hit direction, and a brief animal reaction. Build the automatic ramp trick and drift → boost → jump → landing celebration from committed events; keep the simulation collider stable.
- [ ] Measure time lost and controllability in a four-car pileup and missed jump. Target no ordinary-bump input lock and return to driving within two seconds after recovery begins; report recovery-detection delay separately.
- [ ] Run `NetworkRaceTests`, `RecoveryTests`, `MotorTests`, relevant presentation PlayMode tests, and a four-racer smoke run. Commit the bounded contact/feedback changes.

## Task 5: Add one fair course surprise

**Create:** `Core/Racing/CourseHazardRules.cs`, `Core/Racing/CourseHazardState.cs`, and `Runtime/Presentation/CourseHazardPresenter.cs` under `Assets/FamilyRacing/`.

**Modify:** `Core/Racing/CourseDefinition.cs`, `Core/Racing/NetworkRace.cs`, `Core/Racing/NetworkRaceState.cs`, `Core/Racing/ContentFingerprint.cs`, `Runtime/Content/TrackDefinitionAsset.cs`, `Runtime/Networking/NetworkWireCodec.cs`, `Runtime/Automation/ValidationJson.cs`, and the harbor authoring code.

**Interfaces:** The race coordinator owns the fountain's seed, tick-based phase, and affected-racer state. The presenter consumes authoritative hazard snapshots/events. Hazard definition IDs, timing, and effect strength participate in compatibility checks. New snapshot/wire fields require explicit version handling.

- [ ] Add `CourseHazardTests.cs` and `CourseHazardNetworkPlayTests.cs`: identical seed/input produces identical phases; telegraph lasts at least 1.5 seconds; pause freezes the race clock; reconnect restores the phase; rematch resets state; overlapping hits respect disruption protection.
- [ ] Implement one fountain with anticipation, active, and cooldown phases and an always-open outer route. Use a bounded lateral effect. Reject invalid timings and placement in recovery/landing zones during authoring validation.
- [ ] Add the final-lap timing variant and announce it before the hazard approach. Do not schedule a surprise from a local client clock or race position.
- [ ] Verify shield/foam/contact interactions, event deduplication, content mismatch rejection, and two-process pause/rejoin/rematch. Repeat on physical LAN before accepting multiplayer quality. Commit the hazard with the lifecycle evidence.

## Task 6: Give rivals decisions and finish the replay loop

**Modify:** `Core/Racing/AiDriver.cs`, `Core/Racing/AiDifficultyDefinition.cs`, `Core/Racing/CourseDefinition.cs`, `Runtime/Content/TrackDefinitionAsset.cs`, `Runtime/FoundationShell.Progression.cs`, `Runtime/Presentation/RaceAudioPresenter.cs`, and `Core/Localization/ShellText.cs` under `Assets/FamilyRacing/`.

**Interfaces:** AI still emits ordinary `DriverInput`. Add authored branch routes and decision parameters; do not implement personalities with privileged speed. Results highlights consume existing authoritative metrics, not presentation estimates.

- [ ] Extend `NetworkAiTests.cs`: a cautious driver can stay on the main line; a shortcut driver can choose and finish the branch; occupied lanes change the target; all drivers obey the same motor limits and can recover from the fountain.
- [ ] Implement three visible driving styles using lane choice, shortcut willingness, and gadget timing. Keep difficulty explicit. Tune for encounters; log race gaps without forcing outcomes.
- [ ] Add contextual steer/drift/boost/gadget prompts, a concise finish celebration, one truthful highlight, and immediate Race again. Translate all new text and review icon comprehension without audio.
- [ ] Run repeated races across difficulties and two-process LAN. Review a human race for long empty stretches, confusing item effects, and rematch friction. Commit only after the loop remains understandable.

## Task 7: Prove the slice before expanding it

**Create:** `Tools/PlayfulChaosScenario.py`, focused tooling tests following the existing `Test*.py` convention, and `docs/playtests/playful-chaos/README.md` with a per-session observation template.

**Interfaces:** The scenario drives normal commands and state predicates. It reports content/build identity, completion, encounters, recoveries, hazard lifecycle, and rematches; it never declares the game fun. Human ratings and device measurements remain separately attributed evidence.

- [ ] Add and run meaningful tests for the scenario's command/state handling, timeout/error reporting, and build/content mismatch detection; then exercise it against the real slice build.
- [ ] Run two human playtest rounds using the design's exact replay/comprehension gates. Record spontaneous replay choices before asking for feedback. Adjust one main variable between comparisons.
- [ ] Run on the actual A7 Lite early, including four visible racers, fountain, gadgets, and landing effects. Inspect rendering, touch reach, audio, thermal behavior, and repeated rematches. Missing devices leave these rows unverified; they do not prevent PC design iteration.
- [ ] Verify physical tablet/PC LAN, shared pause, reconnect, host loss, and rematch using existing runbooks. Keep 30/60 fps targets and prior release requirements; an excellent PC GPU is not tablet evidence.
- [ ] Publish a short pass/iterate decision with build hashes, actual participant counts, raw observations, and remaining gaps. If players do not want another race, return to the highest-friction interaction instead of increasing content volume.

## Verification commands and evidence

Existing commands, run from the project root during implementation:

```powershell
.\Tools\Verify.ps1 -Mode CoreOffline
.\Tools\Verify.ps1 -Mode EditMode
.\Tools\Verify.ps1 -Mode PlayMode
python -m unittest discover -s Tools -p 'Test*.py'
```

Run focused checks per task, then the relevant complete suites at the milestone boundary. `WindowsPlayfulChaos` and `PlayfulChaosScenario.py` are planned deliverables, not currently available commands. Do not run a 78-event production qualification against this one-course experiment and misinterpret its expected inventory failures as evidence about fun.

## What comes after the first fun gate

1. **Motorcycle pass:** reuse the course, retune its handling and landing reactions, and test all branches/cameras without mixing race classes.
2. **One additional interaction if needed:** prototype the slow beach-ball gadget; retain it only if players understand and enjoy its counterplay. Defer destruction simulation, combat systems, and stunt-button combinations.
3. **Three-race mini-cup:** ordinary harbor race, a clear hazard variation, and a rival finale. Add a visible cosmetic reward and a short progression loop; enable durable rewards only through the existing validated transaction path.
4. **Content expansion:** finish the inherited tablet/content prerequisites, build the second distinct course, then the remaining worlds. Revisit the 78-event target if it produces repetition. Keep the existing broader plan as the long-term backlog rather than a checklist to rush through.

## Stop conditions and scope control

- Stop adding features when the course is hard to read or recovery feels unfair; fix that first.
- If collision intensity increases frustration, reduce repeated disruption and amplify cosmetic feedback instead of raising forces.
- If the new hazard adds no enjoyable decision, remove it; the course must remain fun with it disabled.
- If the tablet budget is exceeded, reduce effect density, overdraw, shadows, and decorative complexity based on profiling; keep interaction timing consistent across platforms.
- Do not start new worlds, buy an asset bundle, rewrite networking, or promote physical simulation merely because those tasks are easier to count than playtest improvement.

**Recommended first implementation package:** Task 1 plus Task 2, followed immediately by a short human handling check. The next visible result should be a playable four-racer loop with a clear road view.
