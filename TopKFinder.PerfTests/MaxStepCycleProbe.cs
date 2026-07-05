using Xunit;
using Xunit.Abstractions;

// Investigates whether the reference graph of a materialized plan actually contains a cycle
// (a reference resolving back to an ancestor on the reference-traversal stack), and if so prints the
// cycle + the involved states so we can tell a benign display dedup from a construction bug.
// Gated behind RUN_GT_CYCLE.
public sealed class MaxStepCycleProbe
{
    private readonly ITestOutputHelper _out;
    public MaxStepCycleProbe(ITestOutputHelper o) => _out = o;

    [Fact]
    public void FindReferenceCycles()
    {
        if (Environment.GetEnvironmentVariable("RUN_GT_CYCLE") != "1")
            return;

        for (int n = 5; n <= 9; n++)
        for (int m = 2; m <= n; m++)
        for (int k = 1; k <= n / 2; k++)
        {
            if (Program.IsPotentiallySlowSearch(n, m, k))
                continue;

            foreach (var (label, plan) in new (string, StrategyPlan)[]
            {
                ("greedy-feasible", new StrategyBuilder(n, m, k).BuildGreedyFeasiblePlan()),
                ("greedy-tighten", new StrategyBuilder(n, m, k).BuildGreedyTightenPlan()),
                ("step-proof", new StrategyBuilder(n, m, k).BuildStepProofPlan()),
            })
            {
                var targets = new Dictionary<int, StrategyNode>();
                Index(plan.Root, targets);
                var onStack = new HashSet<StrategyNode>(ReferenceEqualityComparer.Instance);
                var done = new HashSet<StrategyNode>(ReferenceEqualityComparer.Instance);
                var cyclePath = new List<string>();
                if (HasCycle(plan.Root, targets, onStack, done, cyclePath))
                {
                    _out.WriteLine($"CYCLE in {label} ({n},{m},{k}): {string.Join(" -> ", cyclePath)}");
                    return; // report the first (smallest) one
                }
            }
        }
        _out.WriteLine("no reference cycle found in n<=9 sweep");
    }

    private static void Index(StrategyNode node, Dictionary<int, StrategyNode> targets)
    {
        if (node.Kind == StrategyNodeKind.Decision && node.Branches.Count > 0)
            targets[node.StateId] = node;
        foreach (var b in node.Branches) Index(b.Next, targets);
    }

    private static bool HasCycle(
        StrategyNode node, Dictionary<int, StrategyNode> targets,
        HashSet<StrategyNode> onStack, HashSet<StrategyNode> done, List<string> path)
    {
        if (done.Contains(node)) return false;
        if (!onStack.Add(node))
        {
            path.Add($"S{node.StateId}({node.Kind}) <-- back-edge");
            return true;
        }
        path.Add($"S{node.StateId}({node.Kind})");

        if (node.Kind == StrategyNodeKind.Reference)
        {
            if (targets.TryGetValue(node.StateId, out var target) &&
                HasCycle(target, targets, onStack, done, path))
                return true;
        }
        else
        {
            foreach (var b in node.Branches)
                if (HasCycle(b.Next, targets, onStack, done, path))
                    return true;
        }

        path.RemoveAt(path.Count - 1);
        onStack.Remove(node);
        done.Add(node);
        return false;
    }
}
