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

    [Fact]
    public void N5M3K2_RenderedTextMatchesSnapshot()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(5, 3, 2)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(5, 3, 2, cancellationToken));

        string rendered = StrategyTestHelpers.NormalizeRenderedSnapshot(StrategyTextRenderer.Render(plan));
        const string expected = """
            n=5, m=3, k=2
            elapsed = <elapsed>
            max step = 3
            searched states = 4
            pending states = 0 (peak 1)
            output states = 4 (expanded 2)

            S1 [step 1] sort(#1, #2, #3)
              #1 > #2 > #3: [in -, out (#3), fixed -, possible (#1, #2, #4, #5)]
                equivalent forms: 5 = 3! - 1
                pattern: permute {#1, #2, #3}
                S2 [step 2] sort(#1, #4, #5)
                  #1 > #4 > #5: [in (#1), out (#5), fixed (#1), possible (#2, #4)]
                    equivalent forms: 1 = 2! - 1
                    pattern: B=permute{#4, #5}; #1 > B1 > B2
                    S3 [step 3] sort(#2, #4)
                      fixed (#1); choose 1 of (#2, #4) into top 2
                  #4 > #1 > #5: [in (#1, #4), out (#2, #5), fixed (#1, #4), possible -] S4: top 2 = (#1, #4)
                    equivalent forms: 3 = 2 x 2! - 1
                    pattern: (B=permute{#4, #5}; B1 > #1 > B2 | B=permute{#4, #5}; B1 > B2 > #1)
            """;

        Assert.Equal(StrategyTestHelpers.NormalizeRenderedSnapshot(expected), rendered);
    }

    [Fact]
    public void N5M3K2_DecisionTransitionEffectRemainsStable()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(5, 3, 2)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(5, 3, 2, cancellationToken));

        StrategyBranch branch = Assert.Single(plan.Root.Branches);

        Assert.Empty(branch.Effect.NewlyGuaranteedTop);
        Assert.Equal(new[] { 2 }, branch.Effect.NewlyExcluded);
        Assert.Empty(branch.Effect.FixedCandidates);
        Assert.Equal(new[] { 0, 1, 3, 4 }, branch.Effect.PossibleCandidates);

        Assert.Equal(StrategyNodeKind.Decision, branch.Next.Kind);
        Assert.Equal(new[] { 0, 3, 4 }, branch.Next.Group);
    }

    [Fact]
    public void N5M3K2_TerminalTransitionEffectRemainsStable()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(5, 3, 2)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(5, 3, 2, cancellationToken));

        StrategyBranch branch = StrategyTestHelpers.FindBranchPath(
            plan.Root,
            "#1 > #2 > #3",
            "#4 > #1 > #5");

        Assert.Equal(new[] { 0, 3 }, branch.Effect.NewlyGuaranteedTop);
        Assert.Equal(new[] { 1, 4 }, branch.Effect.NewlyExcluded);
        Assert.Equal(new[] { 0, 3 }, branch.Effect.FixedCandidates);
        Assert.Empty(branch.Effect.PossibleCandidates);

        Assert.Equal(StrategyNodeKind.Terminal, branch.Next.Kind);
        Assert.Equal(new[] { 0, 3 }, branch.Next.TopSet);
    }

    [Fact]
    public void N10M2K2_ReferenceTransitionEffectRemainsStable()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(10, 2, 2)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(10, 2, 2, cancellationToken));

        StrategyNode referenceTarget = StrategyTestHelpers.FollowBranchPath(
            plan.Root,
            "#1 > #2",
            "#3 > #4",
            "#5 > #6",
            "#7 > #8",
            "#9 > #10",
            "#1 > #3",
            "#2 > #5");

        StrategyBranch branch = StrategyTestHelpers.FindBranchPath(
            plan.Root,
            "#1 > #2",
            "#3 > #4",
            "#5 > #6",
            "#7 > #8",
            "#9 > #10",
            "#1 > #3",
            "#5 > #2",
            "#1 > #5");

        Assert.Empty(branch.Effect.NewlyGuaranteedTop);
        Assert.Equal(new[] { 5 }, branch.Effect.NewlyExcluded);
        Assert.Empty(branch.Effect.FixedCandidates);
        Assert.Equal(new[] { 0, 2, 4, 6, 7, 8, 9 }, branch.Effect.PossibleCandidates);

        Assert.Equal(StrategyNodeKind.Reference, branch.Next.Kind);
        Assert.Equal(referenceTarget.StateId, branch.Next.StateId);
        Assert.Equal(StrategyNodeKind.Decision, referenceTarget.Kind);
        Assert.Equal(new[] { 6, 8 }, referenceTarget.Group);
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

    public static StrategyBranch FindChildBranch(StrategyNode node, string orderText)
    {
        foreach (StrategyBranch branch in node.Branches)
        {
            if (branch.OrderText == orderText)
                return branch;
        }

        throw new Xunit.Sdk.XunitException($"Could not find child branch with order text '{orderText}' from node S{node.StateId}.");
    }

    public static StrategyBranch FindBranchPath(StrategyNode root, params string[] orderTexts)
    {
        StrategyNode current = root;
        StrategyBranch? branch = null;
        foreach (string orderText in orderTexts)
        {
            branch = FindChildBranch(current, orderText);
            current = branch.Next;
        }

        return branch ?? throw new Xunit.Sdk.XunitException("Branch path must contain at least one order text.");
    }

    public static StrategyNode FollowBranchPath(StrategyNode root, params string[] orderTexts)
    {
        StrategyNode current = root;
        foreach (string orderText in orderTexts)
            current = FindChildBranch(current, orderText).Next;
        return current;
    }

    public static IEnumerable<StrategyNode> EnumerateDecisionNodes(StrategyNode root)
    {
        foreach (StrategyNode node in EnumerateNodes(root))
        {
            if (node.Kind == StrategyNodeKind.Decision)
                yield return node;
        }
    }

    public static string NormalizeRenderedSnapshot(string rendered)
    {
        string normalized = rendered.Replace("\r\n", "\n").TrimEnd();
        string[] lines = normalized.Split('\n');
        if (lines.Length > 1 && lines[1].StartsWith("elapsed = ", StringComparison.Ordinal))
            lines[1] = "elapsed = <elapsed>";
        return string.Join("\n", lines);
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
