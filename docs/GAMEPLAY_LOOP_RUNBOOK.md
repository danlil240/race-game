# Gameplay Loop Runbook

Milestone 5 delivers the complete gameplay loop — campaign events,
Quick Race, Time Trial, rotating challenge bounties, upgrades and
cosmetics, persisted progression, and the normalized LAN/result-delivery
rules — on shared Core rules and the existing command/state surfaces.
This runbook is the reproducible command surface for the loop: the
versions/contracts at HEAD, how to drive it externally, the evidence each
run must record, and the current content/build-flavor status. Spec
authority: `GAME_PLAN.md` §1–5; milestone plan:
`docs/superpowers/plans/2026-10-14-complete-gameplay-loop.md`; milestone
report: `docs/tasks/gameplay-loop-report.md`.

## Baseline record (Task 12, recorded 2026-10-15)

| Fact | Value |
|---|---|
| Branch | `feat/m5-gameplay-loop` |
| Revision | `03ef5d2` — `fix: stop the LAN verdict overclaiming what it observed` |
| Pinned editor | Unity `6000.3.26f1` at `C:\Program Files\Unity\Hub\Editor` |
| Profile schema | `3` (`Core/Profiles/ProfileValidation.cs` — ordered 1→2→3 migrations; `ProfileCopy.DeepCopy` owns every copy site) |
| State schema | `6` (`Core/Networking/NetworkSettings.StateSchemaVersion` — adds `soloRace` + `progression` slices) |
| LAN protocol | `3` (`NetworkSettings.LanProtocolVersion`) |
| Validation-command protocol | `1` (unchanged — do not confuse with the LAN protocol) |
| Gameplay rules version | `gameplay-3` (`NetworkSettings.GameplayVersion`; session admission binds the catalog compatibility fingerprint on non-legacy content, driving fingerprint otherwise — `LanSessionService.cs`) |
| Gameplay content identity | `gameplay-loop-1` (`GameplayCatalog.ContentVersion`); `legacy-1` marks the absent/legacy catalog |
| Result format | `formatVersion = 2` / `fr-result-fp-2` for new results; legacy `fr-result-fp-1` receipts stay valid replay barriers |

## Content status at this revision — read first

**The packaged player binds no gameplay catalog.** `Foundation.unity`
carries `gameplayCatalog: {fileID: 0}` (the gadget catalog
`gadget-catalog-01` *is* bound — turbo/bubble-shield/foam-trap), so
`progression.available` reports `false`, `campaignEvents` is `0`, and the
loop commands (`campaign.open`, `campaign.start`, `quickRace.start`,
`timeTrial.start`, `garage.open`, `bounties.open`, `upgrade.purchase`,
`cosmetic.equip`, `results.retrySave`) reject `contentUnavailable`.
Practice/tutorial/LAN racing remain fully playable.

This is honest, not a defect: Task 9 (the authored two-course Sunny
Island world with 24 regular events + 2 finales, measured par times,
rivals, earned vehicles/cosmetics, and the four GameplayLoop build
flavors) is **gated** on the M4 A7 Lite visual/performance evidence +
owner acceptance, which does not exist — no device is attached to this
environment. The Core rules, profile schema, UI, commands, gadgets and
LAN machinery are all implemented and unit-covered; the authored content
and its packaging are the missing piece.

### Build flavors

