using Xunit;

public sealed class DisplayRenderEngineTests
{
    [Theory]
    [InlineData(9, 3, 3)]
    [InlineData(10, 3, 5)]
    [InlineData(12, 4, 5)]
    public void RenderStrategyText_MatchesLegacyRenderer(int n, int m, int k)
    {
        StrategyPlan plan = new StrategyBuilder(n, m, k).BuildStepProofStage();
        var engine = new DisplayRenderEngine();

        string expected = StrategyTextRenderer.Render(plan);
        string actual = engine.RenderStrategyText(plan);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(9, 3, 3)]
    [InlineData(10, 3, 5)]
    [InlineData(12, 4, 5)]
    public void OverviewRendering_MatchesLegacyRenderer(int n, int m, int k)
    {
        StrategyPlan plan = new StrategyBuilder(n, m, k).BuildStepProofStage();
        var engine = new DisplayRenderEngine();

        StrategyOverview expectedOverview = StrategyOverviewRenderer.Build(plan);
        StrategyOverview actualOverview = engine.BuildOverview(plan);
        AssertOverviewEqual(expectedOverview, actualOverview);

        string expectedText = StrategyOverviewRenderer.RenderText(plan);
        string actualText = engine.RenderOverviewText(plan);
        Assert.Equal(expectedText, actualText);
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