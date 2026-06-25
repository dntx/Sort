using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

readonly struct IntSequenceKey : IEquatable<IntSequenceKey>, IComparable<IntSequenceKey>
{
    private readonly int[] _parts;
    private readonly int _hashCode;

    public IntSequenceKey(int[] parts)
    {
        _parts = parts;
        var hash = new HashCode();
        foreach (int part in parts)
            hash.Add(part);
        _hashCode = hash.ToHashCode();
    }

    public bool Equals(IntSequenceKey other)
    {
        if (_parts.Length != other._parts.Length)
            return false;

        for (int i = 0; i < _parts.Length; i++)
        {
            if (_parts[i] != other._parts[i])
                return false;
        }

        return true;
    }

    public int CompareTo(IntSequenceKey other)
    {
        int commonLength = Math.Min(_parts.Length, other._parts.Length);
        for (int i = 0; i < commonLength; i++)
        {
            int comparison = _parts[i].CompareTo(other._parts[i]);
            if (comparison != 0)
                return comparison;
        }

        return _parts.Length.CompareTo(other._parts.Length);
    }

    public override bool Equals(object? obj)
    {
        return obj is IntSequenceKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return _hashCode;
    }

    public static bool operator ==(IntSequenceKey left, IntSequenceKey right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(IntSequenceKey left, IntSequenceKey right)
    {
        return !left.Equals(right);
    }
}

readonly struct SearchStateKey : IEquatable<SearchStateKey>
{
    public SearchStateKey(int remainingSlots, IntSequenceKey stateKey)
    {
        RemainingSlots = remainingSlots;
        StateKey = stateKey;
    }

    public int RemainingSlots { get; }
    public IntSequenceKey StateKey { get; }

    public bool Equals(SearchStateKey other)
    {
        return RemainingSlots == other.RemainingSlots && StateKey.Equals(other.StateKey);
    }

    public override bool Equals(object? obj)
    {
        return obj is SearchStateKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(RemainingSlots, StateKey);
    }
}

readonly struct BuildStateKey : IEquatable<BuildStateKey>
{
    public BuildStateKey(ulong fixedTopMask, SearchStateKey searchKey)
    {
        FixedTopMask = fixedTopMask;
        SearchKey = searchKey;
    }

    public ulong FixedTopMask { get; }
    public SearchStateKey SearchKey { get; }

    public bool Equals(BuildStateKey other)
    {
        return FixedTopMask == other.FixedTopMask && SearchKey.Equals(other.SearchKey);
    }

    public override bool Equals(object? obj)
    {
        return obj is BuildStateKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(FixedTopMask, SearchKey);
    }
}

class ComparisonState
{
    private readonly int _n;
    private readonly ulong _allMask;
    private readonly ulong[] _ancestors;
    private readonly ulong[] _descendants;
    public ulong ActiveMask { get; private set; }
    public int ActiveCount { get; private set; }
    private int[]? _structuralLabelsCache;
    private IntSequenceKey? _canonicalKeyCache;

    public ComparisonState(int n)
    {
        _n = n;
        _allMask = CreateFullMask(n);
        _ancestors = new ulong[n];
        _descendants = new ulong[n];
        ActiveMask = _allMask;
        ActiveCount = n;
    }

    private ComparisonState(int n, ulong[] ancestors, ulong[] descendants, ulong activeMask, int activeCount)
    {
        _n = n;
        _allMask = CreateFullMask(n);
        _ancestors = ancestors;
        _descendants = descendants;
        ActiveMask = activeMask;
        ActiveCount = activeCount;
    }

    public ComparisonState Clone()
    {
        return new ComparisonState(
            _n,
            (ulong[])_ancestors.Clone(),
            (ulong[])_descendants.Clone(),
            ActiveMask,
            ActiveCount);
    }

    private void InvalidateDerivedCaches()
    {
        _structuralLabelsCache = null;
        _canonicalKeyCache = null;
    }

    public void AddRelation(int greater, int lesser)
    {
        ulong greaterBit = Bit(greater);
        ulong lesserBit = Bit(lesser);
        if ((_ancestors[lesser] & greaterBit) != 0)
            return;

        InvalidateDerivedCaches();

        ulong newAncestorsForLesser = (_ancestors[greater] | greaterBit) & ~_ancestors[lesser] & _allMask;
        if (newAncestorsForLesser != 0)
        {
            foreach (int below in EnumerateBits(_descendants[lesser] | lesserBit))
                _ancestors[below] |= newAncestorsForLesser;
        }

        ulong newDescendantsForGreater = (_descendants[lesser] | lesserBit) & ~_descendants[greater] & _allMask;
        if (newDescendantsForGreater != 0)
        {
            foreach (int above in EnumerateBits(_ancestors[greater] | greaterBit))
                _descendants[above] |= newDescendantsForGreater;
        }
    }

    public void ApplyOrder(IReadOnlyList<int> sorted)
    {
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            for (int j = i + 1; j < sorted.Count; j++)
                AddRelation(sorted[i], sorted[j]);
        }
    }

    public void Eliminate(int k)
    {
        ulong removedMask = 0;
        foreach (int item in EnumerateBits(ActiveMask))
        {
            if (BitOperations.PopCount(_ancestors[item] & ActiveMask) >= k)
                removedMask |= Bit(item);
        }

        if (removedMask == 0)
            return;

        InvalidateDerivedCaches();
        ActiveMask &= ~removedMask;
        ActiveCount -= BitOperations.PopCount(removedMask);
    }

    public void Deactivate(ulong removedMask)
    {
        removedMask &= ActiveMask;
        if (removedMask == 0)
            return;

        InvalidateDerivedCaches();
        ActiveMask &= ~removedMask;
        ActiveCount -= BitOperations.PopCount(removedMask);
    }

    public int[] GetStructuralLabels()
    {
        if (_structuralLabelsCache is not null)
            return _structuralLabelsCache;

        _structuralLabelsCache = ComputeStructuralLabels(ActiveMask);
        return _structuralLabelsCache;
    }

    public IntSequenceKey GetCanonicalKey()
    {
        if (_canonicalKeyCache is not null)
            return _canonicalKeyCache.Value;

        _canonicalKeyCache = ComputeCanonicalForm(ActiveMask, fixedTopMask: 0, highlightMask: 0);
        return _canonicalKeyCache.Value;
    }

    public IntSequenceKey GetDisplayCanonicalKey(ulong fixedTopMask)
    {
        return ComputeCanonicalForm(ActiveMask | fixedTopMask, fixedTopMask, highlightMask: 0);
    }

    // Produces a COMPLETE isomorphism invariant of a comparison group within the active
    // sub-poset by canonicalizing the state with the group's members highlighted as a
    // distinct color. Two groups share a key iff some automorphism of the poset maps one
    // onto the other (i.e. they spawn isomorphic search subtrees). The 1-WL signature this
    // replaces is incomplete and can merge genuinely distinct groups, which makes the search
    // drop a uniquely optimal group and over-estimate the worst-case step count.
    public IntSequenceKey GetGroupCanonicalKey(ulong groupMask)
    {
        return ComputeCanonicalForm(ActiveMask, fixedTopMask: 0, highlightMask: groupMask);
    }

    // Produces a COMPLETE canonical invariant of the included sub-poset via
    // individualization-refinement (a McKay-style canonical labeling). 1-WL color
    // refinement alone is not a complete graph invariant: non-isomorphic posets can share
    // a refined coloring and therefore collide. Such collisions corrupt the exact-step
    // caches (a hard state inherits an easier isomorphism-class's cached cost), which in
    // turn breaks compact selection's step-budget invariant. Individualization-refinement
    // distinguishes every non-isomorphic state while still mapping isomorphic states (and
    // fixed-top elements onto fixed-top elements) to an identical key.
    private IntSequenceKey ComputeCanonicalForm(ulong includedMask, ulong fixedTopMask, ulong highlightMask)
    {
        int n = _n;
        var verts = new int[n];
        int a = 0;
        ulong remaining = includedMask;
        while (remaining != 0)
        {
            int i = BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            verts[a++] = i;
        }

        if (a == 0)
            return new IntSequenceKey(new[] { 0 });

        // Position-space adjacency over the active vertices: anc[p] holds the positions
        // dominated by verts[p] (i.e. verts[p] is an ancestor / strictly greater).
        var pos = new int[n];
        for (int p = 0; p < a; p++)
            pos[verts[p]] = p;

        var anc = new ulong[a];
        var desc = new ulong[a];
        for (int p = 0; p < a; p++)
        {
            ulong upMask = _ancestors[verts[p]] & includedMask;
            while (upMask != 0)
            {
                int b = BitOperations.TrailingZeroCount(upMask);
                upMask &= upMask - 1;
                // verts[p]'s ancestor b means b > verts[p]; record as desc edge b->p.
                desc[p] |= 1UL << pos[b];
            }

            ulong downMask = _descendants[verts[p]] & includedMask;
            while (downMask != 0)
            {
                int b = BitOperations.TrailingZeroCount(downMask);
                downMask &= downMask - 1;
                anc[p] |= 1UL << pos[b];
            }
        }

        // Seed colors distinguish fixed-top elements from ordinary active candidates so the
        // canonicalization never maps a guaranteed-top item onto a still-contested one. A
        // highlighted group (if any) gets a further distinct color so a group's canonical key
        // is a complete invariant of (state, group) up to automorphism.
        var seed = new int[a];
        for (int p = 0; p < a; p++)
        {
            ulong bit = 1UL << verts[p];
            int s = (fixedTopMask & bit) != 0 ? 1 : 0;
            if ((highlightMask & bit) != 0)
                s += 2;
            seed[p] = s;
        }

        var refined = RefineCanonicalColoring(a, anc, desc, seed);

        int[]? best = null;
        CanonicalizeRecursive(a, anc, desc, seed, refined, ref best);
        return new IntSequenceKey(best!);
    }

    // Refines a coloring to the 1-WL fixed point using ancestor/descendant color multisets.
    private static int[] RefineCanonicalColoring(int a, ulong[] anc, ulong[] desc, int[] colors)
    {
        var labels = (int[])colors.Clone();
        var order = new int[a];
        var perm = new int[a];

        bool changed;
        do
        {
            int classCount = 0;
            for (int i = 0; i < a; i++)
                if (labels[i] > classCount)
                    classCount = labels[i];
            classCount++;

            int width = 1 + 2 * classCount;
            var sig = new int[a * width];
            for (int i = 0; i < a; i++)
            {
                int baseIdx = i * width;
                sig[baseIdx] = labels[i];

                ulong up = desc[i];
                while (up != 0)
                {
                    int b = BitOperations.TrailingZeroCount(up);
                    up &= up - 1;
                    sig[baseIdx + 1 + labels[b]]++;
                }

                ulong down = anc[i];
                while (down != 0)
                {
                    int b = BitOperations.TrailingZeroCount(down);
                    down &= down - 1;
                    sig[baseIdx + 1 + classCount + labels[b]]++;
                }

                order[i] = i;
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

            var nextLabels = new int[a];
            int color = 0;
            for (int r = 0; r < a; r++)
            {
                if (r > 0 && CompareCanonicalSignatures(sig, perm[r - 1], perm[r], width) != 0)
                    color++;
                nextLabels[order[perm[r]]] = color;
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

            labels = nextLabels;
        }
        while (changed);

        return labels;
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

    private static void CanonicalizeRecursive(int a, ulong[] anc, ulong[] desc, int[] seed, int[] colors, ref int[]? best)
    {
        int classCount = 0;
        for (int i = 0; i < a; i++)
            if (colors[i] + 1 > classCount)
                classCount = colors[i] + 1;

        // Find the smallest color value owning more than one vertex (the target cell).
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
            // Discrete coloring: colors form a bijection onto 0..a-1, giving a canonical order.
            var candidate = ReadCanonicalKey(a, anc, seed, colors);
            if (best is null || CompareKeyArrays(candidate, best) < 0)
                best = candidate;
            return;
        }

        for (int p = 0; p < a; p++)
        {
            if (colors[p] != targetColor)
                continue;

            // Automorphism pruning: skip p when an earlier same-cell vertex is interchangeable
            // with it (identical poset relations to every other vertex and mutually incomparable).
            // Individualizing interchangeable vertices yields isomorphic colored posets and hence
            // the same canonical sub-key, so trying only one representative per interchangeability
            // class cannot change the minimum. This collapses the otherwise factorial branching on
            // large symmetric cells (e.g. antichains) to one branch per class.
            bool redundant = false;
            for (int q = 0; q < p; q++)
            {
                if (colors[q] == targetColor && AreInterchangeable(p, q, anc, desc))
                {
                    redundant = true;
                    break;
                }
            }

            if (redundant)
                continue;

            // Individualize p: it sorts ahead of its former cell-mates; all other cells keep
            // their relative order. Re-refine, then recurse.
            var individualized = new int[a];
            for (int i = 0; i < a; i++)
                individualized[i] = 2 * colors[i] + (colors[i] == targetColor && i != p ? 1 : 0);

            var refined = RefineCanonicalColoring(a, anc, desc, individualized);
            CanonicalizeRecursive(a, anc, desc, seed, refined, ref best);
        }
    }

    // Two vertices (positions) are interchangeable when swapping them is an automorphism of the
    // colored poset: they must be mutually incomparable and have identical ancestor/descendant
    // relations to every other vertex.
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
        // colors[v] is the canonical rank of vertex v (a permutation of 0..a-1).
        var byRank = new int[a];
        for (int v = 0; v < a; v++)
            byRank[colors[v]] = v;

        // Per canonical row: the seed flag, then the ancestor relation to every other canonical
        // column packed as two ints (a <= 64). This is a complete invariant in canonical order.
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
                row |= 1UL << colors[b];
            }

            parts[w++] = (int)(row & 0xFFFFFFFF);
            parts[w++] = (int)(row >> 32);
        }

        return parts;
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

    internal ulong GetAncestorMask(int item)
    {
        return _ancestors[item];
    }

    internal ulong GetDescendantMask(int item)
    {
        return _descendants[item];
    }

    [ThreadStatic] private static int[]? _scratchNextLabels;
    [ThreadStatic] private static int[]? _scratchOrder;
    [ThreadStatic] private static int[]? _scratchPerm;
    [ThreadStatic] private static int[]? _scratchSig;

    private static int[] EnsureScratch(ref int[]? buffer, int minLength)
    {
        if (buffer is null || buffer.Length < minLength)
            buffer = new int[minLength];
        return buffer;
    }

    private static int CompareSignatures(int[] sig, int posLeft, int posRight, int width)
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

    private int[] ComputeStructuralLabels(ulong includedMask)
    {
        int n = _n;
        var labels = new int[n];
        bool anyInactive = false;
        for (int i = 0; i < n; i++)
        {
            if ((includedMask & (1UL << i)) == 0)
            {
                anyInactive = true;
            }
            else
            {
                labels[i] = 1;
            }
        }

        int maxWidth = 1 + (2 * n);
        int[] nextLabels = EnsureScratch(ref _scratchNextLabels, n);
        int[] order = EnsureScratch(ref _scratchOrder, n);
        int[] perm = EnsureScratch(ref _scratchPerm, n);
        int[] sig = EnsureScratch(ref _scratchSig, n * maxWidth);

        bool changed;
        do
        {
            int classCount = 0;
            for (int i = 0; i < n; i++)
            {
                if (labels[i] > classCount)
                    classCount = labels[i];
            }
            classCount++;
            int width = 1 + (2 * classCount);

            // Build a per-element signature slice: [currentLabel, ancestor-counts-by-class,
            // descendant-counts-by-class]. Inactive elements are excluded here; they always
            // sort ahead of every active signature and therefore always receive color 0.
            int activeCount = 0;
            for (int i = 0; i < n; i++)
            {
                if ((includedMask & (1UL << i)) == 0)
                    continue;

                int baseIdx = activeCount * width;
                for (int t = 0; t < width; t++)
                    sig[baseIdx + t] = 0;
                sig[baseIdx] = labels[i];

                ulong anc = _ancestors[i] & includedMask;
                while (anc != 0)
                {
                    int b = BitOperations.TrailingZeroCount(anc);
                    anc &= anc - 1;
                    sig[baseIdx + 1 + labels[b]]++;
                }

                ulong desc = _descendants[i] & includedMask;
                while (desc != 0)
                {
                    int b = BitOperations.TrailingZeroCount(desc);
                    desc &= desc - 1;
                    sig[baseIdx + 1 + classCount + labels[b]]++;
                }

                order[activeCount] = i;
                perm[activeCount] = activeCount;
                activeCount++;
            }

            // Insertion sort the active positions by lexicographic signature order (activeCount
            // is at most 64, so the quadratic sort is negligible and allocation-free).
            for (int a = 1; a < activeCount; a++)
            {
                int keyPos = perm[a];
                int b = a - 1;
                while (b >= 0 && CompareSignatures(sig, perm[b], keyPos, width) > 0)
                {
                    perm[b + 1] = perm[b];
                    b--;
                }

                perm[b + 1] = keyPos;
            }

            for (int i = 0; i < n; i++)
                nextLabels[i] = 0;

            int color = anyInactive ? 1 : 0;
            for (int r = 0; r < activeCount; r++)
            {
                if (r > 0 && CompareSignatures(sig, perm[r - 1], perm[r], width) != 0)
                    color++;
                nextLabels[order[perm[r]]] = color;
            }

            changed = false;
            for (int i = 0; i < n; i++)
            {
                if (labels[i] != nextLabels[i])
                {
                    changed = true;
                    break;
                }
            }

            if (changed)
                Array.Copy(nextLabels, labels, n);
        }
        while (changed);

        return labels;
    }

    public bool IsActive(int item)
    {
        return (ActiveMask & Bit(item)) != 0;
    }

    public bool HasAncestor(int item, int possibleAncestor)
    {
        return (_ancestors[item] & Bit(possibleAncestor)) != 0;
    }

    public int GetAncestorCount(int item)
    {
        return BitOperations.PopCount(_ancestors[item] & ActiveMask);
    }

    public int GetDescendantCount(int item)
    {
        return BitOperations.PopCount(_descendants[item] & ActiveMask);
    }

    public List<int> GetActiveItemsOrdered()
    {
        return MaskToOrderedList(ActiveMask);
    }

    public static List<int> MaskToOrderedList(ulong mask)
    {
        return EnumerateBits(mask).ToList();
    }

    // Builds a relabeling from this state's items (the referenced/original numbering) onto the
    // target state's items, such that it is an isomorphism of the combined (active + fixed-top)
    // partial order, mapping fixed-top items onto fixed-top items. Returns null if none is found,
    // in which case callers should omit the relabeling rather than show an unverified one.
    public IReadOnlyList<ItemRelabel>? TryBuildDisplayRelabeling(
        ulong fixedTopMask,
        ComparisonState target,
        ulong targetFixedTopMask)
    {
        ulong fromMask = ActiveMask | fixedTopMask;
        ulong toMask = target.ActiveMask | targetFixedTopMask;

        if (BitOperations.PopCount(fromMask) != BitOperations.PopCount(toMask))
            return null;

        int[] fromLabels = ComputeStructuralLabels(fromMask);
        int[] toLabels = target.ComputeStructuralLabels(toMask);

        var fromItems = MaskToOrderedList(fromMask);
        var toItems = MaskToOrderedList(toMask);

        // Assign the most-constrained items first to keep the backtracking shallow.
        fromItems.Sort((a, b) =>
        {
            int degreeA = BitOperations.PopCount(_ancestors[a] & fromMask) + BitOperations.PopCount(_descendants[a] & fromMask);
            int degreeB = BitOperations.PopCount(_ancestors[b] & fromMask) + BitOperations.PopCount(_descendants[b] & fromMask);
            return degreeB.CompareTo(degreeA);
        });

        var assignment = new Dictionary<int, int>(fromItems.Count);
        ulong usedTargets = 0;

        bool Assign(int index)
        {
            if (index == fromItems.Count)
                return true;

            int from = fromItems[index];
            bool fromFixed = (fixedTopMask & Bit(from)) != 0;

            foreach (int to in toItems)
            {
                if ((usedTargets & Bit(to)) != 0)
                    continue;
                if (fromLabels[from] != toLabels[to])
                    continue;
                if (((targetFixedTopMask & Bit(to)) != 0) != fromFixed)
                    continue;
                if (!IsRelabelingConsistent(from, to, assignment, target))
                    continue;

                assignment[from] = to;
                usedTargets |= Bit(to);
                if (Assign(index + 1))
                    return true;
                assignment.Remove(from);
                usedTargets &= ~Bit(to);
            }

            return false;
        }

        if (!Assign(0))
            return null;

        var relabels = new List<ItemRelabel>();
        foreach (int from in MaskToOrderedList(fromMask))
        {
            int to = assignment[from];
            if (from != to)
                relabels.Add(new ItemRelabel(from, to));
        }

        return relabels;
    }

    private bool IsRelabelingConsistent(int from, int to, Dictionary<int, int> assignment, ComparisonState target)
    {
        foreach (var pair in assignment)
        {
            int otherFrom = pair.Key;
            int otherTo = pair.Value;

            bool fromIsAncestor = (_ancestors[otherFrom] & Bit(from)) != 0;
            bool toIsAncestor = (target._ancestors[otherTo] & Bit(to)) != 0;
            if (fromIsAncestor != toIsAncestor)
                return false;

            bool fromIsDescendant = (_descendants[otherFrom] & Bit(from)) != 0;
            bool toIsDescendant = (target._descendants[otherTo] & Bit(to)) != 0;
            if (fromIsDescendant != toIsDescendant)
                return false;
        }

        return true;
    }

    private static ulong Bit(int item)
    {
        return 1UL << item;
    }

    private static ulong CreateFullMask(int n)
    {
        return n == 64 ? ulong.MaxValue : (1UL << n) - 1;
    }

    private static IEnumerable<int> EnumerateBits(ulong mask)
    {
        while (mask != 0)
        {
            int bit = BitOperations.TrailingZeroCount(mask);
            yield return bit;
            mask &= mask - 1;
        }
    }
}
