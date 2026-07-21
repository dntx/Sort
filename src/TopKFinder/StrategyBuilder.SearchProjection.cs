using System;
using System.Collections.Generic;
using System.Linq;

namespace TopKFinder;

partial class StrategyBuilder
{
    // Mainline-A seam: build exact display + search artifacts inside one active solver session
    // so both materializers consume the same phase-1 caches without nested entrypoint hand-offs.
    private (SearchTree SearchTree, DisplayTree DisplayTree) BuildExactProjectionFromCurrentSession()
    {
        return RunWithComparisonStateCancellation(() =>
        {
            _progressScope = _reportCombinedRunProgress
                ? ProgressScope.DefaultInCombinedRun
                : ProgressScope.DefaultStandalone;

            DisplayTree displayTree = BuildPlanWithinSession(
                useCompactSelection: false,
                useFeasibleBudget: false,
                initializeSession: true);
            SearchTree searchTree = ProjectSearchTreeFromCurrentExactSession();
            return (searchTree, displayTree);
        });
    }

    // Standalone search-only exact entry: starts its own solver session and materializes
    // the search tree from that session's exact caches.
    private SearchTree ProjectSearchTreeFromStandaloneExactSession()
    {
        return RunWithComparisonStateCancellation(() =>
        {
            InitializeExactSolverSession(useFeasibleBudget: false);
            return ProjectSearchTreeFromCurrentExactSession();
        });
    }

    // Current-session search materialization path shared by layered exact projection
    // and standalone search-only exact entry after session initialization.
    private SearchTree ProjectSearchTreeFromCurrentExactSession()
    {
        _useCompact = false;
        return ProjectSearchTreeFromCurrentExactCaches();
    }

    // Solver-sourced search builder: phase-1 group selection comes from exact caches,
    // and the search tree is materialized directly from solver state recursion.
    private SearchTree ProjectSearchTreeFromCurrentExactCaches()
    {
        var context = new SearchMaterializationContext(
            new Dictionary<IntSequenceKey, ExpandedStateSnapshot>(),
            new HashSet<IntSequenceKey>());
        SearchNode root = BuildSearchState(new ComparisonState(_n), 0, _k, 1, context);
        return new SearchStrategy(
            _n,
            _m,
            _requestedK,
            _k,
            root);
    }

    private SearchNode BuildSearchState(
        ComparisonState state,
        ulong fixedTopMask,
        int remainingSlots,
        int step,
        SearchMaterializationContext context)
    {
        ThrowIfCancellationRequested();
        NormalizeState(state, ref fixedTopMask, ref remainingSlots);

        IntSequenceKey expansionKey = SearchStateKeyService.GetDisplayStateKey(state, fixedTopMask);
        int stateId = GetStateId(expansionKey);

        if (remainingSlots == 0)
            return SearchNode.Terminal(stateId, ComparisonState.MaskToOrderedList(fixedTopMask));

        if (TryGetDeterminedTopSet(state, remainingSlots, out ulong determinedTopMask))
            return SearchNode.Terminal(stateId, ComparisonState.MaskToOrderedList(fixedTopMask | determinedTopMask));

        if (state.ActiveCount <= remainingSlots)
            return SearchNode.Terminal(stateId, ComparisonState.MaskToOrderedList(fixedTopMask | state.ActiveMask));

        var possibleCandidates = GetPossibleCandidates(state);
        if (state.ActiveCount <= _m)
            return SearchNode.Decision(stateId, step, possibleCandidates, Array.Empty<SearchBranch>());

        if (context.ExpandedStates.TryGetValue(expansionKey, out ExpandedStateSnapshot snapshot))
        {
            IReadOnlyList<ItemRelabel>? relabeling =
                snapshot.State.TryBuildDisplayRelabeling(snapshot.FixedTopMask, state, fixedTopMask);
            return SearchNode.Reference(stateId, relabeling);
        }

        bool trackingExpansionPath = TryTrackExpandedState(
            expansionKey,
            state,
            fixedTopMask,
            context.ExpandedStates,
            context.ExpansionPath,
            "Search materialization detected a recursive display-state expansion path.");

        try
        {
            SelectedComparisonGroup chosenGroup = ChooseGroup(state, fixedTopMask, remainingSlots, default);
            IReadOnlyList<SearchBranch> branches = BuildSearchBranches(
                state,
                fixedTopMask,
                remainingSlots,
                chosenGroup,
                step + 1,
                context);
            return SearchNode.Decision(stateId, step, chosenGroup.Group, branches);
        }
        finally
        {
            if (trackingExpansionPath)
                context.ExpansionPath!.Remove(expansionKey);
        }
    }

    private IReadOnlyList<SearchBranch> BuildSearchBranches(
        ComparisonState state,
        ulong fixedTopMask,
        int remainingSlots,
        SelectedComparisonGroup chosenGroup,
        int nextStep,
        SearchMaterializationContext context)
    {
        return BuildSearchTransitionSpecs(state, fixedTopMask, remainingSlots, chosenGroup)
            .Select(spec => new SearchBranch(
                spec.OrderText,
                spec.Effect,
                BuildSearchState(
                    spec.NextState,
                    spec.NextFixedTopMask,
                    spec.NextRemainingSlots,
                    nextStep,
                    context)))
            .ToList();
    }

    private readonly record struct SearchMaterializationContext(
        Dictionary<IntSequenceKey, ExpandedStateSnapshot> ExpandedStates,
        HashSet<IntSequenceKey> ExpansionPath);
}