The Task-9 Verify modes (`GameplayLoopSetup`, `WindowsGameplayLoop`,
`WindowsGameplayLoopOrdinary`, `AndroidGameplayLoop`,
`AndroidGameplayLoopOrdinary`) **do not exist** — `Tools\Verify.ps1`
`-Mode` rejects them (`ValidateSet` lists only the foundation, benchmark
and graybox modes), `Tools\Build-Windows.ps1` accepts only
`Development|Ordinary|Physics`, and `Tools\Build-AndroidOffline.ps1`
accepts only `-Presentation Graybox|VisualBenchmark`. No `Builds\*-
GameplayLoop*` artifacts exist or can be produced at HEAD — the
four-flavor build row is `notVerified` (blocked: tooling and authored
content are Task 9's, gated as above). Do not substitute the Visual
Benchmark flavors: they carry `visual-1` benchmark content, not the
gameplay catalog.

The flavors that *do* exist build `Foundation.unity` (which now carries
the loop screens and the bound gadget catalog — they are the artifacts
the Task-11 scenario evidence below ran on):

| Command | Artifact |
|---|---|
| `Tools\Verify.ps1 -Mode Windows` | `Builds\Windows\FamilyRacing.exe` (Mono development) |
| `Tools\Verify.ps1 -Mode WindowsOrdinary` | `Builds\Windows-Ordinary\` (validation endpoints compiled out) |
| `Tools\Verify.ps1 -Mode Android` | `Builds\Android\FamilyRacing.apk` (IL2CPP development, offline Gradle) |

## Driving the loop externally

`Tools/GameplayLoopScenario.py` is the external qualification harness
(schema 7). Everything it drives goes through the normal command surface
— no command grants coins, stars or traversal. Where the loaded content
cannot support a step, the row reports `notVerified` with the named
reason; a run on the legacy/absent catalog reports `partial`, never
fabricated coverage. Verdicts: `passed` / `partial` / `failed`.

```powershell
# Attach to independently launched validation players (first descriptor
# is the LAN host; ADB-forwarded Android descriptors accepted):
python Tools\GameplayLoopScenario.py --connections <run>\connection.json [more] --scope smoke|campaign|lan --evidence <new-dir>

# Dev convenience — the scenario owns and kills/launches the processes:
python Tools\GameplayLoopScenario.py --launch Builds\Windows\FamilyRacing.exe [--players 2-4] --scope smoke|campaign|lan --evidence <new-dir>
```

`--fixture` is the explicit test-only mode against the synthetic fixture
player; every artifact carries `"fixture": true` and can never be
presented as real-player evidence.

Evidence layout per run: `result.json` (verdict, per-check rows with
reasons, build/executable SHA-256 fingerprints, catalog identity,
participant labels, process generations), per-player `<name>-evidence/`
(`commands.ndjson`, `observed.ndjson`, `process.json`, `launch.json`),
the player's own run dir (`state.ndjson`, `capture-status.json`,
`profiles\`), and player logs. Every emitted stream is finished and
independently re-verified (sequence, delta bases, removed paths, schema,
capture completeness, terminal `finished`); every `lan.*` command must
reach a terminal outcome inside the deadline.

### Recorded runs at this revision

All against `Builds\Windows\FamilyRacing.exe` SHA-256
`B7CCA8B82003185F22644D0EE1C23B3640DE1B996802D4F5D0501E287011AFA6`
(`Core.dll B43FECF1…`, `Runtime.dll AFCD5D5A…`) — the same binary on
disk at HEAD; runtime source is unchanged since it was built (the only
later commits touched `Tools/GameplayLoopScenario.py` itself). All runs
are loopback + AI fill, labeled — never claimed as device/four-human
evidence.

| Run | Scope | Verdict | Rows |
|---|---|---|---|
| `Logs\task11-smoke-2` | smoke (2 process generations incl. real restart) | partial | 13 passed / 10 notVerified — tutorial incl. gadget lesson and restart persistence passed; every progression row notVerified (`contentUnavailable`) |
| `Logs\task11-campaign` | campaign | partial | 0 passed / 4 notVerified — the scope honestly reports **0 authored events** on this build |
| `Logs\task11-lan-2c` | lan, 2 endpoints | partial | 17 passed / 3 notVerified — normalization (car + motorcycle roster), pause, attributed gadget, kill → AI takeover → credentialed rejoin, dedup/retained results, armed failed-save → `lan.results.retrySave`, departure → `undeliverable`, host loss; `unevenUnlocks`/`tuningBaseline`/`delivery.conflicting` notVerified |
| `Logs\task11-lan-4` | lan, 4 endpoints | partial | 18 passed / 2 notVerified — same matrix with three client joins; **pre-tightening** evidence (predates the lan-2c verdict-wording fix — read row claims against lan-2c) |
| `Logs\task11-smoke-1`, `Logs\task11-lan-2` | superseded | — | historical iteration artifacts (earlier harness bugs); not evidence |

Corruption-harness integrity: `Tools\TestGameplayLoopScenario.py` — 19
tests inside the 279-test Tools suite; every mutation (double award,
failed save shown persisted, fake normalized tuning, missing delta,
observer gap, stale reward context, timeout/incomplete result, wrong
attribution, missing process generation) fails verification with its
reason.

## Local verification

```powershell
Tools\Verify.ps1 -Mode CoreOffline   # offline Core suite (no NuGet)
Tools\Verify.ps1 -Mode EditMode      # Logs\verify-editmode-results.xml
Tools\Verify.ps1 -Mode PlayMode      # Logs\verify-playmode-results.xml
python -m unittest discover -s Tools -p "Test*.py"
Tools\Test-VerifyTooling.ps1
Tools\Test-AndroidOffline.ps1
```

The gameplay-loop command/gating parity lives in
`Tests/EditMode/GameplayCommandTests.cs`; screen parity in
`Tests/PlayMode/GameplayScreensPlayTests.cs`; schema-7 stream coverage in
`Tests/PlayMode/GameplayStreamPlayTests.cs`; catalog/asset validation in
`Tests/EditModeAdapter/GameplayCatalogAssetTests.cs`; pure rules in the
Core suite (`CampaignObjectiveTests`, `BountyTests`, `UpgradeTests`,
`SoloRaceTests`, `RaceMetricsTests`, `ProgressionRewardTests`,
`TimeTrialRecordTests`, `ModeEligibilityTests`, `GadgetRulesTests`,
`ProgressionProfileTests`).

## Open gates (all `notVerified` — owners in the milestone report)

- **Task 9 production content + four GameplayLoop flavors** — gated on
  the M4 A7 Lite evidence below; nothing authored or packaged yet.
- **A7 Lite (SM-T220, 3 GB) hosting/performance** — 30 fps, p95 ≤40 ms,
  800 MB process-memory budget, sustained sessions, mode/course switches.
- **Physical LAN matrix** — tablet↔tablet and tablet↔PC both hosting
  directions, internet-disconnected Wi-Fi, four physical humans (needs a
  fourth device), impaired network, rematches/reconnect, ordinary-build
  play.
- **Owner/Hebrew/child checks** — five-year-old reward/next-race/gadget/
  finish comprehension, losses-still-progress, upgrade/bounty
  understandability, text/RTL/audio/cameras/touch, first-world
  duration/retries/enjoyment measurement (never extrapolate the 6–10 h
  campaign target from one world).
- **Ordinary-build capture-overhead A/B** — matched instrumented vs
  `--perf-capture off`/ordinary workload on the same source/device.
- **Tutorial voice clips** — credential-gated (`AZURE_SPEECH_KEY`);
  `EveryCueHasWiredClipForBothLocales` stays `[Ignore]`d.
