using System;
using System.Collections.Generic;
using Xunit;

public class GreedyFeasibleStageTests
{
    // The greedy feasible stage must be a structurally valid strategy: a true tree whose every
    // decision node carries a chosen comparison group, terminating in resolved top sets.
    [Theory]
    [InlineData(10, 5, 5)]
    [InlineData(16, 5, 5)]
    [InlineData(25, 5, 5)]
    [InlineData(12, 4, 4)]
    [InlineData(9, 3, 3)]
    public void GreedyFeasibleStage_IsValidStrategy(int n, int m, int k)
    {
        StrategyPlan plan = new StrategyBuilder(n, m, k).BuildGreedyFeasibleStage();

        Assert.True(plan.IsFeasibleUpperBound);
        Assert.True(plan.MaxStep > 0, "feasible plan should take at least one comparison");
        AssertEveryDecisionHasGroup(plan.Root);
    }

    // The hardest target shape (25,5,5) never resolves under the exact search, but the greedy
    // constructor must still finish near-instantly and yield a finite feasible upper bound.
    [Fact]
    public void GreedyFeasibleStage_HardestShape_CompletesInstantly()
    {
        var start = DateTime.UtcNow;
        StrategyPlan plan = new StrategyBuilder(25, 5, 5).BuildGreedyFeasibleStage();
        var elapsed = DateTime.UtcNow - start;

        Assert.True(plan.MaxStep > 0);
        Assert.True(elapsed < TimeSpan.FromSeconds(20),
            $"greedy feasible stage for 25,5,5 took {elapsed.TotalSeconds:F1}s; expected near-instant");
    }

    // Regression: large shapes (here 26,10,10) produce a feasible-top-set count that exceeds the
    // per-step outcome ceiling (m! = 10! = 3.6M), so the information-theoretic lower-bound loop
    // multiplies its accumulator past int.MaxValue on the second iteration. The accumulator must be
    // wide enough (long) not to throw OverflowException mid-build.
    [Fact]
    public void GreedyFeasibleStage_LargeShape_DoesNotOverflowLowerBound()
    {
        StrategyPlan plan = new StrategyBuilder(26, 10, 10).BuildGreedyFeasibleStage();

        Assert.True(plan.IsFeasibleUpperBound);
        Assert.True(plan.MaxStep > 0, "feasible plan should take at least one comparison");
    }

    // The plan exposes a proven lower bound (the L side of the squeeze) that never exceeds the
    // feasible upper bound U it achieves. L <= opt <= U must hold.
    [Theory]
    [InlineData(10, 5, 5)]
    [InlineData(16, 5, 5)]
    [InlineData(25, 5, 5)]
    public void GreedyFeasibleStage_SqueezeIsConsistent(int n, int m, int k)
    {
        StrategyPlan plan = new StrategyBuilder(n, m, k).BuildGreedyFeasibleStage();

        int lower = plan.SearchStatistics.RootProvenLowerBound;
        Assert.True(lower >= 1, $"expected a positive proven lower bound, got {lower}");
        Assert.True(lower <= plan.MaxStep,
            $"proven lower bound {lower} exceeded the feasible upper bound {plan.MaxStep}");
    }

    // The feasible upper bound must never be below the true optimum on cases the exact search can
    // solve -- it is an achievable strategy, so U >= opt always.
    [Theory]
    [InlineData(10, 5, 5)]
    [InlineData(12, 5, 5)]
    [InlineData(9, 3, 3)]
    [InlineData(12, 4, 4)]
    public void GreedyFeasibleStage_UpperBoundNeverBelowOptimum(int n, int m, int k)
    {
        int optimum = new StrategyBuilder(n, m, k).BuildStepProofStage().MaxStep;
        int feasible = new StrategyBuilder(n, m, k).BuildGreedyFeasibleStage().MaxStep;

        Assert.True(feasible >= optimum,
            $"feasible upper bound {feasible} was below the true optimum {optimum}");
    }

    // Pins the exact raw feasible U (before compact edge-tightening) that the cheap 1-ply
    // constructive lookahead achieves, so a regression that weakened candidate generation or
    // immediate-outcome scoring would fail loudly rather than silently slip back to the pure
    // single-ply antichain bound. The shapes below are the strongest witnesses: on 6,3,3 and
    // 6,3,2 the base antichain heuristic overshoots (U=5 and U=4), while lookahead reaches the
    // proven optimum; 9,5,4 and 7,4,4 also hit optimum. 14,4,5 and 12,6,6 are still above
    // optimum but their exact tightened values are pinned to catch any drift. See
    // docs/known-optimal-max-steps.md for the optima and docs/core-algorithm.md sec 4.6 for the policy.
    [Theory]
    [InlineData(6, 3, 3, 3)]
    [InlineData(6, 3, 2, 3)]
    [InlineData(9, 5, 4, 3)]
    [InlineData(7, 4, 4, 3)]
    [InlineData(14, 4, 5, 8)]
    [InlineData(12, 6, 6, 4)]
    public void GreedyFeasibleStage_LookaheadPinsRawUpperBound(int n, int m, int k, int expectedRawU)
    {
        int feasible = new StrategyBuilder(n, m, k).BuildGreedyFeasibleStage().MaxStep;

        Assert.Equal(expectedRawU, feasible);
    }

    // m=2 is intentionally routed around the generic 1-ply group lookahead. In the pairwise regime
    // each step has only two outcomes and the current immediate-outcome scorer is too low-signal for
    // its lower-bound cost, so greedy-feasible falls back to the base antichain heuristic. These raw
    // U pins lock that special-case behavior and fail loudly if m=2 is accidentally sent back through
    // the generic lookahead path.
    [Theory]
    [InlineData(6, 2, 2, 7)]
    [InlineData(7, 2, 3, 10)]
    [InlineData(8, 2, 3, 11)]
    public void GreedyFeasibleStage_M2SpecialCasePinsRawUpperBound(int n, int m, int k, int expectedRawU)
    {
        int feasible = new StrategyBuilder(n, m, k).BuildGreedyFeasibleStage().MaxStep;

        Assert.Equal(expectedRawU, feasible);
    }

    private static void AssertEveryDecisionHasGroup(StrategyNode node)
    {
        if (node.Branches.Count > 0)
        {
            Assert.NotNull(node.Group);
            Assert.NotEmpty(node.Group);
            foreach (StrategyBranch branch in node.Branches)
                AssertEveryDecisionHasGroup(branch.Next);
        }
    }
}
