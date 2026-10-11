# Foundation Service Report (Part A)

Pure application service for the foundation shell per `docs/tasks/foundation-shell.md`.
Parts B (Unity shell) and C (validation transport/sink) are not started.
All checks are offline; no network, restore, editor launch, commits, or vault changes.

## Delivered

| File | Purpose |
| --- | --- |
| `Assets/FamilyRacing/Core/Automation/GameCommand.cs` | Immutable command; copies the parameter map at construction. |
| `Assets/FamilyRacing/Core/Automation/CommandResult.cs` | Serializable terminal result (`applied`/`rejected`/`failed`), `Copy()` for cache-safe returns. |
| `Assets/FamilyRacing/Core/Automation/CommandDescriptor.cs` | Action/parameter schema descriptors with `available`/`reason`. |
| `Assets/FamilyRacing/Core/Foundation/FoundationState.cs` | Deeply-copied serializable DTO: `FoundationState`, `InputState`, `SlotState`, `FeatureCoverage`. |
| `Assets/FamilyRacing/Core/Foundation/FoundationService.cs` | Command validation/dispatch, identity cache (10k, reject-not-evict, `Lookup`), slot/screen/draft/input state, discovery, observation tick. |
| `Assets/FamilyRacing/Tests/EditMode/FoundationTests.cs` | 89 new test cases covering the required cases; real temp-file `ProfileStore`. |
| `Tools/CoreTests/FamilyRacing.CoreTests.csproj` | Added `Core/Foundation/*.cs` and `Core/Automation/*.cs` globs. |

Harness note: `Tools/OfflineTestRunner/Run-OfflineTests.ps1` already globs
`Assets/FamilyRacing/Core/**/*.cs` recursively, so no runner change was needed;
the csproj is the second harness and now includes both new subfolders.

## Evidence

| Run | Result | Log |
| --- | --- | --- |
| RED vs `NotImplementedException` stubs | 133 passed, 89 failed | `Logs/foundation-tests-RED.log` |
| GREEN (full suite, Unity 6.3 bundled NUnit `net40`) | **222 passed, 0 failed** | `Logs/foundation-tests-GREEN.log` |
| netstandard 2.1 reference compile (csc `/nostdlib /langversion:9` vs `6000.3.26f1\Editor\Data\NetStandard\ref\2.1.0\netstandard.dll`) | exit 0, no diagnostics | `Logs/netstandard-check-6.3/compile.log` |
| Diff artifact (new files vs empty, csproj vs snapshot) | 7 file diffs | `Logs/part-a.diff` (snapshot `Logs/part-a-snapshot/`) |

## Semantics implemented (Part A decisions)

- Protocol 1, exact `runId`, participant `local`; rejects before mutation with
  codes `invalidCommand`/`unsupportedProtocol`/`wrongRun`/`wrongParticipant`/
  `unknownAction`/`missingParameter`/`unknownParameter`/`invalidParameter`/
  `unavailable`/`identityConflict`/`commandCapacity`/`saveFailed`/`internalError`.
  `duplicateParameter` is unreachable at Core level (`IReadOnlyDictionary`
  cannot carry duplicate names); wire-level duplicate detection belongs to the
  Part C JSON parser.
- Identity cache keyed on `commandId`; canonical envelope covers protocol,
  run, participant, action and order-independent parameters. All terminal
  results are cached; repeats return a copy with `duplicate=true` and the
  original tick. `Lookup` returns a stored copy unchanged (`duplicate=false`),
  `null` for unknown/empty ids, and never consumes capacity. Stored and
  returned results are distinct objects so caller mutation cannot corrupt the
  cache. `Execute`/`Lookup` always return copies.
- `input.set` holds steering/drift/boost for `durationTicks` ticks
  (set tick counts as one); gadget/recover/changeCamera/pause are one-tick
  pulses cleared on the next `AdvanceTick`. `pause` pulse toggles
  `service.paused`; `changeCamera` pulse toggles the draft camera only for a
  writable (Loaded) selection — a recovered profile's draft is never edited.
  `pause.toggle` toggles paused and clears input; both are sandbox-only while
  `input.clear`/`input.set` semantics follow the spec (set works paused).
- `AdvanceTick` increments `tick` even while paused and is not a race clock.
- `persistenceStatus` values: `""` (none), `saved`, `recovered`, `saveFailed`.
  `profile.select` sets `recovered` for recovered slots; create/save set
  `saved`; failed save restores prior draft/language and sets `saveFailed`.
- `profile.select` leaves `screen` unchanged (spec assigns screen only to
  `profile.create`); it copies saved/draft and language, clears input and
  pending edits.
- Discovery returns all ten actions in fixed order with per-action parameter
  schemas; `reason` tokens: `requiresSelectedProfile`, `readOnlyProfile`,
  `sandboxOnly`, `noMissingSlot`, `noSelectableSlot`, `useSettingsUpdate`.
- Draft and saved profiles are independent deep copies at every boundary;
  snapshots copy slots, profiles, input, and coverage so caller mutation
  cannot reach service state. No paths or raw file contents appear in state.
- `featureCoverage` marks tutorial, drivingSimulation, campaign,
  rewardsUpgradesCosmetics, lan, and content as `notImplemented`.

## Notes and open items

- Orphaned valid backups load as `RecoveredFromBackup` (existing
  `ProfileStore` behavior), so creation over such a slot is refused by the
  `Missing`-only rule rather than a `Blocked` status; the test asserts the
  actual contract.
- Wire-integer parsing requires optional `-` followed by digits only (rejects
  `+1`, ` 1`, `1.5`, empty); leading-zero forms like `01` parse normally.
  Decimal parsing uses invariant culture, rejects NaN/Infinity, whitespace,
  and out-of-range values at the action layer.
- `input.set` inside the sandbox still applies for a recovered (read-only)
  profile — the sandbox is input exploration, not settings mutation; only the
  camera-draft edit is gated on writability.
- Part C will need the external-agent scenario runner, transport limits and
  protocol framing — owned by the lead, not implemented here.
