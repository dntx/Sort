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

    // Regression: on (20,19,19) the dualized case (20,19,1) creates a symmetric order family
    // with multiplicity 19!, which does not fit in Int32. The displayed equivalent-order count
    // must saturate instead of throwing OverflowException.
    [Fact]
    public void GreedyFeasibleStage_20_19_19_DoesNotOverflowEquivalentOrderCount()
    {
        StrategyPlan plan = new StrategyBuilder(20, 19, 19).BuildGreedyFeasibleStage();

        Assert.True(plan.IsFeasibleUpperBound);
        Assert.True(plan.MaxStep > 0, "feasible plan should take at least one comparison");
    }

    // Regression: large-m greedy lookahead previously overflowed the per-step outcome ceiling
    // (m!) and passed a negative capacity into HashSet, crashing on (25,24,1).
    [Fact]
    public void GreedyFeasibleStage_25_24_1_DoesNotThrowCapacityOutOfRange()
    {
        StrategyPlan plan = new StrategyBuilder(25, 24, 1).BuildGreedyFeasibleStage();

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
    // docs/optimal-max-steps.md for the optima and docs/core-algorithm.md sec 4.6 for the policy.
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

    // Regression for the greedy 1-ply scoring hot path optimization: the scorer now reuses each
    // outcome's already-computed NextSearchKey to hit the lower-bound cache directly. These cases
    // should exercise that path and produce at least one direct reuse hit in a normal feasible build.
    [Theory]
    [InlineData(9, 3, 3)]
    [InlineData(12, 4, 4)]
    [InlineData(16, 5, 5)]
    public void GreedyFeasibleStage_ReusesLowerBoundCacheKeyInScoring(int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k);

        _ = builder.BuildGreedyFeasibleStage();

        Assert.True(
            builder.GreedyScoreLowerBoundCacheReuseHits > 0,
            $"expected direct lower-bound cache key reuse hits in greedy scoring for ({n},{m},{k})");
    }

    // Regression guard for the m>=3 lookahead generalization and successor-width-first scoring:
    // unresolved-density candidates and tie-break behavior now run broadly on m>=3, and we lock in
    // the resulting tree-size gains with relaxed upper envelopes (not exact pins) to tolerate small
    // tie-order drift while preventing large backslides.
    [Theory]
    [InlineData(20, 3, 6, 23, 9000, 4000)]
    [InlineData(22, 3, 6, 28, 13000, 5000)]
    [InlineData(16, 5, 5, 6, 1200, 260)]
    [InlineData(12, 4, 4, 6, 200, 90)]
    public void GreedyFeasibleStage_MGe3GeneralizedLookahead_StaysWithinTreeSizeEnvelope(
        int n,
        int m,
        int k,
        int maxAllowedSteps,
        int maxAllowedEdges,
        int maxAllowedOutputStates)
    {
        StrategyPlan plan = new StrategyBuilder(n, m, k).BuildGreedyFeasibleStage();

        Assert.True(
            plan.MaxStep <= maxAllowedSteps,
            $"expected max steps <= {maxAllowedSteps} for ({n},{m},{k}), got {plan.MaxStep}");
        Assert.True(
            plan.TotalBranchEdges <= maxAllowedEdges,
            $"expected edges <= {maxAllowedEdges} for ({n},{m},{k}), got {plan.TotalBranchEdges}");
        Assert.True(
            plan.SearchStatistics.OutputStates <= maxAllowedOutputStates,
            $"expected output states <= {maxAllowedOutputStates} for ({n},{m},{k}), got {plan.SearchStatistics.OutputStates}");
    }

    // Regression guard for internal candidate-membership data-structure refactors: constructive
    // selection is deterministic for a fixed (n,m,k), so repeated builds must produce identical
    // strategy shape metrics.
    [Theory]
    [InlineData(20, 3, 6)]
    [InlineData(12, 4, 4)]
    [InlineData(16, 5, 5)]
    public void GreedyFeasibleStage_RepeatedBuildsRemainDeterministic(int n, int m, int k)
    {
        StrategyPlan first = new StrategyBuilder(n, m, k).BuildGreedyFeasibleStage();
        StrategyPlan second = new StrategyBuilder(n, m, k).BuildGreedyFeasibleStage();

        Assert.Equal(first.MaxStep, second.MaxStep);
        Assert.Equal(first.TotalBranchEdges, second.TotalBranchEdges);
        Assert.Equal(first.SearchStatistics.OutputStates, second.SearchStatistics.OutputStates);
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
