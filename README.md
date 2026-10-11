# Family Racing

A local-multiplayer family racing game for Android tablets and Windows PC, in
early development. Design and acceptance criteria live in `GAME_PLAN.md`;
progress is tracked in `IMPLEMENTATION_STATUS.md` and
`docs/FOUNDATIONS_PLAN.md`.

## What exists today

**Foundation shell, no gameplay yet.** `Assets/FamilyRacing/Core` is a
Unity-free, C# 9, .NET Standard 2.1-compatible assembly:

| Area | Files | Contract |
|---|---|---|
| Profiles | `Core/Profiles/*.cs` | `ProfileSave` (schema 1: id, slot, avatar, colorIndex, language, camera, reducedCameraShake, three volumes, coins, unlockedVehicles), `ProfileFactory.Create(slot, "en"\|"he")` with per-slot defaults (fox/rabbit/panda/raccoon, chase camera, 0.65/0.85/1.0 volumes, `car-01`+`motorcycle-01`), `IProfileSerializer`, `ProfileStore`, `ProfileLoadResult`. |
| Driving input | `Core/Driving/*.cs` | Immutable `DriverInput` (steering clamped to [-1,1], non-finite → 0, six command flags), `DriverInputComposer.Compose` (clamp summed steering, OR commands) and `FromButtons` (right−left steering). |
| Shell text | `Core/Localization/ShellText.cs` | Static `en`/`he` catalog (29 keys), `Get`, `Keys`, `IsRightToLeft`. Hebrew stored in logical order. |
| Commands | `Core/Automation/*.cs` | Immutable `GameCommand` (copied `ReadOnlyDictionary` params), terminal `CommandResult` (`applied`/`rejected`/`failed`, duplicate flag, original tick preserved), `CommandDescriptor` discovery schema. |
| Foundation service | `Core/Foundation/*.cs` | `FoundationService` — profiles/settings/navigation/input/pause commands, 10k reject-not-evict identity cache, read-only `Lookup`, deep-copy `FoundationState` snapshots incl. `FeatureCoverage` (all `notImplemented`) and `PresentationState`. |

`Assets/FamilyRacing/Runtime` adds the Unity app (`FoundationApp`, 30 Hz
observation ticks), a keyboard+touch input adapter dispatching `input.set` on
change through the same `Execute` path agents use, a bilingual UI Toolkit
shell (4 animal profile cards, settings, input sandbox; tree rebuilds only on
screen/locale/selection change), and — behind `--validation-dir` in
editor/development builds only — a token-authenticated loopback NDJSON
command channel + `state.ndjson`/`capture-status.json` structured sink
(`Automation/ValidationBridge`, `StateStream`, `ControllerSession`,
`ValidationJson`). `Assets/FamilyRacing/Editor` holds the repeatable
`FoundationProjectSetup`/`FoundationBuild` entry points; generated
URP/panel/scene assets live under `Assets/FamilyRacing/Generated`.

**Profile persistence safety.** `ProfileStore` writes `profile-{0-3}.json` plus
a last-known-good `.bak`, atomically (`File.Replace` via a unique sibling temp
file, flushed first). Loads return `Missing` / `Loaded` /
`RecoveredFromBackup` / `Blocked`. Corrupt, newer-schema, foreign-identity, or
orphaned files are never overwritten or silently reset — saves fail with a
safe `InvalidOperationException`, and serialized output is deserialized and
re-validated (identity + slot) before any disk write.

## Verifying

