using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace TopKFinder.PerfTests;

// On-demand experiment focused on one question:
// Does running GreedyTighten FIRST make the FOLLOWING proof-tighten probe faster enough to justify GT cost?
//
// Enable:
//   $env:RUN_GT_PROOF_SEED_EXPERIMENT = "1"
//   dotnet test tests\TopKFinder.PerfTests\TopKFinder.PerfTests.csproj --filter GreedyTightenProofSeedingExperimentTests
//
// Optional knobs:
//   GT_PROOF_SEED_CASES             (default "10,2,5;12,4,4")
//   GT_PROOF_SEED_TIMEOUT_SECONDS   (default 90)
//   GT_PROOF_SEED_WARMUP_RUNS       (default 0)
//   GT_PROOF_SEED_MEASURED_RUNS     (default 3)
//   GT_PROOF_SEED_REPORT_PATH       (default <repo>\artifacts\gt-proof-seeding-report.csv)
public sealed class GreedyTightenProofSeedingExperimentTests
{
    private readonly ITestOutputHelper _output;

    public GreedyTightenProofSeedingExperimentTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed record CaseSpec(int N, int M, int K)
    {
        public override string ToString() => $"{N},{M},{K}";
    }

    private sealed record ProbeRun(
        int FeasibleU,
        int? GtU,
        bool GtImproved,
        int Budget,
        StageOutcome Outcome,
        double ProofMs,
        double GtMs,
        int? Searched,
        int? Outcomes,
        int? Candidates);

    private sealed record ExperimentRow(
        string Case,
        double BaselineProofMedianMs,
        double SeededProofMedianMs,
        double GtMedianMs,
        double NetDeltaMedianMs,
        double ProofDeltaMedianMs,
        double ProofSpeedupRatio,
        double AvgBudgetDrop,
        string BaselineBand,
        string SeededBand,
        string NetBandImpact,
        string Notes);

