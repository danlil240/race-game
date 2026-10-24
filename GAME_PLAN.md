# Family Racing Game — Design and Implementation Plan

## 1. Summary and agreed boundaries

**Deliverable:** `C:\Users\example-user\race-game\GAME_PLAN.md`.

**File status:** Saved in the project root on 2026-10-11. This is the approved design; implementation and validation status are tracked separately in `IMPLEMENTATION_STATUS.md`.

Build a visually appealing family racing game for ages five and older, featuring **rally cars and dirt motorcycles**, friendly animal racers, spectacular jumps, drifting, boosts, playful gadgets, bounties, and upgrades.

| Area | Agreed direction |
|---|---|
| Platforms | Android tablets and Windows PC |
| Required tablet | Samsung Galaxy Tab A7 Lite SM-T220; assume the weaker 3 GB configuration |
| Graphics | Animated-movie-inspired stylized 3D; tablet-first optimization and richer PC presentation |
| Performance | Target stable 30 fps on the A7 Lite; 60 fps on the reference PC |
| Multiplayer | Two to four people over home Wi-Fi; four racers total |
| Connectivity | All gameplay works offline after installation |
| Vehicles | Four rally cars and four dirt motorcycles; separate race classes |
| Content | Three worlds, six shared courses, one combined campaign |
| Campaign length | Target 6–10 hours for family play, including learning and retries |
| Languages | Hebrew and English, including short packaged voice prompts |
| Agent testability | Every game control and feature is command-accessible; complete logical game state is streamed to a structured sink for end-to-end agent testing |
| Development | You with AI assistance; quality-based milestones without a fixed deadline |
| Budget | US$100 total for assets and tools, excluding devices and development time |

The initial release is privately distributed to the family. Public stores, advertising, purchases, accounts, internet multiplayer, split-screen, controller support, cloud saves, and profile transfers are outside this release.

“Most exciting” becomes a set of testable goals: responsive driving, memorable stunt routes, attractive visuals on the actual tablet, understandable challenges, and family members wanting another race.

## 2. Game experience and content

### Driving and controls

- Automatic acceleration on both platforms.
- Tablet controls: large left/right steering buttons, drift, boost, and gadget buttons.
- Keyboard defaults: arrows or A/D to steer, Space to drift, Shift to boost, E for gadgets, R to recover, C to change camera, Escape to pause.
- Switchable chase and elevated overhead cameras, saved per profile.
- Light assistance: automatic recovery when overturned, stuck, or outside the course; no automatic steering through ordinary corners.
- Motorcycles balance and lean automatically. Players control steering, drifting, boosts, and gadgets.
- Cars feel heavier and planted; motorcycles feel lighter and more agile. Suspension, rider leaning, and landing reactions reinforce the difference.
- Gentle racer collisions produce bounded nudges without damage or prolonged loss of control.
- Recovery returns the racer to the last valid checkpoint and cannot award skipped checkpoints or duplicate pickups.

A short interactive tutorial teaches steering first, then drifting, boosts, jumps, and gadgets. Each lesson combines demonstration, icons, and spoken guidance.

### Signature excitement

Each course contains:

- A clearly readable main route.
- At least two optional shortcuts with meaningful driving challenges.
- A landmark jump or stunt sequence.
- Opportunities to chain drifting into boosts and jumps.
- Safe recovery routes so mistakes cost time without ending the event.

Drifting and clean landings charge a boost meter. Collectible gadgets occupy one slot:

| Gadget | Effect |
|---|---|
| Turbo | Brief extra acceleration |
| Bubble shield | Blocks one rival gadget |
| Foam trap | Creates a clearly visible, short-lived slowdown patch |

Limit repeated slowdown effects with a brief protection period. Gadget distribution does not secretly depend on race position.

### Worlds and courses

These are six environments used by both classes, with separate vehicle tuning and event configurations.

| World | Courses | Visual and driving identity |
|---|---|---|
| Sunny Island | Harbor Hop; Lighthouse Loop | Turquoise water, bright villages, boardwalks, coastal jumps |
| Redrock Canyon | Canyon Dash; Mesa Skyway | Warm rock formations, bridges, sweeping drifts, elevated shortcuts |
| Snowy Peaks | Snowpine Circuit; Aurora Pass | Colorful alpine scenery, ice accents, snow ramps, mountain tunnels |

