using System;
using System.Collections.Generic;

partial class StrategyBuilder
{
    private const int DefaultCompactGreedyCandidateCap = 128;
    // EXPERIMENTAL (PoC): when enabled, phase 2 reads comparison groups from a
    // "compact" pattern cache produced by a secondary two-level DP. The DP keeps the
    // optimal worst-case step count (computed by phase 1) as the primary objective and,
    // among all equally-optimal groups, minimizes the total number of search-tree branch
    // edges (children.Count at each expanded node, summed recursively). This lets us
    // measure whether a global "prefer the fewest-edges equally-optimal solution" rule
    // shrinks the trees.
    private bool _useCompact;
    private bool _compactUsesFeasibleBudget;
    // When true, the feasible-budget compact solve only proves feasibility at the threaded step
    // ceiling (children.Count-proxy sort is kept -- it is load-bearing for feasibility at tight
    // budgets -- and no exact edge pass is performed in this mode). Drives the greedy-mode
    // Phase A feasibility-only tightening; Phase B clears it to run one real min-edge pass at
    // the determined step.
    private bool _compactFeasibilityOnly;


    // Per-state cap on how many candidate groups the greedy edge phase (mode A) generates and
    // evaluates before committing. At large m a single state can have 1000+ distinct step-optimal
    // groups, and generating + orbit-deduping + FitChildren over all of them is what makes the edge
    // phase appear to hang on shapes like 25,10,10 while the constructive step phase finishes in
    // seconds. The constructive group is always evaluated first (a proven within-budget choice), so any
    // cap keeps the phase correct -- it only trades a little per-state edge-count minimization for a
    // bounded, interruptible runtime. int.MaxValue preserves the original exhaustive behavior.
    internal int CompactGreedyCandidateCap = DefaultCompactGreedyCandidateCap;

    private int GetCompactGreedyCandidateCap(int activeCount, int groupSize)
        => ScaleDefaultCandidateCap(CompactGreedyCandidateCap, DefaultCompactGreedyCandidateCap, activeCount, groupSize);

    internal int GetCompactGreedyCandidateCapForTesting(int activeCount, int groupSize)
        => GetCompactGreedyCandidateCap(activeCount, groupSize);

    // Set true whenever the greedy candidate cap (a finite generationCap) actually truncated a state's
    // group enumeration during the current probe -- i.e. more representatives existed than the cap
    // allowed us to generate. Reset per feasible-compact probe. When a probe concludes infeasible, this
    // flag distinguishes a real proof (complete enumeration, cap never bit) from a merely-incomplete
    // search (cap truncated some state, so an untried group might have fit): the latter must not be
    // reported as proven optimal.
    private bool _compactEnumerationCapped;

    // Snapshot of _compactEnumerationCapped taken at the moment a feasible-compact probe concluded
    // infeasible (root cost == sentinel), so the tightening loop can tell a proven-infeasible ceiling
    // (complete search) from a merely-incomplete one (cap truncated some state's enumeration).
    private bool _lastProbeEnumerationCapped;

    // Greedy edge-phase U tightening. The constructive step phase's worst-case step count U is a feasible
    // upper bound that is typically opt+1, and the edge phase inherits it as its ceiling. After the
    // baseline compact pass at U, this opportunistically re-runs the compact pass at U-1, U-2, ...: a
    // tighter ceiling forces the greedy to pick shallower groups, and whenever the pass still yields a
    // feasible tree we have lowered the reported max-step (usually down to the true optimum, which the
    // measured gap shows is almost always exactly U-1). Each retry is a full compact pass; the loop is
    // bounded below by the proven analytic lower bound L (it can never beat L) and by the run's
    // cancellation token. There is no wall-clock budget: tightening runs to completion, and a user who no
    // longer wants to wait cancels (GUI Stop / CLI Ctrl+C), keeping the best plan found so far. Default on.
    internal bool EnableFeasibleTightening = true;

    // Per-pass root budget override used only by the tightening loop (-1 = use the threaded step U).
    private int _feasibleRootBudgetActive = -1;

    // Root cost returned by the most recent compact phase-1b solve; int.MaxValue means the budget was
    // infeasible, so the tightening loop must stop rather than materialize an unsolved tree.
    private int _compactRootCost = int.MaxValue;

