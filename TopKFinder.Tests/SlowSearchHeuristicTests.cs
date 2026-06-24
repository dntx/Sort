using Xunit;

public sealed class SlowSearchHeuristicTests
{
    // Hard-shape configurations measured (Release) to run ~13 s .. >60 s, i.e. ten-plus seconds.
    [Theory]
    [InlineData(18, 3, 3)]
    [InlineData(18, 4, 4)]
    [InlineData(18, 5, 5)]
    [InlineData(18, 6, 6)]
    [InlineData(18, 5, 6)]
    [InlineData(19, 3, 3)]
    [InlineData(19, 5, 5)]
    [InlineData(20, 4, 6)]
    public void IsPotentiallySlowSearch_FlagsExpensiveConfigurations(int n, int m, int k)
    {
        Assert.True(Program.IsPotentiallySlowSearch(n, m, k));
    }

    // Configurations measured to finish well under ten seconds (mostly seconds or less).
    [Theory]
    [InlineData(16, 4, 4)]   // ~3.1 s
    [InlineData(16, 5, 5)]   // ~3.1 s
    [InlineData(17, 3, 3)]   // ~5.3 s
    [InlineData(17, 4, 4)]   // ~7.1 s
    [InlineData(17, 5, 5)]   // ~6.7 s
    [InlineData(15, 4, 4)]   // ~1.2 s
    [InlineData(14, 3, 3)]   // ~0.5 s
    public void IsPotentiallySlowSearch_AllowsSubTenSecondConfigurations(int n, int m, int k)
    {
        Assert.False(Program.IsPotentiallySlowSearch(n, m, k));
    }

    // Large n but a large sort (m > n/3) keeps the tree shallow and the search fast.
    [Theory]
    [InlineData(18, 7, 7)]   // ~1.4 s
    [InlineData(18, 8, 8)]   // ~1.9 s
    [InlineData(18, 9, 9)]   // ~0.5 s
    [InlineData(17, 8, 8)]   // ~0.6 s
    [InlineData(16, 9, 9)]   // ~0.02 s
    public void IsPotentiallySlowSearch_AllowsLargeSortCoverage(int n, int m, int k)
    {
        Assert.False(Program.IsPotentiallySlowSearch(n, m, k));
    }

    // Tiny or near-complete top sets (min(k, n-k) <= 2) stay cheap regardless of n.
    [Theory]
    [InlineData(18, 2, 2)]   // ~0.2 s
    [InlineData(16, 3, 1)]
    [InlineData(22, 2, 1)]
    [InlineData(64, 3, 1)]
    [InlineData(20, 3, 19)]
    [InlineData(20, 3, 18)]
    public void IsPotentiallySlowSearch_AllowsTrivialBoundary(int n, int m, int k)
    {
        Assert.False(Program.IsPotentiallySlowSearch(n, m, k));
    }

    // A single sort that already isolates >= k items keeps the search fast.
    [Theory]
    [InlineData(16, 5, 4)]   // ~0.1 s
    [InlineData(17, 5, 4)]   // ~0.2 s
    [InlineData(17, 6, 5)]   // ~0.2 s
    [InlineData(10, 10, 5)]
    public void IsPotentiallySlowSearch_AllowsSingleSortCoverage(int n, int m, int k)
    {
        Assert.False(Program.IsPotentiallySlowSearch(n, m, k));
    }
}
