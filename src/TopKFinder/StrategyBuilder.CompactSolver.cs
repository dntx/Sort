using System;
using System.Collections.Generic;

namespace TopKFinder;

partial class StrategyBuilder
{
    // Encapsulates compact-stage solve algorithms while reusing StrategyBuilder's session/state.
    private sealed class CompactSolver
    {
        private readonly StrategyBuilder _owner;

        public CompactSolver(StrategyBuilder owner)
        {
            _owner = owner;
        }

        public int SolveCompact(ComparisonState state, int remainingSlots, int feasibleBudget = int.MaxValue)
        {
            _owner.ThrowIfCancellationRequested();
            ulong ignoredFixedTopMask = 0;
            _owner.NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);

            if (TryResolveCompactTerminalOrCached(
                    state,
                    remainingSlots,
                    feasibleBudget,
                    out int resolvedCost,
                    out int optimalSteps,
                    out SearchStateKey key,
                    out (SearchStateKey, int) memoKey))
            {
                return resolvedCost;
            }

            _owner._compactCostMemo[memoKey] = int.MaxValue;
            _owner._compactStatesSolved++;
            _owner.ReportProgress();

            if (_owner._compactUsesFeasibleBudget)
                return _owner._compactFeasibilityOnly
                    ? SolveBudgetFeasibility(state, remainingSlots, optimalSteps, key, memoKey)
                    : SolveEdgeCompactGreedy(state, remainingSlots, optimalSteps, key, memoKey);

            var candidates = state.GetActiveItemsOrdered();
            int groupSize = Math.Min(_owner._m, candidates.Count);

            var selection = SelectBestCompactGroup(state, remainingSlots, key, optimalSteps, candidates, groupSize);
            if (selection.BestGroup is null)
            {
                if (_owner._compactUsesFeasibleBudget)
                    return int.MaxValue;
                throw new InvalidOperationException("Compact selection found no step-optimal comparison group.");
            }

            return FinalizeCompactSelection(
                state,
                key,
                memoKey,
                selection.BestGroup,
                selection.BestCost,
                selection.BestChildren);
        }

        public int SolveEdgeCompactGreedy(
            ComparisonState state, int remainingSlots, int budget, SearchStateKey key, (SearchStateKey, int) memoKey)
        {
            var candidates = state.GetActiveItemsOrdered();
            int groupSize = Math.Min(_owner._m, candidates.Count);
            int branchBudget = budget - 1;

            var fits = CollectBudgetFeasibleCandidates(
                state,
                remainingSlots,
                key,
                candidates,
                groupSize,
                branchBudget);

            foreach (var (group, children) in fits)
            {
                if (!TrySumChildCostsWithinBudget(children, branchBudget, out int branchCostSum))
                    continue;

                CacheCompactPatternForBudget(key, state, group, budget);
                int cost = children.Count + branchCostSum;
                _owner._compactCostMemo[memoKey] = cost;

                _owner._compactRealStepsMemo[key] = 1 + ComputeChildrenRealSteps(children);
                return cost;
            }

            return int.MaxValue;
        }

        public int SolveBudgetFeasibility(
            ComparisonState state, int remainingSlots, int budget, SearchStateKey key, (SearchStateKey, int) memoKey)
        {
            var candidates = state.GetActiveItemsOrdered();
            int groupSize = Math.Min(_owner._m, candidates.Count);
            int branchBudget = budget - 1;

            var fits = CollectBudgetFeasibleCandidates(
                state,
                remainingSlots,
                key,
                candidates,
                groupSize,
                branchBudget);

            foreach (var (group, children) in fits)
            {
                if (!TryGetChildrenRealStepsWithinBudget(children, branchBudget, out int realSteps))
                    continue;

                _owner._compactGroupPatternCache[key] = MakeGroupPattern(state, group);
                _owner._compactGroupPatternTightestBudget[key] = budget;
                int cost = 1 + realSteps;
                _owner._compactCostMemo[memoKey] = cost;
                _owner._compactRealStepsMemo[key] = cost;
                return cost;
            }

            return int.MaxValue;
        }

