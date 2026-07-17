using Xunit;

public sealed class SearchToDisplayProjectorTests
{
    [Fact]
    public void FromSearchStrategy_RoundTripsSharedReferenceTree()
    {
        StrategyPlan plan = CreatePlan();
        SearchStrategy search = DisplayToSearchExpander.FromStrategyPlan(plan);

        StrategyPlan projected = SearchToDisplayProjector.FromSearchStrategy(
            search,
            plan.SearchStatistics,
            plan.Elapsed,
            plan.IsFeasibleUpperBound);

        SearchStrategy roundTrip = DisplayToSearchExpander.FromStrategyPlan(projected);

        AssertSearchStrategyEquivalent(search, roundTrip);
        Assert.Same(projected.Root.Branches[0].Next, projected.Root.Branches[1].Next);
        Assert.Same(roundTrip.Root.Branches[0].Next, roundTrip.Root.Branches[1].Next);
    }

    private static StrategyPlan CreatePlan()
    {
        StrategyNode shared = StrategyNode.Decision(
            stateId: 2,
            step: 2,
            group: new[] { 1, 2 },
            branches: new[]
            {
                new StrategyBranch(
                    "#2 > #3",
                    null,
                    new StrategyEffect(new[] { 1 }, new[] { 2 }, new[] { 1 }, new[] { 1, 2 }),
                    StrategyNode.Terminal(3, new[] { 1 }))
            });

        StrategyNode root = StrategyNode.Decision(
            stateId: 1,
            step: 1,
            group: new[] { 0, 1 },
            branches: new[]
            {
                new StrategyBranch(
                    "#1 > #2",
                    null,
                    new StrategyEffect(new[] { 0 }, new[] { 1 }, new[] { 0 }, new[] { 0, 1 }),
                    shared),
                new StrategyBranch(
                    "#2 > #1",
                    null,
                    new StrategyEffect(new[] { 1 }, new[] { 0 }, new[] { 1 }, new[] { 0, 1 }),
                    shared)
            });

        return new StrategyPlan(
            n: 3,
            m: 2,
            requestedK: 1,
            k: 1,
            root: root,
            elapsed: System.TimeSpan.Zero,
            searchStatistics: CreateSearchStatistics());
    }

    private static SearchStatistics CreateSearchStatistics()
    {
        return new SearchStatistics(
            searchedStates: 0,
            pendingStates: 0,
            peakPendingStates: 0,
            outputStates: 0,
            expandedOutputStates: 0,
            lowerBoundStates: 0,
            feasibleTopSetStates: 0,
            diagnostics: new SearchDiagnostics(
                rootIncumbents: System.Array.Empty<SearchMilestone>(),
                lowerBoundPrunes: 0,
                duplicateOutcomeSkips: 0,
                mergedOutcomeCollisions: 0,
                exactCacheHits: 0,
                lowerBoundCacheHits: 0,
                feasibleTopSetCacheHits: 0,
                bestGroupPatternCacheHits: 0),
            phase1Milliseconds: 0,
            phase1bMilliseconds: 0,
            phase2Milliseconds: 0,
            outcomesConstructed: 0,
            candidateGroupsEnumerated: 0,
            searchTreeEdges: null,
            compactStatesSolved: 0,
            compactGroupsEnumerated: 0,
            compactStepOptimalGroups: 0,
            rootProvenLowerBound: 0);
    }

    private static void AssertSearchStrategyEquivalent(SearchStrategy expected, SearchStrategy actual)
    {
        Assert.Equal(expected.N, actual.N);
        Assert.Equal(expected.M, actual.M);
        Assert.Equal(expected.RequestedK, actual.RequestedK);
        Assert.Equal(expected.K, actual.K);
        AssertSearchNodeEquivalent(expected.Root, actual.Root);
    }

    private static void AssertSearchNodeEquivalent(SearchNode expected, SearchNode actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.StateId, actual.StateId);
        Assert.Equal(expected.Step, actual.Step);
        Assert.Equal(expected.Group, actual.Group);
        Assert.Equal(expected.TopSet, actual.TopSet);
        Assert.Equal(expected.ReferenceRelabeling, actual.ReferenceRelabeling);
        Assert.Equal(expected.Branches.Count, actual.Branches.Count);

        for (int i = 0; i < expected.Branches.Count; i++)
        {
            SearchBranch expectedBranch = expected.Branches[i];
            SearchBranch actualBranch = actual.Branches[i];

            Assert.Equal(expectedBranch.OrderText, actualBranch.OrderText);
            Assert.Equal(expectedBranch.Effect.NewlyGuaranteedTop, actualBranch.Effect.NewlyGuaranteedTop);
            Assert.Equal(expectedBranch.Effect.NewlyExcluded, actualBranch.Effect.NewlyExcluded);
            Assert.Equal(expectedBranch.Effect.FixedCandidates, actualBranch.Effect.FixedCandidates);
            Assert.Equal(expectedBranch.Effect.PossibleCandidates, actualBranch.Effect.PossibleCandidates);

            AssertSearchNodeEquivalent(expectedBranch.Next, actualBranch.Next);
        }
    }
}
