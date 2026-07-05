using System;
using System.Collections.Generic;
using System.Diagnostics;

partial class StrategyBuilder
{
    // GreedyTighten (Phase 0) — local restructuring of the greedy-feasible tree to lower the longest
    // path. See docs/core-algorithm.md 4.7 for the full design/rationale. This is the FRAMEWORK slice
    // (阶段 A): multi-round + critical-path post-order + AND short-circuit + single-state edit +
    // override map + lean-depth DP + one final materialization. The candidate SOURCE / SCORER are the
    // deferred tuning part (阶段 B): v1 uses the existing distinct-group enumeration in canonical order
    // (no scoring). NO proof semantics — it only tightens the feasible upper bound.
    //
    // It is NOT wired into the production pipeline yet; BuildGreedyTightenPlan is only exercised by
    // tests until the mechanism is validated.
    internal int GreedyTightenCandidateCap = 128;
    internal int? GreedyTightenMaxRoundsForTesting { get; set; }

    // Accumulated local edits: canonical stateKey -> chosen comparison group. Where present, both the
    // lean-depth DP and the materializing ChooseGroup route use the override instead of the
    // constructive selector; absent keys fall back to the same ChooseConstructiveGroup that the
    // greedy-feasible baseline uses (so an empty override map reproduces greedy-feasible exactly).
    //
    // The group is stored in the label space of the concrete ANCHOR state it was committed on (kept in
    // _greedyTightenOverrideAnchors under the same key). Because the key is the canonical isomorphism
    // class, one entry is shared by every concrete relabeling in the class; CurrentGreedyTightenGroup
    // relabels the stored group onto the concrete state via the poset isomorphism before use. This
    // keeps GreedyTightenHeight (memoized by the canonical key) consistent across isomorphic states --
    // applying a concrete group literally to a sibling labeling would otherwise yield a different
    // subtree height and corrupt the shared height memo.
    private readonly Dictionary<SearchStateKey, List<int>> _greedyTightenOverrides = new();
    private readonly Dictionary<SearchStateKey, ComparisonState> _greedyTightenOverrideAnchors = new();
    private readonly Dictionary<SearchStateKey, int> _greedyTightenSharedHeightMemo = new();
    private bool _useGreedyTightenSelection;

    // Diagnostics (per BuildGreedyTightenPlan run).
    private int _greedyTightenRounds;
    private int _greedyTightenCommits;
    private int _greedyTightenStatesVisited;
    private int _greedyTightenCandidateGroupsTried;
    private int _greedyTightenHeightCalls;
    private int _greedyTightenHeightMemoHits;
    private int _greedyTightenHeightUnderGroupCalls;
    private int _greedyTightenCriticalShortCircuits;
    private int _greedyTightenCommitCandidateRankSum;
    private readonly Dictionary<int, int> _greedyTightenVisitedDepthHistogram = new();
    private readonly Dictionary<int, int> _greedyTightenCommitDepthHistogram = new();
    private readonly List<GreedyTightenRoundDiagnostics> _greedyTightenRoundDiagnostics = new();
    internal int GreedyTightenRounds => _greedyTightenRounds;
    internal int GreedyTightenCommits => _greedyTightenCommits;
    internal int GreedyTightenStatesVisited => _greedyTightenStatesVisited;
    internal int GreedyTightenCandidateGroupsTried => _greedyTightenCandidateGroupsTried;
    internal int GreedyTightenHeightCalls => _greedyTightenHeightCalls;
    internal int GreedyTightenHeightMemoHits => _greedyTightenHeightMemoHits;
    internal int GreedyTightenHeightUnderGroupCalls => _greedyTightenHeightUnderGroupCalls;
    internal int GreedyTightenCriticalShortCircuits => _greedyTightenCriticalShortCircuits;
    internal int GreedyTightenAverageCommitCandidateRank => _greedyTightenCommits == 0
        ? 0
        : _greedyTightenCommitCandidateRankSum / _greedyTightenCommits;
    internal IReadOnlyDictionary<int, int> GreedyTightenVisitedDepthHistogram => _greedyTightenVisitedDepthHistogram;
    internal IReadOnlyDictionary<int, int> GreedyTightenCommitDepthHistogram => _greedyTightenCommitDepthHistogram;
    internal IReadOnlyList<GreedyTightenRoundDiagnostics> GreedyTightenRoundTrace => _greedyTightenRoundDiagnostics;

