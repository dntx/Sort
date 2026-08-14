using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using CsvHelper;
using CsvHelper.Configuration;
using Xunit;
using TopKFinder;

// Nightly matrix gate for representative performance coverage across exact, greedy, greedy-tighten,
// proof-tighten, and edge-compact stages. This is intentionally broader than the single-case
// proof-tighten smoke gate: it is meant to catch stage-specific regressions and stage-selection
// regressions (e.g. greedy-tighten no longer runs, or proof-tighten starts from a worse budget).
//
// Enable:
//   $env:RUN_STRATEGY_MATRIX = "1"
//   dotnet test tests\TopKFinder.PerfTests\TopKFinder.PerfTests.csproj --filter StrategyMatrixTests
//
// Optional knobs:
//   STRATEGY_MATRIX_TIMEOUT_SECONDS        (default 180)
//   STRATEGY_MATRIX_WARMUP_RUNS            (default 1)
//   STRATEGY_MATRIX_MEASURED_RUNS          (default 3)
//   STRATEGY_MATRIX_REGRESSION_PERCENT     (default 20)
//   STRATEGY_MATRIX_CASE_SET               (default full; smoke skips the heaviest rows)
//   STRATEGY_MATRIX_EXCLUDE_KEYS           (default empty; semicolon/newline-separated matrix keys to exclude)
//   STRATEGY_MATRIX_BASELINE_PATH          (default .\scripts\strategy-matrix-baseline.csv if present)
//   STRATEGY_MATRIX_BASELINE_ONLY          (default 0; when 1, writes current results and exits)
//   STRATEGY_MATRIX_REPORT_PATH           (default <repo>\strategy-matrix-report.csv)
//
// Baseline format:
//   CSV with one or more rows per matrix key. When multiple rows exist for a key, the comparison
//   uses the median of the baseline rows, which makes the file behave like a rolling accepted
//   history rather than a single noisy sample.
public sealed class StrategyMatrixTests
{
    private sealed record MatrixEntry(
        string Key,
        string Mode,
        string Stage,
        int N,
        int M,
        int K,
        Func<CancellationToken, MatrixObservation> Measure);

    private sealed record MatrixObservation(
        string Outcome,
        bool HasPlan,
        int MaxStep,
        int TotalBranchEdges,
        int SearchedStates,
        int OutcomesConstructed,
        int CandidateGroupsEnumerated,
        int RootProvenLowerBound,
        double ElapsedMilliseconds);

    private sealed record MatrixRow(
        string Key,
        string Mode,
        string Stage,
        int N,
        int M,
        int K,
        string Outcome,
        bool HasPlan,
        int MaxStep,
        int TotalBranchEdges,
        int SearchedStates,
        int OutcomesConstructed,
        int CandidateGroupsEnumerated,
        int RootProvenLowerBound,
        double MedianMilliseconds,
        double AverageMilliseconds,
        string Status,
        string Samples);

    private sealed class StrategyMatrixCsvRow
    {
        public string Key { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string Stage { get; set; } = string.Empty;
        public int N { get; set; }
        public int M { get; set; }
        public int K { get; set; }
        public string Outcome { get; set; } = string.Empty;
        public bool HasPlan { get; set; }
        public int MaxStep { get; set; }
        public int TotalBranchEdges { get; set; }
        public int SearchedStates { get; set; }
        public int OutcomesConstructed { get; set; }
        public int CandidateGroupsEnumerated { get; set; }
        public int RootProvenLowerBound { get; set; }
        public double MedianMilliseconds { get; set; }
        public double AverageMilliseconds { get; set; }
        public string Status { get; set; } = string.Empty;
        public string Samples { get; set; } = string.Empty;
    }

