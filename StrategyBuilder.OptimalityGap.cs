using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

// Measurement-only exact oracle (option D). It quantifies how far the production "patched compact"
// plan is from the TRUE minimum number of displayed branch edges over all step-optimal selections.
//
// Background: the compact pass minimizes an ADDITIVE per-state edge proxy that (a) double-counts
// shared subtrees and (b) memoizes on the coarser SearchStateKey while the materializer de-dups on
// the finer display key. Minimizing that proxy is therefore not the same as minimizing the real
// rendered edge count, so BuildCompactPlan only guarantees "no worse than default", NOT global
// optimality. This oracle brute-forces the real objective on small inputs to measure the gap and
// decide whether a deeper algorithmic fix (aligning the DP key to the display key / sharing-aware
// local search) is worth building.
//
// Method: every decision state's group choice is materialized through the per-SearchStateKey
// _compactGroupPatternCache. We enumerate, per reachable decision state, the FULL set of
// step-optimal group patterns (the real decision variables), then for every combination of choices
// we inject the assignment into the cache, materialize the real de-duplicated display tree, and
// count its edges. The minimum over all combinations is the true optimum; both the compact and the
// default selections are members of this search space, so trueMin <= patched-compact edges.
partial class StrategyBuilder
{
    private const int OrbitDiagnosticsMinBranchCount = 2;

    internal readonly struct CompactOptimalityGapResult
    {
        public CompactOptimalityGapResult(
            int decisionStates,
            int freeStates,
            BigInteger combinations,
            bool exhaustiveFeasible,
            int? trueMinEdges,
            int? trueMinMaxStep)
        {
            DecisionStates = decisionStates;
            FreeStates = freeStates;
            Combinations = combinations;
            ExhaustiveFeasible = exhaustiveFeasible;
            TrueMinEdges = trueMinEdges;
            TrueMinMaxStep = trueMinMaxStep;
        }

        // Total reachable states that render a branch line (have a real group choice).
        public int DecisionStates { get; }

        // Decision states with more than one step-optimal group pattern (the real free variables).
        public int FreeStates { get; }

        // Product of per-free-state choice counts = size of the exhaustive search space.
        public BigInteger Combinations { get; }

        // True when Combinations was within the cap, so TrueMinEdges is the exact optimum.
        public bool ExhaustiveFeasible { get; }

        // Exact minimum displayed-edge count over all step-optimal selections, or null when the
        // search space exceeded the materialization cap (no exact answer computed).
        public int? TrueMinEdges { get; }

        // MaxStep of the edge-minimal tree (must equal the optimal worst-case depth), or null when
        // infeasible. Verifies the oracle's winner is a legitimate step-optimal alternative.
        public int? TrueMinMaxStep { get; }
    }

    // Enumerates per-decision-state step-optimal pattern choice sets, then exhaustively searches all
    // assignments (when the product is within materializationCap) to find the true edge minimum.
    internal CompactOptimalityGapResult MeasureCompactOptimalityGap(long materializationCap)
    {
        EnsurePhase1Solved();

        var choiceSets = new Dictionary<SearchStateKey, List<BestGroupPattern>>();
        CollectDecisionChoiceSets(new ComparisonState(_n), _k, choiceSets);

        // Stable ordering so the assignment index vector maps deterministically to states.
        var orderedKeys = new List<SearchStateKey>(choiceSets.Keys);

        int freeStates = 0;
        BigInteger combinations = BigInteger.One;
        foreach (SearchStateKey key in orderedKeys)
        {
            int count = choiceSets[key].Count;
            if (count > 1)
            {
                freeStates++;
                combinations *= count;
            }
        }

        bool feasible = combinations <= materializationCap;
        int trueMin = 0;
        int trueMinMaxStep = 0;
        if (feasible)
            trueMin = ExhaustiveMinEdges(orderedKeys, choiceSets, out trueMinMaxStep, out _);

        return new CompactOptimalityGapResult(
            choiceSets.Count,
            freeStates,
            combinations,
            feasible,
            feasible ? trueMin : (int?)null,
            feasible ? trueMinMaxStep : (int?)null);
    }

