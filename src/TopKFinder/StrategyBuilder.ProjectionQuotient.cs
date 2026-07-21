using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace TopKFinder;

// Principle-D projection-orbit merging, generalized to multi-family components. This is the engine
// behind EnableProjectionOrbitMerging (Transitions.cs; default ON). It unions ALL parent orbits related
// by a projection automorphism -- including count>=2 symmetric families, not just single orderings --
// and renders each resulting component honestly in a STRUCTURAL QUOTIENT notation. BuildProjectionQuotientSummary
// is a dispatcher over several shapes:
//     top-anchored    A1 > {A2, #p} ; A = {chains} ; drop tail(A2)                       (canonical + shape A)
//     bottom-anchored {A1, #p} > A2 ; A = {chains} ; drop chain(A2)                      (shape B)
//     two-block       A1 > B1 > {A2, B2} ; A = {..}, B = {..} ; drop {chain(A2), B2}     (shape C1)
//     three-block     {A1, A2} > {A3, #p} ; A = {..} ; drop tail(#p)                     (shape C3)
// (E.g. the canonical shape: block A is the parent-automorphism orbit of chain heads, {A2, #p} is the
// post-projection brace, and "drop tail(A2)" is the structural, covariant drop.) A multi-family
// component is folded only when (a) the global-drop honesty guard ComponentIsSingleGlobalDropOrbit
// holds AND (b) one of the shape renderers can express it; any component that fails either test falls
// back to the legacy singleton merge (or an honest split), so this pass is never worse than the
// singleton-only merge and never worse than no merging at all. Setting the toggle off recovers the
// finer pre-merge split (kept for the on/off comparison tests and as a safety fallback).
partial class StrategyBuilder
{
    private const int ProjectionQuotientMinHeadCount = 3;
    private const int ProjectionQuotientMaxHeadCount = 4;
    private const int TopAnchoredQuotientExpectedOrderingCount = 4;

    // All-orbit projection merge. Unions every pair of parent orbits whose representatives are
    // related by a projection automorphism, then keeps a multi-orbit component folded only when it
    // is an honest single global-drop orbit and (for multi-family components) the structural quotient
    // renderer accepts it. Singleton-only components fold exactly as the legacy singleton pass does.
    private List<(List<MergedFamilyOutcome> Members, bool ProjectionMerged)> MergeOrbitsByProjection(
        ComparisonState state, List<List<MergedFamilyOutcome>> orbits)
    {
        var projectionCache = new Dictionary<ulong, (ComparisonState State, int[] Colors)>();
        return ProjectionKernel.MergeProjectionOrbits(
            orbits,
            areProjectionEquivalent: (left, right) =>
                TryProjectionAutomorphism(state, left, right, projectionCache),
            canFoldMultiFamilyComponent: ordered =>
                ComponentIsSingleGlobalDropOrbit(state, ordered, out _)
                && BuildProjectionQuotientSummary(state, ordered, ordered[0]) is not null,
            orderRepresentativeFirst: OrderRepresentativeFirst,
            getFamilyCount: outcome => outcome.Family.Count);
    }

    // Returns the component's families with the lexicographically-smallest representative first, so
    // the honesty guard and the structural renderer agree on which ordering is the representative.
    private static List<MergedFamilyOutcome> OrderRepresentativeFirst(List<MergedFamilyOutcome> families)
    {
        MergedFamilyOutcome representative = SelectOrbitRepresentative(families);
        var ordered = new List<MergedFamilyOutcome>(families.Count) { representative };
        foreach (MergedFamilyOutcome outcome in families)
        {
            if (!ReferenceEquals(outcome, representative))
                ordered.Add(outcome);
        }
        return ordered;
    }

