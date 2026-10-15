# M5 complete gameplay loop — milestone qualification report

Date: 2026-10-15. Branch: `feat/m5-gameplay-loop`. Qualified revision:
`03ef5d2` (`fix: stop the LAN verdict overclaiming what it observed`) —
every suite figure below was re-run on this revision; external scenario
evidence ran against the development player binary that is still on disk
at HEAD (`Builds\Windows\FamilyRacing.exe`, exe SHA-256
`B7CCA8B82003185F22644D0EE1C23B3640DE1B996802D4F5D0501E287011AFA6`; the
only commits after that build touched the Python harness and docs, so
runtime source is unchanged). This report and the doc updates land in
one docs-only commit on top of `03ef5d2`; no runtime, content or test
code changed after the recorded runs.

Plan: `docs/superpowers/plans/2026-10-14-complete-gameplay-loop.md`,
tasks 1–12. Spec authority: `GAME_PLAN.md` §1–5. Reproducible command
surface: `docs/GAMEPLAY_LOOP_RUNBOOK.md`.

**Status: development substantially complete; acceptance NOT met.** Two
separate verdicts, per the task contract:

- *Development completion* — Tasks 1–8, 10 and 11 are implemented,
  reviewed and tested: profile schema 3 with lossless copies/migration,
  the shared local race modes and authoritative metrics, host-
  authoritative gadgets, objectives/bounties evaluation, atomic
  reward/purchase/record transactions, mode-specific selection rules,
  the bilingual command/UI surface, schema-6 state, LAN protocol-3
  gameplay-fingerprint admission, and the external
  `GameplayLoopScenario` harness with its corruption suite. **Task 9
  was never dispatched** — production Sunny Island content is gated on
  M4 A7 Lite visual/performance evidence + owner acceptance that does
  not exist. The packaged player therefore binds **no gameplay
  catalog** (`Foundation.unity`: `gameplayCatalog: {fileID: 0}`;
  `progression.available=false`, `campaignEvents=0`).
- *Milestone acceptance* — **not met.** M5 requires two real courses,
  26 playable authored events, externally-proven persisted
  progression/purchases/records, normalized LAN/Time Trial evidence,
  complete command/state coverage and the device/human rows. The
  authored-content and device/human requirements are `notVerified`,
  not failed; nothing here claims otherwise.

## Automated gate (fresh runs on the qualified revision)

| Suite | Command | Result |
|---|---|---|
| Core (offline runner) | `Tools\Verify.ps1 -Mode CoreOffline` | **803 passed / 0 failed / 0 skipped** |
| EditMode | `Tools\Verify.ps1 -Mode EditMode` | **875 passed / 0 failed / 1 skipped** (876 total; `Logs\verify-editmode-results.xml`) — the skip is `AdapterEditModeTests.EveryCueHasWiredClipForBothLocales` (pre-existing `[Ignore]` — tutorial voice clips pending owner `AZURE_SPEECH_KEY`) |
| PlayMode | `Tools\Verify.ps1 -Mode PlayMode` | **309 passed / 0 failed / 0 skipped** (`Logs\verify-playmode-results.xml`) — both previously-carried impaired-link tests now pass after the Task-1 reliable-ordered snapshot fix |
| Python tooling | `python -m unittest discover -s Tools -p 'Test*.py'` | **279 passed / 0 failed** (~248 s; includes `Tools\TestGameplayLoopScenario.py`, 19 tests: the fixture suite plus every corruption mutant — double award, failed-save-shown-persisted, fake normalized tuning, missing delta, observer gap, stale reward context, timeout/incomplete result, wrong attribution, missing process generation — each failing verification with its reason) |
| Verify tooling | `Tools\Test-VerifyTooling.ps1` | **18 passed / 0 failed** |
| Android offline tooling | `Tools\Test-AndroidOffline.ps1` | **30 passed / 0 failed** |

