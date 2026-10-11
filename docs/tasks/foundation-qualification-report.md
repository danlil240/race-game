# Foundation Qualification Report — offline Android build + saved-profile restart

Date: 2026-10-11. Scope: `docs/FOUNDATION_QUALIFICATION_PLAN.md` Task 2
(repeatable offline Android build) and the restart portion of Task 3.
Branch `feature/family-racing`, uncommitted. No racing features; Milestone 1
remains in progress.

## Delivered

### Development-only synthetic profile store (`--validation-profile-dir`)

`FoundationApp.Awake` resolves the profiles directory through the new private
`SelectProfilesDirectory(validationDirectory, persistentDataPath, arguments)`
**before** `ProfileStore`/`FoundationService` are constructed:

- Editor/development builds launched with `--validation-dir` may also pass
  one `--validation-profile-dir <path>`; the value is normalized with
  `Path.GetFullPath` so a shared synthetic store survives across fresh,
  per-run capture directories. Without the override, validation keeps the
  legacy `<validationDir>/profiles` child.
- Malformed launches fail loudly: a duplicate flag, a missing value, an
  empty/whitespace value, or a next token starting `--` raise
  `ArgumentException` instead of silently testing the wrong store.
- Ordinary family launches (and release builds, where the validation bridge
  does not exist) ignore the flag entirely and keep
  `Application.persistentDataPath/Profiles`. Nothing is precreated;
  `ProfileStore.Save` creates the selected directory on first write.

Safety: the override only exists inside `#if UNITY_EDITOR || DEVELOPMENT_BUILD`
validation mode — it cannot redirect a normal user's saves.

### Restart evidence runner

`Tools/RestartScenario.py` owns a fresh evidence directory and drives two
real player processes: the first creates two profiles, applies all six
settings, and saves; the second (a distinct runId started only after the
first exits) must load both identities and every saved field. Assertions
cover identical SHA-256 primary+backup files before/after, durable capture
finish, and a whole-build fingerprint (launcher + `FamilyRacing_Data` +
`UnityPlayer.dll`) checked between and after the processes — the launcher
exe alone stays byte-identical when only managed code changes.

`assert_profile` compares the three volume fields as exact binary32 bits:
Core `ProfileSave` volumes are `float`, so JsonUtility's on-disk spelling
(`0.6499999761581421`) and the stream's shortest decimal (`0.65`) encode the
same value; the comparison accepts identical bits and still rejects any
different representable float. All other fields require exact value and
type.

## Evidence

Pinned editor `C:\Program Files\Unity\Hub\Editor\6000.3.26f1\Editor\Unity.exe`;
all Unity invocations serialized.

| Check | Command | Result | Evidence |
|---|---|---|---|
| Override regressions | `Unity.exe -batchmode -projectPath . -runTests -testPlatform EditMode -testFilter FamilyRacing.Tests.ValidationProfileDirectoryTests` | RED 4/4 → GREEN 4/4 | `Logs/qualification-continuation/editmode-red.xml`, `editmode-green.xml` |
| Windows Mono dev build | `-executeMethod FamilyRacing.EditorTools.FoundationBuild.Windows` | `result=Succeeded errors=0` | `Logs/qualification-continuation/final-build-windows.log` |
| Real restart | `python Tools/RestartScenario.py Builds/Windows/FamilyRacing.exe --evidence Logs/qualification-continuation/restart-final` | **passed 6/6 checks**; identical save hashes across two processes | `Logs/qualification-continuation/restart-final/result.json` (+ per-phase `*-evidence/`, `*-run/` traces) |
| Scenario fixtures | `python -m unittest discover -s Tools -p "Test*Scenario.py" -v` | 17/17 OK | `Logs/qualification-continuation/python-scenario-tests-final.log` |
| Final EditMode | `-runTests -testPlatform EditMode` | 241/245; only `FoundationFontSetupTests` ×4 failed (packaged fonts not yet delivered — Task 1) | `Logs/qualification-continuation/final-editmode.xml/.log` |
| Final PlayMode | `-runTests -testPlatform playmode` | 18/20; only `FoundationFontPlayTests` ×2 failed (same Task 1 gate) | `Logs/qualification-continuation/final-playmode.xml/.log` |
| Offline Android | `Tools\Verify.ps1 -Mode Android` | **succeeded** — Unity export + bundled Gradle `--offline`, BUILD SUCCESSFUL, 57 tasks | `Logs/android-offline/20261011-123733-d853a75210284129ba058d708bd3606e/result.json` (+ `export.log`, `export-process.log`, `gradle.log`) |
| APK | — | `Builds/Android/FamilyRacing.apk`, 46,622,529 bytes, SHA-256 `B639CD00E92CE9543D3B2070EFC1D52698EA183EB74DE5D5B95DE4C728C3D805` | published by the offline run |

Historical attempts preserved: `restart-evidence/` (first real run — failed
on the then-buggy exact-double comparison and correctly proved the override
worked), `restart-evidence-2/`, `Logs/android-offline/20261011-122824-*/`
(first offline APK on the pre-revert tree).

## What this does NOT prove

- No graceful-exit/autosave or normal user profile path — only explicitly
  saved synthetic profiles in an isolated store.
- No reconnect, slow/broken-sink failure injection, or physical input/UI
  parity beyond the earlier 9-check scenario (`Logs/evidence-lead/`,
  `evidence-visible-lead/`).
- No Hebrew rendering quality — packaged fonts are still absent (Task 1 RED
  pending network approval); `AgentScenario`'s bilingual gate stays deferred.
- No Android device qualification — the APK was built offline but never
  installed or run; ADB/device acceptance remains open.
- External-agent foundation scenario (`Tools/AgentScenario.py`) was not
  re-run on this build because it now enforces font readiness first.

## How to reproduce

```powershell
Tools\Verify.ps1 -Mode Windows    # dev player -> Builds\Windows\
python Tools\RestartScenario.py Builds\Windows\FamilyRacing.exe --evidence <new-dir>
Tools\Verify.ps1 -Mode Android    # export + offline Gradle -> Logs\android-offline\<runId>\
```

Full diff of this continuation: `Logs/qualification-continuation/final-changes.diff`;
before-snapshots: `Logs/qualification-continuation/baseline/`;
ledger: `.superpowers/sdd/FOUNDATION_QUALIFICATION_PLAN/progress.md`.
