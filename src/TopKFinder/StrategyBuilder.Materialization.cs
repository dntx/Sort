using System;
using System.Collections.Generic;
using System.Linq;

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

    private sealed class GroupSelectionHelper
    {
        private static void GenerateClassRepresentatives(
            StrategyBuilder owner,
            ComparisonState state,
            List<List<int>> classes,
            int[] suffixCapacity,
            int classIndex,
            int remaining,
            List<int> prefix,
            List<List<int>> collected,
            int generationCap)
        {
            owner.ProbeCancellation();

            if (collected.Count >= generationCap)
                return;

            if (remaining == 0)
            {
                owner.ThrowIfCancellationRequested();
                owner._candidateGroupsEnumerated++;
                var group = new List<int>(prefix);
                group.Sort();
                collected.Add(group);
                return;
            }

            // Prune branches that can no longer reach the required group size.
            if (classIndex == classes.Count || suffixCapacity[classIndex] < remaining)
                return;

            List<int> cls = classes[classIndex];
            int maxTake = Math.Min(cls.Count, remaining);
            for (int take = 0; take <= maxTake; take++)
            {
                for (int j = 0; j < take; j++)
                    prefix.Add(cls[j]);

                GenerateClassRepresentatives(
                    owner,
                    state,
                    classes,
                    suffixCapacity,
                    classIndex + 1,
                    remaining - take,
                    prefix,
                    collected,
                    generationCap);

                prefix.RemoveRange(prefix.Count - take, take);

                if (collected.Count >= generationCap)
                {
                    // Stopped short of trying the remaining (larger-`take`) siblings at this level
                    // because the cap filled up: the enumeration is genuinely truncated. Flag it so
                    // a probe that concludes infeasible under a finite cap is reported as incomplete,
                    // not a proof.
                    if (generationCap != int.MaxValue)
                        owner._compactEnumerationCapped = true;
                    return;
                }
            }
        }

        public static IntSequenceKey GetGroupPattern(ComparisonState state, IReadOnlyList<int> group)
        {
            ulong mask = 0;
            for (int i = 0; i < group.Count; i++)
                mask |= 1UL << group[i];
            return state.GetGroupCanonicalKey(mask);
        }

        // Builds a BestGroupPattern carrying both the canonical group pattern and a cheap color
        // pre-filter signature (the sorted multiset of the group's per-item active colors). ChooseGroup
        // uses the signature to skip the expensive canonical-key check for groups that cannot match.
        public static BestGroupPattern MakeGroupPattern(ComparisonState state, IReadOnlyList<int> group)
        {
            int[] colors = state.GetActiveItemColors();
            return new BestGroupPattern(
                group.Count,
                GetGroupPattern(state, group),
                GroupEnumerationService.BuildSortedColorSignature(colors, group));
        }

        public static IEnumerable<List<int>> EnumerateDistinctGroups(
            StrategyBuilder owner,
            ComparisonState state,
            IReadOnlyList<int> candidates,
            int groupSize,
            int generationCap)
        {
            // Exploit the active poset's automorphisms to avoid enumerating all C(active, groupSize)
            // combinations. Active items are partitioned into "free symmetry classes" (items with
            // identical active-restricted ancestor and descendant sets); every within-class permutation
            // is an automorphism, so all size-a selections from a class lie in one orbit and the class's
            // a smallest items canonically represent them. We therefore build a single candidate per
            // per-class count vector and canonically de-duplicate across classes, keeping the
            // lexicographically smallest member of each orbit. This produces exactly one representative
            // per orbit - identical to scanning every combination - but builds far fewer candidates on
            // symmetric states (e.g. a single candidate at the fully symmetric root instead of C(n, m)).
            //
            // generationCap bounds how many raw representatives we generate before the (cap-bounded) orbit
            // dedup and sort. The default int.MaxValue is the exact, complete enumeration used by the exact
            // compact DP and the optimality-gap audit; the greedy edge phase passes a finite cap so a single
            // large-m state cannot generate (and then FitChildren over) thousands of groups -- the
            // materialized generation and McKay dedup over the full set is what makes that phase hang.
            List<List<int>> classes = state.GetFreeSymmetryClasses();

            var suffixCapacity = new int[classes.Count + 1];
            for (int c = classes.Count - 1; c >= 0; c--)
                suffixCapacity[c] = suffixCapacity[c + 1] + classes[c].Count;

            var collected = new List<List<int>>();
            var prefix = new List<int>(groupSize);
            GenerateClassRepresentatives(owner, state, classes, suffixCapacity, 0, groupSize, prefix, collected, generationCap);

            // Orbit de-duplication via a cheap pre-filter. The full group canonical key
            // (GetGroupPattern -> McKay) is the only sound way to merge two groups that lie in the same
            // automorphism orbit, but it is expensive and dominates the search cost. Color-refinement
            // structural labels are an automorphism invariant, so two groups in the same orbit always
            // share the same sorted multiset of member labels. We bucket groups by that cheap signature:
            // groups with distinct signatures are provably in different orbits and need no canonical key,
            // so McKay only runs to disambiguate groups that collide on the cheap signature.
            int[] labels = state.GetStructuralLabels();
            var buckets = new Dictionary<IntSequenceKey, List<List<int>>>();
            foreach (var group in collected)
            {
                owner.ProbeCancellation();
                IntSequenceKey cheap = GroupEnumerationService.BuildCheapGroupSignature(labels, group);
                if (!buckets.TryGetValue(cheap, out List<List<int>>? bucket))
                {
                    bucket = new List<List<int>>();
                    buckets[cheap] = bucket;
                }

                bucket.Add(group);
            }

            var ordered = new List<List<int>>(buckets.Count);
            foreach (List<List<int>> bucket in buckets.Values)
            {
                owner.ProbeCancellation();
                if (bucket.Count == 1)
                {
                    ordered.Add(bucket[0]);
                    continue;
                }

                var representatives = new Dictionary<IntSequenceKey, List<int>>();
                foreach (List<int> group in bucket)
                {
                    owner.ProbeCancellation();
                    IntSequenceKey pattern = GetGroupPattern(state, group);
                    if (!representatives.TryGetValue(pattern, out List<int>? existing) ||
                        GroupEnumerationService.CompareGroupsLexicographically(group, existing) < 0)
                    {
                        representatives[pattern] = group;
                    }
                }

                ordered.AddRange(representatives.Values);
            }

            ordered.Sort(GroupEnumerationService.CompareGroupsLexicographically);
            return ordered;
        }

        public static IEnumerable<List<int>> EnumeratePrioritizedGroups(
            StrategyBuilder owner,
            ComparisonState state,
            int remainingSlots,
            IReadOnlyList<int> candidates,
            int groupSize)
        {
            var scoredGroups = new List<(List<int> Group, HeuristicGroupScore Score)>();
            foreach (var group in EnumerateDistinctGroups(owner, state, candidates, groupSize, int.MaxValue))
            {
                owner.ThrowIfCancellationRequested();
                scoredGroups.Add((group, BuildHeuristicGroupScore(state, remainingSlots, group)));
            }

            foreach (var entry in scoredGroups.OrderByDescending(entry => entry.Score))
                yield return entry.Group;
        }

        public static HeuristicGroupScore BuildHeuristicGroupScore(ComparisonState state, int remainingSlots, IReadOnlyList<int> group)
        {
            var components = BranchSelectionScoringService.BuildScoreComponents(state, remainingSlots, group);
            return new HeuristicGroupScore(
                components.GuaranteedTopHits,
                components.FreshItems,
                components.UnrelatedScore,
                components.UnresolvedPairs,
                components.GroupSize);
        }
    }
}