# LAN Transport Test Runbook

Milestone 3 transport evidence. Scope: the pinned LAN transport
(Netcode for GameObjects 2.7.0 on Unity Transport 2.6.0) running on the
existing build pipeline — Windows Mono development/ordinary players and the
offline Android Gradle build. All gameplay is offline LAN; no Relay, Unity
Services authentication, or cloud lobby dependency exists anywhere in the
path.

## Fixed facts

| Item | Value |
|---|---|
| Gameplay socket | UDP 47777 default (`LanTransport.DefaultGameplayPort`), configurable per `StartHost`/`StartClient` so several processes can share one machine |
| Discovery socket | UDP 47778 default (`LanDiscovery.DefaultDiscoveryPort`), configurable |
| Discovery protocol | IPv4 UDP broadcast query/response on every active LAN adapter; 1-second query cadence; entries expire after 3 s; every datagram bounded to 1024 bytes |
| Validation sockets | Loopback only (`ValidationBridge`, ADB-forwarded on Android), token-protected, compiled out of ordinary builds — LAN work does not touch them |
| Manual join | Always available: `StartClient(address, port)` is unicast and independent of discovery; blocked broadcast only reduces auto-discovery, never the manual path |

## Automated gate

`Tools\Verify.ps1 -Mode PlayMode` runs the LAN PlayMode suite
(`LanTransportPlayTests`, `NetworkLifecyclePlayTests`,
`NetworkScreensPlayTests`, `NetworkStreamPlayTests`,
`NetworkResultsPlayTests`, `NetworkPredictionPlayTests`, …) including the
three transport smoke tests in
`Assets/FamilyRacing/Tests/PlayMode/LanTransportPlayTests.cs`:

- `HostClientExchangeUsesRealTransport` — real host/client UDP handshake and
  bidirectional payload round-trip inside one process.
- `StopReleasesSockets` — `Stop()` frees the bound port; a second host binds
  the exact same port and accepts a client.
- `DeniedAccessDoesNotOpenSockets` — a denied `ILanNetworkAccess` gate makes
  start fail immediately; no socket opens and no pending join lingers.

Latest recorded result: **174/174 PlayMode**
(`Logs/verify-playmode-results.xml`, M3 task-10 gate, 2026-10-12). Full
gate at that revision: EditMode 507 + 1 explicit voice-clip Ignore
(`EveryCueHasWiredClipForBothLocales`, credential-gated), Core 468,
Python tooling 133, verify-tooling 12/12.

## Windows: private-network firewall setup (do NOT disable the firewall)

Windows Defender Firewall may prompt the first time the player binds
UDP 47777/47778. Two supported setups:

1. Accept the prompt and check **Private networks only**.
2. Or pre-authorize the exact executable from an elevated PowerShell:

   ```powershell
   New-NetFirewallRule -DisplayName "Family Racing LAN (UDP In)" `
       -Direction Inbound -Protocol UDP -LocalPort 47777,47778 `
       -Profile Private -Program "C:\path\to\FamilyRacing.exe" -Action Allow
   ```

Notes:

- Same-machine testing (host + client on one PC) needs **no** rule — the
  firewall does not filter loopback.
- If the prompt was declined or the rule is missing, the client sees
  "access denied"/timeout and manual join by IP still reaches hosts that
  are allowed; fix by adding the rule above, never by disabling the
  firewall or switching the network profile to Public.

## Android permissions (verified on the exported project)

Exported target/compile SDK: **36** (automatic), min SDK **25**
(`unityLibrary/build.gradle` under the offline export). The merged
`unityLibrary` manifest carries exactly the three additions this slice
needs (`Assets/Plugins/Android/AndroidManifest.xml`):

- `INTERNET`, `ACCESS_NETWORK_STATE`, `CHANGE_WIFI_MULTICAST_STATE` — all
  Android *normal* permissions granted at install; **no runtime dialog is
  required or shown** for LAN unicast/broadcast on the actual OS/target
  combination.
- `NEARBY_WIFI_DEVICES` is intentionally **not** declared: the game uses
  plain UDP sockets, not Wi-Fi Aware/RTT, so requesting it would be an
  unjustified dangerous permission.
- `AndroidLanNetworkAccess.EnsureAccess()` acquires a
  `WifiManager.MulticastLock` so devices that filter broadcast replies
  still deliver them. Lock failure degrades discovery only — unicast
  manual join is unaffected.
- `LanNetworkAccess` reports `available|requesting|denied|unsupported`
  before any socket opens; denial fails start immediately
  (`DeniedAccessDoesNotOpenSockets`).

## Android: ordinary (non-development) build

