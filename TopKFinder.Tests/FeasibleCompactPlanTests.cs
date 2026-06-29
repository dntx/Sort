using System;
using Xunit;

// Guards the greedy-mode EDGE phase (BuildFeasibleCompactPlan), whose step ceiling is the
// constructive feasible upper bound U (ConstructiveRootUpperBound). The step phase has its own
// coverage in FeasiblePlanTests; this fixes the previously-untested edge path so the budget source
// can never silently over-constrain the compact pass (returning an unsolvable sentinel) or emit a
// plan that violates the U/opt bounds.
public class FeasibleCompactPlanTests
{
    // The edge pass must always produce a valid, fully-grouped strategy under the constructive U
    // budget -- never throw "no group fits the budget" -- and stay a feasible plan.
    [Theory]
    [InlineData(10, 5, 5)]
    [InlineData(16, 5, 5)]
    [InlineData(25, 5, 5)]
    [InlineData(12, 4, 4)]
    [InlineData(12, 5, 5)]
    [InlineData(9, 3, 3)]
    public void FeasibleCompactPlan_IsValidStrategy(int n, int m, int k)
    {
        StrategyPlan plan = new StrategyBuilder(n, m, k).BuildFeasibleCompactPlan();

        Assert.True(plan.IsFeasibleUpperBound);
        Assert.True(plan.MaxStep > 0, "feasible compact plan should take at least one comparison");
        AssertEveryDecisionHasGroup(plan.Root);
    }

    // The edge pass minimizes displayed edges under the budget and may pick up a smaller real step
    // for free, so its MaxStep must never exceed the step phase's feasible U. This mirrors the
    // production orchestrators (Program.cs / MainForm.cs), which reuse ONE builder for step then edge:
    // the step phase threads its materialized U as the edge budget, guaranteeing edge is no worse.
    [Theory]
    [InlineData(10, 5, 5)]
    [InlineData(16, 5, 5)]
    [InlineData(12, 4, 4)]
    [InlineData(12, 5, 5)]
    public void FeasibleCompactPlan_StepNeverExceedsFeasibleUpperBound(int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k);
        int stepU = builder.BuildFeasiblePlan().MaxStep;
        int edgeStep = builder.BuildFeasibleCompactPlan().MaxStep;

        Assert.True(edgeStep <= stepU,
            $"feasible compact step {edgeStep} exceeded the feasible upper bound {stepU}");
    }

    // The edge plan is still an achievable strategy, so its worst-case steps must never drop below
    // the true optimum on cases the exact search can solve.
    [Theory]
    [InlineData(10, 5, 5)]
    [InlineData(12, 5, 5)]
    [InlineData(9, 3, 3)]
    [InlineData(12, 4, 4)]
    public void FeasibleCompactPlan_StepNeverBelowOptimum(int n, int m, int k)
    {
        int optimum = new StrategyBuilder(n, m, k).BuildDefaultPlan().MaxStep;
        int edgeStep = new StrategyBuilder(n, m, k).BuildFeasibleCompactPlan().MaxStep;

        Assert.True(edgeStep >= optimum,
            $"feasible compact step {edgeStep} was below the true optimum {optimum}");
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
