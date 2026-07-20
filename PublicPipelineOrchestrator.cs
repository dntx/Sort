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
        var callbacks = new PipelineCallbacks(onStageCompleted, onStageStart);

        StrategyPlan stepPlan = PipelineStageProtocol.ExecuteCompletedPlanStage(
            StageNames.StepProof,
            () => ExecuteExactStepStage(builder),
            callbacks);

        StrategyPlan compactPlan = PipelineStageProtocol.ExecuteCompletedPlanStage(
            StageNames.FormatExactEdgeCompact(stepPlan.MaxStep),
            () => ExecuteExactCompactStage(builder),
            callbacks);
        return compactPlan;
    }

    public static StrategyPlan RunGreedyPipeline(
        StrategyBuilder builder,
        Action<StageResult>? onStageCompleted = null,
        Action<string>? onStageStart = null,
        bool emitPreparationStages = true,
        bool preparationAlreadyApplied = false)
    {
        var callbacks = new PipelineCallbacks(onStageCompleted, onStageStart);

        if (!preparationAlreadyApplied)
        {
            string feasibleStageName = StageNames.GreedyFeasible;
            if (emitPreparationStages)
                callbacks.Start(feasibleStageName);
            GreedyPreparationResult prep = PrepareGreedyUpperBound(builder);
            if (emitPreparationStages)
            {
                PipelineStageProtocol.EmitCompletedPlanStage(
                    feasibleStageName,
                    prep.BaseFeasiblePlan,
                    prep.GreedyFeasibleElapsed,
                    callbacks);
            }

            if (prep.GreedyTightenProbeRun && prep.GreedyTightenPlan is not null)
            {
                string tightenStageName = StageNames.GreedyTighten;
                if (emitPreparationStages)
                {
                    callbacks.Start(tightenStageName);
                    PipelineStageProtocol.EmitCompletedPlanStage(
                        tightenStageName,
                        prep.GreedyTightenPlan,
                        prep.GreedyTightenElapsed,
                        callbacks);
                }
            }
        }

        return builder.RunGreedyPipelineCore(onStageCompleted, onStageStart);
    }

    // Executes exact stage-1 inside the shared orchestrator via the shared search projection helper.
    private static (StrategyPlan StepPlan, TimeSpan Elapsed) ExecuteExactStepStage(StrategyBuilder builder)
    {
        var stopwatch = Stopwatch.StartNew();
        (SearchTree _, DisplayTree stepPlan) = builder.ProjectDisplayAndSearchTrees();
        stopwatch.Stop();
        return (stepPlan, stopwatch.Elapsed);
    }

    // Executes exact stage-2 inside the shared orchestrator.
    private static (StrategyPlan CompactPlan, TimeSpan Elapsed) ExecuteExactCompactStage(StrategyBuilder builder)
    {
        var stopwatch = Stopwatch.StartNew();
        StrategyPlan compactPlan = builder.ExecuteEdgeCompactStage();
        stopwatch.Stop();
        return (compactPlan, stopwatch.Elapsed);
    }

    // Shared greedy pre-stage orchestration used by public callers (CLI/UI): build a feasible upper
    // bound, optionally run one greedy-tighten round, and apply the improved bound override when
    // tightening wins. Search semantics are unchanged; this only centralizes pipeline routing.
    public static GreedyPreparationResult PrepareGreedyUpperBound(StrategyBuilder builder)
    {
        var feasibleStopwatch = System.Diagnostics.Stopwatch.StartNew();
        StrategyPlan baseFeasiblePlan = builder.ExecuteGreedyFeasibleStage();
        feasibleStopwatch.Stop();
        StrategyPlan effectiveFeasiblePlan = baseFeasiblePlan;

        bool gtProbeRun = builder.ShouldRunGreedyTightenByRootProbe();
        StrategyPlan? gtPlan = null;
        bool gtImproved = false;
        TimeSpan gtElapsed = TimeSpan.Zero;
        if (gtProbeRun)
        {
            var gtStopwatch = System.Diagnostics.Stopwatch.StartNew();
            gtPlan = builder.ExecuteGreedyTightenStage();
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
