using System;
using System.Collections.Generic;

partial class StrategyBuilder
{
    // EXPERIMENTAL (PoC): when enabled, phase 2 reads comparison groups from a
    // "compact" pattern cache produced by a secondary two-level DP. The DP keeps the
    // optimal worst-case step count (computed by phase 1) as the primary objective and,
    // among all equally-optimal groups, minimizes the total number of displayed branch
    // edges (the count of branch lines the renderer draws across the whole subtree). This
    // lets us measure whether a global "prefer the fewest-edges equally-optimal solution"
    // rule shrinks the trees.
    private bool _useCompactSelection;
    private int _compactStatesSolved;
    private int _compactGroupsEnumerated;
    private int _compactStepOptimalGroups;
    private readonly Dictionary<SearchStateKey, BestGroupPattern> _compactGroupPatternCache = new();
    private readonly Dictionary<SearchStateKey, int> _compactCostMemo = new();

    // Returns the proxy subtree cost (number of displayed branch edges) under the
    // compact-optimal choice for this state, populating _compactGroupPatternCache.
    private int SolveCompactSelection(ComparisonState state, int remainingSlots)
    {
        ThrowIfCancellationRequested();
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);

        // Terminal and final-choice states render no branch lines (terminals carry the
        // resolved top set; final-choice nodes are summarized by FinalChoiceSummary with an
        // empty Branches list), so they contribute zero edges to the subtree.
        if (remainingSlots == 0)
            return 0;
        if (TryGetDeterminedTopSet(state, remainingSlots, out _))
            return 0;
        if (state.ActiveCount <= remainingSlots)
            return 0;
        if (state.ActiveCount <= _m)
            return 0;

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

        // Enumerate in a stable lexicographic order. Among groups with equal edge cost this
        // keeps the first, which empirically yields the most subtree sharing. Branch-and-bound
        // prunes provably-larger groups: the displayed-edge count of a node is always at least
        // the number of distinct step-optimal children it has (the display path can only split
        // a successor state into more lines, never fewer), and every child subtree contributes
        // a non-negative number of edges, so children.Count is a valid lower bound on a group's
        // total cost before the heavier display enumeration runs.
        foreach (var group in EnumerateDistinctGroups(state, candidates, groupSize))
        {
            ThrowIfCancellationRequested();
            _compactGroupsEnumerated++;

            var children = GetStepOptimalChildren(group);
            if (children is null)
                continue;
            _compactStepOptimalGroups++;

            // Cheap lower bound (distinct children) before any child recursion or the heavy
            // display enumeration.
            if (children.Count >= bestCost)
                continue;

            int branchCostSum = 0;
            bool pruned = false;
            for (int i = 0; i < children.Count; i++)
            {
                branchCostSum += SolveCompactSelection(children[i].State, children[i].RemainingSlots);

                // The display edge count for this node is at least children.Count, so the group
                // cannot beat the incumbent once children.Count + the accumulated child cost does.
                if (children.Count + branchCostSum >= bestCost)
                {
                    pruned = true;
                    break;
                }
            }

            if (pruned)
                continue;

            // Only now pay for the heavy display enumeration that yields the exact edge count.
            int edgeCount = CountDisplayBranches(state, remainingSlots, group);
            int groupCost = edgeCount + branchCostSum;
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

    // Number of displayed branch lines this state renders for the given comparison group,
    // including doomed-tail folding and the symmetry-orbit vs. distinct-family split. Counts
    // exactly what BuildBranchSpecs would emit, but without building the per-branch pattern
    // summaries for the common single-family case: a merged branch backed by one order family is
    // a single relabeling orbit, so its pattern can never carry the " | " disjunction separator
    // and always renders as exactly one branch. Only multi-family merged branches need the
    // (expensive) pattern engine to decide single-orbit (1) vs. split (one line per family). This
    // keeps the compact DP's edge counting cheap on wide, low-m search spaces.
    private int CountDisplayBranches(ComparisonState state, int remainingSlots, IReadOnlyList<int> group)
    {
        IReadOnlyList<MergedBranch> merged = BuildMergedComparisonOutcomes(state, fixedTopMask: 0, remainingSlots, group);
        var chosenGroup = new SelectedComparisonGroup(group, merged);

        List<BranchSpec>? doomedTailSpecs = TryBuildDoomedTailSpecs(state, remainingSlots, chosenGroup);
        if (doomedTailSpecs is not null)
            return doomedTailSpecs.Count;

        int count = 0;
        foreach (MergedBranch branch in merged)
        {
            List<MergedFamilyOutcome> families = branch.FamilyOutcomes;
            if (families.Count == 1)
            {
                count += 1;
                continue;
            }

            EquivalentOrderSummary? combinedSummary = BuildEquivalentOrderSummary(
                families.ConvertAll(outcome => outcome.Family));
            count += MergedOrderingsFormSingleOrbit(combinedSummary) ? 1 : families.Count;
        }

        return count;
    }
}
