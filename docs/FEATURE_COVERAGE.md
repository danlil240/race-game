# Feature coverage registry — foundation slice

Registry mapping the foundation and LAN commands to the actual state
fields they control and the evidence that actually covers them. Proof column
reads the frozen lead scenario (`Logs/evidence-lead/result.json`, all 9
checks passed on the wave-3 Windows build, run-validation-lead) plus the
Unity suites. The six later-gameplay categories named in `FeatureCoverage`
are explicitly `notImplemented`; additional QA gaps are listed below.

## Command → state-field map

| Action | Mutates / controls | Streamed state fields | Proof |
|---|---|---|---|
| `profile.create` | creates and selects a slot | `activeSlot`, `slots[].profile/status`, `savedProfile`, `draftProfile`, `screen`, `persistenceStatus` | scenario check 3; Core `CreateAndSelectEachSlotSurvivesFreshService` |
| `profile.select` | changes selection, clears input and pending edits | `activeSlot`, `savedProfile`, `draftProfile`, `language`, `input`, `pendingSettings` | scenario check 6 |
| `ui.navigate` | changes screen, clears sandbox input on transition | `screen`, `input` | scenario enters sandbox before check 7; Core `LeavingSandboxClearsInput` |
| `ui.language` | changes unselected-profile UI/default language | `language`, `presentation.textDirection/fontWarning` | Core `UiLanguageChangesDefaultOnProfilesScreen`; PlayMode `LocaleBoundaryCarriesRtlPresentation`; external scenario `agent-evidence/result.json` check "packaged bilingual fonts and locale command boundaries" (he + en) |
| `settings.update` | changes draft only | `draftProfile`, `language`, `pendingSettings`, `presentation` | scenario check 4 (draft/save separation), check 5 (invalid update) |
| `settings.save` | persists and commits draft | `savedProfile`, `draftProfile`, `slots[].profile`, `pendingSettings`, `persistenceStatus` | scenario check 4 (save and duplicate delivery); Core save-failure tests |
| `input.set` | changes held input and pulses | `input.*`, `draftProfile.camera`, `pendingSettings`, `paused` | scenario check 7 (controls and camera pulse), check 8 (paused expiry); PlayMode `OpposingTouchInputsComposeNeutral` |
| `input.clear` | clears held/transient input | `input.*` | scenario check 8 explicitly clears input; separate PlayMode dead-session/disable tests cover cleanup |
| `camera.toggle` | toggles draft camera | `draftProfile.camera`, `pendingSettings` | Core `CameraToggleRequiresWritableSelectedProfile`; external scenario `agent-evidence/result.json` check "camera toggle command updates selected profile draft" plus the conflicting-identity rejection |
| `pause.toggle` | toggles sandbox pause, clears input; inside a LAN session it is the host's shared-pause toggle / the client's pause request | `paused`, `input`, `network.paused`, `network.pauseRequests` | scenario check 8; PlayMode `NetworkLifecyclePlayTests` |
| `lan.host` | binds host socket, opens the lobby, starts discovery responder | `network.role/phase/descriptor`, `screen` (`lan-lobby`) | `NetworkCommandTests`, `NetworkScreensPlayTests` |
| `lan.discover` / `lan.discovery.stop` | bounded host search on the discovery port | `network.phase`, `network.discovered` | `NetworkCommandTests`, `NetworkScreensPlayTests` |
| `lan.join` | async connect+join on a discovered/manual endpoint | `network.phase` (`connecting`→`lobby`), `network.participants`, `pendingOperations` | async accepted→terminal once; `NetworkCommandTests`, `NetworkScreensPlayTests` |
| `lan.configure` | host-only lobby event/class selection | `network.descriptor.eventId/classId`, roster `vehicleId` legality | `NetworkCommandTests` (`NonHostCannotStartOrConfigure`, `ClassChangeClearsReady`) |
| `lan.vehicle` | participant's vehicle inside the configured class | `network.participants[].vehicleId` | `NetworkCommandTests`, `NetworkScreensPlayTests` |
| `lan.ready` | participant readiness (client: async request) | `network.participants[].ready`, `pendingOperations` | `NetworkCommandTests` (`ClientReadyCompletesAfterHostAck`) |
| `lan.start` | host-only race start once every occupied seat is ready | `network.phase` (`countdown`→`racing`), `networkRace` | `NetworkScreensPlayTests`, `NetworkLifecyclePlayTests` |
| `lan.pause.request` / `lan.pause.set` | client pause request / host shared pause | `network.pauseRequests`, `network.paused` | `NetworkLifecyclePlayTests` |
| `lan.reconnect` | credentialed rejoin from recovery using stored material | `network.phase` (`recovery`→`reconnecting`→…), roster `controlSource`/`ownershipEpoch` | `LanSessionTests` credentialed-rejoin coverage; `NetworkScreensPlayTests` |
| `lan.results.request` / `lan.results.retrySave` | client result re-offer / local persistence retry | `network.results[]` delivery states | `NetworkResultsPlayTests`, `NetworkRewardTests` |
| `lan.rematch` | host-only return to lobby keeping retained results | `network.phase` (`results`→`lobby`) | `NetworkResultsPlayTests` |
| `lan.leave` | clean teardown; the only session exit | `network.role`→`none`, `screen`→`lan-browser` | `NetworkScreensPlayTests` |

