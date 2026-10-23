# Family Racing: playful chaos design

Date: 18 October 2026. Status: proposed direction for review; planning only.

## Intent

The owner describes the current game as early and not fun, and explicitly chose **chaotic racing with collisions, stunts, and surprises** as the desired experience.

The next milestone should produce one race people voluntarily replay. The proposed promise is: **bump a rival, take a risky shortcut, launch over the harbor, land with a boost, and recover quickly when it goes wrong.**

Keep the existing family audience, ages five and older, Windows/Android platforms, offline play, four racers, Hebrew/English, separate car and motorcycle classes, and $100 total asset/tool budget. Start the experiment with the existing car. Bring the motorcycle through the same course after the car version passes its first playtest. This changes the order of work, not the eventual roster.

## What the evidence says

This assessment uses current source, the 18 October QA report, and its screenshots. It is not a new hands-on driving playtest. Subjective handling problems below are hypotheses to test.

| Observed evidence | Implication |
| --- | --- |
| `Generated/Foundation.unity` has `gameplayCatalog: {fileID: 0}`. Session 02 reports `available=false`, content identity `none`, and zero authored campaign events. | The normal build still lacks a complete player-facing race/progression loop. Do not confuse implemented services with available gameplay. |
| Session 02's race screenshot shows broad HUD strips and two rows of large controls over the graybox view. The home screen is called Input sandbox. | The interface presents a development tool and uses space needed for reading the road. |
| `DrivingPrototypeSetup` creates a large rectangular ground plane, four progress gates, obstacles, a ramp, and optional branches. | Shortcuts and a jump already exist; the next task is a coherent course with readable racing lines and interesting sequences. |
| `ArcadeMotor` and `DriftBoostRules` already provide auto-acceleration, drift, passive/earned charge, held boost, jumping, and landing credit. | Improve tuning, reward clarity, and combinations instead of adding a second version of each mechanic. |
| `RacerContacts` implements bounded separation nudges. `AiDriver` follows waypoints with lane offsets; shortcut willingness is stored but does not currently select a shortcut. | Collision personality and rival decisions need work before a race can deliver intentional chaos. |
| Gadgets, authoritative local/LAN races, result handling, effects/audio presenters, and profile transactions exist. | Reuse these systems. No engine replacement or networking rewrite is justified. |
| QA still reports missing tutorial recordings and an obsolete extracted release build. | Playtests need a clearly identified current build and understandable onboarding. Existing acceptance gaps remain open. |

Source anchors: `GAME_PLAN.md`; `docs/qa/pc-session-02-2026-10-18/REPORT.md`; `docs/qa/pc-session-02-2026-10-18/runtime/scenarios/runtime-report.md`; `Assets/FamilyRacing/Core/Driving`; `Assets/FamilyRacing/Core/Racing`; `Assets/FamilyRacing/Runtime/Presentation`.

## Approaches considered

| Approach | Advantage | Cost / weakness | Decision |
| --- | --- | --- | --- |
| Concentrate on one excellent chaotic race | Fast feedback on the actual fun; reuses most existing machinery | Requires repeated playtesting and discarding weak ideas | Recommended |
| Finish six courses, eight vehicles, and 78 events first | Meets the existing inventory plan | Multiplies unproven driving and course design | Defer expansion until the race passes its fun gate |
| Make physical simulation the default first | More natural suspension and impacts | Adds tuning and network uncertainty; realism does not establish fun | Keep the arcade backend for the experiment; retain physical mode as an optional comparator |

## The first playable slice: Harbor Havoc

Working title for a new Sunny Island prototype, not a rename of shipped content IDs.

- One course, one car initially, four racers: one player plus three AI, then two to four LAN players with AI fill.
- Two laps; target total race duration 90–150 seconds including the countdown, excluding menus.
- One primary action on the home screen: Race now. Results offer Race again immediately.
- Existing drift, boost, turbo, shield, and foam trap; no new control button in this slice.
- Two optional shortcuts, one landmark jump, one type of active course hazard, and lightweight smashable scenery.
- A small final-lap change in hazard timing and presentation, announced before the player reaches it. The main route stays available.
- Race position remains the competitive objective. Stunt celebrations do not replace finishing the race.

### Course rhythm

Target a 45–70 second lap. These times are design starting points, not measured facts.

