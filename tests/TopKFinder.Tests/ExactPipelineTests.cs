using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using TopKFinder;

public sealed class ExactPipelineTests
{
    [Fact]
    public void RunExactPipelineDeferred_EmitsSolutionsWithoutMaterializedPlans()
    {
        var stages = new List<StageResult>();

        PublicPipelineOrchestrator.RunExactPipelineDeferred(
            new StrategyBuilder(9, 3, 3),
            stages.Add);

        Assert.Equal(2, stages.Count);
        Assert.All(stages, stage =>
        {
            Assert.False(stage.HasPlan);
            Assert.NotNull(stage.Solution);
            Assert.Equal(TimeSpan.Zero, stage.Timings.Materialize);
            Assert.Equal(stage.Elapsed, stage.Timings.Solve + stage.Timings.Freeze);
        });
    }

    [Fact]
    public void RunExactPipelineDeferred_BoundariesNameFollowingStage()
    {
        var boundaries = new List<StageCompletion>();

        PublicPipelineOrchestrator.RunExactPipelineDeferred(
            new StrategyBuilder(9, 3, 3),
            onStageCompleted: static _ => { },
            onStageBoundary: boundaries.Add);

        Assert.Equal(2, boundaries.Count);
        Assert.Equal(boundaries[1].Stage.Name, boundaries[0].NextStageName);
        Assert.Null(boundaries[1].NextStageName);
    }

    [Fact]
    public void RunExactPipeline_EmitsCanonicalStages_AndReturnsLastStagePlan()
    {
        var started = new List<string>();
        var completed = new List<StageResult>();

        StrategyPlan plan = new StrategyBuilder(9, 3, 3).RunExactPipeline(
            onStageCompleted: completed.Add,
            onStageStart: started.Add);

        Assert.Equal(2, started.Count);
        Assert.Equal(2, completed.Count);

        Assert.Equal(StageNames.StepProof, started[0]);

        StageResult step = completed[0];
        StageResult compact = completed[1];

        Assert.True(step.HasPlan);
        Assert.True(compact.HasPlan);
        Assert.NotNull(step.Solution);
        Assert.NotNull(compact.Solution);
        Assert.Equal(StageOutcome.Completed, step.Outcome);
        Assert.Equal(StageOutcome.Completed, compact.Outcome);
        Assert.Equal(SolvedStrategyStageKind.StepProof, step.Solution!.Provenance.Kind);
        Assert.Equal(SolvedStrategyStageKind.ExactEdgeCompact, compact.Solution!.Provenance.Kind);
        Assert.Equal(step.Elapsed, step.Timings.Total);
        Assert.Equal(compact.Elapsed, compact.Timings.Total);
        Assert.True(step.Timings.Freeze > TimeSpan.Zero);
        Assert.True(compact.Timings.Freeze > TimeSpan.Zero);
        Assert.True(step.Timings.Materialize > TimeSpan.Zero);
        Assert.True(compact.Timings.Materialize > TimeSpan.Zero);

        string expectedCompactName = StageNames.FormatExactEdgeCompact(step.MaterializedPlan!.MaxStep);
        Assert.Equal(expectedCompactName, started[1]);

        Assert.Equal(started, completed.Select(stage => stage.Name).ToList());
        Assert.Same(plan, compact.MaterializedPlan);
        Assert.Equal(step.MaterializedPlan!.MaxStep, plan.MaxStep);
        Assert.Equal(step.MaterializedPlan.MaxStep, step.Solution.Score.WorstCaseSteps);
        Assert.Equal(compact.MaterializedPlan!.MaxStep, compact.Solution.Score.WorstCaseSteps);
    }

    [Fact]
    public void ExactCompact_SearchCostImprovementWinsWhenDisplayEdgesIncrease()
    {
        var stages = new List<StageResult>();

        new StrategyBuilder(10, 4, 8).RunExactPipeline(onStageCompleted: stages.Add);

        Assert.Equal(2, stages.Count);
        StageResult step = stages[0];
        StageResult compact = stages[1];
        Assert.True(compact.Solution!.Score.SearchEdgeCost < step.Solution!.Score.SearchEdgeCost);
        Assert.True(compact.MaterializedPlan!.TotalBranchEdges > step.MaterializedPlan!.TotalBranchEdges);
        Assert.True(PipelineStageProtocol.IsImprovement(compact, step));
    }
}
