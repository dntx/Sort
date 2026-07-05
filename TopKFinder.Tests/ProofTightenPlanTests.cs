using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

// Guards the greedy-mode proof-tighten pipeline (BuildProofTightenPlan), whose step ceiling is the
// constructive feasible upper bound U (ConstructiveRootUpperBound). The step phase has its own
// coverage in GreedyFeasiblePlanTests; this fixes the previously-untested edge path so the budget source
// can never silently over-constrain the compact pass (returning an unsolvable sentinel) or emit a
// plan that violates the U/opt bounds.
public class ProofTightenPlanTests
{
    // The edge pass must always produce a valid, fully-grouped strategy under the constructive U
    // budget -- never throw "no group fits the budget" -- and stay a feasible plan.
    //
    // Tightening is left ON: exercising the full feasible-compact build (baseline + tightening) is the
    // point. Inputs are limited to shapes whose tightening completes quickly. Since #153 removed the
    // tightening time budget, large shapes like 16,5,5 / 25,5,5 run tightening to completion (many
    // seconds) while adding no grouping-validity coverage the small cases don't already give, so they
    // are intentionally omitted here.
    [Theory]
    [InlineData(10, 5, 5)]
    [InlineData(12, 4, 4)]
    [InlineData(12, 5, 5)]
    [InlineData(9, 3, 3)]
    public void ProofTightenPlan_IsValidStrategy(int n, int m, int k)
    {
        StrategyPlan plan = new StrategyBuilder(n, m, k).BuildProofTightenPlan();

        Assert.True(plan.IsFeasibleUpperBound);
        Assert.True(plan.MaxStep > 0, "feasible compact plan should take at least one comparison");
        AssertEveryDecisionHasGroup(plan.Root);
    }

    // The edge pass minimizes displayed edges under the budget and may pick up a smaller real step
    // for free, so its MaxStep must never exceed the step phase's feasible U. This mirrors the
    // production orchestrators (Program.cs / MainForm.cs), which reuse ONE builder for step then edge:
    // the step phase threads its materialized U as the edge budget, guaranteeing edge is no worse.
    // Tightening is left ON; 16,5,5 is omitted because its tightening does not complete quickly.
    [Theory]
    [InlineData(10, 5, 5)]
    [InlineData(12, 4, 4)]
    [InlineData(12, 5, 5)]
    public void ProofTightenPlan_StepNeverExceedsFeasibleUpperBound(int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k);
        int stepU = builder.BuildGreedyFeasiblePlan().MaxStep;
        int edgeStep = builder.BuildProofTightenPlan().MaxStep;

