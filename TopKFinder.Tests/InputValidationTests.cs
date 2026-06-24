using Xunit;

public sealed class InputValidationTests
{
    [Theory]
    [InlineData("3", "2", "1")]
    [InlineData("5", "3", "2")]
    [InlineData("4", "4", "4")]
    public void TryParseAndValidate_AcceptsValidInput(string n, string m, string k)
    {
        bool ok = Program.TryParseAndValidate(n, m, k, out _, out _, out _, out string? error);

        Assert.True(ok);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("3", "4", "1")]
    [InlineData("5", "6", "2")]
    [InlineData("2", "3", "1")]
    public void TryParseAndValidate_RejectsMGreaterThanN(string n, string m, string k)
    {
        bool ok = Program.TryParseAndValidate(n, m, k, out _, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("Error: m must be <= n", error);
    }

    [Theory]
    [InlineData("3", "1", "1")]
    [InlineData("5", "0", "2")]
    public void TryParseAndValidate_RejectsMBelowTwo(string n, string m, string k)
    {
        bool ok = Program.TryParseAndValidate(n, m, k, out _, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("Error: m must be >= 2", error);
    }

    [Theory]
    [InlineData("x", "2", "1", "Error: n must be an integer")]
    [InlineData("3", "y", "1", "Error: m must be an integer")]
    [InlineData("3", "2", "z", "Error: k must be an integer")]
    [InlineData("0", "2", "1", "Error: n must be positive")]
    [InlineData("65", "2", "1", "Error: n must be <= 64")]
    [InlineData("3", "2", "4", "Error: k must satisfy 1 <= k <= n")]
    [InlineData("3", "2", "0", "Error: k must satisfy 1 <= k <= n")]
    public void TryParseAndValidate_RejectsInvalidInput(string n, string m, string k, string expectedError)
    {
        bool ok = Program.TryParseAndValidate(n, m, k, out _, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal(expectedError, error);
    }
}
