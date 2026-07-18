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
    // folds orderings that share a canonical search successor, and never changes the optimal MaxStep.
    // It only affects displayed branch lines. Set false to recover the older, finer split.
    internal bool EnableProjectionOrbitMerging { get; set; } = true;

    private List<StrategyBranch> BuildBranches(
        ComparisonState state,
        ulong fixedTopMask,
        int remainingSlots,
        SelectedComparisonGroup chosenGroup,
        int nextStep,
        MaterializationContext context)
    {
        return BuildDisplayTransitionSpecs(state, fixedTopMask, remainingSlots, chosenGroup)
            .Select(spec => new StrategyBranch(
                spec.OrderText,
                spec.Summary,
                spec.Effect,
                BuildState(
                    spec.NextState,
                    spec.NextFixedTopMask,
                    spec.NextRemainingSlots,
                    nextStep,
                    context)))
            .ToList();
    }

    // Display transition planner: consumes display branch-line specs (including equivalence-summary
    // shaping) and projects them into transition payloads.
    private IReadOnlyList<TransitionSpec> BuildDisplayTransitionSpecs(
        ComparisonState state,
        ulong fixedTopMask,
        int remainingSlots,
        SelectedComparisonGroup chosenGroup)
    {
        return BuildTransitionSpecsFromBranchSpecs(
            state,
            fixedTopMask,
            BuildBranchSpecs(state, remainingSlots, chosenGroup));
    }

    // Search transition planner seam: currently reuses the display branch-line planner so behavior
    // stays stable while search-side planning is being decoupled incrementally.
    private IReadOnlyList<SearchTransitionSpec> BuildSearchTransitionSpecs(
        ComparisonState state,
        ulong fixedTopMask,
        int remainingSlots,
        SelectedComparisonGroup chosenGroup)
    {
        return BuildTransitionSpecsFromSearchBranchSpecs(
            state,
            fixedTopMask,
            BuildSearchBranchSpecs(state, remainingSlots, chosenGroup));
    }

    // Search branch planner: mirrors the current display line-planning policy, but keeps a
    // search-only branch payload that does not carry display summary fields.
    private List<SearchBranchSpec> BuildSearchBranchSpecs(
        ComparisonState state,
        int remainingSlots,
        SelectedComparisonGroup chosenGroup)
    {
        ThrowIfCancellationRequested();

        List<SearchBranchSpec>? doomedTailSpecs =
            TryBuildSearchDoomedTailSpecs(state, remainingSlots, chosenGroup);
        if (doomedTailSpecs is not null)
            return doomedTailSpecs
                .OrderBy(spec => spec.OrderText, StringComparer.Ordinal)
                .ToList();

        return BuildPlannedBranchSpecsForChosenGroup(
            state,
            chosenGroup,
            line => BuildSearchBranchSpecForLine(state, line.Members, line.ProjectionMerged),
            spec => spec.OrderText);
    }

    private SearchBranchSpec BuildSearchBranchSpecForLine(
        ComparisonState state,
        List<MergedFamilyOutcome> line,
        bool projectionMerged)
    {
        var families = line.Select(outcome => outcome.Family).ToList();
        EquivalentOrderSummary? summary = BuildEquivalentOrderSummary(families);
        BranchRepresentativeSelection selection =
            SelectBranchRepresentativeForLine(state, line, projectionMerged, summary, families);

        return new SearchBranchSpec(
            selection.Representative.Family.RepresentativeOrder,
            selection.Representative.NextState,
            selection.Representative.NextFixedTopMask,
            selection.Representative.NextRemainingSlots);
    }

    private BranchRepresentativeSelection SelectBranchRepresentativeForLine(
        ComparisonState state,
        List<MergedFamilyOutcome> line,
        bool projectionMerged,
        EquivalentOrderSummary? summary,
        List<OrderFamilyDescriptor> families)
    {
        if (line.Count <= 1)
            return BranchRepresentativeSelection.Default(line[0]);

        bool allSingleton = families.All(family => family.Count == 1);
        if (projectionMerged && !allSingleton)
        {
            MergedFamilyOutcome quotientRepresentative = SelectOrbitRepresentative(line);
            EquivalentOrderSummary? quotientSummary =
                BuildProjectionQuotientSummary(state, line, quotientRepresentative);
            if (quotientSummary is not null)
                return BranchRepresentativeSelection.ProjectionQuotient(quotientRepresentative, quotientSummary);
        }

        if (allSingleton
            && summary is not null
            && (projectionMerged || summary.PatternText.Contains(" | ", StringComparison.Ordinal)))
        {
            return BranchRepresentativeSelection.RelabelOrbit(SelectOrbitRepresentative(line));
        }

        return BranchRepresentativeSelection.Default(line[0]);
    }

    private IReadOnlyList<TransitionSpec> BuildTransitionSpecsFromBranchSpecs(
        ComparisonState state,
        ulong fixedTopMask,
        IReadOnlyList<BranchSpec> branchSpecs)
    {
        return branchSpecs
            .Select(spec => new TransitionSpec(
                spec.OrderText,
                spec.Summary,
                BuildComparisonEffect(state, fixedTopMask, spec.Outcome.NextState, spec.Outcome.NextFixedTopMask),
                spec.Outcome.NextState,
                spec.Outcome.NextFixedTopMask,
                spec.Outcome.NextRemainingSlots))
            .ToList();
    }

    private IReadOnlyList<SearchTransitionSpec> BuildTransitionSpecsFromSearchBranchSpecs(
        ComparisonState state,
        ulong fixedTopMask,
        IReadOnlyList<SearchBranchSpec> branchSpecs)
    {
        return branchSpecs
            .Select(spec => new SearchTransitionSpec(
                spec.OrderText,
                BuildSearchComparisonEffect(state, fixedTopMask, spec.NextState, spec.NextFixedTopMask),
                spec.NextState,
                spec.NextFixedTopMask,
                spec.NextRemainingSlots))
            .ToList();
    }

    private SearchEffect BuildSearchComparisonEffect(
        ComparisonState before,
        ulong beforeFixedTopMask,
        ComparisonState after,
        ulong afterFixedTopMask)
    {
        StrategyEffect effect = BuildComparisonEffect(before, beforeFixedTopMask, after, afterFixedTopMask);
        return new SearchEffect(
            effect.NewlyGuaranteedTop,
            effect.NewlyExcluded,
            effect.FixedCandidates,
            effect.PossibleCandidates);
    }

    // Builds the ordered display-branch specs for a chosen comparison group without
    // materializing the subtrees. BuildBranches maps these through BuildTransitionBranch
    // (which recurses). The returned list is already in display order,
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

        return BuildPlannedBranchSpecsForChosenGroup(
            state,
            chosenGroup,
            line => BuildBranchSpecForLine(state, line.Members, line.ProjectionMerged),
            spec => spec.OrderText);
    }

    private List<TSpec> BuildPlannedBranchSpecsForChosenGroup<TSpec>(
        ComparisonState state,
        SelectedComparisonGroup chosenGroup,
        Func<DisplayRenderEngine.BranchLine<MergedFamilyOutcome>, TSpec> buildSpec,
        Func<TSpec, string> getOrderText)
    {
        var specs = new List<TSpec>();
        foreach (var line in PlanBranchLinesForChosenGroup(state, chosenGroup))
            specs.Add(buildSpec(line));

        return specs
            .OrderBy(getOrderText, StringComparer.Ordinal)
            .ToList();
    }

    private List<DisplayRenderEngine.BranchLine<MergedFamilyOutcome>> PlanBranchLinesForChosenGroup(
        ComparisonState state,
        SelectedComparisonGroup chosenGroup)
    {
        var plannedLines = new List<DisplayRenderEngine.BranchLine<MergedFamilyOutcome>>();
        foreach (MergedBranch merged in chosenGroup.Branches)
        {
            if (EnableProjectionPairingProbe)
            {
                RecordProjectionPairingBucket(state, merged.FamilyOutcomes);
            }

            plannedLines.AddRange(SplitMergedBucketIntoBranchLines(state, merged.FamilyOutcomes));
        }

        return plannedLines;
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
        BranchRepresentativeSelection selection =
            SelectBranchRepresentativeForLine(state, line, projectionMerged, summary, families);

        if (selection.QuotientSummary is not null)
            return new BranchSpec(
                selection.Representative.Family.RepresentativeOrder,
                selection.Representative,
                selection.QuotientSummary);

        if (selection.UseRelabelSummary)
        {
            EquivalentOrderSummary relabelSummary =
                BuildRelabelingOrbitSummary(state, line, selection.Representative);
            return new BranchSpec(
                selection.Representative.Family.RepresentativeOrder,
                selection.Representative,
                relabelSummary);
        }

        return new BranchSpec(
            selection.Representative.Family.RepresentativeOrder,
            selection.Representative,
            summary);
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
    // The line-planning policy is now hosted in DisplayBranchLinePlanner (display layer), while this
    // adapter provides the builder-specific orbit partition/projection merge hooks.
    private List<DisplayRenderEngine.BranchLine<MergedFamilyOutcome>> SplitMergedBucketIntoBranchLines(
        ComparisonState state, List<MergedFamilyOutcome> families)
    {
        return DisplayRenderEngine.PlanBranchLines(
            families,
            buildSummary: members => BuildEquivalentOrderSummary(members.Select(outcome => outcome.Family).ToList()),
            partitionFamiliesIntoOrbits: members => PartitionFamiliesIntoOrbits(state, members),
            mergeOrbitsByProjection: parentOrbits =>
                EnableProjectionOrbitMerging
                    ? MergeOrbitsByProjection(state, parentOrbits)
                    : parentOrbits.Select(orbit => (orbit, false)).ToList(),
            getFamilyCount: outcome => outcome.Family.Count);
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
        Action<DisplayRenderEngine.ProjectionAutomorphismProbeEvent>? probeHook = null;
        if (EnableProjectionPairingProbe)
        {
            probeHook = evt =>
            {
                switch (evt)
                {
                    case DisplayRenderEngine.ProjectionAutomorphismProbeEvent.ColorPrefilterSkip:
                        _projectionOrbitColorPrefilterSkips++;
                        break;
                    case DisplayRenderEngine.ProjectionAutomorphismProbeEvent.AutomorphismCheck:
                        _projectionOrbitAutomorphismChecks++;
                        break;
                    case DisplayRenderEngine.ProjectionAutomorphismProbeEvent.ProjectedStateBuild:
                        _projectionOrbitProjectedStateBuilds++;
                        break;
                    case DisplayRenderEngine.ProjectionAutomorphismProbeEvent.ProjectedStateCacheHit:
                        _projectionOrbitProjectedStateCacheHits++;
                        break;
                }
            };
        }

        return DisplayRenderEngine.TryProjectionAutomorphism(
            state,
            new DisplayRenderEngine.ProjectionOutcomeData(
                a.Family.RepresentativeOrderItems,
                EliminatedMask(state, a)),
            new DisplayRenderEngine.ProjectionOutcomeData(
                b.Family.RepresentativeOrderItems,
                EliminatedMask(state, b)),
            projectionCache,
            probeHook);
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

    // template describes a genuine relabeling orbit, so it stays a single branch. The pattern
    // engine emits the " | " separator only when the merged orderings cannot be unified into one
    // template (distinct orderings that merely converge to isomorphic next states); those are
    // split. " | " is exclusively the engine's disjunction separator (all other segment joins use
    // " > ").
    private static bool MergedOrderingsFormSingleOrbit(EquivalentOrderSummary? combinedSummary)
    {
        return DisplayRenderEngine.IsSingleMergedOrbit(combinedSummary);
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

    private readonly struct BranchRepresentativeSelection
    {
        private BranchRepresentativeSelection(
            MergedFamilyOutcome representative,
            bool useRelabelSummary,
            EquivalentOrderSummary? quotientSummary)
        {
            Representative = representative;
            UseRelabelSummary = useRelabelSummary;
            QuotientSummary = quotientSummary;
        }

        public MergedFamilyOutcome Representative { get; }
        public bool UseRelabelSummary { get; }
        public EquivalentOrderSummary? QuotientSummary { get; }

        public static BranchRepresentativeSelection Default(MergedFamilyOutcome representative)
            => new(representative, useRelabelSummary: false, quotientSummary: null);

        public static BranchRepresentativeSelection RelabelOrbit(MergedFamilyOutcome representative)
            => new(representative, useRelabelSummary: true, quotientSummary: null);

        public static BranchRepresentativeSelection ProjectionQuotient(
            MergedFamilyOutcome representative,
            EquivalentOrderSummary quotientSummary)
            => new(representative, useRelabelSummary: false, quotientSummary);
    }

    private readonly struct SearchBranchSpec
    {
        public SearchBranchSpec(
            string orderText,
            ComparisonState nextState,
            ulong nextFixedTopMask,
            int nextRemainingSlots)
        {
            OrderText = orderText;
            NextState = nextState;
            NextFixedTopMask = nextFixedTopMask;
            NextRemainingSlots = nextRemainingSlots;
        }

        public string OrderText { get; }
        public ComparisonState NextState { get; }
        public ulong NextFixedTopMask { get; }
        public int NextRemainingSlots { get; }
    }

    private readonly struct SearchTransitionSpec
    {
        public SearchTransitionSpec(
            string orderText,
            SearchEffect effect,
            ComparisonState nextState,
            ulong nextFixedTopMask,
            int nextRemainingSlots)
        {
            OrderText = orderText;
            Effect = effect;
            NextState = nextState;
            NextFixedTopMask = nextFixedTopMask;
            NextRemainingSlots = nextRemainingSlots;
        }

        public string OrderText { get; }
        public SearchEffect Effect { get; }
        public ComparisonState NextState { get; }
        public ulong NextFixedTopMask { get; }
        public int NextRemainingSlots { get; }
    }

    private readonly struct TransitionSpec
    {
        public TransitionSpec(
            string orderText,
            EquivalentOrderSummary? summary,
            StrategyEffect effect,
            ComparisonState nextState,
            ulong nextFixedTopMask,
            int nextRemainingSlots)
        {
            OrderText = orderText;
            Summary = summary;
            Effect = effect;
            NextState = nextState;
            NextFixedTopMask = nextFixedTopMask;
            NextRemainingSlots = nextRemainingSlots;
        }

        public string OrderText { get; }
        public EquivalentOrderSummary? Summary { get; }
        public StrategyEffect Effect { get; }
        public ComparisonState NextState { get; }
        public ulong NextFixedTopMask { get; }
        public int NextRemainingSlots { get; }
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
