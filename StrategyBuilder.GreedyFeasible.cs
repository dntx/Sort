using System;
using System.Collections.Generic;

partial class StrategyBuilder
{
    // Constructive feasible-strategy upper bound ("tournament keeping the poset").
    //
    // This is the greedy mode's step phase (BuildGreedyFeasibleStage). An earlier enumeration-based greedy
    // committed to a single policy but still PICKED that policy by fully enumerating + scoring every
    // distinct candidate group at each state (EnumeratePrioritizedGroups -> ~C(active, m) work). On
    // large m that selection dominated (25,10,10 step phase ~49 s) even though only the #1 group was
    // used; this constructive selector replaces it and is both faster and no worse on U.
    //
    // This module computes a feasible upper bound from a CONSTRUCTIVE policy whose group is built in
    // O(active^2) directly from the current partial order -- no group enumeration. It is a tournament
    // that reuses accumulated comparison results: at each state it contests the maximal "frontier"
    // (items with the fewest proven-greater elements), so each sort surfaces a new top candidate and
    // pushes the losers toward elimination, while every already-resolved pair is skipped.
    //
    // Correctness (U >= opt): any complete, valid strategy yields a worst-case step count that is an
    // upper bound on the optimum. Validity here reduces to STRICT PROGRESS -- every sort must add at
    // least one new comparison relation on every adversary branch, so no branch can loop back to an
    // (display-)equivalent ancestor and the tree genuinely terminates in resolved top sets. A sort
    // makes progress iff the chosen group contains an unresolved (mutually unrelated) pair; whenever
    // this chooser runs the top-k set is not yet determined, so such a pair always exists among the
    // active items, and ChooseConstructiveGroup guarantees one is included.
    //
    // This builder reuses the existing BuildState materialization: ChooseGroup computes the group
    // directly via ChooseConstructiveGroup when _useConstructiveSelection is set, so no precomputed
    // pattern cache / closure pre-solve is needed (the chooser is cheap and deterministic).
    private bool _useConstructiveSelection;
    // Test-only switch: when true, candidate lookahead scoring always runs to completion (no
    // incumbent-based early prune). Used by regression tests to A/B lock the pruning win via
    // deterministic work counters.
    internal bool DisableConstructiveCandidateEarlyPruneForTesting { get; set; }
    // Test-only switch: when true, disables the active-count gate for display-line tie-break so
    // tests can A/B compare heavy tie-break evaluation work.
    internal bool DisableConstructiveDisplayLineTieBreakActiveGateForTesting { get; set; }
    private Dictionary<SearchStateKey, int>? _constructiveDepthMemo;
    private int _greedyScoreLowerBoundCacheReuseHits;
    private int _constructiveDisplayLineTieBreakEvaluations;

    internal int GreedyScoreLowerBoundCacheReuseHits => _greedyScoreLowerBoundCacheReuseHits;
    internal int ConstructiveDisplayLineTieBreakEvaluations => _constructiveDisplayLineTieBreakEvaluations;

    // Feasible step budget U threaded from the step phase to the edge phase within one combined run.
    // BuildGreedyFeasibleStage sets it to the MATERIALIZED MaxStep of the just-built step tree (the tightest
    // sound budget: the step plan itself witnesses a U-step solution, so the compact pass under this
    // ceiling can never need more than U). The edge phase (RunGreedyPipeline) reads it so it
    // never produces a plan worse than the step phase. -1 until a step plan is built; deliberately NOT
    // cleared by ResetPerBuildTransientState so it survives the step->edge build boundary on the same
    // builder. When the edge phase runs standalone (no prior step build) it falls back to the lean
    // ConstructiveRootUpperBound, which is sound but looser.
    private int _feasibleRootBudget = -1;

