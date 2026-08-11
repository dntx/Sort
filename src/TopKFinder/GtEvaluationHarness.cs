using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TopKFinder;

public sealed record GtEvaluationCase(string CaseName, int N, int M, int K);

public sealed record GtEvaluationResult(
    string CaseName,
    int N,
    int M,
    int K,
    int BaselineUpper,
    int GtUpper,
    int BaselineBudget,
    int GtBudget,
    double BaselineProofMs,
    double GtProofMs,
    double GtExtraMs,
    bool GtImproved,
    bool GtProbeRan,
    bool GtProbeWouldHelp)
{
    public double NetDeltaMs => GtProofMs + GtExtraMs - BaselineProofMs;
    public int UpperDelta => BaselineUpper - GtUpper;
}

public sealed class GtEvaluationHarness
{
    public IReadOnlyList<GtEvaluationResult> EvaluateMany(IEnumerable<GtEvaluationCase> cases)
        => cases.Select(EvaluateSingle).ToList();

    public GtEvaluationResult EvaluateSingle(GtEvaluationCase probeCase)
        => EvaluateSingle(probeCase.CaseName, probeCase.N, probeCase.M, probeCase.K);

    public GtEvaluationResult EvaluateSingle(string caseName, int n, int m, int k)
    {
        var baselineBuilder = new StrategyBuilder(n, m, k);
        StrategyPlan baselineUpperPlan = baselineBuilder.ExecuteGreedyFeasibleStage();
        int baselineBudget = Math.Max(1, baselineUpperPlan.MaxStep - 1);

        var baselineProofSw = Stopwatch.StartNew();
        StageResult baselineProofStage = baselineBuilder.ExecuteProofTightenStageWithSolution(baselineBudget).Result;
        baselineProofSw.Stop();
        StrategyPlan baselineProofPlan = baselineProofStage.MaterializedPlan ?? baselineUpperPlan;

        var gtBuilder = new StrategyBuilder(n, m, k);
        StrategyPlan gtUpperPlan = gtBuilder.ExecuteGreedyFeasibleStage();
        int gtBudget = Math.Max(1, gtUpperPlan.MaxStep - 1);

        bool gtProbeRan = gtBuilder.ShouldRunGreedyTightenByRootProbe();
        double gtExtraMs = 0.0;
        if (gtProbeRan)
        {
            var gtSw = Stopwatch.StartNew();
            StrategyPlan gtPlan = gtBuilder.ExecuteGreedyTightenStage();
            gtSw.Stop();
            gtExtraMs = gtSw.Elapsed.TotalMilliseconds;
            if (gtPlan.MaxStep < gtUpperPlan.MaxStep)
                gtBudget = Math.Max(1, gtPlan.MaxStep - 1);
        }

        var gtProofSw = Stopwatch.StartNew();
        StageResult gtProofStage = gtBuilder.ExecuteProofTightenStageWithSolution(gtBudget).Result;
        gtProofSw.Stop();
        StrategyPlan gtProofPlan = gtProofStage.MaterializedPlan ?? gtUpperPlan;

        bool gtImproved = gtProofPlan.MaxStep < baselineProofPlan.MaxStep;
        bool gtProbeWouldHelp = gtProbeRan && gtImproved;

        return new GtEvaluationResult(
            caseName,
            n,
            m,
            k,
            baselineUpperPlan.MaxStep,
            gtUpperPlan.MaxStep,
            baselineBudget,
            gtBudget,
            baselineProofSw.Elapsed.TotalMilliseconds,
            gtProofSw.Elapsed.TotalMilliseconds,
            gtExtraMs,
            gtImproved,
            gtProbeRan,
            gtProbeWouldHelp);
    }

    public void WriteCsv(string path, IEnumerable<GtEvaluationResult> results)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        using var writer = new StreamWriter(path, false);
        writer.WriteLine(string.Join(",",
            "case",
            "n",
            "m",
            "k",
            "baseline_upper",
            "gt_upper",
            "baseline_budget",
            "gt_budget",
            "baseline_proof_ms",
            "gt_proof_ms",
            "gt_extra_ms",
            "net_delta_ms",
            "upper_delta",
            "gt_improved",
            "gt_probe_ran",
            "gt_probe_would_help"));

        foreach (GtEvaluationResult result in results)
        {
            writer.WriteLine(string.Join(",",
                Escape(result.CaseName),
                result.N,
                result.M,
                result.K,
                result.BaselineUpper,
                result.GtUpper,
                result.BaselineBudget,
                result.GtBudget,
                result.BaselineProofMs.ToString("F3", CultureInfo.InvariantCulture),
                result.GtProofMs.ToString("F3", CultureInfo.InvariantCulture),
                result.GtExtraMs.ToString("F3", CultureInfo.InvariantCulture),
                result.NetDeltaMs.ToString("F3", CultureInfo.InvariantCulture),
                result.UpperDelta,
                result.GtImproved.ToString().ToLowerInvariant(),
                result.GtProbeRan.ToString().ToLowerInvariant(),
                result.GtProbeWouldHelp.ToString().ToLowerInvariant()));
        }
    }

    private static string Escape(string value)
        => value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
