using Xunit;

// On-demand dump that renders, for a single (n, m, k), the production DEFAULT plan next to the
// edge-minimal step-optimal plan proven by the gap oracle (StrategyBuilder.OptimalityGap.cs). It lets
// us eyeball *why* the true optimum has fewer displayed edges than default/compact (e.g. 10,4,8: 8 -> 5).
//
// Gated behind RUN_GAP_DUMP. To run:
//   $env:RUN_GAP_DUMP = "1"; $env:GAP_DUMP_CASE = "10,4,8"
//   dotnet test tests\TopKFinder.PerfTests\TopKFinder.PerfTests.csproj --filter GapTreeDump
// Knobs: GAP_DUMP_CASE (default "10,4,8"), GAP_DUMP_CAP (default 2000000),
//   GAP_DUMP_PATH (default <repo>\compact-gap-dump.txt).
public sealed class GapTreeDumpTests
{
    [Fact]
    public void GapTreeDump()
    {
        if (Environment.GetEnvironmentVariable("RUN_GAP_DUMP") != "1")
            return; // skipped in normal runs

        string caseSpec = Environment.GetEnvironmentVariable("GAP_DUMP_CASE") ?? "10,4,8";
        long cap = long.TryParse(Environment.GetEnvironmentVariable("GAP_DUMP_CAP"), out long c) && c > 0 ? c : 2000000;
        string path = Environment.GetEnvironmentVariable("GAP_DUMP_PATH")
            ?? Path.Combine(FindRepoRoot(), "compact-gap-dump.txt");

        string[] f = caseSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int n = int.Parse(f[0]), m = int.Parse(f[1]), k = int.Parse(f[2]);

        StrategyPlan def = new StrategyBuilder(n, m, k).ExecuteStepProofStage();
        StrategyPlan compact = new StrategyBuilder(n, m, k).ExecuteEdgeCompactStage();
        var optimalBuilder = new StrategyBuilder(n, m, k);
        StrategyPlan? optimal = optimalBuilder.BuildEdgeOptimalPlan(cap);

        // Honesty audit of the compact plan: forward (false-split) + reverse (false-merge).
        var compactAuditBuilder = new StrategyBuilder(n, m, k);
        StrategyPlan compactForAudit = compactAuditBuilder.ExecuteEdgeCompactStage();
        List<string> compactFalseSplits = compactAuditBuilder.CheckPlanFalseSplits(compactForAudit);
        string compactOrbitDiag = compactAuditBuilder.DiagnoseOrbitPartition(compactForAudit.Root);

        using var w = new StreamWriter(path, append: false);
        w.WriteLine($"# Gap tree dump for {n},{m},{k}");
        w.WriteLine($"default edges = {def.TotalBranchEdges} (maxStep {def.MaxStep})");
        w.WriteLine($"compact edges = {compact.TotalBranchEdges} (maxStep {compact.MaxStep})");
        w.WriteLine(optimal is null
            ? "edge-optimal = INFEASIBLE (search space over cap)"
            : $"edge-optimal edges = {optimal.TotalBranchEdges} (maxStep {optimal.MaxStep})");
        w.WriteLine();
        w.WriteLine("============ COMPACT HONESTY AUDIT ============");
        w.WriteLine($"compact false-splits (sibling orbit rendered split): {compactFalseSplits.Count}");
        foreach (string s in compactFalseSplits)
            w.WriteLine($"  {s}");
        w.WriteLine(compactOrbitDiag);
        w.WriteLine();
        w.WriteLine("==================== DEFAULT PLAN ====================");
        w.WriteLine(StrategyTextRenderer.Render(def));
        if (optimal is not null)
        {
            w.WriteLine();
            w.WriteLine("================= EDGE-OPTIMAL PLAN =================");
            w.WriteLine(StrategyTextRenderer.Render(optimal));

            w.WriteLine();
            w.WriteLine("============ EDGE-OPTIMAL STRUCTURE WALK ============");
            WalkStructure(optimal.Root, 0, w);

            w.WriteLine();
            w.WriteLine("======== SIBLING-MERGE SPLIT DIAGNOSIS ========");
            w.WriteLine(optimalBuilder.DiagnoseSiblingMergeSplit(optimal.Root));

            w.WriteLine();
            w.WriteLine("======== ORBIT-PARTITION (approach B) DIAGNOSIS ========");
            w.WriteLine(optimalBuilder.DiagnoseOrbitPartition(optimal.Root));
        }
    }

    // Prints raw node structure (id, kind, branch count, per-branch order text + equivalent-forms
    // count + child id/kind) so sibling-branch merging can be inspected directly, independent of the
    // pretty renderer's relabeling.
    private static void WalkStructure(StrategyNode node, int depth, TextWriter w)
    {
        string indent = new string(' ', depth * 2);
        string head = node.Kind == StrategyNodeKind.Decision
            ? (node.FinalChoice is not null ? "FINAL" : "DECISION")
            : node.Kind.ToString().ToUpperInvariant();
        w.WriteLine($"{indent}S{node.StateId} {head} step={node.Step} branches={node.Branches.Count}" +
            (node.Group.Count > 0 ? $" group=[{string.Join(",", node.Group)}]" : ""));
        foreach (StrategyBranch b in node.Branches)
        {
            int forms = b.EquivalentOrders?.Count ?? 1;
            w.WriteLine($"{indent}  -> '{b.OrderText}' forms={forms} childS{b.Next.StateId}/{b.Next.Kind}");
            WalkStructure(b.Next, depth + 2, w);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "TopKFinder", "TopKFinder.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
