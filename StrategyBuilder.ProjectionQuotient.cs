using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

// Principle-D projection-orbit merging, generalized to multi-family components. This is the engine
// behind EnableProjectionOrbitMerging (Transitions.cs). It unions ALL parent orbits related by a
// projection automorphism -- including count>=2 symmetric families, not just single orderings -- and
// renders the resulting component honestly in the STRUCTURAL QUOTIENT notation
//
//     A1 > {A2, #7} ; A = {#1 > #2, #4 > #5} ; drop tail(A2)
//
// where block A is the parent-automorphism orbit of chain heads (each shown WITH its active tail
// chain), {A2, #7} is the post-projection brace (A's loser and the partner leaf become
// interchangeable once A2's tail is dropped), and "drop tail(A2)" is the structural, covariant drop
// (A2 = #4 drops #5; A2 = #1 drops #2). A multi-family component is folded only when (a) the
// global-drop honesty guard ComponentIsSingleGlobalDropOrbit holds AND (b) the structural renderer
// can express it; any component that fails either test falls back to the legacy singleton merge, so
// this pass is never worse than the singleton-only merge and never worse than no merging at all. The
// default plan (toggle off) is unaffected.
partial class StrategyBuilder
{
    // All-orbit projection merge. Unions every pair of parent orbits whose representatives are
    // related by a projection automorphism, then keeps a multi-orbit component folded only when it
    // is an honest single global-drop orbit and (for multi-family components) the structural quotient
    // renderer accepts it. Singleton-only components fold exactly as the legacy singleton pass does.
    private List<(List<MergedFamilyOutcome> Members, bool ProjectionMerged)> MergeOrbitsByProjection(
        ComparisonState state, List<List<MergedFamilyOutcome>> orbits)
    {
        int n = orbits.Count;
        if (n < 2)
            return orbits.Select(orbit => (orbit, false)).ToList();

        var parent = new int[n];
        for (int i = 0; i < n; i++)
            parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (Find(i) == Find(j))
                    continue;
                if (TryProjectionAutomorphism(state, orbits[i][0], orbits[j][0]))
                    parent[Find(i)] = Find(j);
            }
        }

        var componentsByRoot = new Dictionary<int, List<int>>();
        var order = new List<int>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!componentsByRoot.TryGetValue(root, out List<int>? members))
            {
                members = new List<int>();
                componentsByRoot[root] = members;
                order.Add(root);
            }
            members.Add(i);
        }

        var result = new List<(List<MergedFamilyOutcome>, bool)>();
        foreach (int root in order)
        {
            List<int> component = componentsByRoot[root];
            if (component.Count == 1)
            {
                result.Add((orbits[component[0]], false));
                continue;
            }

            var flattened = new List<MergedFamilyOutcome>();
            foreach (int orbitIndex in component)
                flattened.AddRange(orbits[orbitIndex]);

            bool multiFamily = flattened.Any(outcome => outcome.Family.Count > 1);

            // A singleton-only component reproduces the legacy singleton merge exactly (the relabeling
            // summary renders it); multi-family components are the genuinely new case and must clear
            // both the honesty guard and the structural renderer before they fold.
            bool fold;
            if (!multiFamily)
            {
                fold = true;
            }
            else
            {
                List<MergedFamilyOutcome> ordered = OrderRepresentativeFirst(flattened);
                fold = ComponentIsSingleGlobalDropOrbit(state, ordered, out _)
                    && BuildProjectionQuotientSummary(state, ordered, ordered[0]) is not null;
            }

            if (fold)
            {
                result.Add((flattened, true));
            }
            else
            {
                // Unsupported multi-family shape: don't claim the quotient. Fall back to the legacy
                // singleton merge over this component's parent orbits so any singleton-vs-singleton
                // saving is still taken -- the all-orbit pass is therefore never worse than the
                // singleton-only pass, only ever better.
                var componentOrbits = component.Select(orbitIndex => orbits[orbitIndex]).ToList();
                result.AddRange(MergeSingletonOrbitsByProjection(state, componentOrbits));
            }
        }

        return result;
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
        if (headSet.Count != 3)
            return null;

        foreach (MergedFamilyOutcome member in line)
        {
            if (!headSet.SetEquals(member.Family.RepresentativeOrderItems))
                return null;
        }

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

        // Two mirror shapes: the symmetric block is whichever side has two members (both tailed for the
        // canonical shape, both leaves for shape A); the odd head out is the partner. Exactly one of
        // {A2, partner} therefore carries a tail, and that tail is the covariant drop.
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
            totalCount += member.Family.Count;
        }

        // |block| * (|heads| - 1)! = 2 * 2! = 4 distinct orderings.
        if (totalCount != 4)
            return null;

        int repFirst = repOrder[0];
        if (repFirst != b1 && repFirst != b2)
            return null;

        string patternText = $"A1 > {{A2, #{partner + 1}}}";
        return new EquivalentOrderSummary(totalCount, patternText, "2! x 2", legend);
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
