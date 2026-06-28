using System;
using System.Collections.Generic;
using System.Linq;

partial class StrategyBuilder
{
    private List<StrategyBranch> BuildBranches(ComparisonState state, ulong fixedTopMask, int remainingSlots, SelectedComparisonGroup chosenGroup, int nextStep)
    {
        return BuildBranchSpecs(state, remainingSlots, chosenGroup)
            .Select(spec => BuildTransitionBranch(state, fixedTopMask, spec, nextStep))
            .ToList();
    }

    // Builds the ordered display-branch specs for a chosen comparison group without
    // materializing the subtrees. BuildBranches maps these through BuildTransitionBranch
    // (which recurses); the compact DP counts them via CountDisplayBranches to minimize
    // the total number of displayed edges. The returned list is already in display order,
    // so its Count is exactly the number of branch lines this node will render.
    private List<BranchSpec> BuildBranchSpecs(ComparisonState state, int remainingSlots, SelectedComparisonGroup chosenGroup)
    {
        ThrowIfCancellationRequested();

        // A merged branch groups every order family whose outcome maps to the same
        // display-canonical next state. Two very different situations land here:
        //
        //   1. A genuine relabeling-symmetry orbit. The merged orderings are interchangeable
        //      (e.g. the three triple-winners #1, #4, #7), so the pattern engine unifies them
        //      into one disjunction-free template such as "permute {#1, #4, #7}". These stay a
        //      single branch; showing the representative effect (up to relabeling) is honest.
        //
        //   2. Distinct orderings that merely converge to isomorphic (but differently-labeled)
        //      next states (e.g. sort(#2, #4, #5) where #4 > #5 is already known). The pattern
        //      engine cannot unify them and falls back to a disjunction "(… | …)" with a partial
        //      "permute", which reads like a false symmetry claim and hides the per-ordering
        //      effect. We split these into one branch per family so each shows its own effect;
        //      the shared result subtree is materialized once under the first branch (build order
        //      follows display order) and the rest become →S references with a relabel map via
        //      the existing BuildState dedup.
        // When the chosen sort produces a genuinely doomed tail (items already eliminated whatever
        // their final rank), fold every tail permutation into a single edge whose pattern carries
        // the tail as an unordered brace set. This replaces the per-family listing, which would
        // otherwise spell out each tail permutation as its own misleading branch.
        List<BranchSpec>? doomedTailSpecs = TryBuildDoomedTailSpecs(state, remainingSlots, chosenGroup);
        if (doomedTailSpecs is not null)
        {
            return doomedTailSpecs
                .OrderBy(spec => spec.OrderText, StringComparer.Ordinal)
                .ToList();
        }

        var specs = new List<BranchSpec>();
        foreach (MergedBranch merged in chosenGroup.Branches)
        {
            foreach (List<MergedFamilyOutcome> line in SplitMergedBucketIntoBranchLines(state, merged.FamilyOutcomes))
            {
                specs.Add(BuildBranchSpecForLine(state, line));
            }
        }

        return specs
            .OrderBy(spec => spec.OrderText, StringComparer.Ordinal)
            .ToList();
    }

    // Builds the spec for one displayed branch line. A line with a single family, or several
    // families the pattern engine unifies into one disjunction-free template, is summarized
    // directly. A line that is a genuine parent-automorphism orbit the engine cannot express as a
    // single template (e.g. two symmetric sorted chains, where "#1 > #11 > ..." mirrors
    // "#11 > #1 > ...") would otherwise render as a misleading "(... | ...)" disjunction; instead we
    // fold it onto one representative line annotated with the relabeling that proves the equivalence.
    private BranchSpec BuildBranchSpecForLine(ComparisonState state, List<MergedFamilyOutcome> line)
    {
        var families = line.Select(outcome => outcome.Family).ToList();
        EquivalentOrderSummary? summary = BuildEquivalentOrderSummary(families);

        if (line.Count >= 2
            && families.All(family => family.Count == 1)
            && summary is not null
            && summary.PatternText.Contains(" | ", StringComparison.Ordinal))
        {
            MergedFamilyOutcome representative = SelectOrbitRepresentative(line);
            EquivalentOrderSummary relabelSummary = BuildRelabelingOrbitSummary(state, line, representative);
            return new BranchSpec(representative.Family.RepresentativeOrder, representative, relabelSummary);
        }

        MergedFamilyOutcome lineRepresentative = line[0];
        return new BranchSpec(lineRepresentative.Family.RepresentativeOrder, lineRepresentative, summary);
    }

