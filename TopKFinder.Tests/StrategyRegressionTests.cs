using System.Collections.Generic;
using Xunit;

public sealed class StrategyRegressionTests
{
    // Headroom for the slowest cases. The compact pass's secondary objective minimizes total
    // displayed edges, which adds a (now lightweight) display-branch enumeration; on a fresh
    // builder BuildCompactPlan also re-runs phase 1 first. For the largest stress case (10,2,4)
    // the exact-step search alone runs ~10-28s depending on machine load and phase 1b adds ~7s,
    // so the combined fresh build can still approach 30s under parallel test load. Keep this
    // generous so genuine hangs still fail while legitimate heavy work does not flake.
    private static readonly TimeSpan RegressionTestTimeout = TimeSpan.FromSeconds(90);

    [Fact]
    public void N10M9K9_RemainsTwoStepPermutationCase()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.BuildDefaultPlan(10, 9, 9)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(10, 9, 9, cancellationToken).BuildDefaultPlan());

        Assert.Equal(2, plan.MaxStep);
        Assert.Equal(2, plan.SearchStatistics.SearchedStates);
        Assert.Equal(2, plan.SearchStatistics.OutputStates);
        StrategyBranch rootBranch = Assert.Single(plan.Root.Branches);
        Assert.Equal("#1 > #2 > #3 > #4 > #5 > #6 > #7 > #8 > #9", rootBranch.OrderText);
        Assert.NotNull(rootBranch.EquivalentOrders);
        Assert.Equal(362880, rootBranch.EquivalentOrders!.Count);
        Assert.Equal("9!", rootBranch.EquivalentOrders.CountFormula);
        Assert.Equal("{#1 ~ #9}", rootBranch.EquivalentOrders.PatternText);
    }

    [Fact]
    public void N9M3K3_RootChoiceRemainsCanonicalOpeningGroup()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.BuildDefaultPlan(9, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(9, 3, 3, cancellationToken).BuildDefaultPlan());

        Assert.Equal(new[] { 0, 1, 2 }, plan.Root.Group);

        StrategyBranch rootBranch = Assert.Single(plan.Root.Branches);
        Assert.Equal("#1 > #2 > #3", rootBranch.OrderText);
        Assert.NotNull(rootBranch.EquivalentOrders);
        Assert.Equal(6, rootBranch.EquivalentOrders!.Count);
        Assert.Equal("3!", rootBranch.EquivalentOrders.CountFormula);
        Assert.Equal("{#1, #2, #3}", rootBranch.EquivalentOrders.PatternText);

        Assert.Equal(new[] { 3, 4, 5 }, rootBranch.Next.Group);
    }

    [Fact]
    public void N9M3K3_DistinctClassPermutationRendersAsPermute()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.BuildDefaultPlan(9, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(9, 3, 3, cancellationToken).BuildDefaultPlan());

        // At this node the compared items #1, #4 and #7 sit in different symmetry classes, yet all
        // six orderings collapse to the same next state. The summary should compress them to a
        // single any-order "{...}" set rather than listing every order as a disjunction.
        StrategyBranch branch = StrategyTestHelpers.FindBranchPath(
            plan.Root, "#1 > #2 > #3", "#4 > #5 > #6", "#7 > #8 > #9", "#1 > #4 > #7");

        Assert.NotNull(branch.EquivalentOrders);
        Assert.Equal(6, branch.EquivalentOrders!.Count);
        Assert.Equal("3!", branch.EquivalentOrders.CountFormula);
        Assert.Equal("{#1, #4, #7}", branch.EquivalentOrders.PatternText);
    }

    // Structural baseline for the default (fast) pass across a spread of k=m, k<m (find a few
    // winners using wide comparisons) and k>m (find more winners than a single comparison can
    // rank) shapes. Pins the worst-case step count, the root group size, and the output-state
    // counts. Searched-state caps live in Default_SearchedStateCountStaysWithinBaseline; the
    // dominant outcome-construction cost lives in Default_OutcomesConstructedStaysWithinBaseline.
    [Theory]
    [InlineData(9, 3, 3, 6, 3, 7, 5)]
    [InlineData(12, 3, 3, 7, 3, 15, 8)]
    [InlineData(10, 3, 5, 6, 3, 8, 5)]
    [InlineData(12, 4, 5, 6, 4, 21, 8)]
    [InlineData(8, 4, 2, 3, 4, 3, 2)]
    [InlineData(9, 4, 3, 4, 4, 6, 3)]
    [InlineData(12, 4, 3, 5, 4, 8, 4)]
    [InlineData(8, 3, 4, 5, 3, 17, 6)]
    [InlineData(9, 3, 4, 6, 3, 7, 5)]
    [InlineData(8, 2, 3, 10, 2, 12, 10)]
    [InlineData(10, 3, 6, 7, 3, 54, 18)]
    [InlineData(6, 2, 2, 7, 2, 7, 6)]
    [InlineData(25, 5, 3, 7, 5, 7, 6)]
    public void Default_StructuralBaselineRemainsStable(
        int n, int m, int k, int maxStep, int rootGroupCount, int outputStateCap, int expandedOutputStateCap)
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildDefaultPlan({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildDefaultPlan());

        Assert.Equal(maxStep, plan.MaxStep);
        Assert.Equal(rootGroupCount, plan.Root.Group.Count);
        Assert.True(
            plan.SearchStatistics.OutputStates <= outputStateCap,
            $"output states regressed to {plan.SearchStatistics.OutputStates} (cap {outputStateCap})");
        Assert.True(
            plan.SearchStatistics.ExpandedOutputStates <= expandedOutputStateCap,
            $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates} (cap {expandedOutputStateCap})");
    }

    [Fact]
    public void Builder_ProducesDeterministicOutputAcrossRuns()
    {
        foreach ((int n, int m, int k) in new[] { (9, 3, 3), (12, 3, 3), (10, 3, 5) })
        {
            StrategyPlan first = TestTimeoutHelper.RunWithTimeout(
                $"StrategyBuilder.BuildDefaultPlan({n}, {m}, {k}) first",
                RegressionTestTimeout,
                cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildDefaultPlan());
            StrategyPlan second = TestTimeoutHelper.RunWithTimeout(
                $"StrategyBuilder.BuildDefaultPlan({n}, {m}, {k}) second",
                RegressionTestTimeout,
                cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildDefaultPlan());

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
            "StrategyBuilder.BuildDefaultPlan(10, 3, 5)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(10, 3, 5, cancellationToken).BuildDefaultPlan());

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
            "StrategyBuilder.BuildDefaultPlan(5, 3, 2) with progress",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(5, 3, 2, cancellationToken, snapshot => snapshots.Add(snapshot)).BuildDefaultPlan());

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
            "StrategyBuilder.BuildDefaultPlan(12, 4, 4)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(12, 4, 4, cancellationToken).BuildDefaultPlan());

        Assert.Equal(5, plan.MaxStep);
        Assert.True(plan.SearchStatistics.SearchedStates <= 289, $"searched states regressed to {plan.SearchStatistics.SearchedStates}");
        Assert.True(plan.SearchStatistics.OutputStates <= 29, $"output states regressed to {plan.SearchStatistics.OutputStates}");
        Assert.True(plan.SearchStatistics.ExpandedOutputStates <= 9, $"expanded output states regressed to {plan.SearchStatistics.ExpandedOutputStates}");

        // #2 and #6 are interchangeable here via a group-fixing automorphism (the same one the
        // search path already used to dedup #2>#6 against #6>#2). The display path now recognizes
        // that symmetry too, so both #2/#6 and #9/#10 collapse into adjacent any-order sets and
        // the formerly-split #6 > #2 sibling is folded into this branch's equivalent orders.
        StrategyBranch branch = StrategyTestHelpers.FindBranchByOrderText(plan.Root, "#2 > #6 > #9 > #10");
        Assert.NotNull(branch.EquivalentOrders);
        Assert.Equal(4, branch.EquivalentOrders!.Count);
        Assert.Equal("2! x 2!", branch.EquivalentOrders.CountFormula);
        Assert.Equal("{#2, #6} > {#9, #10}", branch.EquivalentOrders.PatternText);
        Assert.Null(branch.EquivalentOrders.Legend);

        // The #6 > #2 ordering is no longer a distinct branch; it is now part of the permute family
        // above, so no separate (reference) sibling exists for it.
        Assert.DoesNotContain(
            StrategyTestHelpers.EnumerateBranches(plan.Root),
            candidate => candidate.OrderText == "#6 > #2 > #9 > #10");
    }

    [Fact]
    public void N12M3K3_DoesNotCreateSelfReferentialBranches()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.BuildDefaultPlan(12, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(12, 3, 3, cancellationToken).BuildDefaultPlan());

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
            "StrategyBuilder.BuildDefaultPlan(5, 3, 2)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(5, 3, 2, cancellationToken).BuildDefaultPlan());

        string rendered = StrategyTestHelpers.NormalizeRenderedSnapshot(StrategyTextRenderer.Render(plan));
        const string expected = """
            ==================== summary ====================
            n=5, m=3, k=2
            worst-case steps = 3
            total edges = 4
            elapsed = <elapsed>
            phases: <phases>

            ==================== diagnostics ====================
            searched states = 4
            pending states = 0 (peak 2)
            output states = 4 (expanded 2)
            lower-bound states = 2, feasible-top-set states = 3
            outcomes constructed = 12 (duplicate skips 3, merged collisions 1)
            candidate groups enumerated = 4 (symmetry-class representatives canonicalized before cross-class dedup)
            lower-bound prunes = 2
            cache hits = exact 0, lower-bound 1, feasible-top-set 11, best-group-pattern 2

            ==================== legend ====================
            #i                            item i (1-based labels; may be relabeled in references)
            #i ~ #j                       items #i through #j inclusive (a run of 4+ consecutive items)
            S{id} [step x/y] sort(...)    decision state: do this sort at step x of at most y
            a > b > c                     the sort revealed a ranks above b above c
            equivalent forms: N = ...     this branch stands for N symmetric orderings (e.g. 3! = 6)
            pattern: ...                  shape of those orderings; "{...}" = any order, "A = {...}" names a split block (members A1, A2 ...)
            S{id}: top k = (...)          solved: the top-k set is fully determined
            →S{id} (+N steps) [map: ...]  reuse state S{id}'s subtree (N more sorts); [map] relabels referenced→current

            [+ ..., - ..., fixed ..., possible ...]   effect after an outcome (empty entries are omitted):
                 +         newly guaranteed into the top-k
                 -         newly excluded from the top-k
                 fixed     already locked into the top-k
                 possible  still competing for the remaining slots

            ==================== strategy ====================
            S1 [step 1/3] sort(#1, #2, #3)
              #1 > #2 > #3: [- (#3), possible (#1, #2, #4, #5)]
                equivalent forms: 6 = 3!
                pattern: {#1, #2, #3}
                S2 [step 2/3] sort(#1, #4, #5)
                  #1 > #4 > #5: [+ (#1), - (#5), fixed (#1), possible (#2, #4)]
                    equivalent forms: 2 = 2!
                    pattern: #1 > {#4, #5}
                    S3 [step 3/3] sort(#2, #4)
                      fixed (#1); choose 1 of (#2, #4) into top 2
                  #4 > #1 > #5: [+ (#1, #4), - (#2, #5), fixed (#1, #4)] S4: top 2 = (#1, #4)
                    equivalent forms: 2 = 2!
                    pattern: A1 > #1 > A2 ; A = {#4, #5}
                  #4 > #5 > #1: [+ (#4, #5), - (#1, #2), fixed (#4, #5)] S4: top 2 = (#4, #5)
                    equivalent forms: 2 = 2!
                    pattern: {#4, #5} > #1
            """;

        Assert.Equal(StrategyTestHelpers.NormalizeRenderedSnapshot(expected), rendered);
    }

    [Fact]
    public void N12M3K3_DecisionTransitionEffectRemainsStable()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.BuildDefaultPlan(12, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(12, 3, 3, cancellationToken).BuildDefaultPlan());

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
            "StrategyBuilder.BuildDefaultPlan(9, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(9, 3, 3, cancellationToken).BuildDefaultPlan());

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
            "StrategyBuilder.BuildDefaultPlan(11, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(11, 3, 3, cancellationToken).BuildDefaultPlan());

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
            "StrategyBuilder.BuildDefaultPlan(11, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(11, 3, 3, cancellationToken).BuildDefaultPlan());

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
        Assert.Contains($"→S{referenceTarget.StateId} {StrategyTextRenderer.FormatRemainingSteps(expectedRemaining)}", rendered);
    }

    [Fact]
    public void N11M3K3_ReferenceRelabelingIsAnIsomorphismOfDisplayedSets()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.BuildDefaultPlan(11, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(11, 3, 3, cancellationToken).BuildDefaultPlan());

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
            "StrategyBuilder.BuildDefaultPlan(12, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(12, 3, 3, cancellationToken).BuildDefaultPlan());

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
            "StrategyBuilder.BuildDefaultPlan(12, 3, 3)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(12, 3, 3, cancellationToken).BuildDefaultPlan());

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
            "StrategyBuilder.BuildDefaultPlan(10, 2, 2)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(10, 2, 2, cancellationToken).BuildDefaultPlan());

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
            "StrategyBuilder.BuildDefaultPlan(12, 4, 4)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(12, 4, 4, cancellationToken).BuildDefaultPlan());

        string rendered = StrategyTestHelpers.NormalizeRenderedSnapshot(StrategyTextRenderer.Render(plan));
        string excerpt = StrategyTestHelpers.ExtractRenderedSection(
            rendered,
            "        S3 [step 3/5] sort(#2, #6, #9, #10)",
            "              #11 > #5 > #12 > #3");

        const string expected = """
                    S3 [step 3/5] sort(#2, #6, #9, #10)
                      #2 > #6 > #9 > #10: [+ (#1), - (#7 ~ #10), fixed (#1), possible (#2 ~ #6, #11, #12)]
                        equivalent forms: 4 = 2! x 2!
                        pattern: {#2, #6} > {#9, #10}
                        S4 [step 4/5] sort(#3, #5, #11, #12)
                          #11 > #12 > #3 > #5: [+ (#2, #11, #12), - (#3 ~ #6), fixed (#1, #2, #11, #12)] S5: top 4 = (#1, #2, #11, #12)
                            equivalent forms: 2 = 2!
                            pattern: {#11, #12} > #3 > #5
                          #11 > #12 > #5 > #3: [+ (#11, #12), - (#3, #4, #6), fixed (#1, #11, #12), possible (#2, #5)]
                            equivalent forms: 2 = 2!
                            pattern: {#11, #12} > #5 > #3
                            S6 [step 5/5] sort(#2, #5)
                              fixed (#1, #11, #12); choose 1 of (#2, #5) into top 4
                          #11 > #3 > #12 > #5: [+ (#2, #3, #11), - (#4, #5, #6, #12), fixed (#1, #2, #3, #11)] S7: top 4 = (#1, #2, #3, #11)
                            equivalent forms: 2 = 2!
                            pattern: A1 > #3 > A2 > #5 ; A = {#11, #12}
                          #11 > #3 > #5 > #12: [+ (#2, #3, #11), - (#4, #5, #6, #12), fixed (#1, #2, #3, #11)] S7: top 4 = (#1, #2, #3, #11)
                            equivalent forms: 2 = 2!
                            pattern: A1 > #3 > #5 > A2 ; A = {#11, #12}
            """;

        Assert.Equal(StrategyTestHelpers.NormalizeRenderedSnapshot(expected), excerpt);
    }

    // These exact inputs previously crashed compact mode: incomplete 1-WL group
    // de-duplication merged structurally distinct groups, dropped a uniquely-optimal
    // group, and left compact's budget with no admissible group (bestGroup == null ->
    // throw). Pin them so the regression can never silently return. The complete group
    // invariant must keep the optimal worst-case step count for these cases.
    [Theory]
    [InlineData(10, 2, 4)]
    [InlineData(12, 3, 4)]
    public void Compact_DoesNotCrashOnPreviouslyFailingInputs(int n, int m, int k)
    {
        StrategyPlan baseline = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildDefaultPlan({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildDefaultPlan());

        StrategyPlan compact = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildCompactPlan({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildCompactPlan());

        Assert.Equal(baseline.MaxStep, compact.MaxStep);
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
    public void Compact_PreservesMaxStepAndDoesNotRegressEdges(int n, int m, int k)
    {
        StrategyPlan baseline = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildDefaultPlan({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildDefaultPlan());

        StrategyPlan compact = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildCompactPlan({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildCompactPlan());

        Assert.Equal(baseline.MaxStep, compact.MaxStep);
        Assert.True(
            compact.TotalBranchEdges <= baseline.TotalBranchEdges,
            $"compact total edges {compact.TotalBranchEdges} exceeded baseline {baseline.TotalBranchEdges}");
    }

    [Theory]
    [InlineData(11, 3, 3, 8)]
    // 12,4,4: honest minimum is 38, not 35. The prior 35 relied on a false sibling-merge (a
    // misleading disjunction) at one node; the automorphism-orbit honesty fix correctly splits it.
    // Verified: the 38-edge compact tree has objective==render at every node, 0 false-splits, and
    // 0 unbacked merges, and the consistent DP is exhaustive over step-optimal groups, so 38 is the
    // true minimum displayed-edge count under honest rendering (any lower count is necessarily a
    // dishonest merge).
    [InlineData(12, 4, 4, 35)]
    [InlineData(10, 3, 4, 9)]
    public void Compact_ShrinksTreesWithRedundantSolutions(int n, int m, int k, int expectedEdgeCap)
    {
        StrategyPlan baseline = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildDefaultPlan({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildDefaultPlan());

        StrategyPlan compact = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildCompactPlan({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildCompactPlan());

        Assert.Equal(baseline.MaxStep, compact.MaxStep);
        Assert.True(
            compact.TotalBranchEdges < baseline.TotalBranchEdges,
            $"compact total edges {compact.TotalBranchEdges} did not improve on baseline {baseline.TotalBranchEdges}");
        Assert.True(
            compact.TotalBranchEdges <= expectedEdgeCap,
            $"compact total edges regressed to {compact.TotalBranchEdges}");
    }

    // k<=n/2 regression guard for the full-bucket pre-merge fix. 12,4,4 previously compacted to 38
    // because one renderable bucket was split before the pattern engine could summarize it; with the
    // fix, compact correctly reaches 35 while preserving max-step optimality.
    [Fact]
    public void Compact_KLeHalf_CapturesFullBucketMerge_1244()
    {
        StrategyPlan baseline = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.BuildDefaultPlan(12, 4, 4)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(12, 4, 4, cancellationToken).BuildDefaultPlan());

        StrategyPlan compact = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.BuildCompactPlan(12, 4, 4)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(12, 4, 4, cancellationToken).BuildCompactPlan());

        Assert.Equal(baseline.MaxStep, compact.MaxStep);
        Assert.Equal(35, compact.TotalBranchEdges);
    }

    // Searched-state monitor for the compact pass. Compact runs a second, less-prunable
    // search on top of phase 1, so its searched-state count is the main lever for its cost.
    // These caps pin the current work so that future algorithm changes surface any regression
    // (an increase) or improvement (which should be ratcheted down here) as an explicit diff.
    // Values are deterministic; update them deliberately when the search work legitimately
    // changes.
    [Theory]
    [InlineData(9, 3, 3, 163)]
    [InlineData(11, 3, 3, 588)]
    [InlineData(12, 4, 4, 515)]
    [InlineData(10, 3, 4, 1126)]
    [InlineData(12, 4, 3, 137)]
    [InlineData(12, 3, 3, 735)]
    [InlineData(8, 4, 2, 7)]
    [InlineData(10, 3, 5, 674)]
    [InlineData(13, 4, 3, 146)]
    public void Compact_SearchedStateCountStaysWithinBaseline(int n, int m, int k, int searchedStateCap)
    {
        StrategyPlan compact = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildCompactPlan({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildCompactPlan());

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
    [InlineData(9, 3, 3, 115)]
    [InlineData(11, 3, 3, 353)]
    [InlineData(12, 3, 3, 606)]
    [InlineData(12, 4, 4, 289)]
    [InlineData(12, 4, 3, 79)]
    [InlineData(10, 3, 4, 451)]
    [InlineData(10, 3, 5, 416)]
    [InlineData(12, 4, 5, 785)]
    [InlineData(13, 4, 3, 136)]
    [InlineData(8, 4, 2, 6)]
    [InlineData(9, 4, 3, 18)]
    [InlineData(8, 3, 4, 59)]
    [InlineData(8, 2, 3, 319)]
    [InlineData(9, 3, 4, 176)]
    [InlineData(10, 3, 6, 514)]
    [InlineData(5, 3, 2, 4)]
    [InlineData(6, 2, 2, 22)]
    [InlineData(10, 2, 2, 115)]
    [InlineData(25, 5, 3, 1706)]
    public void Default_SearchedStateCountStaysWithinBaseline(int n, int m, int k, int searchedStateCap)
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildDefaultPlan({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildDefaultPlan());

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
    [InlineData(9, 3, 3, 1667)]
    [InlineData(11, 3, 3, 7238)]
    [InlineData(12, 3, 3, 12535)]
    [InlineData(12, 4, 4, 14128)]
    [InlineData(12, 4, 3, 1024)]
    [InlineData(10, 3, 4, 8774)]
    [InlineData(10, 3, 5, 7498)]
    [InlineData(13, 4, 3, 2490)]
    [InlineData(8, 4, 2, 7)]
    [InlineData(9, 4, 3, 124)]
    [InlineData(8, 3, 4, 647)]
    [InlineData(9, 3, 4, 3461)]
    [InlineData(10, 3, 6, 10019)]
    [InlineData(5, 3, 2, 12)]
    [InlineData(6, 2, 2, 96)]
    [InlineData(10, 2, 2, 935)]
    [InlineData(25, 5, 3, 108023)]
    public void Default_OutcomesConstructedStaysWithinBaseline(int n, int m, int k, int outcomesCap)
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildDefaultPlan({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildDefaultPlan());

        Assert.True(
            plan.SearchStatistics.OutcomesConstructed <= outcomesCap,
            $"default outcomes constructed regressed to {plan.SearchStatistics.OutcomesConstructed} (cap {outcomesCap})");
    }

    // Outcome-construction monitor for the compact pass. The compact selection re-enumerates
    // group outcomes on top of phase 1 -- including the heavier display-branch enumeration used
    // to count displayed edges for its objective -- so it constructs many more outcomes than the
    // default; this count is the primary cost target for compact-search optimization. Caps are
    // the current deterministic counts -- ratchet them down when an optimization legitimately
    // cuts outcome construction.
    [Theory]
    [InlineData(9, 3, 3, 6111)]
    [InlineData(11, 3, 3, 20303)]
    [InlineData(12, 4, 4, 29758)]
    [InlineData(10, 3, 4, 62856)]
    [InlineData(12, 4, 3, 6753)]
    [InlineData(12, 3, 3, 16106)]
    [InlineData(8, 4, 2, 31)]
    [InlineData(10, 3, 5, 12644)]
    [InlineData(13, 4, 3, 3046)]
    public void Compact_OutcomesConstructedStaysWithinBaseline(int n, int m, int k, int outcomesCap)
    {
        StrategyPlan compact = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildCompactPlan({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildCompactPlan());

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
    [InlineData(9, 3, 3, 155)]
    [InlineData(11, 3, 3, 534)]
    [InlineData(12, 3, 3, 943)]
    [InlineData(12, 4, 4, 2789)]
    [InlineData(12, 4, 3, 205)]
    [InlineData(10, 3, 4, 617)]
    [InlineData(10, 3, 5, 510)]
    [InlineData(13, 4, 3, 539)]
    [InlineData(8, 4, 2, 0)]
    [InlineData(9, 4, 3, 37)]
    [InlineData(8, 3, 4, 65)]
    [InlineData(9, 3, 4, 298)]
    [InlineData(10, 3, 6, 617)]
    [InlineData(5, 3, 2, 3)]
    [InlineData(10, 2, 2, 9)]
    public void Default_DuplicateOutcomeSkipsStaysWithinBaseline(int n, int m, int k, int duplicateSkipCap)
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildDefaultPlan({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildDefaultPlan());

        Assert.True(
            plan.SearchStatistics.Diagnostics.DuplicateOutcomeSkips <= duplicateSkipCap,
            $"default duplicate outcome skips regressed to {plan.SearchStatistics.Diagnostics.DuplicateOutcomeSkips} (cap {duplicateSkipCap})");
    }

    [Fact]
    public void BuildDefaultPlan_ReducesKAboveHalf_ToDualProblem()
    {
        StrategyPlan reduced = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.BuildDefaultPlan(10, 4, 2)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(10, 4, 2, cancellationToken).BuildDefaultPlan());

        StrategyPlan dualInput = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.BuildDefaultPlan(10, 4, 8)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(10, 4, 8, cancellationToken).BuildDefaultPlan());

        Assert.Equal(2, dualInput.K);
        Assert.Equal(reduced.MaxStep, dualInput.MaxStep);
        Assert.Equal(reduced.TotalBranchEdges, dualInput.TotalBranchEdges);
    }

    // Candidate-group enumeration monitor for the default pass. CandidateGroupsEnumerated counts the
    // symmetry-class representatives canonicalized before cross-class de-duplication -- i.e. the
    // distinct comparison groups the search actually materializes per state. The symmetry-aware
    // group generator (PR #96) collapses each automorphism orbit to a single representative up
    // front, so this counter is the direct measure of that win: on highly symmetric states it is
    // far below the naive C(active, m). The 25,5,3 row is the showcase -- its root alone would
    // canonicalize C(25,5)=53,130 groups without symmetry awareness but emits one representative.
    // Caps pin the current deterministic counts; an increase is a regression and a deliberate
    // decrease (a stronger symmetry collapse) is an improvement -- ratchet them down when one lands.
    [Theory]
    [InlineData(9, 3, 3, 1714)]
    [InlineData(11, 3, 3, 8152)]
    [InlineData(12, 3, 3, 15959)]
    [InlineData(12, 4, 4, 12932)]
    [InlineData(12, 4, 5, 37827)]
    [InlineData(12, 4, 3, 1024)]
    [InlineData(10, 3, 4, 8087)]
    [InlineData(10, 3, 5, 5679)]
    [InlineData(13, 4, 3, 2475)]
    [InlineData(8, 4, 2, 5)]
    [InlineData(9, 4, 3, 91)]
    [InlineData(8, 3, 4, 526)]
    [InlineData(8, 2, 3, 4213)]
    [InlineData(9, 3, 4, 3069)]
    [InlineData(10, 3, 6, 8773)]
    [InlineData(5, 3, 2, 4)]
    [InlineData(6, 2, 2, 99)]
    [InlineData(10, 2, 2, 1307)]
    [InlineData(25, 5, 3, 158293)]
    public void Default_CandidateGroupsEnumeratedStaysWithinBaseline(int n, int m, int k, int candidateGroupsCap)
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildDefaultPlan({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildDefaultPlan());

        Assert.True(
            plan.SearchStatistics.CandidateGroupsEnumerated <= candidateGroupsCap,
            $"default candidate groups enumerated regressed to {plan.SearchStatistics.CandidateGroupsEnumerated} (cap {candidateGroupsCap})");
    }

    // Symmetry-redundancy monitor for the compact pass. The compact selection re-enumerates group
    // outcomes on top of phase 1, so it surfaces more symmetric duplicates than the default pass;
    // this is the primary symmetry-collapse target for compact search. Caps pin the current
    // deterministic counts -- ratchet them down when an orbit/block-symmetry optimization lands.
    [Theory]
    [InlineData(9, 3, 3, 781)]
    [InlineData(11, 3, 3, 1865)]
    [InlineData(12, 4, 4, 6724)]
    [InlineData(10, 3, 4, 5715)]
    [InlineData(12, 4, 3, 2466)]
    [InlineData(12, 3, 3, 1097)]
    [InlineData(8, 4, 2, 11)]
    [InlineData(10, 3, 5, 732)]
    [InlineData(13, 4, 3, 659)]
    public void Compact_DuplicateOutcomeSkipsStaysWithinBaseline(int n, int m, int k, int duplicateSkipCap)
    {
        StrategyPlan compact = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildCompactPlan({n}, {m}, {k})",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildCompactPlan());

        Assert.True(
            compact.SearchStatistics.Diagnostics.DuplicateOutcomeSkips <= duplicateSkipCap,
            $"compact duplicate outcome skips regressed to {compact.SearchStatistics.Diagnostics.DuplicateOutcomeSkips} (cap {duplicateSkipCap})");
    }

    // The 25,6,3 fifth step sorts six leaders whose tail positions are all doomed regardless of
    // the comparison result. The display path folds those orderings into 19 "doomed-tail" edges
    // (one per distinct doomed prefix, symmetry already collapsed), each carrying a brace-set
    // pattern, an inline legend appended to the pattern line, and a factorial "a sym x b tail"
    // count factorization. The edge counts must still cover every real ordering, so they sum to the
    // full 6! = 360 permutations.
    private const string S5DoomedTailLegend = "A = {#7, #13, #19}";

    [Fact]
    public void N25M6K3_FifthStepRendersNineteenDoomedTailEdges()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.BuildDefaultPlan(25, 6, 3)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(25, 6, 3, cancellationToken).BuildDefaultPlan());

        StrategyNode s5 = StrategyTestHelpers.FollowBranchPath(
            plan.Root,
            "#1 > #2 > #3 > #4 > #5 > #6",
            "#7 > #8 > #9 > #10 > #11 > #12",
            "#13 > #14 > #15 > #16 > #17 > #18",
            "#19 > #20 > #21 > #22 > #23 > #24");

        Assert.Equal(new[] { 0, 1, 6, 12, 18, 24 }, s5.Group);
        Assert.Equal(19, s5.Branches.Count);

        int total = 0;
        foreach (StrategyBranch branch in s5.Branches)
        {
            Assert.NotNull(branch.EquivalentOrders);

            // Edges whose surviving class collapses fully into an inline "{...}" set carry no
            // legend; the rest name that single class with the shared "A = {#7, #13, #19}" legend.
            string? legend = branch.EquivalentOrders!.Legend;
            Assert.True(
                legend is null || legend == S5DoomedTailLegend,
                $"unexpected legend '{legend}'");
            total += branch.EquivalentOrders.Count;
        }

        Assert.Equal(360, total);
    }

    [Theory]
    // 3! tail: the prefix already pins the representative, the doomed tail is free; the
    // whole surviving class sits inline, so this edge needs no legend.
    [InlineData("#1 > #2 > #25 > #7 > #13 > #19", 6, "3! tail", "#1 > #2 > #25 > {#7, #13, #19}", null)]
    // 3! sym x 3! tail: one class member leads in the prefix while the rest share the tail, so the
    // class stays split across placeholders and keeps its legend.
    [InlineData("#1 > #7 > #13 > #2 > #19 > #25", 36, "3! sym x 3! tail", "#1 > A1 > A2 > {#2, A3, #25}", "A = {#7, #13, #19}")]
    // 3! sym x 3!/2! tail: both #1 and #2 land in the doomed tail, so the tail keeps #1 > #2; the
    // surviving class is a contiguous prefix run and folds into an inline set.
    [InlineData("#7 > #13 > #19 > #1 > #2 > #25", 18, "3! sym x 3!/2! tail", "{#7, #13, #19} > {#1, #2, #25} ; #1 > #2", null)]
    public void N25M6K3_DoomedTailEdgeCarriesExpectedPatternAndFormula(
        string orderText, int expectedCount, string expectedFormula, string expectedPattern, string? expectedLegend)
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.BuildDefaultPlan(25, 6, 3)",
            RegressionTestTimeout,
            cancellationToken => new StrategyBuilder(25, 6, 3, cancellationToken).BuildDefaultPlan());

        StrategyNode s5 = StrategyTestHelpers.FollowBranchPath(
            plan.Root,
            "#1 > #2 > #3 > #4 > #5 > #6",
            "#7 > #8 > #9 > #10 > #11 > #12",
            "#13 > #14 > #15 > #16 > #17 > #18",
            "#19 > #20 > #21 > #22 > #23 > #24");

        StrategyBranch branch = StrategyTestHelpers.FindChildBranch(s5, orderText);

        Assert.NotNull(branch.EquivalentOrders);
        Assert.Equal(expectedCount, branch.EquivalentOrders!.Count);
        Assert.Equal(expectedFormula, branch.EquivalentOrders.CountFormula);
        Assert.Equal(expectedPattern, branch.EquivalentOrders.PatternText);
        Assert.Equal(expectedLegend, branch.EquivalentOrders.Legend);

        // The legend, when present, is appended inline to the pattern line rather than on its own row.
        string expectedLine = expectedLegend is null
            ? $"pattern: {expectedPattern}"
            : $"pattern: {expectedPattern} ; {expectedLegend}";
        Assert.Equal(expectedLine, StrategyTextRenderer.FormatEquivalentPatternLine(branch.EquivalentOrders));
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
            else if (lines[i].StartsWith("phases: ", StringComparison.Ordinal))
                lines[i] = "phases: <phases>";
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

    public static IEnumerable<StrategyBranch> EnumerateBranches(StrategyNode node)
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
