# Foundation gaps report

Date: 2026-10-11. Plan: `docs/FOUNDATION_GAPS_PLAN.md`.
Status: **Executed — all four targeted `notVerified` surfaces now have
recorded evidence; two production defects found and fixed; gate fully
green.** Work was uncommitted per plan constraint (no commits).

## Scope

Closed the automatable remainder of Milestone 1's QA gaps: graceful-exit
finalization evidence, the normal user profile path, finish delivery to a
stalled controller, and physical-input/UI parity coverage. Out of scope
(blocked): Android ADB/device, human Hebrew review, IL2CPP *build*
qualification.

## Changes (production)

- `FoundationApp.cs` — new development-only `--validation-normal-profiles`
  flag: valueless, only meaningful with `--validation-dir`, selects the REAL
  `Path.Combine(persistentDataPath, "Profiles")` store; mutually exclusive
  with `--validation-profile-dir` (`ArgumentException`); duplicates rejected;
  ordinary launches ignore it entirely (args uninspected without
  `--validation-dir`).
- `FoundationApp.cs` — `Application.runInBackground = true` inside the
  validation block only. **Gate-discovered defect:** a validation player
  paused on focus loss (`runInBackground: 0`), freezing `Update`→`Pump` so
  the controller never attached; `AgentScenario` timed out on the
  pre-fix build. Ordinary family builds keep focus-pause.
- `FoundationShell.cs` — `AddHoldButton` pointer registrations moved to the
  **trickle-down phase**. **Test-discovered defect:** `Button.Clickable`
  (registered first) consumed primary/touch `PointerDownEvent`s via
  `StopImmediatePropagation`, so sandbox hold controls could never engage —
  on real touch input as well as synthetic events. Trickle-down runs before
  Clickable's bubble-phase handler; release/cancel/capture-out paths
  verified against the 6000.3 event-registry contract.

## Tests added

| Fixture | Tests | Notes |
|---|---|---|
| `ValidationProfileDirectoryTests` (EditMode, +4) | flag selection, mutual exclusivity both orders, duplicate rejection, ordinary-launch ignore | RED 3-fail → GREEN |
| `FoundationQuitPlayTests` (+5) | `processExit` terminal status on dispose, valid-finish status preserved, bounded dispose mid-capture, stalled-sink bounded join, no-draft-autopersist | GREEN-on-arrival — locks in already-correct `StateStream.Dispose` semantics |
| `FoundationInputPlayTests` (+10) | virtual `Keyboard` via `InputTestFixture`: steering incl. opposing-neutral, drift/boost holds, E/R pulses, C/Esc dispatches, agent-ownership + off-sandbox suppression, `OnDisable`→`input.clear`, `ResetPhysical` | |
| `FoundationScreensPlayTests` (+8) | pointer parity on remaining screens: card create/select, language toggle + RTL, nav/back, hold down/up/cancel via `input.set`, gadget pulse | exposed the Clickable defect |

PlayMode asmdef gained `Unity.InputSystem` + `Unity.InputSystem.TestFramework`
references.

## External scenarios (new tooling)

- `Tools/FinishDeliveryScenario.py` + `TestFinishDeliveryScenario.py`
  (integrity suite, 11/11): stalled non-reading controller sends `finish`
  after 32 legal lookup frames; terminal `streamError` status, fresh
  controllers rejected `captureClosed`, frozen valid trace prefix, player
  stays alive. Expected-fault evidence — correct detection, not capture
  certification.
- `Tools/NormalProfileScenario.py` + `TestNormalProfileScenario.py`
  (integrity suite, 17/17): `--validation-normal-profiles` two-process run;
  backs up/quarantines/restores the real `Profiles` store (exception-safe
  layered finally); `taskkill /pid` without `/F` (WM_CLOSE) graceful exit →
  terminal `captureError:"processExit"`; saved profile survives into the
  relaunch; unsaved drafts never persisted; clean finish on second run.

## Evidence (`Logs/qualification-gaps/`, `Logs/qualification-exit/`)

| Gate | Result | Artifact |
|---|---|---|
| EditMode | 253/253 | `Logs/verify-editmode-results.xml`, `qualification-exit/final-editmode.xml` |
| PlayMode | 53/53 | `Logs/verify-playmode-results.xml` |
| Core `dotnet test` | 225/225 (NuGet restore approved + done) | `Tools/CoreTests` |
| Windows Mono dev build | `result=Succeeded errors=0 warnings=0` | `Logs/verify-unity-windows.log` |
| AgentScenario | 11/11 | `qualification-gaps/agent-evidence/result.json` |
| RestartScenario | 6/6 | `qualification-gaps/restart/result.json` |
| StreamRobustnessScenario | 18/18 | `qualification-gaps/stream-robustness/result.json` |
| FinishDeliveryScenario | 6/6 | `qualification-gaps/finish-delivery/result.json` |
| NormalProfileScenario | 6/6 | `qualification-gaps/normal-profile/result.json` |

RED evidence for the flag tests: `qualification-exit/red-editmode.xml`
(3 genuine assertion failures) → `green-editmode.xml`. Task reviews:
spec-compliance + quality verdicts recorded in
`.superpowers/sdd/foundation-gaps/` task reports.

## Environment changes this wave

- Windows IL2CPP module installed for 6000.3.26f1 (owner-approved;
  `CheckEnvironment` now OK). No IL2CPP build produced or qualified yet.
- nuget.org added as a user-level package source (owner-approved); `dotnet
  restore`/`dotnet test` now work (`-Mode Core`, 225/225).

## Not claimed / open

- OS-level physical key/touch and on-device UI (synthetic callbacks only).
- Android device/ADB qualification (no device attached).
- Human Hebrew reading/pronunciation review.
- IL2CPP build qualification (module present; preview remains Mono dev).
- All gameplay features remain `notImplemented` (later milestones).
- Minor deferred findings: `FoundationQuitPlayTests` duplicates ~120 lines
  of the robustness fixture's helpers (drift risk); `ValidationDirectory()`
  takes `args[i+1]` blindly so `--validation-dir --validation-normal-profiles`
  (flag immediately after) parses the flag as a path — pre-existing pattern,
  flagged for a later hardening pass.
