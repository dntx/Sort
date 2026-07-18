using Xunit;

public sealed class DisplayToSearchExpanderTests
{
    [Fact]
    public void BuildDisplayTreeAndExpandedSearch_ReturnsConsistentSearchAndDisplayModels()
    {
        var builder = new StrategyBuilder(12, 4, 5);
        (SearchTree searchTree, DisplayTree displayPlan) = builder.BuildDisplayTreeAndExpandedSearch();

        SearchStrategy mapped = DisplayToSearchExpander.FromStrategyPlan(displayPlan);
        AssertSearchStrategyEquivalent(mapped, searchTree);

        var baselineBuilder = new StrategyBuilder(12, 4, 5);
        StrategyPlan baselinePlan = baselineBuilder.BuildStepProofStage();
        Assert.Equal(baselinePlan.MaxStep, displayPlan.MaxStep);
        Assert.Equal(baselinePlan.TotalBranchEdges, displayPlan.TotalBranchEdges);
    }

    [Theory]
    [InlineData(9, 3, 3)]
    [InlineData(10, 4, 8)]
    [InlineData(12, 4, 5)]
    public void BuildSearchTree_ReturnsSearchModelEquivalentToMappedStepPlan(int n, int m, int k)
    {
        var stepBuilder = new StrategyBuilder(n, m, k);
        StrategyPlan stepPlan = stepBuilder.BuildStepProofStage();

        var searchBuilder = new StrategyBuilder(n, m, k);
        SearchStrategy built = searchBuilder.BuildSearchTree();

        SearchStrategy mapped = DisplayToSearchExpander.FromStrategyPlan(stepPlan);

        AssertSearchStrategyEquivalent(mapped, built);
    }

    [Theory]
    [InlineData(9, 3, 3)]
    [InlineData(10, 4, 8)]
    [InlineData(12, 4, 5)]
    public void BuildDisplayTreeAndExpandedSearch_And_BuildSearchTree_ProduceEquivalentSearchModels(int n, int m, int k)
    {
        var layeredBuilder = new StrategyBuilder(n, m, k);
        (SearchTree layeredSearch, DisplayTree _) = layeredBuilder.BuildDisplayTreeAndExpandedSearch();

        var directBuilder = new StrategyBuilder(n, m, k);
        SearchStrategy directSearch = directBuilder.BuildSearchTree();

        AssertSearchStrategyEquivalent(layeredSearch, directSearch);
    }

    [Theory]
    [InlineData(7, 3, 2)]
    [InlineData(8, 3, 3)]
    [InlineData(9, 4, 4)]
    [InlineData(10, 4, 5)]
    [InlineData(11, 4, 4)]
    [InlineData(12, 4, 5)]
    public void BuildDisplayTreeAndExpandedSearch_And_BuildSearchTree_RemainEquivalentAcrossProjectionMergingModes(int n, int m, int k)
    {
        AssertEquivalentAcrossProjectionMergingMode(n, m, k, projectionMerging: false);
        AssertEquivalentAcrossProjectionMergingMode(n, m, k, projectionMerging: true);
    }

    [Fact]
    public void FromStrategyPlan_MapsBasicMetadataAndRoot()
    {
        StrategyPlan plan = new StrategyBuilder(9, 3, 3).BuildStepProofStage();

        SearchStrategy mapped = DisplayToSearchExpander.FromStrategyPlan(plan);

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

        SearchStrategy mapped = DisplayToSearchExpander.FromStrategyPlan(plan);
        SearchNode left = mapped.Root.Branches[0].Next;
        SearchNode right = mapped.Root.Branches[1].Next;

        Assert.Same(left, right);
    }

    private static void AssertSearchStrategyEquivalent(SearchStrategy expected, SearchStrategy actual)
    {
        Assert.Equal(expected.N, actual.N);
        Assert.Equal(expected.M, actual.M);
        Assert.Equal(expected.RequestedK, actual.RequestedK);
        Assert.Equal(expected.K, actual.K);
        AssertSearchNodeEquivalent(expected.Root, actual.Root);
    }

    private static void AssertEquivalentAcrossProjectionMergingMode(int n, int m, int k, bool projectionMerging)
    {
        var layeredBuilder = new StrategyBuilder(n, m, k)
        {
            EnableProjectionOrbitMerging = projectionMerging,
        };
        (SearchTree layeredSearch, DisplayTree _) = layeredBuilder.BuildDisplayTreeAndExpandedSearch();

        var directBuilder = new StrategyBuilder(n, m, k)
        {
            EnableProjectionOrbitMerging = projectionMerging,
        };
        SearchStrategy directSearch = directBuilder.BuildSearchTree();

        AssertSearchStrategyEquivalent(layeredSearch, directSearch);
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