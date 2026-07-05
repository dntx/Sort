using Xunit;

// Locks the reference-depth MaxStep bug. A Reference node is a leaf that stands in for an
// already-expanded subtree still needing more sorts; counting it as depth 0 undercounts the true
// worst-case steps when the reference lies on the deepest path. greedy-feasible(6,2,2) materializes a
// valid 7-sort tree whose deepest path ends in a "+1 step" reference; before the fix MaxStep reported
// 6 -- BELOW the proven optimum 7, i.e. an unsound feasible upper bound. No prior test covered this
// (the existing upper-bound-never-below-opt test only used m>=3 shapes, and exact trees for these
// shapes happen not to trigger the undercount).
public class MaxStepReferenceDepthTests
{
    [Fact]
    public void GreedyFeasible_6_2_2_MaxStepCountsReferenceDepth()
    {
        StrategyPlan feasible = new StrategyBuilder(6, 2, 2).BuildGreedyFeasiblePlan();

        // True worst case is 7 (== proven optimum); the naive leaf-counting reported 6.
        Assert.Equal(7, feasible.MaxStep);
    }

    // General soundness across low-m shapes (which trigger the reference-depth undercount): a feasible
    // upper bound U must never be below the proven optimum. 6,2,2 fails this before the fix.
    [Theory]
    [InlineData(6, 2, 2)]
    [InlineData(7, 2, 3)]
    [InlineData(8, 2, 3)]
    [InlineData(9, 2, 2)]
    public void GreedyFeasible_UpperBoundNeverBelowOptimum_LowM(int n, int m, int k)
    {
        int optimum = new StrategyBuilder(n, m, k).BuildStepProofPlan().MaxStep;
        int feasible = new StrategyBuilder(n, m, k).BuildGreedyFeasiblePlan().MaxStep;

        Assert.True(feasible >= optimum,
            $"feasible upper bound {feasible} was below the true optimum {optimum} for ({n},{m},{k})");
    }
}
