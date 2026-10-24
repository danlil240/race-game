# M6 full content — Task 9 slice-A harness report

Date: 2026-10-15. Branch: `feat/m6-full-content`. Plan:
`docs/superpowers/plans/2026-10-15-full-content.md`, task 9.
Structural contract: `docs/FULL_CONTENT_RUNBOOK.md`,
`Assets/FamilyRacing/Editor/FullContentValidation.cs`.

**Status: slice A complete; production evidence NOT produced.** This
slice delivers the external scenario harness and its mutation suite
only. No authored production inventory exists under
`Generated/FullContent` (Tasks 5–8), so no real full-content run was
executed or claimed — every scope would report `partial` with named
gaps by design.

## Delivered

- `Tools/FullContentScenario.py` — `--launch <exe>` or
  `--connections <conn.json> ...`, required `--scope
  catalog|campaign|modes|lan`, required new `--evidence` dir, optional
  `--fixture` / `--players` / `--host-address`. Reuses the
  `GameplayLoopScenario` client/capture/LAN helpers unchanged; no
  `AgentScenario.py` change was needed (`validate_schema7_worlds`
  already exists).
- `Tools/TestFullContentScenario.py` — 32 tests: contract shape,
  reconcile-level mutants and fixture-driven catalog/campaign scopes.
- Docs: `docs/FULL_CONTENT_RUNBOOK.md` §Scenario evidence,
  `docs/CONTENT_MATRIX.md` evidence convention,
  `docs/FEATURE_COVERAGE.md` tooling section + gap entry.

## Evidence contract

Per-event record (`events.ndjson`): world/course/class/type, build +
content identities, command ids and hold counts, race/result
identities, completion + per-bit objective proofs, rewards (coin
receipt; finale livery/vehicle), measured duration, capture validity,
attribution, named human-only gaps. Reconciliation (`coverage.json`)
compares the synthesized 72+6 contract against observed rows per
world/course/class/type/objective/reward — never the reverse.

## Mutation suite (every mutant prevented from passing)

Removed world, removed event, duplicated cell, fixture substitution,
dropped stream records, AI-attributed finish, omitted objective proof,
77/78 completions, forged proof for an unearned bit, shared rival
proof, unexpected event row, malformed row — each fails or gaps
honestly; `partial`/`notVerified` are never counted as success.

## Deferred (slice B / later gates)

Real 78-event traversal, authored per-course route observations
(replacing the graybox pursuit follower), the Quick Race/Time Trial
12-combination production run, the physical LAN matrix, pacing
measurement, family playtests, and ordinary-build acceptance.

## Concerns

- Solo driving rides the shared graybox `SoloDriver` until authored
  course routes exist — recorded on every record via the run's
  trace/gap fields, not hidden.
- The harness is large because the honesty contract is: per-cell
  evidence, independent expectation, reward-level facts and capture
  validation all live in one reconciler.
