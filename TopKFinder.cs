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
    // Best proven lower bound on the root optimum (opt >= this). Lifted each failed iterative-
    // deepening pass; recorded only during the phase-1 root search. The L side of the squeeze.
    // Like the phase-1 incumbents and the _rootSearchInitialized latch, this is a product of the
    // once-only phase-1 solve (memoized by _phase1Solved) and is therefore NOT cleared by
    // ResetPerBuildTransientState; otherwise the later compact build would reset it to 0 and the
    // squeeze display would regress from "opt = N (proven)" back to "? <= opt <= ?".
    private int _rootProvenLowerBound;
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
        // Returns the raw compact candidate: the compact DP keeps the optimal worst-case step count
        // (so MaxStep always matches default) and, among equally-optimal groups, minimizes a per-state
        // displayed-edge proxy. That proxy does not model the materializer's display-key Reference
        // de-duplication, so on rare shapes the compact selection can render MORE branch edges than
        // default (e.g. 10,4,8: 8 -> 10). This builder no longer guards against that internally --
        // the orchestrator's mainline rule (keep a phase's plan only when it strictly improves on the
        // global best) is the single place that decides whether the compact plan is shown, so a
        // worse-than-default compact candidate is simply never used.
        _progressScope = _reportCombinedRunProgress
            ? ProgressScope.CompactPrimaryInCombinedRun
            : ProgressScope.DefaultStandalone;
        return BuildPlan(useCompactSelection: true, useFeasibleBudget: false);
    }

    // Feasible+compact (greedy mode edge phase): the step ceiling is the constructive feasible upper
    // bound U instead of the proven optimum, so the exact search is never run. The compact DP minimizes
    // displayed edges under U and reports the actual MaxStep realized (often below U via free pickup).
    // Fast and interruptible, not proven optimal.
    // onStage, when supplied, is invoked synchronously on this thread each time an edge stage becomes
    // available: first for the baseline compact pass ("compact"), then once per downward tightening
    // attempt ("compact≤N", carrying the smaller plan), and finally once for the proven-infeasible
    // ceiling (a no-solution stage whose plan is null). This drives an anytime UI/CLI that surfaces the
    // full compact → compact≤N progression as it is found, rather than only the final result.
    public StrategyPlan BuildFeasibleCompactPlan(Action<GreedyEdgeStage>? onStage = null)
    {
        _progressScope = _reportCombinedRunProgress
            ? ProgressScope.CompactFeasibleInCombinedRun
            : ProgressScope.DefaultStandalone;

        // Baseline compact pass at the threaded step ceiling U (always feasible: the step tree itself
        // witnesses a U-step solution).
        StrategyPlan baseline = BuildPlan(useCompactSelection: true, useFeasibleBudget: true);
        onStage?.Invoke(new GreedyEdgeStage("compact", baseline, baseline.Elapsed));
        return EnableFeasibleTightening ? TightenFeasibleCompact(baseline, onStage) : baseline;
    }

    // Opportunistically lowers the greedy edge plan's max-step by re-running the compact pass at
    // progressively tighter ceilings (U-1, U-2, ...). Each accepted retry strictly decreases the realized
    // max-step; the loop stops at the first infeasible ceiling (optimum reached), at the proven lower
    // bound L, when the soft time budget is exhausted, or on user cancellation -- always returning the
    // best (smallest max-step) plan found, never worse than the baseline.
    private StrategyPlan TightenFeasibleCompact(StrategyPlan baseline, Action<GreedyEdgeStage>? onStage)
    {
        StrategyPlan best = baseline;

        // If the compact baseline already failed to honor the feasible step budget U (the greedy
        // plan's MaxStep, threaded in via _feasibleRootBudget), its materialized tree is deeper than U.
        // The compact group selection is a budget-agnostic heuristic, so probing ever-tighter ceilings
        // cannot beat a baseline that could not even meet U -- skip tightening and let the greedy plan
        // remain the incumbent downstream.
        if (_feasibleRootBudget >= 0 && baseline.MaxStep > _feasibleRootBudget)
            return baseline;

        int provenLowerBound = Math.Max(1, _rootProvenLowerBound);
        long baselineMs = (long)baseline.Elapsed.TotalMilliseconds;
        long timeBudgetMs = Math.Max(
            FeasibleTighteningMinTimeBudgetMs,
            (long)(baselineMs * FeasibleTighteningTimeBudgetFactor));

        var stopwatch = Stopwatch.StartNew();
        int budget = best.MaxStep - 1;
        while (budget >= provenLowerBound)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            long remainingMs = timeBudgetMs - stopwatch.ElapsedMilliseconds;
            if (remainingMs <= 0)
                break;

            _tighteningDeadlineUtc = DateTime.UtcNow.AddMilliseconds(remainingMs);
            System.Threading.Volatile.Write(ref _tighteningDeadlineTicksUtc, _tighteningDeadlineUtc.Value.Ticks);
            _tighteningDeadlineHit = false;
            StrategyPlan? candidate;
            // This probe's own wall time, captured even when the probe proves infeasible (returns null)
            // so the no-solution stage still reports its elapsed.
            var probeStopwatch = Stopwatch.StartNew();
            try
            {
                candidate = ProbeFeasibleCompact(budget);
            }
            catch (OperationCanceledException) when (_tighteningDeadlineHit && !_cancellationToken.IsCancellationRequested)
            {
                candidate = null; // soft deadline: abandon this retry and keep the best plan so far
            }
            finally
            {
                _tighteningDeadlineUtc = null;
                System.Threading.Volatile.Write(ref _tighteningDeadlineTicksUtc, 0);
                probeStopwatch.Stop();
            }

            string stageName = $"compact\u2264{budget}";

            // The soft deadline aborted this probe before it could decide feasibility: surface it as a
            // timed-out stage (no proof either way -- the best plan so far still stands) and stop.
            if (_tighteningDeadlineHit)
            {
                stopwatch.Stop();
                onStage?.Invoke(new GreedyEdgeStage(
                    stageName, null, probeStopwatch.Elapsed, GreedyEdgeStageOutcome.TimedOut));
                break;
            }

            if (candidate is null)
            {
                // Proven infeasible at this ceiling -> the previous best is the optimum within reach.
                // Raise the proven lower bound to budget+1 (== best.MaxStep): the search has now proven
                // opt >= best.MaxStep, and best achieves it, so the L <= opt <= U squeeze closes to a
                // proven optimum. Surface it as a no-solution stage so the UI/CLI shows the search
                // bottomed out here. Pause the budget clock across the callback: a synchronous consumer
                // (e.g. the GUI's pause-each-stage modal) blocks the worker here, and that wait must not
                // count against the tightening time budget.
                RecordRootProvenLowerBound(budget + 1);
                stopwatch.Stop();
                onStage?.Invoke(new GreedyEdgeStage(
                    stageName, null, probeStopwatch.Elapsed, GreedyEdgeStageOutcome.NoSolution));
                break;
            }

            // Ground-truth guard: trust the materialized MaxStep, not the budget-agnostic compact
            // group cache that produced it. A probe can return a tree that fails to honor the ceiling
            // it was given (the cache keys group choices by state only, so a group picked under a looser
            // budget can leak into a tighter path and deepen the subtree). Such a result never beats the
            // incumbent, and accepting it would re-derive budget = MaxStep - 1 above the current ceiling
            // and oscillate until the time budget runs out. Stop at the first probe that does not
            // strictly improve the best plan so far.
            if (candidate.MaxStep >= best.MaxStep)
            {
                stopwatch.Stop();
                break;
            }

            best = candidate;
            stopwatch.Stop();
            onStage?.Invoke(new GreedyEdgeStage(stageName, candidate, probeStopwatch.Elapsed));
            stopwatch.Start();
            budget = best.MaxStep - 1; // realized max-step may already be below the attempted ceiling
        }

        // Reflect any proven lower bound learned during tightening (a proven-infeasible ceiling closes
        // the squeeze on this feasible plan to a proven optimum) on the returned plan as well, so direct
        // callers of the return value see the same closed squeeze the staged consumers do.
        return best.WithRootProvenLowerBound(_rootProvenLowerBound);
    }

    // Runs a single compact pass at a fixed root ceiling, returning the materialized plan or null if the
    // ceiling is infeasible (root solve yields the unsolvable sentinel). Resets the per-budget compact
    // caches first. Progress snapshots flow normally so the bar/ETA track the current tightening probe.
    private StrategyPlan? ProbeFeasibleCompact(int rootBudget)
    {
        ResetPerBuildTransientState();
        ResetCompactSelectionState();

        var stopwatch = Stopwatch.StartNew();
        _compactUsesFeasibleBudget = true;
        _feasibleRootBudgetActive = rootBudget;
        try
        {
            EnsureCompactSelectionSolved();
            _phase1bMilliseconds = stopwatch.ElapsedMilliseconds;
            if (_compactRootCost == int.MaxValue)
                return null;

            _useCompactSelection = true;
            var root = BuildState(new ComparisonState(_n), 0, _k, 1);
            _phase2Milliseconds = stopwatch.ElapsedMilliseconds - _phase1bMilliseconds;
            stopwatch.Stop();
            return new StrategyPlan(
                _n, _m, _requestedK, _k, root, stopwatch.Elapsed, CreateSearchStatistics(),
                isFeasibleUpperBound: true);
        }
        finally
        {
            _feasibleRootBudgetActive = -1;
        }
    }

    private StrategyPlan BuildPlan(bool useCompactSelection, bool useFeasibleBudget = false)
    {
        ResetPerBuildTransientState();
        var stopwatch = Stopwatch.StartNew();
        ReportProgress(force: true);

        _compactUsesFeasibleBudget = useFeasibleBudget;
        if (!useFeasibleBudget)
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
        bool feasible = useFeasibleBudget;
        return new StrategyPlan(_n, _m, _requestedK, _k, root, stopwatch.Elapsed, CreateSearchStatistics(),
            isFeasibleUpperBound: feasible);
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

        // The constructive feasible plan computes its group directly from the current partial order
        // (cheap, O(m*active^2)), so unlike greedy/compact it needs no precomputed pattern cache.
        if (_useConstructiveSelection)
        {
            List<int> constructiveGroup = ChooseConstructiveGroup(state, remainingSlots);
            return new SelectedComparisonGroup(
                constructiveGroup,
                BuildMergedComparisonOutcomes(state, fixedTopMask, remainingSlots, constructiveGroup));
        }

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

        int[]? colorSignature = cachedPattern.ColorSignature;
        int[]? activeColors = colorSignature is null ? null : state.GetActiveItemColors();

        foreach (var group in EnumerateCombinations(candidates, cachedPattern.GroupSize))
        {
            if (activeColors is not null && !GroupMatchesColorSignature(activeColors, group, colorSignature!))
                continue;

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

    // Builds a BestGroupPattern carrying both the canonical group pattern and a cheap color
    // pre-filter signature (the sorted multiset of the group's per-item active colors). ChooseGroup
    // uses the signature to skip the expensive canonical-key check for groups that cannot match.
    private static BestGroupPattern MakeGroupPattern(ComparisonState state, IReadOnlyList<int> group)
    {
        int[] colors = state.GetActiveItemColors();
        return new BestGroupPattern(group.Count, GetGroupPattern(state, group), BuildSortedColorSignature(colors, group));
    }

    private static int[] BuildSortedColorSignature(int[] colors, IReadOnlyList<int> group)
    {
        var signature = new int[group.Count];
        for (int i = 0; i < group.Count; i++)
            signature[i] = colors[group[i]];
        Array.Sort(signature);
        return signature;
    }

    // Necessary condition for GetGroupPattern(state, group) == target pattern: the group's sorted
    // color multiset must equal the cached signature. Allocation-free (group size is tiny).
    private static bool GroupMatchesColorSignature(int[] colors, IReadOnlyList<int> group, int[] target)
    {
        int count = group.Count;
        if (target.Length != count)
            return false;

        Span<int> signature = stackalloc int[count];
        for (int i = 0; i < count; i++)
            signature[i] = colors[group[i]];

        for (int i = 1; i < count; i++)
        {
            int value = signature[i];
            int j = i - 1;
            while (j >= 0 && signature[j] > value)
            {
                signature[j + 1] = signature[j];
                j--;
            }

            signature[j + 1] = value;
        }

        for (int i = 0; i < count; i++)
            if (signature[i] != target[i])
                return false;

        return true;
    }

    private IEnumerable<List<int>> EnumerateDistinctGroups(
        ComparisonState state,
        IReadOnlyList<int> candidates,
        int groupSize,
        int generationCap = int.MaxValue)
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
        //
        // generationCap bounds how many raw representatives we generate before the (cap-bounded) orbit
        // dedup and sort. The default int.MaxValue is the exact, complete enumeration used by the exact
        // compact DP and the optimality-gap audit; the greedy edge phase passes a finite cap so a single
        // large-m state cannot generate (and then FitChildren over) thousands of groups -- the
        // materialized generation and McKay dedup over the full set is what makes that phase hang.
        List<List<int>> classes = state.GetFreeSymmetryClasses();

        var suffixCapacity = new int[classes.Count + 1];
        for (int c = classes.Count - 1; c >= 0; c--)
            suffixCapacity[c] = suffixCapacity[c + 1] + classes[c].Count;

        var collected = new List<List<int>>();
        var prefix = new List<int>(groupSize);
        GenerateClassRepresentatives(state, classes, suffixCapacity, 0, groupSize, prefix, collected, generationCap);

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
        List<List<int>> collected,
        int generationCap = int.MaxValue)
    {
        if (collected.Count >= generationCap)
            return;

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
                state, classes, suffixCapacity, classIndex + 1, remaining - take, prefix, collected, generationCap);

            prefix.RemoveRange(prefix.Count - take, take);

            if (collected.Count >= generationCap)
                return;
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
            _compactStepOptimalGroups,
            _rootProvenLowerBound);
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
            _feasibleCompactStateEstimate,
            estimatedProgress01,
            estimatedRemainingMs,
            _rootProvenLowerBound));
    }

    private (double Progress01, long RemainingMs) MapToReportedProgress(long elapsedMs, double localProgress01, long localRemainingMs)
    {
        if (!_reportCombinedRunProgress)
            return (localProgress01, localRemainingMs);

        // The combined-run bar is split into two visible stages: step then edge. The ratio differs
        // per mode. Exact mode: step (exact solve) gets 60%, edge (compact) gets 40%. Greedy mode:
        // step (feasible bound) gets 10%, so edge (feasible-compact) gets the remaining 90%.
        (double progressBase, double progressSpan) = _progressScope switch
        {
            ProgressScope.FeasibleInCombinedRun => (0.0, 0.10),
            ProgressScope.DefaultInCombinedRun => (0.0, 0.60),
            ProgressScope.CompactPrimaryInCombinedRun => (0.60, 0.40),
            ProgressScope.CompactFeasibleInCombinedRun => (0.10, 0.90),
            _ => (0.0, 1.0),
        };

        // The feasible phase is a single indivisible greedy slice with no internal progress signal,
        // so instead of sitting at 0% (which reads as "nothing happening") it fills its whole 10% band
        // and hands off continuously to the default phase, which starts at 10%.
        double localFraction = _progressScope == ProgressScope.FeasibleInCombinedRun
            ? 1.0
            : Math.Clamp(localProgress01, 0.0, 1.0);
        double progress = Math.Clamp(progressBase + (localFraction * progressSpan), 0.0, 1.0);
        if (progress <= 0.0 || elapsedMs <= 0)
            return (progress, -1);

        long remaining = progress >= 1.0
            ? 0
            : Math.Max(0, (long)(elapsedMs * ((1.0 / progress) - 1.0)));
        return (progress, remaining);
    }

    private (double Progress01, long RemainingMs) EstimateProgress(long elapsedMs)
    {
        // Greedy-mode edge phase: the compact solve has no pending/searched signal (those counters are
        // reset and never touched here), so drive progress off _compactStatesSolved. Once the solve
        // finishes (_phase1bSolved) the remaining phase-2 materialization is effectively instant, so
        // report a full local fraction. MapToReportedProgress bands this into the edge stage's
        // 10%..100% slice and derives the remaining-time estimate.
        if (_progressScope == ProgressScope.CompactFeasibleInCombinedRun)
        {
            if (_phase1bSolved)
                return (1.0, -1);
            if (_feasibleCompactStateEstimate <= 0)
                return (0.0, -1);

            // The step-phase state count is only a rough SCALE for the edge phase's work -- measured
            // ratios of edge-solved to step-states span ~0.3x..27x across shapes, mostly because the
            // edge phase's iterative-deepening depth is not predictable up front. A hard denominator
            // therefore either tops out early (under-estimate) or stalls at a clamp. Use a
            // self-correcting asymptote instead: fraction = solved / (solved + scale). It rises strictly
            // with every solved state, stays below 1 however badly the scale under/over-shoots, and
            // never sticks -- under-estimates self-correct because the effective denominator grows with
            // solved. The watched (slow) shapes have solved >> scale, so the bar climbs smoothly toward
            // ~1 and only snaps to 100% when the solve actually completes above.
            double scale = _feasibleCompactStateEstimate;
            double fraction = _compactStatesSolved / (_compactStatesSolved + scale);
            return (Math.Min(fraction, 0.999), -1);
        }

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

    // Monotonically lifts the root proven lower bound (the L side of the squeeze). Called only
    // during the phase-1 root search; ignores non-increasing values so it stays monotone even
    // though the single-pass path reports the analytic bound before the exact result.
    private void RecordRootProvenLowerBound(int provenLowerBound)
    {
        if (provenLowerBound <= _rootProvenLowerBound)
            return;
        _rootProvenLowerBound = provenLowerBound;
        ReportProgress(force: true);
    }

    private void ThrowIfCancellationRequested()
    {
        _cancellationToken.ThrowIfCancellationRequested();

        // Soft deadline for a U-tightening retry: enforced via the same frequent checkpoints the search
        // already calls, so a too-slow retry is abandoned without a dedicated timer thread. Recorded so
        // the tightening loop can tell a deadline abort (keep the best plan) from a user cancellation.
        if (_tighteningDeadlineUtc is { } deadline && DateTime.UtcNow >= deadline)
        {
            _tighteningDeadlineHit = true;
            throw new OperationCanceledException();
        }
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

        // Optional phase 1b: among equally-optimal groups, choose the ones that minimize the
        // materialized subtree size (a proxy for displayed output states). The root budget is the
        // proven optimum for exact mode, or the constructive feasible upper bound U for feasible mode:
        // the materialized U threaded from the step phase when present (tightest, keeps the edge plan
        // no worse than step), else the sound-but-looser lean ConstructiveRootUpperBound.
        int rootBudget = _compactUsesFeasibleBudget
            ? (_feasibleRootBudgetActive >= 0
                ? _feasibleRootBudgetActive
                : (_feasibleRootBudget >= 0 ? _feasibleRootBudget : ConstructiveRootUpperBound()))
            : int.MaxValue;
        _compactRootCost = SolveCompactSelection(new ComparisonState(_n), _k, rootBudget);
        _phase1bSolved = true;
    }

    private void ResetPerBuildTransientState()
    {
        _stateIds.Clear();
        _expandedStates.Clear();
        _nextStateId = 1;

        _visitedSearchStates.Clear();
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
        FeasibleInCombinedRun = 4,
        CompactFeasibleInCombinedRun = 8,
    }

}
