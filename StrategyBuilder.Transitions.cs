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
            List<MergedFamilyOutcome> families = merged.FamilyOutcomes;
            EquivalentOrderSummary? combinedSummary = BuildEquivalentOrderSummary(
                families.Select(outcome => outcome.Family).ToList());

            if (MergedOrderingsFormSingleOrbit(combinedSummary))
            {
                MergedFamilyOutcome representative = families[0];
                specs.Add(new BranchSpec(representative.Family.RepresentativeOrder, representative, combinedSummary));
            }
            else
            {
                foreach (MergedFamilyOutcome outcome in families)
                {
                    EquivalentOrderSummary? familySummary = BuildEquivalentOrderSummary(
                        new List<OrderFamilyDescriptor> { outcome.Family });
                    specs.Add(new BranchSpec(outcome.Family.RepresentativeOrder, outcome, familySummary));
                }
            }
        }

        return specs
            .OrderBy(spec => spec.OrderText, StringComparer.Ordinal)
            .ToList();
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
