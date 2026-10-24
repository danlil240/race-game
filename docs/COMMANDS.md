# Command and validation reference

Exact commands for building, running, and validating the foundation shell.
All Unity invocations use the pinned editor `6000.3.26f1`. The verified
project resolves from this editor's bundled package cache; do not assume a
fresh installation has every dependency cached or authorize downloads implicitly.

## Local verification

```powershell
# Shared Core tests, fully offline (local .NET SDK + bundled Unity NUnit):
Tools\Verify.ps1 -Mode CoreOffline

# Environment check (pinned editor, modules, .NET SDK):
Tools\Verify.ps1 -Mode CheckEnvironment

# Unity project setup (repeatable; writes generated scene/assets):
Tools\Verify.ps1 -Mode Setup

# Unity test platforms (results XML under Logs\):
Tools\Verify.ps1 -Mode EditMode
Tools\Verify.ps1 -Mode PlayMode

# Windows Mono development build -> Builds\Windows\FamilyRacing.exe:
Tools\Verify.ps1 -Mode Windows

# Dedicated Windows build script with per-run evidence
# (Logs\windows-build\<flavor>\<run-id>\result.json):
Tools\Build-Windows.ps1                          # development flavor
Tools\Build-Windows.ps1 -Configuration Ordinary  # -> Builds\Windows-Ordinary\
# Opt-in physical-backend development player -> Builds\Windows-Physics\
# (self-bootstrapping PhysicsRace scene, PhysicalAssisted catalog):
Tools\Build-Windows.ps1 -Configuration Physics
```

`Verify.ps1` modes write `Logs\verify-unity-<mode>.log` and propagate the
editor exit code. `dotnet test` (`-Mode Core`) works after the approved
one-time `dotnet restore` (nuget.org added as a user-level package source;
225/225 green on 2026-10-11). `-Mode CoreOffline` still runs the same checks
without NuGet.

```powershell
# Repeatable offline Android build -> Builds\Android\FamilyRacing.apk:
Tools\Verify.ps1 -Mode Android
```

The Android mode exports with the pinned editor and builds the exported
Gradle project with the bundled JDK/Gradle under `--offline`; the produced
APK is a build artifact only — no device/ADB qualification is claimed.

The Windows IL2CPP module is now installed for 6000.3.26f1
(`CheckEnvironment` reports it OK); `FoundationBuild.Windows` still emits the
Mono development player.

## Validation run

The loopback endpoint exists only in editor/development builds and only
when opted in:

```powershell
# Run this in a persistent terminal. In an agent terminal, keep this command
# backgrounded with its shell alive; detaching alone may not escape its job.
$exe = (Resolve-Path "Builds\Windows\FamilyRacing.exe").Path
$run = Join-Path (Get-Location) "Logs\validation-<unique-name>"
$player = Start-Process $exe -ArgumentList "--validation-dir",$run -PassThru
$player.WaitForExit()
```

The directory receives `connection.json` (`runId`, `token`, `port`,
`protocolVersion`, `participantId`), `state.ndjson` (sequenced NDJSON
records), `capture-status.json`, and an isolated `profiles/` store. Two
development-only flags change the profile store selection (ignored entirely
without `--validation-dir` and in ordinary builds):

- `--validation-profile-dir <path>` — point at a specific synthetic store
  (shareable across fresh capture dirs for restart scenarios; mutually
  exclusive with `--validation-normal-profiles`).
- `--validation-normal-profiles` — use the REAL
  `persistentDataPath\Profiles` store; valueless; exercised by
  `Tools\NormalProfileScenario.py`.
- `--validation-trace-mb <n>` — raise the still-bounded trace ceiling for
  this run (default 256 MiB, clamped to 64–2048). Physical-diagnostic
  streams emit ~500 KB/s of boundary data, so a long physical soak
  launches with e.g. `--validation-trace-mb 1024`; the run's effective
  bound is published on `connection.json` as `traceLimitBytes` so
  `verify_trace` measures the capture against it.

Validation launches set `Application.runInBackground = true` so agents do
not depend on window focus; ordinary launches keep focus-pause.

