using System.Globalization;
using System.IO;
using System.Linq;
using TopKFinder;
using Xunit;
using Xunit.Abstractions;

// Focused experiment for the key product question:
// does running greedy-tighten first make the FOLLOWING proof-tighten stage faster enough to matter?
//
// Enable:
//   $env:RUN_GT_SEED_IMPACT = "1"
//   dotnet test tests\TopKFinder.PerfTests\TopKFinder.PerfTests.csproj --filter GreedyTightenSeedImpactPerfTests
//
// Optional knobs:
//   GT_SEED_IMPACT_TIMEOUT_SECONDS  (default 180)
//   GT_SEED_IMPACT_WARMUP_RUNS      (default 1)
//   GT_SEED_IMPACT_MEASURED_RUNS    (default 3)
//   GT_SEED_IMPACT_CASES            (default "10,2,5;12,4,4;20,2,6")
//   GT_SEED_IMPACT_REPORT_PATH      (default <repo>\artifacts\gt-seed-impact-report.csv)
//   GT_SEED_IMPACT_FORCE_GT         (default 0; when 1, always execute GT before proof)
public sealed class GreedyTightenSeedImpactPerfTests
{
    private readonly ITestOutputHelper _output;

    public GreedyTightenSeedImpactPerfTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed record CaseSpec(int N, int M, int K);

    private sealed record Observation(
        bool ProbeRun,
        bool GtExecuted,
        bool GtImproved,
        int BaselineBudget,
        int SeededBudget,
        StageOutcome BaselineOutcome,
        StageOutcome SeededOutcome,
        double BaselineProofMs,
        double SeededProofMs,
        double SeededGtMs,
        double BaselinePostFeasibleMs,
        double SeededPostFeasibleMs);

    private sealed record Row(
        string Case,
        bool ForcedGtMode,
        bool ProbeRun,
        bool GtExecuted,
        bool GtImproved,
        int BaselineBudget,
        int SeededBudget,
        string BaselineOutcome,
        string SeededOutcome,
        double BaselineProofMedianMs,
        double SeededProofMedianMs,
        double ProofDeltaMs,
        double SeededGtMedianMs,
        double BaselinePostFeasibleMedianMs,
        double SeededPostFeasibleMedianMs,
        double PostFeasibleDeltaMs,
        string ProofBandShift,
        string PostFeasibleBandShift,
        string BaselineProofSamplesMs,
        string SeededProofSamplesMs,
        string SeededGtSamplesMs,
        string BaselinePostFeasibleSamplesMs,
        string SeededPostFeasibleSamplesMs);

