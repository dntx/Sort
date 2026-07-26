using TopKFinder;
using System.Threading;
using Xunit;

public sealed class CompactSnapshotTests
{
    [Fact]
    public void SolvedStrategy_MaterializesOnIndependentBuilderAndPreservesSolverStatistics()
    {
        var builder = new StrategyBuilder(8, 3, 3);
        CompactStageArtifacts artifacts = builder.ExecuteEdgeCompactStageWithSolution();

        StrategyPlan replayed = StrategyBuilder.MaterializeSolvedStrategy(
            artifacts.Solution,
            artifacts.Timings.Solve + artifacts.Timings.Freeze);
        StrategyPlan eagerPlan = Assert.IsType<StrategyPlan>(artifacts.Plan);

        Assert.Equal(eagerPlan.MaxStep, replayed.MaxStep);
        Assert.Equal(eagerPlan.TotalBranchEdges, replayed.TotalBranchEdges);
        Assert.Equal(artifacts.Solution.Score.SearchEdgeCost, replayed.SearchStatistics.SearchTreeEdges);
        Assert.Equal(eagerPlan.SearchStatistics.SearchedStates, replayed.SearchStatistics.SearchedStates);
        Assert.Equal(eagerPlan.SearchStatistics.CandidateGroupsEnumerated, replayed.SearchStatistics.CandidateGroupsEnumerated);
        Assert.Equal(eagerPlan.SearchStatistics.OutputStates, replayed.SearchStatistics.OutputStates);
        Assert.Equal(eagerPlan.SearchStatistics.OutcomesConstructed, replayed.SearchStatistics.OutcomesConstructed);
        Assert.Equal(
            eagerPlan.SearchStatistics.Diagnostics.DuplicateOutcomeSkips,
            replayed.SearchStatistics.Diagnostics.DuplicateOutcomeSkips);
        Assert.Equal(
            eagerPlan.SearchStatistics.Diagnostics.MergedOutcomeCollisions,
            replayed.SearchStatistics.Diagnostics.MergedOutcomeCollisions);
    }

    [Fact]
    public void SolvedStrategy_MaterializationHonorsPresentationCancellation()
    {
        CompactStageArtifacts artifacts = new StrategyBuilder(8, 3, 3)
            .ExecuteEdgeCompactStageWithSolution();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            StrategyBuilder.MaterializeSolvedStrategy(
                artifacts.Solution,
                artifacts.Timings.Solve + artifacts.Timings.Freeze,
                cancellation.Token));
    }

    [Theory]
    [InlineData(6, 3, 3)]
    [InlineData(8, 2, 2)]
    public void ExactEdgeCompactSnapshot_ReplaysAfterLaterCompactProbe(int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k);
        CompactStageArtifacts first = builder.ExecuteEdgeCompactStageWithSolution();
        StrategyPlan firstPlan = Assert.IsType<StrategyPlan>(first.Plan);

        Assert.Equal(SolvedStrategyStageKind.ExactEdgeCompact, first.Solution.Provenance.Kind);
        Assert.True(first.Solution.Bounds.IsProvenOptimal);
        Assert.Equal(firstPlan.MaxStep, first.Solution.Score.WorstCaseSteps);
        Assert.NotNull(first.Solution.Score.SearchEdgeCost);

        builder.ExecuteProofTightenStage(System.Math.Max(1, firstPlan.MaxStep - 1));
        StrategyPlan replayed = builder.MaterializeCompactSolutionForTesting(first.Solution);

        Assert.Equal(firstPlan.MaxStep, replayed.MaxStep);
        Assert.Equal(firstPlan.TotalBranchEdges, replayed.TotalBranchEdges);
    }

    [Theory]
    [InlineData(8, 3, 3)]
    [InlineData(10, 4, 4)]
    public void GreedyEdgeCompactSnapshot_ReplaysAfterLaterCompactProbe(int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k);
        StrategyPlan feasible = builder.ExecuteGreedyFeasibleStage();
        CompactPlanResult first = builder.BuildEdgeCompactPlanAtBudget(feasible.MaxStep);
        StrategyPlan firstPlan = Assert.IsType<StrategyPlan>(first.Plan);

        Assert.NotNull(first.Solution);
        Assert.Equal(SolvedStrategyStageKind.GreedyEdgeCompact, first.Solution!.Provenance.Kind);
        Assert.False(first.Solution.Bounds.IsProvenOptimal);
        Assert.Equal(firstPlan.MaxStep, first.Solution.Score.WorstCaseSteps);

        builder.ExecuteProofTightenStage(System.Math.Max(1, firstPlan.MaxStep - 1));
        StrategyPlan replayed = builder.MaterializeCompactSolutionForTesting(first.Solution);

        Assert.Equal(firstPlan.MaxStep, replayed.MaxStep);
        Assert.Equal(firstPlan.TotalBranchEdges, replayed.TotalBranchEdges);
    }
}