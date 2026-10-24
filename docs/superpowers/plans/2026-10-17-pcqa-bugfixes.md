# PC QA Session 01 — Defect Fix Plan

**Spec:** [PC QA Session 01 REPORT](../../../docs/qa/pc-session-01-2026-10-17/REPORT.md) — defects PCQA-001 through PCQA-011.

**Goal:** Fix every defect in the QA report that is a code/asset bug in this repository, with tests, on branch `fix/pcqa-session-01`.

**Architecture:** Unity 6000.3.26f1 UI Toolkit shell (`Assets/FamilyRacing/Runtime/FoundationShell*.cs`, partial class), pure-C# Core (`Assets/FamilyRacing/Core`), bilingual localization (`Assets/FamilyRacing/Core/Localization/ShellText.cs` — every key has `{"en", "he"}`), ScriptableObject content assets (`Assets/FamilyRacing/Runtime/Content`).

## Out of scope (not code bugs — documented in plan for honesty)

- **PCQA-002** (modes unavailable): `Foundation.unity` binds `gameplayCatalog: {fileID: 0}` intentionally; M5 Task 9 authored content is gated on M4 A7 Lite device evidence + owner acceptance (`docs/tasks/m6-prerequisite-reconciliation.md`, `IMPLEMENTATION_STATUS.md`). Authoring the 3-world/6-course/78-event catalog is a gated project decision, not a bug fix.
- **PCQA-006** (tutorial voice recordings absent): requires licensed audio assets that do not exist; a human/licensing gate.

## Global constraints

- All user-facing strings are bilingual: add both English and Hebrew entries in `ShellText.cs` for every new key. Follow the existing `T(key, state)` lookup pattern and `KeyNotFoundException` fallback behavior.
- Every interactive control gets a stable semantic `name` (e.g. `"action.settings"`, `"lan.join.error"`) — the QA harness and PlayMode tests address controls by name.
- Commands route through `app.Dispatch("action", params)` exactly like agent-driven commands; disabled controls carry the discovery `reason` on `tooltip` via `ApplyAvailability`.
- The shell rebuilds its tree only when the rebuild key changes (see `FoundationShell.Refresh`); in-place updates go through `UpdateIndicators`. Any new rendered fact that must rebuild the tree must join the rebuild signature — never rebuild per tick (held buttons lose pointer capture).
- Participant/event ids and other internal identifiers must never render as primary family-facing text; keep them in diagnostic/name attributes only.
- Do not break the existing rebuild-signature, pointer-capture, or RTL/LTR contracts (Hebrew UI mirrors; numbers/ids/addresses stay LTR via `\u200E` + `languageDirection`).
- Ordinary (non-development) builds keep validation endpoints compiled out; nothing new may depend on `DEVELOPMENT_BUILD`.
- TDD where feasible: add EditMode/PlayMode regression coverage for each fix. Existing suites: `Tools\Verify.ps1 -Mode EditMode`, `-Mode PlayMode`, `-Mode CoreOffline`. Do not regress the green suites (last gate: CoreOffline 843, EditMode 878/879 + 1 voice-clip skip, PlayMode 309/309).
- No Unity Editor-only APIs in `Runtime/`. `Assets/FamilyRacing/Editor/` code may use `UnityEditor` APIs.

## Task 1: Fix cold-launch blank text and packaged-build text absence (PCQA-005, PCQA-001)

**Defect:** On the Oct17 development build, the initial profile screen renders cards/buttons with no text at ~4.6 s after launch at 1024×768, 1280×720 and 1920×1080; text appears only after navigating to Settings. On the packaged ordinary build (stale Oct15 payload), no game text renders on ANY screen across profiles/sandbox/driving/HUD/pause.

**Likely area:** `FoundationShell.Bind`/`ApplyTextConfiguration` (`Assets/FamilyRacing/Runtime/FoundationShell.cs:66-94`) applies `unityTextGenerator`/`unityFontDefinition` to `document.rootVisualElement` once at `Bind` (called from `FoundationApp.Awake`). `FoundationFonts.IsReady` (same folder) is the readiness probe; `FoundationFontPlayTests` exists. Investigate why the first rendered screen lacks text:

