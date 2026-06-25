using Xunit;

public sealed class StrategyPerformanceTests
{
    private static readonly TimeSpan PerfTestTimeout = TimeSpan.FromSeconds(30);

    // Wall-clock smoke checks for BuildDefaultPlan. CI hardware runs several times slower than a
    // dev machine and wall-clock timing is inherently noisy, so these budgets are set at roughly
    // 2x the time observed on CI to stay stable rather than tight. They are a secondary guard only:
    // the deterministic counter caps in StrategyRegressionTests (searched states, outcomes
    // constructed, candidate groups enumerated) are the primary, machine-independent regression net
    // -- in particular Default_CandidateGroupsEnumeratedStaysWithinBaseline locks in the
    // symmetry-aware group generation win without depending on wall-clock time.
    [Fact]
    public void N6M2K2_CompletesWithinBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(6, 2, 2, iterations: 5);
        Assert.True(medianMs <= 50, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    [Fact]
    public void N10M9K9_CompletesWithinBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(10, 9, 9, iterations: 5);
        Assert.True(medianMs <= 50, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    [Fact]
    public void N9M3K3_CompletesWithinBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(9, 3, 3, iterations: 5);
        Assert.True(medianMs <= 500, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    [Fact]
    public void N12M5K5_CompletesWithinBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(12, 5, 5, iterations: 3);
        Assert.True(medianMs <= 700, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    [Fact]
    public void N12M3K3_CompletesWithinBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(12, 3, 3, iterations: 3);
        Assert.True(medianMs <= 2500, $"Median elapsed regressed to {medianMs:F1} ms.");
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