Use four animal characters sharing a compatible animation rig. Character selection is cosmetic. Regional rivals reuse this cast with distinct personalities and liveries.

### Campaign structure

Build **78 authored event configurations**:

- Each world has two courses.
- Each course supports both vehicle classes.
- Each course/class pairing has six events: circuit race, boost challenge, stunt challenge, time challenge, gadget race, and clean-driving challenge.
- That produces 24 regular events per world.
- Add one car rival finale and one motorcycle rival finale per world.

Every regular event awards:

1. One star for completing the prescribed course and laps.
2. One star for its introductory performance objective.
3. One star for its mastery objective.

Missing a time or stunt target does not remove the completion star. Objectives must include accessible alternatives to winning.

Complete the world’s 24 regular events and earn 30 regional stars to unlock its two finales. Completing both finales advances the adventure; beating the rivals awards additional cosmetics and completes their victory bounties.

Start with one car and one motorcycle. Completing each world unlocks one additional vehicle in each class, reaching four of each.

The 6–10 hour duration is a playtest target. The events themselves provide approximately 2.6–5.2 hours at two to four minutes each. Do not stretch the campaign with waiting timers or compulsory currency grinding.

### Bounties, upgrades, and other modes

- Offer three active challenge bounties at a time, drawn from a fixed offline catalog.
- Include drift, boost, landing, clean-driving, shortcut, and finishing objectives.
- Named-rival bounties reward defeating specific rivals.
- Rotate completed challenges immediately; use no daily attendance requirements.
- Award coins for completed campaign, quick, and local-network races. Better results earn modest bonuses.
- Provide three upgrade categories—acceleration, handling, and boost—with three levels each.
- Initial tuning: each level adds 5% to its category; costs are 60, 120, and 180 coins. Keep these values in editable content data and validate progression during playtests.
- Paints, accessories, and upgrade parts provide visible collection rewards.
- Easy, Medium, and Hard change visible AI capabilities. Avoid hidden catch-up speed advantages.
- Quick Race offers immediate races against computer opponents.
- Time Trial uses normalized vehicle performance and stores personal bests by course and class. It awards records rather than campaign progress.

Campaign and Quick Race respect profile unlocks. Local multiplayer exposes every course and vehicle model.

## 3. Technical design

### Engine and module boundaries

Use **Unity 6.3 LTS, C#, Universal Render Pipeline, the Input System, UI Toolkit, and Unity Test Framework**. Pin the production editor patch selected during setup and commit package lock files.

