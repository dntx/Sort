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
            RunGreedyPreparation(builder, onStageCompleted, onStageStart, emitPreparationStages);

        return builder.RunGreedyPipelineCore(onStageCompleted, onStageStart);
    }

    public static GreedyPreparationResult RunGreedyPreparation(
        StrategyBuilder builder,
        Action<StageResult>? onStageCompleted = null,
        Action<string>? onStageStart = null,
        bool emitStages = true)
    {
        var callbacks = new PipelineCallbacks(onStageCompleted, onStageStart);
        GreedyPreparationResult prep = PrepareGreedyUpperBound(builder);

        if (!emitStages)
            return prep;

        PipelineStageProtocol.EmitCompletedPlanStage(
            StageNames.GreedyFeasible,
            prep.BaseFeasiblePlan,
            prep.GreedyFeasibleElapsed,
            callbacks,
            emitStart: true);

        if (prep.GreedyTightenProbeRun && prep.GreedyTightenPlan is not null)
        {
            PipelineStageProtocol.EmitCompletedPlanStage(
                StageNames.GreedyTighten,
                prep.GreedyTightenPlan,
                prep.GreedyTightenElapsed,
                callbacks,
                emitStart: true);
        }

        return prep;
    }

    // Executes exact stage-1 inside the shared orchestrator via the shared search projection helper.
    private static (StrategyPlan StepPlan, TimeSpan Elapsed) ExecuteExactStepStage(StrategyBuilder builder)
    {
        var stopwatch = Stopwatch.StartNew();
        (SearchStrategy _, StrategyPlan stepPlan) = builder.ProjectDisplayAndSearchTrees();
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
