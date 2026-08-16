using TopKFinder;
using Xunit;

public sealed class LowerBoundExperimentTests
{
    [Fact]
    public void WidthLimitedInformation_TwoLongChains_StrictlyBeatsExistingStaticBounds()
    {
        ComparisonState state = CreateDisjointChains(chainLength: 10);
        var builder = new StrategyBuilder(n: 20, m: 3, k: 10);

        LowerBoundBreakdown breakdown = builder.GetLowerBoundBreakdownForTesting(state, remainingSlots: 10);

        Assert.Equal(2, breakdown.Information);
        Assert.Equal(1, breakdown.Antichain);
        Assert.Equal(2, breakdown.Baseline);
        Assert.Equal(3, breakdown.WidthLimitedInformation);

        int exact = builder.GetMinWorstCaseStepsExactForTesting(state, remainingSlots: 10);
        Assert.Equal(4, exact);
    }

    [Fact]
    public void WidthLimitedInformation_TwoLongChains_RejectsBudgetWithoutExpandingOutcomes()
    {
        ComparisonState state = CreateDisjointChains(chainLength: 10);
        var withoutBound = new StrategyBuilder(n: 20, m: 3, k: 10)
        {
            EnableWidthLimitedInformationBoundForTesting = false,
        };
        var withBound = new StrategyBuilder(n: 20, m: 3, k: 10);

        int resultWithout = withoutBound.GetMinWorstCaseStepsBoundedForTesting(
            state.Clone(), remainingSlots: 10, budget: 2);
        int resultWith = withBound.GetMinWorstCaseStepsBoundedForTesting(
            state.Clone(), remainingSlots: 10, budget: 2);

        Assert.True(resultWithout > 2);
        Assert.Equal(3, resultWith);
        Assert.True(withoutBound.OutcomesConstructedForTesting > 0);
        Assert.Equal(0, withBound.OutcomesConstructedForTesting);
    }

    private static ComparisonState CreateDisjointChains(int chainLength)
    {
        var state = new ComparisonState(chainLength * 2);
        for (int chain = 0; chain < 2; chain++)
        {
            int offset = chain * chainLength;
            for (int i = 0; i < chainLength - 1; i++)
                state.AddRelation(offset + i, offset + i + 1);
        }

        return state;
    }
}
