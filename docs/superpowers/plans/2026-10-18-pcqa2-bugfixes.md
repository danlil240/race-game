# PC QA Session 02 — Defect Fix Plan

**Spec:** [PC QA Session 02 REPORT](../../../docs/qa/pc-session-02-2026-10-18/REPORT.md) and its [open-defects.csv](../../../docs/qa/pc-session-02-2026-10-18/open-defects.csv).

**Goal:** Fix every Session 02 defect that is actionable in this repository, with tests, on branch `fix/pcqa-session-02` (branched from `fix/pcqa-session-01` @ d142030, which carries the verified Session-01 fixes).

**Architecture:** Unity 6000.3.26f1 UI Toolkit shell (`Assets/FamilyRacing/Runtime/FoundationShell*.cs`, partial class), bilingual localization (`Assets/FamilyRacing/Core/Localization/ShellText.cs` — `{"en", "he"}` pairs), scripted build/packaging (`Tools/Build-Windows.ps1`, `Tools/Verify.ps1`, `Tools/Build-FamilyRelease.ps1`).

## Open-defect ledger (all four open defects accounted for)

| ID | Severity | Disposition |
| --- | --- | --- |
| PCQA2-001 | P2 | **Task 1** — code bug: English welcome disclaimer renders unwrapped and clips at both viewport edges |
| PCQA-001 | P0 | **Task 2** — stale extracted artifact: refresh `Builds/Release/FamilyRacing` from the reviewed candidate via the honest rehearsal path |
| PCQA-002 | P1 | Out of scope — authored content catalog gated on M4 A7 Lite device evidence + owner acceptance (`docs/tasks/m6-prerequisite-reconciliation.md`, `IMPLEMENTATION_STATUS.md`); carried ruling from Session-01 plan still applies |
| PCQA-006 | P1 | Out of scope — licensed voice clips; the authoring pipeline (`Tools/Generate-TutorialCues.py`) requires `AZURE_SPEECH_KEY` (not provisioned) and acceptance requires a human intelligibility/mix review |

## Global constraints

- All user-facing strings are bilingual: every new/changed ShellText key needs both `en` and `he`. Follow `T(key, state)` / `KeyNotFoundException` fallback.
- Every interactive control keeps a stable semantic `name` — the QA harness and PlayMode tests address controls by name.
- The shell rebuilds its tree only when the rebuild key changes (`FoundationShell.Refresh`); never rebuild per tick.
- Internal identifiers must never render as primary family-facing text.
- Do not break the rebuild-signature, pointer-capture, or RTL/LTR contracts (Hebrew mirrors; numbers/ids stay LTR).
- Ordinary builds keep validation endpoints compiled out; nothing new may depend on `DEVELOPMENT_BUILD`.
- TDD where feasible: PlayMode/EditMode regression coverage for each fix. Do not regress the green suites (Session-02 baseline: CoreOffline 843, exit 0).
- No `UnityEditor` APIs in `Runtime/`.
- **Packaging honesty (QA-mandated):** the refreshed artifact stays a `packageKind="rehearsal"` / `acceptanceStatus="unqualified"` package. Never relabel it a release candidate — the production content gate is still open.

## Task 1: PCQA2-001 — welcome disclaimer must wrap inside the viewport

**Defect:** On the profiles screen, `shell.disclaimer.unavailable` (the long honest variant added for PCQA-003) renders as one centered line wider than the client area: it begins mid-word at the left edge and ends before the sentence finishes at the right edge. Reproduced at 1024×768, 1280×720, 1920×1080, cold-launch and after returning with a selected profile, development and updated-ordinary builds. The shorter Hebrew string fits but must keep fitting.

**Root cause:** `BuildProfiles` (`Assets/FamilyRacing/Runtime/FoundationShell.Screens.cs:58-61`) creates the disclaimer through `AddLabel`, which never sets `whiteSpace`. Under the Advanced text generator the effective default is no-wrap, so the centered text overflows the element symmetrically and both ends clip. The codebase already treats `whiteSpace = WhiteSpace.Normal` as the explicit opt-in for wrapping labels — see `FoundationShell.Progression.cs:418` and the `LongWorldNamesWrapInsideTheCard` PlayMode test (`GameplayScreensPlayTests.cs:1544-1564`).

