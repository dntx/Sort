using System;
using System.Collections.Generic;

namespace TopKFinder;

partial class StrategyBuilder
{
    private MaterializationHelper? _materializationHelper;
    private MaterializationHelper Materialization => _materializationHelper ??= new MaterializationHelper(this);

    private sealed class MaterializationHelper
    {
        private readonly StrategyBuilder _owner;

        public MaterializationHelper(StrategyBuilder owner)
        {
            _owner = owner;
        }

        public StrategyNode BuildState(
            ComparisonState state,
            ulong fixedTopMask,
            int remainingSlots,
            int step,
            MaterializationContext context = default)
        {
            _owner.ThrowIfCancellationRequested();
            _owner.ThrottledReportProgressDuringFeasibleBuild();
            _owner.NormalizeState(state, ref fixedTopMask, ref remainingSlots);
            _owner.ObserveSearchState(state, remainingSlots);

            IntSequenceKey expansionKey = SearchStateKeyService.GetDisplayStateKey(state, fixedTopMask);
            int stateId = _owner.GetStateId(expansionKey);

            if (remainingSlots == 0)
                return StrategyNode.Terminal(stateId, ComparisonState.MaskToOrderedList(fixedTopMask));

            if (_owner.TryGetDeterminedTopSet(state, remainingSlots, out ulong determinedTopMask))
                return StrategyNode.Terminal(stateId, ComparisonState.MaskToOrderedList(fixedTopMask | determinedTopMask));

            if (state.ActiveCount <= remainingSlots)
                return StrategyNode.Terminal(stateId, ComparisonState.MaskToOrderedList(fixedTopMask | state.ActiveMask));

            var possibleCandidates = GetPossibleCandidates(state);
            if (state.ActiveCount <= _owner._m)
            {
                return StrategyNode.Decision(
                    stateId,
                    step,
                    possibleCandidates,
                    Array.Empty<StrategyBranch>(),
                    new FinalChoiceSummary(
                        ComparisonState.MaskToOrderedList(fixedTopMask),
                        possibleCandidates,
                        remainingSlots));
            }

            if (_owner._expandedStates.TryGetValue(expansionKey, out ExpandedStateSnapshot snapshot))
            {
                IReadOnlyList<ItemRelabel>? relabeling =
                    snapshot.State.TryBuildDisplayRelabeling(snapshot.FixedTopMask, state, fixedTopMask);
                return StrategyNode.Reference(stateId, relabeling);
            }

            bool trackingDisplayPath = _owner._useGreedyTightenSelection;
            TryTrackExpandedState(
                expansionKey,
                state,
                fixedTopMask,
                _owner._expandedStates,
                trackingDisplayPath ? _owner._materializationDisplayPath : null,
                "GreedyTighten materialization detected a recursive display-state expansion path.");

            try
            {
                SelectedComparisonGroup chosenGroup = ChooseGroup(
                    state,
                    fixedTopMask,
                    remainingSlots,
                    context);
                IReadOnlyList<StrategyBranch> branches = _owner.BuildBranches(
                    state,
                    fixedTopMask,
                    remainingSlots,
                    chosenGroup,
                    step + 1,
                    context);
                return StrategyNode.Decision(stateId, step, chosenGroup.Group, branches);
            }
            finally
            {
                if (trackingDisplayPath)
                    _owner._materializationDisplayPath.Remove(expansionKey);
            }
        }

        public List<int> GetPossibleCandidates(ComparisonState state)
        {
            return state.GetActiveItemsOrdered();
        }

