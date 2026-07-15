using Xunit;

public sealed class SearchModelMapperTests
{
    [Fact]
    public void FromStrategyPlan_MapsBasicMetadataAndRoot()
    {
        StrategyPlan plan = new StrategyBuilder(9, 3, 3).BuildStepProofStage();

        SearchStrategy mapped = SearchModelMapper.FromStrategyPlan(plan);

        Assert.Equal(plan.N, mapped.N);
        Assert.Equal(plan.M, mapped.M);
        Assert.Equal(plan.RequestedK, mapped.RequestedK);
        Assert.Equal(plan.K, mapped.K);
        Assert.Equal(plan.Root.Kind.ToString(), mapped.Root.Kind.ToString());
        Assert.Equal(plan.Root.StateId, mapped.Root.StateId);
    }

    [Fact]
    public void FromStrategyPlan_ReusesMappedNodeIdentityForSharedReferences()
    {
        // Build a tiny tree where two branches reference the same child decision node.
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

        StrategyPlan plan = new(
            n: 3,
            m: 2,
            requestedK: 1,
            k: 1,
            root: root,
            elapsed: System.TimeSpan.Zero,
            searchStatistics: new SearchStatistics(
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
                rootProvenLowerBound: 0));

        SearchStrategy mapped = SearchModelMapper.FromStrategyPlan(plan);
        SearchNode left = mapped.Root.Branches[0].Next;
        SearchNode right = mapped.Root.Branches[1].Next;

        Assert.Same(left, right);
    }
}