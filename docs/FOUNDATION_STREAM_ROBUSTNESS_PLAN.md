# Foundation Stream Robustness Implementation Plan

> **For agentic workers:** Use subagent-driven-development and test-driven-development. Preserve existing changes; do not commit or push the game repository.

**Goal:** Bound stalled validation socket writes and verify reconnect/outcome lookup without certifying a capture that lost history.

**Architecture:** Keep the existing FoundationService, ControllerSession and StateStream paths. Set a finite synchronous send timeout at socket ownership, close a failed session so its queued records cannot each consume another timeout, and propagate writer failure to the existing sticky capture/status contract. Exercise actual loopback sockets and isolated profiles in PlayMode.

**Tech Stack:** Unity 6000.3.26f1, C#, existing NUnit/Unity Test Framework, Python standard-library scenario runner.

**Spec:** GAME_PLAN.md sections 3 and 4; docs/FOUNDATIONS_PLAN.md Task 3.

## Global Constraints

- Continue in the existing feature/family-racing checkout; preserve the uncommitted packaged-font work and existing index.
- No installs, downloads, paid assets, game commits/pushes or changes to ordinary profile storage.
- Only one pinned Unity Editor process may use this project at a time.
- Validation remains loopback/token-protected and editor/development opt-in; no new commands or wire-schema changes.
- Keep Foundations in progress. Physical Android and human Hebrew acceptance remain open; do not expand to racing gameplay before the milestone review.

## Review Focus

- A connected peer that stops reading must not leave finish pending forever, even below outgoing queue capacity.
- A send failure must close that session; queued records must not each incur the full send timeout.
- A writer failure must invalidate independent durable status without needing finish, queue overflow or process teardown.
- Reconnect must return a fresh snapshot and the original command outcome without repeating a persisted mutation.
- Reconnect must not restore capture validity or carry held input from the old controller.

## Task 1: Bounded Sends and Sticky Failure

**Files:** Runtime/Automation/ControllerSession.cs, Runtime/Automation/StateStream.cs, new Tests/PlayMode/ValidationStreamRobustnessPlayTests.cs (under Assets/FamilyRacing), matching .meta.

**Interfaces:** ControllerSession has an internal SendTimeoutMs constant of 2000. Configure TcpClient.SendTimeout before starting its reader; the rejection path also uses this bound. Socket-write failures dispose the exact failed session. StateStream writer failure invokes its existing Invalidate("streamError") path rather than only assigning Failure. Preserve async RequestFinish, sequence identities and capture-prefix semantics.

- [x] Add a real loopback regression StalledSinkFinishCompletesWithoutQueueOverflow. Authenticate a client with a small receive buffer, stop reading after the attached snapshot, enqueue a lookup containing an 8 MiB ASCII command identity plus 32 ordinary state records (well below 256 capacity), then send finish through the client and pump the bridge. Assert finish was requested, main-thread ticks progress, FinishCompleted within 8 seconds, FinishSucceeded false, service capture invalid, no outgoingQueueFull error, and durable captureValid/captureComplete false. In a finally block close the client before disposing the bridge; failed RED runs must not leak a blocked writer.
- [x] Add StalledSinkInvalidatesDurableStatusWithoutFinish. Same non-reading client and large lookup, no finish or overflow. Pump until Failure is non-null and service invalid; assert capture-status is invalid/incomplete while the bridge is still alive. The trace stays a prefix and is not reopened.
- [x] Observe behaviorally meaningful RED before changing production code. Keep log/XML evidence in a fresh Logs/qualification-stream-robustness directory.
- [x] Set the finite timeout and close failed sessions in every socket-write path, including reader protocol replies and stream finish writes. Route background writer failure to sticky invalidation; do not introduce configurable test-only production hooks.
- [x] Run the new focused PlayMode fixture GREEN. Preserve existing healthy hello/finish, queued-dead-session and queue-overflow behavior.

## Task 2: Reconnect and Integration Evidence

**Files:** new PlayMode fixture above; README.md, IMPLEMENTATION_STATUS.md, docs/FEATURE_COVERAGE.md; new docs/tasks/foundation-stream-robustness-report.md.

**Interfaces:** Use existing hello, snapshot, lookup, command, finish and FoundationService.Lookup. No new resume protocol or LAN claim.

- [x] Add ReconnectReturnsSnapshotAndOriginalOutcomeWithoutRepeatingSave. Create a synthetic profile, navigate to sandbox, hold steering/drift, record an applied profile.create identity and saved-file SHA-256, then disconnect. Pump until old attachment is gone and capture fails. Authenticate a new controller; assert snapshot has the same profile identity, neutral input, captureValid false and controllerDisconnected. Assert lookup returns the original applied outcome/tick. Re-deliver the identical profile.create command, assert duplicate true with original applied outcome and unchanged save SHA-256/profile identity. Assert a subsequent finish cannot certify the invalid capture.
- [x] Run the focused new fixture and existing ValidationBridgePlayTests, preserving exact commands and XML/log evidence.
- [x] Run one final full EditMode and PlayMode gate, then build the Windows Mono development player. Do not rerun unrelated Android/offline tooling gates.
- [x] Execute unchanged Tools/AgentScenario.py and Tools/RestartScenario.py on fresh evidence directories using the built player; preserve their existing assertions. Do not author or modify external scoring/scenario logic.
- [x] Document observed RED/GREEN, final test/build/scenario evidence, the 2000 ms write policy and remaining physical/external fault-injection gaps. Do not relabel PlayMode reconnect evidence as an external-player reconnect run.

## Execution Notes

- The approved continuation and existing command/sink contract authorize this remaining foundation slice without a new product-design cycle.
- Tasks share the new fixture; Task 2 runs after Task 1. Their common interface is the unchanged protocol and sticky capture failure. No task contradicts the constraints.
- Context7 was queried for synchronous socket timeouts, but returned unrelated WCF documentation or no match. Verify actual timeout behavior in the pinned Unity runtime; do not cite WCF as TcpClient evidence.
