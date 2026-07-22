using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;

namespace TopKFinder
{

internal static class ComparisonStateAlgorithms
{
    public static int[] GetActiveItemColors(
        int n,
        ulong activeMask,
        ulong[] ancestors,
        ulong[] descendants,
        Action throwIfCancellationRequested)
    {
        throwIfCancellationRequested();
        var colors = new int[n];
        for (int i = 0; i < n; i++)
            colors[i] = -1;

        var verts = new int[n];
        int a = 0;
        ulong remaining = activeMask;
        while (remaining != 0)
        {
            throwIfCancellationRequested();
            int i = BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            verts[a++] = i;
        }

        if (a == 0)
            return colors;

        BuildPositionSpaceGraph(n, activeMask, ancestors, descendants, verts, a, throwIfCancellationRequested, out ulong[] anc, out ulong[] desc);

        var seed = new int[a];
        var workspace = new CanonicalizationWorkspace(a);
        int[] refined = RefineCanonicalColoring(a, anc, desc, seed, workspace, throwIfCancellationRequested);
        for (int p = 0; p < a; p++)
        {
            throwIfCancellationRequested();
            colors[verts[p]] = refined[p];
        }

        return colors;
    }

    public static IntSequenceKey ComputeCanonicalForm(
        int n,
        ulong includedMask,
        ulong fixedTopMask,
        ulong highlightMask,
        ulong[] ancestors,
        ulong[] descendants,
        Action throwIfCancellationRequested)
    {
        var verts = new int[n];
        int a = 0;
        ulong remaining = includedMask;
        throwIfCancellationRequested();
        while (remaining != 0)
        {
            int i = BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            verts[a++] = i;
        }

        if (a == 0)
            return new IntSequenceKey(new[] { 0 });

        BuildPositionSpaceGraph(n, includedMask, ancestors, descendants, verts, a, throwIfCancellationRequested, out ulong[] anc, out ulong[] desc);

        var seed = new int[a];
        for (int p = 0; p < a; p++)
        {
            ulong bit = 1UL << verts[p];
            int s = (fixedTopMask & bit) != 0 ? 1 : 0;
            if ((highlightMask & bit) != 0)
                s += 2;
            seed[p] = s;
        }

        var workspace = new CanonicalizationWorkspace(a);
        int[] refined = RefineCanonicalColoring(a, anc, desc, seed, workspace, throwIfCancellationRequested);

        int[]? best = null;
        CanonicalizeRecursive(a, anc, desc, seed, refined, workspace, throwIfCancellationRequested, ref best);
        return new IntSequenceKey(best!);
    }

    public static bool TryFindOrderAutomorphism(
        ulong activeMask,
        ulong fixedTopMask,
        ulong[] ancestors,
        ulong[] descendants,
        IReadOnlyList<int> orderA,
        IReadOnlyList<int> orderB,
        Action throwIfCancellationRequested,
        out Dictionary<int, int>? automorphism)
    {
        throwIfCancellationRequested();
        automorphism = null;
        if (orderA.Count != orderB.Count)
            return false;

        ulong mask = activeMask | fixedTopMask;
        List<int> items = ComparisonState.MaskToOrderedList(mask);

        var assignment = new Dictionary<int, int>(items.Count);
        ulong used = 0;

        bool Consistent(int from, int to)
        {
            throwIfCancellationRequested();
            if (((fixedTopMask >> from) & 1UL) != ((fixedTopMask >> to) & 1UL))
                return false;
            foreach (KeyValuePair<int, int> pair in assignment)
            {
                throwIfCancellationRequested();
                int of = pair.Key;
                int ot = pair.Value;
                if (((ancestors[of] >> from) & 1UL) != ((ancestors[ot] >> to) & 1UL))
                    return false;
                if (((descendants[of] >> from) & 1UL) != ((descendants[ot] >> to) & 1UL))
                    return false;
            }
            return true;
        }

        for (int i = 0; i < orderA.Count; i++)
        {
            throwIfCancellationRequested();
            int from = orderA[i];
            int to = orderB[i];
            if ((used & (1UL << to)) != 0)
            {
                if (assignment.TryGetValue(from, out int existing) && existing == to)
                    continue;
                return false;
            }

            if (assignment.ContainsKey(from) || !Consistent(from, to))
                return false;

            assignment[from] = to;
            used |= 1UL << to;
        }

        var unassigned = new List<int>();
        foreach (int item in items)
        {
            throwIfCancellationRequested();
            if (!assignment.ContainsKey(item))
                unassigned.Add(item);
        }

        bool Search(int idx)
        {
            throwIfCancellationRequested();
            if (idx == unassigned.Count)
                return true;

            int from = unassigned[idx];
            foreach (int to in items)
            {
                throwIfCancellationRequested();
                if ((used & (1UL << to)) != 0 || !Consistent(from, to))
                    continue;

                assignment[from] = to;
                used |= 1UL << to;
                if (Search(idx + 1))
                    return true;
                assignment.Remove(from);
                used &= ~(1UL << to);
            }

            return false;
        }

        if (!Search(0))
            return false;

        automorphism = assignment;
        return true;
    }

