using System.Collections.Generic;
using System.Linq;
using Xunit;

public sealed class StrategyBuilderProgressAndGroupingTests
{
	[Fact]
	public void CombinatoricsService_EnumerateCombinations_ReturnsExpectedLexicographicTuples()
	{
		int[] items = { 1, 2, 3, 4 };
		int probeCount = 0;

		List<List<int>> combinations = CombinatoricsService
			.EnumerateCombinations(items, 2, () => probeCount++)
			.ToList();

		Assert.True(probeCount > 0);
		Assert.Equal(6, combinations.Count);
		Assert.Equal(new[] { 1, 2 }, combinations[0]);
		Assert.Equal(new[] { 1, 3 }, combinations[1]);
		Assert.Equal(new[] { 1, 4 }, combinations[2]);
		Assert.Equal(new[] { 2, 3 }, combinations[3]);
		Assert.Equal(new[] { 2, 4 }, combinations[4]);
		Assert.Equal(new[] { 3, 4 }, combinations[5]);
	}

	[Fact]
	public void SearchStateKeyService_BuildSearchStateKey_ReusesCanonicalMemoByRawStructure()
	{
		var state = new ComparisonState(5);
		var memo = new Dictionary<RawStructureKey, IntSequenceKey>();

		SearchStateKey keyA = SearchStateKeyService.BuildSearchStateKey(state, remainingSlots: 4, memo);
		SearchStateKey keyB = SearchStateKeyService.BuildSearchStateKey(state, remainingSlots: 2, memo);

		Assert.Equal(1, memo.Count);
		Assert.Equal(keyA.StateKey, keyB.StateKey);
		Assert.Equal(4, keyA.RemainingSlots);
		Assert.Equal(2, keyB.RemainingSlots);
	}

	[Fact]
	public void SearchStateKeyService_BuildSearchStateKey_AddsMemoEntryWhenStructureChanges()
	{
		var state = new ComparisonState(5);
		var memo = new Dictionary<RawStructureKey, IntSequenceKey>();

		SearchStateKey baseline = SearchStateKeyService.BuildSearchStateKey(state, remainingSlots: 4, memo);
		Assert.Equal(1, memo.Count);

		state.ApplyOrder(new[] { 0, 1, 2 });
		SearchStateKey changed = SearchStateKeyService.BuildSearchStateKey(state, remainingSlots: 4, memo);

		Assert.Equal(2, memo.Count);
		Assert.NotEqual(baseline.StateKey, changed.StateKey);
	}

	[Fact]
	public void SearchStateKeyService_GetDisplayStateKey_MatchesComparisonStateDisplayKey()
	{
		var state = new ComparisonState(4);
		state.ApplyOrder(new[] { 0, 1, 2 });
		ulong fixedTopMask = 1UL << 0;

		IntSequenceKey expected = state.GetDisplayCanonicalKey(fixedTopMask);
		IntSequenceKey actual = SearchStateKeyService.GetDisplayStateKey(state, fixedTopMask);

		Assert.Equal(expected, actual);
	}

	[Fact]
	public void SearchStateKeyService_GetDisplayStateKey_ZeroMaskMatchesCanonicalKey()
	{
		var state = new ComparisonState(5);
		state.ApplyOrder(new[] { 0, 2, 4 });

		IntSequenceKey canonical = state.GetCanonicalKey();
		IntSequenceKey displayZeroMask = SearchStateKeyService.GetDisplayStateKey(state, fixedTopMask: 0);

		Assert.Equal(canonical, displayZeroMask);
	}

	[Fact]
	public void CombinatoricsService_EnumerateCombinations_HandlesZeroAndOversizedCounts()
	{
		int[] items = { 1, 2, 3 };

		List<List<int>> zeroCount = CombinatoricsService
			.EnumerateCombinations(items, count: 0, probeCancellation: () => { })
			.ToList();

		List<List<int>> oversizedCount = CombinatoricsService
			.EnumerateCombinations(items, count: 5, probeCancellation: () => { })
			.ToList();

		Assert.Single(zeroCount);
		Assert.Empty(zeroCount[0]);
		Assert.Empty(oversizedCount);
	}

	[Fact]
	public void BranchSelectionScoringService_BuildScoreComponents_ReflectsGuaranteedAndPairMetrics()
	{
		var state = new ComparisonState(4);
		state.ApplyOrder(new[] { 0, 1, 2, 3 });
		var group = new List<int> { 0, 1, 2 };

		var score = BranchSelectionScoringService.BuildScoreComponents(state, remainingSlots: 2, group);

		Assert.Equal(2, score.GuaranteedTopHits);
		Assert.Equal(3, score.GroupSize);
		Assert.Equal(0, score.FreshItems);
		Assert.Equal(0, score.UnresolvedPairs);
		Assert.True(score.UnrelatedScore < 0);
	}
}