    // Clears the per-budget compact caches so a tightening retry re-solves from scratch at the new
    // (tighter) ceiling. The cross-phase step budget/estimate fields are intentionally left untouched.
    // This resets the compact selection caches, ensuring subsequent phases start with clean state.
    private void ResetCompactState()
    {
        _session.ResetCompactCaches();
        _phase1bSolved = false;
        _compactPatternCacheReadyForMaterialization = false;
        _compactRootCost = int.MaxValue;
    }

    // Greedy min-edge mode (non-feasibility-only) still materializes from a single per-state pattern
    // cache; keep the pattern found under the tightest seen budget so a later looser visit cannot
    // overwrite a tighter-feasible choice.
    private void CacheCompactPatternForBudget(SearchStateKey key, ComparisonState state, IReadOnlyList<int> group, int budget)
    {
        if (_compactGroupPatternTightestBudget.TryGetValue(key, out int existingBudget) && budget >= existingBudget)
            return;

        _compactGroupPatternCache[key] = MakeGroupPattern(state, group);
        _compactGroupPatternTightestBudget[key] = budget;
    }

    // Returns the proxy subtree cost (number of displayed branch edges) under the
    // compact-optimal choice for this state, populating _compactGroupPatternCache. The step
    // ceiling is supplied by the caller: in exact+compact mode the per-state proven optimum
    // (GetMinWorstCaseSteps) is used; in feasible+compact mode the feasible budget threads from the
    // root upper bound U, decremented one per level, so the tree can never exceed U. The cost memo is
    // keyed by (state, budget) because the same state reached with a looser budget may pick a wider
    // group; in exact mode the budget equals the state's opt so the key reduces to state-only.
    private int SolveCompact(ComparisonState state, int remainingSlots, int feasibleBudget = int.MaxValue)
    {
        ThrowIfCancellationRequested();
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);

        // Terminal and final-choice states render no branch lines (terminals carry the
        // resolved top set; final-choice nodes are summarized by FinalChoiceSummary with an
        // empty Branches list), so they contribute zero edges to the subtree.
        if (remainingSlots == 0)
            return 0;
        if (TryGetDeterminedTopSet(state, remainingSlots, out _))
            return 0;
        if (state.ActiveCount <= remainingSlots)
            return 0;
        if (state.ActiveCount <= _m)
            return 0;

        // Step ceiling for this state: the proven optimum (exact mode) or the threaded feasible
        // budget (feasible mode). Feasible mode never calls the exact search.
        int optimalSteps = _compactUsesFeasibleBudget
            ? feasibleBudget
            : GetMinWorstCaseSteps(state, remainingSlots);

        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        var memoKey = (key, optimalSteps);
        if (_compactCostMemo.TryGetValue(memoKey, out int cachedCost))
            return cachedCost;

        // Feasibility-only probes: if this state was already solved under a tighter budget B<=budget,
        // feasibility is monotone, so reuse that solved result directly and skip recomputation.
        if (_compactUsesFeasibleBudget
            && _compactFeasibilityOnly
            && _compactGroupPatternTightestBudget.TryGetValue(key, out int existingBudget)
            && existingBudget <= optimalSteps
            && _compactGroupPatternCache.ContainsKey(key))
        {
            int reusedCost;
            if (_compactCostMemo.TryGetValue((key, existingBudget), out int tighterCost)
                && tighterCost != int.MaxValue)
            {
                reusedCost = tighterCost;
            }
            else if (_compactRealStepsMemo.TryGetValue(key, out int cachedRealSteps)
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
                _compactCostMemo[memoKey] = reusedCost;
                return reusedCost;
            }
        }

        // Sentinel guards against revisiting this state while it is being solved.
        // The search space is acyclic (children are strictly more resolved), so this
        // is only defensive.
        _compactCostMemo[memoKey] = int.MaxValue;
        _compactStatesSolved++;
        ReportProgress();

        // Mode A (feasible+compact) skips the global edge-minimizing search: it takes the first
        // budget-fitting group with the fewest distinct children (a cheap edge proxy) and recurses
        // once, so the whole pass stays linear in reachable states like the feasible phase. Edge count
        // is a fast next-best, not the proven minimum.
        if (_compactUsesFeasibleBudget)
            return _compactFeasibilityOnly
                ? SolveBudgetFeasibility(state, remainingSlots, optimalSteps, key, memoKey)
                : SolveEdgeCompactGreedy(state, remainingSlots, optimalSteps, key, memoKey);

