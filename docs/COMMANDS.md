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

Validation launches set `Application.runInBackground = true` so agents do
not depend on window focus; ordinary launches keep focus-pause.

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
`camera.toggle`, `pause.toggle`, `race.start`, `race.restart`,
`race.exit`, `tutorial.start`, `tutorial.replay`, `tutorial.exit`.

`race.start` takes `vehicleId` + `eventId` (catalog: `car-01`,
`motorcycle-01`, `practice-02`, `intro-01`; the `tutorial-01` free-drive
stage is internal and `race.start` rejects it with `unavailable` — only
`tutorial.start` may open it) and needs a selected profile;
`race.restart`/`race.exit` need an active race and take no parameters.
`input.set` is available on `sandbox` and `driving` screens only; a
`gadget` sample is rejected with `unavailable` while a race is active.
While paused `input.set` still applies: a `recover` sample latches into
`driving.pendingActions` (observable in state). It is consumed at the
first simulated boundary after resume when resume is an `input.set
pause=true` sample; `pause.toggle` resume cancels queued actions instead
(observable via `action.cancelled`).

`tutorial.start` takes `vehicleId` and needs a selected profile plus
validated tutorial content; it opens the ordered onboarding (steering →
drift → boost → jump → recovery lessons inside the free-drive event,
then the unlocked one-lap `intro-01` trial). `tutorial.replay`
re-announces the current cue without any progression; `tutorial.exit`
abandons the session. There is intentionally no `tutorial.complete` or
checkpoint-teleport action — lessons and the graduation lap advance only
from genuine driving events (`motor.driftStart`, `motor.boostStart`,
`motor.landed`, `race.recovered`, `race.finished`, held-steering
boundaries). `race.restart` inside a tutorial retries the current stage
without losing completed lessons; `race.exit` and profile switches end
the session-local tutorial state. `driving.tutorial` in the state slice
is `null` unless a tutorial is active; `featureCoverage.tutorial` stays
`notImplemented` (the full-release tutorial incl. gadget onboarding is
deferred) while `featureCoverage.drivingOnboarding` reports this slice.

Sequenced server records carry `protocolVersion`, `runId`, `deviceId`
(random per run), `participantId`, `buildVersion`, `contentVersion`,
contiguous `sequence`, `monotonicMs`, `tick`: `snapshot`, `state`,
`delta`, `result`, `discovery`, `lookup`, `health`, `screenshot`,
`finished`.
`snapshot`/`state` records carry a complete `state` payload and
`stateSchemaVersion` (currently `2`: the state adds the `driving` session
slice, `drivingEvents`, and `driving.simulationTick` alongside the
observation `tick`); consumers must reject unsupported schemas before
trusting driving evidence.
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