        private bool TryResolveCompactTerminalOrCached(
            ComparisonState state,
            int remainingSlots,
            int feasibleBudget,
            out int resolvedCost,
            out int optimalSteps,
            out SearchStateKey key,
            out (SearchStateKey, int) memoKey)
        {
            resolvedCost = 0;
            optimalSteps = _owner._compactUsesFeasibleBudget
                ? feasibleBudget
                : _owner.GetMinWorstCaseSteps(state, remainingSlots);
            key = _owner.GetSearchStateKey(state, remainingSlots);
            memoKey = (key, optimalSteps);

            if (remainingSlots == 0
                || _owner.TryGetDeterminedTopSet(state, remainingSlots, out _)
                || state.ActiveCount <= remainingSlots
                || state.ActiveCount <= _owner._m)
            {
                return true;
            }

            if (_owner._compactCostMemo.TryGetValue(memoKey, out int cachedCost))
            {
                resolvedCost = cachedCost;
                return true;
            }

            if (_owner._compactUsesFeasibleBudget
                && _owner._compactFeasibilityOnly
                && _owner._compactGroupPatternTightestBudget.TryGetValue(key, out int existingBudget)
                && existingBudget <= optimalSteps
                && _owner._compactGroupPatternCache.ContainsKey(key))
            {
                int reusedCost;
                if (_owner._compactCostMemo.TryGetValue((key, existingBudget), out int tighterCost)
                    && tighterCost != int.MaxValue)
                {
                    reusedCost = tighterCost;
                }
                else if (_owner._compactRealStepsMemo.TryGetValue(key, out int cachedRealSteps)
                         && cachedRealSteps <= optimalSteps)
                {
                    reusedCost = cachedRealSteps;
                }
                else
                {
                    reusedCost = -1;
                }

                if (reusedCost >= 0)
                {
                    _owner._compactCostMemo[memoKey] = reusedCost;
                    resolvedCost = reusedCost;
                    return true;
                }
            }

            resolvedCost = int.MaxValue;
            return false;
        }

        private (List<int>? BestGroup, int BestCost, List<(ComparisonState State, int RemainingSlots)>? BestChildren)
            SelectBestCompactGroup(
                ComparisonState state,
                int remainingSlots,
                SearchStateKey key,
                int optimalSteps,
                IReadOnlyList<int> candidates,
                int groupSize)
        {
            List<int>? bestGroup = null;
            int bestCost = int.MaxValue;
            List<(ComparisonState State, int RemainingSlots)>? bestChildren = null;
            int branchBudget = optimalSteps - 1;

            foreach (var group in _owner.EnumerateDistinctGroups(state, candidates, groupSize))
            {
                _owner.ThrowIfCancellationRequested();
                _owner._compactGroupsEnumerated++;

                var children = EvaluateStepOptimalChildren(state, remainingSlots, key, group, branchBudget);
                if (children is null)
                    continue;
                _owner._compactStepOptimalGroups++;

                if (children.Count >= bestCost)
                    continue;

                if (!TrySumChildCostsWithPruning(children, branchBudget, bestCost, out int branchCostSum))
                    continue;

                int groupCost = children.Count + branchCostSum;
                if (groupCost < bestCost)
                {
                    bestCost = groupCost;
                    bestGroup = group;
                    bestChildren = children;
                }
            }

            return (bestGroup, bestCost, bestChildren);
        }

        private List<(ComparisonState State, int RemainingSlots)>? EvaluateStepOptimalChildren(
            ComparisonState state,
            int remainingSlots,
            SearchStateKey key,
            IReadOnlyList<int> group,
            int branchBudget)
        {
            bool rejected = false;
            var children = new List<(ComparisonState State, int RemainingSlots)>();
            OutcomeTraversalSummary traversal = _owner.VisitComparisonOutcomes(
                state,
                fixedTopMask: 0,
                remainingSlots,
                group,
                currentKey: key,
                collectMergedBranches: false,
                onUsefulOutcome: outcome =>
                {
                    bool overBudget = _owner.GetMinWorstCaseLowerBound(outcome.NextState, outcome.NextRemainingSlots) > branchBudget;
                    if (!overBudget && !_owner._compactUsesFeasibleBudget)
                        overBudget = _owner.GetMinWorstCaseSteps(outcome.NextState, outcome.NextRemainingSlots) > branchBudget;
                    if (overBudget)
                    {
                        rejected = true;
                        return false;
                    }

                    children.Add((outcome.NextState, outcome.NextRemainingSlots));
                    return true;
                });

            return rejected || !traversal.IsUseful ? null : children;
        }

