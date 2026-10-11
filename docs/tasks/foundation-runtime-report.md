# Foundation Runtime Report — Parts B/C (plus Part A corrections)

Date: 2026-10-11. Scope: Part A corrections, Runtime app/input/UI adapters,
repeatable project setup, token-authenticated loopback NDJSON bridge/sink,
Windows Mono development build + lead-scenario execution, one offline Android
attempt. No racing features; feature coverage stays `notImplemented`.

## Part A corrections (lead review findings)

- `GameCommand.Parameters` now returns `ReadOnlyDictionary<string,string>`
  wrapping a copied ordinal `Dictionary` — the exposed map cannot be cast back
  and mutated (regression test casts and attempts mutation).
- `CommandEnvelope` string concatenation replaced with structural ordinal
  comparison of all headers plus exact parameter keys/values/count —
  order-insensitive. The newline-collision resubmission
  (`action = "ui.language\nlanguage=en"`) now correctly rejects with
  `identityConflict` instead of being treated as a duplicate. Null vs empty
  parameter values compare distinctly. Map-reordering duplicate case retained.
- Report wording corrected (tests, not "assertions"; parser description fixed).

## Delivered

### Core (Unity-free)
- `FoundationState.PresentationState` DTO: screen geometry, inputSource
  (none/physical/agent), textDirection, hebrewFontAvailable, fontWarning,
  buildTarget, validationActive — populated by the runtime so UI decisions
  are reconstructable from the stream instead of hidden state.
- `FoundationService.SetPresentation` (stores an independent copy).

### Runtime (`Assets/FamilyRacing/Runtime`)
- `FoundationApp` — composition root; one `FoundationService`; 30 Hz
  observation ticks (`Time.unscaledDeltaTime`); commands publish
  action-boundary state+result before the next tick; `--validation-dir`
  opt-in gated to `UNITY_EDITOR || DEVELOPMENT_BUILD`; physical input +
  UI share `Execute` with the agent channel.
- `FoundationInput` — keyboard (Input System: arrows/A-D steering, Space
  drift, Shift boost, E gadget, R recover, C camera, Esc pause) + touch holds
  composed via `DriverInputComposer`; dispatches `input.set` on change plus a
  ~16-tick hold refresh — no per-frame command spam; suppressed while the
  agent owns input without clearing agent holds.
- `FoundationShell` (+`.Screens.cs`) — bilingual UI Toolkit shell: four
  animal profile cards (fox/rabbit/panda/raccoon, warm-coastal palette),
  settings (language/camera/reduced-shake/three volume sliders/save),
  sandbox (hold left/right/drift/boost, press gadget/recover, camera/pause/
  back, live input LEDs). Full-tree rebuild only on screen/locale/selection/
  slot-status change; all indicators update in place to preserve pointer
  capture. RTL via `root.languageDirection`; physical steering direction kept.
  Hebrew font warning surfaces when no Hebrew font asset exists.
- `Automation/ValidationJson` — strict inbound parsing: ≤16 KiB frames,
  depth ≤16 (`JsonReader.MaxDepth` + post-check), duplicate-property
  rejection, string-valued `parameters` enforced.
- `Automation/ControllerSession` — bounded line reads, 5 s auth window,
  hello handshake (protocolVersion/runId/token), graceful reject-close
  (drain + `Shutdown(Send)` to avoid RST discarding the error frame).
- `Automation/StateStream` — sequenced NDJSON record stream; background
  writer thread does all file/socket IO; queue bound 256 (overflow → sticky
  capture invalidation); trace bound 256 MiB; `capture-status.json` written
  at start/invalidation/finish; `finished` protocol: flush trace → stamp
  status complete → deliver `finished` → close.
- `Automation/ValidationBridge` — single controller on `127.0.0.1` port 0;
  `connection.json` holds protocolVersion/host/port/token/runId; fresh
  snapshot is the first live frame after authenticated hello at the current
  global sequence; inbound queue bound 128; disconnect → `ClearInput` +
  sticky `controllerDisconnected` invalidation; reconnect gets fresh
  snapshot + lookups (invalid status preserved); extra controllers rejected
  `controllerBusy`.
- `AssemblyInfo.cs` — `InternalsVisibleTo` for both test assemblies.

### Editor (`Assets/FamilyRacing/Editor`)
- `FoundationProjectSetup.Create()` — idempotent, non-destructive: generates
  `Generated/` URP asset+renderer, PanelSettings+PanelTextSettings, and the
  `Foundation.unity` bootstrap scene (camera/light/App+UIDocument+components)
  only when missing; registers the scene in build settings once; applies
  player settings (Mono backend for Windows, IL2CPP/ARM64/GLES3/minSdk 25
  for Android, landscape, app identifier, input = Both).
- `FoundationBuild.Windows()` / `.Android()` — batch entry points with
  `BuildReport` summaries and failure propagation.

### Tests
- `Tests/EditModeAdapter/AdapterEditModeTests.cs` (8): serializer round-trip,
  strict parse rejects (oversize/deep/duplicate/non-object), setup
  idempotence/completeness.
