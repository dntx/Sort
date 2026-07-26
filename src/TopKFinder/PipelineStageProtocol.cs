using System;

namespace TopKFinder;

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
    public static void EmitStage(StageResult stage, PipelineCallbacks callbacks)
        => callbacks.Complete(stage);

    public static bool ReachedStageLimit(int emittedStages, int? stageLimit)
        => stageLimit.HasValue && emittedStages >= stageLimit.Value;

    public static bool IsImprovement(StageResult stage, StageResult incumbent)
    {
        if (stage.Solution is not null || incumbent.Solution is not null)
        {
            return stage.Solution is not null
                && incumbent.Solution is not null
                && stage.Solution.Score.IsStrictRefinementOver(incumbent.Solution.Score);
        }

        return stage.HasPlan
            && incumbent.HasPlan
            && stage.Plan!.IsStrictRefinementOver(incumbent.Plan!);
    }

    public static string NoSolutionMarker(StageResult stage)
        => stage.Incomplete ? "search incomplete (candidate cap reached)" : "no solution";

    public static string NextGreedyStageName(SolvedStrategy feasibleSolution, int incumbentMaxStep)
    {
        int lower = Math.Max(1, feasibleSolution.Bounds.ProvenLowerBound);
        int nextBudget = incumbentMaxStep - 1;
        return nextBudget >= lower
            ? StageNames.FormatProofTighten(nextBudget)
            : StageNames.FormatGreedyEdgeCompact(incumbentMaxStep);
    }
}