    // Materializes the full edge-minimal step-optimal StrategyPlan (the tree the gap oracle proves is
    // optimal), or null when the search space exceeds the cap. Used to render and inspect the optimum.
    internal StrategyPlan? BuildEdgeOptimalPlan(long materializationCap)
    {
        EnsurePhase1Solved();

        var choiceSets = new Dictionary<SearchStateKey, List<BestGroupPattern>>();
        CollectDecisionChoiceSets(new ComparisonState(_n), _k, choiceSets);
        var orderedKeys = new List<SearchStateKey>(choiceSets.Keys);

        BigInteger combinations = BigInteger.One;
        foreach (SearchStateKey key in orderedKeys)
        {
            int count = choiceSets[key].Count;
            if (count > 1)
                combinations *= count;
        }

        if (combinations > materializationCap)
            return null;

        _ = ExhaustiveMinEdges(orderedKeys, choiceSets, out _, out Dictionary<SearchStateKey, BestGroupPattern>? best);

        _useCompact = true;
        _compactGroupPatternCache.Clear();
        _compactGroupPatternTightestBudget.Clear();
        foreach (KeyValuePair<SearchStateKey, BestGroupPattern> kv in best!)
            _compactGroupPatternCache[kv.Key] = kv.Value;

        ResetPerBuildTransientState();
        StrategyNode root = BuildState(new ComparisonState(_n), 0, _k, 1);
        return new StrategyPlan(_n, _m, _requestedK, _k, root, TimeSpan.Zero, CreateSearchStatistics());
    }

    private int ExhaustiveMinEdges(
        List<SearchStateKey> orderedKeys,
        Dictionary<SearchStateKey, List<BestGroupPattern>> choiceSets,
        out int bestMaxStep,
        out Dictionary<SearchStateKey, BestGroupPattern>? bestAssignment)
    {
        // Only free states (>1 choice) vary; forced states contribute their single pattern always.
        var freeKeys = new List<SearchStateKey>();
        foreach (SearchStateKey key in orderedKeys)
        {
            if (choiceSets[key].Count > 1)
                freeKeys.Add(key);
        }

        var indices = new int[freeKeys.Count];
        int best = int.MaxValue;
        bestMaxStep = 0;
        bestAssignment = null;

        while (true)
        {
            ThrowIfCancellationRequested();

            var assignment = new Dictionary<SearchStateKey, BestGroupPattern>(orderedKeys.Count);
            foreach (SearchStateKey key in orderedKeys)
                assignment[key] = choiceSets[key][0];
            for (int i = 0; i < freeKeys.Count; i++)
                assignment[freeKeys[i]] = choiceSets[freeKeys[i]][indices[i]];

            int edges = MaterializeEdgeCountWithAssignment(assignment, out int maxStep);
            if (edges < best)
            {
                best = edges;
                bestMaxStep = maxStep;
                bestAssignment = assignment;
            }

            // Advance the mixed-radix index vector over the free-state choice sets.
            int pos = 0;
            for (; pos < freeKeys.Count; pos++)
            {
                indices[pos]++;
                if (indices[pos] < choiceSets[freeKeys[pos]].Count)
                    break;
                indices[pos] = 0;
            }

            if (pos == freeKeys.Count)
                break;
        }

        return best;
    }

    // Materializes the real display tree under a fixed per-state group-pattern assignment and returns
    // its exact displayed-edge count (References render as zero-edge leaves, so they are free). Also
    // emits the tree's MaxStep so callers can confirm the selection stays at the optimal depth.
    private int MaterializeEdgeCountWithAssignment(Dictionary<SearchStateKey, BestGroupPattern> assignment, out int maxStep)
    {
        _useCompact = true;
        _compactGroupPatternCache.Clear();
        _compactGroupPatternTightestBudget.Clear();
        foreach (KeyValuePair<SearchStateKey, BestGroupPattern> kv in assignment)
            _compactGroupPatternCache[kv.Key] = kv.Value;

        ResetPerBuildTransientState();
        StrategyNode root = BuildState(new ComparisonState(_n), 0, _k, 1);
        maxStep = MaxStepOf(root);
        return CountDisplayedEdges(root);
    }

    private static int MaxStepOf(StrategyNode node)
    {
        int selfStep = node.Step ?? 0;
        if (node.Branches.Count == 0)
            return selfStep;

        int max = selfStep;
        foreach (StrategyBranch branch in node.Branches)
        {
            int childMax = MaxStepOf(branch.Next);
            if (childMax > max)
                max = childMax;
        }

        return max;
    }

