using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

public sealed class ComparisonStateTests
{
    [Fact]
    public void AddRelation_PropagatesTransitiveClosure()
    {
        var state = new ComparisonState(4);

        state.AddRelation(0, 1);
        state.AddRelation(1, 2);

        Assert.True(state.HasAncestor(2, 0));
        Assert.Equal(2, state.GetAncestorCount(2));
        Assert.Equal(2, state.GetDescendantCount(0));
    }

    [Fact]
    public void Eliminate_RemovesItemsWithAtLeastKKnownAncestors()
    {
        var state = new ComparisonState(4);
        state.ApplyOrder(new[] { 0, 1, 2 });

        state.Eliminate(k: 2);

        Assert.Equal(new[] { 0, 1, 3 }, state.GetActiveItemsOrdered());
    }

    [Fact]
    public void CanonicalKey_IsStableAcrossIsomorphicStates()
    {
        var first = new ComparisonState(4);
        first.AddRelation(0, 1);
        first.AddRelation(2, 3);

        var second = new ComparisonState(4);
        second.AddRelation(0, 2);
        second.AddRelation(1, 3);

        Assert.Equal(first.GetCanonicalKey(), second.GetCanonicalKey());
    }

    [Fact]
    public void CanonicalKey_IsStableAcrossDenseIsomorphicStates()
    {
        var first = new ComparisonState(7);
        first.AddRelation(0, 2);
        first.AddRelation(0, 3);
        first.AddRelation(1, 3);
        first.AddRelation(1, 4);
        first.AddRelation(2, 5);
        first.AddRelation(3, 5);
        first.AddRelation(4, 6);

        var second = new ComparisonState(7);
        second.AddRelation(1, 4);
        second.AddRelation(1, 3);
        second.AddRelation(0, 3);
        second.AddRelation(0, 2);
        second.AddRelation(4, 6);
        second.AddRelation(3, 5);
        second.AddRelation(2, 5);

        Assert.Equal(first.GetCanonicalKey(), second.GetCanonicalKey());
    }

    [Fact]
    public void CanonicalKey_DiffersForNonIsomorphicStates()
    {
        var chain = new ComparisonState(4);
        chain.ApplyOrder(new[] { 0, 1, 2, 3 });

        var star = new ComparisonState(4);
        star.AddRelation(0, 1);
        star.AddRelation(0, 2);
        star.AddRelation(0, 3);

        Assert.NotEqual(chain.GetCanonicalKey(), star.GetCanonicalKey());
    }