    // Builds the GreedyTighten stage plan: runs the local restructuring to tighten the upper bound,
    // then materializes the tightened tree once. Returns a feasible-upper-bound plan (never proven).
    public StrategyPlan BuildGreedyTightenPlan()
    {
        _progressScope = _reportCombinedRunProgress
            ? ProgressScope.FeasibleInCombinedRun
            : ProgressScope.DefaultStandalone;

        ResetPerBuildTransientState();
        var stopwatch = Stopwatch.StartNew();

        // L side of the squeeze: proven analytic lower bound (independent of the never-finishing exact
        // search), identical to the greedy-feasible path.
        RecordRootProvenLowerBound(GetMinWorstCaseLowerBound(new ComparisonState(_n), _k));

        RunGreedyTighten();

        // Materialize the tightened tree once (Option B: search on the lean-depth DP, materialize only
        // at the end). Absent overrides fall back to ChooseConstructiveGroup, so this yields the
        // greedy-feasible tree plus the committed local edits.
        _useGreedyTightenSelection = true;
        StrategyNode root = BuildState(new ComparisonState(_n), 0, _k, 1);
        _useGreedyTightenSelection = false;

        stopwatch.Stop();

        return new StrategyPlan(
            _n, _m, _requestedK, _k, root, stopwatch.Elapsed, CreateSearchStatistics(),
            isFeasibleUpperBound: true);
    }

