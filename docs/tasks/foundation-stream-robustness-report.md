# Foundation stream robustness report

Status: implemented and locally verified on `feature/family-racing`
(uncommitted, like the rest of the milestone). Evidence directory:
`Logs/qualification-stream-robustness/`; full command-level report:
`Logs/qualification-stream-robustness/implementation-report.md`; diff:
`Logs/qualification-stream-robustness/changes.diff` (against pre-change
snapshots in `baseline/`).

## Scope

`docs/FOUNDATION_STREAM_ROBUSTNESS_PLAN.md` Tasks 1 and 2: bound stalled
validation socket writes, close a failed controller session, and propagate
writer failure to the existing sticky capture-status contract — plus
reconnect/outcome-lookup regression coverage. Foundation robustness only;
no driving milestone scope.

## Root cause and fix

- `ControllerSession` owned the `NetworkStream` and ran its reader with no
  `SendTimeout`; every synchronous `Stream.Write` (hello/error replies,
  stream records, the finish record) could block forever once a peer
  stopped reading.
- `StateStream.RequestFinish` only queues the finished record behind
  earlier writes, so a below-capacity queue with a blocked write left
  finish pending forever without triggering `outgoingQueueFull`.
- Writer failure was a bare `failure` assignment: durable
  `capture-status.json` stayed valid until finish/overflow/dispose.

Fix:

- `ControllerSession.SendTimeoutMs = 2000` bounds every synchronous write:
  set on the accepted `TcpClient` before the reader starts, on
  `RejectAndClose`, and implicitly on all reader/writer send paths.
- A socket-write failure disposes the exact failed session (reader replies
  via `WriteRaw`; stream sends via `SendToTarget`/`SendFinished`), so
  queued records skip it instead of each paying another 2 s.
- `WriterLoop` and the send helpers route failure through the existing
  sticky `Invalidate("streamError")`; `Invalidate` precedes `Dispose` so a
  racing disconnect check cannot relabel a sink failure as
  `controllerDisconnected`.

## Test coverage (new PlayMode fixture)

`Assets/FamilyRacing/Tests/PlayMode/ValidationStreamRobustnessPlayTests.cs`
— real loopback sockets, under-capacity queues, synthetic temp profile
stores, no production test hooks. The 8 MiB lookup record is emitted
directly through the internal `StateStream` (test-side only; not a wire
command or input-limit change).

- `StalledSinkFinishCompletesWithoutQueueOverflow` — RED 8.32 s on the
  unbounded write (finish never completed); GREEN 2.34 s: finish completed
  (`FinishSucceeded=false`, `streamError`, invalid durable status, no
  `outgoingQueueFull`) and main-thread ticks progressed.
- `StalledSinkInvalidatesDurableStatusWithoutFinish` — RED 8.30 s (failure
  never surfaced); GREEN 3.14 s: sticky invalidation reached service state
  and `capture-status.json` with the bridge still alive; the trace stayed a
  stable prefix — `Invalidate` does not close the trace handle, already
  queued pre-failure records may still drain to it, but emissions after
  invalidation are never appended.
- `ReconnectReturnsSnapshotAndOriginalOutcomeWithoutRepeatingSave` —
  passed on pre-change code (2.63 s): reconnect returns a fresh snapshot
  with the same profile identity, neutral input, `captureValid=false` /
  `controllerDisconnected`; `lookup` returns the original applied
  outcome/tick; re-delivered `profile.create` returns `duplicate=true`
  with the original tick and unchanged save SHA-256; a later finish cannot
  certify the invalid capture. This was already-correct behavior recorded
  as coverage, not a forced RED.

## Gate and external evidence

- Focused GREEN: 3/3 (`playmode-robustness-green2.xml`).
- Existing `ValidationBridgePlayTests`: 18/18 (`playmode-bridge-green.xml`)
  — hello/finish, dead-session drop, queue overflow, status failure all
  preserved.
- Final gate, one run each on pinned Unity 6000.3.26f1: EditMode 249/249
  (`final-editmode.xml`), PlayMode 25/25 (`final-playmode.xml`).
- Windows Mono development build: `result=Succeeded errors=0 warnings=0`
  (`unity-build-windows.log`, `Builds/Windows/FamilyRacing.exe`).
- Unchanged `Tools/RestartScenario.py` on the final build: passed 6/6
  (`restart-evidence/result.json`).
- Unchanged `Tools/AgentScenario.py` on the final build: passed 11/11
  (`agent-evidence/result.json`). The agent player was left running for
  review (PID 53552, run `bd63e51dcbbc4f84b248a0845406878c`).

## Not claimed / open

- The reconnect coverage is an in-process PlayMode loopback client, not an
  external-player reconnect or fault-injection run; external
  reconnect/sink-failure injection remains open.
- Physical keyboard/touch parity, human Hebrew review, Android device/ADB
  qualification, Windows IL2CPP, and NuGet restore remain open as before.
- Milestone 1 (Foundations) stays in progress.
