# Visual Benchmark Runbook

Milestone 4 builds a separate "Sunny Island" visual-benchmark scene on the
proven `graybox-01` course under content version `visual-1`, measured on an
A7 Lite tablet and a Windows PC. This runbook is the reproducible command
surface for the benchmark content: setup, all four build flavors, and the
evidence each build must record. Spec authority: `GAME_PLAN.md` §2–5;
milestone plan: `docs/superpowers/plans/2026-10-13-visual-benchmark.md`.

## Baseline record (Task 1, recorded 2026-10-13)

| Fact | Value |
|---|---|
| Branch | `fix/driving-playtest-1` |
| Baseline commit | `25d39d416d300cd902ee7ee897a0b9ecce3ad455` (`25d39d4`) |
| Working tree at task start | **clean** — `git status --porcelain` empty; zero dirty files, so no dirty-file hashes exist to record |
| Physics qualification source edits | **retained and committed** — the physics qualification work (commits through `def1b78`/`e67fde7`/`19c332c`) is entirely inside baseline `25d39d4`. Controller ruling: preserve the tree as-is; nothing was reset or reverted |
| Pinned editor | Unity `6000.3.26f1` at `C:\Program Files\Unity\Hub\Editor` |
| Render pipeline | URP 17.3.0, render scale 0.75 (enforced by `FoundationProjectSetup.EnsureUrp`) |

### Known gate outcomes and failures carried into M4

Source: `docs/tasks/vehicle-physics-report.md` and `docs/NETWORK_TEST_RUNBOOK.md`.

- EditMode at baseline report time: 652/653 passed, 1 skipped
  (`SkippedCheckpointCannotFinish`, pre-existing skip).
- PlayMode: 245/247 passed, 2 environment-blocked failures —
  `NetworkPredictionPlayTests.ImpairedLinkExercisesPredictionAndInterpolation`
  and `PhysicsPredictionPlayTests.ImpairedLanPhysicalCorrections`
  (impaired UTP session never establishes in this environment; not product
  failures).
- Open gates (all `notVerified`): impaired-LAN correction targets,
  cross-device LAN reachability, A7 Lite device matrix, ordinary-build
  performance, human handling acceptance, rendered frame times, Android
  on-device behavior, IL2CPP Windows build, 3-consecutive-lap solo
  acceptance, solo motorcycle rollover episode.
- Ordinary Windows precedent (2026-10-13): ordinary player launched with
  `--validation-dir` produced no `connection.json`, no token dir, no
  listening validation socket (`Logs/run-t12-ordinary-check*`).
- `ps4Passcode` auto-fill in `ProjectSettings.asset` is a secret-like
  value: never committed; revert the hunk if Unity re-adds it.

## Commands

