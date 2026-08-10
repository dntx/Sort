using System;
using System.Diagnostics;

namespace TopKFinder;

readonly record struct GreedyPreparationResult(
    StrategyPlan? BaseFeasiblePlan,
    StrategyPlan? EffectiveFeasiblePlan,
    StrategyPlan? GreedyTightenPlan,
    SolvedStrategy BaseFeasibleSolution,
    SolvedStrategy EffectiveFeasibleSolution,
    SolvedStrategy? GreedyTightenSolution,
    bool GreedyTightenProbeRun,
    bool GreedyTightenImproved,
    TimeSpan GreedyFeasibleElapsed,
    TimeSpan GreedyTightenElapsed,
    StageTimings GreedyFeasibleTimings,
    StageTimings GreedyTightenTimings);

static class PublicPipelineOrchestrator
{
    internal static void RunExactPipelineDeferred(
        StrategyBuilder builder,
        Action<StageResult> onStageCompleted,
        Action<string>? onStageStart = null)
    {
        var callbacks = new PipelineCallbacks(onStageCompleted, onStageStart);
        int sequence = 0;

        callbacks.Start(StageNames.StepProof);
        ExactStepProofStageArtifacts stepArtifacts = builder.BuildExactStepProofStageArtifacts(materialize: false);
        var stepStage = new StageResult(
            StageNames.StepProof,
            materializedPlan: null,
            stepArtifacts.Timings.Total,
            StageOutcome.Completed,
            stepArtifacts.Solution,
            stepArtifacts.Timings,
            sequence: sequence++,
            improvesPreviousStage: false);
        PipelineStageProtocol.EmitStage(
            stepStage,
            callbacks);

        string compactStageName = StageNames.FormatExactEdgeCompact(
            stepArtifacts.Solution.Score.WorstCaseSteps);
        callbacks.Start(compactStageName);
        CompactStageArtifacts compactArtifacts = builder.ExecuteEdgeCompactStageWithSolution(materialize: false);
        PipelineStageProtocol.EmitStage(
            new StageResult(
                compactStageName,
                materializedPlan: null,
                compactArtifacts.Timings.Total,
                StageOutcome.Completed,
                compactArtifacts.Solution,
                compactArtifacts.Timings,
                sequence: sequence++,
                improvesPreviousStage: true),
            callbacks);
    }

    public static StrategyPlan RunExactPipeline(
        StrategyBuilder builder,
        Action<StageResult>? onStageCompleted = null,
        Action<string>? onStageStart = null)
    {
        var callbacks = new PipelineCallbacks(onStageCompleted, onStageStart);
        int sequence = 0;

        callbacks.Start(StageNames.StepProof);
        ExactStepProofStageArtifacts stepArtifacts = builder.ExecuteStepProofStageWithSolution();
        var stepStage = new StageResult(
            StageNames.StepProof,
            stepArtifacts.Plan,
            stepArtifacts.Timings.Total,
            StageOutcome.Completed,
            stepArtifacts.Solution,
            stepArtifacts.Timings,
            sequence: sequence++,
            improvesPreviousStage: false);
        PipelineStageProtocol.EmitStage(stepStage, callbacks);
        StrategyPlan stepPlan = stepArtifacts.Plan!;

        string compactStageName = StageNames.FormatExactEdgeCompact(
            stepArtifacts.Solution.Score.WorstCaseSteps);
        callbacks.Start(compactStageName);
        CompactStageArtifacts compactArtifacts = builder.ExecuteEdgeCompactStageWithSolution();
        var compactStage = new StageResult(
            compactStageName,
            compactArtifacts.Plan,
            compactArtifacts.Plan!.Elapsed,
            StageOutcome.Completed,
            compactArtifacts.Solution,
            compactArtifacts.Timings,
            sequence: sequence++,
            improvesPreviousStage: true);
        PipelineStageProtocol.EmitStage(compactStage, callbacks);
        return compactArtifacts.Plan!;
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
            RunGreedyPreparation(
                builder,
                onStageCompleted,
                onStageStart,
                emitStages: emitPreparationStages);

        return builder.RunGreedyPipelineCore(onStageCompleted, onStageStart);
    }

    internal static void RunGreedyPipelineDeferred(
        StrategyBuilder builder,
        Action<StageResult> onStageCompleted,
        Action<string>? onStageStart = null,
        bool preparationAlreadyApplied = false)
    {
        if (!preparationAlreadyApplied)
            RunGreedyPreparation(builder, emitStages: false, materialize: false);

        builder.RunGreedyPipelineCore(
            onStageCompleted,
            onStageStart,
            materializeStages: false);
    }

