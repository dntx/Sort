using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;

partial class StrategyBuilder
{
    private const int ProgressReportIntervalMs = 100;
    private readonly int _n;
    private readonly int _m;
    private readonly int _k;
    private readonly CancellationToken _cancellationToken;
    private readonly Action<SearchProgressSnapshot>? _progressCallback;
    private readonly Dictionary<IntSequenceKey, int> _stateIds = new();
    private readonly HashSet<IntSequenceKey> _expandedStates = new();
    private readonly HashSet<SearchStateKey> _visitedSearchStates = new();
    private readonly Dictionary<SearchStateKey, int> _minWorstCaseStepsCache = new();
    private readonly Dictionary<SearchStateKey, int> _lowerBoundStepsCache = new();
    private readonly Dictionary<SearchStateKey, FeasibleTopSetInfo> _feasibleTopSetCache = new();
    private readonly Dictionary<SearchStateKey, BestGroupPattern> _bestGroupPatternCache = new();
    private readonly Stopwatch _progressStopwatch = Stopwatch.StartNew();
    private int _nextStateId = 1;
    private int _searchedStates;
    private int _pendingStates;
    private int _peakPendingStates;
    private long _lastProgressReportMs = -ProgressReportIntervalMs;

    public StrategyBuilder(int n, int m, int k, CancellationToken cancellationToken = default, Action<SearchProgressSnapshot>? progressCallback = null)
    {
        _n = n;
        _m = m;
        _k = k;
        _cancellationToken = cancellationToken;
        _progressCallback = progressCallback;
    }

    public StrategyPlan Build()
    {
        var stopwatch = Stopwatch.StartNew();
        ReportProgress(force: true);
        var initial = new ComparisonState(_n);
        var root = BuildState(initial, 0, _k, 1);
        stopwatch.Stop();
        ReportProgress(force: true);
        return new StrategyPlan(_n, _m, _k, root, stopwatch.Elapsed, CreateSearchStatistics());
    }

    public static StrategyPlan Generate(int n, int m, int k)
    {
        return new StrategyBuilder(n, m, k).Build();
    }

    public static StrategyPlan Generate(int n, int m, int k, CancellationToken cancellationToken)
    {
        return new StrategyBuilder(n, m, k, cancellationToken).Build();
    }

    public static StrategyPlan Generate(int n, int m, int k, CancellationToken cancellationToken, Action<SearchProgressSnapshot> progressCallback)
    {
        return new StrategyBuilder(n, m, k, cancellationToken, progressCallback).Build();
    }

    private StrategyNode BuildState(ComparisonState state, ulong fixedTopMask, int remainingSlots, int step)
    {
        ThrowIfCancellationRequested();
        NormalizeState(state, ref fixedTopMask, ref remainingSlots);
        ObserveSearchState(state, remainingSlots);

        IntSequenceKey displayKey = GetDisplayStateKey(state, fixedTopMask);
        int stateId = GetStateId(displayKey);

        if (remainingSlots == 0)
            return StrategyNode.Terminal(stateId, ComparisonState.MaskToOrderedList(fixedTopMask));

        if (TryGetDeterminedTopSet(state, remainingSlots, out ulong determinedTopMask))
            return StrategyNode.Terminal(stateId, ComparisonState.MaskToOrderedList(fixedTopMask | determinedTopMask));

        if (state.ActiveCount <= remainingSlots)
            return StrategyNode.Terminal(stateId, ComparisonState.MaskToOrderedList(fixedTopMask | state.ActiveMask));

        var possibleCandidates = GetPossibleCandidates(state);
        if (state.ActiveCount <= _m)
        {
            return StrategyNode.Decision(
                stateId,
                step,
                possibleCandidates,
                Array.Empty<StrategyBranch>(),
                new FinalChoiceSummary(
                    ComparisonState.MaskToOrderedList(fixedTopMask),
                    possibleCandidates,
                    remainingSlots));
        }

        if (_expandedStates.Contains(displayKey))
            return StrategyNode.Reference(stateId);

        _expandedStates.Add(displayKey);

        ChosenGroupResult chosenGroup = ChooseGroup(state, remainingSlots);
        var branches = BuildBranches(state, fixedTopMask, remainingSlots, chosenGroup, step + 1);

        return StrategyNode.Decision(stateId, step, chosenGroup.Group, branches);
    }