**Fix:** Constrain the disclaimer label to the available width and allow wrapping/reflow. Apply `whiteSpace = WhiteSpace.Normal` to the disclaimer label in `BuildProfiles` (one label covers both `shell.disclaimer` and `shell.disclaimer.unavailable`). Do NOT change `AddLabel` globally — other call sites rely on current behavior. A centered max-width cap is acceptable for readability at 1920 but is not required; the QA bar is that the complete message fits inside the client area at all three sizes in both languages, on both the fresh cold-launch profiles screen and the returning-profile profiles screen.

**Tests:** Extend the existing PlayMode screen fixtures (see `FoundationScreensPlayTests.cs` around lines 414-423 for the disclaimer tests and `GameplayScreensPlayTests.cs:1544` for the wrap-assertion pattern):
- Assert `shell.disclaimer` resolves `whiteSpace == WhiteSpace.Normal`.
- Assert the label's `worldBound` stays within the root's content width (no horizontal overflow) and, at the harness's layout width, wraps to more than one line for the long English string (`worldBound.height` clearly exceeding a single line).
- Cover both locales and both visits to the profiles screen (cold state and after selecting an existing profile), matching the QA retest instruction.

## Task 2: PCQA-001 — refresh the stale extracted rehearsal artifact

**Defect:** `Builds/Release/FamilyRacing/FamilyRacing.exe` — the artifact positioned for family distribution — still contains the Oct-15 gameplay payload (Runtime.dll SHA256 `AE9CBB08…`), renders no text at all, and predates every verified Session-01 fix.

**Fix (no relabeling — see global constraint):**
1. Rebuild the ordinary Windows player from the fixed source: `Tools\Verify.ps1 -Mode WindowsOrdinary` (this is the invocation the Session-01 closeout used; it refreshes `Builds\Windows-Ordinary`).
2. Re-run the rehearsal packaging path against the fresh ordinary output: `Tools\Build-FamilyRelease.ps1 -Config Release\release.json -Output <new dir under Builds\Releases> -Rehearsal -WindowsSource Builds\Windows-Ordinary`. The output dir must be new/empty (the script enforces it). A missing Android artifact is recorded as a named blocker — acceptable and honest.
3. Replace the extracted `Builds\Release\FamilyRacing` tree with the `FamilyRacing\` folder from the fresh zip so the on-disk artifact under test is the current payload. The previous zip remains under `Builds\Releases\rehearsal-1.0.0` for provenance; do not delete or overwrite it.
4. Record evidence: the new `release-manifest.json` (stamps `packageKind="rehearsal"`, `acceptanceStatus="unqualified"`, fresh per-file SHA256) and the new Runtime.dll hash for the report.

**Verify:** Ordinary players compile the validation bridge out, so verification is a native OS cold-launch check (the same method Session-01 closeout used): launch `Builds/Release/FamilyRacing/FamilyRacing.exe` windowed ~1280×720, foreground the window, capture an OS screenshot after the first screen settles, and confirm the profile screen renders text (title, disclaimer, driver names, buttons). Store the capture under `Logs\`.

**Tests:** No new automated tests — this is packaging of already-tested code; the PlayMode/EditMode suites remain the code gate.

## Controller verification (after both tasks)

- Rebuild the development player (`Tools\Build-Windows.ps1`) and re-run the Session-02 resolution harness (`docs/qa/pc-session-02-2026-10-18/runtime/resolutions/capture_resolutions_session02.py`, adapted output dir + a third pass at 1280×720) — confirm the disclaimer is fully readable at all three sizes in EN and HE, cold and returning-profile.
- Native cold-launch capture of the refreshed `Builds/Release` player — confirm text renders.
- `Tools\Verify.ps1 -Mode CoreOffline` plus the PlayMode suite (at minimum the touched fixtures) — no regressions.
