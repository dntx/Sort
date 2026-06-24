using Xunit;

public sealed class SlowSearchHeuristicTests
{
    // Configurations measured to take >= ~1.4 s (up to several seconds) in a probe sweep.
    [Theory]
    [InlineData(14, 3, 3)]
    [InlineData(15, 3, 3)]
    [InlineData(15, 4, 4)]
    [InlineData(16, 5, 5)]
    [InlineData(20, 4, 6)]
    public void IsPotentiallySlowSearch_FlagsExpensiveConfigurations(int n, int m, int k)
    {
        Assert.True(Program.IsPotentiallySlowSearch(n, m, k));
    }

    // Configurations measured to finish in <= ~1.2 s.
    [Theory]
    [InlineData(12, 3, 3)]
    [InlineData(13, 3, 3)]
    [InlineData(14, 4, 4)]
    [InlineData(14, 5, 5)]
    [InlineData(18, 9, 9)]
    [InlineData(16, 9, 9)]
    public void IsPotentiallySlowSearch_AllowsModerateConfigurations(int n, int m, int k)
    {
        Assert.False(Program.IsPotentiallySlowSearch(n, m, k));
    }

    // Tiny or near-complete top sets stay cheap regardless of n.
    [Theory]
    [InlineData(16, 3, 1)]
    [InlineData(22, 2, 1)]
    [InlineData(64, 3, 1)]
    [InlineData(20, 3, 19)]
    public void IsPotentiallySlowSearch_AllowsTrivialBoundary(int n, int m, int k)
    {
        Assert.False(Program.IsPotentiallySlowSearch(n, m, k));
    }

    [Theory]
    [InlineData(8, 8, 4)]
    [InlineData(10, 10, 5)]
    public void IsPotentiallySlowSearch_AllowsSingleSortCoverage(int n, int m, int k)
    {
        Assert.False(Program.IsPotentiallySlowSearch(n, m, k));
    }
}