Begin networking with **Netcode for GameObjects 2.7.0 and Unity Transport 2.6.0**. The official package manifest specifies that dependency and a Unity 6000.0 minimum. Validate this combination in the first device build. [Official package manifest](https://raw.githubusercontent.com/Unity-Technologies/com.unity.netcode.gameobjects/ngo/2.7.0/com.unity.netcode.gameobjects/package.json)

Separate responsibilities into driving, race rules, opponents, networking, progression, presentation, and persistence. Keep authorable content in ScriptableObjects and profile state in versioned save files.

The important internal contracts are:

| Contract | Responsibility |
|---|---|
| `VehicleDefinition` | Class, visuals, baseline tuning, upgrade limits |
| `TrackDefinition` / `EventDefinition` | Course, checkpoints, class, objectives, reward configuration |
| `DriverInput` / `MotorState` | Shared driving input and simulation state |
| `RaceEvent` | Validated checkpoints, pickups, stunts, impacts, and finishes |
| `RaceResult` | Unique race identity, standings, eligible objectives, reward facts |
| `ProfileSave` | Unlocks, coins, upgrades, settings, records, and applied reward identities |
| `SessionDescriptor` | Host address, compatible build/content version, selected event, available slots |
| `GameCommand` / `CommandResult` | Versioned commands, correlation identities, validation, execution status, and observable outcomes |
| `GameStateSnapshot` / `GameStateDelta` / `IGameStateSink` | Complete reconstructable logical state, ordered changes, and structured stream delivery |

Race rules generate events; presentation consumes them. Reward calculation consumes finalized results and never grants currency directly from visual effects or UI actions.

### Command-driven control and agent-observable state

**Required scope, added 2026-10-11:** All game controls and all implemented features must be executable through documented commands, and every logical game state must be observable through a structured stream delivered to a sink. An agent must be able to operate and verify the running game without a human clicking through it. This is a cross-cutting design requirement from Foundations onward, not a debug interface added after the game is finished.

**Command coverage**

- Route keyboard, touch, UI actions, and agent commands through shared input/application services and the same validation, race rules, ownership checks, and persistence paths. An agent command must not use a parallel implementation that makes tests pass while ordinary gameplay is broken.
- Expose all steering and action controls, including press, hold, release, simultaneous controls, drift, boost, gadgets, recovery, camera switching, and pause/resume. Held input must have a bounded duration and clear on control-channel disconnect, focus loss, scene teardown, and test termination.
- Expose every menu and feature action: profile selection/creation, settings and language, tutorial actions and voice replay, character/vehicle/course/event/difficulty selection, Campaign, Quick Race, Time Trial, bounties, upgrades and cosmetics, results and rematch, and LAN host/discovery/manual join/ready/leave/reconnect.
- Use stable semantic action and UI-element identifiers, not localized button text or screen coordinates. Command discovery must report available commands, parameter schemas, build/content/protocol versions, current permissions, and unavailable actions with reasons.
- Each `GameCommand` carries a protocol version, run identity, unique command identity, target participant, action, and parameters. `CommandResult` distinguishes queued/accepted execution from applied, rejected, or failed completion, includes a safe reason, and correlates the outcome with stream sequence/tick identities. Invalid commands must not partially mutate state.
- Re-delivering the same command identity in the same run must not repeat a side effect; different payloads reusing an identity are rejected. Reconnect must permit outcome lookup without blindly replaying purchases, rewards, or other mutations. The agent waits for terminal outcomes and state predicates with timeouts rather than assuming arbitrary sleeps prove completion.
- Normal commands never directly grant coins/stars, unlock content, teleport past checkpoints, or override host authority. Fixture loading, random-seed selection, clock stepping, and fault injection are separate test-only capabilities, clearly marked in traces; passing a feature scenario must exercise the real feature path.

**State coverage and sink contract**

- `IGameStateSink` receives machine-readable, versioned records. The baseline wire/storage format is newline-delimited JSON (NDJSON); live subscribers and a per-run local trace file consume the same record schema. No cloud service or internet connection is required.
- Start a capture with a complete `GameStateSnapshot`, then emit ordered `GameStateDelta` records for every logical state change and a state record after every simulation tick, including ticks with no changes. Emit non-simulation changes immediately as well, including menu, loading, paused, failure, and persistence states. An independent stream sequence must continue while the race clock is paused.
- Snapshot plus deltas must reconstruct the complete logical state at every captured boundary. Records carry run/session/device/participant identities, build/content/schema versions, a monotonic sequence, monotonic time, simulation tick where applicable, and related command/event identities. Record transient events as well as final values so events that begin and end within one tick are not lost.
- Include current screen/dialog/loading state and semantic UI state; selected profile and settings; unlocks, coins, upgrades, bounties, records and applied reward identities; tutorial progress; race phase, clocks, checkpoint/lap/standing/objective/finish state; all racers' motor/input/airborne/recovery/drift/boost/gadget/collision state; pickups and effects; camera/audio/presentation state; and LAN roles, ownership, readiness, pause, disconnect/reconnect, prediction/correction and final-result delivery state.
- Distinguish pending in-memory changes from successfully persisted data and failed writes. Distinguish authoritative host state, client-predicted state, and interpolated presentation state; each test asserts against the state source relevant to the feature rather than treating a client prediction as a confirmed result.
- Emit command outcomes, validation failures, save failures, game errors, and stream-health information. Exclude credentials, connection tokens, raw unreadable saves, and unnecessary private machine data; use synthetic profiles and an isolated save directory for automated runs.
- Capture a new full snapshot on observer attachment, scene/session changes, and resynchronization. Detect missing/out-of-order records and permit explicit snapshot requests. Resynchronization restores observation but does not make a capture with missing required history a passing test.
- Serialize/write asynchronously with bounded memory and bounded trace-file storage; never block the race thread on a slow sink. In full-state validation mode, do not silently sample, coalesce away, or discard required state/event records. Overflow, sink disconnection, or write failure explicitly invalidates the capture and fails the affected automated run; report this through a separate control/status path if the sink itself is unavailable.
- "Every game state" means all logical game/application state and observable presentation decisions, not a dump of Unity objects, textures, audio buffers, or every rendered pixel. Add screenshot/frame capture commands and semantic layout/audio observations where visual or audio features need evidence; retain physical-device and human acceptance checks for rendering, pronunciation, responsiveness, and enjoyment.

**Access, execution, and evidence**

- Provide the interface in the Unity Editor and dedicated Windows/Android validation builds. Default transport is a token-protected loopback-only command/stream connection; Android agents connect through ADB forwarding to each device. It must not expose an unauthenticated control port to the home Wi-Fi network. Keep validation access disabled in ordinary family builds.
- Run commands on the appropriate game-thread/tick boundary. Tests may control deterministic seeds and advance bounded ticks in isolated solo validation sessions; live LAN tests still run the real network, clocks, ownership, prediction, pause and reconnect paths.
- Maintain a feature coverage registry mapping every control/feature and failure path to its commands, observed state fields/events, automated scenarios, and remaining human/device checks. Newly implemented features cannot pass their milestone without command access, state visibility, and recorded scenario evidence; unavailable/unimplemented features are explicit gaps, not passing coverage.
- Each scenario saves its command trace, full reconstructable state stream, assertions and timeout/failure reasons, relevant logs and screenshots, and build/content/seed identities. Test-tool setup/fixtures must not masquerade as successful gameplay.
- Qualify the full capture path on the required A7 Lite, including a tablet host and four racers. Measure instrumentation overhead, memory, and storage separately from normal-build performance; both instrumented validation and ordinary family builds retain the existing performance targets.

### Graphics and audio

Use URP Forward rendering with a dedicated A7 Lite profile:

- OpenGL ES 3.
- Baked environmental lighting and simple vehicle shadows.
- Start at 0.75 internal render scale; keep the interface sharp.
- Load one course at a time.
- Use mesh detail levels, pooled effects, restrained transparency, and compact texture atlases.
- Avoid expensive full-screen effects, real-time reflections, and motion blur.
- Profile both cameras; the overhead camera can expose substantially more scenery.

The PC profile adds resolution, shadows, environmental detail, and effects while preserving identical course collision and gameplay visibility.

Create a small art guide covering palette, proportions, materials, lighting, vegetation, character silhouettes, and effects. Approve a representative course on the actual tablet before producing the remaining worlds.

Use coordinated engine sounds, motorcycle audio, landing effects, musical transitions, and clear gadget cues. Provide separate music, effects, and voice volume controls.

Generate short Hebrew and English prompts during production using licensed stock synthetic voices, then bundle the audio. Azure Speech is the initial production choice: it supports Hebrew voices and exporting synthesized speech to files. No speech service or credentials belong in the running game. [Voice support](https://learn.microsoft.com/azure/cognitive-services/speech-service/language-support?tabs=tts), [Audio-file synthesis](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/get-started-text-to-speech)

### Hebrew, English, and accessibility

Use UI Toolkit’s Advanced Text Generator with bundled dynamic Hebrew and Latin font assets. It provides Unicode shaping support; Hebrew layouts also require deliberate right-to-left configuration. [Unity text rendering](https://docs.unity3d.com/6000.3/Documentation/Manual/UIE-advanced-text-generator.html)

- Store Hebrew strings in normal logical order.
- Localize complete sentences rather than assembling translated fragments.
- Keep timers and network addresses in left-to-right fields.
- Mirror navigation appropriately; retain the physical meaning of steering arrows.
- Use icons alongside text, large touch targets, and symbols in addition to color.
- Include optional camera shake reduction and immediate voice replay.
- Package fonts, translations, and audio for offline operation.
- Require a Hebrew-speaking review of text, layout, and pronunciation.

### Local multiplayer

Any supported tablet or PC can host. Tablet-to-tablet racing must work without a PC.

Flow: **Host → nearby discovery → Join → choose class/course → Ready → Race → Results → Rematch**.

- Use local UDP discovery with a parent-accessible manual IP fallback.
- Show useful diagnostics for blocked discovery, incompatible versions, full lobbies, and unreachable hosts.
- Require matching gameplay/content versions.
- Handle Windows firewall setup and relevant Android network permissions. Android’s permission requirements depend on target SDK and OS; implement the applicable flow rather than assuming local networking is always permitted. [Android local-network permissions](https://developer.android.com/privacy-and-security/local-network-permission)
- Four racers total; computer racers fill vacant human slots.
- Normalize vehicle performance within the selected class, including upgrades. Keep cosmetics.
- The host owns checkpoints, pickups, gadget effects, standings, and final results.
- Use a shared arcade motor with ground probes, collision sweeps, and controlled airborne states.
- Start with 30 Hz simulation and 15 Hz snapshots. Predict local movement, acknowledge inputs, replay unacknowledged input after corrections, and interpolate remote racers.

*Implementation note (2026-10-13): an opt-in `PhysicalAssisted` backend (raycast-wheel force model, fixed substeps, assisted motorcycle balance, host-authoritative physical LAN world with approximate client replay) exists behind a dedicated development player for qualification. The arcade motor above remains the shipped default and developer fallback; switching the default requires the open device/human/impaired-network gates in `docs/tasks/vehicle-physics-report.md` to pass.*

Prediction is a dedicated engineering task: NGO does not supply a complete prediction/reconciliation loop. [NGO prediction limits](https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.7/manual/advanced-topics/client-anticipation.html)

**Pausing and recovery:**

- Any player can request a pause; the host controls shared pause/resume.
- A disconnected player retains their slot for the remainder of the race.
- AI temporarily drives that racer. Returning clients receive authoritative state and regain control.
- AI actions during absence do not earn human skill-bounty credit.
- Retain results for reconnecting participants while the host session remains open.
- Apply each reward once, including after reconnection or repeated result delivery.
- Closing the host ends the session. Preserve previously saved progress and return clients to a recovery screen; there is no host migration.
- Aborted, unfinished races do not award completion rewards.

### Profiles and persistence

Provide four local profile slots per installation, identified by animal portraits and colors.

Save progress after finalized rewards, purchases, unlocks, and settings changes. Use atomic replacement, a last-known-good backup, and a schema version. Validate loaded values; preserve unreadable or newer-version saves rather than silently overwriting them.

Local races award coins and eligible challenge bounties. Campaign stars and rival unlocks remain solo. Time Trial uses separate normalized records.

## 4. Implementation milestones and acceptance tests

Each milestone ends with a playable build, evidence of its checks, and a review before expanding scope.

Every milestone also requires command/state-stream coverage for all features implemented in that milestone, following the agent-testability contract in section 3. Milestone 1 establishes the command dispatcher, sink protocol, isolated test profiles, and external-agent scenario runner alongside the input/UI shell; later milestones extend that foundation rather than inventing separate testing shortcuts.

| Milestone | Deliverable | Required acceptance |
|---|---|---|
| **1. Foundations** | Unity project, repeatable Android/Windows builds, input shell, bilingual interface, local profiles | Launch on both platforms; Hebrew renders correctly; fresh install operates offline; saved settings survive restart |
| **2. Driving prototype** | One graybox course, one rally car, one motorcycle, both cameras, drift/boost/jumps/recovery | Both vehicles feel distinct; motorcycle balance is automatic; checkpoints cannot be skipped; a child can finish after the tutorial |
| **3. Network prototype** | Hosting, discovery, synchronized racing, prediction, pause, reconnect, results | Two tablets work without PC/internet; tablet–PC works with either hosting; loss/reconnect and duplicate results behave correctly |
| **4. Visual benchmark** | One representative course at intended quality, animals, lighting, effects, audio, four racers | Owner accepts tablet visuals; sustained performance passes with the tablet hosting; both cameras remain usable |
| **5. Complete gameplay loop** | One world’s campaign, bounties, upgrades, rival finales, Quick Race, Time Trial | Progression works without mandatory victories; rewards and purchases persist; LAN normalization and solo unlock rules are correct |
| **6. Full content** | Three worlds, six courses, eight vehicles, 78 events, final bilingual audio | Every event is completable by its assigned class; objectives and rewards are correct; campaign pacing is measured |
| **7. Family release** | Signed Android APK, Windows package, installation/update guide, backups and license inventory | Physical-device multiplayer matrix, long sessions, update preservation, and final family playtests pass |

**Automated verification**

- Command/UI/input parity for every implemented action; command discovery, validation, completion correlation, duplicate delivery, bounded holds, and disconnect cleanup.
- Complete snapshot/delta reconstruction for every captured state boundary; transient events, paused-state changes, observer reconnect, ordering/gap detection, and explicit failure on slow/broken sinks.
- Agent-driven end-to-end feature scenarios on running Windows and Android builds, including multi-device host/client scenarios, with isolated saves and reproducible command/state evidence.
- Driving state transitions, boost limits, recovery, and bounded collisions.
- Ordered checkpoints, lap completion, finish ordering, and shortcut legality.
- Objective evaluation, upgrade costs/caps, star gates, and rival rewards.
- Reward deduplication, interrupted saves, backup recovery, and save migration.
- Reconnect ownership, version rejection, paused race clocks, and host-loss cleanup.
- Content validation for missing references, translations, audio, or impossible objectives.

**Physical-device verification**

- Tablet–tablet and tablet–PC, with each device type hosting.
- Four physical devices, including an A7 Lite host.
- Home Wi-Fi with internet disconnected.
- Permission denial, blocked discovery, manual connection, and incompatible builds.
- Disconnect during countdown, racing, pausing, finishing, and result delivery.
- Repeated rematches, profile switching, and application restarts.
- Induced 100 ms round-trip delay, jitter, and 2% packet loss.
- Thirty-minute sessions to expose thermal slowdown and memory growth.

**Initial performance acceptance**

- Tablet: 30 fps target, 95th-percentile steady-state frame time at or below 40 ms, without recurring stalls above 100 ms.
- Start with an 800 MB process-memory budget on the 3 GB tablet; investigate growth across repeated races.
- PC: 60 fps at 1080p on the available reference PC.
- Immediate local steering feedback; network corrections must not routinely produce visible large jumps.

These are qualification targets, not measured results. If the A7 Lite fails, optimize scenery, rendering, memory, and simulation before expanding content. Preserve the required tablet and four-player scope.

**Family acceptance**

A five-year-old should be able to complete onboarding, recognize controls, finish an introductory race, understand the reward, and start another event with little reading assistance. Older players should find satisfying mastery in shortcuts, drift timing, stunts, and stronger opponents.

Measure enjoyment and repetition during the first world. Measure the 6–10 hour campaign target with the full event set; do not manufacture playtime through unnecessary grind.

## 5. Budget, ownership, and release defaults

| Allocation | Maximum |
|---|---:|
| Environment assets | US$35 |
| Vehicles and characters | US$35 |
| Audio and synthetic prompts | US$15 |
| Reserve | US$15 |
| **Total** | **US$100** |

Use a coherent free-asset foundation and spend only where it materially improves consistency or saves production work. Maintain an asset inventory with source, license, attribution, and cost. The budget is a planning cap, not authorization to purchase.

The owner supplies device access, reviews the visual benchmark, verifies Hebrew, and coordinates family playtests. AI assistance handles code, tooling, content configuration, documentation, and test support. Art assembly, animation correction, profiling, and device testing remain explicit production work.

Two tablets and one PC are available. **A fourth supported physical device must be borrowed or obtained before four-player acceptance can pass.**

Distribute a signed APK and a Windows x64 package, with all game content included. Preserve the Android signing key and application identity so updates retain saves. Test updates over an existing installation and document recovery steps.

Save this complete plan in the project root as `GAME_PLAN.md`. Keep implementation progress, measured performance, asset spending, and any subsequently approved scope changes alongside the corresponding milestones.
