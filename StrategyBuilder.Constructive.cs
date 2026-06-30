using System;
using System.Collections.Generic;

partial class StrategyBuilder
{
    // Constructive feasible-strategy upper bound ("tournament keeping the poset").
    //
    // This is the greedy mode's step phase (BuildFeasiblePlan). An earlier enumeration-based greedy
    // committed to a single policy but still PICKED that policy by fully enumerating + scoring every
    // distinct candidate group at each state (EnumeratePrioritizedGroups -> ~C(active, m) work). On
    // large m that selection dominated (25,10,10 step phase ~49 s) even though only the #1 group was
    // used; this constructive selector replaces it and is both faster and no worse on U.
    //
    // This module computes a feasible upper bound from a CONSTRUCTIVE policy whose group is built in
    // O(active^2) directly from the current partial order -- no group enumeration. It is a tournament
    // that reuses accumulated comparison results: at each state it contests the maximal "frontier"
    // (items with the fewest proven-greater elements), so each sort surfaces a new top candidate and
    // pushes the losers toward elimination, while every already-resolved pair is skipped.
    //
    // Correctness (U >= opt): any complete, valid strategy yields a worst-case step count that is an
    // upper bound on the optimum. Validity here reduces to STRICT PROGRESS -- every sort must add at
    // least one new comparison relation on every adversary branch, so no branch can loop back to an
    // (display-)equivalent ancestor and the tree genuinely terminates in resolved top sets. A sort
    // makes progress iff the chosen group contains an unresolved (mutually unrelated) pair; whenever
    // this chooser runs the top-k set is not yet determined, so such a pair always exists among the
    // active items, and ChooseConstructiveGroup guarantees one is included.
    //
    // This builder reuses the existing BuildState materialization: ChooseGroup computes the group
    // directly via ChooseConstructiveGroup when _useConstructiveSelection is set, so no precomputed
    // pattern cache / closure pre-solve is needed (the chooser is cheap and deterministic).
    private bool _useConstructiveSelection;
    private Dictionary<SearchStateKey, int>? _constructiveDepthMemo;

    // Feasible step budget U threaded from the step phase to the edge phase within one combined run.
    // BuildFeasiblePlan sets it to the MATERIALIZED MaxStep of the just-built step tree (the tightest
    // sound budget: the step plan itself witnesses a U-step solution, so the compact pass under this
    // ceiling can never need more than U). The edge phase (BuildFeasibleCompactPlan) reads it so it
    // never produces a plan worse than the step phase. -1 until a step plan is built; deliberately NOT
    // cleared by ResetPerBuildTransientState so it survives the step->edge build boundary on the same
    // builder. When the edge phase runs standalone (no prior step build) it falls back to the lean
    // ConstructiveRootUpperBound, which is sound but looser.
    private int _feasibleRootBudget = -1;

    // Total distinct canonical search states the step phase visited, captured at the end of
    // BuildFeasiblePlan and (like _feasibleRootBudget) deliberately NOT cleared by
    // ResetPerBuildTransientState so it survives the step->edge boundary on the same builder. The edge
    // phase's compact solve walks the same canonical SearchStateKey space, so this count is a sound
    // denominator estimate for turning _compactStatesSolved into a live progress fraction (the edge
    // phase otherwise has no pending/searched signal and would pin progress and eta). -1 until a step
    // plan is built; the standalone edge phase (no prior step build) leaves it -1 and keeps the old
    // behavior.
    private int _feasibleCompactStateEstimate = -1;