    [Fact]
    public void ReadCanonicalKey_InvalidColorRank_ThrowsDeterministicInvariantException()
    {
        Type? algorithmsType = typeof(ComparisonState).Assembly.GetType("ComparisonStateAlgorithms");
        Assert.NotNull(algorithmsType);

        MethodInfo? readCanonicalKey = algorithmsType!.GetMethod(
            "ReadCanonicalKey",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(readCanonicalKey);

        object?[] args =
        {
            3,
            new ulong[] { 0UL, 0UL, 0UL },
            new[] { 0, 0, 0 },
            new[] { 0, 3, 1 },
        };

        TargetInvocationException ex = Assert.Throws<TargetInvocationException>(() => readCanonicalKey!.Invoke(null, args));
        InvalidOperationException inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("outside", inner.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisplayCanonicalKey_WithZeroMask_EqualsCanonicalKey()
    {
        var state = new ComparisonState(4);
        state.AddRelation(0, 1);
        state.AddRelation(2, 3);

        Assert.Equal(state.GetCanonicalKey(), state.GetDisplayCanonicalKey(fixedTopMask: 0));
    }

    [Fact]
    public void DisplayCanonicalKey_RepeatedCallsStayStable_AndMutationChangesKey()
    {
        var state = new ComparisonState(5);
        state.AddRelation(0, 1);
        ulong fixedTopMask = CreateMask(2);

        IntSequenceKey before = state.GetDisplayCanonicalKey(fixedTopMask);
        IntSequenceKey repeat = state.GetDisplayCanonicalKey(fixedTopMask);
        Assert.Equal(before, repeat);

        state.AddRelation(0, 2);
        IntSequenceKey after = state.GetDisplayCanonicalKey(fixedTopMask);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void GroupCanonicalKey_RepeatedCallsStayStable_AndMutationChangesKey()
    {
        var state = new ComparisonState(4);
        ulong groupMask = CreateMask(0, 1);

        IntSequenceKey before = state.GetGroupCanonicalKey(groupMask);
        IntSequenceKey repeat = state.GetGroupCanonicalKey(groupMask);
        Assert.Equal(before, repeat);

        state.AddRelation(0, 1);
        IntSequenceKey after = state.GetGroupCanonicalKey(groupMask);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ActiveItemColors_AreStableAcrossEquivalentRelationInsertionOrders()
    {
        var first = new ComparisonState(6);
        first.AddRelation(0, 1);
        first.AddRelation(1, 2);
        first.AddRelation(3, 4);
        first.AddRelation(0, 5);

        var second = new ComparisonState(6);
        second.AddRelation(3, 4);
        second.AddRelation(0, 5);
        second.AddRelation(1, 2);
        second.AddRelation(0, 1);

        Assert.Equal(first.GetActiveItemColors(), second.GetActiveItemColors());
    }

    [Fact]
    public void ActiveItemColors_RepeatedReadsStayStable_AndDoNotAffectCanonicalKey()
    {
        var state = new ComparisonState(6);
        state.AddRelation(0, 1);
        state.AddRelation(0, 2);
        state.AddRelation(3, 4);

        IntSequenceKey before = state.GetCanonicalKey();
        int[] first = state.GetActiveItemColors();
        int[] second = state.GetActiveItemColors();
        IntSequenceKey after = state.GetCanonicalKey();

        Assert.Equal(first, second);
        Assert.Equal(before, after);
    }

    [Fact]
    public void GuaranteedTopMask_IdentifiesForcedTopCandidates()
    {
        var builder = new StrategyBuilder(4, 2, 2);
        var state = new ComparisonState(4);
        state.AddRelation(0, 1);
        state.AddRelation(1, 2);

        ulong mask = builder.GetGuaranteedTopMaskForTesting(state, remainingSlots: 2);

        Assert.Equal(CreateMask(0), mask);
    }

    [Fact]
    public void FeasibleTopSetInfo_CountsAlternativeTopSets()
    {
        var builder = new StrategyBuilder(4, 2, 2);
        var state = new ComparisonState(4);
        state.AddRelation(0, 1);
        state.AddRelation(2, 3);

        FeasibleTopSetInfo info = builder.GetFeasibleTopSetInfoForTesting(state, remainingSlots: 2);

        Assert.Equal(3, info.Count);
        Assert.Equal(0UL, info.UniqueMask);
    }

    [Fact]
    public void FeasibleTopSetInfo_TracksUniqueDeterminedTopSet()
    {
        var builder = new StrategyBuilder(3, 2, 2);
        var state = new ComparisonState(3);
        state.ApplyOrder(new[] { 0, 1, 2 });

        FeasibleTopSetInfo info = builder.GetFeasibleTopSetInfoForTesting(state, remainingSlots: 2);

        Assert.Equal(1, info.Count);
        Assert.Equal(CreateMask(0, 1), info.UniqueMask);
    }

    [Fact]
    public void MinWorstCaseLowerBound_IsZeroWhenTopSetIsAlreadyDetermined()
    {
        var builder = new StrategyBuilder(3, 2, 2);
        var state = new ComparisonState(3);
        state.ApplyOrder(new[] { 0, 1, 2 });

        int lowerBound = builder.GetMinWorstCaseLowerBoundForTesting(state, remainingSlots: 2);

        Assert.Equal(0, lowerBound);
    }

    [Fact]
    public void MinWorstCaseLowerBound_UsesOutcomeCapacityForUndeterminedTopSets()
    {
        var builder = new StrategyBuilder(4, 2, 2);
        var state = new ComparisonState(4);
        state.AddRelation(0, 1);
        state.AddRelation(2, 3);

        int lowerBound = builder.GetMinWorstCaseLowerBoundForTesting(state, remainingSlots: 2);

        Assert.Equal(2, lowerBound);
    }

    // Regression: large m should keep information-theoretic lower-bound math stable without relying on
    // integer factorial accumulation/overflow behavior.
    [Fact]
    public void MinWorstCaseLowerBound_LargeM_LogDomainComputationRemainsStable()
    {
        var builder = new StrategyBuilder(25, 24, 1);
        var state = new ComparisonState(25);

        int lowerBound = builder.GetMinWorstCaseLowerBoundForTesting(state, remainingSlots: 1);

        Assert.True(lowerBound >= 1, "undetermined non-terminal state should keep a positive lower bound");
    }

    // Determinability floor: a normalized non-terminal state with activeCount > m provably needs at least
    // 2 comparisons -- a single step totally orders one group of m active items but cannot decide an active
    // item left outside that group, so the state is not determined in one step (full proof in
    // docs/core-algorithm.md sec 7.7). This state's active poset is the near-chain 1>0>2 plus two free
    // items (3,4). NormalizeState fixes the forced-top item 1, leaving 4 active items over 2 remaining slots
    // with width <= m, so the information-theoretic and width bounds only prove 1; the floor is what lifts
    // the bound to the true minimum of 2. The floor applies to both the greedy and the exact plan.
    [Fact]
    public void MinWorstCaseLowerBound_DeterminabilityFloor_LiftsNearChainToTwo()
    {
        int bound = new StrategyBuilder(5, 3, 3)
            .GetMinWorstCaseLowerBoundForTesting(NearChainWithFreeItems(), remainingSlots: 3);

        Assert.Equal(2, bound);
    }

    // Placement guard: the floor sits AFTER the base cases, so a state whose active count is exactly m must
    // still return the base-case value of 1 (line 354), not be lifted to 2. Three free items with two
    // remaining slots is undetermined and not forced-reduced by normalization, so activeCount stays m == 3.
    [Fact]
    public void MinWorstCaseLowerBound_DeterminabilityFloor_DoesNotLiftBaseCaseActiveCountEqualsM()
    {
        int bound = new StrategyBuilder(3, 3, 3)
            .GetMinWorstCaseLowerBoundForTesting(new ComparisonState(3), remainingSlots: 2);

        Assert.Equal(1, bound);
    }

    // Minimal hand-checkable witness of the floor's correctness (n=4, m=3, k=2). The active poset is just
    // a>b with two free items c,d -- all four survive Eliminate (each has < 2 active ancestors), so the
    // state the real search hands to the bound has activeCount=4 > m=3. The old bounds all prove only 1:
    // the widest antichain is {a,c,d} (width 3) giving ceil((3-1)/(3-1))=1, and the feasible top-2 count is
    // small enough that the information-theoretic bound is also 1. Yet opt is genuinely 2: any single group
    // of 3 leaves one active item f outside it, and since f has < 2 active ancestors it stays strictly
    // undecided in some outcome (e.g. comparing {a,c,d} and getting a>c>d leaves b vs c for the 2nd slot
    // unresolved), so no single step determines the top-2. The floor is what lifts the bound from 1 to the
    // true value of 2.
    [Fact]
    public void MinWorstCaseLowerBound_DeterminabilityFloor_LiftsSingleEdgeWithFreeItemsToTwo()
    {
        var state = new ComparisonState(4);
        state.AddRelation(0, 1); // a > b; items c=2, d=3 free

        int bound = new StrategyBuilder(4, 3, 2)
            .GetMinWorstCaseLowerBoundForTesting(state, remainingSlots: 2);

        Assert.Equal(2, bound);
    }

    // The raw structure key backs the cross-instance canonical-key memo. Its core soundness contract
    // is that structurally identical active sub-posets -- regardless of the order relations were added --
    // yield equal raw keys (so the memo hits) and, in turn, equal canonical keys.
    [Fact]
    public void RawStructureKey_EqualForIdenticalStructureBuiltInDifferentOrder()
    {
        var first = new ComparisonState(4);
        first.AddRelation(0, 1);
        first.AddRelation(1, 2);

        var second = new ComparisonState(4);
        second.AddRelation(1, 2);
        second.AddRelation(0, 1);

        Assert.Equal(first.GetRawStructureKey(), second.GetRawStructureKey());
        Assert.Equal(first.GetRawStructureKey().GetHashCode(), second.GetRawStructureKey().GetHashCode());
        Assert.Equal(first.GetCanonicalKey(), second.GetCanonicalKey());
    }

    [Fact]
    public void RawStructureKey_DiffersForDifferentActiveStructure()
    {
        var chain = new ComparisonState(4);
        chain.ApplyOrder(new[] { 0, 1, 2, 3 });

        var star = new ComparisonState(4);
        star.AddRelation(0, 1);
        star.AddRelation(0, 2);
        star.AddRelation(0, 3);

        Assert.NotEqual(chain.GetRawStructureKey(), star.GetRawStructureKey());
    }

    // The raw key only reflects the ACTIVE sub-poset: a state whose eliminated items are pruned must
    // match a freshly built state that never had them, so the memo reuses the canonical key correctly.
    [Fact]
    public void RawStructureKey_ReflectsOnlyActiveSubPoset()
    {
        var eliminated = new ComparisonState(4);
        eliminated.ApplyOrder(new[] { 0, 1 }); // 0 > 1
        eliminated.Eliminate(k: 1);            // removes item 1 (>=1 known ancestor), leaving 0, 2, 3 active
        Assert.Equal(new[] { 0, 2, 3 }, eliminated.GetActiveItemsOrdered());

        var reference = new ComparisonState(4);
        reference.Deactivate(1UL << 1);        // three free active items 0, 2, 3

        Assert.Equal(reference.GetRawStructureKey(), eliminated.GetRawStructureKey());
        Assert.Equal(reference.GetCanonicalKey(), eliminated.GetCanonicalKey());
    }

    // Soundness property backing the memo: across a broad family of small posets, any two states that
    // share a raw structure key MUST share a canonical key. If this ever failed, memoizing the canonical
    // key by the raw key would return a wrong key and silently corrupt the search.
    [Fact]
    public void RawStructureKey_UniquelyDeterminesCanonicalKey()
    {
        const int n = 6;
        var byRawKey = new Dictionary<RawStructureKey, IntSequenceKey>();
        var rng = new Random(12345);

        for (int trial = 0; trial < 4000; trial++)
        {
            var state = new ComparisonState(n);
            int relations = rng.Next(0, 8);
            for (int r = 0; r < relations; r++)
            {
                int a = rng.Next(n);
                int b = rng.Next(n);
                if (a != b && !state.HasAncestor(a, b) && !state.HasAncestor(b, a))
                    state.AddRelation(a, b);
            }

            RawStructureKey raw = state.GetRawStructureKey();
            IntSequenceKey canonical = state.GetCanonicalKey();
            if (byRawKey.TryGetValue(raw, out IntSequenceKey seen))
                Assert.Equal(seen, canonical);
            else
                byRawKey[raw] = canonical;
        }
    }

    // Regression guard for the canonicalization workspace reuse change: a batch of branchy but
    // structurally distinct states should stay within a modest allocation budget when computing
    // canonical keys. Before the workspace reuse, the recursive canonicalizer allocated scratch
    // arrays repeatedly inside the search tree; this test fails if that behavior comes back.
    [Fact]
    public void CanonicalKey_BranchyStates_StayUnderAllocationBudget()
    {
        var warmup = BuildBranchyState(0, 2, 3);
        _ = warmup.GetCanonicalKey();

        var states = new List<ComparisonState>
        {
            BuildBranchyState(2, 3, 4),
            BuildBranchyState(2, 3, 5),
            BuildBranchyState(2, 3, 6),
            BuildBranchyState(2, 4, 5),
            BuildBranchyState(2, 4, 6),
            BuildBranchyState(2, 5, 6),
            BuildBranchyState(1, 3, 4),
            BuildBranchyState(1, 3, 5),
        };

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (ComparisonState state in states)
            _ = state.GetCanonicalKey();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated <= 1_500_000,
            $"expected branchy canonicalization batch to stay under 1.5 MB of allocations, got {allocated:n0} bytes");
    }

    private static ComparisonState NearChainWithFreeItems()
    {
        var state = new ComparisonState(5);
        state.AddRelation(1, 0); // 1 > 0
        state.AddRelation(0, 2); // 0 > 2  => chain 1 > 0 > 2, items 3 and 4 free
        return state;
    }

    private static ulong CreateMask(params int[] items)
    {
        ulong mask = 0;
        foreach (int item in items)
            mask |= 1UL << item;
        return mask;
    }

    private static ComparisonState BuildBranchyState(int first, int second, int third)
    {
        var state = new ComparisonState(7);
        state.AddRelation(0, 1);
        state.AddRelation(first, second);
        state.AddRelation(second, third);
        return state;
    }
}
