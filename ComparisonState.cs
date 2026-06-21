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
    private static readonly IntSequenceKey InactiveSignature = new(new[] { 0 });
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

    private int[] ComputeStructuralLabels(ulong includedMask)
    {
        var labels = Enumerable.Range(0, _n)
            .Select(i => (includedMask & Bit(i)) == 0 ? 0 : 1)
            .ToArray();

        bool changed;
        do
        {
            changed = false;
            int classCount = labels.Max() + 1;
            var signatures = new IntSequenceKey[_n];
            for (int i = 0; i < _n; i++)
            {
                signatures[i] = (includedMask & Bit(i)) == 0
                    ? InactiveSignature
                    : BuildElementSignature(labels[i], _ancestors[i] & includedMask, _descendants[i] & includedMask, labels, classCount);
            }

            var orderedSignatures = signatures
                .Distinct()
                .OrderBy(signature => signature)
                .ToList();

            var signatureToColor = new Dictionary<IntSequenceKey, int>(orderedSignatures.Count);
            for (int index = 0; index < orderedSignatures.Count; index++)
                signatureToColor[orderedSignatures[index]] = index;

            var nextLabels = signatures.Select(signature => signatureToColor[signature]).ToArray();
            changed = !labels.SequenceEqual(nextLabels);
            labels = nextLabels;
        }
        while (changed);

        return labels;
    }

    private IntSequenceKey BuildCanonicalKey(ulong includedMask, IReadOnlyList<int> labels)
    {
        var includedClassIds = Enumerable.Range(0, _n)
            .Where(i => (includedMask & Bit(i)) != 0)
            .Select(i => labels[i])
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var parts = new List<int> { includedClassIds.Count };
        foreach (int classId in includedClassIds)
        {
            var members = Enumerable.Range(0, _n)
                .Where(i => (includedMask & Bit(i)) != 0 && labels[i] == classId)
                .ToList();

            parts.Add(members.Count);
            parts.Add(1);

            foreach (int otherClass in includedClassIds)
            {
                var counts = members
                    .Select(member => CountNeighborsWithLabel(_ancestors[member] & includedMask, labels, otherClass))
                    .OrderBy(x => x);
                parts.AddRange(counts);
            }

            foreach (int otherClass in includedClassIds)
            {
                var counts = members
                    .Select(member => CountNeighborsWithLabel(_descendants[member] & includedMask, labels, otherClass))
                    .OrderBy(x => x);
                parts.AddRange(counts);
            }
        }

        return new IntSequenceKey(parts.ToArray());
    }

    private static IntSequenceKey BuildElementSignature(
        int currentLabel,
        ulong ancestorMask,
        ulong descendantMask,
        IReadOnlyList<int> labels,
        int classCount)
    {
        int[] parts = new int[1 + (2 * classCount)];
        parts[0] = currentLabel;
        AddNeighborCounts(parts, 1, ancestorMask, labels);
        AddNeighborCounts(parts, 1 + classCount, descendantMask, labels);
        return new IntSequenceKey(parts);
    }

    private static void AddNeighborCounts(int[] target, int offset, ulong mask, IReadOnlyList<int> labels)
    {
        foreach (int item in EnumerateBits(mask))
            target[offset + labels[item]]++;
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
