using System.Collections.Generic;
using TopKFinder;
using Xunit;

// Locks in the "what you see is what was searched" invariant: for every materialized decision node
// the displayed branches must mirror exactly the distinct successors the search expanded. Equivalent
// orderings are folded into a single branch (with a visible ×count), never dropped, and the tree must
// never show a branch the search did not process. See StrategyBuilder.CheckDisplaySearchParity.
public sealed class DisplaySearchParityTests
{
    private static readonly TimeSpan ParityTestTimeout = TimeSpan.FromSeconds(90);

    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { 6, 3, 3 },
        new object[] { 7, 3, 2 },
        new object[] { 8, 4, 2 },
        new object[] { 9, 3, 3 },
        new object[] { 9, 4, 4 },
        new object[] { 10, 5, 5 },
        new object[] { 11, 3, 3 },
        new object[] { 12, 3, 3 },
        new object[] { 8, 2, 2 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void DefaultPlan_DisplayBranchesMirrorSearchExpansion(int n, int m, int k)
        => AssertParity("default", n, m, k, builder => builder.ExecuteStepProofStage());

    [Theory]
    [MemberData(nameof(Cases))]
    public void FeasiblePlan_DisplayBranchesMirrorSearchExpansion(int n, int m, int k)
        => AssertParity("feasible", n, m, k, builder => builder.ExecuteGreedyFeasibleStage());

    [Theory]
    [MemberData(nameof(Cases))]
    public void CompactPlan_DisplayBranchesMirrorSearchExpansion(int n, int m, int k)
        => AssertParity("compact", n, m, k, builder => builder.ExecuteEdgeCompactStage());

    [Theory]
    [MemberData(nameof(Cases))]
    public void ProjectDisplayAndSearchTrees_ReturnsSearchModelEquivalentTo_ProjectSearchTree(int n, int m, int k)
    {
        var layeredBuilder = new StrategyBuilder(n, m, k);
        (SearchStrategy layeredSearch, StrategyPlan _) = layeredBuilder.ProjectDisplayAndSearchTrees();

        var directBuilder = new StrategyBuilder(n, m, k);
        SearchStrategy directSearch = directBuilder.ProjectSearchTree();

        AssertSearchStrategyEquivalent(layeredSearch, directSearch);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ProjectDisplayAndSearchTrees_ReturnsDisplayAndSearchTreesWithMatchingBackbone(int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k);
        (SearchStrategy searchTree, StrategyPlan displayPlan) = builder.ProjectDisplayAndSearchTrees();

        Assert.Equal(displayPlan.N, searchTree.N);
        Assert.Equal(displayPlan.M, searchTree.M);
        Assert.Equal(displayPlan.RequestedK, searchTree.RequestedK);
        Assert.Equal(displayPlan.K, searchTree.K);
        AssertDisplayAndSearchNodeEquivalent(displayPlan.Root, searchTree.Root);

        var baselineBuilder = new StrategyBuilder(n, m, k);
        StrategyPlan baselinePlan = baselineBuilder.ExecuteStepProofStage();
        Assert.Equal(baselinePlan.MaxStep, displayPlan.MaxStep);
        Assert.Equal(baselinePlan.TotalBranchEdges, displayPlan.TotalBranchEdges);
    }

    private static void AssertParity(string phase, int n, int m, int k, Func<StrategyBuilder, StrategyPlan> build)
    {
        List<string> residual = TestTimeoutHelper.RunWithTimeout(
            $"DisplaySearchParity {phase} ({n},{m},{k})",
            ParityTestTimeout,
            token =>
            {
                // The parity check reads the snapshots captured during the LAST build, so build and
                // check on the same builder instance before anything else can reset them.
                var builder = new StrategyBuilder(n, m, k, token);
                StrategyPlan plan = build(builder);
                return builder.CheckDisplaySearchParity(plan);
            });

        Assert.True(residual.Count == 0, string.Join("\n", residual));
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

    private static void AssertDisplayAndSearchNodeEquivalent(StrategyNode displayNode, SearchNode searchNode)
    {
        Assert.Equal(displayNode.Kind.ToString(), searchNode.Kind.ToString());
        Assert.Equal(displayNode.StateId, searchNode.StateId);
        Assert.Equal(displayNode.Step, searchNode.Step);
        Assert.Equal(displayNode.Group, searchNode.Group);
        Assert.Equal(displayNode.TopSet, searchNode.TopSet);
        Assert.Equal(displayNode.ReferenceRelabeling, searchNode.ReferenceRelabeling);

        if (displayNode.FinalChoice is not null)
        {
            Assert.Equal(StrategyNodeKind.Decision, displayNode.Kind);
            Assert.Equal(SearchNodeKind.Decision, searchNode.Kind);
            Assert.Empty(displayNode.Branches);
            Assert.Empty(searchNode.Branches);
            return;
        }

        Assert.Equal(displayNode.Branches.Count, searchNode.Branches.Count);
        for (int i = 0; i < displayNode.Branches.Count; i++)
        {
            StrategyBranch displayBranch = displayNode.Branches[i];
            SearchBranch searchBranch = searchNode.Branches[i];

            Assert.Equal(displayBranch.OrderText, searchBranch.OrderText);
            Assert.Equal(displayBranch.Effect.NewlyGuaranteedTop, searchBranch.Effect.NewlyGuaranteedTop);
            Assert.Equal(displayBranch.Effect.NewlyExcluded, searchBranch.Effect.NewlyExcluded);
            Assert.Equal(displayBranch.Effect.FixedCandidates, searchBranch.Effect.FixedCandidates);
            Assert.Equal(displayBranch.Effect.PossibleCandidates, searchBranch.Effect.PossibleCandidates);

            AssertDisplayAndSearchNodeEquivalent(displayBranch.Next, searchBranch.Next);
        }
    }
}
