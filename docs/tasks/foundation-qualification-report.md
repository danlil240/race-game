# Foundation Qualification Report - offline Android build + saved-profile restart

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
  `Application.persistentDataPath/Profiles`. The selector never precreates a
  *validation* profile path - `ProfileStore.Save` creates the selected
  directory on first write, including a shared override; ordinary startup
  retains its existing `Directory.CreateDirectory` behavior.

Safety: the override only exists inside `#if UNITY_EDITOR || DEVELOPMENT_BUILD`
validation mode - it cannot redirect a normal user's saves.

### Restart evidence runner

`Tools/RestartScenario.py` owns a fresh evidence directory and drives two
real player processes: the first creates two profiles, applies all six
settings, and saves; the second (a distinct runId started only after the
first exits) must load both identities and every saved field. Assertions
cover identical SHA-256 primary+backup files before/after, durable capture
finish, and a player-code/content fingerprint - SHA-256 of the launcher exe,
every file under `FamilyRacing_Data`, and `UnityPlayer.dll` - checked between
and after the processes, because the launcher alone stays byte-identical
when only managed code changes.

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
| Override regressions | `Unity.exe -batchmode -projectPath . -runTests -testPlatform EditMode -testFilter FamilyRacing.Tests.ValidationProfileDirectoryTests` | RED 4/4 -> GREEN 4/4 | `Logs/qualification-continuation/editmode-red.xml`, `editmode-green.xml` |
| Windows Mono dev build | `-executeMethod FamilyRacing.EditorTools.FoundationBuild.Windows` | `result=Succeeded errors=0 warnings=0` | `Logs/qualification-continuation/final-build-windows.log` |
| Real restart | `python Tools/RestartScenario.py Builds/Windows/FamilyRacing.exe --evidence Logs/qualification-continuation/restart-final` | **passed 6/6 checks**; identical save hashes across two processes | `Logs/qualification-continuation/restart-final/result.json` (+ per-phase `*-evidence/`, `*-run/` traces) |
| Scenario fixtures | `python -m unittest discover -s Tools -p "Test*Scenario.py" -v` | 17/17 OK | `Logs/qualification-continuation/python-scenario-tests-final.log` |
| Final EditMode | `-runTests -testPlatform EditMode` | 241/245 | `Logs/qualification-continuation/final-editmode.xml/.log` |
| Final PlayMode | `-runTests -testPlatform playmode` | 18/20 | `Logs/qualification-continuation/final-playmode.xml/.log` |
| Offline Android | `Tools\Verify.ps1 -Mode Android` | **succeeded** - Unity export + bundled Gradle `--offline`, BUILD SUCCESSFUL, 57 tasks | `Logs/android-offline/20261011-123733-d853a75210284129ba058d708bd3606e/result.json` (+ `export.log`, `export-process.log`, `gradle.log`) |
| APK | - | `Builds/Android/FamilyRacing.apk`, 46,622,529 bytes, SHA-256 `B639CD00E92CE9543D3B2070EFC1D52698EA183EB74DE5D5B95DE4C728C3D805` | published by the offline run |

The six failing tests are all pre-existing packaged-font fixtures gated on
deferred Task 1 font downloads; no test was weakened or removed to pass:

- `FamilyRacing.Tests.FoundationFontSetupTests.SetupEnablesAdvancedTextInPinnedEditorProjectSettings`
- `FamilyRacing.Tests.FoundationFontSetupTests.SetupPackagesDynamicSourcesCoveringBothCatalogsAndLanguageButtons`
- `FamilyRacing.Tests.FoundationFontSetupTests.SetupRepairsFontReferencesAndPreservesAuthoredFallbacks`
- `FamilyRacing.Tests.FoundationFontSetupTests.SetupRepairsMissingTextBindingWithoutReplacingExistingPanel`
- `FamilyRacing.Tests.FoundationFontPlayTests.SceneBindsAdvancedBilingualFontAndKeepsPhysicalSteeringInRtl`
- `FamilyRacing.Tests.FoundationFontPlayTests.ReadinessTracksMissingAndRestoredSettingsWithoutLocaleChange`

### Artifact diagnostics (not failures)

