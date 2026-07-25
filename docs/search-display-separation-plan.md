# Search / Display Separation Plan

## Status

- State: Architecture decisions resolved; Batches 0-5 complete; Batch 6 ready after Batch 5 merge
- Baseline: `main` after PR #446 (`85259ed`)
- Related work: PR #442 is intentionally paused and is not a dependency of this plan
- Execution rule: implement one batch at a time and complete its focused validation before starting the next
- Source of truth: when this document and chat history differ, update and follow this document

## 1. Goal

Separate strategy solving from display materialization across the complete exact and greedy pipelines while preserving correctness, stage semantics, cancellation behavior, and the CLI/GUI user experience.

The target flow is:

```text
exact / greedy / compact solver
    -> immutable solved-strategy snapshot
        -> search projection / validation
        -> display materialization
            -> CLI text rendering
            -> GUI tree and overview
```

The solver result must be useful without constructing a display tree. Display materialization must consume an immutable result and must not read mutable solver-session caches.

## 2. Scope

This plan covers all production stages:

| Mode | Stage | Current role |
| --- | --- | --- |
| exact | `step-proof` | solve the exact minimum worst-case step strategy |
| exact | `exact-edge-compact@S` | select a secondary compact strategy at the exact step count |
| greedy | `greedy-feasible` | construct an initial feasible policy and upper bound `U` |
| greedy | `greedy-tighten` | locally improve the feasible policy before proof tightening |
| greedy | `proof-tighten<=N` | find a feasible strategy under a lower ceiling or prove/classify failure |
| greedy | `greedy-edge-compact@S` | perform the final secondary compact pass at the chosen step count |

It also covers:

- stage emission and incumbent selection,
- proof/lower-bound propagation,
- search and display metrics,
- cancellation and anytime guarantees,
- CLI final/intermediate output,
- GUI stage timeline, tree browsing, placeholders, and pause behavior.

It does not initially change:

- minimax, greedy, or compact selection semantics,
- canonical search-state identity,
- display folding/reference semantics,
- the public visual/text shape of already materialized plans.

## 3. Current Architecture And Coupling

### 3.1 Current stage boundary

`StageResult` currently carries an optional `StrategyPlan`. `StrategyPlan` simultaneously acts as:

1. a witness that a strategy exists,
2. the value used to compare stage improvements (`MaxStep`, then displayed branch edges),
3. the fully materialized display tree consumed by CLI and GUI.

This makes display materialization part of stage completion even when later search only needs a step bound or policy assignment.

### 3.2 Existing search-side artifacts

| Stage | Existing search-side artifact | Current limitation |
| --- | --- | --- |
| exact step | `_bestGroupPatternCache` and exact depth caches | mutable builder-session state; no immutable stage snapshot |
| greedy feasible | `GreedyPolicySolution` | already explicit and immutable-like, but stage output is still `StrategyPlan` |
| greedy tighten | override and anchor dictionaries | partial delta over a base policy; tied to mutable session state |
| proof tighten | compact pattern assignment and budget memos | overwritten/reset between probes; success immediately materializes display |
| edge compact | compact pattern assignment and cost/depth memos | stage comparison still depends on materialized display edges |

### 3.3 Display coupling below the stage boundary

`BuildState` is the effective search-to-display adapter. It:

- replays a selected group pattern under concrete labels,
- constructs display branch lines and summaries,
- assigns display state IDs,
- folds equivalent branches,
- emits Reference leaves,
- computes display-derived counts.

The current search projection is not yet fully pure because parts of transition planning still reuse display branch-line planning. A true separation therefore requires both:

1. an immutable solved-strategy boundary before `BuildState`, and
2. a later split between raw comparison transitions and display-only branch shaping.

## 4. Proposed Target Model

### 4.1 Central artifact

The proposed central artifact is an immutable solved-strategy snapshot, provisionally named `SolvedStrategy`.

```csharp
sealed class SolvedStrategy
{
    public ProblemShape Problem { get; }
    public SearchStateKey RootKey { get; }
    public IReadOnlyDictionary<SearchStateKey, SolvedStrategyNode> Nodes { get; }
    public StrategyScore Score { get; }
    public BoundEvidence Bounds { get; }
    public StageProvenance Provenance { get; }
    public SearchStatistics SearchStatistics { get; }
}
```

Decision D1 fixes this common concrete artifact name as `SolvedStrategy`.

