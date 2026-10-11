# Implementation status

Plan: [GAME_PLAN.md](GAME_PLAN.md)

Updated: 2026-10-11

## Current state

- The approved complete plan is saved in the project root.
- The folder began empty, with no existing game or Git repository.
- Unity Hub 3.22.2 and Unity CLI 1.0.0-beta.13 were installed successfully.
- Unity Editor 6000.3.26f1 (changeset de8167e7440c), the selected 6.3 LTS patch, is installed and verified at `C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Unity.exe` (`Tools\Verify.ps1 -Mode CheckEnvironment`, `Logs\agent-plan-environment-check.log`). The earlier 6000.6.5f1 install is no longer present.
- Installed editor modules: Windows standalone (Mono) and Android Build Support including SDK platform-tools, NDK, and OpenJDK. Windows IL2CPP is still missing.
- A Unity Personal entitlement license is activated and verified working on 6.3. Batch-mode launch, project import, tests, setup, and Windows builds succeeded using the installed editor and bundled packages. The repeatable offline Android path is delivered and exercised for real: `Tools\Verify.ps1 -Mode Android` exports with the pinned editor and builds the exported Gradle project with the bundled JDK/Gradle under `--offline` — the latest run succeeded and published `Builds\Android\FamilyRacing.apk` (SHA-256 `B639CD00E92CE9543D3B2070EFC1D52698EA183EB74DE5D5B95DE4C728C3D805`; evidence `Logs\android-offline\20261011-123733-d853a75210284129ba058d708bd3606e\result.json`). This is a build artifact only — no device/ADB qualification is claimed.
- .NET SDK 10.0.401 is installed; the NuGet package cache was empty at the previous verification, so `dotnet restore`/`dotnet test` awaits network approval. The offline Core run passed 133/133 checks under .NET and 225/225 after the Part A foundation service; the Unity EditMode suite now passes 236/236 and PlayMode 16/16 on the pinned 6.3 editor (see `docs/tasks/foundation-runtime-report.md`).
- Foundation shell runtime is implemented: pure Core service, loopback NDJSON validation bridge (editor/development opt-in), bilingual UI Toolkit shell, physical input adapter, repeatable project setup, and a Windows Mono development build. The lead's external agent scenario **passed all 9 checks** twice: on the wave-3 build (`Logs\evidence-lead\result.json`) and on the post-blank-shell-fix build (`Logs\evidence-visible-lead\result.json`, runId `df5ffd888d4746bb982f0a5b5d182aec`, frozen evidence). A blank-shell bug (generated scene's `UIDocument.panelSettings` was `fileID:0`; setup never repaired existing scenes) was fixed upstream and verified with EditMode + real-scene PlayMode regressions, a fresh build, and an accepted game-window capture (`Logs\game-window.png`) — `docs/tasks/blank-shell-fix.md`. This is the foundation milestone's shell/transport slice only — no racing features exist; feature coverage remains `notImplemented`.
- No playable racing build, device performance result, or completed milestone is claimed.

## Milestone ledger

| Milestone | State | Evidence required before completion |
|---|---|---|
| 1. Foundations | In progress | Windows and Android builds, bilingual rendering, persistent profiles, offline launch |
| 2. Driving prototype | Not started | Tested driving, cameras, recovery, checkpoints, child onboarding |
| 3. Network prototype | Not started | Physical tablet pair and mixed-platform races; reconnect/pause/results |
| 4. Visual benchmark | Not started | Accepted tablet visuals and sustained four-racer profiling |
| 5. Complete gameplay loop | Not started | Validated first-world progression and all modes |
| 6. Full content | Not started | Six courses, eight vehicles, 78 verified events, final bilingual audio |
| 7. Family release | Not started | Signed packages, update preservation, four-device and family acceptance |

## Execution decisions

- The approved plan also serves as the design specification; its milestone acceptance gates remain binding.
- Work starts in the explicitly requested project root. The folder has no existing code or branch requiring a separate checkout.
- Exact editor patch: 6000.3.26f1, the verified production 6.3 LTS patch released 2026-10-08.
- Owner requirement added 2026-10-11: every game control and feature must be command-accessible and every logical game state must be streamed to a structured sink for agent testing. The contract and per-milestone acceptance gates are documented in GAME_PLAN.md; the foundation-slice implementation (Core service + loopback bridge + NDJSON sink) is delivered and the lead's external scenario passed all 9 checks on the wave-3 build — frozen evidence `Logs\evidence-lead\result.json`, command→state registry in `docs/FEATURE_COVERAGE.md`, full report in `docs/tasks/foundation-runtime-report.md`. Saved-profile persistence across a real player restart is now evidenced: development/validation launches accept `--validation-profile-dir` (ignored by ordinary launches) so two fresh processes share one synthetic store — `Tools\RestartScenario.py` passed all 6 checks (`Logs\qualification-continuation\restart-final\result.json`); see `docs/tasks/foundation-qualification-report.md`.
- No paid assets or services have been purchased. Asset/tool spending remains US$0 of US$100.

## External validation prerequisites

- An activated eligible Unity license is needed to run the Editor and builds.
- A7 Lite connection and on-device testing are required to qualify Android behavior and performance.
- Two tablets and a PC are available according to the owner; a fourth physical device is required before four-player release acceptance.
- Hebrew reading/pronunciation review and child playtests require the owner's participation.