    [Fact]
    public void GreedyTightenSeedImpact_Report()
    {
        if (Environment.GetEnvironmentVariable("RUN_GT_SEED_IMPACT") != "1")
            return;

        int timeoutSeconds = ReadPositiveIntEnv("GT_SEED_IMPACT_TIMEOUT_SECONDS", 180);
        int warmupRuns = ReadNonNegativeIntEnv("GT_SEED_IMPACT_WARMUP_RUNS", 1);
        int measuredRuns = ReadPositiveIntEnv("GT_SEED_IMPACT_MEASURED_RUNS", 3);
        string rawCases = ReadStringEnv("GT_SEED_IMPACT_CASES", "10,2,5;12,4,4;20,2,6");
        bool forceGt = ReadBoolEnv("GT_SEED_IMPACT_FORCE_GT", fallback: false);
        string reportPath = ReadStringEnv(
            "GT_SEED_IMPACT_REPORT_PATH",
            Path.Combine(FindRepoRoot(), "artifacts", "gt-seed-impact-report.csv"));

        List<CaseSpec> cases = ParseCases(rawCases);
        Assert.NotEmpty(cases);

        var rows = new List<Row>(cases.Count);
        foreach (CaseSpec c in cases)
        {
            for (int i = 0; i < warmupRuns; i++)
            {
                _ = MeasureOnce(c, timeoutSeconds, $"warmup-{i + 1}", seededFirst: (i % 2) == 1, forceGt);
            }

            var baselineProofSamples = new List<double>(measuredRuns);
            var seededProofSamples = new List<double>(measuredRuns);
            var seededGtSamples = new List<double>(measuredRuns);
            var baselinePostFeasibleSamples = new List<double>(measuredRuns);
            var seededPostFeasibleSamples = new List<double>(measuredRuns);
            Observation? last = null;

            for (int i = 0; i < measuredRuns; i++)
            {
                Observation obs = MeasureOnce(c, timeoutSeconds, $"measure-{i + 1}", seededFirst: (i % 2) == 1, forceGt);
                last = obs;

                baselineProofSamples.Add(obs.BaselineProofMs);
                seededProofSamples.Add(obs.SeededProofMs);
                seededGtSamples.Add(obs.SeededGtMs);
                baselinePostFeasibleSamples.Add(obs.BaselinePostFeasibleMs);
                seededPostFeasibleSamples.Add(obs.SeededPostFeasibleMs);
            }

            double baselineProofMedian = Median(baselineProofSamples);
            double seededProofMedian = Median(seededProofSamples);
            double seededGtMedian = Median(seededGtSamples);
            double baselinePostFeasibleMedian = Median(baselinePostFeasibleSamples);
            double seededPostFeasibleMedian = Median(seededPostFeasibleSamples);

            double proofDelta = seededProofMedian - baselineProofMedian;
            double postFeasibleDelta = seededPostFeasibleMedian - baselinePostFeasibleMedian;

            Row row = new(
                Case: $"{c.N},{c.M},{c.K}",
                ForcedGtMode: forceGt,
                ProbeRun: last?.ProbeRun ?? false,
                GtExecuted: last?.GtExecuted ?? false,
                GtImproved: last?.GtImproved ?? false,
                BaselineBudget: last?.BaselineBudget ?? -1,
                SeededBudget: last?.SeededBudget ?? -1,
                BaselineOutcome: (last?.BaselineOutcome ?? StageOutcome.Incomplete).ToString(),
                SeededOutcome: (last?.SeededOutcome ?? StageOutcome.Incomplete).ToString(),
                BaselineProofMedianMs: Round3(baselineProofMedian),
                SeededProofMedianMs: Round3(seededProofMedian),
                ProofDeltaMs: Round3(proofDelta),
                SeededGtMedianMs: Round3(seededGtMedian),
                BaselinePostFeasibleMedianMs: Round3(baselinePostFeasibleMedian),
                SeededPostFeasibleMedianMs: Round3(seededPostFeasibleMedian),
                PostFeasibleDeltaMs: Round3(postFeasibleDelta),
                ProofBandShift: $"{ToBand(baselineProofMedian)}->{ToBand(seededProofMedian)}",
                PostFeasibleBandShift: $"{ToBand(baselinePostFeasibleMedian)}->{ToBand(seededPostFeasibleMedian)}",
                BaselineProofSamplesMs: JoinSamples(baselineProofSamples),
                SeededProofSamplesMs: JoinSamples(seededProofSamples),
                SeededGtSamplesMs: JoinSamples(seededGtSamples),
                BaselinePostFeasibleSamplesMs: JoinSamples(baselinePostFeasibleSamples),
                SeededPostFeasibleSamplesMs: JoinSamples(seededPostFeasibleSamples));

            rows.Add(row);

            _output.WriteLine(
                $"GTSeedImpact case={row.Case} forcedGt={row.ForcedGtMode} gateHit={row.ProbeRun} gtExecuted={row.GtExecuted} gtImproved={row.GtImproved} " +
                $"budget(base={row.BaselineBudget},seeded={row.SeededBudget}) " +
                $"proof(base={row.BaselineProofMedianMs:F3}ms,seeded={row.SeededProofMedianMs:F3}ms,delta={row.ProofDeltaMs:F3}ms,{row.ProofBandShift}) " +
                $"postFeasible(base={row.BaselinePostFeasibleMedianMs:F3}ms,seeded={row.SeededPostFeasibleMedianMs:F3}ms,delta={row.PostFeasibleDeltaMs:F3}ms,{row.PostFeasibleBandShift})");
        }

        WriteReport(reportPath, rows);
        Assert.NotEmpty(rows);
    }

