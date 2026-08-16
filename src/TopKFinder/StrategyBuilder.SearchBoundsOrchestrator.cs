using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TopKFinder;

partial class StrategyBuilder
{
    private sealed class SearchBoundsOrchestrator
    {
        private readonly StrategyBuilder _owner;

        public SearchBoundsOrchestrator(StrategyBuilder owner)
        {
            _owner = owner;
        }

        // Shared prelude for exact, bounded, and lower-bound queries:
        // normalize once, record observability once, and short-circuit terminal shapes.
        private bool TryPrepareAndResolveTerminal(ComparisonState state, ref int remainingSlots, out int steps)
        {
            _owner.ThrowIfCancellationRequested();
            ulong ignoredFixedTopMask = 0;
            _owner.NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);
            _owner.ObserveSearchState(state, remainingSlots);

            if (remainingSlots == 0)
            {
                steps = 0;
                return true;
            }

            if (_owner.TryGetDeterminedTopSet(state, remainingSlots, out _))
            {
                steps = 0;
                return true;
            }

            if (state.ActiveCount <= remainingSlots)
            {
                steps = 0;
                return true;
            }

            if (state.ActiveCount <= _owner._m)
            {
                steps = 1;
                return true;
            }

            steps = -1;
            return false;
        }

        private bool TryGetCachedExactSteps(SearchStateKey key, out int cached)
        {
            if (_owner._minWorstCaseStepsCache.TryGetValue(key, out cached))
            {
                _owner._exactCacheHits++;
                return true;
            }

            return false;
        }

        // Canonical exact-resolution write path used by both exact and bounded searches.
        private void CommitExactResolution(
            SearchStateKey key,
            ComparisonState state,
            int remainingSlots,
            int bestWorstCase,
            List<int>? bestGroup,
            bool dominanceProbed,
            DominanceProbeResult dominanceProbe)
        {
            if (bestGroup is not null)
                _owner._bestGroupPatternCache[key] = StrategyBuilder.MakeGroupPattern(state, bestGroup);

            _owner._minWorstCaseStepsCache[key] = bestWorstCase;

            if (_owner.EnableDominanceMetric && dominanceProbed)
                _owner.RecordDominanceProbe(dominanceProbe, bestWorstCase, state, remainingSlots);
            _owner.AddDominanceLibraryEntry(state, remainingSlots, bestWorstCase);
        }

        private bool TryGetKnownLowerBoundOverBudget(SearchStateKey key, int analyticLowerBound, int budget, out int knownLowerBound)
        {
            knownLowerBound = analyticLowerBound;
            if (_owner._searchLowerBoundCache.TryGetValue(key, out int learned) && learned > knownLowerBound)
                knownLowerBound = learned;

            return knownLowerBound > budget;
        }

        // Bounded-search fail path: publish the strongest proven lower bound for future passes.
        private int RegisterFailBound(SearchStateKey key, int budget, int bestWorstCase, int failSoftBound)
        {
            int failBound = failSoftBound == int.MaxValue ? bestWorstCase : failSoftBound;
            if (failBound <= budget)
                failBound = budget + 1;

            if (!_owner._searchLowerBoundCache.TryGetValue(key, out int prior) || failBound > prior)
                _owner._searchLowerBoundCache[key] = failBound;

            return failBound;
        }

        public int GetMinWorstCaseSteps(ComparisonState state, int remainingSlots)
        {
            bool useIterativeDeepening = _owner.ForceIterativeDeepeningForTesting ?? _owner._useIterativeDeepening;
            if (!useIterativeDeepening)
            {
                int exact = GetMinWorstCaseStepsExact(state, remainingSlots);
                if (_owner._recordRootIncumbents)
                    _owner.RecordRootProvenLowerBound(exact);
                return exact;
            }

            int budget = GetMinWorstCaseLowerBound(state, remainingSlots);
            while (true)
            {
                if (_owner._recordRootIncumbents)
                    _owner.RecordRootProvenLowerBound(budget);

                int result = GetMinWorstCaseStepsBounded(state, remainingSlots, budget, depth: 0);
                if (result <= budget)
                {
                    if (_owner._recordRootIncumbents)
                        _owner.RecordRootProvenLowerBound(result);
                    return result;
                }

                budget = result;
            }
        }

