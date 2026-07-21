using System;
using System.Collections.Generic;

namespace TopKFinder;

static class CombinatoricsService
{
    internal static IEnumerable<List<int>> EnumerateCombinations(
        IReadOnlyList<int> items,
        int count,
        Action probeCancellation)
    {
        probeCancellation();
        var current = new List<int>(count);
        foreach (List<int> combination in Enumerate(items, count, 0, current, probeCancellation))
            yield return combination;
    }

    private static IEnumerable<List<int>> Enumerate(
        IReadOnlyList<int> items,
        int count,
        int start,
        List<int> current,
        Action probeCancellation)
    {
        probeCancellation();
        if (current.Count == count)
        {
            yield return new List<int>(current);
            yield break;
        }

        for (int i = start; i <= items.Count - (count - current.Count); i++)
        {
            probeCancellation();
            current.Add(items[i]);
            foreach (List<int> combination in Enumerate(items, count, i + 1, current, probeCancellation))
                yield return combination;
            current.RemoveAt(current.Count - 1);
        }
    }
}