All Unity modes use the pinned editor and write logs under `Logs\`.

| Command | Effect |
|---|---|
| `Tools\Verify.ps1 -Mode VisualBenchmarkSetup` | Run `FoundationProjectSetup` then `VisualBenchmarkSetup.Create` — creates missing benchmark scaffolding and validates references |
| `Tools\Verify.ps1 -Mode WindowsVisualBenchmark` | Windows Mono **development** player of `Generated/VisualBenchmark/VisualBenchmark.unity` → `Builds\Windows-VisualBenchmark\` |
| `Tools\Verify.ps1 -Mode WindowsVisualBenchmarkOrdinary` | Windows Mono **ordinary** player (no `DEVELOPMENT_BUILD`; validation endpoints compiled out) → `Builds\Windows-VisualBenchmark-Ordinary\` |
| `Tools\Verify.ps1 -Mode AndroidVisualBenchmark` | Export benchmark Gradle project + offline Gradle build → `Builds\Android-VisualBenchmark\` |
| `Tools\Verify.ps1 -Mode AndroidVisualBenchmarkOrdinary` | Same with the ordinary export entry point (no development flag) → `Builds\Android-VisualBenchmark-Ordinary\` |

The Android wrapper also composes directly:
`Tools\Build-AndroidOffline.ps1 -Presentation Graybox|VisualBenchmark -Configuration Development|Ordinary`.
Defaults are `Graybox`/`Development`, preserving the pre-M4 behavior of
`-Mode Android` (`Builds\Android\FamilyRacing.apk`). `Graybox`+`Ordinary`
maps to `Builds\Android-Ordinary\` (the previously undocumented ordinary
Android path from `docs/NETWORK_TEST_RUNBOOK.md`, now automated).

### What setup guarantees

`VisualBenchmarkSetup.Create()` is repeatable and non-destructive:

- Creates `Assets/FamilyRacing/Generated/VisualBenchmark/`,
  `visual-benchmark-01.asset` (`VisualBenchmarkDefinitionAsset`) and
  `VisualBenchmark.unity` only when missing.
- The definition carries `presentationVersion = "visual-1"`, the visible
  `courseVisualPrefab`, and `sourceTrack` (the shared `graybox-01` track —
  course, gates, route and recovery rules stay the single gameplay source).
- Repairs only missing/mismatched contract references on the scene's
  `DrivingWorld` (vehicles, track, events, tutorial, `coursePrefab`,
  `contentVersion`). It never regenerates authored meshes, materials,
  scene dressing or baked lighting — Task 3 owns those.
- Throws `InvalidDataException` when a required reference cannot be
  resolved, so builds fail honestly instead of producing partial content.

## Build evidence

> Superseded: the hash tables below record the first produced artifacts.
> The **final same-source manifests** produced on the Task-7 closing
> revision (`99f4dcf` + the snapshot-guard fix committed with this
> closeout) are in "Task 7 — closeout" at the bottom of this runbook
> and in `docs/tasks/visual-benchmark-report.md`.

Produced 2026-10-13/14 from the working tree at baseline `25d39d4` plus
the uncommitted Task-1 slice (files listed in the task report). Every
flavor builds `Generated/VisualBenchmark/VisualBenchmark.unity` only —
the graybox `Foundation.unity` stays the default player scene.

| Identity | Value |
|---|---|
| Scene | `Assets/FamilyRacing/Generated/VisualBenchmark/VisualBenchmark.unity` |
| Content version | `visual-1` (player `connection.json` confirms) |
| Track | `graybox-01` (`Generated/Driving/graybox-01.asset`) — shared, not copied |
| Application id | `com.daniel.familyracing` (both APKs; unchanged from ProjectSettings) |
| Save compatibility | unchanged — no PlayerSettings/profile schema changes in this slice |

### `Builds\Windows-VisualBenchmark\` (Mono, development)

| Item | Value |
|---|---|
| `FamilyRacing.exe` SHA256 | `B7CCA8B82003185F22644D0EE1C23B3640DE1B996802D4F5D0501E287011AFA6` |
| `FamilyRacing.Core.dll` SHA256 | `AD07C53E8A1CE755E59BA8E23C9FB56485EBC74281E951928D2D2010CA4C12F9` |
| `FamilyRacing.Runtime.dll` SHA256 | `EBC5B860D2B66BDD1CB3FDAC6D309DC79072B20EAABC346BA984996223C898A3` |
| `level0` (scene content) SHA256 | `5229CCA9A139CF6F86FA2FA8F97DE58D9D163CC9A8E3A23811DAD6BB4C5A947D` |
| Build result | `result=Succeeded errors=0` (~237 MB) |

### `Builds\Windows-VisualBenchmark-Ordinary\` (Mono, ordinary)

| Item | Value |
|---|---|
| `FamilyRacing.exe` SHA256 | `EB143EAD6441B6DD9D7D145ADB85410E9389FB3878899AE537E8C9869CCE38FA` |
| `FamilyRacing.Core.dll` SHA256 | `9B58B3095E04C7886B8973D2E664C7FB81244C642C780E4174458FECF7FF44FC` |
| `FamilyRacing.Runtime.dll` SHA256 | `7FDAD6E7F63D1692549657117D440D279FB60FC40CE64BBE87E48133DF44DF78` |
| `level0` (scene content) SHA256 | `10C2FE6503744E3799497A0F5727FFA5C4495AD5C5693F7502B61A94BBD61606` |
| Build result | `result=Succeeded errors=0` (~168 MB) |

### `Builds\Android-VisualBenchmark\FamilyRacing.apk` (IL2CPP, development)

| Item | Value |
|---|---|
| APK SHA256 | `E89EAC5D75883DA553952CF65D6D4B1EF9E75037BAF38FEAD360967EA08A6072` |
| Size | 51,314,971 bytes |
| Export entry point | `FoundationBuild.ExportAndroidVisualBenchmark` |
| Evidence | `Logs\android-offline\20261013-214852-679d1fc30429446e96c0c7e9f282de7c\result.json` |

### `Builds\Android-VisualBenchmark-Ordinary\FamilyRacing.apk` (IL2CPP, ordinary)

| Item | Value |
|---|---|
| APK SHA256 | `DA8C47CFFBC578EF8C9838841865335101AFC230E978124D253C692EEAA97E02` |
| Size | 45,316,579 bytes |
| Export entry point | `FoundationBuild.ExportAndroidVisualBenchmarkOrdinary` |
| Evidence | `Logs\android-offline\20261013-215330-a69ecdcc350b4107bec3b60ea287d769\result.json` |

### Validation-endpoint absence, observed 2026-10-14

- Ordinary player launched 25 s with `--validation-dir <new-empty-dir>`:
  directory stayed **empty** — no `connection.json`, no `profiles\`, no
  trace files — and `netstat` showed **zero listeners** owned by the
  process (`Logs\vb-ordinary-check\val-ordinary`, run log
  `ordinary-player.log`).
- Positive control, same steps against the development player:
  `connection.json` (`"contentVersion":"visual-1"`, protocol 1,
  schema 4), `profiles\`, `capture-status.json`, `state.ndjson`, and a
  `127.0.0.1` validation listener all appeared — the check discriminates.
- Android ordinary: `ExportAndroidVisualBenchmarkOrdinary` omits
  `BuildOptions.Development`, so `DEVELOPMENT_BUILD` is undefined and the
  bridge/listener block compiles out (same mechanism as the ordinary
  Windows player). Device-side confirmation is `notVerified` until the
  A7 Lite is available.

### Windows-path note (fixed in Task 1)

CMake/ninja enforce the 260-char path limit; `Android-VisualBenchmark-Ordinary`'s
`Builds\<flavor>\offline\<runId>\gradle\unityLibrary\.cxx\...` tree exceeded it.
The wrapper now runs Gradle through a short TEMP junction
(`gradleProjectDir` in `result.json`); artifacts still land under the
mandated `Builds\<flavor>\offline\<runId>\gradle` export root.

## Ordinary-build validation absence

Ordinary builds must never open a validation endpoint. Check, per player:

```
$dir = "<new-empty-dir>"
& "Builds\Windows-VisualBenchmark-Ordinary\FamilyRacing.exe" --validation-dir $dir
# after ~20 s: $dir must contain no connection.json and no listener must
# appear; the player log records "--validation-dir ignored ... disabled".
```

A development build run through the same steps must produce
`connection.json` — the positive control proving the check discriminates.
Agent runs on development players should use isolated synthetic profiles:
`--validation-dir <dir> --validation-profile-dir <dir>\profiles`
(the default is already `<dir>\profiles`; never pass the real user store
except through the explicit `--validation-normal-profiles` opt-in).

Android ordinary cannot be driven this way without a device; its check is
the export entry point (`ExportAndroidVisualBenchmarkOrdinary` omits
`BuildOptions.Development`, so `DEVELOPMENT_BUILD` is undefined and the
bridge/listener code compiles out) plus a device-side check when hardware
is available — recorded as `notVerified` until then.

## Task-1 verification results (2026-10-14)

| Check | Result |
|---|---|
| `Verify.ps1 -Mode VisualBenchmarkSetup` | clean run; scene wiring matches `Foundation.unity` exactly except `contentVersion: visual-1` |
| `Verify.ps1 -Mode EditMode` | 654/655 passed, 0 failed, 1 skipped (`EveryCueHasWiredClipForBothLocales`, pre-existing `[Ignore]` — voice clips pending `AZURE_SPEECH_KEY`) |
| `Tools\Test-VerifyTooling.ps1` | 18/18 passed |
| `Tools\Test-AndroidOffline.ps1` | 30/30 passed |
| `SetupTwicePreservesAuthoredAssets` / `BenchmarkSceneUsesExistingRules` | 2/2 passed — same GUIDs and byte-identical files across re-run; identical rules under `visual-1` |
| All four build flavors | succeeded (hashes above) |
| Ordinary validation absence | verified on Windows player; Android compiles-out by construction, device check open |

Not run (environment): PlayMode suite, A7 Lite device matrix, rendered
frame capture, on-device Android launch. The two impaired-LAN PlayMode
failures carried from the physics report remain environment-blocked.

## Task 2 — render profiles and truthful measurements (2026-10-14)

Two authored URP profiles live under `Generated/VisualBenchmark/` and are
selected automatically by the scene's `RenderProfileController`:

| Profile | Selection | Caps |
|---|---|---|
| `VisualBenchmarkTabletURP` | Android (or `--render-profile tablet`) | 30 fps target, 0.75 render scale, 1 shadow cascade / 30 m hard shadows, no HDR, 1x MSAA |
| `VisualBenchmarkPcURP` | Windows/other platforms (or `--render-profile pc`) | 60 fps target, full render scale, 2 cascades / 80 m soft shadows, HDR, 2x MSAA |

Neither profile enables real-time reflections, motion blur, camera
depth/opaque grabs or other expensive full-screen effects; UI Toolkit
renders at display resolution regardless of internal render scale.
`TabletAndPcProfilesKeepCollisionEqual` pins that a profile change never
alters collision layers, gravity, solver budgets, checkpoints or route
visibility — the profiles own presentation settings only.

### Player measurement arguments (development AND ordinary builds)

| Argument | Effect |
|---|---|
| `--render-profile tablet\|pc` | Force the profile (benchmark evidence; otherwise platform-selected) |
| `--perf-capture off` | Disable the frame recorder — the matched no-capture arm for capture-overhead A/B runs |
| `--perf-report-dir <dir>` | Report destination (default `persistentDataPath\performance`) |

At quit the player writes compact `perf-report-<runId>.json` on a
background writer (bounded 4 MB, failure-visible via `reportStatus`/
`reportError` and, in development builds, a sequenced `performance`
stream record). Ordinary builds produce the same report with no
validation socket. The schema-5 presentation slice publishes
`renderProfileId`, `renderScale`, `targetFrameRate`,
`renderingAvailable` and a bounded `performance` summary on every state
boundary — headless runs report `renderingAvailable: false` and can
never pass evaluation.

### External process-memory sampling

Process memory is measured **outside** the player once per second —
never Unity heap allocations:

```powershell
# Windows player (working set via psapi GetProcessMemoryInfo):
python Tools\PerfSampler.py --pid <player-pid> --evidence <new-dir> [--seconds N]
# Android player (app total PSS via `adb dumpsys meminfo`):
python Tools\PerfSampler.py --adb-serial <serial> [--package com.daniel.familyracing] --evidence <new-dir> [--seconds N]
```

Every `memory.ndjson` record names its `metric` (`process.workingSet`
vs `process.totalPss`), `source` (psapi vs adb dumpsys) and `units`
(bytes); a failed sample is `status: "unavailable"`, never zero. The
two metrics are differently-defined quantities — each device compares
only against its own baseline, never platform-to-platform. GPU timing
is a separate quantity from CPU frame intervals and reports
`unavailable` on this profile (no GPU timer exposed); socket cadence is
likewise never substituted for rendered frame intervals.

### Task-2 verification results

| Check | Result |
|---|---|
| `Verify.ps1 -Mode CoreOffline` | 599/599 passed |
| `Verify.ps1 -Mode EditMode` | 659 passed, 0 failed, 1 pre-existing skip |
| `Verify.ps1 -Mode PlayMode` | 245/247 passed — 2 documented environment-blocked impaired-LAN timeouts (unchanged signature) |
| Focused PlayMode `RenderProfilePlayTests` | 3/3 passed |
| Python scenario tooling suites | 118 tests passed (incl. new schema-5 guards + `TestPerfSampler`) |
| Rendered Windows dev sample (~90 s race) | `perf-report-*.json`: 5,415 frames, 1,834 racing samples, median/p95/max 16.67/16.67/16.75 ms, 0 stalls, `evidenceStatus: collected`, `profile: pc` |
| Headless sample (`-batchmode -nographics`) | `renderingAvailable: false` — cannot pass |
| Ordinary player (15 s) | report written (`buildIdentity: 0.1.0-ordinary`); `--validation-dir` stayed empty — no socket |
| External memory sampler | `memory.ndjson`: 90 samples @ 1 Hz, `process.workingSet`/psapi/bytes, ~387→527 MB |

Pending: A7 Lite device qualification (tablet profile, PSS baseline,
p95 ≤40 ms), quantified capture-overhead A/B, on-device Android launch.

## Task 3 — Sunny Island course environment (2026-10-14)

The benchmark's `courseVisualPrefab` now binds the authored
`sunny-island-benchmark-01` prefab under
`Assets/FamilyRacing/Art/VisualBenchmark/Environment/` (stable visual-root
ID = root name). The prefab nests the shared `GrayboxCourse` prefab
instance — the only collision in the hierarchy — so gameplay geometry
stays byte-authoritative while every renderer is dressed. Dressing adds
zero colliders and zero rigidbodies; tall occluder-class objects sit on
the built-in Ignore Raycast layer (2), invisible to motor/wheel probes
but still camera-occlusion raycastable. Warm sun, trilight ambient,
turquoise haze and the procedural skybox live in `VisualBenchmark.unity`
(scene-authored, migrated once from the Task-1 scaffold values);
`PresentationState.courseVisualId` publishes the bound visual root.

Regenerate only when the prefab is absent: `VisualBenchmarkSetup.Create()`
calls `SunnyIslandCourseAuthoring.Ensure()` — hand-authored tweaks on top
survive every setup re-run (`SetupRerunKeepsSunnyIslandArt`).

### Task-3 verification results

| Check | Result |
|---|---|
| `Verify.ps1 -Mode EditMode` | 662 passed / 663 total, 0 failed, 1 pre-existing skip — incl. `DressingPreservesGameplayGeometry` (all 16 colliders byte-identical, dressing adds none), `AllReferencedAssetsHaveInventoryEntries` (26 authored assets inventoried, US$0), `SetupRerunKeepsSunnyIslandArt`, both setup tests |
| `Verify.ps1 -Mode VisualBenchmarkSetup` | clean run; definition migrated to the sunny prefab, scene re-bound |
| Focused PlayMode `VisualBenchmarkCoursePlayTests` | 2/2 passed — both shortcuts traversable, ramp airborne+landing, gate-3 recovery respawn, all authored recover poses probe clean ground |
| `Verify.ps1 -Mode PlayMode` | 248/250 passed — 2 documented environment-blocked impaired-LAN timeouts; stale schema-4 pin in `HelloRecordShapeIsUnsequencedAndAdvertisesSchema` repaired to 5 |
| `Verify.ps1 -Mode WindowsVisualBenchmark` | dev player built clean (`result=Succeeded errors=0`) |
| Rendered dev player scenario (`Tools/SunnyIslandShots.py`) | both classes driven through every landmark; `presentation.courseVisualId=sunny-island-benchmark-01`, `renderProfileId=pc`, scale 1.0, 1280x800; 30 screenshots under `Logs/sunny-shots/run/screenshots/` |
| Screenshot inspection | 0% missing-shader magenta; palette/layout grids confirm deck/water/islands/route language and consistent HUD — see task-3 report |

Pending: manual image review on a display, A7 Lite device
qualification (tablet profile at 0.75 scale + sustained four-racer
measurement), real lightmap bake (scene carries GI-contributing flags;
no LightingDataAsset is baked yet), sustained multi-racer soak.

## Task 5 — effects, audio, camera readability, bilingual presentation (2026-10-14)

The benchmark now runs the catalog-driven race presentation layer
(`race-presentation-01`, `Runtime/Content/RacePresentationCatalogAsset.cs`):
`RaceEffectsPresenter` pools authored quad effects (drift dust, boost
flare, landing puff, wall shunt, checkpoint sparkle, finish burst under
`Art/VisualBenchmark/Effects/`) and `RaceAudioPresenter` plays the
offline-synthesized WAV pack under `Audio/VisualBenchmark/` (engine/
skid/boost loops on live motor state, event-deduplicated one-shot cues,
racing/results music). Event bursts deduplicate by stable event identity
through `RaceEventCursor` — the first frame after any (re)bind is a
history baseline, so reconnect/correction replays never replay one-shots.
Pause suspends every loop and the music; `race.exit` stops them
outright. Volumes ride the profile's three buses — effects slider owns
cues/engine/skid/boost, music slider owns music, voice slider owns
tutorial voice — nothing crosses. Missing clips/prefabs publish on
`presentation.audio.missingCues` / `presentation.effects.missingEffects`
(bounded, `missingDropped` overflow) instead of claiming playback; pool
and voice exhaustion publish as `exhausted`/`droppedOneShots` counters.
The slices ride every schema-5 boundary as `presentation.effects` /
`presentation.audio` (null when no catalog is bound). Both presenters
are pure consumers — they never write `driving`/`drivingEvents`.

### Task-5 verification results

| Check | Result |
|---|---|
| Focused PlayMode `RacePresentationPlayTests` | 8/8 — dedupe on repeated snapshots, correction replay, reconnect baseline, pause/exit loop stop, three-bus volume isolation, reduced-shake + occlusion fit, truthful missing-clip reporting, facts publishing |
| `Verify.ps1 -Mode EditMode` | 662/664 passed — 1 pre-existing packaged-font fixture failure (`FoundationFontSetupTests.SetupRepairsFontReferencesAndPreservesAuthoredFallbacks`, gated on the deferred font-download fixtures), 1 pre-existing skip (`EveryCueHasWiredClipForBothLocales`, the intentionally open tutorial-voice gate) |
| `Verify.ps1 -Mode PlayMode` | 268/270 — 2 documented environment-blocked impaired-LAN timeouts (unchanged signature) |
| `Verify.ps1 -Mode WindowsVisualBenchmark` | dev player built clean (`result=Succeeded errors=0`, ~238 MB) |
| Rendered dev player scenario (`Tools/RacePresentationShots.py`) | 10/10 checks — live `presentation.audio`/`presentation.effects` asserted at every beat (music-race/results transitions, engine/skid/boost loops, pause/resume, exit teardown, zero missing cues/effects); 5 screenshots under `Logs/t5-shots/run/screenshots/`, 0% missing-shader magenta; per-beat facts in `Logs/t5-shots/evidence/presentation-beats.json` |
| Audio pack decode | all 14 WAVs decode as valid 22050 Hz mono PCM via Python `wave` |

Pending: human listening review of the bundled cues/music, tutorial
voice clips still gated on owner Azure Speech credentials (the missing-
audio gate stays open by design — `Audio/Tutorial/README.md`), manual
image review on a display, A7 Lite device qualification.

## Task 6 — qualification scenario through the command path (2026-10-15)

`Tools/VisualBenchmarkScenario.py` proves the benchmark end-to-end
through the existing command and validation surfaces — no new gameplay
bypass, no teleport, no sleeps-as-proof. It composes the shared
protocol client (`AgentScenario.Client`), the LAN lifecycle and driver
helpers (`NetworkScenario`), the sustained-cycle/health machinery
(`NetworkSoak`), the landmark-navigation pattern (`SunnyIslandShots`)
and the schema-5 presentation assertions (`RacePresentationShots`).

### Commands

```powershell
# Mandated interface — 2-4 independently launched dev players.
# The FIRST descriptor is the host; Android descriptors are
# per-device ADB-forwarded loopback endpoints:
python Tools\VisualBenchmarkScenario.py --connections <run1>\connection.json <run2>\connection.json --evidence <new-dir> --minutes 30

