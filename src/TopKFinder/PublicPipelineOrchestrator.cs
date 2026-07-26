using System;

namespace TopKFinder;

readonly record struct GreedyPreparationResult(
    StrategyPlan BaseFeasiblePlan,
    StrategyPlan EffectiveFeasiblePlan,
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
    public static StrategyPlan RunExactPipeline(
        StrategyBuilder builder,
        Action<StageResult>? onStageCompleted = null,
        Action<string>? onStageStart = null)
    {
        var callbacks = new PipelineCallbacks(onStageCompleted, onStageStart);

        callbacks.Start(StageNames.StepProof);
        StrategyBuilder.ExactProjectionArtifacts stepArtifacts =
            builder.BuildExactProjectionArtifactsFromCurrentSession();
        var stepStage = new StageResult(
            StageNames.StepProof,
            stepArtifacts.DisplayTree,
            stepArtifacts.Timings.Total,
            StageOutcome.Completed,
            stepArtifacts.Solution,
            stepArtifacts.Timings);
        PipelineStageProtocol.EmitStage(stepStage, callbacks);
        StrategyPlan stepPlan = stepArtifacts.DisplayTree;

        string compactStageName = StageNames.FormatExactEdgeCompact(
            stepArtifacts.Solution.Score.WorstCaseSteps);
        callbacks.Start(compactStageName);
        CompactStageArtifacts compactArtifacts = builder.ExecuteEdgeCompactStageWithSolution();
        var compactStage = new StageResult(
            compactStageName,
            compactArtifacts.Plan,
            compactArtifacts.Plan.Elapsed,
            StageOutcome.Completed,
            compactArtifacts.Solution,
            compactArtifacts.Timings);
        PipelineStageProtocol.EmitStage(compactStage, callbacks);
        return compactArtifacts.Plan;
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

        callbacks.Start(StageNames.GreedyFeasible);
        PipelineStageProtocol.EmitStage(
            new StageResult(
                StageNames.GreedyFeasible,
                prep.BaseFeasiblePlan,
                prep.GreedyFeasibleElapsed,
                StageOutcome.Completed,
                prep.BaseFeasibleSolution,
                prep.GreedyFeasibleTimings),
            callbacks);

        if (prep.GreedyTightenProbeRun && prep.GreedyTightenPlan is not null)
        {
            callbacks.Start(StageNames.GreedyTighten);
            PipelineStageProtocol.EmitStage(
                new StageResult(
                    StageNames.GreedyTighten,
                    prep.GreedyTightenPlan,
                    prep.GreedyTightenElapsed,
                    StageOutcome.Completed,
                    prep.GreedyTightenSolution,
                    prep.GreedyTightenTimings),
                callbacks);
        }

        return prep;
    }

    // Shared greedy pre-stage orchestration used by public callers (CLI/UI): build a feasible upper
    // bound, optionally run one greedy-tighten round, and apply the improved bound override when
    // tightening wins. Search semantics are unchanged; this only centralizes pipeline routing.
    public static GreedyPreparationResult PrepareGreedyUpperBound(StrategyBuilder builder)
    {
        GreedyFeasibleStageArtifacts feasibleArtifacts = builder.ExecuteGreedyFeasibleStageWithSolution();
        StrategyPlan baseFeasiblePlan = feasibleArtifacts.Plan;
        SolvedStrategy baseFeasibleSolution = feasibleArtifacts.Solution;
        StrategyPlan effectiveFeasiblePlan = baseFeasiblePlan;
        SolvedStrategy effectiveFeasibleSolution = baseFeasibleSolution;

        bool gtProbeRun = builder.ShouldRunGreedyTightenByRootProbe();
        StrategyPlan? gtPlan = null;
        SolvedStrategy? gtSolution = null;
        bool gtImproved = false;
        TimeSpan gtElapsed = TimeSpan.Zero;
        StageTimings gtTimings = default;
        if (gtProbeRun)
        {
            GreedyTightenStageArtifacts gtArtifacts = builder.ExecuteGreedyTightenStageWithSolution();
            gtPlan = gtArtifacts.Plan;
            gtSolution = gtArtifacts.Solution;
            gtElapsed = gtPlan.Elapsed;
            gtTimings = gtArtifacts.Timings;
            gtImproved = gtSolution.Score.IsStrictRefinementOver(baseFeasibleSolution.Score);
            if (gtImproved)
            {
                effectiveFeasiblePlan = gtPlan;
                effectiveFeasibleSolution = gtSolution;
                builder.OverrideGreedyPipelineUpperBound(effectiveFeasibleSolution.Score.WorstCaseSteps);
            }
        }

        return new GreedyPreparationResult(
            baseFeasiblePlan,
            effectiveFeasiblePlan,
            gtPlan,
            baseFeasibleSolution,
            effectiveFeasibleSolution,
            gtSolution,
            gtProbeRun,
            gtImproved,
            baseFeasiblePlan.Elapsed,
            gtElapsed,
            feasibleArtifacts.Timings,
            gtTimings);
    }
}