    private static void BuildPositionSpaceGraph(
        int n,
        ulong includedMask,
        ulong[] ancestors,
        ulong[] descendants,
        int[] verts,
        int a,
        Action throwIfCancellationRequested,
        out ulong[] anc,
        out ulong[] desc)
    {
        var pos = new int[n];
        for (int p = 0; p < a; p++)
            pos[verts[p]] = p;

        anc = new ulong[a];
        desc = new ulong[a];
        for (int p = 0; p < a; p++)
        {
            throwIfCancellationRequested();
            ulong upMask = ancestors[verts[p]] & includedMask;
            while (upMask != 0)
            {
                throwIfCancellationRequested();
                int b = BitOperations.TrailingZeroCount(upMask);
                upMask &= upMask - 1;
                desc[p] |= 1UL << pos[b];
            }

            ulong downMask = descendants[verts[p]] & includedMask;
            while (downMask != 0)
            {
                throwIfCancellationRequested();
                int b = BitOperations.TrailingZeroCount(downMask);
                downMask &= downMask - 1;
                anc[p] |= 1UL << pos[b];
            }
        }
    }

    private static int[] RefineCanonicalColoring(
        int a,
        ulong[] anc,
        ulong[] desc,
        int[] colors,
        CanonicalizationWorkspace workspace,
        Action throwIfCancellationRequested)
    {
        int[] labels = workspace.Labels;
        int[] nextLabels = workspace.NextLabels;
        int[] perm = workspace.Perm;
        int[] sig = workspace.Signature;

        Array.Copy(colors, labels, a);

        int maxLabel = 0;
        for (int i = 0; i < a; i++)
        {
            if (labels[i] > maxLabel)
                maxLabel = labels[i];
        }

        if (maxLabel + 1 > a)
        {
            var present = new bool[maxLabel + 1];
            for (int i = 0; i < a; i++)
                present[labels[i]] = true;

            var map = new int[maxLabel + 1];
            int next = 0;
            for (int v = 0; v <= maxLabel; v++)
            {
                if (present[v])
                    map[v] = next++;
            }

            for (int i = 0; i < a; i++)
                labels[i] = map[labels[i]];
        }

        bool changed;
        do
        {
            throwIfCancellationRequested();
            int classCount = 0;
            for (int i = 0; i < a; i++)
            {
                throwIfCancellationRequested();
                if (labels[i] > classCount)
                    classCount = labels[i];
            }
            classCount++;

            int width = 1 + 2 * classCount;
            Array.Clear(sig, 0, a * width);
            for (int i = 0; i < a; i++)
            {
                throwIfCancellationRequested();
                int baseIdx = i * width;
                sig[baseIdx] = labels[i];

                ulong up = desc[i];
                while (up != 0)
                {
                    throwIfCancellationRequested();
                    int b = BitOperations.TrailingZeroCount(up);
                    up &= up - 1;
                    sig[baseIdx + 1 + labels[b]]++;
                }

                ulong down = anc[i];
                while (down != 0)
                {
                    throwIfCancellationRequested();
                    int b = BitOperations.TrailingZeroCount(down);
                    down &= down - 1;
                    sig[baseIdx + 1 + classCount + labels[b]]++;
                }

                perm[i] = i;
            }

            for (int x = 1; x < a; x++)
            {
                int keyPos = perm[x];
                int y = x - 1;
                while (y >= 0 && CompareCanonicalSignatures(sig, perm[y], keyPos, width) > 0)
                {
                    perm[y + 1] = perm[y];
                    y--;
                }

                perm[y + 1] = keyPos;
            }

            int color = 0;
            for (int r = 0; r < a; r++)
            {
                if (r > 0 && CompareCanonicalSignatures(sig, perm[r - 1], perm[r], width) != 0)
                    color++;
                nextLabels[perm[r]] = color;
            }

            changed = false;
            for (int i = 0; i < a; i++)
            {
                if (labels[i] != nextLabels[i])
                {
                    changed = true;
                    break;
                }
            }

            if (changed)
            {
                int[] tmp = labels;
                labels = nextLabels;
                nextLabels = tmp;
            }
        }
        while (changed);

        return (int[])labels.Clone();
    }