```powershell
# Offline test run (no NuGet restore, no network). Compiles the shared
# sources with the local .NET SDK and a bundled Unity nunit.framework.
Tools\Verify.ps1 -Mode CoreOffline
# or directly:
Tools\OfflineTestRunner\Run-OfflineTests.ps1 [-NunitFrameworkPath <dll>] [-EditorRoot <dir>]

# Standard harness once NuGet restore is explicitly approved (needs network):
dotnet restore Tools\CoreTests\FamilyRacing.CoreTests.csproj
Tools\Verify.ps1 -Mode Core        # dotnet test --no-restore

# Environment prerequisites for the pinned Unity editor:
Tools\Verify.ps1 -Mode CheckEnvironment

# Tooling self-check (fake editor layout in %TEMP%, no Unity needed):
Tools\Test-VerifyTooling.ps1

# Unity 6.3 (batch, offline) via Verify.ps1 — logs + exit codes propagate:
Tools\Verify.ps1 -Mode Setup      # FoundationProjectSetup
Tools\Verify.ps1 -Mode EditMode   # Logs\verify-editmode-results.xml
Tools\Verify.ps1 -Mode PlayMode   # Logs\verify-playmode-results.xml
Tools\Verify.ps1 -Mode Windows    # Builds\Windows\FamilyRacing.exe (Mono dev)

# Repeatable offline Android build — Unity export, then bundled Gradle with
# --offline (no downloads; missing cache entries fail with diagnostics):
Tools\Verify.ps1 -Mode Android    # Logs\android-offline\<runId>\result.json

# Saved-profile restart evidence on the Windows dev player — two real
# processes share one synthetic store via --validation-profile-dir while each
# gets a fresh capture directory:
python Tools\RestartScenario.py Builds\Windows\FamilyRacing.exe --evidence <new-dir>

# Expected-fault qualification on the same build — launches three fresh
# validation players (graceful disconnect, TCP reset, stalled sink) and
# verifies durable invalid status, reconnect, original outcome lookup and
# duplicate protection. Passing means the injected faults were detected —
# every capture stays INVALID; it never certifies missing history:
python Tools\StreamRobustnessScenario.py Builds\Windows\FamilyRacing.exe --evidence Logs\stream-fault-<new-name>

# Validation: keep this launch terminal alive (background its shell in an
# agent environment), then run the scenario from a second terminal.
$exe = (Resolve-Path "Builds\Windows\FamilyRacing.exe").Path
$run = Join-Path (Get-Location) "Logs\validation-<unique-name>"
$player = Start-Process $exe -ArgumentList "--validation-dir",$run -PassThru
$player.WaitForExit()
# From the second terminal, using the same run directory:
python Tools\AgentScenario.py <dir>\connection.json --evidence <new-evidence-dir>
```

Full command/wire-protocol reference: `docs/COMMANDS.md`; command →
state-field coverage registry: `docs/FEATURE_COVERAGE.md`.

`Tools/OfflineTestRunner/Runner.cs` is a small reflection runner supporting
`[Test]`, `[TestCase]`, `[SetUp]`, `[TearDown]`; it exits nonzero when zero
tests are discovered.

## Remaining gates (Task 3 qualification)

- Windows IL2CPP module is now installed for the pinned editor
  (`Tools\Verify.ps1 -Mode CheckEnvironment` OK since 2026-10-11,
  owner-approved). No IL2CPP build has been produced or qualified yet —
  Windows still ships the Mono development build, and M1 acceptance does
  not require a particular Windows scripting backend. Kept as a separate
  build-hardening item. Android Build Support incl. SDK/NDK/OpenJDK is
  present and exercised by the offline build path.
- Android APK (`Builds\Android\FamilyRacing.apk`, ~47 MB) is built by the
  offline path (`Tools\Verify.ps1 -Mode Android`, Unity export + bundled
  Gradle `--offline`; `Logs\android-offline\<runId>\result.json` records
  status/hash). Latest run rebuilt the APK from current source
  (`Logs\android-offline\20261011-193051-31b4d38aaa6f49fa9d4791f5fe422f76\result.json`,
  SHA-256 `DD8EF19C…70CB0B`). Still not device-qualified — `adb devices`
  (bundled platform-tools) found no attached device on 2026-10-11; no
  ADB/on-device run exists. Report:
  `docs\tasks\foundation-device-qualification-report.md`.
- Saved-profile restart evidence now exists: `Tools\RestartScenario.py`
  drove two real player processes against one synthetic store and verified
  identical identities, all six settings, and unchanged save-file hashes
  (`Logs\qualification-continuation\restart-final\result.json`). The
  `--validation-profile-dir` override is honored only next to
  `--validation-dir` in editor/development builds and is ignored by ordinary
  family launches.
- NuGet restore is no longer blocked: the nuget.org user-level source was
  added with owner approval (2026-10-11) and `Tools\Verify.ps1 -Mode Core`
  passes 225/225. `Tools\Verify.ps1 -Mode CoreOffline` remains the
  no-network path.
