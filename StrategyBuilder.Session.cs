using System.Collections.Generic;

sealed class StrategyBuilderSession
{
    // Statistics/diagnostics state.
    public List<SearchMilestone> RootIncumbents { get; } = new();

    public int LowerBoundPrunes;
    public int DuplicateOutcomeSkips;
    public int MergedOutcomeCollisions;
    public int ExactCacheHits;
    public int LowerBoundCacheHits;
    public int FeasibleTopSetCacheHits;
    public int BestGroupPatternCacheHits;
    public int OutcomesConstructed;
    public int CandidateGroupsEnumerated;

    public int CompactStatesSolved;
    public int CompactGroupsEnumerated;
    public int CompactStepOptimalGroups;

    // Exact/compact solver caches.
    public HashSet<SearchStateKey> VisitedSearchStates { get; } = new();
    public Dictionary<SearchStateKey, int> MinWorstCaseStepsCache { get; } = new();
    public Dictionary<SearchStateKey, int> LowerBoundStepsCache { get; } = new();
    public Dictionary<SearchStateKey, int> SearchLowerBoundCache { get; } = new();
    public Dictionary<SearchStateKey, FeasibleTopSetInfo> FeasibleTopSetCache { get; } = new();
    public Dictionary<SearchStateKey, BestGroupPattern> BestGroupPatternCache { get; } = new();

    public Dictionary<SearchStateKey, BestGroupPattern> CompactGroupPatternCache { get; } = new();
    public Dictionary<SearchStateKey, int> CompactGroupPatternTightestBudget { get; } = new();
    public Dictionary<(SearchStateKey Key, int Budget), int> CompactCostMemo { get; } = new();
    public Dictionary<SearchStateKey, int> CompactRealStepsMemo { get; } = new();

    // Greedy feasible/tighten session state.
    public Dictionary<SearchStateKey, int>? ConstructiveDepthMemo;
    public int GreedyScoreLowerBoundCacheReuseHits;
    public int ConstructiveDisplayLineTieBreakEvaluations;
    public int FeasibleRootBudget = -1;
    public int FeasibleCompactStateEstimate = -1;

    public Dictionary<SearchStateKey, List<int>> GreedyTightenOverrides { get; } = new();
    public Dictionary<SearchStateKey, ComparisonState> GreedyTightenOverrideAnchors { get; } = new();
    public Dictionary<SearchStateKey, int> GreedyTightenSharedHeightMemo { get; } = new();
    public bool UseGreedyTightenSelection;

    public int GreedyTightenRounds;
    public int GreedyTightenCommits;
    public int GreedyTightenStatesVisited;
    public int GreedyTightenCandidateGroupsTried;
    public int GreedyTightenHeightCalls;
    public int GreedyTightenHeightMemoHits;
    public int GreedyTightenHeightUnderGroupCalls;
    public int GreedyTightenCriticalShortCircuits;
    public int GreedyTightenCommitCandidateRankSum;
    public Dictionary<int, int> GreedyTightenVisitedDepthHistogram { get; } = new();
    public Dictionary<int, int> GreedyTightenCommitDepthHistogram { get; } = new();

    public void ResetPerBuildTransientState()
    {
        VisitedSearchStates.Clear();

        LowerBoundPrunes = 0;
        DuplicateOutcomeSkips = 0;
        MergedOutcomeCollisions = 0;
        ExactCacheHits = 0;
        LowerBoundCacheHits = 0;
        FeasibleTopSetCacheHits = 0;
        BestGroupPatternCacheHits = 0;
        GreedyScoreLowerBoundCacheReuseHits = 0;
        OutcomesConstructed = 0;
        CandidateGroupsEnumerated = 0;
        CompactStatesSolved = 0;
        CompactGroupsEnumerated = 0;
        CompactStepOptimalGroups = 0;
    }

    public void ResetCompactCaches()
    {
        CompactGroupPatternCache.Clear();
        CompactGroupPatternTightestBudget.Clear();
        CompactCostMemo.Clear();
        CompactRealStepsMemo.Clear();
    }

    public void LoadCompactPatternAssignment(IReadOnlyDictionary<SearchStateKey, BestGroupPattern> assignment)
    {
        ResetCompactCaches();
        foreach (KeyValuePair<SearchStateKey, BestGroupPattern> kv in assignment)
            CompactGroupPatternCache[kv.Key] = kv.Value;
    }

    public void ResetGreedyTightenRunState()
    {
        GreedyTightenOverrides.Clear();
        GreedyTightenOverrideAnchors.Clear();
        GreedyTightenSharedHeightMemo.Clear();
        GreedyTightenRounds = 0;
        GreedyTightenCommits = 0;
        GreedyTightenStatesVisited = 0;
        GreedyTightenCandidateGroupsTried = 0;
        GreedyTightenHeightCalls = 0;
        GreedyTightenHeightMemoHits = 0;
        GreedyTightenHeightUnderGroupCalls = 0;
        GreedyTightenCriticalShortCircuits = 0;
        GreedyTightenCommitCandidateRankSum = 0;
        GreedyTightenVisitedDepthHistogram.Clear();
        GreedyTightenCommitDepthHistogram.Clear();
    }
}