# Foundation settings parity report

Date: 2026-10-11. Plan: `docs/FOUNDATION_SETTINGS_PARITY_PLAN.md`.
Status: **Reviewed: implementation and evidence accepted; focused completion
of the settings-screen parity slice only.**

## Bug and fix

`FoundationShell.BuildSettings` captured the `FoundationState` from build time
inside every settings control callback. Because `FoundationShell.Refresh`
rebuilds only on screen/language/activeSlot/slot-status changes and updates
sliders in place otherwise, a second UI edit merged unspecified fields from
that stale draft — a later volume edit reset earlier pending volumes, a camera
click reverted a command-driven draft, and the reduced-shake button computed
its target from the captured value so it could never toggle twice.

Fix in `Assets/FamilyRacing/Runtime/FoundationShell.Screens.cs` only:
`UpdateSettings` now takes
`(string language = null, string camera = null, bool toggleShake = false,
float? music = null, float? effects = null, float? voice = null)`, reads
`app.Service.Snapshot().draftProfile` at invocation time, merges every
unspecified field from that live draft, resolves shake as
`toggleShake ? !draft.reducedCameraShake : draft.reducedCameraShake`, and
dispatches the unchanged six-field `settings.update` schema. Core service,
schema and command surface untouched. No rebuild-on-edit workaround.

## Tests (new `Tests/PlayMode/FoundationSettingsPlayTests.cs`)

Fixture: inactive `FoundationApp` host (Awake never runs — no
`persistentDataPath` access) with reflection-injected `service`/`runId`; an
active `UIDocument`+`FoundationShell` bound to an instantiated clone of the
generated `FoundationPanelSettings` asset; real `ProfileStore` in a GUID temp
directory deleted on teardown. Controls are exercised through the real
rendered paths — `Slider.value` change callbacks and pooled
`Touch`/`PointerDownEvent`/`PointerUpEvent` on `Button`s.

| Test | RED (pre-fix) | GREEN |
|---|---|---|
| `SequentialVolumeControlsPreservePendingEdits` | music stayed 0.65 after later edits | pass |
| `ShakeButtonTogglesBothDirections` | second press stayed true | pass |
| `CameraAndVolumeControlsMergeLatestCommandDraft` | pending shake/camera/volumes lost | pass |
| `SavedUiEditsSurviveSyntheticStoreReload` | persisted camera fell back to stale "chase" | pass |
| `FailedUiSaveRestoresSavedDraftAndPreservesFiles` | passed pre-fix (safety regression) | pass |

## Evidence (`Logs/qualification-settings-parity/`)

| Gate | Command | Result | Artifact |
|---|---|---|---|
| Focused RED | `Unity.exe -batchmode -projectPath . -runTests -testPlatform playmode -testFilter FamilyRacing.Tests.FoundationSettingsPlayTests` | 4 failed / 1 passed (expected assertions) | `red-playmode.xml` / `red-playmode.log` |
| Focused GREEN | same filter after the fix | 5/5 passed | `green-playmode.xml` / `green-playmode.log` |
| Full EditMode | `-runTests -testPlatform EditMode` | 249/249 | `final-editmode.xml` / `final-editmode.log` |
| Full PlayMode | `-runTests -testPlatform playmode` | 30/30 | `final-playmode.xml` / `final-playmode.log` |
| Windows Mono dev build | `-executeMethod FamilyRacing.EditorTools.FoundationBuild.Windows -quit` | `result=Succeeded errors=0 warnings=0` | `build-windows.log`, `Builds/Windows/FamilyRacing.exe` |
| Post-review refinement | same focused PlayMode filter | 5/5 | `refinement-playmode.xml` / `refinement-playmode.log` |

Editor: pinned `C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Unity.exe`.

Post-review refinement (test-only, production unchanged): the fixture class
now carries `[UnityPlatform(WindowsEditor, OSXEditor, LinuxEditor)]` so the
editor-only PanelSettings/AssetDatabase source is excluded from standalone
player runs instead of failing setup; `CameraAndVolumeControlsMergeLatestCommandDraft`
gained the intermediate six-field assertion after the music edit. Refinement
run 5/5 (`refinement-playmode.xml`); the full gate above (EditMode 249/249,
PlayMode 30/30) predates this test-only change and the production Windows
build is unchanged — the original 4-failure RED evidence stands.

## Scope labels and remaining gaps

- Evidence here is synthetic UI Toolkit PlayMode callbacks in the editor:
  `Slider.value` change events and pooled pointer down/up events. It is **not**
  OS-level/physical touch evidence and **not** a real process-restart check —
  persistence was verified via a fresh `ProfileStore.Load` on the same
  synthetic directory.
- Still open: Android device/ADB qualification, human Hebrew reading review,
  the full physical keyboard/touch lifecycle, the normal user profile path
  (`persistentDataPath`), graceful-exit autosave, Windows IL2CPP, NuGet
  restore (network), and all gameplay features.
- Prior external `AgentScenario`/`RestartScenario`/`StreamRobustnessScenario`
  evidence remains historical — those tools were not rerun for this focused
  shell fix.