- The `UIDocument` may recreate `rootVisualElement` or attach it to the panel after `Bind` runs in `Awake`, dropping or never inheriting the applied inline styles. `Refresh` re-applies only when `root == null || root.panel == null` — verify that check actually fires when the root is recreated (a detached cached root vs a brand-new root object).
- The first `RebuildTree` may run before the dynamic `FontAsset` has any glyphs/atlas, before `panelSettings.textSettings.defaultFontAsset` resolves, or before the root is attached to a panel — leaving children with no inherited font that is never repaired until the next screen change.
- For the ordinary build: determine whether the packaged `FoundationPanelSettings`/`FoundationTextSettings`/font assets are missing or the readiness config never applies. Reproduce with a fresh ordinary build (`Tools\Verify.ps1 -Mode WindowsOrdinary` or `Tools\Build-Windows.ps1 -Configuration Ordinary`) and inspect `presentation.hebrewFontAvailable`/`fontWarning` via a fresh launch if possible; QA evidence: `docs/qa/pc-session-01-2026-10-17/runtime/ordinary-player.log`.

**Expected:** Cold launch renders all text on the first profile screen without any navigation, in development AND ordinary builds, at any window size. `FoundationFonts.IsReady` reports ready on the very first rendered frame.

**Tests:** Extend `FoundationFontPlayTests` (or new PlayMode fixture) to prove the first rendered screen — without any navigation — has the Advanced text generator + bound default font resolving on the actual `rootVisualElement` in the panel, and that labels built by the first `RebuildTree` resolve a non-null font. If the root is recreated between `Bind` and first refresh, add a regression test covering that path.

## Task 2: Catalog-aware welcome copy and a Settings route for existing profiles (PCQA-003, PCQA-004)

**Defect A (PCQA-003):** `ShellText["shell.disclaimer"]` claims "campaign races, quick race, time trial, garage and home-network racing are all playable" in both languages, while all five loop commands reject `contentUnavailable`. Fix: make the profile-screen subtitle reflect the loaded catalog — e.g. one localized string for when gameplay content is available and another for when it is not (naming what IS playable: practice, tutorial and LAN). Drive the choice off actual availability (e.g. `app.Service.GameplayContent != null` or the `campaign.open` discovery availability), never a hardcoded claim. Render it in `BuildProfiles` (`FoundationShell.Screens.cs:18`).

**Defect B (PCQA-004):** An existing profile lands on `sandbox`; the only `settings` dispatch is the new-profile branch in `SelectCard` (`FoundationShell.Screens.cs:67-69`). A returning player cannot reopen Settings. Fix: add a persistent, localized Settings entry reachable by an existing profile — a Settings button in the sandbox actions row (name it e.g. `"action.settings"`, dispatch `ui.navigate` → `settings`, `ApplyAvailability` on `ui.navigate`). Settings' Back button currently always navigates to `profiles` (`FoundationShell.Screens.cs:126-128`): track the origin so Back returns to `sandbox` when the user arrived from a selected profile, and to `profiles` when arriving from profile creation (keep the existing create→settings→Back→profiles path working). Also consider a Settings button on the profiles screen itself — optional; the sandbox route is required.

**Tests:** PlayMode coverage: select an existing profile → sandbox shows a settings affordance → navigate → edit/save/back returns to sandbox; the disclaimer text switches with catalog availability (existing PlayMode fixtures already build synthetic states — see `GameplayScreensPlayTests`/`FoundationScreensPlayTests` patterns).

## Task 3: LAN join validation feedback and readable lobby names (PCQA-007, PCQA-008)

**Defect A (PCQA-007):** `BuildLanBrowser` (`FoundationShell.Network.cs:204-208`) dispatches `lan.join` with the raw field values; an empty address is rejected `invalidParameter` with no visible feedback (`network.lastError` stays empty). Fix: surface synchronous input-validation rejections near the address field — capture the `app.Dispatch` result (it returns `status`/`error` like `SelectCard` does), show a localized error label next to the manual-join fields, highlight the offending field, and cover empty address, malformed address and non-numeric/out-of-range port in both languages. Keep `network.lastError` honest — do not fake service errors; this is shell-side validation feedback. If the current rebuild signature would not repaint the new message, extend `LanRebuildSignature` appropriately (or update the label in place).

**Defect B (PCQA-008):** `BuildLanLobby` renders `p.participantId` (a full UUID) as the player-card name (`FoundationShell.Network.cs:344-345`), and course buttons show raw ids (`lan-car-01`, `lan-motorcycle-01`) when `lan.event.<id>` has no ShellText entry. Fix:

