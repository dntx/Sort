using System;
using System.Collections.Generic;
using System.Numerics;
using TopKFinder;
using Xunit;

// Verifies the core invariant behind symmetry-aware group generation: enumerating one
// representative per free-symmetry-class count vector (then canonically de-duplicating across
// classes) yields exactly the same set of automorphism orbits as scanning every m-subset.
public sealed class FreeSymmetryClassTests
{
    [Theory]
    [InlineData(5, 3)]
    [InlineData(6, 3)]
    [InlineData(7, 4)]
    [InlineData(8, 3)]
    [InlineData(9, 5)]
    public void ClassRepresentatives_CoverExactlyTheSameOrbits_AsBruteForce(int n, int groupSize)
    {
        var random = new Random(1234 + n * 31 + groupSize);
        for (int trial = 0; trial < 200; trial++)
        {
            ComparisonState state = BuildRandomState(n, random);
            if (state.ActiveCount < groupSize)
                continue;

            int size = Math.Min(groupSize, state.ActiveCount);

            HashSet<IntSequenceKey> bruteForce = CollectBruteForceOrbits(state, size);
            HashSet<IntSequenceKey> classBased = CollectClassRepresentativeOrbits(state, size);

            Assert.Equal(bruteForce, classBased);
        }
    }

    private static ComparisonState BuildRandomState(int n, Random random)
    {
        var state = new ComparisonState(n);
        int relations = random.Next(0, n * 2);
        for (int i = 0; i < relations; i++)
        {
            int a = random.Next(n);
            int b = random.Next(n);
            if (a != b)
                state.AddRelation(a, b);
        }

        return state;
    }

    private static HashSet<IntSequenceKey> CollectBruteForceOrbits(ComparisonState state, int size)
    {
        var keys = new HashSet<IntSequenceKey>();
        var active = state.GetActiveItemsOrdered();
        foreach (var combo in EnumerateCombinations(active, size))
        {
            ulong mask = 0;
            foreach (int item in combo)
                mask |= 1UL << item;
            keys.Add(state.GetGroupCanonicalKey(mask));
        }

        return keys;
    }

    private static HashSet<IntSequenceKey> CollectClassRepresentativeOrbits(ComparisonState state, int size)
    {
        List<List<int>> classes = state.GetFreeSymmetryClasses();
        var keys = new HashSet<IntSequenceKey>();
        EnumerateClassVectors(state, classes, 0, size, new List<int>(), keys);
        return keys;
    }

    private static void EnumerateClassVectors(
        ComparisonState state,
        List<List<int>> classes,
        int classIndex,
        int remaining,
        List<int> prefix,
        HashSet<IntSequenceKey> keys)
    {
        if (remaining == 0)
        {
            ulong mask = 0;
            foreach (int item in prefix)
                mask |= 1UL << item;
            keys.Add(state.GetGroupCanonicalKey(mask));
            return;
        }

        if (classIndex == classes.Count)
            return;

        List<int> cls = classes[classIndex];
        int maxTake = Math.Min(cls.Count, remaining);
        for (int take = 0; take <= maxTake; take++)
        {
            for (int j = 0; j < take; j++)
                prefix.Add(cls[j]);

            EnumerateClassVectors(state, classes, classIndex + 1, remaining - take, prefix, keys);

            prefix.RemoveRange(prefix.Count - take, take);
        }
    }

    [Theory]
    [InlineData(5, 3)]
    [InlineData(8, 4)]
    public void EmptyPoset_CollapsesToSingleOrbit(int n, int groupSize)
    {
        var state = new ComparisonState(n);
        HashSet<IntSequenceKey> classBased = CollectClassRepresentativeOrbits(state, groupSize);
        Assert.Single(classBased);
        Assert.Equal(CollectBruteForceOrbits(state, groupSize), classBased);
    }

    private static IEnumerable<List<int>> EnumerateCombinations(IReadOnlyList<int> items, int count)
    {
        var current = new List<int>(count);
        return Recurse(items, count, 0, current);

        static IEnumerable<List<int>> Recurse(IReadOnlyList<int> items, int count, int start, List<int> current)
        {
            if (current.Count == count)
            {
                yield return new List<int>(current);
                yield break;
            }

            for (int i = start; i <= items.Count - (count - current.Count); i++)
            {
                current.Add(items[i]);
                foreach (var combo in Recurse(items, count, i + 1, current))
                    yield return combo;
                current.RemoveAt(current.Count - 1);
            }
        }
    }
}
