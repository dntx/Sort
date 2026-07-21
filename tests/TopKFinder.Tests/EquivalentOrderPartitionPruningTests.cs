using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using TopKFinder;
using Xunit;

// Deterministic, machine-independent lock for the permutation-template partition pruning in
// StrategyBuilder.EquivalentOrders.cs (TryBuildPermutationTemplateSummary -> EnumeratePartitions).
//
// The pattern engine looks for a "permute {block}" template by walking set partitions of the
// remaining items. Before the optimization it enumerated every partition -- the Bell number of the
// item count, which grows super-exponentially -- and only the orders.Count == product-of-block-
// factorials gate downstream rejected the non-matching ones. Rendering the 20,10,10 default plan
// drove that to ~11 s. The pruning threads the running product of block factorials through the
// recursion and abandons any branch once it exceeds the target order count.
//
// These tests are the count-based counterpart to the loose wall-clock guard
// (StrategyPerformanceTests.N20M10K10_CompletesWithinBudget): they assert, with no timing, that the
// pruned enumeration (1) yields EXACTLY the partitions a full enumeration filtered to product <=
// target would (correctness -- it never drops a partition the consumer could have used) and (2)
// visits dramatically fewer partitions than the unpruned Bell-number count (the perf win). A
// regression that reinstated the full enumeration would blow the count assertion well before any
// timer noticed.
public sealed class EquivalentOrderPartitionPruningTests
{
    [Theory]
    [InlineData(6, 1)]
    [InlineData(6, 2)]
    [InlineData(6, 6)]
    [InlineData(8, 2)]
    [InlineData(10, 2)]
    public void PrunedEnumeration_MatchesFullEnumerationFilteredByProduct(int itemCount, int target)
    {
        var items = Enumerable.Range(0, itemCount).ToList();

        HashSet<string> expected = EnumerateAllPartitions(items)
            .Where(partition => BlockFactorialProduct(partition) <= target)
            .Select(Canonicalize)
            .ToHashSet();

        List<List<List<int>>> pruned = StrategyBuilder.EnumeratePartitions(items, target).ToList();
        HashSet<string> actual = pruned.Select(Canonicalize).ToHashSet();

        // No valid partition is dropped and no spurious one (product > target) is yielded.
        Assert.Equal(expected, actual);

        // The pruned walk never yields a partition whose product already exceeds the target.
        Assert.All(pruned, partition => Assert.True(BlockFactorialProduct(partition) <= target));

        // Distinct partitions only -- the recursion must not double-emit.
        Assert.Equal(pruned.Count, actual.Count);
    }

    [Fact]
    public void PrunedEnumeration_VisitsFarFewerThanBellNumber()
    {
        // The 20,10,10 hot path renders branch lines that sort up to 10 items where the merged
        // order families collapse to a handful of orderings (target order counts of 2 are typical).
        // Bell(10) = 115975 partitions; the pruning must keep only the all-singleton partition and
        // the single-transposition partitions (one block of size 2), i.e. 1 + C(10,2) = 46.
        var items = Enumerable.Range(0, 10).ToList();

        int prunedCount = StrategyBuilder.EnumeratePartitions(items, 2).Count();

        Assert.Equal(46, prunedCount);

        // Guard the order of magnitude explicitly: a reverted pruning would yield Bell(10) = 115975.
        Assert.True(prunedCount < 1000, $"Pruned partition count regressed to {prunedCount}.");
    }

    private static IEnumerable<List<List<int>>> EnumerateAllPartitions(IReadOnlyList<int> items)
    {
        var blocks = new List<List<int>>();
        return EnumerateAllPartitions(items, 0, blocks);
    }

    private static IEnumerable<List<List<int>>> EnumerateAllPartitions(
        IReadOnlyList<int> items, int index, List<List<int>> blocks)
    {
        if (index == items.Count)
        {
            yield return blocks.Select(block => block.ToList()).ToList();
            yield break;
        }

        int item = items[index];
        for (int blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            blocks[blockIndex].Add(item);
            foreach (var partition in EnumerateAllPartitions(items, index + 1, blocks))
                yield return partition;
            blocks[blockIndex].RemoveAt(blocks[blockIndex].Count - 1);
        }

        blocks.Add(new List<int> { item });
        foreach (var partition in EnumerateAllPartitions(items, index + 1, blocks))
            yield return partition;
        blocks.RemoveAt(blocks.Count - 1);
    }

    private static BigInteger BlockFactorialProduct(IReadOnlyList<List<int>> partition)
    {
        BigInteger product = BigInteger.One;
        foreach (var block in partition)
        {
            BigInteger factorial = BigInteger.One;
            for (int i = 2; i <= block.Count; i++)
                factorial *= i;
            product *= factorial;
        }
        return product;
    }

    // A set partition is identified by the sorted set of its blocks, each block sorted; order of
    // items within a block and order of blocks are irrelevant to the partition's identity.
    private static string Canonicalize(IReadOnlyList<List<int>> partition)
    {
        return string.Join("|", partition
            .Select(block => string.Join(",", block.OrderBy(item => item)))
            .OrderBy(block => block, System.StringComparer.Ordinal));
    }
}