Visual-benchmark players (development AND ordinary builds) also accept
presentation flags — none of them enable a control endpoint in ordinary
builds:

- `--render-profile tablet|pc` — force the render profile (default:
  Android→tablet at 30 fps/0.75 render scale, other platforms→PC at
  60 fps/full scale).
- `--perf-capture off` — disable the frame-interval recorder for a
  matched no-capture run (capture-overhead A/B measurement arm).
- `--perf-report-dir <path>` — where the compact `perf-report-<runId>.json`
  lands at quit (default `persistentDataPath\performance`); written on a
  background writer in both development and ordinary builds.

External process-memory sampling (once per second, outside the player —
never Unity heap allocations; Android reports app **total PSS** via
`adb dumpsys meminfo`, Windows reports **working set** via psapi; each
record names metric/source/units and devices compare only against their
own baseline):

```powershell
python Tools\PerfSampler.py --pid <player-pid> --evidence <new-dir> [--seconds N]
python Tools\PerfSampler.py --adb-serial <serial> [--package com.daniel.familyracing] --evidence <new-dir> [--seconds N]
```

The lead-owned scenario drives it via:

```powershell
python Tools\AgentScenario.py <dir>\connection.json --evidence <new-evidence-dir>
# Restart persistence across two player processes:
python Tools\RestartScenario.py <player.exe> --evidence <new-dir>
# Expected-fault stream robustness (disconnect/reset/stalled sink):
python Tools\StreamRobustnessScenario.py <player.exe> --evidence <new-dir>
# Complete driving observation (both vehicles, reconstruction, capacity):
python Tools\DrivingScenario.py <dir>\connection.json --evidence <new-evidence-dir>
# Finish delivery to a stalled non-reading controller (expected-fault):
python Tools\FinishDeliveryScenario.py <player.exe> --evidence <new-dir>
# Real profile store + graceful exit (WM_CLOSE) + restart:
python Tools\NormalProfileScenario.py <player.exe> --evidence <new-dir>
# Complete external gameplay-loop qualification (schema 7): fresh
# profile -> tutorial -> campaign/quick-race/time-trial progression,
# real failed-save + retrySave, process-restart persistence, and a
# hostile-delivery LAN segment (kill/AI-takeover/rejoin, dedup,
# undeliverable departure, host loss). Every emitted trace is finished
# and re-verified by independent reconstruction; runs are honestly
# "partial" when the build binds no gameplay catalog (progression
# commands reject contentUnavailable and the rows stay notVerified).
# Attach to independently launched players, or self-launch:
python Tools\GameplayLoopScenario.py --connections <run>\connection.json [more descriptors] --scope smoke|campaign|lan --evidence <new-dir>
python Tools\GameplayLoopScenario.py --launch <player.exe> [--players 2-4] --scope smoke|campaign|lan --evidence <new-dir>
# Test-only fixture mode (--fixture) labels every trace and is never
# real-player evidence.
# Multi-instance LAN lifecycle over the real UDP transport (schema 7 —
# LAN protocol stays 3; the observation schema moved on).
# The mandated interface attaches to 2-4 independently launched players —
# the first connection is driven as the host; Android descriptors are
# per-device ADB-forwarded loopback endpoints:
python Tools\NetworkScenario.py --connections <host-run>\connection.json <client-run>\connection.json --evidence <new-dir>
# Optional --host-address <lan-ip> is the direct-join fallback when
# broadcast discovery cannot reach the host's subnet.
# Dev convenience: self-launch local players (adds the labeled
# process-kill fault-injection segment — real AI takeover, credentialed
# rejoin, host loss):
python Tools\NetworkScenario.py --launch <player.exe> [--players 2-4] [--no-fault-injection] --evidence <new-dir>
# Sustained real-time LAN soak — attaches the same way (first connection
# is the host) and repeats lobby→race→results→rematch cycles in wall-clock
# time for --minutes, sampling per-device health and boundary cadence;
# the once-only result/delivery ledger is checked across every rematch:
python Tools\NetworkSoak.py --connections <host-run>\connection.json <client-run>\connection.json --minutes 30 --evidence <new-dir> [--host-address <ip>]
# M4 visual-benchmark qualification on the Sunny Island course
# (schema 5). The mandated interface attaches to 2-4 independently
# launched benchmark players — the first connection is driven as the
# host; Android descriptors are per-device ADB-forwarded loopback
# endpoints. Empty seats fill through normal AI fill to four racers.
# The run is three labeled segments: solo tutorial/locale/volume phase
# per endpoint (before any hosting), the LAN lifecycle (both classes,
# both cameras, drift/boost/jump/recovery, shared pause, drop +
# reconnect path, avatar/color continuity, immutable results, labeled
# landmark screenshots, bounded pools, quiet teardown), then a separate
# performance segment accumulating --minutes of observed active racing
# per endpoint. The verdict demands the fixed floors (>=1,800 s racing
# per endpoint after the 60 s warm-up; every class/camera combination
# >=300 s) plus verified streams, rendered-frame evidence, a landed
# screenshot set, and honest loopback/AI-fill labels — a short
# --minutes run produces a shorter measurement and an honest failure.
python Tools\VisualBenchmarkScenario.py --connections <host-run>\connection.json <client-run>\connection.json [more descriptors] --evidence <new-dir> --minutes 30 [--host-address <ip>] [--allow-loopback] [--perf-report-dir <dir>]
# Dev convenience: self-launch local benchmark players (adds the
# labeled process-kill fault-injection segment, collects the quit-time
# perf reports under evidence, optionally forces --render-profile):
python Tools\VisualBenchmarkScenario.py --launch <player.exe> [--players 2-4] [--no-fault-injection] [--render-profile pc|tablet] --evidence <new-dir> --minutes 30
# Physical-backend qualification: attach to a launched
# Builds\Windows-Physics\FamilyRacing.exe validation run. Drives the
# authored physics-01 event on both physical vehicles through the same
# command path — acceleration/coast/braking, grip, ordered gates,
# recovery epochs, diagnostics (two 1/60 s substeps per 30 Hz tick,
# live body count, invalid-state faults), the ordered substep path and
# the full wheel-array physics state on the stream — then verifies the
# whole trace by independent reconstruction:
python Tools\PhysicsScenario.py <dir>\connection.json --evidence <new-evidence-dir>
# Sustained physical LAN soak: attaches to 2-4 independently launched
# physical players (first connection is driven as the host), repeats
# physics-01 lobby->race->results->rematch cycles in wall-clock time,
# samples physical state/diagnostics/fingerprints/recovery epochs per
# peer, checks the compact wire projection never leaks the full
# diagnostic slice, and verifies each trace — a lost peer is an
# incident, never silently replaced:
python Tools\PhysicsSoak.py --connections <host-run>\connection.json <client-run>\connection.json ... --minutes 30 --evidence <new-dir> [--host-address <ip>]
```

