# Foundations continuation report: environment verification, Tasks 1–2

Date: 2026-10-11. Workspace: `C:\Users\example-user\race-game`, unborn `feature/family-racing` (all files untracked; nothing committed).

## Environment verification

| Item | Result |
|---|---|
| Installed Unity editors | `C:\Program Files\Unity\Hub\Editor\6000.6.5f1` only (`Editor\Unity.exe` present, Unity 6.6, changeset 3ff58d469c8a). |
| Pinned editor 6000.3.26f1 | **Not found** under `C:\Program Files\Unity\Hub\Editor` or other checked locations (`C:\Program Files\Unity`, common drive roots). Hub `info-log.json` contains no `6000.3` reference — the 6000.3.26f1 install was never queued. Hub instead downloaded and installed 6000.6.5f1 (installer payloads still in Hub `downloads\`). |
| Install in progress? | No. Hub log: `Install batch finished` at 2026-10-11T06:48:47Z; download queue and `paused-downloads.json` are empty. Unity Hub + Unity.Licensing.Client processes are running (left running, not touched). |
| Installed modules | `Editor\Data\PlaybackEngines`: `windowsstandalonesupport`, `WebGLSupport`; offline documentation installed. **Missing: Android Build Support/SDK/NDK/OpenJDK and Windows IL2CPP** required by the plan. |
| License | Unity Personal entitlement **activated** (Hub licensing log: entitlement licenses activated, personal EULA agreed; license file under the Hub package `LocalCache\Local\Unity\licenses`). The 6000.6.5f1 editor launched once in batch mode (`%LOCALAPPDATA%\Unity\Editor\Editor.log`). |
| .NET | SDK `10.0.401`, runtime `10.0.12`, `Microsoft.NETCore.App.Ref` pack present under `C:\Program Files\dotnet`. |
| NuGet cache | `C:\Users\example-user\.nuget\packages` does not exist — **no cached packages**, so `dotnet restore`/`dotnet test` for `Tools/CoreTests` requires network and was NOT run. |

### Blockers for later tasks

- Pinned editor 6000.3.26f1 absent in checked locations; only unapproved 6000.6.5f1 present (plan forbids substitution without approval).
- Android + Windows IL2CPP modules missing → no required platform builds yet.
- NuGet restore blocked pending network approval.

## Verification commands and results

| Command | Result | Log |
|---|---|---|
| `Tools\OfflineTestRunner\Run-OfflineTests.ps1` (profile stubs) | RED 0/86, all `NotImplementedException` | `Logs\profile-tests-RED.log` |
| same, after Task 1 implementation | GREEN 86/86 | `Logs\profile-tests-GREEN.log` |
| same, Task 2 tests vs stubs | RED 38 new-case failures | `Logs\input-localization-tests-RED.log` |
| same, after Task 2 implementation | GREEN 126/126 | `Logs\core-tests-GREEN.log` |
| same, correction-pass tests vs pre-fix code | RED 7 failures (newlineVehicle ×2, serializer-message leak, file-path dir, changed-slot, recovered-diagnostic ×2) | `Logs\corrections-RED.log` |
| `Tools\Verify.ps1 -Mode CoreOffline` (final, exact unchanged sources) | **GREEN 133/133**, exit 0 | `Logs\verify-coreoffline.log`, `Logs\core-tests-GREEN-final.log` |
| `Tools\Verify.ps1 -Mode Core` (`dotnet test --no-restore`) | Honest failure NETSDK1004 — no restore assets, no network attempted | `Logs\verify-core-norestore.log` |
| `Tools\Verify.ps1 -Mode CheckEnvironment` | Exit 1; actionable: pinned editor, Windows IL2CPP, Android module+SDK/NDK/JDK missing; .NET 10 SDK OK | `Logs\verify-checkenvironment.log` |
| `Tools\Test-VerifyTooling.ps1` vs pre-fix scripts | RED 8/12 — generic il2cpp wrongly accepted, Android SDK/NDK/JDK unchecked, cache gate blocked a complete layout, success wording overclaimed, invalid NUnit path fell back and ran the suite | `Logs\verify-tooling-RED.log` |
| `Tools\Test-VerifyTooling.ps1` vs fixed scripts | GREEN 12/12 — fixture builds a fake editor layout in %TEMP% | `Logs\verify-tooling-GREEN.log` |
| `csc /target:library /nostdlib /langversion:9 /r:<editor>\Data\NetStandard\ref\2.1.0\netstandard.dll <Core sources>` | Exit 0 — all Core APIs are netstandard-2.1-compatible (reference check only; not a Unity 6.3 compile or Unity qualification) | `Logs\netstandard-check\compile.log` (command, sources, reference, exit code) |
| PowerShell `Parser::ParseFile` on `Tools\Verify.ps1` | 0 errors | — |

## Offline test harness

`Tools\OfflineTestRunner\Run-OfflineTests.ps1` compiles `Assets/FamilyRacing/Core/**/*.cs` + `Assets/FamilyRacing/Tests/EditMode/*.cs` **unchanged** with the SDK `csc.dll` against the .NET 10 ref pack and a `nunit.framework.dll` (default: the one bundled under `-EditorRoot`, overridable via `-NunitFrameworkPath`), then runs `Runner.cs`. dotnet path, SDK/ref-pack/runtime dirs, and .NET 10 versions are discovered from the dotnet executable and sorted semantically. `Runner.cs` executes `[Test]`/`[TestCase]`/`[SetUp]`/`[TearDown]` via real NUnit assertions and exits nonzero when zero tests are discovered. Limitations: no TestCaseSource/categories/fixture attributes; console output, not TRX.

`Tools\Verify.ps1` modes: `CoreOffline` (above), `Core` (`dotnet test --no-restore`), `CheckEnvironment` — pinned editor is fixed internally at 6000.3.26f1 (only `-EditorRoot` is overridable); checks `Editor\Unity.exe`, `PlaybackEngines\WindowsStandaloneSupport\Variations\win64*il2cpp*`, `PlaybackEngines\AndroidPlayer` plus `SDK\platform-tools\adb.exe`, `NDK\ndk-build.cmd`, `OpenJDK\bin\java.exe`, and `dotnet --list-sdks` returning a stable 10.x SDK. Cache-directory existence is not a gate; success reports only "checked file prerequisites found" with license/import/build/device checks still required. An invalid explicit `-NunitFrameworkPath` fails instead of falling back.

## Task 1: Profile persistence — done

- `ProfileFactory.Create`: per-slot avatar/colorIndex, schema 1, `Guid "D"` id, explicit `en`/`he` validation, spec defaults.
- `ProfileValidation` (shared helper): schema 1 (newer reported separately), D-format non-empty Guid id, slot range/match, known avatar, colorIndex 0–3, language, camera, coins ≥ 0, finite volumes in [0,1], non-null vehicle list with `^[A-Za-z0-9]+(-[A-Za-z0-9]+)*\z` entries (absolute end — `\z` not `$`, so trailing newlines cannot slip through).
- `ProfileStore.Load`: Missing/Loaded/RecoveredFromBackup/Blocked; newer primary blocks even with valid backup; corrupt primary + valid backup recovers **without altering files**; safe diagnostics. Recovered diagnostic now reads "loaded from its backup; the original files were left unchanged" (no false restoration claim).
- `ProfileStore.Save`: validates profile → serializes → deserializes and re-validates output incl. id/slot match → refuses over unreadable/newer/foreign primary, invalid/future/orphaned backup → unique sibling temp, flushed write, `File.Replace` with `.bak` rotation → cleans only its own temp on failure. **All** serialization exceptions are wrapped in a safe generic `InvalidOperationException` (detail kept as inner exception); directory creation moved inside the same try so a file-path directory fails with the same safe message.

## Task 2: Shared input + bilingual text — done

- `DriverInput` (immutable readonly struct): ctor order (steering, drift, boost, gadget, recover, changeCamera, pause); finite steering clamped [-1,1], non-finite → 0; `default` neutral.
- `DriverInputComposer.Compose`: summed clamped steering (opposing cancels), commands OR. `FromButtons`: right−left, remaining commands false.
- `ShellText`: 29-key `en`/`he` catalog; `en`/`he` only; unknown key → `KeyNotFoundException`, bad language → `ArgumentException`, null key → `ArgumentNullException`; `IsRightToLeft` true only for `he`. `status.recovered` wording corrected (loaded from backup, files unchanged, saving off).
- Tests: `InputTests.cs` (steering extremes/NaN/±∞, ctor order, default neutral, button combos, compose clamp, OR), `LocalizationTests.cs` (required keys, non-empty both languages, no duplicates, en≠he, RTL, rejections).

## Corrections pass changes (this revision)

- Removed all `Assert.Multiple` wrappers from `ProfileTests.cs`/`InputTests.cs` (sequential `Assert.That`; Unity's bundled nunit 3.5.0 lacks `Assert.Multiple`) and deleted the source-transform shim — the unsupported-API dependency is gone so the sources compile unchanged in the offline runner; actual Unity EditMode compilation/execution is NOT yet verified.
- `VehicleId` regex `$` → `\z`; added `whitespaceVehicle`/`newlineVehicle` cases to both invalid-profile test lists (RED → GREEN).
- Save path: all serializer exceptions wrapped safely; `Directory.CreateDirectory` inside the write try; new test `SaveFailsSafelyWhenDirectoryPathIsAnExistingFile`.
- New coverage tests `SaveRefusesSerializerOutputWithChangedIdentity`/`...ChangedSlot` (verified output must keep submitted id/slot; passed immediately = coverage, not RED).
- New: `Tools/Verify.ps1`, `Tools/Test-VerifyTooling.ps1` (tooling fixture), `README.md`. Deleted: `Tools/OfflineTestRunner/OfflineCompat.cs`.
- Runner: zero-tests guard; script rewritten (derived dotnet paths, semver sort, net10, `-NunitFrameworkPath`, strict invalid-path failure).
- `CheckEnvironment` hardened: real Windows IL2CPP variation dirs (not generic `Data\il2cpp`), Android SDK/NDK/JDK tool checks, `dotnet --list-sdks` stable-10.x requirement, cache-dir gate removed, scoped success wording, pinned version fixed internally.

## Changed files

| File | Change |
|---|---|
| `Assets/FamilyRacing/Core/Profiles/ProfileValidation.cs` | New shared invariant helper |
| `Assets/FamilyRacing/Core/Profiles/ProfileFactory.cs` | Implemented |
| `Assets/FamilyRacing/Core/Profiles/ProfileStore.cs` | Implemented |
| `Assets/FamilyRacing/Core/Driving/DriverInput.cs` | New |
| `Assets/FamilyRacing/Core/Driving/DriverInputComposer.cs` | New |
| `Assets/FamilyRacing/Core/Localization/ShellText.cs` | New |
| `Assets/FamilyRacing/Tests/EditMode/InputTests.cs` | New |
| `Assets/FamilyRacing/Tests/EditMode/LocalizationTests.cs` | New |
| `Assets/FamilyRacing/Tests/EditMode/ProfileTests.cs` | Multiple removal, new cases/tests |
| `Tools/CoreTests/FamilyRacing.CoreTests.csproj` | Linked Driving/Localization sources and all EditMode tests |
| `Tools/OfflineTestRunner/{Runner.cs,Run-OfflineTests.ps1}` | Offline runner |
| `Tools/Verify.ps1` | New verification entry point |
| `Tools/Test-VerifyTooling.ps1` | Tooling regression fixture |
| `README.md` | New |
| `.gitignore` | Unignore `Tools/CoreTests/FamilyRacing.CoreTests.csproj`; ignore `Tools/**/bin|obj/` |
| `IMPLEMENTATION_STATUS.md` | Corrected environment facts; evidence |
| `docs/FOUNDATIONS_PLAN.md` | Tasks 1–2 checked; Task 3 deferred |
| `docs/tasks/foundations-continuation-report.md` | This report |

Core stays Unity-free, C# 9, .NET Standard 2.1-compatible (verified against the installed editor's netstandard 2.1 reference). Not run: Unity compile/EditMode/build (Task 3, blocked), `dotnet test` (restore blocked). Task 3 not started.