The published APK **is** a development build — this is now stated
explicitly. `Tools\Verify.ps1 -Mode Android` runs
`Tools\Build-AndroidOffline.ps1`, which calls
`FamilyRacing.EditorTools.FoundationBuild.ExportAndroid`
(`Assets/FamilyRacing/Editor/FoundationBuild.cs`): the Gradle export is
produced with `BuildOptions.Development |
BuildOptions.AcceptExternalModificationsToPlayer`, then bundled Gradle
builds `:launcher:assembleDebug` under `--offline`. `BuildOptions.
Development` defines `DEVELOPMENT_BUILD`, so the debug APK **does**
carry `DEVELOPMENT_BUILD` and **does** contain the loopback,
token-protected validation bridge — that is exactly what lets the
scenario tools attach over ADB forwarding on Android.

An ordinary Android build has **no automated `Verify` mode today**. The
path is the same two stages with the development flag removed:

1. Export the Gradle project from the pinned editor without
   `BuildOptions.Development` — either a `FoundationBuild` entry point
   that passes `BuildOptions.AcceptExternalModificationsToPlayer` alone
   to `BuildPipeline.BuildPlayer`, or the Editor's Build Settings window
   with "Development Build" unchecked. Everything else in
   `ExportAndroid` stays identical (`exportAsGoogleAndroidProject`,
   `buildAppBundle = false`, the `Builds\Android\offline` export-dir
   contract, `VerifyBuildConfiguration`).
2. Build it with the same bundled-Gradle offline invocation
   (`:launcher:assembleDebug --offline`) used by
   `Tools\Build-AndroidOffline.ps1`.

That APK is the Android analogue of `-Mode WindowsOrdinary`: same
content and backend, `DEVELOPMENT_BUILD` undefined, validation
endpoints compiled out — no `--validation-dir` socket, no
`connection.json`, no agent attach.

**Instrumented-vs-ordinary comparison (honest line):** instrumented
(development) performance is measurable through the validation harness
— boundary cadence, health samples, backlog — on both Windows and
ADB-attached Android. Ordinary-build performance **cannot** be measured
by this harness on either platform: the endpoints are compiled out, so
it requires physical profiling (e.g. Unity Profiler against a
development build approximates, but the ordinary figure needs
device-side measurement on the ordinary player itself). No such
measurement exists yet — it stays in the open-gates list below.

## Scenario tools (all attach via `connection.json`, never an exe path)

| Tool | Drives | Evidence |
|---|---|---|
| `Tools\NetworkScenario.py --connections <host.json> <client.json> [--host-address <ip>] --evidence <new-dir>` | Complete LAN lifecycle on 2–4 launched players: discovery/manual join, lobby, both vehicle classes, bounded input, shared pause, AI takeover (leave) or process-kill credentialed rejoin (`--launch` mode), immutable results, rematch, host loss, recovery | Per-endpoint `result.json` + independently verified `state.ndjson` |
| `Tools\NetworkScenario.py --launch <player.exe> [--players 2-4] [--no-fault-injection]` | Dev convenience: owns the processes so the labeled fault-injection segment can kill/relaunch a client (real AI takeover + credentialed rejoin) and kill the host (real host loss) | Same |
| `Tools\NetworkSoak.py --connections <host.json> <client.json> ... --minutes 30 --evidence <new-dir>` | Sustained real-time soak: repeated lobby→race→results→rematch cycles in wall-clock time (no deterministic clock stepping), periodic bounded input + per-device health samples, once-only result/delivery ledger across rematches, per-device boundary cadence | Per-endpoint `samples.json`, `result.json`, verified traces |
| `Tools\PhysicsScenario.py <run>\connection.json --evidence <new-dir>` | Agent-driven physical-backend qualification on the `Windows-Physics` player: physics-01 on `car-physics-01` + `motorcycle-physics-01` — accel/coast/brake, grip, ordered gates, input/recover epochs, simulation diagnostics (2 substeps/tick, bodies, faults), ordered substep path, full wheel physics — never teleports | `result.json` checks + independently verified `state.ndjson` |
| `Tools\PhysicsSoak.py --connections <host.json> <client.json> ... --minutes 30 --evidence <new-dir>` | Sustained physical LAN soak (2–4 physical players): physics-01 lobby→race→results→rematch cycles alternating classes, per-peer physical state/diagnostics/fingerprint/recovery-epoch sampling, compact-wire-projection checks (no leaked full diagnostics), trace integrity per endpoint — a lost peer is an incident, never substituted | Per-endpoint `samples.json`, `result.json`, verified traces |

## Evidence log

