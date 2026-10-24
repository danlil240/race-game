# Production Updates Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task, inline.

**Goal:** Automate production packaging and suggest newer GitHub releases at game startup.

**Architecture:** Pure Core release policy, Unity coroutine transport, existing shell menu notice, portable CI plus dedicated Windows production runner.

**Tech Stack:** C# / Unity 6000.3.26f1, NUnit, PowerShell, Python, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-10-24-production-updates-design.md`

## Global Constraints

- Public release source: `danlil240/race-game`; no player credentials.
- Six-second request timeout; one attempt per component lifetime; failures do not interrupt play.
- Ordinary Windows x64 and signed Android packages; existing production gates retained.
- Preserve current unrelated working changes. Owner authorized judgment and continuous execution.

## Review Focus

- Numeric comparison must handle 1.10.0 versus 1.9.0.
- Drafts, prereleases, bad versions, absent platform assets must not prompt.
- Response URLs must not redirect players to an unrelated host/repository.
- Network errors and destruction must dispose requests and leave the game usable.
- CI must reject mismatched tags and partial/rehearsal package sets before drafting a release.

### Task 1: Version policy and startup notice

- [x] Add NUnit cases for newer/equal/older and malformed versions, draft/prerelease, platform packages and trusted release URL.
- [x] Run CoreOffline and observe missing policy failure.
- [x] Add `Core/Foundation/ReleaseUpdatePolicy.cs`, consumed automatically by both Core harnesses; implement `FindUpdate(current, release, platform)` returning a validated offer or null.
- [x] Add `Runtime/ReleaseUpdateChecker.cs` coroutine and `FoundationShell.Updates.cs` menu notice; connect through shell Bind/Refresh/rebuild and add EN/HE catalog strings.
- [x] Add Unity tests for response decoding, held-control preservation, notice dismissal/localization; run CoreOffline and compilation checks.
- [ ] Execute Unity adapter/PlayMode tests: blocked by the local editor license (exit 198).

### Task 2: Production workflow and operating instructions

- [x] Add portable PR CI for Core + tooling, and tag-triggered dedicated Windows verification/package workflow.
- [x] Add release validation tooling and fixture tests for tag/config equality and complete production manifests/assets, including rejection of rehearsal output.
- [x] Validate workflow syntax and scripts; run existing packaging tests and new fixture tests.
- [x] Document production runner prerequisites, environment secrets, version/tag progression, draft publication, client update behavior and troubleshooting.
- [x] Review only this task's diff, run final checks, and record results below.

## Execution ledger

- Baseline: CoreOffline 886 passed, 0 failed, 0 skipped.
- Ruling: work in place because the active gameplay edits are part of the intended current game; a clean HEAD worktree would omit them. Do not stage or revert them.
- Added update policy: 901 CoreOffline tests passed, 0 failed (15 additional cases).
- Full Python tooling suite: 344 tests passed in 371.253 seconds. The subsequently added protocol-consistency regression also passed in the final six-test production subset.
- New production validation subset: 6 tests passed. Protocol manifest mismatch reproduced before fixing packaging constant 3 to match runtime 4.
- PowerShell fixtures: verification 28/28, signed release/packaging 82/82, Android offline 47/47, profile backup/restore 70/70.
- GUI verification regression: compiling the Unity fixture as `winexe` reproduced false success (exit 0 instead of 29). Explicit `Start-Process -Wait -PassThru -WindowStyle Hidden` fixed it; real Unity license failure now correctly returns 198.
- Compilation of Core/Runtime/Editor/Tests against installed Unity assemblies passed with 0 warnings and 0 errors. This is a compile check, not execution of Unity APIs.
- Workflow lint: checksum-verified actionlint 1.7.12 passed. `git diff --check` passed. Actions pinned to verified release commit SHAs.
- Independent read-only review: fixed late offers rebuilding active racing controls; added always-upload packaging diagnostics. No other actionable review findings.
- GitHub: created `production` environment and custom tag-only deployment policy `v*`; verified zero runners registered and zero environment secrets configured.
- Delivery follow-up (2026-10-24): the owner requested all changes be committed by subject and pushed. Combat gadgets, Unity verification tooling, startup updates, and production CI are separate commits on `fix/pcqa-session-02`. No production tag, binary or release is published by this push. Production activation requires integration into main, registering the dedicated Windows runner, activating Unity for its account, provisioning offline build caches and adding signing secrets. See `docs/PRODUCTION.md`.
