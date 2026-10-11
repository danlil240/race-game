# Foundation Shell and Validation Bridge

This is the implementation brief for Task 3 of `docs/FOUNDATIONS_PLAN.md`, under the approved `GAME_PLAN.md`. Continue in the requested root; preserve existing files and do not commit/push.

## Scope and Decisions

- Build a usable profile/settings/input shell, not a race or progression system.
- Use Unity 6000.3.26f1 and its verified locally available package versions. Offline sources are acceptable; document how another installation resolves the same packages.
- A Windows Mono development preview is allowed while Windows IL2CPP is missing. Do not label that preview an IL2CPP build or release qualification. Android remains IL2CPP ARM64.
- Use UI Toolkit, Input System, a lightweight URP scene, and the existing pure Core profile/input/localization contracts.
- Do not redistribute Windows system fonts. Missing licensed Hebrew font assets are an explicit warning and outstanding qualification gap.
- Validation is opt-in in Editor/development builds via `--validation-dir <absolute-directory>`. Ordinary builds must not start a listener, create validation credentials, or write traces.
- Keep validation profiles under `<validation-dir>/profiles`, separate from ordinary `Application.persistentDataPath` profiles.
- Protocol/service/controller/sink implementations are product code; the external scenario runner and its assertions are lead-authored separately.

## Part A: Pure Application Service

**Files:** `Core/Foundation/FoundationState.cs`, `Core/Foundation/FoundationService.cs`, `Core/Automation/GameCommand.cs`, `Core/Automation/CommandResult.cs`, `Core/Automation/CommandDescriptor.cs`, and `Tests/EditMode/FoundationTests.cs`. Extend both shared test harnesses to include new Core subfolders.

**Namespace:** `FamilyRacing.Core.Foundation` for the state/service, `FamilyRacing.Core.Automation` for command contracts.

**Interfaces:**

- `GameCommand(int protocolVersion, string runId, string commandId, string participantId, string action, IReadOnlyDictionary<string,string> parameters)`; immutable values, copy parameters so later caller mutation cannot change a submitted command.
- `CommandResult`: public fields `commandId`, `action`, `status`, `code`, `tick`, `duplicate`; terminal statuses are `applied`, `rejected`, `failed`. No raw exceptions, save contents, or paths in `code`.
- `CommandDescriptor`: public fields `action`, `available`, `reason`, and parameter schema entries (`name`, `type`, `required`, `allowedValues`). Discovery reflects the current selection/screen/save state.
- `FoundationService(ProfileStore store, string defaultLanguage, string runId)`, `CommandResult Execute(GameCommand command)`, `FoundationState Snapshot()`, `IReadOnlyList<CommandDescriptor> Discover()`, `void AdvanceTick()`, `void ClearInput()`, `void SetCaptureFailure(string code)`.
- `FoundationState` is a deeply copied serializable DTO with fields `screen`, `language`, `activeSlot`, `paused`, `tick`, `input`, `slots`, `savedProfile`, `draftProfile`, `pendingSettings`, `persistenceStatus`, `captureValid`, `captureError`, and `featureCoverage`.
- `screen` is `profiles`, `settings`, or `sandbox`; initial screen `profiles`, activeSlot `-1`, no active profile, input neutral.
- Each slot exposes `slot`, `status` (existing load status name), and a copied profile or null. State must never expose storage paths/raw corrupt files.
- `input` is a serializable DTO with all seven existing DriverInput fields, using lower camel case. `savedProfile` and `draftProfile` are independent deep copies. `featureCoverage` explicitly marks tutorial, driving simulation, campaign, rewards/upgrades/cosmetics, LAN, and content as `notImplemented`.

**Commands (protocol 1, exact runId, participantId `local`):**

| Action | Parameters | Behavior |
|---|---|---|
| `profile.create` | required `slot` integer 0..3, `language` en/he | Only Missing slots. Save real factory profile, select it, show settings; failure leaves selection and prior files unchanged. |
| `profile.select` | required `slot` integer 0..3 | Loaded/recovered slots only; select copied saved/draft state and language. Recovered slots are read-only. Clear input and pending edits from previous selection. |
| `ui.navigate` | required `screen` profiles/settings/sandbox | Settings/sandbox require a selected profile. Leaving sandbox clears input; entering it starts neutral. No implicit saves. |
| `ui.language` | required `language` en/he | On profiles screen changes the language/default for explicit creation only. With a selected profile use settings.update instead; reject this action then. |
| `settings.update` | required `language`, `camera` chase/overhead, `reducedCameraShake` true/false, `musicVolume`, `effectsVolume`, `voiceVolume` finite invariant-culture decimals in [0,1] | Selected Loaded profile only. Validate every parameter first, then replace draft only, update visible language, mark pending. No disk write. |
| `settings.save` | no parameters | Selected Loaded profile only. Save candidate through ProfileStore, commit copies only on success. On failure restore the prior saved draft/language, mark `saveFailed`; persisted profile unchanged. |
| `input.set` | required `steering` finite [-1,1], `drift`, `boost`, `gadget`, `recover`, `changeCamera`, `pause` true/false, `durationTicks` integer 1..150 | Sandbox only, even when paused. Set current requested input; drift/boost/steering held for bounded ticks. Gadget/recover/changeCamera/pause are one-tick actions. ChangeCamera updates the draft camera; pause toggles service.paused. The sandbox has no race clock or motor. |
| `input.clear` | no parameters | Clear all input even while paused or outside sandbox. |
| `camera.toggle` | no parameters | Selected Loaded profile, toggle draft camera and mark pending. |
| `pause.toggle` | no parameters | Sandbox only; toggle paused, clear input. |

