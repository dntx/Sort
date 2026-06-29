using System;
using System.Collections.Generic;

partial class StrategyBuilder
{
    // Greedy feasible-strategy constructor ("anytime" upper bound).
    //
    // The exact search (phase 1) explodes on the hardest shapes (e.g. 25,5,5) because it does a
    // full minimax over EVERY candidate group at every node AND proves optimality. The greedy
    // constructor instead commits to a SINGLE policy: at each state it takes only the #1 group
    // from the heuristic priority order (EnumeratePrioritizedGroups) and recurses on ALL adversary
    // outcomes. This collapses the work to the single-policy state closure, which is tiny and
    // instant (25,5,5: ~11 states, <1s), yielding a real, displayable strategy whose worst-case
    // step count is a feasible UPPER bound U on the optimum -- never a proof of optimality.
    //
    // Combined with the analytic proven lower bound L (GetMinWorstCaseLowerBound on the root),
    // this gives a squeeze "L <= opt <= U" that we can display even for cases the exact search
    // never resolves. Measurement shows the gap U - opt is small (1-2 on solvable cases), and a
    // beam width > 1 does NOT improve U on 25,5,5 while it does blow up the closure, so the
    // constructor deliberately keeps beam width 1.
    private bool _useGreedySelection;
    private bool _greedySolved;
    private int _greedyStatesSolved;
    private readonly Dictionary<SearchStateKey, BestGroupPattern> _greedyGroupPatternCache = new();
    private readonly HashSet<SearchStateKey> _greedyVisited = new();
    private readonly Dictionary<SearchStateKey, int> _greedyStepsMemo = new();

    // Solves the greedy single-policy closure once and reuses it across phases. Feasible+compact
    // depends on the per-state feasible step memo this populates.
    private void EnsureGreedySolved()
    {
        if (_greedySolved)
            return;
        SolveGreedySelection(new ComparisonState(_n), _k);
        _greedySolved = true;
    }

    public StrategyPlan BuildFeasiblePlan()
    {
        // The greedy phase is effectively instant. In a combined run it occupies the first 1% band of
        // the unified progress bar (a single indivisible slice), so the bar shows 1% while it runs and
        // the exact (default) phase then continues cleanly from 1%.
        _progressScope = _reportCombinedRunProgress
            ? ProgressScope.FeasibleInCombinedRun
            : ProgressScope.DefaultStandalone;

        ResetPerBuildTransientState();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ReportProgress(force: true);

        // Greedy "phase 1": pick the single best-priority group per reachable state and cache its
        // pattern. This does NOT call EnsurePhase1Solved -- the exact search is exactly what we are
        // avoiding here.
        EnsureGreedySolved();
        _phase1Milliseconds = stopwatch.ElapsedMilliseconds;

        // L side of the squeeze: a proven analytic lower bound on the root optimum, computed
        // independently of the (never-finishing) exact search. Surfaced via
        // SearchStatistics.RootProvenLowerBound, identical to the default path.
        RecordRootProvenLowerBound(GetMinWorstCaseLowerBound(new ComparisonState(_n), _k));
        _phase1bMilliseconds = stopwatch.ElapsedMilliseconds - _phase1Milliseconds;

        // Phase 2: materialize the strategy tree from the greedy pattern cache.
        _useCompactSelection = false;
        _useGreedySelection = true;
        var root = BuildState(new ComparisonState(_n), 0, _k, 1);
        _phase2Milliseconds = stopwatch.ElapsedMilliseconds - _phase1Milliseconds - _phase1bMilliseconds;
        stopwatch.Stop();
        ReportProgress(force: true);

        return new StrategyPlan(
            _n, _m, _requestedK, _k, root, stopwatch.Elapsed, CreateSearchStatistics(),
            isFeasibleUpperBound: true);
    }

    // Walks the single-policy state closure, recording the #1-priority group pattern for every
    // state the greedy materializer will later visit. Base cases mirror BuildState's terminals so
    // we never record a group for a state the renderer resolves without a comparison.
    private void SolveGreedySelection(ComparisonState state, int remainingSlots)
    {
        ThrowIfCancellationRequested();
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);

        // Terminal / final-choice states render no chosen comparison group (BuildState returns a
        // terminal or a FinalChoiceSummary without calling ChooseGroup), so they need no pattern.
        if (remainingSlots == 0)
            return;
        if (TryGetDeterminedTopSet(state, remainingSlots, out _))
            return;
        if (state.ActiveCount <= remainingSlots)
            return;
        if (state.ActiveCount <= _m)
            return;

        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        if (!_greedyVisited.Add(key))
            return;

        var candidates = state.GetActiveItemsOrdered();
        int groupSize = Math.Min(_m, candidates.Count);

        // Greedy commitment: take ONLY the top-priority group from the heuristic ranking.
        List<int>? chosen = null;
        foreach (var group in EnumeratePrioritizedGroups(state, remainingSlots, candidates, groupSize))
        {
            chosen = group;
            break;
        }

        if (chosen is null)
            throw new InvalidOperationException("Greedy selection found no candidate comparison group.");

        _greedyGroupPatternCache[key] = MakeGroupPattern(state, chosen);
        _greedyStatesSolved++;
        ReportProgress();

        // Recurse on every adversary outcome (the "max" layer is unavoidable for a valid strategy).
        // Track the deepest branch so this state's feasible worst-case step count is cached: it is
        // the per-state upper bound the feasible+compact pass uses as its edge-minimization budget.
        int maxBranchSteps = 0;
        VisitComparisonOutcomes(
            state,
            fixedTopMask: 0,
            remainingSlots,
            chosen,
            currentKey: key,
            collectMergedBranches: false,
            onUsefulOutcome: outcome =>
            {
                SolveGreedySelection(outcome.NextState, outcome.NextRemainingSlots);
                int branchSteps = GetGreedyFeasibleSteps(outcome.NextState, outcome.NextRemainingSlots);
                if (branchSteps > maxBranchSteps)
                    maxBranchSteps = branchSteps;
                return true;
            });

        _greedyStepsMemo[key] = 1 + maxBranchSteps;
    }

    // Feasible worst-case steps for a state under the greedy policy: 0 for the no-comparison
    // terminals (mirrors SolveGreedySelection's base cases), otherwise the cached subtree depth.
    private int GetGreedyFeasibleSteps(ComparisonState state, int remainingSlots)
    {
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);
        if (remainingSlots == 0)
            return 0;
        if (TryGetDeterminedTopSet(state, remainingSlots, out _))
            return 0;
        if (state.ActiveCount <= remainingSlots)
            return 0;
        if (state.ActiveCount <= _m)
            return 0;
        return _greedyStepsMemo.TryGetValue(GetSearchStateKey(state, remainingSlots), out int steps) ? steps : 0;
    }
}