    private List<StrategyBranch> BuildBranches(ComparisonState state, ulong fixedTopMask, int remainingSlots, ChosenGroupResult chosenGroup, int nextStep)
    {
        ThrowIfCancellationRequested();

        var groupedBranches = new Dictionary<IntSequenceKey, BranchInfo>();
        foreach (GroupTransition transition in chosenGroup.Transitions)
        {
            ThrowIfCancellationRequested();
            ulong nextFixedTopMask = fixedTopMask | transition.AddedFixedTopMask;
            ComparisonState next = transition.NextState;

            IntSequenceKey nextKey = GetDisplayStateKey(next, nextFixedTopMask);
            if (!groupedBranches.TryGetValue(nextKey, out BranchInfo? branch))
            {
                groupedBranches[nextKey] = new BranchInfo(next, nextFixedTopMask, transition.NextRemainingSlots, transition.OrderFamily);
            }
            else
            {
                branch.OrderFamilies.Add(transition.OrderFamily);
            }
        }

        return groupedBranches.Values
            .OrderBy(v => v.RepresentativeOrder, StringComparer.Ordinal)
            .Select(v => new StrategyBranch(
                v.RepresentativeOrder,
                BuildEquivalentOrderSummary(v.OrderFamilies),
                BuildComparisonEffect(state, fixedTopMask, v.NextState, v.NextFixedTopMask),
                BuildState(v.NextState, v.NextFixedTopMask, v.NextRemainingSlots, nextStep)))
            .ToList();
    }

    private StrategyEffect BuildComparisonEffect(ComparisonState before, ulong beforeFixedTopMask, ComparisonState after, ulong afterFixedTopMask)
    {
        var newlyGuaranteedTop = ComparisonState.MaskToOrderedList(afterFixedTopMask & ~beforeFixedTopMask);
        var newlyExcluded = ComparisonState.MaskToOrderedList(before.ActiveMask & ~after.ActiveMask & ~afterFixedTopMask);
        var fixedCandidates = ComparisonState.MaskToOrderedList(afterFixedTopMask);
        var possibleCandidates = after.GetActiveItemsOrdered();

        return new StrategyEffect(newlyGuaranteedTop, newlyExcluded, fixedCandidates, possibleCandidates);
    }

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

    private List<int> GetPossibleCandidates(ComparisonState state)
    {
        return state.GetActiveItemsOrdered();
    }