    // Total distinct canonical search states the step phase visited, captured at the end of
    // BuildGreedyFeasibleStage and (like _feasibleRootBudget) deliberately NOT cleared by
    // ResetPerBuildTransientState so it survives the step->edge boundary on the same builder. The edge
    // phase has no pending/searched signal, so this serves as the SCALE anchor for a self-correcting
    // asymptote (see EstimateProgress) that turns _compactStatesSolved into a live progress fraction --
    // it is only a rough scale (edge work can be many times larger or smaller than the step state
    // count), not a hard denominator. -1 until a step plan is built; the standalone edge phase (no
    // prior step build) leaves it -1 and keeps the pinned-progress behavior.
    private int _feasibleCompactStateEstimate = -1;

    // Allows a caller that computed a tighter feasible plan on the same builder (for example an
    // optional GreedyTighten pre-step) to seed the proof-tighten loop with that tighter U.
    internal void OverrideGreedyPipelineUpperBound(int feasibleUpperBound)
    {
        if (feasibleUpperBound <= 0)
            return;

        if (_feasibleRootBudget < 0 || feasibleUpperBound < _feasibleRootBudget)
            _feasibleRootBudget = feasibleUpperBound;
    }

    // Builds the greedy-mode feasible strategy (step tree): a valid, displayable strategy whose
    // worst-case step count is a feasible UPPER bound U on the optimum -- never a proof of optimality.
    // Combined with the analytic proven lower bound L (GetMinWorstCaseLowerBound on the root) this
    // gives a squeeze "L <= opt <= U" displayable even for shapes the exact search never resolves.
    //
    // The comparison group at each state is built constructively from the current partial order
    // (ChooseConstructiveGroup, O(m*active^2)), so unlike the old enumeration-based greedy there is no
    // phase-1 closure / pattern cache: the policy is computed on the fly during materialization.
    public StrategyPlan BuildGreedyFeasibleStage()
    {
        ComparisonState.SetThreadCancellationToken(_cancellationToken);
        try
        {
        // The feasible phase is effectively instant. In a combined run it occupies the first band of
        // the unified progress bar (a single indivisible slice).
        _progressScope = _reportCombinedRunProgress
            ? ProgressScope.FeasibleInCombinedRun
            : ProgressScope.DefaultStandalone;

        ResetPerBuildTransientState();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ReportProgress(force: true);

        // No phase-1 closure: the constructive policy is computed on the fly during materialization.
        _phase1Milliseconds = stopwatch.ElapsedMilliseconds;

        // L side of the squeeze: a proven analytic lower bound on the root optimum, computed
        // independently of the (never-finishing) exact search. Surfaced via
        // SearchStatistics.RootProvenLowerBound, identical to the default path.
        RecordRootProvenLowerBound(GetMinWorstCaseLowerBound(new ComparisonState(_n), _k));
        _phase1bMilliseconds = stopwatch.ElapsedMilliseconds - _phase1Milliseconds;

        _useCompact = false;
        _useConstructiveSelection = true;
        _feasiblePhase2StartMs = _progressStopwatch.ElapsedMilliseconds;  // Mark the start of the costly BuildState phase
        var root = BuildState(new ComparisonState(_n), 0, _k, 1);
        _useConstructiveSelection = false;
        _feasiblePhaseSolved = true;  // Mark feasible stage complete so progress jumps to 100%
        _phase2Milliseconds = stopwatch.ElapsedMilliseconds - _phase1Milliseconds - _phase1bMilliseconds;
        stopwatch.Stop();
        ReportProgress(force: true);

        var plan = new StrategyPlan(
            _n, _m, _requestedK, _k, root, stopwatch.Elapsed, CreateSearchStatistics(),
            isFeasibleUpperBound: true);

        // Surface the exact U this materialized tree achieves so the edge (compact) phase in the same
        // combined run uses it as its step ceiling -- the tightest sound budget, guaranteeing the edge
        // plan is never worse than this step plan.
        _feasibleRootBudget = plan.MaxStep;
        _latestGreedyIncumbentPlan = plan;

        // Denominator estimate for the edge phase's live progress (see field doc): the distinct
        // canonical states this step pass touched approximate the compact solve's total work.
        _feasibleCompactStateEstimate = _visitedSearchStates.Count;
        return plan;
        }
        finally
        {
            ComparisonState.SetThreadCancellationToken(default);
        }
    }

