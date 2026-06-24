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
        Assert.Equal(362880, rootBranch.EquivalentOrders!.Count);
        Assert.Equal("9!", rootBranch.EquivalentOrders.CountFormula);
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
        Assert.True(plan.SearchStatistics.SearchedStates <= 104, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 7, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 5, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");
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
        Assert.Equal(6, rootBranch.EquivalentOrders!.Count);
        Assert.Equal("3!", rootBranch.EquivalentOrders.CountFormula);
        Assert.Equal("permute {#1, #2, #3}", rootBranch.EquivalentOrders.PatternText);

        Assert.Equal(new[] { 3, 4, 5 }, rootBranch.Next.Group);
    }

    [Fact]
    public void N9M3K3_DistinctClassPermutationRendersAsPermute()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(9, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(9, 3, 3, cancellationToken));

        // At this node the compared items #1, #4 and #7 sit in different symmetry classes, yet all
        // six orderings collapse to the same next state. The summary should compress them to a
        // single "permute {...}" form rather than listing every order as a disjunction.
        StrategyBranch branch = StrategyTestHelpers.FindBranchPath(
            plan.Root, "#1 > #2 > #3", "#4 > #5 > #6", "#7 > #8 > #9", "#1 > #4 > #7");

        Assert.NotNull(branch.EquivalentOrders);
        Assert.Equal(6, branch.EquivalentOrders!.Count);
        Assert.Equal("3!", branch.EquivalentOrders.CountFormula);
        Assert.Equal("permute {#1, #4, #7}", branch.EquivalentOrders.PatternText);
    }

    [Fact]
    public void N12M3K3_SearchWorkStaysWithinBaseline()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(12, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(12, 3, 3, cancellationToken));

        Assert.Equal(7, plan.MaxStep);
        Assert.True(plan.SearchStatistics.SearchedStates <= 555, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
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
        Assert.True(plan.SearchStatistics.SearchedStates <= 416, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
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
        Assert.True(plan.SearchStatistics.SearchedStates <= 705, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 21, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 8, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");
    }

    // The following cases deliberately exercise k < m (find a few winners using wide
    // comparisons) and k > m (find more winners than a single comparison can rank).

    [Fact]
    public void N8M4K2_NarrowTopKWithinWideGroupBaseline()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(8, 4, 2)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(8, 4, 2, cancellationToken));

        Assert.Equal(3, plan.MaxStep);
        Assert.Equal(4, plan.Root.Group.Count);
        Assert.True(plan.SearchStatistics.SearchedStates <= 6, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 3, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 2, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");
    }

    [Fact]
    public void N9M4K3_NarrowTopKBaseline()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(9, 4, 3)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(9, 4, 3, cancellationToken));

        Assert.Equal(4, plan.MaxStep);
        Assert.Equal(4, plan.Root.Group.Count);
        Assert.True(plan.SearchStatistics.SearchedStates <= 18, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 6, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 3, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");
    }

    [Fact]
    public void N12M4K3_NarrowTopKBaseline()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(12, 4, 3)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(12, 4, 3, cancellationToken));

        Assert.Equal(5, plan.MaxStep);
        Assert.Equal(4, plan.Root.Group.Count);
        Assert.True(plan.SearchStatistics.SearchedStates <= 76, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 8, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 4, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");
    }

    [Fact]
    public void N8M3K4_WideTopKBeyondGroupBaseline()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(8, 3, 4)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(8, 3, 4, cancellationToken));

        Assert.Equal(5, plan.MaxStep);
        Assert.Equal(3, plan.Root.Group.Count);
        Assert.True(plan.SearchStatistics.SearchedStates <= 57, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 17, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 6, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");
    }

    [Fact]
    public void N9M3K4_WideTopKBaseline()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(9, 3, 4)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(9, 3, 4, cancellationToken));

        Assert.Equal(6, plan.MaxStep);
        Assert.Equal(3, plan.Root.Group.Count);
        Assert.True(plan.SearchStatistics.SearchedStates <= 161, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 7, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 5, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");
    }

    [Fact]
    public void N8M2K3_PairwiseWideTopKBaseline()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(8, 2, 3)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(8, 2, 3, cancellationToken));

        Assert.Equal(10, plan.MaxStep);
        Assert.Equal(2, plan.Root.Group.Count);
        Assert.True(plan.SearchStatistics.SearchedStates <= 304, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 12, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 10, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");
    }

    [Fact]
    public void N10M3K6_WideTopKBaseline()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(10, 3, 6)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(10, 3, 6, cancellationToken));

        Assert.Equal(7, plan.MaxStep);
        Assert.Equal(3, plan.Root.Group.Count);
        Assert.True(plan.SearchStatistics.SearchedStates <= 432, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 54, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 18, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");
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
        Assert.True(plan.SearchStatistics.SearchedStates <= 260, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 29, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 9, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");

        StrategyBranch branch = StrategyTestHelpers.FindBranchByOrderText(plan.Root, "#2 > #6 > #9 > #10");
        Assert.NotNull(branch.EquivalentOrders);
        Assert.Equal(2, branch.EquivalentOrders!.Count);
        Assert.Equal("2!", branch.EquivalentOrders.CountFormula);
        Assert.Contains("permute{#9, #10}", branch.EquivalentOrders.PatternText);
        Assert.Contains("#2 > #6", branch.EquivalentOrders.PatternText);
        Assert.DoesNotContain("#6 > #2", branch.EquivalentOrders.PatternText);

        // The genuinely-distinct #6 > #2 ordering is now a separate branch that keeps its own
        // #9/#10 alias compression and reuses the shared result subtree via a reference.
        StrategyBranch siblingBranch = StrategyTestHelpers.FindBranchByOrderText(plan.Root, "#6 > #2 > #9 > #10");
        Assert.NotNull(siblingBranch.EquivalentOrders);
        Assert.Equal(2, siblingBranch.EquivalentOrders!.Count);
        Assert.Equal("2!", siblingBranch.EquivalentOrders.CountFormula);
        Assert.Contains("permute{#9, #10}", siblingBranch.EquivalentOrders.PatternText);
        Assert.Contains("#6 > #2", siblingBranch.EquivalentOrders.PatternText);
        Assert.Equal(StrategyNodeKind.Reference, siblingBranch.Next.Kind);
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
              phases: <phases>
            max step = 3
            searched states = 4
            pending states = 0 (peak 2)
            output states = 4 (expanded 2)
            lower-bound states = 1, feasible-top-set states = 3
            outcomes constructed = 12 (duplicate skips 3, merged collisions 1)
            lower-bound prunes = 2
            cache hits = exact 0, lower-bound 0, feasible-top-set 8, best-group-pattern 2

            S1 [step 1/3] sort(#1, #2, #3)
              #1 > #2 > #3: [+ (), - (#3), fixed (), possible (#1, #2, #4, #5)]
                equivalent forms: 6 = 3!
                pattern: permute {#1, #2, #3}
                S2 [step 2/3] sort(#1, #4, #5)
                  #1 > #4 > #5: [+ (#1), - (#5), fixed (#1), possible (#2, #4)]
                    equivalent forms: 2 = 2!
                    pattern: B=permute{#4, #5}; #1 > B1 > B2
                    S3 [step 3/3] sort(#2, #4)
                      fixed (#1); choose 1 of (#2, #4) into top 2
                  #4 > #1 > #5: [+ (#1, #4), - (#2, #5), fixed (#1, #4), possible ()] S4: top 2 = (#1, #4)
                    equivalent forms: 2 = 2!
                    pattern: B=permute{#4, #5}; B1 > #1 > B2
                  #4 > #5 > #1: [+ (#4, #5), - (#1, #2), fixed (#4, #5), possible ()] S4: top 2 = (#4, #5)
                    equivalent forms: 2 = 2!
                    pattern: B=permute{#4, #5}; B1 > B2 > #1
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
        Assert.Equal(new[] { 3, 9, 11 }, branch.Next.Group);
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
            "#4 > #5 > #6",
            "#7 > #8 > #9",
            "#1 > #4 > #7",
            "#5 > #2 > #7");

        Assert.Equal(new[] { 3, 4 }, branch.Effect.NewlyGuaranteedTop);
        Assert.Equal(new[] { 1, 2, 6 }, branch.Effect.NewlyExcluded);
        Assert.Equal(new[] { 0, 3, 4 }, branch.Effect.FixedCandidates);
        Assert.Empty(branch.Effect.PossibleCandidates);

        Assert.Equal(StrategyNodeKind.Terminal, branch.Next.Kind);
        Assert.Equal(new[] { 0, 3, 4 }, branch.Next.TopSet);
    }

    [Fact]
    public void N11M3K3_ReferenceTransitionEffectRemainsStable()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(11, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(11, 3, 3, cancellationToken));

        StrategyNode referenceTarget = StrategyTestHelpers.FollowBranchPath(
            plan.Root,
            "#1 > #2 > #3",
            "#4 > #5 > #6",
            "#7 > #8 > #9",
            "#1 > #10 > #11",
            "#4 > #7 > #2");

        StrategyBranch branch = StrategyTestHelpers.FindBranchPath(
            plan.Root,
            "#1 > #2 > #3",
            "#4 > #5 > #6",
            "#7 > #8 > #9",
            "#10 > #1 > #11",
            "#4 > #7 > #2");

        Assert.Empty(branch.Effect.NewlyGuaranteedTop);
        Assert.Equal(new[] { 1, 8 }, branch.Effect.NewlyExcluded);
        Assert.Empty(branch.Effect.FixedCandidates);
        Assert.Equal(new[] { 0, 3, 4, 5, 6, 7, 9, 10 }, branch.Effect.PossibleCandidates);

        Assert.Equal(StrategyNodeKind.Reference, branch.Next.Kind);
        Assert.Equal(referenceTarget.StateId, branch.Next.StateId);
        Assert.Equal(StrategyNodeKind.Decision, referenceTarget.Kind);
        Assert.Equal(new[] { 4, 6, 9 }, referenceTarget.Group);
    }

    [Fact]
    public void N11M3K3_DepthAnnotationsAreConsistent()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(11, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(11, 3, 3, cancellationToken));

        var depthIndex = StrategyDepthIndex.Build(plan.Root);

        // The root's subtree height (references not followed) equals the reported max step.
        Assert.Equal(plan.MaxStep, depthIndex.SubtreeMaxStep(plan.Root));

        StrategyNode referenceTarget = StrategyTestHelpers.FollowBranchPath(
            plan.Root,
            "#1 > #2 > #3",
            "#4 > #5 > #6",
            "#7 > #8 > #9",
            "#1 > #10 > #11",
            "#4 > #7 > #2");

        StrategyBranch referenceBranch = StrategyTestHelpers.FindBranchPath(
            plan.Root,
            "#1 > #2 > #3",
            "#4 > #5 > #6",
            "#7 > #8 > #9",
            "#10 > #1 > #11",
            "#4 > #7 > #2");

        Assert.Equal(StrategyNodeKind.Reference, referenceBranch.Next.Kind);
        Assert.Equal(referenceTarget.StateId, referenceBranch.Next.StateId);

        int expectedRemaining = depthIndex.SubtreeMaxStep(referenceTarget) - (referenceTarget.Step ?? 0);
        Assert.True(expectedRemaining > 0);

        Assert.True(depthIndex.TryGetReferenceRemaining(referenceBranch.Next.StateId, out int remaining));
        Assert.Equal(expectedRemaining, remaining);

        string rendered = StrategyTextRenderer.Render(plan);
        Assert.Contains($"→S{referenceTarget.StateId} (+{expectedRemaining} steps)", rendered);
    }

    [Fact]
    public void N11M3K3_ReferenceRelabelingIsAnIsomorphismOfDisplayedSets()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(11, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(11, 3, 3, cancellationToken));

        // Branch leading into the reference site and the branch leading into its target.
        StrategyBranch referenceBranch = StrategyTestHelpers.FindBranchPath(
            plan.Root,
            "#1 > #2 > #3",
            "#4 > #5 > #6",
            "#7 > #8 > #9",
            "#10 > #1 > #11",
            "#4 > #7 > #2");

        StrategyBranch targetBranch = StrategyTestHelpers.FindBranchPath(
            plan.Root,
            "#1 > #2 > #3",
            "#4 > #5 > #6",
            "#7 > #8 > #9",
            "#1 > #10 > #11",
            "#4 > #7 > #2");

        StrategyNode reference = referenceBranch.Next;
        Assert.Equal(StrategyNodeKind.Reference, reference.Kind);
        Assert.Equal(targetBranch.Next.StateId, reference.StateId);

        // The differing comparison paths mean the numbering must differ, so a relabeling is expected.
        Assert.NotEmpty(reference.ReferenceRelabeling);

        // The relabeling must be a partial bijection with no identity entries.
        Assert.Equal(
            reference.ReferenceRelabeling.Count,
            reference.ReferenceRelabeling.Select(r => r.ReferencedItem).Distinct().Count());
        Assert.Equal(
            reference.ReferenceRelabeling.Count,
            reference.ReferenceRelabeling.Select(r => r.CurrentItem).Distinct().Count());
        Assert.DoesNotContain(reference.ReferenceRelabeling, r => r.ReferencedItem == r.CurrentItem);

        // Applying the relabeling to the target's displayed sets must reproduce the reference site's sets.
        var map = reference.ReferenceRelabeling.ToDictionary(r => r.ReferencedItem, r => r.CurrentItem);
        int Map(int item) => map.TryGetValue(item, out int mapped) ? mapped : item;

        var mappedPossible = targetBranch.Effect.PossibleCandidates.Select(Map).OrderBy(x => x).ToArray();
        var mappedFixed = targetBranch.Effect.FixedCandidates.Select(Map).OrderBy(x => x).ToArray();

        Assert.Equal(referenceBranch.Effect.PossibleCandidates.OrderBy(x => x).ToArray(), mappedPossible);
        Assert.Equal(referenceBranch.Effect.FixedCandidates.OrderBy(x => x).ToArray(), mappedFixed);

        string rendered = StrategyTextRenderer.Render(plan);
        Assert.Contains("[map: ", rendered);
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
    public void N10M2K2_AfterInitialComparisonsChainsIndependentPairs()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.Generate(10, 2, 2)",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(10, 2, 2, cancellationToken));

        // The tie-break prefers fresh, mutually independent pairs over reusing the leader.
        StrategyNode afterFirstComparison = StrategyTestHelpers.FollowBranchPath(
            plan.Root,
            "#1 > #2");
        Assert.Equal(new[] { 2, 3 }, afterFirstComparison.Group);

        StrategyNode afterSecondComparison = StrategyTestHelpers.FollowBranchPath(
            plan.Root,
            "#1 > #2",
            "#3 > #4");
        Assert.Equal(new[] { 4, 5 }, afterSecondComparison.Group);
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
            "        S3 [step 3/5] sort(#2, #6, #9, #10)",
            "              #11 > #5 > #12 > #3");

        const string expected = """
                    S3 [step 3/5] sort(#2, #6, #9, #10)
                      #2 > #6 > #9 > #10: [+ (#1), - (#7, #8, #9, #10), fixed (#1), possible (#2, #3, #4, #5, #6, #11, #12)]
                        equivalent forms: 2 = 2!
                        pattern: C=permute{#9, #10}; #2 > #6 > C1 > C2
                        S4 [step 4/5] sort(#3, #5, #11, #12)
                          #11 > #12 > #3 > #5: [+ (#2, #11, #12), - (#3, #4, #5, #6), fixed (#1, #2, #11, #12), possible ()] S5: top 4 = (#1, #2, #11, #12)
                            equivalent forms: 2 = 2!
                            pattern: C=permute{#11, #12}; C1 > C2 > #3 > #5
                          #11 > #12 > #5 > #3: [+ (#11, #12), - (#3, #4, #6), fixed (#1, #11, #12), possible (#2, #5)]
                            equivalent forms: 2 = 2!
                            pattern: C=permute{#11, #12}; C1 > C2 > #5 > #3
                            S6 [step 5/5] sort(#2, #5)
                              fixed (#1, #11, #12); choose 1 of (#2, #5) into top 4
                          #11 > #3 > #12 > #5: [+ (#2, #3, #11), - (#4, #5, #6, #12), fixed (#1, #2, #3, #11), possible ()] S7: top 4 = (#1, #2, #3, #11)
                            equivalent forms: 2 = 2!
                            pattern: C=permute{#11, #12}; C1 > #3 > C2 > #5
                          #11 > #3 > #5 > #12: [+ (#2, #3, #11), - (#4, #5, #6, #12), fixed (#1, #2, #3, #11), possible ()] S7: top 4 = (#1, #2, #3, #11)
                            equivalent forms: 2 = 2!
                            pattern: C=permute{#11, #12}; C1 > #3 > #5 > C2
            """;

        Assert.Equal(StrategyTestHelpers.NormalizeRenderedSnapshot(expected), excerpt);
    }

    // Compact selection (opt-in) keeps the optimal worst-case step count but, among the
    // equally-optimal solutions, prefers the one with the smallest materialized tree. These
    // tests pin the two invariants: max step is preserved and output states never regress
    // above the default, plus the concrete shrink on cases known to have redundant trees.

    [Theory]
    [InlineData(9, 3, 3)]
    [InlineData(11, 3, 3)]
    [InlineData(12, 4, 4)]
    [InlineData(10, 3, 4)]
    [InlineData(12, 4, 3)]
    public void Compact_PreservesMaxStepAndDoesNotRegressOutputStates(int n, int m, int k)
    {
        StrategyPlan baseline = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.Generate({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(n, m, k, cancellationToken));

        StrategyPlan compact = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.GenerateCompact({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.GenerateCompact(n, m, k, cancellationToken));

        Assert.Equal(baseline.MaxStep, compact.MaxStep);
        Assert.True(
            compact.SearchStatistics.OutputStates <= baseline.SearchStatistics.OutputStates,
            $"compact output states {compact.SearchStatistics.OutputStates} exceeded baseline {baseline.SearchStatistics.OutputStates}");
    }

    [Theory]
    [InlineData(11, 3, 3, 9)]
    [InlineData(12, 4, 4, 21)]
    [InlineData(10, 3, 4, 9)]
    public void Compact_ShrinksTreesWithRedundantSolutions(int n, int m, int k, int expectedOutputStatesCap)
    {
        StrategyPlan baseline = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.Generate({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(n, m, k, cancellationToken));

        StrategyPlan compact = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.GenerateCompact({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.GenerateCompact(n, m, k, cancellationToken));

        Assert.Equal(baseline.MaxStep, compact.MaxStep);
        Assert.True(
            compact.SearchStatistics.OutputStates < baseline.SearchStatistics.OutputStates,
            $"compact output states {compact.SearchStatistics.OutputStates} did not improve on baseline {baseline.SearchStatistics.OutputStates}");
        Assert.True(
            compact.SearchStatistics.OutputStates <= expectedOutputStatesCap,
            $"compact output states regressed to {compact.SearchStatistics.OutputStates}");
    }

    // Searched-state monitor for the compact pass. Compact runs a second, less-prunable
    // search on top of phase 1, so its searched-state count is the main lever for its cost.
    // These caps pin the current work so that future algorithm changes surface any regression
    // (an increase) or improvement (which should be ratcheted down here) as an explicit diff.
    // Values are deterministic; update them deliberately when the search work legitimately
    // changes.
    [Theory]
    [InlineData(9, 3, 3, 163)]
    [InlineData(11, 3, 3, 528)]
    [InlineData(12, 4, 4, 492)]
    [InlineData(10, 3, 4, 1102)]
    [InlineData(12, 4, 3, 135)]
    [InlineData(12, 3, 3, 669)]
    [InlineData(8, 4, 2, 7)]
    [InlineData(10, 3, 5, 605)]
    [InlineData(13, 4, 3, 140)]
    public void Compact_SearchedStateCountStaysWithinBaseline(int n, int m, int k, int searchedStateCap)
    {
        StrategyPlan compact = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.GenerateCompact({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.GenerateCompact(n, m, k, cancellationToken));

        Assert.True(
            compact.SearchStatistics.SearchedStates <= searchedStateCap,
            $"compact searched states regressed to {compact.SearchStatistics.SearchedStates} (cap {searchedStateCap})");
    }

    // Searched-state monitor for the default (fast) pass. Several tests above already cap
    // searched states for individual cases, but loosely; this consolidated theory pins the
    // current deterministic count tightly across a spread of k=m, k<m and k>m shapes so that
    // future algorithm changes surface any regression (an increase) or improvement (a
    // deliberate cap update) as an explicit diff. Update values deliberately when the search
    // work legitimately changes.
    [Theory]
    [InlineData(9, 3, 3, 104)]
    [InlineData(11, 3, 3, 330)]
    [InlineData(12, 3, 3, 555)]
    [InlineData(12, 4, 4, 260)]
    [InlineData(12, 4, 3, 76)]
    [InlineData(10, 3, 4, 451)]
    [InlineData(10, 3, 5, 416)]
    [InlineData(13, 4, 3, 132)]
    [InlineData(8, 4, 2, 6)]
    [InlineData(9, 4, 3, 18)]
    [InlineData(8, 3, 4, 57)]
    [InlineData(9, 3, 4, 161)]
    [InlineData(10, 3, 6, 432)]
    [InlineData(5, 3, 2, 4)]
    [InlineData(10, 2, 2, 115)]
    public void Default_SearchedStateCountStaysWithinBaseline(int n, int m, int k, int searchedStateCap)
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.Generate({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(n, m, k, cancellationToken));

        Assert.True(
            plan.SearchStatistics.SearchedStates <= searchedStateCap,
            $"default searched states regressed to {plan.SearchStatistics.SearchedStates} (cap {searchedStateCap})");
    }

    // Outcome-construction monitor for the default (fast) pass. OutcomesConstructed counts every
    // ComparisonOutcome materialized (Clone + ApplyOrder + Eliminate + Normalize) and is the
    // dominant per-state search cost, so it is the most sensitive lever for raw search work --
    // more so than searched states, since most constructed outcomes are duplicates discarded
    // afterwards. These caps pin the current deterministic count so future algorithm changes
    // surface any regression (an increase) or improvement (a deliberate cap update) as a diff.
    // Caps are the current deterministic counts; ratchet them down when an optimization cuts
    // construction. The lean search-path order enumerator can visit a group's representative
    // orders in a different sequence than the display-path family enumerator, which shifts when
    // the worst-case-step branch-and-bound exits early, so a few k>m caps move up slightly even
    // though the search results (and the snapshot/output-state monitors) are byte-identical; the
    // net effect on heavy cases is a large reduction in both outcomes and wall-clock time.
    [Theory]
    [InlineData(9, 3, 3, 1231)]
    [InlineData(11, 3, 3, 6516)]
    [InlineData(12, 3, 3, 11141)]
    [InlineData(12, 4, 4, 11579)]
    [InlineData(12, 4, 3, 900)]
    [InlineData(10, 3, 4, 8564)]
    [InlineData(10, 3, 5, 7498)]
    [InlineData(13, 4, 3, 2176)]
    [InlineData(8, 4, 2, 7)]
    [InlineData(9, 4, 3, 114)]
    [InlineData(8, 3, 4, 618)]
    [InlineData(9, 3, 4, 2740)]
    [InlineData(10, 3, 6, 8228)]
    [InlineData(5, 3, 2, 12)]
    [InlineData(10, 2, 2, 873)]
    public void Default_OutcomesConstructedStaysWithinBaseline(int n, int m, int k, int outcomesCap)
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.Generate({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(n, m, k, cancellationToken));

        Assert.True(
            plan.SearchStatistics.OutcomesConstructed <= outcomesCap,
            $"default outcomes constructed regressed to {plan.SearchStatistics.OutcomesConstructed} (cap {outcomesCap})");
    }

    // Outcome-construction monitor for the compact pass. The compact selection re-enumerates
    // group outcomes on top of phase 1, so it constructs many more outcomes than the default;
    // this count is the primary cost target for compact-search optimization. Caps are the
    // current deterministic counts -- ratchet them down when an optimization legitimately cuts
    // outcome construction.
    [Theory]
    [InlineData(9, 3, 3, 5358)]
    [InlineData(11, 3, 3, 16623)]
    [InlineData(12, 4, 4, 24573)]
    [InlineData(10, 3, 4, 55896)]
    [InlineData(12, 4, 3, 4893)]
    [InlineData(12, 3, 3, 13887)]
    [InlineData(8, 4, 2, 24)]
    [InlineData(10, 3, 5, 12031)]
    [InlineData(13, 4, 3, 2568)]
    public void Compact_OutcomesConstructedStaysWithinBaseline(int n, int m, int k, int outcomesCap)
    {
        StrategyPlan compact = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.GenerateCompact({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.GenerateCompact(n, m, k, cancellationToken));

        Assert.True(
            compact.SearchStatistics.OutcomesConstructed <= outcomesCap,
            $"compact outcomes constructed regressed to {compact.SearchStatistics.OutcomesConstructed} (cap {outcomesCap})");
    }

    // Symmetry-redundancy monitor for the default pass. DuplicateOutcomeSkips counts, within a
    // single group's outcome enumeration, how many constructed outcomes collapsed onto an
    // already-seen canonical next-state key -- i.e. isomorphic/symmetric duplicates. These are
    // precisely the redundant orders that an up-front orbit/block-symmetry detector could avoid
    // constructing (e.g. the five extra orders of sort(1,4,7) in 9,3,3 that all map to the same
    // next state). OutcomesConstructed measures total work; this counter isolates the portion of
    // that work that is wasted on symmetry, so it is the direct lever for symmetry-collapse
    // optimizations: a correct orbit detector must ratchet these caps DOWN. Caps pin the current
    // deterministic counts; an increase is a regression, a deliberate decrease is an improvement.
    [Theory]
    [InlineData(9, 3, 3, 112)]
    [InlineData(11, 3, 3, 511)]
    [InlineData(12, 3, 3, 875)]
    [InlineData(12, 4, 4, 2379)]
    [InlineData(12, 4, 3, 188)]
    [InlineData(10, 3, 4, 617)]
    [InlineData(10, 3, 5, 510)]
    [InlineData(13, 4, 3, 509)]
    [InlineData(8, 4, 2, 0)]
    [InlineData(9, 4, 3, 37)]
    [InlineData(8, 3, 4, 64)]
    [InlineData(9, 3, 4, 259)]
    [InlineData(10, 3, 6, 523)]
    [InlineData(5, 3, 2, 3)]
    [InlineData(10, 2, 2, 9)]
    public void Default_DuplicateOutcomeSkipsStaysWithinBaseline(int n, int m, int k, int duplicateSkipCap)
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.Generate({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.Generate(n, m, k, cancellationToken));

        Assert.True(
            plan.SearchStatistics.Diagnostics.DuplicateOutcomeSkips <= duplicateSkipCap,
            $"default duplicate outcome skips regressed to {plan.SearchStatistics.Diagnostics.DuplicateOutcomeSkips} (cap {duplicateSkipCap})");
    }

    // Symmetry-redundancy monitor for the compact pass. The compact selection re-enumerates group
    // outcomes on top of phase 1, so it surfaces more symmetric duplicates than the default pass;
    // this is the primary symmetry-collapse target for compact search. Caps pin the current
    // deterministic counts -- ratchet them down when an orbit/block-symmetry optimization lands.
    [Theory]
    [InlineData(9, 3, 3, 692)]
    [InlineData(11, 3, 3, 2635)]
    [InlineData(12, 4, 4, 14251)]
    [InlineData(10, 3, 4, 7411)]
    [InlineData(12, 4, 3, 1811)]
    [InlineData(12, 3, 3, 3324)]
    [InlineData(8, 4, 2, 9)]
    [InlineData(10, 3, 5, 4025)]
    [InlineData(13, 4, 3, 1151)]
    public void Compact_DuplicateOutcomeSkipsStaysWithinBaseline(int n, int m, int k, int duplicateSkipCap)
    {
        StrategyPlan compact = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.GenerateCompact({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => StrategyBuilder.GenerateCompact(n, m, k, cancellationToken));

        Assert.True(
            compact.SearchStatistics.Diagnostics.DuplicateOutcomeSkips <= duplicateSkipCap,
            $"compact duplicate outcome skips regressed to {compact.SearchStatistics.Diagnostics.DuplicateOutcomeSkips} (cap {duplicateSkipCap})");
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
        for (int i = 0; i < lines.Length; i++)
        {
            // Wall-clock timing lines are inherently non-deterministic; canonicalize them so
            // snapshot/determinism comparisons only assert on the deterministic counters.
            if (lines[i].StartsWith("elapsed = ", StringComparison.Ordinal))
                lines[i] = "elapsed = <elapsed>";
            else if (lines[i].StartsWith("  phases: ", StringComparison.Ordinal))
                lines[i] = "  phases: <phases>";
        }

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
