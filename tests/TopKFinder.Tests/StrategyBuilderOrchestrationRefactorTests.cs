using System.Collections.Generic;
using TopKFinder;
using Xunit;

public sealed class StrategyBuilderOrchestrationRefactorTests
{
    [Fact]
    public void StepProof_WithProgressCallback_ReportsBoundedProgressAndConsistentTerminalSnapshot()
    {
        var snapshots = new List<SearchProgressSnapshot>();
        var builder = new StrategyBuilder(
            n: 9,
            m: 3,
            k: 3,
            progressCallback: snapshots.Add,
            reportCombinedRunProgress: false);

        StrategyPlan plan = builder.ExecuteStepProofStage();

        Assert.NotEmpty(snapshots);
        SearchProgressSnapshot terminal = snapshots[^1];

        Assert.InRange(terminal.EstimatedProgress01, 0.0, 1.0);
        Assert.True(terminal.SearchedStates >= 0);
        Assert.True(terminal.PendingStates >= 0);
        Assert.True(terminal.PeakPendingStates >= terminal.PendingStates);
        Assert.True(terminal.RootProvenLowerBound >= 1);
        Assert.Equal(plan.MaxStep, terminal.RootProvenLowerBound);
    }

    [Fact]
    public void GreedyPipeline_WithCombinedProgress_ReportsMonotoneRootLowerBound()
    {
        var snapshots = new List<SearchProgressSnapshot>();
        var builder = new StrategyBuilder(
            n: 9,
            m: 3,
            k: 3,
            progressCallback: snapshots.Add,
            reportCombinedRunProgress: true);

        StrategyPlan plan = builder.RunGreedyPipeline();

        Assert.NotEmpty(snapshots);

        int previous = 0;
        foreach (SearchProgressSnapshot snapshot in snapshots)
        {
            Assert.InRange(snapshot.EstimatedProgress01, 0.0, 1.0);
            Assert.True(snapshot.RootProvenLowerBound >= previous);
            previous = snapshot.RootProvenLowerBound;
        }

        SearchProgressSnapshot terminal = snapshots[^1];
        Assert.True(terminal.RootProvenLowerBound >= 1);
        Assert.True(terminal.RootProvenLowerBound <= plan.MaxStep);
    }
}
