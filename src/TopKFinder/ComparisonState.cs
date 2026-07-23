using System;
using System.Numerics;
using System.Threading;
using System.Collections.Generic;

namespace TopKFinder;

class ComparisonState
{
    [ThreadStatic] private static CancellationToken _threadCancellationToken;

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

    internal static void SetThreadCancellationToken(CancellationToken cancellationToken)
    {
        _threadCancellationToken = cancellationToken;
    }

    private static void ThrowIfThreadCancellationRequested()
    {
        if (_threadCancellationToken.IsCancellationRequested)
            _threadCancellationToken.ThrowIfCancellationRequested();
    }

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

    private void RemoveFromActiveSet(ulong removedMask)
    {
        InvalidateDerivedCaches();
        ActiveMask &= ~removedMask;
        ActiveCount -= BitOperations.PopCount(removedMask);
    }

    private IntSequenceKey ComputeCanonicalKeyForMasks(ulong includedMask, ulong fixedTopMask, ulong highlightMask)
    {
        return ComparisonStateAlgorithms.ComputeCanonicalForm(
            _n,
            includedMask,
            fixedTopMask,
            highlightMask,
            _ancestors,
            _descendants,
            ThrowIfThreadCancellationRequested);
    }

    private IntSequenceKey GetMaskedCanonicalKey(
        ref Dictionary<ulong, IntSequenceKey>? cache,
        ulong cacheKey,
        ulong includedMask,
        ulong fixedTopMask,
        ulong highlightMask)
    {
        cache ??= new Dictionary<ulong, IntSequenceKey>();
        if (cache.TryGetValue(cacheKey, out IntSequenceKey cached))
            return cached;

        IntSequenceKey key = ComputeCanonicalKeyForMasks(includedMask, fixedTopMask, highlightMask);
        cache[cacheKey] = key;
        return key;
    }

    private void PropagateNewAncestors(int greater, int lesser, ulong greaterBit, ulong lesserBit)
    {
        ulong newAncestorsForLesser = (_ancestors[greater] | greaterBit) & ~_ancestors[lesser] & _allMask;
        if (newAncestorsForLesser == 0)
            return;

        ulong belowMask = _descendants[lesser] | lesserBit;
        while (belowMask != 0)
        {
            int below = BitOperations.TrailingZeroCount(belowMask);
            belowMask &= belowMask - 1;
            ThrowIfThreadCancellationRequested();
            _ancestors[below] |= newAncestorsForLesser;
        }
    }

    private void PropagateNewDescendants(int greater, int lesser, ulong greaterBit, ulong lesserBit)
    {
        ulong newDescendantsForGreater = (_descendants[lesser] | lesserBit) & ~_descendants[greater] & _allMask;
        if (newDescendantsForGreater == 0)
            return;

        ulong aboveMask = _ancestors[greater] | greaterBit;
        while (aboveMask != 0)
        {
            int above = BitOperations.TrailingZeroCount(aboveMask);
            aboveMask &= aboveMask - 1;
            ThrowIfThreadCancellationRequested();
            _descendants[above] |= newDescendantsForGreater;
        }
    }

    private void ApplyOrderFromPivot(IReadOnlyList<int> sorted, int pivot)
    {
        int greater = sorted[pivot];
        for (int j = pivot + 1; j < sorted.Count; j++)
        {
            ThrowIfThreadCancellationRequested();
            AddRelation(greater, sorted[j]);
        }
    }

    public void AddRelation(int greater, int lesser)
    {
        ThrowIfThreadCancellationRequested();
        ulong greaterBit = Bit(greater);
        ulong lesserBit = Bit(lesser);
        if ((_ancestors[lesser] & greaterBit) != 0)
            return;

        InvalidateDerivedCaches();

        PropagateNewAncestors(greater, lesser, greaterBit, lesserBit);
        PropagateNewDescendants(greater, lesser, greaterBit, lesserBit);
    }

    public void ApplyOrder(IReadOnlyList<int> sorted)
    {
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            ThrowIfThreadCancellationRequested();
            ApplyOrderFromPivot(sorted, i);
        }
    }

    public void Eliminate(int k)
    {
        ThrowIfThreadCancellationRequested();
        ulong removedMask = 0;
        ulong itemMask = ActiveMask;
        while (itemMask != 0)
        {
            int item = BitOperations.TrailingZeroCount(itemMask);
            itemMask &= itemMask - 1;
            ThrowIfThreadCancellationRequested();
            if (BitOperations.PopCount(_ancestors[item] & ActiveMask) >= k)
                removedMask |= Bit(item);
        }

        if (removedMask == 0)
            return;

        RemoveFromActiveSet(removedMask);
    }

    public void Deactivate(ulong removedMask)
    {
        removedMask &= ActiveMask;
        if (removedMask == 0)
            return;

        RemoveFromActiveSet(removedMask);
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

        _canonicalKeyCache = ComputeCanonicalKeyForMasks(
            ActiveMask,
            fixedTopMask: 0,
            highlightMask: 0);
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

        return GetMaskedCanonicalKey(
            ref _displayCanonicalKeyCache,
            fixedTopMask,
            ActiveMask | fixedTopMask,
            fixedTopMask,
            highlightMask: 0);
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

        return GetMaskedCanonicalKey(
            ref _groupCanonicalKeyCache,
            groupMask,
            ActiveMask,
            fixedTopMask: 0,
            highlightMask: groupMask);
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
        return ComparisonStateAlgorithms.GetActiveItemColors(
            _n,
            ActiveMask,
            _ancestors,
            _descendants,
            ThrowIfThreadCancellationRequested);
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
        return ComparisonStateAlgorithms.TryFindOrderAutomorphism(
            ActiveMask,
            fixedTopMask,
            _ancestors,
            _descendants,
            orderA,
            orderB,
            ThrowIfThreadCancellationRequested,
            out automorphism);
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
        ulong itemMask = ActiveMask;
        while (itemMask != 0)
        {
            int item = BitOperations.TrailingZeroCount(itemMask);
            itemMask &= itemMask - 1;
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
        var items = new List<int>(BitOperations.PopCount(mask));
        while (mask != 0)
        {
            int item = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            items.Add(item);
        }

        return items;
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
