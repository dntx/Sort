using Xunit;

// Deterministic, machine-independent regression net for the relabeling-orbit display folding
// (StrategyBuilder.Transitions.cs BuildBranchSpecForLine / SplitMergedBucketIntoBranchLines).
//
// At 20,10,10 the feasible plan's node S3 sorts two interchangeable sorted chains (#1~#10 and
// #11~#20). The chain-swap #i <-> #i+10 is a genuine active-poset automorphism, so every sibling
// ordering has a mirror that converges to the same canonical child. The pattern engine cannot
// express that cross-relabeling as a disjunction-free template, so before folding these mirror
// pairs were re-split into separate branch lines -- an honest-but-redundant blow-up of ~250 edges
// where ~125 were automorphism-backed duplicates. CheckPlanFalseSplits counts exactly those
// automorphism-backed-yet-split sibling pairs; folding drives it to zero. This test locks that in
// with a counter (not wall-clock), so a regression that re-splits genuine orbits fails here.
[Trait("Category", "Slow")]
public sealed class RelabelingOrbitFoldingTests
{
    [Fact]
    public void FeasiblePlan_N20M10K10_HasNoAutomorphismBackedSplits()
    {
        var builder = new StrategyBuilder(20, 10, 10);
        StrategyPlan plan = builder.ExecuteGreedyFeasibleStage();

        System.Collections.Generic.List<string> residual = builder.CheckPlanFalseSplits(plan);

        Assert.True(residual.Count == 0,
            $"Found {residual.Count} automorphism-backed sibling pairs still rendered split. First: " +
            $"{(residual.Count > 0 ? residual[0] : string.Empty)}");
    }
}
