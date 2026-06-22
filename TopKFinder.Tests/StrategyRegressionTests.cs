using System.Collections.Generic;
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
        Assert.True(plan.SearchStatistics.SearchedStates <= 117, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 22, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 13, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");
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

        Assert.Equal(new[] { 0, 1, 3 }, rootBranch.Next.Group);
    }

    [Fact]
    public void N12M3K3_SearchWorkStaysWithinBaseline()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(12, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(12, 3, 3, cancellationToken));

        Assert.Equal(7, plan.MaxStep);
        Assert.True(plan.SearchStatistics.SearchedStates <= 1010, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 15, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 8, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");
    }

    [Fact]
    public void N10M3K5_SearchWorkStaysWithinWideTopKBaseline()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(10, 3, 5)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(10, 3, 5, cancellationToken));

        Assert.Equal(6, plan.MaxStep);
        Assert.True(plan.SearchStatistics.SearchedStates <= 1311, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 8, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 5, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");
    }

    [Fact]
    public void N12M4K5_SearchWorkStaysWithinWideTopKBaseline()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(12, 4, 5)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(12, 4, 5, cancellationToken));

        Assert.Equal(6, plan.MaxStep);
        Assert.True(plan.SearchStatistics.SearchedStates <= 1562, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 20, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 8, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");
    }

    [Fact]
    public void Builder_ProducesDeterministicOutputAcrossRuns()
    {
        foreach ((int n, int m, int k) in new[] { (9, 3, 3), (12, 3, 3), (10, 3, 5) })
        {
            StrategyPlan first = TestTimeoutHelper.RunWithTimeout(
                $"StrategyBuilder.Generate({n}, {m}, {k}) first",
                RegressionTestTimeout,
                cancellationToken => StrategyBuilder.Generate(n, m, k, cancellationToken));
            StrategyPlan second = TestTimeoutHelper.RunWithTimeout(
                $"StrategyBuilder.Generate({n}, {m}, {k}) second",
                RegressionTestTimeout,
                cancellationToken => StrategyBuilder.Generate(n, m, k, cancellationToken));

            string firstRendered = StrategyTestHelpers.NormalizeRenderedSnapshot(StrategyTextRenderer.Render(first));
            string secondRendered = StrategyTestHelpers.NormalizeRenderedSnapshot(StrategyTextRenderer.Render(second));

            Assert.Equal(firstRendered, secondRendered);
            Assert.Equal(first.MaxStep, second.MaxStep);
            Assert.NotEmpty(first.SearchStatistics.Diagnostics.RootIncumbents);
        }
    }

    [Fact]
    public void N10M3K5_RecordsDescendingRootIncumbentMilestones()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(10, 3, 5)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(10, 3, 5, cancellationToken));

        SearchDiagnostics diagnostics = plan.SearchStatistics.Diagnostics;
        Assert.NotEmpty(diagnostics.RootIncumbents);
        Assert.Equal(plan.MaxStep, diagnostics.RootIncumbents[^1].BestWorstCaseSteps);
        Assert.True(diagnostics.LowerBoundPrunes > 0);

        int previousSteps = int.MaxValue;
        foreach (SearchMilestone milestone in diagnostics.RootIncumbents)
        {
            Assert.True(milestone.BestWorstCaseSteps < previousSteps, "root incumbent steps should strictly decrease");
            previousSteps = milestone.BestWorstCaseSteps;
        }
    }

    [Fact]
    public void N5M3K2_ProgressSnapshotsExposeLiveDiagnostics()
    {
        var snapshots = new List<SearchProgressSnapshot>();
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(5, 3, 2) with progress",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(5, 3, 2, cancellationToken, snapshot => snapshots.Add(snapshot)));

        Assert.NotEmpty(snapshots);
        Assert.True(snapshots.Exists(snapshot => snapshot.RootIncumbentCount > 0));

        SearchProgressSnapshot finalSnapshot = snapshots[^1];
        Assert.Equal(plan.SearchStatistics.SearchedStates, finalSnapshot.SearchedStates);
        Assert.Equal(plan.SearchStatistics.OutputStates, finalSnapshot.OutputStates);
        Assert.Equal(plan.SearchStatistics.Diagnostics.RootIncumbents.Count, finalSnapshot.RootIncumbentCount);
        Assert.NotNull(finalSnapshot.LatestRootIncumbent);
        Assert.Equal(plan.MaxStep, finalSnapshot.LatestRootIncumbent!.BestWorstCaseSteps);
    }

    [Fact]
    public void N12M4K4_PreservesRepresentativeAliasCompression()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(12, 4, 4)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(12, 4, 4, cancellationToken));

        Assert.Equal(5, plan.MaxStep);
        Assert.True(plan.SearchStatistics.SearchedStates <= 551, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 23, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 12, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");

        StrategyBranch branch = StrategyTestHelpers.FindBranchByOrderText(plan.Root, "#2 > #5 > #8 > #9");
        Assert.NotNull(branch.EquivalentOrders);
        Assert.Equal(3, branch.EquivalentOrders!.Count);
        Assert.Equal("2 x 2! - 1", branch.EquivalentOrders.CountFormula);
        Assert.Contains("permute{#8, #9}", branch.EquivalentOrders.PatternText);
        Assert.Contains("#2 > #5", branch.EquivalentOrders.PatternText);
        Assert.Contains("#5 > #2", branch.EquivalentOrders.PatternText);
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
            pending states = 0 (peak 2)
            output states = 3 (expanded 2)

            S1 [step 1] sort(#1, #2, #3)
              #1 > #2 > #3: [+in -, +out (#3), fixed -, possible (#1, #2, #4, #5)]
                equivalent forms: 5 = 3! - 1
                pattern: permute {#1, #2, #3}
                S2 [step 2] sort(#1, #2, #4)
                  #1 > #2 > #4: [+in (#1), +out (#4), fixed (#1), possible (#2, #5)]
                    equivalent forms: 2 = 3 - 1
                    pattern: (#1 > #2 > #4 | #1 > #4 > #2 | #4 > #1 > #2)
                    [step 3] sort(#2, #5)
                      fixed (#1); choose 1 of (#2, #5) into top 2
            """;

        Assert.Equal(StrategyTestHelpers.NormalizeRenderedSnapshot(expected), rendered);
    }

    [Fact]
    public void N12M3K3_DecisionTransitionEffectRemainsStable()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(12, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(12, 3, 3, cancellationToken));

        StrategyBranch branch = StrategyTestHelpers.FindBranchPath(
            plan.Root,
            "#1 > #2 > #3",
            "#4 > #5 > #6",
            "#7 > #8 > #9",
            "#1 > #4 > #7",
            "#10 > #2 > #11");

        Assert.Equal(new[] { 0 }, branch.Effect.NewlyGuaranteedTop);
        Assert.Equal(new[] { 2, 10 }, branch.Effect.NewlyExcluded);
        Assert.Equal(new[] { 0 }, branch.Effect.FixedCandidates);
        Assert.Equal(new[] { 1, 3, 4, 6, 9, 11 }, branch.Effect.PossibleCandidates);

        Assert.Equal(StrategyNodeKind.Decision, branch.Next.Kind);
        Assert.Equal(new[] { 3, 4, 9 }, branch.Next.Group);
    }

    [Fact]
    public void N9M3K3_TerminalTransitionEffectRemainsStable()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(9, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(9, 3, 3, cancellationToken));

        StrategyBranch branch = StrategyTestHelpers.FindBranchPath(
            plan.Root,
            "#1 > #2 > #3",
            "#1 > #2 > #4",
            "#1 > #5 > #6",
            "#5 > #7 > #8",
            "#9 > #2 > #5");

        Assert.Equal(new[] { 1, 8 }, branch.Effect.NewlyGuaranteedTop);
        Assert.Equal(new[] { 2, 3, 4, 5, 6 }, branch.Effect.NewlyExcluded);
        Assert.Equal(new[] { 0, 1, 8 }, branch.Effect.FixedCandidates);
        Assert.Empty(branch.Effect.PossibleCandidates);

        Assert.Equal(StrategyNodeKind.Terminal, branch.Next.Kind);
        Assert.Equal(new[] { 0, 1, 8 }, branch.Next.TopSet);
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
            "#1 > #3",
            "#1 > #4",
            "#5 > #6",
            "#5 > #7",
            "#8 > #9",
            "#5 > #8",
            "#1 > #5");

        StrategyBranch branch = StrategyTestHelpers.FindBranchPath(
            plan.Root,
            "#1 > #2",
            "#1 > #3",
            "#1 > #4",
            "#5 > #6",
            "#5 > #7",
            "#8 > #9",
            "#8 > #5",
            "#1 > #8");

        Assert.Equal(new[] { 0 }, branch.Effect.NewlyGuaranteedTop);
        Assert.Equal(new[] { 4, 8 }, branch.Effect.NewlyExcluded);
        Assert.Equal(new[] { 0 }, branch.Effect.FixedCandidates);
        Assert.Equal(new[] { 1, 2, 3, 7, 9 }, branch.Effect.PossibleCandidates);

        Assert.Equal(StrategyNodeKind.Reference, branch.Next.Kind);
        Assert.Equal(referenceTarget.StateId, branch.Next.StateId);
        Assert.Equal(StrategyNodeKind.Decision, referenceTarget.Kind);
        Assert.Equal(new[] { 1, 2 }, referenceTarget.Group);
    }

    [Fact]
    public void N12M3K3_AfterInitialPrefixesChoosesCrossBlockGroup()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(12, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(12, 3, 3, cancellationToken));

        StrategyNode node = StrategyTestHelpers.FollowBranchPath(
            plan.Root,
            "#1 > #2 > #3",
            "#4 > #5 > #6",
            "#7 > #8 > #9");

        Assert.Equal(new[] { 0, 3, 6 }, node.Group);
    }

    [Fact]
    public void N12M3K3_WhenLeadingCandidateFixedChoosesBalancedFollowUpGroup()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(12, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(12, 3, 3, cancellationToken));

        StrategyNode node = StrategyTestHelpers.FollowBranchPath(
            plan.Root,
            "#1 > #2 > #3",
            "#4 > #5 > #6",
            "#7 > #8 > #9",
            "#1 > #4 > #7",
            "#10 > #2 > #11");

        Assert.Equal(new[] { 3, 4, 9 }, node.Group);
    }

    [Fact]
    public void N10M2K2_AfterInitialComparisonsChainsKingOfTheHillGroups()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(10, 2, 2)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(10, 2, 2, cancellationToken));

        StrategyNode afterFirstComparison = StrategyTestHelpers.FollowBranchPath(
            plan.Root,
            "#1 > #2");
        Assert.Equal(new[] { 0, 2 }, afterFirstComparison.Group);

        StrategyNode afterSecondComparison = StrategyTestHelpers.FollowBranchPath(
            plan.Root,
            "#1 > #2",
            "#1 > #3");
        Assert.Equal(new[] { 0, 3 }, afterSecondComparison.Group);
    }

    [Fact]
    public void N12M4K4_RenderedAliasCompressionSectionMatchesSnapshot()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(12, 4, 4)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(12, 4, 4, cancellationToken));

        string rendered = StrategyTestHelpers.NormalizeRenderedSnapshot(StrategyTextRenderer.Render(plan));
        string excerpt = StrategyTestHelpers.ExtractRenderedSection(
            rendered,
            "        S3 [step 3] sort(#2, #5, #8, #9)",
            "              #10 > #3 > #5 > #11");

        const string expected = """
                    S3 [step 3] sort(#2, #5, #8, #9)
                      #2 > #5 > #8 > #9: [+in (#1), +out (#7, #9), fixed (#1), possible (#2, #3, #4, #5, #6, #8, #10, #11, #12)]
                        equivalent forms: 3 = 2 x 2! - 1
                        pattern: (C=permute{#8, #9}; #2 > #5 > C1 > C2 | C=permute{#8, #9}; #5 > #2 > C1 > C2)
                        S4 [step 4] sort(#3, #5, #10, #11)
                          #10 > #11 > #3 > #5: [+in (#10), +out (#3, #4, #5, #6, #8), fixed (#1, #10), possible (#2, #11, #12)]
                            equivalent forms: 3 = 2 x 2! - 1
                            pattern: (C=permute{#10, #11}; C1 > C2 > #3 > #5 | C=permute{#10, #11}; C1 > C2 > #5 > #3)
                            [step 5] sort(#2, #11, #12)
                              fixed (#1, #10); choose 2 of (#2, #11, #12) into top 4
            """;

        Assert.Equal(StrategyTestHelpers.NormalizeRenderedSnapshot(expected), excerpt);
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

    public static string ExtractRenderedSection(string rendered, string startLinePrefix, string endLinePrefixExclusive)
    {
        string[] lines = NormalizeRenderedSnapshot(rendered).Split('\n');
        int startIndex = Array.FindIndex(lines, line => line.StartsWith(startLinePrefix, StringComparison.Ordinal));
        if (startIndex < 0)
            throw new Xunit.Sdk.XunitException($"Could not find rendered line starting with '{startLinePrefix}'.");

        int endIndex = Array.FindIndex(lines, startIndex + 1, lines.Length - startIndex - 1, line => line.StartsWith(endLinePrefixExclusive, StringComparison.Ordinal));
        if (endIndex < 0)
            throw new Xunit.Sdk.XunitException($"Could not find rendered line starting with '{endLinePrefixExclusive}'.");

        if (endIndex <= startIndex)
            throw new Xunit.Sdk.XunitException("Rendered section end must appear after the start line.");

        return string.Join("\n", lines[startIndex..endIndex]);
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