    public static GreedyPreparationResult RunGreedyPreparation(
        StrategyBuilder builder,
        Action<StageResult>? onStageCompleted = null,
        Action<string>? onStageStart = null,
        bool emitStages = true,
        bool materialize = true)
    {
        var callbacks = new PipelineCallbacks(onStageCompleted, onStageStart);
        return PrepareGreedyUpperBound(
            builder,
            materialize,
            callbacks.Start,
            emitStages ? callbacks.Complete : null);
    }

    // Shared greedy pre-stage orchestration used by public callers (CLI/UI): build a feasible upper
    // bound, optionally run one greedy-tighten round, and apply the improved bound override when
    // tightening wins. Search semantics are unchanged; this only centralizes pipeline routing.
    public static GreedyPreparationResult PrepareGreedyUpperBound(
        StrategyBuilder builder,
        bool materialize = true,
        Action<string>? onStageStart = null,
        Action<StageResult>? onStageCompleted = null)
    {
        int sequence = 0;
        onStageStart?.Invoke(StageNames.GreedyFeasible);
        GreedyFeasibleStageArtifacts feasibleArtifacts = builder.ExecuteGreedyFeasibleStageWithSolution(materialize);
        StrategyPlan? baseFeasiblePlan = materialize ? feasibleArtifacts.Plan : null;
        SolvedStrategy baseFeasibleSolution = feasibleArtifacts.Solution;
        StrategyPlan? effectiveFeasiblePlan = baseFeasiblePlan;
        SolvedStrategy effectiveFeasibleSolution = baseFeasibleSolution;

        var greedyFeasibleStage = new StageResult(
            StageNames.GreedyFeasible,
            baseFeasiblePlan,
            feasibleArtifacts.Timings.Total,
            StageOutcome.Completed,
            baseFeasibleSolution,
            feasibleArtifacts.Timings,
            sequence: sequence++,
            improvesPreviousStage: false);
        onStageCompleted?.Invoke(greedyFeasibleStage);

        onStageStart?.Invoke(StageNames.GreedyTighten);
        var gtProbeStopwatch = Stopwatch.StartNew();
        bool gtProbeRun = builder.ShouldRunGreedyTightenByRootProbe();
        gtProbeStopwatch.Stop();
        TimeSpan gtProbeElapsed = gtProbeStopwatch.Elapsed;
        StrategyPlan? gtPlan = null;
        SolvedStrategy? gtSolution = null;
        bool gtImproved = false;
        TimeSpan gtElapsed = gtProbeElapsed;
        StageTimings gtTimings = StageTimings.Legacy(gtProbeElapsed);
        if (gtProbeRun)
        {
            GreedyTightenStageArtifacts gtArtifacts = builder.ExecuteGreedyTightenStageWithSolution(materialize);
            gtPlan = materialize ? gtArtifacts.Plan : null;
            gtSolution = gtArtifacts.Solution;
            gtTimings = new StageTimings(
                gtProbeElapsed + gtArtifacts.Timings.Solve,
                gtArtifacts.Timings.Freeze,
                gtArtifacts.Timings.Materialize);
            gtElapsed = gtTimings.Total;
            gtImproved = gtSolution.Score.IsStrictRefinementOver(baseFeasibleSolution.Score);
            if (gtImproved)
            {
                effectiveFeasiblePlan = gtPlan;
                effectiveFeasibleSolution = gtSolution;
                builder.OverrideGreedyPipelineUpperBound(effectiveFeasibleSolution.Score.WorstCaseSteps);
            }
        }

        var greedyTightenStage = new StageResult(
            StageNames.GreedyTighten,
            gtPlan,
            gtElapsed,
            gtSolution is null ? StageOutcome.Skipped : StageOutcome.Completed,
            gtSolution,
            gtTimings,
            gtPlan is null ? StagePresentationMode.SearchOnlySummary : StagePresentationMode.Auto,
            sequence: sequence++,
            improvesPreviousStage: gtImproved);
        onStageCompleted?.Invoke(greedyTightenStage);

        return new GreedyPreparationResult(
            baseFeasiblePlan,
            effectiveFeasiblePlan,
            gtPlan,
            baseFeasibleSolution,
            effectiveFeasibleSolution,
            gtSolution,
            gtProbeRun,
            gtImproved,
            feasibleArtifacts.Timings.Total,
            gtElapsed,
            feasibleArtifacts.Timings,
            gtTimings);
    }
}
