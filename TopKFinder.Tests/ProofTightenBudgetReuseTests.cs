using System;
using System.Collections.Generic;
using Xunit;

// Guards the feasibility-only compact tightening cache behavior this change introduced:
//   (a) the per-state pattern cache keeps the group proven under the TIGHTEST budget, so a later
//       looser-budget visit can never overwrite a tighter-feasible choice; and
//   (b) a looser-budget visit REUSES the already-proven tighter feasible result instead of recomputing.
// These are internal invariants, so the tests pin them through the public proof-tighten stage API:
// a missed metadata clear, a looser-budget overwrite, or a reuse of a non-materializable entry would
// surface here as an over-budget / invalid / non-deterministic probe.
//
// Every case has feasible upper bound U > optimum, so ExecuteGreedyFeasibleStage yields U and the
// tightening ceilings U-1, U-2, … actually probe multiple budgets per state (exercising the reuse
// and keep-tightest paths). All shapes are small enough to run well within the greedy candidate cap.
public class ProofTightenBudgetReuseTests
{
    public static IEnumerable<object[]> TighteningCases() => new[]
    {
        new object[] { 5, 2, 2 },
        new object[] { 6, 2, 3 },
        new object[] { 8, 3, 2 },
        new object[] { 8, 3, 4 },
        new object[] { 10, 4, 4 },
        new object[] { 10, 4, 6 },
    };

    // A Tightened probe must carry a fully materialized, valid strategy: every reachable decision
    // state's kept/reused pattern must exist in the cache (so BuildState can render it) and every
    // branch must carry a non-empty group. If a looser-budget pattern had overwritten a tighter one,
    // or a non-materializable entry had been reused, materialization would produce a null/empty group.
    [Theory]
    [MemberData(nameof(TighteningCases))]
    public void ProofTightenProbe_TightenedPlanIsValidAndWithinBudget(int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k);
        int budget = builder.ExecuteGreedyFeasibleStage().MaxStep - 1;

        StageResult probe = builder.ExecuteProofTightenStage(budget);

        // Only a Tightened probe promises a within-budget realized plan; ProvenInfeasible /
        // Incomplete are the other valid terminal outcomes for a given ceiling and are covered elsewhere.
        // (An over-budget materialized plan is no longer an outcome -- it throws in ProbeAndClassify.)
        if (probe.Outcome != StageOutcome.Tightened)
            return;

        Assert.NotNull(probe.Plan);
        Assert.True(probe.Plan!.MaxStep <= budget,
            $"({n},{m},{k}): Tightened probe realized step {probe.Plan.MaxStep} above budget {budget}");
        Assert.True(probe.Plan.IsFeasibleUpperBound);
        AssertEveryDecisionHasGroup(probe.Plan.Root);
    }

    // Feasibility is monotone in the budget: a strategy proven feasible at budget b is also feasible at
    // b+1. The reuse optimization relies on exactly this (it hands a tighter feasible result back for a
    // looser request), so guard it end to end -- whenever a probe at b materializes a plan, the probe at
    // the looser b+1 must materialize one too.
    [Theory]
    [MemberData(nameof(TighteningCases))]
    public void ProofTightenProbe_FeasibilityIsMonotoneInBudget(int n, int m, int k)
    {
        int u = new StrategyBuilder(n, m, k).ExecuteGreedyFeasibleStage().MaxStep;

        for (int b = 1; b < u; b++)
        {
            bool feasibleAtTight = new StrategyBuilder(n, m, k).ExecuteProofTightenStage(b).HasPlan;
            if (!feasibleAtTight)
                continue;

            bool feasibleAtLooser = new StrategyBuilder(n, m, k).ExecuteProofTightenStage(b + 1).HasPlan;
            Assert.True(feasibleAtLooser,
                $"({n},{m},{k}): feasible at budget {b} but no plan at looser budget {b + 1}");
        }
    }

    // Determinism / no stale metadata: repeating the same probe on a fresh builder must yield the same
    // outcome and realized step. This guards that the new tightest-budget metadata is cleared in lockstep
    // with the pattern cache (ResetCompactState) -- a missed clear would let a second run diverge.
    [Theory]
    [MemberData(nameof(TighteningCases))]
    public void ProofTightenProbe_IsDeterministicAcrossBuilders(int n, int m, int k)
    {
        int budget = new StrategyBuilder(n, m, k).ExecuteGreedyFeasibleStage().MaxStep - 1;

        StageResult first = new StrategyBuilder(n, m, k).ExecuteProofTightenStage(budget);
        StageResult second = new StrategyBuilder(n, m, k).ExecuteProofTightenStage(budget);

        Assert.Equal(first.Outcome, second.Outcome);
        Assert.Equal(first.Plan?.MaxStep, second.Plan?.MaxStep);
    }

    // No-overshoot invariant (the point of the tightest-budget-keep fix): at EVERY budget b in
    // [1, U-1], a probe that materializes a plan must honor the ceiling (MaxStep <= b). Since PR #223,
    // an over-budget materialized plan is an internal invariant violation that throws in
    // ProbeAndClassify, so this sweep would fail loudly (via the exception) if any probe overshot.
    // Kept to the shared small shape set so the all-budgets sweep stays well within the per-PR budget.
    [Theory]
    [MemberData(nameof(TighteningCases))]
    public void ProofTightenProbe_NeverOvershootsAcrossBudgets(int n, int m, int k)
    {
        int u = new StrategyBuilder(n, m, k).ExecuteGreedyFeasibleStage().MaxStep;

        for (int b = 1; b < u; b++)
        {
            StageResult probe = new StrategyBuilder(n, m, k).ExecuteProofTightenStage(b);
            if (!probe.HasPlan)
                continue;

            Assert.Equal(StageOutcome.Tightened, probe.Outcome);
            Assert.True(probe.Plan!.MaxStep <= b,
                $"({n},{m},{k}): probe at budget {b} realized step {probe.Plan.MaxStep} above the ceiling");
        }
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