    private static void CanonicalizeRecursive(
        int a,
        ulong[] anc,
        ulong[] desc,
        int[] seed,
        int[] colors,
        CanonicalizationWorkspace workspace,
        Action throwIfCancellationRequested,
        ref int[]? best)
    {
        throwIfCancellationRequested();
        int classCount = 0;
        for (int i = 0; i < a; i++)
        {
            throwIfCancellationRequested();
            if (colors[i] + 1 > classCount)
                classCount = colors[i] + 1;
        }

        var cellSize = new int[classCount];
        for (int i = 0; i < a; i++)
            cellSize[colors[i]]++;

        int targetColor = -1;
        for (int c = 0; c < classCount; c++)
        {
            if (cellSize[c] > 1)
            {
                targetColor = c;
                break;
            }
        }

        if (targetColor < 0)
        {
            int[] candidate = ReadCanonicalKey(a, anc, seed, colors);
            if (best is null || CompareKeyArrays(candidate, best) < 0)
                best = candidate;
            return;
        }

        for (int p = 0; p < a; p++)
        {
            throwIfCancellationRequested();
            if (colors[p] != targetColor)
                continue;

            bool redundant = false;
            for (int q = 0; q < p; q++)
            {
                throwIfCancellationRequested();
                if (colors[q] == targetColor && AreInterchangeable(p, q, anc, desc))
                {
                    redundant = true;
                    break;
                }
            }

            if (redundant)
                continue;

            // Rebuild the individualized baseline for each branch so deeper recursion cannot
            // leak workspace mutations into sibling branches in this frame.
            for (int i = 0; i < a; i++)
                workspace.Individualized[i] = 2 * colors[i] + (colors[i] == targetColor ? 1 : 0);

            workspace.Individualized[p] = 2 * colors[p];
            int[] refined = RefineCanonicalColoring(a, anc, desc, workspace.Individualized, workspace, throwIfCancellationRequested);
            CanonicalizeRecursive(a, anc, desc, seed, refined, workspace, throwIfCancellationRequested, ref best);
        }
    }

    private static bool AreInterchangeable(int p, int q, ulong[] anc, ulong[] desc)
    {
        ulong bp = 1UL << p;
        ulong bq = 1UL << q;

        if (((anc[p] | desc[p]) & bq) != 0)
            return false;

        if ((anc[p] & ~bq) != (anc[q] & ~bp))
            return false;

        if ((desc[p] & ~bq) != (desc[q] & ~bp))
            return false;

        return true;
    }

