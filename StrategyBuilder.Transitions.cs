using System;
using System.Collections.Generic;
using System.Linq;

partial class StrategyBuilder
{
    // Principle-D rendering (default ON). When true, MergeOrbitsByProjection folds sibling orderings
    // that become interchangeable only *after removing the items they all eliminate this step* (the
    // doomed "drop" set) onto one displayed branch, instead of relying on the stricter parent-state
    // automorphism alone. This covers both single orderings (rendered as a relabeling representative
    // plus a "drop {...}" legend) and multi-family components (rendered in the structural quotient
    // notation "A1 > {A2, #7} ; A = {...} ; drop tail(A2)"); any component the structural renderer
    // cannot express falls back to the singleton merge, so this is never worse than no merging.
    // It is a strict display refinement of the search (CheckDisplaySearchParity holds), only ever
    // folds orderings that share a canonical search successor, and never changes the optimal MaxStep;
    // it affects displayed branch lines and the compact edge-count proxy (CountDisplayBranches).
    // NOTE: the compact-selection objective (CountDisplayBranches) evaluates the merge with
    // fixedTopMask=0, so on rare shapes the merged compact tree can render more edges than the
    // merge-off compact tree (e.g. 10,3,4: 9 -> 11) -- a known inconsistency tracked as a follow-up
    // (see /memories/repo). Set false to recover the older, finer split.
    internal bool EnableProjectionOrbitMerging { get; set; } = true;