    // Builds the greedy-mode feasible strategy (step tree): a valid, displayable strategy whose
    // worst-case step count is a feasible UPPER bound U on the optimum -- never a proof of optimality.
    // Combined with the analytic proven lower bound L (GetMinWorstCaseLowerBound on the root) this
    // gives a squeeze "L <= opt <= U" displayable even for shapes the exact search never resolves.
    //
    // The comparison group at each state is built constructively from the current partial order
    // (ChooseConstructiveGroup, O(m*active^2)), so unlike the old enumeration-based greedy there is no
    // phase-1 closure / pattern cache: the policy is computed on the fly during materialization.
    public StrategyPlan BuildFeasiblePlan()
    {
        // The feasible phase is effectively instant. In a combined run it occupies the first band of
        // the unified progress bar (a single indivisible slice).
        _progressScope = _reportCombinedRunProgress
            ? ProgressScope.FeasibleInCombinedRun
            : ProgressScope.DefaultStandalone;

        ResetPerBuildTransientState();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ReportProgress(force: true);

        // No phase-1 closure: the constructive policy is computed on the fly during materialization.
        _phase1Milliseconds = stopwatch.ElapsedMilliseconds;

        // L side of the squeeze: a proven analytic lower bound on the root optimum, computed
        // independently of the (never-finishing) exact search. Surfaced via
        // SearchStatistics.RootProvenLowerBound, identical to the default path.
        RecordRootProvenLowerBound(GetMinWorstCaseLowerBound(new ComparisonState(_n), _k));
        _phase1bMilliseconds = stopwatch.ElapsedMilliseconds - _phase1Milliseconds;

        _useCompactSelection = false;
        _useConstructiveSelection = true;
        var root = BuildState(new ComparisonState(_n), 0, _k, 1);
        _useConstructiveSelection = false;
        _phase2Milliseconds = stopwatch.ElapsedMilliseconds - _phase1Milliseconds - _phase1bMilliseconds;
        stopwatch.Stop();
        ReportProgress(force: true);

        var plan = new StrategyPlan(
            _n, _m, _requestedK, _k, root, stopwatch.Elapsed, CreateSearchStatistics(),
            isFeasibleUpperBound: true);

        // Surface the exact U this materialized tree achieves so the edge (compact) phase in the same
        // combined run uses it as its step ceiling -- the tightest sound budget, guaranteeing the edge
        // plan is never worse than this step plan.
        _feasibleRootBudget = plan.MaxStep;

        // Denominator estimate for the edge phase's live progress (see field doc): the distinct
        // canonical states this step pass touched approximate the compact solve's total work.
        _feasibleCompactStateEstimate = _visitedSearchStates.Count;
        return plan;
    }

    // Lean worst-case step count of the constructive strategy from the root: a sound but looser
    // feasible step budget for the edge phase when the materialized U is unavailable (the edge phase
    // run standalone, with no prior step build on this builder). Without the materializer's display-key
    // Reference de-duplication this counts the full longest path, so it is >= the materialized MaxStep.
    private int ConstructiveRootUpperBound()
    {
        _constructiveDepthMemo = new Dictionary<SearchStateKey, int>();
        return ConstructiveDepth(new ComparisonState(_n), _k);
    }

    private int ConstructiveDepth(ComparisonState state, int remainingSlots)
    {
        ThrowIfCancellationRequested();
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);

        // Terminals mirror BuildState: no comparison needed.
        if (remainingSlots == 0)
            return 0;
        if (TryGetDeterminedTopSet(state, remainingSlots, out _))
            return 0;
        if (state.ActiveCount <= remainingSlots)
            return 0;

        // A single final sort of the <= m active items fully orders them and resolves the top set
        // (BuildState renders this as a FinalChoiceSummary, counted as one step).
        if (state.ActiveCount <= _m)
            return 1;

        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        if (_constructiveDepthMemo!.TryGetValue(key, out int cached))
            return cached;

        List<int> group = ChooseConstructiveGroup(state, remainingSlots);

        int maxChildSteps = 0;
        VisitComparisonOutcomes(
            state,
            fixedTopMask: 0,
            remainingSlots,
            group,
            currentKey: key,
            collectMergedBranches: false,
            onUsefulOutcome: outcome =>
            {
                int childSteps = ConstructiveDepth(outcome.NextState, outcome.NextRemainingSlots);
                if (childSteps > maxChildSteps)
                    maxChildSteps = childSteps;
                return true;
            });

