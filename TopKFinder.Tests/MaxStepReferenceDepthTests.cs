using System;
using Xunit;

// Locks the reference-aware MaxStep logic with CONCRETE, hand-built trees (independent of any
// strategy, whose generated shapes may drift over time):
//   1. a valid forward reference must have its target subtree depth counted (not treated as a
//      0-depth leaf), or MaxStep undercounts the true worst case;
//   2. a reference cycle (a reference resolving back onto its own resolution path) is a MALFORMED
//      tree and must THROW, not silently return a number the caller would trust.
// Two real-plan integration checks are also kept: greedy-feasible(6,2,2) is a genuine valid tree
// whose deepest path ends in a "+1 step" reference (true MaxStep 7, previously undercounted to 6).
public class MaxStepReferenceDepthTests
{
    // --- Concrete-tree logic locks ---

    // Tree (decision step in [] ; each node below has a single branch unless noted):
    //   S1[1] -+- S2[2] -- S3[3, final-choice]
    //          +- S4[2] -- S5[3] -- ref->S2
    // S2's subtree needs 2 more sorts (S2 then final-choice S3). The reference under S5 sits deeper
    // than S2 was expanded, so its path resolves in 1(S1)+1(S4)+1(S5)+2(reuse S2) = 5 sorts. Counting
    // the reference as a 0-depth leaf would instead report only the deepest decision step, 3.
    [Fact]
    public void MaxStep_CountsForwardReferenceDepth()
    {
        StrategyNode s3 = FinalChoice(3, step: 3);
        StrategyNode s2 = Decision(2, step: 2, Branch(s3));
        StrategyNode s5 = Decision(5, step: 3, Branch(StrategyNode.Reference(2))); // reuse S2's subtree
        StrategyNode s4 = Decision(4, step: 2, Branch(s5));
        StrategyNode s1 = Decision(1, step: 1, Branch(s2), Branch(s4));

        Assert.Equal(5, StrategyPlan.GetMaxStep(s1));
    }

    // A reference that resolves back onto its own resolution path (here S1 references S1) is a
    // malformed, non-terminating tree; MaxStep must surface it as an error rather than a silent value.
    [Fact]
    public void MaxStep_ThrowsOnReferenceCycle()
    {
        StrategyNode selfReference = StrategyNode.Reference(1);        // points back at S1
        StrategyNode terminal = StrategyNode.Terminal(2, new[] { 0, 1 });
        StrategyNode s1 = Decision(1, step: 1, Branch(terminal), Branch(selfReference));

        Assert.Throws<InvalidOperationException>(() => StrategyPlan.GetMaxStep(s1));
    }

    // --- Real-plan integration guards (valid trees; must not throw) ---

    [Fact]
    public void GreedyFeasible_6_2_2_MaxStepCountsReferenceDepth()
    {
        StrategyPlan feasible = new StrategyBuilder(6, 2, 2).BuildGreedyFeasibleStage();
        Assert.Equal(7, feasible.MaxStep); // deepest path ends in a "+1 step" reference; naive count is 6
    }

    [Theory]
    [InlineData(6, 2, 2)]
    [InlineData(7, 2, 3)]
    [InlineData(8, 2, 3)]
    [InlineData(9, 2, 2)]
    public void GreedyFeasible_UpperBoundNeverBelowOptimum_LowM(int n, int m, int k)
    {
        int optimum = new StrategyBuilder(n, m, k).BuildStepProofStage().MaxStep;
        int feasible = new StrategyBuilder(n, m, k).BuildGreedyFeasibleStage().MaxStep;

        Assert.True(feasible >= optimum,
            $"feasible upper bound {feasible} was below the true optimum {optimum} for ({n},{m},{k})");
    }

    // --- helpers ---

    private static StrategyBranch Branch(StrategyNode next)
        => new StrategyBranch("order", null,
            new StrategyEffect(Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()),
            next);

    private static StrategyNode Decision(int stateId, int step, params StrategyBranch[] branches)
        => StrategyNode.Decision(stateId, step, new[] { 0, 1 }, branches);

    private static StrategyNode FinalChoice(int stateId, int step)
        => StrategyNode.Decision(stateId, step, new[] { 0, 1 }, Array.Empty<StrategyBranch>(),
            new FinalChoiceSummary(Array.Empty<int>(), new[] { 0, 1 }, 1));
}