# Same-machine players: pass --host-address 127.0.0.1 (or
# --allow-loopback for the loopback direct-join fallback); both are
# labeled loopback evidence, never LAN acceptance.
python Tools\VisualBenchmarkScenario.py --connections <run1>\connection.json <run2>\connection.json --evidence <new-dir> --minutes 30 --host-address 127.0.0.1

# Dev convenience: the scenario launches and owns the players (adds the
# labeled process-kill fault injection + quit-time perf reports):
python Tools\VisualBenchmarkScenario.py --launch Builds\Windows-VisualBenchmark\FamilyRacing.exe --players 2 --evidence <new-dir> --minutes 30
```

Attach mode needs each player launched with a validation dir:

```powershell
& "Builds\Windows-VisualBenchmark\FamilyRacing.exe" `
  --validation-dir <run-N> --perf-report-dir <run-N>\perf
```

For a device run each player is a separate process (Windows) or
`adb forward tcp:<port> tcp:<port>` per device (Android development
builds); the ADB descriptor is the same `connection.json` shape over a
forwarded loopback socket and needs no scenario changes.

### What a run proves (all through real commands)

- Segment A, solo tutorial phase per endpoint **before any hosting**:
  profile create, `ui.language` he+en boundaries, all three volume
  controls through `settings.update`/`settings.save`, `tutorial.start`,
  `tutorial.replay` (voice replay re-issues the cue), honest clip
  identity reporting, then `tutorial.exit`.
