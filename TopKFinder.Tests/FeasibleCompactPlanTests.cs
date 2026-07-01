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
        // Tightening runs unbounded since #153 (it relies on interactive Ctrl+C / GUI Stop to end),
        // and it is not what this test validates, so disable it here to keep the large cases
        // (25,5,5 etc.) fast. The tightening loop itself stays covered by the (12,4,4) *Infeasibility
        // Fact tests below.
        StrategyPlan plan = new StrategyBuilder(n, m, k) { EnableFeasibleTightening = false }
            .BuildFeasibleCompactPlan();

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
        // Baseline (pre-tightening) step already witnesses step <= U; skip the unbounded tightening.
        var builder = new StrategyBuilder(n, m, k) { EnableFeasibleTightening = false };
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
        // Baseline (pre-tightening) step is >= optimum too; skip the unbounded tightening.
        int edgeStep = new StrategyBuilder(n, m, k) { EnableFeasibleTightening = false }
            .BuildFeasibleCompactPlan().MaxStep;

        Assert.True(edgeStep >= optimum,
            $"feasible compact step {edgeStep} was below the true optimum {optimum}");
    }

    // Soundness guard for the greedy compact tightening: when the candidate cap (CompactGreedyCandidateCap)
    // truncates a state's group enumeration, a probe that finds no feasible group has NOT proven the ceiling
    // infeasible -- an untried group might have fit. Such a terminal stage must be reported as Incomplete
    // (not NoSolution) and must leave the squeeze open (no proven-optimal claim). 12,4,4 exercises this: at
    // the default cap the compact<=4 probe truncates, so it is Incomplete; raising the cap enough to enumerate
    // completely flips the same probe to a genuine NoSolution proof that closes the squeeze.
    [Fact]
    public void FeasibleCompactPlan_CappedInfeasibility_IsIncomplete_NotProvenOptimal()
    {
        GreedyEdgeStageOutcome cappedTerminal = TerminalOutcome(new StrategyBuilder(12, 4, 4), out StrategyPlan cappedPlan);
        Assert.Equal(GreedyEdgeStageOutcome.Incomplete, cappedTerminal);
        Assert.True(
            cappedPlan.SearchStatistics.RootProvenLowerBound < cappedPlan.MaxStep,
            "a cap-truncated (incomplete) probe must not close the squeeze to a proven optimum");
    }

    [Fact]
    public void FeasibleCompactPlan_CompleteInfeasibility_IsProvenOptimal()
    {
        var builder = new StrategyBuilder(12, 4, 4) { CompactGreedyCandidateCap = 2_000_000 };
        GreedyEdgeStageOutcome terminal = TerminalOutcome(builder, out StrategyPlan plan);
        Assert.Equal(GreedyEdgeStageOutcome.NoSolution, terminal);
        Assert.Equal(plan.MaxStep, plan.SearchStatistics.RootProvenLowerBound);
    }

    // Runs the greedy edge progression and returns the outcome of its final solution-less stage (the terminal
    // NoSolution / Incomplete), or Solution if the progression never bottomed out.
    private static GreedyEdgeStageOutcome TerminalOutcome(StrategyBuilder builder, out StrategyPlan plan)
    {
        var terminal = GreedyEdgeStageOutcome.Solution;
        plan = builder.BuildFeasibleCompactPlan(stage =>
        {
            if (!stage.HasSolution)
                terminal = stage.Outcome;
        });
        return terminal;
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
