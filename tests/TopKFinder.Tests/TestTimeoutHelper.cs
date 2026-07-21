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
}
