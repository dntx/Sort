using Xunit;

// Locks in the quantified phase-1 dominance (subsumption) opportunity and, critically, the
// soundness invariant: every dominance bound derived from an already-solved state must bracket the
// true worst-case step count (lower <= true <= upper). A regression that breaks monotonicity would
// surface here as a non-zero unsound observation before any pruning is ever wired into the search.
public class DominanceMetricTests
{
    [Theory]
    [InlineData(9, 3, 3, 8, 54)]
    [InlineData(13, 4, 3, 24, 81)]
    [InlineData(12, 4, 4, 105, 312)]
    public void Phase1DominanceBoundsAreSoundAndPresent(
        int n, int m, int k, int minExactDeterminations, int minLowerFound)
    {
        var builder = new StrategyBuilder(n, m, k) { EnableDominanceMetric = true };
        builder.Build();

        // Soundness: bounds never contradict the true cost, and the verified embedding search never
        // gives up early at these sizes.
        Assert.Equal(0, builder.DominanceUnsoundObservations);
        Assert.Equal(0, builder.DominanceBudgetExhaustions);

        // Opportunity: a meaningful share of distinct (non-isomorphic) states could be resolved or
        // bounded purely by dominance against earlier solutions. These are floors; legitimate
        // improvements may only raise them.
        Assert.True(
            builder.DominanceExactDeterminations >= minExactDeterminations,
            $"exact determinations regressed to {builder.DominanceExactDeterminations} (floor {minExactDeterminations})");
        Assert.True(
            builder.DominanceLowerFound >= minLowerFound,
            $"lower bounds found regressed to {builder.DominanceLowerFound} (floor {minLowerFound})");
    }
}
