# Refactor Roadmap (Post-PR344)

This note tracks the medium-sized roadmap so we do not lose direction between small slices.

## Mainline A: Search-First Boundary Closure (completed)

Goal:
- Make exact search entrypoints independent from display materialization.
- Keep display as projection/render layer over solver semantics.

Current progress:
- `BuildSearchTree()` now goes through a dedicated solver-state exact core path, no longer through `BuildExactSearchProjection()`.
- Search tree materialization no longer depends on `DisplayTree` metadata transport.
- Exact entrypoints now share one explicit search-materialization path over exact caches, with a clear `prepareSession` split between standalone search entry and layered exact projection.
- Exact projection now builds display/search artifacts inside one active solver session (single token/session scope), instead of nested entrypoint hand-offs.
- Exact search entry APIs now use explicit standalone/current-session helpers (no bool mode switches), reducing misuse risk and clarifying boundary semantics.

Mainline A exit status:
- Exact projection and search-only exact entry are both solver-session based and no longer depend on bridge-style nested entrypoints.
- Shared exact session/bootstrap and shared expansion tracking are in place.
- Core algorithm doc and roadmap reflect the current boundary contract.

Next strategic focus:
- Mainline B (test layering governance)
- Mainline C (performance baseline governance)

## Mainline B: Test Layering Governance (TODO)

Goal:
- Make Fast/Slow/Nightly boundaries explicit and enforceable in CI.

TODO:
- Keep required PR gate on fast tests only.
- Keep slow parity matrix in opt-in/manual or nightly lanes.
- Add a short contributor section describing when to run fast vs slow vs perf gates.

## Mainline C: Performance Baseline Governance (TODO)

Goal:
- Shift performance regression control to deterministic counters and shape-specific baselines.

TODO:
- Define and ratchet key-shape counter budgets (`OutcomesConstructed`, `SearchedStates`, `CandidateGroupsEnumerated`).
- Keep wall-clock checks as smoke diagnostics, not the primary regression gate.
- Add a repeatable baseline refresh protocol for `scripts/benchmark-greedy-stage1.ps1`.