- Segment B, LAN lifecycle: discovery/direct join, AI fill to four
  racers, car and motorcycle races through rematch, both cameras via
  `camera.toggle`, held drift, boost, the landmark ramp, deduplicated
  recover, client-request + host-toggle shared pause, a mid-race drop
  with host-observed `controlSource: ai` takeover plus the reconnect
  path (attach mode honestly records the `noSession` rejection; launch
  mode kills and relaunches a process and exercises the credentialed
  rejoin), avatar/color continuity across the rematch, immutable
  results (duplicate `lan.results.request` serves the identical
  result), bounded pools, and quiet presentation facts after teardown.
- Labeled landmark screenshots (`vb-<peer>-<landmark>`) issued when a
  peer's own racer enters the authored landmark radius while four
  racers are presented; request→completion is correlated by
  `captureId`, tick, race id, vehicle and applied camera. A `failed` or
  `timedOut` record, a missing/empty PNG, or a peer with no captured
  shot is a verdict failure — never a successful capture. Landmarks
  never approached are recorded `missed` (honest coverage gaps).
- Segment C, the **separate** performance segment: a fresh session,
  then real-time race cycles until every endpoint's stream-measured
  active racing reaches `--minutes` (default 30). Each cycle assigns
  every endpoint the camera least covered for the race's class so the
  four class×camera cells fill evenly; per-cycle results settle through
  the once-only ledger and health is sampled against the 256-record
  outgoing bound. The benchmark's `VBDriver` route follower also paces
  its `input.set` resends (`MIN_SEND_TICKS = 10`, ~3/s — an opt-in the
  shared `LanDriver` leaves off, since its short races cannot reach the
  ceiling): every command — `input.set` included, rejections included —
  permanently consumes one of the 10,000 result identities in
  `FoundationService.IdentityCapacity`, and an unthrottled driver would
  exhaust a long session's store mid-benchmark (rejected
  `commandCapacity` from then on).