    // Renders a multi-family projection component in the structural quotient notation. Supported
    // shapes (this is intentionally conservative -- any unsupported component returns null and is left
    // un-merged): the orderings range over exactly three heads forming a two-member symmetric block
    // (a genuine parent-automorphism pair) plus one partner, and the component is exactly the set of
    // orderings in which a block member is the unique maximum. Exactly one of {block loser A2, partner}
    // carries the doomed active tail, giving two mirror sub-shapes that fold to
    //
    //     canonical: A1 > {A2, #p} ; A = {<chain b1>, <chain b2>} ; drop tail(A2)
    //                (block members carry the chains, partner is a leaf; A2's tail is dropped)
    //     shape A:   A1 > {A2, #p} ; A = {#b1, #b2} ; drop tail(#p)
    //                (block members are leaves, partner carries the chain; the partner's tail is dropped)
    //
    // A1/A2 are the block's winner/loser roles; {A2, #p} is the post-projection brace; "drop tail(...)"
    // is the structural (covariant) drop of the doomed tail. Honesty of the folded representative
    // subtree is separately guaranteed by ComponentIsSingleGlobalDropOrbit; this method only validates
    // that the printed structure faithfully describes the component.
    private EquivalentOrderSummary? BuildProjectionQuotientSummary(
        ComparisonState state, List<MergedFamilyOutcome> line, MergedFamilyOutcome representative)
    {
        IReadOnlyList<int> repOrder = representative.Family.RepresentativeOrderItems;
        var headSet = new HashSet<int>(repOrder);
        if (headSet.Count < ProjectionQuotientMinHeadCount || headSet.Count > ProjectionQuotientMaxHeadCount)
            return null;

        foreach (MergedFamilyOutcome member in line)
        {
            if (!headSet.SetEquals(member.Family.RepresentativeOrderItems))
                return null;
        }

        // Two anchoring orientations over three heads, mirror images of each other:
        //   top-anchored    -- a block member is the unique MAXIMUM  (canonical + shape A)
        //   bottom-anchored -- a block member is the unique MINIMUM   (shape B)
        // plus two four-head shapes:
        //   two-block          -- two symmetric pairs, the two losers interchangeable post-drop (shape C1)
        //   three-block-partner -- a symmetric 3-block of leaves + a tailed partner (shape C3)
        return TryTopAnchoredQuotient(state, line, repOrder, headSet)
            ?? TryBottomAnchoredQuotient(state, line, repOrder, headSet)
            ?? TryTwoBlockQuotient(state, line, repOrder, headSet)
            ?? TryThreeBlockPartnerQuotient(state, line, repOrder, headSet);
    }