    private ChosenGroupResult ChooseGroup(ComparisonState state, int remainingSlots)
    {
        ThrowIfCancellationRequested();
        var candidates = state.GetActiveItemsOrdered();
        int groupSize = Math.Min(_m, candidates.Count);
        SearchStateKey currentKey = GetSearchStateKey(state, remainingSlots);
        var labels = state.GetStructuralLabels();

        if (_bestGroupPatternCache.TryGetValue(currentKey, out BestGroupPattern cachedPattern))
        {
            foreach (var group in EnumerateCombinations(candidates, cachedPattern.GroupSize))
            {
                if (GetGroupPattern(group, labels) == cachedPattern.Pattern)
                    return new ChosenGroupResult(group, BuildAllGroupTransitions(state, remainingSlots, group));
            }
        }

        List<int>? bestGroup = null;
        IReadOnlyList<GroupTransition>? bestTransitions = null;
        (int negWorstCaseSteps, int negFreshItems, int negUnrelatedScore, int negGroupSize, int distinctStates, int totalReduction, int unresolvedPairs) bestScore =
            (int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue);

        ThrowIfCancellationRequested();
        // Under the current cost model, a size-m comparison weakly dominates any smaller
        // non-terminal comparison because it costs the same one step and reveals a superset
        // of ordering information.
        var seenGroupPatterns = new HashSet<IntSequenceKey>();
        foreach (var group in EnumerateCombinations(candidates, groupSize))
        {
            ThrowIfCancellationRequested();
            if (!seenGroupPatterns.Add(GetGroupPattern(group, labels)))
                continue;

            var nextStateKeys = new HashSet<SearchStateKey>();
            int worstCaseSteps = 0;
            int totalReduction = 0;
            bool isUseful = false;
            int bestKnownWorstCase = bestGroup is null ? int.MaxValue : -bestScore.negWorstCaseSteps;
            var transitions = new List<GroupTransition>();

            foreach (OrderFamilyDescriptor orderFamily in EnumerateFeasibleOrderFamilies(state, group))
            {
                ThrowIfCancellationRequested();
                ComparisonState next = state.Clone();
                next.ApplyOrder(orderFamily.RepresentativeOrderItems);
                next.Eliminate(remainingSlots);

                ulong addedFixedTopMask = 0;
                int nextRemainingSlots = remainingSlots;
                NormalizeState(next, ref addedFixedTopMask, ref nextRemainingSlots);

                var transition = new GroupTransition(
                    next,
                    addedFixedTopMask,
                    nextRemainingSlots,
                    GetSearchStateKey(next, nextRemainingSlots),
                    state.ActiveCount - next.ActiveCount,
                    orderFamily);
                transitions.Add(transition);

                SearchStateKey nextKey = transition.NextSearchKey;
                if (nextKey.Equals(currentKey))
                    continue;

                isUseful = true;
                totalReduction += transition.Reduction;
                nextStateKeys.Add(nextKey);

                int branchLowerBound = 1 + GetMinWorstCaseLowerBound(transition.NextState, transition.NextRemainingSlots);
                if (branchLowerBound > bestKnownWorstCase)
                {
                    worstCaseSteps = branchLowerBound;
                    break;
                }

                int branchSteps = 1 + GetMinWorstCaseSteps(transition.NextState, transition.NextRemainingSlots);
                worstCaseSteps = Math.Max(worstCaseSteps, branchSteps);
            }

            if (!isUseful)
                continue;

            int freshItems = group.Count(i => state.GetAncestorCount(i) == 0 && state.GetDescendantCount(i) == 0);
            int unrelatedScore = -group.Sum(i => state.GetAncestorCount(i) + state.GetDescendantCount(i));
            int unresolvedPairs = CountUnresolvedPairs(state, group);
            var score = (-worstCaseSteps, freshItems, unrelatedScore, group.Count, nextStateKeys.Count, totalReduction, unresolvedPairs);

            if (bestGroup is null || score.CompareTo(bestScore) > 0)
            {
                bestGroup = group;
                bestTransitions = transitions;
                bestScore = score;
            }
        }

        if (bestGroup is not null && bestTransitions is not null)
        {
            _bestGroupPatternCache[currentKey] = new BestGroupPattern(bestGroup.Count, GetGroupPattern(bestGroup, labels));
            return new ChosenGroupResult(bestGroup, bestTransitions);
        }

        List<int> fallbackGroup = candidates.Take(groupSize).ToList();
        return new ChosenGroupResult(fallbackGroup, BuildAllGroupTransitions(state, remainingSlots, fallbackGroup));
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
            var seenGroupPatterns = new HashSet<IntSequenceKey>();
            foreach (var group in EnumerateCombinations(candidates, groupSize))
            {
                ThrowIfCancellationRequested();
                if (!seenGroupPatterns.Add(GetGroupPattern(group, labels)))
                    continue;

                int groupWorstCase = 0;
                bool isUseful = false;

                foreach (var orderFamily in EnumerateFeasibleOrderFamilies(state, group))
                {
                    ThrowIfCancellationRequested();
                    var next = state.Clone();
                    next.ApplyOrder(orderFamily.RepresentativeOrderItems);
                    next.Eliminate(remainingSlots);

                    ulong nextIgnoredFixedTopMask = 0;
                    int nextRemainingSlots = remainingSlots;
                    NormalizeState(next, ref nextIgnoredFixedTopMask, ref nextRemainingSlots);

                    SearchStateKey nextKey = GetSearchStateKey(next, nextRemainingSlots);
                    if (nextKey.Equals(key))
                        continue;

                    isUseful = true;
                    int branchLowerBound = 1 + GetMinWorstCaseLowerBound(next, nextRemainingSlots);
                    if (branchLowerBound >= bestWorstCase)
                    {
                        groupWorstCase = branchLowerBound;
                        break;
                    }

                    int branchSteps = 1 + GetMinWorstCaseSteps(next, nextRemainingSlots);
                    groupWorstCase = Math.Max(groupWorstCase, branchSteps);

                    if (groupWorstCase >= bestWorstCase)
                        break;
                }

                if (isUseful)
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

    private IReadOnlyList<GroupTransition> BuildAllGroupTransitions(ComparisonState state, int remainingSlots, IReadOnlyList<int> group)
    {
        ThrowIfCancellationRequested();
        var transitions = new List<GroupTransition>();
        foreach (OrderFamilyDescriptor orderFamily in EnumerateFeasibleOrderFamilies(state, group))
        {
            ThrowIfCancellationRequested();
            ComparisonState next = state.Clone();
            next.ApplyOrder(orderFamily.RepresentativeOrderItems);
            next.Eliminate(remainingSlots);

            ulong addedFixedTopMask = 0;
            int nextRemainingSlots = remainingSlots;
            NormalizeState(next, ref addedFixedTopMask, ref nextRemainingSlots);

            transitions.Add(new GroupTransition(
                next,
                addedFixedTopMask,
                nextRemainingSlots,
                GetSearchStateKey(next, nextRemainingSlots),
                state.ActiveCount - next.ActiveCount,
                orderFamily));
        }

        return transitions;
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

    private static IntSequenceKey GetGroupPattern(IReadOnlyList<int> group, IReadOnlyList<int> labels)
    {
        return new IntSequenceKey(group.Select(i => labels[i]).OrderBy(x => x).ToArray());
    }

    private static int CountUnresolvedPairs(ComparisonState state, IReadOnlyList<int> group)
    {
        int count = 0;
        for (int i = 0; i < group.Count - 1; i++)
        {
            for (int j = i + 1; j < group.Count; j++)
            {
                int a = group[i];
                int b = group[j];
                if (!state.HasAncestor(a, b) && !state.HasAncestor(b, a))
                    count++;
            }
        }

        return count;
    }

    private IEnumerable<List<int>> EnumerateCombinations(IReadOnlyList<int> items, int count)
    {
        ThrowIfCancellationRequested();
        var current = new List<int>(count);
        foreach (var combination in EnumerateCombinations(items, count, 0, current))
            yield return combination;
    }

    private IEnumerable<List<int>> EnumerateCombinations(
        IReadOnlyList<int> items,
        int count,
        int start,
        List<int> current)
    {
        ThrowIfCancellationRequested();
        if (current.Count == count)
        {
            yield return new List<int>(current);
            yield break;
        }

        for (int i = start; i <= items.Count - (count - current.Count); i++)
        {
            ThrowIfCancellationRequested();
            current.Add(items[i]);
            foreach (var combination in EnumerateCombinations(items, count, i + 1, current))
                yield return combination;
            current.RemoveAt(current.Count - 1);
        }
    }

    private int GetStateId(IntSequenceKey key)
    {
        ThrowIfCancellationRequested();
        if (_stateIds.TryGetValue(key, out int id))
            return id;

        id = _nextStateId++;
        _stateIds[key] = id;
        return id;
    }

    private SearchStateKey GetSearchStateKey(ComparisonState state, int remainingSlots)
    {
        return new SearchStateKey(remainingSlots, state.GetCanonicalKey());
    }

    private IntSequenceKey GetDisplayStateKey(ComparisonState state, ulong fixedTopMask)
    {
        return state.GetDisplayCanonicalKey(fixedTopMask);
    }

    private void NormalizeState(ComparisonState state, ref ulong fixedTopMask, ref int remainingSlots)
    {
        while (remainingSlots > 0)
        {
            ulong guaranteedTopMask = GetGuaranteedTopMask(state, remainingSlots);
            if (guaranteedTopMask == 0)
                break;

            fixedTopMask |= guaranteedTopMask;
            remainingSlots -= BitOperations.PopCount(guaranteedTopMask);
            state.Deactivate(guaranteedTopMask);
        }
    }

    private static string FormatOrder(IEnumerable<int> items)
    {
        return string.Join(" > ", items.Select(i => $"#{i + 1}"));
    }

    private void EnterSearchState()
    {
        _pendingStates++;
        _peakPendingStates = Math.Max(_peakPendingStates, _pendingStates);
        ReportProgress();
    }

    private void ExitSearchState()
    {
        _pendingStates--;
        ReportProgress();
    }

    private SearchStatistics CreateSearchStatistics()
    {
        _searchedStates = _visitedSearchStates.Count;
        return new SearchStatistics(
            _searchedStates,
            _pendingStates,
            _peakPendingStates,
            _stateIds.Count,
            _expandedStates.Count,
            _lowerBoundStepsCache.Count,
            _feasibleTopSetCache.Count);
    }

    private void ReportProgress(bool force = false)
    {
        if (_progressCallback is null)
            return;

        _searchedStates = _visitedSearchStates.Count;
        long elapsedMs = _progressStopwatch.ElapsedMilliseconds;
        if (!force && elapsedMs - _lastProgressReportMs < ProgressReportIntervalMs)
            return;

        _lastProgressReportMs = elapsedMs;
        _progressCallback(new SearchProgressSnapshot(
            _searchedStates,
            _pendingStates,
            _peakPendingStates,
            _stateIds.Count));
    }

    private void ObserveSearchState(ComparisonState state, int remainingSlots)
    {
        _visitedSearchStates.Add(GetSearchStateKey(state, remainingSlots));
    }

    private void ThrowIfCancellationRequested()
    {
        _cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class BranchInfo
    {
        public ComparisonState NextState { get; }
        public ulong NextFixedTopMask { get; }
        public int NextRemainingSlots { get; }
        public string RepresentativeOrder { get; }
        public List<OrderFamilyDescriptor> OrderFamilies { get; }

        public BranchInfo(ComparisonState nextState, ulong nextFixedTopMask, int nextRemainingSlots, OrderFamilyDescriptor representativeFamily)
        {
            NextState = nextState;
            NextFixedTopMask = nextFixedTopMask;
            NextRemainingSlots = nextRemainingSlots;
            RepresentativeOrder = representativeFamily.RepresentativeOrder;
            OrderFamilies = new List<OrderFamilyDescriptor> { representativeFamily };
        }
    }

    private sealed class ChosenGroupResult
    {
        public ChosenGroupResult(IReadOnlyList<int> group, IReadOnlyList<GroupTransition> transitions)
        {
            Group = group;
            Transitions = transitions;
        }

        public IReadOnlyList<int> Group { get; }
        public IReadOnlyList<GroupTransition> Transitions { get; }
    }

    private sealed class GroupTransition
    {
        public GroupTransition(
            ComparisonState nextState,
            ulong addedFixedTopMask,
            int nextRemainingSlots,
            SearchStateKey nextSearchKey,
            int reduction,
            OrderFamilyDescriptor orderFamily)
        {
            NextState = nextState;
            AddedFixedTopMask = addedFixedTopMask;
            NextRemainingSlots = nextRemainingSlots;
            NextSearchKey = nextSearchKey;
            Reduction = reduction;
            OrderFamily = orderFamily;
        }

        public ComparisonState NextState { get; }
        public ulong AddedFixedTopMask { get; }
        public int NextRemainingSlots { get; }
        public SearchStateKey NextSearchKey { get; }
        public int Reduction { get; }
        public OrderFamilyDescriptor OrderFamily { get; }
    }
}