    [Fact]
    public void BaselineCsvRoundTripsQuotedKeyAndSamples()
    {
        string path = Path.Combine(Path.GetTempPath(), $"strategy-matrix-baseline-{Guid.NewGuid():N}.csv");

        try
        {
            File.WriteAllText(path,
                "Key,Mode,Stage,N,M,K,Outcome,HasPlan,MaxStep,TotalBranchEdges,SearchedStates,OutcomesConstructed,CandidateGroupsEnumerated,RootProvenLowerBound,MedianMilliseconds,AverageMilliseconds,Status,Samples\n" +
                "\"exact-step:6,2,2\",exact,step-proof,6,2,2,Resolved,True,7,6,21,72,85,7,1.77,1.77,PASS,\"1.77,1.80,1.82\"\n");

            MethodInfo? load = typeof(StrategyMatrixTests).GetMethod("LoadBaselineRows", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(load);

            var rows = load.Invoke(null, new object[] { path });
            Assert.NotNull(rows);

            var dictionary = rows as System.Collections.IDictionary;
            Assert.NotNull(dictionary);
            Assert.True(dictionary!.Contains("exact-step:6,2,2"));

            var items = dictionary["exact-step:6,2,2"];
            Assert.NotNull(items);
            Assert.Equal(1, (int)items.GetType().GetProperty("Count")!.GetValue(items)!);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void NightlyStrategyMatrix()
    {
        if (Environment.GetEnvironmentVariable("RUN_STRATEGY_MATRIX") != "1")
            return;

        int timeoutSeconds = ReadPositiveIntEnv("STRATEGY_MATRIX_TIMEOUT_SECONDS", 180);
        int warmupRuns = ReadPositiveIntEnv("STRATEGY_MATRIX_WARMUP_RUNS", 1);
        int measuredRuns = ReadPositiveIntEnv("STRATEGY_MATRIX_MEASURED_RUNS", 3);
        double regressionPercent = ReadNonNegativeDoubleEnv("STRATEGY_MATRIX_REGRESSION_PERCENT", 20);
        string caseSet = ReadStringEnv("STRATEGY_MATRIX_CASE_SET", "full");
        bool baselineOnly = ReadBoolEnv("STRATEGY_MATRIX_BASELINE_ONLY", false);
        string reportPath = Environment.GetEnvironmentVariable("STRATEGY_MATRIX_REPORT_PATH")
            ?? Path.Combine(FindRepoRoot(), "strategy-matrix-report.csv");

        string? baselinePath = Environment.GetEnvironmentVariable("STRATEGY_MATRIX_BASELINE_PATH");
        if (string.IsNullOrWhiteSpace(baselinePath))
        {
            string defaultBaseline = Path.Combine(FindRepoRoot(), "scripts", "strategy-matrix-baseline.csv");
            if (File.Exists(defaultBaseline))
                baselinePath = defaultBaseline;
        }

        var entries = BuildMatrixEntries(caseSet);
        var results = new List<MatrixRow>(entries.Count);

        foreach (MatrixEntry entry in entries)
        {
            MeasureEntry(entry, timeoutSeconds, warmupRuns, measuredRuns, results);
        }

        WriteReport(reportPath, results);

        if (baselineOnly)
        {
            WriteBaseline(reportPath, baselinePath, results);
            return;
        }

        if (string.IsNullOrWhiteSpace(baselinePath) || !File.Exists(baselinePath))
        {
            Assert.True(results.Count > 0, "matrix produced no rows");
            return;
        }

        var baselineRows = LoadBaselineRows(baselinePath);
        var comparison = CompareAgainstBaseline(results, baselineRows, regressionPercent);

        WriteReport(Path.ChangeExtension(reportPath, ".comparison.csv"), comparison);

        int failures = comparison.Count(row => row.Status is "FAIL_STRUCTURE_CHANGED" or "FAIL_TIME_REGRESSION");
        Assert.True(failures == 0, $"strategy matrix found {failures} regression(s). See {reportPath} and baseline {baselinePath}.");
    }

    private static List<MatrixEntry> BuildMatrixEntries(string caseSet)
    {
        var full = new List<MatrixEntry>
        {
            new("exact-step:6,2,2", "exact", StageNames.StepProof, 6, 2, 2, ct => RunPlan(ct, 6, 2, 2, b => b.ExecuteStepProofStage())),
            new("exact-edge:6,2,2", "exact", "edge-compact", 6, 2, 2, ct => RunPlan(ct, 6, 2, 2, b => b.ExecuteEdgeCompactStage())),
            new("exact-step:10,2,5", "exact", StageNames.StepProof, 10, 2, 5, ct => RunPlan(ct, 10, 2, 5, b => b.ExecuteStepProofStage())),
            new("exact-edge:10,2,5", "exact", "edge-compact", 10, 2, 5, ct => RunPlan(ct, 10, 2, 5, b => b.ExecuteEdgeCompactStage())),
            new("exact-step:12,4,4", "exact", StageNames.StepProof, 12, 4, 4, ct => RunPlan(ct, 12, 4, 4, b => b.ExecuteStepProofStage())),
            new("exact-edge:12,4,4", "exact", "edge-compact", 12, 4, 4, ct => RunPlan(ct, 12, 4, 4, b => b.ExecuteEdgeCompactStage())),

            new("greedy-feasible:10,2,5", "greedy", StageNames.GreedyFeasible, 10, 2, 5, ct => RunPlan(ct, 10, 2, 5, b => b.ExecuteGreedyFeasibleStage())),
            new("greedy-tighten:10,2,5", "greedy", StageNames.GreedyTighten, 10, 2, 5, ct => RunGreedyTighten(ct, 10, 2, 5)),
            new("proof-tighten-first:10,2,5", "greedy", "proof-tighten", 10, 2, 5, ct => RunProofTightenFirst(ct, 10, 2, 5)),
            new("greedy-full:10,2,5", "greedy", "greedy-full", 10, 2, 5, ct => RunPlan(ct, 10, 2, 5, b => b.RunGreedyPipeline())),

            new("greedy-feasible:12,4,4", "greedy", StageNames.GreedyFeasible, 12, 4, 4, ct => RunPlan(ct, 12, 4, 4, b => b.ExecuteGreedyFeasibleStage())),
            new("greedy-tighten:12,4,4", "greedy", StageNames.GreedyTighten, 12, 4, 4, ct => RunGreedyTighten(ct, 12, 4, 4)),
            new("proof-tighten-first:12,4,4", "greedy", "proof-tighten", 12, 4, 4, ct => RunProofTightenFirst(ct, 12, 4, 4)),
            new("greedy-full:12,4,4", "greedy", "greedy-full", 12, 4, 4, ct => RunPlan(ct, 12, 4, 4, b => b.RunGreedyPipeline())),

            new("greedy-feasible:20,2,6", "greedy", StageNames.GreedyFeasible, 20, 2, 6, ct => RunPlan(ct, 20, 2, 6, b => b.ExecuteGreedyFeasibleStage())),
            new("greedy-tighten:20,2,6", "greedy", StageNames.GreedyTighten, 20, 2, 6, ct => RunGreedyTighten(ct, 20, 2, 6)),
            new("proof-tighten-first:20,2,6", "greedy", "proof-tighten", 20, 2, 6, ct => RunProofTightenFirst(ct, 20, 2, 6)),
            new("greedy-full:20,2,6", "greedy", "greedy-full", 20, 2, 6, ct => RunPlan(ct, 20, 2, 6, b => b.RunGreedyPipeline())),
        };

        List<MatrixEntry> selected = string.Equals(caseSet, "smoke", StringComparison.OrdinalIgnoreCase)
            ? full.Where(entry => entry.N <= 12).ToList()
            : full;

        string excludeRaw = ReadStringEnv("STRATEGY_MATRIX_EXCLUDE_KEYS", string.Empty);

        // Keep old case-set name as a compatibility alias for existing workflow runs.
        if (string.Equals(caseSet, "full-no-greedy-full-20-2-6", StringComparison.OrdinalIgnoreCase))
            excludeRaw = string.IsNullOrWhiteSpace(excludeRaw)
                ? "greedy-full:20,2,6"
                : $"{excludeRaw};greedy-full:20,2,6";

        if (string.IsNullOrWhiteSpace(excludeRaw))
            return selected;

        var excludedKeys = excludeRaw
            .Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(key => key.Trim())
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return selected.Where(entry => !excludedKeys.Contains(entry.Key)).ToList();
    }

    private static MatrixObservation RunGreedyTighten(CancellationToken cancellationToken, int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k, cancellationToken);
        StrategyPlan feasible = builder.ExecuteGreedyFeasibleStage();
        StrategyPlan tightened = builder.ExecuteGreedyTightenStage();
        return new MatrixObservation(
            Outcome: tightened.IsStrictRefinementOver(feasible) ? "Tightened" : "NoImprovement",
            HasPlan: true,
            MaxStep: tightened.MaxStep,
            TotalBranchEdges: tightened.TotalBranchEdges,
            SearchedStates: tightened.SearchStatistics.SearchedStates,
            OutcomesConstructed: tightened.SearchStatistics.OutcomesConstructed,
            CandidateGroupsEnumerated: tightened.SearchStatistics.CandidateGroupsEnumerated,
            RootProvenLowerBound: tightened.SearchStatistics.RootProvenLowerBound,
            ElapsedMilliseconds: tightened.Elapsed.TotalMilliseconds);
    }

    private static MatrixObservation RunProofTightenFirst(CancellationToken cancellationToken, int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k, cancellationToken);
        StrategyPlan feasible = builder.ExecuteGreedyFeasibleStage();
        StageResult probe = builder.ExecuteProofTightenStage(feasible.MaxStep - 1);

        return new MatrixObservation(
            Outcome: probe.Outcome.ToString(),
            HasPlan: probe.HasPlan,
            MaxStep: probe.MaterializedPlan?.MaxStep ?? feasible.MaxStep,
            TotalBranchEdges: probe.MaterializedPlan?.TotalBranchEdges ?? feasible.TotalBranchEdges,
            SearchedStates: probe.MaterializedPlan?.SearchStatistics.SearchedStates ?? feasible.SearchStatistics.SearchedStates,
            OutcomesConstructed: probe.MaterializedPlan?.SearchStatistics.OutcomesConstructed ?? feasible.SearchStatistics.OutcomesConstructed,
            CandidateGroupsEnumerated: probe.MaterializedPlan?.SearchStatistics.CandidateGroupsEnumerated ?? feasible.SearchStatistics.CandidateGroupsEnumerated,
            RootProvenLowerBound: probe.MaterializedPlan?.SearchStatistics.RootProvenLowerBound ?? feasible.SearchStatistics.RootProvenLowerBound,
            ElapsedMilliseconds: probe.Elapsed.TotalMilliseconds);
    }

    private static void MeasureEntry(
        MatrixEntry entry,
        int timeoutSeconds,
        int warmupRuns,
        int measuredRuns,
        List<MatrixRow> results)
    {
        for (int i = 0; i < warmupRuns; i++)
        {
            _ = TestTimeoutHelper.RunWithTimeout(
                $"{entry.Key} warmup {i + 1}",
                TimeSpan.FromSeconds(timeoutSeconds),
                entry.Measure);
        }

        var samples = new List<double>(measuredRuns);
        MatrixObservation? lastObservation = null;
        for (int i = 0; i < measuredRuns; i++)
        {
            MatrixObservation observation = TestTimeoutHelper.RunWithTimeout(
                $"{entry.Key} measurement {i + 1}",
                TimeSpan.FromSeconds(timeoutSeconds),
                entry.Measure);

            lastObservation = observation;
            samples.Add(observation.ElapsedMilliseconds);
        }

        samples.Sort();
        double median = samples[samples.Count / 2];
        double average = samples.Average();

        results.Add(new MatrixRow(
            entry.Key,
            entry.Mode,
            entry.Stage,
            entry.N,
            entry.M,
            entry.K,
            lastObservation?.Outcome ?? "Unknown",
            lastObservation?.HasPlan ?? false,
            lastObservation?.MaxStep ?? 0,
            lastObservation?.TotalBranchEdges ?? 0,
            lastObservation?.SearchedStates ?? 0,
            lastObservation?.OutcomesConstructed ?? 0,
            lastObservation?.CandidateGroupsEnumerated ?? 0,
            lastObservation?.RootProvenLowerBound ?? 0,
            Math.Round(median, 3),
            Math.Round(average, 3),
            "PASS",
            string.Join(",", samples.Select(s => Math.Round(s, 3).ToString(CultureInfo.InvariantCulture)))));
    }

    private static List<MatrixRow> CompareAgainstBaseline(
        List<MatrixRow> current,
        Dictionary<string, List<MatrixRow>> baselineRows,
        double regressionPercent)
    {
        var comparison = new List<MatrixRow>(current.Count);
        foreach (MatrixRow row in current)
        {
            if (!baselineRows.TryGetValue(row.Key, out List<MatrixRow>? rows) || rows.Count == 0)
            {
                comparison.Add(row with { Samples = "MISSING_BASELINE" });
                continue;
            }

            double baselineMedian = Median(rows.Select(r => r.MedianMilliseconds).ToArray());
            double delta = row.MedianMilliseconds - baselineMedian;
            double deltaPercent = baselineMedian > 0 ? delta / baselineMedian * 100.0 : double.NaN;
            string status = row.MedianMilliseconds > baselineMedian * (1.0 + regressionPercent / 100.0)
                ? "FAIL_TIME_REGRESSION"
                : "PASS";

            bool structureMatch = rows.All(r =>
                r.Mode == row.Mode &&
                r.Stage == row.Stage &&
                r.HasPlan == row.HasPlan &&
                r.MaxStep == row.MaxStep &&
                r.TotalBranchEdges == row.TotalBranchEdges &&
                r.SearchedStates == row.SearchedStates &&
                r.OutcomesConstructed == row.OutcomesConstructed &&
                r.CandidateGroupsEnumerated == row.CandidateGroupsEnumerated &&
                r.RootProvenLowerBound == row.RootProvenLowerBound);

            if (!structureMatch)
                status = "FAIL_STRUCTURE_CHANGED";

            comparison.Add(row with
            {
                Status = status,
                Samples = $"baselineMedian={baselineMedian:F3};delta={delta:F3};deltaPercent={(double.IsNaN(deltaPercent) ? "NaN" : deltaPercent.ToString("F2", CultureInfo.InvariantCulture))};status={status}"
            });
        }

        return comparison;
    }

    private static Dictionary<string, List<MatrixRow>> LoadBaselineRows(string baselinePath)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            IgnoreBlankLines = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null
        };

