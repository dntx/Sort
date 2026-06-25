using Xunit;

// Guards the always-on phase-1 dominance (subsumption) lower-bound pruning on two axes:
//   1. Soundness  -- every dominance bound derived from an already-solved state brackets the true
//      worst-case step count (lower <= true <= upper) and the verified embedding search never
//      exhausts its budget at these sizes. A monotonicity regression surfaces here as a non-zero
//      unsound/budget-exhaustion observation under the opt-in metric.
//   2. Effectiveness -- the pruning actually fires in the production configuration: each "bound
//      raise" is a search branch whose analytic lower bound was tightened by a verified embedding
//      into an earlier solved state, pruning equally-optimal ties sooner WITHOUT changing the
//      displayed strategy tree (verified separately by the byte-identical snapshot/output monitors).
public class DominanceMetricTests
{
    [Theory]
    [InlineData(9, 3, 3)]
    [InlineData(13, 4, 3)]
    [InlineData(12, 4, 4)]
    public void Phase1DominanceBoundsAreSound(int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k) { EnableDominanceMetric = true };
        builder.BuildDefaultPlan();

        Assert.Equal(0, builder.DominanceUnsoundObservations);
        Assert.Equal(0, builder.DominanceBudgetExhaustions);
    }

    [Theory]
    [InlineData(9, 3, 3, 26)]
    [InlineData(13, 4, 3, 68)]
    [InlineData(12, 4, 4, 166)]
    public void Phase1DominanceLowerBoundPruningFires(int n, int m, int k, int minBoundRaises)
    {
        var builder = new StrategyBuilder(n, m, k);
        builder.BuildDefaultPlan();

        // Floor, not an exact count: a legitimate improvement may only raise it. The accompanying
        // searched-state / outcomes-constructed monitors pin the downstream search-work reduction.
        Assert.True(
            builder.DominanceBoundRaises >= minBoundRaises,
            $"dominance bound raises regressed to {builder.DominanceBoundRaises} (floor {minBoundRaises})");
    }
}