        var candidates = state.GetActiveItemsOrdered();
        int groupSize = Math.Min(_m, candidates.Count);

        // Gathers the distinct step-optimal child states for a group, or returns null if
        // the group is not useful or would not preserve the optimal worst-case step count.
        //
        // A group is step-optimal iff every branch can be resolved within the remaining
        // budget (optimalSteps - 1 further steps); since optimalSteps is the global minimum,
        // any group has 1 + max(branchSteps) >= optimalSteps, so "no branch exceeds the
        // budget" is exactly the step-optimal condition. We therefore bail out of the outcome
        // enumeration as soon as a single branch breaks the budget, mirroring phase 1's
        // lower-bound pruning. This avoids fully expanding the many non-optimal groups.
        List<(ComparisonState State, int RemainingSlots)>? GetStepOptimalChildren(IReadOnlyList<int> group)
        {
            int branchBudget = optimalSteps - 1;
            bool rejected = false;
            var children = new List<(ComparisonState State, int RemainingSlots)>();
            OutcomeTraversalSummary traversal = VisitComparisonOutcomes(
                state,
                fixedTopMask: 0,
                remainingSlots,
                group,
                currentKey: key,
                collectMergedBranches: false,
                onUsefulOutcome: outcome =>
                {
                    // Cheap lower bound first; only fall back to the exact step count when the lower
                    // bound cannot rule the branch out. Feasible mode never calls the exact search --
                    // its branches are validated by the threaded budget alone (the greedy tree already
                    // proves a <= U strategy exists), so only the lower bound applies.
                    bool overBudget = GetMinWorstCaseLowerBound(outcome.NextState, outcome.NextRemainingSlots) > branchBudget;
                    if (!overBudget && !_compactUsesFeasibleBudget)
                        overBudget = GetMinWorstCaseSteps(outcome.NextState, outcome.NextRemainingSlots) > branchBudget;
                    if (overBudget)
                    {
                        rejected = true;
                        return false;
                    }

                    children.Add((outcome.NextState, outcome.NextRemainingSlots));
                    return true;
                });

            if (rejected || !traversal.IsUseful)
                return null;
            return children;
        }

        List<int>? bestGroup = null;
        int bestCost = int.MaxValue;

        // Enumerate in a stable lexicographic order. Among groups with equal edge cost this
        // keeps the first, which empirically yields the most subtree sharing. Branch-and-bound
        // prunes provably-larger groups: the displayed-edge count of a node is always at least
        // the number of distinct step-optimal children it has (the display path can only split
        // a successor state into more lines, never fewer), and every child subtree contributes
        // a non-negative number of edges, so children.Count is a valid lower bound on a group's
        // total cost before the heavier display enumeration runs.
        foreach (var group in EnumerateDistinctGroups(state, candidates, groupSize))
        {
            ThrowIfCancellationRequested();
            _compactGroupsEnumerated++;

            var children = GetStepOptimalChildren(group);
            if (children is null)
                continue;
            _compactStepOptimalGroups++;

            // Cheap lower bound (distinct children) before any child recursion or the heavy
            // display enumeration.
            if (children.Count >= bestCost)
                continue;

            int branchCostSum = 0;
            bool pruned = false;
            int branchBudget = optimalSteps - 1;
            for (int i = 0; i < children.Count; i++)
            {
                int childCost = SolveCompact(children[i].State, children[i].RemainingSlots, branchBudget);
                // Feasible mode: a child that cannot be resolved within the budget returns the
                // unsolvable sentinel; reject the whole group rather than throwing.
                if (childCost == int.MaxValue)
                {
                    pruned = true;
                    break;
                }
                branchCostSum += childCost;

                // The display edge count for this node is at least children.Count, so the group
                // cannot beat the incumbent once children.Count + the accumulated child cost does.
                if (children.Count + branchCostSum >= bestCost)
                {
                    pruned = true;
                    break;
                }
            }

            if (pruned)
                continue;

            // Only now pay for the heavy display enumeration that yields the exact edge count.
            int edgeCount = children.Count;
            int groupCost = edgeCount + branchCostSum;
            if (groupCost < bestCost)
            {
                bestCost = groupCost;
                bestGroup = group;
            }
        }

