# Foundation Qualification Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development and test-driven-development. Record evidence here and in `IMPLEMENTATION_STATUS.md`.

**Goal:** Finish the remaining locally executable bilingual rendering and repeatable Android build work in Milestone 1.

**Architecture:** Keep the existing shared command/service paths. Bundle openly licensed font source files and generated dynamic font assets; configure UI Toolkit Advanced Text Generator and derive observable font readiness from the real configuration. Export Android Gradle projects and build with an explicit offline invocation. Extend real-player evidence without claiming physical-device acceptance.

**Tech Stack:** Unity 6000.3.26f1, UI Toolkit/TextCore, PowerShell, existing Python agent scenario runner.

**Spec:** `GAME_PLAN.md`, `docs/FOUNDATIONS_PLAN.md` Task 3.

## Global Constraints

- Work in the existing unborn `feature/family-racing` branch; preserve existing files and do not commit or push.
- Free font downloads and official setup documentation are authorized by the user for this continuation. No paid assets, installs, or other dependency downloads.
- Keep the existing isolated profile and token-protected loopback validation architecture.
- Keep Milestone 1 in progress until Android device/offline launch and human Hebrew checks are evidenced. No gameplay or later-milestone claims.
- Only one Unity process may use this project at a time; the lead coordinates all Unity tests/builds.

## Review Focus

- Re-running setup must repair generated font bindings without discarding authored assets.
- All Hebrew/English catalog characters must be covered by packaged fonts, including the Hebrew language button in English mode.
- Runtime state must report the actual text configuration; missing assets must remain an explicit gap.
- Offline Android builds must fail on a missing dependency and preserve useful diagnostics.
- Settings must survive a real player restart using synthetic isolated profiles.

## Task 1: Packaged bilingual text

**Files:** font sources/license under `Assets/FamilyRacing/Fonts`, generated font assets; `Editor/FoundationProjectSetup.cs`, a focused font setup helper; `Runtime/FoundationShell.cs`, `Runtime/FoundationApp.cs`, focused runtime font readiness helper; new focused EditMode/PlayMode tests.

**Interfaces:** Existing `PresentationState.hebrewFontAvailable` and `fontWarning` describe the bound dynamic font/ATG configuration. The UI keeps logical Hebrew strings and physical steering direction. Font setup is called by `FoundationProjectSetup.Create()` for new and existing generated assets.

- [ ] Write regressions for actual packaged glyph coverage, setup repair/idempotence, runtime font readiness, ATG and RTL configuration; observe RED.
- [ ] Download free Noto font source and license with recorded provenance; implement the setup/runtime wiring.
- [ ] Run EditMode and PlayMode suites, then inspect a built-player Hebrew frame and retain evidence.

## Task 2: Repeatable offline Android build

**Files:** `Editor/FoundationBuild.cs`, `Tools/Verify.ps1`, focused Android helper and tests.

**Interfaces:** `Tools/Verify.ps1 -Mode Android` exports via the pinned editor and builds the exported project with bundled JDK/Gradle and `--offline`. Failures propagate with distinct export/build logs; no global configuration edits.

- [x] Add behavioral tooling regression tests proving offline arguments, paths with spaces, missing tools/dependencies and exit propagation; observe RED. — `Logs/android-offline-tooling-RED.log` → `Logs/android-offline-tooling-GREEN.log` (16 checks).
- [x] Implement the export/build helper using local installed tooling. — `Tools/Build-AndroidOffline.ps1`, `Tools/Test-AndroidOffline.ps1`, `Tools/Tests/AndroidToolFixture.cs`, `Verify.ps1 -Mode Android` delegation, `FoundationBuild.ExportAndroid`.
- [x] Run tooling regressions and one real offline Android build; preserve actual success or cache-gap failure evidence. — Real run `20261011-123733-d853a75210284129ba058d708bd3606e` succeeded fully offline (Gradle `--offline`, BUILD SUCCESSFUL; APK sha256 `B639CD00E92CE9543D3B2070EFC1D52698EA183EB74DE5D5B95DE4C728C3D805`); `Logs/android-offline/<runId>/result.json`.

## Task 3: Integrated runtime evidence and documentation

**Files:** `Tools/AgentScenario.py` or a focused restart companion, `Tools/TestAgentScenario.py`, docs/status/coverage.

**Interface:** Development/editor launches may use `--validation-profile-dir` together with `--validation-dir` to keep one synthetic save store across fresh capture directories. Ordinary launches retain their existing profile path. The restart runner owns all paths under its fresh evidence directory.

- [x] Verify saved profile/settings across a real Windows player restart with isolated profiles, recording commands, streamed state and outcomes. — `Tools/RestartScenario.py` passed 6/6 checks on the development player (`Logs/qualification-continuation/restart-final/result.json`); `--validation-profile-dir` override implemented in `FoundationApp`.
- [ ] Build the Windows player and re-run the external foundation scenario. — Windows Mono dev build succeeded (`Logs/qualification-continuation/final-build-windows.log`); the external `AgentScenario` re-run remains deferred — it enforces packaged-font readiness, which is Task 1 and still RED pending network approval.
- [ ] Review the changed files independently, resolve important findings, record suite/build/evidence results and remaining physical gates. — Lead review of the continuation diff done; serializer revert + runner fix applied; see `Logs/qualification-continuation/report.md`.

## Execution Ledger

- Pre-flight: tasks 1 and 2 share Unity execution but no product files; lead serializes Unity runs. Task 3 consumes the built player and existing state schema.
- Existing approved root/no-commit preference overrides generic worktree/commit steps. Existing approved design and continuation request authorize execution without a new design approval cycle.
- Task 3 discovery: the bridge requires an empty capture directory and the service caches profile loads before bridge startup. Add the development-only profile-directory override above so restart evidence reads the same saved file through the real load path; no copy-after-load fixture can prove this.
- Continuation results (2026-10-11): `FoundationApp.SelectProfilesDirectory` implemented (EditMode RED→GREEN, `Logs/qualification-continuation/editmode-*.xml`); real restart passed 6/6 (`restart-final/`); one real offline Android build succeeded (`Logs/android-offline/20261011-123733-*/result.json`); qualification final gate EditMode 241/245, PlayMode 18/20 — all failures are the known Task 1 packaged-font fixtures. Restart runner `assert_profile` now compares volume fields by exact binary32 bits (JsonUtility's file spelling of a float can differ from the stream's decimal text for the same value); `build_fingerprint` covers launcher + `_Data` + `UnityPlayer.dll`. Task 2 complete; Task 3 restart portion evidenced; Task 1 untouched/RED.
