# Vehicle physics — build, device qualification and rollout report

Date: 2026-10-13. Plan: `docs/superpowers/plans/2026-10-13-vehicle-physics.md`
task 12. Spec: `docs/superpowers/specs/2026-10-13-vehicle-physics-design.md`
(staged acceptance and rollout, section "Staged acceptance and rollout").
Source commit under test: see provenance below.

## Rollout decision

**The physical backend stays OPT-IN.** The arcade motor remains the
shipped default on every player build and remains the developer
comparator/fallback. No default was flipped in this task and no
shipped content selects `PhysicalAssisted`; the backend is reachable
only through the dedicated `Builds\Windows-Physics` development player
(`Tools\Build-Windows.ps1 -Configuration Physics`). The default switch
stays gated on the open device/human/impaired-network rows in the
"Not verified" ledger below.

Deterministic PhysX replay is **not** claimed anywhere: client
prediction is approximate replay of authoritative boundaries through
the same force model; convergence and user-visible corrections are the
measured criteria, not cross-platform bitwise identity.

The snapshot wire ceiling is **32 KiB**
(`NetworkSettings.SnapshotCeilingBytes`), raised from the spec's
16 KiB after live evidence showed real physical snapshots overflowing
16 KiB mid-race (`snapshotOverflow`, host silently stopped publishing).
Evidence-justified deviation recorded in the progress ledger; partner
sign-off pending. Hard encode/send enforcement is retained.

## Provenance — what was tested

`Logs/evidence-t12-provenance/provenance.json` records the source
commit, the dirty working-tree list, and SHA-256 of every shipped
binary plus every file inside each player's `FamilyRacing_Data`
(content manifests, managed assemblies, resources). Headline hashes:

| Artifact | SHA-256 (prefix) | Build evidence |
|---|---|---|
| `Builds\Windows\FamilyRacing.exe` (Mono dev) | `B7CCA8B8…` | `Logs\verify-unity-windows.log` |
| `Builds\Windows-Ordinary\FamilyRacing.exe` | `EB143EAD…` | `Logs\verify-unity-windowsordinary.log` |
| `Builds\Windows-Physics\FamilyRacing.exe` | `B7CCA8B8…` | `Logs\windows-build\physics\20261013-174100-8d9f5320712c4b9791c8f3adc9c741f5\result.json` |
| `Builds\Android\FamilyRacing.apk` (51,306,655 B, dev/IL2CPP debug) | `CFDC36B6657F70D999B9B835E21E5B25FF46BB7A92BDDE7819489455DB05C87A` | `Logs\android-offline\20261013-173823-fe361b4f3e7944a6b1962f2082ecbbce\result.json` |