    // Top-anchored quotient: the three heads form a two-member symmetric block plus a partner, and the
    // component is exactly { orderings with a block member on top }. Two sub-shapes, distinguished by
    // which side has two members:
    //   canonical: block = the two tailed heads, partner = leaf   -> A = {chain1, chain2} ; drop tail(A2)
    //   shape A:   block = the two leaf heads,   partner = tailed  -> A = {#b1, #b2}       ; drop tail(#p)
    private EquivalentOrderSummary? TryTopAnchoredQuotient(
        ComparisonState state, List<MergedFamilyOutcome> line, IReadOnlyList<int> repOrder, HashSet<int> headSet)
    {
        if (headSet.Count != ProjectionQuotientMinHeadCount)
            return null;

        ulong active = state.ActiveMask;
        var tailedHeads = new List<int>();
        var leafHeads = new List<int>();
        foreach (int head in headSet)
        {
            if ((state.GetDescendantMask(head) & active) != 0)
                tailedHeads.Add(head);
            else
                leafHeads.Add(head);
        }

        List<int> blockHeads;
        int partner;
        bool blockCarriesTail;
        if (tailedHeads.Count == 2 && leafHeads.Count == 1)
        {
            blockHeads = tailedHeads;
            partner = leafHeads[0];
            blockCarriesTail = true;
        }
        else if (leafHeads.Count == 2 && tailedHeads.Count == 1)
        {
            blockHeads = leafHeads;
            partner = tailedHeads[0];
            blockCarriesTail = false;
        }
        else
        {
            return null;
        }

        blockHeads.Sort();
        int b1 = blockHeads[0];
        int b2 = blockHeads[1];

        // The two block heads must be a genuine parent-automorphism pair (so A1/A2 are symmetric).
        if (!state.TryMapOrderByAutomorphism(0, new[] { b1 }, new[] { b2 }))
            return null;

        // The printed quotient describes A1, A2 and the partner as three DISJOINT members, each with
        // its own active down-chain. If any two of their active down-sets overlapped, the
        // "A = {chain1, chain2}" / "{A2, #p}" structure would list a shared item twice, so the printed
        // shape would no longer faithfully describe the component -- refuse to fold (fall back to split).
        ulong ChainMask(int head) => (state.GetDescendantMask(head) & active) | (1UL << head);
        ulong maskB1 = ChainMask(b1);
        ulong maskB2 = ChainMask(b2);
        ulong maskPartner = ChainMask(partner);
        if ((maskB1 & maskB2) != 0 || (maskB1 & maskPartner) != 0 || (maskB2 & maskPartner) != 0)
            return null;

        // The doomed tail (block loser's for the canonical shape, partner's for shape A) must be a
        // single total chain so the "tail(...)" notation is unambiguous.
        string legend;
        if (blockCarriesTail)
        {
            string? chain1 = FormatActiveChain(state, b1);
            string? chain2 = FormatActiveChain(state, b2);
            if (chain1 is null || chain2 is null)
                return null;
            legend = $"A = {{{chain1}, {chain2}}} ; drop tail(A2)";
        }
        else
        {
            if (FormatActiveChain(state, partner) is null)
                return null;
            legend = $"A = {{#{b1 + 1}, #{b2 + 1}}} ; drop tail(#{partner + 1})";
        }

        // The component must be exactly { orderings of the three heads with a block member on top }.
        // Each family's representative must lead with a block head; parent symmetry then keeps every
        // expanded ordering block-led, and the partner can never reach rank 1 within the bucket.
        int totalCount = 0;
        foreach (MergedFamilyOutcome member in line)
        {
            int memberFirst = member.Family.RepresentativeOrderItems[0];
            if (memberFirst != b1 && memberFirst != b2)
                return null;
            totalCount = SaturatingAdd(totalCount, member.Family.Count);
        }

        // |block| * (|heads| - 1)! = 2 * 2! = 4 distinct orderings.
        if (totalCount != TopAnchoredQuotientExpectedOrderingCount)
            return null;

        int repFirst = repOrder[0];
        if (repFirst != b1 && repFirst != b2)
            return null;

        string patternText = $"A1 > {{A2, #{partner + 1}}}";
        return new EquivalentOrderSummary(totalCount, patternText, "2! x 2", legend);
    }

