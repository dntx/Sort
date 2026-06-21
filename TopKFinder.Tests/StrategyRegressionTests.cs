using Xunit;

public sealed class StrategyRegressionTests
{
    private static readonly TimeSpan RegressionTestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void N10M9K9_RemainsTwoStepPermutationCase()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(10, 9, 9)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(10, 9, 9, cancellationToken));

        Assert.Equal(2, plan.MaxStep);
        Assert.Equal(2, plan.SearchStatistics.SearchedStates);
        Assert.Equal(2, plan.SearchStatistics.OutputStates);
        StrategyBranch rootBranch = Assert.Single(plan.Root.Branches);
        Assert.Equal("#1 > #2 > #3 > #4 > #5 > #6 > #7 > #8 > #9", rootBranch.OrderText);
        Assert.NotNull(rootBranch.EquivalentOrders);
        Assert.Equal(362879, rootBranch.EquivalentOrders!.Count);
        Assert.Equal("9! - 1", rootBranch.EquivalentOrders.CountFormula);
        Assert.Equal("permute {#1, #2, #3, #4, #5, #6, #7, #8, #9}", rootBranch.EquivalentOrders.PatternText);
    }

    [Fact]
    public void N9M3K3_SearchWorkStaysWithinBaseline()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(9, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(9, 3, 3, cancellationToken));

        Assert.Equal(6, plan.MaxStep);
        Assert.True(plan.SearchStatistics.SearchedStates <= 157, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 13, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 7, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");
    }

    [Fact]
    public void N9M3K3_RootChoiceRemainsCanonicalOpeningGroup()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(9, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(9, 3, 3, cancellationToken));

        Assert.Equal(new[] { 0, 1, 2 }, plan.Root.Group);

        StrategyBranch rootBranch = Assert.Single(plan.Root.Branches);
        Assert.Equal("#1 > #2 > #3", rootBranch.OrderText);
        Assert.NotNull(rootBranch.EquivalentOrders);
        Assert.Equal(5, rootBranch.EquivalentOrders!.Count);
        Assert.Equal("3! - 1", rootBranch.EquivalentOrders.CountFormula);
        Assert.Equal("permute {#1, #2, #3}", rootBranch.EquivalentOrders.PatternText);

        Assert.Equal(new[] { 3, 4, 5 }, rootBranch.Next.Group);
    }

    [Fact]
    public void N12M3K3_SearchWorkStaysWithinBaseline()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(12, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(12, 3, 3, cancellationToken));

        Assert.Equal(7, plan.MaxStep);
        Assert.True(plan.SearchStatistics.SearchedStates <= 1442, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 15, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 8, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");
    }

    [Fact]
    public void N12M4K4_PreservesRepresentativeAliasCompression()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(12, 4, 4)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(12, 4, 4, cancellationToken));

        Assert.Equal(5, plan.MaxStep);
        Assert.True(plan.SearchStatistics.SearchedStates <= 1136, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 29, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 9, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");

        StrategyBranch branch = StrategyTestHelpers.FindBranchByOrderText(plan.Root, "#2 > #6 > #9 > #10");
        Assert.NotNull(branch.EquivalentOrders);
        Assert.Equal(3, branch.EquivalentOrders!.Count);
        Assert.Equal("2 x 2! - 1", branch.EquivalentOrders.CountFormula);
        Assert.Contains("permute{#9, #10}", branch.EquivalentOrders.PatternText);
        Assert.Contains("#2 > #6", branch.EquivalentOrders.PatternText);
        Assert.Contains("#6 > #2", branch.EquivalentOrders.PatternText);
    }

    [Fact]
    public void N12M3K3_DoesNotCreateSelfReferentialBranches()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(12, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(12, 3, 3, cancellationToken));

        foreach (StrategyNode node in StrategyTestHelpers.EnumerateDecisionNodes(plan.Root))
        {
            foreach (StrategyBranch branch in node.Branches)
            {
                Assert.False(
                    branch.Next.Kind == StrategyNodeKind.Reference && branch.Next.StateId == node.StateId,
                    $"Node S{node.StateId} contains a self-referential branch '{branch.OrderText}'.");
            }
        }
    }
}

internal static class StrategyTestHelpers
{
    public static StrategyBranch FindBranchByOrderText(StrategyNode root, string orderText)
    {
        foreach (StrategyBranch branch in EnumerateBranches(root))
        {
            if (branch.OrderText == orderText)
                return branch;
        }

        throw new Xunit.Sdk.XunitException($"Could not find branch with order text '{orderText}'.");
    }

    public static IEnumerable<StrategyNode> EnumerateDecisionNodes(StrategyNode root)
    {
        foreach (StrategyNode node in EnumerateNodes(root))
        {
            if (node.Kind == StrategyNodeKind.Decision)
                yield return node;
        }
    }

    private static IEnumerable<StrategyBranch> EnumerateBranches(StrategyNode node)
    {
        foreach (StrategyBranch branch in node.Branches)
        {
            yield return branch;

            if (branch.Next.Kind != StrategyNodeKind.Reference)
            {
                foreach (StrategyBranch nested in EnumerateBranches(branch.Next))
                    yield return nested;
            }
        }
    }

    private static IEnumerable<StrategyNode> EnumerateNodes(StrategyNode node)
    {
        yield return node;

        foreach (StrategyBranch branch in node.Branches)
        {
            if (branch.Next.Kind != StrategyNodeKind.Reference)
            {
                foreach (StrategyNode nested in EnumerateNodes(branch.Next))
                    yield return nested;
            }
        }
    }
}
