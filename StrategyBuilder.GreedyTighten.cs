using System;
using System.Collections.Generic;
using System.Diagnostics;

partial class StrategyBuilder
{
    // GreedyTighten (Phase 0) — local restructuring of the greedy-feasible tree to lower the longest
    // path. See docs/core-algorithm.md 4.7 for the full design/rationale. This is the FRAMEWORK slice
    // (阶段 A): multi-round + critical-path post-order + AND short-circuit + single-state edit +
    // override map + lean-depth DP + one final materialization. The candidate SOURCE / SCORER are the
    // deferred tuning part (阶段 B): v1 uses the existing distinct-group enumeration in canonical order
    // (no scoring). NO proof semantics — it only tightens the feasible upper bound.
    //
    // It is NOT wired into the production pipeline yet; BuildGreedyTightenPlan is only exercised by
    // tests until the mechanism is validated.
    internal int GreedyTightenCandidateCap = 128;

    // Accumulated local edits: canonical stateKey -> chosen comparison group. Where present, both the
    // lean-depth DP and the materializing ChooseGroup route use the override instead of the
    // constructive selector; absent keys fall back to the same ChooseConstructiveGroup that the
    // greedy-feasible baseline uses (so an empty override map reproduces greedy-feasible exactly).
    private readonly Dictionary<SearchStateKey, List<int>> _greedyTightenOverrides = new();
    private bool _useGreedyTightenSelection;

    // Diagnostics (per BuildGreedyTightenPlan run).
    private int _greedyTightenRounds;
    private int _greedyTightenCommits;
    internal int GreedyTightenRounds => _greedyTightenRounds;
    internal int GreedyTightenCommits => _greedyTightenCommits;

    // Builds the GreedyTighten stage plan: runs the local restructuring to tighten the upper bound,
    // then materializes the tightened tree once. Returns a feasible-upper-bound plan (never proven).
    public StrategyPlan BuildGreedyTightenPlan()
    {
        _progressScope = _reportCombinedRunProgress
            ? ProgressScope.FeasibleInCombinedRun
            : ProgressScope.DefaultStandalone;

        ResetPerBuildTransientState();
        var stopwatch = Stopwatch.StartNew();

        // L side of the squeeze: proven analytic lower bound (independent of the never-finishing exact
        // search), identical to the greedy-feasible path.
        RecordRootProvenLowerBound(GetMinWorstCaseLowerBound(new ComparisonState(_n), _k));

        RunGreedyTighten();

        // Materialize the tightened tree once (Option B: search on the lean-depth DP, materialize only
        // at the end). Absent overrides fall back to ChooseConstructiveGroup, so this yields the
        // greedy-feasible tree plus the committed local edits.
        _useGreedyTightenSelection = true;
        StrategyNode root = BuildState(new ComparisonState(_n), 0, _k, 1);
        _useGreedyTightenSelection = false;

        stopwatch.Stop();

        return new StrategyPlan(
            _n, _m, _requestedK, _k, root, stopwatch.Elapsed, CreateSearchStatistics(),
            isFeasibleUpperBound: true);
    }