    // Multi-round driver. Each round runs one critical-path post-order pass from the root; a pass that
    // lowers the root height ends the round and a fresh (tighter) round starts from scratch. When a
    // pass cannot lower the root, GreedyTighten has converged. Overrides persist across rounds; the
    // lean-depth tree is recomputed from the override map each round.
    private void RunGreedyTighten()
    {
        _greedyTightenOverrides.Clear();
        _greedyTightenOverrideAnchors.Clear();
        _greedyTightenRounds = 0;
        _greedyTightenCommits = 0;
        _greedyTightenStatesVisited = 0;
        _greedyTightenCandidateGroupsTried = 0;
        _greedyTightenHeightCalls = 0;
        _greedyTightenHeightMemoHits = 0;
        _greedyTightenHeightUnderGroupCalls = 0;
        _greedyTightenCriticalShortCircuits = 0;
        _greedyTightenCommitCandidateRankSum = 0;
        _greedyTightenVisitedDepthHistogram.Clear();
        _greedyTightenCommitDepthHistogram.Clear();
        _greedyTightenRoundDiagnostics.Clear();
        _greedyTightenSharedHeightMemo.Clear();

        while (true)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            _greedyTightenRounds++;
            int round = _greedyTightenRounds;
            int statesVisitedBefore = _greedyTightenStatesVisited;
            int candidatesTriedBefore = _greedyTightenCandidateGroupsTried;
            int commitsBefore = _greedyTightenCommits;
            int heightCallsBefore = _greedyTightenHeightCalls;
            int memoHitsBefore = _greedyTightenHeightMemoHits;
            int heightUnderGroupCallsBefore = _greedyTightenHeightUnderGroupCalls;
            int shortCircuitsBefore = _greedyTightenCriticalShortCircuits;
            var stopwatch = Stopwatch.StartNew();
            int rootHeightBefore = GreedyTightenHeight(new ComparisonState(_n), _k, _greedyTightenSharedHeightMemo);
            bool rootDropped = TryLowerHeight(new ComparisonState(_n), _k, 0);
            int rootHeightAfter = GreedyTightenHeight(new ComparisonState(_n), _k, _greedyTightenSharedHeightMemo);
            stopwatch.Stop();
            _greedyTightenRoundDiagnostics.Add(new GreedyTightenRoundDiagnostics(
                round,
                rootHeightBefore,
                rootHeightAfter,
                stopwatch.ElapsedMilliseconds,
                _greedyTightenStatesVisited - statesVisitedBefore,
                _greedyTightenCandidateGroupsTried - candidatesTriedBefore,
                _greedyTightenCommits - commitsBefore,
                _greedyTightenHeightCalls - heightCallsBefore,
                _greedyTightenHeightMemoHits - memoHitsBefore,
                _greedyTightenHeightUnderGroupCalls - heightUnderGroupCallsBefore,
                _greedyTightenCriticalShortCircuits - shortCircuitsBefore,
                rootDropped));

            if (GreedyTightenMaxRoundsForTesting is int maxRounds && _greedyTightenRounds >= maxRounds)
                break;
            if (!rootDropped)
                break;
        }
    }

    // Critical-path post-order pass at (state, remainingSlots). Returns true iff this state's lean
    // subtree height strictly decreased -- either by lowering ALL of its current critical (max-height)
    // children (AND relationship, short-circuited on the first child that cannot drop), or by replacing
    // its own comparison group with a candidate that yields a strictly shorter subtree. Any override
    // committed in a descendant is permanent regardless of whether this state (or the root) drops.
    private bool TryLowerHeight(ComparisonState state, int remainingSlots, int depth)
    {
        ThrowIfCancellationRequested();
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);

        // Terminals / final-choice states carry no editable comparison group.
        if (remainingSlots == 0)
            return false;
        if (TryGetDeterminedTopSet(state, remainingSlots, out _))
            return false;
        if (state.ActiveCount <= remainingSlots)
            return false;
        if (state.ActiveCount <= _m)
            return false;

        _greedyTightenStatesVisited++;
        IncrementGreedyTightenDepthHistogram(_greedyTightenVisitedDepthHistogram, depth);

        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        int height = GreedyTightenHeight(state, remainingSlots, _greedyTightenSharedHeightMemo);
        List<int> group = CurrentGreedyTightenGroup(state, remainingSlots, key);

        // Enumerate children under the current group and find the critical (max-height) ones.
        var children = new List<(ComparisonState State, int Rem, int Height)>();
        VisitComparisonOutcomes(
            state, fixedTopMask: 0, remainingSlots, group, currentKey: key, collectMergedBranches: false,
            onUsefulOutcome: outcome =>
            {
                int h = GreedyTightenHeight(outcome.NextState, outcome.NextRemainingSlots, _greedyTightenSharedHeightMemo);
                children.Add((outcome.NextState, outcome.NextRemainingSlots, h));
                return true;
            });

        int childMax = 0;
        foreach (var child in children)
            if (child.Height > childMax)
                childMax = child.Height;

        // Option (a): lower every critical child (AND). Short-circuit on the first that cannot drop --
        // keeping this group, the parent can only fall when all its max-height children fall.
        bool allCriticalDropped = true;
        foreach (var child in children)
        {
            if (child.Height < childMax)
                continue;
            if (!TryLowerHeight(child.State, child.Rem, depth + 1))
            {
                _greedyTightenCriticalShortCircuits++;
                allCriticalDropped = false;
                break;
            }
        }
        if (allCriticalDropped)
        {
            int newHeight = GreedyTightenHeight(state, remainingSlots, _greedyTightenSharedHeightMemo);
            if (newHeight < height)
                return true;
        }

        // Option (b): replace this state's own group. v1 tries the existing distinct-group enumeration
        // (capped) in canonical order and commits the first candidate that strictly lowers the subtree
        // height (hit-once-and-move-on). Scoring/ordering is the deferred 阶段 B tuning.
        var candidates = state.GetActiveItemsOrdered();
        int groupSize = Math.Min(_m, candidates.Count);
        int candidateRank = 0;
        foreach (List<int> candidate in EnumerateDistinctGroups(state, candidates, groupSize, GreedyTightenCandidateCap))
        {
            if (!GroupHasUnresolvedPair(state, candidate))
                continue; // must make progress, else the subtree does not terminate
            if (SameGroupSequence(candidate, group))
                continue;

            candidateRank++;
            _greedyTightenCandidateGroupsTried++;

            int candidateHeight = GreedyTightenHeightUnderGroup(
                state, remainingSlots, candidate, _greedyTightenSharedHeightMemo);
            if (candidateHeight < height)
            {
                _greedyTightenOverrides[key] = new List<int>(candidate);
                _greedyTightenOverrideAnchors[key] = state.Clone();
                _greedyTightenCommits++;
                _greedyTightenCommitCandidateRankSum += candidateRank;
                IncrementGreedyTightenDepthHistogram(_greedyTightenCommitDepthHistogram, depth);
                // A committed override changes the effective policy for this state, so previously
                // memoized heights may be stale under the new override map.
                _greedyTightenSharedHeightMemo.Clear();
                return true;
            }
        }

        return false;
    }

    // Lean subtree height at (state, remainingSlots) under the current override map: 1 + max child
    // height, where each state's group is its override (if any) or the constructive selector. Budget
    // independent; callers may share a memo across repeated evaluations, but any committed override
    // change must invalidate that memo before heights are queried again. Mirrors ConstructiveDepth's
    // terminal cases so it matches the materialized tree's structure (modulo display-key Reference
    // de-duplication).
    private int GreedyTightenHeight(ComparisonState state, int remainingSlots, Dictionary<SearchStateKey, int> memo)
    {
        _greedyTightenHeightCalls++;
        ThrowIfCancellationRequested();
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);

        if (remainingSlots == 0)
            return 0;
        if (TryGetDeterminedTopSet(state, remainingSlots, out _))
            return 0;
        if (state.ActiveCount <= remainingSlots)
            return 0;
        if (state.ActiveCount <= _m)
            return 1;

        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        if (memo.TryGetValue(key, out int cached))
        {
            _greedyTightenHeightMemoHits++;
            return cached;
        }

        List<int> group = CurrentGreedyTightenGroup(state, remainingSlots, key);
        int result = 1 + MaxChildHeight(state, remainingSlots, key, group, memo);
        memo[key] = result;
        return result;
    }

    // Lean subtree height if `state` used comparison group `group` for its next step (one level), with
    // descendants falling back to the override map / constructive selector. Used to evaluate a
    // candidate replacement without mutating the override map.
    private int GreedyTightenHeightUnderGroup(
        ComparisonState state, int remainingSlots, List<int> group, Dictionary<SearchStateKey, int> memo)
    {
        _greedyTightenHeightUnderGroupCalls++;
        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        return 1 + MaxChildHeight(state, remainingSlots, key, group, memo);
    }

    private int MaxChildHeight(
        ComparisonState state, int remainingSlots, SearchStateKey key, List<int> group,
        Dictionary<SearchStateKey, int> memo)
    {
        int maxChild = 0;
        VisitComparisonOutcomes(
            state, fixedTopMask: 0, remainingSlots, group, currentKey: key, collectMergedBranches: false,
            onUsefulOutcome: outcome =>
            {
                int childHeight = GreedyTightenHeight(outcome.NextState, outcome.NextRemainingSlots, memo);
                if (childHeight > maxChild)
                    maxChild = childHeight;
                return true;
            });
        return maxChild;
    }

    // The comparison group GreedyTighten currently uses at a state: the committed override if present,
    // otherwise the same constructive selector as the greedy-feasible baseline. The override group is
    // stored in its anchor state's label space, so it is relabeled onto this concrete `state` via the
    // poset isomorphism first (see the _greedyTightenOverrides field note for why this is required).
    private List<int> CurrentGreedyTightenGroup(ComparisonState state, int remainingSlots, SearchStateKey key)
    {
        if (!_greedyTightenOverrides.TryGetValue(key, out List<int>? overrideGroup))
            return ChooseConstructiveGroup(state, remainingSlots);

        if (_greedyTightenOverrideAnchors.TryGetValue(key, out ComparisonState? anchor))
        {
            List<int>? relabeled = RelabelOverrideGroup(anchor, state, overrideGroup);
            if (relabeled is not null)
                return relabeled;
        }

        // Defensive fallback: an override whose anchor cannot be relabeled onto this labeling cannot be
        // applied safely (this should not happen for two states sharing a canonical key), so use the
        // always-valid constructive selector rather than a mislabeled group.
        return ChooseConstructiveGroup(state, remainingSlots);
    }

    // Translates an override group from the anchor state's label space into `state`'s labels using the
    // poset isomorphism between the two (they share a canonical SearchStateKey, so one exists). Returns
    // null only if no isomorphism is found. Items unchanged by the relabeling map to themselves.
    private static List<int>? RelabelOverrideGroup(ComparisonState anchor, ComparisonState state, List<int> anchorGroup)
    {
        IReadOnlyList<ItemRelabel>? relabeling = anchor.TryBuildDisplayRelabeling(0, state, 0);
        if (relabeling is null)
            return null;

        var map = new Dictionary<int, int>(relabeling.Count);
        foreach (ItemRelabel relabel in relabeling)
            map[relabel.ReferencedItem] = relabel.CurrentItem;

        var translated = new List<int>(anchorGroup.Count);
        foreach (int item in anchorGroup)
            translated.Add(map.TryGetValue(item, out int mapped) ? mapped : item);

        translated.Sort();
        return translated;
    }

    private static bool SameGroupSequence(List<int> a, List<int> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }

    private static void IncrementGreedyTightenDepthHistogram(Dictionary<int, int> histogram, int depth)
    {
        histogram[depth] = histogram.TryGetValue(depth, out int count) ? count + 1 : 1;
    }

    internal readonly record struct GreedyTightenRoundDiagnostics(
        int Round,
        int RootHeightBefore,
        int RootHeightAfter,
        long ElapsedMilliseconds,
        int StatesVisited,
        int CandidateGroupsTried,
        int Commits,
        int HeightCalls,
        int HeightMemoHits,
        int HeightUnderGroupCalls,
        int CriticalShortCircuits,
        bool RootDropped);
}