    private static Observation MeasureOnce(CaseSpec c, int timeoutSeconds, string tag, bool seededFirst, bool forceGt)
    {
        return TestTimeoutHelper.RunWithTimeout(
            $"gt-seed-impact {c.N},{c.M},{c.K} {tag}",
            TimeSpan.FromSeconds(timeoutSeconds),
            cancellationToken =>
            {
                ObservationSeed seeded;
                ObservationBaseline baseline;

                if (seededFirst)
                {
                    seeded = MeasureSeededPath(c, cancellationToken, forceGt);
                    baseline = MeasureBaselinePath(c, cancellationToken);
                }
                else
                {
                    baseline = MeasureBaselinePath(c, cancellationToken);
                    seeded = MeasureSeededPath(c, cancellationToken, forceGt);
                }

                double baselineProofMs = baseline.Proof.Elapsed.TotalMilliseconds;
                double seededProofMs = seeded.Proof.Elapsed.TotalMilliseconds;
                double seededGtMs = seeded.GtMs;
                double baselinePostFeasibleMs = baselineProofMs;
                double seededPostFeasibleMs = seededGtMs + seededProofMs;

                return new Observation(
                    ProbeRun: seeded.ProbeRun,
                    GtExecuted: seeded.GtExecuted,
                    GtImproved: seeded.GtImproved,
                    BaselineBudget: baseline.Budget,
                    SeededBudget: seeded.Budget,
                    BaselineOutcome: baseline.Proof.Outcome,
                    SeededOutcome: seeded.Proof.Outcome,
                    BaselineProofMs: baselineProofMs,
                    SeededProofMs: seededProofMs,
                    SeededGtMs: seededGtMs,
                    BaselinePostFeasibleMs: baselinePostFeasibleMs,
                    SeededPostFeasibleMs: seededPostFeasibleMs);
            });
    }

    private sealed record ObservationBaseline(int Budget, StageResult Proof);

    private sealed record ObservationSeed(bool ProbeRun, bool GtExecuted, bool GtImproved, int Budget, StageResult Proof, double GtMs);

    private static ObservationBaseline MeasureBaselinePath(CaseSpec c, CancellationToken cancellationToken)
    {
        var baselineBuilder = new StrategyBuilder(c.N, c.M, c.K, cancellationToken);
        StrategyPlan baselineFeasible = baselineBuilder.ExecuteGreedyFeasibleStage();
        int baselineBudget = baselineFeasible.MaxStep - 1;
        StageResult baselineProof = baselineBuilder.ExecuteProofTightenStage(baselineBudget);
        return new ObservationBaseline(baselineBudget, baselineProof);
    }

    private static ObservationSeed MeasureSeededPath(CaseSpec c, CancellationToken cancellationToken, bool forceGt)
    {
        var seededBuilder = new StrategyBuilder(c.N, c.M, c.K, cancellationToken);
        StrategyPlan seededFeasible = seededBuilder.ExecuteGreedyFeasibleStage();

        var gtStopwatch = System.Diagnostics.Stopwatch.StartNew();
        bool probeRun = seededBuilder.ShouldRunGreedyTightenByRootProbe();
        bool gtExecuted = forceGt || probeRun;
        bool gtImproved = false;
        int seededBudget = seededFeasible.MaxStep - 1;
        if (gtExecuted)
        {
            StrategyPlan gtPlan = seededBuilder.ExecuteGreedyTightenStage();
            gtImproved = gtPlan.MaxStep < seededFeasible.MaxStep;
            if (gtImproved)
            {
                seededBuilder.OverrideGreedyPipelineUpperBound(gtPlan.MaxStep);
                seededBudget = gtPlan.MaxStep - 1;
            }
        }

        gtStopwatch.Stop();
        StageResult seededProof = seededBuilder.ExecuteProofTightenStage(seededBudget);
        return new ObservationSeed(probeRun, gtExecuted, gtImproved, seededBudget, seededProof, gtStopwatch.Elapsed.TotalMilliseconds);
    }

