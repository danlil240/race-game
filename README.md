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

- Windows IL2CPP module still missing (Windows ships Mono development build;
  Android Build Support incl. SDK/NDK/OpenJDK present).
- Android APK (`Builds\Android\FamilyRacing.apk`, ~46 MB) is now built by the
  offline path (`Tools\Verify.ps1 -Mode Android`, Unity export + bundled
  Gradle `--offline`; `Logs\android-offline\<runId>\result.json` records
  status/hash). Still not device-qualified — no ADB/on-device run exists.
- Saved-profile restart evidence now exists: `Tools\RestartScenario.py`
  drove two real player processes against one synthetic store and verified
  identical identities, all six settings, and unchanged save-file hashes
  (`Logs\qualification-continuation\restart-final\result.json`). The
  `--validation-profile-dir` override is honored only next to
  `--validation-dir` in editor/development builds and is ignored by ordinary
  family launches.
- NuGet restore blocked pending network approval.
- Hebrew/Latin font assets not yet bundled; the shell surfaces an explicit
  missing-font qualification warning instead of wrong glyphs.
- Physical device testing (A7 Lite etc.) still required.
- Lead agent scenario passed all 9 checks on the current build
  (`Logs\evidence-lead\result.json` and, post-blank-shell-fix,
  `Logs\evidence-visible-lead\result.json`); the game-window capture
  `Logs\game-window.png` was reviewed and accepted as the actual UI.
  Still unverified per `notVerified`: full physical keyboard/touch
  coverage, reconnect/failure scenarios beyond PlayMode coverage,
  graceful-exit autosave and the normal user profile path (the restart
  evidence covers explicitly saved synthetic profiles only), Hebrew font
  quality (blank glyphs — no bundled font), Android on-device behavior, any
  racing features.