    // The lexicographically smallest ordering in a relabeling orbit, so the folded line shows the
    // most natural representative (e.g. the "#1 > ..." form rather than a mirror "#11 > ..." form).
    private static MergedFamilyOutcome SelectOrbitRepresentative(List<MergedFamilyOutcome> line)
    {
        MergedFamilyOutcome best = line[0];
        string bestText = best.Family.RepresentativeOrder;
        for (int i = 1; i < line.Count; i++)
        {
            string text = line[i].Family.RepresentativeOrder;
            if (string.CompareOrdinal(text, bestText) < 0)
            {
                best = line[i];
                bestText = text;
            }
        }
        return best;
    }

    // Splits one merged bucket's order families into the exact set of displayed branch lines.
    // Each returned inner list is the families folded onto a single line; its first element is the
    // line's representative. Both BuildBranchSpecs (which materializes the line) and
    // CountDisplayBranches (which only counts lines) route through here, so the displayed edge
    // count and the compact DP's edge count can never disagree.
    //
    // A merged bucket groups every order family whose outcome maps to the same display-canonical
    // next state. If the full bucket can be summarized by one disjunction-free pattern, we render it
    // as one line directly: this captures "comparison-before visible" equivalences (for example
    // {A1, B1} > {A2, B2}) even when the parent-state automorphism partition is stricter. If the full
    // bucket cannot be summarized cleanly, we fall back to parent-automorphism orbits and apply the
    // same disjunction-free check per orbit, finally splitting to per-family lines when needed.
    private List<List<MergedFamilyOutcome>> SplitMergedBucketIntoBranchLines(
        ComparisonState state, List<MergedFamilyOutcome> families)
    {
        if (families.Count == 1)
            return new List<List<MergedFamilyOutcome>> { families };

        EquivalentOrderSummary? fullBucketSummary = BuildEquivalentOrderSummary(
            families.Select(outcome => outcome.Family).ToList());
        if (MergedOrderingsFormSingleOrbit(fullBucketSummary))
            return new List<List<MergedFamilyOutcome>> { families };

        var lines = new List<List<MergedFamilyOutcome>>();
        foreach (List<MergedFamilyOutcome> orbit in PartitionFamiliesIntoOrbits(state, families))
        {
            if (orbit.Count == 1)
            {
                lines.Add(orbit);
                continue;
            }

            EquivalentOrderSummary? combinedSummary = BuildEquivalentOrderSummary(
                orbit.Select(outcome => outcome.Family).ToList());

            // The orbit is parent-automorphism-backed (interchangeable up to relabeling). Keep it on
            // one line when it is either a clean disjunction-free template ("permute {...}") or an
            // all-singleton relabeling orbit, which BuildBranchSpecForLine renders as one
            // representative annotated with the relabeling. Only fall back to per-family lines when
            // neither honest single-line form applies (e.g. a member carries its own internal
            // permutation), which would otherwise read as a misleading "(... | ...)" disjunction.
            bool isCleanTemplate = MergedOrderingsFormSingleOrbit(combinedSummary);
            bool isRelabelingOrbit = orbit.All(outcome => outcome.Family.Count == 1);
            if (isCleanTemplate || isRelabelingOrbit)
            {
                lines.Add(orbit);
            }
            else
            {
                foreach (MergedFamilyOutcome outcome in orbit)
                    lines.Add(new List<MergedFamilyOutcome> { outcome });
            }
        }

        return lines;
    }

