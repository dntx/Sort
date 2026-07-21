using System.Collections.Generic;

static class SearchStateKeyService
{
    internal static SearchStateKey BuildSearchStateKey(
        ComparisonState state,
        int remainingSlots,
        Dictionary<RawStructureKey, IntSequenceKey> canonicalKeyMemo)
    {
        return new SearchStateKey(remainingSlots, GetCanonicalKeyMemoized(state, canonicalKeyMemo));
    }

    internal static IntSequenceKey GetDisplayStateKey(ComparisonState state, ulong fixedTopMask)
    {
        return state.GetDisplayCanonicalKey(fixedTopMask);
    }

    private static IntSequenceKey GetCanonicalKeyMemoized(
        ComparisonState state,
        Dictionary<RawStructureKey, IntSequenceKey> canonicalKeyMemo)
    {
        RawStructureKey rawKey = state.GetRawStructureKey();
        if (canonicalKeyMemo.TryGetValue(rawKey, out IntSequenceKey cached))
            return cached;

        IntSequenceKey canonical = state.GetCanonicalKey();
        canonicalKeyMemo[rawKey] = canonical;
        return canonical;
    }
}