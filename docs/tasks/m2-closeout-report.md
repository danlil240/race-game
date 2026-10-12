# M2 driving prototype — development closeout report

Date: 2026-10-12. Branch: `fix/driving-playtest-1`.
Plan executed: `docs/superpowers/plans/2026-10-12-m2-closeout.md`
(native execution, nine tasks, one commit per task).

This pass closes every code-addressable minor from the M2 review triage
table in `driving-prototype-report.md`. What remains open is exactly the
human/device gate list — see "Open gates" below. M2 development is
complete; M2 acceptance is not claimed.

## Owner decisions (executed)

| Decision | Result |
|---|---|
| Held boost: keep auto-fire | `DriftBoostRules.CanStartBoost` no longer requires `!boostLatched`; a held boost re-fires whenever `boostCharge >= boostMinCharge`. `boostLatched` is cleared at boost end so its streamed meaning stays truthful; recovery-flagged movement can never fire. |
| Tutorial voice: build TTS pipeline now | `Tools/Generate-TutorialCues.py` + `Tools/tutorial-cues.json` (Azure Speech REST, stdlib-only, env-var credentials, committed WAV output). Clip **generation** remains credential-gated — see Open gates. |
| Verification: targeted + full suites once | Executed; counts below. |

## Per-minor disposition

The full triage table with per-row dispositions now lives in
`driving-prototype-report.md` ("Deferred minors — triage"). Summary:

- **Fixed (code):** beached-recovery rule; `countdownTicks=0` race.running;
  lifted-cast partial-clearance wall mark; probe-outage clean-landing guard
  (`airPeakY − landingY ≥ landingMinDrop`); tuning-driven drift threshold;
  held-boost re-fire; `Disengage` savedPose persistence; none-mode zeroed
  facts; fallback silhouette; unique pause/finish element ids; fail-closed
  null-lesson validation; `tutorialExit` reason token; `PlayingCue` only
  while audio plays; one deep copy per boundary/frame/command.
- **Fixed (test debt):** real missing-delta fixture; boostCharge drain now
  asserted in the live scenario; races-set covers restart identities;
  `MAX_STEPS_PER_FRAME` derived from source; strict `droppedDrivingEvents`
  access; JObject copy cost measured (0.047 ms/boundary — ~700× under the
  33 ms frame).
- **Documented-as-decision:** `input.set`-during-pause queue semantics;
  moto tuning dual-source (presets = authored truth, generated asset =
  parity-bound mirror); gate[0] = start/finish; `driving.tutorial` shape.
- **Deferred/gated:** voice WAV generation (owner credentials); HE glyph
  coverage (human Hebrew review).

Notable discovery during closeout: the DrivingScenario ramp leg used to
pass partly on flat-ground probe-flicker `landed` events; Task 2's probe
outage fix removed that noise (one `becameAirborne` per real crest launch),
so the scenario now waits out the actual landing — evidence quality went
up, not down.

## Verification evidence (final gate, single pass)

| Suite | Result |
|---|---|
| `Verify.ps1 -Mode Core` | 362/362 |
| `Verify.ps1 -Mode EditMode` | 393 passed / 1 explicitly Ignored (voice-clip gate) |
| `Verify.ps1 -Mode PlayMode` | 121/121 |
| Python tooling (`unittest discover`) | 71/71 |
| `Test-VerifyTooling.ps1` | 12/12 |
| `Verify.ps1 -Mode Windows` | succeeded (`Logs/verify-unity-windows.log`, `Builds/Windows/FamilyRacing.exe`) |
| `DrivingScenario.py` on fresh dev build | 28 checks passed — `Logs/evidence-m2c-driving/result.json` (both vehicles, ramp landings, charge drain, 4 distinct raceIds, capacity projection) |
| `AgentScenario.py` on fresh dev build | 21 checks passed — `Logs/evidence-m2c-agent/result.json` |

## Open gates (unchanged acceptance items — none code)

1. **A7 Lite device:** attach SM-T220 (3 GB), install APK, touch input,
   ADB scenario, 30 fps / p95 ≤ 40 ms / ≤800 MB measurement.
2. **Owner feel review:** car vs motorcycle distinction on real driving.
3. **Human Hebrew review:** text, glyph coverage, and (once generated)
   voice lines.
4. **Child playtest.**
5. **Voice clips:** run `python Tools\Generate-TutorialCues.py` with
   `AZURE_SPEECH_KEY`/`AZURE_SPEECH_REGION` (free tier F0), re-run
   `Verify.ps1 -Mode Setup`, remove the `Ignore` on
   `EveryCueHasWiredClipForBothLocales`, commit WAVs. Pipeline + manifest
   are landed and tested; only generation is gated.
6. **Physical input parity / ordinary-build and IL2CPP performance:**
   unmeasured.
7. **M1 holdovers:** none new; Android APK exists but is device-untested.

## Rulings worth recording

- `airStartY` was specced in the plan but the shipped guard uses
  `airPeakY − landingY` — equivalent, no extra state field needed.
- `EveryCueHasWiredClipForBothLocales` is `[Ignore]`d, not conditional —
  explicit per plan; remove it when WAVs land.
- `Tools/Build-Windows.ps1` + its `docs/COMMANDS.md` section existed
  uncommitted; reconciled and committed under the doc-debt task.
