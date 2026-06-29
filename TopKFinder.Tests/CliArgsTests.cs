using Xunit;

public sealed class CliArgsTests
{
    [Fact]
    public void TryParseCliArgs_ParsesPositionals()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3" },
            out string? n, out string? m, out string? k, out bool feasibleMode, out string? error);

        Assert.True(ok);
        Assert.Equal("9", n);
        Assert.Equal("3", m);
        Assert.Equal("3", k);
        Assert.False(feasibleMode);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("A", true)]
    [InlineData("a", true)]
    [InlineData("B", false)]
    [InlineData("b", false)]
    public void TryParseCliArgs_ParsesMode(string value, bool expected)
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3", "--mode", value },
            out _, out _, out _, out bool feasibleMode, out string? error);

        Assert.True(ok);
        Assert.Equal(expected, feasibleMode);
        Assert.Null(error);
    }

    [Fact]
    public void TryParseCliArgs_RejectsUnknownMode()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3", "--mode", "X" },
            out _, out _, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("Error: unknown mode 'X' (expected A or B)", error);
    }

    [Fact]
    public void TryParseCliArgs_RejectsModeWithoutValue()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3", "--mode" },
            out _, out _, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("Error: --mode requires a value (A or B)", error);
    }

    [Theory]
    [InlineData("--compact")]
    [InlineData("-c")]
    [InlineData("--two-phase")]
    [InlineData("--verbose")]
    public void TryParseCliArgs_RejectsUnknownOption(string option)
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3", option },
            out _, out _, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal($"Error: unknown option '{option}'", error);
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
        Assert.Equal("Error: unknown option '--compact'", error);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void IsHelpRequested_DetectsHelpFlag(string flag)
    {
        Assert.True(Program.IsHelpRequested(new[] { flag }));
        Assert.True(Program.IsHelpRequested(new[] { "9", "3", "3", flag }));
    }

    [Fact]
    public void IsHelpRequested_FalseWithoutHelpFlag()
    {
        Assert.False(Program.IsHelpRequested(new[] { "9", "3", "3" }));
        Assert.False(Program.IsHelpRequested(new[] { "9", "3", "3", "--two-phase" }));
    }
}
