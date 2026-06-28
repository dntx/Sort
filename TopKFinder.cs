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
    private readonly int _requestedK;
    private readonly int _k;
    private readonly CancellationToken _cancellationToken;
    private readonly Action<SearchProgressSnapshot>? _progressCallback;
    private readonly bool _reportCombinedRunProgress;
    // Iterative deepening (IDA*-style bounded minimax) is enabled only in the deep, large-k regime
    // where measurement shows the tight global budget prunes enough deep nodes to outweigh the
    // re-exploration cost of multiple passes. Shallow/wide shapes keep the single-pass exact search
    // (GetMinWorstCaseStepsExact), which is byte-identical to the pre-ID algorithm, so they never
    // pay the re-exploration overhead. The threshold is an empirical heuristic, not a soundness
    // boundary: both code paths return the same exact MaxStep optimum. They do NOT necessarily
    // materialize the same tree -- among equally-optimal groups the bounded path can break ties
    // differently, so a gated (5,5) case may yield a different (still MaxStep-optimal) tree than the
    // single-pass path. See docs/core-algorithm.md sec 4.3.
    private readonly bool _useIterativeDeepening;
    // Test-only override of the iterative-deepening gate. When non-null it forces the search path
    // regardless of the (m, k, n) heuristic, letting a regression test run the SAME case under both
    // paths and assert they reach the same MaxStep optimum while iterative deepening constructs
    // strictly fewer outcomes. Null in production.
    internal bool? ForceIterativeDeepeningForTesting { get; set; }
    private readonly Dictionary<IntSequenceKey, int> _stateIds = new();
    private readonly Dictionary<IntSequenceKey, ExpandedStateSnapshot> _expandedStates = new();
    private readonly HashSet<SearchStateKey> _visitedSearchStates = new();
    private readonly Dictionary<SearchStateKey, int> _minWorstCaseStepsCache = new();
    private readonly Dictionary<SearchStateKey, int> _lowerBoundStepsCache = new();
    // Iterative-deepening transposition memo: the best lower bound on a state's exact cost learned
    // from passes that failed to resolve it under their budget. Lets a later node/pass prune a state
    // immediately when this learned bound already exceeds the current budget.
    private readonly Dictionary<SearchStateKey, int> _searchLowerBoundCache = new();
    private readonly Dictionary<SearchStateKey, FeasibleTopSetInfo> _feasibleTopSetCache = new();
    private readonly Dictionary<SearchStateKey, BestGroupPattern> _bestGroupPatternCache = new();
    private readonly Stopwatch _progressStopwatch = Stopwatch.StartNew();
    private readonly List<SearchMilestone> _rootIncumbents = new();
    private int _nextStateId = 1;
    private int _searchedStates;
    private int _pendingStates;
    private int _peakPendingStates;
    private long _lastProgressReportMs = -ProgressReportIntervalMs;
    private int _lowerBoundPrunes;
    private int _duplicateOutcomeSkips;
    private int _mergedOutcomeCollisions;
    private int _exactCacheHits;
    private int _lowerBoundCacheHits;
    private int _feasibleTopSetCacheHits;
    private int _bestGroupPatternCacheHits;
    private int _outcomesConstructed;
    private int _candidateGroupsEnumerated;
    private long _phase1Milliseconds;
    private long _phase1bMilliseconds;
    private long _phase2Milliseconds;
    // Set true only around the phase-1 iterative-deepening driver so root incumbents are recorded
    // for the progress UI; other callers (optimality-gap, compact) reuse the search silently.
    private bool _recordRootIncumbents;
    // First-top-level-entry latch for the single-pass exact search path (matches the pre-ID
    // algorithm): root incumbents are recorded only for the first (phase-1) search of a build.
    private bool _rootSearchInitialized;
    private bool _phase1Solved;
    private bool _phase1bSolved;
    private bool _progressEstimateInitialized;
    private double _progressEstimateEma01;
    private long _lastProgressSampleElapsedMs;
    private int _lastProgressSampleSearched;
    private bool _pendingCostEstimateInitialized;
    private double _pendingCostStatesPerPending;
    private double _pendingCostConservativeStatesPerPending;
    private int _pendingAtCostSample;
    private long _searchedSinceCostSample;
    private bool _searchRateEstimateInitialized;
    private double _searchRateStatesPerMs;
    private bool _pendingZeroSettling;
    private long _pendingZeroSinceMs;
    private int _pendingZeroSearchedAtStart;
    private ProgressScope _progressScope;

    public StrategyBuilder(
        int n,
        int m,
        int k,
        CancellationToken cancellationToken = default,
        Action<SearchProgressSnapshot>? progressCallback = null,
        bool reportCombinedRunProgress = false)
    {
        _n = n;
        _m = m;
        _requestedK = k;
        _k = k > n - k ? n - k : k;
        _useIterativeDeepening = _m >= 5 && _k >= 5 && _n >= 2 * _m;
        _cancellationToken = cancellationToken;
        _progressCallback = progressCallback;
        _reportCombinedRunProgress = reportCombinedRunProgress;
        _progressScope = ProgressScope.DefaultStandalone;
    }

    public StrategyPlan BuildDefaultPlan()
    {
        _progressScope = _reportCombinedRunProgress
            ? ProgressScope.DefaultInCombinedRun
            : ProgressScope.DefaultStandalone;
        return BuildPlan(useCompactSelection: false);
    }

    public StrategyPlan BuildCompactPlan()
    {
        _progressScope = _reportCombinedRunProgress
            ? ProgressScope.CompactPrimaryInCombinedRun
            : ProgressScope.DefaultStandalone;
        StrategyPlan compact = BuildPlan(useCompactSelection: true);

        // The compact DP minimizes a per-state edge proxy that sums each child subtree
        // independently; it does not model the materializer's display-key Reference
        // de-duplication (a state reached a second time renders as a zero-edge Reference
        // leaf). On rare states this proxy mismatch makes the compact selection render
        // MORE branch edges than the default selection (e.g. 10,4,8: 8 -> 10). The compact
        // pass must never be worse than default, so when it fails to strictly reduce the
        // materialized edge count, fall back to the default selection. Compact only ever
        // chooses among step-optimal groups, so MaxStep already matches default and the
        // edge count is the sole tie-breaker.
        _progressScope = _reportCombinedRunProgress
            ? ProgressScope.CompactFallbackInCombinedRun
            : ProgressScope.DefaultStandalone;
        StrategyPlan fallback = BuildPlan(useCompactSelection: false);
        return compact.TotalBranchEdges < fallback.TotalBranchEdges ? compact : fallback;
    }

    private StrategyPlan BuildPlan(bool useCompactSelection)
    {
        ResetPerBuildTransientState();
        var stopwatch = Stopwatch.StartNew();
        ReportProgress(force: true);

        EnsurePhase1Solved();
        _phase1Milliseconds = stopwatch.ElapsedMilliseconds;

        if (useCompactSelection)
            EnsureCompactSelectionSolved();
        _phase1bMilliseconds = stopwatch.ElapsedMilliseconds - _phase1Milliseconds;

        // Phase 2: materialize the strategy tree, reusing the cached group patterns.
        _useCompactSelection = useCompactSelection;
        var root = BuildState(new ComparisonState(_n), 0, _k, 1);
        _phase2Milliseconds = stopwatch.ElapsedMilliseconds - _phase1Milliseconds - _phase1bMilliseconds;
        stopwatch.Stop();
        ReportProgress(force: true);
        return new StrategyPlan(_n, _m, _requestedK, _k, root, stopwatch.Elapsed, CreateSearchStatistics());
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

        if (_expandedStates.TryGetValue(displayKey, out ExpandedStateSnapshot snapshot))
        {
            IReadOnlyList<ItemRelabel>? relabeling =
                snapshot.State.TryBuildDisplayRelabeling(snapshot.FixedTopMask, state, fixedTopMask);
            return StrategyNode.Reference(stateId, relabeling);
        }

        _expandedStates.Add(displayKey, new ExpandedStateSnapshot(state.Clone(), fixedTopMask));

        SelectedComparisonGroup chosenGroup = ChooseGroup(state, fixedTopMask, remainingSlots);
        var branches = BuildBranches(state, fixedTopMask, remainingSlots, chosenGroup, step + 1);

        return StrategyNode.Decision(stateId, step, chosenGroup.Group, branches);
    }

    private List<int> GetPossibleCandidates(ComparisonState state)
    {
        return state.GetActiveItemsOrdered();
    }

    private SelectedComparisonGroup ChooseGroup(ComparisonState state, ulong fixedTopMask, int remainingSlots)
    {
        ThrowIfCancellationRequested();
        var candidates = state.GetActiveItemsOrdered();
        SearchStateKey currentKey = GetSearchStateKey(state, remainingSlots);

        // Phase 1 solves the optimal worst-case for every reachable state and caches the
        // chosen comparison-group pattern, so phase 2 always finds a populated entry here.
        // The compact PoC overrides the choice with its size-minimizing pattern when enabled.
        BestGroupPattern cachedPattern;
        if (_useCompactSelection && _compactGroupPatternCache.TryGetValue(currentKey, out BestGroupPattern compactPattern))
        {
            cachedPattern = compactPattern;
        }
        else if (!_bestGroupPatternCache.TryGetValue(currentKey, out cachedPattern))
        {
            throw new InvalidOperationException(
                "Phase 1 must populate the best-group pattern cache for every state materialized in phase 2.");
        }

        foreach (var group in EnumerateCombinations(candidates, cachedPattern.GroupSize))
        {
            if (GetGroupPattern(state, group) == cachedPattern.Pattern)
            {
                _bestGroupPatternCacheHits++;
                return new SelectedComparisonGroup(group, BuildMergedComparisonOutcomes(state, fixedTopMask, remainingSlots, group));
            }
        }

        throw new InvalidOperationException(
            "Cached best-group pattern did not match any candidate combination in the current state.");
    }

    private IReadOnlyList<MergedBranch> BuildMergedComparisonOutcomes(ComparisonState state, ulong fixedTopMask, int remainingSlots, IReadOnlyList<int> group)
    {
        return VisitComparisonOutcomes(
            state,
            fixedTopMask,
            remainingSlots,
            group,
            currentKey: null,
            collectMergedBranches: true,
            onUsefulOutcome: _ => true).MergedBranches;
    }

    private static IntSequenceKey GetGroupPattern(ComparisonState state, IReadOnlyList<int> group)
    {
        ulong mask = 0;
        for (int i = 0; i < group.Count; i++)
            mask |= 1UL << group[i];
        return state.GetGroupCanonicalKey(mask);
    }

    private IEnumerable<List<int>> EnumerateDistinctGroups(
        ComparisonState state,
        IReadOnlyList<int> candidates,
        int groupSize)
    {
        // Exploit the active poset's automorphisms to avoid enumerating all C(active, groupSize)
        // combinations. Active items are partitioned into "free symmetry classes" (items with
        // identical active-restricted ancestor and descendant sets); every within-class permutation
        // is an automorphism, so all size-a selections from a class lie in one orbit and the class's
        // a smallest items canonically represent them. We therefore build a single candidate per
        // per-class count vector and canonically de-duplicate across classes, keeping the
        // lexicographically smallest member of each orbit. This produces exactly one representative
        // per orbit - identical to scanning every combination - but builds far fewer candidates on
        // symmetric states (e.g. a single candidate at the fully symmetric root instead of C(n, m)).
        List<List<int>> classes = state.GetFreeSymmetryClasses();

        var suffixCapacity = new int[classes.Count + 1];
        for (int c = classes.Count - 1; c >= 0; c--)
            suffixCapacity[c] = suffixCapacity[c + 1] + classes[c].Count;

        var collected = new List<List<int>>();
        var prefix = new List<int>(groupSize);
        GenerateClassRepresentatives(state, classes, suffixCapacity, 0, groupSize, prefix, collected);

        // Orbit de-duplication via a cheap pre-filter. The full group canonical key
        // (GetGroupPattern -> McKay) is the only sound way to merge two groups that lie in the same
        // automorphism orbit, but it is expensive and dominates the search cost. Color-refinement
        // structural labels are an automorphism invariant, so two groups in the same orbit always
        // share the same sorted multiset of member labels. We bucket groups by that cheap signature:
        // groups with distinct signatures are provably in different orbits and need no canonical key,
        // so McKay only runs to disambiguate groups that collide on the cheap signature.
        int[] labels = state.GetStructuralLabels();
        var buckets = new Dictionary<IntSequenceKey, List<List<int>>>();
        foreach (var group in collected)
        {
            IntSequenceKey cheap = CheapGroupSignature(labels, group);
            if (!buckets.TryGetValue(cheap, out List<List<int>>? bucket))
            {
                bucket = new List<List<int>>();
                buckets[cheap] = bucket;
            }

            bucket.Add(group);
        }

        var ordered = new List<List<int>>(buckets.Count);
        foreach (List<List<int>> bucket in buckets.Values)
        {
            if (bucket.Count == 1)
            {
                ordered.Add(bucket[0]);
                continue;
            }

            var representatives = new Dictionary<IntSequenceKey, List<int>>();
            foreach (List<int> group in bucket)
            {
                IntSequenceKey pattern = GetGroupPattern(state, group);
                if (!representatives.TryGetValue(pattern, out List<int>? existing) ||
                    CompareGroupsLexicographically(group, existing) < 0)
                {
                    representatives[pattern] = group;
                }
            }

            ordered.AddRange(representatives.Values);
        }

        ordered.Sort(CompareGroupsLexicographically);
        return ordered;
    }

    private static IntSequenceKey CheapGroupSignature(int[] labels, IReadOnlyList<int> group)
    {
        var values = new int[group.Count];
        for (int i = 0; i < group.Count; i++)
            values[i] = labels[group[i]];
        Array.Sort(values);
        return new IntSequenceKey(values);
    }

    private void GenerateClassRepresentatives(
        ComparisonState state,
        List<List<int>> classes,
        int[] suffixCapacity,
        int classIndex,
        int remaining,
        List<int> prefix,
        List<List<int>> collected)
    {
        if (remaining == 0)
        {
            ThrowIfCancellationRequested();
            _candidateGroupsEnumerated++;
            var group = new List<int>(prefix);
            group.Sort();
            collected.Add(group);
            return;
        }

        // Prune branches that can no longer reach the required group size.
        if (classIndex == classes.Count || suffixCapacity[classIndex] < remaining)
            return;

        List<int> cls = classes[classIndex];
        int maxTake = Math.Min(cls.Count, remaining);
        for (int take = 0; take <= maxTake; take++)
        {
            for (int j = 0; j < take; j++)
                prefix.Add(cls[j]);

            GenerateClassRepresentatives(
                state, classes, suffixCapacity, classIndex + 1, remaining - take, prefix, collected);

            prefix.RemoveRange(prefix.Count - take, take);
        }
    }

    private static int CompareGroupsLexicographically(List<int> a, List<int> b)
    {
        int min = Math.Min(a.Count, b.Count);
        for (int i = 0; i < min; i++)
        {
            int cmp = a[i].CompareTo(b[i]);
            if (cmp != 0)
                return cmp;
        }

        return a.Count.CompareTo(b.Count);
    }

    private IEnumerable<List<int>> EnumeratePrioritizedGroups(
        ComparisonState state,
        int remainingSlots,
        IReadOnlyList<int> candidates,
        int groupSize)
    {
        var scoredGroups = new List<(List<int> Group, HeuristicGroupScore Score)>();
        foreach (var group in EnumerateDistinctGroups(state, candidates, groupSize))
        {
            ThrowIfCancellationRequested();
            scoredGroups.Add((group, BuildHeuristicGroupScore(state, remainingSlots, group)));
        }

        foreach (var entry in scoredGroups.OrderByDescending(entry => entry.Score))
            yield return entry.Group;
    }

    private static HeuristicGroupScore BuildHeuristicGroupScore(ComparisonState state, int remainingSlots, IReadOnlyList<int> group)
    {
        int guaranteedTopHits = 0;
        for (int i = 0; i < group.Count; i++)
        {
            if (state.ActiveCount - 1 - state.GetDescendantCount(group[i]) <= remainingSlots - 1)
                guaranteedTopHits++;
        }

        return new HeuristicGroupScore(
            guaranteedTopHits,
            CountFreshItems(state, group),
            CalculateUnrelatedScore(state, group),
            CountUnresolvedPairs(state, group),
            group.Count);
    }

    private static int CountFreshItems(ComparisonState state, IReadOnlyList<int> group)
    {
        int count = 0;
        for (int i = 0; i < group.Count; i++)
        {
            int item = group[i];
            if (state.GetAncestorCount(item) == 0 && state.GetDescendantCount(item) == 0)
                count++;
        }

        return count;
    }

    private static int CalculateUnrelatedScore(ComparisonState state, IReadOnlyList<int> group)
    {
        int sum = 0;
        for (int i = 0; i < group.Count; i++)
        {
            int item = group[i];
            sum += state.GetAncestorCount(item) + state.GetDescendantCount(item);
        }

        return -sum;
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
            _feasibleTopSetCache.Count,
            new SearchDiagnostics(
                _rootIncumbents.ToArray(),
                _lowerBoundPrunes,
                _duplicateOutcomeSkips,
                _mergedOutcomeCollisions,
                _exactCacheHits,
                _lowerBoundCacheHits,
                _feasibleTopSetCacheHits,
                _bestGroupPatternCacheHits),
            _phase1Milliseconds,
            _phase1bMilliseconds,
            _phase2Milliseconds,
            _outcomesConstructed,
            _candidateGroupsEnumerated,
            _compactStatesSolved,
            _compactGroupsEnumerated,
            _compactStepOptimalGroups);
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
        (double localProgress01, long localRemainingMs) = EstimateProgress(elapsedMs);
        (double estimatedProgress01, long estimatedRemainingMs) =
            MapToReportedProgress(elapsedMs, localProgress01, localRemainingMs);
        _progressCallback(new SearchProgressSnapshot(
            elapsedMs,
            _searchedStates,
            _pendingStates,
            _peakPendingStates,
            _stateIds.Count,
            _rootIncumbents.Count == 0 ? null : _rootIncumbents[^1],
            _rootIncumbents.Count,
            _lowerBoundPrunes,
            _duplicateOutcomeSkips,
            _mergedOutcomeCollisions,
            _exactCacheHits,
            _lowerBoundCacheHits,
            _feasibleTopSetCacheHits,
            _bestGroupPatternCacheHits,
            _outcomesConstructed,
            _candidateGroupsEnumerated,
            _lowerBoundStepsCache.Count,
            _feasibleTopSetCache.Count,
            _compactStatesSolved,
            _compactGroupsEnumerated,
            _compactStepOptimalGroups,
            estimatedProgress01,
            estimatedRemainingMs));
    }

    private (double Progress01, long RemainingMs) MapToReportedProgress(long elapsedMs, double localProgress01, long localRemainingMs)
    {
        if (!_reportCombinedRunProgress)
            return (localProgress01, localRemainingMs);

        (double progressBase, double progressSpan) = _progressScope switch
        {
            ProgressScope.DefaultInCombinedRun => (0.0, 0.60),
            ProgressScope.CompactPrimaryInCombinedRun => (0.60, 0.39),
            ProgressScope.CompactFallbackInCombinedRun => (0.99, 0.01),
            _ => (0.0, 1.0),
        };

        double progress = Math.Clamp(progressBase + (Math.Clamp(localProgress01, 0.0, 1.0) * progressSpan), 0.0, 1.0);
        if (progress <= 0.0 || elapsedMs <= 0)
            return (progress, -1);

        long remaining = progress >= 1.0
            ? 0
            : Math.Max(0, (long)(elapsedMs * ((1.0 / progress) - 1.0)));
        return (progress, remaining);
    }

    private (double Progress01, long RemainingMs) EstimateProgress(long elapsedMs)
    {
        if (_searchedStates <= 0)
            return (0.0, -1);

        if (_lastProgressSampleElapsedMs >= 0)
        {
            int deltaSearched = Math.Max(0, _searchedStates - _lastProgressSampleSearched);
            long deltaElapsedMs = Math.Max(0, elapsedMs - _lastProgressSampleElapsedMs);
            if (deltaElapsedMs > 0 && deltaSearched > 0)
            {
                double observedSearchRate = deltaSearched / (double)deltaElapsedMs;
                if (!_searchRateEstimateInitialized)
                {
                    _searchRateEstimateInitialized = true;
                    _searchRateStatesPerMs = observedSearchRate;
                }
                else
                {
                    const double searchRateAlpha = 0.22;
                    _searchRateStatesPerMs += searchRateAlpha * (observedSearchRate - _searchRateStatesPerMs);
                }
            }

            if (_pendingAtCostSample < 0)
                _pendingAtCostSample = _pendingStates;

            _searchedSinceCostSample += deltaSearched;

            int consumedPending = _pendingAtCostSample - _pendingStates;
            if (consumedPending > 0 && _searchedSinceCostSample > 0)
            {
                // Download-like adaptive throughput: estimate how many searched states are
                // needed to consume one pending state, then update continuously.
                double observedCostPerPending = _searchedSinceCostSample / (double)consumedPending;
                if (!_pendingCostEstimateInitialized)
                {
                    _pendingCostEstimateInitialized = true;
                    _pendingCostStatesPerPending = observedCostPerPending;
                    _pendingCostConservativeStatesPerPending = observedCostPerPending;
                }
                else
                {
                    const double pendingCostAlpha = 0.35;
                    _pendingCostStatesPerPending += pendingCostAlpha * (observedCostPerPending - _pendingCostStatesPerPending);

                    const double conservativeRiseAlpha = 0.45;
                    const double conservativeFallAlpha = 0.04;
                    double conservativeAlpha =
                        observedCostPerPending >= _pendingCostConservativeStatesPerPending
                            ? conservativeRiseAlpha
                            : conservativeFallAlpha;
                    _pendingCostConservativeStatesPerPending +=
                        conservativeAlpha * (observedCostPerPending - _pendingCostConservativeStatesPerPending);
                }

                _searchedSinceCostSample = 0;
                _pendingAtCostSample = _pendingStates;
            }
            else if (_pendingStates > _pendingAtCostSample)
            {
                _pendingAtCostSample = _pendingStates;
            }

            if (_pendingStates > 0 && _searchedSinceCostSample > 0 && _pendingCostEstimateInitialized)
            {
                double noDrainFloor = _searchedSinceCostSample / (double)_pendingStates;
                _pendingCostStatesPerPending = Math.Max(_pendingCostStatesPerPending, noDrainFloor);
                _pendingCostConservativeStatesPerPending = Math.Max(_pendingCostConservativeStatesPerPending, noDrainFloor);
            }
        }

        _lastProgressSampleElapsedMs = elapsedMs;
        _lastProgressSampleSearched = _searchedStates;

        if (!_pendingCostEstimateInitialized)
        {
            _pendingCostEstimateInitialized = true;
            _pendingCostStatesPerPending = _pendingStates > 0
                ? Math.Max(64.0, _searchedStates / (double)_pendingStates)
                : 64.0;
            _pendingCostConservativeStatesPerPending = _pendingCostStatesPerPending;
        }

        bool isDefaultScope =
            _progressScope is ProgressScope.DefaultStandalone or ProgressScope.DefaultInCombinedRun;
        double costPerPending = Math.Max(1.0, _pendingCostStatesPerPending);
        if (isDefaultScope)
            costPerPending = Math.Max(costPerPending, _pendingCostConservativeStatesPerPending);

        int effectivePending = _pendingStates;
        if (_pendingStates == 0)
        {
            if (!_pendingZeroSettling)
            {
                _pendingZeroSettling = true;
                _pendingZeroSinceMs = elapsedMs;
                _pendingZeroSearchedAtStart = _searchedStates;
            }

            bool zeroSettled =
                elapsedMs - _pendingZeroSinceMs >= 400 &&
                _searchedStates == _pendingZeroSearchedAtStart;
            if (zeroSettled)
            {
                _progressEstimateInitialized = true;
                _progressEstimateEma01 = 1.0;
                return (1.0, 0);
            }

            // Avoid instant "100%" spikes when pending briefly touches zero mid-search.
            effectivePending = 1;
        }
        else
        {
            _pendingZeroSettling = false;
        }

        if (isDefaultScope && effectivePending <= 3)
        {
            // In default phase, tiny pending counts are often heavy tails rather than near-finish.
            // Apply a conservative inflation so progress does not pin near 100% too early.
            double inflation = effectivePending switch
            {
                1 => 3.0,
                2 => 2.1,
                _ => 1.5,
            };
            costPerPending *= inflation;
        }

        double estimatedRemainingSearchStates = effectivePending * costPerPending;
        double estimatedTotal = _searchedStates + estimatedRemainingSearchStates;
        if (estimatedTotal <= 0)
            return (0.0, -1);

        double rawProgress = Math.Clamp(_searchedStates / estimatedTotal, 0.0, 1.0);
        if (effectivePending > 0)
            rawProgress = Math.Min(rawProgress, 0.995);

        if (!_progressEstimateInitialized)
        {
            _progressEstimateInitialized = true;
            _progressEstimateEma01 = rawProgress;
        }
        else
        {
            const double riseAlpha = 0.25;
            const double fallAlpha = 0.08;
            double alpha = rawProgress >= _progressEstimateEma01 ? riseAlpha : fallAlpha;
            _progressEstimateEma01 += alpha * (rawProgress - _progressEstimateEma01);
        }

        double progress = Math.Clamp(_progressEstimateEma01, 0.0, 1.0);

        if (progress <= 0)
            return (progress, -1);

        long remaining;
        if (effectivePending == 0)
        {
            remaining = 0;
        }
        else if (_searchRateEstimateInitialized && _searchRateStatesPerMs > 0)
        {
            remaining = Math.Max(0, (long)(estimatedRemainingSearchStates / _searchRateStatesPerMs));
        }
        else
        {
            remaining = -1;
        }

        return (progress, remaining);
    }

    private void ObserveSearchState(ComparisonState state, int remainingSlots)
    {
        _visitedSearchStates.Add(GetSearchStateKey(state, remainingSlots));
    }

    private void RecordRootIncumbent(int bestWorstCaseSteps, IReadOnlyList<int> group)
    {
        _searchedStates = _visitedSearchStates.Count;
        _rootIncumbents.Add(new SearchMilestone(
            bestWorstCaseSteps,
            $"sort({StrategyTextRenderer.FormatSet(group)})",
            _progressStopwatch.ElapsedMilliseconds,
            _searchedStates,
            _pendingStates,
            _peakPendingStates,
            _stateIds.Count,
            _lowerBoundPrunes));
        ReportProgress(force: true);
    }

    private void ThrowIfCancellationRequested()
    {
        _cancellationToken.ThrowIfCancellationRequested();
    }

    private void EnsurePhase1Solved()
    {
        if (_phase1Solved)
            return;

        // Phase 1: solve the exact minimum worst-case cost for every reachable state,
        // caching the optimal comparison-group pattern per state along the way.
        _recordRootIncumbents = true;
        try
        {
            _ = GetMinWorstCaseSteps(new ComparisonState(_n), _k);
        }
        finally
        {
            _recordRootIncumbents = false;
        }
        _phase1Solved = true;
    }

    private void EnsureCompactSelectionSolved()
    {
        if (_phase1bSolved)
            return;

        // Optional phase 1b (PoC): among equally-optimal groups, choose the ones that
        // minimize the materialized subtree size (a proxy for displayed output states).
        _ = SolveCompactSelection(new ComparisonState(_n), _k);
        _phase1bSolved = true;
    }

    private void ResetPerBuildTransientState()
    {
        _stateIds.Clear();
        _expandedStates.Clear();
        _nextStateId = 1;

        _visitedSearchStates.Clear();
        _rootIncumbents.Clear();
        _rootSearchInitialized = false;
        _searchedStates = 0;
        _pendingStates = 0;
        _peakPendingStates = 0;

        _lowerBoundPrunes = 0;
        _duplicateOutcomeSkips = 0;
        _mergedOutcomeCollisions = 0;
        _exactCacheHits = 0;
        _lowerBoundCacheHits = 0;
        _feasibleTopSetCacheHits = 0;
        _bestGroupPatternCacheHits = 0;
        _outcomesConstructed = 0;
        _candidateGroupsEnumerated = 0;
        _compactStatesSolved = 0;
        _compactGroupsEnumerated = 0;
        _compactStepOptimalGroups = 0;
        _progressEstimateInitialized = false;
        _progressEstimateEma01 = 0.0;
        _lastProgressSampleElapsedMs = -1;
        _lastProgressSampleSearched = 0;
        _pendingCostEstimateInitialized = false;
        _pendingCostStatesPerPending = 0.0;
        _pendingCostConservativeStatesPerPending = 0.0;
        _pendingAtCostSample = -1;
        _searchedSinceCostSample = 0;
        _searchRateEstimateInitialized = false;
        _searchRateStatesPerMs = 0.0;
        _pendingZeroSettling = false;
        _pendingZeroSinceMs = 0;
        _pendingZeroSearchedAtStart = 0;
    }

    private sealed class SelectedComparisonGroup
    {
        public SelectedComparisonGroup(IReadOnlyList<int> group, IReadOnlyList<MergedBranch> branches)
        {
            Group = group;
            Branches = branches;
        }

        public IReadOnlyList<int> Group { get; }
        public IReadOnlyList<MergedBranch> Branches { get; }
    }

    private readonly struct ExpandedStateSnapshot
    {
        public ExpandedStateSnapshot(ComparisonState state, ulong fixedTopMask)
        {
            State = state;
            FixedTopMask = fixedTopMask;
        }

        public ComparisonState State { get; }
        public ulong FixedTopMask { get; }
    }

    private sealed class OutcomeTraversalSummary
    {
        public OutcomeTraversalSummary(
            IReadOnlyList<MergedBranch> mergedBranches,
            bool isUseful)
        {
            MergedBranches = mergedBranches;
            IsUseful = isUseful;
        }

        public IReadOnlyList<MergedBranch> MergedBranches { get; }
        public bool IsUseful { get; }
    }

    private readonly record struct HeuristicGroupScore(
        int GuaranteedTopHits,
        int FreshItems,
        int UnrelatedScore,
        int UnresolvedPairs,
        int GroupSize) : IComparable<HeuristicGroupScore>
    {
        // Among groups that achieve the optimal worst-case (the solver only ever caches an
        // optimal group), prefer the most independent/symmetric comparison: more fresh items
        // and fewer existing order relations. This keeps the worst-case step count optimal
        // while producing smaller, more symmetric, and easier-to-verify strategy trees.
        public int CompareTo(HeuristicGroupScore other)
        {
            int result = FreshItems.CompareTo(other.FreshItems);
            if (result != 0)
                return result;

            result = UnrelatedScore.CompareTo(other.UnrelatedScore);
            if (result != 0)
                return result;

            result = GuaranteedTopHits.CompareTo(other.GuaranteedTopHits);
            if (result != 0)
                return result;

            result = UnresolvedPairs.CompareTo(other.UnresolvedPairs);
            if (result != 0)
                return result;

            return GroupSize.CompareTo(other.GroupSize);
        }
    }

    private enum ProgressScope
    {
        DefaultStandalone = 0,
        DefaultInCombinedRun = 1,
        CompactPrimaryInCombinedRun = 2,
        CompactFallbackInCombinedRun = 3,
    }

}
