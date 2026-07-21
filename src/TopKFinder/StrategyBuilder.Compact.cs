using System;
using System.Collections.Generic;

namespace TopKFinder;

partial class StrategyBuilder
{
    private const int DefaultCompactGreedyCandidateCap = 128;
    // EXPERIMENTAL (PoC): when enabled, phase 2 reads comparison groups from a
    // "compact" pattern cache produced by a secondary two-level DP. The DP keeps the
    // optimal worst-case step count (computed by phase 1) as the primary objective and,
    // among all equally-optimal groups, minimizes the total number of search-tree branch
    // edges (children.Count at each expanded node, summed recursively). This lets us
    // measure whether a global "prefer the fewest-edges equally-optimal solution" rule
    // shrinks the trees.
    // When true, the feasible-budget compact solve only proves feasibility at the threaded step
    // ceiling (children.Count-proxy sort is kept -- it is load-bearing for feasibility at tight
    // budgets -- and no exact edge pass is performed in this mode). Drives the greedy-mode
    // Phase A feasibility-only tightening; Phase B clears it to run one real min-edge pass at
    // the determined step.
    


    // Per-state cap on how many candidate groups the greedy edge phase (mode A) generates and
    // evaluates before committing. At large m a single state can have 1000+ distinct step-optimal
    // groups, and generating + orbit-deduping + FitChildren over all of them is what makes the edge
    // phase appear to hang on shapes like 25,10,10 while the constructive step phase finishes in
    // seconds. The constructive group is always evaluated first (a proven within-budget choice), so any
    // cap keeps the phase correct -- it only trades a little per-state edge-count minimization for a
    // bounded, interruptible runtime. int.MaxValue preserves the original exhaustive behavior.
    internal int CompactGreedyCandidateCap = DefaultCompactGreedyCandidateCap;

    private int GetCompactGreedyCandidateCap(int activeCount, int groupSize)
        => ScaleDefaultCandidateCap(CompactGreedyCandidateCap, DefaultCompactGreedyCandidateCap, activeCount, groupSize);

    internal int GetCompactGreedyCandidateCapForTesting(int activeCount, int groupSize)
        => GetCompactGreedyCandidateCap(activeCount, groupSize);

    // Set true whenever the greedy candidate cap (a finite generationCap) actually truncated a state's
    // group enumeration during the current probe -- i.e. more representatives existed than the cap
    // allowed us to generate. Reset per feasible-compact probe. When a probe concludes infeasible, this
    // flag distinguishes a real proof (complete enumeration, cap never bit) from a merely-incomplete
    // search (cap truncated some state, so an untried group might have fit): the latter must not be
    // reported as proven optimal.
    

    // Snapshot of _compactEnumerationCapped taken at the moment a feasible-compact probe concluded
    // infeasible (root cost == sentinel), so the tightening loop can tell a proven-infeasible ceiling
    // (complete search) from a merely-incomplete one (cap truncated some state's enumeration).
    

    // Greedy edge-phase U tightening. The constructive step phase's worst-case step count U is a feasible
    // upper bound that is typically opt+1, and the edge phase inherits it as its ceiling. After the
    // baseline compact pass at U, this opportunistically re-runs the compact pass at U-1, U-2, ...: a
    // tighter ceiling forces the greedy to pick shallower groups, and whenever the pass still yields a
    // feasible tree we have lowered the reported max-step (usually down to the true optimum, which the
    // measured gap shows is almost always exactly U-1). Each retry is a full compact pass; the loop is
    // bounded below by the proven analytic lower bound L (it can never beat L) and by the run's
    // cancellation token. There is no wall-clock budget: tightening runs to completion, and a user who no
    // longer wants to wait cancels (GUI Stop / CLI Ctrl+C), keeping the best plan found so far. Default on.
    // Per-pass root budget override used only by the tightening loop (-1 = use the threaded step U).
    

    // Root cost returned by the most recent compact phase-1b solve; int.MaxValue means the budget was
    // infeasible, so the tightening loop must stop rather than materialize an unsolved tree.
    

