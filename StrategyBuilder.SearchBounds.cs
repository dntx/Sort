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
        {
            _exactCacheHits++;
            return cached;
        }

        bool isRootSearch = false;
        if (!_rootSearchInitialized)
        {
            _rootSearchInitialized = true;
            isRootSearch = true;
        }

        EnterSearchState();

        var candidates = state.GetActiveItemsOrdered();
        int groupSize = Math.Min(_m, candidates.Count);
        var labels = state.GetStructuralLabels();
        // Always rank candidate groups with the independent/symmetric heuristic, including at
        // early nodes where remainingSlots < _m (e.g. before any winner is fixed). This makes the
        // displayed optimal strategy prefer regular, fresh groupings (such as sorting an untouched
        // block) over equally-optimal but less intuitive mixed groups.
        IEnumerable<List<int>> groups =
            EnumeratePrioritizedGroups(state, remainingSlots, candidates, groupSize, labels);
        List<int>? bestGroup = null;
        int bestWorstCase = int.MaxValue;
        try
        {
            ThrowIfCancellationRequested();
            foreach (var group in groups)
            {
                ThrowIfCancellationRequested();
                int groupWorstCase = 0;
                OutcomeTraversalSummary traversal = VisitComparisonOutcomes(
                    state,
                    fixedTopMask: 0,
                    remainingSlots,
                    group,
                    key,
                    collectMergedBranches: false,
                    onUsefulOutcome: outcome =>
                    {
                        int branchLowerBound = 1 + GetMinWorstCaseLowerBound(outcome.NextState, outcome.NextRemainingSlots);
                        if (branchLowerBound >= bestWorstCase)
                        {
                            _lowerBoundPrunes++;
                            groupWorstCase = branchLowerBound;
                            return false;
                        }

                        int branchSteps = 1 + GetMinWorstCaseSteps(outcome.NextState, outcome.NextRemainingSlots);
                        groupWorstCase = Math.Max(groupWorstCase, branchSteps);
                        return groupWorstCase < bestWorstCase;
                    });

                if (traversal.IsUseful && groupWorstCase < bestWorstCase)
                {
                    int previousBestWorstCase = bestWorstCase;
                    bestWorstCase = Math.Min(bestWorstCase, groupWorstCase);
                    bestGroup = group;
                    if (isRootSearch && bestWorstCase < previousBestWorstCase)
                        RecordRootIncumbent(bestWorstCase, group);
                }
            }
        }
        finally
        {
            ExitSearchState();
        }

        if (bestWorstCase == int.MaxValue)
            throw new InvalidOperationException("Expected at least one useful comparison group when unresolved candidates exceed comparison size.");

        if (bestGroup is not null)
            _bestGroupPatternCache[key] = new BestGroupPattern(bestGroup.Count, GetGroupPattern(bestGroup, labels));

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
        {
            _lowerBoundCacheHits++;
            return cached;
        }

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
        {
            _feasibleTopSetCacheHits++;
            return cached;
        }

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

        ulong excludedMask = pivotBit | (state.GetDescendantMask(pivot) & candidateMask);
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
            if ((state.GetAncestorMask(item) & candidateMask) != 0)
                continue;

            int excludedCount = BitOperations.PopCount((state.GetDescendantMask(item) & candidateMask) | (1UL << item));
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

    internal ulong GetGuaranteedTopMaskForTesting(ComparisonState state, int remainingSlots)
    {
        return GetGuaranteedTopMask(state, remainingSlots);
    }

    internal int GetMinWorstCaseLowerBoundForTesting(ComparisonState state, int remainingSlots)
    {
        return GetMinWorstCaseLowerBound(state, remainingSlots);
    }

    internal FeasibleTopSetInfo GetFeasibleTopSetInfoForTesting(ComparisonState state, int remainingSlots)
    {
        return GetFeasibleTopSetInfo(state, remainingSlots);
    }
}