    // Bottom-anchored quotient (shape B): the mirror of the top-anchored case. The three heads form a
    // two-member symmetric block A = {b1, b2} plus a partner, and the component is exactly { orderings
    // with a block member as the unique MINIMUM }. Because the block loser A2 is the eliminated minimum,
    // its WHOLE chain is the covariant drop (not just its tail):
    //     {A1, #p} > A2 ; A = {chain(b1), chain(b2)} ; drop chain(A2)
    // The surviving block member A1 and the partner become interchangeable exactly once A2's chain is
    // dropped; that projection equivalence is separately certified by ComponentIsSingleGlobalDropOrbit.
    private EquivalentOrderSummary? TryBottomAnchoredQuotient(
        ComparisonState state, List<MergedFamilyOutcome> line, IReadOnlyList<int> repOrder, HashSet<int> headSet)
    {
        if (headSet.Count != ProjectionQuotientMinHeadCount)
            return null;

        ulong active = state.ActiveMask;

        // The block is the unique parent-automorphism pair among the three heads; the odd head is the
        // partner. (In shape B every head carries a chain, so the tailed/leaf split cannot pick it out.)
        var heads = headSet.ToList();
        heads.Sort();
        int b1 = -1;
        int b2 = -1;
        int symmetricPairs = 0;
        for (int i = 0; i < heads.Count; i++)
        {
            for (int j = i + 1; j < heads.Count; j++)
            {
                if (state.TryMapOrderByAutomorphism(0, new[] { heads[i] }, new[] { heads[j] }))
                {
                    symmetricPairs++;
                    b1 = heads[i];
                    b2 = heads[j];
                }
            }
        }
        if (symmetricPairs != 1)
            return null;
        int partner = heads.Single(h => h != b1 && h != b2);

        // Every family representative must end with a block head (the partner is never the minimum), and
        // the component must be the full 4-ordering orbit.
        int totalCount = 0;
        foreach (MergedFamilyOutcome member in line)
        {
            IReadOnlyList<int> order = member.Family.RepresentativeOrderItems;
            int last = order[order.Count - 1];
            if (last != b1 && last != b2)
                return null;
            totalCount = SaturatingAdd(totalCount, member.Family.Count);
        }
        if (totalCount != 4)
            return null;

        int repLast = repOrder[repOrder.Count - 1];
        if (repLast != b1 && repLast != b2)
            return null;

        // Each head owns a single total active chain, and the three chains must be disjoint so
        // "A = {chain(b1), chain(b2)}" plus the partner name three separate subtrees.
        string? chain1 = FormatActiveChain(state, b1);
        string? chain2 = FormatActiveChain(state, b2);
        if (chain1 is null || chain2 is null || FormatActiveChain(state, partner) is null)
            return null;

        ulong ChainMask(int head) => (state.GetDescendantMask(head) & active) | (1UL << head);
        if ((ChainMask(b1) & ChainMask(b2)) != 0
            || (ChainMask(b1) & ChainMask(partner)) != 0
            || (ChainMask(b2) & ChainMask(partner)) != 0)
            return null;

        // The covariant drop must be exactly the eliminated minimum's whole chain: the global common
        // doomed set has to equal the chain of the block member at the bottom of the representative (A2).
        ulong globalDrop = ~0UL;
        foreach (MergedFamilyOutcome member in line)
            globalDrop &= EliminatedMask(state, member);
        if (globalDrop != ChainMask(repLast))
            return null;

        string patternText = $"{{A1, #{partner + 1}}} > A2";
        string legend = $"A = {{{chain1}, {chain2}}} ; drop chain(A2)";
        return new EquivalentOrderSummary(totalCount, patternText, "2! x 2", legend);
    }

