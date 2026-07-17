using System;
using System.Diagnostics;

readonly record struct GreedyPreparationResult(
    StrategyPlan BaseFeasiblePlan,
    StrategyPlan EffectiveFeasiblePlan,
    StrategyPlan? GreedyTightenPlan,
    bool GreedyTightenProbeRun,
    bool GreedyTightenImproved,
    TimeSpan GreedyFeasibleElapsed,
    TimeSpan GreedyTightenElapsed);

static class PublicPipelineOrchestrator
{
    public static StrategyPlan RunExactPipeline(
        StrategyBuilder builder,
        Action<StageResult>? onStageCompleted = null,
        Action<string>? onStageStart = null)
    {
        const string stepStageName = "step-proof";
        onStageStart?.Invoke(stepStageName);
        (StrategyPlan stepPlan, TimeSpan stepElapsed) = ExecuteExactStepStage(builder);
        onStageCompleted?.Invoke(new StageResult(stepStageName, stepPlan, stepElapsed, StageOutcome.Completed));

        string compactStageName = StrategyBuilder.FormatExactEdgeCompactStageName(stepPlan.MaxStep);
        onStageStart?.Invoke(compactStageName);
        (StrategyPlan compactPlan, TimeSpan compactElapsed) = ExecuteExactCompactStage(builder);
        onStageCompleted?.Invoke(new StageResult(compactStageName, compactPlan, compactElapsed, StageOutcome.Completed));
        return compactPlan;
    }

    public static StrategyPlan RunGreedyPipeline(
        StrategyBuilder builder,
        Action<StageResult>? onStageCompleted = null,
        Action<string>? onStageStart = null,
        bool emitPreparationStages = true,
        bool preparationAlreadyApplied = false)
    {
        if (!preparationAlreadyApplied)
        {
            const string feasibleStageName = "greedy-feasible";
            if (emitPreparationStages)
                onStageStart?.Invoke(feasibleStageName);
            GreedyPreparationResult prep = PrepareGreedyUpperBound(builder);
            if (emitPreparationStages)
            {
                onStageCompleted?.Invoke(new StageResult(
                    feasibleStageName,
                    prep.BaseFeasiblePlan,
                    prep.GreedyFeasibleElapsed,
                    StageOutcome.Completed));
            }

            if (prep.GreedyTightenProbeRun && prep.GreedyTightenPlan is not null)
            {
                const string tightenStageName = "greedy-tighten";
                if (emitPreparationStages)
                {
                    onStageStart?.Invoke(tightenStageName);
                    onStageCompleted?.Invoke(new StageResult(
                        tightenStageName,
                        prep.GreedyTightenPlan,
                        prep.GreedyTightenElapsed,
                        StageOutcome.Completed));
                }
            }
        }

        return builder.RunGreedyPipelineCore(onStageCompleted, onStageStart);
    }

    // Executes exact stage-1 inside the shared orchestrator. The layered exact entrypoint is kept so
    // the canonical search->display flow remains explicit.
    private static (StrategyPlan StepPlan, TimeSpan Elapsed) ExecuteExactStepStage(StrategyBuilder builder)
    {
        var stopwatch = Stopwatch.StartNew();
        (SearchTree _, DisplayTree stepPlan) = builder.BuildDisplayTreeAndExpandedSearch();
        stopwatch.Stop();
        return (stepPlan, stopwatch.Elapsed);
    }

    // Executes exact stage-2 inside the shared orchestrator.
    private static (StrategyPlan CompactPlan, TimeSpan Elapsed) ExecuteExactCompactStage(StrategyBuilder builder)
    {
        var stopwatch = Stopwatch.StartNew();
        StrategyPlan compactPlan = builder.BuildEdgeCompactStage();
        stopwatch.Stop();
        return (compactPlan, stopwatch.Elapsed);
    }

    // Shared greedy pre-stage orchestration used by public callers (CLI/UI): build a feasible upper
    // bound, optionally run one greedy-tighten round, and apply the improved bound override when
    // tightening wins. Search semantics are unchanged; this only centralizes pipeline routing.
    public static GreedyPreparationResult PrepareGreedyUpperBound(StrategyBuilder builder)
    {
        var feasibleStopwatch = System.Diagnostics.Stopwatch.StartNew();
        StrategyPlan baseFeasiblePlan = builder.BuildGreedyFeasibleStage();
        feasibleStopwatch.Stop();
        StrategyPlan effectiveFeasiblePlan = baseFeasiblePlan;

        bool gtProbeRun = builder.ShouldRunGreedyTightenByRootProbe();
        StrategyPlan? gtPlan = null;
        bool gtImproved = false;
        TimeSpan gtElapsed = TimeSpan.Zero;
        if (gtProbeRun)
        {
            var gtStopwatch = System.Diagnostics.Stopwatch.StartNew();
            gtPlan = builder.BuildGreedyTightenPlan();
            gtStopwatch.Stop();
            gtElapsed = gtStopwatch.Elapsed;
            gtImproved = gtPlan.IsStrictRefinementOver(baseFeasiblePlan);
            if (gtImproved)
            {
                effectiveFeasiblePlan = gtPlan;
                builder.OverrideGreedyPipelineUpperBound(effectiveFeasiblePlan.MaxStep);
            }
        }

        return new GreedyPreparationResult(
            baseFeasiblePlan,
            effectiveFeasiblePlan,
            gtPlan,
            gtProbeRun,
            gtImproved,
            feasibleStopwatch.Elapsed,
            gtElapsed);
    }
}