### The verdict (`result.json`)

`evaluate_verdict` fails the run when ANY dimension is missing —
verified complete streams per endpoint (contiguous sequencing, legal
transitions, `captureComplete`), identical build/content/protocol/
schema across endpoints, rendered-player evidence (`renderingAvailable`
plus nonzero `racingSamples` — headless captures can never pass), the
shot set, the honesty labels, and the fixed performance floors:
**≥1,800 observed active-racing seconds per endpoint after the 60 s
warm-up and ≥300 s in every class/camera combination.** Warm-up,
paused, lobby, countdown and results time all land in the reported
`excluded` table — slow transitions can never hide in a good aggregate.
A shorter `--minutes` only shortens the drive loop; the floors do not
move, so a bring-up run reports an honest `failed` verdict.

### Evidence layout

```
<evidence>/result.json            verdict, failures, checks, labels,
                                  perf totals + excluded accounting,
                                  shot manifest, incidents, hashes
<evidence>/<role>-evidence/       attach.json or launch.json,
                                  process.json, commands/observed logs
<evidence>/<role>-run/            connection.json, state.ndjson,
                                  screenshots/, capture-status.json
<evidence>/<role>-perf/           memory.ndjson (launch mode),
                                  perf-report-<runId>.json at quit
```

`perf-report-<runId>.json` is validated on collection (reportVersion 1
wrapper, `captureValid`, `renderingAvailable`, `evidenceStatus:
collected`, nonzero racing samples) and hashed; its racing seconds are
recomputed from the segment table, never trusted from the summary
field. Attach mode accepts `--perf-report-dir`; absent reports are
recorded `pendingPlayerQuit` — a named gap, never a fabrication.

