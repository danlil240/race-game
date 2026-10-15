# Full Content Runbook (M6 Task 4, slice A; Task 9 slice A tooling)

Validation and packaging contract for the full-content inventory:
three worlds (`sunny-island`, `redrock-canyon`, `snowy-peaks`), six
courses, eight vehicle models, 72 regular events plus six rival
finales, bilingual display names and final bilingual voice prompts.

This document covers the gates and flavors that exist today plus the
Task 9 slice-A scenario harness. The authored production inventory is
still absent (Tasks 5–8), so every scenario scope reports `partial`
with named gaps rather than `passed`.

## Inventory contract

Authored full content lives under
`Assets/FamilyRacing/Generated/FullContent` (produced by the Task 5–7
world setups and Task 8 voice production): exactly one
`GameplayCatalogAsset`, plus all `VehicleDefinitionAsset`,
`TrackDefinitionAsset` and `EventDefinitionAsset` rows for the six
courses.

`Editor/FullContentValidation.cs`
(`Validate(GameplayCatalogAsset, DrivingContentCatalog) ->
IReadOnlyList<string>`) enforces the fixed production contract:

- exactly 3 worlds in the fixed order, exactly 6 courses, exactly 8
  models (`car-01..04`, `motorcycle-01..04`);
- exactly 72 regular typed events covering every
  class x course x type cell exactly once; event ids are
  `<courseId>-<classId>-<type>` with `type` one of
  `circuit|boost|stunt|time|gadget|clean` (`clean` maps to the runtime
  type `clean-driving`);
- exactly 6 finales `<worldId>-<classId>-finale` claimed by their world
  with rival `<worldId>-<classId>-rival` and the world-completion
  reward vehicle (`*-02` sunny, `*-03` redrock, `*-04` snowy);
- six unique rivals, each with a one-time victory bounty (the reward
  path granting its `livery-<rivalId>` cosmetic);
- bilingual world display names (Latin letters in `nameEn`, Hebrew
  codepoints in `nameHe`);
- per course: exactly 4 spawn slots, at least one authored recovery
  fallback pose, at least one gadget pickup column, and at least two
  legal optional shortcut lanes (no lane inside a mandatory gate's
  crossing region, no two lanes on the same plane);
- the full required voice cue set (14 cues) resolved in both locales —
  missing final clips fail final-content validation.

Empty result means **structurally valid, not playtested**. Generic
`GameplayCatalogAsset.ToCatalog` validation is a different level: a
tiny fixture can convert cleanly and still fail production validation.
Keep the two levels distinct — never weaken the generic rules to make
production pass.

## Commands

```powershell
# Validate the authored production inventory (fails honestly while
# Tasks 5-8 content is missing):
powershell -NoProfile -File Tools/Verify.ps1 -Mode FullContentSetup

# Final flavors (production gate -> complete manifest -> package):
powershell -NoProfile -File Tools/Verify.ps1 -Mode WindowsFullContent
powershell -NoProfile -File Tools/Verify.ps1 -Mode WindowsFullContentOrdinary
powershell -NoProfile -File Tools/Verify.ps1 -Mode AndroidFullContent
powershell -NoProfile -File Tools/Verify.ps1 -Mode AndroidFullContentOrdinary

# Development-only intermediate checks (generic validation over
# authored content only; contentStage=partial; never a candidate):
powershell -NoProfile -File Tools/Verify.ps1 -Mode WindowsFullContentPreview
powershell -NoProfile -File Tools/Verify.ps1 -Mode AndroidFullContentPreview
```

The Android wrapper also accepts the presentations directly:
`Tools/Build-AndroidOffline.ps1 -Presentation FullContent
-Configuration Development|Ordinary` and `-Presentation
FullContentPreview -Configuration Development` (Ordinary preview is
refused — there is no ordinary partial candidate).

## Outputs

| Flavor | Root | Stage |
| --- | --- | --- |
| WindowsFullContent | `Builds/Windows-FullContent` | `complete` |
| WindowsFullContentOrdinary | `Builds/Windows-FullContent-Ordinary` | `complete` |
| WindowsFullContentPreview | `Builds/Windows-FullContent-Preview` | `partial` |
| AndroidFullContent | `Builds/Android-FullContent` | `complete` |
| AndroidFullContentOrdinary | `Builds/Android-FullContent-Ordinary` | `complete` |
| AndroidFullContentPreview | `Builds/Android-FullContent-Preview` | `partial` |