The two impaired-link PlayMode failures and the font-fixture failure
carried into this milestone were **root-caused and fixed in Task 1**
(impaired links: snapshots moved to the reliable-ordered lane; font
test: a transient-fixture test defect) — they are no longer present.
The only remaining skip is the credential-gated voice-clip fixture,
labeled inherited/environmental.

## GameplayLoop flavor builds — blocked (honest outcome)

The plan's four flavors (`WindowsGameplayLoop`,
`WindowsGameplayLoopOrdinary`, `AndroidGameplayLoop`,
`AndroidGameplayLoopOrdinary`) **cannot be built at this revision**: the
Verify modes and `FoundationBuild` entry points are Task-9 deliverables
and do not exist — `Tools\Verify.ps1 -Mode WindowsGameplayLoop` fails
parameter validation (`"WindowsGameplayLoop" does not belong to the set
"CoreOffline,Core,CheckEnvironment,Setup,EditMode,PlayMode,Windows,WindowsOrdinary,Android,VisualBenchmarkSetup,WindowsVisualBenchmark,WindowsVisualBenchmarkOrdinary,AndroidVisualBenchmark,AndroidVisualBenchmarkOrdinary"`),
`Tools\Build-Windows.ps1` accepts only `Development|Ordinary|Physics`,
and `Tools\Build-AndroidOffline.ps1` accepts only `-Presentation
Graybox|VisualBenchmark`. Even if the modes existed, the flavors would
carry no authored catalog (same Task-9 gate). Recorded as
`notVerified`/`blocked` — no fake artifacts.

