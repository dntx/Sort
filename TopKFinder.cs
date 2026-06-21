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
            return CompareByLowerWorstCaseSteps(other)
                ?? CompareByMoreFreshItems(other)
                ?? CompareByMoreUnrelatedItems(other)
                ?? CompareByLargerGroup(other)
                ?? CompareByMoreDistinctStates(other)
                ?? CompareByMoreTotalReduction(other)
                ?? CompareByMoreUnresolvedPairs(other)
                ?? 0;
        }

        private int? CompareByLowerWorstCaseSteps(ComparisonGroupScore other)
            => ComparePreference(other.WorstCaseSteps.CompareTo(WorstCaseSteps));

        private int? CompareByMoreFreshItems(ComparisonGroupScore other)
            => ComparePreference(FreshItems.CompareTo(other.FreshItems));

        private int? CompareByMoreUnrelatedItems(ComparisonGroupScore other)
            => ComparePreference(UnrelatedScore.CompareTo(other.UnrelatedScore));

        private int? CompareByLargerGroup(ComparisonGroupScore other)
            => ComparePreference(GroupSize.CompareTo(other.GroupSize));

        private int? CompareByMoreDistinctStates(ComparisonGroupScore other)
            => ComparePreference(DistinctStates.CompareTo(other.DistinctStates));

        private int? CompareByMoreTotalReduction(ComparisonGroupScore other)
            => ComparePreference(TotalReduction.CompareTo(other.TotalReduction));

        private int? CompareByMoreUnresolvedPairs(ComparisonGroupScore other)
            => ComparePreference(UnresolvedPairs.CompareTo(other.UnresolvedPairs));

        private static int? ComparePreference(int comparison)
            => comparison == 0 ? null : comparison;
    }

}