Proven recipe (wave-3 build, 2026-10-11): preview at
`Logs\run-validation-lead` (`connection.json` → port + token), scenario
passed all 9 checks — frozen evidence `Logs\evidence-lead\result.json`.
The player stays alive after `finish` for inspection, while the launch
terminal remains alive. The command/state capture is then closed; start a
new player with a fresh validation directory for another scenario.

## Wire protocol (protocolVersion 1)

Client → server frames, one JSON object per line, max 16 KiB, depth ≤ 16,
no duplicate keys, no trailing content, strict UTF-8:

- `{"type":"hello","protocolVersion":1,"runId":...,"token":...}` — first
  frame within a 5-second total deadline; bad/missing auth is rejected.
- `{"type":"command","protocolVersion":1,"runId":...,"commandId":...,"participantId":"local","action":...,"parameters":{...}}`
- `{"type":"discover"|"snapshot"|"health"|"finish"}`, `{"type":"lookup","commandId":...}`,
  `{"type":"screenshot","captureId":...}` — `captureId` is `[A-Za-z0-9_-]{1,64}`;
  the PNG lands at `<run>/screenshots/screenshot-<captureId>.png` and a
  sequenced `screenshot` record reports `status` `captured`, `failed`, or
  `rejected` (invalid ids and queue overflow are refused with a `code`,
  never an unsequenced error).