The existing flavors do build at/around this revision: the Windows Mono
development player on disk is the binary the Task-11 evidence ran
against (hashes above), and `Builds\Windows-Ordinary` was **rebuilt at
`03ef5d2`** for this report (`Logs\verify-unity-windowsordinary.log`:
`result=Succeeded errors=0`, `Core.dll` SHA-256 `67214164FB46B028253E737CA76EBB6C5CF19B31C88D2F60241AD8A00D097F39`).
Ordinary-endpoint absence re-verified on that fresh artifact
(`Logs\t12-ordinary-check`): the ordinary player ran 25 s with
`--validation-dir` — the directory stayed **empty** (no
`connection.json`, `state.ndjson` or `profiles\`) and the process held
**zero** TCP listeners; the Task-11 runs against the development player
are the discriminating positive control at the same runtime source.
`Builds\Android\*` and `Builds\Windows-VisualBenchmark*` predate M5
runtime changes — **stale** for M5 claims; Android endpoint absence at
HEAD is compile-time by construction (`DEVELOPMENT_BUILD` unset strips
the validation bridge) and no device exists to re-check on.

## External scenario evidence (Task 11, referenced — not re-run)

All runs: development player above, `catalog.available=false`,
loopback transport + AI fill labeled. Verdict `partial` everywhere is
the honest ceiling of a build with no authored catalog — progression
rows are `notVerified` with the named reason, never fabricated.

| Evidence dir | Scope | Verdict | Rows |
|---|---|---|---|
| `Logs\task11-smoke-2` | smoke | partial | 13 passed / 10 notVerified (23 checks): fresh profile, full tutorial incl. gadget lesson, graduation trial, **process restart with durable profile** and a distinct second generation; campaign/coins/earnings, garage purchase, failed-save+retrySave, Quick Race, Time Trial record, replay — all `notVerified` `contentUnavailable` |
| `Logs\task11-campaign` | campaign | partial | 4 notVerified: the scope reports the honest count **0 authored events** (legacy/empty catalog); `campaign.coverage` names the Task-9/hardware gate |
| `Logs\task11-lan-2c` | lan, 2 endpoints | partial | 17 passed / 3 notVerified (post-tightening harness): setup/discovery/join, roster normalization on **both** classes (`lan.normalization` + `lan.normalization.motorcycle` — shared `vehicleId`/`eventId` asserted), shared pause, human-attributed gadget use, process kill → AI takeover → credentialed rejoin reclaiming the same seat, duplicate `results.request` dedup, retained result re-served to a relaunched generation without re-minting, armed write-deny failed-save → `lan.results.retrySave` settled, permanent departure → terminal `undeliverable`, host loss → client recovery |
| `Logs\task11-lan-4` | lan, 4 endpoints | partial | 18 passed / 2 notVerified — same matrix with three client joins; **pre-tightening** wording — read row claims against `task11-lan-2c` |

`lan` rows that stay `notVerified` in both runs:
`lan.unevenUnlocks` (no catalog → profiles cannot own uneven
unlocks/upgrades), `lan.tuningBaseline` (the wire streams no per-seat
tuning object — equal class tuning is a Core-rule fact, unit-covered),
`lan.delivery.conflicting` (no honest external command can mint a
conflicting same-resultId redelivery — covered by Core dedup tests and
fixture mutants). `task11-lan-2`, `task11-smoke-1` are superseded
iteration artifacts, not evidence.

## M5 requirement checklist

Verdicts: `passed` | `failed` | `notVerified`, with the exact
artifact/run identity and the named remaining owner.

| # | Requirement (plan §global constraints / Task 12) | Verdict | Evidence / remaining owner |
|---|---|---|---|
| 1 | Two real courses (Harbor Hop + Lighthouse Loop) with readable route, two legal shortcuts, landmark jump, four spawn slots, recovery poses, pickup placement | `notVerified` | No authored courses exist — Task 9 gated on M4 A7 Lite evidence + owner approval. Owner: controller/Task 9 dispatch (blocked on hardware owner for the A7 Lite gate) |
| 2 | 26 playable authored events (2 courses × 2 classes × 6 types + 2 finales), positive measured `parTicks` | `notVerified` | `Logs\task11-campaign`: `campaignEvents=0`; catalog machinery + validation exist in code (`GameplayCatalog`, `GameplayCatalogAssetTests`), content absent. Owner: Task 9 (blocked) |
| 3 | Persisted progression/purchases/records (profile schema 3, atomic rewards, exactly-once, restart-safe) | `passed` (unit + restart) / externally `notVerified` | CoreOffline suite (`ProgressionProfileTests`, `ProgressionRewardTests`, `TimeTrialRecordTests`, `UpgradeTests`); external `restart.progress` + `restart.generations` passed (`Logs\task11-smoke-2`); campaign/purchase/record external rows `notVerified` (`contentUnavailable`) — owner: Task 9 content + re-run |
| 4 | Normalized LAN within class (upgrades ignored, cosmetics kept) | `passed` (reachable surface) / `lan.tuningBaseline` `notVerified` | `Logs\task11-lan-2c`: uniform roster asserted both classes; per-seat tuning equality unverifiable externally (no tuning object on the wire) — Core-covered. Owner: external-evidence gap documented, no further work unless a tuning slice is streamed |
| 5 | Normalized Time Trial records by course/class, records-not-progress | `passed` (Core) / externally `notVerified` | `TimeTrialRecordTests`, `ModeEligibilityTests`; `timeTrial.record` notVerified in smoke-2 (`contentUnavailable`). Owner: Task 9 content + re-run |
| 6 | Six event types per course/class; completion/introductory/mastery stars; replay union; finale gate 24 completions + ≥30 stars; non-victory advancement | `passed` (Core rules) / authored-content arm `notVerified` | `CampaignObjectiveTests`, `CampaignRules` boundary pins (23+30 locked, 24+29 locked, 24+30 open, replay no-inflation); zero authored events to drive them on. Owner: Task 9 |
| 7 | Three rotating challenge bounties + one-time rival bounties; AI actions earn no human credit | `passed` (Core) / authored arm `notVerified` | `BountyTests`, `DivergedBoundSlotReceivesNoFacts`, attribution pinned in `RaceMetricsAccumulator`; no catalog to exercise live. Owner: Task 9 |
| 8 | Upgrades 3 categories × 3 levels at 60/120/180, additive ≤1.15; cosmetics owned/equipped, no stat effect | `passed` (Core + UI) / external purchase `notVerified` | `UpgradeTests` price/cap/idempotency, `GameplayScreensPlayTests` garage; `garage.purchase` notVerified in smoke-2. Owner: Task 9 + re-run |
| 9 | Host-authoritative gadgets (turbo/shield/foam) with pickups, cooldowns, AI parity | `passed` | `GadgetRulesTests`, `GadgetPlayTests`, `GadgetNetworkPlayTests`; live: `tutorial.gadget` (smoke-2), `lan.gadget` human attribution (lan-2c) |
| 10 | Local modes seat correctly: Campaign/Quick = human + 3 same-class AI; Time Trial = lone normalized; abort/DNF no rewards | `passed` (Core + PlayMode) | `SoloRaceTests`, `SoloRacePlayTests`, `RaceModeRules.CanEnter` shared by UI/commands; externally gated on catalog |
| 11 | Command-complete loop surface + full schema-6 state stream | `passed` | `GameplayCommandTests` (open gating, starts, purchases, results commands, no-grant invariant), `GameplayStreamPlayTests` (slices on every boundary incl. menus/failed saves, delta reconstruction); `docs/COMMANDS.md` + `docs/FEATURE_COVERAGE.md` updated |
| 12 | LAN admission fences incompatible gameplay fingerprints before seat allocation | `passed` | `Protocol3FencesLegacyPeerBeforeSeatAllocation`; `LanSessionService` binds catalog fingerprint / legacy fallback |
| 13 | Delivery lifecycle: explicit leave → terminal `undeliverable`, transient drop stays pending, fingerprints unchanged | `passed` | `ExplicitLeaveTerminatesPendingDeliveryButDisconnectDoesNot`; live `lan.departure`/`lan.retainedState`/`lan.delivery.dedup` in lan-2c |
| 14 | Credentialed rejoin: no input stall, seat reclaimed | `passed` | Task-1 fix (drain starvation + reclaim offer-skip); `RejoinedHumanInputAppliesWithinCommandDeadline`; live `lan.rejoin`/`lan.fault.kill`/`lan.takeover` in lan-2c; `Logs\ns-m5-task1-fix` 23 checks |
| 15 | Offline after install; arcade backend default; physics opt-in | `passed` (unchanged posture) | No network dependency added; `VehicleTuningSelectsArcadeUntilDeliberatelyConfigured` |
| 16 | US$0 budget | `passed` | `docs/ASSET_INVENTORY.md` — gadget WAVs/catalog added, all original/synthesized, US$0 |
| 17 | Four GameplayLoop build flavors + ordinary-endpoint absence | `notVerified` | Blocked — tooling/content are Task 9's (see build section). The ordinary-endpoint mechanism re-verified on a fresh `WindowsOrdinary` build at `03ef5d2` (`Logs\t12-ordinary-check`: empty validation dir, zero listeners); GameplayLoop-flavor builds/artifacts remain unproduced. Owner: Task 9 (blocked) |
| 18 | A7 Lite hosting/performance (30 fps, p95 ≤40 ms, 800 MB, sustained, switches) | `notVerified` | No device attached (`adb devices` empty). Owner: hardware owner (Daniel) |
| 19 | Physical LAN matrix (tablet↔tablet, tablet↔PC both directions, offline Wi-Fi, four humans, impaired net, rematches/reconnect, ordinary-build play) | `notVerified` | All task-11 evidence is loopback + AI fill, labeled. Owner: hardware owner; a fourth device is still needed |
| 20 | Owner/Hebrew/child checks (comprehension, losses progress, RTL/audio/touch, first-world duration/enjoyment) | `notVerified` | No human review run; voice clips credential-gated. Owner: Daniel (owner) + Hebrew speaker + child playtest |
| 21 | Full-campaign external proof (all 24 + finales, ≥6 non-victory stars, once-only rewards, per-objective achievability) | `notVerified` | `Logs\task11-campaign` — 0 authored events. Owner: Task 9 + scenario re-run on authored build |

## M1–M4 dependency reconciliation (plan §Dependencies)

| Dependency | Disposition at HEAD |
|---|---|
| M4 A7 Lite visual acceptance + sustained performance | **Open** — `notVerified`. No device. Blocks Task 9 and therefore M5 acceptance. Owner: hardware owner + owner acceptance |
| Credentialed-rejoin input stall (~110–130 s) | **Closed** — Task 1 root-caused (harness drain starvation + product reclaim offer-skip), fixed, bounded by command-deadline watchdog + `RejoinedHumanInputAppliesWithinCommandDeadline`; `Logs\ns-m5-task1-fix` green; no recurrence in task-11 LAN runs |
| Permanently departed result recipient | **Closed** — policy implemented (explicit leave terminalizes outstanding rows `failed`/`undeliverable`; transient drops stay pending; fingerprints immutable) and evidenced live (`lan.departure`) |
| Memory sampler after relaunch | **Code closed, evidence open** — `GenerationSamplers` per-generation sampling shipped (Task 1); a fresh qualifying run producing gen-2 samples is still required — device/perf rows stay `notVerified` |
| Known baseline failures | **Closed** — font fixture defect and both impaired-link transport failures fixed in Task 1; the only remaining non-pass is the credential-gated voice-clip `[Ignore]` |
| Ordinary-build overhead/device evidence | **Open** — no matched-workload capture A/B; ordinary players compile endpoints out by construction (structurally unmeasurable by this harness) |
| Voices, Hebrew, child/device review | **Open** — voice clips need `AZURE_SPEECH_KEY` (owner); human reviews pending |
| Four-human physical LAN | **Open** — owner has two tablets + PC; a fourth device is still required; AI fill/loopback stay labeled substitutes |

M1–M4 milestone posture is unchanged and re-confirmed: **none is
accepted** — M1 in progress (device/human), M2 in progress
(device/human + ordinary perf), M3 development complete / acceptance
open (physical matrix), M4 development complete / acceptance open
(A7 Lite matrix, owner review). M5 inherits every open row; this report
closes none of them.

## Self-review notes

- Every suite figure above is from a fresh run at `03ef5d2`
  (`Logs\verify-editmode-results.xml`, `Logs\verify-playmode-results.xml`,
  console output for CoreOffline/Python/PowerShell suites). The only
  non-pass is the documented credential-gated `[Ignore]`.
- Task-11 evidence is *referenced*, not re-run (per dispatch ruling);
  `task11-lan-2c` post-dates the verdict-tightening fix and is the
  authoritative 2-endpoint record; `task11-lan-4` is pre-tightening and
  labeled as such.
- The development player the evidence ran on is byte-identical to the
  artifact on disk at HEAD (recorded `buildFiles` hashes re-checked:
  exe `B7CCA8B8…`, `Core.dll B43FECF1…`, `Runtime.dll AFCD5D5A…`);
  runtime source is unchanged since that build — later commits touched
  only the Python harness and docs.
- `Builds\Windows-Ordinary` was rebuilt at `03ef5d2` and its
  validation-endpoint absence re-verified (`Logs\t12-ordinary-check`) —
  fresh same-source evidence for the ordinary-build mechanism; the M5
  flavor rows themselves stay blocked/notVerified.
- No gameplay content was fabricated for this report; no tuning changes
  were needed — no evidence contradicted the shipped defaults.
- Unrelated dirty files in the working tree (VisualBenchmark materials,
  generated assets, `ps4Passcode`, `AndroidManifest.xml`, editor churn)
  were left uncommitted; only documentation changed in this task.
- Nothing was pushed; no vault writes.
