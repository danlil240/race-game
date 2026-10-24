# M6 Task 0 — prerequisite reconciliation and fresh gate evidence

Date: 2026-10-15. Branch: `feat/m6-full-content`. Measured revision:
`fbbd7cd` (`docs: add the milestone-6 full-content implementation plan`)
— every suite figure below is a fresh run on this revision.

Plan: `docs/superpowers/plans/2026-10-15-full-content.md`, Task 0.
Dispatch ruling (binding): only the reconcilable portion was in scope —
the M4 tablet/visual gate has no hardware (`adb devices` empty,
re-checked 2026-10-15), and M5 Task 9's own gate (M4 A7 Lite evidence +
owner acceptance) is open, so no authored content, `CourseLoader`,
`GameplayLoopSetup`, `SunnyIslandCampaignAuthoring`, or GameplayLoop
build flavors were produced or attempted.

**Status: reconciliation complete; the production-prerequisite chain is
unchanged-open.** Fresh automated evidence is green (one pre-existing
credential-gated skip); every hardware/human/authored-content row stays
`notVerified` with a named owner. No milestone was relabeled.

## Fresh automated gate (all runs on `fbbd7cd`)

| Suite | Command | Result |
|---|---|---|
| Core (offline runner) | `Tools\Verify.ps1 -Mode CoreOffline` | **806 passed / 0 failed / 0 skipped** |
| EditMode | `Tools\Verify.ps1 -Mode EditMode` | **878 passed / 0 failed / 1 skipped** (879 total; `Logs\verify-editmode-results.xml`, log `Logs\verify-unity-editmode.log`) — the skip is `AdapterEditModeTests.EveryCueHasWiredClipForBothLocales` (pre-existing `[Ignore]`; tutorial voice clips pending owner `AZURE_SPEECH_KEY`, unset in this environment) |
| PlayMode | `Tools\Verify.ps1 -Mode PlayMode` | **309 passed / 0 failed / 0 skipped** (`Logs\verify-playmode-results.xml`, log `Logs\verify-unity-playmode.log`) |

Comparison against the M5 closeout baselines at `03ef5d2`
(CoreOffline 803/803, EditMode 875/876, PlayMode 309/309): **no new
failures.** CoreOffline +3 and EditMode +3 are the tests added by the
three post-`03ef5d2` Core commits (`c631eaa` localized event-type
strings + `LocalizationTests`, `4e32fff` `readOnlyProfile` advertisement
+ `GameplayCommandTests`, `fd9c8ab` authored-pickup fingerprint coverage
+ `GameplayCatalogTests`). The M4-carried PlayMode impaired-link
failures and the EditMode font-fixture failure remain fixed — they did
not recur.

## Fresh external scenario evidence

The development player on disk (`Builds\Windows\FamilyRacing.exe`,
SHA-256 `B7CCA8B82003185F22644D0EE1C23B3640DE1B996802D4F5D0501E287011AFA6`)
is byte-identical to the artifact the M5 Task-11 evidence ran against.
It launched; a fresh smoke run executed:

| Evidence dir | Command | Verdict | Rows |
|---|---|---|---|
| `Logs\t0-smoke` | `python Tools\GameplayLoopScenario.py --launch Builds\Windows\FamilyRacing.exe --evidence Logs\t0-smoke --scope smoke` | **partial** | 13 passed / 10 notVerified / 0 failed (23 checks): fresh profile, full tutorial incl. gadget lesson, graduation trial, process restart with durable profile and a distinct second generation — all passed; campaign/stars/coins/earnings, garage purchase, failed-save + retrySave, Quick Race, Time Trial record, replay — all `notVerified` `contentUnavailable` |

`catalog.available=false`, `campaignEvents=0` — the honest ceiling of a
build with no bound gameplay catalog. The binary predates the three
post-`03ef5d2` Core commits (localization strings, `readOnlyProfile`
advertisement, pickup-site fingerprint coverage); it remains the correct
discriminating artifact for the unchanged no-catalog posture, and the
scenario records the build hashes in `result.json`. The campaign/LAN
scopes were not re-run — their M5 evidence (`Logs\task11-campaign`,
`Logs\task11-lan-2c`) is measured on this same binary and the open rows
they carry are catalog/hardware-gated, unchanged by HEAD.

## Source reconciliation at `fbbd7cd`

| Claim in prior reports / plan | Verified at HEAD |
|---|---|
| Packaged player binds no gameplay catalog | Confirmed — `Assets/FamilyRacing/Generated/Foundation.unity`: `gameplayCatalog: {fileID: 0}` (line 426); `track`/`coursePrefab` still single-bind the shared graybox course (lines 417, 428) |
| `CourseLoader`, `GameplayLoopSetup`, `SunnyIslandCampaignAuthoring` do not exist | Confirmed absent — no `Runtime/Driving/CourseLoader.cs`, `Editor/GameplayLoopSetup.cs`, or `Editor/SunnyIslandCampaignAuthoring.cs`; no `CourseLoader` references in source |
| GameplayLoop build flavors do not exist | Confirmed — `Tools\Verify.ps1` `ValidateSet` (line 40) has no GameplayLoop modes; `Build-Windows.ps1` accepts only `Development|Ordinary|Physics`; `Build-AndroidOffline.ps1` has no `-Presentation GameplayLoop` |
| Contract identities | LAN wire protocol **3**, observation schema **6**, gameplay rules **gameplay-3** (`NetworkSettings.cs` lines 17/30/40), profile schema **3** (`Core/Profiles/ProfileValidation.cs` line 20) — matching the M6 plan's baseline, unchanged since M5 |
| No Android device | Confirmed — bundled platform-tools `adb devices` lists zero devices (re-run 2026-10-15) |
| No real lightmap bake | Confirmed — `VisualBenchmark.unity` `m_LightingDataAsset` resolves to the built-in default; no `LightingDataAsset`/`.exr` in Assets |
| Voice-clip credential | `AZURE_SPEECH_KEY` unset — the EditMode `[Ignore]` is still environmental |

