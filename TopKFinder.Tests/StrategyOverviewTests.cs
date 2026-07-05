using System;
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
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildStepProofPlan());
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
    public void N12M4K4_EndsOnASolvedTerminal()
    {
        StrategyOverview overview = BuildOverview(12, 4, 4);

        OverviewRow finish = overview.Rows[^1];
        Assert.StartsWith("Finish: top 4 =", finish.Headline);
    }

    [Fact]
    public void EveryLinkStateIdResolvesToARealState()
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            "StrategyBuilder.BuildDefaultPlan(25, 5, 3)",
            OverviewTestTimeout,
            cancellationToken => new StrategyBuilder(25, 5, 3, cancellationToken).BuildStepProofPlan());

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
}
