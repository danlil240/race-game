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
| `ui.language` | changes unselected-profile UI/default language | `language`, `presentation.textDirection/fontWarning` | Core `UiLanguageChangesDefaultOnProfilesScreen`; PlayMode `LocaleBoundaryCarriesRtlPresentation`; not called by the external scenario |
| `settings.update` | changes draft only | `draftProfile`, `language`, `pendingSettings`, `presentation` | scenario check 4 (draft/save separation), check 5 (invalid update) |
| `settings.save` | persists and commits draft | `savedProfile`, `draftProfile`, `slots[].profile`, `pendingSettings`, `persistenceStatus` | scenario check 4 (save and duplicate delivery); Core save-failure tests |
| `input.set` | changes held input and pulses | `input.*`, `draftProfile.camera`, `pendingSettings`, `paused` | scenario check 7 (controls and camera pulse), check 8 (paused expiry); PlayMode `OpposingTouchInputsComposeNeutral` |
| `input.clear` | clears held/transient input | `input.*` | scenario check 8 explicitly clears input; separate PlayMode dead-session/disable tests cover cleanup |
| `camera.toggle` | toggles draft camera | `draftProfile.camera`, `pendingSettings` | Core `CameraToggleRequiresWritableSelectedProfile`; external scenario tests only a conflicting-identity rejection of this action, not its successful execution |
| `pause.toggle` | toggles sandbox pause, clears input | `paused`, `input` | scenario check 8 |

## Streamed state (every record carries)

`type`, `protocolVersion`, `runId`, `deviceId` (per-run random),
`participantId` (`"local"`), `buildVersion`, `contentVersion`, `sequence`
(contiguous), `monotonicMs`, `tick`; state/snapshot bodies carry `screen`,
`language`, `activeSlot`, `slots[]`, `savedProfile`, `draftProfile`,
`pendingSettings`, `input`, `paused`, `persistenceStatus`,
`captureValid/captureError`, `presentation`, `featureCoverage`. Camera mode
is a profile field (`savedProfile.camera` / `draftProfile.camera`), not a
top-level state field. Hello and parser-error replies are unsequenced;
they must not be mistaken for sequenced state records.

## Verified (frozen lead scenarios — `evidence-lead/result.json` and `evidence-visible-lead/result.json`, 9 checks each on successive builds)

Isolated fresh profiles; command discovery; real profile
creation/persistence; draft/save separation, duplicate protection, outcome
lookup; conflicting identity and invalid settings rejected; profile
switching preserves identity/settings; held controls, transient pulses,
camera, bounded expiry; paused observation + input cleanup; unimplemented
features rejected; capture finished cleanly (`captureValid=true`,
`captureComplete=true`).

## Known QA gaps (`notVerified` — do not read as passed)

- Full physical keyboard/touch coverage and on-device UI (the accepted
  `PrintWindow` capture `Logs\game-window.png` shows the rendered shell —
  title + four profile cards — but is a static image, not input coverage;
  a real pointer down/up profile click is covered by PlayMode
  `FoundationSceneRendersLiveShell`).
- Reconnect and failure-injection beyond PlayMode coverage. Restart of
  explicitly saved synthetic profiles is now evidenced
  (`Logs\qualification-continuation\restart-final\result.json`, 6/6 checks)
  — it does not cover graceful-exit autosave, the normal user profile path,
  or reconnect.
- Visual/Hebrew/audio quality — Hebrew font assets absent by design this
  iteration (`fontWarning` surfaces it).
- Android device behavior (APK is now built by the enforced-offline path —
  `Logs\android-offline\20261011-123733-d853a75210284129ba058d708bd3606e\result.json` —
  but no ADB/on-device run has been attempted).
- Windows IL2CPP release configuration (preview is Mono development).
- All racing gameplay: simulation, campaign, rewards/upgrades/cosmetics,
  LAN multiplayer, content — `notImplemented`, later milestones.
