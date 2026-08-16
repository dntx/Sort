using TopKFinder;
using Xunit;

public sealed class LowerBoundExperimentTests
{
    [Theory]
    [InlineData(9, 18, 9)]
    [InlineData(10, 20, 10)]
    [InlineData(11, 22, 11)]
    [InlineData(12, 24, 12)]
    public void WidthLimitedInformation_TwoLongChains_StrictlyBeatsExistingStaticBounds(
        int chainLength,
        int n,
        int k)
    {
        ComparisonState state = CreateDisjointChains(chainLength);
        var builder = new StrategyBuilder(n, m: 3, k);

        LowerBoundBreakdown breakdown = builder.GetLowerBoundBreakdownForTesting(state, remainingSlots: k);

        Assert.Equal(2, breakdown.Information);
        Assert.Equal(1, breakdown.Antichain);
        Assert.Equal(2, breakdown.Baseline);
        Assert.Equal(3, breakdown.WidthLimitedInformation);

        int exact = builder.GetMinWorstCaseStepsExactForTesting(state, remainingSlots: k);
        Assert.Equal(4, exact);
    }

    [Theory]
    [InlineData(9, 18, 9)]
    [InlineData(10, 20, 10)]
    [InlineData(11, 22, 11)]
    [InlineData(12, 24, 12)]
    public void WidthLimitedInformation_TwoLongChains_RejectsBudgetWithoutExpandingOutcomes(
        int chainLength,
        int n,
        int k)
    {
        ComparisonState state = CreateDisjointChains(chainLength);
        var withoutBound = new StrategyBuilder(n, m: 3, k)
        {
            EnableWidthLimitedInformationBoundForTesting = false,
        };
        var withBound = new StrategyBuilder(n, m: 3, k);

        int resultWithout = withoutBound.GetMinWorstCaseStepsBoundedForTesting(
            state.Clone(), remainingSlots: k, budget: 2);
        int resultWith = withBound.GetMinWorstCaseStepsBoundedForTesting(
            state.Clone(), remainingSlots: k, budget: 2);

        Assert.True(resultWithout > 2);
        Assert.Equal(3, resultWith);
        Assert.True(withoutBound.OutcomesConstructedForTesting > 0);
        Assert.Equal(0, withBound.OutcomesConstructedForTesting);
    }

    [Fact]
    public void WidthLimitedInformation_ThreeChainsAndFourWayComparisons_StrictlyBeatsBaseline()
    {
        ComparisonState state = CreateDisjointChains(chainCount: 3, chainLength: 16);
        var builder = new StrategyBuilder(n: 48, m: 4, k: 16);

        LowerBoundBreakdown breakdown = builder.GetLowerBoundBreakdownForTesting(state, remainingSlots: 16);

        Assert.Equal(2, breakdown.Information);
        Assert.Equal(1, breakdown.Antichain);
        Assert.Equal(2, breakdown.Baseline);
        Assert.Equal(3, breakdown.WidthLimitedInformation);
    }

    private static ComparisonState CreateDisjointChains(int chainLength)
        => CreateDisjointChains(chainCount: 2, chainLength);

    private static ComparisonState CreateDisjointChains(int chainCount, int chainLength)
    {
        var state = new ComparisonState(chainLength * chainCount);
        for (int chain = 0; chain < chainCount; chain++)
        {
            int offset = chain * chainLength;
            for (int i = 0; i < chainLength - 1; i++)
                state.AddRelation(offset + i, offset + i + 1);
        }

        return state;
    }
}
