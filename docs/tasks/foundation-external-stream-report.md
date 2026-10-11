# Foundation external stream qualification report

Status: implemented and verified on `feature/family-racing`
(uncommitted, like the rest of the milestone). Evidence directory:
`Logs/qualification-external-stream-robustness/`; accepted run:
`external-run-2/result.json` (18/18); scoped reviews: `review.md`,
`re-review.md`, `refinement.diff`, `review-package/`; doc diff:
`changes.diff` (against pre-change snapshots in `baseline/`).

## Scope

`docs/FOUNDATION_EXTERNAL_STREAM_PLAN.md` Tasks 1 and 2: close the
external Windows-player reconnect and stalled/broken-socket evidence gap
without certifying an invalid capture. A passing result means the
injected faults were correctly detected — every fault capture stays
invalid/incomplete. Foundation robustness only; no driving milestone
scope.

## Harness (lead-authored, unchanged since acceptance)

- `Tools/StreamRobustnessScenario.py` — three fresh owned validation
  players, one phase each: graceful disconnect (`shutdown(SHUT_RDWR)`),
  TCP reset (`SO_LINGER(1,0)` close), stalled sink (`SO_RCVBUF` 1024,
  stop reading, 32 legal lookup frames carrying 16000-byte ASCII
  commandIds — each frame below the real 16 KiB limit and below the
  128-inbound/256-outgoing capacities). Per phase: synthetic profile 0,
  sandbox, held steering/drift; durable `capture-status.json` invalid
  while the process is still alive (before finish or teardown); reconnect
  to a fresh same-run snapshot; original outcome lookup; identical
  command redelivery; finish that must stay invalid; trace prefix
  byte-frozen across post-fault commands and finish.
- `FaultClient` is the only place invalid capture state is accepted; the
  healthy `AgentScenario.Client` still rejects it — `AgentScenario.py`
  and `RestartScenario.py` stayed byte-identical (runtime `toolHashes`
  verify the three tool files inside the report).
- Evidence dirs are exclusive-create (`exist_ok=False`); an existing
  path is refused and never overwritten. The harness owns only its three
  `Popen` players and terminates each after its checks; pre-existing
  processes are never touched.
- Reproducibility: 251-file build fingerprint before and after the run;
  accepted run's `buildFiles` equals the pre-run prep fingerprint
  (`build-fingerprint.json`).

## Test coverage

- `Tools/TestStreamRobustnessScenario.py` — 7 integrity tests:
  invalid-status/complete/error-set rejection (including non-boolean
  flag values), reconnect rejecting changed profile/held input/restored
  capture, duplicate requiring original terminal outcome and tick,
  failed-prefix validation (truncated/gapped/non-snapshot-first/corrupt
  or credential-leaking records, identity drift, unsequenced hello/error
  replies, appended finish), evidence-overwrite refusal,
  startup-failure-recorded-not-passing, live fault observer accepting an
  invalid snapshot but rejecting a sequence gap.
- RED/GREEN record: 6 RED for the missing companion
  (`python-integrity-red.log`), 7 GREEN (`python-integrity-green.log`);
  three later corruption mutants RED3 (`python-prefix-integrity-red.log`)
  then GREEN7 (`python-prefix-integrity-green.log`) after the trace-prefix
  validator hardening. The live-observer test is later-added coverage
  without its own missing-companion RED.
- Full Python suite: 24/24 before the validator-only refinement
  (`python-final-suite.log`); no Unity suite or build rerun for this
  tooling-only pass — the last reviewed runtime gate (EditMode 249/249,
  PlayMode 25/25) is historical, not re-run here.

## Accepted run (`external-run-2`)

| Phase | Fault | Detection | Reconnect snapshot | Original create |
|---|---|---|---|---|
| disconnect | graceful `SHUT_RDWR` | `controllerDisconnected`, 0.032 s | seq 105, tick 96 | tick 81 |
| reset | `SO_LINGER` RST | `controllerDisconnected`, 0.016 s | seq 105, tick 96 | tick 81 |
| stalled | never-reading socket | `streamError`, 2.094 s | seq 199, tick 158 | tick 81 |

A TCP reset may surface as `controllerDisconnected` or `streamError`
(run 1 observed `streamError`); both are allowed, never a valid capture.
Owned players 50792/60068/62944 all exited; only the primary
`profile-0.json` existed — a create does not write a backup, so no
backup-write coverage is claimed. Each trace begins at sequence 1 and
ends before the fault-observation records; SHA-256 unchanged across
live commands and finish.

## Not claimed / open

- Scenario pass = expected fault correctly detected with all captures
  invalid; it is not capture certification.
- Finish delivery to the original non-reading controller remains
  PlayMode-only; this run observes the independent failure without
  finish and the reconnected reader's finish stays invalid.
- Neutral input was observed in all phases, but only disconnect/reset
  prove disconnect cleanup specifically (guarded by the 150-tick input
  window); in the stalled phase neutral input may also be natural
  bounded expiry.
- Still open: physical keyboard/touch and UI parity, graceful-exit
  autosave and the normal user profile path (evidence covers explicit
  synthetic saves only), human Hebrew review, Android device/ADB,
  Windows IL2CPP, NuGet restore, all racing features.
- The earlier preview player (PID 53552) exited externally during the
  first verification run; no lingering preview is claimed.
- Milestone 1 (Foundations) stays in progress.