Actions: `profile.create`, `profile.select`, `ui.navigate`, `ui.language`,
`settings.update`, `settings.save`, `input.set`, `input.clear`,
`camera.toggle`, `pause.toggle`, `app.focusLost`, `race.start`,
`race.restart`,
`race.exit`, `tutorial.start`, `tutorial.replay`, `tutorial.exit`,
`solo.start`, `upgrade.purchase`, `cosmetic.equip`, and the
M5 gameplay-loop surface `campaign.open`, `campaign.start`,
`quickRace.open`, `quickRace.start`, `timeTrial.open`, `timeTrial.start`,
`garage.open`, `bounties.open`, `results.retrySave`, `results.retry`,
`results.continue`, plus the
LAN surface `lan.host`, `lan.discover`, `lan.discovery.stop`, `lan.join`,
`lan.configure`, `lan.vehicle`, `lan.ready`, `lan.start`,
`lan.pause.request`, `lan.pause.set`, `lan.reconnect`,
`lan.results.request`, `lan.results.retrySave`, `lan.rematch`,
`lan.leave`.

`app.focusLost` runs the same input-cleanup path an OS focus loss
dispatches: `FoundationService.ApplicationFocusLost` clears held input
and queued driving actions and invokes the rearm hook wired to
`FoundationInput.RequireRearm`. `ObserveInputCleanup`
(`OnApplicationFocus`/`OnApplicationPause`) dispatches this command
itself, so the agent command and a real focus/pause event exercise one
code path; it is deliberately unblocked inside LAN sessions and solo
races.

`race.start` takes `vehicleId` + `eventId` (catalog: `car-01`,
`motorcycle-01`, `practice-02`, `intro-01`; the `tutorial-01` free-drive
stage is internal and `race.start` rejects it with `unavailable` — only
`tutorial.start` may open it) and needs a selected profile;
`race.restart`/`race.exit` need an active race and take no parameters.
A vehicle stopped on valid flat ground for `beachedSeconds` (default 8 s)
auto-recovers — `race.recovered` detail reports `"beached"` versus the
wall-pin `"stuck"` and the input-initiated `"manual"`.
`input.set` is available on `sandbox`, `driving`, and `lan-race` screens
only; a `gadget` sample is rejected with `unavailable` while a race is
active. Two optional held demands extend the sample: `brake` (decimal,
`[0,1]`) slows the vehicle without reversing it, and `throttle`
(decimal, `[0,1]`) overrides the automatic acceleration — an omitted
`throttle` keeps automatic throttle (effective `1`), while an explicit
`0` is a deliberate coast. Malformed or out-of-range `throttle`/`brake`
values reject the whole sample atomically before any state changes.
On the LAN wire these ride `NetworkInputFrame.brake`,
`hasThrottleOverride`, and `throttle`; the host rejects non-finite or
out-of-range demands (`nonFiniteInput`/`outOfRangeInput`) before a
sample is accepted, and a frame from an older peer without the new
fields decodes as automatic throttle with no brake.
Holding `boost` auto-fires: whenever `boostCharge` refills past
`boostMinCharge` a new burst begins without a release (the meter drains
in bursts as it refills); `driving.motor.boostLatched` reports whether
a boost has fired since the last release.
While paused `input.set` still applies: a `recover` sample latches into
`driving.pendingActions` (observable in state). It is consumed at the
first simulated boundary after resume when resume is an `input.set
pause=true` sample; `pause.toggle` resume cancels queued actions instead
(observable via `action.cancelled`).

