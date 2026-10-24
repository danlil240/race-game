# Playful Chaos: first playable package

19 October 2026. Implements the first package (Tasks 1–2) of the [plan](../superpowers/plans/2026-10-18-playful-chaos.md). Human handling comparison and tablet acceptance remain open.

## Experience

Choose a driver, select a visible difficulty, and press **Race now**. Race against three AI on the existing course with its Sunny Island visual treatment. **Race again** starts a fresh race directly from results.

The Windows HUD uses compact position/lap, boost and gadget readouts with a small pause button. Keyboard controls remain left/right or A/D, Space drift, Shift boost, E gadget, R recovery, C camera, Escape pause. Touch devices use steering at the lower left and actions at the lower right. The development argument `--touch-controls` enables that layout on a keyboard PC for visual checks; it is not a physical multitouch qualification.

The experimental car has faster acceleration, a more controlled drift, stronger grip on release, and more forgiving wall contact. Small speed/boost FOV changes provide movement feedback; reduced motion fixes the lens at its baseline. Existing engine, drift, boost and landing audio comes from the shared presentation catalog.

## Build and launch

Run from this checkout:

```powershell
.\Tools\Verify.ps1 -Mode WindowsPlayfulChaos
& .\Builds\Windows-PlayfulChaos\FamilyRacing.exe
```

The build is explicitly partial content. Its profiles live under the game's persistent-data directory at `PlayfulChaos/Profiles`, separate from ordinary family profiles. Development validation launches still use their own capture/profile directories.

The experiment uses a validated Quick Race-only catalog. Campaign, garage, purchases, bounties and persistent rewards remain unavailable in this configuration. Existing full-content validation and release requirements still apply to production builds.

## Handling comparison

Two authored car assets live under `Assets/FamilyRacing/Generated/PlayfulChaos`: `car-responsive.asset` and `car-baseline.asset`. Both identify `car-01`; only one is bound in the scene. For an editor comparison, switch the car reference in the scene's DrivingWorld vehicles array and enter Play Mode. The setup repairs the default scene binding to responsive on a build, while preserving authored values in both assets. These are comparison candidates; no human preference has been inferred from automated runs.

For the first family session, observe whether the player can start unaided, exit a drift, read the next turn, recover from a mistake and voluntarily select another race. Record the age/experience, difficulty, camera, build identity, interruptions and replay choice before discussing changes.

## Next checkpoint

The next implementation package is Harbor Havoc course authoring, followed by collision/stunt feedback, a fountain hazard and rival route choices. The approved plan places a human handling checkpoint after this first package. Confirm driving/readability with the preview before extending those interactions.

## Verification and review

The Windows preview built successfully after the review fixes. The final targeted layout rerun passed 2/2 tests. Build records and English/Hebrew screenshots are local generated evidence under `docs/playtests/playful-chaos-preview/` and are excluded from Git.

The first package passed 849 core tests, 977 EditMode tests (one inherited ignored), 333 Python tooling tests and 27 verification-tooling checks. The runtime smoke run completed a two-lap race through ordinary driving inputs, displayed results, and started a fresh rematch without granting progression rewards. The player finished in 56.2 seconds; all three AI finished by 62.1 seconds. This existing course is shorter than the planned Harbor Havoc target.

Independent code review found two important layout issues: manual recovery was hidden on touch, and the tutorial overlapped the compact HUD. Both received reproducing tests before fixes. Recovery now remains visible and dispatches ordinary recovery input; the tutorial sits below the readouts in both supported languages.

The final PlayMode rerun passed 341/342 tests, including both new regressions. `PhysicsPredictionPlayTests.ImpairedLanPhysicalCorrections` exceeded its 3 m correction limit (3.872 m); an isolated rerun passed at 1.620 m. The earlier full 340-test run passed. This live-timing network test varies between runs; its intermittent failure remains unresolved and LAN qualification is open. No networking code was changed to suppress it.

Review boundaries remain open: human handling preference, voluntary replay, physical tablet reach and multitouch, subjective audio quality, LAN HUD parity and device qualification. Desktop touch simulation and captured images are layout evidence only. Tasks 3–7 remain pending at the approved human checkpoint. There were no deferred minor review findings.

Implementation rulings: keep the preview in an isolated worktree and profile namespace; use the existing dressed course for this package; preserve baseline and responsive tuning candidates; mark tuning as normalized because rewards/upgrades are disabled; add the missing scene AudioListener; use supported steering glyphs in hints; and use a compiler response file to support long Windows worktree paths. The Windows build wrapper must run in the current PowerShell environment: a nested legacy PowerShell run failed during post-build hashing, while the direct invocation succeeded.
