using System.Collections.Generic;
using Xunit;
using TopKFinder;

public sealed class BranchSelectionScoringServiceTests
{
    [Fact]
    public void BuildScoreComponents_ReflectsGuaranteedAndPairMetrics()
    {
        var state = new ComparisonState(4);
        state.ApplyOrder(new[] { 0, 1, 2, 3 });
        var group = new List<int> { 0, 1, 2 };

        var score = BranchSelectionScoringService.BuildScoreComponents(state, remainingSlots: 2, group);

        Assert.Equal(2, score.GuaranteedTopHits);
        Assert.Equal(3, score.GroupSize);
        Assert.Equal(0, score.FreshItems);
        Assert.Equal(0, score.UnresolvedPairs);
        Assert.True(score.UnrelatedScore < 0);
    }
}