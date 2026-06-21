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
    public ulong[] Ancestors { get; }
    public ulong[] Descendants { get; }
    public ulong ActiveMask { get; private set; }
    public int ActiveCount { get; private set; }
    private int[]? _structuralLabelsCache;
    private IntSequenceKey? _canonicalKeyCache;

    public ComparisonState(int n)
    {
        _n = n;
        _allMask = CreateFullMask(n);
        Ancestors = new ulong[n];
        Descendants = new ulong[n];
        ActiveMask = _allMask;
        ActiveCount = n;
    }

    private ComparisonState(int n, ulong[] ancestors, ulong[] descendants, ulong activeMask, int activeCount)
    {
        _n = n;
        _allMask = CreateFullMask(n);
        Ancestors = ancestors;
        Descendants = descendants;
        ActiveMask = activeMask;
        ActiveCount = activeCount;
    }

    public ComparisonState Clone()
    {
        return new ComparisonState(
            _n,
            (ulong[])Ancestors.Clone(),
            (ulong[])Descendants.Clone(),
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
        if ((Ancestors[lesser] & greaterBit) != 0)
            return;

        InvalidateDerivedCaches();

        ulong newAncestorsForLesser = (Ancestors[greater] | greaterBit) & ~Ancestors[lesser] & _allMask;
        if (newAncestorsForLesser != 0)
        {
            foreach (int below in EnumerateBits(Descendants[lesser] | lesserBit))
                Ancestors[below] |= newAncestorsForLesser;
        }

        ulong newDescendantsForGreater = (Descendants[lesser] | lesserBit) & ~Descendants[greater] & _allMask;
        if (newDescendantsForGreater != 0)
        {
            foreach (int above in EnumerateBits(Ancestors[greater] | greaterBit))
                Descendants[above] |= newDescendantsForGreater;
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
            if (BitOperations.PopCount(Ancestors[item] & ActiveMask) >= k)
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

        int n = Ancestors.Length;
        var labels = Enumerable.Range(0, n)
            .Select(i => IsActive(i) ? 1 : 0)
            .ToArray();

        bool changed;
        do
        {
            changed = false;
            int classCount = labels.Max() + 1;
            var signatures = new IntSequenceKey[n];
            for (int i = 0; i < n; i++)
            {
                signatures[i] = IsActive(i)
                    ? BuildElementSignature(labels[i], Ancestors[i] & ActiveMask, Descendants[i] & ActiveMask, labels, classCount)
                    : InactiveSignature;
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

        _structuralLabelsCache = labels;
        return labels;
    }

    public IntSequenceKey GetCanonicalKey()
    {
        if (_canonicalKeyCache is not null)
            return _canonicalKeyCache.Value;

        int n = Ancestors.Length;
        var labels = GetStructuralLabels();
        var activeClassIds = Enumerable.Range(0, n)
            .Where(IsActive)
            .Select(i => labels[i])
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var parts = new List<int> { activeClassIds.Count };

        foreach (int classId in activeClassIds)
        {
            var members = Enumerable.Range(0, n).Where(i => labels[i] == classId).ToList();
            int memberCount = members.Count;
            int representative = members[0];

            parts.Add(memberCount);
            parts.Add(IsActive(representative) ? 1 : 0);

            foreach (int otherClass in activeClassIds)
            {
                var counts = members
                    .Select(member => CountNeighborsWithLabel(Ancestors[member] & ActiveMask, labels, otherClass))
                    .OrderBy(x => x);

                parts.AddRange(counts);
            }

            foreach (int otherClass in activeClassIds)
            {
                var counts = members
                    .Select(member => CountNeighborsWithLabel(Descendants[member] & ActiveMask, labels, otherClass))
                    .OrderBy(x => x);

                parts.AddRange(counts);
            }
        }

        _canonicalKeyCache = new IntSequenceKey(parts.ToArray());
        return _canonicalKeyCache.Value;
    }

    public IntSequenceKey GetDisplayCanonicalKey(ulong fixedTopMask)
    {
        ulong combinedMask = ActiveMask | fixedTopMask;
        int n = Ancestors.Length;
        var labels = Enumerable.Range(0, n)
            .Select(i => (combinedMask & Bit(i)) == 0 ? 0 : 1)
            .ToArray();

        bool changed;
        do
        {
            changed = false;
            int classCount = labels.Max() + 1;
            var signatures = new IntSequenceKey[n];
            for (int i = 0; i < n; i++)
            {
                signatures[i] = (combinedMask & Bit(i)) == 0
                    ? new IntSequenceKey(new[] { 0 })
                    : BuildElementSignature(labels[i], Ancestors[i] & combinedMask, Descendants[i] & combinedMask, labels, classCount);
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

        var activeClassIds = Enumerable.Range(0, n)
            .Where(i => (combinedMask & Bit(i)) != 0)
            .Select(i => labels[i])
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var parts = new List<int> { activeClassIds.Count };
        foreach (int classId in activeClassIds)
        {
            var members = Enumerable.Range(0, n).Where(i => labels[i] == classId).ToList();
            parts.Add(members.Count);
            parts.Add(1);

            foreach (int otherClass in activeClassIds)
            {
                var counts = members
                    .Select(member => CountNeighborsWithLabel(Ancestors[member] & combinedMask, labels, otherClass))
                    .OrderBy(x => x);
                parts.AddRange(counts);
            }

            foreach (int otherClass in activeClassIds)
            {
                var counts = members
                    .Select(member => CountNeighborsWithLabel(Descendants[member] & combinedMask, labels, otherClass))
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
        return (Ancestors[item] & Bit(possibleAncestor)) != 0;
    }

    public int GetAncestorCount(int item)
    {
        return BitOperations.PopCount(Ancestors[item] & ActiveMask);
    }

    public int GetDescendantCount(int item)
    {
        return BitOperations.PopCount(Descendants[item] & ActiveMask);
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
