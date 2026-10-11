# Blank-shell fix — UIDocument panelSettings never persisted (2026-10-11)

## Symptom / root cause

Windows preview showed only the off-white background: no profile cards, no
shell. `Assets/FamilyRacing/Generated/Foundation.unity` carried
`m_PanelSettings:{fileID:0}` — the UIDocument had no panel to attach to.
`FoundationProjectSetup.EnsureScene` returned immediately when the scene
asset existed, so the broken reference was never inspected or repaired.
The original `document.panelSettings = panel` property assignment had not
persisted into the saved YAML.

## Fix (upstream, no YAML hand-edits)

- `FoundationProjectSetup.EnsureScene` splits into create + repair:
  - `AssignPanel` writes `m_PanelSettings` via `SerializedObject` —
    guaranteed serialized reference — on both fresh creation and repair.
  - `RepairScenePanel` opens the existing generated scene (additively only
    if not already open), fixes every UIDocument whose `panelSettings` is
    missing or points at the wrong asset, `MarkSceneDirty`+`SaveScene` on
    that generated scene only, and closes only scenes it opened — the
    user's open-scene context and authored objects are preserved.
- `FoundationShell.Refresh` rebinds: keeps the `UIDocument` reference and
  when the cached `root` is null/detached (`root.panel == null`) it picks
  up the live `document.rootVisualElement` and forces a rebuild — covers
  disable/re-enable and panel recreation instead of trusting the
  Awake-cached root.

## Regression evidence

| Check | Result | Artifact |
|---|---|---|
| RED — new EditMode test before fix | 1/1 failed exactly at `doc.panelSettings` — `Expected: FoundationPanelSettings, But was: null` | `Logs\test-editmode-scene-red.xml` |
| GREEN — same test + idempotence test | 2/2 pass; scene YAML now `m_PanelSettings: {fileID:11400000, guid:50d75a8fc339e2a48a62dd56713b5c32}` | `Logs\test-editmode-scene-green.xml` |
| PlayMode — real scene end-to-end | 1/1 pass (0.7s) | `Logs\test-playmode-scene3.xml`, `unity-playmode-scene3.log` (stage markers `scene-test:*`) |
| Windows Mono rebuild | `result=Succeeded errors=0 warnings=0` | `Logs\unity-build-windows-5.log` (`Runtime.dll` stamped 14:53; includes the post-review `ProcessFinish` fix from the previous wave — resolved, no longer source-only) |
| Game-window capture | `PrintWindow` (PW_RENDERFULLCONTENT) of the live window, 1296×839 — **accepted by lead as actual UI image** (Family Racing title + four profile cards visible; Hebrew button blank = known missing-font gap, not a regression) | `Logs\game-window.png` |
| Lead `AgentScenario.py` on this build | **PASSED all 9 checks** (runId `df5ffd888d4746bb982f0a5b5d182aec`) | `Logs\evidence-visible-lead\result.json` (frozen) |
| Diff (touched files vs recovered baseline) | 5 files | `Logs\blank-shell-fix.diff` |

`FoundationSceneRendersLiveShell` (PlayMode) loads the actual generated
scene, injects the isolated temp `ProfileStore` via reflection before any
command dispatch, then verifies: `doc.rootVisualElement.panel` non-null,
positive `resolvedStyle` width/height, four named `profile.N.select`
buttons visible, a real pointer down/up on `profile.0.select` creates the
profile and navigates to settings (`settings.save` present), sandbox shows
`input.left/right/drift/boost`, and disable→re-enable of the document
leaves a live rebuilt tree.

Note: `Clickable` consumes `PointerDown/Up` events — an early test attempt
sending `ClickEvent` directly was a test-harness mistake (the event is
emitted by `Clickable.Invoke`, not received); the shipped test presses the
button with a real pointer event pair at its `worldBound.center`.

## Runtime history

- The fix build's first preview (`Logs\run-validation-blank-fix`, PID 63168)
  supplied the accepted UI capture, but exited before the lead's attempted
  scenario connection. The successful scenario used a separate persistent
  preview at `Logs\run-validation-visible-lead`, which was then gracefully
  closed after its completed capture.
- A fresh English profile-menu instance is running at
  `Logs\run-validation-ready-menu` (lead-launched, persistent) for user
  interaction — it is a convenience instance, **not** test evidence.
- Window capture: `Logs\game-window.png` (lead-reviewed and accepted as
  the actual UI).

## Remaining caveats

- Prior "UI complete" claims were not visually verified until this fix;
  `docs/FOUNDATIONS_PLAN.md` and the runtime report now reflect that.
- Proven: real pointer down/up profile click, live panel/root attach,
  re-enable rebinding, scenario all-9-checks on this build. **Not**
  proven: physical keyboard/touch coverage end-to-end, on-device UI,
  Hebrew font quality (blank Hebrew glyphs — font asset absent).
- The synthetic profile possibly left by the pre-isolation fixture in the
  normal `persistentDataPath` store remains untouched as before.