        private List<(ComparisonState State, int RemainingSlots)>? EvaluateBudgetFitChildren(
            ComparisonState state,
            int remainingSlots,
            SearchStateKey key,
            IReadOnlyList<int> group,
            int branchBudget)
        {
            bool rejected = false;
            var children = new List<(ComparisonState State, int RemainingSlots)>();
            OutcomeTraversalSummary traversal = _owner.VisitComparisonOutcomes(
                state,
                fixedTopMask: 0,
                remainingSlots,
                group,
                currentKey: key,
                collectMergedBranches: false,
                onUsefulOutcome: outcome =>
                {
                    if (_owner.GetMinWorstCaseLowerBound(outcome.NextState, outcome.NextRemainingSlots) > branchBudget)
                    {
                        rejected = true;
                        return false;
                    }

                    children.Add((outcome.NextState, outcome.NextRemainingSlots));
                    return true;
                });

            return rejected || !traversal.IsUseful ? null : children;
        }

        private List<(List<int> Group, List<(ComparisonState State, int RemainingSlots)> Children)> CollectBudgetFeasibleCandidates(
            ComparisonState state,
            int remainingSlots,
            SearchStateKey key,
            IReadOnlyList<int> candidates,
            int groupSize,
            int branchBudget)
        {
            var fits = new List<(List<int> Group, List<(ComparisonState State, int RemainingSlots)> Children)>();

            List<int> constructiveGroup = _owner.ChooseConstructiveGroup(state, remainingSlots);
            var seen = new HashSet<IntSequenceKey>();
            var constructiveChildren = EvaluateBudgetFitChildren(state, remainingSlots, key, constructiveGroup, branchBudget);
            if (constructiveChildren is not null)
            {
                _owner._compactGroupsEnumerated++;
                _owner._compactStepOptimalGroups++;
                seen.Add(new IntSequenceKey(constructiveGroup.ToArray()));
                fits.Add((constructiveGroup, constructiveChildren));
            }

            int candidateCap = _owner.GetCompactGreedyCandidateCap(candidates.Count, groupSize);
            foreach (var group in _owner.EnumerateDistinctGroups(state, candidates, groupSize, candidateCap))
            {
                if (!seen.Add(new IntSequenceKey(group.ToArray())))
                    continue;

                _owner.ThrowIfCancellationRequested();
                _owner._compactGroupsEnumerated++;

                var children = EvaluateBudgetFitChildren(state, remainingSlots, key, group, branchBudget);
                if (children is null)
                    continue;

                _owner._compactStepOptimalGroups++;
                fits.Add((group, children));
            }

            fits.Sort((a, b) => a.Children.Count.CompareTo(b.Children.Count));
            return fits;
        }

        private bool TrySumChildCostsWithPruning(
            List<(ComparisonState State, int RemainingSlots)> children,
            int branchBudget,
            int bestCost,
            out int branchCostSum)
        {
            branchCostSum = 0;
            for (int i = 0; i < children.Count; i++)
            {
                int childCost = SolveCompact(children[i].State, children[i].RemainingSlots, branchBudget);
                if (childCost == int.MaxValue)
                    return false;

                branchCostSum += childCost;
                if (children.Count + branchCostSum >= bestCost)
                    return false;
            }

            return true;
        }

        private bool TrySumChildCostsWithinBudget(
            List<(ComparisonState State, int RemainingSlots)> children,
            int branchBudget,
            out int branchCostSum)
        {
            branchCostSum = 0;
            foreach (var (childState, childRemaining) in children)
            {
                int childCost = SolveCompact(childState, childRemaining, branchBudget);
                if (childCost == int.MaxValue)
                    return false;

                branchCostSum += childCost;
            }

            return true;
        }

