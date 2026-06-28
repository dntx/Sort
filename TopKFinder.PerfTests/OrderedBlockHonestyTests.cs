using Xunit;

// Honesty/completeness sweep for the ordered-block-permutation detector (StrategyBuilder.EquivalentOrders.cs).
//
// The pattern engine sees only orderings, not the parent state P, so a pattern-only merge could in
// principle fuse a sibling family that is NOT backed by a genuine parent-state automorphism (a false
// symmetry claim). Conversely, after adding the detector, a family that IS automorphism-backed but
// still renders as separate sibling branches is an incompleteness bug (the detector missed a real
// merge). FindResidualFalseSplits (StrategyBuilder.OptimalityGap.cs) builds the edge-optimal plan and
// reports every same-child sibling pair that is automorphism-backed yet still split. A clean sweep
// (no residuals) across all feasible small (n,m,k) is the empirical evidence the merge is honest.
//
// Gated behind RUN_BLOCK_HONESTY. To run:
//   $env:RUN_BLOCK_HONESTY = "1"
//   dotnet test TopKFinder.PerfTests\TopKFinder.PerfTests.csproj --filter OrderedBlockHonesty
// Knobs: BLOCK_HONESTY_NMAX (default 10), BLOCK_HONESTY_CAP (default 2000000),
//   BLOCK_HONESTY_PATH (default <repo>\block-honesty-report.txt).
public sealed class OrderedBlockHonestyTests
{
    [Fact]
    public void OrderedBlockHonesty()
    {
        if (Environment.GetEnvironmentVariable("RUN_BLOCK_HONESTY") != "1")
            return; // skipped in normal runs

        int nMax = int.TryParse(Environment.GetEnvironmentVariable("BLOCK_HONESTY_NMAX"), out int nm) && nm > 0 ? nm : 10;
        int nMin = int.TryParse(Environment.GetEnvironmentVariable("BLOCK_HONESTY_NMIN"), out int ni) && ni >= 2 ? ni : 2;
        long cap = long.TryParse(Environment.GetEnvironmentVariable("BLOCK_HONESTY_CAP"), out long c) && c > 0 ? c : 2000000;
        string path = Environment.GetEnvironmentVariable("BLOCK_HONESTY_PATH")
            ?? Path.Combine(FindRepoRoot(), "block-honesty-report.txt");

        var residuals = new List<string>();
        int casesChecked = 0;
        int casesSkippedOverCap = 0;

        for (int n = nMin; n <= nMax; n++)
            for (int m = 2; m <= n; m++)
                for (int k = 1; k <= n; k++)
                {
                    var builder = new StrategyBuilder(n, m, k);
                    List<string> hits;
                    try
                    {
                        hits = builder.FindResidualFalseSplits(cap);
                    }
                    catch (Exception ex)
                    {
                        residuals.Add($"{n},{m},{k}: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                        continue;
                    }

                    casesChecked++;
                    foreach (string hit in hits)
                        residuals.Add($"{n},{m},{k} {hit}");
                }

        using (var w = new StreamWriter(path, append: false))
        {
            w.WriteLine($"# Ordered-block honesty sweep (n up to {nMax}, cap {cap})");
            w.WriteLine($"cases checked        = {casesChecked}");
            w.WriteLine($"residual false-splits = {residuals.Count}");
            w.WriteLine();
            if (residuals.Count == 0)
                w.WriteLine("CLEAN: every same-child sibling pair that is parent-automorphism-backed is merged by the detector.");
            else
                foreach (string r in residuals)
                    w.WriteLine(r);
        }

        Assert.True(residuals.Count == 0,
            $"Found {residuals.Count} automorphism-backed sibling pairs still rendered split (see {path}). First: {(residuals.Count > 0 ? residuals[0] : "")}");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TopKFinder.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