    // Two-block quotient (shape C1): the orderings range over FOUR heads forming two disjoint symmetric
    // pairs A = {A1, A2} and B = {B1, B2}. The component is exactly the 8 orderings A1 > B1 > {A2, B2}:
    // an A winner on top, a B member second, then the A loser and the other B member interchangeable
    // once A2's whole chain and B2 are dropped:
    //     A1 > B1 > {A2, B2} ; A = {chain(A1), chain(A2)}, B = {chain(B1), chain(B2)} ; drop {chain(A2), B2}
    // {A2, B2} is the projection-merged brace (a loser from each block); honesty is separately certified
    // by ComponentIsSingleGlobalDropOrbit.
    private EquivalentOrderSummary? TryTwoBlockQuotient(
        ComparisonState state, List<MergedFamilyOutcome> line, IReadOnlyList<int> repOrder, HashSet<int> headSet)
    {
        if (headSet.Count != ProjectionQuotientMaxHeadCount || repOrder.Count != ProjectionQuotientMaxHeadCount)
            return null;

        int a1 = repOrder[0];
        int b1 = repOrder[1];
        int x = repOrder[2];
        int y = repOrder[3];

        bool Sym(int p, int q) => state.TryMapOrderByAutomorphism(0, new[] { p }, new[] { q });

        // A2 is A1's symmetric partner among the last two heads; B2 is B1's. Each must resolve to exactly
        // one of {x, y}, the two must differ, and the two blocks must be distinct (A1 not ~ B1) -- so the
        // four heads split cleanly into the pairs {A1, A2} and {B1, B2}.
        bool a1x = Sym(a1, x);
        bool a1y = Sym(a1, y);
        bool b1x = Sym(b1, x);
        bool b1y = Sym(b1, y);
        int a2 = (a1x ^ a1y) ? (a1x ? x : y) : -1;
        int b2 = (b1x ^ b1y) ? (b1x ? x : y) : -1;
        if (a2 < 0 || b2 < 0 || a2 == b2 || Sym(a1, b1))
            return null;

        // Every family representative must be A1 > B1 > (the two losers in either order), and together
        // they must be the full 8-ordering orbit.
        int totalCount = 0;
        foreach (MergedFamilyOutcome member in line)
        {
            IReadOnlyList<int> order = member.Family.RepresentativeOrderItems;
            if (order.Count != ProjectionQuotientMaxHeadCount || order[0] != a1 || order[1] != b1)
                return null;
            if (!((order[2] == a2 && order[3] == b2) || (order[2] == b2 && order[3] == a2)))
                return null;
            totalCount = SaturatingAdd(totalCount, member.Family.Count);
        }
        if (totalCount != 8)
            return null;

        // Each head owns a single total active chain, and the four chains must be disjoint so the printed
        // A/B legends name four separate subtrees.
        string? chainA1 = FormatActiveChain(state, a1);
        string? chainA2 = FormatActiveChain(state, a2);
        string? chainB1 = FormatActiveChain(state, b1);
        string? chainB2 = FormatActiveChain(state, b2);
        if (chainA1 is null || chainA2 is null || chainB1 is null || chainB2 is null)
            return null;

        ulong active = state.ActiveMask;
        ulong ChainMask(int head) => (state.GetDescendantMask(head) & active) | (1UL << head);
        ulong maskA1 = ChainMask(a1);
        ulong maskA2 = ChainMask(a2);
        ulong maskB1 = ChainMask(b1);
        ulong maskB2 = ChainMask(b2);
        if ((maskA1 & maskA2) != 0 || (maskA1 & maskB1) != 0 || (maskA1 & maskB2) != 0
            || (maskA2 & maskB1) != 0 || (maskA2 & maskB2) != 0 || (maskB1 & maskB2) != 0)
            return null;

        // The covariant drop must be exactly the two losers' chains: A2's whole chain plus B2's.
        ulong globalDrop = ~0UL;
        foreach (MergedFamilyOutcome member in line)
            globalDrop &= EliminatedMask(state, member);
        if (globalDrop != (maskA2 | maskB2))
            return null;

        const string patternText = "A1 > B1 > {A2, B2}";
        string legend = $"A = {{{chainA1}, {chainA2}}}, B = {{{chainB1}, {chainB2}}} ; drop {{chain(A2), B2}}";
        return new EquivalentOrderSummary(totalCount, patternText, "2! x 2! x 2", legend);
    }