- Packaged bilingual fonts implemented: pinned Noto Sans / Noto Sans
  Hebrew / Noto Sans Symbols2 sources under `Assets\FamilyRacing\Fonts\`
  with OFL license and provenance (`README.md` there); generated dynamic
  FontAssets, Advanced Text Generator enabled, live font-readiness
  warning recomputes each refresh. OFL is also bundled into
  `Assets\StreamingAssets\ThirdPartyLicenses\Noto-OFL.txt` (verified
  byte-identical inside the Windows player and the APK).
- Physical device testing (A7 Lite etc.) still required.
- External agent scenario passed 11/11 checks on the final packaged-font
  build (`Logs\qualification-fonts\agent-evidence\result.json`), including
  the packaged-font/locale and camera-toggle steps; earlier lead runs
  passed 9/9 (`Logs\evidence-lead\result.json`, `Logs\evidence-visible-lead\result.json`).
  English and Hebrew window captures were lead-accepted
  (`Logs\qualification-fonts\player-evidence-2\screenshot-*.png`); the
  stream-robustness gate at that stage was EditMode 249/249 and
  PlayMode 25/25.
- Stalled-sink robustness is now bounded and evidenced: every synchronous
  validation socket write carries a 2000 ms send timeout
  (`ControllerSession.SendTimeoutMs`), a failed write closes the exact
  failed session so queued records skip it, and writer failure flips the
  sticky capture/`capture-status.json` contract without needing finish,
  overflow, or teardown — real loopback PlayMode regressions
  (`ValidationStreamRobustnessPlayTests`, RED observed before the fix);
  report `docs\tasks\foundation-stream-robustness-report.md`.
- External expected-fault qualification now passed on the same reviewed
  build: `Tools\StreamRobustnessScenario.py` drove three fresh validation
  players through graceful disconnect, TCP reset and stalled sink —
  durable invalid/incomplete status while each process lived, a fresh
  same-run snapshot with neutral input, original outcome lookup,
  duplicate redelivery with unchanged save-file hashes, finish staying
  invalid and a byte-frozen trace prefix — 18/18 checks
  (`Logs\qualification-external-stream-robustness\external-run-2\result.json`;
  report `docs\tasks\foundation-external-stream-report.md`).
  Unverified per `notVerified` at that stage (partially superseded by
  the foundation-gaps wave below): full physical keyboard/touch
  coverage, finish delivery to the original non-reading controller,
  graceful-exit autosave and the normal user profile path, human Hebrew
  reading/quality review, Android on-device behavior, any racing
  features.
- Settings-screen UI/command parity is now covered by real-widget PlayMode
  tests: `FoundationSettingsPlayTests` drives actual `Slider.value` callbacks
  and pooled pointer down/up on the rendered buttons against a synthetic
  `ProfileStore`, and proved a stale-captured-draft bug that dropped pending
  edits (fixed in `FoundationShell.Screens.cs`: `UpdateSettings` merges the
  live `Snapshot().draftProfile`). RED 4-fail/GREEN 5-pass evidence and the
  full gate (EditMode 249/249, PlayMode 30/30, Windows Mono build) are under
  `Logs\qualification-settings-parity\`; report
  `docs\tasks\foundation-settings-parity-report.md`. These are synthetic UI
  Toolkit callbacks — not OS touch and not a process-restart check.
- Foundation-gaps wave (2026-10-11, `docs\FOUNDATION_GAPS_PLAN.md`)
  closed the remaining automatable `notVerified` surfaces:
  `--validation-normal-profiles` (real `persistentDataPath` store under
  agent control), `Application.runInBackground` inside validation
  launches only (gate-found unfocused-player freeze), and hold-button
  registrations moved to trickle-down (test-found `Button.Clickable`
  defect that blocked real touch holds). New external evidence:
  `Tools\FinishDeliveryScenario.py` 6/6 (stalled-controller finish →
  terminal `streamError`, `captureClosed` for new controllers) and
  `Tools\NormalProfileScenario.py` 6/6 (real store, `taskkill` graceful
  exit → `processExit`, saved profiles survive, drafts never
  autopersist) (`Logs\qualification-gaps\`, `Logs\qualification-exit\`;
  report `docs\tasks\foundation-gaps-report.md`). **Latest recorded
  gate: EditMode 253/253, PlayMode 53/53** (`Logs\verify-editmode-results.xml`,
  `Logs\verify-playmode-results.xml`), Core `dotnet test` 225/225,
  AgentScenario 11/11, Restart 6/6, StreamRobustness 18/18 — the earlier
  249/25 and 249/30 counts cited above are historical per-pass records.
  Still open: OS-level physical keyboard/touch coverage, human Hebrew
  reading/quality review, Android on-device/ADB behavior, IL2CPP build
  qualification, and all racing features (`notImplemented`).