    private static int[] ReadCanonicalKey(int a, ulong[] anc, int[] seed, int[] colors)
    {
        if (a < 0 || anc.Length < a || seed.Length < a || colors.Length < a)
        {
            throw new InvalidOperationException(
                $"Canonical key invariant violation: invalid inputs (a={a}, anc.Length={anc.Length}, seed.Length={seed.Length}, colors.Length={colors.Length}).");
        }

        var byRank = new int[a];
        var seen = new bool[a];
        for (int v = 0; v < a; v++)
        {
            int rank = colors[v];
            if ((uint)rank >= (uint)a)
            {
                throw new InvalidOperationException(
                    $"Canonical key invariant violation: rank out of range at colors[{v}]={rank} with a={a}. Input state may be inconsistent.");
            }

            if (seen[rank])
            {
                throw new InvalidOperationException(
                    $"Canonical key invariant violation: duplicate rank {rank} in discrete coloring with a={a}. Input state may be inconsistent.");
            }

            seen[rank] = true;
            byRank[rank] = v;
        }

        var parts = new int[1 + a * 3];
        parts[0] = a;
        int w = 1;
        for (int rc = 0; rc < a; rc++)
        {
            int v = byRank[rc];
            parts[w++] = seed[v];

            ulong row = 0;
            ulong ancMask = anc[v];
            while (ancMask != 0)
            {
                int b = BitOperations.TrailingZeroCount(ancMask);
                ancMask &= ancMask - 1;
                int rank = colors[b];
                if ((uint)rank >= (uint)a)
                {
                    throw new InvalidOperationException(
                        $"Canonical key invariant violation: ancestor rank out of range at colors[{b}]={rank} with a={a}. Input state may be inconsistent.");
                }

                row |= 1UL << rank;
            }

            parts[w++] = (int)(row & 0xFFFFFFFF);
            parts[w++] = (int)(row >> 32);
        }

        return parts;
    }

    private static int CompareCanonicalSignatures(int[] sig, int posLeft, int posRight, int width)
    {
        int left = posLeft * width;
        int right = posRight * width;
        for (int t = 0; t < width; t++)
        {
            int diff = sig[left + t] - sig[right + t];
            if (diff != 0)
                return diff;
        }

        return 0;
    }

    private static int CompareKeyArrays(int[] left, int[] right)
    {
        int len = Math.Min(left.Length, right.Length);
        for (int i = 0; i < len; i++)
        {
            int diff = left[i] - right[i];
            if (diff != 0)
                return diff;
        }

        return left.Length - right.Length;
    }

    private sealed class CanonicalizationWorkspace
    {
        public CanonicalizationWorkspace(int vertexCount)
        {
            Labels = new int[vertexCount];
            NextLabels = new int[vertexCount];
            Perm = new int[vertexCount];
            Individualized = new int[vertexCount];
            Signature = new int[vertexCount * (1 + 4 * vertexCount)];
        }

        public int[] Labels { get; }
        public int[] NextLabels { get; }
        public int[] Perm { get; }
        public int[] Individualized { get; }
        public int[] Signature { get; }
    }
}

}

internal static class ComparisonStateAlgorithms
{
    private static int[] ReadCanonicalKey(int a, ulong[] anc, int[] seed, int[] colors)
    {
        Type? targetType = typeof(ComparisonState).Assembly.GetType("TopKFinder.ComparisonStateAlgorithms");
        if (targetType is null)
            throw new InvalidOperationException("Canonical key helper type was not found.");

        MethodInfo? target = targetType.GetMethod(
            "ReadCanonicalKey",
            BindingFlags.NonPublic | BindingFlags.Static);
        if (target is null)
            throw new InvalidOperationException("Canonical key helper method was not found.");

        try
        {
            return (int[])target.Invoke(null, new object?[] { a, anc, seed, colors })!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}