    // Clears the per-budget compact caches so a tightening retry re-solves from scratch at the new
    // (tighter) ceiling. The cross-phase step budget/estimate fields are intentionally left untouched.
    // This resets the compact selection caches, ensuring subsequent phases start with clean state.
    private void ResetCompactState()
    {
        _session.ResetCompactSelectionState();
        _phase1bSolved = false;
    }

    private void PrepareFeasibleCompactProbe()
    {
        ResetPerBuildTransientState();
        ResetCompactState();
        _lastProbeEnumerationCapped = false;
    }

    // Returns the proxy subtree cost (number of displayed branch edges) under the
    // compact-optimal choice for this state, populating _compactGroupPatternCache. The step
    // ceiling is supplied by the caller: in exact+compact mode the per-state proven optimum
    // (GetMinWorstCaseSteps) is used; in feasible+compact mode the feasible budget threads from the
    // root upper bound U, decremented one per level, so the tree can never exceed U. The cost memo is
    // keyed by (state, budget) because the same state reached with a looser budget may pick a wider
    // group; in exact mode the budget equals the state's opt so the key reduces to state-only.
    private int SolveCompact(ComparisonState state, int remainingSlots, int feasibleBudget = int.MaxValue)
        => CompactSolverInstance.SolveCompact(state, remainingSlots, feasibleBudget);

    // Greedy compact selection for mode A: instead of enumerating every step-optimal group and
    // globally minimizing displayed edges, it picks the budget-fitting group with the fewest distinct
    // children (a cheap edge proxy) and recurses once. Linear in reachable states, fast and
    // interruptible; edge count is a next-best, not proven minimal. MaxStep is still bounded by the
    // threaded budget, and free pickup may realize fewer real steps.
    private int SolveEdgeCompactGreedy(
        ComparisonState state, int remainingSlots, int budget, SearchStateKey key, (SearchStateKey, int) memoKey)
        => CompactSolverInstance.SolveEdgeCompactGreedy(state, remainingSlots, budget, key, memoKey);

    // Feasibility-only variant of SolveEdgeCompactGreedy. Proves a state is solvable within the
    // threaded step ceiling using the first solvable group in children-count-proxy order, and returns
    // as soon as one is found. It mirrors the min-edge greedy's candidate gathering and children.Count
    // sort (that ordering is load-bearing for feasibility at tight budgets, not just an edge
    // optimization), and performs no edge minimization in this phase. It still caches the chosen group
    // pattern (so BuildState can materialize) and the realized step count (so the plan's MaxStep is
    // correct). Returns 1 + maxChildRealSteps as a finite "cost" (callers only compare against
    // int.MaxValue), or int.MaxValue if no group fits the ceiling.
    private int SolveBudgetFeasibility(
        ComparisonState state, int remainingSlots, int budget, SearchStateKey key, (SearchStateKey, int) memoKey)
        => CompactSolverInstance.SolveBudgetFeasibility(state, remainingSlots, budget, key, memoKey);

    // Actual worst-case steps realized by the compact selection for a state: 0 for the
    // no-comparison terminals (mirrors SolveCompactSelection's base cases), otherwise the memoized
    // chosen-group depth. Lets feasible+compact surface a MaxStep below the feasible budget.
    private int GetCompactRealSteps(ComparisonState state, int remainingSlots)
        => CompactSolverInstance.GetCompactRealSteps(state, remainingSlots);

    // Search objective over the implicit search tree keyed by SearchStateKey:
    // children.Count + sum(child costs). Distinct from display/materialized edge count.
    internal int GetStepOptimalSearchTreeEdges()
    {
        EnsurePhase1Solved();
        return ComputeSearchTreeEdgesForSelection(useCompactSelection: false);
    }

    internal int GetCompactSearchTreeEdges()
    {
        EnsureCompactSolved();
        return _compactRootCost;
    }

    private int ComputeSearchTreeEdgesForSelection(bool useCompactSelection)
        => CompactSolverInstance.ComputeSearchTreeEdgesForSelection(useCompactSelection);

    private int ComputeSearchTreeEdges(
        ComparisonState state,
        int remainingSlots,
        Dictionary<SearchStateKey, int> memo)
        => CompactSolverInstance.ComputeSearchTreeEdges(state, remainingSlots, memo);

}

