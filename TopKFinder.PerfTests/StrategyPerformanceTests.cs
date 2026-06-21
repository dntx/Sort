using Xunit;

public sealed class StrategyPerformanceTests
{
    [Fact]
    public void N10M9K9_CompletesWithinLooseBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(10, 9, 9, iterations: 5);
        Assert.True(medianMs <= 250, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    [Fact]
    public void N9M3K3_CompletesWithinLooseBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(9, 3, 3, iterations: 5);
        Assert.True(medianMs <= 1500, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    [Fact]
    public void N12M3K3_CompletesWithinLooseBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(12, 3, 3, iterations: 3);
        Assert.True(medianMs <= 6000, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    private static double MeasureMedianElapsedMilliseconds(int n, int m, int k, int iterations)
    {
        _ = StrategyBuilder.Generate(n, m, k);

        var samples = new List<double>(iterations);
        for (int i = 0; i < iterations; i++)
        {
            StrategyPlan plan = StrategyBuilder.Generate(n, m, k);
            samples.Add(plan.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return samples[samples.Count / 2];
    }
}
