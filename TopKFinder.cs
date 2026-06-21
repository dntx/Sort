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

        SelectedComparisonGroup chosenGroup = ChooseGroup(state, remainingSlots);
        var branches = BuildBranches(state, fixedTopMask, remainingSlots, chosenGroup, step + 1);

        return StrategyNode.Decision(stateId, step, chosenGroup.Group, branches);
    }

    private List<StrategyBranch> BuildBranches(ComparisonState state, ulong fixedTopMask, int remainingSlots, SelectedComparisonGroup chosenGroup, int nextStep)
    {
        ThrowIfCancellationRequested();

        var groupedBranches = new Dictionary<IntSequenceKey, MergedBranch>();
        foreach (ComparisonOutcome outcome in chosenGroup.Outcomes)
        {
            ThrowIfCancellationRequested();
            ulong nextFixedTopMask = fixedTopMask | outcome.AddedFixedTopMask;
            ComparisonState next = outcome.NextState;

            IntSequenceKey nextKey = GetDisplayStateKey(next, nextFixedTopMask);
            if (!groupedBranches.TryGetValue(nextKey, out MergedBranch? branch))
            {
                groupedBranches[nextKey] = new MergedBranch(next, nextFixedTopMask, outcome.NextRemainingSlots, outcome.OrderFamily);
            }
            else
            {
                branch.OrderFamilies.Add(outcome.OrderFamily);
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

    private List<int> GetPossibleCandidates(ComparisonState state)
    {
        return state.GetActiveItemsOrdered();
    }

    private SelectedComparisonGroup ChooseGroup(ComparisonState state, int remainingSlots)
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
                    return new SelectedComparisonGroup(group, BuildAllComparisonOutcomes(state, remainingSlots, group));
            }
        }

        List<int>? bestGroup = null;
        IReadOnlyList<ComparisonOutcome>? bestOutcomes = null;
        ComparisonGroupScore? bestScore = null;

        ThrowIfCancellationRequested();
        // Under the current cost model, a size-m comparison weakly dominates any smaller
        // non-terminal comparison because it costs the same one step and reveals a superset
        // of ordering information.
        foreach (var group in EnumerateDistinctGroups(candidates, groupSize, labels))
        {
            ThrowIfCancellationRequested();
            ComparisonGroupEvaluation? evaluation = EvaluateComparisonGroup(
                state,
                remainingSlots,
                group,
                currentKey,
                bestScore?.WorstCaseSteps ?? int.MaxValue);
            if (evaluation is null)
                continue;

            if (bestScore is null || evaluation.Score.CompareTo(bestScore.Value) > 0)
            {
                bestGroup = group;
                bestOutcomes = evaluation.Outcomes;
                bestScore = evaluation.Score;
            }
        }

        if (bestGroup is not null && bestOutcomes is not null)
        {
            _bestGroupPatternCache[currentKey] = new BestGroupPattern(bestGroup.Count, GetGroupPattern(bestGroup, labels));
            return new SelectedComparisonGroup(bestGroup, bestOutcomes);
        }

        List<int> fallbackGroup = candidates.Take(groupSize).ToList();
        return new SelectedComparisonGroup(fallbackGroup, BuildAllComparisonOutcomes(state, remainingSlots, fallbackGroup));
    }

    private ComparisonGroupEvaluation? EvaluateComparisonGroup(
        ComparisonState state,
        int remainingSlots,
        IReadOnlyList<int> group,
        SearchStateKey currentKey,
        int bestKnownWorstCase)
    {
        int worstCaseSteps = 0;
        OutcomeTraversalSummary traversal = VisitComparisonOutcomes(
            state,
            remainingSlots,
            group,
            currentKey,
            collectAllOutcomes: true,
            onUsefulOutcome: outcome =>
            {
                int branchLowerBound = 1 + GetMinWorstCaseLowerBound(outcome.NextState, outcome.NextRemainingSlots);
                if (branchLowerBound > bestKnownWorstCase)
                {
                    worstCaseSteps = branchLowerBound;
                    return false;
                }

                int branchSteps = 1 + GetMinWorstCaseSteps(outcome.NextState, outcome.NextRemainingSlots);
                worstCaseSteps = Math.Max(worstCaseSteps, branchSteps);
                return true;
            });

        if (!traversal.IsUseful)
            return null;

        ComparisonGroupScore score = BuildComparisonGroupScore(
            state,
            group,
            worstCaseSteps,
            traversal.DistinctNextStateCount,
            traversal.TotalReduction);
        return new ComparisonGroupEvaluation(traversal.AllOutcomes, score);
    }

    private IReadOnlyList<ComparisonOutcome> BuildAllComparisonOutcomes(ComparisonState state, int remainingSlots, IReadOnlyList<int> group)
    {
        return VisitComparisonOutcomes(
            state,
            remainingSlots,
            group,
            currentKey: null,
            collectAllOutcomes: true,
            onUsefulOutcome: _ => true).AllOutcomes;
    }

    private ComparisonOutcome CreateComparisonOutcome(ComparisonState state, int remainingSlots, OrderFamilyDescriptor orderFamily)
    {
        ComparisonState next = state.Clone();
        next.ApplyOrder(orderFamily.RepresentativeOrderItems);
        next.Eliminate(remainingSlots);

        ulong addedFixedTopMask = 0;
        int nextRemainingSlots = remainingSlots;
        NormalizeState(next, ref addedFixedTopMask, ref nextRemainingSlots);

        return new ComparisonOutcome(
            next,
            addedFixedTopMask,
            nextRemainingSlots,
            GetSearchStateKey(next, nextRemainingSlots),
            state.ActiveCount - next.ActiveCount,
            orderFamily);
    }

    private OutcomeTraversalSummary VisitComparisonOutcomes(
        ComparisonState state,
        int remainingSlots,
        IReadOnlyList<int> group,
        SearchStateKey? currentKey,
        bool collectAllOutcomes,
        Func<ComparisonOutcome, bool> onUsefulOutcome)
    {
        ThrowIfCancellationRequested();
        var nextStateKeys = new HashSet<SearchStateKey>();
        var allOutcomes = collectAllOutcomes ? new List<ComparisonOutcome>() : null;
        int totalReduction = 0;
        bool isUseful = false;

        foreach (OrderFamilyDescriptor orderFamily in EnumerateFeasibleOrderFamilies(state, group))
        {
            ThrowIfCancellationRequested();
            ComparisonOutcome outcome = CreateComparisonOutcome(state, remainingSlots, orderFamily);
            allOutcomes?.Add(outcome);

            if (currentKey is not null && outcome.NextSearchKey.Equals(currentKey.Value))
                continue;

            isUseful = true;
            totalReduction += outcome.Reduction;
            nextStateKeys.Add(outcome.NextSearchKey);

            if (!onUsefulOutcome(outcome))
                break;
        }

        return new OutcomeTraversalSummary(
            allOutcomes is not null ? allOutcomes : Array.Empty<ComparisonOutcome>(),
            isUseful,
            totalReduction,
            nextStateKeys.Count);
    }

    private static ComparisonGroupScore BuildComparisonGroupScore(
        ComparisonState state,
        IReadOnlyList<int> group,
        int worstCaseSteps,
        int distinctStates,
        int totalReduction)
    {
        return new ComparisonGroupScore(
            worstCaseSteps,
            CountFreshItems(state, group),
            CalculateUnrelatedScore(state, group),
            group.Count,
            distinctStates,
            totalReduction,
            CountUnresolvedPairs(state, group));
    }

    private static IntSequenceKey GetGroupPattern(IReadOnlyList<int> group, IReadOnlyList<int> labels)
    {
        return new IntSequenceKey(group.Select(i => labels[i]).OrderBy(x => x).ToArray());
    }

    private IEnumerable<List<int>> EnumerateDistinctGroups(
        IReadOnlyList<int> candidates,
        int groupSize,
        IReadOnlyList<int> labels)
    {
        var seenGroupPatterns = new HashSet<IntSequenceKey>();
        foreach (var group in EnumerateCombinations(candidates, groupSize))
        {
            ThrowIfCancellationRequested();
            if (seenGroupPatterns.Add(GetGroupPattern(group, labels)))
                yield return group;
        }
    }

    private static int CountFreshItems(ComparisonState state, IReadOnlyList<int> group)
    {
        return group.Count(i => state.GetAncestorCount(i) == 0 && state.GetDescendantCount(i) == 0);
    }

    private static int CalculateUnrelatedScore(ComparisonState state, IReadOnlyList<int> group)
    {
        return -group.Sum(i => state.GetAncestorCount(i) + state.GetDescendantCount(i));
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

    private sealed class MergedBranch
    {
        public ComparisonState NextState { get; }
        public ulong NextFixedTopMask { get; }
        public int NextRemainingSlots { get; }
        public string RepresentativeOrder { get; }
        public List<OrderFamilyDescriptor> OrderFamilies { get; }

        public MergedBranch(ComparisonState nextState, ulong nextFixedTopMask, int nextRemainingSlots, OrderFamilyDescriptor representativeFamily)
        {
            NextState = nextState;
            NextFixedTopMask = nextFixedTopMask;
            NextRemainingSlots = nextRemainingSlots;
            RepresentativeOrder = representativeFamily.RepresentativeOrder;
            OrderFamilies = new List<OrderFamilyDescriptor> { representativeFamily };
        }
    }

    private sealed class SelectedComparisonGroup
    {
        public SelectedComparisonGroup(IReadOnlyList<int> group, IReadOnlyList<ComparisonOutcome> outcomes)
        {
            Group = group;
            Outcomes = outcomes;
        }

        public IReadOnlyList<int> Group { get; }
        public IReadOnlyList<ComparisonOutcome> Outcomes { get; }
    }

    private sealed class ComparisonGroupEvaluation
    {
        public ComparisonGroupEvaluation(IReadOnlyList<ComparisonOutcome> outcomes, ComparisonGroupScore score)
        {
            Outcomes = outcomes;
            Score = score;
        }

        public IReadOnlyList<ComparisonOutcome> Outcomes { get; }
        public ComparisonGroupScore Score { get; }
    }

    private sealed class OutcomeTraversalSummary
    {
        public OutcomeTraversalSummary(
            IReadOnlyList<ComparisonOutcome> allOutcomes,
            bool isUseful,
            int totalReduction,
            int distinctNextStateCount)
        {
            AllOutcomes = allOutcomes;
            IsUseful = isUseful;
            TotalReduction = totalReduction;
            DistinctNextStateCount = distinctNextStateCount;
        }

        public IReadOnlyList<ComparisonOutcome> AllOutcomes { get; }
        public bool IsUseful { get; }
        public int TotalReduction { get; }
        public int DistinctNextStateCount { get; }
    }

    private readonly record struct ComparisonGroupScore(
        int WorstCaseSteps,
        int FreshItems,
        int UnrelatedScore,
        int GroupSize,
        int DistinctStates,
        int TotalReduction,
        int UnresolvedPairs) : IComparable<ComparisonGroupScore>
    {
        public int CompareTo(ComparisonGroupScore other)
        {
            int result = other.WorstCaseSteps.CompareTo(WorstCaseSteps);
            if (result != 0)
                return result;

            result = FreshItems.CompareTo(other.FreshItems);
            if (result != 0)
                return result;

            result = UnrelatedScore.CompareTo(other.UnrelatedScore);
            if (result != 0)
                return result;

            result = GroupSize.CompareTo(other.GroupSize);
            if (result != 0)
                return result;

            result = DistinctStates.CompareTo(other.DistinctStates);
            if (result != 0)
                return result;

            result = TotalReduction.CompareTo(other.TotalReduction);
            if (result != 0)
                return result;

            return UnresolvedPairs.CompareTo(other.UnresolvedPairs);
        }
    }

    private sealed class ComparisonOutcome
    {
        public ComparisonOutcome(
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
