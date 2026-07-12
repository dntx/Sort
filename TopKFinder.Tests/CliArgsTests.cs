using Xunit;

public sealed class CliArgsTests
{
    [Fact]
    public void TryParseCliArgs_ParsesPositionals()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3" },
            out string? n, out string? m, out string? k, out Program.Mode mode, out int? stageLimit, out bool greedyFixedCandidate, out string? error);

        Assert.True(ok);
        Assert.Equal("9", n);
        Assert.Equal("3", m);
        Assert.Equal("3", k);
        Assert.Equal(Program.Mode.Exact, mode);
        Assert.Null(stageLimit);
        Assert.False(greedyFixedCandidate);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("greedy", "Greedy")]
    [InlineData("GREEDY", "Greedy")]
    [InlineData("exact", "Exact")]
    [InlineData("EXACT", "Exact")]
    public void TryParseCliArgs_ParsesMode(string value, string expected)
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3", "--mode", value },
            out _, out _, out _, out Program.Mode mode, out int? stageLimit, out bool greedyFixedCandidate, out string? error);

        Assert.True(ok);
        Assert.Equal(expected, mode.ToString());
        Assert.Null(stageLimit);
        Assert.False(greedyFixedCandidate);
        Assert.Null(error);
    }

    [Fact]
    public void TryParseCliArgs_ParsesStage()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3", "--stage", "2" },
            out _, out _, out _, out Program.Mode mode, out int? stageLimit, out bool greedyFixedCandidate, out string? error);

        Assert.True(ok);
        Assert.Equal(Program.Mode.Exact, mode);
        Assert.Equal(2, stageLimit);
        Assert.False(greedyFixedCandidate);
        Assert.Null(error);
    }

    [Fact]
    public void TryParseCliArgs_ParsesModeAndStageTogether()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3", "--mode", "greedy", "--stage", "1" },
            out _, out _, out _, out Program.Mode mode, out int? stageLimit, out bool greedyFixedCandidate, out string? error);

        Assert.True(ok);
        Assert.Equal(Program.Mode.Greedy, mode);
        Assert.Equal(1, stageLimit);
        Assert.False(greedyFixedCandidate);
        Assert.Null(error);
    }

    [Fact]
    public void TryParseCliArgs_ParsesGreedyFixedCandidateFlag()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3", "--greedy-fixed-candidate" },
            out _, out _, out _, out Program.Mode mode, out int? stageLimit, out bool greedyFixedCandidate, out string? error);

        Assert.True(ok);
        Assert.Equal(Program.Mode.Exact, mode);
        Assert.Null(stageLimit);
        Assert.True(greedyFixedCandidate);
        Assert.Null(error);
    }

    [Fact]
    public void TryParseCliArgs_RejectsUnknownMode()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3", "--mode", "X" },
            out _, out _, out _, out _, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("Error: unknown mode 'X' (expected exact or greedy)", error);
    }

    [Fact]
    public void TryParseCliArgs_RejectsModeWithoutValue()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3", "--mode" },
            out _, out _, out _, out _, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("Error: --mode requires a value (exact or greedy)", error);
    }

    [Fact]
    public void TryParseCliArgs_RejectsStageWithoutValue()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3", "--stage" },
            out _, out _, out _, out _, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal("Error: --stage requires a positive integer value", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void TryParseCliArgs_RejectsInvalidStage(string value)
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3", "--stage", value },
            out _, out _, out _, out _, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal($"Error: invalid stage '{value}' (expected a positive integer)", error);
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
            out _, out _, out _, out _, out _, out _, out string? error);

        Assert.False(ok);
        Assert.Equal($"Error: unknown option '{option}'", error);
    }

    [Fact]
    public void TryParseCliArgs_RejectsTooFewPositionals()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3" }, out _, out _, out _, out _, out _, out _, out string? error);

        Assert.False(ok);
        Assert.StartsWith("Error: expected 3 positional arguments", error);
    }

    [Fact]
    public void TryParseCliArgs_RejectsTooManyPositionals()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "9", "3", "3", "2" }, out _, out _, out _, out _, out _, out _, out string? error);

        Assert.False(ok);
        Assert.StartsWith("Error: expected 3 positional arguments", error);
    }

    [Fact]
    public void TryParseCliArgs_RejectsFlagOnlyInput()
    {
        bool ok = Program.TryParseCliArgs(
            new[] { "--compact" }, out _, out _, out _, out _, out _, out _, out string? error);

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