    private static List<CaseSpec> ParseCases(string raw)
    {
        var list = new List<CaseSpec>();
        foreach (string entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = entry.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 3)
                throw new InvalidDataException($"Invalid case spec '{entry}'. Expected n,m,k.");

            list.Add(new CaseSpec(
                int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture),
                int.Parse(parts[2], CultureInfo.InvariantCulture)));
        }

        return list;
    }

    private static void WriteReport(string path, List<Row> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        using var writer = new StreamWriter(path, false);
        writer.WriteLine(
            "Case,ForcedGtMode,ProbeRun,GtExecuted,GtImproved,BaselineBudget,SeededBudget,BaselineOutcome,SeededOutcome," +
            "BaselineProofMedianMs,SeededProofMedianMs,ProofDeltaMs,SeededGtMedianMs," +
            "BaselinePostFeasibleMedianMs,SeededPostFeasibleMedianMs,PostFeasibleDeltaMs," +
            "ProofBandShift,PostFeasibleBandShift,BaselineProofSamplesMs,SeededProofSamplesMs," +
            "SeededGtSamplesMs,BaselinePostFeasibleSamplesMs,SeededPostFeasibleSamplesMs");

        foreach (Row row in rows)
        {
            writer.WriteLine(string.Join(",",
                QuoteCsv(row.Case),
                row.ForcedGtMode,
                row.ProbeRun,
                row.GtExecuted,
                row.GtImproved,
                row.BaselineBudget.ToString(CultureInfo.InvariantCulture),
                row.SeededBudget.ToString(CultureInfo.InvariantCulture),
                row.BaselineOutcome,
                row.SeededOutcome,
                row.BaselineProofMedianMs.ToString(CultureInfo.InvariantCulture),
                row.SeededProofMedianMs.ToString(CultureInfo.InvariantCulture),
                row.ProofDeltaMs.ToString(CultureInfo.InvariantCulture),
                row.SeededGtMedianMs.ToString(CultureInfo.InvariantCulture),
                row.BaselinePostFeasibleMedianMs.ToString(CultureInfo.InvariantCulture),
                row.SeededPostFeasibleMedianMs.ToString(CultureInfo.InvariantCulture),
                row.PostFeasibleDeltaMs.ToString(CultureInfo.InvariantCulture),
                row.ProofBandShift,
                row.PostFeasibleBandShift,
                QuoteCsv(row.BaselineProofSamplesMs),
                QuoteCsv(row.SeededProofSamplesMs),
                QuoteCsv(row.SeededGtSamplesMs),
                QuoteCsv(row.BaselinePostFeasibleSamplesMs),
                QuoteCsv(row.SeededPostFeasibleSamplesMs)));
        }
    }

    private static string QuoteCsv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

    private static string JoinSamples(List<double> samples)
        => string.Join(';', samples.Select(v => Round3(v).ToString(CultureInfo.InvariantCulture)));

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            throw new InvalidOperationException("Cannot compute median for empty samples.");

        List<double> sorted = values.OrderBy(v => v).ToList();
        int n = sorted.Count;
        return n % 2 == 1
            ? sorted[n / 2]
            : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    private static string ToBand(double ms)
    {
        if (ms < 100) return "sub-100ms";
        if (ms < 1000) return "100ms-1s";
        if (ms < 10000) return "1s-10s";
        if (ms < 100000) return "10s-100s";
        return "100s+";
    }

    private static double Round3(double value)
        => Math.Round(value, 3);

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

    private static string ReadStringEnv(string name, string fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
    }

    private static bool ReadBoolEnv(string name, bool fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        return raw is "1" or "true" or "True" or "TRUE";
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "TopKFinder", "TopKFinder.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}