    private static int CountDisplayedEdges(StrategyNode node)
    {
        int total = node.Branches.Count;
        foreach (StrategyBranch branch in node.Branches)
            total += CountDisplayedEdges(branch.Next);
        return total;
    }

    // DFS that mirrors the materializer's decision-state guards and the compact pass's step-optimal
    // group filter, recording every distinct step-optimal pattern per reachable decision state and
    // recursing into the union of all step-optimal children (the full reachable decision DAG).
    private void CollectDecisionChoiceSets(
        ComparisonState state,
        int remainingSlots,
        Dictionary<SearchStateKey, List<BestGroupPattern>> choiceSets)
    {
        ThrowIfCancellationRequested();
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);

        // Non-decision states render no branch line, exactly matching SolveCompactSelection's guards.
        if (remainingSlots == 0)
            return;
        if (TryGetDeterminedTopSet(state, remainingSlots, out _))
            return;
        if (state.ActiveCount <= remainingSlots)
            return;
        if (state.ActiveCount <= _m)
            return;

        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        if (choiceSets.ContainsKey(key))
            return;

        int optimalSteps = GetMinWorstCaseSteps(state, remainingSlots);
        var candidates = state.GetActiveItemsOrdered();
        int groupSize = Math.Min(_m, candidates.Count);

        var patterns = new Dictionary<IntSequenceKey, BestGroupPattern>();
        var allChildren = new List<(ComparisonState State, int RemainingSlots)>();

        foreach (var group in EnumerateDistinctGroups(state, candidates, groupSize))
        {
            ThrowIfCancellationRequested();
            List<(ComparisonState State, int RemainingSlots)>? children =
                GetStepOptimalChildrenForGap(state, remainingSlots, key, optimalSteps, group);
            if (children is null)
                continue;

            IntSequenceKey pattern = GetGroupPattern(state, group);
            if (!patterns.ContainsKey(pattern))
                patterns[pattern] = new BestGroupPattern(group.Count, pattern);
            allChildren.AddRange(children);
        }

        if (patterns.Count == 0)
            throw new InvalidOperationException("Gap oracle found no step-optimal group for a decision state.");

        choiceSets[key] = new List<BestGroupPattern>(patterns.Values);

