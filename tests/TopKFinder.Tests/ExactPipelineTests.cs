using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using TopKFinder;

public sealed class ExactPipelineTests
{
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
        Assert.Equal(StageOutcome.Completed, step.Outcome);
        Assert.Equal(StageOutcome.Completed, compact.Outcome);

        string expectedCompactName = StageNames.FormatExactEdgeCompact(step.Plan!.MaxStep);
        Assert.Equal(expectedCompactName, started[1]);

        Assert.Equal(started, completed.Select(stage => stage.Name).ToList());
        Assert.Same(plan, compact.Plan);
        Assert.Equal(step.Plan!.MaxStep, plan.MaxStep);
    }
}