**Recorder bounds** (`RenderPerformanceCapture`,
`RenderPerformanceState.cs`): `MaxSamples = 300,000` frame-interval
samples — ~83 min of rendered frames at 60 fps, ~2.4 MB of retained
doubles — sized to out-cover the mandated capture, which needs ≥31 min
at 60 fps (the 30-minute sustained segment plus warm-up, lifecycle and
results frames ≈ 45–50 min total). The previous 120,000 bound held only
~33 min and truncated a real `--minutes 30` run mid-capture
(`sampleLimitExceeded`, `droppedFrames` counted). Overflow is never
silent: it flips `captureValid: false` with `captureInvalidReason:
sampleLimitExceeded`, which fails `check_perf_reports` on the quit
report and `check_rendered_samples` on the live stream. `MaxSegments =
1024` is the second bound (a full qualification uses ~100).

### Honesty labels and exclusions

`labels` records `transport` (`loopback`/`mixed`/`lan` — ADB-forwarded
descriptors honestly report loopback), `loopbackEndpoints`,
`humanRacers`, `aiFilledSeats` and the `aiFillNoted`/`loopbackNoted`
flags; the verdict recomputes them from the descriptors and fails on
any mismatch. `notVerified` always names: cross-device LAN
reachability on loopback runs, physical input/UI parity, human visual/
Hebrew/audio review, and the ordinary-build arm.

