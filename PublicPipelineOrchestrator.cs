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

readonly record struct ExactStepStageResult(
    SearchStrategy SearchTree,
    StrategyPlan StepPlan,
    TimeSpan Elapsed);

readonly record struct ExactCompactStageResult(
    StrategyPlan CompactPlan,
    bool Improved,
    TimeSpan Elapsed);

static class PublicPipelineOrchestrator
{
    public static StrategyPlan RunExactPipeline(
        StrategyBuilder builder,
        Action<StageResult>? onStageCompleted = null,
        Action<string>? onStageStart = null)
    {
        const string stepStageName = "step-proof";
        onStageStart?.Invoke(stepStageName);
        ExactStepStageResult step = PrepareExactStepStage(builder);
        onStageCompleted?.Invoke(new StageResult(stepStageName, step.StepPlan, step.Elapsed, StageOutcome.Completed));

        string compactStageName = StrategyBuilder.FormatExactEdgeCompactStageName(step.StepPlan.MaxStep);
        onStageStart?.Invoke(compactStageName);
        ExactCompactStageResult compact = PrepareExactCompactStage(builder, step.StepPlan);
        onStageCompleted?.Invoke(new StageResult(compactStageName, compact.CompactPlan, compact.Elapsed, StageOutcome.Completed));
        return compact.CompactPlan;
    }

    public static StrategyPlan RunGreedyPipeline(
        StrategyBuilder builder,
        Action<StageResult>? onStageCompleted = null,
        Action<string>? onStageStart = null)
    {
        const string feasibleStageName = "greedy-feasible";
        onStageStart?.Invoke(feasibleStageName);
        GreedyPreparationResult prep = PrepareGreedyUpperBound(builder);
        onStageCompleted?.Invoke(new StageResult(
            feasibleStageName,
            prep.BaseFeasiblePlan,
            prep.GreedyFeasibleElapsed,
            StageOutcome.Completed));

        if (prep.GreedyTightenProbeRun && prep.GreedyTightenPlan is not null)
        {
            const string tightenStageName = "greedy-tighten";
            onStageStart?.Invoke(tightenStageName);
            onStageCompleted?.Invoke(new StageResult(
                tightenStageName,
                prep.GreedyTightenPlan,
                prep.GreedyTightenElapsed,
                StageOutcome.Completed));
        }

        return builder.RunGreedyPipelineCore(onStageCompleted, onStageStart);
    }

    // Shared exact stage-1 orchestration used by public callers (CLI/UI): materialize the exact
    // step-proof display plan together with its projected search model through the explicit layered
    // entrypoint.
    public static ExactStepStageResult PrepareExactStepStage(StrategyBuilder builder)
    {
        var stopwatch = Stopwatch.StartNew();
        (SearchStrategy searchTree, StrategyPlan stepPlan) = builder.BuildLayeredStepProof();
        stopwatch.Stop();
        return new ExactStepStageResult(searchTree, stepPlan, stopwatch.Elapsed);
    }

    // Shared exact stage-2 orchestration used by public callers (CLI/UI): run one compact pass and
    // classify whether it is a strict refinement over the exact step-stage incumbent.
    public static ExactCompactStageResult PrepareExactCompactStage(StrategyBuilder builder, StrategyPlan incumbent)
    {
        var stopwatch = Stopwatch.StartNew();
        StrategyPlan compactPlan = builder.BuildEdgeCompactStage();
        stopwatch.Stop();
        return new ExactCompactStageResult(
            compactPlan,
            compactPlan.IsStrictRefinementOver(incumbent),
            stopwatch.Elapsed);
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
