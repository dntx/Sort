using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;

// Guards the greedy-mode proof-tighten pipeline (RunGreedyPipeline), whose step ceiling is the
// constructive feasible upper bound U (ConstructiveRootUpperBound). The step phase has its own
// coverage in GreedyFeasibleStageTests; this fixes the previously-untested edge path so the budget source
// can never silently over-constrain the compact pass (returning an unsolvable sentinel) or emit a
// plan that violates the U/opt bounds.
public class GreedyPipelineTests
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
    public void GreedyPipeline_IsValidStrategy(int n, int m, int k)
    {
        StrategyPlan plan = new StrategyBuilder(n, m, k).RunGreedyPipeline();

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
    public void GreedyPipeline_StepNeverExceedsFeasibleUpperBound(int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k);
        int stepU = builder.BuildGreedyFeasibleStage().MaxStep;
        int edgeStep = builder.RunGreedyPipeline().MaxStep;

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
    public void GreedyPipeline_StepNeverBelowOptimum(int n, int m, int k)
    {
        int optimum = new StrategyBuilder(n, m, k).BuildStepProofStage().MaxStep;
        int edgeStep = new StrategyBuilder(n, m, k).RunGreedyPipeline().MaxStep;

        Assert.True(edgeStep >= optimum,
            $"feasible compact step {edgeStep} was below the true optimum {optimum}");
    }

    // Proof-tighten now auto-expands capped probes on the SAME budget (starting from
    // CompactGreedyCandidateCap, then x4 until complete), so a cap-truncated inconclusive infeasibility
    // should converge to a genuine proof when full enumeration is tractable. 12,4,4 exercises this.
    [Fact]
    public void GreedyPipeline_DefaultCap_AutoExpandsToProvenInfeasible()
    {
        StageOutcome terminal = TerminalOutcome(new StrategyBuilder(12, 4, 4), out StrategyPlan plan);
        Assert.Equal(StageOutcome.ProvenInfeasible, terminal);
        Assert.True(
            plan.SearchStatistics.RootProvenLowerBound == plan.MaxStep,
            "auto-expanded capped probes should close the squeeze when infeasibility is proven");
    }

    [Fact]
    public void GreedyPipeline_ExplicitCompleteEnumeration_IsProvenOptimal()
    {
        var builder = new StrategyBuilder(12, 4, 4) { CompactGreedyCandidateCap = 2_000_000 };
        StageOutcome terminal = TerminalOutcome(builder, out StrategyPlan plan);
        Assert.Equal(StageOutcome.ProvenInfeasible, terminal);
        Assert.Equal(plan.MaxStep, plan.SearchStatistics.RootProvenLowerBound);
    }

    // Locks the new per-probe auto-expansion behavior: even with a tiny starting cap, a single
    // proof-tighten probe at U-1 should internally widen caps on the same budget until it reaches a
    // conclusive answer (Tightened or ProvenInfeasible), rather than stopping at Incomplete.
    [Fact]
    public void ProofTightenProbe_TinyStartingCap_AutoExpandsToConclusiveOutcome()
    {
        var builder = new StrategyBuilder(12, 4, 4) { CompactGreedyCandidateCap = 1 };
        int budget = builder.BuildGreedyFeasibleStage().MaxStep - 1;

        StageResult stage = builder.BuildProofTightenStage(budget);

        Assert.Equal($"proof-tighten\u2264{budget}", stage.Name);
        Assert.NotEqual(StageOutcome.Incomplete, stage.Outcome);
        if (stage.Outcome == StageOutcome.Tightened)
        {
            Assert.True(stage.HasPlan);
            Assert.True(stage.Plan!.MaxStep <= budget,
                $"tightened probe must realize a step within budget {budget}, got {stage.Plan.MaxStep}");
        }
        else
        {
            Assert.Equal(StageOutcome.ProvenInfeasible, stage.Outcome);
            Assert.False(stage.HasPlan);
        }
    }

    // ProbeAndClassify temporarily overrides CompactGreedyCandidateCap while retrying higher caps.
    // This locks the restoration contract so callers' configured cap survives each probe unchanged.
    [Fact]
    public void ProofTightenProbe_RestoresConfiguredCandidateCap_AfterAutoExpansion()
    {
        var builder = new StrategyBuilder(12, 4, 4) { CompactGreedyCandidateCap = 1 };
        int originalCap = builder.CompactGreedyCandidateCap;
        int budget = builder.BuildGreedyFeasibleStage().MaxStep - 1;

        _ = builder.BuildProofTightenStage(budget);

        Assert.Equal(originalCap, builder.CompactGreedyCandidateCap);
    }

    // Pins the user-facing stage-name contract emitted by RunGreedyPipeline: each downward
    // tightening ceiling is announced as "proof-tighten\u2264N" and the final edge pass as
    // "edge-compact@S" (S = the resulting plan's MaxStep). These labels are shared verbatim by the CLI
    // headers, GUI tree roots, and the progress panel, so renaming them is a real behavior change --
    // this test fails if the labels drift back to the old "feasible\u2264N" / "compact" wording. 12,4,4
    // has U > opt so its tightening probes run, exercising both the proof-tighten ceilings and the
    // terminal edge-compact stage.
    [Fact]
    public void GreedyPipeline_EmitsProofTightenAndEdgeCompactStageNames()
    {
        var startedStages = new List<string>();
        var solvedStages = new List<string>();

        StrategyPlan plan = new StrategyBuilder(12, 4, 4).RunGreedyPipeline(
            onStageCompleted: stage => { if (stage.HasPlan) solvedStages.Add(stage.Name); },
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

    // Strong-output contract (the interface tightening): every stage the driver ANNOUNCES via
    // onStageStart must be completed by exactly one onStageCompleted carrying a typed {Outcome, Plan}. This locks
    // the fix that removed the silent "guard-reject" break, which previously started a probe stage but
    // never reported its result. Same names in the same order => no dangling and no duplicated stage.
    [Theory]
    [InlineData(12, 4, 4)]
    [InlineData(9, 3, 3)]
    [InlineData(10, 3, 6)]
    public void ProofTighten_EveryStartedStageIsCompletedExactlyOnce(int n, int m, int k)
    {
        var started = new List<string>();
        var completed = new List<string>();

        new StrategyBuilder(n, m, k).RunGreedyPipeline(
            onStageCompleted: stage => completed.Add(stage.Name),
            onStageStart: name => started.Add(name));

        Assert.NotEmpty(started);
        Assert.Equal(started, completed);
    }

    // Regression guard for the tighter-budget keep/reuse fix in this change. Previously, on (20,4,6) a
    // looser-budget pattern overwrote the tighter-feasible one, so the materialized plan OVERSHOT the
    // proof-tighten<=14 ceiling (realized step 16) and the probe was classified Overshot. Keeping the
    // tightest-budget pattern makes materialization honor the ceiling: the probe now TIGHTENS to a
    // within-budget plan (historically step 14; newer heuristics may do better). Being a single
    // feasibility probe it still does not prove optimality. Only the <=14 probe is exercised: with
    // the overshoot gone the full pipeline keeps tightening toward the true optimum, a deliberately
    // separate (and much longer) computation.
    [Fact]
    public void ProofTighten_Budget14TightensInsteadOfOvershooting_20_4_6()
    {
        var builder = new StrategyBuilder(20, 4, 6);
        int budget = builder.BuildGreedyFeasibleStage().MaxStep - 1;

        StageResult probe = builder.BuildProofTightenStage(budget);

        Assert.Equal($"proof-tighten\u2264{budget}", probe.Name);
        Assert.Equal(StageOutcome.Tightened, probe.Outcome);
        Assert.NotNull(probe.Plan);
        Assert.True(probe.Plan!.MaxStep <= budget,
            $"tightened probe must realize a step within budget {budget}, got {probe.Plan.MaxStep}");
        Assert.True(probe.Plan.MaxStep <= 14,
            $"expected <=14 at the tightened probe, got {probe.Plan.MaxStep}");
        Assert.False(probe.ProvesOptimal);
    }

    // Single-probe API contract: a direct BuildProofTightenStage(U-1) call must report the same first
    // tightening-stage result as RunGreedyPipeline (which internally drives the same probe). The pipeline
    // is cancelled as soon as that first stage is observed: with the overshoot fixed the pipeline no
    // longer stops early at <=14 but keeps tightening toward the optimum, so running it to completion
    // would be a much longer computation irrelevant to this wiring check.
    [Fact]
    public void ProofTighten_SingleProbeMatchesPipelineFirstStage_20_4_6()
    {
        var probeBuilder = new StrategyBuilder(20, 4, 6);
        int budget = probeBuilder.BuildGreedyFeasibleStage().MaxStep - 1;

        StageResult probe = probeBuilder.BuildProofTightenStage(budget);

        Assert.Equal($"proof-tighten\u2264{budget}", probe.Name);
        Assert.Equal(StageOutcome.Tightened, probe.Outcome);
        Assert.NotNull(probe.Plan);

        // Capture only the pipeline's FIRST proof-tighten stage, then cancel to avoid the deeper rounds.
        using var cts = new CancellationTokenSource();
        StageResult? firstTighten = null;
        try
        {
            _ = new StrategyBuilder(20, 4, 6, cts.Token).RunGreedyPipeline(onStageCompleted: stage =>
            {
                if (firstTighten is null
                    && stage.Name.StartsWith("proof-tighten\u2264", StringComparison.Ordinal))
                {
                    firstTighten = stage;
                    cts.Cancel();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }

        Assert.NotNull(firstTighten);
        Assert.Equal(probe.Name, firstTighten!.Value.Name);
        Assert.Equal(probe.Outcome, firstTighten.Value.Outcome);
        Assert.NotNull(firstTighten.Value.Plan);
        Assert.Equal(probe.Plan!.MaxStep, firstTighten.Value.Plan!.MaxStep);
    }

    // Item-3 strong-output lock for Phase B: the final min-edge pass ALWAYS emits exactly one
    // Completed stage that carries the returned plan (previously the should-not-happen fall back
    // returned a plan without emitting any stage). Completed is terminal and is not a tightening.
    [Theory]
    [InlineData(12, 4, 4)]
    [InlineData(9, 3, 3)]
    [InlineData(10, 3, 6)]
    public void ProofTighten_FinalStageIsSingleCompletedCarryingPlan(int n, int m, int k)
    {
        var stages = new List<StageResult>();

        StrategyPlan plan = new StrategyBuilder(n, m, k).RunGreedyPipeline(onStageCompleted: stages.Add);

        var edgeStages = stages.Where(s => s.Outcome == StageOutcome.Completed).ToList();
        Assert.Single(edgeStages);                                                 // exactly one, never dangling
        Assert.Equal(StageOutcome.Completed, stages[^1].Outcome);  // and it is the last stage
        Assert.True(edgeStages[0].HasPlan);                                        // carries a plan
        Assert.False(edgeStages[0].IsTightened);                                   // not a step tightening
        Assert.Same(plan, edgeStages[0].Plan);                                     // it IS the returned plan
    }

    // End-to-end value lock for the GT pre-step integration: on this case GT tightens the feasible
    // bound first, so seeding proof-tighten with that tighter U must start probing at a stricter
    // ceiling than the baseline path, while keeping the final result no worse.
    [Fact]
    public void GreedyPipeline_WithTighterSeededUpperBound_StartsFromTighterBudget_AndFinalIsNotWorse_10_2_5()
    {
        var baselineBuilder = new StrategyBuilder(10, 2, 5);
        _ = baselineBuilder.BuildGreedyFeasibleStage();
        int baselineFirstBudget = FirstProofTightenBudget(baselineBuilder, out StrategyPlan baselinePlan);

        var gatedBuilder = new StrategyBuilder(10, 2, 5);
        StrategyPlan feasible = gatedBuilder.BuildGreedyFeasibleStage();
        StrategyPlan gt = gatedBuilder.BuildGreedyTightenPlan();
        Assert.True(gt.IsStrictRefinementOver(feasible),
            "expected GT pre-step to improve the feasible bound on (10,2,5)");
        gatedBuilder.OverrideGreedyPipelineUpperBound(gt.MaxStep);

        int gatedFirstBudget = FirstProofTightenBudget(gatedBuilder, out StrategyPlan gatedPlan);

        Assert.True(gatedFirstBudget < baselineFirstBudget,
            $"expected tighter first proof budget with GT pre-step: baseline {baselineFirstBudget}, gated {gatedFirstBudget}");
        Assert.True(gatedPlan.MaxStep <= baselinePlan.MaxStep,
            $"gated pipeline should not end worse than baseline: baseline {baselinePlan.MaxStep}, gated {gatedPlan.MaxStep}");
    }

    // Neutral lock: when root probe says "skip", not applying any GT pre-step must keep the same
    // proof-tighten start ceiling and final result as the baseline path.
    [Fact]
    public void GreedyPipeline_RootProbeSkip_PathMatchesBaseline_12_4_4()
    {
        var baselineBuilder = new StrategyBuilder(12, 4, 4);
        _ = baselineBuilder.BuildGreedyFeasibleStage();
        int baselineFirstBudget = FirstProofTightenBudget(baselineBuilder, out StrategyPlan baselinePlan);

        var gatedBuilder = new StrategyBuilder(12, 4, 4);
        _ = gatedBuilder.BuildGreedyFeasibleStage();
        Assert.False(gatedBuilder.ShouldRunGreedyTightenByRootProbe());
        int gatedFirstBudget = FirstProofTightenBudget(gatedBuilder, out StrategyPlan gatedPlan);

        Assert.Equal(baselineFirstBudget, gatedFirstBudget);
        Assert.Equal(baselinePlan.MaxStep, gatedPlan.MaxStep);
    }

    // Lightweight canary for the m=2 proof-tighten performance cliff: this shape used to complete
    // quickly in normal conditions but becomes much slower when the exact-feasibility prune path
    // regresses. Keep the budget short to avoid inflating the suite runtime.
    [Fact]
    public void ProofTighten_FirstProbeCompletesQuickly_14_2_4()
    {
        _ = TestTimeoutHelper.RunWithTimeout(
            "BuildProofTightenStage(14,2,4) first probe",
            TimeSpan.FromSeconds(10),
            cancellationToken =>
            {
                var builder = new StrategyBuilder(14, 2, 4, cancellationToken);
                int budget = builder.BuildGreedyFeasibleStage().MaxStep - 1;
                StageResult stage = builder.BuildProofTightenStage(budget);

                Assert.Equal($"proof-tighten\u2264{budget}", stage.Name);
                Assert.True(
                    stage.Outcome == StageOutcome.Tightened || stage.Outcome == StageOutcome.ProvenInfeasible,
                    $"expected a conclusive first probe outcome, got {stage.Outcome}");

                if (stage.Outcome == StageOutcome.Tightened)
                {
                    Assert.True(stage.HasPlan);
                    Assert.True(stage.Plan!.MaxStep <= budget,
                        $"tightened probe must realize a step within budget {budget}, got {stage.Plan.MaxStep}");
                }

                return 0;
            });
    }

    // Runs the greedy edge progression and returns the outcome of its final TIGHTENING terminal stage
    // (ProvenInfeasible / Incomplete); ignores the always-last Completed pass. Returns
    // Tightened if the progression never bottomed out.
    private static StageOutcome TerminalOutcome(StrategyBuilder builder, out StrategyPlan plan)
    {
        var terminal = StageOutcome.Tightened;
        plan = builder.RunGreedyPipeline(stage =>
        {
            if (!stage.IsTightened && !stage.IsCompleted)
                terminal = stage.Outcome;
        });
        return terminal;
    }

    private static int FirstProofTightenBudget(StrategyBuilder builder, out StrategyPlan plan)
    {
        string? firstProofName = null;
        plan = builder.RunGreedyPipeline(
            onStageStart: name =>
            {
                if (firstProofName is null && name.StartsWith("proof-tighten\u2264", StringComparison.Ordinal))
                    firstProofName = name;
            });

        Assert.NotNull(firstProofName);
        return int.Parse(firstProofName!["proof-tighten\u2264".Length..]);
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
