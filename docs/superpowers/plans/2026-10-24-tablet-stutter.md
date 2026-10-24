# Tablet stutter fix implementation plan

Quality restoration after user feedback:
- [x] Remove the rejected0.4 render-scale override; preserve authored0.75 and earlier CPU/interpolation fixes.
- [x] Verify original-quality diagnostic: Coastal p9533.048ms, zero frames above50ms in32.640s, mirror on.
- [x] Reproduce and fix scenery reactivation placement-buffer allocation without changing graphics.27 focused checks pass; independent review found no significant issue.
- [x] Build and install ordinary Grand Tour replacement, SHA2563254977BC905CCEDBB523F8B5E7883C79C307D4C256AEFE29051B443F5321D97. Restored-quality race screenshot inspected.
- [x] Finish replacement APK timing/error-log/hash checks and leave paused for the user. Final Coastal:0 intervals above50ms in32.526s; p9549.109ms still uneven. Error log empty and installed APK hash matched. No resolution tradeoff remains; see Logs/tablet-stutter-fix-20261024/quality-restoration.md.

> For agentic workers: execute inline under the executing-plans and test-driven-development skills.

Goal: reduce recurring approximately 65 ms Android frames and remove stripped-collider gadget errors; deploy and measure on the connected SM-T220.
Architecture: remove unnecessary observation copies at their call sites while preserving full snapshots for actual validation, and retain dynamically created collider types through Unity's linker configuration.
Spec: user requested the fix after Logs/tablet-stutter-20261024/report.md documented the reproducer.

- [x] Reproduce allocation overhead with real Unity GC.Alloc counters. Both new PlayMode tests failed before changes.
- [x] Expose a scalar current-screen read and skip unconsumed tick snapshots without a validation bridge.
- [x] Preserve SphereCollider and CapsuleCollider in the Android IL2CPP player.
- [x] Run allocation, input, screen, font, gadget, mirror, validation, world and camera regressions (128/128); CoreOffline 864/864.
- [x] Independent reviews completed. Review-found skipped-recovery regression reproduced RED and corrected; 128/128 GREEN.
- [x] Rebuild Grand Tour, install, repeat the same SurfaceFlinger race timing sample, and check runtime logs.
- [x] Report before/after measurements and remaining limitations.

Constraints: work in the current checkout to include the owner's ongoing UI work in the deployed APK; preserve all existing changes. No gameplay/race-rule redesign. Capture performance without detailed application tracing, which perturbed the baseline. No commit or external publication requested.
Additional verified causes: per-frame font readiness retried34 glyph additions despite bind warmup; now coverage-only. Vehicle interpolation used a fixed2m teleport threshold across multiple simulation ticks and reused an older-than-one-tick prior sample. A skipped render tick at45m/s snapped to z13 instead of interpolating to z11.875. Fixed the final-tick window and constrained expanded snap tolerance to plausible travel. Review additionally caught a recovery flag clearing during skipped ticks: expectedz16 but got14.5; velocity-aware tolerance now preserves that snap. Physical backend interpolation is unchanged.
Further measured optimization: reused CompleteStep-owned snapshots in NetworkRace publication and SoloRaceSession.Step; input sampling now gets a narrow independent motor/input view rather than an entire checkpoint ledger. Full public observation snapshots remain independent. Core864/864 and selected Unity134/134 passed, with independent read-only review. Recurring unobserved input.set command snapshots were additionally removed: RED showed30 extra allocations, latest Unity87/87 GREEN. Other commands preserve immediate locale/font presentation refresh.

Device experiments rejected a31fps request and a60fps request combined with Android's30fps limiter at the original0.75 scale. Lowering only internal scale to0.4 with the original30fps request still failed the pacing gate. A normal Android GE8320 exception combining internal0.4 scale with a60fps ceiling then passed verified diagnostic Coastal and Canyon race samples (p9533.072/32.884ms, one interval above50ms in each32s sample). HUD remains full resolution; mirror stays active; other GPU profiles retain their existing settings. Temporary diagnostics were removed before the final ordinary build, and all Android GameManager overrides reset.

Final installed APK EC8B5FF94F37FF8943DEB87E1E8E0A0230BDC8EC3CF0A0812E75676F7A592634 matched the built artifact. Final Coastal:0 intervals above50ms in32.509s (baseline13 in21.280s); p9549.038ms means the strict even-pacing gate still fails on that course. Final Canyon:1 interval above50ms in32.509s and p9532.884ms, passes. Runtime error log empty after touch/control checks. Report honestly records the remaining49ms spacing and the lower 3D resolution; no universal stable-FPS claim. App left paused and user data preserved. Full evidence: Logs/tablet-stutter-fix-20261024/report.md.