    // Lean worst-case step count of the constructive strategy from the root: a sound but looser
    // feasible step budget for the edge phase when the materialized U is unavailable (the edge phase
    // run standalone, with no prior step build on this builder). Without the materializer's display-key
    // Reference de-duplication this counts the full longest path, so it is >= the materialized MaxStep.
    private int ConstructiveRootUpperBound()
    {
        _constructiveDepthMemo = new Dictionary<SearchStateKey, int>();
        return ConstructiveDepth(new ComparisonState(_n), _k);
    }

    private int ConstructiveDepth(ComparisonState state, int remainingSlots)
    {
        ThrowIfCancellationRequested();
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);

        // Terminals mirror BuildState: no comparison needed.
        if (remainingSlots == 0)
            return 0;
        if (TryGetDeterminedTopSet(state, remainingSlots, out _))
            return 0;
        if (state.ActiveCount <= remainingSlots)
            return 0;

        // A single final sort of the <= m active items fully orders them and resolves the top set
        // (BuildState renders this as a FinalChoiceSummary, counted as one step).
        if (state.ActiveCount <= _m)
            return 1;

        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        if (_constructiveDepthMemo!.TryGetValue(key, out int cached))
            return cached;

        // Rollout always uses the base antichain policy (never lookahead), so this is a fixed policy
        // whose depth the lookahead scorer can trust as a stable estimate.
        List<int> group = ChooseConstructiveGroupBase(state, remainingSlots);

        int maxChildSteps = 0;
        VisitComparisonOutcomes(
            state,
            fixedTopMask: 0,
            remainingSlots,
            group,
            currentKey: key,
            collectMergedBranches: false,
            onUsefulOutcome: outcome =>
            {
                int childSteps = ConstructiveDepth(outcome.NextState, outcome.NextRemainingSlots);
                if (childSteps > maxChildSteps)
                    maxChildSteps = childSteps;
                return true;
            });

