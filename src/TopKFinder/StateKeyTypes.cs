using System;

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