## Open dependency / evidence table

Each inherited open gate, its owner, the next check, and whether it
blocks M6 production work (Tasks 5–8 content production, Task 4 slice B
loader, Task 9 live qualification runs, Task 10 device qualification).

| # | Open gate | Owner | Next check | Blocks M6 production? |
|---|---|---|---|---|
| 1 | M4 A7 Lite visual/performance acceptance: real lighting bake, owner visual approval on SM-T220, both classes/cameras, four racers with tablet hosting, 30-min sustained render + memory evidence | Hardware owner (Daniel) supplies devices + owner acceptance; implementer runs capture | Attach the SM-T220 (`adb devices`), bake lighting, run the M4 device matrix on the fresh benchmark build | **Yes** — gates M5 Task 9 and therefore every production-content task |
| 2 | M5 Task 9: two authored courses, 26 events, `CourseLoader`, four GameplayLoop flavors | Implementer dispatch — blocked on gate 1 | Dispatch only after gate 1 evidence is recorded; then re-run M5 smoke/campaign/LAN on the authored build | **Yes** — binds the catalog (`progression.available`), unblocks all `contentUnavailable` rows |
| 3 | Physical LAN matrix: tablet↔tablet, tablet↔PC both directions, offline Wi-Fi, four humans, impaired network, rematches/reconnects, ordinary-build play | Hardware owner; a fourth device is still required | Run `NetworkSoak`/`GameplayLoopScenario --connections` against attached devices on a disconnected Wi-Fi | Yes — for M6 Task 10 device rows and M3/M5 acceptance; not for fixture-eligible Tasks 1–4 slice A |
| 4 | Owner / Hebrew / child reviews: comprehension, losses progress, RTL/audio/touch, first-world duration/enjoyment | Daniel (owner) + Hebrew speaker + child playtest | Human sessions once an authored build exists | Yes — acceptance rows; also blocks the family first-world review the M5 plan requires before expansion |
| 5 | Voice clips / final bilingual audio | Credential owner (`AZURE_SPEECH_KEY`) + listening review | Set the credential, run `Generate-TutorialCues`, un-ignore `EveryCueHasWiredClipForBothLocales` | Yes — M6 Task 8; currently the only non-pass in the fresh gate |
| 6 | Ordinary-build performance arm (matched-workload instrumentation overhead + device ordinary play) | Implementer with hardware; overhead A/B is runnable on PC now | Run `--perf-capture off`/on matched workloads; device arm waits on gate 1 | Partial — does not block fixture work; blocks the M6 Task 10 measured-overhead row |
| 7 | Gen-2 (post-rejoin) process-memory evidence | Implementer — tooling fixed in M5 Task 1 (`GenerationSamplers`); no qualifying run since | Produce gen-2 samples in a fresh qualifying run (M6 Task 9 sustained sessions) | Yes — for M6 Task 9/10 qualification rows; tooling itself is done |
| 8 | M5 authored-content external rows (`campaign.*`, `garage.purchase`, `timeTrial.record`, `lan.unevenUnlocks`, `lan.tuningBaseline` wire gap, `lan.delivery.conflicting`) | Follows gate 2 automatically | Re-run `GameplayLoopScenario` smoke/campaign/lan on the authored build | Yes — same chain as gate 2 |

Closed-by-M5 rows re-confirmed non-recurring in fresh evidence: the
credentialed-rejoin input stall (no recurrence; PlayMode green), the
impaired-link PlayMode failures, and the font-fixture EditMode failure.

## Milestone posture — unchanged

M1 in progress (device/human), M2 in progress (device/human + ordinary
perf), M3 development complete / acceptance open (physical matrix), M4
development complete / acceptance open (A7 Lite matrix, owner review,
bake), M5 code complete for dispatched scope / acceptance not met
(authored content + device/human rows). **No milestone is accepted; this
task changed none of them.** M6 execution has started on
`feat/m6-full-content`; fixture-eligible Tasks 1–3 and the test-only
slices of 4/9 may proceed; production Tasks 5–8 and the live-evidence
arms stay blocked on gates 1–2.

## Self-review notes

- All suite figures are fresh runs on `fbbd7cd`, not carried M5 numbers:
  XML files and Unity logs rewritten this session (timestamps in
  `Logs\`). The single skip is the documented credential-gated
  `[Ignore]` — not a new failure and not hidden.
- The smoke run is real-player evidence on the recorded binary, labeled
  `partial`; its `contentUnavailable` rows are honest evidence of the
  open Task-9 gate, never counted as authored-content coverage.
- The `partial` smoke verdict exits the harness nonzero (honesty
  contract) — expected, not a defect of this run.
- `adb devices` was re-run today rather than trusting the recorded
  ruling; zero devices.
- The working tree's pre-existing asset/ProjectSettings modifications
  were left untouched; this task commits only this report and the
  `IMPLEMENTATION_STATUS.md` evidence update.
- Nothing was pushed; no vault writes.
