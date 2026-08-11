using System;
using System.Collections.Generic;
using System.IO;
using TopKFinder;
using Xunit;

public class GtEvaluationHarnessTests
{
    [Fact]
    public void GtEvaluationHarness_ProducesDeterministicSummary()
    {
        var harness = new GtEvaluationHarness();
        GtEvaluationResult result = harness.EvaluateSingle("sample", 8, 3, 3);

        Assert.Equal("sample", result.CaseName);
        Assert.True(result.BaselineUpper > 0);
        Assert.True(result.GtUpper > 0);
        Assert.True(result.BaselineBudget >= 1);
        Assert.True(result.GtBudget >= 1);
        Assert.True(result.BaselineProofMs >= 0.0);
        Assert.True(result.GtProofMs >= 0.0);
        Assert.True(result.GtExtraMs >= 0.0);
    }

    [Fact]
    public void GtEvaluationHarness_RunsRepresentativeBatch_AndWritesCsv()
    {
        var harness = new GtEvaluationHarness();
        var cases = new List<GtEvaluationCase>
        {
            new("case-8-3-3", 8, 3, 3),
            new("case-9-3-3", 9, 3, 3),
            new("case-10-2-5", 10, 2, 5),
            new("case-10-5-5", 10, 5, 5),
            new("case-12-4-4", 12, 4, 4),
            new("case-12-5-5", 12, 5, 5),
        };

        var outputPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "gt-eval", "gt-batch.csv");
        string resolvedPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(resolvedPath)!);

        IReadOnlyList<GtEvaluationResult> results = harness.EvaluateMany(cases);
        harness.WriteCsv(resolvedPath, results);

        Assert.NotEmpty(results);
        foreach (GtEvaluationResult result in results)
        {
            Console.WriteLine($"{result.CaseName}: upper={result.BaselineUpper}->{result.GtUpper}, budget={result.BaselineBudget}->{result.GtBudget}, proof_ms={result.BaselineProofMs:F3}->{result.GtProofMs:F3}, extra_ms={result.GtExtraMs:F3}, net_delta_ms={result.NetDeltaMs:F3}, improved={result.GtImproved}, probe={result.GtProbeRan}");
        }

        Assert.True(File.Exists(resolvedPath));
    }
}