Each successful flavor writes `manifest.json` at the root:
`schemaVersion`, `presentation` (`FullContent`), `configuration`,
`contentStage`, `contentVersion` (`full-content-1`), `drivingVersion`,
`candidate` and `writtenUtc`. `candidate` is `true` only for
`contentStage=complete` — a preview can never be promoted to an
ordinary or accepted full-content candidate by relabeling.

## Scenario evidence — `Tools/FullContentScenario.py` (Task 9 slice A)

The external harness reconciles the fixed production contract above
against captured observations on a real player's validation stream.
The expected inventory is synthesized from the same constants
`FullContentValidation.cs` enforces — never from observed rows, so a
mutant that removes evidence can never shrink the expectation.

```powershell
# Development: the scenario owns the process(es).
python Tools/FullContentScenario.py --launch Builds/Windows-FullContent/FamilyRacing.exe `
    --evidence Logs/fc-catalog --scope catalog
python Tools/FullContentScenario.py --launch <player.exe> `
    --evidence Logs/fc-campaign --scope campaign
python Tools/FullContentScenario.py --launch <player.exe> `
    --evidence Logs/fc-modes --scope modes
python Tools/FullContentScenario.py --launch <player.exe> `
    --evidence Logs/fc-lan --scope lan --players 2

# Qualification: attach to independently launched players (ADB-
# forwarded Android descriptors accepted for the lan scope).
python Tools/FullContentScenario.py --connections <run>/connection.json `
    --evidence Logs/fc-attach --scope catalog
```

`--evidence` must name a **new** directory. `--fixture` marks every
artifact `fixture:true` for harness development; fixture evidence can
never produce a production `passed` verdict.

Scopes:

- `catalog` — inventory reconciliation only, no driving. Walks the
  schema-7 `progression.worlds` rows, selects each available world via
  `campaign.selectWorld`, and reconciles its published
  `campaignEvents` against the 72+6 contract.
- `campaign` — fresh synthetic profile through every authored campaign
  event in order (regulars then class finales per world), each driven
  through the real command surface; writes one evidence record per
  event with identities, command/trace references, completion and
  objective-bit proofs, rewards, duration and capture validity.
- `modes` — Quick Race and Time Trial over all 12 course×class
  combinations; time trials must seat the normalized starter and mint
  zero campaign credit.
- `lan` — the shared M5 LAN workload on 2–4 endpoints (normalization,
  shared pause, gadget authority, disconnect/AI/rejoin, retained
  results, host loss).

Evidence layout under the evidence directory: `result.json` (verdict,
checks, coverage totals, named human-only gaps), `events.ndjson`
(one record per expected event), `coverage.json` (full reconciliation:
per-cell status, defects, gaps, reward facts), `modes.json` (modes
scope), and per-endpoint `<role>-evidence/` directories with
`attach.json`/`process.json` plus the raw `commands.ndjson` /
`state.ndjson` traces.

Verdict rules: `passed` only when all 78 cells are proven on a
non-fixture run with a valid capture — completed under a bound
receipt, human attribution, measured duration, all objective bits with
proofs, plus the reward-level facts (8 vehicles, 6 distinct rival
victories, ≥6 non-victory extra stars per region). Missing evidence is
a named gap (`partial`); contradictory evidence — duplicates,
unexpected rows, AI-attributed finishes, proofs for unearned bits,
dropped/corrupt stream records — is a defect (`failed`). Finishing an
event alone never proves its objectives, and completion is never
counted as objective achievement.

Slice-B deferrals: authored per-course route/checkpoint observations
(solo driving currently rides the shared graybox pursuit follower),
the real 78-event traversal, the physical LAN matrix, pacing
measurement and family playtests.

## Current state

No production content is authored under `Generated/FullContent` yet.
Every full-content mode therefore fails at the content gate with named
reasons — the correct, test-pinned behavior
(`Tests/EditModeAdapter/FullContentValidationTests.cs`). Do not bypass
the gate by authoring throwaway content; Tasks 5–8 fill the inventory
and flip the gate.

## Evidence

Row-level evidence lives in `docs/CONTENT_MATRIX.md`. All production
rows are `notVerified` until Tasks 5–9 produce captured runs. When a
`Tools/FullContentScenario.py` run produces evidence, link the row to
its per-event record in `<evidence>/events.ndjson` and the
reconciliation in `<evidence>/coverage.json` — never to
`result.json`'s verdict alone, and never to a `--fixture` run.
