# TopK Finder

Generate a **comparison strategy** for finding the top-k elements from n numbers when the only allowed operation is sorting at most m elements at a time.

## Problem

Given n numbers, find the largest k numbers (order among them doesn't matter). The only allowed operation is a `Sort(list)` function that:
- Accepts at most m numbers
- Returns them in sorted order

Instead of printing one concrete run on random data, the program now prints the **decision tree itself**:

- which indices to compare first
- what the possible sort results are
- which group should be compared next under each result
- and only one representative for branches that are symmetric up to relabeling

## Algorithm

At a high level, the solver builds a decision tree over partial-order states:

- each sort contributes a full local order and transitive implications,
- elements proven unable to enter top-k are eliminated,
- recursive minimax search chooses comparisons that minimize worst-case depth,
- equivalent branches are merged by symmetry-aware canonicalization.

Detailed search design and optimality boundaries are in `docs/core-algorithm.md`.
Pattern/equivalence rendering rules are in `docs/output-rendering.md`.

## Usage

The program has three entry points that share the same input validation
(`1 <= n <= 64`, `2 <= m <= n`, `1 <= k <= n`).

### Documentation map

- `docs/core-algorithm.md`: search/minimax design, lower bounds, exact/greedy
  stage semantics, and optimality boundaries.
- `docs/output-rendering.md`: branch/pattern rendering and equivalence folding
  rules.
- `docs/test-strategy.md`: regression/perf testing strategy and guardrails.
- `docs/counter-guardrail-budgets.md`: deterministic counter guardrail profiles,
  shape anchors, and ratchet protocol.
- `docs/ui-explorer.md`: WinForms explorer behavior, stage timeline UI, and
  cancellation/progress semantics.

### Test lane quickstart

- Fast lane (default local/PR validation):
  - `dotnet test .\TopKFinder.Tests\TopKFinder.Tests.csproj --filter "Category!=Slow"`
- Slow parity lane (opt-in before merge or dedicated audits):
  - `dotnet test .\TopKFinder.Tests\TopKFinder.Tests.csproj --filter "Category=Slow"`
- Deterministic counter guardrail lane (machine-independent budgets):
  - `pwsh .\scripts\run-counter-guardrails.ps1 -Profile fast-default`
- Perf baseline lane (manual regression gate):
  - `pwsh .\scripts\run-perf-gate.ps1 -BaselineCsvPath .\scripts\benchmark-greedy-stage1-baseline.csv -RegressionTolerancePercent 5 -EnforceBaseline`
  - dry-run: `pwsh .\scripts\run-perf-gate.ps1 -BaselineCsvPath .\scripts\benchmark-greedy-stage1-baseline.csv -ListOnly`

GitHub Actions lanes:

- `required-pr-tests` (automatic PR gate, fast-only test filter)
- `manual-slow-parity` (manual slow parity matrix)
- `manual-counter-guardrails` (manual deterministic counter-cap guardrails)
- `manual-perf-gate` (manual perf baseline gate)

Counter guardrail profiles (`manual-counter-guardrails` input `profile`):

- `fast-default`: default-path deterministic caps (daily default)
- `iterative-frontier`: iterative-deepening frontier caps
- `compact`: compact-phase deterministic caps
- `full-counter-suite`: all baseline-cap suites + key iterative checks

Lane selection decision table is documented in `docs/test-strategy.md`.

### Pipeline architecture (post-refactor)

The refactor track is complete. The runtime now follows a stable layered boundary:

- Search layer (`StrategyBuilder`): owns search semantics, feasibility/optimality
  reasoning, and stage solving.
- Public orchestration layer (`PublicPipelineOrchestrator`): owns shared stage
  orchestration for CLI/UI and stage-emission contracts.
- Display layer (`DisplayRenderEngine` + UI/text renderers): owns rendering,
  folding, and presentation-only behavior.

Exact search-model projection is now canonical via
`StrategyBuilder.BuildDisplayTreeAndExpandedSearch()` and `BuildSearchTree()`.

### Builder API naming

The in-process builder API uses a consistent naming split between
single-stage construction and multi-stage orchestration:

- `BuildGreedyFeasibleStage`, `BuildStepProofStage`, `BuildProofTightenStage`,
  `BuildEdgeCompactStage`: build one atomic stage result or plan.
- `RunGreedyPipeline`: greedy-mode orchestrator that emits
  `greedy-feasible`, zero or more `proof-tighten≤N`, then a final
  `greedy-edge-compact@S` stage.
- `RunExactPipeline`: exact-mode orchestrator that emits `step-proof`, then
  `exact-edge-compact@S`.
- `StageResult` / `StageOutcome`: the unified stage callback model used by the
  pipelines. Terminal non-tightening stages report `StageOutcome.Completed`.

### Command-line arguments

```bash
dotnet run -- <n> <m> <k> [--mode exact|greedy] [--stage <n>]
```

- Prints the strategy tree for `n`, `m`, `k` to **stdout**.
- The CLI always runs in two stages: **step** (find the worst-case step count),
  then **edge** (minimize displayed branch edges at that fixed step count).
- Two modes select how the step stage is found:
  - **exact** (default): a proven-optimal exact solve for step, then compact
    refinement for edge.
  - **greedy**: a fast greedy feasible strategy for step, then a budget-bounded
    compact pass for edge. Fast and interruptible, but not proven optimal.
- `--stage <n>` stops after stage `n` (1-based):
  - exact: `1` = step-proof, `2` = exact-edge-compact@S
  - greedy: `1` = greedy-feasible, `2+` continues along tightening progression
- If the edge stage does not reduce output states, only the step strategy is
  printed; otherwise both step and edge strategies are printed.
- Search progress is written to **stderr**, so you can redirect the result on its
  own: `dotnet run -- 12 3 3 > tree.txt`.
- `--help` (or `-h`) prints usage and exits.

### Piped stdin

With no arguments but redirected input, the program reads `n`, `m`, `k` from
stdin, one value per line:

```bash
printf "10\n4\n3\n" | dotnet run
```

### Desktop UI

Running with no arguments and no redirected input opens the WinForms explorer
(see `docs/ui-explorer.md`).

### Example

```
$ dotnet run -- 5 3 2

==================== summary ====================
n=5, m=3, k=2
worst-case steps = 3
elapsed = 36.9 ms
phases: step = 18 ms, edge = 0 ms, build = 18 ms

==================== diagnostics ====================
searched states = 4
...

==================== legend ====================
#i                            item i (1-based labels; may be relabeled in references)
S{id} [step x/y] sort(...)    decision state: do this sort at step x of at most y
...

==================== strategy ====================
S1 [step 1/3] sort(#1, #2, #3)
  #1 > #2 > #3  (×6 = 3!)
    pattern: {#1, #2, #3}
    - (#3)
    possible (#1, #2, #4, #5)
    S2 [step 2/3] sort(#1, #4, #5)
      ...
```

The output is grouped into four banner-delimited sections: a **summary**
(parameters and the worst-case number of sorts), **diagnostics** (search
telemetry), a **legend** explaining the notation, and the **strategy** tree
itself. Each branch renders as a header line — the revealed order followed by
`(×N = formula)` when it stands for `N` symmetric orderings — then indented
child lines: a `pattern:` shape line and one line per non-empty effect
(`+ / - / fixed / possible`). This layout is shared by the CLI text output and
the desktop UI tree so both read identically. The CLI runs the step stage first
and then the edge refinement automatically: if the edge stage improves
output-state count, both trees are printed; otherwise only the step tree is
printed.

### Desktop UI details

The WinForms explorer shares the same stage model as the CLI (`step-proof` /
`greedy-feasible` / `proof-tighten≤N` / `exact-edge-compact@S` / `greedy-edge-compact@S`) and shows live
progress, search counters, and per-stage timing. For full UI behavior and
diagnostic panes, see `docs/ui-explorer.md`.

## Requirements

- .NET 8.0 SDK

## Greedy Mode: Min-Step Optimization

### Overview
Greedy mode is feasibility-first: it finds a valid plan quickly, tightens the
step bound when possible, then runs one edge-compaction pass at the final step.
It is fast and interruptible (Ctrl+C in CLI, Stop in GUI), and cancellation
keeps the best plan found so far.

- `greedy-feasible`: build an initial feasible upper bound `U`.
- `proof-tighten≤N`: probe lower ceilings (`U-1`, `U-2`, ...) with feasibility-only compact search.
- `greedy-edge-compact@S`: one min-edge pass at the final feasible step `S`.

For algorithmic details, proofs, and edge-case semantics, see
`docs/core-algorithm.md` (sections on greedy pipeline and compact stage).

Regression coverage lives in `TopKFinder.Tests/GreedyFeasibleStageTests.cs`,
`TopKFinder.Tests/GreedyPipelineTests.cs`, `TopKFinder.Tests/GreedyTightenTests.cs`,
and `TopKFinder.Tests/ProofTightenTests.cs`.
