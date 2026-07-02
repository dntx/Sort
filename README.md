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

The implementation uses a **transitivity-aware elimination strategy**.

When a sort returns:

```text
a > b > c
```

the program records all implied relations:

- `a > b`
- `a > c`
- `b > c`

and then keeps propagating them transitively. For example, if we already know `x > a`, then after sorting `[a, b, c]` we also learn `x > b` and `x > c` without paying for another sort.

An element is eliminated as soon as there are at least `k` elements proven to be larger than it, because it can no longer belong to the top-k set.

### Group selection heuristic

Each round, the program chooses up to `m` active candidates and prefers:

1. **Unseen elements**, so every sort adds new information.
2. **Strong leaders**, which already dominate many others.
3. **Almost eliminated elements**, which need just one more proven larger element to be removed.

This usually beats the earlier batch/tournament approach because it reuses previously learned order relations instead of restarting from scratch.

To print the strategy tree, the program symbolically enumerates all sort outcomes consistent with the currently known partial order, then recursively prints the next comparison for each branch.
Symmetric branches are merged by canonicalizing the comparison state, so equivalent cases are not repeated.

## Usage

The program has three entry points that share the same input validation
(`1 <= n <= 64`, `2 <= m <= n`, `1 <= k <= n`).

### Command-line arguments

```bash
dotnet run -- <n> <m> <k> [--mode exact|greedy]
```

- Prints the strategy tree for `n`, `m`, `k` to **stdout**.
- The CLI always runs in two stages: **step** (find the worst-case step count),
  then **edge** (minimize displayed branch edges at that fixed step count).
- Two modes select how the step stage is found:
  - **exact** (default): a proven-optimal exact solve for step, then compact
    refinement for edge.
  - **greedy**: a fast greedy feasible strategy for step, then a budget-bounded
    compact pass for edge. Fast and interruptible, but not proven optimal.
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
(see below).

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
  #1 > #2 > #3: [- (#3), possible (#1, #2, #4, #5)]
    equivalent forms: 6 = 3!
    pattern: permute {#1, #2, #3}
    S2 [step 2/3] sort(#1, #4, #5)
      ...
```

The output is grouped into four banner-delimited sections: a **summary**
(parameters and the worst-case number of sorts), **diagnostics** (search
telemetry), a **legend** explaining the notation, and the **strategy** tree
itself. The CLI runs the step stage first and then the edge refinement
automatically: if the edge stage improves output-state count, both trees are
printed; otherwise only the step tree is printed.

### Desktop UI details

Running without redirected input opens the WinForms explorer. A mode dropdown
selects **exact (proven)** or **greedy (fast)**, matching the CLI `--mode exact / greedy`.
During a run it shows live:

- searched / pending / output state counts
- the current best root worst-case bound as incumbents improve
- lower-bound pruning and cache-hit counters
- two-stage timing (step, then edge), each counting from zero

You can press **Stop** at any time to cancel a running search. For parameter
combinations that are known to be expensive (large `n` with a mid-range sort
size), the UI shows a confirmation dialog before starting so a long search is
never launched by accident.

After the run finishes, the details pane also includes a root-incumbent timeline
so you can see when the search first found `x` steps, then improved to a smaller
bound.

## Requirements

- .NET 8.0 SDK

## New Features (Stage 1: Greedy Mode Optimization)

### Overview
The greedy mode has been optimized to directly minimize worst-case steps (network depth) instead of edge count as a proxy.

**Key Changes**:
- Phase 2 now uses `SolveCompactSelectionGreedy` with a min-step objective
- Candidates are sorted by `children.Count` as a cheap proxy for tree quality
- Tightening loops (Phase 3+) iteratively refine the solution

**Performance**: Average 2.53x speedup on benchmark cases while preserving solution quality (100% identical results on test suite).

### New CLI Options

#### `--test-stage1`
Validates the Stage 1 min-step greedy implementation on a standard test suite (6 test cases from 8,3,3 to 20,5,5).

```bash
dotnet run -- --test-stage1
```

Output shows steps, edges, execution time, and overall summary for each test case.

#### `--compare-stage1`
Side-by-side comparison of original greedy mode vs. Stage 1 implementation.

```bash
dotnet run -- --compare-stage1
```

Verifies that:
- Solution quality (steps/edges) remains identical
- Performance improvements are measured
- No regressions are introduced