    [Fact]
    public void GtSeeding_ProofTighten_Experiment()
    {
        if (Environment.GetEnvironmentVariable("RUN_GT_PROOF_SEED_EXPERIMENT") != "1")
            return;

        string casesRaw = ReadStringEnv("GT_PROOF_SEED_CASES", "10,2,5;12,4,4");
        int timeoutSeconds = ReadPositiveIntEnv("GT_PROOF_SEED_TIMEOUT_SECONDS", 90);
        int warmupRuns = ReadNonNegativeIntEnv("GT_PROOF_SEED_WARMUP_RUNS", 0);
        int measuredRuns = ReadPositiveIntEnv("GT_PROOF_SEED_MEASURED_RUNS", 3);
        string reportPath = ReadStringEnv(
            "GT_PROOF_SEED_REPORT_PATH",
            Path.Combine(FindRepoRoot(), "artifacts", "gt-proof-seeding-report.csv"));

        List<CaseSpec> cases = ParseCases(casesRaw);
        Assert.NotEmpty(cases);

        var rows = new List<ExperimentRow>();

        foreach (CaseSpec shape in cases)
        {
            var baselineProof = new List<double>();
            var seededProof = new List<double>();
            var seededGt = new List<double>();
            var budgetDrops = new List<int>();
            var baselineOutcomes = new List<string>();
            var seededOutcomes = new List<string>();

            for (int i = 0; i < warmupRuns; i++)
            {
                _ = MeasureBaseline(shape, timeoutSeconds);
                _ = MeasureSeeded(shape, timeoutSeconds);
            }

            for (int i = 0; i < measuredRuns; i++)
            {
                ProbeRun baseRun = MeasureBaseline(shape, timeoutSeconds);
                ProbeRun seedRun = MeasureSeeded(shape, timeoutSeconds);

                baselineProof.Add(baseRun.ProofMs);
                seededProof.Add(seedRun.ProofMs);
                seededGt.Add(seedRun.GtMs);
                budgetDrops.Add(baseRun.Budget - seedRun.Budget);
                baselineOutcomes.Add(baseRun.Outcome.ToString());
                seededOutcomes.Add(seedRun.Outcome.ToString());

                _output.WriteLine(
                    $"case={shape} run={i + 1}/{measuredRuns} " +
                    $"baseline: U={baseRun.FeasibleU}, budget={baseRun.Budget}, outcome={baseRun.Outcome}, proofMs={baseRun.ProofMs:F3}; " +
                    $"seeded: feasibleU={seedRun.FeasibleU}, gtU={(seedRun.GtU.HasValue ? seedRun.GtU.Value.ToString(CultureInfo.InvariantCulture) : "n/a")}, " +
                    $"improved={seedRun.GtImproved}, budget={seedRun.Budget}, outcome={seedRun.Outcome}, gtMs={seedRun.GtMs:F3}, proofMs={seedRun.ProofMs:F3}");
            }

            double baselineMedian = Median(baselineProof);
            double seededMedian = Median(seededProof);
            double gtMedian = Median(seededGt);
            double proofDelta = seededMedian - baselineMedian;
            double netDelta = seededMedian + gtMedian - baselineMedian;
            double speedupRatio = seededMedian > 0 ? baselineMedian / seededMedian : double.NaN;
            double avgBudgetDrop = budgetDrops.Average();

            string baselineBand = ToPerceptualBand(baselineMedian);
            string seededBand = ToPerceptualBand(seededMedian);
            string netBandImpact = DescribeNetImpact(netDelta);

            string notes = $"baselineOutcomes={string.Join("/", baselineOutcomes)};seededOutcomes={string.Join("/", seededOutcomes)}";

            rows.Add(new ExperimentRow(
                shape.ToString(),
                Math.Round(baselineMedian, 3),
                Math.Round(seededMedian, 3),
                Math.Round(gtMedian, 3),
                Math.Round(netDelta, 3),
                Math.Round(proofDelta, 3),
                Math.Round(speedupRatio, 3),
                Math.Round(avgBudgetDrop, 3),
                baselineBand,
                seededBand,
                netBandImpact,
                notes));
        }

        WriteReport(reportPath, rows);

        foreach (ExperimentRow row in rows)
        {
            _output.WriteLine(
                $"summary case={row.Case} " +
                $"proofBaseline={row.BaselineProofMedianMs:F3}ms({row.BaselineBand}) " +
                $"proofSeeded={row.SeededProofMedianMs:F3}ms({row.SeededBand}) " +
                $"gt={row.GtMedianMs:F3}ms netDelta={row.NetDeltaMedianMs:F3}ms impact={row.NetBandImpact} " +
                $"proofSpeedup={row.ProofSpeedupRatio:F3}x budgetDrop={row.AvgBudgetDrop:F3}");
        }

        Assert.NotEmpty(rows);
    }

    private static ProbeRun MeasureBaseline(CaseSpec shape, int timeoutSeconds)
    {
        return TestTimeoutHelper.RunWithTimeout(
            $"baseline proof probe {shape}",
            TimeSpan.FromSeconds(timeoutSeconds),
            cancellationToken =>
            {
                var builder = new StrategyBuilder(shape.N, shape.M, shape.K, cancellationToken);
                StrategyPlan feasible = builder.ExecuteGreedyFeasibleStage();
                int budget = Math.Max(1, feasible.MaxStep - 1);
                StageResult proof = builder.ExecuteProofTightenStage(budget);

                return new ProbeRun(
                    FeasibleU: feasible.MaxStep,
                    GtU: null,
                    GtImproved: false,
                    Budget: budget,
                    Outcome: proof.Outcome,
                    ProofMs: proof.Elapsed.TotalMilliseconds,
                    GtMs: 0,
                    Searched: proof.MaterializedPlan?.SearchStatistics.SearchedStates,
                    Outcomes: proof.MaterializedPlan?.SearchStatistics.OutcomesConstructed,
                    Candidates: proof.MaterializedPlan?.SearchStatistics.CandidateGroupsEnumerated);
            });
    }

