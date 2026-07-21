using System;
using System.Collections.Generic;

namespace TopKFinder;

static class GroupEnumerationService
{
    internal static int[] BuildSortedColorSignature(int[] colors, IReadOnlyList<int> group)
    {
        var signature = new int[group.Count];
        for (int i = 0; i < group.Count; i++)
            signature[i] = colors[group[i]];
        Array.Sort(signature);
        return signature;
    }

    // Necessary condition for canonical-pattern equality: matching sorted color multisets.
    internal static bool GroupMatchesColorSignature(int[] colors, IReadOnlyList<int> group, int[] target)
    {
        int count = group.Count;
        if (target.Length != count)
            return false;

        Span<int> signature = stackalloc int[count];
        for (int i = 0; i < count; i++)
            signature[i] = colors[group[i]];

        for (int i = 1; i < count; i++)
        {
            int value = signature[i];
            int j = i - 1;
            while (j >= 0 && signature[j] > value)
            {
                signature[j + 1] = signature[j];
                j--;
            }

            signature[j + 1] = value;
        }

        for (int i = 0; i < count; i++)
            if (signature[i] != target[i])
                return false;

        return true;
    }

    internal static IntSequenceKey BuildCheapGroupSignature(int[] labels, IReadOnlyList<int> group)
    {
        var values = new int[group.Count];
        for (int i = 0; i < group.Count; i++)
            values[i] = labels[group[i]];
        Array.Sort(values);
        return new IntSequenceKey(values);
    }

    internal static int CompareGroupsLexicographically(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        int min = Math.Min(a.Count, b.Count);
        for (int i = 0; i < min; i++)
        {
            int cmp = a[i].CompareTo(b[i]);
            if (cmp != 0)
                return cmp;
        }

        return a.Count.CompareTo(b.Count);
    }

    internal static int CountFreshItems(ComparisonState state, IReadOnlyList<int> group)
    {
        int count = 0;
        for (int i = 0; i < group.Count; i++)
        {
            int item = group[i];
            if (state.GetAncestorCount(item) == 0 && state.GetDescendantCount(item) == 0)
                count++;
        }

        return count;
    }

    internal static int CalculateUnrelatedScore(ComparisonState state, IReadOnlyList<int> group)
    {
        int sum = 0;
        for (int i = 0; i < group.Count; i++)
        {
            int item = group[i];
            sum += state.GetAncestorCount(item) + state.GetDescendantCount(item);
        }

        return -sum;
    }

    internal static int CountUnresolvedPairs(ComparisonState state, IReadOnlyList<int> group)
    {
        int count = 0;
        for (int i = 0; i < group.Count - 1; i++)
        {
            for (int j = i + 1; j < group.Count; j++)
            {
                int a = group[i];
                int b = group[j];
                if (!state.HasAncestor(a, b) && !state.HasAncestor(b, a))
                    count++;
            }
        }

        return count;
    }
}