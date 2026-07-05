using System.Diagnostics;
using System.Text;
using Xunit;

// On-demand empirical evaluation of GreedyTighten (Phase 0) against its kill-criteria
// (see docs/core-algorithm.md 4.7): does it tighten U at all, does U' reach opt (the only case
// that lets ProofTighten skip the expensive opt probe), and is its cost negligible?
//
// Gated behind RUN_GREEDY_TIGHTEN_EVAL so it never runs in the normal suite. To run:
//   $env:RUN_GREEDY_TIGHTEN_EVAL = "1"
//   dotnet test TopKFinder.PerfTests\TopKFinder.PerfTests.csproj --filter GreedyTightenEval
// Optional knobs: GT_EVAL_NMAX (default 13), GT_EVAL_REPORT_PATH (default <repo>\greedy-tighten-eval.txt).
public sealed class GreedyTightenEvalTests
{
    [Fact]
    public void GreedyTightenEval()
    {
        if (Environment.GetEnvironmentVariable("RUN_GREEDY_TIGHTEN_EVAL") != "1")
            return;

        int nMax = ReadIntEnv("GT_EVAL_NMAX", 13);
        string reportPath = Environment.GetEnvironmentVariable("GT_EVAL_REPORT_PATH")
            ?? Path.Combine(FindRepoRoot(), "greedy-tighten-eval.txt");

        var rows = new List<Row>();
        for (int n = 5; n <= nMax; n++)
        for (int m = 2; m <= n; m++)
        for (int k = 1; k <= n; k++)
        {
            if (m >= n && k >= n)
                continue;
            if (k > n - k)
                continue; // dual symmetry: opt(n,m,k)==opt(n,m,n-k); keep one side
            if (Program.IsPotentiallySlowSearch(n, m, k))
                continue; // keep the exact opt reference cheap

            var swF = Stopwatch.StartNew();
            int u = new StrategyBuilder(n, m, k).BuildGreedyFeasiblePlan().MaxStep;
            swF.Stop();

            var builder = new StrategyBuilder(n, m, k);
            var swT = Stopwatch.StartNew();
            int up = builder.BuildGreedyTightenPlan().MaxStep;
            swT.Stop();
            int rounds = builder.GreedyTightenRounds;
            int commits = builder.GreedyTightenCommits;

            int opt = new StrategyBuilder(n, m, k).BuildStepProofPlan().MaxStep;

            rows.Add(new Row(n, m, k, u, up, opt, rounds, commits,
                swF.Elapsed.TotalMilliseconds, swT.Elapsed.TotalMilliseconds));
        }

        // Aggregate against the kill-criteria.
        int total = rows.Count;
        int tightened = rows.Count(r => r.Up < r.U);
        var gapCases = rows.Where(r => r.Opt < r.U).ToList();    // room to tighten toward opt
        int touchdowns = gapCases.Count(r => r.Up == r.Opt);     // reached opt (the valuable hit)
        int belowOpt = rows.Count(r => r.Up < r.Opt);            // MUST be 0 (soundness)
        int worseThanU = rows.Count(r => r.Up > r.U);            // MUST be 0 (never worse)
        double tightenMsTotal = rows.Sum(r => r.TightenMs);
        double feasibleMsTotal = rows.Sum(r => r.FeasibleMs);

        var sb = new StringBuilder();
        sb.AppendLine("# GreedyTighten empirical evaluation");
        sb.AppendLine($"cases={total}  tightened(U'<U)={tightened}  gap(opt<U)={gapCases.Count}  " +
                      $"touchdown(U'==opt|gap)={touchdowns}/{gapCases.Count}");
        sb.AppendLine($"soundness: belowOpt={belowOpt} (must be 0)   neverWorse: worseThanU={worseThanU} (must be 0)");
        sb.AppendLine($"cost: total tighten={tightenMsTotal:F0}ms vs feasible={feasibleMsTotal:F0}ms " +
                      $"(ratio {(feasibleMsTotal > 0 ? tightenMsTotal / feasibleMsTotal : 0):F2}x)");
        sb.AppendLine();
        sb.AppendLine("n,m,k | U  U' opt | U'<U U'==opt | rounds commits | feas_ms tight_ms");
        foreach (var r in rows.OrderBy(r => r.N).ThenBy(r => r.M).ThenBy(r => r.K))
        {
            string flags = $"{(r.Up < r.U ? "yes" : "no ")}  {(r.Up == r.Opt ? "yes" : "no ")}";
            sb.AppendLine($"{r.N,2},{r.M,2},{r.K,2} | {r.U,2} {r.Up,2} {r.Opt,3} | {flags} | " +
                          $"{r.Rounds,4} {r.Commits,4} | {r.FeasibleMs,7:F1} {r.TightenMs,7:F1}");
        }

        File.WriteAllText(reportPath, sb.ToString());

        // Hard soundness assertions (these would be real bugs).
        Assert.Equal(0, belowOpt);
        Assert.Equal(0, worseThanU);
    }

    private readonly record struct Row(
        int N, int M, int K, int U, int Up, int Opt, int Rounds, int Commits,
        double FeasibleMs, double TightenMs);

    private static int ReadIntEnv(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : fallback;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TopKFinder.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
