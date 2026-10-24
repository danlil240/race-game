# Combat Gadgets Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement task by task, with one final fresh review.

**Goal:** Add homing missiles, armed mines, EMP pulses, and six original HUD icons.
**Architecture:** Extend the deterministic gadget world, catalog, snapshots and pooled presenter already shared by solo and LAN. Keep one-slot inventory and existing controls.
**Tech Stack:** C#, Unity 6, NUnit, original vector artwork rasterized to PNG for UI Toolkit.
**Spec:** ../specs/2026-10-24-combat-gadgets-design.md (approved).

## Global Constraints

- Host owns gameplay; clients render snapshots. All live effects are bounded and deeply copied.
- Shield charges block incoming attacks. Offensive gadgets share slowdown/immunity rules.
- Existing gadget button, seeded pickup bag, and English/Hebrew UI remain the integration points.
- No new dependencies, course changes, or damage system.

## Review Focus

- Missile and racer motion crossing between ticks must hit correctly.
- Finished/recovering racers and different-height routes cannot be attacked.
- Mine and missile hits consume the effect even when shielded; immunity prevents repeated slowdown.
- Protocol validation rejects malformed moving effects; fingerprint binds all new authored parameters.
- Rematches and pool reuse cannot display stale weapons or icons.

### Task 1: Deterministic weapons and content contract

**Files:** Core/Racing/GadgetDefinition.cs, GadgetState.cs, GadgetRules.cs, new GadgetRules.Weapons.cs; Core/Networking/NetworkSettings.cs; Tests/EditMode/CombatGadgetTests.cs.
**Interfaces:** Add heading to GadgetRacerInput; new GadgetProjectileState and GadgetPulseState lists on GadgetWorldState; new mine fields on GadgetPatchState; kinds homing-missile, mine, emp-pulse. Produce missile/mine/EMP events and shared hit application.
- [x] Write tests first using authored definitions and Step; prove missile forward targeting/travel, armed one-shot mines, radial EMP, shield/immunity/exclusions and bounds fail before implementation.
- [x] Implement definitions and validation, deep copy/wire validation, bounded authoritative weapons. Use 45 m/s missile, 70 m acquisition, 120-degree cone, 90-tick life; 18-tick mine arm, 360-tick life, radius 2.5; EMP radius 12. Hit multipliers/durations: .4/27, .35/36, .6/45.
- [x] Run Tools/Verify.ps1 -Mode CoreOffline until green, retaining RED/GREEN evidence under Logs.

### Task 2: Runtime and authored integration

**Files:** DrivingSession.cs, NetworkRace.cs, AiDriver.cs, ContentFingerprint.cs, Runtime/Content/GadgetCatalogAsset.cs, Generated/Driving/gadget-catalog-01.asset, Editor/DrivingPrototypeSetup.cs, NetworkWireCodec.cs; related adapter tests.
**Interfaces:** Feed heading from committed motor; AI context gains rivalAhead/rivalNearby; copy authored weapon fields through asset conversion and fingerprint; snapshots serialize new state with version fence.
- [x] Add tests for new catalog parameters, AI opportunities, copy/fingerprint changes, and round-trip/malformed network state.
- [x] Wire all producers/consumers; keep regeneration equivalent to checked-in catalog.
- [x] Audit event consumers for victim feedback, attribution, and rear-view mine support.
- [x] Run CoreOffline checks.
- [ ] Run Unity EditMode checks after the local Editor license is restored.

### Task 3: Presentation and original icons

**Files:** Runtime/Presentation/GadgetPresenter.cs and a weapons partial, new Runtime/Presentation/GadgetIcons.cs; FoundationShell.Player.cs and .Driving.cs; Core/Localization/ShellText.cs; Resources/GadgetIcons/*.png plus original SVG sources; PlayMode/CombatGadgetPresentationPlayTests.cs.
**Interfaces:** GadgetIcons.TextureFor(kind) returns cached Resources texture; HUD uses texture background on its existing icon label. Pooled weapon views mirror authoritative world; impact events feed existing audio.
- [x] Add lifecycle/HUD tests; create six original 256px icons from editable SVG sources with a reusable rendering script and provenance note.
- [x] Render missiles with exhaust and fins, mines with arming signal, EMP as expanding blue ring; retire views at despawn/rematch/exit.
- [x] Inspect the icon contact sheet and compile the PlayMode test sources.
- [ ] Execute Unity PlayMode tests, inspect in-game evidence, and build a Windows development player after the local Editor license is restored.
- [x] Perform fresh whole-change review; correct material issues; record verification and remaining limits. Leave changes reviewable in the current feature checkout.

## Execution result

Implementation and fresh review complete. CoreOffline: 886 passed; full Unity source compilation: zero errors/warnings. Unity test execution and player rebuild remain blocked by Editor license exit 198, so the runtime verification steps above are completed only as source checks and documented pending checks, not claimed as passing. See ../../qa/combat-gadgets-2026-10-24/README.md.
