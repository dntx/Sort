using System.Collections.Generic;
using Xunit;

public sealed class StrategyBuilderSessionTests
{
    [Fact]
    public void ResetPerBuildTransientState_ClearsTransientCounters_AndKeepsLongLivedCaches()
    {
        var session = new StrategyBuilderSession();
        SearchStateKey key = MakeSearchStateKey(1, 2, 3);

        session.VisitedSearchStates.Add(key);
        session.LowerBoundPrunes = 3;
        session.DuplicateOutcomeSkips = 4;
        session.ExactCacheHits = 5;
        session.BestGroupPatternCacheHits = 6;
        session.OutcomesConstructed = 7;
        session.CompactStatesSolved = 8;
        session.MinWorstCaseStepsCache[key] = 10;
        session.LowerBoundStepsCache[key] = 11;

        session.ResetPerBuildTransientState();

        Assert.Empty(session.VisitedSearchStates);
        Assert.Equal(0, session.LowerBoundPrunes);
        Assert.Equal(0, session.DuplicateOutcomeSkips);
        Assert.Equal(0, session.ExactCacheHits);
        Assert.Equal(0, session.BestGroupPatternCacheHits);
        Assert.Equal(0, session.OutcomesConstructed);
        Assert.Equal(0, session.CompactStatesSolved);

        Assert.Single(session.MinWorstCaseStepsCache);
        Assert.Single(session.LowerBoundStepsCache);
    }

    [Fact]
    public void ResetCompactCaches_ClearsOnlyCompactCacheContainers()
    {
        var session = new StrategyBuilderSession();
        SearchStateKey key = MakeSearchStateKey(4, 5, 6);

        session.CompactGroupPatternCache[key] = new BestGroupPattern(2, new IntSequenceKey(new[] { 1, 2 }));
        session.CompactGroupPatternTightestBudget[key] = 9;
        session.CompactCostMemo[(key, 9)] = 12;
        session.CompactRealStepsMemo[key] = 13;
        session.BestGroupPatternCache[key] = new BestGroupPattern(3, new IntSequenceKey(new[] { 2, 3, 4 }));

        session.ResetCompactCaches();

        Assert.Empty(session.CompactGroupPatternCache);
        Assert.Empty(session.CompactGroupPatternTightestBudget);
        Assert.Empty(session.CompactCostMemo);
        Assert.Empty(session.CompactRealStepsMemo);
        Assert.Single(session.BestGroupPatternCache);
    }

    [Fact]
    public void LoadCompactPatternAssignment_ReplacesExistingCompactPatterns()
    {
        var session = new StrategyBuilderSession();
        SearchStateKey oldKey = MakeSearchStateKey(1, 1, 1);
        SearchStateKey newKey = MakeSearchStateKey(2, 2, 2);

        session.CompactGroupPatternCache[oldKey] = new BestGroupPattern(2, new IntSequenceKey(new[] { 9, 9 }));
        session.CompactGroupPatternTightestBudget[oldKey] = 7;

        var assignment = new Dictionary<SearchStateKey, BestGroupPattern>
        {
            [newKey] = new BestGroupPattern(3, new IntSequenceKey(new[] { 7, 8, 9 }))
        };

        session.LoadCompactPatternAssignment(assignment);

        Assert.Single(session.CompactGroupPatternCache);
        Assert.True(session.CompactGroupPatternCache.ContainsKey(newKey));
        Assert.False(session.CompactGroupPatternCache.ContainsKey(oldKey));
        Assert.Empty(session.CompactGroupPatternTightestBudget);
        Assert.Empty(session.CompactCostMemo);
        Assert.Empty(session.CompactRealStepsMemo);
    }

    [Fact]
    public void ResetGreedyTightenRunState_ClearsMutableRunState()
    {
        var session = new StrategyBuilderSession();
        SearchStateKey key = MakeSearchStateKey(8, 9, 10);

        session.GreedyTightenOverrides[key] = new List<int> { 0, 1 };
        session.GreedyTightenOverrideAnchors[key] = new ComparisonState(6);
        session.GreedyTightenSharedHeightMemo[key] = 5;
        session.GreedyTightenRounds = 1;
        session.GreedyTightenCommits = 2;
        session.GreedyTightenStatesVisited = 3;
        session.GreedyTightenCandidateGroupsTried = 4;
        session.GreedyTightenHeightCalls = 5;
        session.GreedyTightenHeightMemoHits = 6;
        session.GreedyTightenHeightUnderGroupCalls = 7;
        session.GreedyTightenCriticalShortCircuits = 8;
        session.GreedyTightenCommitCandidateRankSum = 9;
        session.GreedyTightenVisitedDepthHistogram[2] = 10;
        session.GreedyTightenCommitDepthHistogram[3] = 11;

        session.ResetGreedyTightenRunState();

        Assert.Empty(session.GreedyTightenOverrides);
        Assert.Empty(session.GreedyTightenOverrideAnchors);
        Assert.Empty(session.GreedyTightenSharedHeightMemo);
        Assert.Equal(0, session.GreedyTightenRounds);
        Assert.Equal(0, session.GreedyTightenCommits);
        Assert.Equal(0, session.GreedyTightenStatesVisited);
        Assert.Equal(0, session.GreedyTightenCandidateGroupsTried);
        Assert.Equal(0, session.GreedyTightenHeightCalls);
        Assert.Equal(0, session.GreedyTightenHeightMemoHits);
        Assert.Equal(0, session.GreedyTightenHeightUnderGroupCalls);
        Assert.Equal(0, session.GreedyTightenCriticalShortCircuits);
        Assert.Equal(0, session.GreedyTightenCommitCandidateRankSum);
        Assert.Empty(session.GreedyTightenVisitedDepthHistogram);
        Assert.Empty(session.GreedyTightenCommitDepthHistogram);
    }

    private static SearchStateKey MakeSearchStateKey(params int[] values)
        => new(values.Length, new IntSequenceKey(values));
}
