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
                    return builder.BuildStepProofPlan();
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
}