| Section | Player decision | Payoff / safe alternative |
| --- | --- | --- |
| Start and harbor sweep | Hold the broad line or drift close to the inside | Early speed sensation and a first successful drift; generous road width |
| Market split | Take a tighter shortcut through colorful breakaway crates or stay on the promenade | Save roughly 1–2 seconds on a clean attempt; crates produce a visual burst, not a physics pileup |
| Pier jump | Spend boost before takeoff or save it for the landing | A clear view of the landing and an expressive automatic animal trick; forgiving lower route if the shortcut is missed |
| Fountain crossing | Take the short center line through a telegraphed water pulse or the longer outside lane | Visible choice and a small lateral bump; no mandatory stop or blind random hit |
| Final bend and rejoin | Carry a drift out of the corner and time a gadget against nearby rivals | A finish-line contest; shortcuts rejoin before mandatory checkpoints |

Route texture, edge markings, arrows, silhouettes, and lighting must communicate the route without reading text. Decorative checkpoint gantries must not become opaque foreground panels. Capture both camera modes at takeoff, landing, each turn, and each hazard approach.

### Driving, collisions, and stunts

1. Tune acceleration, steering response, drift entry/release, and recovery as a group. Compare versions on the same short test course. Preserve auto-acceleration and the existing held-boost behavior.
2. Make speed visible: road markings and nearby scenery, wheel motion, body lean, engine response, boost exhaust, and restrained camera changes. Use a fixed horizon. Reduced-motion mode disables added shake and dynamic FOV effects.
3. Separate glancing bumps from head-on impacts. A side contact should produce a readable shove, body wobble, tire chirp, and animal reaction while steering remains available. Repeated overlap must not continuously kick the player or farm rewards.
4. Preserve the existing 4 m/s per-step nudge ceiling during initial tuning; add a per-pair cooldown and sustained-contact handling rather than increasing impulses globally. Keep physical and arcade contact handling separate.
5. Use forgiving wall slides and quick recovery. Initial target: ordinary bumps cause no input lock; an off-course recovery returns the player to driving within two seconds after recovery begins. Measure detection delay separately.
6. Build a short sequence out of existing actions: drift → boost → jump → clean landing. Celebrate a completed chain with a compact animation and sound. Initially give no extra gameplay charge beyond existing drift/landing rewards; first test whether the sequence itself is satisfying.
7. Automatic trick animation on the signature jump uses the visual rig only. Do not rotate the simulation body for a decorative flip, change the collider, or require aerial button combinations.

### Surprises with understandable rules

The fountain is the only new gameplay hazard in the first slice. It has a visible anticipation phase of at least 1.5 seconds, a brief active phase, and a cooldown. All racers see the same phase and can take the safe line. A race seed and host tick determine its schedule; clients never independently roll hazards.

Reserve the landing and recovery zones from hazard placement. Do not stack a fountain, foam trap, and forced collision into one unavoidable corridor. Use existing slowdown protection and extend it only where needed to prevent repeated disruption.

Smashable crates are presentation-first: bounded pooled fragments, no persistent debris colliders, no requirement to simulate dozens of rigidbodies over LAN. Their apparent impact must agree with the authoritative traversal event.

After the first slice passes, the first candidate new gadget is a **large, slow beach ball** that visibly bounces down the road and gives a bounded shove. It is deferred because projectile authority, collision fairness, and counterplay are a separate work package. It must have shield counterplay and a finite lifetime.

### Rivals and comeback opportunities

Give the three AI drivers distinct decisions: a safe driver, a shortcut-seeker, and a gadget opportunist. Implement lane and branch choices, awareness of occupied space, recovery, and readable gadget use. All use ordinary driver inputs and the same speed limits as the player.

Keep the existing ban on hidden catch-up speed and position-dependent item distributions. Bring the pack together through short laps, deliberate route tradeoffs, shared pickup opportunities, and difficulty presets calibrated to the audience. If novices fall behind immediately, first adjust difficulty, route clarity, and mistake costs.

### Presentation and onboarding