A node should contain only search semantics:

```csharp
sealed class SolvedStrategyNode
{
    public SolvedStrategyNodeKind Kind { get; }
    public SolvedGroupPattern? SelectedGroup { get; }
    public IReadOnlyList<SearchStateKey> DistinctSuccessors { get; }
    public int RemainingDepth { get; }
}
```

`SolvedGroupPattern` is the immutable snapshot equivalent of the mutable-cache-oriented `BestGroupPattern`.
It owns a copied canonical pattern and read-only color signature without changing allocations on the existing
solver hot path.

`SolvedStrategyNodeKind.FinalChoice` represents the search-semantic one-step leaf where the active item count
is at most `M` and one final comparison resolves the requested top set. It has remaining depth 1, no selected
group pattern, and no successors. This keeps solved depth exact without importing display-only
`FinalChoiceSummary` payload.

It must not contain:

- `OrderText`,
- `EquivalentOrderSummary`,
- display state IDs,
- Reference targets,
- display orbit/projection lines,
- `TreeNode` or renderer payloads.

### 4.2 Scores and proof evidence

Search correctness and display presentation need separate values.

Proposed search-side score:

```text
StrategyScore = (WorstCaseSteps, SearchEdgeCost?)
```

Proposed display-side score:

```text
PresentationScore = (MaxStep, DisplayBranchEdges)
```

`BoundEvidence` should hold at least:

- proven root lower bound `L`,
- feasible upper bound `U` when a strategy exists,
- whether the result is proven optimal,
- whether candidate enumeration was capped,
- the attempted ceiling for a proof-tighten result.

A later proof must update evidence on the solved strategy or stage record, not require rebuilding a `StrategyPlan` merely to call `WithRootProvenLowerBound`.

### 4.3 Projections

A solved strategy can be consumed by two independent adapters:

```text
SolvedStrategy
  -> SearchProjection       (canonical strategy DAG / diagnostics / validation)
  -> DisplayMaterializer    (concrete labels / folding / References / summaries)
```

Both projections must preserve strategy semantics. For every materialized result:

```text
SolvedStrategy.Score.WorstCaseSteps == StrategyPlan.MaxStep
```

Materialization must fail fast if:

- a selected pattern cannot be replayed,
- the materialized canonical successor set differs from the snapshot,
- a display Reference cycle appears,
- display depth differs from solved-strategy depth.

## 5. Stage Mapping

### 5.1 Exact `step-proof`

Freeze the root-reachable exact pattern assignment and depths into a `SolvedStrategy`. The exact solver cache may remain mutable internally, but the emitted stage snapshot must not reference it.

During migration, exact step-proof may continue to materialize immediately for GUI compatibility. Pipeline control must gradually switch from `StrategyPlan.MaxStep` to `SolvedStrategy.Score.WorstCaseSteps`.

### 5.2 Exact `exact-edge-compact@S`

Freeze the root-reachable compact assignment at exact step count `S` before any later reset or reuse.

The compact solver's secondary objective and the displayed edge count are not identical because display Reference de-duplication and branch folding are applied later. The stage must therefore distinguish:

- search-side compact completion/improvement,
- display-side edge improvement after materialization.

### 5.3 Greedy `greedy-feasible`

Use the existing `GreedyPolicySolution` as the first implementation seed. Generalize or adapt it to the common solved-strategy contract without weakening its current invariants.

The policy depth is the exact feasible upper bound for this fixed strategy. Materialization remains guarded by:

```text
policy.WorstCaseSteps == plan.MaxStep
```

This is the preferred first production slice because the solve/materialize phases already exist.

### 5.4 Greedy `greedy-tighten`

The emitted result must be a complete solved strategy, not a live base policy plus mutable override dictionaries.

At stage completion, resolve the effective selection for every root-reachable state:

```text
override when present, otherwise base greedy policy
```

Then freeze the resulting assignment and depths. Anchor states and concrete group lists must not leak into the final snapshot unless they are proven necessary for canonical pattern replay.

### 5.5 Greedy `proof-tighten<=N`

A successful probe should produce an immutable compact assignment snapshot and its realized search depth before the compact session is reset or the next budget is attempted.

A failed probe produces no solved strategy:

- `ProvenInfeasible`: full enumeration proved no strategy under the ceiling.
- `Incomplete`: candidate capping prevents a proof.

The tightening driver must choose the next budget from solved-strategy depth, not from a display plan.

