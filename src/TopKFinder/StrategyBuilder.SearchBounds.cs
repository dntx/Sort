using System;
using System.Collections.Generic;
using System.Numerics;

namespace TopKFinder;

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

    // Driver returning the EXACT optimum. In the deep/large-k regime it runs an iterative-deepening
    // (IDA*-style) loop over GetMinWorstCaseStepsBounded with a global budget that starts at the
    // analytic lower bound and jumps to the learned lower bound each time a pass fails; because every
    // pass enforces a tight budget at every node, the analytic bounds (which are only large near the
    // root, where there is no incumbent yet) translate into real pruning instead of being wasted
    // under an unbounded alpha-beta search. Outside that regime it falls back to the single-pass
    // exact search, which is byte-identical to the pre-ID algorithm and avoids ID's re-exploration
    // overhead on shallow/wide shapes. Both paths return the same exact MaxStep optimum. They do NOT
    // necessarily materialize the same tree: the bounded path can pick a different representative
    // among equally-optimal groups, so a gated case may yield a different (still MaxStep-optimal)
    // tree than the single-pass path (see docs/core-algorithm.md sec 4.3).
    private int GetMinWorstCaseSteps(ComparisonState state, int remainingSlots)
    {
        return SearchBounds.GetMinWorstCaseSteps(state, remainingSlots);
    }

    // Single-pass exact minimax (the pre-ID algorithm). Computes each child's exact optimum with no
    // global budget threaded down, pruning only at the outcome level against the current incumbent.
    // Used outside the iterative-deepening regime; kept byte-identical to preserve the established
    // search-statistics baselines for shallow/wide shapes.
    private int GetMinWorstCaseStepsExact(ComparisonState state, int remainingSlots)
    {
        return SearchBounds.GetMinWorstCaseStepsExact(state, remainingSlots);
    }

    // Bounded minimax. Returns the EXACT optimum when it is <= budget; otherwise returns a valid
    // lower bound on the optimum that is strictly greater than budget (a "fail"). The budget is
    // threaded DOWN to children -- each child must come in at <= bestWorstCase - 2 for the current
    // group to still beat the incumbent -- which is exactly what lets a global budget prune deep
    // nodes. The exact-step and best-group-pattern caches are written ONLY on exact resolution, so
    // the materialized strategy tree is byte-identical to the unbounded search (same first-priority
    // optimal group per state); failed passes only deposit a learned lower bound in a separate memo.
    private int GetMinWorstCaseStepsBounded(ComparisonState state, int remainingSlots, int budget, int depth)
    {
        return SearchBounds.GetMinWorstCaseStepsBounded(state, remainingSlots, budget, depth);
    }

    private int GetMinWorstCaseLowerBound(ComparisonState state, int remainingSlots)
    {
        return SearchBounds.GetMinWorstCaseLowerBound(state, remainingSlots);
    }

    // Antichain/width lower bound. In a normalized active state every active item is undecided
    // (elimination removed items with >= remainingSlots active ancestors; NormalizeState removed
    // guaranteed-top items), so the active poset IS the undecided poset. Let w be the width of the
    // active poset (size of its maximum antichain). A single comparison step totally orders at most
    // _m items into a chain, so it can collapse at most _m mutually-incomparable items into a single
    // chain; hence the maximum antichain shrinks by at most _m - 1 per step. To reach a determined
    // state (width 1) from width w therefore needs at least ceil((w - 1) / (_m - 1)) further steps.
    // Width is computed via Dilworth/Koenig: w = ActiveCount - (maximum matching in the strict
    // comparability bipartite graph). Fresh items (the coverage bound) are a special case -- they
    // form an antichain, so this dominates coverage. Soundness is validated empirically by the
    // 229-case MaxStep/edge-invariance regression oracle: an unsound bound would prune an optimal
    // branch and raise some case's MaxStep.
    private int GetAntichainLowerBound(ComparisonState state)
    {
        if (_m <= 1)
            return 0;

        int width = GetActivePosetWidth(state);
        if (width <= 1)
            return 0;

        return (width - 1 + (_m - 1) - 1) / (_m - 1);
    }

    // Maximum antichain width of the active poset, via Dilworth's theorem (max antichain = minimum
    // chain cover) realized as ActiveCount - (maximum bipartite matching), where left/right copies
    // of each active item are joined when one strictly precedes the other (descendant relation).
    private int GetActivePosetWidth(ComparisonState state)
    {
        List<int> items = state.GetActiveItemsOrdered();
        int count = items.Count;
        if (count <= 1)
            return count;

        var index = new Dictionary<int, int>(count);
        for (int i = 0; i < count; i++)
            index[items[i]] = i;

        ulong activeMask = state.ActiveMask;
        var adjacency = new List<int>[count];
        for (int i = 0; i < count; i++)
        {
            ThrowIfCancellationRequested();
            var neighbours = new List<int>();
            ulong descendants = state.GetDescendantMask(items[i]) & activeMask;
            while (descendants != 0)
            {
                int item = BitOperations.TrailingZeroCount(descendants);
                descendants &= descendants - 1;
                neighbours.Add(index[item]);
            }

            adjacency[i] = neighbours;
        }

        var matchRight = new int[count];
        Array.Fill(matchRight, -1);
        int matching = 0;
        var visited = new bool[count];
        for (int left = 0; left < count; left++)
        {
            ThrowIfCancellationRequested();
            Array.Clear(visited, 0, count);
            if (TryAugmentMatching(left, adjacency, matchRight, visited))
                matching++;
        }

        return count - matching;
    }

    private bool TryAugmentMatching(int left, List<int>[] adjacency, int[] matchRight, bool[] visited)
    {
        foreach (int right in adjacency[left])
        {
            if (visited[right])
                continue;

            visited[right] = true;
            if (matchRight[right] == -1 || TryAugmentMatching(matchRight[right], adjacency, matchRight, visited))
            {
                matchRight[right] = left;
                return true;
            }
        }

        return false;
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

    private int GetInformationLowerBoundSteps(int feasibleTopSetCount, int activeCount)
    {
        if (feasibleTopSetCount <= 1)
            return 0;

        double logMaxOutcomesPerStep = 0d;
        int maxGroupSize = Math.Min(_m, activeCount);
        for (int i = 2; i <= maxGroupSize; i++)
        {
            ThrowIfCancellationRequested();
            logMaxOutcomesPerStep += Math.Log(i);
        }

        // Defensive: for valid configs m >= 2 and non-terminal states activeCount > m, so this should
        // always be positive. Keep a safe fallback if an invalid configuration slips through.
        if (logMaxOutcomesPerStep <= 0d)
            return int.MaxValue;

        double logFeasibleCount = Math.Log(feasibleTopSetCount);
        int steps = (int)Math.Floor(logFeasibleCount / logMaxOutcomesPerStep);
        if (steps < 0)
            steps = 0;

        // Advance until the threshold is reached. This stays conservative in edge cases and avoids
        // underestimating by one because of floating rounding noise.
        while ((steps * logMaxOutcomesPerStep) < logFeasibleCount)
            steps++;

        // Guard against tiny upward rounding at exact boundaries.
        while (steps > 0 && ((steps - 1) * logMaxOutcomesPerStep) >= logFeasibleCount)
            steps--;

        return steps;
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