        int result = 1 + maxChildSteps;
        _constructiveDepthMemo[key] = result;
        return result;
    }

    // Builds the next comparison group constructively from the current partial order. Returns a group
    // of min(m, active) items GUARANTEED to contain an unresolved pair (so the sort makes progress).
    // Delegates to the 1-ply lookahead chooser, which refines the base antichain policy.
    private List<int> ChooseConstructiveGroup(ComparisonState state, int remainingSlots)
    {
        // m=2 is a qualitatively different regime: each step has only two outcomes and can shrink the
        // active antichain width by at most one, so the immediate-outcome scorer has much weaker signal
        // than for true group sorts (m>=3) while still paying the same heavy lower-bound cost. Treat it
        // as a pairwise edge-selection problem and use the base antichain heuristic directly.
        if (_m == PairwiseConstructiveSelectionGroupSize)
            return ChooseConstructiveGroupBase(state, remainingSlots);

        List<int>? group = ChooseConstructiveGroupLookahead(state, remainingSlots);
        return group ?? ChooseConstructiveGroupBase(state, remainingSlots);
    }

    // Base (single-ply greedy) group choice: the antichain proposer with the progress guarantee and
    // canonical sort applied. This is the fixed rollout policy that the lookahead scorer evaluates, so
    // it must not itself look ahead.
    private List<int> ChooseConstructiveGroupBase(ComparisonState state, int remainingSlots)
    {
        ProbeCancellation(0);
        List<int> active = state.GetActiveItemsOrdered();

        List<int> group = ProposeAntichainGroup(state, active, remainingSlots) ?? new List<int>();
        if (group.Count == 0)
        {
            int fallbackCount = Math.Min(_m, active.Count);
            for (int i = 0; i < fallbackCount; i++)
            {
                ProbeCancellation(0);
                group.Add(active[i]);
            }
        }

        // Progress guarantee: if the picked group is a total chain (all pairs already resolved), force
        // in an unresolved active pair so the sort still adds a new relation.
        if (!GroupHasUnresolvedPair(state, group))
            ForceUnresolvedPair(state, active, group);

        group.Sort();
        return group;
    }

    // 1-ply lookahead: enumerate a bounded candidate set (the base antichain pick, one antichain
    // seeded by each free-symmetry-class representative, and feature-shaped templates), score each by a cheap immediate-outcome
    // heuristic, and return the minimizer. Ties break toward the lexicographically smallest group for
    // determinism.
    //
    // Seeds range over ONE representative per free symmetry class rather than over every active item:
    // members of a class relate identically to every other item, so seeding the antichain builder with
    // any of them yields isomorphic groups with identical 1-ply scores. Quotienting by the class thus
    // drops the seed loop from O(active) to O(distinct classes) candidates with no score coverage lost
    // -- a pure reduction in scoring work (the dominant cost), and a feature-derived bound with no
    // hardcoded candidate cap.
    private List<int> ChooseConstructiveGroupLookahead(ComparisonState state, int remainingSlots)
    {
        _constructiveDepthMemo ??= new Dictionary<SearchStateKey, int>();
        List<int> active = state.GetActiveItemsOrdered();
        SearchStateKey key = GetSearchStateKey(state, remainingSlots);

        List<int>? bestGroup = null;
        ConstructiveCandidateScore bestScore = ConstructiveCandidateScore.Worst;
        string? bestSig = null;
        var seenCandidates = new HashSet<string>();
        Dictionary<string, int>? displayLineCountCache =
            _m >= DisplayLineTieBreakMinGroupSize &&
            (DisableConstructiveDisplayLineTieBreakActiveGateForTesting ||
             state.ActiveCount <= DisplayLineTieBreakMaxActiveCount)
                ? new Dictionary<string, int>()
                : null;

        int GetDisplayLineCountCached(string sig, List<int> candidate)
        {
            if (displayLineCountCache is null)
                return int.MaxValue;

            if (displayLineCountCache.TryGetValue(sig, out int cached))
                return cached;

            int count = CountDisplayBranches(state, remainingSlots, candidate);
            _constructiveDisplayLineTieBreakEvaluations++;
            displayLineCountCache[sig] = count;
            return count;
        }

        void Consider(List<int> proposed)
        {
            var group = new List<int>(proposed);
            if (!GroupHasUnresolvedPair(state, group))
                ForceUnresolvedPair(state, active, group);
            group.Sort();

            string sig = string.Join(",", group);
            if (!seenCandidates.Add(sig))
                return;

            ConstructiveCandidateScore score = ScoreCandidateGroup(
                state,
                remainingSlots,
                key,
                group,
                DisableConstructiveCandidateEarlyPruneForTesting || bestGroup is null
                    ? null
                    : bestScore);
            bool choose = false;
            int scoreCmp = score.CompareTo(bestScore);
            if (bestGroup is null || scoreCmp < 0)
            {
                choose = true;
            }
            else if (scoreCmp == 0 && bestGroup is not null)
            {
                // Use exact immediate display-branch lines as an additional tie-break. This only
                // runs on score ties, so clear winners stay on the cheap path.
                if (displayLineCountCache is not null && bestSig is not null)
                {
                    int candidateLines = GetDisplayLineCountCached(sig, group);
                    int bestLines = GetDisplayLineCountCached(bestSig, bestGroup);
                    if (candidateLines < bestLines)
                    {
                        choose = true;
                    }
                    else if (candidateLines == bestLines && LexLess(group, bestGroup))
                    {
                        choose = true;
                    }
                }
                else if (LexLess(group, bestGroup))
                {
                    choose = true;
                }
            }

            if (choose)
            {
                bestScore = score;
                bestGroup = group;
                bestSig = sig;
            }
        }

        List<int> primary = ProposeAntichainGroup(state, active, remainingSlots);
        Consider(primary);

        // Seed one antichain per free-symmetry-class representative (smallest id of each class).
        foreach (List<int> symmetryClass in state.GetFreeSymmetryClasses())
            Consider(ProposeAntichainGroup(state, active, remainingSlots, symmetryClass[0]));

        // Feature template A: frontier-first layering by active ancestor count.
        Consider(ProposeFrontierLayerGroup(state, active));
        // Feature template B: boundary-straddling around the current top-k cut.
        Consider(ProposeBoundaryStraddlingGroup(state, active, remainingSlots));
        // Feature template C: maximize unresolved-pair density inside the chosen group.
        Consider(ProposeUnresolvedDensityGroup(state, active, remainingSlots));

        return bestGroup ?? primary;
    }

    // Density template: choose a group rich in unresolved pairs. First maximize each item's
    // unresolved degree in the active set, then while filling the group maximize unresolved links to
    // already-picked members; boundary distance and relation count only break ties.
    private List<int> ProposeUnresolvedDensityGroup(ComparisonState state, List<int> active, int remainingSlots)
    {
        int size = Math.Min(_m, active.Count);
        int boundary = Math.Max(0, remainingSlots - 1);

        var unresolvedDegree = new Dictionary<int, int>(active.Count);
        foreach (int item in active)
        {
            int degree = 0;
            foreach (int other in active)
            {
                if (other == item)
                    continue;
                if (!state.HasAncestor(item, other) && !state.HasAncestor(other, item))
                    degree++;
            }

            unresolvedDegree[item] = degree;
        }

        var group = new List<int>(size);
        var inGroup = new bool[_n];

        while (group.Count < size)
        {
            int best = -1;
            int bestUnresolvedToGroup = -1;
            int bestDegree = -1;
            int bestBoundaryDistance = int.MaxValue;
            int bestRelationScore = int.MaxValue;

            foreach (int item in active)
            {
                if (inGroup[item])
                    continue;

                int unresolvedToGroup = 0;
                foreach (int picked in group)
                {
                    if (!state.HasAncestor(item, picked) && !state.HasAncestor(picked, item))
                        unresolvedToGroup++;
                }

                int degree = unresolvedDegree[item];
                int anc = state.GetAncestorCount(item);
                int boundaryDistance = Math.Abs(anc - boundary);
                int relationScore = anc + state.GetDescendantCount(item);

                if (best < 0 ||
                    unresolvedToGroup > bestUnresolvedToGroup ||
                    (unresolvedToGroup == bestUnresolvedToGroup && degree > bestDegree) ||
                    (unresolvedToGroup == bestUnresolvedToGroup && degree == bestDegree && boundaryDistance < bestBoundaryDistance) ||
                    (unresolvedToGroup == bestUnresolvedToGroup && degree == bestDegree && boundaryDistance == bestBoundaryDistance && relationScore < bestRelationScore) ||
                    (unresolvedToGroup == bestUnresolvedToGroup && degree == bestDegree && boundaryDistance == bestBoundaryDistance && relationScore == bestRelationScore && item < best))
                {
                    best = item;
                    bestUnresolvedToGroup = unresolvedToGroup;
                    bestDegree = degree;
                    bestBoundaryDistance = boundaryDistance;
                    bestRelationScore = relationScore;
                }
            }

            group.Add(best);
            inGroup[best] = true;
        }

        return group;
    }

    // Feature template: fill the group from frontier layers first (fewest active ancestors),
    // tie-breaking toward larger descendant count to include high-influence items.
    private List<int> ProposeFrontierLayerGroup(ComparisonState state, List<int> active)
    {
        int size = Math.Min(_m, active.Count);
        var ordered = new List<int>(active);
        ordered.Sort((a, b) =>
        {
            int ancA = state.GetAncestorCount(a);
            int ancB = state.GetAncestorCount(b);
            if (ancA != ancB)
                return ancA.CompareTo(ancB);

            int descA = state.GetDescendantCount(a);
            int descB = state.GetDescendantCount(b);
            if (descA != descB)
                return descB.CompareTo(descA);

            return a.CompareTo(b);
        });

        var group = new List<int>(size);
        for (int i = 0; i < size; i++)
            group.Add(ordered[i]);

        return group;
    }

    // Feature template: intentionally straddle the current top-k boundary. Prefer items just below
    // and just above the ancestor-count cut, then fill by nearest distance to the cut.
    private List<int> ProposeBoundaryStraddlingGroup(ComparisonState state, List<int> active, int remainingSlots)
    {
        int size = Math.Min(_m, active.Count);
        int boundary = Math.Max(0, remainingSlots - 1);

        var below = new List<int>();
        var above = new List<int>();
        foreach (int item in active)
        {
            int anc = state.GetAncestorCount(item);
            if (anc <= boundary)
                below.Add(item);
            else
                above.Add(item);
        }

        // Closest-to-boundary first on each side.
        below.Sort((a, b) =>
        {
            int da = boundary - state.GetAncestorCount(a);
            int db = boundary - state.GetAncestorCount(b);
            if (da != db)
                return da.CompareTo(db);
            return a.CompareTo(b);
        });
        above.Sort((a, b) =>
        {
            int da = state.GetAncestorCount(a) - boundary;
            int db = state.GetAncestorCount(b) - boundary;
            if (da != db)
                return da.CompareTo(db);
            return a.CompareTo(b);
        });

        var group = new List<int>(size);
        var chosen = new bool[_n];
        int bi = 0, ai = 0;
        bool takeBelow = true;

        while (group.Count < size && (bi < below.Count || ai < above.Count))
        {
            if (takeBelow && bi < below.Count)
            {
                int item = below[bi++];
                if (!chosen[item])
                {
                    chosen[item] = true;
                    group.Add(item);
                }
            }
            else if (!takeBelow && ai < above.Count)
            {
                int item = above[ai++];
                if (!chosen[item])
                {
                    chosen[item] = true;
                    group.Add(item);
                }
            }
            else if (bi < below.Count)
            {
                int item = below[bi++];
                if (!chosen[item])
                {
                    chosen[item] = true;
                    group.Add(item);
                }
            }
            else if (ai < above.Count)
            {
                int item = above[ai++];
                if (!chosen[item])
                {
                    chosen[item] = true;
                    group.Add(item);
                }
            }

            takeBelow = !takeBelow;
        }

        if (group.Count < size)
        {
            var byDistance = new List<int>(active);
            byDistance.Sort((a, b) =>
            {
                int da = Math.Abs(state.GetAncestorCount(a) - boundary);
                int db = Math.Abs(state.GetAncestorCount(b) - boundary);
                if (da != db)
                    return da.CompareTo(db);
                return a.CompareTo(b);
            });

            foreach (int item in byDistance)
            {
                if (group.Count >= size)
                    break;
                if (!chosen[item])
                {
                    chosen[item] = true;
                    group.Add(item);
                }
            }
        }

        return group;
    }

    // Candidate score used by constructive lookahead: a cheap one-step heuristic over immediate
    // outcomes only.
    private ConstructiveCandidateScore ScoreCandidateGroup(
        ComparisonState state,
        int remainingSlots,
        SearchStateKey key,
        List<int> group,
        ConstructiveCandidateScore? cutoff)
    {
        return ScoreCandidateGroupCheap(state, remainingSlots, key, group, cutoff);
    }

    // Fast heuristic score based on immediate outcomes only.
    // Priority: lower max lower-bound, then lower max active count, then fewer distinct immediate
    // successors (a subtree-width proxy), then lower average active count.
    private ConstructiveCandidateScore ScoreCandidateGroupCheap(
        ComparisonState state,
        int remainingSlots,
        SearchStateKey key,
        List<int> group,
        ConstructiveCandidateScore? cutoff)
    {
        int maxChildLowerBound = 0;
        int maxChildActiveCount = 0;
        int sumChildActiveCount = 0;
        int outcomeCount = 0;
        bool pruned = false;

        VisitComparisonOutcomes(
            state,
            fixedTopMask: 0,
            remainingSlots,
            group,
            currentKey: key,
            collectMergedBranches: false,
            onUsefulOutcome: outcome =>
            {
                int childLowerBound;
                // Outcome construction already computed NextSearchKey. Reusing it for the lower-bound
                // cache avoids recomputing the key path in hot 1-ply candidate scoring loops.
                if (_lowerBoundStepsCache.TryGetValue(outcome.NextSearchKey, out int cachedLowerBound))
                {
                    _lowerBoundCacheHits++;
                    _greedyScoreLowerBoundCacheReuseHits++;
                    childLowerBound = cachedLowerBound;
                }
                else
                {
                    childLowerBound = GetMinWorstCaseLowerBound(outcome.NextState, outcome.NextRemainingSlots);
                }

                if (childLowerBound > maxChildLowerBound)
                    maxChildLowerBound = childLowerBound;

                int childActiveCount = outcome.NextState.ActiveCount;
                if (childActiveCount > maxChildActiveCount)
                    maxChildActiveCount = childActiveCount;

                sumChildActiveCount += childActiveCount;
                outcomeCount++;

                // Safe early stop: these score-prefix components are monotonic as we process outcomes,
                // so once they are already strictly worse than the incumbent score, this candidate
                // cannot recover to beat it.
                if (cutoff.HasValue && IsDefinitelyWorseByScorePrefix(
                    maxChildLowerBound,
                    maxChildActiveCount,
                    outcomeCount,
                    cutoff.Value))
                {
                    pruned = true;
                    return false;
                }

                return true;
            });

        if (pruned)
            return ConstructiveCandidateScore.Worst;

        int averageChildActiveCount = outcomeCount == 0 ? 0 : sumChildActiveCount / outcomeCount;
        return new ConstructiveCandidateScore(
            maxChildLowerBound,
            maxChildActiveCount,
            averageChildActiveCount,
            outcomeCount);
    }

    private static bool IsDefinitelyWorseByScorePrefix(
        int maxChildLowerBound,
        int maxChildActiveCount,
        int distinctSuccessorCountSoFar,
        ConstructiveCandidateScore cutoff)
    {
        if (maxChildLowerBound > cutoff.MaxChildLowerBound)
            return true;

        if (maxChildLowerBound < cutoff.MaxChildLowerBound)
            return false;

        if (maxChildActiveCount > cutoff.MaxChildActiveCount)
            return true;

        if (maxChildActiveCount < cutoff.MaxChildActiveCount)
            return false;

        // DistinctSuccessorCount is monotonic non-decreasing during traversal, so once the running
        // count already exceeds the cutoff, this candidate can no longer catch up on later keys.
        return distinctSuccessorCountSoFar > cutoff.DistinctSuccessorCount;
    }

    private readonly record struct ConstructiveCandidateScore(
        int MaxChildLowerBound,
        int MaxChildActiveCount,
        int AverageChildActiveCount,
        int DistinctSuccessorCount) : IComparable<ConstructiveCandidateScore>
    {
        public static ConstructiveCandidateScore Worst =>
            new(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);

        public int CompareTo(ConstructiveCandidateScore other)
        {
            int cmp = MaxChildLowerBound.CompareTo(other.MaxChildLowerBound);
            if (cmp != 0)
                return cmp;

            cmp = MaxChildActiveCount.CompareTo(other.MaxChildActiveCount);
            if (cmp != 0)
                return cmp;

            cmp = DistinctSuccessorCount.CompareTo(other.DistinctSuccessorCount);
            if (cmp != 0)
                return cmp;

            cmp = AverageChildActiveCount.CompareTo(other.AverageChildActiveCount);
            if (cmp != 0)
                return cmp;

            return 0;
        }
    }

    private static bool LexLess(List<int> a, List<int> b)
    {
        int n = Math.Min(a.Count, b.Count);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i])
                return a[i] < b[i];
        }
        return a.Count < b.Count;
    }

    // Build a near-ANTICHAIN incrementally. The maxima (items with zero active ancestors) form an
    // antichain, so contesting mutually-unrelated items extracts the most information per sort (a sort
    // of an antichain resolves every pair at once; a sort that re-includes an already known relation
    // wastes a slot). Each pick maximizes the count of current group members it is UNRELATED to (new
    // unresolved pairs), tie-broken toward the frontier: fewest active ancestors (closest to the top
    // -> most likely a guaranteed top hit), then fewest total relations (most still to learn), then
    // smallest id for determinism. An optional forcedSeed pins the first member (used by the lookahead
    // chooser to generate one candidate group per possible seed).
    private List<int> ProposeAntichainGroup(ComparisonState state, List<int> active, int remainingSlots, int forcedSeed = -1)
    {
        ProbeCancellation(0);
        int size = Math.Min(_m, active.Count);

        var group = new List<int>(size);
        var inGroup = new bool[_n];

        if (forcedSeed >= 0)
        {
            group.Add(forcedSeed);
            inGroup[forcedSeed] = true;
        }

        while (group.Count < size)
        {
            ProbeCancellation(0);
            int best = -1;
            int bestUnrelated = -1, bestAnc = 0, bestRel = 0;
            foreach (int item in active)
            {
                ProbeCancellation();
                if (inGroup[item])
                    continue;

                int unrelated = 0;
                foreach (int g in group)
                {
                    ProbeCancellation();
                    if (!state.HasAncestor(item, g) && !state.HasAncestor(g, item))
                        unrelated++;
                }

                int anc = state.GetAncestorCount(item);
                int rel = anc + state.GetDescendantCount(item);

                if (best < 0 ||
                    unrelated > bestUnrelated ||
                    (unrelated == bestUnrelated && anc < bestAnc) ||
                    (unrelated == bestUnrelated && anc == bestAnc && rel < bestRel) ||
                    (unrelated == bestUnrelated && anc == bestAnc && rel == bestRel && item < best))
                {
                    best = item;
                    bestUnrelated = unrelated;
                    bestAnc = anc;
                    bestRel = rel;
                }
            }

            group.Add(best);
            inGroup[best] = true;
        }

        return group;
    }

    private static bool GroupHasUnresolvedPair(ComparisonState state, List<int> group)
    {
        for (int i = 0; i < group.Count - 1; i++)
            for (int j = i + 1; j < group.Count; j++)
                if (!state.HasAncestor(group[i], group[j]) && !state.HasAncestor(group[j], group[i]))
                    return true;

        return false;
    }

    // Rewrites `group` (a total chain) so it contains some unresolved active pair (a, b), evicting
    // its most-resolved members. Such a pair always exists when the chooser runs (top-k undetermined).
    private static void ForceUnresolvedPair(ComparisonState state, List<int> activeFrontierOrder, List<int> group)
    {
        int a = -1, b = -1;
        for (int i = 0; i < activeFrontierOrder.Count && a < 0; i++)
        {
            for (int j = i + 1; j < activeFrontierOrder.Count; j++)
            {
                int x = activeFrontierOrder[i];
                int y = activeFrontierOrder[j];
                if (!state.HasAncestor(x, y) && !state.HasAncestor(y, x))
                {
                    a = x;
                    b = y;
                    break;
                }
            }
        }

        if (a < 0)
            return; // No unresolved pair: top-k already determined; nothing to force.

        EnsureMember(state, group, a, keep: b);
        EnsureMember(state, group, b, keep: a);
    }

    private static void EnsureMember(ComparisonState state, List<int> group, int item, int keep)
    {
        if (group.Contains(item))
            return;

        int worstIndex = -1;
        int worstScore = -1;
        for (int i = 0; i < group.Count; i++)
        {
            if (group[i] == keep)
                continue;

            int score = state.GetAncestorCount(group[i]) + state.GetDescendantCount(group[i]);
            if (score > worstScore)
            {
                worstScore = score;
                worstIndex = i;
            }
        }

        if (worstIndex >= 0)
            group[worstIndex] = item;
    }
}

