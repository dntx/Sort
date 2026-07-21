using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TopKFinder;
using Xunit;

// Stop latency SLA guards for interactive usage.
// If these regress, treat it as a signal to add/adjust cancellation probes
// (ThrowIfCancellationRequested / ProbeCancellation) in newly expensive paths.
public class StopLatencyTests
{
    // Keep this suite intentionally small: enough shape coverage to catch regressions,
    // but bounded so it does not dominate test runtime.
    [Theory]
    [InlineData("greedy", 20, 2, 6, 2)]
    [InlineData("greedy", 20, 2, 6, 5)]
    [InlineData("greedy", 20, 2, 6, 10)]
    [InlineData("exact", 25, 6, 3, 2)]
    [InlineData("exact", 25, 6, 3, 5)]
    [InlineData("exact", 25, 6, 3, 10)]
    public async Task StopLatency_SoftCancel_StaysWithinFiveSeconds(
        string mode,
        int n,
        int m,
        int k,
        int cancelAfterSeconds)
    {
        using var cts = new CancellationTokenSource();
        var builder = new StrategyBuilder(n, m, k, cts.Token);

        Task run = Task.Run(() =>
        {
            if (string.Equals(mode, "greedy", StringComparison.OrdinalIgnoreCase))
                _ = builder.RunGreedyPipeline();
            else
                _ = builder.RunExactPipeline();
        });

        await Task.Delay(TimeSpan.FromSeconds(cancelAfterSeconds));

        // If the run already completed naturally before the scheduled stop point,
        // this case does not exercise cancellation latency and should not fail.
        if (run.IsCompleted)
        {
            Exception? natural = await Record.ExceptionAsync(async () => await run);
            Assert.Null(natural);
            return;
        }

        long cancelAt = Stopwatch.GetTimestamp();
        cts.Cancel();

        Exception? thrown = await Record.ExceptionAsync(async () => await run);
        Assert.IsType<OperationCanceledException>(thrown);

        double cancelToExitMs = (Stopwatch.GetTimestamp() - cancelAt) * 1000.0 / Stopwatch.Frequency;
        Assert.True(
            cancelToExitMs <= 5000,
            $"Stop SLA regressed for mode={mode}, shape=({n},{m},{k}), cancelAfter={cancelAfterSeconds}s: " +
            $"cancel->exit {cancelToExitMs:F0} ms");
    }
}
