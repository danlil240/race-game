# Full Content Runbook (M6 Task 4, slice A)

Validation and packaging contract for the full-content inventory:
three worlds (`sunny-island`, `redrock-canyon`, `snowy-peaks`), six
courses, eight vehicle models, 72 regular events plus six rival
finales, bilingual display names and final bilingual voice prompts.

This document is a skeleton — it covers the gates and flavors that
exist today. The loader/runtime binding is Task 9 + slice B scope and
is deliberately not implemented here.

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

## Current state

No production content is authored under `Generated/FullContent` yet.
Every full-content mode therefore fails at the content gate with named
reasons — the correct, test-pinned behavior
(`Tests/EditModeAdapter/FullContentValidationTests.cs`). Do not bypass
the gate by authoring throwaway content; Tasks 5–8 fill the inventory
and flip the gate.

## Evidence

Row-level evidence lives in `docs/CONTENT_MATRIX.md`. All production
rows are `notVerified` until Tasks 5–9 produce captured runs.
