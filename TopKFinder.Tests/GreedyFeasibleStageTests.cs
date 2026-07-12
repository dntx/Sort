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

    // Pins the exact raw feasible U (before compact edge-tightening) for the current greedy-feasible
    // default policy (fixed constructive base picker). These values are regression locks for stage-1
    // behavior after removing the external toggle and making fixed-base selection the default.
    [Theory]
    [InlineData(6, 3, 3, 5)]
    [InlineData(6, 3, 2, 4)]
    [InlineData(9, 5, 4, 4)]
    [InlineData(7, 4, 4, 3)]
    [InlineData(14, 4, 5, 8)]
    [InlineData(12, 6, 6, 4)]
    public void GreedyFeasibleStage_FixedBasePinsRawUpperBound(int n, int m, int k, int expectedRawU)
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

    // Under the default fixed-base feasible policy, stage-1 no longer runs lookahead scoring, so the
    // scorer's lower-bound cache-key reuse counter must remain zero.
    [Theory]
    [InlineData(9, 3, 3)]
    [InlineData(12, 4, 4)]
    [InlineData(16, 5, 5)]
    public void GreedyFeasibleStage_FixedBaseDoesNotUseLookaheadScoringCache(int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k);

        _ = builder.BuildGreedyFeasibleStage();

        Assert.Equal(
            0,
            builder.GreedyScoreLowerBoundCacheReuseHits);
    }

    // Regression guard for fixed-base greedy-feasible tree size envelopes. These are intentionally
    // loose upper bounds (not exact pins) to tolerate minor deterministic drift while preventing
    // major structural backslides.
    [Theory]
    [InlineData(20, 3, 6, 23, 9000, 4000)]
    [InlineData(22, 3, 6, 28, 13000, 5000)]
    [InlineData(16, 5, 5, 7, 1200, 260)]
    [InlineData(12, 4, 4, 7, 200, 90)]
    public void GreedyFeasibleStage_FixedBase_StaysWithinTreeSizeEnvelope(
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

    // Test that progress reporting is working correctly during feasible stage materialization.
    // Progress should be reported incrementally and monotonically increase from 0 to completion.
    [Theory]
    [InlineData(25, 5, 10)]  // Historically slow greedy case where progress display matters most
    [InlineData(16, 5, 5)]
    public void GreedyFeasibleStage_ReportsProgressIncrementally(int n, int m, int k)
    {
        var progressReports = new List<double>();
        var builder = new StrategyBuilder(
            n, m, k,
            cancellationToken: default,
            progressCallback: snapshot => progressReports.Add(snapshot.EstimatedProgress01),
            reportCombinedRunProgress: true);

        StrategyPlan plan = builder.BuildGreedyFeasibleStage();

        // Must report progress at least a few times during build
        Assert.True(progressReports.Count > 0, "no progress reports were generated");

        // All reports must be in [0, 1]
        foreach (double progress in progressReports)
        {
            Assert.True(progress >= 0.0 && progress <= 1.0,
                $"progress {progress} is outside valid range [0, 1]");
        }

        // Progress must be monotonically non-decreasing (no regression)
        for (int i = 1; i < progressReports.Count; i++)
        {
            Assert.True(progressReports[i] >= progressReports[i - 1],
                $"progress regressed from {progressReports[i - 1]} to {progressReports[i]} at step {i}");
        }

        // Final report should be at 0.1 (10% in the combined-run split, which is the feasible stage's band)
        // This verifies that the feasible stage correctly maps its local 0-100% to the global 0-10% band.
        Assert.True(progressReports[^1] >= 0.099,
            $"final progress report {progressReports[^1]} was below 0.099 (expected ~0.1 for feasible stage)");

        // Build must complete successfully
        Assert.True(plan.MaxStep > 0);
        Assert.True(plan.IsFeasibleUpperBound);
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