| Date (UTC) | Check | Result |
|---|---|---|
| 2026-10-12 | `Verify.ps1 -Mode PlayMode` LAN + driving suite | 174/174 pass (`Logs/verify-playmode-results.xml`) |
| 2026-10-12 | Full gate (M3 task 10) | Core 468/468, EditMode 507 + 1 explicit voice-clip Ignore, Python 133/133, verify-tooling 12/12 |
| 2026-10-12 | `Verify.ps1 -Mode Windows` Mono development build | Succeeded; see `docs/tasks/network-prototype-report.md` for the hash at the qualified revision |
| 2026-10-12 | `Verify.ps1 -Mode WindowsOrdinary` Mono ordinary build | Succeeded; validation endpoints compiled out — launching with `--validation-dir` produces no `connection.json` and opens no socket |
| 2026-10-12 | `Verify.ps1 -Mode Android` export + offline Gradle | Succeeded; APK hash + exported target SDK recorded in the task-10 report |
| 2026-10-12 | `NetworkScenario.py --launch` on the task-10 build | 22/22 checks pass incl. process-kill AI takeover, credentialed rejoin, host loss (`Logs/evidence-t10-lan-final9`); details in the task-10 report |
| 2026-10-12 | `NetworkSoak.py` tool + unit coverage | Tool delivered (`TestNetworkSoak.py` 28/28); real 30-minute multi-device soak is hardware-gated |
| 2026-10-13 | Task-11 gate: `Verify.ps1 -Mode CoreOffline` | 589/589 pass — Core diagnostics counters, state/fingerprint/recovery wiring |
| 2026-10-13 | `Verify.ps1 -Mode EditMode` | 647 pass / 1 skip / 0 fail — `FoundationBuild.WindowsPhysics` + all runtime code compile clean |
| 2026-10-13 | `Verify.ps1 -Mode PlayMode` | 244/246 pass — both new tests green (`PhysicalDiagnosticsReachTheStreamBoundary`, `WireEncodeStripsStreamOnlyDiagnostics`); the 2 failures are the documented impaired-LAN environmental timeouts (`ImpairedLinkExercisesPredictionAndInterpolation`, `ImpairedLanPhysicalCorrections`: `race=none`, `drops=0`, identical signature at HEAD pre-change — see progress ledger) |
| 2026-10-13 | Python tooling discovery | 194/194 pass (`TestPhysicsScenario` + `TestPhysicsSoak` included) |
| 2026-10-13 | `PhysicsScenario.py` / `PhysicsSoak.py` delivery | Tools + unit coverage delivered; live run needs the `Windows-Physics` player (`Build-Windows.ps1 -Configuration Physics`) and 2+ endpoints for the soak |

## Open evidence gates (explicit — hardware or human review required)

These require physical devices or owner review not available in this
environment. Each is **not verified** — never passed by a desktop
substitute. The authoritative list with per-row status lives in
`docs/tasks/network-prototype-report.md`:

- Ordinary Windows LAN play: two human-driven ordinary Windows builds on
  a real LAN (discovery/manual join, race, results, rematch) —
  validation sockets are compiled out of ordinary builds by design, so
  this can only ever be demonstrated by a human; no agent attach exists.
- Ordinary-build performance, both platforms: the offline Android APK is
  a development build (`DEVELOPMENT_BUILD` defined); an ordinary APK
  path is documented above but not automated, and ordinary-build frame
  performance cannot be measured by this harness — it requires physical
  profiling on the ordinary player.
- Tablet-to-tablet session (two supported tablets, internet disconnected):
  discovery/manual join, ready/countdown, race/results/rematch, touch.
- Mixed-platform session (Windows ↔ Android, either hosting), including
  version-incompatibility rejection and Windows firewall/multi-adapter
  diagnostics on a real LAN.
- Four-physical-device session (A7 Lite host): four humans, repeated
  races, AI takeover/rejoin, 30-minute `NetworkSoak.py --minutes 30` run.
- Discovery failure matrix on a real AP: broadcast blocked → manual IP;
  denied LAN permission; unreachable/full/incompatible lobby responses.
- Lifecycle faults on devices: disconnect in countdown/running/pause/
  finishing, client restart, host close, no duplicate reward.
- Impaired-network matrix: measured/injected 100 ms RTT, ±30 ms jitter,
  2% loss, plus normal-Wi-Fi baseline.
- Performance on devices: tablet 30 fps / p95 steady frame ≤ 40 ms /
  no recurring >100 ms stalls / ≤800 MB starting memory; PC 60 fps @1080p;
  ordinary-build frame performance measured separately from
  instrumentation.
- Full-capture validity on every physical device.
- Human acceptance: responsive steering, understandable flows, Hebrew
  reading and touch usability, prior M2 child/feel/audio gates.
