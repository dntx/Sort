using System.Globalization;
using System.IO;
using TopKFinder;
using Xunit;
using Xunit.Abstractions;

namespace TopKFinder.PerfTests;

// Quick scan for whether GT can change proof's starting budget at all.
//
// Enable:
//   $env:RUN_GT_SEEDABILITY_SCAN = "1"
//   dotnet test tests\TopKFinder.PerfTests\TopKFinder.PerfTests.csproj --filter GreedyTightenSeedabilityScanTests
//
// Optional knobs:
//   GT_SEEDABILITY_CASES        (default "10,2,5;12,4,4;20,2,6")
//   GT_SEEDABILITY_REPORT_PATH  (default <repo>\artifacts\gt-seedability-report.csv)
//   GT_SEEDABILITY_TIMEOUT_SECONDS (default 180)
public sealed class GreedyTightenSeedabilityScanTests
{
    private readonly ITestOutputHelper _output;

    public GreedyTightenSeedabilityScanTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed record CaseSpec(int N, int M, int K);

    [Fact]
    public void ScanGtSeedability()
    {
        if (Environment.GetEnvironmentVariable("RUN_GT_SEEDABILITY_SCAN") != "1")
            return;

        string rawCases = ReadStringEnv("GT_SEEDABILITY_CASES", "10,2,5;12,4,4;20,2,6");
        int timeoutSeconds = ReadPositiveIntEnv("GT_SEEDABILITY_TIMEOUT_SECONDS", 180);
        string reportPath = ReadStringEnv(
            "GT_SEEDABILITY_REPORT_PATH",
            Path.Combine(FindRepoRoot(), "artifacts", "gt-seedability-report.csv"));

        List<CaseSpec> cases = ParseCases(rawCases);
        Assert.NotEmpty(cases);

        var rows = new List<string>();
        rows.Add("Case,ProbeRun,GatedGtImproved,ForcedGtImproved,FeasibleU,GatedGtU,ForcedGtU,ProofBudgetBaseline,ProofBudgetSeededByGated,ProofBudgetSeededByForced,GatedBudgetDelta,ForcedBudgetDelta,ForcedGtElapsedMs");

        foreach (CaseSpec c in cases)
        {
            (bool ProbeRun, bool GatedImproved, bool ForcedImproved, int FeasibleU, int GatedGtU, int ForcedGtU, int BaselineBudget, int GatedSeededBudget, int ForcedSeededBudget, int GatedBudgetDelta, int ForcedBudgetDelta, double ForcedGtElapsedMs) scan =
                TestTimeoutHelper.RunWithTimeout(
                    $"gt-seedability-scan {c.N},{c.M},{c.K}",
                    TimeSpan.FromSeconds(timeoutSeconds),
                    cancellationToken =>
                    {
                        var builder = new StrategyBuilder(c.N, c.M, c.K, cancellationToken);
                        StrategyPlan feasible = builder.ExecuteGreedyFeasibleStage();
                        bool probeRun = builder.ShouldRunGreedyTightenByRootProbe();

                        bool gatedImproved = false;
                        int gatedGtU = feasible.MaxStep;
                        if (probeRun)
                        {
                            StrategyPlan gt = builder.ExecuteGreedyTightenStage();
                            gatedGtU = gt.MaxStep;
                            gatedImproved = gatedGtU < feasible.MaxStep;
                        }

                        // Forced path: run GT regardless of root probe so we can separate "gate did not hit"
                        // from "GT cannot improve U".
                        var forcedBuilder = new StrategyBuilder(c.N, c.M, c.K, cancellationToken);
                        StrategyPlan forcedFeasible = forcedBuilder.ExecuteGreedyFeasibleStage();
                        var forcedStopwatch = System.Diagnostics.Stopwatch.StartNew();
                        StrategyPlan forcedGt = forcedBuilder.ExecuteGreedyTightenStage();
                        forcedStopwatch.Stop();
                        int forcedGtU = forcedGt.MaxStep;
                        bool forcedImproved = forcedGtU < forcedFeasible.MaxStep;

                        int baselineBudget = feasible.MaxStep - 1;
                        int gatedSeededBudget = (gatedImproved ? gatedGtU : feasible.MaxStep) - 1;
                        int forcedSeededBudget = (forcedImproved ? forcedGtU : forcedFeasible.MaxStep) - 1;
                        int gatedBudgetDelta = baselineBudget - gatedSeededBudget;
                        int forcedBudgetDelta = baselineBudget - forcedSeededBudget;
                        return (
                            probeRun,
                            gatedImproved,
                            forcedImproved,
                            feasible.MaxStep,
                            gatedGtU,
                            forcedGtU,
                            baselineBudget,
                            gatedSeededBudget,
                            forcedSeededBudget,
                            gatedBudgetDelta,
                            forcedBudgetDelta,
                            forcedStopwatch.Elapsed.TotalMilliseconds);
                    });

            string caseText = $"{c.N},{c.M},{c.K}";
            rows.Add(string.Join(",",
                QuoteCsv(caseText),
                scan.ProbeRun,
                scan.GatedImproved,
                scan.ForcedImproved,
                scan.FeasibleU.ToString(CultureInfo.InvariantCulture),
                scan.GatedGtU.ToString(CultureInfo.InvariantCulture),
                scan.ForcedGtU.ToString(CultureInfo.InvariantCulture),
                scan.BaselineBudget.ToString(CultureInfo.InvariantCulture),
                scan.GatedSeededBudget.ToString(CultureInfo.InvariantCulture),
                scan.ForcedSeededBudget.ToString(CultureInfo.InvariantCulture),
                scan.GatedBudgetDelta.ToString(CultureInfo.InvariantCulture),
                scan.ForcedBudgetDelta.ToString(CultureInfo.InvariantCulture),
                Math.Round(scan.ForcedGtElapsedMs, 3).ToString(CultureInfo.InvariantCulture)));

            _output.WriteLine(
                $"Seedability case={caseText} probeRun={scan.ProbeRun} " +
                $"gatedImproved={scan.GatedImproved} forcedImproved={scan.ForcedImproved} " +
                $"U={scan.FeasibleU} gatedGtU={scan.GatedGtU} forcedGtU={scan.ForcedGtU} " +
                $"budget(base={scan.BaselineBudget},gated={scan.GatedSeededBudget},forced={scan.ForcedSeededBudget}," +
                $"gatedDelta={scan.GatedBudgetDelta},forcedDelta={scan.ForcedBudgetDelta}) " +
                $"forcedGtMs={scan.ForcedGtElapsedMs:F3}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? AppContext.BaseDirectory);
        File.WriteAllLines(reportPath, rows);
        Assert.True(File.Exists(reportPath));
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

    private static string QuoteCsv(string value)
        => $"\"{value.Replace("\"", "\"\"")}\"";

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

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "TopKFinder", "TopKFinder.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