    private static ProbeRun MeasureSeeded(CaseSpec shape, int timeoutSeconds)
    {
        return TestTimeoutHelper.RunWithTimeout(
            $"seeded proof probe {shape}",
            TimeSpan.FromSeconds(timeoutSeconds),
            cancellationToken =>
            {
                var builder = new StrategyBuilder(shape.N, shape.M, shape.K, cancellationToken);
                StrategyPlan feasible = builder.ExecuteGreedyFeasibleStage();

                StrategyPlan gt = builder.ExecuteGreedyTightenStage();
                bool improved = gt.IsStrictRefinementOver(feasible);
                int seedU = improved ? gt.MaxStep : feasible.MaxStep;
                int budget = Math.Max(1, seedU - 1);

                StageResult proof = builder.ExecuteProofTightenStage(budget);

                return new ProbeRun(
                    FeasibleU: feasible.MaxStep,
                    GtU: gt.MaxStep,
                    GtImproved: improved,
                    Budget: budget,
                    Outcome: proof.Outcome,
                    ProofMs: proof.Elapsed.TotalMilliseconds,
                    GtMs: gt.Elapsed.TotalMilliseconds,
                    Searched: proof.MaterializedPlan?.SearchStatistics.SearchedStates,
                    Outcomes: proof.MaterializedPlan?.SearchStatistics.OutcomesConstructed,
                    Candidates: proof.MaterializedPlan?.SearchStatistics.CandidateGroupsEnumerated);
            });
    }

    private static List<CaseSpec> ParseCases(string raw)
    {
        var cases = new List<CaseSpec>();
        foreach (string token in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = token.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 3)
                throw new InvalidDataException($"Invalid case '{token}', expected n,m,k.");

            if (!int.TryParse(parts[0], out int n)
                || !int.TryParse(parts[1], out int m)
                || !int.TryParse(parts[2], out int k))
            {
                throw new InvalidDataException($"Invalid case numbers in '{token}'.");
            }

            cases.Add(new CaseSpec(n, m, k));
        }

        return cases;
    }

    private static void WriteReport(string path, List<ExperimentRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        using var writer = new StreamWriter(path, false);
        writer.WriteLine("Case,BaselineProofMedianMs,SeededProofMedianMs,GtMedianMs,NetDeltaMedianMs,ProofDeltaMedianMs,ProofSpeedupRatio,AvgBudgetDrop,BaselineBand,SeededBand,NetBandImpact,Notes");
        foreach (ExperimentRow row in rows)
        {
            writer.WriteLine(string.Join(",",
                row.Case,
                row.BaselineProofMedianMs.ToString(CultureInfo.InvariantCulture),
                row.SeededProofMedianMs.ToString(CultureInfo.InvariantCulture),
                row.GtMedianMs.ToString(CultureInfo.InvariantCulture),
                row.NetDeltaMedianMs.ToString(CultureInfo.InvariantCulture),
                row.ProofDeltaMedianMs.ToString(CultureInfo.InvariantCulture),
                row.ProofSpeedupRatio.ToString(CultureInfo.InvariantCulture),
                row.AvgBudgetDrop.ToString(CultureInfo.InvariantCulture),
                row.BaselineBand,
                row.SeededBand,
                row.NetBandImpact,
                row.Notes));
        }
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            throw new InvalidOperationException("Cannot compute median of empty sample list.");

        values.Sort();
        int n = values.Count;
        return n % 2 == 1
            ? values[n / 2]
            : (values[n / 2 - 1] + values[n / 2]) / 2.0;
    }

    private static string ToPerceptualBand(double ms)
    {
        if (ms < 100) return "sub-100ms";
        if (ms < 1000) return "100ms-1s";
        if (ms < 10_000) return "1s-10s";
        if (ms < 100_000) return "10s-100s";
        return "100s+";
    }

    private static string DescribeNetImpact(double netDeltaMs)
    {
        // Focus on perceptible change: >=1s is meaningful for interactive waiting,
        // >=10s and >=60s are major user-experience shifts.
        double abs = Math.Abs(netDeltaMs);
        if (abs < 1000) return "minor(<1s)";
        if (abs < 10_000) return netDeltaMs < 0 ? "improved(1s-10s)" : "regressed(1s-10s)";
        if (abs < 60_000) return netDeltaMs < 0 ? "improved(10s-60s)" : "regressed(10s-60s)";
        return netDeltaMs < 0 ? "improved(60s+)" : "regressed(60s+)";
    }

    private static string ReadStringEnv(string name, string fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
    }

    private static int ReadPositiveIntEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : fallback;
    }

    private static int ReadNonNegativeIntEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int parsed) && parsed >= 0 ? parsed : fallback;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "TopKFinder", "TopKFinder.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
