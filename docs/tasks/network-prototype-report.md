# M3 network prototype — milestone qualification report

Date: 2026-10-13 (UTC run dates 2026-10-12). Branch: `fix/driving-playtest-1`.
Plan: `docs/superpowers/plans/2026-10-12-network-prototype.md`, tasks 1–10.

**Status: development complete; acceptance open.** Every automatable
gate in the task-10 brief was executed on one revision; every row that
needs a physical device, an impaired real network, or a human reviewer
is listed as **not verified** at the bottom — none of them is passed by
a desktop substitute.

## Qualified revision

Qualified on the working tree that became the task-10 commit on
`fix/driving-playtest-1` (HEAD = `c3e15dd` + the task-10 source/tooling/
documentation delta in this commit). Prior task-9 baseline
`c3e15dd`/`375a711` is superseded by this pass.

- Unity `6000.3.26f1` (`de8167e7440c`), batchmode.
- Packages (locked in `Packages/manifest.json`): Netcode for GameObjects
  `2.7.0`, Unity Transport `2.6.0`.
- Transport: gameplay UDP 47777, discovery UDP 47778 (IPv4 broadcast,
  1 s cadence, 3 s expiry, ≤1024-byte datagrams).
- State schema: `3`; authority clock 30 Hz; replication 15 Hz.
- Capacity: 2–4 players, four racer slots including honest AI fill.

## Automated gate (task-10 run, all on the qualified revision)

| Suite | Command | Result |
|---|---|---|
| Core | `Tools\Verify.ps1 -Mode Core` | **468 passed / 0 failed** |
| EditMode | `Tools\Verify.ps1 -Mode EditMode` | **507 passed / 0 failed / 1 explicitly skipped** — `EveryCueHasWiredClipForBothLocales` (unchanged credential-gated voice-clip gate; no AZURE_SPEECH_KEY in this environment) |
| PlayMode | `Tools\Verify.ps1 -Mode PlayMode` | **174 passed / 0 failed** (`Logs/verify-playmode-results.xml`; +3 vs task-9 baseline from new regression tests below) |
| Python tooling | `python -B -m unittest discover -s Tools -p "Test*.py"` | **133 passed / 0 failed** on the fix-round tree (127 at the task-10 commit + 6 soak delivery-verdict honesty tests added in fix round 1) |
| Verify tooling | `Tools\Test-VerifyTooling.ps1` | **12 passed / 0 failed** |

