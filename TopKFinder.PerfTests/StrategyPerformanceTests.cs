using Xunit;

public sealed class StrategyPerformanceTests
{
    private static readonly TimeSpan PerfTestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void N10M9K9_CompletesWithinLooseBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(10, 9, 9, iterations: 5);
        Assert.True(medianMs <= 50, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    [Fact]
    public void N9M3K3_CompletesWithinLooseBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(9, 3, 3, iterations: 5);
        Assert.True(medianMs <= 300, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    [Fact]
    public void N12M3K3_CompletesWithinLooseBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(12, 3, 3, iterations: 3);
        Assert.True(medianMs <= 1500, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    [Fact]
    public void N12M5K5_CompletesWithinLooseBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(12, 5, 5, iterations: 3);
        Assert.True(medianMs <= 400, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    private static double MeasureMedianElapsedMilliseconds(int n, int m, int k, int iterations)
    {
        _ = TestTimeoutHelper.RunWithTimeout(
            $"StrategyBuilder.Generate({n}, {m}, {k}) warmup",
            PerfTestTimeout,
            cancellationToken => StrategyBuilder.Generate(n, m, k, cancellationToken));

        var samples = new List<double>(iterations);
        for (int i = 0; i < iterations; i++)
        {
            StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
                $"StrategyBuilder.Generate({n}, {m}, {k}) iteration {i + 1}",
                PerfTestTimeout,
                cancellationToken => StrategyBuilder.Generate(n, m, k, cancellationToken));
            samples.Add(plan.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return samples[samples.Count / 2];
    }
}
