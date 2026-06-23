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

        var labels = GetStructuralLabels();
        _canonicalKeyCache = BuildCanonicalKey(ActiveMask, labels);
        return _canonicalKeyCache.Value;
    }

    public IntSequenceKey GetDisplayCanonicalKey(ulong fixedTopMask)
    {
        ulong combinedMask = ActiveMask | fixedTopMask;
        int[] labels = ComputeStructuralLabels(combinedMask);
        return BuildCanonicalKey(combinedMask, labels);
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

    private IntSequenceKey BuildCanonicalKey(ulong includedMask, IReadOnlyList<int> labels)
    {
        int n = _n;
        Span<bool> present = stackalloc bool[n];
        ulong remaining = includedMask;
        while (remaining != 0)
        {
            int i = BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            present[labels[i]] = true;
        }

        Span<int> classIds = stackalloc int[n];
        int classCount = 0;
        for (int c = 0; c < n; c++)
        {
            if (present[c])
                classIds[classCount++] = c;
        }

        var parts = new List<int> { classCount };
        Span<int> members = stackalloc int[n];
        Span<int> counts = stackalloc int[n];
        for (int ci = 0; ci < classCount; ci++)
        {
            int classId = classIds[ci];
            int memberCount = 0;
            ulong memberMask = includedMask;
            while (memberMask != 0)
            {
                int i = BitOperations.TrailingZeroCount(memberMask);
                memberMask &= memberMask - 1;
                if (labels[i] == classId)
                    members[memberCount++] = i;
            }

            parts.Add(memberCount);
            parts.Add(1);

            for (int phase = 0; phase < 2; phase++)
            {
                for (int oci = 0; oci < classCount; oci++)
                {
                    int otherClass = classIds[oci];
                    for (int mi = 0; mi < memberCount; mi++)
                    {
                        int member = members[mi];
                        ulong neighborMask = (phase == 0 ? _ancestors[member] : _descendants[member]) & includedMask;
                        counts[mi] = CountNeighborsWithLabel(neighborMask, labels, otherClass);
                    }

                    InsertionSort(counts, memberCount);
                    for (int mi = 0; mi < memberCount; mi++)
                        parts.Add(counts[mi]);
                }
            }
        }

        return new IntSequenceKey(parts.ToArray());
    }

    private static void InsertionSort(Span<int> values, int count)
    {
        for (int a = 1; a < count; a++)
        {
            int key = values[a];
            int b = a - 1;
            while (b >= 0 && values[b] > key)
            {
                values[b + 1] = values[b];
                b--;
            }

            values[b + 1] = key;
        }
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

    private static int CountNeighborsWithLabel(ulong mask, IReadOnlyList<int> labels, int targetLabel)
    {
        int count = 0;
        foreach (int item in EnumerateBits(mask))
        {
            if (labels[item] == targetLabel)
                count++;
        }

        return count;
    }
}
