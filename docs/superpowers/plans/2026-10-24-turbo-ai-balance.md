# Turbo and AI Balance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a stronger turbo pickup and AI that uses full boosts with correctly ordered difficulty.
**Architecture:** Repair decisions in AiDriver without changing motor or network state. Keep the pickup's generated data and authoring defaults aligned.
**Tech Stack:** Unity 6000.3.26f1, C#, NUnit, .NET 10.
**Spec:** docs/superpowers/specs/2026-10-24-turbo-ai-balance-design.md

## Global Constraints

- Preserve shared vehicle rules, upgrades, passive recharge, controls and network state format.
- Pickup: 150 ticks, 1.75 acceleration multiplier, 1.50 speed cap multiplier.
- Default/LAN margin remains unchanged; braking and steering still suppress boost.
- Work in the current non-main checkout to retain the user's existing generated assets and Unity cache; preserve unrelated changes.

## Review Focus

- Partial charge must not cancel a running boost.
- A running boost must still release for braking or a sharp turn.
- Hard must corner faster than Medium, which must corner faster than Easy, on both backends.
- A boosted AI must complete real long routes without recovery loops.
- The stronger pickup must work by itself and alongside vehicle boost, then expire normally.

### Task 1: Repair AI decisions

**Files:** AiDriver.cs; Tests/EditMode/NetworkAiTests.cs; Editor/LongRaceQualification.cs.
**Interfaces:** AiDriver.Sample(DrivingSessionState, CourseDefinition, AiGadgetContext) returns ordinary DriverInput; CorneringMargin() supplies the shared feasibility calculations.

- [x] Add regression cases for boost continuation, unsafe release, and difficulty braking order on arcade/physical backends; run them and observe the failures.
- [x] Divide the base cornering margin by brakeMarginScale; allow an already active boost to remain held below the start threshold.
- [x] Run `dotnet test Tools/CoreTests/FamilyRacing.CoreTests.csproj --no-restore` and require all tests to pass.
- [x] Measure full route completion, time, recoveries and boost use for all three difficulty presets with actual Unity collision.

### Task 2: Strengthen and deliver the pickup

**Files:** Editor/DrivingPrototypeSetup.cs; Generated/Driving/gadget-catalog-01.asset; Tests/PlayMode/LongRaceWorldPlayTests.cs; balance QA report.
**Interfaces:** GadgetCatalogAsset.ToCatalog, GadgetRules.ModifiersFor and ArcadeMotor.Step use the existing multiplier contract.

- [x] Add an authored-asset integration test showing useful sustained acceleration, independent meter, stacking with vehicle boost and correct expiry; observe its old-data failure.
- [x] Set the authoring source and shipping asset to the approved values.
- [x] Run relevant Unity tests, build development and ordinary Grand Tour Windows players, and record results plus practical limits.
- [x] Review only this task's changed files and resolve substantive findings before delivery.
