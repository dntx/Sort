using System;

namespace TopKFinder;

readonly record struct StageCompletion(StageResult Stage, string? NextStageName);

readonly record struct PipelineCallbacks(
    Action<StageResult>? OnStageCompleted,
    Action<string>? OnStageStart,
    Action<StageCompletion>? OnStageBoundary = null)
{
    public void Start(string stageName)
        => OnStageStart?.Invoke(stageName);

    public void Complete(StageResult stage, string? nextStageName = null)
    {
        OnStageCompleted?.Invoke(stage);
        OnStageBoundary?.Invoke(new StageCompletion(stage, nextStageName));
    }
}

static class PipelineStageProtocol
{
    public static void EmitStage(StageResult stage, PipelineCallbacks callbacks, string? nextStageName = null)
        => callbacks.Complete(stage, nextStageName);

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
            && stage.MaterializedPlan!.IsStrictRefinementOver(incumbent.MaterializedPlan!);
    }

    public static string NoSolutionMarker(StageResult stage)
                => stage.Skipped ? "skipped"
              : stage.Incomplete ? "search incomplete (candidate cap reached)"
            : "no solution";

    public static string NextGreedyStageName(SolvedStrategy feasibleSolution, int incumbentMaxStep)
        => NextGreedyStageName(feasibleSolution.Bounds.ProvenLowerBound, incumbentMaxStep);

    public static string NextGreedyStageName(int provenLowerBound, int incumbentMaxStep)
    {
        int lower = Math.Max(1, provenLowerBound);
        int nextBudget = incumbentMaxStep - 1;
        return nextBudget >= lower
            ? StageNames.FormatProofTighten(nextBudget)
            : StageNames.FormatGreedyEdgeCompact(incumbentMaxStep);
    }
}
