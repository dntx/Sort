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

## Mainline B: Test Layering Governance (completed)

Goal:
- Make Fast/Slow/Nightly boundaries explicit and enforceable in CI.

TODO:
- Keep required PR gate on fast tests only. (done)
- Keep slow parity matrix in opt-in/manual or nightly lanes. (manual lane done: `manual-slow-parity`)
- Add a short contributor section describing when to run fast vs slow vs perf gates. (done in `docs/test-strategy.md`)

Mainline B exit status:
- required PR gate stays on fast suite.
- slow parity is available as manual lane (`manual-slow-parity`).
- contributor run matrix is documented in `docs/test-strategy.md` and `README.md`.
- policy decision for nightly slow parity remains optional and can be revisited independently.

## Mainline C: Performance Baseline Governance (foundation completed; ongoing ratchet maintenance)

Goal:
- Shift performance regression control to deterministic counters and shape-specific baselines.

TODO:
- Define and ratchet key-shape counter budgets (`OutcomesConstructed`, `SearchedStates`, `CandidateGroupsEnumerated`). (in progress)
- Keep wall-clock checks as smoke diagnostics, not the primary regression gate. (in progress)
- Add a repeatable baseline refresh protocol for `scripts/benchmark-greedy-stage1.ps1`. (documented)

Current C progress:
- added `manual-counter-guardrails` workflow to run deterministic counter-cap suites on demand.
- updated contributor quickstart to distinguish deterministic counter guardrails vs wall-clock perf baseline lane.
- `manual-counter-guardrails` now supports profile-driven execution (`fast-default`, `iterative-frontier`, `compact`, `full-counter-suite`) for shape-specific guardrail governance.
- added shared runner script (`scripts/run-counter-guardrails.ps1`) and budget manifest (`docs/counter-guardrail-budgets.md`) to reduce filter drift and standardize ratchet practice.
- added shared perf gate runner (`scripts/run-perf-gate.ps1`) and switched manual perf workflow to use it.
- added lane decision table in `docs/test-strategy.md` to standardize when to run counter lanes vs wall-clock perf lane.
- added perf runner dry-run support (`-ListOnly`) and workflow passthrough (`manual-perf-gate` input `list_only`) for parameter-chain validation.
- added machine-readable summary output (`-SummaryJsonPath`) for counter/perf runners and artifact upload in manual workflows.
- fixed `full-counter-suite` selector matching so deterministic full audit runs the intended `StaysWithinBaseline` family.
- added matched-test preflight counting in `run-counter-guardrails.ps1`; `full-counter-suite` now enforces a minimum matched-test threshold to catch selector drift early.
- extended matched-test threshold enforcement to `fast-default`, `iterative-frontier`, and `compact` profiles to catch partial selector drift before execution.
- added `scripts/collect-default-counter-snapshot.ps1` to automate deterministic default-path counter snapshot collection and ratchet delta reporting.
- ratcheted down a batch of default-path counter caps (searched/outcomes/candidate/duplicate) using the collected snapshot as the source of truth.
- added `scripts/collect-compact-counter-snapshot.ps1` to automate compact-path counter snapshot collection (including compact-specific work counters).
- ratcheted down compact-path counter caps (`Compact_WorkCounters*`, `Compact_Searched*`, `Compact_Outcomes*`, `Compact_Duplicate*`) from measured snapshot values.
- added `scripts/collect-iterative-counter-snapshot.ps1` to automate iterative-frontier counter snapshot collection with structural-anchor verification.
- ratcheted down iterative-frontier counter caps in `Default_IterativeDeepeningBaselineRemainsStable` from measured snapshot values.
- added `scripts/collect-all-counter-snapshots.ps1` as a unified entrypoint that emits one combined summary artifact across default/compact/iterative snapshots.
- used the combined summary to ratchet one residual default outcomes cap (`9,4,3`) that still had positive headroom.
- added `scripts/collect-all-counter-snapshots.ps1` to run default/compact/iterative collectors and emit one combined summary artifact for review.
- enhanced `manual-counter-guardrails` workflow with optional unified snapshot collection/upload so reviewers can inspect summary and per-snapshot rows in one dispatch run.
- enhanced `manual-perf-gate` workflow with optional benchmark-row CSV artifact upload so wall-clock smoke runs are auditable beyond pass/fail summary.
- enhanced `manual-perf-gate` workflow with `baseline_csv_path` input and explicit job timeout to make baseline switching safer and long-run behavior bounded.
- enhanced perf runner/workflow with explicit build configuration (`Release`/`Debug`) input so benchmark lanes can switch configuration without script edits.
- enhanced `manual-counter-guardrails` workflow with explicit `build_configuration` input so deterministic test and snapshot lanes can run Debug/Release without editing workflow YAML.
- enhanced `manual-counter-guardrails` workflow with `list_only` support to validate selector/preflight wiring without executing tests or snapshot collection.
- enhanced `manual-counter-guardrails` workflow to always publish matched-test list artifacts for profile selector auditability.
- added `scripts/run-counter-full-audit.ps1`, `manual-counter-full-audit` workflow, and a repository matched-tests baseline so full deterministic audits now produce one combined review bundle.
- enhanced `manual-counter-full-audit` to publish summary content directly in Actions and optionally update a PR comment.
- added `counter-baseline-drift-review` so matched-tests baseline changes require an explicit PR-body explanation.
- expanded compact deterministic counter coverage to include the previously snapshot-only heavy rows `(12,3,4)` and `(10,2,4)` for searched/outcomes/duplicate monitoring.

Mainline C foundation status:
- deterministic governance lanes now exist for focused counter checks, full bundled audits, and wall-clock smoke diagnostics.
- matched-test selector drift is now both reviewable and enforceable.
- future Mainline C work is primarily ratchet maintenance and targeted automation polish, not core governance bootstrapping.