- Replace Input sandbox as the player landing screen with Race now, Race together, Practice, and Settings. Keep diagnostics reachable through developer tooling.
- On keyboard, hide the large touch-button grid; show compact contextual key prompts. On touch, place steering at the lower left and actions at the lower right. Keep menus out of the racing view.
- Show place, lap, held gadget, and boost most prominently. Remove gate index and raw speed from the default family HUD; retain them in diagnostics.
- Keep the central 60% of the viewport width and middle 50% of its height free of persistent control panels in both layouts. This is a layout acceptance target, not a touch-size prescription.
- Start with a brief steer-and-drift prompt inside a playable race. Introduce boost near the first safe straight and explain the gadget when collected. Keep the full tutorial optional and replayable.
- Use icons, motion, distinct sounds, and short Hebrew/English captions. Missing voice assets must not make the race incomprehensible. Final recordings still require human listening review.
- On results, show finish position and one truthful highlight, such as longest jump or best clean chain. One action starts the rematch; no mandatory reward sequence blocks it.

## Architecture and boundaries

Continue using Unity 6000.3.26f1, Core rules, ScriptableObject content, the existing command path, and `SoloRaceSession`/`NetworkRace`. Keep simulation authoritative; presentation consumes committed events.

The immediate technical obstacle is that Quick Race is gated behind the broader gameplay catalog. The recommended narrow change is a validated Quick Race-only catalog capability. It supplies driving content, gadgets, and AI difficulty without requiring dummy campaign finales, upgrade rewards, or 78 events. Campaign, purchases, bounties, and persistent rewards stay unavailable in that experimental configuration. Existing production catalog validation remains strict.

Give the experiment its own content identity and build scene, with explicit vehicle, track, prefab, gadget, and presentation bindings. The existing `DrivingWorld` can bind one course; a general multi-course loader is unnecessary for this slice. Do not overwrite the working graybox or imply the old benchmark scene is the new course.

For LAN, include gameplay-affecting tuning, route branches, and hazard definitions in compatibility checks; replicate the current hazard phase on reconnect. Preserve command/state testability, ordered checkpoints, safe recovery, and exactly-once results. Version wire/state changes when their contract changes.

## How we decide it is fun

Run two rounds with at least three intended family players per round, including a younger beginner and an adult when available. Returning players can participate in both rounds; record that fact. Use the same build and difficulty within a comparison. These are small-sample go/no-go targets, not statistical proof.

| Measure | Proposed gate |
| --- | --- |
| First-time comprehension | At least 2 of 3 can start and finish a race with no verbal coaching after the opening prompt |
| Voluntary replay | At least 2 of 3 independently choose another race after their first; do not prompt them to replay |
| Memorable interaction | At least 2 of 3 can describe a bump, shortcut, jump, or hazard they intentionally used |
| Enjoyment | Median at least 4/5, recorded privately before discussing changes |
| Frustration | No repeated trapping or unavoidable hit chain; record every intervention and quit |
| Rematch friction | One action to rematch; target no more than five seconds to the next countdown on reference PC, measured separately on the tablet |
| Performance | Existing targets: 60 fps reference PC; 30 fps Galaxy Tab A7 Lite SM-T220, weaker 3 GB configuration |

Record raw observations alongside ratings: time to first race, wall hits, recoveries, longest idle stretch, pack gaps, shortcut attempts, gadget encounters, and cause of each interruption. A high event count alone does not mean the race is fun. Observe whether the player understood and enjoyed the event.

Automated tests prove rules and regressions. Human playtests decide feel and replay appeal. If the replay gate fails, change handling, readability, or the course rhythm before adding worlds, vehicles, or progression systems.

## Later expansion

After the one-course gate: port and tune motorcycles; verify real-device LAN and tablet performance; introduce the beach ball if encounters still need variety; produce a three-race mini-cup with one distinctive variation per event and a cosmetic reward. Only then resume the six-course/78-event roadmap, reconsidering repetition against measured family enjoyment rather than treating the inventory count as success.

## External design references

Nintendo's developers describe adding visible whitecaps so players can read and time wave jumps. The applicable principle here is making a stunt opportunity visually legible; it is not a proposal to copy their game or scale. [Nintendo developer interview, part 3](https://www.nintendo.com/us/whatsnew/ask-the-developer-vol-18-mario-kart-world-part-3/).

Unity's pinned-version documentation provides URP profiling and optimization guidance. Measure the new course and effects on the actual low-end target before expanding them. [Unity 6000.3 graphics performance and profiling](https://docs.unity.com/en-us/engine/6000.3/manual/analysis/graphics-performance-profiling/in-urp).
