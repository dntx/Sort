using Xunit;
using TopKFinder;

public sealed class DisplayRenderEngineTests
{
    private static readonly DisplayRenderEngine Engine = new();

    [Theory]
    [InlineData(9, 3, 3)]
    [InlineData(10, 3, 5)]
    [InlineData(12, 4, 5)]
    public void RenderStrategyText_MatchesLegacyRenderer(int n, int m, int k)
    {
        StrategyPlan plan = new StrategyBuilder(n, m, k).ExecuteStepProofStage();

        string expected = StrategyTextRenderer.Render(plan);
        string actual = Engine.RenderStrategyText(plan);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(9, 3, 3)]
    [InlineData(10, 3, 5)]
    [InlineData(12, 4, 5)]
    public void OverviewRendering_MatchesLegacyRenderer(int n, int m, int k)
    {
        StrategyPlan plan = new StrategyBuilder(n, m, k).ExecuteStepProofStage();

        StrategyOverview expectedOverview = StrategyOverviewRenderer.Build(plan);
        StrategyOverview actualOverview = Engine.BuildOverview(plan);
        AssertOverviewEqual(expectedOverview, actualOverview);

        string expectedText = StrategyOverviewRenderer.RenderText(plan);
        string actualText = Engine.RenderOverviewText(plan);
        Assert.Equal(expectedText, actualText);
    }

    [Theory]
    [InlineData(7, 3, 2)]
    [InlineData(8, 3, 3)]
    [InlineData(9, 4, 4)]
    [InlineData(10, 4, 5)]
    public void RenderStrategyText_MatchesLegacyRenderer_OnProjectionSensitiveExactPlans(int n, int m, int k)
    {
        StrategyPlan plan = new StrategyBuilder(n, m, k).ExecuteStepProofStage();

        string expected = StrategyTextRenderer.Render(plan);
        string actual = Engine.RenderStrategyText(plan);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RenderStrategyText_MatchesLegacyRenderer_OnRelabelingOrbitGreedyPlan()
    {
        StrategyPlan plan = new StrategyBuilder(20, 10, 10).ExecuteGreedyFeasibleStage();

        string expected = StrategyTextRenderer.Render(plan);
        string actual = Engine.RenderStrategyText(plan);

        Assert.Equal(expected, actual);
    }

    private static void AssertOverviewEqual(StrategyOverview expected, StrategyOverview actual)
    {
        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        for (int i = 0; i < expected.Rows.Count; i++)
        {
            OverviewRow expectedRow = expected.Rows[i];
            OverviewRow actualRow = actual.Rows[i];

            Assert.Equal(expectedRow.Headline, actualRow.Headline);
            Assert.Equal(expectedRow.Details, actualRow.Details);
            Assert.Equal(expectedRow.LinkStateId, actualRow.LinkStateId);
        }
    }
}