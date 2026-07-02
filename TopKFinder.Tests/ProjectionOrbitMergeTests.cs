using System;
using Xunit;

// Guards the opt-in principle-D projection-orbit merging (EnableProjectionOrbitMerging). The default
// plan must be unaffected by the toggle being off (covered byte-identically by the snapshot/parity
// suites); these tests pin the toggle-on behaviour:
//   1. It folds sibling orderings that become interchangeable only after the items they both
//      eliminate this step are dropped, reducing displayed branch edges WITHOUT changing MaxStep.
//   2. A folded line is rendered honestly -- a total-order representative plus a relabeling legend
//      "(#a) ↔ (#b)" and an explicit "drop {...}" disclosure of the commonly-doomed set -- never as
//      a bare brace that would claim a symmetry of the full poset that does not hold.
//   3. Genuinely identical-outcome literal braces ({#1, #9} > #2 > #6) and parent-symmetric braces
//      ({#1, #5} > #2) are NOT disrupted: they stay clean braces, since choosing either order yields
//      the same (or a true parent-automorphic) successor.
public sealed class ProjectionOrbitMergeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static (StrategyPlan Plan, string Text) Build(int n, int m, int k, bool projectionMerging)
    {
        StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildDefaultPlan({n}, {m}, {k}) projectionMerging={projectionMerging}",
            Timeout,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken)
            {
                EnableProjectionOrbitMerging = projectionMerging,
            }.BuildDefaultPlan());
        return (plan, StrategyTextRenderer.Render(plan));
    }

    [Fact]
    public void N7M3K2_MultiFamilyMergeRendersStructuralQuotient()
    {
        (StrategyPlan off, string offText) = Build(7, 3, 2, projectionMerging: false);
        (StrategyPlan on, string onText) = Build(7, 3, 2, projectionMerging: true);

        // Folding the {block > #7} and {block-split-by-#7} families onto one quotient line saves a
        // displayed edge without changing the optimal depth.
        Assert.Equal(off.MaxStep, on.MaxStep);
        Assert.True(
            on.TotalBranchEdges < off.TotalBranchEdges,
            $"expected projection merging to reduce branch edges; off={off.TotalBranchEdges} on={on.TotalBranchEdges}");

        // The merged multi-family component renders in the structural quotient notation: the block A
        // carries its tail chains, {A2, #7} is the post-projection brace, and the drop is the
        // structural (covariant) tail of A2 -- never a hardcoded instance.
        Assert.Contains("equivalent forms: 4 = 2! x 2", onText);
        Assert.Contains("pattern: A1 > {A2, #7} ; A = {#1 > #2, #4 > #5} ; drop tail(A2)", onText);

        // The default (toggle-off) render keeps the families split and never invents the quotient.
        Assert.DoesNotContain("A1 > {A2", offText);
        Assert.DoesNotContain("drop tail(", offText);
    }

    [Fact]

    public void N9M4K4_ProjectionMergingFoldsEdgesAndDisclosesDrop()
    {
        (StrategyPlan off, string offText) = Build(9, 4, 4, projectionMerging: false);
        (StrategyPlan on, string onText) = Build(9, 4, 4, projectionMerging: true);

        // Same optimal depth; merging only changes how many sibling lines the display folds.
        Assert.Equal(off.MaxStep, on.MaxStep);
        Assert.True(
            on.TotalBranchEdges < off.TotalBranchEdges,
            $"expected projection merging to reduce branch edges; off={off.TotalBranchEdges} on={on.TotalBranchEdges}");

        // The default (toggle-off) render never invents a drop disclosure.
        Assert.DoesNotContain("drop {", offText);

        // A genuine projection merge is disclosed honestly: representative + relabel legend + drop set.
        Assert.Contains("#1 > #6 > #9 > #2 ; (#1) ↔ (#9) ; drop {#2, #3, #4, #8}", onText);

        // Identical-outcome literal braces are preserved -- choosing either order yields the same
        // successor, so they must stay clean braces, not be rerouted through the drop disclosure.
        Assert.Contains("{#1, #9} > #2 > #6", onText);
    }

    [Fact]
    public void N8M3K3_ParentSymmetricBraceIsNotRerouted()
    {
        (_, string onText) = Build(8, 3, 3, projectionMerging: true);

        // {#1, #5} > #2 is a parent-symmetric brace (the two orderings are interchangeable in the
        // full poset). The projection pass must leave it exactly as a brace, never claim a drop.
        Assert.Contains("{#1, #5} > #2", onText);
    }

    [Fact]
    public void N6M3K2_LeafBlockTailedPartnerFoldsShapeA()
    {
        (StrategyPlan off, string offText) = Build(6, 3, 2, projectionMerging: false);
        (StrategyPlan on, string onText) = Build(6, 3, 2, projectionMerging: true);

        // Shape A is the mirror of the canonical quotient: the two-member symmetric block is a pair
        // of leaves {#4, #5}, and the odd partner #1 is the one carrying the doomed tail. Folding the
        // {A2, #1} brace saves an edge without changing the optimal depth.
        Assert.Equal(off.MaxStep, on.MaxStep);
        Assert.True(
            on.TotalBranchEdges < off.TotalBranchEdges,
            $"expected shape-A merging to reduce branch edges; off={off.TotalBranchEdges} on={on.TotalBranchEdges}");

        // The block is printed as bare leaves and the covariant drop targets the partner's tail.
        Assert.Contains("equivalent forms: 4 = 2! x 2", onText);
        Assert.Contains("pattern: A1 > {A2, #1} ; A = {#4, #5} ; drop tail(#1)", onText);

        // The default (toggle-off) render keeps the two families split and never invents the quotient.
        Assert.Contains("pattern: A1 > #1 > A2 ; A = {#4, #5}", offText);
        Assert.Contains("pattern: {#4, #5} > #1", offText);
        Assert.DoesNotContain("A1 > {A2", offText);
    }

    [Fact]
    public void N8M3K3_BottomAnchoredBlockMinFoldsShapeB()
    {
        (StrategyPlan off, string offText) = Build(8, 3, 3, projectionMerging: false);
        (StrategyPlan on, string onText) = Build(8, 3, 3, projectionMerging: true);

        // Shape B is the bottom-anchored mirror of canonical: the symmetric block {#2, #7} sits above
        // the partner #5, so a block member is the unique MINIMUM. The eliminated bottom block member
        // (A2) has its WHOLE chain dropped, and A1 + the partner become interchangeable only then.
        Assert.Equal(off.MaxStep, on.MaxStep);
        Assert.True(
            on.TotalBranchEdges < off.TotalBranchEdges,
            $"expected shape-B merging to reduce branch edges; off={off.TotalBranchEdges} on={on.TotalBranchEdges}");

        // The block carries its chains and the covariant drop is the whole chain of the min (A2).
        Assert.Contains("equivalent forms: 4 = 2! x 2", onText);
        Assert.Contains("pattern: {A1, #5} > A2 ; A = {#2 > #3, #7 > #8} ; drop chain(A2)", onText);

        // The default (toggle-off) render keeps the two families split and never invents the quotient.
        Assert.Contains("pattern: #5 > {#2, #7}", offText);
        Assert.DoesNotContain("{A1, #5} > A2", offText);
        Assert.DoesNotContain("drop chain(", offText);
    }
}