## Streamed state (every record carries)

`type`, `protocolVersion`, `runId`, `deviceId` (per-run random),
`participantId` (`"local"`), `buildVersion`, `contentVersion`, `sequence`
(contiguous), `monotonicMs`, `tick`; state/snapshot bodies carry `screen`,
`language`, `activeSlot`, `slots[]`, `savedProfile`, `draftProfile`,
`pendingSettings`, `input`, `paused`, `persistenceStatus`,
`captureValid/captureError`, `presentation` (incl. `backlogSteps`,
`stepOverloads`, and the actually-applied `presentation.camera` facts),
`featureCoverage`, `driving`, `drivingEvents`,
`droppedDrivingEvents`, and the schema-3+ network slices `network`
(always populated: `role`, `phase`, `localParticipantId`, `descriptor`,
`participants[]`, `paused`, `pauseRequests`, `lastError`,
`pendingOperations[]`, `discovered[]`, `results[]`),
`networkRace` (host-confirmed race state; null outside a LAN race),
`prediction` (client-side predicted motor/ack/correction facts; null on
the host and outside a LAN race) and `remotePresentation` (per-remote
interpolation diagnostics). Reconnect credentials and transport
identities never appear in observable state.
Boundaries may also arrive as `delta` records
(`base` = previous boundary sequence, recursive `changed` merge, dot-path
`removed` list) — an independent reconstructor rebuilds the full state at
every boundary; a delta alone is never a complete state. Camera mode
is a profile field (`savedProfile.camera` / `draftProfile.camera`), not a
top-level state field. Hello and parser-error replies are unsequenced;
they must not be mistaken for sequenced state records.

## Verified (external `Tools/AgentScenario.py` runs on real players)

Latest run on the schema-2 driving build (M2 closeout): 21/21 checks
(`Logs\evidence-m2c-agent\result.json`) — tutorial onboarding and
command-driven driving checks over the delta stream. Prior run on
the same schema (`Logs\evidence-agent6\result.json`). Previous run on the
final packaged-font build: 11/11 checks
(`Logs\qualification-fonts\agent-evidence\result.json`) — isolated fresh
profiles; command discovery; packaged bilingual fonts and locale command
boundaries; real profile creation/persistence; draft/save separation,
duplicate protection, outcome lookup; conflicting identity and invalid
settings rejected; profile switching preserves identity/settings; held
controls, transient pulses, camera, bounded expiry; camera toggle updates
the selected profile draft; paused observation + input cleanup;
unimplemented features rejected; capture finished cleanly.

Earlier frozen lead scenarios: `evidence-lead/result.json` and
`evidence-visible-lead/result.json`, 9 checks each on successive builds
(same coverage minus the packaged-font and camera-toggle steps).

## Verified (external `Tools/StreamRobustnessScenario.py` expected-fault run)

Accepted run: 18/18 checks on the unchanged reviewed Windows build
(`Logs\qualification-external-stream-robustness\external-run-2\result.json`)
— graceful disconnect (`controllerDisconnected` 0.032 s; reconnect
snapshot sequence 105, tick 96, original create tick 81), TCP reset
(`controllerDisconnected` 0.016 s; a reset may surface as
`controllerDisconnected` or `streamError`), stalled sink (`streamError`
2.094 s with `SO_RCVBUF` 1024 and 32 legal lookup frames carrying
16000-byte ASCII commandIds, each below the 16 KiB frame limit; snapshot
sequence 199, tick 158). Per phase: durable invalid/incomplete status
while the process lived — before reconnect, finish or teardown; a fresh
same-run snapshot (same run/device/build identity, advancing sequence
and ticks, neutral input); original outcome lookup at the original
execution tick; identical command redelivery returned `duplicate=true`
with unchanged synthetic save-file hashes; finish stayed invalid and the
trace prefix stayed byte-frozen (each trace begins at sequence 1 and
ends before the fault-observation records). Passing means the injected
faults were correctly detected — no capture is certified by this run.

