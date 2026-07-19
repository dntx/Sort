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
}