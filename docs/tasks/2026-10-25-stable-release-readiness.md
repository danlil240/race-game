# Stable release readiness — 2026-10-25

Daniel selected the full three-world campaign before the first stable release,
then explicitly deferred final voice generation. Stable publication remains
blocked; the existing GitHub preview is unchanged.

## Implemented

- Six original collider-backed campaign circuits across Sunny Island, Redrock
  Canyon and Snowy Peaks. Each has four spawns, AI routes, recovery, pickups,
  two driveable shortcuts and an optional jump with a real gap/landing.
- Eight tuned vehicle variants using the existing car and motorcycle rigs,
  72 typed regular events, six finales, ordered progression, rival rewards,
  repeatable challenges, upgrades, gadgets and three difficulty presets.
- A dedicated production scene with the full campaign plus the existing
  onboarding course, two helper events and original tutorial lesson rules.
  Onboarding helpers are separate from the six-course/78-event campaign.
- Complete Windows/Android build paths explicitly select the production
  scene after validation; they cannot package the foundation scene instead.
- Bilingual prompt manifest, generator, catalog authoring and state-driven
  playback. No silent clips or substitute recordings were generated.
- Authored-course JSON export and per-driver pursuit geometry in the real
  command-driven scenario tooling, with no graybox fallback for missing routes.
- Existing permanent signing certificate verified against release.json;
  all four GitHub production environment signing secrets configured. No
  credential values were copied into the repository or this report.

## Verification

- Core: 907 passed (`dotnet test`, Release configuration).
- Unity EditMode: 1,074 passed, one existing ignored audio test, zero failures
  (`Logs/verify-editmode-results.xml`, before the added jump cases).
- Final focused course tests: eight passed, including actual ArcadeMotor +
  UnityMotorWorld airborne/clean-landing events for both starter classes on
  every course, plus road/shortcut/visibility/identity checks
  (`Logs/production-courses-final.xml`).
- Unity PlayMode: 389 passed, zero failures
  (`Logs/verify-playmode-results.xml`). A save-failure UI test was corrected
  to arm its serializer failure before the final driving tick can commit.
- Python tooling: 349 passed; the authored-route change additionally passed
  122 targeted route/network/loop/full-content/benchmark tests.
- Windows development campaign player built successfully and its ZIP passed
  archive integrity, required-file and non-candidate manifest checks.
- Real built-player catalog probe: no failed checks; Sunny Island publishes
  all 26 expected rows, later worlds correctly lock on a fresh profile.
  Overall catalog evidence is `partial`, since inventory inspection does not
  prove driving/skill objectives (`Logs/campaign-catalog-20261025/result.json`).
- Real built-player race smoke: `harbor-hop-car-circuit`, two laps, completed
  through normal steering/boost inputs with zero recoveries. Result and full
  command/state trace verified. Screenshots were captured and the start
  screenshot visually inspected (`Logs/campaign-live-smoke-20261025/report.json`).
  This automated desktop run does not constitute physical/human acceptance.
- Independent integration/code review found no remaining P1/P2 findings after
  fixing an unreachable landing bounty, restoring onboarding and including
  jump geometry in recovery bounds.

## Test build

`Builds/FamilyRacing-FullCampaign-Validation-Windows-x64.zip` is a development
test build, explicitly `contentStage=partial`, `candidate=false`.
SHA-256: `d0e59d7df206a7e4b1cfefab08ada6b07e8c05dcedd8f9b966ca28d3b83347b5`.
It is not a production release or a stable update asset.

## Remaining before stable publication

1. Supply/generate the 28 English/Hebrew campaign recordings and 18 tutorial
   recordings, review them by listening, and rebuild the authored catalogs.
   Voice generation was explicitly deferred by Daniel. The existing Azure
   generator requires locally configured credentials; do not paste keys in chat.
2. Build the actual ordinary Windows and permanent-key-signed Android
   candidates from a clean source commit with the final audio. The connected
   SM-T220 was detected, but no production candidate was installed and no
   existing installation or saves were removed.
3. Complete the required physical/human acceptance records: all campaign
   events/objectives/rewards, tutorial, offline fresh install, touch controls,
   save/reinstall/update/backup behavior, LAN, sustained performance/memory,
   visual and bilingual audio review. Reconcile acceptance-matrix.json against
   hashes of the exact candidate packages. Existing rows remain unqualified.
4. Owner must recover and verify an off-machine signing-key backup. Local
   certificate verification and GitHub secret configuration do not prove custody.
5. Automated production builds additionally need a dedicated Windows Actions
   runner with activated Unity and populated offline caches; none is registered.
   Manual local production packaging can use the installed toolchain.
6. Choose the next stable version/code, stamp both players, push the matching
   stable tag, validate the draft assets and evidence, then publish as Latest.
   The existing preview's version is 1.0.0/code 1; a later stable update must
   use a higher version/code for update notifications/progression. Debug-signed
   Android preview installs cannot update in place to the permanent release key.

No stable tag, acceptance record or published production release was fabricated.
