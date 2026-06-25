using Xunit;

public sealed class StrategyPerformanceTests
{
    private static readonly TimeSpan PerfTestTimeout = TimeSpan.FromSeconds(30);

    // Wall-clock smoke checks for BuildDefaultPlan. Budgets are ~5x the median observed on a
    // fast dev machine, leaving headroom for slower CI hardware. Timing is machine-dependent, so
    // these are a secondary guard only -- the deterministic counter caps in StrategyRegressionTests
    // (searched states, outcomes constructed, candidate groups) are the primary regression net.
    [Fact]
    public void N6M2K2_CompletesWithinBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(6, 2, 2, iterations: 5);
        Assert.True(medianMs <= 10, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    [Fact]
    public void N10M9K9_CompletesWithinBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(10, 9, 9, iterations: 5);
        Assert.True(medianMs <= 10, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    [Fact]
    public void N9M3K3_CompletesWithinBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(9, 3, 3, iterations: 5);
        Assert.True(medianMs <= 150, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    [Fact]
    public void N12M5K5_CompletesWithinBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(12, 5, 5, iterations: 3);
        Assert.True(medianMs <= 250, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    [Fact]
    public void N12M3K3_CompletesWithinBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(12, 3, 3, iterations: 3);
        Assert.True(medianMs <= 800, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    // Symmetry-aware group generation showcase: a large, highly symmetric state where the
    // up-front orbit collapse is decisive. Median ~2.6s on dev hardware; the generous budget keeps
    // CI green on slower machines while still failing loudly if the symmetry optimization regresses.
    [Fact]
    public void N25M5K3_SymmetryShowcaseCompletesWithinBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(25, 5, 3, iterations: 3);
        Assert.True(medianMs <= 12000, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    private static double MeasureMedianElapsedMilliseconds(int n, int m, int k, int iterations)
    {
        _ = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.BuildDefaultPlan({n}, {m}, {k}) warmup",
            PerfTestTimeout,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildDefaultPlan());

        var samples = new List<double>(iterations);
        for (int i = 0; i < iterations; i++)
        {
            StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
                $"StrategyBuilder.BuildDefaultPlan({n}, {m}, {k}) iteration {i + 1}",
                PerfTestTimeout,
                cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildDefaultPlan());
            samples.Add(plan.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return samples[samples.Count / 2];
    }
}
