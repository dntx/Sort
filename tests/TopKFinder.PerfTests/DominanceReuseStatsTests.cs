using System.Text;
using TopKFinder;
using Xunit;

// On-demand measurement of the "subset reuse" opportunity: how often a state being solved (S2) has
// an already-solved state (S1) that strictly embeds into it (S1 has strictly less comparison
// information, i.e. S2 is a proper superset) AND S2's true worst-case step count equals S1's. In
// that case S1's optimal strategy is valid and step-optimal on S2, so S2 could be rendered as a
// Reference to S1 instead of expanded on its own -- a sharing opportunity beyond today's
// isomorphism-only dedup (isomorphic states never reach the dominance probe; the canonical-key
// cache catches them first).
//
// Gated behind RUN_DOMINANCE_STATS=1 so it never runs in the normal suite. To run:
//   $env:RUN_DOMINANCE_STATS = "1"
//   dotnet test tests\TopKFinder.PerfTests\TopKFinder.PerfTests.csproj --filter DominanceReuseStats
// Optional knobs: STATS_CASE_TIMEOUT_SECONDS (default 25), STATS_REPORT_PATH
//   (default <repo>\dominance-reuse-report.txt).
public sealed class DominanceReuseStatsTests
{
    // A curated grid of fast, representative shapes (kept off the slow m <= n/3 region).
    private static readonly (int N, int M, int K)[] Cases =
    {
        (6, 2, 2), (7, 3, 3), (8, 3, 3), (9, 3, 3), (9, 4, 4), (10, 4, 4),
        (10, 5, 5), (11, 5, 5), (12, 4, 4), (12, 5, 5), (12, 6, 6), (13, 4, 3),
        (14, 5, 5), (14, 7, 7), (15, 5, 5), (16, 5, 4), (12, 3, 3),
    };

    [Fact]
    public void DominanceReuseStats()
    {
        if (Environment.GetEnvironmentVariable("RUN_DOMINANCE_STATS") != "1")
            return;

        int caseTimeoutSeconds = ReadIntEnv("STATS_CASE_TIMEOUT_SECONDS", 25);
        string reportPath = Environment.GetEnvironmentVariable("STATS_REPORT_PATH")
            ?? Path.Combine(FindRepoRoot(), "dominance-reuse-report.txt");
        var caseTimeout = TimeSpan.FromSeconds(caseTimeoutSeconds);

        var sb = new StringBuilder();
        sb.AppendLine("# Subset-reuse opportunity report (dominance upper-bound tightness)");
        sb.AppendLine();
        sb.AppendLine("Legend:");
        sb.AppendLine("  distinctStates = canonical decision states solved (probes performed)");
        sb.AppendLine("  upperFound     = probes where some earlier solved state strictly embeds (S2 superset of S1)");
        sb.AppendLine("  reusable       = of those, ones where cost(S2) == cost(S1) -> S2 can optimally reuse S1");
        sb.AppendLine("  reuse%         = reusable / distinctStates");
        sb.AppendLine("  unsound/budget = soundness guards (must stay 0)");
        sb.AppendLine();
        sb.AppendLine($"{"n,m,k",-10} {"distinct",8} {"upperFound",11} {"reusable",9} {"reuse%",7} {"lowerRaise",11} {"unsound",8} {"budget",7}");

        int totalDistinct = 0, totalReusable = 0, totalUpperFound = 0, totalUnsound = 0, totalBudget = 0;

        foreach ((int n, int m, int k) in Cases)
        {
            StrategyBuilder builder;
            try
            {
                builder = RunWithTimeout(caseTimeout, ct =>
                {
                    var b = new StrategyBuilder(n, m, k, ct) { EnableDominanceMetric = true };
                    b.ExecuteStepProofStage();
                    return b;
                });
            }
            catch (TimeoutException)
            {
                sb.AppendLine($"{$"{n},{m},{k}",-10} {"timeout",8}");
                continue;
            }

            int distinct = builder.DominanceProbes;
            int upperFound = builder.DominanceUpperFound;
            int reusable = builder.DominanceUpperTight;
            int lowerRaise = builder.DominanceBoundRaises;
            int unsound = builder.DominanceUnsoundObservations;
            int budget = builder.DominanceBudgetExhaustions;
            double pct = distinct > 0 ? 100.0 * reusable / distinct : 0.0;

            totalDistinct += distinct;
            totalReusable += reusable;
            totalUpperFound += upperFound;
            totalUnsound += unsound;
            totalBudget += budget;

            sb.AppendLine(
                $"{$"{n},{m},{k}",-10} {distinct,8} {upperFound,11} {reusable,9} {pct,6:F1}% {lowerRaise,11} {unsound,8} {budget,7}");
        }

        sb.AppendLine();
        double totalPct = totalDistinct > 0 ? 100.0 * totalReusable / totalDistinct : 0.0;
        sb.AppendLine(
            $"TOTAL: distinct={totalDistinct} upperFound={totalUpperFound} reusable={totalReusable} " +
            $"reuse%={totalPct:F1}% unsound={totalUnsound} budget={totalBudget}");

        File.WriteAllText(reportPath, sb.ToString());

        // Soundness must hold; the reuse percentage is informational only.
        Assert.Equal(0, totalUnsound);
        Assert.Equal(0, totalBudget);
    }

    private static int ReadIntEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int v) && v > 0 ? v : fallback;
    }

    private static T RunWithTimeout<T>(TimeSpan timeout, Func<CancellationToken, T> action)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return action(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException();
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