### 5.6 Greedy `greedy-edge-compact@S`

Freeze the final compact assignment at `S`. If the final compact attempt is capped and cannot produce a complete assignment, retain the latest complete solved-strategy incumbent.

Display materialization then decides whether the candidate reduces visible branch edges. A completed solver stage and a visible display improvement are separate facts.

## 6. Pipeline Contract And User Experience

### 6.1 Stage result

Migration shape, subject to D2:

```csharp
sealed class StageResult
{
    public StageDescriptor Stage { get; }
    public StageOutcome Outcome { get; }
    public SolvedStrategy? Solution { get; }
    public StrategyPlan? MaterializedPlan { get; }
    public StageTimings Timings { get; }
}
```

During migration, `MaterializedPlan` remains available for existing consumers. Final architecture should allow it to be absent until requested.

Every announced stage must still complete exactly once with a total outcome. Search/display separation must not weaken the current strong emission contract.

### 6.2 Timings and statistics

Replace ambiguous single elapsed values with explicit components:

- solve elapsed,
- snapshot/freeze elapsed if material,
- display materialization elapsed,
- total stage elapsed.

Split statistics into:

- solver/search counters available when the snapshot is emitted,
- display counters available only after materialization.

Existing labels can remain stable during compatibility phases, but the meaning of elapsed and output counters must not silently change.

### 6.3 CLI target behavior

Target behavior:

- emit progression lines from stage metadata and solved-strategy scores,
- do not materialize every successful intermediate proof-tighten stage,
- materialize only the final presentation incumbent,
- on Ctrl+C, materialize the best complete snapshot found so far and print it as interrupted,
- if Ctrl+C is pressed again during materialization, cancel it and suppress strategy-result output,
- preserve a flag or stage-limited mode that explicitly requests an intermediate tree.

Potential later option:

```text
--emit-all-stage-trees
```

This is not required for the first migration.

### 6.4 GUI target behavior

Target behavior:

- add a stage timeline entry as soon as solving completes,
- run all materialization in independent presentation tasks that never block search,
- materialize the currently selected successful snapshot, including a newly selected presentation incumbent,
- cancel obsolete requests on selection changes and suppress stale completions by request generation,
- cache a bounded number of materialized plans,
- retain an explicit `preparing tree...` state while display materialization runs.

Exact mode should preserve the current experience in which the `step-proof` tree becomes browsable while edge compact continues.

For `pause each stage`, materialize the stage before showing the modal so the paused stage remains inspectable.

On Stop:

- cancel solver work promptly,
- preserve the best complete solved-strategy snapshot,
- if solver work is already stopped, a repeated phase-aware Stop cancels active presentation work,
- never claim optimality unless proof evidence closes the squeeze.

## 7. Execution Plan

Each batch must update this document with status, changed files, behavior impact, validation results, and any decision changes.

### Batch 0: Decisions and baselines

Status: Complete, 2026-07-25

- Resolved decisions D1-D10.
- Recorded current exact and greedy stage behavior in the existing pipeline, parity, and regression tests.
- Selected the baseline gates below.
- Do not change production contracts.

Baseline gates:

