# Foundation Settings Parity Implementation Plan

> **For agentic workers:** Use subagent-driven-development and test-driven-development. Preserve existing changes; do not commit or push the game repository.

**Goal:** Close the shell UI → command parity gap on the settings screen: every UI
control edit must merge against the live service draft, so sequential control
changes and command-driven updates never silently drop each other's pending
values.

**Root cause (settled):** `FoundationShell.BuildSettings` captures the
`FoundationState` passed at build time inside every control callback.
`UpdateSettings(state, ...)` merges unspecified fields from that *stale* draft:
a second slider/button edit re-submits the first edit's pre-change values, and
`shake:` is computed from the captured draft so the button can never toggle
twice. `FoundationService.Snapshot()` already deep-copies, `settings.update`
requires all six fields and rebuilds the draft from the submitted values — the
Core contract is correct and stays unchanged.

**Design:** `UpdateSettings` becomes
`UpdateSettings(string language = null, string camera = null,
bool toggleShake = false, float? music = null, float? effects = null,
float? voice = null)`. It reads `app.Service.Snapshot().draftProfile` at
invocation time (returns when null), merges every unspecified field from that
live draft, resolves shake as
`toggleShake ? !draft.reducedCameraShake : draft.reducedCameraShake`, and
dispatches the unchanged full six-field `settings.update` schema. BuildSettings
callbacks no longer capture `state`; the local `draft` stays only for initial
slider values. `Refresh` keeps rebuilding only on
screen/language/activeSlot/slot-status changes — no rebuild-on-edit workaround.

**Tech Stack:** Unity 6000.3.26f1 (pinned editor), C#, existing NUnit/Unity
Test Framework PlayMode suite, real `FoundationService`/`ProfileStore` in a
synthetic GUID directory.

**Spec:** GAME_PLAN.md section 3 (command/UI parity); docs/FOUNDATIONS_PLAN.md
Task 3.

## Global Constraints

- Continue in the existing feature/family-racing checkout; preserve all prior
  uncommitted/index work. No commits, staging, pushes, installs, downloads,
  network, vault edits, or normal user-profile access.
- Only one pinned Unity Editor process may use this project at a time; never
  kill pre-existing processes.
- No production changes outside
  `Assets/FamilyRacing/Runtime/FoundationShell.Screens.cs`; no Python scenario
  edits; no new wire schema or commands.
- Fixture isolation: an inactive host GameObject carries `FoundationApp` so
  `Awake` never touches `Application.persistentDataPath`; the synthetic
  `ProfileStore` lives under a GUID temp directory the fixture deletes; the
  PanelSettings asset is an instantiated clone, never the shared asset.
- Tests use real rendered widgets and real event paths
  (`Slider.value` change callbacks, pooled pointer down/up on `Button`); only
  composition-root private fields (`service`, `runId`) are reflection-injected.
- Keep Foundations in progress. Physical Android, human Hebrew acceptance and
  gameplay features remain open.

## Task 1: Live-Draft Settings Merge + UI Parity Evidence

**Files:** `Assets/FamilyRacing/Runtime/FoundationShell.Screens.cs`;
new `Assets/FamilyRacing/Tests/PlayMode/FoundationSettingsPlayTests.cs` + meta;
new `docs/tasks/foundation-settings-parity-report.md`; focused appends to
`README.md`, `IMPLEMENTATION_STATUS.md`, `docs/FEATURE_COVERAGE.md`,
`docs/FOUNDATIONS_PLAN.md`. Evidence under `Logs/qualification-settings-parity/`.

- [x] Add `SequentialVolumeControlsPreservePendingEdits`: real `Slider.value`
  callbacks set music/effects/voice 0.2/0.35/0.45 with `Refresh` between;
  assert the same slider instance survives (in-place update), the draft keeps
  all three values, `pendingSettings`, and unchanged saved profile/file bytes.
  Expected RED: stale merge resets earlier volumes.
- [x] Add `ShakeButtonTogglesBothDirections`: pooled pointer down/up on
  `settings.reducedShake` toggles true then false without a tree rebuild and
  without touching the save file. Expected RED: second press stays true.
- [x] Add `CameraAndVolumeControlsMergeLatestCommandDraft`: a dispatched
  `settings.update` after the shell was built (overhead/true/0.2/0.35/0.45)
  must survive later UI edits — music slider 0.6, chase click, Hebrew click
  (RTL rebuild), re-queried effects slider 0.7 — with all six fields asserted
  on real service state. Expected RED: captured closures lose the command
  draft.
- [x] Add `SavedUiEditsSurviveSyntheticStoreReload`: UI edits for all six
  fields, save via a real pointer click, assert persisted state plus a fresh
  `ProfileStore.Load(0)` on the same synthetic directory (synthetic store
  reload, not a process-restart claim).
- [x] Add `FailedUiSaveRestoresSavedDraftAndPreservesFiles`: a planted invalid
  `profile-0.json.bak` makes the explicit save fail; assert `saveFailed`,
  draft restored to the saved profile, slider reverted, and byte-identical
  primary/backup files. Safety regression — expected to pass before the fix.
- [x] Run the focused PlayMode fixture RED before changing production code;
  keep XML/log under `Logs/qualification-settings-parity/`.
- [x] Apply the `UpdateSettings` live-snapshot merge exactly as designed; no
  rebuild-on-every-edit workaround.
- [x] Run the focused fixture GREEN, then one full EditMode + PlayMode gate
  and a Windows Mono development build (`FoundationBuild.Windows`).
- [x] Document RED/GREEN/build evidence and remaining gaps (synthetic UI
  Toolkit callbacks vs OS/physical touch vs real process restart; Android
  device, human Hebrew review, full keyboard/touch lifecycle, normal-profile
  path, no gameplay). Accepted by lead code/evidence review plus scoped
  read-only review (`Logs/qualification-settings-parity/review.md`); two
  Minor findings refined test-only (`re-review.md`).

## Execution Notes

- The previous investigation is settled: `Refresh`/`UpdateIndicators`,
  `Snapshot` deep-copies, and the all-six `settings.update` schema were
  verified; do not re-derive them or change Core.
- If RED shows a different root cause than the stale captured draft, stop and
  report evidence instead of implementing a speculative change.
- Prior external agent/restart/stream evidence stays historical unless a fresh
  run is explicitly executed; omitted reruns are labeled as such.