    // Partitions a merged bucket's families into parent-automorphism orbits over the active poset.
    // Two families are unioned when some automorphism of the parent state's active poset maps one
    // family's representative order onto the other's. Fixed-top winners are deactivated before a node
    // is rendered and never affect elimination, so an active-poset automorphism (fixedTopMask: 0) is
    // the exact symmetry that makes two sibling orderings interchangeable for the future — and it is
    // identical in the display path and the compact counting path, which never accumulate the same
    // fixed-top context anyway. Orbits and the families within them preserve input order.
    private List<List<MergedFamilyOutcome>> PartitionFamiliesIntoOrbits(
        ComparisonState state, List<MergedFamilyOutcome> families)
    {
        int n = families.Count;
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
                if (state.TryMapOrderByAutomorphism(
                        0,
                        families[i].Family.RepresentativeOrderItems,
                        families[j].Family.RepresentativeOrderItems))
                {
                    parent[Find(i)] = Find(j);
                }
            }
        }

        var orbitsByRoot = new Dictionary<int, List<MergedFamilyOutcome>>();
        var order = new List<int>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!orbitsByRoot.TryGetValue(root, out List<MergedFamilyOutcome>? orbit))
            {
                orbit = new List<MergedFamilyOutcome>();
                orbitsByRoot[root] = orbit;
                order.Add(root);
            }
            orbit.Add(families[i]);
        }

        return order.Select(root => orbitsByRoot[root]).ToList();
    }

    // A single ordering (no summary) or a summary whose pattern is one disjunction-free symmetry
    // template describes a genuine relabeling orbit, so it stays a single branch. The pattern
    // engine emits the " | " separator only when the merged orderings cannot be unified into one
    // template (distinct orderings that merely converge to isomorphic next states); those are
    // split. " | " is exclusively the engine's disjunction separator (all other segment joins use
    // " > ").
    private static bool MergedOrderingsFormSingleOrbit(EquivalentOrderSummary? combinedSummary)
    {
        return combinedSummary is null
            || !combinedSummary.PatternText.Contains(" | ", StringComparison.Ordinal);
    }

    private void AddMergedBranch(
        ComparisonState state,
        ulong fixedTopMask,
        ComparisonOutcome outcome,
        Dictionary<IntSequenceKey, MergedBranch> groupedBranches)
    {
        ulong nextFixedTopMask = fixedTopMask | outcome.AddedFixedTopMask;
        IntSequenceKey nextKey = state.GetDisplayCanonicalKey(nextFixedTopMask);
        var familyOutcome = new MergedFamilyOutcome(
            outcome.OrderFamily!,
            outcome.NextState,
            nextFixedTopMask,
            outcome.NextRemainingSlots);

        if (!groupedBranches.TryGetValue(nextKey, out MergedBranch? branch))
        {
            groupedBranches[nextKey] = new MergedBranch(familyOutcome);
            return;
        }

        branch.AddFamilyOutcome(familyOutcome);
        _mergedOutcomeCollisions++;
    }

    private StrategyBranch BuildTransitionBranch(ComparisonState state, ulong fixedTopMask, BranchSpec spec, int nextStep)
    {
        MergedFamilyOutcome outcome = spec.Outcome;
        return new StrategyBranch(
            spec.OrderText,
            spec.Summary,
            BuildComparisonEffect(state, fixedTopMask, outcome.NextState, outcome.NextFixedTopMask),
            BuildState(outcome.NextState, outcome.NextFixedTopMask, outcome.NextRemainingSlots, nextStep));
    }

    private StrategyEffect BuildComparisonEffect(ComparisonState before, ulong beforeFixedTopMask, ComparisonState after, ulong afterFixedTopMask)
    {
        var newlyGuaranteedTop = ComparisonState.MaskToOrderedList(afterFixedTopMask & ~beforeFixedTopMask);
        var newlyExcluded = ComparisonState.MaskToOrderedList(before.ActiveMask & ~after.ActiveMask & ~afterFixedTopMask);
        var fixedCandidates = ComparisonState.MaskToOrderedList(afterFixedTopMask);
        var possibleCandidates = after.GetActiveItemsOrdered();

        return new StrategyEffect(newlyGuaranteedTop, newlyExcluded, fixedCandidates, possibleCandidates);
    }

    private ComparisonOutcome CreateComparisonOutcome(
        ComparisonState state,
        int remainingSlots,
        IReadOnlyList<int> order,
        OrderFamilyDescriptor? orderFamily)
    {
        _outcomesConstructed++;
        ComparisonState next = state.Clone();
        next.ApplyOrder(order);
        next.Eliminate(remainingSlots);

        ulong addedFixedTopMask = 0;
        int nextRemainingSlots = remainingSlots;
        NormalizeState(next, ref addedFixedTopMask, ref nextRemainingSlots);

        return new ComparisonOutcome(
            next,
            addedFixedTopMask,
            nextRemainingSlots,
            GetSearchStateKey(next, nextRemainingSlots),
            orderFamily);
    }

    private OutcomeTraversalSummary VisitComparisonOutcomes(
        ComparisonState state,
        ulong fixedTopMask,
        int remainingSlots,
        IReadOnlyList<int> group,
        SearchStateKey? currentKey,
        bool collectMergedBranches,
        Func<ComparisonOutcome, bool> onUsefulOutcome)
    {
        ThrowIfCancellationRequested();
        var evaluatedStateKeys = new HashSet<SearchStateKey>();
        var groupedBranches = collectMergedBranches ? new Dictionary<IntSequenceKey, MergedBranch>() : null;
        bool isUseful = false;

        // The display path (collectMergedBranches) needs every order family to count equivalent
        // orderings, so it enumerates the full family descriptors. The search and compact paths
        // only need the set of distinct next states, so they use a lean enumerator that skips the
        // family/descriptor machinery and prunes orderings that collapse to the same next state.
        IEnumerable<ComparisonOutcome> outcomes = collectMergedBranches
            ? EnumerateDisplayOutcomes(state, remainingSlots, group)
            : EnumerateSearchOutcomes(state, remainingSlots, group);

        foreach (ComparisonOutcome outcome in outcomes)
        {
            ThrowIfCancellationRequested();
            if (groupedBranches is not null)
                AddMergedBranch(outcome.NextState, fixedTopMask, outcome, groupedBranches);

            if (currentKey is not null && outcome.NextSearchKey.Equals(currentKey.Value))
                continue;

            isUseful = true;

            if (!evaluatedStateKeys.Add(outcome.NextSearchKey))
            {
                _duplicateOutcomeSkips++;
                continue;
            }

            if (!onUsefulOutcome(outcome))
                break;
        }

        return new OutcomeTraversalSummary(
            groupedBranches is not null ? groupedBranches.Values.ToList() : Array.Empty<MergedBranch>(),
            isUseful);
    }

    private IEnumerable<ComparisonOutcome> EnumerateDisplayOutcomes(ComparisonState state, int remainingSlots, IReadOnlyList<int> group)
    {
        foreach (OrderFamilyDescriptor orderFamily in EnumerateFeasibleOrderFamilies(state, group))
            yield return CreateComparisonOutcome(state, remainingSlots, orderFamily.RepresentativeOrderItems, orderFamily);
    }

    private IEnumerable<ComparisonOutcome> EnumerateSearchOutcomes(ComparisonState state, int remainingSlots, IReadOnlyList<int> group)
    {
        foreach (IReadOnlyList<int> order in EnumerateSearchOrders(state, group, remainingSlots))
            yield return CreateComparisonOutcome(state, remainingSlots, order, null);
    }

    private readonly struct BranchSpec
    {
        public BranchSpec(string orderText, MergedFamilyOutcome outcome, EquivalentOrderSummary? summary)
        {
            OrderText = orderText;
            Outcome = outcome;
            Summary = summary;
        }

        public string OrderText { get; }
        public MergedFamilyOutcome Outcome { get; }
        public EquivalentOrderSummary? Summary { get; }
    }

    private sealed class MergedBranch
    {
        public List<MergedFamilyOutcome> FamilyOutcomes { get; }

        public MergedBranch(MergedFamilyOutcome firstOutcome)
        {
            FamilyOutcomes = new List<MergedFamilyOutcome> { firstOutcome };
        }

        public void AddFamilyOutcome(MergedFamilyOutcome outcome)
        {
            FamilyOutcomes.Add(outcome);
        }
    }

    private sealed class MergedFamilyOutcome
    {
        public MergedFamilyOutcome(
            OrderFamilyDescriptor family,
            ComparisonState nextState,
            ulong nextFixedTopMask,
            int nextRemainingSlots)
        {
            Family = family;
            NextState = nextState;
            NextFixedTopMask = nextFixedTopMask;
            NextRemainingSlots = nextRemainingSlots;
        }

        public OrderFamilyDescriptor Family { get; }
        public ComparisonState NextState { get; }
        public ulong NextFixedTopMask { get; }
        public int NextRemainingSlots { get; }
    }

    private sealed class ComparisonOutcome
    {
        public ComparisonOutcome(
            ComparisonState nextState,
            ulong addedFixedTopMask,
            int nextRemainingSlots,
            SearchStateKey nextSearchKey,
            OrderFamilyDescriptor? orderFamily)
        {
            NextState = nextState;
            AddedFixedTopMask = addedFixedTopMask;
            NextRemainingSlots = nextRemainingSlots;
            NextSearchKey = nextSearchKey;
            OrderFamily = orderFamily;
        }

        public ComparisonState NextState { get; }
        public ulong AddedFixedTopMask { get; }
        public int NextRemainingSlots { get; }
        public SearchStateKey NextSearchKey { get; }
        public OrderFamilyDescriptor? OrderFamily { get; }
    }
}