        int result = 1 + maxChildSteps;
        _constructiveDepthMemo[key] = result;
        return result;
    }

    // Builds the next comparison group constructively from the current partial order. Returns a group
    // of min(m, active) items GUARANTEED to contain an unresolved pair (so the sort makes progress).
    //
    // Strategy: build a near-ANTICHAIN incrementally. The maxima (items with zero active ancestors)
    // form an antichain, so contesting mutually-unrelated items extracts the most information per
    // sort (a sort of an antichain resolves every pair at once; a sort that re-includes an already
    // known relation wastes a slot). Each pick maximizes the count of current group members it is
    // UNRELATED to (new unresolved pairs), tie-broken toward the frontier: fewest active ancestors
    // (closest to the top -> most likely a guaranteed top hit), then fewest total relations (most
    // still to learn), then smallest id for determinism.
    private List<int> ChooseConstructiveGroup(ComparisonState state, int remainingSlots)
    {
        List<int> active = state.GetActiveItemsOrdered();
        int size = Math.Min(_m, active.Count);

        var group = new List<int>(size);
        var inGroup = new HashSet<int>();

        while (group.Count < size)
        {
            int best = -1;
            int bestUnrelated = -1, bestAnc = 0, bestRel = 0;
            foreach (int item in active)
            {
                if (inGroup.Contains(item))
                    continue;

                int unrelated = 0;
                foreach (int g in group)
                    if (!state.HasAncestor(item, g) && !state.HasAncestor(g, item))
                        unrelated++;

                int anc = state.GetAncestorCount(item);
                int rel = anc + state.GetDescendantCount(item);

                if (best < 0 ||
                    unrelated > bestUnrelated ||
                    (unrelated == bestUnrelated && anc < bestAnc) ||
                    (unrelated == bestUnrelated && anc == bestAnc && rel < bestRel) ||
                    (unrelated == bestUnrelated && anc == bestAnc && rel == bestRel && item < best))
                {
                    best = item;
                    bestUnrelated = unrelated;
                    bestAnc = anc;
                    bestRel = rel;
                }
            }

            group.Add(best);
            inGroup.Add(best);
        }

        // Progress guarantee: if the picked group is a total chain (all pairs already resolved), force
        // in an unresolved active pair so the sort still adds a new relation.
        if (!GroupHasUnresolvedPair(state, group))
            ForceUnresolvedPair(state, active, group);

        group.Sort();
        return group;
    }

    private static bool GroupHasUnresolvedPair(ComparisonState state, List<int> group)
    {
        for (int i = 0; i < group.Count - 1; i++)
            for (int j = i + 1; j < group.Count; j++)
                if (!state.HasAncestor(group[i], group[j]) && !state.HasAncestor(group[j], group[i]))
                    return true;

        return false;
    }

    // Rewrites `group` (a total chain) so it contains some unresolved active pair (a, b), evicting
    // its most-resolved members. Such a pair always exists when the chooser runs (top-k undetermined).
    private static void ForceUnresolvedPair(ComparisonState state, List<int> activeFrontierOrder, List<int> group)
    {
        int a = -1, b = -1;
        for (int i = 0; i < activeFrontierOrder.Count && a < 0; i++)
        {
            for (int j = i + 1; j < activeFrontierOrder.Count; j++)
            {
                int x = activeFrontierOrder[i];
                int y = activeFrontierOrder[j];
                if (!state.HasAncestor(x, y) && !state.HasAncestor(y, x))
                {
                    a = x;
                    b = y;
                    break;
                }
            }
        }

        if (a < 0)
            return; // No unresolved pair: top-k already determined; nothing to force.

        EnsureMember(state, group, a, keep: b);
        EnsureMember(state, group, b, keep: a);
    }

    private static void EnsureMember(ComparisonState state, List<int> group, int item, int keep)
    {
        if (group.Contains(item))
            return;

        int worstIndex = -1;
        int worstScore = -1;
        for (int i = 0; i < group.Count; i++)
        {
            if (group[i] == keep)
                continue;

            int score = state.GetAncestorCount(group[i]) + state.GetDescendantCount(group[i]);
            if (score > worstScore)
            {
                worstScore = score;
                worstIndex = i;
            }
        }

        if (worstIndex >= 0)
            group[worstIndex] = item;
    }
}