    private List<StrategyBranch> BuildBranches(
        ComparisonState state,
        ulong fixedTopMask,
        int remainingSlots,
        SelectedComparisonGroup chosenGroup,
        int nextStep,
        bool forceConstructiveFixedCandidateSelection)
    {
        return BuildBranchSpecs(state, remainingSlots, chosenGroup)
            .Select(spec => BuildTransitionBranch(
                state,
                fixedTopMask,
                spec,
                nextStep,
                forceConstructiveFixedCandidateSelection))
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
            if (EnableProjectionPairingProbe)
            {
                RecordProjectionPairingBucket(state, merged.FamilyOutcomes);
            }

            foreach (DisplayBranchLine line in SplitMergedBucketIntoBranchLines(state, merged.FamilyOutcomes))
            {
                specs.Add(BuildBranchSpecForLine(state, line.Members, line.ProjectionMerged));
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
    //
    // projectionMerged marks a line that exists only because the opt-in principle-D pass unioned two
    // or more distinct parent-automorphism orbits (orderings that are interchangeable only after the
    // commonly-doomed items are dropped). Such a line is never honest as a bare "{...}" brace -- the
    // brace would claim a parent symmetry that does not hold -- so it always routes through the
    // relabeling summary, which renders a total-order representative plus a "... ; drop {...}" legend
    // disclosing the doomed set. Parent orbits (projectionMerged == false) keep their exact rendering.
    private BranchSpec BuildBranchSpecForLine(ComparisonState state, List<MergedFamilyOutcome> line, bool projectionMerged)
    {
        var families = line.Select(outcome => outcome.Family).ToList();
        EquivalentOrderSummary? summary = BuildEquivalentOrderSummary(families);

        bool allSingleton = families.All(family => family.Count == 1);

        // A multi-family projection merge cannot be a relabeling orbit (its members carry their own
        // internal symmetry), so it routes through the structural quotient renderer instead. The
        // merge step only folds such a component when this renderer accepts it, so a null here means
        // the line was not actually a quotient merge and falls through to the normal handling.
        if (line.Count >= 2 && projectionMerged && !allSingleton)
        {
            MergedFamilyOutcome quotientRepresentative = SelectOrbitRepresentative(line);
            EquivalentOrderSummary? quotientSummary =
                BuildProjectionQuotientSummary(state, line, quotientRepresentative);
            if (quotientSummary is not null)
                return new BranchSpec(
                    quotientRepresentative.Family.RepresentativeOrder, quotientRepresentative, quotientSummary);
        }

        if (line.Count >= 2
            && allSingleton
            && summary is not null
            && (projectionMerged || summary.PatternText.Contains(" | ", StringComparison.Ordinal)))
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
    // One displayed branch line: the order families folded onto it (first element is the line's
    // representative) plus whether the line exists only because the opt-in principle-D pass unioned
    // two or more distinct parent-automorphism orbits. ProjectionMerged lines are rendered with an
    // explicit "drop {...}" disclosure rather than a bare brace.
    private readonly record struct DisplayBranchLine(List<MergedFamilyOutcome> Members, bool ProjectionMerged);

    private List<DisplayBranchLine> SplitMergedBucketIntoBranchLines(
        ComparisonState state, List<MergedFamilyOutcome> families)
    {
        if (families.Count == 1)
            return new List<DisplayBranchLine> { new(families, false) };

        EquivalentOrderSummary? fullBucketSummary = BuildEquivalentOrderSummary(
            families.Select(outcome => outcome.Family).ToList());
        if (MergedOrderingsFormSingleOrbit(fullBucketSummary))
            return new List<DisplayBranchLine> { new(families, false) };

        var lines = new List<DisplayBranchLine>();
        List<List<MergedFamilyOutcome>> parentOrbits = PartitionFamiliesIntoOrbits(state, families);

        List<(List<MergedFamilyOutcome> Members, bool ProjectionMerged)> orbits =
            EnableProjectionOrbitMerging
                ? MergeOrbitsByProjection(state, parentOrbits)
                : parentOrbits.Select(orbit => (orbit, false)).ToList();

        foreach ((List<MergedFamilyOutcome> orbit, bool projectionMerged) in orbits)
        {
            if (orbit.Count == 1)
            {
                lines.Add(new(orbit, false));
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
            // A multi-family projection merge is neither a clean template nor a relabeling orbit, but
            // it renders honestly through the structural quotient notation, so keep it on one line.
            bool isProjectionQuotient = projectionMerged && orbit.Any(outcome => outcome.Family.Count > 1);
            if (isCleanTemplate || isRelabelingOrbit || isProjectionQuotient)
            {
                lines.Add(new(orbit, projectionMerged));
            }
            else
            {
                foreach (MergedFamilyOutcome outcome in orbit)
                    lines.Add(new(new List<MergedFamilyOutcome> { outcome }, false));
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
        int[] activeColors = state.GetActiveItemColors();

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
            IReadOnlyList<int> orderI = families[i].Family.RepresentativeOrderItems;
            for (int j = i + 1; j < n; j++)
            {
                if (Find(i) == Find(j))
                    continue;

                IReadOnlyList<int> orderJ = families[j].Family.RepresentativeOrderItems;
                if (!OrdersHaveMatchingActiveColorSequence(activeColors, orderI, orderJ))
                {
                    if (EnableProjectionPairingProbe)
                        _parentOrbitColorPrefilterSkips++;
                    continue;
                }

                if (EnableProjectionPairingProbe)
                    _parentOrbitAutomorphismChecks++;

                if (state.TryMapOrderByAutomorphism(
                        0,
                        orderI,
                        orderJ))
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

    // Principle-D projection merge (opt-in via EnableProjectionOrbitMerging). After the strict
    // parent-automorphism partition, fold together orbits that each consist of a single ordering
    // (a singleton family) whenever an automorphism of the parent poset *projected by removing the
    // items both orderings eliminate this step* maps one onto the other. Only singleton-vs-singleton
    // orbits are eligible: a multi-ordering family carries its own internal symmetry whose members
    // do not all share the projection witness, so merging it would overclaim a symmetry that does
    // not hold family-wide. The doomed "drop" set is surfaced in the rendered legend. Input order is
    // preserved so the displayed-edge count stays identical between the display and counting paths.
    private List<(List<MergedFamilyOutcome> Members, bool ProjectionMerged)> MergeSingletonOrbitsByProjection(
        ComparisonState state, List<List<MergedFamilyOutcome>> orbits)
    {
        int n = orbits.Count;
        if (n < 2)
            return orbits.Select(orbit => (orbit, false)).ToList();

        var projectionCache = new Dictionary<ulong, (ComparisonState State, int[] Colors)>();

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

        static bool IsSingleton(List<MergedFamilyOutcome> orbit)
            => orbit.Count == 1 && orbit[0].Family.Count == 1;

        for (int i = 0; i < n; i++)
        {
            if (!IsSingleton(orbits[i]))
                continue;
            for (int j = i + 1; j < n; j++)
            {
                if (!IsSingleton(orbits[j]) || Find(i) == Find(j))
                    continue;
                if (TryProjectionAutomorphism(state, orbits[i][0], orbits[j][0], projectionCache))
                    parent[Find(i)] = Find(j);
            }
        }

        var byRoot = new Dictionary<int, List<MergedFamilyOutcome>>();
        var combinedCount = new Dictionary<int, int>();
        var order = new List<int>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!byRoot.TryGetValue(root, out List<MergedFamilyOutcome>? merged))
            {
                merged = new List<MergedFamilyOutcome>();
                byRoot[root] = merged;
                combinedCount[root] = 0;
                order.Add(root);
            }
            merged.AddRange(orbits[i]);
            combinedCount[root]++;
        }

        // A root that absorbed two or more original parent orbits is a genuine projection merge and
        // must be disclosed with a "drop {...}" legend; a root that is a single pass-through orbit
        // (singleton with no projection partner, or any multi-ordering parent orbit) keeps its exact
        // parent-automorphism rendering.
        return order.Select(root => (byRoot[root], combinedCount[root] >= 2)).ToList();
    }

    // Tests the principle-D projection symmetry between two single orderings: remove the items both
    // eliminate this step (the common drop set, which this step's comparisons determine uniquely),
    // then ask whether an automorphism of the projected parent poset maps one surviving ordering
    // onto the other. With an empty drop set this is exactly the parent-state automorphism test.
    private bool TryProjectionAutomorphism(
        ComparisonState state,
        MergedFamilyOutcome a,
        MergedFamilyOutcome b,
        Dictionary<ulong, (ComparisonState State, int[] Colors)>? projectionCache = null)
    {
        ulong commonDrop = EliminatedMask(state, a) & EliminatedMask(state, b);
        List<int> orderA = RestrictOrder(a.Family.RepresentativeOrderItems, commonDrop);
        List<int> orderB = RestrictOrder(b.Family.RepresentativeOrderItems, commonDrop);
        if (orderA.Count != orderB.Count)
            return false;

        if (commonDrop == 0)
        {
            int[] activeColors = state.GetActiveItemColors();
            if (!OrdersHaveMatchingActiveColorSequence(activeColors, orderA, orderB))
            {
                if (EnableProjectionPairingProbe)
                    _projectionOrbitColorPrefilterSkips++;
                return false;
            }

            if (EnableProjectionPairingProbe)
                _projectionOrbitAutomorphismChecks++;

            return state.TryMapOrderByAutomorphism(0, orderA, orderB);
        }

        ComparisonState projected;
        int[] projectedColors;
        if (projectionCache is not null && projectionCache.TryGetValue(commonDrop, out var cached))
        {
            projected = cached.State;
            projectedColors = cached.Colors;
            if (EnableProjectionPairingProbe)
                _projectionOrbitProjectedStateCacheHits++;
        }
        else
        {
            projected = state.Clone();
            projected.Deactivate(commonDrop);
            projectedColors = projected.GetActiveItemColors();
            projectionCache?.Add(commonDrop, (projected, projectedColors));
            if (EnableProjectionPairingProbe)
                _projectionOrbitProjectedStateBuilds++;
        }

        if (!OrdersHaveMatchingActiveColorSequence(projectedColors, orderA, orderB))
        {
            if (EnableProjectionPairingProbe)
                _projectionOrbitColorPrefilterSkips++;
            return false;
        }

        if (EnableProjectionPairingProbe)
            _projectionOrbitAutomorphismChecks++;

        return projected.TryMapOrderByAutomorphism(0, orderA, orderB);
    }

    private static bool OrdersHaveMatchingActiveColorSequence(
        int[] activeColors,
        IReadOnlyList<int> orderA,
        IReadOnlyList<int> orderB)
    {
        for (int i = 0; i < orderA.Count; i++)
        {
            if (activeColors[orderA[i]] != activeColors[orderB[i]])
                return false;
        }

        return true;
    }

    // Items active in the parent state that this ordering's outcome neither kept active nor promoted
    // to a fixed-top winner -- i.e. the items it eliminates this step.
    private static ulong EliminatedMask(ComparisonState state, MergedFamilyOutcome outcome)
        => state.ActiveMask & ~outcome.NextState.ActiveMask & ~outcome.NextFixedTopMask;

    private static List<int> RestrictOrder(IReadOnlyList<int> order, ulong dropMask)
    {
        var result = new List<int>(order.Count);
        foreach (int item in order)
        {
            if ((dropMask & (1UL << item)) == 0)
                result.Add(item);
        }
        return result;
    }

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

    private StrategyBranch BuildTransitionBranch(
        ComparisonState state,
        ulong fixedTopMask,
        BranchSpec spec,
        int nextStep,
        bool forceConstructiveFixedCandidateSelection)
    {
        MergedFamilyOutcome outcome = spec.Outcome;
        return new StrategyBranch(
            spec.OrderText,
            spec.Summary,
            BuildComparisonEffect(state, fixedTopMask, outcome.NextState, outcome.NextFixedTopMask),
            BuildState(
                outcome.NextState,
                outcome.NextFixedTopMask,
                outcome.NextRemainingSlots,
                nextStep,
                forceConstructiveFixedCandidateSelection));
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
        // GetMaxOutcomesPerStep is a loose factorial ceiling; for large m it can be enormous and
        // preallocating that many buckets can fail before traversal starts.
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
