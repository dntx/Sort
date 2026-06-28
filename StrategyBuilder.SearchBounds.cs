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
        bool useIterativeDeepening = ForceIterativeDeepeningForTesting ?? _useIterativeDeepening;
        if (!useIterativeDeepening)
        {
            // Single-pass path: this completes in one go, so the exact result IS the proven lower
            // bound. Record only after solving so the shared lower-bound cache counters stay
            // byte-identical to the pre-squeeze algorithm (no extra GetMinWorstCaseLowerBound call).
            int exact = GetMinWorstCaseStepsExact(state, remainingSlots);
            if (_recordRootIncumbents)
                RecordRootProvenLowerBound(exact);
            return exact;
        }

        int budget = GetMinWorstCaseLowerBound(state, remainingSlots);
        while (true)
        {
            // Reaching a pass with this budget means every earlier pass proved no strategy
            // <= budget - 1 exists, so `budget` is a PROVEN lower bound on the root optimum.
            if (_recordRootIncumbents)
                RecordRootProvenLowerBound(budget);
            int result = GetMinWorstCaseStepsBounded(state, remainingSlots, budget, depth: 0);
            if (result <= budget)
            {
                if (_recordRootIncumbents)
                    RecordRootProvenLowerBound(result);
                return result;
            }
            budget = result;       // failed: jump the budget up to the learned lower bound and retry
        }
    }

    // Single-pass exact minimax (the pre-ID algorithm). Computes each child's exact optimum with no
    // global budget threaded down, pruning only at the outcome level against the current incumbent.
    // Used outside the iterative-deepening regime; kept byte-identical to preserve the established
    // search-statistics baselines for shallow/wide shapes.
    private int GetMinWorstCaseStepsExact(ComparisonState state, int remainingSlots)
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

        DominanceProbeResult dominanceProbe = default;
        bool dominanceProbed = false;
        if (EnableDominanceMetric && state.ActiveCount > _m && remainingSlots > 0)
        {
            dominanceProbe = ProbeDominance(state, remainingSlots);
            dominanceProbed = true;
        }

        bool isRootSearch = false;
        if (_recordRootIncumbents && !_rootSearchInitialized)
        {
            _rootSearchInitialized = true;
            isRootSearch = true;
        }

        EnterSearchState();

        var candidates = state.GetActiveItemsOrdered();
        int groupSize = Math.Min(_m, candidates.Count);
        IEnumerable<List<int>> groups =
            EnumeratePrioritizedGroups(state, remainingSlots, candidates, groupSize);
        List<int>? bestGroup = null;
        int bestWorstCase = int.MaxValue;
        int stateLowerBound = GetMinWorstCaseLowerBound(state, remainingSlots);
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

                        int branchSteps = 1 + GetMinWorstCaseStepsExact(outcome.NextState, outcome.NextRemainingSlots);
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

                    if (bestWorstCase <= stateLowerBound)
                        break;
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
            _bestGroupPatternCache[key] = MakeGroupPattern(state, bestGroup);

        _minWorstCaseStepsCache[key] = bestWorstCase;

        if (EnableDominanceMetric && dominanceProbed)
            RecordDominanceProbe(dominanceProbe, bestWorstCase, state, remainingSlots);
        AddDominanceLibraryEntry(state, remainingSlots, bestWorstCase);

        return bestWorstCase;
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
            return cached;                                    // exact, independent of budget
        }

        // Entry prune: a known lower bound (analytic, or learned from a prior failed pass) that
        // already exceeds the budget resolves this node as a fail with no search.
        int analyticLowerBound = GetMinWorstCaseLowerBound(state, remainingSlots);
        int knownLowerBound = analyticLowerBound;
        if (_searchLowerBoundCache.TryGetValue(key, out int learned) && learned > knownLowerBound)
            knownLowerBound = learned;
        if (knownLowerBound > budget)
            return knownLowerBound;

        DominanceProbeResult dominanceProbe = default;
        bool dominanceProbed = false;
        if (EnableDominanceMetric && state.ActiveCount > _m && remainingSlots > 0)
        {
            dominanceProbe = ProbeDominance(state, remainingSlots);
            dominanceProbed = true;
        }

        EnterSearchState();

        var candidates = state.GetActiveItemsOrdered();
        int groupSize = Math.Min(_m, candidates.Count);
        // Always rank candidate groups with the independent/symmetric heuristic, including at
        // early nodes where remainingSlots < _m (e.g. before any winner is fixed). This makes the
        // displayed optimal strategy prefer regular, fresh groupings (such as sorting an untouched
        // block) over equally-optimal but less intuitive mixed groups.
        IEnumerable<List<int>> groups =
            EnumeratePrioritizedGroups(state, remainingSlots, candidates, groupSize);
        List<int>? bestGroup = null;
        int bestWorstCase = budget + 1;                      // fail sentinel: nothing <= budget yet
        int failSoftBound = int.MaxValue;                    // min over non-improving groups' bounds
        bool anyUseful = false;
        // The state's own lower bound is a proven floor on the optimum. Once a candidate group
        // achieves it, no remaining group can do better, so we stop scoring the rest. This is
        // behaviour-preserving for the chosen group: when the lower bound is tight the first group
        // in priority order to reach the minimum is selected either way (later equal groups never
        // replace it), and when it is not tight the break never fires.
        int stateLowerBound = analyticLowerBound;
        try
        {
            ThrowIfCancellationRequested();
            foreach (var group in groups)
            {
                ThrowIfCancellationRequested();
                int groupWorstCase = 0;
                int childBudget = bestWorstCase - 2;         // child must be <= this to stay viable
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
                            groupWorstCase = Math.Max(groupWorstCase, branchLowerBound);
                            return false;
                        }

                        int childResult = GetMinWorstCaseStepsBounded(
                            outcome.NextState, outcome.NextRemainingSlots, childBudget, depth + 1);
                        int branchSteps = 1 + childResult;
                        groupWorstCase = Math.Max(groupWorstCase, branchSteps);
                        return groupWorstCase < bestWorstCase;
                    });

                if (!traversal.IsUseful)
                    continue;

                anyUseful = true;
                if (groupWorstCase < bestWorstCase)
                {
                    int previousBestWorstCase = bestWorstCase;
                    bestWorstCase = groupWorstCase;
                    bestGroup = group;
                    if (depth == 0 && _recordRootIncumbents && bestWorstCase < previousBestWorstCase)
                        RecordRootIncumbent(bestWorstCase, group);

                    if (bestWorstCase <= stateLowerBound)
                        break;
                }
                else
                {
                    failSoftBound = Math.Min(failSoftBound, groupWorstCase);
                }
            }
        }
        finally
        {
            ExitSearchState();
        }

        if (!anyUseful)
            throw new InvalidOperationException("Expected at least one useful comparison group when unresolved candidates exceed comparison size.");

        if (bestWorstCase <= budget)
        {
            // Resolved exactly under the budget: commit the optimum and its first-priority optimal
            // group so the materialized tree matches the unbounded search.
            if (bestGroup is not null)
                _bestGroupPatternCache[key] = MakeGroupPattern(state, bestGroup);

            _minWorstCaseStepsCache[key] = bestWorstCase;

            if (EnableDominanceMetric && dominanceProbed)
                RecordDominanceProbe(dominanceProbe, bestWorstCase, state, remainingSlots);
            AddDominanceLibraryEntry(state, remainingSlots, bestWorstCase);

            return bestWorstCase;
        }

        // Budget failed: every group's worst case exceeds the budget. failSoftBound is the smallest
        // such lower bound across groups -- a valid (and often tighter than budget + 1) lower bound
        // on the optimum -- which both jumps the next iterative-deepening threshold and is memoized.
        int failBound = failSoftBound == int.MaxValue ? bestWorstCase : failSoftBound;
        if (failBound <= budget)
            failBound = budget + 1;
        if (!_searchLowerBoundCache.TryGetValue(key, out int prior) || failBound > prior)
            _searchLowerBoundCache[key] = failBound;

        return failBound;
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

        steps = Math.Max(steps, GetAntichainLowerBound(state));
        steps = ApplyDominanceLowerBound(state, remainingSlots, steps);

        _lowerBoundStepsCache[key] = steps;
        return steps;
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