Reject missing/unknown/duplicate parameter names, invalid values, unsupported protocol, wrong run/participant, empty IDs, unavailable actions, and unknown actions before mutation. Parameter values are strings in the wire format. Parse numbers with invariant culture and reject nonfinite values.

Cache terminal command results per run, including failed/rejected commands. Same identity+same envelope returns the original result with `duplicate=true`, without applying again; reused identity with different payload returns `rejected/identityConflict`. Compare canonical parameter maps independent of map ordering. Maximum 10,000 identities; reject new identities with `commandCapacity` when full, never evict and then reapply an old identity. Expose lookup in the runtime control protocol without routing another mutation.

AdvanceTick increments the sandbox/observation tick even if paused (it is NOT a race clock), expires holds, and clears one-tick actions. ClearInput clears all held/transient state. Snapshot returns deep copies so callers cannot mutate the real application state.

**Required tests (tests first, real temp-file stores):**

- Missing four slots; create/select each; fresh service reload preserves identity/settings; creation fails safely over blocked/orphaned slots.
- Invalid headers/action/parameters reject with identical before/after state and disk files; parameter map copies prevent post-construction mutation.
- Draft update does not save; explicit save survives restart; serialization/disk failure preserves prior saved state and resets draft. Recovered/blocked profiles never become writable.
- Identical repeated create/save/pause/input command applies once; map reordering is identical; conflicting payload is rejected; cache capacity cannot evict and permit reapplication.
- Held input at steering extremes, duration 1/150, expiry, paused expiry, transient pulse clearing, leaving sandbox, selection changes, and ClearInput are neutral when required.
- Discovery reflects missing/read-only/selected/sandbox states; all implemented actions have schemas; future gameplay features explicitly remain unavailable.
- Mutating a returned snapshot/profile/list never changes service state; language/camera/volume validations match existing contracts.

## Part B: Unity Shell and Offline Project

**Files:** `Runtime/FamilyRacing.Runtime.asmdef`, `Runtime/UnityProfileSerializer.cs`, `Runtime/FoundationApp.cs`, `Runtime/FoundationInput.cs`, `Runtime/FoundationShell.cs`, `Editor/FamilyRacing.Editor.asmdef`, `Editor/FoundationProjectSetup.cs`, `Editor/FoundationBuild.cs`, generated `Scenes/Foundation.unity`, UI/URP assets, package manifest/lock and project settings.

- FoundationApp owns ONE FoundationService instance and dispatches all UI/keyboard/touch/validation commands into it. Serialize profiles with JsonUtility and avoid any Unity dependency in Core.
- FoundationInput uses Input System keyboard bindings from GAME_PLAN plus UI Toolkit touch pointer capture. Hold steering/drift/boost, pulse gadget/recover/camera/pause. Release/cancel/lost capture/focus/disable clears input; opposing sources resolve through DriverInputComposer. Input must not silently replace agent holds with neutral physical input on every frame; track sources and choose explicit active-source ownership for validation sessions.
- Use a 30 Hz foundation observation loop to AdvanceTick and produce state records; paused UI still receives commands. This does not simulate vehicles.
- FoundationShell displays four large distinct animal/color cards, settings with explicit Save and persistence status, and an input sandbox with large left/right/drift/boost/gadget controls, camera/pause/back actions and live input indicators. All callbacks use service commands. Disabled actions reflect discovery reasons; corrupt data is never reset automatically.
- Keep large touch targets, landscape layout with scroll fallback, intentional warm coastal palette, semantic element names, logical-order Hebrew and purposeful RTL layout. Physical steering arrows retain their direction. Missing licensed Hebrew font assets visibly warn rather than claiming qualification.
- Setup is repeatable: create only absent generated scene/UI/URP assets, preserve authored assets, add enabled foundation scene, configure landscape GLES3 Android, URP renderScale .75, package input handling and an appropriate UI text setting. Editor APIs must be verified against installed source/reference docs.
- Build methods: `FamilyRacing.Editor.FoundationBuild.Windows()` (Mono development preview), `.Android()` (ARM64 IL2CPP development APK), with explicit failed BuildReport/module errors. Stable Android application ID `com.daniel.familyracing`. Do not create/use release signing keys.
- Runtime/Editor test sources live in separate assemblies from pure shared tests. Test JsonUtility round-trip, setup idempotence, real UI commands vs service state, input lifecycle and source cleanup. Keep .NET harness free of Unity adapter test sources.

