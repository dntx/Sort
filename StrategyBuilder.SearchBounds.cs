using System;
using System.Collections.Generic;
using System.Numerics;

partial class StrategyBuilder
{
    private ulong GetGuaranteedTopMask(ComparisonState state, int remainingSlots)
    {
        ThrowIfCancellationRequested();
        ulong mask = 0;
        for (int i = 0; i < _n; i++)
        {
            if (state.IsActive(i) && state.ActiveCount - 1 - state.GetDescendantCount(i) <= remainingSlots - 1)
                mask |= 1UL << i;
        }

        return mask;
    }

    private int GetMinWorstCaseSteps(ComparisonState state, int remainingSlots)
    {
        ThrowIfCancellationRequested();
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);
        ObserveSearchState(state, remainingSlots);

        if (remainingSlots == 0)
            return 0;

        if (TryGetDeterminedTopSet(state, remainingSlots, out _))
            return 0;

        if (state.ActiveCount <= remainingSlots)
            return 0;

        if (state.ActiveCount <= _m)
            return 1;

        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        if (_minWorstCaseStepsCache.TryGetValue(key, out int cached))
            return cached;

        EnterSearchState();

        var candidates = state.GetActiveItemsOrdered();
        int groupSize = Math.Min(_m, candidates.Count);
        var labels = state.GetStructuralLabels();
        int bestWorstCase = int.MaxValue;
        try
        {
            ThrowIfCancellationRequested();
            foreach (var group in EnumerateDistinctGroups(candidates, groupSize, labels))
            {
                ThrowIfCancellationRequested();
                int groupWorstCase = 0;
                GroupTransitionVisitResult visitResult = VisitGroupTransitions(
                    state,
                    remainingSlots,
                    group,
                    key,
                    collectAllTransitions: false,
                    onUsefulTransition: transition =>
                    {
                        int branchLowerBound = 1 + GetMinWorstCaseLowerBound(transition.NextState, transition.NextRemainingSlots);
                        if (branchLowerBound >= bestWorstCase)
                        {
                            groupWorstCase = branchLowerBound;
                            return false;
                        }

                        int branchSteps = 1 + GetMinWorstCaseSteps(transition.NextState, transition.NextRemainingSlots);
                        groupWorstCase = Math.Max(groupWorstCase, branchSteps);
                        return groupWorstCase < bestWorstCase;
                    });

                if (visitResult.IsUseful)
                    bestWorstCase = Math.Min(bestWorstCase, groupWorstCase);
            }
        }
        finally
        {
            ExitSearchState();
        }

        if (bestWorstCase == int.MaxValue)
            bestWorstCase = 0;

        _minWorstCaseStepsCache[key] = bestWorstCase;
        return bestWorstCase;
    }

    private int GetMinWorstCaseLowerBound(ComparisonState state, int remainingSlots)
    {
        ThrowIfCancellationRequested();
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);
        ObserveSearchState(state, remainingSlots);

        if (remainingSlots == 0)
            return 0;

        if (TryGetDeterminedTopSet(state, remainingSlots, out _))
            return 0;

        if (state.ActiveCount <= remainingSlots)
            return 0;

        if (state.ActiveCount <= _m)
            return 1;

        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        if (_lowerBoundStepsCache.TryGetValue(key, out int cached))
            return cached;

        FeasibleTopSetInfo info = GetFeasibleTopSetInfo(state, remainingSlots);
        int maxOutcomesPerStep = GetMaxOutcomesPerStep(state);
        int distinguishable = 1;
        int steps = 0;
        while (distinguishable < info.Count)
        {
            ThrowIfCancellationRequested();
            steps++;
            checked
            {
                distinguishable *= maxOutcomesPerStep;
            }
        }

        _lowerBoundStepsCache[key] = steps;
        return steps;
    }

    private bool TryGetDeterminedTopSet(ComparisonState state, int remainingSlots, out ulong topMask)
    {
        ThrowIfCancellationRequested();
        FeasibleTopSetInfo info = GetFeasibleTopSetInfo(state, remainingSlots);
        if (info.Count == 1)
        {
            topMask = info.UniqueMask;
            return true;
        }

        topMask = 0;
        return false;
    }

    private FeasibleTopSetInfo GetFeasibleTopSetInfo(ComparisonState state, int remainingSlots)
    {
        ThrowIfCancellationRequested();
        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        _visitedSearchStates.Add(key);
        if (_feasibleTopSetCache.TryGetValue(key, out FeasibleTopSetInfo cached))
            return cached;

        var memo = new Dictionary<FeasibleTopSetSubproblemKey, FeasibleTopSetInfo>();
        FeasibleTopSetInfo info = CountFeasibleTopSets(state, state.ActiveMask, remainingSlots, memo);

        _feasibleTopSetCache[key] = info;
        return info;
    }

    private FeasibleTopSetInfo CountFeasibleTopSets(
        ComparisonState state,
        ulong candidateMask,
        int remainingSlots,
        Dictionary<FeasibleTopSetSubproblemKey, FeasibleTopSetInfo> memo)
    {
        ThrowIfCancellationRequested();
        int candidateCount = BitOperations.PopCount(candidateMask);
        if (remainingSlots < 0 || candidateCount < remainingSlots)
            return new FeasibleTopSetInfo(0, 0);

        if (remainingSlots == 0)
            return new FeasibleTopSetInfo(1, 0);

        if (candidateCount == remainingSlots)
            return new FeasibleTopSetInfo(1, candidateMask);

        var key = new FeasibleTopSetSubproblemKey(candidateMask, remainingSlots);
        if (memo.TryGetValue(key, out FeasibleTopSetInfo cached))
            return cached;

        int pivot = ChooseFeasibleTopSetPivot(state, candidateMask);
        ulong pivotBit = 1UL << pivot;

        FeasibleTopSetInfo includeInfo = CountFeasibleTopSets(
            state,
            candidateMask & ~pivotBit,
            remainingSlots - 1,
            memo);

        ulong excludedMask = pivotBit | (state.Descendants[pivot] & candidateMask);
        FeasibleTopSetInfo excludeInfo = CountFeasibleTopSets(
            state,
            candidateMask & ~excludedMask,
            remainingSlots,
            memo);

        int totalCount = checked(includeInfo.Count + excludeInfo.Count);
        ulong uniqueMask = 0;
        if (totalCount == 1)
        {
            uniqueMask = includeInfo.Count == 1
                ? includeInfo.UniqueMask | pivotBit
                : excludeInfo.UniqueMask;
        }

        FeasibleTopSetInfo info = new(totalCount, uniqueMask);
        memo[key] = info;
        return info;
    }

    private int ChooseFeasibleTopSetPivot(ComparisonState state, ulong candidateMask)
    {
        ThrowIfCancellationRequested();
        int bestItem = -1;
        int bestExcludedCount = -1;
        ulong remaining = candidateMask;
        while (remaining != 0)
        {
            int item = BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            if ((state.Ancestors[item] & candidateMask) != 0)
                continue;

            int excludedCount = BitOperations.PopCount((state.Descendants[item] & candidateMask) | (1UL << item));
            if (excludedCount > bestExcludedCount || (excludedCount == bestExcludedCount && item < bestItem))
            {
                bestItem = item;
                bestExcludedCount = excludedCount;
            }
        }

        return bestItem;
    }

    private int GetMaxOutcomesPerStep(ComparisonState state)
    {
        int maxGroupSize = Math.Min(_m, state.ActiveCount);
        int outcomes = 1;
        for (int i = 2; i <= maxGroupSize; i++)
            outcomes *= i;
        return outcomes;
    }
}
