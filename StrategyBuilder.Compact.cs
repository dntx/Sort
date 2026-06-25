using System;
using System.Collections.Generic;

partial class StrategyBuilder
{
    // EXPERIMENTAL (PoC): when enabled, phase 2 reads comparison groups from a
    // "compact" pattern cache produced by a secondary two-level DP. The DP keeps the
    // optimal worst-case step count (computed by phase 1) as the primary objective and,
    // among all equally-optimal groups, minimizes the size of the materialized subtree
    // (a proxy for the displayed output-state count). This lets us measure whether a
    // global "prefer the simplest equally-optimal solution" rule shrinks the trees.
    private bool _useCompactSelection;
    private int _compactStatesSolved;
    private int _compactGroupsEnumerated;
    private int _compactStepOptimalGroups;
    private readonly Dictionary<SearchStateKey, BestGroupPattern> _compactGroupPatternCache = new();
    private readonly Dictionary<SearchStateKey, int> _compactCostMemo = new();

    // Returns the proxy subtree cost (number of materialized nodes) under the
    // compact-optimal choice for this state, populating _compactGroupPatternCache.
    private int SolveCompactSelection(ComparisonState state, int remainingSlots)
    {
        ThrowIfCancellationRequested();
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);

        if (remainingSlots == 0)
            return 1;
        if (TryGetDeterminedTopSet(state, remainingSlots, out _))
            return 1;
        if (state.ActiveCount <= remainingSlots)
            return 1;
        if (state.ActiveCount <= _m)
            return 1;

        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        if (_compactCostMemo.TryGetValue(key, out int cachedCost))
            return cachedCost;

        // Sentinel guards against revisiting this state while it is being solved.
        // The search space is acyclic (children are strictly more resolved), so this
        // is only defensive.
        _compactCostMemo[key] = int.MaxValue;

        int optimalSteps = GetMinWorstCaseSteps(state, remainingSlots);
        _compactStatesSolved++;
        ReportProgress();

        var candidates = state.GetActiveItemsOrdered();
        int groupSize = Math.Min(_m, candidates.Count);

        // Gathers the distinct step-optimal child states for a group, or returns null if
        // the group is not useful or would not preserve the optimal worst-case step count.
        //
        // A group is step-optimal iff every branch can be resolved within the remaining
        // budget (optimalSteps - 1 further steps); since optimalSteps is the global minimum,
        // any group has 1 + max(branchSteps) >= optimalSteps, so "no branch exceeds the
        // budget" is exactly the step-optimal condition. We therefore bail out of the outcome
        // enumeration as soon as a single branch breaks the budget, mirroring phase 1's
        // lower-bound pruning. This avoids fully expanding the many non-optimal groups.
        List<(ComparisonState State, int RemainingSlots)>? GetStepOptimalChildren(IReadOnlyList<int> group)
        {
            int branchBudget = optimalSteps - 1;
            bool rejected = false;
            var children = new List<(ComparisonState State, int RemainingSlots)>();
            OutcomeTraversalSummary traversal = VisitComparisonOutcomes(
                state,
                fixedTopMask: 0,
                remainingSlots,
                group,
                currentKey: key,
                collectMergedBranches: false,
                onUsefulOutcome: outcome =>
                {
                    // Cheap lower bound first; only fall back to the exact (cached) step count
                    // when the lower bound cannot already rule the branch out of budget.
                    if (GetMinWorstCaseLowerBound(outcome.NextState, outcome.NextRemainingSlots) > branchBudget ||
                        GetMinWorstCaseSteps(outcome.NextState, outcome.NextRemainingSlots) > branchBudget)
                    {
                        rejected = true;
                        return false;
                    }

                    children.Add((outcome.NextState, outcome.NextRemainingSlots));
                    return true;
                });

            if (rejected || !traversal.IsUseful)
                return null;
            return children;
        }

        List<int>? bestGroup = null;
        int bestCost = int.MaxValue;

        // Enumerate in a stable lexicographic order. Among groups with equal proxy cost
        // this keeps the first, which empirically yields the most subtree sharing (smallest
        // real output-state count). Branch-and-bound prunes provably-larger groups.
        foreach (var group in EnumerateDistinctGroups(state, candidates, groupSize))
        {
            ThrowIfCancellationRequested();
            _compactGroupsEnumerated++;

            var children = GetStepOptimalChildren(group);
            if (children is null)
                continue;
            _compactStepOptimalGroups++;

            // A group cannot beat the incumbent if even its minimal possible cost (one
            // node per child) reaches it.
            if (1 + children.Count >= bestCost)
                continue;

            int branchCostSum = 0;
            bool pruned = false;
            for (int i = 0; i < children.Count; i++)
            {
                branchCostSum += SolveCompactSelection(children[i].State, children[i].RemainingSlots);

                // Remaining unvisited children still contribute at least one node each.
                int partialLowerBound = 1 + branchCostSum + (children.Count - 1 - i);
                if (partialLowerBound >= bestCost)
                {
                    pruned = true;
                    break;
                }
            }

            if (pruned)
                continue;

            int groupCost = 1 + branchCostSum;
            if (groupCost < bestCost)
            {
                bestCost = groupCost;
                bestGroup = group;
            }
        }

        if (bestGroup is null)
        {
            throw new InvalidOperationException("Compact selection found no step-optimal comparison group.");
        }

        _compactGroupPatternCache[key] = new BestGroupPattern(bestGroup.Count, GetGroupPattern(state, bestGroup));
        _compactCostMemo[key] = bestCost;
        return bestCost;
    }
}
