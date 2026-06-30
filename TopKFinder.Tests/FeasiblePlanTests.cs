using System;
using System.Collections.Generic;
using Xunit;

public class FeasiblePlanTests
{
    // The greedy feasible plan must be a structurally valid strategy: a true tree whose every
    // decision node carries a chosen comparison group, terminating in resolved top sets.
    [Theory]
    [InlineData(10, 5, 5)]
    [InlineData(16, 5, 5)]
    [InlineData(25, 5, 5)]
    [InlineData(12, 4, 4)]
    [InlineData(9, 3, 3)]
    public void FeasiblePlan_IsValidStrategy(int n, int m, int k)
    {
        StrategyPlan plan = new StrategyBuilder(n, m, k).BuildFeasiblePlan();

        Assert.True(plan.IsFeasibleUpperBound);
        Assert.True(plan.MaxStep > 0, "feasible plan should take at least one comparison");
        AssertEveryDecisionHasGroup(plan.Root);
    }

    // The hardest target shape (25,5,5) never resolves under the exact search, but the greedy
    // constructor must still finish near-instantly and yield a finite feasible upper bound.
    [Fact]
    public void FeasiblePlan_HardestShape_CompletesInstantly()
    {
        var start = DateTime.UtcNow;
        StrategyPlan plan = new StrategyBuilder(25, 5, 5).BuildFeasiblePlan();
        var elapsed = DateTime.UtcNow - start;

        Assert.True(plan.MaxStep > 0);
        Assert.True(elapsed < TimeSpan.FromSeconds(20),
            $"greedy feasible plan for 25,5,5 took {elapsed.TotalSeconds:F1}s; expected near-instant");
    }

    // Regression: large shapes (here 26,10,10) produce a feasible-top-set count that exceeds the
    // per-step outcome ceiling (m! = 10! = 3.6M), so the information-theoretic lower-bound loop
    // multiplies its accumulator past int.MaxValue on the second iteration. The accumulator must be
    // wide enough (long) not to throw OverflowException mid-build.
    [Fact]
    public void FeasiblePlan_LargeShape_DoesNotOverflowLowerBound()
    {
        StrategyPlan plan = new StrategyBuilder(26, 10, 10).BuildFeasiblePlan();

        Assert.True(plan.IsFeasibleUpperBound);
        Assert.True(plan.MaxStep > 0, "feasible plan should take at least one comparison");
    }

    // The plan exposes a proven lower bound (the L side of the squeeze) that never exceeds the
    // feasible upper bound U it achieves. L <= opt <= U must hold.
    [Theory]
    [InlineData(10, 5, 5)]
    [InlineData(16, 5, 5)]
    [InlineData(25, 5, 5)]
    public void FeasiblePlan_SqueezeIsConsistent(int n, int m, int k)
    {
        StrategyPlan plan = new StrategyBuilder(n, m, k).BuildFeasiblePlan();

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
    public void FeasiblePlan_UpperBoundNeverBelowOptimum(int n, int m, int k)
    {
        int optimum = new StrategyBuilder(n, m, k).BuildDefaultPlan().MaxStep;
        int feasible = new StrategyBuilder(n, m, k).BuildFeasiblePlan().MaxStep;

        Assert.True(feasible >= optimum,
            $"feasible upper bound {feasible} was below the true optimum {optimum}");
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