- The successful Windows build log ends with Mono teardown noise
  (`abort_threads: Failed aborting id ...`, `debugger-agent: Unable to
  listen on 5084`) after the successful `BuildReport` line - editor shutdown
  chatter, not a product or test failure, and no claim of a clean log is made.
- The successful Gradle run prints a deprecation warning that the generated
  build scripts will be incompatible with a future Gradle 10 - recorded as
  diagnostic, not a defect in this build.

Historical attempts preserved: `restart-evidence/` (first real run - failed
on the then-buggy exact-double comparison and correctly proved the override
worked), `restart-evidence-2/`, `Logs/android-offline/20261011-122824-*/`
(first offline APK on the pre-revert tree).

## What this does NOT prove

- No graceful-exit/autosave or normal user profile path - only explicitly
  saved synthetic profiles in an isolated store.
- No reconnect, slow/broken-sink failure injection, or physical input/UI
  parity beyond the earlier 9-check scenario (`Logs/evidence-lead/`,
  `evidence-visible-lead/`).
- No human Hebrew acceptance - packaged fonts now ship and machine checks
  pass, and the lead accepted the English/Hebrew window captures as actual
  rendered UI, but native-speaker reading/quality review is still open.
- No Android device qualification - the APK was built offline but never
  installed or run; ADB/device acceptance remains open.

## How to reproduce

```powershell
Tools\Verify.ps1 -Mode Windows    # dev player -> Builds\Windows\
python Tools\RestartScenario.py Builds\Windows\FamilyRacing.exe --evidence <new-dir>
Tools\Verify.ps1 -Mode Android    # export + offline Gradle -> Logs\android-offline\<runId>\
```

Full diff of this continuation: `Logs/qualification-continuation/final-changes.diff`;
before-snapshots: `Logs/qualification-continuation/baseline/`;
ledger: `.superpowers/sdd/FOUNDATION_QUALIFICATION_PLAN/progress.md`.

## Task 1 record - packaged bilingual text (implemented; human review open)

Final integration gate on the packaged-font build (2026-10-11, pinned
editor; evidence under `Logs/qualification-fonts/`):

- Full suites once: EditMode **249/249** (`final-editmode.xml`) and
  PlayMode **22/22** (`final-playmode.xml`) - all green.
- Windows Mono dev build `result=Succeeded errors=0 warnings=0`
  (`final-build-windows.log`).
- Offline Android `Verify.ps1 -Mode Android` **succeeded**
  (`Logs/android-offline/20261011-134021-98837ad9964547dba7f88ab9fbe916cb/result.json`;
  APK 47,402,599 bytes).
- Unchanged `Tools/AgentScenario.py` on a fresh validation player:
  **passed 11/11** including "packaged bilingual fonts and locale command
  boundaries" and "camera toggle command updates selected profile draft"
  (`agent-evidence/result.json`).
- Unchanged `Tools/RestartScenario.py` on the final build:
  **passed 6/6** (`restart-final/result.json`).
- OFL bundling: `Assets/StreamingAssets/ThirdPartyLicenses/Noto-OFL.txt`
  is byte-identical to `Assets/FamilyRacing/Fonts/OFL.txt`; verified
  identical inside `Builds/Windows/FamilyRacing_Data/StreamingAssets/ThirdPartyLicenses/`
  and APK entry `assets/ThirdPartyLicenses/Noto-OFL.txt`.
- Fresh Hebrew convenience preview left running for the user:
  pid 11496, `preview-he/screenshot-he.png`
  (validation dir `preview-he/run/`).

Lead-review rework applied before the gate: `FoundationFonts.IsReady`
uses the pinned `StyleKeyword` semantics (Undefined = literal inline
value, Null/unset = resolvedStyle, other keywords cannot qualify) plus an
attached-root (`root.panel`) check; the `WarmFont` catch workaround was
removed from editor code and the authored-font fixtures now construct
valid fonts via the real `FontAsset.CreateFontAsset` factory (fixture
correctness fix, assertions unchanged); the importer test now exercises
actual repair (breaks `includeFontData`/`fontTextureCase`, verifies setup
restores them); a new reflection-based PlayMode regression covers
detached-root/inline-Standard/inline-null-font readiness; the StreamingAssets
license copy + byte-identity test were added.

