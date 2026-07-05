using System;
using Xunit;

// Framework-level guards for the GreedyTighten (Phase 0) stage (StrategyBuilder.GreedyTighten.cs).
// GreedyTighten locally restructures the greedy-feasible tree to lower the longest path; it only
// tightens the feasible upper bound (NO proof semantics). These tests pin the invariants that must
// hold for ANY candidate source / scorer (the 阶段 B tuning): the produced plan is a valid strategy,
// never drops below the true optimum, and is never worse than the greedy-feasible baseline it edits.
public class GreedyTightenTests
{
    [Theory]
    [InlineData(8, 3, 3)]
    [InlineData(10, 5, 5)]
    [InlineData(12, 4, 4)]
    [InlineData(12, 5, 5)]
    [InlineData(9, 3, 3)]
    public void GreedyTightenPlan_IsValidStrategy(int n, int m, int k)
    {
        StrategyPlan plan = new StrategyBuilder(n, m, k).BuildGreedyTightenPlan();

        Assert.True(plan.IsFeasibleUpperBound);
        Assert.True(plan.MaxStep > 0, "greedy-tighten plan should take at least one comparison");
        AssertEveryDecisionHasGroup(plan.Root);
    }

    // Soundness: the tightened tree is still an achievable strategy, so its worst-case step count can
    // never drop below the true optimum on shapes the exact search can solve.
    [Theory]
    [InlineData(8, 3, 3)]
    [InlineData(10, 5, 5)]
    [InlineData(12, 5, 5)]
    [InlineData(9, 3, 3)]
    [InlineData(12, 4, 4)]
    public void GreedyTightenPlan_StepNeverBelowOptimum(int n, int m, int k)
    {
        int optimum = new StrategyBuilder(n, m, k).BuildStepProofPlan().MaxStep;
        int tightened = new StrategyBuilder(n, m, k).BuildGreedyTightenPlan().MaxStep;

        Assert.True(tightened >= optimum,
            $"greedy-tighten step {tightened} was below the true optimum {optimum}");
    }

    // Never worse than the greedy-feasible baseline it restructures: GreedyTighten only commits an
    // edit when it strictly lowers a subtree height, and an empty edit set reproduces greedy-feasible.
    [Theory]
    [InlineData(8, 3, 3)]
    [InlineData(10, 5, 5)]
    [InlineData(12, 5, 5)]
    [InlineData(9, 3, 3)]
    [InlineData(12, 4, 4)]
    public void GreedyTightenPlan_NeverWorseThanFeasible(int n, int m, int k)
    {
        int feasible = new StrategyBuilder(n, m, k).BuildGreedyFeasiblePlan().MaxStep;
        int tightened = new StrategyBuilder(n, m, k).BuildGreedyTightenPlan().MaxStep;

        Assert.True(tightened <= feasible,
            $"greedy-tighten step {tightened} was worse than the greedy-feasible upper bound {feasible}");
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