**Ordinary builds cannot be attached** — validation endpoints are
compiled out (proven in the Task-1 absence check). The ordinary arm is
manual: launch the ordinary player with `--perf-report-dir <dir>` and
`--render-profile tablet` (or platform default), drive the same course
by hand for the segment, then read `perf-report-<runId>.json` plus
`Tools\PerfSampler.py` process memory. The report carries the same
`RenderPerformanceSummary` fields — that is the only measurement an
ordinary build can produce, and this scenario's verdict can never
consume it.

### Task-6 verification results

| Check | Result |
|---|---|
| `python -m unittest discover -s Tools -p 'Test*.py'` | 245/245 — includes `TestVisualBenchmarkScenario` (18): the six required negative validators (`RejectsMissingRenderedSamples`, `RejectsIncompleteStream`, `DoesNotCountPausedTime`, `RequiresBothClassesAndCameras`, `LabelsLoopbackAndAiFillHonestly`, `RejectsMismatchedBuildManifest`) each corrupt one evidence dimension and force a failing verdict; invalidated captures and the quit-report entry shape are covered too; the valid fixture passes |
| RED evidence | test module ran before implementation — `ModuleNotFoundError: No module named 'VisualBenchmarkScenario'` |
| `--minutes 0.5` bring-up (`Logs\vb-bringup-10`) | mechanically complete — verdict `failed` only on the fixed floors, as a short run must |
| `--minutes 30` qualification (`Logs\vb-qual-5`, `Logs\vb-qual-6`) | **passed** — 1,808–1,836 stream-measured racing s per endpoint, all four class×camera cells ≥374 s, both quit-time perf reports `validated` (`captureValid: true`, 123k/113k racing samples, p95 16.7 ms, zero stalls), 12/12 landmark shots, process-kill → AI takeover → credentialed rejoin labeled (`client-0-gen1` `faultCasualty`), immutable results + once-only ledger across 21 cycles, transport `loopback` + `aiFilledSeats: 2` labeled. Bring-up iterations surfaced and fixed real defects: a client-descriptor `lan.vehicle` race (`invalidVehicle`), `input.set` identity-budget exhaustion (`commandCapacity` at `IdentityCapacity=10000`), the recorder's own `MaxSamples` bound (120k→300k — ~33 min at 60 fps was shorter than the mandated capture), and a quit-time crash on invalid-report collection. |

## Task 7 — device-free optimization check and closeout (2026-10-14)

The A7 Lite and four physical participants are unavailable in this
environment (`adb devices` on the bundled platform-tools returned an
empty list on 2026-10-14). Every hardware/human gate stays
`notVerified`; the milestone closes as **development complete;
acceptance open** (`docs/tasks/visual-benchmark-report.md`).

### Final same-source builds (all four flavors, Task-7 closing revision)