## Verified (external `Tools/DrivingScenario.py` driving observation run)

Latest run on the M2-closeout build (`Logs\evidence-m2c-driving\result.json`,
28 checks, run `Logs\run-m2c3\`; prior run `Logs\evidence-t7-driving\result.json`,
27 checks; earlier accepted run
`Logs\evidence-driving\result.json` / `Logs\validation-driving2\`): both `car-01` and
`motorcycle-01` completed the authored `intro-01` lap through the shared
command path only — bounded `input.set` holds re-evaluated from observed
motor state (never teleports, never sleeps-as-proof): ordered gates
1–4 plus the start-line wrap finish, `boostStart` with charge drain,
`driftStart`/`driftEnd` through the west turn, the landmark-ramp
`becameAirborne`/`landed` episode, a mid-drive explicit resnapshot that
re-baselined reconstruction onto the live race identity, pause freezing
`simulationTick`/`elapsedTicks` while observation ticks advanced,
`camera.toggle` switching the real `driving.camera.mode`, a latched
recovery consumed on a simulated boundary respawning at the authored
gate-3 safe pose with no manufactured progress, `race.restart` producing
a new race identity, `race.exit` returning to practice, and durable
screenshots inside the run directory. The full `state.ndjson` was
independently reconstructed (snapshot/delta chain, contiguous sequence,
boundary-per-tick including unchanged ticks, schema-2 validation);
capacity was measured at ~1.7 MiB trace / 205 commands / 2537 boundaries
over ~78 s and projected to ~39 MiB / ~4756 identities over the
representative 30-minute schedule — under the 256 MiB trace cap and the
10,000-identity bound, with zero backlog steps, zero overloads and zero
dropped driving events.

## Verified (external `Tools/DrivingSoak.py` sustained-session run)

Task-7 review pass (`Logs\evidence-t7-soak3\result.json`, plus the two
disclosed prior runs `evidence-t7-soak`/`evidence-t7-soak2` whose
measurements are equivalent): a fresh validation player drove the
authored course for a **real** 1800 s — 55 completed laps (28 `car-01` /
27 `motorcycle-01`), 23 `race.restart` identities, 55 camera toggles
covering both modes, 0 driving incidents. Measured, not projected:
~30.0 Hz steady tick cadence (p50 31 ms / p95–p99 47 ms boundary gaps,
1 gap >100 ms, max 344 ms), zero step-overload growth and zero sampled
backlog after a disclosed ~2.5 s startup warm-up, process working set
375→541 MB (private peak ~967 MB), 45.8 MB trace, 5,316 command
identities, 59,409 boundaries, empty outgoing queue, capture valid
end-to-end. The bounded 128-event `drivingEvents` window evicted 835
events — every eviction durably marked via `droppedDrivingEvents`; the
window is diagnostic feed, not required boundary history. Scope: Windows
Mono development player only — ordinary-build performance is
structurally unmeasurable for this harness (the `--validation-dir`
endpoint is compiled out of non-development builds, so no agent can
attach; a comparison would need in-build scripting or manual profiling)
and on-device performance is not inferred from it.

## Verified (synthetic UI Toolkit PlayMode callbacks)

`FoundationSettingsPlayTests` (5 tests, RED 4-fail before the fix, GREEN 5/5)
drives the rendered settings screen end-to-end — real `Slider.value` change
callbacks and pooled pointer down/up events on `Button`s — against a real
`FoundationService` and a synthetic `ProfileStore`: sequential volume edits
keep all pending values, the reduced-shake button toggles both directions,
command-driven `settings.update` drafts survive later UI edits, an explicit
save persists all six fields through a store reload on the same synthetic
directory, and a refused save restores the saved draft with byte-identical
files (`Logs\qualification-settings-parity\`; report
`docs/tasks/foundation-settings-parity-report.md`). These are synthetic UI
Toolkit callbacks — not OS/physical touch and not a process restart.

## Verified (synthetic input/UI parity PlayMode callbacks)

`FoundationInputPlayTests` (10 tests) drives a virtual `Keyboard` through
`InputTestFixture` into `FoundationInput.Pump`: arrows/A-D steering with
release-to-neutral, opposing arrows neutral, Space drift + Shift boost
holds, E/R one-frame pulses, C→`camera.toggle`, Esc→`pause.toggle`,
agent-ownership and off-sandbox suppression, `OnDisable`→`input.clear`,
`ResetPhysical`. `FoundationScreensPlayTests` (8 tests) drives pooled
pointer events on the remaining screens: profile-card create/select,
profiles language toggle + RTL, navigation/back buttons, sandbox hold
buttons (down→held, up→release, cancel→release through `input.set` parity)
and the gadget pulse. These are synthetic callbacks, not OS input — they
also exposed and locked a real defect: `Button.Clickable` consumed primary
pointer-downs before the hold handlers, so `AddHoldButton` registrations now
run in the trickle-down phase (`Logs\qualification-gaps\`; report
`docs/tasks/foundation-gaps-report.md`).

## Verified (LAN screens and schema-4 stream, Task 9)

`NetworkScreensPlayTests` drives the five LAN screens with synthetic
pointer events against a real `FoundationService` + `LanSessionService`
(faked transport seams): host lands on the phase-derived lobby,
discovery toggles and discovered-host cards join the advertised
address, lobby configure/vehicle/ready/start round-trip real commands,
client-side host-only controls render disabled with the honest reason,
the race HUD exposes shared clock/standings/pause plus LTR-locked
steering controls, hold buttons produce bounded `input.set`, host loss
lands on the recovery screen and reconnect reuses the stored endpoint,
the results screen renders the delivery ledger, and Hebrew renders the
LAN surface RTL while addresses/participant ids stay LTR.
`NetworkStreamPlayTests` proves schema-4 record identity (all required
fields plus `stateSchemaVersion` on boundary, discovery and nested
state), deep-copy isolation of every emitted network slice under
producer mutation, and independent snapshot+delta reconstruction where
each delta names its previous boundary sequence.

## Verified (external fault + real-store scenarios on real players)

`Tools\FinishDeliveryScenario.py` 6/6 on the post-gap build
(`Logs\qualification-gaps\finish-delivery\result.json`): a stalled
non-reading controller sends `finish`; the writer's bounded send produces
terminal `streamError` status, fresh controllers are rejected
`captureClosed` (game thread never blocked), and the trace prefix stays
frozen. `Tools\NormalProfileScenario.py` 6/6
(`Logs\qualification-gaps\normal-profile\result.json`): two real processes
with `--validation-normal-profiles` — create+save lands in the REAL
`persistentDataPath\Profiles` store, `taskkill` (WM_CLOSE, no `/F`) stamps
terminal `captureError:"processExit"`, the saved profile survives into the
relaunch's saved AND draft state, unsaved drafts are never persisted, and a
clean finish completes the second capture.

## Verified (physical-backend observability, Task 11)

The physical backend now publishes complete diagnostics on the state
stream while the compact LAN wire keeps its bounded projection —
proven by PlayMode on both ends:

- `DrivingStreamPlayTests.PhysicalDiagnosticsReachTheStreamBoundary`
  runs a real `PhysicsVehicleSimulation` through `FoundationService` +
  `ValidationBridge` and reconstructs the boundary independently:
  `driving.simulationBackend` reports `physicalAssisted`, the 64-hex
  `contentFingerprint` is present, `lastRecoveryReason` is empty before
  any recovery, the ordered three-sample `substepPath` endpoint agrees
  with the motor pose, `driving.simulation` reports ≥6 ticks, exactly
  two substeps per tick, one body and zero invalid-state faults, the
  full four-wheel `motor.physics` state is streamed verbatim, and the
  physics forward velocity agrees with the legacy motor speed.
- `NetworkStreamPlayTests` pins the wire boundary:
  `BoundaryRecordsCarrySchema4IdentityAndSlices` now streams the full
  diagnostic slice (backend identity, fingerprint, ordered substep
  path, world counters, wheel array, recovery epoch), and
  `WireEncodeStripsStreamOnlyDiagnostics` proves the compact snapshot
  strips `substepPath`/`simulation`/`MotorState.physics` before encode,
  rebuilds `motor.physics` from the compact `racer.physics` DTO on
  decode, keeps the small identity strings crossing, stays under the
  16 KiB ceiling, and never mutates the host's live state.
- Core `OfflineTestRunner` covers the diagnostic counters directly
  (`VehicleSimulationBoundaryTests` and the backend diagnostics tests:
  arcade honestly reports `substepsSimulated = 0`, the physical world
  reports two substeps per logical tick, invalid requests/restores
  increment `invalidStateFaults` instead of vanishing).
- Python tooling: `Tools\TestPhysicsScenario.py` +
  `Tools\TestPhysicsSoak.py` lock the validators (schema-4 physical
  slices, recovery-epoch monotonicity, fingerprint agreement, compact
  wire projection, trace reconstruction, peer-loss honesty).
- Live tools delivered for the physical player:
  `Tools\PhysicsScenario.py <run>\connection.json --evidence <dir>`
  (agent-driven physics-01 qualification on both physical vehicles) and
  `Tools\PhysicsSoak.py --connections ... --minutes 30`
  (2–4-player sustained physical LAN observation). A live scenario/soak
  run requires the `Builds\Windows-Physics\` player — see
  `docs/COMMANDS.md`.

## Known QA gaps (`notVerified` — do not read as passed)

- Physical input/UI parity is covered by synthetic PlayMode callbacks only
  (keyboard device simulation + pointer events); OS-level key/touch and
  on-device UI remain unverified.
- Reconnect and sink failure: external-player expected-fault coverage
  now exists — see the section above (18/18 on the reviewed build,
  re-run on the post-gap build `Logs\qualification-gaps\stream-robustness\`)
  plus the new stalled-finish and graceful-exit evidence above.
  Loopback PlayMode covers the sink side
  (`ValidationStreamRobustnessPlayTests`; report
  `docs/tasks/foundation-stream-robustness-report.md`).
- Visual/Hebrew/audio quality — packaged Noto Latin/Hebrew/Symbols2 fonts
  are now bundled (OFL, provenance in `Assets\FamilyRacing\Fonts\README.md`),
  ATG enabled, and live readiness recomputes each refresh; lead-accepted
  en/he window captures exist (`Logs\qualification-fonts\player-evidence-2\screenshot-*.png`).
  Human Hebrew review is still not claimed.
- Android device behavior (APK is built by the enforced-offline path —
  latest run `Logs\android-offline\20261011-234621-67c0086c90654c0383c539db54c2f6c8\result.json`
  rebuilt from current source — but no ADB/on-device run exists; the
  bundled platform-tools `adb devices` check on 2026-10-12 found no
  attached device).
- Windows IL2CPP module is now installed for the pinned editor
  (`CheckEnvironment` OK) but no IL2CPP build has been produced or qualified
  — preview remains Mono development.
- Remaining racing gameplay: campaign, rewards/upgrades/cosmetics and
  richer content stay `notImplemented`, later milestones. The LAN
  slice (`lanScreens`, `lanSession`, `networkRace`, prediction /
  interpolation observation) is qualified by the EditMode/PlayMode
  suites plus `Tools\NetworkScenario.py`, which attaches to 2-4
  independently launched players via their connection files (`--connections`,
  ADB-forwarded loopback included) or self-launches locally (`--launch`,
  adding the labeled fault-injection segment for AI takeover, credentialed
  rejoin, and host loss). `Tools\NetworkSoak.py` now provides the
  sustained-session tool (`--connections` attach, `--minutes` wall-clock
  duration, per-device health/cadence sampling, once-only delivery
  ledger across rematches); a real multi-device 30-minute soak is
  hardware-gated. Cross-device physical LAN qualification ran as
  M3 task 10: every hardware/human matrix row is **not verified** —
  see `docs/tasks/network-prototype-report.md`.
  The graybox driving slice (`drivingSimulation`, `drivingOnboarding`)
  is qualified by the DrivingScenario run and PlayMode suites above.
- A real 30-minute solo soak now exists — see the DrivingSoak section
  above (Windows dev player). The multi-player equivalent
  (`Tools\NetworkSoak.py`) is delivered and unit-covered
  (`Tools\TestNetworkSoak.py`) but its 30-minute multi-device run is
  hardware-gated. On-device soak, ordinary-build frame performance
  (structurally unmeasurable by this harness — validation endpoints are
  compiled out of ordinary builds, proven in the task-10 report),
  render frame times, and the four-racer/tablet-host target remain
  unmeasured.
- Ordinary Windows LAN play is **not verified**: it can only be
  demonstrated by humans driving ordinary builds on two machines —
  validation sockets are compiled out of ordinary builds by design, so
  no agent attach exists. The Android analogue is likewise open: the
  offline APK pipeline builds with `BuildOptions.Development`
  (`DEVELOPMENT_BUILD` defined, validation bridge present for ADB
  attach); an ordinary APK path is documented in
  `docs/NETWORK_TEST_RUNBOOK.md` ("Android: ordinary (non-development)
  build") but no Verify mode produces one, and ordinary-build
  performance needs physical profiling rather than this harness.
