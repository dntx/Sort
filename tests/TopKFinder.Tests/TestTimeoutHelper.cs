using System.Diagnostics;
using Xunit.Sdk;

internal static class TestTimeoutHelper
{
    public static T RunWithTimeout<T>(string operationName, TimeSpan timeout, Func<CancellationToken, T> action)
    {
        using var cancellationTokenSource = new CancellationTokenSource(timeout);

        try
        {
            return action(cancellationTokenSource.Token);
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
            throw new XunitException($"{operationName} exceeded timeout of {timeout.TotalSeconds:F0} seconds.");
        }
    }

    public static T RunWithGate<T>(string operationName, TimeSpan gateTimeout, TimeSpan hardTimeout, Func<CancellationToken, T> action)
    {
        if (hardTimeout < gateTimeout)
            throw new ArgumentOutOfRangeException(nameof(hardTimeout), "hardTimeout must be >= gateTimeout.");

        using var cancellationTokenSource = new CancellationTokenSource(hardTimeout);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            T result = action(cancellationTokenSource.Token);
            stopwatch.Stop();

            if (stopwatch.Elapsed > gateTimeout)
            {
                throw new XunitException(
                    $"{operationName} exceeded gate of {gateTimeout.TotalSeconds:F0} seconds but completed within the hard timeout of {hardTimeout.TotalSeconds:F0} seconds. " +
                    $"Actual duration: {stopwatch.Elapsed.TotalSeconds:F1} seconds.");
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
            stopwatch.Stop();
            throw new XunitException(
                $"{operationName} exceeded hard timeout of {hardTimeout.TotalSeconds:F0} seconds (gate: {gateTimeout.TotalSeconds:F0} seconds). " +
                $"Still running after {stopwatch.Elapsed.TotalSeconds:F1} seconds; likely a severe regression or a hang.");
        }
    }
}