        Assert.True(edgeStep <= stepU,
            $"feasible compact step {edgeStep} exceeded the feasible upper bound {stepU}");
    }

    // The edge plan is still an achievable strategy, so its worst-case steps must never drop below
    // the true optimum on cases the exact search can solve. This is the key soundness guard on the
    // tightening: it drives the greedy step DOWN toward the optimum (e.g. 10,5,5: U=5 -> 3;
    // 12,4,4: 6 -> 5; 12,5,5: 5 -> 4) and must never undershoot it. All inputs tighten quickly.
    [Theory]
    [InlineData(10, 5, 5)]
    [InlineData(12, 5, 5)]
    [InlineData(9, 3, 3)]
    [InlineData(12, 4, 4)]
    public void FeasibleCompactPlan_StepNeverBelowOptimum(int n, int m, int k)
    {
        int optimum = new StrategyBuilder(n, m, k).BuildStepProofPlan().MaxStep;
        int edgeStep = new StrategyBuilder(n, m, k).BuildProofTightenPlan().MaxStep;

        Assert.True(edgeStep >= optimum,
            $"feasible compact step {edgeStep} was below the true optimum {optimum}");
    }

    // Soundness guard for the greedy compact tightening: when the candidate cap (CompactGreedyCandidateCap)
    // truncates a state's group enumeration, a probe that finds no feasible group has NOT proven the ceiling
    // infeasible -- an untried group might have fit. Such a terminal stage must be reported as Incomplete
    // (not NoSolution) and must leave the squeeze open (no proven-optimal claim). 12,4,4 exercises this: at
    // the default cap the feasible<=4 probe truncates, so it is Incomplete; raising the cap enough to enumerate
    // completely flips the same probe to a genuine NoSolution proof that closes the squeeze.
    [Fact]
    public void FeasibleCompactPlan_CappedInfeasibility_IsIncomplete_NotProvenOptimal()
    {
        ProofTightenStageOutcome cappedTerminal = TerminalOutcome(new StrategyBuilder(12, 4, 4), out StrategyPlan cappedPlan);
        Assert.Equal(ProofTightenStageOutcome.Incomplete, cappedTerminal);
        Assert.True(
            cappedPlan.SearchStatistics.RootProvenLowerBound < cappedPlan.MaxStep,
            "a cap-truncated (incomplete) probe must not close the squeeze to a proven optimum");
    }

    [Fact]
    public void FeasibleCompactPlan_CompleteInfeasibility_IsProvenOptimal()
    {
        var builder = new StrategyBuilder(12, 4, 4) { CompactGreedyCandidateCap = 2_000_000 };
        ProofTightenStageOutcome terminal = TerminalOutcome(builder, out StrategyPlan plan);
        Assert.Equal(ProofTightenStageOutcome.NoSolution, terminal);
        Assert.Equal(plan.MaxStep, plan.SearchStatistics.RootProvenLowerBound);
    }

    // Pins the user-facing stage-name contract emitted by BuildProofTightenPlan: each downward
    // tightening ceiling is announced as "proof-tighten\u2264N" and the final edge pass as
    // "edge-compact@S" (S = the resulting plan's MaxStep). These labels are shared verbatim by the CLI
    // headers, GUI tree roots, and the progress panel, so renaming them is a real behavior change --
    // this test fails if the labels drift back to the old "feasible\u2264N" / "compact" wording. 12,4,4
    // has U > opt so its tightening probes run, exercising both the proof-tighten ceilings and the
    // terminal edge-compact stage.
    [Fact]
    public void FeasibleCompactPlan_EmitsProofTightenAndEdgeCompactStageNames()
    {
        var startedStages = new List<string>();
        var solvedStages = new List<string>();

        StrategyPlan plan = new StrategyBuilder(12, 4, 4).BuildProofTightenPlan(
            onStage: stage => { if (stage.HasSolution) solvedStages.Add(stage.Name); },
            onStageStart: name => startedStages.Add(name));

        string edgeCompactName = $"edge-compact@{plan.MaxStep}";

        // The final edge-compaction pass is always announced and always carries the returned plan.
        Assert.Contains(edgeCompactName, startedStages);
        Assert.Contains(edgeCompactName, solvedStages);

        // At least one downward tightening ceiling ran, and every announced stage uses either the
        // "proof-tighten\u2264N" tightening label or the terminal "edge-compact@S" label.
        Assert.Contains(startedStages, name => name.StartsWith("proof-tighten\u2264", StringComparison.Ordinal));
        Assert.All(startedStages, name =>
            Assert.True(
                name.StartsWith("proof-tighten\u2264", StringComparison.Ordinal) || name == edgeCompactName,
                $"unexpected stage label '{name}'"));

        // The progression is ordered: every proof-tighten ceiling precedes the terminal edge-compact
        // pass, which is announced exactly once and always last.
        Assert.Equal(edgeCompactName, startedStages[^1]);
        Assert.Equal(1, startedStages.Count(name => name == edgeCompactName));
        int firstEdgeCompactIndex = startedStages.IndexOf(edgeCompactName);
        Assert.All(startedStages.Take(firstEdgeCompactIndex), name =>
            Assert.StartsWith("proof-tighten\u2264", name, StringComparison.Ordinal));

        // The tightening ceilings step strictly downward (U-1, then lower), matching the U-1, U-2, …
        // probe order the CLI/GUI progression surfaces.
        var tightenBudgets = startedStages
            .Where(name => name.StartsWith("proof-tighten\u2264", StringComparison.Ordinal))
            .Select(name => int.Parse(name.Substring("proof-tighten\u2264".Length)))
            .ToList();
        for (int i = 1; i < tightenBudgets.Count; i++)
            Assert.True(tightenBudgets[i] < tightenBudgets[i - 1],
                $"tightening ceilings must strictly descend, saw {tightenBudgets[i - 1]} then {tightenBudgets[i]}");
    }

    // Runs the greedy edge progression and returns the outcome of its final solution-less stage (the terminal
    // NoSolution / Incomplete), or Solution if the progression never bottomed out.
    private static ProofTightenStageOutcome TerminalOutcome(StrategyBuilder builder, out StrategyPlan plan)
    {
        var terminal = ProofTightenStageOutcome.Solution;
        plan = builder.BuildProofTightenPlan(stage =>
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
