using System.Collections.Generic;
using System.Linq;
using Xunit;

public sealed class CombinatoricsServiceTests
{
    [Fact]
    public void EnumerateCombinations_ReturnsExpectedLexicographicTuples()
    {
        int[] items = { 1, 2, 3, 4 };
        int probeCount = 0;

        List<List<int>> combinations = CombinatoricsService
            .EnumerateCombinations(items, 2, () => probeCount++)
            .ToList();

        Assert.True(probeCount > 0);
        Assert.Equal(6, combinations.Count);
        Assert.Equal(new[] { 1, 2 }, combinations[0]);
        Assert.Equal(new[] { 1, 3 }, combinations[1]);
        Assert.Equal(new[] { 1, 4 }, combinations[2]);
        Assert.Equal(new[] { 2, 3 }, combinations[3]);
        Assert.Equal(new[] { 2, 4 }, combinations[4]);
        Assert.Equal(new[] { 3, 4 }, combinations[5]);
    }

    [Fact]
    public void EnumerateCombinations_HandlesZeroAndOversizedCounts()
    {
        int[] items = { 1, 2, 3 };

        List<List<int>> zeroCount = CombinatoricsService
            .EnumerateCombinations(items, count: 0, probeCancellation: () => { })
            .ToList();

        List<List<int>> oversizedCount = CombinatoricsService
            .EnumerateCombinations(items, count: 5, probeCancellation: () => { })
            .ToList();

        Assert.Single(zeroCount);
        Assert.Empty(zeroCount[0]);
        Assert.Empty(oversizedCount);
    }
}