## Part C: Validation Transport and Sink

**Files:** `Runtime/Automation/ValidationBridge.cs`, `Runtime/Automation/StateStream.cs`, validation adapters/tests, `Tools/Verify.ps1` Unity modes, `docs/COMMANDS.md`, and coverage documentation.

Dedicated validation startup writes `<validation-dir>/connection.json` containing `protocolVersion:1`, the actual loopback `port`, fresh random `token`, `runId`, and `participantId:"local"`. Never put token in stream/logs; use an app-private/user-owned directory. Bind IPv4 loopback with an OS-assigned port. Single authenticated controller/observer connection is sufficient for this foundation; reject extra controllers explicitly.

Wire frames are UTF-8 NDJSON:

- Client first sends `{"type":"hello","protocolVersion":1,"runId":"...","token":"..."}`; incorrect/missing fields reject without exposing state. Authentication timeout 5 seconds.
- Server sends `{"type":"hello","protocolVersion":1,"runId":"...","buildVersion":"foundation-0.1","contentVersion":"foundation-0.1"}` then a full snapshot.
- Client commands contain `type:"command"` and every GameCommand header plus a string-valued `parameters` object.
- Control queries: `type:"discover"` -> descriptors plus featureCoverage; `type:"snapshot"` -> full state; `type:"lookup",commandId:"..."` -> cached terminal result or explicit unknown outcome; `type:"health"` -> capture validity/error, queue/file/connection status. These read-only queries do not consume identity-cache capacity.
- Query reply types are `discovery` (`commands`, `featureCoverage`), `lookup` (`commandId`, `found`, `result` or null), and `health` (`captureValid`, `captureError`). Queries include the common record metadata/sequence; credentials are never included.
- `type:"finish"` gracefully ends this capture: clear input, record final state, drain/flush trace output, send a sequenced `type:"finished",captureValid:<bool>,captureError:<code>` reply, then close the controller without marking this intentional close as capture failure. Gameplay may continue but this run's capture is closed. A second command connection cannot silently start a new capture under the same run/directory.
- All sequenced reply types carry the common state-record metadata. The complete trace starts with a snapshot at sequence 1 and ends with the flushed finished record. `<validation-dir>/capture-status.json` includes `captureValid`, `captureError`, and `captureComplete`; set captureComplete only after final trace flush, before delivering the finished reply.
- State records: `type:"snapshot"` on attach/query; `type:"state"` on tick/change; full replacement FoundationState payload under `state` for this small first slice (no delta optimization yet). Include `protocolVersion`, `runId`, `deviceId` (per-run identifier, not hardware identity), `participantId`, `buildVersion`, `contentVersion`, `sequence` (strictly increasing per run), `monotonicMs`, `tick`, and `commandId` where related.
- Command replies: `type:"result"`, fields from CommandResult plus runId/protocolVersion and sequence/tick correlation. Trace replies and state; duplicate replies preserve the original execution tick. Parse errors have safe `type:"error",code:"..."`.

Take immutable/deep snapshots on the game thread; do not read mutable Unity/game objects from worker threads. Append to `<validation-dir>/state.ndjson` and send live frames from background I/O. State/profile/draft/input/screen/pause/capture-health changes must appear at each action boundary; emit a full state after every tick, including unchanged ticks. Separate results preserve transient actions.

Publish the action-boundary state followed by its result before advancing another observation tick. The state carries the related commandId, so clients can inspect one-tick pulses even if their latest displayed state has already advanced.

Limits: inbound frame 16 KiB UTF-8, JSON depth 16, pending command queue 128, outgoing record queue 256, trace storage 256 MiB per run. Disconnect/invalid authentication/oversized/deep frames never mutate gameplay. Trace cannot overwrite a previous run: validation-dir must be new/empty on startup; reconnect uses the running instance's existing directory.

Backpressure/write/socket failure marks capture invalid with a stable error code and fails the run; never silently drop/coalesce required records or block the game thread. Stop capture after failure; expose failure through control health and `<validation-dir>/capture-status.json` even if state sink is unavailable. Controller disconnect invalidates the current full-capture run and clears its input on the game thread; observer resync does not erase the invalid status. Reconnect may still look up outcomes and obtain a fresh snapshot.

All TCP parsing/authentication, framing, bounded queues, thread shutdown, capture failure, reconnection, malformed input and build-disabled behavior need focused tests. Limits and trace schema are fixed here; implementers must not invent sampling/threshold changes.

## Verification and Completion

- Record actual RED/GREEN for new pure behavior, then Unity EditMode/PlayMode adapter evidence.
- Run setup/import with the exact editor using offline packages; preserve generated real lock/meta/settings files.
- Build and launch Windows Mono preview, run the lead-authored external scenario against that running instance, preserve its trace and screenshots when possible.
- Attempt Android development build if the local installed toolchain can build without downloads; report missing offline dependencies rather than reaching the network.
- Update status honestly: command/state coverage is foundation-only; screenshot/audio/layout/device/family and later-gameplay coverage gaps remain explicit. No milestone-complete claim without all required acceptance.
