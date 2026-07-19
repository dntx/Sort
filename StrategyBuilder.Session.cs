using System.Collections.Generic;

sealed class StrategyBuilderSession
{
    // Phase-2: migrated statistics/diagnostics state.
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

    // Phase-3: migrated exact/compact solver caches.
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
}