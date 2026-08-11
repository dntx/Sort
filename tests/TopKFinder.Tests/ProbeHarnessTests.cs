using System;
using TopKFinder;
using Xunit;

public class ProbeHarnessTests
{
    [Fact]
    public void ProbeHarness_ComparesBaselineAndSeededRuns()
    {
        var harness = new ProbeHarness();

        ProbeSummary summary = harness.Compare("sample", 8, 3, 3);

        Assert.Equal("sample", summary.CaseName);
        Assert.Equal("baseline", summary.Baseline.Mode);
        Assert.Equal("seeded", summary.Seeded.Mode);
        Assert.True(summary.Baseline.FeasibleUpper > 0);
        Assert.True(summary.Seeded.FeasibleUpper > 0);
        Assert.True(summary.Baseline.Budget >= 1);
        Assert.True(summary.Seeded.Budget >= 1);
        Assert.True(summary.Baseline.ProofMs >= 0.0);
        Assert.True(summary.Seeded.ProofMs >= 0.0);
        Assert.True(summary.Seeded.GtMs >= 0.0);
    }
}
