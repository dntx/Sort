using System;

readonly record struct PipelineCallbacks(
    Action<StageResult>? OnStageCompleted,
    Action<string>? OnStageStart)
{
    public void Start(string stageName)
        => OnStageStart?.Invoke(stageName);

    public void Complete(StageResult stage)
        => OnStageCompleted?.Invoke(stage);
}

static class PipelineStageProtocol
{
    public static StrategyPlan ExecuteCompletedPlanStage(
        string stageName,
        Func<(StrategyPlan Plan, TimeSpan Elapsed)> stageBody,
        PipelineCallbacks callbacks)
    {
        callbacks.Start(stageName);
        (StrategyPlan plan, TimeSpan elapsed) = stageBody();
        callbacks.Complete(new StageResult(stageName, plan, elapsed, StageOutcome.Completed));
        return plan;
    }

    public static void EmitCompletedPlanStage(
        string stageName,
        StrategyPlan plan,
        TimeSpan elapsed,
        PipelineCallbacks callbacks,
        bool emitStart = false)
    {
        if (emitStart)
            callbacks.Start(stageName);
        EmitStage(new StageResult(stageName, plan, elapsed, StageOutcome.Completed), callbacks);
    }

    public static void EmitStage(StageResult stage, PipelineCallbacks callbacks)
        => callbacks.Complete(stage);

    public static bool ReachedStageLimit(int emittedStages, int? stageLimit)
        => stageLimit.HasValue && emittedStages >= stageLimit.Value;

    public static bool IsImprovement(StageResult stage, StrategyPlan incumbent)
        => stage.HasPlan && stage.Plan!.IsStrictRefinementOver(incumbent);

    public static string NoSolutionMarker(StageResult stage)
        => stage.Incomplete ? "search incomplete (candidate cap reached)" : "no solution";

    public static string NextGreedyStageName(StrategyPlan feasiblePlan, int incumbentMaxStep)
    {
        int lower = Math.Max(1, feasiblePlan.SearchStatistics.RootProvenLowerBound);
        int nextBudget = incumbentMaxStep - 1;
        return nextBudget >= lower
            ? StageNames.FormatProofTighten(nextBudget)
            : StageNames.FormatGreedyEdgeCompact(incumbentMaxStep);
    }
}