        foreach (var child in allChildren)
            CollectDecisionChoiceSets(child.State, child.RemainingSlots, choiceSets);
    }

    // Mirrors the local GetStepOptimalChildren in SolveCompactSelection: returns the distinct
    // step-optimal child states for a group, or null when the group is not useful / breaks budget.
    private List<(ComparisonState State, int RemainingSlots)>? GetStepOptimalChildrenForGap(
        ComparisonState state,
        int remainingSlots,
        SearchStateKey key,
        int optimalSteps,
        IReadOnlyList<int> group)
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
                if (GetMinWorstCaseLowerBound(outcome.NextState, outcome.NextRemainingSlots) > branchBudget ||
                    GetMinWorstCaseSteps(outcome.NextState, outcome.NextRemainingSlots) > branchBudget)
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

    // Diagnostic (measurement-only): after BuildEdgeOptimalPlan has run on THIS builder instance,
    // finds a decision node whose chosen group splits into >=2 sibling branches that converge to the
    // SAME child state id, then tests whether the two orderings are related by a genuine automorphism
    // of the PARENT state P (so the split is an incomplete-template artifact, not honest case-2
    // separation). Relies on _expandedStates / _stateIds still being populated from the last build.
    internal string DiagnoseSiblingMergeSplit(StrategyNode root)
    {
        var idToSnapshot = new Dictionary<int, ExpandedStateSnapshot>();
        foreach (KeyValuePair<IntSequenceKey, ExpandedStateSnapshot> kv in _expandedStates)
            idToSnapshot[GetStateId(kv.Key)] = kv.Value;

        (StrategyNode Node, StrategyBranch A, StrategyBranch B)? hit = FindSplitToSameChild(root);
        if (hit is null)
            return "No decision node with two sibling branches converging to the same child id was found.";

        StrategyNode node = hit.Value.Node;
        StrategyBranch a = hit.Value.A;
        StrategyBranch b = hit.Value.B;

        if (!idToSnapshot.TryGetValue(node.StateId, out ExpandedStateSnapshot snapshot))
            return $"Found split at S{node.StateId} but no captured parent state snapshot.";

        ComparisonState p = snapshot.State;
        ulong fixedTopMask = snapshot.FixedTopMask;

        int[] orderA = ParseOrderItems(a.OrderText);
        int[] orderB = ParseOrderItems(b.OrderText);
        if (orderA.Length != orderB.Length)
            return "Sibling orderings have different lengths; cannot pair.";

        var forced = new Dictionary<int, int>(orderA.Length);
        for (int i = 0; i < orderA.Length; i++)
            forced[orderA[i]] = orderB[i];

        bool isAuto = TryExtendToAutomorphism(p, fixedTopMask, forced, out string? witness);

        string sigma = string.Join(", ", forced
            .OrderBy(kv => kv.Key)
            .Where(kv => kv.Key != kv.Value)
            .Select(kv => $"#{kv.Key + 1}->#{kv.Value + 1}"));

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"split node           : S{node.StateId} step={node.Step} group=[{string.Join(",", node.Group.Select(g => "#" + (g + 1)))}]");
        sb.AppendLine($"branch A             : '{a.OrderText}'  -> child S{a.Next.StateId}");
        sb.AppendLine($"branch B             : '{b.OrderText}'  -> child S{b.Next.StateId}");
        sb.AppendLine($"same child id        : {a.Next.StateId == b.Next.StateId}");
        sb.AppendLine($"forced map (A->B)    : {sigma}");
        sb.AppendLine($"parent active items  : [{string.Join(",", ComparisonState.MaskToOrderedList(p.ActiveMask).Select(x => "#" + (x + 1)))}]");
        sb.AppendLine($"parent fixed-top     : [{string.Join(",", ComparisonState.MaskToOrderedList(fixedTopMask).Select(x => "#" + (x + 1)))}]");
        sb.AppendLine($"A->B is P-automorphism: {isAuto}");
        if (isAuto)
            sb.AppendLine($"full automorphism    : {witness}");
        sb.AppendLine(isAuto
            ? "VERDICT: genuine symmetry orbit (case 1) -> the two branches SHOULD merge; split is an incomplete-template artifact."
            : "VERDICT: no parent automorphism relates them (case 2) -> the split is honest; merging would be a false symmetry claim.");
        return sb.ToString();
    }

    private static (StrategyNode Node, StrategyBranch A, StrategyBranch B)? FindSplitToSameChild(StrategyNode node)
    {
        if (node.Kind == StrategyNodeKind.Decision && node.FinalChoice is null)
        {
            for (int i = 0; i < node.Branches.Count; i++)
                for (int j = i + 1; j < node.Branches.Count; j++)
                    if (node.Branches[i].Next.StateId == node.Branches[j].Next.StateId)
                        return (node, node.Branches[i], node.Branches[j]);
        }

        foreach (StrategyBranch branch in node.Branches)
        {
            var found = FindSplitToSameChild(branch.Next);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static int[] ParseOrderItems(string orderText)
    {
        var items = new List<int>();
        foreach (string token in orderText.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string digits = new string(token.Where(char.IsDigit).ToArray());
            if (digits.Length > 0)
                items.Add(int.Parse(digits) - 1);
        }
        return items.ToArray();
    }

    // Backtracking search for a bijection on (active | fixedTop) that (a) extends the forced pairing,
    // (b) preserves the transitive ancestor/descendant relation (poset isomorphism), and (c) keeps the
    // fixed-top vs active coloring. Existence proves the forced pairing extends to an automorphism of P.
    private static bool TryExtendToAutomorphism(
        ComparisonState p,
        ulong fixedTopMask,
        Dictionary<int, int> forced,
        out string? witness)
    {
        witness = null;
        ulong mask = p.ActiveMask | fixedTopMask;
        List<int> items = ComparisonState.MaskToOrderedList(mask);

        var assignment = new Dictionary<int, int>();
        ulong used = 0;

        bool Consistent(int from, int to)
        {
            if (((fixedTopMask >> from) & 1UL) != ((fixedTopMask >> to) & 1UL))
                return false;
            foreach (KeyValuePair<int, int> pair in assignment)
            {
                int of = pair.Key, ot = pair.Value;
                bool fa = (p.GetAncestorMask(of) & (1UL << from)) != 0;
                bool ta = (p.GetAncestorMask(ot) & (1UL << to)) != 0;
                if (fa != ta) return false;
                bool fd = (p.GetDescendantMask(of) & (1UL << from)) != 0;
                bool td = (p.GetDescendantMask(ot) & (1UL << to)) != 0;
                if (fd != td) return false;
            }
            return true;
        }

        foreach (KeyValuePair<int, int> kv in forced)
        {
            if ((used & (1UL << kv.Value)) != 0 || !Consistent(kv.Key, kv.Value))
                return false;
            assignment[kv.Key] = kv.Value;
            used |= 1UL << kv.Value;
        }

        List<int> unassigned = items.Where(i => !forced.ContainsKey(i)).ToList();

        bool Search(int idx)
        {
            if (idx == unassigned.Count)
                return true;
            int from = unassigned[idx];
            foreach (int to in items)
            {
                if ((used & (1UL << to)) != 0 || !Consistent(from, to))
                    continue;
                assignment[from] = to;
                used |= 1UL << to;
                if (Search(idx + 1))
                    return true;
                assignment.Remove(from);
                used &= ~(1UL << to);
            }
            return false;
        }

        if (!Search(0))
            return false;

        witness = string.Join(", ", assignment
            .OrderBy(kv => kv.Key)
            .Where(kv => kv.Key != kv.Value)
            .Select(kv => $"#{kv.Key + 1}->#{kv.Value + 1}"));
        return true;
    }

    // Completeness check for the ordered-block-permutation detector. For one (n,m,k), builds the
    // edge-optimal plan and finds EVERY decision node whose chosen group still renders two sibling
    // branches that converge to the same child id (a residual split). For each such pair it tests
    // whether the orderings are related by a genuine parent-state automorphism. A pair that IS
    // automorphism-backed but still rendered split = an honest merge the detector FAILED to make
    // (incompleteness bug). Returns the list of such residual false-splits (empty = complete).
    internal List<string> FindResidualFalseSplits(long materializationCap)
    {
        var residual = new List<string>();
        StrategyPlan? plan = BuildEdgeOptimalPlan(materializationCap);
        if (plan?.Root is null)
            return residual;

        return CheckPlanFalseSplits(plan);
    }

    // Same automorphism-backed false-split check as FindResidualFalseSplits, but over an
    // already-built plan (e.g. the compact plan) using the snapshots populated during that build.
    internal List<string> CheckPlanFalseSplits(StrategyPlan plan)
    {
        var residual = new List<string>();
        if (plan?.Root is null)
            return residual;

        var idToSnapshot = new Dictionary<int, ExpandedStateSnapshot>();
        foreach (KeyValuePair<IntSequenceKey, ExpandedStateSnapshot> kv in _expandedStates)
            idToSnapshot[GetStateId(kv.Key)] = kv.Value;

        foreach ((StrategyNode node, StrategyBranch a, StrategyBranch b) in EnumerateSplitsToSameChild(plan.Root))
        {
            if (!idToSnapshot.TryGetValue(node.StateId, out ExpandedStateSnapshot snapshot))
                continue;

            int[] orderA = ParseOrderItems(a.OrderText);
            int[] orderB = ParseOrderItems(b.OrderText);
            if (orderA.Length != orderB.Length)
                continue;

            var forced = new Dictionary<int, int>(orderA.Length);
            for (int i = 0; i < orderA.Length; i++)
                forced[orderA[i]] = orderB[i];

            if (TryExtendToAutomorphism(snapshot.State, snapshot.FixedTopMask, forced, out _))
                residual.Add($"S{node.StateId}: '{a.OrderText}' ~ '{b.OrderText}' (same child S{a.Next.StateId}) is automorphism-backed but rendered split");
        }

        return residual;
    }

    // Verifies the display/search parity invariant for every materialized decision node: the displayed
    // branches faithfully mirror what the search actually expanded. For a node's chosen group both the
    // display and search enumerators (StrategyBuilder.Transitions.cs) run over the SAME parent state.
    // The check asserts, for each node:
    //   1. the set of distinct successor SEARCH states reachable via the display enumerator equals the
    //      set the search enumerator expands (excluding the no-progress self-loop). This proves the
    //      tree never lands on a state the search did not process (no fabrication) and never drops a
    //      genuinely distinct outcome (no hiding) -- equivalent orderings are folded with a visible
    //      count, never discarded; and
    //   2. the number of rendered branches is at least the number of distinct searched successors, so
    //      the display is a refinement: it may SPLIT a folded family into multiple orbit rows, but it
    //      can never collapse two genuinely distinct searched successors into one row.
    // Relies on _expandedStates / _stateIds still populated from the last build on this builder.
    internal List<string> CheckDisplaySearchParity(StrategyPlan plan)
    {
        var residual = new List<string>();
        if (plan?.Root is null)
            return residual;

        var idToSnapshot = new Dictionary<int, ExpandedStateSnapshot>();
        foreach (KeyValuePair<IntSequenceKey, ExpandedStateSnapshot> kv in _expandedStates)
            idToSnapshot[GetStateId(kv.Key)] = kv.Value;

        var visited = new HashSet<int>();
        foreach (StrategyNode node in EnumerateUniqueDecisionNodes(plan.Root, visited))
        {
            if (!idToSnapshot.TryGetValue(node.StateId, out ExpandedStateSnapshot snapshot))
                continue;

            ComparisonState state = snapshot.State;
            ulong fixedTopMask = snapshot.FixedTopMask;
            int remainingSlots = _k - BitOperations.PopCount(fixedTopMask);
            IReadOnlyList<int> group = node.Group;

            // The self-loop (an ordering that normalizes back to the parent) is skipped by both paths
            // in VisitComparisonOutcomes as non-progressing; exclude it on both sides here too.
            SearchStateKey currentKey = GetSearchStateKey(state, remainingSlots);

            // What the search actually expanded: distinct progressing successor search keys.
            var searchSuccessors = new HashSet<SearchStateKey>();
            foreach (ComparisonOutcome outcome in EnumerateSearchOutcomes(state, remainingSlots, group))
            {
                if (outcome.NextSearchKey.Equals(currentKey))
                    continue;
                searchSuccessors.Add(outcome.NextSearchKey);
            }

            // What the display path reaches: the distinct search states the rendered branches land on.
            var displaySuccessors = new HashSet<SearchStateKey>();
            foreach (ComparisonOutcome outcome in EnumerateDisplayOutcomes(state, remainingSlots, group))
            {
                if (outcome.NextSearchKey.Equals(currentKey))
                    continue;
                displaySuccessors.Add(outcome.NextSearchKey);
            }

            if (!displaySuccessors.SetEquals(searchSuccessors))
                residual.Add(
                    $"S{node.StateId}: display successors ({displaySuccessors.Count}) != search successors " +
                    $"({searchSuccessors.Count}); display-only={displaySuccessors.Except(searchSuccessors).Count()}, " +
                    $"search-only={searchSuccessors.Except(displaySuccessors).Count()}");

            if (node.Branches.Count < searchSuccessors.Count)
                residual.Add(
                    $"S{node.StateId}: {node.Branches.Count} rendered branches < {searchSuccessors.Count} distinct searched successors (a successor was hidden)");
        }

        return residual;
    }

    // Yields each distinct (by canonical StateId) materialized decision node once, pruning re-entry
    // into an already-visited subtree so reference-heavy plans stay linear.
    private static IEnumerable<StrategyNode> EnumerateUniqueDecisionNodes(StrategyNode node, HashSet<int> visited)
    {
        bool isDecision = node.Kind == StrategyNodeKind.Decision && node.FinalChoice is null && node.Branches.Count > 0;
        if (isDecision && !visited.Add(node.StateId))
            yield break;

        if (isDecision)
            yield return node;

        foreach (StrategyBranch branch in node.Branches)
            foreach (StrategyNode inner in EnumerateUniqueDecisionNodes(branch.Next, visited))
                yield return inner;
    }

    private static IEnumerable<(StrategyNode Node, StrategyBranch A, StrategyBranch B)> EnumerateSplitsToSameChild(StrategyNode node)
    {
        if (node.Kind == StrategyNodeKind.Decision && node.FinalChoice is null)
        {
            for (int i = 0; i < node.Branches.Count; i++)
                for (int j = i + 1; j < node.Branches.Count; j++)
                    if (node.Branches[i].Next.StateId == node.Branches[j].Next.StateId)
                        yield return (node, node.Branches[i], node.Branches[j]);
        }

        foreach (StrategyBranch branch in node.Branches)
            foreach (var hit in EnumerateSplitsToSameChild(branch.Next))
                yield return hit;
    }

    // Empirical validation of approach B (lightweight augmented-canonical-key orbit partition).
    // For each decision node, partitions its branches by an "augmented orbit key" = the canonical key
    // of (parent + branch's representative order edges), colored with the parent's ORIGINAL fixed-top
    // mask, computed BEFORE elimination. Approach B claims two branches are the same display orbit iff
    // they share this key. For every pair B would MERGE, we cross-check with the rigorous parent-state
    // automorphism prover (approach A). The report lets us confirm B's partition (a) is non-trivial
    // where expected (e.g. 9,4,2 S3 -> two orbits {1,4},{2,3}) and (b) NEVER merges a pair that A
    // cannot back (no false symmetry). A clean report = B is safe to wire into the merge layer.
    internal string DiagnoseOrbitPartition(StrategyNode root)
    {
        var idToSnapshot = new Dictionary<int, ExpandedStateSnapshot>();
        foreach (KeyValuePair<IntSequenceKey, ExpandedStateSnapshot> kv in _expandedStates)
            idToSnapshot[GetStateId(kv.Key)] = kv.Value;

        var sb = new System.Text.StringBuilder();
        int mergeNodes = 0;
        int falseMerges = 0;
        VisitOrbitPartition(root, idToSnapshot, sb, ref mergeNodes, ref falseMerges);

        sb.Insert(0, $"nodes where B merges >={OrbitDiagnosticsMinBranchCount} branches : {mergeNodes}\n" +
                     $"B-merged pairs A cannot back (BAD) : {falseMerges}\n\n");
        return sb.ToString();
    }

    private void VisitOrbitPartition(
        StrategyNode node,
        Dictionary<int, ExpandedStateSnapshot> idToSnapshot,
        System.Text.StringBuilder sb,
        ref int mergeNodes,
        ref int falseMerges)
    {
        if (node.Kind == StrategyNodeKind.Decision && node.FinalChoice is null && node.Branches.Count >= OrbitDiagnosticsMinBranchCount
            && idToSnapshot.TryGetValue(node.StateId, out ExpandedStateSnapshot snapshot))
        {
            // Group branch indices by augmented orbit key (approach B).
            var keyToBranches = new Dictionary<IntSequenceKey, List<int>>();
            for (int i = 0; i < node.Branches.Count; i++)
            {
                int[] order = ParseOrderItems(node.Branches[i].OrderText);
                ComparisonState aug = snapshot.State.Clone();
                aug.ApplyOrder(order);
                IntSequenceKey key = aug.GetDisplayCanonicalKey(snapshot.FixedTopMask);
                if (!keyToBranches.TryGetValue(key, out List<int>? list))
                    keyToBranches[key] = list = new List<int>();
                list.Add(i);
            }

            var orbits = keyToBranches.Values.Where(g => g.Count >= OrbitDiagnosticsMinBranchCount).ToList();
            if (orbits.Count > 0)
            {
                mergeNodes++;
                sb.AppendLine($"S{node.StateId} step={node.Step} branches={node.Branches.Count}: B-orbits = " +
                    string.Join(" ", keyToBranches.Values.Select(g => "{" + string.Join(",", g.Select(i => node.Branches[i].OrderText)) + "}")));

                foreach (List<int> orbit in orbits)
                {
                    int rep = orbit[0];
                    int[] orderRep = ParseOrderItems(node.Branches[rep].OrderText);
                    for (int t = 1; t < orbit.Count; t++)
                    {
                        int other = orbit[t];
                        int[] orderOther = ParseOrderItems(node.Branches[other].OrderText);
                        bool sameChild = node.Branches[rep].Next.StateId == node.Branches[other].Next.StateId;
                        bool auto = false;
                        if (orderRep.Length == orderOther.Length)
                        {
                            var forced = new Dictionary<int, int>(orderRep.Length);
                            for (int q = 0; q < orderRep.Length; q++)
                                forced[orderRep[q]] = orderOther[q];
                            auto = TryExtendToAutomorphism(snapshot.State, snapshot.FixedTopMask, forced, out _);
                        }

                        if (!auto)
                        {
                            falseMerges++;
                            sb.AppendLine($"    BAD: '{node.Branches[rep].OrderText}' ~ '{node.Branches[other].OrderText}' merged by B but NOT automorphism-backed (sameChild={sameChild})");
                        }
                        else
                        {
                            sb.AppendLine($"    ok : '{node.Branches[rep].OrderText}' ~ '{node.Branches[other].OrderText}' (automorphism-backed, sameChild={sameChild})");
                        }
                    }
                }
            }
        }

        foreach (StrategyBranch branch in node.Branches)
            VisitOrbitPartition(branch.Next, idToSnapshot, sb, ref mergeNodes, ref falseMerges);
    }
}