    // Three-block-partner quotient (shape C3): FOUR heads = a three-member symmetric block A of leaves
    // plus one tailed partner. The component is the 12 orderings {A1, A2} > {A3, #p}: two block members
    // on top, then the third block member A3 and the partner interchangeable at the bottom once the
    // partner's tail is dropped:
    //     {A1, A2} > {A3, #p} ; A = {#b1, #b2, #b3} ; drop tail(#p)
    // A3 and #p are the projection-merged pair (a block leaf and the partner-without-tail); honesty is
    // separately certified by ComponentIsSingleGlobalDropOrbit.
    private EquivalentOrderSummary? TryThreeBlockPartnerQuotient(
        ComparisonState state, List<MergedFamilyOutcome> line, IReadOnlyList<int> repOrder, HashSet<int> headSet)
    {
        if (headSet.Count != ProjectionQuotientMaxHeadCount || repOrder.Count != ProjectionQuotientMaxHeadCount)
            return null;

        ulong active = state.ActiveMask;

        // The block is three symmetric leaves; the partner is the single head carrying an active tail.
        var leaves = new List<int>();
        var tailed = new List<int>();
        foreach (int head in headSet)
        {
            if ((state.GetDescendantMask(head) & active) != 0)
                tailed.Add(head);
            else
                leaves.Add(head);
        }
        if (leaves.Count != ProjectionQuotientMinHeadCount || tailed.Count != 1)
            return null;
        int partner = tailed[0];

        // The three block leaves must be mutually parent-automorphism symmetric.
        for (int i = 0; i < leaves.Count; i++)
        {
            for (int j = i + 1; j < leaves.Count; j++)
            {
                if (!state.TryMapOrderByAutomorphism(0, new[] { leaves[i] }, new[] { leaves[j] }))
                    return null;
            }
        }

        // Every family representative must place the partner in the bottom two positions (rank 3 or 4),
        // leaving the other three positions to block members; together they are the full 12-order orbit.
        int totalCount = 0;
        foreach (MergedFamilyOutcome member in line)
        {
            IReadOnlyList<int> order = member.Family.RepresentativeOrderItems;
            if (order.Count != ProjectionQuotientMaxHeadCount)
                return null;
            if (order[2] != partner && order[3] != partner)
                return null;
            totalCount = SaturatingAdd(totalCount, member.Family.Count);
        }
        if (totalCount != 12)
            return null;

        if (repOrder[2] != partner && repOrder[3] != partner)
            return null;

        // The block leaves and the partner's chain must be disjoint single chains.
        leaves.Sort();
        string? chain1 = FormatActiveChain(state, leaves[0]);
        string? chain2 = FormatActiveChain(state, leaves[1]);
        string? chain3 = FormatActiveChain(state, leaves[2]);
        if (chain1 is null || chain2 is null || chain3 is null || FormatActiveChain(state, partner) is null)
            return null;

        ulong ChainMask(int head) => (state.GetDescendantMask(head) & active) | (1UL << head);
        ulong maskL0 = ChainMask(leaves[0]);
        ulong maskL1 = ChainMask(leaves[1]);
        ulong maskL2 = ChainMask(leaves[2]);
        ulong maskPartner = ChainMask(partner);
        if ((maskL0 & maskL1) != 0 || (maskL0 & maskL2) != 0 || (maskL1 & maskL2) != 0
            || (maskL0 & maskPartner) != 0 || (maskL1 & maskPartner) != 0 || (maskL2 & maskPartner) != 0)
            return null;

        // The covariant drop must be exactly the partner's tail: the partner SURVIVES (it is not the
        // eliminated one); dropping only its doomed down-set is what makes it and A3 interchangeable.
        ulong partnerTail = state.GetDescendantMask(partner) & active;
        ulong globalDrop = ~0UL;
        foreach (MergedFamilyOutcome member in line)
            globalDrop &= EliminatedMask(state, member);
        if (partnerTail == 0 || globalDrop != partnerTail)
            return null;

        string patternText = $"{{A1, A2}} > {{A3, #{partner + 1}}}";
        string legend =
            $"A = {{{chain1}, {chain2}, {chain3}}} ; drop tail(#{partner + 1})";
        return new EquivalentOrderSummary(totalCount, patternText, "3! x 2", legend);
    }

    // Formats a chain head together with its active down-set as "#a > #b > ...", or null when the
    // down-closure is not a single total chain (so the structural "tail(A2)" notation would be
    // ambiguous and the component must stay un-merged).
    private static string? FormatActiveChain(ComparisonState state, int head)
    {
        ulong active = state.ActiveMask;
        ulong members = (state.GetDescendantMask(head) & active) | (1UL << head);
        List<int> items = ComparisonState.MaskToOrderedList(members);

        items.Sort((a, b) =>
        {
            int da = BitOperations.PopCount(state.GetDescendantMask(a) & active);
            int db = BitOperations.PopCount(state.GetDescendantMask(b) & active);
            return db - da;
        });

        for (int i = 0; i + 1 < items.Count; i++)
        {
            if ((state.GetDescendantMask(items[i]) & (1UL << items[i + 1])) == 0)
                return null;
        }

        return string.Join(" > ", items.Select(item => $"#{item + 1}"));
    }
}
