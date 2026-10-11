# Feature coverage registry — foundation slice

Registry mapping the ten foundation commands to the actual state
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
| `pause.toggle` | toggles sandbox pause, clears input | `paused`, `input` | scenario check 8 |

## Streamed state (every record carries)

`type`, `protocolVersion`, `runId`, `deviceId` (per-run random),
`participantId` (`"local"`), `buildVersion`, `contentVersion`, `sequence`
(contiguous), `monotonicMs`, `tick`; state/snapshot bodies carry `screen`,
`language`, `activeSlot`, `slots[]`, `savedProfile`, `draftProfile`,
`pendingSettings`, `input`, `paused`, `persistenceStatus`,
`captureValid/captureError`, `presentation` (incl. `backlogSteps`,
`stepOverloads`), `featureCoverage`, `driving`, `drivingEvents`,
`droppedDrivingEvents`. Boundaries may also arrive as `delta` records
(`base` = previous boundary sequence, recursive `changed` merge, dot-path
`removed` list) — an independent reconstructor rebuilds the full state at
every boundary; a delta alone is never a complete state. Camera mode
is a profile field (`savedProfile.camera` / `draftProfile.camera`), not a
top-level state field. Hello and parser-error replies are unsequenced;
they must not be mistaken for sequenced state records.

## Verified (external `Tools/AgentScenario.py` runs on real players)

Latest run on the schema-2 driving build: 21/21 checks
(`Logs\evidence-agent6\result.json`) — adds tutorial onboarding and
command-driven driving checks over the delta stream. Previous run on the
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

Accepted run on the schema-2 build (`Logs\evidence-driving\result.json`,
27 checks, run `Logs\validation-driving2\`): both `car-01` and
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
capacity was measured at 1.6 MiB trace / 210 commands / 2497 boundaries
over 76 s and projected to ~37 MiB / ~4965 identities over the
representative 30-minute schedule — under the 256 MiB trace cap and the
10,000-identity bound, with zero backlog steps, zero overloads and zero
dropped driving events.

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
  latest run `Logs\android-offline\20261011-193051-31b4d38aaa6f49fa9d4791f5fe422f76\result.json`
  rebuilt from current source — but no ADB/on-device run exists; the
  bundled platform-tools `adb devices` check on 2026-10-11 found no
  attached device).
- Windows IL2CPP module is now installed for the pinned editor
  (`CheckEnvironment` OK) but no IL2CPP build has been produced or qualified
  — preview remains Mono development.
- Remaining racing gameplay: campaign, rewards/upgrades/cosmetics,
  LAN multiplayer, richer content — `notImplemented`, later milestones.
  The graybox driving slice (`drivingSimulation`, `drivingOnboarding`)
  is qualified by the DrivingScenario run and PlayMode suites above.
- A real 30-minute soak is projected, not run — the DrivingScenario
  capacity measurement extrapolates observed stream load honestly.