- `Tests/PlayMode/ValidationBridgePlayTests.cs` (5): real loopback —
  hello→attach snapshot→command boundary+result→finish trace/status;
  unauthorized rejection; oversize/duplicate/malformed frame rejection;
  second-controller busy; disconnect capture invalidation + input clear.
- `ProfileTests.cs` — `nullVehicles` stored-file invariant now writes its
  literal JSON (`"unlockedVehicles":null`); `TestJsonSerializer` Unity branch
  mirrors the product adapter (JsonUtility write + strict Newtonsoft read)
  because `JsonUtility.FromJson` silently maps a stored null list to an
  empty list.

### Project plumbing
- `Packages/manifest.json` — URP 17.3.0, Input System 1.20.0,
  Newtonsoft.Json 3.2.2, Test Framework 1.6.0, ugui + all engine modules —
  resolved entirely offline from bundled editor packages/cache; genuine
  `packages-lock.json` generated.
- `ProjectSettings/ProjectVersion.txt` pinned to 6000.3.26f1 (de8167e7440c).
- asmdefs: Core (noEngineReferences), Runtime, Editor, EditMode tests,
  EditModeAdapter tests, PlayMode tests. Test asmdefs use
  `precompiledReferences: ["Newtonsoft.Json.dll"]` — precompiled package
  DLLs are not auto-referenced into test assemblies.

## Evidence

### Final wave (wave-3, 2026-10-11)