`tutorial.start` takes `vehicleId` and needs a selected profile plus
validated tutorial content; it opens the ordered onboarding (steering →
brake → drift → boost → jump → recovery lessons inside the free-drive
event, then the unlocked one-lap `intro-01` trial). `tutorial.replay`
re-announces the current cue without any progression; `tutorial.exit`
abandons the session. There is intentionally no `tutorial.complete` or
checkpoint-teleport action — lessons and the graduation lap advance only
from genuine driving events (`motor.driftStart`, `motor.boostStart`,
`motor.landed`, `race.recovered`, `race.finished`, held-steering and
held-brake simulated boundaries). `race.restart` inside a tutorial retries the current stage
without losing completed lessons; `race.exit` and profile switches end
the session-local tutorial state. `driving.tutorial` in the state slice
is `null` unless a tutorial is active; when active it is a `TutorialState`
object — `phase` (`lessons`/`trial`/`graduated`), `lessonId`,
`lessonIndex`/`lessonTotal`, `completedLessons`, the current cue
(`objective`, `icon`, `cueId`, `cueSequence`) and the bundled clip
identities `audioEn`/`audioHe` (empty until voice clips ship).
`featureCoverage.tutorial` stays
`notImplemented` (the full-release tutorial incl. gadget onboarding is
deferred) while `featureCoverage.drivingOnboarding` reports this slice.
`tutorial.exit` inside an active tutorial abandons the session and its
result detail reports `tutorialExit`; with no tutorial running the same
command reports `noActiveTutorial`.

`driving.checkpoints` is the ordered gate list for the active event:
index 0 is the start/finish line and racing gates begin at index 1;
`driving.nextCheckpoint` names the next un-crossed index.
Vehicle tuning has a deliberate dual source: `VehicleTuning` presets in
Core are the authored truth (used by tests and setup); generated
`VehicleDefinitionAsset` YAML is the runtime mirror, re-serialized from
the presets on every `Verify.ps1 -Mode Setup` and bound by an
asset↔preset parity test.

Sequenced server records carry `protocolVersion`, `runId`, `deviceId`
(random per run), `participantId`, `buildVersion`, `contentVersion`,
contiguous `sequence`, `monotonicMs`, `tick`: `snapshot`, `state`,
`delta`, `result`, `discovery`, `lookup`, `health`, `screenshot`,
`finished`.
`snapshot`/`state` records carry a complete `state` payload and
`stateSchemaVersion` (currently `7`: on top of the schema-2 `driving`
slice the state adds `network` (session role/phase, roster, shared pause,
pending operations, discovered hosts, result deliveries — always
populated, idle/none without a session), `networkRace` (host-confirmed
shared race facts; null outside a LAN race), `prediction` (client-side
local prediction facts; null on the host and outside a LAN race) and
`remotePresentation` (per-remote interpolation diagnostics); schema 4 adds
the protocol-2 surfaces — the session descriptor's `contentFingerprint`,
a `compat` token on every discovered host, and per-racer physical state
inside LAN race snapshots; schema 5 adds the `presentation` render
profile/performance slice; schema 6 adds `soloRace` (the local-mode
session — `race` for campaign/quickRace, `trial` for timeTrial, the
finalized `result`, and `tuningSource` naming the human seat's tune
provenance) and `progression` (the resolved campaign access rows, bounty
tray and unresolved-reward gate — present on every boundary, menus and
failed saves included); schema 7 adds `progression.worlds` (every
authored campaign world in order — bilingual names, availability,
`lockedReason`, regional completion/star totals, finale counts,
completion reward vehicles, `worldComplete` and the `selected` marker)
plus `progression.adventureComplete`, and retargets
`progression.campaignWorldId`/`campaignEvents` at the SELECTED world));
consumers
must reject unsupported schemas before trusting driving evidence. The
`hello` frame, `connection.json` and `discovery` records all advertise
the same `stateSchemaVersion`.
Tick and command boundaries emit as `delta` records: `base` names the
previous boundary's `sequence`, `changed` is a recursive object merge
(scalars/arrays/nulls replace wholesale), and `removed` lists dot-paths
absent from the new state. A `delta` is never a complete state on its own
— an independent consumer reconstructs by applying it to the named base
boundary; a wrong or missing base, unresolved removed path, or
non-contiguous sequence fails reconstruction. Complete snapshots are still
emitted for attach and explicit `snapshot` resynchronization.
`droppedDrivingEvents` in the state counts events evicted by the bounded
`drivingEvents` slice — eviction is never silent.
`driving.motor` serializes the full `MotorState`, including `airPeakY`
(the highest y of the current airborne episode; `0` while grounded) —
it backs the `landingMinDrop` clean-landing check and lets observers
distinguish a real jump from a probe outage.
On the physical backend the driving session slice additionally carries
full observability: `driving.simulationBackend`
(`"arcade"`/`"physicalAssisted"`), `driving.contentFingerprint` (the
64-hex canonical tuning fingerprint every LAN peer must agree on
before a race starts), `driving.lastRecoveryReason` (the most recent
recovery cause, suffixed with `#<recoveryEpoch>` for physical
resets), `driving.substepPath` (the ordered three-pose path through
the tick's two fixed 1/60 s substeps — gate evaluation follows the
segments, not the end-to-end chord), `driving.simulation` (world
diagnostics: `backend`, `ticksSimulated`, `substepsSimulated`,
`bodies`, `lastStepSeconds`, `totalStepSeconds`,
`invalidStateFaults`), and `driving.motor.physics` (the complete
`VehiclePhysicsState` — attitude quaternion, velocities, controller
histories, recovery epoch and the index-ordered wheel array with
contact/load/slip diagnostics). `substepPath`, `simulation` and the
diagnostic-only physics fields (contact points/normals/loads/slips)
are **stream-only**: the compact LAN snapshot carries the small
identity strings plus the compact `racer.physics` wire DTO, and
`MotorState.physics` is stripped before encode and rebuilt from that
DTO on decode — full diagnostics never cross the gameplay wire.
`contentVersion` now travels independently of `buildVersion` and names the
driving content catalog (`driving-0.1`). The trace opens with
a `snapshot` at sequence 1; an authenticated controller's first live frame
is a fresh snapshot at the current sequence. Records emitted while no
controller is attached are trace-only; queued records are never retargeted
to a newer session.