Note: one intermediate EditMode run showed a transient
`SetupRepairsFontSourceImporterSettings` failure ("Cannot open … .meta
for write" during the test's own `SaveAndReimport`); the re-run on the
identical tree is 507+1 clean — a meta-write race, not a product
regression.

## Builds at the qualified revision

| Build | Command | Artifact | SHA-256 / detail |
|---|---|---|---|
| Windows Mono **development** | `Tools\Verify.ps1 -Mode Windows` | `Builds\Windows\FamilyRacing.exe` | exe stub `b7cca8b82003185f22644d0ee1c23b3640de1b996802d4f5d0501e287011afa6` (Unity launcher stub is incremental — freshness is in `FamilyRacing_Data\Managed\FamilyRacing.Core.dll`/`FamilyRacing.Runtime.dll`, rebuilt 2026-10-13 02:25 local); log `Logs\verify-unity-windows.log` |
| Windows Mono **ordinary** | `Tools\Verify.ps1 -Mode WindowsOrdinary` | `Builds\Windows-Ordinary\FamilyRacing.exe` | exe `eb143ead6441b6dd9d7d145adb85410e9389fb3878899ae537e8c9869cce38fa`; `BuildOptions.None`, no `DEVELOPMENT_BUILD` |
| Android IL2CPP debug APK (**development** — `BuildOptions.Development`, `DEVELOPMENT_BUILD` defined, validation bridge present for ADB attach) | `Tools\Verify.ps1 -Mode Android` (offline export + bundled Gradle `--offline`) | `Builds\Android\FamilyRacing.apk` | **`21c9498d3aa5ca978e1cf943ba74bfecc9181321999b9d4e4db304811f254977`**; evidence `Logs\android-offline\20261012-234239-c9fac196bebf4b3c9c9358c5aab86e87\result.json`; exported `unityLibrary\build.gradle`: compileSdk **36**, minSdk **25**, targetSdk **36** (serialized `AndroidTargetSdkVersion: 0` = automatic, resolves to 36 in the export). No `Verify` mode produces an Android *ordinary* build today — the path and its consequences are documented in `docs/NETWORK_TEST_RUNBOOK.md` ("Android: ordinary (non-development) build") |

### Ordinary-build validation absence — demonstrated, not asserted

The rebuilt ordinary player was launched with
`--validation-dir Logs\ordinary-validation-check`: the directory
received only `stdout.txt` — **no `connection.json`, no `state.ndjson`,
no `profiles/`**, and no validation socket was bound. The flag is
compiled out of ordinary builds (`FoundationApp` + `ValidationBridge`
are behind `DEVELOPMENT_BUILD`/`UNITY_EDITOR`), so automation can only
ever attach to development/editor players. The Android equivalent —
an ordinary (non-development) APK — is documented in
`docs/NETWORK_TEST_RUNBOOK.md` ("Android: ordinary (non-development)
build"); no `Verify` mode produces one today.

## Real-process scenario evidence (fresh development build)

All scenarios ran against real built players over the real validation
surface; no deterministic clock stepping, no teleports, no privileged
input paths.

| Scenario | Evidence | Result |
|---|---|---|
| `NetworkScenario.py --launch` (two instances, real UDP, process-kill fault injection) | `Logs\evidence-t10-lan-final9\result.json` (+ `evidence-t10-network-h` on the pre-cleanup build) | **22/22 checks passed** — discovery/join, schema-3, duplicate command dedupe, countdown→racing, bounded input normalization, camera toggles, recover ack, shared pause/resume, **process-kill → AI takeover → credentialed rejoin**, immutable results + honest delivery (`ineligible:dnf` for the dropped seat), rematch, second vehicle class, **host loss → lan-recovery → honest failed reconnect**, browser return; verified traces (host 13379 / client-0 16737 events, no incomplete commands, legal phase table) |
| `DrivingScenario.py` | `Logs\evidence-t10-driving2\result.json` | **28/28 checks** — both vehicles, honest full laps, recovery, restart identities, capacity projection |
| `AgentScenario.py` | `Logs\evidence-t10-agent3\result.json` | **21/21 checks** — command surface, profiles, tutorial, driving, capture |
| `RestartScenario.py` | `Logs\evidence-t10-restart2\result.json` | **6/6** — persistence across two processes, byte-identical save hashes |
| `StreamRobustnessScenario.py` | `Logs\evidence-t10-stream2\result.json` | **18/18** — disconnect / reset / stalled-sink faults: durable invalid status, exact trace prefix, duplicate redelivery is not a second completion |
| `FinishDeliveryScenario.py` | `Logs\evidence-t10-finish2\result.json` | **6/6** — finish delivered to a stalled non-reading sink: `streamError`, game thread unblocked, prefix contiguous and credential-free |
| `NormalProfileScenario.py` | `Logs\evidence-t10-normal2\result.json` | **6/6** — real `persistentDataPath\Profiles` store, WM_CLOSE exit → `processExit`, restart reload |

## Product fixes this task required (all PlayMode/EditMode-covered)

- `DrivingWorld.cs` — solo/LAN course-visibility ownership was last-write-
  wins per frame; `ApplyRaceFrame(null)` every non-LAN frame hid the
  course colliders under a live solo race (regression test
  `EmptyRaceFrameCannotHideSoloCourse`).
- `NetworkRaceBridge.cs` — a finish landing on an odd host tick skipped
  the final `finished` snapshot on the 15 Hz cadence; the final snapshot
  is now published immediately before the immutable result.
- `LanSessionService.cs` / `NetworkSettings.cs` — `session.closed`/leave
  datagrams were dropped by same-call `link.Stop()`; the observable
  session now ends immediately on leave while the transport survives one
  `SessionCloseFlushSeconds` (0.5 s) flush window so peers receive the
  explicit reason instead of a bare disconnect. Any new command during
  the window tears the link down at once — the flush never blocks the
  user. Delivery outcomes carry an observable `code`
  (`persisted`/`failed:*`/`ineligible:*`) outside the immutable result
  fingerprint.
- `NetworkPrototypeSetup.cs` — LAN event assets are now wired into the
  bootstrap scene's `DrivingWorld.events` (previously `lan.configure`
  rejected every lan event as `unknownEvent` on built players).
- `Tools/NetworkScenario.py` — corner-aware lookahead for the remote
  driver, driver suspension during input/expiry assertions, partial
  check preservation on failure, two-phase live-capture finish,
  host-loss recovery segment, `idle → reconnecting` legalized for
  relaunched credentialed processes. The stall detector's catch-up
  budget now ratchets to the worst setback since the frontier instead
  of re-reading the live gap: the decaying credit used to recover a
  moving car metres short of its own best mark and loop the respawn
  deterministically past `LAN_MAX_RECOVERIES`.
- `Tools/AgentScenario.py` — cooperative peer draining during waits;
  invalid/closed mock sockets skipped (keeps unit doubles valid).
- `Tools/NetworkSoak.py` (new) + `Tools/TestNetworkSoak.py` — the
  mandated `--connections … --minutes … --evidence` attach harness for a
  sustained multi-device soak; unit-tested, hardware-gated for the real
  30-minute run. A `failed` delivery row is a legal outcome only as the
  permanent `ineligible:<outcome>` verdict the immutable result itself
  implies (the `NetworkScenario.settle_results` standard) — any other
  code (`saveFailed`, absent, or result-mismatched) logs a
  `deliveryFailed` incident *and* fails the run. Teardown `lan.leave`
  tolerates an honest `noSession` rejection as an already-clean exit.

## Physical / human evidence matrix — NOT VERIFIED

The following rows require resources absent from this environment (no
tablets, no second physical device, no internet-disconnected AP, no
impairment rig, no owner reviewer). Each is **not verified** — nothing
above substitutes for them:

| Row | Status | What is missing |
|---|---|---|
| Ordinary Windows LAN play (two human-driven ordinary Windows builds on a real LAN: discovery/manual join, race, results, rematch) | **not verified** | Human-driven ordinary builds on two machines — validation sockets are compiled out of ordinary builds by design, so no agent attach exists |
| Tablet-to-tablet session (discovery or manual join, countdown, race, results, rematch, touch input) | **not verified** | Two supported tablets |
| Mixed-platform session (Windows ↔ Android, either hosting; version-mismatch rejection on real devices) | **not verified** | One Android device |
| Four-physical-device session (A7 Lite host, four humans, repeated races, AI takeover/rejoin, 30-min `NetworkSoak.py`) | **not verified** | Four devices + participants |
| Discovery failure matrix on a real AP (broadcast blocked → manual IP; denied LAN permission; unreachable/full/incompatible lobby) | **not verified** | Controlled access point |
| Lifecycle faults on devices (disconnect in each phase; client restart; host close; no duplicate reward) | **not verified** | Physical fault injection |
| Impaired-network matrix (100 ms RTT, ±30 ms jitter, 2% loss, normal-Wi-Fi baseline) | **not verified** | Impairment rig / real RF |
| Performance on devices (tablet 30 fps, p95 frame ≤40 ms, no recurring >100 ms stalls, ≤800 MB start; PC 60 fps @1080p; ordinary-build perf separate from dev) | **not verified** | Profiling devices |
| Full-capture validity on every physical device | **not verified** | Devices |
| Human acceptance (responsive steering, understandable flows, Hebrew readability, touch usability, M2 child/feel/audio gates) | **not verified** | Owner/child reviewers |
| Tutorial voice clips (`EveryCueHasWiredClipForBothLocales` EditMode skip) | **not verified** | `AZURE_SPEECH_KEY` credential |

## Residual risks accepted into acceptance

- `--launch` qualification ran host+client on one Windows machine over
  real UDP sockets — it proves protocol, transport and lifecycle honesty,
  not physical reachability, RF behavior, or cross-OS interop.
- The 0.5 s session-close flush keeps the transport alive briefly after
  observable leave; commands during that window force an immediate
  teardown (tested). This is a deliberate trade for delivering the
  explicit `session.closed` reason.
- `NetworkSoak.py` is delivered and unit-tested but has no sustained-run
  evidence — first real run is part of the physical matrix.
