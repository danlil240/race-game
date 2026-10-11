# Foundation device qualification report

Date: 2026-10-11. Task: M1 acceptance closeout, agent-doable slice
(Task 0 of `docs/superpowers/plans/2026-10-11-driving-prototype.md`).
Status: **Executed — documentation reconciled, Android rebuilt from
current source, device check attempted. M1 remains open: physical-device
and human-review gates are outstanding and no device was attached.**

## Scope

Agent-doable closeout items only: reconcile stale status documents
against recorded evidence, rebuild the Android APK from the latest
reviewed source, check for an attached device, and record precisely what
is evidenced versus what remains blocked. Per the binding scope ruling,
physical-device checks, human Hebrew review, and any Android launch
adapter work are out of scope for this pass and are recorded below as
open blockers. M1 is **not** marked complete anywhere.

## What is evidenced (existing, unchanged)

| Surface | Evidence |
|---|---|
| Core tests | `Tools\Verify.ps1 -Mode Core` 225/225 (NuGet restore owner-approved 2026-10-11); `python -B -m unittest discover -s Tools -p 'Test*.py'` 52/52; `Tools\Test-VerifyTooling.ps1` 12/12 |
| Unity suites (latest recorded) | EditMode 253/253 `Logs\verify-editmode-results.xml`; PlayMode 53/53 `Logs\verify-playmode-results.xml` (gaps-wave gate; earlier 249/25 and 249/30 figures are historical per-pass records) |
| Windows player | Latest reviewed Mono development build `Logs\verify-unity-windows.log`; Mono is a deliberate `FoundationProjectSetup.ApplyPlayerSettings` choice — no IL2CPP build exists or is required for M1 |
| External scenarios on real players | AgentScenario 11/11, Restart 6/6, StreamRobustness 18/18, FinishDelivery 6/6, NormalProfile 6/6 — `Logs\qualification-gaps\`, `Logs\qualification-exit\`, `Logs\qualification-external-stream-robustness\`; reports `docs\tasks\foundation-gaps-report.md`, `foundation-external-stream-report.md`, `foundation-stream-robustness-report.md`, `foundation-settings-parity-report.md` |
| Packaged bilingual fonts | Pinned Noto sources + OFL under `Assets\FamilyRacing\Fonts\`; en/he window captures lead-accepted `Logs\qualification-fonts\player-evidence-2\screenshot-*.png` |
| Environment | `CheckEnvironment` passes: pinned editor 6000.3.26f1, Windows IL2CPP module installed (no IL2CPP build produced), Android SDK/NDK/OpenJDK present |

## Attempted this pass

### Android rebuild from current source — succeeded

- Command: `powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Verify.ps1 -Mode Android`
- Source: HEAD `15934d614a5f06675ce286b90f01bfaa51eb04a0`
  (`feature/family-racing`); dirty at start: `IMPLEMENTATION_STATUS.md`
  (+2 planning-residue lines) and untracked `docs/superpowers/`; only
  documentation edits landed during the build — no product or build
  inputs changed. Unity export transiently rewrote
  `ProjectSettings/ProjectSettings.asset` (random `ps4Passcode`);
  reverted after the build. Full record:
  `Logs\android-offline\20261011-193051-31b4d38aaa6f49fa9d4791f5fe422f76\source-state.txt`.
- Tools: Unity 6000.3.26f1; Unity-bundled Gradle + OpenJDK under
  `--offline --no-daemon`; .NET SDK 10.0.401; Python 3.12.10.
- Result: `succeeded`, export ~2 min + Gradle `BUILD SUCCESSFUL in
  1m 39s`, exit code 0 —
  `Logs\android-offline\20261011-193051-31b4d38aaa6f49fa9d4791f5fe422f76\result.json`.
- Artifact: `Builds\Android\FamilyRacing.apk`, 47,404,615 bytes, SHA-256
  `DD8EF19CC7ACA31B183E10135EA53B388308BF3CC209C334C505EC574370CB0B`
  (verified against `Get-FileHash`). This build supersedes the previous
  APK (`20129690…78F74`), which predated the stream/settings/input fixes.

### Device check — no device attached

- `adb devices` via the Unity-bundled platform-tools
  (`…\AndroidPlayer\SDK\platform-tools\adb.exe`): daemon started, list
  empty — **no Galaxy Tab A7 Lite or any other device attached**.
- `adb` is not on PATH; the bundled copy is the repo-documented tool.
- No install, launch, or on-device smoke was attempted; none is
  fabricated here. The per-brief checklist (install, offline launch,
  English/Hebrew profiles/settings/touch, explicit save + restart)
  remains entirely unexecuted.

### Android validation launch path — not qualified

The Windows validation launch contract (`--validation-dir`,
`--validation-profile-dir`, `--validation-normal-profiles`, loopback +
token, isolated stores) is editor/development-only and has never run on
Android. Windows command-line flags do not prove Android startup works.
Qualifying the Android path (protected loopback, ADB forwarding,
isolated profiles, complete capture) plausibly needs a
development-only Android launch adapter — judged a Task 1+ scope
decision per the scope ruling; no adapter was built.

### Documentation reconciliation — done

- `README.md`: IL2CPP "still missing" → installed (no IL2CPP build);
  "NuGet restore blocked" → approved 2026-10-11, `-Mode Core` 225/225;
  stale `notVerified` list marked superseded; new foundation-gaps-wave
  bullet with the latest gate (253/53) and the current open items;
  Android bullet now cites this rebuild + the empty `adb devices` result.
- `IMPLEMENTATION_STATUS.md`: superseded "current reviewed runtime gate"
  phrasing corrected to historical; new closeout-pass entry added.
- `docs/FEATURE_COVERAGE.md`: Android gap entry updated to this rebuild
  run and the attempted-but-empty device check. Otherwise already
  current; `docs\COMMANDS.md` and `docs\FOUNDATIONS_PLAN.md` were
  checked and are already consistent with the evidence.

## Open blockers (explicit, not waived)

- **Physical tablet (Galaxy Tab A7 Lite) touch checks** — no device
  attached; install, offline launch, profile/settings/touch exercise,
  and save/restart on real hardware all outstanding.
- **Device-qualified Android launch** — APK is current-source but has
  never run on a device; Android offline-launch acceptance unproven.
- **Android validation path** — launch/connection story (loopback, ADB
  forwarding, isolated profiles) unqualified; may need a
  development-only launch adapter (Task 1+ scope decision).
- **Physical Windows keyboard checks** — synthetic
  `InputTestFixture`/pointer coverage only; no OS-level input evidence.
- **Human Hebrew reading/layout review** — bundled fonts and
  lead-accepted captures exist; owner review not performed.
  Pronunciation acceptance stays separate until tutorial audio exists.
- **Device performance** — M1 tablet/PC targets unmeasured on hardware.
- **Windows IL2CPP build** — module installed; no build produced.
  Recorded as build-hardening, not an M1 acceptance requirement.

Per `GAME_PLAN.md`, these hardware/human gates keep M1 open without
blocking the M2 plan review; any M2 implementation overlap with the open
M1 gates requires an explicit recorded scope decision.