Integrity rules: unintended disconnect, outgoing-queue overflow, trace
limit (256 MiB) or sink failure invalidate the capture and clear agent
input; after `finish` the run is closed (`captureClosed` rejects new
controllers/commands); `capture-status.json` reports `captureComplete`
only after the trace is flushed and the final status stamped.

## Gameplay loop commands and screens (schema 7)

The M5 loop surface is command-first: every screen the family UI shows
is reachable for an agent through the same `FoundationService.Execute`
path, and no command grants coins, stars or traversal — durable
progression only ever lands through a finalized race result or an
authored purchase/equip.

- `campaign.open`, `quickRace.open`, `timeTrial.open`, `garage.open`,
  `bounties.open` are the ONLY route to their screens (`campaign`,
  `quickRace`, `timeTrial`, `garage`, `bounties`): `ui.navigate` cannot
  target them, each open checks gameplay content + a selected profile,
  and every open rejects `raceActive` while a race runs or
  `networkActive` inside a LAN session.
- `campaign.selectWorld {worldId}` (M6 Task 3) moves the transient
  campaign-world pick: `unknownWorld` for an id no authored world owns,
  `worldLocked` for a world the profile has not unlocked — both reject
  without touching the current selection. The pick is session state,
  never persisted: profile switches re-derive it (the first accessible
  unfinished world, or the last world once the whole adventure is
  complete), it stays legal while paused, and a LAN session fences it
  with `networkActive` like every other solo-lifecycle command. The
  pick decides which world `progression.campaignWorldId` and
  `campaignEvents` describe — it never gates event starts.
- `campaign.start {eventId, vehicleId, difficulty}` enforces the
  campaign traversal gate against the event's OWNING world
  (`worldLocked` while that world is closed — independent of the
  selected world, `eventLocked` on unavailable regulars and
  finales) and profile vehicle ownership (`vehicleLocked`).
  `results.continue` from a campaign result re-selects the world the
  raced event belongs to so the map returns to the raced context.
- `quickRace.start {courseId, classId, vehicleId, difficulty}` resolves
  the authored circuit event for the course+class pick — a pick with no
  authored circuit rejects `unknownEvent`.
