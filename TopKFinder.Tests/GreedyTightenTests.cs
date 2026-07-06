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
    [InlineData(10, 2, 5)]
    [InlineData(12, 5, 5)]
    [InlineData(9, 3, 3)]
    [InlineData(12, 4, 4)]
    public void GreedyTightenPlan_StepNeverBelowOptimum(int n, int m, int k)
    {
        int optimum = new StrategyBuilder(n, m, k).BuildStepProofStage().MaxStep;
        int tightened = new StrategyBuilder(n, m, k).BuildGreedyTightenPlan().MaxStep;

        Assert.True(tightened >= optimum,
            $"greedy-tighten step {tightened} was below the true optimum {optimum}");
    }

    // Never worse than the greedy-feasible baseline it restructures: GreedyTighten only commits an
    // edit when it strictly lowers a subtree height, and an empty edit set reproduces greedy-feasible.
    [Theory]
    [InlineData(8, 3, 3)]
    [InlineData(10, 5, 5)]
    [InlineData(10, 2, 5)]
    [InlineData(12, 5, 5)]
    [InlineData(9, 3, 3)]
    [InlineData(12, 4, 4)]
    public void GreedyTightenPlan_NeverWorseThanFeasible(int n, int m, int k)
    {
        int feasible = new StrategyBuilder(n, m, k).BuildGreedyFeasibleStage().MaxStep;
        int tightened = new StrategyBuilder(n, m, k).BuildGreedyTightenPlan().MaxStep;

        Assert.True(tightened <= feasible,
            $"greedy-tighten step {tightened} was worse than the greedy-feasible upper bound {feasible}");
    }

    // Independent soundness lock, valid even where the exact search is intractable (unlike
    // StepNeverBelowOptimum, which needs BuildStepProofStage). Re-simulates the committed policy from the
    // root, checking every state makes progress, no adversary path cycles, and every path ends at a
    // trusted top-k terminal; it throws on any violation. A returned depth equal to the plan's MaxStep
    // confirms MaxStep is the true worst case of a genuinely valid strategy -- so the greedy-tighten
    // upper bound is sound. Includes hard shapes (e.g. 15,4,4; 10,2,5) beyond the exact-checkable range.
    [Theory]
    [InlineData(8, 3, 3)]
    [InlineData(9, 2, 4)]
    [InlineData(10, 2, 5)]
    [InlineData(10, 5, 5)]
    [InlineData(12, 4, 4)]
    [InlineData(12, 5, 5)]
    [InlineData(14, 4, 4)]
    [InlineData(15, 4, 4)]
    public void GreedyTightenPlan_PolicyIsValidAndDepthMatchesMaxStep(int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k);
        int maxStep = builder.BuildGreedyTightenPlan().MaxStep;

        // Throws if the committed policy is not a valid terminating strategy.
        int validatedDepth = builder.ValidateGreedyTightenPolicyDepthForTesting();

        Assert.Equal(maxStep, validatedDepth);
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

    // Regression guard for test-only round cap control: when configured, the driver should stop at
    // the exact cap and still produce a valid feasible-upper-bound strategy.
    [Fact]
    public void GreedyTightenPlan_RespectsConfiguredRoundCap()
    {
        const int n = 8;
        const int m = 2;
        const int k = 4;

        var builder = new StrategyBuilder(n, m, k)
        {
            GreedyTightenMaxRoundsForTesting = 1,
        };

        StrategyPlan plan = builder.BuildGreedyTightenPlan();

        Assert.True(plan.IsFeasibleUpperBound);
        Assert.Equal(1, builder.GreedyTightenRounds);
        Assert.Single(builder.GreedyTightenRoundTrace);
    }

    // The production default runs a single critical-path round (measurement showed extra rounds rarely
    // change U' but roughly double the cost). A case that tightens across several commits must still
    // stop after one round when no cap is configured.
    [Fact]
    public void GreedyTightenPlan_DefaultsToSingleRound()
    {
        var builder = new StrategyBuilder(10, 2, 5);

        StrategyPlan plan = builder.BuildGreedyTightenPlan();

        Assert.True(plan.IsFeasibleUpperBound);
        Assert.Equal(1, builder.GreedyTightenRounds);
        Assert.Single(builder.GreedyTightenRoundTrace);
    }

    // The shared memo optimization must preserve internally consistent accounting across total
    // counters and per-round diagnostics.
    [Fact]
    public void GreedyTightenPlan_DiagnosticsRemainConsistentWithSharedMemo()
    {
        const int n = 9;
        const int m = 2;
        const int k = 4;

        var builder = new StrategyBuilder(n, m, k);
        _ = builder.BuildGreedyTightenPlan();

        Assert.NotEmpty(builder.GreedyTightenRoundTrace);
        Assert.Equal(builder.GreedyTightenRounds, builder.GreedyTightenRoundTrace.Count);
        Assert.True(builder.GreedyTightenHeightCalls >= builder.GreedyTightenHeightMemoHits);

        int totalRoundHeightCalls = 0;
        int totalRoundMemoHits = 0;
        int totalRoundHeightUnderGroupCalls = 0;
        int totalRoundCommits = 0;
        foreach (StrategyBuilder.GreedyTightenRoundDiagnostics round in builder.GreedyTightenRoundTrace)
        {
            totalRoundHeightCalls += round.HeightCalls;
            totalRoundMemoHits += round.HeightMemoHits;
            totalRoundHeightUnderGroupCalls += round.HeightUnderGroupCalls;
            totalRoundCommits += round.Commits;
            Assert.True(round.HeightCalls >= round.HeightMemoHits);
        }

        Assert.Equal(builder.GreedyTightenHeightCalls, totalRoundHeightCalls);
        Assert.Equal(builder.GreedyTightenHeightMemoHits, totalRoundMemoHits);
        Assert.Equal(builder.GreedyTightenHeightUnderGroupCalls, totalRoundHeightUnderGroupCalls);
        Assert.Equal(builder.GreedyTightenCommits, totalRoundCommits);
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
