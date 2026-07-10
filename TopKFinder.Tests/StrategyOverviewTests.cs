using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

public sealed class StrategyOverviewTests
{
    private static readonly TimeSpan OverviewTestTimeout = TimeSpan.FromSeconds(30);

    private static StrategyOverview BuildOverview(int n, int m, int k)
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildDefaultPlan({n}, {m}, {k})",
            OverviewTestTimeout,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildStepProofStage());
        return StrategyOverviewRenderer.Build(plan);
    }

    private static StrategyOverview BuildGreedyOverview(int n, int m, int k)
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildGreedyFeasibleStage({n}, {m}, {k})",
            OverviewTestTimeout,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildGreedyFeasibleStage());
        return StrategyOverviewRenderer.Build(plan);
    }

    [Fact]
    public void N25M5K3_FoldsTheGroupingRoundAndFinishes()
    {
        StrategyOverview overview = BuildOverview(25, 5, 3);

        // Fully linear spine: heat round (steps 1-5) + leaders round (step 6) + finish.
        Assert.Equal(3, overview.Rows.Count);
        Assert.Contains("5 disjoint groups of 5", overview.Rows[0].Headline);
        Assert.StartsWith("Round 2", overview.Rows[1].Headline);
        Assert.StartsWith("Finish", overview.Rows[2].Headline);

        // Every row links to a real state so the UI can focus the matching tree node.
        Assert.All(overview.Rows, row => Assert.NotNull(row.LinkStateId));

        // No fork on this strategy: the line is a single representative path.
        Assert.DoesNotContain(overview.Rows, row => row.Details.Any(d => d.Contains("branches into")));
    }

    [Fact]
    public void N9M3K3_FlagsTheForkForTheDetailTree()
    {
        StrategyOverview overview = BuildOverview(9, 3, 3);

        Assert.Contains("3 disjoint groups of 3", overview.Rows[0].Headline);
        Assert.Contains(overview.Rows, row => row.Details.Any(d => d.Contains("branches into")));
        Assert.StartsWith("Finish", overview.Rows[^1].Headline);
    }

    [Fact]
    public void N10M9K9_ProducesSingleSortThenFinalChoice()
    {
        StrategyOverview overview = BuildOverview(10, 9, 9);

        Assert.Equal(2, overview.Rows.Count);
        Assert.StartsWith("Round 1", overview.Rows[0].Headline);
        Assert.Contains("sort", overview.Rows[0].Headline);
        Assert.StartsWith("Finish", overview.Rows[1].Headline);
        Assert.Contains("choose 1 of", overview.Rows[1].Headline);
    }

    [Fact]
    public void FoldsSecondPairingWaveIntoOneRound_WhenItemsWereSeenEarlier()
    {
        // Synthetic representative spine mirroring the greedy 20,2,6 rhythm:
        // steps 1-10 pairwise over 20 items, then a second disjoint pairing wave on survivors.
        int[][] firstWave =
        {
            new[] { 0, 1 }, new[] { 2, 3 }, new[] { 4, 5 }, new[] { 6, 7 }, new[] { 8, 9 },
            new[] { 10, 11 }, new[] { 12, 13 }, new[] { 14, 15 }, new[] { 16, 17 }, new[] { 18, 19 }
        };
        int[][] secondWave =
        {
            new[] { 0, 2 }, new[] { 4, 6 }, new[] { 8, 10 }, new[] { 12, 14 }, new[] { 16, 18 }
        };

        StrategyNode tail = StrategyNode.Terminal(17, new[] { 0, 2, 4, 8, 12, 16 });
        var waves = new List<IReadOnlyList<int>>(firstWave);
        waves.AddRange(secondWave);
        waves.Add(new[] { 0, 4 }); // break the 11-15 run with a new phase

        for (int step = waves.Count; step >= 1; step--)
        {
            IReadOnlyList<int> group = waves[step - 1];
            var effect = new StrategyEffect(
                Array.Empty<int>(),
                new[] { (step * 2 + 1) % 20 },
                Array.Empty<int>(),
                Enumerable.Range(0, Math.Max(0, 20 - step)).ToArray());
            tail = StrategyNode.Decision(
                step,
                step,
                group,
                new[] { new StrategyBranch($"g{step}", null, effect, tail) });
        }

        StrategyPlan plan = new(
            n: 20,
            m: 2,
            requestedK: 6,
            k: 6,
            root: tail,
            elapsed: TimeSpan.Zero,
            searchStatistics: CreateEmptySearchStatistics());

        StrategyOverview overview = StrategyOverviewRenderer.Build(plan);

        Assert.Contains(
            overview.Rows,
            row => row.Headline.Contains("steps 1\u201310") && row.Headline.Contains("disjoint groups of 2"));

        Assert.Contains(
            overview.Rows,
            row => row.Headline.Contains("steps 11\u201315") && row.Headline.Contains("disjoint groups of 2"));
    }

    [Fact]
    public void N12M4K4_EndsOnASolvedTerminal()
    {
        StrategyOverview overview = BuildOverview(12, 4, 4);

        OverviewRow finish = overview.Rows[^1];
        Assert.StartsWith("Finish: top 4 =", finish.Headline);
    }

    [Fact]
    public void N25M2K1_Greedy_FoldsAnchorChallengeChainIntoSingleRound()
    {
        StrategyOverview overview = BuildGreedyOverview(25, 2, 1);

        Assert.Equal(2, overview.Rows.Count);
        Assert.Contains("steps 1\u201323", overview.Rows[0].Headline);
        Assert.Contains("compare (#1) against 23 challengers", overview.Rows[0].Headline);
        Assert.Contains(overview.Rows[0].Details, d => d.Contains("challengers: (#2", StringComparison.Ordinal));
        Assert.StartsWith("Finish", overview.Rows[1].Headline);
    }

    [Fact]
    public void EveryLinkStateIdResolvesToARealState()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.BuildDefaultPlan(25, 5, 3)",
            OverviewTestTimeout,
            cancellationToken => new StrategyBuilder(25, 5, 3, cancellationToken).BuildStepProofStage());

        var stateIds = CollectStateIds(plan.Root).ToHashSet();
        foreach (OverviewRow row in StrategyOverviewRenderer.Build(plan).Rows)
            Assert.Contains(row.LinkStateId!.Value, stateIds);
    }

    private static System.Collections.Generic.IEnumerable<int> CollectStateIds(StrategyNode node)
    {
        yield return node.StateId;
        foreach (StrategyBranch branch in node.Branches)
        {
            if (branch.Next.Kind != StrategyNodeKind.Reference)
            {
                foreach (int id in CollectStateIds(branch.Next))
                    yield return id;
            }
            else
            {
                yield return branch.Next.StateId;
            }
        }
    }

    private static SearchStatistics CreateEmptySearchStatistics()
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
                rootIncumbents: Array.Empty<SearchMilestone>(),
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
            compactStatesSolved: 0,
            compactGroupsEnumerated: 0,
            compactStepOptimalGroups: 0,
            rootProvenLowerBound: 0);
    }
}