- Participant names: investigate what readable identity the LAN layer publishes (`NetworkSessionState`/participant fields, profile animal names via `profile.animal.*` keys, `localParticipantId`). Prefer resolving a real published display name (e.g. the local profile's animal name, a participant name the protocol already carries, or a slot-based `Player N` localized fallback). Do NOT invent a wire-protocol field just for display unless a clean seam exists. Keep the participant id on `name`/tooltip for diagnostics.
- Course names: add localized `lan.event.lan-car-01`/`lan.event.lan-motorcycle-01` (and any other event ids `LanEventIds()` can return — check the driving catalog for the full list) OR resolve the catalog's authored event display name if one exists. The `EventName` fallback to raw id stays for unknown ids.
- Check `BuildLanLobby`/`EventName`/`ClassName` and `LanSessionService`/`NetworkSessionState` for what is actually published before choosing.

**Tests:** PlayMode/EditMode: join with empty address → visible localized error, no dispatch; malformed port → same. Lobby renders a non-UUID primary name and localized course labels; unknown ids still fall back honestly.

## Task 4: Tutorial lap counter and HUD contrast (PCQA-010, PCQA-011)

**Defect A (PCQA-010):** During tutorial lessons the HUD shows `Lap 1/99` (`lapLabel`, `UpdateDrivingIndicators` at `FoundationShell.Driving.cs:504-507`) because the authored tutorial event uses `laps = 99` for free-drive. Fix: during tutorial lesson phases (`session.tutorial.phase == "lessons"`, see `FoundationShell.Driving.cs:546-560`) hide the lap label or replace it with the lesson progress — the tutorial strip already shows `lesson x/7`. Restore the real lap readout for the graduation/trial phase and for non-tutorial races. Check `session.tutorial.phase` values in `Core/Tutorial` before choosing the gate.

**Defect B (PCQA-011):** The driving HUD top strip draws dark text directly over the world with no backdrop — gate/speed labels lose contrast over dark geometry. Fix: give the top readout strip (and the boost row / keyboard legend if needed for consistency) a consistent semi-opaque backdrop, matching the existing overlay treatment (`new Color(0.99f, 0.97f, 0.92f, 0.85f)` + 14-radius used by `tutorialPanel`/`Overlay`). Keep the world visible elsewhere — only the strip(s) get the backdrop.

**Tests:** PlayMode: tutorial lessons hide/replace the lap label and the trial phase restores it; the HUD strip container has a non-fully-transparent `backgroundColor` (resolved style) on the driving screen.

## Task 5: Eliminate recursive objective serialization depth overflow (PCQA-009)

**Defect:** Unity logs `Serialization depth limit 10 exceeded` at `ObjectiveConditionRow.alsoRequires` on startup (dev and ordinary). Root cause: `EventDefinitionAsset.ObjectiveConditionRow` (`Runtime/Content/EventDefinitionAsset.cs:46-54`) is `[Serializable]` with a self-referencing `ObjectiveConditionRow[] alsoRequires` — Unity's serializer walks the recursive type to its depth limit. Core validation already forbids nested conjuncts (`ObjectiveDefinition.cs:87-88`: `alsoRequires.nested` rejected), so the recursive asset field can express content the domain can never accept.

**Fix:** Remove the self-reference: introduce a non-recursive leaf row type (e.g. `ObjectiveConditionLeaf` with metric/comparator/threshold only) used for `ObjectiveConditionRow.alsoRequires`; `ObjectiveRow.alternatives` keeps `ObjectiveConditionRow` (legs AND their leaf conjuncts; OR across legs). Update `ToCondition` accordingly. This makes the serialized model match the validated domain model exactly and ends the warning. Verify the round-trip: author objectives with alternatives + conjuncts, convert, and confirm data survives.

**Caution:** check for `.asset` files or serialized scenes that already contain `alsoRequires` data (grep `Assets` for `alsoRequires:` YAML) — a field-type change must not silently drop authored rows; if content exists, preserve compatibility.

**Tests:** EditMode tests in `GameplayCatalogTests`/`GameplayCatalogAssetTests` cover conversion — keep them green; add a test that nested conjunct content converts to the leaf structure and that `alsoRequires.nested` remains rejected. Confirm no `Serialization depth limit` warnings in a fresh play log (manual check on a launched player or test log).
