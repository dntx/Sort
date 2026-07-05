using System.Collections.Generic;
using Xunit;

// Locks in the "what you see is what was searched" invariant: for every materialized decision node
// the displayed branches must mirror exactly the distinct successors the search expanded. Equivalent
// orderings are folded into a single branch (with a visible ×count), never dropped, and the tree must
// never show a branch the search did not process. See StrategyBuilder.CheckDisplaySearchParity.
public sealed class DisplaySearchParityTests
{
    private static readonly TimeSpan ParityTestTimeout = TimeSpan.FromSeconds(90);

    public static IEnumerable<object[]> Cases() => new[]
    {
        new object[] { 6, 3, 3 },
        new object[] { 7, 3, 2 },
        new object[] { 8, 4, 2 },
        new object[] { 9, 3, 3 },
        new object[] { 9, 4, 4 },
        new object[] { 10, 5, 5 },
        new object[] { 8, 2, 2 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void DefaultPlan_DisplayBranchesMirrorSearchExpansion(int n, int m, int k)
        => AssertParity("default", n, m, k, builder => builder.BuildStepProofPlan());

    [Theory]
    [MemberData(nameof(Cases))]
    public void FeasiblePlan_DisplayBranchesMirrorSearchExpansion(int n, int m, int k)
        => AssertParity("feasible", n, m, k, builder => builder.BuildGreedyFeasiblePlan());

    [Theory]
    [MemberData(nameof(Cases))]
    public void CompactPlan_DisplayBranchesMirrorSearchExpansion(int n, int m, int k)
        => AssertParity("compact", n, m, k, builder => builder.BuildEdgeCompactPlan());

    private static void AssertParity(string phase, int n, int m, int k, Func<StrategyBuilder, StrategyPlan> build)
    {
        List<string> residual = TestTimeoutHelper.RunWithTimeout(
            $"DisplaySearchParity {phase} ({n},{m},{k})",
            ParityTestTimeout,
            token =>
            {
                // The parity check reads the snapshots captured during the LAST build, so build and
                // check on the same builder instance before anything else can reset them.
                var builder = new StrategyBuilder(n, m, k, token);
                StrategyPlan plan = build(builder);
                return builder.CheckDisplaySearchParity(plan);
            });

        Assert.True(residual.Count == 0, string.Join("\n", residual));
    }
}