        using var reader = new StreamReader(baselinePath);
        using var csv = new CsvReader(reader, config);

        var rows = csv.GetRecords<StrategyMatrixCsvRow>()
            .Select(row => new MatrixRow(
                row.Key,
                row.Mode,
                row.Stage,
                row.N,
                row.M,
                row.K,
                row.Outcome,
                row.HasPlan,
                row.MaxStep,
                row.TotalBranchEdges,
                row.SearchedStates,
                row.OutcomesConstructed,
                row.CandidateGroupsEnumerated,
                row.RootProvenLowerBound,
                row.MedianMilliseconds,
                row.AverageMilliseconds,
                row.Status,
                row.Samples))
            .GroupBy(row => row.Key)
            .ToDictionary(group => group.Key, group => group.ToList());
        return rows;
    }

    private static void WriteBaseline(string reportPath, string? baselinePath, List<MatrixRow> rows)
    {
        string outputPath = baselinePath ?? Path.Combine(FindRepoRoot(), "scripts", "strategy-matrix-baseline.csv");
        WriteReport(outputPath, rows);
    }

    private static void WriteReport(string path, List<MatrixRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        using var writer = new StreamWriter(path, false);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);

        csv.WriteRecords(rows.Select(row => new StrategyMatrixCsvRow
        {
            Key = row.Key,
            Mode = row.Mode,
            Stage = row.Stage,
            N = row.N,
            M = row.M,
            K = row.K,
            Outcome = row.Outcome,
            HasPlan = row.HasPlan,
            MaxStep = row.MaxStep,
            TotalBranchEdges = row.TotalBranchEdges,
            SearchedStates = row.SearchedStates,
            OutcomesConstructed = row.OutcomesConstructed,
            CandidateGroupsEnumerated = row.CandidateGroupsEnumerated,
            RootProvenLowerBound = row.RootProvenLowerBound,
            MedianMilliseconds = row.MedianMilliseconds,
            AverageMilliseconds = row.AverageMilliseconds,
            Status = row.Status,
            Samples = row.Samples
        }));
    }

    private static MatrixObservation RunPlan(CancellationToken cancellationToken, int n, int m, int k, Func<StrategyBuilder, StrategyPlan> action)
    {
        return RunPlanInternal(cancellationToken, n, m, k, action);
    }

    private static MatrixObservation RunPlanInternal(CancellationToken cancellationToken, int n, int m, int k, Func<StrategyBuilder, StrategyPlan> action)
    {
        var builder = new StrategyBuilder(n, m, k, cancellationToken);
        StrategyPlan plan = action(builder);
        return new MatrixObservation(
            Outcome: plan.IsFeasibleUpperBound ? "Feasible" : "Resolved",
            HasPlan: true,
            MaxStep: plan.MaxStep,
            TotalBranchEdges: plan.TotalBranchEdges,
            SearchedStates: plan.SearchStatistics.SearchedStates,
            OutcomesConstructed: plan.SearchStatistics.OutcomesConstructed,
            CandidateGroupsEnumerated: plan.SearchStatistics.CandidateGroupsEnumerated,
            RootProvenLowerBound: plan.SearchStatistics.RootProvenLowerBound,
            ElapsedMilliseconds: plan.Elapsed.TotalMilliseconds);
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        int n = values.Length;
        if (n == 0)
            throw new InvalidOperationException("Cannot compute median for an empty sample set.");
        return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2.0;
    }

    private static int ReadPositiveIntEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : fallback;
    }

    private static double ReadNonNegativeDoubleEnv(string name, double fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed >= 0
            ? parsed
            : fallback;
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