        public SelectedComparisonGroup ChooseGroup(
            ComparisonState state,
            ulong fixedTopMask,
            int remainingSlots,
            MaterializationContext context)
        {
            _owner.ThrowIfCancellationRequested();

            if (context.ForceFixedConstructiveSelection)
            {
                List<int> constructiveGroup = _owner.ChooseConstructiveGroup(
                    state,
                    remainingSlots,
                    forceFixedCandidateSelection: true);
                return new SelectedComparisonGroup(
                    constructiveGroup,
                    BuildMergedComparisonOutcomes(state, fixedTopMask, remainingSlots, constructiveGroup));
            }

            if (_owner._useGreedyTightenSelection)
            {
                SearchStateKey key = SearchStateKeyService.BuildSearchStateKey(state, remainingSlots, _owner._canonicalKeyMemo);
                List<int> tightenGroup = _owner.CurrentGreedyTightenGroup(state, remainingSlots, key);
                if (!GroupAvoidsDisplayBackEdge(state, fixedTopMask, remainingSlots, tightenGroup))
                {
                    _owner._greedyTightenOverrides.Remove(key);
                    _owner._greedyTightenOverrideAnchors.Remove(key);
                    tightenGroup = _owner.ChooseConstructiveGroup(state, remainingSlots);
                    if (!GroupAvoidsDisplayBackEdge(state, fixedTopMask, remainingSlots, tightenGroup))
                    {
                        throw new InvalidOperationException(
                            "GreedyTighten materialization found no display-progress group at the current state.");
                    }
                }

                return new SelectedComparisonGroup(
                    tightenGroup,
                    BuildMergedComparisonOutcomes(state, fixedTopMask, remainingSlots, tightenGroup));
            }

            var candidates = state.GetActiveItemsOrdered();
            SearchStateKey currentKey = SearchStateKeyService.BuildSearchStateKey(state, remainingSlots, _owner._canonicalKeyMemo);

            BestGroupPattern cachedPattern;
            if (_owner._useCompact)
            {
                if (!_owner._compactPatternCacheReadyForMaterialization)
                {
                    throw new InvalidOperationException(
                        "Compact phase 1b must finish with a complete group-pattern cache before phase 2 materialization.");
                }

                if (!_owner._compactGroupPatternCache.TryGetValue(currentKey, out BestGroupPattern compactPattern))
                {
                    throw new InvalidOperationException(
                        "Compact phase 1b must populate the group-pattern cache for every state materialized in phase 2.");
                }

                cachedPattern = compactPattern;
            }
            else if (!_owner._bestGroupPatternCache.TryGetValue(currentKey, out cachedPattern))
            {
                throw new InvalidOperationException(
                    "Phase 1 must populate the best-group pattern cache for every state materialized in phase 2.");
            }

            int[]? colorSignature = cachedPattern.ColorSignature;
            int[]? activeColors = colorSignature is null ? null : state.GetActiveItemColors();

            foreach (var group in CombinatoricsService.EnumerateCombinations(
                candidates,
                cachedPattern.GroupSize,
                () => _owner.ProbeCancellation(0)))
            {
                if (activeColors is not null && !GroupEnumerationService.GroupMatchesColorSignature(activeColors, group, colorSignature!))
                    continue;

                if (GetGroupPattern(state, group) == cachedPattern.Pattern)
                {
                    _owner._bestGroupPatternCacheHits++;
                    return new SelectedComparisonGroup(group, BuildMergedComparisonOutcomes(state, fixedTopMask, remainingSlots, group));
                }
            }

            throw new InvalidOperationException(
                "Cached best-group pattern did not match any candidate combination in the current state.");
        }

        private IReadOnlyList<MergedBranch> BuildMergedComparisonOutcomes(
            ComparisonState state,
            ulong fixedTopMask,
            int remainingSlots,
            IReadOnlyList<int> group)
        {
            return _owner.VisitComparisonOutcomes(
                state,
                fixedTopMask,
                remainingSlots,
                group,
                currentKey: null,
                collectMergedBranches: true,
                onUsefulOutcome: _ => true).MergedBranches;
        }

        private bool GroupAvoidsDisplayBackEdge(
            ComparisonState state,
            ulong fixedTopMask,
            int remainingSlots,
            IReadOnlyList<int> group)
        {
            bool anyOutcome = false;
            foreach (ComparisonOutcome outcome in _owner.EnumerateDisplayOutcomes(state, remainingSlots, group))
            {
                anyOutcome = true;
                IntSequenceKey nextDisplayKey = SearchStateKeyService.GetDisplayStateKey(
                    outcome.NextState,
                    fixedTopMask | outcome.AddedFixedTopMask);
                if (_owner._materializationDisplayPath.Contains(nextDisplayKey))
                    return false;
            }

            return anyOutcome;
        }
    }
}