Historical notes below describe the pre-rework evidence chain.

Network approval received for the four pinned Noto sources only.

Fonts are vendored byte-for-byte from notofonts/noto-fonts revision
ffebf8c1ee449e544955a7e813c54f9b73848eac under `Assets/FamilyRacing/Fonts/`
(three TTFs + `OFL.txt` + `README.md` provenance with URLs and SHA-256s;
verified as real TTF data, not HTML).

Changes:

- New `Assets/FamilyRacing/Editor/FoundationFontSetup.cs` - TTF importer
  repair (`includeFontData`, `fontTextureCase = Dynamic`), three generated
  dynamic FontAssets in `Generated/` (Latin/Hebrew/Symbols) with material +
  atlas serialized as subassets, sourceFontFile binding repair via
  SerializedObject (`m_SourceFontFile`), packaged-fallback repair that
  preserves authored entries and GUIDs, ATG enabled via the pinned
  `UIToolkitProjectSettings.enableAdvancedText` property (fails clearly if
  the API changes).
- `FoundationProjectSetup` - calls font setup after `EnsurePanelSettings`;
  panel repair now fixes a missing `textSettings` binding in place instead
  of returning early (sizing/resolution and authored settings preserved;
  `defaultFontAsset` set to Latin only when null).
- New `Assets/FamilyRacing/Runtime/FoundationFonts.cs` - live readiness
  from the actual bound document: attached root, inline/resolved Advanced
  generator, bound font == panel default, whole fallback chain dynamic with
  packaged sources (cycle-safe), and full repertoire coverage from both
  ShellText catalogs + language labels + ASCII punctuation/digits +
  U+25C0/U+25B6.
- `FoundationApp` - `hebrewFontAvailable`/`fontWarning` recomputed at every
  refresh boundary (not just locale changes); warning
  "Bilingual font or Advanced Text Generator configuration unavailable."
  shown even in English because the Hebrew control is always visible.
- `FoundationShell` - root gets `unityTextGenerator = Advanced` +
  `FontDefinition.FromSDFFont(defaultFontAsset)` on bind/rebind only, so a
  deliberate mutation is reported by readiness instead of silently fixed.
- `ProjectSettings/UIToolkitProjectSettings.asset` (new,
  `m_EnableAdvancedText: 1`); `FoundationPanelSettings.asset` gained the
  ATG `m_ICUDataAsset` reference; `FoundationTextSettings.asset` gained
  `m_DefaultFontAsset`.

Evidence (`Logs/qualification-fonts/`):

- Focused EditMode font tests (post-rework): 8/8 passed
  (`editmode-font-rework.xml`); the initial iteration logged 7/7
  (`editmode-font-green.xml`) with the pre-strengthening importer test,
  and new tests RED first (`editmode-font-red.xml`, 6 failed).
- Focused PlayMode font tests (post-rework): 4/4 passed
  (`playmode-font-rework.xml`; initial iteration 3/3 in
  `playmode-font.xml`), including the fallback-removal/source-less
  readiness recovery test and the detached-root/inline-Standard/null-font
  reflection regression.
- Pinned-editor `FoundationProjectSetup.Create`: exit 0
  (`unity-setup.log`, benign teardown noise only).
- Windows Mono dev build: `result=Succeeded errors=0 warnings=0`
  (`unity-build-windows.log`).
- Real player (`player-evidence-2/`, final code): launched with
  `--validation-dir`, connection.json + state.ndjson captured; window
  screenshots `screenshot-en.png` and `screenshot-he.png` (1296x839,
  non-uniform pixels). Stream shows en/ltr -> he/rtl with
  `hebrewFontAvailable=true` and empty `fontWarning` after the command
  boundary. Player left running (pid 58700, recorded in `result.json`)
  for lead review. Earlier `player-evidence/` run is preserved from the
  pre-final-code build.
- `changes.diff` - full focused diff; `baseline/` holds before-snapshots
  including the pre-existing dirty-doc baseline.

Not claimed: human Hebrew acceptance, Android device qualification,
Milestone 1 completion, or any racing features; no game commits/pushes.