- `timeTrial.start {courseId, classId}` seats the authored normalized
  starter for the class (`soloRace.tuningSource` reports `normalized`
  versus `purchased` for the upgrade-resolved seats).
- All three funnel into the same `SoloRaceSession` path `solo.start`
  uses: progression modes bind the durable reward context before the
  first simulated boundary, aborted runs produce no result, and the
  finalized result feeds the same exactly-once reward seam LAN uses.
- `upgrade.purchase {purchaseId, vehicleId, category, expectedLevel}`
  commits the authored-priced level atomically (receipt-keyed,
  idempotent on replay); `cosmetic.equip {slot, cosmeticId, equipped?}`
  requires `slot` to match the cosmetic's authored slot prefix and
  defaults `equipped` to true. Only `upgrade.purchase` rejects
  `rewardUnresolved` while a result's save is pending — an equip carries
  no progression reward facts, so it stays legal alongside the retained
  result+context pair.
- `results.retrySave` replays the retained result+context pair after a
  failed save (`noPendingResult` when nothing is owed);
  `results.retry` replays the finished session's stored request under a
  fresh race identity; `results.continue` retires the session back to
  the mode's own setup screen. `results.retry`/`results.continue`
  reject `unavailable` without a finished result.

Loop screens reject `raceActive`/`networkActive` during a race or LAN
session; progression writes (`upgrade.purchase`, `cosmetic.equip`) are
additionally fenced off a running solo race.

## LAN commands and screens (schema 7)

The five LAN screens are `lan-browser` (the only user-navigable LAN
screen — a `ui.navigate` target), plus the phase-derived `lan-lobby`,
`lan-race`, `lan-results` and `lan-recovery`, which the session phase
selects automatically. Solo-only actions (`profile.create`,
`profile.select`, `race.*`, `tutorial.*`) reject with `networkActive`
inside a session; LAN entry (`lan.host`/`lan.join`/`lan.reconnect`)
rejects with `raceActive` during a solo race; navigating away from a
session other than to `lan-browser` rejects `networkActive` — leave with
`lan.leave`.

Async commands (`lan.join`, `lan.reconnect`, and every client-side
request: `lan.ready`, `lan.vehicle`, `lan.pause.request`,
`lan.results.request`, `lan.results.retrySave`) return `accepted`
immediately and complete exactly once as a terminal `result` record
(`applied`/`rejected`/`failed`) on a later boundary; `pendingOperations`
in the `network` slice exposes each in-flight command id. Host-side
commands (`lan.configure`, `lan.start`, `lan.pause.set`, `lan.rematch`)
apply synchronously; on a client they reject `hostOnly`.

Parameters: `lan.host {port?}` (0 = ephemeral; the bound port is
observable on `network.descriptor.hostPort`), `lan.join
{address, port}`, `lan.configure {eventId, classId}` (LAN events are the
`lan-*` catalog ids), `lan.vehicle {vehicleId}`, `lan.ready {ready}`,
`lan.pause.set {paused}`, `lan.results.request {raceId?}`.
`pause.toggle` inside a session is a host pause toggle / client pause
request (observed via `network.paused` and `network.pauseRequests`).
On `lan-race`, `input.set` carries the bounded held sample; a `recover`
sample becomes a deduplicated one-shot action on the replication bridge
whose action id is the command's identity — `action.queued` /
`action.consumed` / `action.cancelled` events acknowledge it on
`drivingEvents`, and pause/disconnect/finalization cancel it honestly.

Availability reason codes surfaced to disabled controls include
`inSession`, `requiresSelectedProfile`, `hostOnly`, `notReady`,
`notConfigured`, `noSession`, `joinPending`, `pendingFull`,
`lobbyFull`, `incompatible`, `alreadyDiscovering`, `notDiscovering`,
`requestTimeout`, `sessionFull`, `hostClosed`, `hostLost`,
`invalidVehicle`, `invalidClass`, `connectFailed`, `joinTimeout`,
`duplicatePending`, `missingParameter`, `invalidParameter`,
`sendFailed`, `raceActive`, `networkActive`. Reconnect credentials and
transport identities never appear in observable state.
