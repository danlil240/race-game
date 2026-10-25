# Full Campaign Stable Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Finish the three-world campaign chosen by Daniel, then build and qualify signed Windows/Android packages for a stable GitHub release.

**Architecture:** Author production assets through deterministic Unity editor tools, retaining FullContentValidation as the contract. Bind six playable courses and the ordered campaign to a dedicated scene. Reuse permanent signing identity and existing release/package/evidence tooling. Work on codex/full-campaign-release in the existing Unity checkout to retain its imported assets and offline caches.

**Tech Stack:** Unity 6000.3.26f1, C#, PowerShell, Python, Android SDK/ADB.

**Spec:** Existing docs/FULL_CONTENT_RUNBOOK.md, Release/acceptance-matrix.json; user chose the full three-world campaign on 2026-10-25.

## Global Constraints
- Exact IDs and counts in FullContentValidation: three worlds, six courses, eight vehicles, 78 events, six rivals, bilingual names and final voice prompts.
- Preserve save format, production certificate, offline play and ordinary-package exclusion of development listeners.
- Human acceptance and physical evidence must be measured; never fabricate passing records or weaken gates.
- Keep credentials outside Git and command output. Preserve the existing preview release.

## Review Focus
- Course switches must change visible geometry and collision together, including after reconnect.
- Progression must unlock the next world without softlocks and grant each reward once.
- Reauthoring must preserve asset GUIDs and avoid duplicate identities.
- Every event needs attainable objectives and safe route/pickup/shortcut geometry.
- Missing voice, signing, or physical evidence must remain a named blocker.

### Task 1: Author six world courses
- [ ] Add ProductionCourseAuthoring.cs with Create(), PrefabPath and TrackPath(int index).
- [ ] Author distinct coastal, canyon and snow courses, with two routes per world, collision, four spawns, recovery, pickups and two genuine optional shortcuts each.
- [ ] Add adapter coverage for production IDs, topology, route geometry and idempotent authoring. Run tests through Unity.

### Task 2: Author campaign and roster
- [ ] Add ProductionCampaignAuthoring.cs exposing Create() and CatalogPath. Consume tracks from ProductionCourseAuthoring.TrackPath.
- [ ] Author eight tuned vehicles/models, 72 regular events and six finales, ordered worlds, rivals/liveries, economy/bounties and difficulties.
- [ ] Verify generic conversion, full inventory and progression/objective reachability with meaningful tests.

### Task 3: Integrate scene and voice
- [ ] Add production scene authoring and bind course geometry, all catalog assets and release identity into the shipping scene.
- [ ] Supply genuine English/Hebrew prompt audio if installed providers/credentials permit; otherwise record the exact missing resource.
- [ ] Verify content gate and EditMode/PlayMode and fix actual failures.

### Task 4: Qualify and release
- [ ] Verify existing permanent certificate without exposing credentials; preserve key custody/backup requirement.
- [ ] Build ordinary production Windows and release-signed Android; create hashes and manifest from a clean commit.
- [ ] Test on the connected SM-T220 without deleting existing saves or uninstalling an incompatible app.
- [ ] Run evidence reconciliation; publish stable only when required evidence is complete. Report any remaining human-only requirements precisely.
