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
        Assert.True(plan.SearchStatistics.OutputStates <= 24, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 8, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");
    }

    [Fact]
    public void TwoPhaseBuilder_PreservesOptimalStepOnRepresentativeCases()
    {
        foreach ((int n, int m, int k) in new[] { (9, 3, 3), (12, 3, 3), (10, 3, 5) })
        {
            StrategyPlan baseline = TestTimeoutHelper.RunWithTimeout(
                $"StrategyBuilder.Generate({n}, {m}, {k}) baseline",
                RegressionTestTimeout,
                cancellationToken => StrategyBuilder.Generate(n, m, k, cancellationToken));
            StrategyPlan twoPhase = TestTimeoutHelper.RunWithTimeout(
                $"StrategyBuilder.GenerateTwoPhase({n}, {m}, {k})",
                RegressionTestTimeout,
                cancellationToken => StrategyBuilder.GenerateTwoPhase(n, m, k, cancellationToken));

            Assert.Equal(baseline.MaxStep, twoPhase.MaxStep);
            Assert.NotEmpty(twoPhase.SearchStatistics.Diagnostics.RootIncumbents);
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

        Assert.Equal(new[] { 3, 9, 11 }, node.Group);
    }

    [Fact]
    public void N10M2K2_AfterPairwisePrefixChoosesCrossPairComparisons()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(10, 2, 2)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(10, 2, 2, cancellationToken));

        StrategyNode firstCrossPairNode = StrategyTestHelpers.FollowBranchPath(
            plan.Root,
            "#1 > #2",
            "#3 > #4",
            "#5 > #6",
            "#7 > #8",
            "#9 > #10");
        Assert.Equal(new[] { 0, 2 }, firstCrossPairNode.Group);

        StrategyNode secondCrossPairNode = StrategyTestHelpers.FollowBranchPath(
            plan.Root,
            "#1 > #2",
            "#3 > #4",
            "#5 > #6",
            "#7 > #8",
            "#9 > #10",
            "#1 > #3");
        Assert.Equal(new[] { 1, 4 }, secondCrossPairNode.Group);
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
            "        S3 [step 3] sort(#2, #6, #9, #10)",
            "              #11 > #5 > #3 > #12");

        const string expected = """
                    S3 [step 3] sort(#2, #6, #9, #10)
                      #2 > #6 > #9 > #10: [in (#1), out (#7, #8, #9, #10), fixed (#1), possible (#2, #3, #4, #5, #6, #11, #12)]
                        equivalent forms: 3 = 2 x 2! - 1
                        pattern: (C=permute{#9, #10}; #2 > #6 > C1 > C2 | C=permute{#9, #10}; #6 > #2 > C1 > C2)
                        S4 [step 4] sort(#3, #5, #11, #12)
                          #11 > #3 > #5 > #12: [in (#2, #3, #11), out (#4, #5, #6, #12), fixed (#1, #2, #3, #11), possible -] S5: top 4 = (#1, #2, #3, #11)
                            equivalent forms: 3 = 2 x 2! - 1
                            pattern: (C=permute{#11, #12}; C1 > #3 > #5 > C2 | C=permute{#11, #12}; C1 > #3 > C2 > #5)
                          #11 > #5 > #12 > #3: [in (#5, #11), out (#3, #4, #6), fixed (#1, #5, #11), possible (#2, #12)]
                            equivalent forms: 3 = 2 x 2! - 1
                            pattern: (C=permute{#11, #12}; C1 > #5 > C2 > #3 | C=permute{#11, #12}; C1 > C2 > #5 > #3)
                            S6 [step 5] sort(#2, #12)
                              fixed (#1, #5, #11); choose 1 of (#2, #12) into top 4
            """;

        Assert.Equal(StrategyTestHelpers.NormalizeRenderedSnapshot(expected), excerpt);
    }

    [Fact]
    public void ComparisonGroupScore_PrefersLowerWorstCaseStepsBeforeOtherMetrics()
    {
        StrategyBuilder.ComparisonGroupScore better = StrategyTestHelpers.CreateComparisonGroupScore(
            worstCaseSteps: 3,
            freshItems: 0,
            unrelatedScore: 0,
            groupSize: 0,
            distinctStates: 0,
            totalReduction: 0,
            unresolvedPairs: 0);
        StrategyBuilder.ComparisonGroupScore worse = StrategyTestHelpers.CreateComparisonGroupScore(
            worstCaseSteps: 4,
            freshItems: 10,
            unrelatedScore: 10,
            groupSize: 10,
            distinctStates: 10,
            totalReduction: 10,
            unresolvedPairs: 10);

        Assert.True(better.CompareTo(worse) > 0);
    }

    [Fact]
    public void ComparisonGroupScore_UsesFreshItemsAsFirstTieBreaker()
    {
        StrategyBuilder.ComparisonGroupScore better = StrategyTestHelpers.CreateComparisonGroupScore(
            worstCaseSteps: 4,
            freshItems: 3,
            unrelatedScore: 0,
            groupSize: 0,
            distinctStates: 0,
            totalReduction: 0,
            unresolvedPairs: 0);
        StrategyBuilder.ComparisonGroupScore worse = StrategyTestHelpers.CreateComparisonGroupScore(
            worstCaseSteps: 4,
            freshItems: 2,
            unrelatedScore: 10,
            groupSize: 10,
            distinctStates: 10,
            totalReduction: 10,
            unresolvedPairs: 10);

        Assert.True(better.CompareTo(worse) > 0);
    }

    [Fact]
    public void ComparisonGroupScore_PrefersMoreUnrelatedItemsBeforeLargerGroup()
    {
        StrategyBuilder.ComparisonGroupScore better = StrategyTestHelpers.CreateComparisonGroupScore(
            worstCaseSteps: 4,
            freshItems: 2,
            unrelatedScore: -1,
            groupSize: 2,
            distinctStates: 0,
            totalReduction: 0,
            unresolvedPairs: 0);
        StrategyBuilder.ComparisonGroupScore worse = StrategyTestHelpers.CreateComparisonGroupScore(
            worstCaseSteps: 4,
            freshItems: 2,
            unrelatedScore: -2,
            groupSize: 99,
            distinctStates: 99,
            totalReduction: 99,
            unresolvedPairs: 99);

        Assert.True(better.CompareTo(worse) > 0);
    }

    [Fact]
    public void ComparisonGroupScore_PrefersLargerGroupBeforeDistinctStates()
    {
        StrategyBuilder.ComparisonGroupScore better = StrategyTestHelpers.CreateComparisonGroupScore(
            worstCaseSteps: 4,
            freshItems: 2,
            unrelatedScore: -2,
            groupSize: 4,
            distinctStates: 1,
            totalReduction: 0,
            unresolvedPairs: 0);
        StrategyBuilder.ComparisonGroupScore worse = StrategyTestHelpers.CreateComparisonGroupScore(
            worstCaseSteps: 4,
            freshItems: 2,
            unrelatedScore: -2,
            groupSize: 3,
            distinctStates: 99,
            totalReduction: 99,
            unresolvedPairs: 99);

        Assert.True(better.CompareTo(worse) > 0);
    }

    [Fact]
    public void ComparisonGroupScore_PrefersMoreDistinctStatesBeforeTotalReduction()
    {
        StrategyBuilder.ComparisonGroupScore better = StrategyTestHelpers.CreateComparisonGroupScore(
            worstCaseSteps: 4,
            freshItems: 2,
            unrelatedScore: -2,
            groupSize: 3,
            distinctStates: 5,
            totalReduction: 1,
            unresolvedPairs: 0);
        StrategyBuilder.ComparisonGroupScore worse = StrategyTestHelpers.CreateComparisonGroupScore(
            worstCaseSteps: 4,
            freshItems: 2,
            unrelatedScore: -2,
            groupSize: 3,
            distinctStates: 4,
            totalReduction: 99,
            unresolvedPairs: 99);

        Assert.True(better.CompareTo(worse) > 0);
    }

    [Fact]
    public void ComparisonGroupScore_PrefersMoreTotalReductionBeforeUnresolvedPairs()
    {
        StrategyBuilder.ComparisonGroupScore better = StrategyTestHelpers.CreateComparisonGroupScore(
            worstCaseSteps: 4,
            freshItems: 2,
            unrelatedScore: -2,
            groupSize: 3,
            distinctStates: 4,
            totalReduction: 8,
            unresolvedPairs: 0);
        StrategyBuilder.ComparisonGroupScore worse = StrategyTestHelpers.CreateComparisonGroupScore(
            worstCaseSteps: 4,
            freshItems: 2,
            unrelatedScore: -2,
            groupSize: 3,
            distinctStates: 4,
            totalReduction: 7,
            unresolvedPairs: 99);

        Assert.True(better.CompareTo(worse) > 0);
    }

    [Fact]
    public void ComparisonGroupScore_UsesUnresolvedPairsAsFinalTieBreakerAndReturnsZeroForEqualScores()
    {
        StrategyBuilder.ComparisonGroupScore better = StrategyTestHelpers.CreateComparisonGroupScore(
            worstCaseSteps: 4,
            freshItems: 2,
            unrelatedScore: -2,
            groupSize: 3,
            distinctStates: 4,
            totalReduction: 7,
            unresolvedPairs: 6);
        StrategyBuilder.ComparisonGroupScore worse = StrategyTestHelpers.CreateComparisonGroupScore(
            worstCaseSteps: 4,
            freshItems: 2,
            unrelatedScore: -2,
            groupSize: 3,
            distinctStates: 4,
            totalReduction: 7,
            unresolvedPairs: 5);
        StrategyBuilder.ComparisonGroupScore equal = StrategyTestHelpers.CreateComparisonGroupScore(
            worstCaseSteps: 4,
            freshItems: 2,
            unrelatedScore: -2,
            groupSize: 3,
            distinctStates: 4,
            totalReduction: 7,
            unresolvedPairs: 6);

        Assert.True(better.CompareTo(worse) > 0);
        Assert.Equal(0, better.CompareTo(equal));
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

    public static StrategyBuilder.ComparisonGroupScore CreateComparisonGroupScore(
        int worstCaseSteps,
        int freshItems,
        int unrelatedScore,
        int groupSize,
        int distinctStates,
        int totalReduction,
        int unresolvedPairs)
    {
        return new StrategyBuilder.ComparisonGroupScore(
            worstCaseSteps,
            freshItems,
            unrelatedScore,
            groupSize,
            distinctStates,
            totalReduction,
            unresolvedPairs);
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
