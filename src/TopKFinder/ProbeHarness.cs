using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TopKFinder;

public sealed record ProbeCase(string CaseName, int N, int M, int K);

public sealed record ProbeResult(
    string CaseName,
    string Mode,
    int FeasibleUpper,
    int Budget,
    double ProofMs,
    double GtMs,
    string Outcome);

public sealed record ProbeSummary(
    string CaseName,
    ProbeResult Baseline,
    ProbeResult Seeded,
    double ProofDeltaMs,
    double NetDeltaMs)
{
    public static string CsvHeader()
        => string.Join(",",
            "case",
            "baseline_upper",
            "seeded_upper",
            "baseline_budget",
            "seeded_budget",
            "baseline_proof_ms",
            "seeded_proof_ms",
            "proof_delta_ms",
            "seeded_gt_ms",
            "net_delta_ms");

    public string ToCsvRow()
        => string.Join(",",
            Escape(CaseName),
            Baseline.FeasibleUpper,
            Seeded.FeasibleUpper,
            Baseline.Budget,
            Seeded.Budget,
            Baseline.ProofMs.ToString("F3", CultureInfo.InvariantCulture),
            Seeded.ProofMs.ToString("F3", CultureInfo.InvariantCulture),
            ProofDeltaMs.ToString("F3", CultureInfo.InvariantCulture),
            Seeded.GtMs.ToString("F3", CultureInfo.InvariantCulture),
            NetDeltaMs.ToString("F3", CultureInfo.InvariantCulture));

    private static string Escape(string value)
        => value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}

public sealed class ProbeHarness
{
    public ProbeSummary Compare(string caseName, int n, int m, int k)
    {
        ProbeResult baseline = RunBaseline(caseName, n, m, k);
        ProbeResult seeded = RunSeeded(caseName, n, m, k);

        double proofDeltaMs = seeded.ProofMs - baseline.ProofMs;
        double netDeltaMs = seeded.GtMs + seeded.ProofMs - baseline.ProofMs;
        return new ProbeSummary(caseName, baseline, seeded, proofDeltaMs, netDeltaMs);
    }

    public IReadOnlyList<ProbeSummary> CompareMany(IEnumerable<ProbeCase> cases)
        => cases.Select(c => Compare(c.CaseName, c.N, c.M, c.K)).ToList();

    public void WriteCsv(string path, IEnumerable<ProbeSummary> summaries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        using var writer = new StreamWriter(path, false);
        writer.WriteLine(ProbeSummary.CsvHeader());
        foreach (ProbeSummary summary in summaries)
            writer.WriteLine(summary.ToCsvRow());
    }

    private static ProbeResult RunBaseline(string caseName, int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k);
        StrategyPlan upper = builder.ExecuteGreedyFeasibleStage();
        int budget = Math.Max(1, upper.MaxStep - 1);

        var proofStopwatch = Stopwatch.StartNew();
        StageResult proofStage = builder.ExecuteProofTightenStageWithSolution(budget).Result;
        proofStopwatch.Stop();
        StrategyPlan proofPlan = proofStage.MaterializedPlan ?? upper;

        return new ProbeResult(
            caseName,
            "baseline",
            upper.MaxStep,
            budget,
            proofStopwatch.Elapsed.TotalMilliseconds,
            0.0,
            proofPlan.MaxStep.ToString());
    }

    private static ProbeResult RunSeeded(string caseName, int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k);
        StrategyPlan upper = builder.ExecuteGreedyFeasibleStage();
        int budget = Math.Max(1, upper.MaxStep - 1);

        double gtMs = 0.0;
        if (builder.ShouldRunGreedyTightenByRootProbe())
        {
            var gtStopwatch = Stopwatch.StartNew();
            StrategyPlan tightened = builder.ExecuteGreedyTightenStage();
            gtStopwatch.Stop();
            gtMs = gtStopwatch.Elapsed.TotalMilliseconds;
            if (tightened.MaxStep < upper.MaxStep)
                budget = Math.Max(1, tightened.MaxStep - 1);
        }

        var proofStopwatch = Stopwatch.StartNew();
        StageResult proofStage = builder.ExecuteProofTightenStageWithSolution(budget).Result;
        proofStopwatch.Stop();
        StrategyPlan proofPlan = proofStage.MaterializedPlan ?? upper;

        return new ProbeResult(
            caseName,
            "seeded",
            upper.MaxStep,
            budget,
            proofStopwatch.Elapsed.TotalMilliseconds,
            gtMs,
            proofPlan.MaxStep.ToString());
    }
}
