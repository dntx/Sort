using TopKFinder;
using Xunit;

public sealed class CompactSnapshotTests
{
    [Theory]
    [InlineData(6, 3, 3)]
    [InlineData(8, 2, 2)]
    public void ExactEdgeCompactSnapshot_ReplaysAfterLaterCompactProbe(int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k);
        CompactStageArtifacts first = builder.ExecuteEdgeCompactStageWithSolution();

        Assert.Equal(SolvedStrategyStageKind.ExactEdgeCompact, first.Solution.Provenance.Kind);
        Assert.True(first.Solution.Bounds.IsProvenOptimal);
        Assert.Equal(first.Plan.MaxStep, first.Solution.Score.WorstCaseSteps);
        Assert.NotNull(first.Solution.Score.SearchEdgeCost);

        builder.ExecuteProofTightenStage(System.Math.Max(1, first.Plan.MaxStep - 1));
        StrategyPlan replayed = builder.MaterializeCompactSolutionForTesting(first.Solution);

        Assert.Equal(first.Plan.MaxStep, replayed.MaxStep);
        Assert.Equal(first.Plan.TotalBranchEdges, replayed.TotalBranchEdges);
    }

    [Theory]
    [InlineData(8, 3, 3)]
    [InlineData(10, 4, 4)]
    public void GreedyEdgeCompactSnapshot_ReplaysAfterLaterCompactProbe(int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k);
        StrategyPlan feasible = builder.ExecuteGreedyFeasibleStage();
        CompactPlanResult first = builder.BuildEdgeCompactPlanAtBudget(feasible.MaxStep);

        Assert.NotNull(first.Solution);
        Assert.Equal(SolvedStrategyStageKind.GreedyEdgeCompact, first.Solution!.Provenance.Kind);
        Assert.False(first.Solution.Bounds.IsProvenOptimal);
        Assert.Equal(first.Plan.MaxStep, first.Solution.Score.WorstCaseSteps);

        builder.ExecuteProofTightenStage(System.Math.Max(1, first.Plan.MaxStep - 1));
        StrategyPlan replayed = builder.MaterializeCompactSolutionForTesting(first.Solution);

        Assert.Equal(first.Plan.MaxStep, replayed.MaxStep);
        Assert.Equal(first.Plan.TotalBranchEdges, replayed.TotalBranchEdges);
    }
}