using Xunit;
using Xunit.Abstractions;

// One-off diagnostic for the (6,2,2) anomaly surfaced by the GreedyTighten eval:
// BuildGreedyFeasiblePlan().MaxStep == 6 but the proven optimum is 7 (U < opt should be impossible).
// Prints the greedy-feasible and step-proof trees + node stats so we can tell whether the greedy tree
// is a valid 7-step tree whose MaxStep is undercounted by a deep Reference, or an invalid tree.
// Gated behind RUN_GT_ANOMALY.
public sealed class GreedyTightenAnomalyProbe
{
    private readonly ITestOutputHelper _out;
    public GreedyTightenAnomalyProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Probe_6_2_2()
    {
        if (Environment.GetEnvironmentVariable("RUN_GT_ANOMALY") != "1")
            return;

        var feasible = new StrategyBuilder(6, 2, 2).BuildGreedyFeasiblePlan();
        var exact = new StrategyBuilder(6, 2, 2).BuildStepProofPlan();

        _out.WriteLine($"greedy-feasible MaxStep = {feasible.MaxStep}");
        _out.WriteLine($"step-proof     MaxStep = {exact.MaxStep}");
        _out.WriteLine($"feasible node stats: {NodeStats(feasible.Root)}");
        _out.WriteLine($"feasible reference-aware worst-case depth = {ReferenceAwareDepth(feasible.Root)}");
        _out.WriteLine("");
        _out.WriteLine("=== greedy-feasible tree ===");
        _out.WriteLine(StrategyTextRenderer.Render(feasible));
    }

    private static string NodeStats(StrategyNode root)
    {
        int decisions = 0, terminals = 0, references = 0, maxDecisionStep = 0;
        void Walk(StrategyNode n)
        {
            switch (n.Kind)
            {
                case StrategyNodeKind.Decision:
                    decisions++;
                    if ((n.Step ?? 0) > maxDecisionStep) maxDecisionStep = n.Step ?? 0;
                    break;
                case StrategyNodeKind.Terminal: terminals++; break;
                case StrategyNodeKind.Reference: references++; break;
            }
            foreach (var b in n.Branches) Walk(b.Next);
        }
        Walk(root);
        return $"decisions={decisions} terminals={terminals} references={references} maxDecisionStep={maxDecisionStep}";
    }

    // Depth of the deepest leaf counting every Decision edge, treating a Reference as a leaf at its
    // own position (same as GetMaxStep). If this equals GetMaxStep, References are NOT the cause.
    private static int ReferenceAwareDepth(StrategyNode root)
    {
        int max = 0;
        void Walk(StrategyNode n, int depth)
        {
            if (n.Kind == StrategyNodeKind.Decision && n.Branches.Count > 0)
                foreach (var b in n.Branches) Walk(b.Next, depth + 1);
            else if (depth > max) max = depth;
        }
        Walk(root, 0);
        return max;
    }
}
