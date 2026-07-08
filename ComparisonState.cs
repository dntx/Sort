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

// A cheap, non-canonical fingerprint of the active sub-poset structure: the active mask plus each
// active vertex's ancestor set restricted to active items. Two states share this key iff they have
// literally identical relation structure (before relabeling), which guarantees identical canonical
// keys. Used to memoize the expensive McKay canonicalization across the many distinct state instances
// that the same logical poset spawns along different search paths.
readonly struct RawStructureKey : IEquatable<RawStructureKey>
{
    private readonly ulong[] _words;
    private readonly int _hashCode;

    public RawStructureKey(ulong[] words)
    {
        _words = words;
        var hash = new HashCode();
        foreach (ulong w in words)
            hash.Add(w);
        _hashCode = hash.ToHashCode();
    }

    public bool Equals(RawStructureKey other)
    {
        if (_words.Length != other._words.Length)
            return false;

        for (int i = 0; i < _words.Length; i++)
        {
            if (_words[i] != other._words[i])
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is RawStructureKey other && Equals(other);

    public override int GetHashCode() => _hashCode;
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
    private Dictionary<ulong, IntSequenceKey>? _displayCanonicalKeyCache;
    private Dictionary<ulong, IntSequenceKey>? _groupCanonicalKeyCache;

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
        _displayCanonicalKeyCache = null;
        _groupCanonicalKeyCache = null;
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

    // Cheap non-canonical fingerprint of the active sub-poset (active mask + each active vertex's
    // ancestor set restricted to active items). Fully determines GetCanonicalKey (which reads only
    // these), so it is a sound memoization key for the expensive canonicalization. O(active) to build.
    public RawStructureKey GetRawStructureKey()
    {
        var words = new ulong[ActiveCount + 1];
        words[0] = ActiveMask;
        int p = 1;
        ulong remaining = ActiveMask;
        while (remaining != 0)
        {
            int i = BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            words[p++] = _ancestors[i] & ActiveMask;
        }

        return new RawStructureKey(words);
    }

    public IntSequenceKey GetDisplayCanonicalKey(ulong fixedTopMask)
    {
        if (fixedTopMask == 0)
            return GetCanonicalKey();

        _displayCanonicalKeyCache ??= new Dictionary<ulong, IntSequenceKey>();
        if (_displayCanonicalKeyCache.TryGetValue(fixedTopMask, out IntSequenceKey cached))
            return cached;

        IntSequenceKey key = ComputeCanonicalForm(ActiveMask | fixedTopMask, fixedTopMask, highlightMask: 0);
        _displayCanonicalKeyCache[fixedTopMask] = key;
        return key;
    }

    // Produces a COMPLETE isomorphism invariant of a comparison group within the active
    // sub-poset by canonicalizing the state with the group's members highlighted as a
    // distinct color. Two groups share a key iff some automorphism of the poset maps one
    // onto the other (i.e. they spawn isomorphic search subtrees). The 1-WL signature this
    // replaces is incomplete and can merge genuinely distinct groups, which makes the search
    // drop a uniquely optimal group and over-estimate the worst-case step count.
    public IntSequenceKey GetGroupCanonicalKey(ulong groupMask)
    {
        if (groupMask == 0)
            return GetCanonicalKey();

        _groupCanonicalKeyCache ??= new Dictionary<ulong, IntSequenceKey>();
        if (_groupCanonicalKeyCache.TryGetValue(groupMask, out IntSequenceKey cached))
            return cached;

        IntSequenceKey key = ComputeCanonicalForm(ActiveMask, fixedTopMask: 0, highlightMask: groupMask);
        _groupCanonicalKeyCache[groupMask] = key;
        return key;
    }

    // Per-item 1-WL color of the active sub-poset (no group highlighted), matching the coloring
    // GetGroupCanonicalKey refines on top of. Colors are assigned in canonical order, so isomorphic
    // states produce identical color labelings. Returns an array indexed by item; inactive items
    // hold -1. Two groups can share a GetGroupCanonicalKey only if their members carry the same
    // multiset of these colors (an automorphism mapping one group onto the other must preserve the
    // coloring), so the sorted color multiset is a cheap necessary condition used to skip the
    // expensive canonical-key computation for groups that cannot match a target pattern.
    public int[] GetActiveItemColors()
    {
        int n = _n;
        var colors = new int[n];
        for (int i = 0; i < n; i++)
            colors[i] = -1;

        var verts = new int[n];
        int a = 0;
        ulong remaining = ActiveMask;
        while (remaining != 0)
        {
            int i = BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            verts[a++] = i;
        }

        if (a == 0)
            return colors;

        var pos = new int[n];
        for (int p = 0; p < a; p++)
            pos[verts[p]] = p;

        var anc = new ulong[a];
        var desc = new ulong[a];
        for (int p = 0; p < a; p++)
        {
            ulong upMask = _ancestors[verts[p]] & ActiveMask;
            while (upMask != 0)
            {
                int b = BitOperations.TrailingZeroCount(upMask);
                upMask &= upMask - 1;
                desc[p] |= 1UL << pos[b];
            }

            ulong downMask = _descendants[verts[p]] & ActiveMask;
            while (downMask != 0)
            {
                int b = BitOperations.TrailingZeroCount(downMask);
                downMask &= downMask - 1;
                anc[p] |= 1UL << pos[b];
            }
        }

        var seed = new int[a];
        int[] refined = RefineCanonicalColoring(a, anc, desc, seed);
        for (int p = 0; p < a; p++)
            colors[verts[p]] = refined[p];

        return colors;
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

    // Tests whether some automorphism of this state's poset (preserving the fixed-top vs. active
    // coloring) maps orderA[i] -> orderB[i] for every i. Existence proves the two orderings are a
    // genuine symmetry orbit of the decision state, so their branches are interchangeable up to
    // relabeling and may honestly merge into one displayed branch. Backtracking over a bijection of
    // (active | fixedTop) that extends the forced pairing and preserves the transitive
    // ancestor/descendant relation and the fixed-top coloring. orderA and orderB must be equal length.
    internal bool TryMapOrderByAutomorphism(ulong fixedTopMask, IReadOnlyList<int> orderA, IReadOnlyList<int> orderB)
        => TryFindOrderAutomorphism(fixedTopMask, orderA, orderB, out _);

    // Witness-returning form of TryMapOrderByAutomorphism. On success, automorphism is the full
    // bijection on (active | fixedTop) that extends orderA[i] -> orderB[i]; on failure it is null.
    // Used to render a relabeling-equivalence legend for genuine automorphism orbits that the
    // pattern engine cannot express as a single disjunction-free template.
    internal bool TryFindOrderAutomorphism(
        ulong fixedTopMask,
        IReadOnlyList<int> orderA,
        IReadOnlyList<int> orderB,
        out Dictionary<int, int>? automorphism)
    {
        automorphism = null;
        if (orderA.Count != orderB.Count)
            return false;

        ulong mask = ActiveMask | fixedTopMask;
        List<int> items = MaskToOrderedList(mask);

        var assignment = new Dictionary<int, int>(items.Count);
        ulong used = 0;

        bool Consistent(int from, int to)
        {
            if (((fixedTopMask >> from) & 1UL) != ((fixedTopMask >> to) & 1UL))
                return false;
            foreach (KeyValuePair<int, int> pair in assignment)
            {
                int of = pair.Key, ot = pair.Value;
                if (((_ancestors[of] >> from) & 1UL) != ((_ancestors[ot] >> to) & 1UL))
                    return false;
                if (((_descendants[of] >> from) & 1UL) != ((_descendants[ot] >> to) & 1UL))
                    return false;
            }
            return true;
        }

        for (int i = 0; i < orderA.Count; i++)
        {
            int from = orderA[i], to = orderB[i];
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
            if (!assignment.ContainsKey(item))
                unassigned.Add(item);

        bool Search(int idx)
        {
            if (idx == unassigned.Count)
                return true;
            int from = unassigned[idx];
            foreach (int to in items)
            {
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

    // Partitions the active items into "free symmetry classes": maximal sets of active items that
    // share identical active-restricted ancestor and descendant sets. Such a class is necessarily
    // an antichain whose members relate identically to every other item, so permuting its members
    // (and fixing everything else) is always an automorphism of the active poset. This matches the
    // equivalence captured by GetGroupCanonicalKey, which canonicalizes the active sub-poset over
    // ActiveMask with fixedTopMask 0. Each class is returned as an ascending list of item indices,
    // and classes are ordered by their smallest member.
    public List<List<int>> GetFreeSymmetryClasses()
    {
        var classes = new List<List<int>>();
        var keyToIndex = new Dictionary<(ulong Ancestors, ulong Descendants), int>();
        foreach (int item in EnumerateBits(ActiveMask))
        {
            var key = (_ancestors[item] & ActiveMask, _descendants[item] & ActiveMask);
            if (!keyToIndex.TryGetValue(key, out int index))
            {
                index = classes.Count;
                keyToIndex[key] = index;
                classes.Add(new List<int>());
            }

            classes[index].Add(item);
        }

        return classes;
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