| Flavor | Artifact | SHA-256 / size |
|---|---|---|
| Windows Mono development | `Builds\Windows-VisualBenchmark\FamilyRacing.exe` | exe `B7CCA8B82003185F22644D0EE1C23B3640DE1B996802D4F5D0501E287011AFA6`; Core.dll `DA2A5A7B35F858D8A1A4C471B9334CCF7E19E9C8DC3BE99271C278B03523A3EE`; Runtime.dll `9B934A697B2F16B8D6040F26119177F07519D90CF9F456BB868DCF39D2254D3F`; level0 `E85DDA974D2F42419294E96DA4A947E6B074E42A88D63461F690405AF0675BC5` — Core.dll differs from the qual-5/6 evidence player by the snapshot-guard fix; opt-5 measured this artifact directly |
| Windows Mono ordinary | `Builds\Windows-VisualBenchmark-Ordinary\FamilyRacing.exe` | exe `EB143EAD6441B6DD9D7D145ADB85410E9389FB3878899AE537E8C9869CCE38FA`; Core.dll `E224631214F61DE78E65515702132EE0D56F281ADC8D1A56E52737FEC19EB409`; Runtime.dll `B1DD42D7AE17D7081EB00987D3DA9419F07F588EAB7FBFC062D1C9AFE53DEADC`; level0 `5DF00FE7BBD88ACA491E7D26E038FAFF02653308D577C798E9351245AD071AD2` |
| Android IL2CPP development | `Builds\Android-VisualBenchmark\FamilyRacing.apk` | `8265D6BE2CFA31A2994582451C44FC2FA1177E87A7C52B02D5E144BF925B158D`, 51,550,871 B (`Logs\android-offline\20261014-110723-5d6c59bb62b1480fb508b514005cfe5f\result.json`) |
| Android IL2CPP ordinary | `Builds\Android-VisualBenchmark-Ordinary\FamilyRacing.apk` | `686E4EF26A46A39EBC85F5BA6250603F5C941F72968195420144F135560E9424`, 45,550,179 B (`Logs\android-offline\20261014-110942-2f08b509b04d4be0ad9600e47fe79580\result.json`) |

The earlier Task-1 APKs recorded above predated the Task-3/4/5 content —
these are the first same-source benchmark APKs carrying the Sunny Island
content. The ordinary player's validation absence was re-verified on
this final build (`Logs\vb-t7-ordinary-check`: empty `--validation-dir`,
zero listeners; dev control produced `connection.json` + socket).

### Task-7 gate results

| Suite | Result |
|---|---|
| `Verify.ps1 -Mode CoreOffline` | 600/600 — incl. new `PredictionSliceNeverPublishesOutsideClientRace` |
| `Verify.ps1 -Mode EditMode` | 663/665 — 1 pre-existing font-fixture failure + 1 voice-clip `[Ignore]`, unchanged signatures |
| `Verify.ps1 -Mode PlayMode` | 270/272 — the 2 documented environment-blocked impaired-link failures, unchanged |
| `python -m unittest discover -s Tools -p 'Test*.py'` | 245/245 |
| `Tools\Test-VerifyTooling.ps1` / `Tools\Test-AndroidOffline.ps1` | 18/18 and 30/30 |
| Rendered optimization pass (`Logs\vb-t7-opt-5`, final build) | validated perf reports: host p95 16.670 ms / max 23.16 ms / 0 stalls; client p95 16.670 ms / max 18.11 ms / 0 stalls |

### Optimization check outcome

The rendered profile pass on the final build (`Logs\vb-t7-opt-5`,
validated quit-time reports: host p95 16.670 ms / max 23.16 ms, client
p95 16.670 ms / max 18.11 ms, 0 stalls) plus the qual-6 segment table
show **no actionable hotspot on PC**: every class×camera racing segment
sits at p95 ≈ 16.67 ms (vsync-locked 60 fps), worst observed racing
frame 35.0 ms, zero steady-state >100 ms stalls, host working set
499→624 MB peak over ~43 min. Per the controller ruling (optimize only
measured causes), **no optimization changes were made**; the full
reasoning and the open-gate/owner table live in
`docs/tasks/visual-benchmark-report.md`.

Note: `Logs\vb-t7-opt`/`vb-t7-opt-2` (pre-fix) surfaced and pinned the
snapshot-slice defect; `vb-t7-opt-3`/`-4` (`--no-fault-injection`)
honestly failed at race-2 delivery settle — a permanently-departed
participant's ledger row stays `pending` past the 60 s settle window
(disclosed in the report as an open lifecycle-design question, not a
perf defect).

### Recorded tooling/product gaps for the device pass

- `MemorySampler` is not re-bound after a fault-injection relaunch —
  gen-2 process memory is unsampled and dead-PID samples read ~0 MB
  `ok` instead of `unavailable` (recorded in the report; gate rows
  stay `notVerified`).
- Intermittent credentialed-rejoin `input.set` stall (~110–130 s,
  burst-executed at race end) — product defect disclosed in the
  report, needs its own investigation before device acceptance.
- Fixed this task: one-boundary stale `prediction` slice on
  `lan.leave` teardown — `FoundationService.Snapshot` now gates the
  slice on the record's own role/race facts
  (`PredictionSliceNeverPublishesOutsideClientRace`).
- Real lightmap bake still absent (GI flags set, no
  `LightingDataAsset`) — required **before** the device arm so bake
  cost is inside the measurement.