        if (bestGroup is null)
        {
            // Feasible mode: no group fits the threaded budget here -- report unsolvable so the parent
            // rejects the offending branch. Exact mode is always solvable (opt is achievable).
            if (_compactUsesFeasibleBudget)
                return int.MaxValue;
            throw new InvalidOperationException("Compact selection found no step-optimal comparison group.");
        }

        _compactGroupPatternCache[key] = MakeGroupPattern(state, bestGroup);
        _compactCostMemo[memoKey] = bestCost;

        // Free pickup: record the actual worst-case depth realized by the chosen group. Each child's
        // real depth is already memoized by the recursion above, so the real MaxStep often comes out
        // below the feasible budget without any extra search; the materializer then displays it.
        int realSteps = 0;
        var bestChildren = GetStepOptimalChildren(bestGroup);
        if (bestChildren is not null)
            foreach (var (childState, childRemaining) in bestChildren)
                realSteps = Math.Max(realSteps, GetCompactRealSteps(childState, childRemaining));
        _compactRealStepsMemo[key] = 1 + realSteps;
        return bestCost;
    }

    // Greedy compact selection for mode A: instead of enumerating every step-optimal group and
    // globally minimizing displayed edges, it picks the budget-fitting group with the fewest distinct
    // children (a cheap edge proxy) and recurses once. Linear in reachable states, fast and
    // interruptible; edge count is a next-best, not proven minimal. MaxStep is still bounded by the
    // threaded budget, and free pickup may realize fewer real steps.
    private int SolveEdgeCompactGreedy(
        ComparisonState state, int remainingSlots, int budget, SearchStateKey key, (SearchStateKey, int) memoKey)
    {
        var candidates = state.GetActiveItemsOrdered();
        int groupSize = Math.Min(_m, candidates.Count);
        int branchBudget = budget - 1;

        List<(ComparisonState State, int RemainingSlots)>? FitChildren(IReadOnlyList<int> group)
        {
            bool rejected = false;
            var children = new List<(ComparisonState State, int RemainingSlots)>();
            OutcomeTraversalSummary traversal = VisitComparisonOutcomes(
                state, fixedTopMask: 0, remainingSlots, group, currentKey: key, collectMergedBranches: false,
                onUsefulOutcome: outcome =>
                {
                    if (GetMinWorstCaseLowerBound(outcome.NextState, outcome.NextRemainingSlots) > branchBudget)
                    {
                        rejected = true;
                        return false;
                    }
                    children.Add((outcome.NextState, outcome.NextRemainingSlots));
                    return true;
                });
            return rejected || !traversal.IsUseful ? null : children;
        }

        var fits = new List<(List<int> Group, List<(ComparisonState State, int RemainingSlots)> Children)>();

        // Always evaluate the constructive group first: it is the exact choice the step phase made and
        // is therefore guaranteed solvable within the threaded budget for THIS state, so it always
        // provides a within-budget seed. (The cap must still be generous enough -- 128 by default -- that
        // children reached through the greedy recursion can likewise find a solvable group; starving the
        // candidate set too aggressively makes a descendant return the unsolvable sentinel and cascade
        // failures back up, which is why very low caps are unsound.)
        List<int> constructiveGroup = ChooseConstructiveGroup(state, remainingSlots);
        var seen = new HashSet<IntSequenceKey>();
        var constructiveChildren = FitChildren(constructiveGroup);
        if (constructiveChildren is not null)
        {
            _compactGroupsEnumerated++;
            _compactStepOptimalGroups++;
            seen.Add(new IntSequenceKey(constructiveGroup.ToArray()));
            fits.Add((constructiveGroup, constructiveChildren));
        }

        // Evaluate enumerated groups (generation-capped by CompactGreedyCandidateCap), looking for one
        // with fewer distinct children (a cheap displayed-edge proxy) than the constructive choice. The
        // cap bounds BOTH the representative generation/dedup and the FitChildren cost per state -- the
        // materialized full enumeration over thousands of large-m groups is what makes the edge phase
        // hang. The constructive group above guarantees correctness regardless of the cap.
        int candidateCap = GetCompactGreedyCandidateCap(candidates.Count, groupSize);
        foreach (var group in EnumerateDistinctGroups(state, candidates, groupSize, candidateCap))
        {
            if (!seen.Add(new IntSequenceKey(group.ToArray())))
                continue;
            ThrowIfCancellationRequested();
            _compactGroupsEnumerated++;
            var children = FitChildren(group);
            if (children is null)
                continue;
            _compactStepOptimalGroups++;
            fits.Add((group, children));
        }
        
        // Sort candidates by immediate FitChildren count as cheap proxy for tree quality.
        // Children?.Count should never be null (FitChildren always returns a list or null, never a
        // null list), but the null-safe accessor provides defensive safety. If somehow Children is null,
        // treating it as 0 (worst proxy ranking) ensures the group is explored last, preserving algorithm
        // correctness (all groups are still tried; only ordering changes).
        fits.Sort((a, b) => {
            int aCount = a.Children?.Count ?? 0;
            int bCount = b.Children?.Count ?? 0;
            return aCount.CompareTo(bCount);
        });

        foreach (var (group, children) in fits)
        {
            int branchCostSum = 0;
            bool solvable = true;
            foreach (var (childState, childRemaining) in children)
            {
                int childCost = SolveCompact(childState, childRemaining, branchBudget);
                if (childCost == int.MaxValue)
                {
                    solvable = false;
                    break;
                }
                branchCostSum += childCost;
            }
            if (!solvable)
                continue;

            CacheCompactPatternForBudget(key, state, group, budget);
            int cost = children.Count + branchCostSum;
            _compactCostMemo[memoKey] = cost;

            // Track the realized worst-case depth separately from the edge cost so the plan's MaxStep
            // can drop below the threaded budget when the chosen groups happen to resolve faster.
            int realSteps = 0;
            foreach (var (childState, childRemaining) in children)
                realSteps = Math.Max(realSteps, GetCompactRealSteps(childState, childRemaining));
            _compactRealStepsMemo[key] = 1 + realSteps;
            return cost;
        }

        return int.MaxValue;
    }

    // Feasibility-only variant of SolveEdgeCompactGreedy. Proves a state is solvable within the
    // threaded step ceiling using the first solvable group in children-count-proxy order, and returns
    // as soon as one is found. It mirrors the min-edge greedy's candidate gathering and children.Count
    // sort (that ordering is load-bearing for feasibility at tight budgets, not just an edge
    // optimization), and performs no edge minimization in this phase. It still caches the chosen group
    // pattern (so BuildState can materialize) and the realized step count (so the plan's MaxStep is
    // correct). Returns 1 + maxChildRealSteps as a finite "cost" (callers only compare against
    // int.MaxValue), or int.MaxValue if no group fits the ceiling.
    private int SolveBudgetFeasibility(
        ComparisonState state, int remainingSlots, int budget, SearchStateKey key, (SearchStateKey, int) memoKey)
    {
        var candidates = state.GetActiveItemsOrdered();
        int groupSize = Math.Min(_m, candidates.Count);
        int branchBudget = budget - 1;

        List<(ComparisonState State, int RemainingSlots)>? FitChildren(IReadOnlyList<int> group)
        {
            bool rejected = false;
            var children = new List<(ComparisonState State, int RemainingSlots)>();
            OutcomeTraversalSummary traversal = VisitComparisonOutcomes(
                state, fixedTopMask: 0, remainingSlots, group, currentKey: key, collectMergedBranches: false,
                onUsefulOutcome: outcome =>
                {
                    if (GetMinWorstCaseLowerBound(outcome.NextState, outcome.NextRemainingSlots) > branchBudget)
                    {
                        rejected = true;
                        return false;
                    }
                    children.Add((outcome.NextState, outcome.NextRemainingSlots));
                    return true;
                });
            return rejected || !traversal.IsUseful ? null : children;
        }

        // Mirror SolveEdgeCompactGreedy's candidate gathering and children-count proxy sort.
        var fits = new List<(List<int> Group, List<(ComparisonState State, int RemainingSlots)> Children)>();
        List<int> constructiveGroup = ChooseConstructiveGroup(state, remainingSlots);
        var seen = new HashSet<IntSequenceKey>();
        var constructiveChildren = FitChildren(constructiveGroup);
        if (constructiveChildren is not null)
        {
            _compactGroupsEnumerated++;
            _compactStepOptimalGroups++;
            seen.Add(new IntSequenceKey(constructiveGroup.ToArray()));
            fits.Add((constructiveGroup, constructiveChildren));
        }
        int candidateCap = GetCompactGreedyCandidateCap(candidates.Count, groupSize);
        foreach (var group in EnumerateDistinctGroups(state, candidates, groupSize, candidateCap))
        {
            if (!seen.Add(new IntSequenceKey(group.ToArray())))
                continue;
            ThrowIfCancellationRequested();
            _compactGroupsEnumerated++;
            var children = FitChildren(group);
            if (children is null)
                continue;
            _compactStepOptimalGroups++;
            fits.Add((group, children));
        }
        fits.Sort((a, b) => a.Children.Count.CompareTo(b.Children.Count));

        foreach (var (group, children) in fits)
        {
            bool solvable = true;
            int realSteps = 0;
            foreach (var (childState, childRemaining) in children)
            {
                int childCost = SolveCompact(childState, childRemaining, branchBudget);
                if (childCost == int.MaxValue)
                {
                    solvable = false;
                    break;
                }
                realSteps = Math.Max(realSteps, GetCompactRealSteps(childState, childRemaining));
            }
            if (!solvable)
                continue;

            _compactGroupPatternCache[key] = MakeGroupPattern(state, group);
            _compactGroupPatternTightestBudget[key] = budget;
            int cost = 1 + realSteps;   // steps-as-cost; callers only test against int.MaxValue
            _compactCostMemo[memoKey] = cost;
            _compactRealStepsMemo[key] = cost;
            return cost;
        }

        return int.MaxValue;
    }

    // Actual worst-case steps realized by the compact selection for a state: 0 for the
    // no-comparison terminals (mirrors SolveCompactSelection's base cases), otherwise the memoized
    // chosen-group depth. Lets feasible+compact surface a MaxStep below the feasible budget.
    private int GetCompactRealSteps(ComparisonState state, int remainingSlots)
    {
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);
        if (remainingSlots == 0)
            return 0;
        if (TryGetDeterminedTopSet(state, remainingSlots, out _))
            return 0;
        if (state.ActiveCount <= remainingSlots)
            return 0;
        if (state.ActiveCount <= _m)
            return 0;
        return _compactRealStepsMemo.TryGetValue(GetSearchStateKey(state, remainingSlots), out int steps) ? steps : 0;
    }

    // Search objective over the implicit search tree keyed by SearchStateKey:
    // children.Count + sum(child costs). Distinct from display/materialized edge count.
    internal int GetStepOptimalSearchTreeEdges()
    {
        EnsurePhase1Solved();
        return ComputeSearchTreeEdgesForSelection(useCompactSelection: false);
    }

    internal int GetCompactSearchTreeEdges()
    {
        EnsureCompactSolved();
        return _compactRootCost;
    }

    private int ComputeSearchTreeEdgesForSelection(bool useCompactSelection)
    {
        bool previousUseCompact = _useCompact;
        _useCompact = useCompactSelection;
        try
        {
            var memo = new Dictionary<SearchStateKey, int>();
            return ComputeSearchTreeEdges(new ComparisonState(_n), _k, memo);
        }
        finally
        {
            _useCompact = previousUseCompact;
        }
    }

    private int ComputeSearchTreeEdges(
        ComparisonState state,
        int remainingSlots,
        Dictionary<SearchStateKey, int> memo)
    {
        ulong fixedTopMask = 0;
        NormalizeState(state, ref fixedTopMask, ref remainingSlots);

        if (remainingSlots == 0)
            return 0;
        if (TryGetDeterminedTopSet(state, remainingSlots, out _))
            return 0;
        if (state.ActiveCount <= remainingSlots)
            return 0;
        if (state.ActiveCount <= _m)
            return 0;

        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        if (memo.TryGetValue(key, out int cached))
            return cached;

        SelectedComparisonGroup chosen = ChooseGroup(
            state,
            fixedTopMask: 0,
            remainingSlots,
            context: default);

        int childCount = 0;
        int childCostSum = 0;
        OutcomeTraversalSummary traversal = VisitComparisonOutcomes(
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

    // Number of displayed branch lines this state renders for the given comparison group,

}

