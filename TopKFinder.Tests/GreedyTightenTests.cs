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

    // Regression lock for the previously observed back-edge-prone region: materialization must not
    // create reference cycles in the rendered strategy graph.
    [Theory]
    [InlineData(9, 2, 4)]
    [InlineData(8, 2, 4)]
    [InlineData(7, 2, 3)]
    public void GreedyTightenPlan_DoesNotCreateReferenceCycles(int n, int m, int k)
    {
        StrategyPlan plan = new StrategyBuilder(n, m, k).BuildGreedyTightenPlan();

        Assert.False(HasReferenceCycle(plan.Root),
            $"greedy-tighten produced a reference cycle for ({n},{m},{k})");
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

    private static bool HasReferenceCycle(StrategyNode root)
    {
        var targets = new Dictionary<int, StrategyNode>();
        IndexTargets(root, targets);
        var onStack = new HashSet<StrategyNode>(ReferenceEqualityComparer.Instance);
        var done = new HashSet<StrategyNode>(ReferenceEqualityComparer.Instance);
        return DetectCycle(root, targets, onStack, done);
    }

    private static void IndexTargets(StrategyNode node, Dictionary<int, StrategyNode> targets)
    {
        if (node.Kind == StrategyNodeKind.Decision && node.Branches.Count > 0)
            targets[node.StateId] = node;
        foreach (StrategyBranch branch in node.Branches)
            IndexTargets(branch.Next, targets);
    }

    private static bool DetectCycle(
        StrategyNode node,
        Dictionary<int, StrategyNode> targets,
        HashSet<StrategyNode> onStack,
        HashSet<StrategyNode> done)
    {
        if (done.Contains(node))
            return false;
        if (!onStack.Add(node))
            return true;

        bool cycle;
        if (node.Kind == StrategyNodeKind.Reference)
        {
            cycle = targets.TryGetValue(node.StateId, out StrategyNode? target)
                && DetectCycle(target, targets, onStack, done);
        }
        else
        {
            cycle = false;
            foreach (StrategyBranch branch in node.Branches)
            {
                if (DetectCycle(branch.Next, targets, onStack, done))
                {
                    cycle = true;
                    break;
                }
            }
        }

        onStack.Remove(node);
        done.Add(node);
        return cycle;
    }
}
