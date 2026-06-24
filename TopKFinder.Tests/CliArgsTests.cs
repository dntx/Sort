using Xunit;

public sealed class CliArgsTests
{
    [Fact]
    public void TryParseCliArgs_ParsesPositionalsWithoutCompact()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3" },
            out string? n, out string? m, out string? k, out bool compact, out string? error);

        Assert.True(ok);
        Assert.Equal("9", n);
        Assert.Equal("3", m);
        Assert.Equal("3", k);
        Assert.False(compact);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("--compact")]
    [InlineData("-c")]
    public void TryParseCliArgs_DetectsCompactFlag(string flag)
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3", flag },
            out _, out _, out _, out bool compact, out string? error);

        Assert.True(ok);
        Assert.True(compact);
        Assert.Null(error);
    }

    [Fact]
    public void TryParseCliArgs_AllowsFlagBeforePositionals()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "--compact", "9", "3", "3" },
            out string? n, out _, out _, out bool compact, out _);

        Assert.True(ok);
        Assert.True(compact);
        Assert.Equal("9", n);
    }

    [Fact]
    public void TryParseCliArgs_RejectsUnknownOption()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3", "--verbose" },
            out _, out _, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("Error: unknown option '--verbose'", error);
    }

    [Fact]
    public void TryParseCliArgs_RejectsTooFewPositionals()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3" }, out _, out _, out _, out _, out string? error);

        Assert.False(ok);
        Assert.StartsWith("Error: expected 3 positional arguments", error);
    }

    [Fact]
    public void TryParseCliArgs_RejectsTooManyPositionals()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3", "2" }, out _, out _, out _, out _, out string? error);

        Assert.False(ok);
        Assert.StartsWith("Error: expected 3 positional arguments", error);
    }

    [Fact]
    public void TryParseCliArgs_RejectsFlagOnlyInput()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "--compact" }, out _, out _, out _, out _, out string? error);

        Assert.False(ok);
        Assert.StartsWith("Error: expected 3 positional arguments", error);
    }
}