    // Multi-round driver. Each round runs one critical-path post-order pass from the root; a pass that
    // lowers the root height ends the round and a fresh (tighter) round starts from scratch. When a
    // pass cannot lower the root, GreedyTighten has converged. Overrides persist across rounds; the
    // lean-depth tree is recomputed from the override map each round.
    private void RunGreedyTighten()
    {
        _greedyTightenOverrides.Clear();
        _greedyTightenRounds = 0;
        _greedyTightenCommits = 0;

        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _greedyTightenRounds++;
            bool rootDropped = TryLowerHeight(new ComparisonState(_n), _k);
            if (!rootDropped)
                break;
        }
    }

    // Critical-path post-order pass at (state, remainingSlots). Returns true iff this state's lean
    // subtree height strictly decreased -- either by lowering ALL of its current critical (max-height)
    // children (AND relationship, short-circuited on the first child that cannot drop), or by replacing
    // its own comparison group with a candidate that yields a strictly shorter subtree. Any override
    // committed in a descendant is permanent regardless of whether this state (or the root) drops.
    private bool TryLowerHeight(ComparisonState state, int remainingSlots)
    {
        ThrowIfCancellationRequested();
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);

        // Terminals / final-choice states carry no editable comparison group.
        if (remainingSlots == 0)
            return false;
        if (TryGetDeterminedTopSet(state, remainingSlots, out _))
            return false;
        if (state.ActiveCount <= remainingSlots)
            return false;
        if (state.ActiveCount <= _m)
            return false;

        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        int height = GreedyTightenHeight(state, remainingSlots, new Dictionary<SearchStateKey, int>());
        List<int> group = CurrentGreedyTightenGroup(state, remainingSlots, key);

        // Enumerate children under the current group and find the critical (max-height) ones.
        var children = new List<(ComparisonState State, int Rem, int Height)>();
        VisitComparisonOutcomes(
            state, fixedTopMask: 0, remainingSlots, group, currentKey: key, collectMergedBranches: false,
            onUsefulOutcome: outcome =>
            {
                int h = GreedyTightenHeight(outcome.NextState, outcome.NextRemainingSlots, new Dictionary<SearchStateKey, int>());
                children.Add((outcome.NextState, outcome.NextRemainingSlots, h));
                return true;
            });

        int childMax = 0;
        foreach (var child in children)
            if (child.Height > childMax)
                childMax = child.Height;

        // Option (a): lower every critical child (AND). Short-circuit on the first that cannot drop --
        // keeping this group, the parent can only fall when all its max-height children fall.
        bool allCriticalDropped = true;
        foreach (var child in children)
        {
            if (child.Height < childMax)
                continue;
            if (!TryLowerHeight(child.State, child.Rem))
            {
                allCriticalDropped = false;
                break;
            }
        }
        if (allCriticalDropped)
        {
            int newHeight = GreedyTightenHeight(state, remainingSlots, new Dictionary<SearchStateKey, int>());
            if (newHeight < height)
                return true;
        }

        // Option (b): replace this state's own group. v1 tries the existing distinct-group enumeration
        // (capped) in canonical order and commits the first candidate that strictly lowers the subtree
        // height (hit-once-and-move-on). Scoring/ordering is the deferred 阶段 B tuning.
        var candidates = state.GetActiveItemsOrdered();
        int groupSize = Math.Min(_m, candidates.Count);
        foreach (List<int> candidate in EnumerateDistinctGroups(state, candidates, groupSize, GreedyTightenCandidateCap))
        {
            if (!GroupHasUnresolvedPair(state, candidate))
                continue; // must make progress, else the subtree does not terminate
            if (SameGroupSequence(candidate, group))
                continue;

            int candidateHeight = GreedyTightenHeightUnderGroup(
                state, remainingSlots, candidate, new Dictionary<SearchStateKey, int>());
            if (candidateHeight < height)
            {
                _greedyTightenOverrides[key] = new List<int>(candidate);
                _greedyTightenCommits++;
                return true;
            }
        }

        return false;
    }

    // Lean subtree height at (state, remainingSlots) under the current override map: 1 + max child
    // height, where each state's group is its override (if any) or the constructive selector. Budget
    // independent; memoized within a single evaluation (a fresh memo per call keeps it correct as
    // overrides change between evaluations). Mirrors ConstructiveDepth's terminal cases so it matches
    // the materialized tree's structure (modulo display-key Reference de-duplication).
    private int GreedyTightenHeight(ComparisonState state, int remainingSlots, Dictionary<SearchStateKey, int> memo)
    {
        ThrowIfCancellationRequested();
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);

        if (remainingSlots == 0)
            return 0;
        if (TryGetDeterminedTopSet(state, remainingSlots, out _))
            return 0;
        if (state.ActiveCount <= remainingSlots)
            return 0;
        if (state.ActiveCount <= _m)
            return 1;

        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        if (memo.TryGetValue(key, out int cached))
            return cached;

        List<int> group = CurrentGreedyTightenGroup(state, remainingSlots, key);
        int result = 1 + MaxChildHeight(state, remainingSlots, key, group, memo);
        memo[key] = result;
        return result;
    }

    // Lean subtree height if `state` used comparison group `group` for its next step (one level), with
    // descendants falling back to the override map / constructive selector. Used to evaluate a
    // candidate replacement without mutating the override map.
    private int GreedyTightenHeightUnderGroup(
        ComparisonState state, int remainingSlots, List<int> group, Dictionary<SearchStateKey, int> memo)
    {
        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        return 1 + MaxChildHeight(state, remainingSlots, key, group, memo);
    }

    private int MaxChildHeight(
        ComparisonState state, int remainingSlots, SearchStateKey key, List<int> group,
        Dictionary<SearchStateKey, int> memo)
    {
        int maxChild = 0;
        VisitComparisonOutcomes(
            state, fixedTopMask: 0, remainingSlots, group, currentKey: key, collectMergedBranches: false,
            onUsefulOutcome: outcome =>
            {
                int childHeight = GreedyTightenHeight(outcome.NextState, outcome.NextRemainingSlots, memo);
                if (childHeight > maxChild)
                    maxChild = childHeight;
                return true;
            });
        return maxChild;
    }

    // The comparison group GreedyTighten currently uses at a state: the committed override if present,
    // otherwise the same constructive selector as the greedy-feasible baseline.
    private List<int> CurrentGreedyTightenGroup(ComparisonState state, int remainingSlots, SearchStateKey key)
        => _greedyTightenOverrides.TryGetValue(key, out List<int>? overrideGroup)
            ? overrideGroup
            : ChooseConstructiveGroup(state, remainingSlots);

    private static bool SameGroupSequence(List<int> a, List<int> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }
}
