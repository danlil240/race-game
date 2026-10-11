# Foundation External Stream Qualification Plan

> **For agentic workers:** Use subagent-driven-development and test-driven-development. The lead authors and checks scenario assertions/evidence logic; delegate only mechanical execution and documentation from frozen results. Preserve existing changes; do not stage, commit or push the game repository.

**Goal:** Close the external Windows-player reconnect and stalled/broken-socket evidence gap without certifying an invalid capture.

**Architecture:** Add a Python standard-library companion to the existing scenarios. Launch fresh validation players with isolated synthetic profiles, use the unchanged loopback protocol, and retain per-connection observation logs plus the independent durable capture status. Expected fault detection is a passing fault-injection scenario, never a passing full-state capture.

**Tech Stack:** Existing Windows Mono development player, Python standard library, unchanged Unity 6000.3.26f1 runtime.

**Spec:** GAME_PLAN.md section 3 command/state/sink contract and section 4; docs/FOUNDATIONS_PLAN.md Task 3 agent-testability acceptance.

## Global Constraints

- Continue in the existing feature/family-racing checkout; preserve all uncommitted work and the existing index.
- No installs, downloads, paid assets, game commits/pushes, ordinary profile access, new wire commands or production test hooks.
- Reuse the already-built timeout-fixed Windows player; fingerprint its launcher, managed code and content before/after qualification.
- Own only newly launched scenario processes and new evidence directories; leave the existing preview alone.
- Keep Milestone 1 in progress. Physical Android, human Hebrew review and physical input parity remain open. Do not expand into driving.
- Existing AgentScenario.py and RestartScenario.py remain byte-identical.

## Review Focus

- Reconnect has a fresh sequence baseline, but gaps within each attachment still fail.
- A disconnected controller cannot retain held input or repeat a persisted command.
- A never-reading socket must fail independently of finish, overflow or process teardown.
- An injected TCP reset may surface as disconnect or streamError, never a valid capture.
- A passing expected-fault result must explicitly label its capture invalid and preserve the trace prefix; existing paths and unrelated processes must not be overwritten/terminated.

## Task 1: Lead-authored external companion and integrity tests

**Files:** Tools/StreamRobustnessScenario.py, Tools/TestStreamRobustnessScenario.py.

**Interfaces:** Reuse AgentScenario.Client command/action helpers and record validators, plus RestartScenario.wait_connection/build_fingerprint/file_hash. A specialized client accepts invalid capture state only for these expected-fault scenarios; do not weaken the healthy scenario.

- [x] Add integrity tests first: reject healthy/incomplete-status mismatches, wrong reconnect identity/input/sequence, changed outcome tick/duplicate flag, corrupt/truncated/gapped/credential-leaking trace prefixes, and overwriting existing evidence. Durable status has no run identity; bind it to the owned fresh run directory, not an invented schema field.
- [x] Observe RED for the missing companion before authoring its implementation.
- [x] Implement run_scenario(player_command, evidence, startup_timeout=60, shutdown_timeout=10): three fresh players, one each for graceful disconnect/reconnect, TCP-reset/reconnect, and stalled sink/reconnect.
- [x] On each player create profile 0, enter sandbox, hold steering/drift, and preserve the original create result/frame and hashes of every synthetic save file present. Creating a profile need not create a backup; do not claim backup-write coverage from this test. After disconnect/reset, require invalid durable status while the process is alive. Reconnect to fresh snapshot, verify unchanged profile and neutral input, lookup the original outcome, resend the identical command and require duplicate/original tick/unchanged files. Finish must remain invalid/incomplete.
- [x] For stalled sink shrink SO_RCVBUF to 1024, stop reading, and send 32 legal lookup frames carrying 16000-byte ASCII identities (below the actual 16 KiB frame and 128 inbound/256 outgoing capacities). Require streamError durable status within 8 seconds of load start, without finish. Reconnect proves ticks continued. Do not infer timeout versus overflow from elapsed time alone: require streamError, not outgoingQueueFull.
- [x] Require independent per-attachment contiguous sequences, initial complete snapshots, identity/time/tick checks, redacted hello logs, a valid frozen trace prefix starting at sequence 1, and unchanged prefix bytes after post-fault commands/finish. Final reports identify expected-invalid captures separately from scenario status.
- [x] Observe GREEN on the focused Python integrity tests. No Unity suite rerun is needed for tooling-only work.

## Task 2: Mechanical execution, lead evidence review and docs

**Files:** README.md, IMPLEMENTATION_STATUS.md, docs/FEATURE_COVERAGE.md, docs/FOUNDATIONS_PLAN.md; docs/tasks/foundation-external-stream-report.md; fresh Logs/qualification-external-stream-robustness/ evidence.

- [x] Run the exact lead-authored companion against Builds/Windows/FamilyRacing.exe in a new evidence child directory and preserve console output/result/commands/observations/player logs/fingerprints.
- [x] Lead checks all result assertions and raw evidence; if a harness defect appears, the lead authors the correction, preserving failed evidence. A runtime defect requires root-cause investigation and a separately designed fix.
- [x] Run one final Python tooling suite: python -m unittest discover -s Tools -p 'Test*Scenario.py' -v.
- [x] Document only frozen observed results, distinguish external fault scenarios from valid captures and PlayMode proofs, and retain all physical/human/unrelated gaps.

## Execution Decisions

- This continues the explicitly documented external-scenario gap under the approved foundation acceptance contract; no product behavior or milestone boundary changes.
- Task 2 consumes Task 1's unchanged harness. Documentation cannot convert a failed or partial run into acceptance.
- No new vault note/schema edits without user confirmation; project evidence stays in the repository.
