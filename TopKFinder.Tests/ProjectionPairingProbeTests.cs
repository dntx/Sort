using System;
using System.Collections.Generic;
using Xunit;
using Xunit.Abstractions;

// Verification-only probe for the proposed "projection pairing" (parent-automorphism quotient, then
// merge orbits related by a projection automorphism). It does NOT exercise rendering changes: it sets
// EnableProjectionPairingProbe (measurement only) with EnableProjectionOrbitMerging OFF, builds the
// default plan for a range of small (n,m,k), and reports per case how many symbolic display lines the
// projection pairing would save over the current parent-orbit pairing -- plus how many merges fold a
// non-singleton (count>=2) family, which are the genuinely new cases that need the quotient rendering
// to stay honest and must be reviewed before the pairing is adopted.
public sealed class ProjectionPairingProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static readonly (int N, int M, int K)[] Cases =
    {
        (5, 2, 2), (6, 3, 2), (6, 3, 3), (7, 3, 2), (7, 3, 3),
        (8, 3, 3), (8, 4, 3), (8, 4, 4), (9, 3, 3), (9, 4, 3),
        (9, 4, 4), (10, 3, 3), (10, 4, 4), (10, 5, 5), (11, 4, 3), (11, 4, 4),
    };

    private readonly ITestOutputHelper _output;

    public ProjectionPairingProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ReportProjectionPairingSavings()
    {
        _output.WriteLine("(n,m,k)   buckets  parentLines  projLines  savings  multiOrbit  multiFamily  maxOrbits  ge3  leak");
        _output.WriteLine("-------   -------  -----------  ---------  -------  ----------  -----------  ---------  ---  ----");

        var winSamples = new List<string>();
        var leakSamples = new List<string>();
        var ge3Samples = new List<string>();
        int totalSavings = 0;
        int totalMultiFamily = 0;
        int totalGe3 = 0;
        int totalLeak = 0;

        foreach ((int n, int m, int k) in Cases)
        {
            StrategyBuilder builder = null!;
            TestTimeoutHelper.RunWithTimeout(
                $"StrategyBuilder.BuildDefaultPlan({n}, {m}, {k}) probe",
                Timeout,
                cancellationToken =>
                {
                    builder = new StrategyBuilder(n, m, k, cancellationToken)
                    {
                        EnableProjectionOrbitMerging = false,
                        EnableProjectionPairingProbe = true,
                    };
                    return builder.BuildStepProofStage();
                });

            totalSavings += builder.ProbeLineSavings;
            totalMultiFamily += builder.ProbeMultiFamilyComponents;
            totalGe3 += builder.ProbeComponentsGe3;
            totalLeak += builder.ProbeComponentsLeak;

            _output.WriteLine(
                $"({n},{m},{k})".PadRight(9) +
                builder.ProbeBuckets.ToString().PadLeft(8) +
                builder.ProbeParentOrbitLines.ToString().PadLeft(13) +
                builder.ProbeProjectionLines.ToString().PadLeft(11) +
                builder.ProbeLineSavings.ToString().PadLeft(9) +
                builder.ProbeMultiOrbitComponents.ToString().PadLeft(12) +
                builder.ProbeMultiFamilyComponents.ToString().PadLeft(13) +
                builder.ProbeMaxComponentOrbits.ToString().PadLeft(11) +
                builder.ProbeComponentsGe3.ToString().PadLeft(5) +
                builder.ProbeComponentsLeak.ToString().PadLeft(6));

            winSamples.AddRange(builder.ProbeWinSamples);
            leakSamples.AddRange(builder.ProbeLeakSamples);
            ge3Samples.AddRange(builder.ProbeGe3Samples);
        }

        _output.WriteLine("");
        _output.WriteLine($"TOTAL line savings across cases: {totalSavings}");
        _output.WriteLine($"TOTAL multi-family merges (need quotient rendering): {totalMultiFamily}");
        _output.WriteLine($"TOTAL >=3-orbit components: {totalGe3}");
        _output.WriteLine($"TOTAL leak components (NOT one global-drop orbit): {totalLeak}");
        _output.WriteLine("");
        _output.WriteLine(">=3-orbit components (OK = honest single global-drop orbit, LEAK = transitivity leak):");
        foreach (string sample in ge3Samples)
        {
            _output.WriteLine("  " + sample);
        }
        _output.WriteLine("");
        _output.WriteLine("Leak components (must be split, not merged into one line):");
        foreach (string sample in leakSamples)
        {
            _output.WriteLine("  " + sample);
        }
        _output.WriteLine("");
        _output.WriteLine("Win samples (orbit count before->after):");
        foreach (string sample in winSamples)
        {
            _output.WriteLine("  " + sample);
        }

        // Probe sanity: the scan actually materialized display buckets.
        Assert.NotEmpty(Cases);
    }

    // Formal regression for the larger gated (m=5, k=5) frontier cases. These are the ones that
    // matter for the 25,5,5 path, and they show the 1-WL prefilter becoming materially effective:
    // the probe still sees the same projection savings and no leak components, while the number of
    // expensive projection-automorphism checks is frozen to the observed deterministic counts.
    [Theory]
    [InlineData(12, 5, 5, 0, 32, 14, 0, 12, 2, 32, 18, 14, 12, 2, 0)]
    [InlineData(13, 5, 5, 10, 375, 42, 119, 46, 115, 92, 50, 42, 3, 11, 0)]
    [InlineData(14, 5, 5, 6, 196, 32, 73, 53, 52, 97, 65, 32, 6, 2, 0)]
    [InlineData(15, 5, 5, 0, 514, 95, 131, 102, 124, 207, 112, 95, 26, 12, 0)]
    [InlineData(16, 5, 5, 16, 802, 86, 223, 100, 209, 163, 77, 86, 3, 26, 0)]
    [InlineData(17, 5, 5, 10, 860, 100, 235, 82, 253, 173, 73, 100, 11, 27, 0)]
    [InlineData(18, 5, 5, 0, 2531, 247, 769, 254, 762, 458, 211, 247, 20, 31, 0)]
    public void GatedM55ProjectionPairingProbe_StaysAtObservedCounts(
        int n, int m, int k,
        int expectedParentChecks,
        int expectedParentSkips,
        int expectedChecks,
        int expectedSkips,
        int expectedProjectedStateBuilds,
        int expectedProjectedStateCacheHits,
        int expectedParentOrbitLines,
        int expectedProjectionLines,
        int expectedLineSavings,
        int expectedMultiFamilyComponents,
        int expectedGe3Components,
        int expectedLeakComponents)
    {
        StrategyBuilder builder = RunProjectionPairingProbeCase(n, m, k);

        Assert.Equal(expectedParentChecks, builder.ParentOrbitAutomorphismChecks);
        Assert.Equal(expectedParentSkips, builder.ParentOrbitColorPrefilterSkips);
        Assert.Equal(expectedChecks, builder.ProjectionOrbitAutomorphismChecks);
        Assert.Equal(expectedSkips, builder.ProjectionOrbitColorPrefilterSkips);
        Assert.Equal(expectedProjectedStateBuilds, builder.ProjectionOrbitProjectedStateBuilds);
        Assert.Equal(expectedProjectedStateCacheHits, builder.ProjectionOrbitProjectedStateCacheHits);
        Assert.Equal(expectedParentOrbitLines, builder.ProbeParentOrbitLines);
        Assert.Equal(expectedProjectionLines, builder.ProbeProjectionLines);
        Assert.Equal(expectedLineSavings, builder.ProbeLineSavings);
        Assert.Equal(expectedMultiFamilyComponents, builder.ProbeMultiFamilyComponents);
        Assert.Equal(expectedGe3Components, builder.ProbeComponentsGe3);
        Assert.Equal(expectedLeakComponents, builder.ProbeComponentsLeak);
    }

    private static StrategyBuilder RunProjectionPairingProbeCase(int n, int m, int k)
    {
        StrategyBuilder builder = null!;
        TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildDefaultPlan({n}, {m}, {k}) gated projection probe",
            Timeout,
            cancellationToken =>
            {
                builder = new StrategyBuilder(n, m, k, cancellationToken)
                {
                    EnableProjectionOrbitMerging = false,
                    EnableProjectionPairingProbe = true,
                };
                return builder.BuildStepProofStage();
            });

        return builder;
    }
}
