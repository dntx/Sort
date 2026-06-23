using System;
using System.Collections.Generic;
using System.Linq;

partial class StrategyBuilder
{
    private List<StrategyBranch> BuildBranches(ComparisonState state, ulong fixedTopMask, int remainingSlots, SelectedComparisonGroup chosenGroup, int nextStep)
    {
        ThrowIfCancellationRequested();

        return chosenGroup.Branches
            .OrderBy(branch => branch.RepresentativeOrder, StringComparer.Ordinal)
            .Select(branch => BuildTransitionBranch(state, fixedTopMask, branch, nextStep))
            .ToList();
    }

    private void AddMergedBranch(
        ComparisonState state,
        ulong fixedTopMask,
        ComparisonOutcome outcome,
        Dictionary<IntSequenceKey, MergedBranch> groupedBranches)
    {
        ulong nextFixedTopMask = fixedTopMask | outcome.AddedFixedTopMask;
        IntSequenceKey nextKey = state.GetDisplayCanonicalKey(nextFixedTopMask);

        if (!groupedBranches.TryGetValue(nextKey, out MergedBranch? branch))
        {
            groupedBranches[nextKey] = new MergedBranch(
                outcome.NextState,
                nextFixedTopMask,
                outcome.NextRemainingSlots,
                outcome.OrderFamily!);
            return;
        }

        branch.AddOrderFamily(outcome.OrderFamily!);
        _mergedOutcomeCollisions++;
    }

    private StrategyBranch BuildTransitionBranch(ComparisonState state, ulong fixedTopMask, MergedBranch branch, int nextStep)
    {
        return new StrategyBranch(
            branch.RepresentativeOrder,
            BuildEquivalentOrderSummary(branch.OrderFamilies),
            BuildComparisonEffect(state, fixedTopMask, branch.NextState, branch.NextFixedTopMask),
            BuildState(branch.NextState, branch.NextFixedTopMask, branch.NextRemainingSlots, nextStep));
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

    private sealed class MergedBranch
    {
        public ComparisonState NextState { get; }
        public ulong NextFixedTopMask { get; }
        public int NextRemainingSlots { get; }
        public string RepresentativeOrder { get; }
        public List<OrderFamilyDescriptor> OrderFamilies { get; }

        public MergedBranch(ComparisonState nextState, ulong nextFixedTopMask, int nextRemainingSlots, OrderFamilyDescriptor representativeFamily)
        {
            NextState = nextState;
            NextFixedTopMask = nextFixedTopMask;
            NextRemainingSlots = nextRemainingSlots;
            RepresentativeOrder = representativeFamily.RepresentativeOrder;
            OrderFamilies = new List<OrderFamilyDescriptor> { representativeFamily };
        }

        public void AddOrderFamily(OrderFamilyDescriptor orderFamily)
        {
            OrderFamilies.Add(orderFamily);
        }
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
