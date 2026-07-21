using System.Collections.Generic;
using Xunit;
using TopKFinder;

public sealed class SearchStateKeyServiceTests
{
    [Fact]
    public void BuildSearchStateKey_ReusesCanonicalMemoByRawStructure()
    {
        var state = new ComparisonState(5);
        var memo = new Dictionary<RawStructureKey, IntSequenceKey>();

        SearchStateKey keyA = SearchStateKeyService.BuildSearchStateKey(state, remainingSlots: 4, memo);
        SearchStateKey keyB = SearchStateKeyService.BuildSearchStateKey(state, remainingSlots: 2, memo);

        Assert.Single(memo);
        Assert.Equal(keyA.StateKey, keyB.StateKey);
        Assert.Equal(4, keyA.RemainingSlots);
        Assert.Equal(2, keyB.RemainingSlots);
    }

    [Fact]
    public void BuildSearchStateKey_AddsMemoEntryWhenStructureChanges()
    {
        var state = new ComparisonState(5);
        var memo = new Dictionary<RawStructureKey, IntSequenceKey>();

        SearchStateKey baseline = SearchStateKeyService.BuildSearchStateKey(state, remainingSlots: 4, memo);
        Assert.Single(memo);

        state.ApplyOrder(new[] { 0, 1, 2 });
        SearchStateKey changed = SearchStateKeyService.BuildSearchStateKey(state, remainingSlots: 4, memo);

        Assert.Equal(2, memo.Count);
        Assert.NotEqual(baseline.StateKey, changed.StateKey);
    }

    [Fact]
    public void GetDisplayStateKey_MatchesComparisonStateDisplayKey()
    {
        var state = new ComparisonState(4);
        state.ApplyOrder(new[] { 0, 1, 2 });
        ulong fixedTopMask = 1UL << 0;

        IntSequenceKey expected = state.GetDisplayCanonicalKey(fixedTopMask);
        IntSequenceKey actual = SearchStateKeyService.GetDisplayStateKey(state, fixedTopMask);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GetDisplayStateKey_ZeroMaskMatchesCanonicalKey()
    {
        var state = new ComparisonState(5);
        state.ApplyOrder(new[] { 0, 2, 4 });

        IntSequenceKey canonical = state.GetCanonicalKey();
        IntSequenceKey displayZeroMask = SearchStateKeyService.GetDisplayStateKey(state, fixedTopMask: 0);

        Assert.Equal(canonical, displayZeroMask);
    }
}