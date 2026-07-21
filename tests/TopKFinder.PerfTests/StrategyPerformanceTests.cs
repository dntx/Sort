using TopKFinder;
using Xunit;

public sealed class StrategyPerformanceTests
{
    private static readonly TimeSpan PerfTestTimeout = TimeSpan.FromSeconds(30);

    // DIAGNOSTIC-ONLY wall-clock smoke checks for BuildDefaultPlan (P2). These exist to catch a
    // gross, order-of-magnitude blow-up or an outright hang on a few representative shapes -- NOT to
    // police incremental performance regressions. Wall-clock timing is inherently noisy and CI
    // hardware runs several times slower than a dev machine, so the budgets are deliberately LOOSE
    // (~2x or more of observed CI time) to avoid flaky false positives.
    //
    // The REAL, machine-independent regression net lives in StrategyRegressionTests as deterministic
    // counter caps:
    //   - default search:  Default_SearchedStateCountStaysWithinBaseline,
    //                       Default_OutcomesConstructedStaysWithinBaseline (OutcomesConstructed is the
    //                       dominant per-state cost and the primary time proxy),
    //                       Default_CandidateGroupsEnumeratedStaysWithinBaseline;
    //   - iterative-deepening (5,5) frontier: Default_IterativeDeepeningBaselineRemainsStable;
    //   - compact phase:    Compact_WorkCountersStayWithinBaseline.
    // Those counters, not these timers, are what should fail when work increases. See
    // docs/test-strategy.md for the full architecture.
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

    // Regression guard for the ChooseGroup/feasible materialization rendering path. The 20,10,10
    // default plan materializes a node that sorts m=10 items into ~250 branch lines; each line runs
    // the equivalent-order pattern engine, which used to enumerate every set partition of the 10
    // remaining items (Bell(10) ~= 116k) per line and cost ~11 s. Pruning the partition enumeration
    // by the block-factorial product cut that to well under a second. A gross regression here would
    // re-introduce the multi-second blow-up, so the budget is loose but far below the old cost.
    [Fact]
    public void N20M10K10_CompletesWithinBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(20, 10, 10, iterations: 3);
        Assert.True(medianMs <= 6000, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    // Heavy greedy-feasible smoke check for the case that previously sat at roughly the 30-second
    // mark. This keeps the current scratch-buffer / hot-path collection / progress-throttling gains
    // from silently sliding back while staying loose enough for normal local variance.
    // The single-iteration budget keeps the sentinel practical in CI and local runs.
    [Fact]
    public void N28M3K6_GreedyFeasibleCompletesWithinBudget()
    {
        double medianMs = MeasureMedianElapsedMilliseconds(
            "StrategyBuilder.ExecuteGreedyFeasibleStage(28, 3, 6)",
            iterations: 1,
            cancellationToken => new StrategyBuilder(28, 3, 6, cancellationToken).ExecuteGreedyFeasibleStage());

        Assert.True(medianMs <= 15000, $"Median elapsed regressed to {medianMs:F1} ms.");
    }

    private static double MeasureMedianElapsedMilliseconds(int n, int m, int k, int iterations)
        => MeasureMedianElapsedMilliseconds(
            $"StrategyBuilder.BuildDefaultPlan({n}, {m}, {k})",
            iterations,
            cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).ExecuteStepProofStage());

    private static double MeasureMedianElapsedMilliseconds(
        string description,
        int iterations,
        Func<CancellationToken, StrategyPlan> buildPlan)
    {
        _ = TestTimeoutHelper.RunWithTimeout(
            $"{description} warmup",
            PerfTestTimeout,
            buildPlan);

        var samples = new List<double>(iterations);
        for (int i = 0; i < iterations; i++)
        {
            StrategyPlan plan = TestTimeoutHelper.RunWithTimeout(
                $"{description} iteration {i + 1}",
                PerfTestTimeout,
                buildPlan);
            samples.Add(plan.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return samples[samples.Count / 2];
    }
}
