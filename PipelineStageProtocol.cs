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
        PipelineCallbacks callbacks)
        => EmitStage(new StageResult(stageName, plan, elapsed, StageOutcome.Completed), callbacks);

    public static void EmitStage(StageResult stage, PipelineCallbacks callbacks)
        => callbacks.Complete(stage);
}