| Check | Result | Artifact |
|---|---|---|
| EditMode suite | **236/236 pass** (predates wave-3 source; wave-3 changes have no EditMode tests, compile covered by PlayMode + build) | `Logs\test-editmode-wave2.xml`, `unity-editmode-wave2.log` |
| PlayMode suite | **16/16 pass** + narrow post-review regression **1/1 pass** (`StatusWriteFailureRejectsFinishedReply`) | `Logs\test-playmode-wave3b.xml`, `Logs\test-playmode-wave3c.xml` |
| Windows Mono rebuild | Succeeded, errors=0 warnings=0 | `Logs\unity-build-windows-4.log`, `Builds\Windows\FamilyRacing.exe` (`FamilyRacing.Runtime.dll` stamped 13:42; unchanged assemblies keep prior timestamps) |
| Lead persistent preview | ran to completion (lead-launched via Start-Process+WaitForExit; sidekick's earlier PID 61956 was transient-shell and exited 13:49; PID 52200 exited after proof) | `Logs\run-validation-lead\connection.json` |
| Lead `AgentScenario.py` on wave-3 build | **PASSED — all 9 checks**; harness verified capture complete+valid; player stays alive after finish | `Logs\evidence-lead\result.json` (frozen) + `commands.ndjson`, `observed.ndjson`; run dir `Logs\run-validation-lead` |
| Lead `AgentScenario.py` on post-blank-shell build | **PASSED — all 9 checks** (runId `df5ffd888d4746bb982f0a5b5d182aec`) | `Logs\evidence-visible-lead\result.json` (frozen) |
| `Tools\Verify.ps1` syntax | 0 parse errors | `Logs\check-verify-syntax.ps1` |
| Game-window capture | `PrintWindow` (PW_RENDERFULLCONTENT) — **lead-accepted as actual UI image**: Family Racing title + four profile cards visible; blank Hebrew button = known missing-font gap | `Logs\game-window.png` |
| Command→state registry | doc | `docs\FEATURE_COVERAGE.md` |
| Diff artifact (full, recovered-originals → current) | 46 files | `Logs\part-bc-wave3.diff` (13,894 lines); prior wave diff `Logs\review-wave2.diff` |

### Earlier evidence (pre-review waves — superseded source, kept for history)

| Check | Result | Artifact |
|---|---|---|
| EditMode suite (Unity) | 233/233 pass | `Logs\editmode-results-3.xml` |
| PlayMode suite (Unity) | 5/5 pass | `Logs\playmode-results-3.xml` |
| `AgentScenario.py` (pre-wave build) | passed — all 9 checks | `Logs\evidence-1\result.json` — **stale: predates wave-2/3 source** |
| Android APK | produced, 46,383,225 bytes | `Builds\Android\FamilyRacing.apk` — **pre-fix and unqualified**: Gradle ran without `--offline`; offline-ness unproven |

## Review waves fixed

### Wave-2 (9 findings — code paths confirmed by lead review)
1. Trace truncation on limit — `CreateNew` once, exact UTF-8 byte bound,
   `traceClosed` stops writes; prefix preserved.
2. Queue/status/trace/dispose failures unified into sticky `Failure` →
   `service.SetCaptureFailure`; abnormal exit writes invalid+incomplete status.
3. Async finish (`RequestFinish`/`FinishCompleted` poll); post-finish
   commands/controllers rejected `captureClosed`.
4. Per-session `SendLock`; work items carry exact target session — dead or
   replaced targets skipped, never retargeted; fresh snapshot first live frame.
5. Inbound frames session-associated; dead-session frames dropped; dead
   pending sessions cleared; total 5 s hello deadline.
6. Trailing JSON, overflow integer (`Int()` safe), invalid UTF-8,
   incomplete-EOF all rejected before mutation.
7. Touch steering right−left (opposing = 0); `MarkStale`/`OnDisable` reset;
   hold buttons capture pointer IDs and release on up/cancel/lost/detach.
8. Presentation refreshed inside command boundaries and per tick; focus/pause
   emits observable `input.clear`; stable semantic element IDs; discovery-based
   enable/disable; URP `renderScale=0.75`.
9. `deviceId` = fresh GUID per run; `connection.json` gains `participantId`.

### Wave-3 (3 final findings)
1. **Finish false-completion**: `WriteStatus` returns explicit bool;
   `ProcessFinish` recomputes validity after final writes (not from stale
   pre-write state); `finished` payload reflects writer's actual failure;
   `SendToTarget` requires the exact live current session; disconnect during
   finish invalidates via `DetectDisconnect` (no finish-requested skip);
   thread errors recorded into `captureError`. Wave-3 fix within the wave:
   `WriterLoop` no longer writes the finished item's trace line before
   `ProcessFinish` serializes it (was a null-line NRE → false EOF on client).
   New tests: `StatusWriteFailurePreventsComplete`,
   `DisconnectDuringFinishInvalidatesCapture`.
2. **Isolated PlayMode storage**: app fixtures replace `FoundationApp.service`
   with `FoundationService(temp ProfileStore)` via reflection before any
   dispatch — no `Application.persistentDataPath` profiles. Note: an earlier
   fixture revision could create a synthetic profile under the normal data
   folder; existing user data was never deleted or reset.
3. **`OnDisable` clears service input**: dispatches `input.clear` through the
   shared path plus physical/stale reset; test proves component disable →
   immediate neutral service input.

### Blank-shell fix (2026-10-11, post-proof)

Generated scene's `UIDocument.panelSettings` was `fileID:0` → preview
showed only the background color; setup never repaired existing scenes.
Fixed upstream (`AssignPanel` via `SerializedObject` + `RepairScenePanel`;
`FoundationShell` rebinds on detached root). Evidence: EditMode
RED→GREEN (`test-editmode-scene-red/green.xml`), real-scene PlayMode
`FoundationSceneRendersLiveShell` 1/1 (`test-playmode-scene3.xml`),
Windows rebuild `unity-build-windows-5.log` (includes the
`ProcessFinish` fix below — now resolved, not source-only), lead-accepted
window capture `Logs\game-window.png`, lead scenario re-proof
`Logs\evidence-visible-lead\result.json` (9/9), report
`docs/tasks/blank-shell-fix.md`. The proof preview exited after the run;
a fresh English menu instance at `Logs\run-validation-ready-menu` was
left for user interaction — convenience only, not test evidence.

### Post-review Error-Path Fix (Shipped)

`ProcessFinish`: when `WriteStatus` fails after the finished payload was
serialized, the item is re-stamped (`PatchFinished`) before `SendFinished`
so the socket reply can never stay `captureValid=true` after a durable
status failure. Covered by `StatusWriteFailureRejectsFinishedReply`
(wire-level, live bridge, status path swapped for a directory). Resolved —
shipped in `unity-build-windows-5` and exercised by the lead's re-proof
(`Logs\evidence-visible-lead`).

## Outstanding gaps (explicit, not claimed)

- Lead scenario **passed all 9 checks** on both the wave-3 build and the
  post-blank-shell build (`Logs\evidence-lead`, `Logs\evidence-visible-lead`);
  per the frozen `notVerified` list, still unverified: full physical
  keyboard/touch coverage, on-device UI, Hebrew font quality (blank glyphs —
  font asset absent), restart/reconnect/failure injection, Android device
  behavior, all racing gameplay.
- Game-window capture `Logs\game-window.png` was lead-reviewed and accepted
  as the actual UI (title + four profile cards); broader visual/Hebrew/audio
  quality remains unverified.
- `ProcessFinish` fix above is shipped in the latest rebuild and proven.
- Known limitation: the synchronous status-write fallback path can stall
  the writer on path failure; not all capture I/O is guaranteed
  non-blocking.
- An earlier fixture revision (pre-isolation) may have created a synthetic
  profile slot in the normal `Application.persistentDataPath` store;
  existing user files were left untouched and never deleted or reset.
- Android APK is pre-fix and unqualified (no forced-offline Gradle),
  46,383,225 bytes.
- Hebrew font asset absent — `fontWarning`/`warning.missingFont` surfaced.
- Windows IL2CPP module absent — Mono used; IL2CPP remains a release gap.
- A fresh English profile-menu instance runs at
  `Logs\run-validation-ready-menu` (lead-launched, persistent background
  shell) for user interaction — convenience only, not test evidence.
  Earlier instances (PID 63168 UI-capture preview, PID 52200, PID 61956)
  exited; the separate successful `run-validation-visible-lead` scenario
  preview was closed after its completed capture.