- full functional suite: `tests/TopKFinder.Tests` (583 tests passed on the PR #441 baseline),
- exact stage protocol: `ExactPipelineTests.RunExactPipeline_EmitsCanonicalStages_AndReturnsLastStagePlan`,
- greedy stage protocol: `GreedyPipelineTests.GreedyPipeline_EmitsProofTightenAndEdgeCompactStageNames` and
    `ProofTighten_EveryStartedStageIsCompletedExactlyOnce`,
- solved/materialized depth: all eight cases in
    `GreedyFeasibleStageTests.GreedyFeasiblePolicy_IsSolvedBeforeEquivalentPlanIsMaterialized`,
- successor parity: all cases in `DisplaySearchParityTests` for exact, greedy feasible, and compact plans,
- solver cancellation: all six cases in `StopLatencyTests.StopLatency_SoftCancel_StaysWithinFiveSeconds`,
- deterministic work counters: `Default_SearchedStateCountStaysWithinBaseline`,
    `Default_OutcomesConstructedStaysWithinBaseline`, `Default_CandidateGroupsEnumeratedStaysWithinBaseline`,
    `Default_IterativeDeepeningBaselineRemainsStable`, and `Compact_WorkCountersStayWithinBaseline`,
- wall-clock smoke: `StrategyPerformanceTests.N28M3K6_GreedyFeasibleCompletesWithinBudget` and
    `ProofTightenPerfGateTests.GreedyProofTighten_FirstProbe_20_2_6_CompletesWithinGate`.

New gates required by later batches:

- Batch 7 must add CLI tests for search cancellation followed by successful final materialization and for a
    second Ctrl+C cancelling materialization without strategy output,
- Batch 8 must add GUI tests for non-blocking search, selection replacement, stale-result suppression,
    presentation cancellation, atomic publication, and bounded cache behavior.

Acceptance:

- all blocking decisions are recorded: complete,
- baseline tests/cases are named: complete,
- PR #442 dependency decision is explicit: complete; it remains paused and independent.

### Batch 1: Common immutable solution model

Status: Complete, 2026-07-25

- Introduced the common solved-strategy, immutable group/node, score, evidence, and provenance types in
    `src/TopKFinder/SolvedStrategyModel.cs`.
- Added snapshot-copy support for array-backed canonical keys in `src/TopKFinder/StateKeyTypes.cs`.
- Added display-independent structural validation for root depth, strictly decreasing decision edges,
    root-reachability, and bound consistency.
- Added immutable ownership and validation tests in
    `tests/TopKFinder.Tests/SolvedStrategyModelTests.cs`.
- Existing pipeline outputs are unchanged.

Acceptance:

- model contains no display payload: complete,
- snapshot collections cannot be mutated through builder session state: complete,
- unit tests cover immutability and depth validation: complete; focused result `4/4` passed.

### Batch 2: Greedy-feasible dual output

Status: Complete, 2026-07-25

- Added a lossless `GreedyPolicySolution` to `SolvedStrategy` adapter, including explicit one-step final-choice
    states that were previously implicit in policy depth.
- Added internal `GreedyFeasibleStageArtifacts(Solution, Plan)` dual output while preserving the public
    `ExecuteGreedyFeasibleStage()` plan-only API.
- Changed the same-run compact budget source from `plan.MaxStep` to
    `solution.Score.WorstCaseSteps` after enforcing their equality invariant.
- Kept CLI, GUI, and public pipeline behavior unchanged.
- Expanded the existing eight-case regression matrix to compare problem shape, score, bounds, provenance,
    canonical group patterns, successors, final-choice states, and materialized depth.

Acceptance:

- all current greedy-feasible tests pass: focused class result `50/50` passed,
- solved depth equals materialized `MaxStep` across the regression matrix: `8/8` passed,
- later greedy stages can obtain `U` from the solution: complete via `_feasibleRootBudget`,
- full functional suite: `587/587` passed in Release,
- heavy greedy-feasible performance sentinel: `N28M3K6_GreedyFeasibleCompletesWithinBudget` passed in Release.

### Batch 3: Exact step-proof snapshot

Status: Complete, 2026-07-25

- Added `ExactStepProofStageArtifacts(Solution, Plan)` while preserving the public plan-only step-proof API.
- Frozen the root-reachable exact pattern, successor, and remaining-depth assignment immediately after phase 1.
- Routed exact display and search projection through the immutable solution; standalone search projection no longer
    selects groups from live exact caches.
- Kept eager display materialization and all public CLI/UI pipeline behavior unchanged.

Acceptance:

- exact strategy values and display outputs remain unchanged: display/search parity and orchestration `50/50` passed,
- standalone search projection no longer depends on mutable exact caches after snapshot creation: focused cache-clear
    regression `3/3` passed,
- snapshot depth equals plan `MaxStep`: enforced at runtime and covered by the focused regression,
- full functional suite: `590/590` passed in Release,
- exact `StrategyPerformanceTests` smoke gates passed in Release.

### Batch 4: Greedy-tighten complete snapshot

Status: Complete, 2026-07-25

- Added `GreedyTightenStageArtifacts` while preserving the public `StrategyPlan` return contract.
- Resolved the constructive base policy plus committed overrides into a complete root-reachable
    `SolvedStrategy` before display materialization.
- Preserved the historical display back-edge fallback in a pre-freeze policy-resolution traversal: unsafe overrides
    are removed before the final immutable policy is declared complete, without producing a display tree.
- Frozen canonical group patterns, distinct successor keys, exact remaining depths, feasible-bound evidence,
    and greedy-tighten provenance without display payload.
- Final materialization now consumes only the frozen solution; it no longer reads or mutates live override/anchor
    dictionaries.
- Kept the root-probe gate, candidate ordering, commit behavior, round cap, and public pipeline behavior unchanged.
- Retained display back-edge protection during final materialization as a read-only fail-fast invariant for the
    already-resolved greedy-tighten snapshot.
- Added a cache-independence regression that clears live override/anchor dictionaries after freezing and
    rematerializes the same depth and branch structure from the snapshot.

Acceptance:

- current greedy-tighten soundness and back-edge tests pass: complete; focused snapshot/back-edge result `6/6`,
- larger-policy replay validation passes: complete as part of the full functional suite,
- materialization consumes only the frozen solution: complete; the focused regression clears live override/anchor
    dictionaries before rematerialization,
- full Release functional suite: `593/593` passed,
- workspace build and Release test-project build pass with zero compiler errors; `git diff --check` passes,
- local strategy-matrix smoke was attempted, but the terminal runner ended during a long silent interval without a
    result artifact; PR CI remains the performance gate.

### Batch 5: Compact snapshot freezing

Status: Complete, 2026-07-25

- Added a shared compact freezer that captures root-reachable group patterns, canonical successor keys,
    exact remaining depths, score/evidence, provenance, and immutable statistics before display materialization.
- Successful proof-tighten probes freeze their assignments before any later probe reset; the tightening loop now
    chooses the next budget from `SolvedStrategy.Score.WorstCaseSteps` while preserving the existing `StageResult`
    compatibility API.
- Exact edge-compact and successful greedy edge-compact stages materialize only from their frozen solutions.
- Proven-infeasible, incomplete, and capped Phase-B incumbent-fallback outcomes carry no compact solution; partial
    assignments are never represented as solved strategies.
- Removed the obsolete production compact path that materialized directly from live compact caches.
- Kept overshoot as an invariant violation, now checked against the frozen solution score.

Acceptance:

- old stage snapshots remain stable after later probes: complete; focused proof/exact-edge/greedy-edge replay tests
    clear or overwrite compact caches and rematerialize the original depth and branch structure,
- proof-tighten next-budget decisions use solution depth: complete,
- overshoot remains an invariant violation: complete,
- focused compact snapshot/no-fake-solution tests: `8/8` passed; capped Phase-B fallback test passed,
- Release build: zero warnings and zero errors,
- functional suite: `600/601` passed in the parallel VS Code runner; the sole failure was the existing 10-second
    `14,2,4` performance canary under suite load, which passed `3/3` in isolated Release processes.

### Batch 6: Pipeline control moves to solutions

Status: Not started

- Extend `StageResult` compatibly with solved strategy and split timings.
- Move incumbent step comparisons and squeeze updates off `StrategyPlan`.
- Preserve existing eager materialization and callbacks.

Acceptance:

- exact and greedy stage sequences are unchanged,
- every stage start has exactly one completion,
- cancellation still leaves a usable incumbent,
- GUI/CLI output remains equivalent.

### Batch 7: CLI deferred materialization

Status: Not started

- Stop materializing intermediate stages not printed by default.
- Materialize the final or interrupted incumbent.
- Preserve stage-limit behavior and progression text.

Acceptance:

- default CLI output remains semantically equivalent,
- search cancellation materializes and prints the best complete strategy unless presentation is cancelled,
- a subsequent Ctrl+C during materialization suppresses strategy-result output,
- materialization count is reduced on multi-stage greedy runs.

### Batch 8: GUI controlled materialization

Status: Not started

- Introduce explicit solve-complete and display-materializing states.
- Materialize the selected snapshot in an independent cancellable presentation task.
- Suppress stale completions and cache a bounded number of validated plans.
- Preserve browse-during-compact and pause-each-stage semantics.

Acceptance:

- no blank primary experience,
- no incoherent stage/tree labels,
- Stop remains prompt,
- historical stages cannot observe mutated solver state.

### Batch 9: Raw transition semantic split

Status: Not started

- Extract raw outcome/successor semantics shared by search and display.
- Remove search projection's dependency on display branch-line planning.
- Keep display orbit/folding/summary logic in the display adapter.

Acceptance:

- search projection contains no display summary or branch-line policy,
- search/display successor parity tests pass,
- existing display output remains stable unless an explicitly approved change is recorded.

### Batch 10: Remove compatibility path and close documentation

Status: Not started

- Remove obsolete eager-plan-only APIs and mutable-cache projection paths.
- Finalize statistics/timing labels.
- Update architecture, core algorithm, output rendering, test strategy, UI, and README documentation.

Acceptance:

- all production stages use immutable solved-strategy snapshots,
- display materialization is an adapter and never a solver transport,
- full tests and selected performance/cancellation gates pass.

## 8. Decisions To Resolve

Decisions are marked `Blocking` when implementation should not pass the named batch without an answer.

### D1. Common artifact name and shape

Status: Resolved, 2026-07-25

Decision: use one concrete immutable `SolvedStrategy` type for every successful stage.

Rationale and constraints:

- materialization, search projection, validation, and pipeline control should depend on one semantic contract,
- exact/greedy/compact differences belong in `StageProvenance` and `BoundEvidence`,
- the common node shape remains selected canonical group + distinct canonical successors + remaining depth,
- do not introduce an inheritance hierarchy preemptively,
- if a solver needs stage-specific transient data, keep it in private solve context and freeze only the common
    root-reachable strategy semantics into `SolvedStrategy`.

Options:

- A. `SolvedStrategy`: one common immutable graph/assignment type for all successful stages.
- B. `StrategySolution`: same concept with a more general name.
- C. Common interface with stage-specific implementations (`ExactSolution`, `GreedyPolicySolution`, `CompactSolution`).

Selected: A.

### D2. `StageResult` migration strategy

Status: Resolved, 2026-07-25

Decision: extend the existing `StageResult` incrementally with `Solution` and optional `MaterializedPlan`.

Migration and validity rules:

- a successful solved stage must have a non-null `Solution`,
- `MaterializedPlan` may be null when display materialization has not been requested or completed,
- a successful materialized stage must have both `Solution` and `MaterializedPlan`,
- a proof, cancellation, or failure result with no complete incumbent has neither artifact,
- retain the existing `Plan` API only as a temporary compatibility bridge to `MaterializedPlan`,
- new or migrated code must depend on `Solution` for pipeline control and request `MaterializedPlan` only for
    presentation,
- remove the compatibility `Plan` API after solver, orchestrator, CLI, GUI, and tests have migrated; do not
    maintain two permanent stage-result contracts.

Options:

- A. Extend current `StageResult` with `Solution` and optional `MaterializedPlan`.
- B. Introduce a new `SolvedStageResult`; adapt it to legacy `StageResult` temporarily.
- C. Replace `StageResult` in one breaking migration.

Selected: A.

### D3. Meaning of edge improvement

Status: Resolved, 2026-07-25

Decision: search-side compact cost determines whether an edge-compact result is an improvement and replaces
the incumbent, even when the materialized display branch-edge count does not improve.

Reporting rules:

- preserve search-side compact cost and display branch-edge count as separate named metrics,
- stage outcome and incumbent replacement use the search-side compact comparison,
- CLI, GUI, diagnostics, and tests must not describe a search-cost improvement as a reduction in display
    edges unless the display metric also decreased,
- materialization still computes and reports display edges so any divergence remains visible,
- depth feasibility remains mandatory; an edge-cost improvement cannot replace an incumbent at an invalid or
    worse-than-required depth.

Question: after separation, which metric determines whether edge compact is considered an improvement?

Options:

- A. User-facing improvement remains `(WorstCaseSteps, DisplayBranchEdges)`; search edge cost is diagnostic/selection input only.
- B. Search edge cost determines stage improvement even when displayed edges do not improve.
- C. Report both: solver compact improvement and display improvement as separate statuses.

Selected: B.

### D4. Snapshot contents and memory policy

Status: Resolved, 2026-07-25

Decision: freeze only the root-reachable selected policy graph in each `SolvedStrategy`.

Ownership and lifetime rules:

- `SolvedStrategy` owns an immutable copy of the selected nodes reachable from its root,
- `SolvedStrategy` must not reference mutable solver caches or the active solver session,
- the active `SolverSession` continues to own full mutable caches and may reuse them across adjacent search
    stages in the same pipeline run,
- later cache mutation, reset, or replacement must not change an earlier stage snapshot,
- restoring full solver caches from a historical snapshot, across pipeline runs, or across processes is not a
    supported contract,
- consider a persistent immutable base plus overlays only if measurements show root-reachable snapshot copying
    to be a material cost.

Options:

- A. Freeze all solver cache entries.
- B. Freeze only the root-reachable selected policy graph.
- C. Store a persistent overlay referencing a shared immutable base.

Selected: B.

### D5. Successor representation

Status: Resolved, 2026-07-25

Decision: each `SolvedStrategyNode` stores only its distinct canonical successor keys.

Validation and extension rules:

- raw outcomes, outcome multiplicity, orbit grouping, and display branch lines are not part of the common
    solved-strategy contract,
- the materializer recomputes concrete outcomes and display grouping from the selected group and canonical
    state,
- materialization must verify that the distinct recomputed canonical successor set exactly matches the frozen
    successor set; missing or additional successors are invariant violations,
- successor ordering in the snapshot is deterministic but has no display-order semantics,
- add semantic multiplicity only if compact cost calculation or validation is proven to require it; do not add
    it speculatively.

Question: should each node store only distinct canonical successor keys, or retain multiplicity/raw outcome mapping?

Options:

- A. Distinct canonical successors only; display recomputes concrete outcomes and verifies the set.
- B. Store raw outcome-to-successor mapping in the solution.
- C. Store distinct successors plus semantic multiplicity, but no display grouping.

Selected: A.

### D6. CLI materialization policy

Status: Resolved, 2026-07-25

Decision: by default, materialize only the final incumbent or the best complete incumbent preserved when search
is interrupted.

CLI rules:

- intermediate successful stages retain validated `SolvedStrategy` snapshots without constructing display
    plans,
- progress and stage summaries use search-side solution metadata and do not require materialization,
- normal completion materializes only the final incumbent,
- Ctrl+C during search stops solver work and then starts materializing the best complete incumbent with a
    separate cancellation token,
- an explicit diagnostic mode may request intermediate materialization later, but it is not part of the
    default execution path.

Options:

- A. Materialize only the final/interrupted incumbent by default.
- B. Materialize every improving stage but print only final.
- C. Preserve all current eager materialization.

Selected: A.

### D7. GUI materialization policy

Status: Resolved, 2026-07-25

Decision: use fully asynchronous, selection-driven materialization. Search publishes immutable
`SolvedStrategy` snapshots and never waits for GUI display construction.

GUI rules:

- a new presentation incumbent may become the selected history entry according to existing GUI selection
    behavior, but its materialization runs in an independent presentation task,
- selecting any successful history entry starts or reuses materialization for that snapshot,
- when selection changes, cancel materialization that is no longer needed unless its result is already cached,
- key every request by stable snapshot identity and a request generation; a stale completion must not replace
    the plan for the current selection,
- publish a `StrategyPlan` to controls atomically only after construction and invariant validation complete,
- cache a bounded number of completed validated plans; never cache or display a partial plan,
- materialization failure or cancellation affects presentation state only and does not invalidate the
    `SolvedStrategy` or stop search,
- search progress, stage history, and incumbent metadata remain usable while presentation is pending.

Options:

- A. Eagerly materialize every improving stage; no lazy history.
- B. Eagerly materialize the current presentation incumbent, lazily materialize other successful history entries when selected.
- C. Materialize only the final result.
- D. Fully asynchronous selection-driven materialization; search never waits for display work.

Selected: D (the discussed B2 variant).

### D8. Cancellation during display materialization

Status: Resolved, 2026-07-25

Decision: use a separately cancellable materialization phase. After Ctrl+C stops search, a subsequent Ctrl+C
during incumbent materialization cancels materialization and suppresses strategy-result output.

Cancellation and publication rules:

- solver cancellation and materialization cancellation use separate tokens and phase-aware Ctrl+C handling,
- Ctrl+C during normal final materialization has the same effect: cancel materialization and suppress the
    strategy result,
- never publish or render a partially materialized `StrategyPlan`,
- publish the materialized result atomically only after construction and invariant validation complete,
- after materialization cancellation, the CLI may print a concise cancellation status but must not print the
    materialized strategy tree or claim that a display result completed,
- the preserved `SolvedStrategy` remains valid internally even when its display materialization is cancelled,
- GUI search Stop cancels active solver work first; once solver work is stopped, a repeated phase-aware Stop
    cancels the active presentation task,
- changing GUI history selection may cancel the obsolete presentation task without affecting search,
- cancellation and stale-result checks must both guard GUI publication because cancellation can race with
    materialization completion.

Options:

- A. Solver cancellation also cancels display; an unmaterialized snapshot may remain unseen.
- B. Stop cancels solver work, then final incumbent materialization completes with a separate token.
- C. Ask the user whether to finish materialization after Stop.

Selected: B with phase-aware repeated Ctrl+C cancellation.

### D9. Timing semantics

Status: Resolved, 2026-07-25

Decision: record solve, materialization, and total elapsed separately, while preserving total elapsed as the
compatibility meaning of the existing `Elapsed` label.

Timing rules:

- solve elapsed covers solver execution and freezing the immutable solved-strategy snapshot,
- snapshot-freeze elapsed may also be exposed as a diagnostic sub-measurement when material,
- materialization elapsed covers constructing and validating `StrategyPlan` from `SolvedStrategy`,
- total elapsed covers the full stage work performed for that result and must not be derived by adding
    overlapping intervals,
- an unmaterialized successful stage has zero or absent materialization elapsed, according to the final timing
    value type chosen in Batch 6,
- legacy CLI, GUI, logs, and tests that read `Elapsed` continue to receive total elapsed during migration,
- new performance diagnostics should compare the named components rather than infer solve time from total.

Options:

- A. Keep one elapsed value including solve and materialization.
- B. Split solve, materialize, and total; preserve total as the compatibility label.
- C. Stage elapsed means solve only after migration.

Selected: B.

### D10. PR #442 relationship

Status: Resolved, 2026-07-25

Decision: keep PR #442 paused while implementing complete search/display separation. It is neither a
dependency nor part of the separation change set.

Re-evaluation rules:

- do not merge, close, rebase, or extend PR #442 as part of the separation batches,
- establish the post-separation materialization boundary and performance baseline first,
- after separation is complete, measure whether the planned-summary reuse still removes material work on the
    new architecture,
- continue investment only if the remaining benefit justifies rebasing or reimplementing the optimization
    against the new boundary,
- closing or superseding PR #442 remains a later explicit decision rather than an assumed outcome.

Options:

- A. Keep #442 paused and build the separation work independently; rebase/re-evaluate later.
- B. Merge #442 first because it reduces current eager materialization cost.
- C. Close #442 because the future materializer will make the optimization obsolete.

Selected: A.

## 9. Validation Strategy

### 9.1 Invariants

For every successful solution/materialization pair:

- solved depth equals materialized `MaxStep`,
- canonical successor sets match at every reachable non-terminal state,
- every selected group makes strict progress,
- the strategy graph is terminating,
- display References resolve acyclically,
- exact/proven stages retain their proof semantics,
- capped probes never claim infeasibility proof.

### 9.2 Stage contract tests

Cover exact and greedy sequences:

- exact: `step-proof -> exact-edge-compact@S`,
- greedy without GT,
- greedy with improving GT,
- successful proof-tighten followed by lower budget,
- proven infeasible terminal ceiling,
- candidate-cap incomplete terminal ceiling,
- final edge compact improvement and no-display-improvement cases.

Assert:

- one completion per announced stage,
- immutable earlier snapshots after later probes,
- incumbent and squeeze updates,
- no display plan required for solver control decisions.

### 9.3 CLI/GUI acceptance

CLI:

- normal exact and greedy final output,
- stage-limited output,
- Ctrl+C before and after first incumbent,
- progression labels and proof wording.

GUI:

- initial placeholder,
- exact browse-during-compact,
- greedy anytime progression,
- pause each stage,
- Stop during solve and materialization,
- no-improvement and no-solution notes,
- history selection, stale-result suppression, and the bounded materialization cache selected by D7.

### 9.4 Performance evidence

Track separately:

- solver time,
- snapshot freeze time and size,
- materialization time,
- number of materializations per run,
- final output equivalence,
- deterministic search counters.

Do not claim a wall-clock improvement from structural changes without a stable A/B measurement.

## 10. Batch Record Template

```text
Batch:
Status: Not started | In progress | Blocked | Done
Decision dependencies:
Changed files:
Behavior impact:
Validation commands:
Validation results:
Performance evidence:
Risks and follow-ups:
```

## 11. Immediate Next Step

After Batch 5 is reviewed and merged, implement Batch 6 as a pipeline-control slice:

1. extend `StageResult` compatibly with the solved strategy and split solve/freeze/materialization timings,
2. move incumbent comparisons and squeeze updates from `StrategyPlan` to solved-strategy score/evidence,
3. preserve eager compatibility materialization and existing callback ordering,
4. keep paused PR #442 untouched.