        public int GetMinWorstCaseStepsExact(ComparisonState state, int remainingSlots)
        {
            if (TryPrepareAndResolveTerminal(state, ref remainingSlots, out int terminalSteps))
                return terminalSteps;

            SearchStateKey key = _owner.GetSearchStateKey(state, remainingSlots);
            if (TryGetCachedExactSteps(key, out int cached))
                return cached;

            DominanceProbeResult dominanceProbe = default;
            bool dominanceProbed = false;
            if (_owner.EnableDominanceMetric && state.ActiveCount > _owner._m && remainingSlots > 0)
            {
                dominanceProbe = _owner.ProbeDominance(state, remainingSlots);
                dominanceProbed = true;
            }

            bool isRootSearch = false;
            if (_owner._recordRootIncumbents && !_owner._rootSearchInitialized)
            {
                _owner._rootSearchInitialized = true;
                isRootSearch = true;
            }

            _owner.EnterSearchState();

            var candidates = state.GetActiveItemsOrdered();
            int groupSize = Math.Min(_owner._m, candidates.Count);
            IEnumerable<List<int>> groups =
                _owner.EnumeratePrioritizedGroups(state, remainingSlots, candidates, groupSize);
            List<int>? bestGroup = null;
            int bestWorstCase = int.MaxValue;
            int stateLowerBound = GetMinWorstCaseLowerBound(state, remainingSlots);
            try
            {
                _owner.ThrowIfCancellationRequested();
                foreach (var group in groups)
                {
                    _owner.ThrowIfCancellationRequested();
                    int groupWorstCase = 0;
                    OutcomeTraversalSummary traversal = _owner.VisitComparisonOutcomes(
                        state,
                        fixedTopMask: 0,
                        remainingSlots,
                        group,
                        key,
                        collectMergedBranches: false,
                        onUsefulOutcome: outcome =>
                        {
                            int branchLowerBound = 1 + GetMinWorstCaseLowerBound(outcome.NextState, outcome.NextRemainingSlots);
                            if (branchLowerBound >= bestWorstCase)
                            {
                                _owner._lowerBoundPrunes++;
                                groupWorstCase = branchLowerBound;
                                return false;
                            }

                            int branchSteps = 1 + GetMinWorstCaseStepsExact(outcome.NextState, outcome.NextRemainingSlots);
                            groupWorstCase = Math.Max(groupWorstCase, branchSteps);
                            return groupWorstCase < bestWorstCase;
                        });

                    if (traversal.IsUseful && groupWorstCase < bestWorstCase)
                    {
                        int previousBestWorstCase = bestWorstCase;
                        bestWorstCase = Math.Min(bestWorstCase, groupWorstCase);
                        bestGroup = group;
                        if (isRootSearch && bestWorstCase < previousBestWorstCase)
                            _owner.RecordRootIncumbent(bestWorstCase, group);

                        if (bestWorstCase <= stateLowerBound)
                            break;
                    }
                }
            }
            finally
            {
                _owner.ExitSearchState();
            }

            if (bestWorstCase == int.MaxValue)
                throw new InvalidOperationException("Expected at least one useful comparison group when unresolved candidates exceed comparison size.");

            CommitExactResolution(key, state, remainingSlots, bestWorstCase, bestGroup, dominanceProbed, dominanceProbe);

            return bestWorstCase;
        }