The Mono dev and physics launchers share one stub hash — game code
lives in `FamilyRacing_Data\Managed`; per-file hashes for all three
data trees are in the provenance file, so any scenario result binds to
an exact build+content identity. Uncommitted build inputs at hash time:
font `.meta` churn, generated `Foundation*.asset` re-serialization,
`GrayboxCourse.prefab`, `ProjectSettings.asset` (listed verbatim in the
provenance file's `gitStatusShort`).

Build-history honesty: the first `-Mode Android`/physics attempts at
17:31 UTC aborted on the Unity project lock (another editor instance
held the project — `Verify.ps1` launches the GUI-subsystem Unity
without waiting; recorded honestly in `Logs\task12\unity-gates.log`),
and the 17:37 physics rebuild failed `file in use` because stale
task-11 player processes still held `Builds\Windows-Physics`. All were
re-run serialized and are recorded below with fresh artifacts.

## 1. Automated test evidence

| Gate | Command | Result | Evidence |
|---|---|---|---|
| Core offline | `Tools\Verify.ps1 -Mode CoreOffline` | **594 passed / 0 failed / 0 skipped** | `Logs\task12\local-gates.log` |
| EditMode | `Tools\Verify.ps1 -Mode EditMode` | **652/653 passed, 0 failed, 1 skipped** (pre-existing `SkippedCheckpointCannotFinish`) | `Logs\verify-editmode-results.xml`, `Logs\verify-unity-editmode.log` |
| PlayMode | `Tools\Verify.ps1 -Mode PlayMode` | **245/247 passed, 2 failed** — `NetworkPredictionPlayTests.ImpairedLinkExercisesPredictionAndInterpolation`, `PhysicsPredictionPlayTests.ImpairedLanPhysicalCorrections`. Both are the documented impaired-LAN **environment** limitation (the impaired UTP session never establishes: `race=none`, zero drops, zero errors — identical signature since task 10). Env-blocked, not product failures. | `Logs\verify-playmode-results.xml` (fresh, 17:35–17:36 UTC) |
| Python tooling | `python -m unittest discover -s Tools -p 'Test*.py'` | **214 tests, OK** at gate time; **216 OK** after the `inputRefused` tests (§3, soak4) | `Logs\task12\local-gates.log` |
| Verify tooling | `Tools\Test-VerifyTooling.ps1` | **12/12 pass** | `Logs\task12\local-gates.log` |
| Windows dev player | `Tools\Verify.ps1 -Mode Windows` | **Succeeded** (`result=Succeeded`, 237 MB output) | `Logs\verify-unity-windows.log` |
| Windows ordinary player | `Tools\Verify.ps1 -Mode WindowsOrdinary` | **Succeeded** (`result=Succeeded`, 168 MB output) | `Logs\verify-unity-windowsordinary.log` |
| Android (offline) | `Tools\Verify.ps1 -Mode Android` | **Succeeded** — export + bundled Gradle `--offline`; APK hash above | `Logs\android-offline\20261013-173823-…\result.json` |
| Physics player | `Tools\Build-Windows.ps1 -Configuration Physics` | **Succeeded** on retry after the file-in-use abort | `Logs\windows-build\physics\20261013-174100-…\result.json` |

Tooling tweaks this task (no runtime change): `PhysicsSoak` /
`PhysicsScenario` `result.json` now record `effectiveSettings`
(results/input/sample timeouts, warmup and capacity bounds) so a run
binds to the exact thresholds it was judged against; and an honest
`unavailable` `input.set` refusal at the results boundary is recorded
as informational `inputRefused` instead of a hard `inputRejected`
(§3, soak4 — the harness phase snapshot is one stream boundary stale).

## 2. Live solo evidence (development player, headless)

`python Tools\PhysicsScenario.py Logs\run-physics-t12-scenario\connection.json --evidence Logs\evidence-t12-scenario`
— **passed**, ~118 s wall on a fresh `Windows-Physics` player
(`runId 00927c60…`, content `physics-1`, backend `physicalAssisted`,
canonical fingerprint bound):

- Car: real accel/coast/brake through held inputs (brake 1.52 vs coast
  0.11 m/s²); slalom 3 steering reversals ≤3.10 m/s lateral slip;
  landmark ramp launch + landing (peak 3.73 m); rollover → overturned
  auto-recovery; 10 s intentional park with no recovery; manual
  recovery epoch bump to 3; wall-pin → `stuck` auto-recovery (epoch 4);
  pause froze sim/world counters with queued recover consumed after
  resume; `app.focusLost` cleared held steering/brake through the same
  path an OS focus loss dispatches.
- Motorcycle: 2-wheel physics array; balance controller held a
  standing bike upright 4 s (up-dot 1.00); ramp flight; manual
  recovery. **Rollover attempted ×3 — no overturned episode observed →
  `notVerified` for this run** (car rollover is evidenced; the bike
  leg stays honest).
- Recovery ledger: 5 recoveries, strictly increasing epochs, mirrored
  by `lastRecoveryReason`.
- Stream: 9.2 MiB trace / 369 commands / 3745 physical boundaries;
  durable trace replays through the physical validators; capacity
  projection onto the 1800 s schedule ≈ 139 MiB trace / 5591
  identities — under the configured bounds.

## 3. LAN correction / soak evidence

All physical LAN evidence is same-machine (loopback + local discovery);
cross-device reachability stays `notVerified`.

### Soak attempt 1 — FAILED, evidence kept (`Logs\evidence-t12-soak`)

`--minutes 30`, authored `--results-timeout 600` (default), two
headless players, `--validation-trace-mb 1024`. Wall 1277.2 s; the run
tore down after the doomed-cycle floor once the third race exhausted
its window. Outcome: `status: failed`, hard incident
`resultsTimeout` on cycle 2.

- Cycle 0 (car): completed, immutable result
  `lan-13385161e8a94379b06fca455018e706-r1/result`, cycle elapsed 478.1 s.
- Cycle 1 (motorcycle): completed, immutable result `…-r2/result`,
  cycle elapsed 672.9 s.
- Cycle 2 (car): **no results phase within 600 s** — both endpoints
  `resultsTimeout`. Honest post-analysis of the host's reconstructed
  stream: racers kept MOVING (positions advance every boundary; ~57
  recovery epochs on the client's racer) but progress was glacial —
  the leader reached lap 1/gate 2 of a 2-lap race inside the window.
  This is crash-heavy slow-race variance above the 600 s bound, not a
  protocol stall (same signature family as task-11's doomed-cycle
  finding; races on this content ran 224–520 s in earlier evidence).
- All `failed` delivery rows on both endpoints carried
  `ineligible:dnf` with `honest: true` — the task-11 `ApplyOutcome`
  verdict-parity fix held live.
- Host `maxBacklogSteps` 0; client compact-wire exclusion held
  (`diagnosticsSeen` 0 vs the host's per-race diagnostic stream);
  `captureValid` true end-to-end at 826 MB host trace (~83% of the
  1 GiB configured bound — the bound held; a 256 MiB default would
  have invalidated at ~8 min, exactly the task-11 finding).
- Rematch baseline clean across 6 observed race identities (bodies,
  `ticksSimulated`, prediction history rebase).

### Soak attempt 2 — FAILED, evidence kept (`Logs\evidence-t12-soak2`)

`--minutes 30 --results-timeout 900`, fresh players, 1 GiB trace
bound. Wall 1665.2 s.

- Cycle 0 (car): completed, immutable result
  `lan-902420a13a014dbabec3501ed6ca08f2-r1/result`, 509.6 s.
- Cycle 1 (motorcycle): completed, immutable result `…-r2/result`,
  **761.9 s** — the 600 s bound would have killed a legitimate race;
  the widening was justified.
- Cycle 2 (car): at elapsed ~1631 s the **host's capture hit
  `traceLimit`** (1 GiB) mid-race — inputs rejected `traceLimit`,
  `captureInvalid` on the host, cycle `resultsTimeout`, teardown
  failed on the same bound. Measured truth: the physics stream ran
  ~1.05 GB / 28 min here, slightly above the task-11 estimate; the
  256 MiB default would have invalidated at ~8 min (task-11 finding
  confirmed again).
- All `failed` delivery rows `ineligible:dnf` + `honest: true` on both
  endpoints again.

### Soak attempt 3 — PASSED, short of 30 min (`Logs\evidence-t12-soak3`)

`--minutes 30 --results-timeout 900`, fresh players, 2 GiB trace
bound. `status: passed`, wall **1174.0 s (~19.6 min)**, 3 cycles
completed — the runner declines to start a race when the remaining
requested window cannot fit one more `resultsTimeout + settle + 30 s`
cycle, so a 30-minute request can legally end early. All contract
checks passed (compact-wire exclusion, rematch baseline, immutable
results, teardown); every `failed` delivery row was honest
`ineligible:dnf`. Host cadence: gap p50 31 ms / p95 47 ms /
p99 63 ms / max 250 ms, 130 gaps >100 ms, `maxBacklogSteps` 0,
`captureValid` end-to-end. Cycle times: car 435.0 s, motorcycle,
car — see `result.json.cycles`. Verdict for the 30-minute gate:
**not satisfied** — valid evidence but only 19.6 observed minutes.

### Soak attempt 4 — FAILED on a boundary artifact, evidence kept (`Logs\evidence-t12-soak4`)

`--minutes 45 --results-timeout 900`, fresh players, 2 GiB trace
bound. `status: failed`, wall **1880.9 s (~31.3 min)** — the first
run to clear 30 observed minutes — 4 races completed with immutable
results (car 589.7 s, motorcycle 810.6 s cumulative, car, motorcycle;
cycle ledger in `result.json`). Every contract check green: compact-
wire exclusion per race, rematch baseline across 8 observed race
identities, honest `ineligible:dnf` deliveries only (30 rows),
clean teardown, `captureValid` end-to-end, `maxBacklogSteps` 0 on
both endpoints. Host cadence p50 31 ms / p95 47 ms / p99 78 ms /
max 406 ms.

The single hard incident: `inputRejected` on client-0 at elapsed
1880.3 s — one periodic `input.set` refused **`unavailable`** at the
results boundary. `SetInput` only returns `unavailable` when the
endpoint has already left the driving screen (or a gadget demand,
never sent here), so the wire answer was the honest truth — the
harness's `phase == "racing"` snapshot was stale by one stream
boundary. A genuine check→send TOCTOU in the soak loop, not a
product defect.

**Tooling fix (no runtime change):** `PhysicsSoak.rejection_code()`
now reads the wire code off the `applied()` assertion payload; an
`unavailable` refusal is recorded as informational `inputRefused`
(not in `HARD_INCIDENTS`), every other code stays hard
`inputRejected`. Unit coverage added (`TestPhysicsSoak`, +2 tests →
216). The arcade `NetworkSoak` shares the same check→send edge and
was left unchanged — noted, not needed for this evidence.

### Soak attempt 5 — PASSED, ≥30 observed minutes (`Logs\evidence-t12-soak5`)

Identical launch to soak4 with the `inputRefused` fix in place
(players `--validation-trace-mb 2048`). `status: passed`, wall
**1832.8 s (30.5 min)**, **5 races completed** — car, motorcycle,
car, motorcycle, car — each with an immutable result and the compact-
wire exclusion re-proven per race (`result.json.checks`,
`resultId`s `lan-d1a8fb87c9ab40e5935e76bcf19ef2fe-r1…r5/result`):

- Rematch baseline clean across 10 observed race identities
  (bodies/ticks/history rebased).
- 45 `deliveryFailed` rows, every one honest `ineligible:dnf`; zero
  dishonest deliveries, zero lost participants.
- 5 informational `inputRefused` rows — each lands within 0.3 s of a
  cycle's results transition (316.3→316.6 s, 520.0→520.6 s,
  1368.1 s, 1832.7/1832.8 s), exactly the honest boundary refusal the
  fix describes; none mid-race.
- Host cadence gap p50 31 ms / p95 47 ms / p99 93 ms / max 344 ms;
  `maxBacklogSteps` 0 on both endpoints; `captureValid` end-to-end;
  host trace 1.21 GB and client 1.30 GB — both under the 2 GiB bound
  (the measured ~0.7 MB/s stream would have invalidated a 1 GiB
  launch at ~24 min, the soak2 finding).
- Teardown clean; traces reconstruct on both endpoints.
- Effective settings recorded: results timeout 900 s, input interval
  8 s, sample 15 s (`effectiveSettings`).

**30-minute physical LAN soak gate: CLOSED** — 30.5 observed minutes,
5 completed races across both classes, verdict `passed`.

### Physical LAN fault-injection lifecycle

`run_physics_lan.py` — a `NetworkScenario --launch` lifecycle with the
class catalog pointed at `physics-01`/`car-physics-01`/
`motorcycle-physics-01` (the lifecycle is backend-agnostic; catalog
ids are the only physics-specific input). Two attempts:

- **Attempt 1 — FAILED, evidence kept** (`Logs\evidence-t12-lan`).
  Drove the full lifecycle — host/discovery/join, car race, bounded
  input, cameras, shared pause, process-kill → AI takeover →
  credentialed `lan.reconnect` seat reclaim — then tripped the
  scenario's arcade-tuned `LAN_MAX_RECOVERIES = 12` bound: physical
  racing legitimately produces more recovery epochs. Validator-bound
  failure, not a product failure; evidence retained.
- **Attempt 2 — PASSED** (`Logs\evidence-t12-lan2`, `STATUS: passed`,
  physics exe `B7CCA8B8…`). All 22 checks green: 2 instances on the
  LAN browser with schema-4 slices; duplicate command marker; async
  `lan.join` applied once; `lan.start` → racing on **car** with
  predicted-vs-authoritative separation proven; bounded input;
  both cameras; recover action; **shared pause/resume**;
  **process-kill mid-race → AI takeover (`controlSource=ai`) →
  relaunch → credentialed `lan.reconnect` reclaimed the reserved
  seat** (client pid 13616 killed, generation-2 pid 57448 rejoined);
  immutable results with settled delivery ledger; duplicate
  `lan.results.request` served the identical result; `lan.rematch` →
  lobby; second race on **motorcycle** to immutable results;
  **host loss** → clients on `lan-recovery`, reconnect attempts
  failed honestly; `lan.leave`/`noSession` returned every endpoint
  to the browser; traces reconstruct, phases legal, accepted commands
  completed exactly once. Host trace: 27,656 events.
  **Credentialed rejoin under physics: CLOSED** on this evidence.

## 4. Ordinary-build exclusion evidence

`Builds\Windows-Ordinary\FamilyRacing.exe` launched with
`--validation-dir Logs\run-t12-ordinary-check` (headless): process
alive after 15 s, **zero listening TCP sockets** owned by the process
(`Get-NetTCPConnection -State Listen`), **zero files** created in the
validation directory — no `connection.json`, no token, no
`state.ndjson`. The `--validation-dir` argument is compiled out with
the validation bridge in non-development builds. Evidence:
`Logs\run-t12-ordinary-check*` + recorded command output in the task-12
report. (Consistent with the task-10 demonstration.)

## 5. Device qualification — `notVerified` (explicit checklist)

No physical devices exist in this environment. Every row below is
**not verified** — the same-PC evidence above does not satisfy them:

- Galaxy Tab A7 Lite (SM-T220, 3 GB) as host with **four physical
  racers** — needs the tablet.
- Tablet-to-tablet offline Wi-Fi session (internet disconnected).
- Mixed Windows/Android session, each platform hosting.
- Ordinary vs instrumented performance measured separately on-device
  (ordinary performance is structurally unmeasurable by this harness —
  endpoints compiled out).
- Touch input, Hebrew rendering, thermal behavior across 30-minute
  sessions; device frame-time/memory/network targets from
  `GAME_PLAN.md` (30 fps, p95 ≤ 40 ms, ≤800 MB).
- Cross-device reachability (all LAN evidence here is loopback on one
  machine; the impaired-LAN matrix — 100 ms RTT, ±30 ms jitter, 2%
  loss — additionally remains **environment-blocked**: the impaired
  UTP session has never established in this environment, documented
  across tasks 9–11).

## 6. Human handling acceptance — `notVerified` (explicitly open)

No human review exists in this environment. Open rows: braking is
discoverable; ordinary corners remain controllable; car vs motorcycle
feel distinct; assisted bike works at low speed; rollovers feel
consequential without being constant; recovery/camera comfort;
child/tutorial acceptance. A usable opt-in build is preserved
(`Builds\Windows-Physics`).

## 7. Not-verified ledger (all rows)

| Row | Status | What closes it |
|---|---|---|
| 30-minute LAN soak | **CLOSED** — `evidence-t12-soak5` `passed`, 1832.8 s observed, 5 races both classes, all invariants green (soak1–4 retained: slow-race timeout, `traceLimit`, 19.6-min pass, boundary-artifact fail) | — |
| Credentialed rejoin under physics (process death → rejoin) | **CLOSED** — `evidence-t12-lan2` passed: kill → AI takeover → credentialed `lan.reconnect` seat reclaim | — |
| Motorcycle rollover episode (solo) | `notVerified` this run — attempted ×3, no overturned episode | Repeat scenario attempts or a dedicated flip maneuver/content |
| Three consecutive laps per class on the current course (spec stage-2 solo acceptance) | `notVerified` literally — closest evidence is one full physical lap (car, PlayMode) + 2-lap AI races per class in `evidence-t12-soak5`; no solo 3-consecutive-lap run | Drive the scenario (or a driver) through 3 consecutive authored-course laps per class |
| Impaired-LAN correction targets (p95 ≤0.75 m/≤12° under 100 ms/jitter/loss; ordinary-LAN ≤0.25 m/≤5°) | `notVerified` — impaired session cannot be established in this environment (env-blocked since task 9) | An environment where the impaired UTP session establishes, or hardware devices |
| Cross-device LAN reachability | `notVerified` — all evidence is same-machine loopback | Physical devices on a real Wi-Fi network |
| Device matrix (A7 Lite host, 4 racers, tablet↔tablet offline, mixed OS, touch/Hebrew/thermal, perf targets) | `notVerified` — no devices | Hardware access |
| Ordinary-build performance | `notVerified` — unmeasurable by this harness by design | Physical profiling on the ordinary player |
| Human handling acceptance | `notVerified` — no human available | Owner/child playtest on the opt-in build |
| Render frame times on players | `notVerified` — cadence is a socket-observed proxy; soak players ran headless (no GPU) | Rendered-player run / device profiling |
| Sustained physical cost headroom | `notVerified` — diagnostics observed, not load-tested | Longer/larger soak or profiling pass |
| Android on-device behavior | `notVerified` — APK builds offline; no ADB device | Device install + run |
| IL2CPP Windows build | `notVerified` — module installed, no IL2CPP build produced | Produce + qualify an IL2CPP player |

## Prior failed evidence retained

Nothing was deleted: `Logs\evidence-physics-soak-x/-y/-aa/-bb`
(occlusion backlog, delivery-code asymmetry, traceLimit, doomed
cycle), the aborted 17:31/17:37 build runs under
`Logs\windows-build\physics\` and `Logs\android-offline\`, this
task's retained failures (`Logs\evidence-t12-soak`,
`evidence-t12-soak2`, `evidence-t12-soak4`, `evidence-t12-lan`), and
every earlier task evidence directory remain on disk.
