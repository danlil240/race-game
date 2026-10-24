# Family Racing

A local-multiplayer family racing game for Android tablets and Windows PC, in
early development. Design and acceptance criteria live in `GAME_PLAN.md`;
progress is tracked in `IMPLEMENTATION_STATUS.md` and
`docs/FOUNDATIONS_PLAN.md`.

Production CI, signed release setup and startup GitHub update notifications:
[production guide](docs/PRODUCTION.md).

## What exists today

**Graybox driving prototype (M2, under review).** On top of the
foundation below, the repo now ships a command-driven driving trial:
profile select → practice screen → `race.start` on `intro-01` (or the
`tutorial-01` onboarding) with `car-01`/`motorcycle-01`, ordered
checkpoints and laps, drift/boost, the landmark-ramp jump, wall impacts,
latched recovery to authored safe poses, chase/overhead cameras,
pause/retry/exit — every action through the same command path agents
use, streamed as schema-2 snapshot+delta boundaries and proven by the
real-player scenarios in `Tools\` (`DrivingScenario`, `DrivingSoak`).
Milestone acceptance is **open**: see `docs\tasks\driving-prototype-report.md`.

**LAN network prototype (M3, development complete; acceptance open).**
Offline-LAN multiplayer on Netcode for GameObjects 2.7.0 / Unity
Transport 2.6.0: broadcast discovery + manual IP join, a four-slot lobby
with host-configured event/class/vehicle/ready, one authoritative
30 Hz `NetworkRace` with honest AI fill and bounded contact, client
prediction + remote interpolation, shared pause, reserved-seat
credentialed rejoin, AI takeover on disconnect, once-only immutable
results with completion coins, rematch, and host-loss recovery — every
surface bilingual and command-driven, streamed as schema-3
snapshot+delta boundaries proven by `Tools\NetworkScenario.py` and the
sustained `Tools\NetworkSoak.py` harness. Physical-device and human
acceptance gates are **open**: see
`docs\tasks\network-prototype-report.md`.

**Opt-in physical vehicle backend (vehicle-physics plan, qualification
open).** Alongside the shipped arcade motor, a `PhysicalAssisted`
backend exists: raycast-wheel force model, two fixed 1/60 s substeps per
30 Hz logical tick, suspension/braking/rollover, assisted motorcycle
balance, host-authoritative physical LAN world with a compact wire DTO
and approximate client-side replay prediction. It is **not** the
default — arcade remains the shipped backend and the developer
comparator/fallback; physics runs via the dedicated
`Builds\Windows-Physics` development player (see `docs\COMMANDS.md`).
Qualification is proven by `Tools\PhysicsScenario.py` (solo, both
classes) and `Tools\PhysicsSoak.py` (multi-endpoint LAN); device and
human handling gates are **open**: see
`docs\tasks\vehicle-physics-report.md`.

**Sunny Island visual benchmark (M4, development complete; acceptance
open).** The proven `graybox-01` course runs dressed as a representative
benchmark (`visual-1` content): authored environment/vehicle/animal
visuals, cataloged effects and offline-synthesized audio, pc/tablet URP
render profiles, rendered-frame measurement (`--perf-report-dir`,
`PerfSampler`), four same-source build flavors, and the schema-5
qualification scenario `Tools\VisualBenchmarkScenario.py` — two
consecutive 30-minute PC runs passed (≥1,800 racing s per endpoint, all
four class×camera cells, p95 16.7 ms, zero steady-state stalls; see
`Logs\vb-qual-5`/`Logs\vb-qual-6`). Device and owner-review gates are
**open**: see `docs\tasks\visual-benchmark-report.md` and
`docs\VISUAL_BENCHMARK_RUNBOOK.md`.

**Complete gameplay loop machinery (M5, code complete; acceptance
open).** Profile schema 3 (ordered migrations, lossless copies), the
shared local race modes (Campaign/Quick Race seat a human + three
same-class AI through the LAN `NetworkRace` coordinator; Time Trial runs
a lone normalized clock), host-authoritative gadgets
(turbo/bubble-shield/foam-trap with pickups and cooldowns), objective
and bounty evaluation, atomic exactly-once reward/purchase/record
transactions, upgrade/cosmetic selection rules, the full bilingual loop
UI and semantic commands (`campaign.*`, `quickRace.*`, `timeTrial.*`,
`garage.open`, `bounties.open`, `upgrade.purchase`, `cosmetic.equip`,
`results.*`), state schema 7 (`soloRace`/`progression` slices plus the
ordered `progression.worlds` multiworld rows and `campaign.selectWorld`)
and LAN protocol 3 with gameplay-fingerprint admission. Exercised externally by
`Tools\GameplayLoopScenario.py` — **but the packaged player binds no
gameplay catalog**: the authored two-course/26-event Sunny Island world
(Task 9 of the plan) is gated on the M4 tablet evidence and was never
produced, so progression commands reject `contentUnavailable` and every
production-content row is honestly `notVerified`. See
`docs\tasks\gameplay-loop-report.md` and `docs\GAMEPLAY_LOOP_RUNBOOK.md`.

**Foundation shell.** `Assets/FamilyRacing/Core` is a
Unity-free, C# 9, .NET Standard 2.1-compatible assembly:

| Area | Files | Contract |
|---|---|---|
| Profiles | `Core/Profiles/*.cs` | `ProfileSave` (schema 1: id, slot, avatar, colorIndex, language, camera, reducedCameraShake, three volumes, coins, unlockedVehicles), `ProfileFactory.Create(slot, "en"\|"he")` with per-slot defaults (fox/rabbit/panda/raccoon, chase camera, 0.65/0.85/1.0 volumes, `car-01`+`motorcycle-01`), `IProfileSerializer`, `ProfileStore`, `ProfileLoadResult`. |
| Driving input | `Core/Driving/*.cs` | Immutable `DriverInput` (steering clamped to [-1,1], non-finite → 0, six command flags), `DriverInputComposer.Compose` (clamp summed steering, OR commands) and `FromButtons` (right−left steering). |
| Shell text | `Core/Localization/ShellText.cs` | Static `en`/`he` catalog (29 keys), `Get`, `Keys`, `IsRightToLeft`. Hebrew stored in logical order. |
| Commands | `Core/Automation/*.cs` | Immutable `GameCommand` (copied `ReadOnlyDictionary` params), terminal `CommandResult` (`applied`/`rejected`/`failed`, duplicate flag, original tick preserved), `CommandDescriptor` discovery schema. |
| Foundation service | `Core/Foundation/*.cs` | `FoundationService` — profiles/settings/navigation/input/pause commands, 10k reject-not-evict identity cache, read-only `Lookup`, deep-copy `FoundationState` snapshots incl. `FeatureCoverage` (all `notImplemented`) and `PresentationState`. |

`Assets/FamilyRacing/Runtime` adds the Unity app (`FoundationApp`, 30 Hz
observation ticks), a keyboard+touch input adapter dispatching `input.set` on
change through the same `Execute` path agents use, a bilingual UI Toolkit
shell (4 animal profile cards, settings, input sandbox; tree rebuilds only on
screen/locale/selection change), and — behind `--validation-dir` in
editor/development builds only — a token-authenticated loopback NDJSON
command channel + `state.ndjson`/`capture-status.json` structured sink
(`Automation/ValidationBridge`, `StateStream`, `ControllerSession`,
`ValidationJson`). `Assets/FamilyRacing/Editor` holds the repeatable
`FoundationProjectSetup`/`FoundationBuild` entry points; generated
URP/panel/scene assets live under `Assets/FamilyRacing/Generated`.

**Profile persistence safety.** `ProfileStore` writes `profile-{0-3}.json` plus
a last-known-good `.bak`, atomically (`File.Replace` via a unique sibling temp
file, flushed first). Loads return `Missing` / `Loaded` /
`RecoveredFromBackup` / `Blocked`. Corrupt, newer-schema, foreign-identity, or
orphaned files are never overwritten or silently reset — saves fail with a
safe `InvalidOperationException`, and serialized output is deserialized and
re-validated (identity + slot) before any disk write.

## Verifying

```powershell
# Offline test run (no NuGet restore, no network). Compiles the shared
# sources with the local .NET SDK and a bundled Unity nunit.framework.
Tools\Verify.ps1 -Mode CoreOffline
# or directly:
Tools\OfflineTestRunner\Run-OfflineTests.ps1 [-NunitFrameworkPath <dll>] [-EditorRoot <dir>]

# Standard harness once NuGet restore is explicitly approved (needs network):
dotnet restore Tools\CoreTests\FamilyRacing.CoreTests.csproj
Tools\Verify.ps1 -Mode Core        # dotnet test --no-restore

# Environment prerequisites for the pinned Unity editor:
Tools\Verify.ps1 -Mode CheckEnvironment

# Tooling self-check (fake editor layout in %TEMP%, no Unity needed):
Tools\Test-VerifyTooling.ps1

# Unity 6.3 (batch, offline) via Verify.ps1 — logs + exit codes propagate:
Tools\Verify.ps1 -Mode Setup      # FoundationProjectSetup
Tools\Verify.ps1 -Mode EditMode   # Logs\verify-editmode-results.xml
Tools\Verify.ps1 -Mode PlayMode   # Logs\verify-playmode-results.xml
Tools\Verify.ps1 -Mode Windows    # Builds\Windows\FamilyRacing.exe (Mono dev)
Tools\Verify.ps1 -Mode WindowsOrdinary  # Builds\Windows-Ordinary\ (Mono, no dev flag,
                                  #  validation endpoints compiled out)

# Repeatable offline Android build — Unity export, then bundled Gradle with
# --offline (no downloads; missing cache entries fail with diagnostics):
Tools\Verify.ps1 -Mode Android    # Logs\android-offline\<runId>\result.json

# Saved-profile restart evidence on the Windows dev player — two real
# processes share one synthetic store via --validation-profile-dir while each
# gets a fresh capture directory:
python Tools\RestartScenario.py Builds\Windows\FamilyRacing.exe --evidence <new-dir>

# Expected-fault qualification on the same build — launches three fresh
# validation players (graceful disconnect, TCP reset, stalled sink) and
# verifies durable invalid status, reconnect, original outcome lookup and
# duplicate protection. Passing means the injected faults were detected —
# every capture stays INVALID; it never certifies missing history:
python Tools\StreamRobustnessScenario.py Builds\Windows\FamilyRacing.exe --evidence Logs\stream-fault-<new-name>

# Validation: keep this launch terminal alive (background its shell in an
# agent environment), then run the scenario from a second terminal.
$exe = (Resolve-Path "Builds\Windows\FamilyRacing.exe").Path
$run = Join-Path (Get-Location) "Logs\validation-<unique-name>"
$player = Start-Process $exe -ArgumentList "--validation-dir",$run -PassThru
$player.WaitForExit()
# From the second terminal, using the same run directory:
python Tools\AgentScenario.py <dir>\connection.json --evidence <new-evidence-dir>
python Tools\DrivingScenario.py <dir>\connection.json --evidence <new-evidence-dir>

# Sustained soak — owns its own validation player, drives both vehicles /
# cameras with repeated restarts for N minutes, sampling memory, cadence,
# trace growth and queue/backlog health:
python Tools\DrivingSoak.py Builds\Windows\FamilyRacing.exe --evidence <new-dir> [--minutes 30]

# LAN lifecycle + soak — attach to 2-4 independently launched validation
# players by their connection.json (first is the host; --host-address is
# the manual-join fallback when broadcast discovery cannot reach):
python Tools\NetworkScenario.py --connections <host-run>\connection.json <client-run>\connection.json --evidence <new-dir>
python Tools\NetworkSoak.py --connections <host-run>\connection.json <client-run>\connection.json --minutes 30 --evidence <new-dir>
# Dev convenience: self-launch local players incl. process-kill fault
# injection (AI takeover, credentialed rejoin, host loss):
python Tools\NetworkScenario.py --launch Builds\Windows\FamilyRacing.exe --players 2 --evidence <new-dir>
```

Full command/wire-protocol reference: `docs/COMMANDS.md`; command →
state-field coverage registry: `docs/FEATURE_COVERAGE.md`.

`Tools/OfflineTestRunner/Runner.cs` is a small reflection runner supporting
`[Test]`, `[TestCase]`, `[SetUp]`, `[TearDown]`; it exits nonzero when zero
tests are discovered.

## Remaining gates

- **M5 gameplay-loop closeout (2026-10-15, task 12)**: fresh gate at
  `03ef5d2` — CoreOffline 803/803, EditMode 875/876 (1 credential-gated
  voice-clip skip), PlayMode 309/309, Python tooling
  279/279, verify-tooling 18/18, android-tooling 30/30. External
  `GameplayLoopScenario` evidence on the development player (loopback +
  AI fill labeled): smoke `partial` (tutorial incl. gadget lesson +
  process-restart persistence passed; all progression rows
  `contentUnavailable`), campaign `partial` (0 authored events —
  honest), LAN 2- and 4-endpoint `partial` (normalization, attributed
  gadget, kill/AI-takeover/credentialed rejoin, dedup/retained results,
  armed failed-save retry, `undeliverable` departure, host loss all
  passed). **Open**: Task 9 production content (two authored courses +
  26 events) and the four GameplayLoop build flavors — gated on M4 A7
  Lite evidence; the A7 Lite matrix, physical-human LAN, ordinary-build
  performance arm, and owner/Hebrew/child reviews — all `notVerified`
  in `docs\tasks\gameplay-loop-report.md`. **M5: code complete for the
  dispatched scope; acceptance open — not marked complete.**
- **M4 visual-benchmark closeout (2026-10-14, task 7)**: final
  same-source Windows/Android development + ordinary benchmark builds
  produced and hashed; gate re-run (CoreOffline 600/600, EditMode
  663/665 with 1 pre-existing font-fixture failure + 1 voice-clip skip,
  PlayMode 270/272 with the 2 documented impaired-link environment
  failures, Python 245/245, tooling 18/18 + 30/30); ordinary validation
  absence re-verified; PC-side optimization pass found no actionable
  hotspot (60 fps p95 16.7 ms, 0 steady-state >100 ms stalls) so no
  optimization changes were made. **Open**: A7 Lite device matrix,
  four-human physical LAN, ordinary-build performance arm, owner
  tablet/Hebrew/audio review, real lightmap bake, the disclosed
  credentialed-rejoin `input.set` stall defect, thermal/rematch device
  evidence — all `notVerified` in
  `docs\tasks\visual-benchmark-report.md`. **M4: development complete;
  acceptance open.**
- **Vehicle-physics qualification (2026-10-13, task 12)**: the opt-in
  `PhysicalAssisted` backend is implemented for both vehicle classes,
  LAN-replicated (schema 4, protocol 2) with approximate client
  prediction — deterministic PhysX replay is **not** claimed. Latest
  gate: Core offline ~594, EditMode 652/653 (1 pre-existing skip),
  PlayMode green except the 2 documented impaired-link environment
  failures, Python tooling 216, verify-tooling 12/12; Windows dev,
  ordinary (validation compiled out) and physics players build; offline
  Android APK per `Tools\Verify.ps1 -Mode Android`. Live evidence:
  solo `PhysicsScenario` (both classes: accel/brake, slalom, ramp,
  rollover→recovery, park, pause/focus cleanup); a **passed** 30.5-min
  `PhysicsSoak` LAN run (5 races both classes, immutable results,
  rematch baselines, wire exclusion — `Logs\evidence-t12-soak5`); and
  a passed fault-injection lifecycle (process-kill → AI takeover →
  credentialed `lan.reconnect`, host loss — `Logs\evidence-t12-lan2`).
  **Rollout decision: physics stays opt-in** — the device matrix
  (A7 Lite), impaired-LAN, cross-device and human handling gates
  remain `notVerified`. Report:
  `docs\tasks\vehicle-physics-report.md`.
- **M3 network-prototype gate (2026-10-12, task 10)**: Core 468/468,
  EditMode 507 + 1 explicit voice-clip Ignore, PlayMode 174/174, Python
  tooling 127/127, verify-tooling 12/12; same-revision Windows Mono dev,
  Windows Mono ordinary (validation compiled out — demonstrated) and
  offline Android APK produced; `NetworkSoak.py` delivered. Every
  physical/human matrix row is **not verified** — no tablets, second
  device, internet-disconnected Wi-Fi or owner review here. **M3:
  development complete; acceptance open.** Report:
  `docs\tasks\network-prototype-report.md`.
- **M2 review gate (2026-10-12, task 7)**: Core 349/349, EditMode 377/377,
  PlayMode 105/105, Python tooling 71/71, verify-tooling 12/12; Windows
  Mono dev + Android IL2CPP debug APK (47,688,467 B, SHA-256 `E332C362…`)
  built from the same revision; real-player Driving 27 / Agent 21 /
  Restart / StreamRobustness 18 / FinishDelivery 6 / NormalProfile 6
  checks all pass (`Logs\evidence-t7-*`); a real 30-minute soak measured
  55 laps / 23 restarts / both cameras, ~30 Hz steady cadence, 45.8 MB
  trace, no steady-state overload, 835 bounded+marked event evictions
  (`Logs\evidence-t7-soak3\`; report
  `docs\tasks\driving-prototype-report.md`). Open: every device/human
  gate below — **neither M1 nor M2 is marked complete**.
- Windows IL2CPP module is now installed for the pinned editor
  (`Tools\Verify.ps1 -Mode CheckEnvironment` OK since 2026-10-11,
  owner-approved). No IL2CPP build has been produced or qualified yet —
  Windows still ships the Mono development build (plus the new Mono
  ordinary build via `-Mode WindowsOrdinary`, which compiles the
  validation endpoint out), and M1 acceptance does not require a
  particular Windows scripting backend. Kept as a separate
  build-hardening item. Android Build Support incl. SDK/NDK/OpenJDK is
  present and exercised by the offline build path.

### Earlier gate history (foundation waves)

- Windows IL2CPP module is now installed for the pinned editor
  (`Tools\Verify.ps1 -Mode CheckEnvironment` OK since 2026-10-11,
  owner-approved). No IL2CPP build has been produced or qualified yet —
  at the time of this entry Windows shipped only the Mono development
  build (superseded: `-Mode WindowsOrdinary` and the M2 review gate are
  recorded above), and M1 acceptance does not require a particular
  Windows scripting backend. Kept as a separate build-hardening item.
  Android Build Support incl. SDK/NDK/OpenJDK is present and exercised
  by the offline build path.
- Android APK (`Builds\Android\FamilyRacing.apk`, ~47 MB) is built by the
  offline path (`Tools\Verify.ps1 -Mode Android`, Unity export + bundled
  Gradle `--offline`; `Logs\android-offline\<runId>\result.json` records
  status/hash). Latest run rebuilt the APK from current source
  (`Logs\android-offline\20261011-234621-67c0086c90654c0383c539db54c2f6c8\result.json`,
  SHA-256 `E332C362…8ECA`). Still not device-qualified — `adb devices`
  (bundled platform-tools) found no attached device on 2026-10-12; no
  ADB/on-device run exists. Report:
  `docs\tasks\foundation-device-qualification-report.md`.
- Saved-profile restart evidence now exists: `Tools\RestartScenario.py`
  drove two real player processes against one synthetic store and verified
  identical identities, all six settings, and unchanged save-file hashes
  (`Logs\qualification-continuation\restart-final\result.json`). The
  `--validation-profile-dir` override is honored only next to
  `--validation-dir` in editor/development builds and is ignored by ordinary
  family launches.
- NuGet restore is no longer blocked: the nuget.org user-level source was
  added with owner approval (2026-10-11) and `Tools\Verify.ps1 -Mode Core`
  passes 225/225. `Tools\Verify.ps1 -Mode CoreOffline` remains the
  no-network path.
- Packaged bilingual fonts implemented: pinned Noto Sans / Noto Sans
  Hebrew / Noto Sans Symbols2 sources under `Assets\FamilyRacing\Fonts\`
  with OFL license and provenance (`README.md` there); generated dynamic
  FontAssets, Advanced Text Generator enabled, live font-readiness
  warning recomputes each refresh. OFL is also bundled into
  `Assets\StreamingAssets\ThirdPartyLicenses\Noto-OFL.txt` (verified
  byte-identical inside the Windows player and the APK).
- Physical device testing (A7 Lite etc.) still required.
- External agent scenario passed 11/11 checks on the final packaged-font
  build (`Logs\qualification-fonts\agent-evidence\result.json`), including
  the packaged-font/locale and camera-toggle steps; earlier lead runs
  passed 9/9 (`Logs\evidence-lead\result.json`, `Logs\evidence-visible-lead\result.json`).
  English and Hebrew window captures were lead-accepted
  (`Logs\qualification-fonts\player-evidence-2\screenshot-*.png`); the
  stream-robustness gate at that stage was EditMode 249/249 and
  PlayMode 25/25.
- Stalled-sink robustness is now bounded and evidenced: every synchronous
  validation socket write carries a 2000 ms send timeout
  (`ControllerSession.SendTimeoutMs`), a failed write closes the exact
  failed session so queued records skip it, and writer failure flips the
  sticky capture/`capture-status.json` contract without needing finish,
  overflow, or teardown — real loopback PlayMode regressions
  (`ValidationStreamRobustnessPlayTests`, RED observed before the fix);
  report `docs\tasks\foundation-stream-robustness-report.md`.
- External expected-fault qualification now passed on the same reviewed
  build: `Tools\StreamRobustnessScenario.py` drove three fresh validation
  players through graceful disconnect, TCP reset and stalled sink —
  durable invalid/incomplete status while each process lived, a fresh
  same-run snapshot with neutral input, original outcome lookup,
  duplicate redelivery with unchanged save-file hashes, finish staying
  invalid and a byte-frozen trace prefix — 18/18 checks
  (`Logs\qualification-external-stream-robustness\external-run-2\result.json`;
  report `docs\tasks\foundation-external-stream-report.md`).
  Unverified per `notVerified` at that stage (partially superseded by
  the foundation-gaps wave below): full physical keyboard/touch
  coverage, finish delivery to the original non-reading controller,
  graceful-exit autosave and the normal user profile path, human Hebrew
  reading/quality review, Android on-device behavior, any racing
  features.
- Settings-screen UI/command parity is now covered by real-widget PlayMode
  tests: `FoundationSettingsPlayTests` drives actual `Slider.value` callbacks
  and pooled pointer down/up on the rendered buttons against a synthetic
  `ProfileStore`, and proved a stale-captured-draft bug that dropped pending
  edits (fixed in `FoundationShell.Screens.cs`: `UpdateSettings` merges the
  live `Snapshot().draftProfile`). RED 4-fail/GREEN 5-pass evidence and the
  full gate (EditMode 249/249, PlayMode 30/30, Windows Mono build) are under
  `Logs\qualification-settings-parity\`; report
  `docs\tasks\foundation-settings-parity-report.md`. These are synthetic UI
  Toolkit callbacks — not OS touch and not a process-restart check.
- Foundation-gaps wave (2026-10-11, `docs\FOUNDATION_GAPS_PLAN.md`)
  closed the remaining automatable `notVerified` surfaces:
  `--validation-normal-profiles` (real `persistentDataPath` store under
  agent control), `Application.runInBackground` inside validation
  launches only (gate-found unfocused-player freeze), and hold-button
  registrations moved to trickle-down (test-found `Button.Clickable`
  defect that blocked real touch holds). New external evidence:
  `Tools\FinishDeliveryScenario.py` 6/6 (stalled-controller finish →
  terminal `streamError`, `captureClosed` for new controllers) and
  `Tools\NormalProfileScenario.py` 6/6 (real store, `taskkill` graceful
  exit → `processExit`, saved profiles survive, drafts never
  autopersist) (`Logs\qualification-gaps\`, `Logs\qualification-exit\`;
  report `docs\tasks\foundation-gaps-report.md`). **Latest recorded
  gate: EditMode 253/253, PlayMode 53/53** (`Logs\verify-editmode-results.xml`,
  `Logs\verify-playmode-results.xml`), Core `dotnet test` 225/225,
  AgentScenario 11/11, Restart 6/6, StreamRobustness 18/18 — the earlier
  249/25 and 249/30 counts cited above are historical per-pass records,
  superseded by the task-7 M2 gate at the top of this section.
  Still open: OS-level physical keyboard/touch coverage, human Hebrew
  reading/quality review, Android on-device/ADB behavior, IL2CPP build
  qualification, licensed EN/HE voice cues, owner feel review, child
  playtest, and all later-milestone features (`notImplemented`).