        public int GetMinWorstCaseStepsBounded(ComparisonState state, int remainingSlots, int budget, int depth)
        {
            if (TryPrepareAndResolveTerminal(state, ref remainingSlots, out int terminalSteps))
                return terminalSteps;

            SearchStateKey key = _owner.GetSearchStateKey(state, remainingSlots);
            if (TryGetCachedExactSteps(key, out int cached))
                return cached;

            int analyticLowerBound = GetMinWorstCaseLowerBound(state, remainingSlots);
            if (TryGetKnownLowerBoundOverBudget(key, analyticLowerBound, budget, out int knownLowerBound))
                return knownLowerBound;

            DominanceProbeResult dominanceProbe = default;
            bool dominanceProbed = false;
            if (_owner.EnableDominanceMetric && state.ActiveCount > _owner._m && remainingSlots > 0)
            {
                dominanceProbe = _owner.ProbeDominance(state, remainingSlots);
                dominanceProbed = true;
            }

            _owner.EnterSearchState();

            var candidates = state.GetActiveItemsOrdered();
            int groupSize = Math.Min(_owner._m, candidates.Count);
            IEnumerable<List<int>> groups =
                _owner.EnumeratePrioritizedGroups(state, remainingSlots, candidates, groupSize);
            List<int>? bestGroup = null;
            int bestWorstCase = budget + 1;
            int failSoftBound = int.MaxValue;
            bool anyUseful = false;
            int stateLowerBound = analyticLowerBound;
            try
            {
                _owner.ThrowIfCancellationRequested();
                foreach (var group in groups)
                {
                    _owner.ThrowIfCancellationRequested();
                    int groupWorstCase = 0;
                    int childBudget = bestWorstCase - 2;
                    OutcomeTraversalSummary traversal = _owner.VisitComparisonOutcomes(
                        state,
                        fixedTopMask: 0,
                        remainingSlots,
                        group,
                        key,
                        collectMergedBranches: false,
                        onUsefulOutcome: outcome =>
                        {
                            int branchLowerBound = 1 + GetMinWorstCaseLowerBound(outcome.NextState, outcome.NextRemainingSlots);
                            if (branchLowerBound >= bestWorstCase)
                            {
                                _owner._lowerBoundPrunes++;
                                groupWorstCase = Math.Max(groupWorstCase, branchLowerBound);
                                return false;
                            }

                            int childResult = GetMinWorstCaseStepsBounded(
                                outcome.NextState, outcome.NextRemainingSlots, childBudget, depth + 1);
                            int branchSteps = 1 + childResult;
                            groupWorstCase = Math.Max(groupWorstCase, branchSteps);
                            return groupWorstCase < bestWorstCase;
                        });

                    if (!traversal.IsUseful)
                        continue;

                    anyUseful = true;
                    if (groupWorstCase < bestWorstCase)
                    {
                        int previousBestWorstCase = bestWorstCase;
                        bestWorstCase = groupWorstCase;
                        bestGroup = group;
                        if (depth == 0 && _owner._recordRootIncumbents && bestWorstCase < previousBestWorstCase)
                            _owner.RecordRootIncumbent(bestWorstCase, group);

                        if (bestWorstCase <= stateLowerBound)
                            break;
                    }
                    else
                    {
                        failSoftBound = Math.Min(failSoftBound, groupWorstCase);
                    }
                }
            }
            finally
            {
                _owner.ExitSearchState();
            }

            if (!anyUseful)
                throw new InvalidOperationException("Expected at least one useful comparison group when unresolved candidates exceed comparison size.");

            if (bestWorstCase <= budget)
            {
                CommitExactResolution(key, state, remainingSlots, bestWorstCase, bestGroup, dominanceProbed, dominanceProbe);
                return bestWorstCase;
            }

            return RegisterFailBound(key, budget, bestWorstCase, failSoftBound);
        }

        public int GetMinWorstCaseLowerBound(ComparisonState state, int remainingSlots)
        {
            long startTimestamp = Stopwatch.GetTimestamp();
            _owner._lowerBoundCalls++;
            try
            {
                if (TryPrepareAndResolveTerminal(state, ref remainingSlots, out int terminalSteps))
                    return terminalSteps;

                SearchStateKey key = _owner.GetSearchStateKey(state, remainingSlots);
                if (_owner._lowerBoundStepsCache.TryGetValue(key, out int cached))
                {
                    _owner._lowerBoundCacheHits++;
                    return cached;
                }

                FeasibleTopSetInfo info = _owner.GetFeasibleTopSetInfo(state, remainingSlots);
                int steps = _owner.GetInformationLowerBoundSteps(info.Count, state.ActiveCount);

                int antichain = _owner.GetAntichainLowerBound(state, out int width);
                steps = Math.Max(steps, antichain);
                int groupSize = Math.Min(_owner._m, state.ActiveCount);
                if (_owner.EnableWidthLimitedInformationBoundForTesting && width < groupSize)
                {
                    steps = Math.Max(
                        steps,
                        _owner.GetWidthLimitedInformationLowerBoundSteps(info.Count, state.ActiveCount, width));
                }
                steps = Math.Max(steps, 2);

                steps = _owner.ApplyDominanceLowerBound(state, remainingSlots, steps);

                _owner._lowerBoundStepsCache[key] = steps;
                return steps;
            }
            finally
            {
                _owner._lowerBoundElapsedTicks += Stopwatch.GetElapsedTime(startTimestamp).Ticks;
            }
        }
    }
}