        private bool TryGetChildrenRealStepsWithinBudget(
            List<(ComparisonState State, int RemainingSlots)> children,
            int branchBudget,
            out int realSteps)
        {
            realSteps = 0;
            foreach (var (childState, childRemaining) in children)
            {
                int childCost = SolveCompact(childState, childRemaining, branchBudget);
                if (childCost == int.MaxValue)
                    return false;

                realSteps = Math.Max(realSteps, GetCompactRealSteps(childState, childRemaining));
            }

            return true;
        }

        private int FinalizeCompactSelection(
            ComparisonState state,
            SearchStateKey key,
            (SearchStateKey, int) memoKey,
            List<int> bestGroup,
            int bestCost,
            List<(ComparisonState State, int RemainingSlots)>? bestChildren)
        {
            _owner._compactGroupPatternCache[key] = MakeGroupPattern(state, bestGroup);
            _owner._compactCostMemo[memoKey] = bestCost;
            _owner._compactRealStepsMemo[key] = 1 + ComputeChildrenRealSteps(bestChildren);
            return bestCost;
        }

        private int ComputeChildrenRealSteps(List<(ComparisonState State, int RemainingSlots)>? children)
        {
            if (children is null)
                return 0;

            int realSteps = 0;
            foreach (var (childState, childRemaining) in children)
                realSteps = Math.Max(realSteps, GetCompactRealSteps(childState, childRemaining));

            return realSteps;
        }

        public int GetCompactRealSteps(ComparisonState state, int remainingSlots)
        {
            ulong ignoredFixedTopMask = 0;
            _owner.NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);
            if (remainingSlots == 0)
                return 0;
            if (_owner.TryGetDeterminedTopSet(state, remainingSlots, out _))
                return 0;
            if (state.ActiveCount <= remainingSlots)
                return 0;
            if (state.ActiveCount <= _owner._m)
                return 0;
            return _owner._compactRealStepsMemo.TryGetValue(_owner.GetSearchStateKey(state, remainingSlots), out int steps)
                ? steps
                : 0;
        }

        public int ComputeSearchTreeEdgesForSelection(bool useCompactSelection)
        {
            bool previousUseCompact = _owner._useCompact;
            _owner._useCompact = useCompactSelection;
            try
            {
                var memo = new Dictionary<SearchStateKey, int>();
                return ComputeSearchTreeEdges(new ComparisonState(_owner._n), _owner._k, memo);
            }
            finally
            {
                _owner._useCompact = previousUseCompact;
            }
        }

        public int ComputeSearchTreeEdges(
            ComparisonState state,
            int remainingSlots,
            Dictionary<SearchStateKey, int> memo)
        {
            ulong fixedTopMask = 0;
            _owner.NormalizeState(state, ref fixedTopMask, ref remainingSlots);

            if (remainingSlots == 0)
                return 0;
            if (_owner.TryGetDeterminedTopSet(state, remainingSlots, out _))
                return 0;
            if (state.ActiveCount <= remainingSlots)
                return 0;
            if (state.ActiveCount <= _owner._m)
                return 0;

            SearchStateKey key = _owner.GetSearchStateKey(state, remainingSlots);
            if (memo.TryGetValue(key, out int cached))
                return cached;

            SelectedComparisonGroup chosen = _owner.ChooseGroup(
                state,
                fixedTopMask: 0,
                remainingSlots,
                context: default);

            int childCount = 0;
            int childCostSum = 0;
            OutcomeTraversalSummary traversal = _owner.VisitComparisonOutcomes(
                state,
                fixedTopMask: 0,
                remainingSlots,
                chosen.Group,
                currentKey: key,
                collectMergedBranches: false,
                onUsefulOutcome: outcome =>
                {
                    childCount++;
                    childCostSum += ComputeSearchTreeEdges(
                        outcome.NextState,
                        outcome.NextRemainingSlots,
                        memo);
                    return true;
                });

            int value = traversal.IsUseful ? childCount + childCostSum : 0;
            memo[key] = value;
            return value;
        }

        private void CacheCompactPatternForBudget(SearchStateKey key, ComparisonState state, IReadOnlyList<int> group, int budget)
        {
            if (_owner._compactGroupPatternTightestBudget.TryGetValue(key, out int existingBudget) && budget >= existingBudget)
                return;

            _owner._compactGroupPatternCache[key] = MakeGroupPattern(state, group);
            _owner._compactGroupPatternTightestBudget[key] = budget;
        }
    }
}
