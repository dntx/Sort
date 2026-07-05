using System;
using System.Collections.Generic;
using System.Linq;

enum StrategyNodeKind
{
    Decision,
    Terminal,
    Reference,
}

sealed class StrategyPlan
{
    public int N { get; }
    public int M { get; }
    public int RequestedK { get; }
    public int K { get; }
    public StrategyNode Root { get; }
    public TimeSpan Elapsed { get; }
    public int MaxStep { get; }
    public int TotalBranchEdges { get; }
    public SearchStatistics SearchStatistics { get; }

    // True for a greedy feasible plan: MaxStep is a feasible upper bound on the optimum
    // (a strategy that achieves it), NOT a proven optimum. Surfacing code must label such a
    // plan as "feasible / not proven optimal" and never collapse the squeeze to "opt = N
    // (proven)" off MaxStep alone -- only the proven lower bound (RootProvenLowerBound) may
    // close the squeeze.
    public bool IsFeasibleUpperBound { get; }

    public StrategyPlan(int n, int m, int requestedK, int k, StrategyNode root, TimeSpan elapsed, SearchStatistics searchStatistics, bool isFeasibleUpperBound = false)
    {
        N = n;
        M = m;
        RequestedK = requestedK;
        K = k;
        Root = root;
        Elapsed = elapsed;
        MaxStep = GetMaxStep(root);
        TotalBranchEdges = GetTotalBranchEdges(root);
        SearchStatistics = searchStatistics;
        IsFeasibleUpperBound = isFeasibleUpperBound;
    }

    // Mainline refinement rule shared by every orchestrator (CLI and GUI): a later phase's plan is
    // kept only when it strictly improves on the current best, comparing lexicographically by
    // worst-case steps first (fewer is better) and then by displayed branch edges (fewer is better).
    // This is the single place that decides whether a refinement (e.g. the compact pass) is shown, so
    // individual builders may return raw candidates that are not guaranteed to beat the incumbent.
    public bool IsStrictRefinementOver(StrategyPlan incumbent)
    {
        if (MaxStep != incumbent.MaxStep)
            return MaxStep < incumbent.MaxStep;
        return TotalBranchEdges < incumbent.TotalBranchEdges;
    }

    // Returns a copy of this plan whose SearchStatistics carries a (higher) proven lower bound on the
    // optimum. Used when a later proof closes the L <= opt <= U squeeze after this plan was already
    // materialized -- e.g. a compact tightening pass proves the next ceiling infeasible, so this
    // (feasible) plan's MaxStep is in fact optimal. Never lowers an existing bound; returns the same
    // instance when there is nothing to raise.
    public StrategyPlan WithRootProvenLowerBound(int rootProvenLowerBound)
    {
        if (rootProvenLowerBound <= SearchStatistics.RootProvenLowerBound)
            return this;
        return new StrategyPlan(N, M, RequestedK, K, Root, Elapsed,
            SearchStatistics.WithRootProvenLowerBound(rootProvenLowerBound), IsFeasibleUpperBound);
    }

    private static int GetMaxStep(StrategyNode root)
    {
        // A Reference node is a leaf standing in for an already-expanded state's subtree, which still
        // needs additional sorts to resolve. Counting it as a 0-depth leaf UNDERCOUNTS the worst case
        // whenever a reference lies on the deepest path and its target was first expanded shallower
        // (e.g. greedy-feasible 6,2,2: the deepest path ends in a "+1 step" reference, true worst case
        // 7, naive count 6 -- below the proven optimum). Resolve references through their target
        // subtree so MaxStep is the true worst-case sort count.
        var referenceTargets = new Dictionary<int, StrategyNode>();
        IndexReferenceTargets(root, referenceTargets);
        return StepsToResolve(root, referenceTargets,
            new Dictionary<StrategyNode, int>(ReferenceEqualityComparer.Instance),
            new HashSet<StrategyNode>(ReferenceEqualityComparer.Instance));
    }

    // Records every genuinely-expanded decision state (Branches > 0) by its StateId. Final-choice
    // leaves are never reference targets (they are returned before a state is registered as expanded),
    // so only branching decisions can be referenced.
    private static void IndexReferenceTargets(StrategyNode node, Dictionary<int, StrategyNode> targets)
    {
        if (node.Kind == StrategyNodeKind.Decision && node.Branches.Count > 0)
            targets[node.StateId] = node;
        foreach (StrategyBranch branch in node.Branches)
            IndexReferenceTargets(branch.Next, targets);
    }

    // Worst-case additional sorts from `node` down to a resolved top set, resolving Reference nodes
    // through their target subtree. Memoized by node identity. Display-key normalization + relabeling
    // can make two references point at each other (A's subtree references B, B's references A) even
    // though the underlying search is acyclic and every actual play is finite; resolving such a display
    // cycle by naive expansion would not terminate. The `visiting` set breaks any such cycle (a
    // re-entered node contributes 0), which is exact for the common acyclic case and guarantees
    // termination otherwise.
    private static int StepsToResolve(
        StrategyNode node, Dictionary<int, StrategyNode> targets, Dictionary<StrategyNode, int> memo,
        HashSet<StrategyNode> visiting)
    {
        if (memo.TryGetValue(node, out int cached))
            return cached;
        if (!visiting.Add(node))
            return 0; // break a reference display-cycle; this path is counted along its other branch

        int result;
        switch (node.Kind)
        {
            case StrategyNodeKind.Terminal:
                result = 0; // already resolved by the parent's sort
                break;
            case StrategyNodeKind.Reference:
                result = targets.TryGetValue(node.StateId, out StrategyNode? target)
                    ? StepsToResolve(target, targets, memo, visiting)
                    : 0;
                break;
            default: // Decision
                if (node.Branches.Count == 0)
                    result = 1; // final-choice: one last sort resolves the top set
                else
                {
                    int maxChild = 0;
                    foreach (StrategyBranch branch in node.Branches)
                        maxChild = Math.Max(maxChild, StepsToResolve(branch.Next, targets, memo, visiting));
                    result = 1 + maxChild;
                }
                break;
        }

        visiting.Remove(node);
        memo[node] = result;
        return result;
    }

    // Total number of displayed branch lines across the whole tree. The materialized tree is
    // a true tree (References are leaf nodes, not back-pointers), so summing Branches.Count at
    // every node yields exactly the number of edges the renderer draws. This is the compact
    // pass's secondary minimization objective (after MaxStep).
    private static int GetTotalBranchEdges(StrategyNode node)
    {
        int total = node.Branches.Count;
        foreach (StrategyBranch branch in node.Branches)
            total += GetTotalBranchEdges(branch.Next);
        return total;
    }
}

// How a proof-tighten progression stage ended. Solution: a strategy was materialized (Plan is set).
// NoSolution: a tightening probe ran to completion over the COMPLETE candidate enumeration and proved
// no strategy exists at that step ceiling (the previous best is therefore optimal). Incomplete: a probe
// finished and found no feasible strategy, but the greedy candidate cap truncated the group enumeration
// on some state, so "no group fit" is NOT a proof that none exists -- it leaves the squeeze open (no
// proven-optimal claim).
enum ProofTightenStageOutcome
{
    Solution,
    NoSolution,
    Incomplete,
}

// One stage of the proof-tighten progression as it is produced by BuildProofTightenPlan: the
// final edge-compaction pass, each successful downward tightening, or a terminal ceiling that yielded
// no solution. Name is the stage label (e.g. "edge-compact@5", "proof-tighten<=4"); Plan is the materialized
// strategy, or null for the NoSolution/Incomplete outcomes. Elapsed is the stage's own wall time,
// not a cumulative total.
readonly struct ProofTightenStage
{
    public ProofTightenStage(string name, StrategyPlan? plan, TimeSpan elapsed,
        ProofTightenStageOutcome outcome = ProofTightenStageOutcome.Solution)
    {
        Name = name;
        Plan = plan;
        Elapsed = elapsed;
        Outcome = outcome;
    }

    public string Name { get; }
    public StrategyPlan? Plan { get; }
    public TimeSpan Elapsed { get; }
    public ProofTightenStageOutcome Outcome { get; }
    public bool HasSolution => Plan is not null;

    // A completed probe whose infeasibility verdict is not a proof because the greedy candidate cap
    // truncated the group enumeration: it leaves the incumbent standing without closing the squeeze to a
    // proven optimum.
    public bool Incomplete => Outcome == ProofTightenStageOutcome.Incomplete;

    // True only for a completed, complete-enumeration probe that proved the ceiling infeasible: the one
    // outcome that certifies the incumbent optimal and closes the squeeze.
    public bool ProvesOptimal => Outcome == ProofTightenStageOutcome.NoSolution;
}

sealed class SearchMilestone
{
    public SearchMilestone(
        int bestWorstCaseSteps,
        string comparisonGroupText,
        long elapsedMilliseconds,
        int searchedStates,
        int pendingStates,
        int peakPendingStates,
        int outputStates,
        int lowerBoundPrunes)
    {
        BestWorstCaseSteps = bestWorstCaseSteps;
        ComparisonGroupText = comparisonGroupText;
        ElapsedMilliseconds = elapsedMilliseconds;
        SearchedStates = searchedStates;
        PendingStates = pendingStates;
        PeakPendingStates = peakPendingStates;
        OutputStates = outputStates;
        LowerBoundPrunes = lowerBoundPrunes;
    }

    public int BestWorstCaseSteps { get; }
    public string ComparisonGroupText { get; }
    public long ElapsedMilliseconds { get; }
    public int SearchedStates { get; }
    public int PendingStates { get; }
    public int PeakPendingStates { get; }
    public int OutputStates { get; }
    public int LowerBoundPrunes { get; }
}

sealed class SearchDiagnostics
{
    public SearchDiagnostics(
        IReadOnlyList<SearchMilestone> rootIncumbents,
        int lowerBoundPrunes,
        int duplicateOutcomeSkips,
        int mergedOutcomeCollisions,
        int exactCacheHits,
        int lowerBoundCacheHits,
        int feasibleTopSetCacheHits,
        int bestGroupPatternCacheHits)
    {
        RootIncumbents = rootIncumbents;
        LowerBoundPrunes = lowerBoundPrunes;
        DuplicateOutcomeSkips = duplicateOutcomeSkips;
        MergedOutcomeCollisions = mergedOutcomeCollisions;
        ExactCacheHits = exactCacheHits;
        LowerBoundCacheHits = lowerBoundCacheHits;
        FeasibleTopSetCacheHits = feasibleTopSetCacheHits;
        BestGroupPatternCacheHits = bestGroupPatternCacheHits;
    }

    public IReadOnlyList<SearchMilestone> RootIncumbents { get; }
    public int LowerBoundPrunes { get; }
    public int DuplicateOutcomeSkips { get; }
    public int MergedOutcomeCollisions { get; }
    public int ExactCacheHits { get; }
    public int LowerBoundCacheHits { get; }
    public int FeasibleTopSetCacheHits { get; }
    public int BestGroupPatternCacheHits { get; }
}

readonly struct SearchProgressSnapshot
{
    public SearchProgressSnapshot(
        long elapsedMilliseconds,
        int searchedStates,
        int pendingStates,
        int peakPendingStates,
        int outputStates,
        SearchMilestone? latestRootIncumbent,
        int rootIncumbentCount,
        int lowerBoundPrunes,
        int duplicateOutcomeSkips,
        int mergedOutcomeCollisions,
        int exactCacheHits,
        int lowerBoundCacheHits,
        int feasibleTopSetCacheHits,
        int bestGroupPatternCacheHits,
        int outcomesConstructed,
        int candidateGroupsEnumerated,
        int lowerBoundStates,
        int feasibleTopSetStates,
        int compactStatesSolved,
        int compactGroupsEnumerated,
        int compactStepOptimalGroups,
        int compactStateEstimate,
        double estimatedProgress01,
        long estimatedRemainingMilliseconds,
        int rootProvenLowerBound)
    {
        ElapsedMilliseconds = elapsedMilliseconds;
        SearchedStates = searchedStates;
        PendingStates = pendingStates;
        PeakPendingStates = peakPendingStates;
        OutputStates = outputStates;
        LatestRootIncumbent = latestRootIncumbent;
        RootIncumbentCount = rootIncumbentCount;
        LowerBoundPrunes = lowerBoundPrunes;
        DuplicateOutcomeSkips = duplicateOutcomeSkips;
        MergedOutcomeCollisions = mergedOutcomeCollisions;
        ExactCacheHits = exactCacheHits;
        LowerBoundCacheHits = lowerBoundCacheHits;
        FeasibleTopSetCacheHits = feasibleTopSetCacheHits;
        BestGroupPatternCacheHits = bestGroupPatternCacheHits;
        OutcomesConstructed = outcomesConstructed;
        CandidateGroupsEnumerated = candidateGroupsEnumerated;
        LowerBoundStates = lowerBoundStates;
        FeasibleTopSetStates = feasibleTopSetStates;
        CompactStatesSolved = compactStatesSolved;
        CompactGroupsEnumerated = compactGroupsEnumerated;
        CompactStepOptimalGroups = compactStepOptimalGroups;
        CompactStateEstimate = compactStateEstimate;
        EstimatedProgress01 = estimatedProgress01;
        EstimatedRemainingMilliseconds = estimatedRemainingMilliseconds;
        RootProvenLowerBound = rootProvenLowerBound;
    }

    public long ElapsedMilliseconds { get; }
    public int SearchedStates { get; }
    public int PendingStates { get; }
    public int PeakPendingStates { get; }
    public int OutputStates { get; }
    public SearchMilestone? LatestRootIncumbent { get; }
    public int RootIncumbentCount { get; }
    public int LowerBoundPrunes { get; }
    public int DuplicateOutcomeSkips { get; }
    public int MergedOutcomeCollisions { get; }
    public int ExactCacheHits { get; }
    public int LowerBoundCacheHits { get; }
    public int FeasibleTopSetCacheHits { get; }
    public int BestGroupPatternCacheHits { get; }
    public int OutcomesConstructed { get; }
    public int CandidateGroupsEnumerated { get; }
    public int LowerBoundStates { get; }
    public int FeasibleTopSetStates { get; }
    public int CompactStatesSolved { get; }
    public int CompactGroupsEnumerated { get; }
    public int CompactStepOptimalGroups { get; }

    // Estimated total number of compact states the edge phase will solve, captured from the step
    // phase's distinct-canonical-state count (-1 when unknown, e.g. exact mode or standalone edge).
    // Lets the GUI show the live "solved / ~estimate" denominator during the edge phase.
    public int CompactStateEstimate { get; }
    public double EstimatedProgress01 { get; }
    public long EstimatedRemainingMilliseconds { get; }

    // Best PROVEN lower bound on the root optimum so far (opt >= this). In the iterative-deepening
    // regime it is the current global budget, which each failed pass lifts; outside that regime it
    // is the analytic root lower bound. Once the search resolves it equals the exact optimum. This
    // is the L side of the L <= opt <= U squeeze; the U side is LatestRootIncumbent.
    public int RootProvenLowerBound { get; }
}

sealed class SearchStatistics
{
    public SearchStatistics(
        int searchedStates,
        int pendingStates,
        int peakPendingStates,
        int outputStates,
        int expandedOutputStates,
        int lowerBoundStates,
        int feasibleTopSetStates,
        SearchDiagnostics diagnostics,
        long phase1Milliseconds,
        long phase1bMilliseconds,
        long phase2Milliseconds,
        int outcomesConstructed,
        int candidateGroupsEnumerated,
        int compactStatesSolved,
        int compactGroupsEnumerated,
        int compactStepOptimalGroups,
        int rootProvenLowerBound)
    {
        SearchedStates = searchedStates;
        PendingStates = pendingStates;
        PeakPendingStates = peakPendingStates;
        OutputStates = outputStates;
        ExpandedOutputStates = expandedOutputStates;
        LowerBoundStates = lowerBoundStates;
        FeasibleTopSetStates = feasibleTopSetStates;
        Diagnostics = diagnostics;
        Phase1Milliseconds = phase1Milliseconds;
        Phase1bMilliseconds = phase1bMilliseconds;
        Phase2Milliseconds = phase2Milliseconds;
        OutcomesConstructed = outcomesConstructed;
        CandidateGroupsEnumerated = candidateGroupsEnumerated;
        CompactStatesSolved = compactStatesSolved;
        CompactGroupsEnumerated = compactGroupsEnumerated;
        CompactStepOptimalGroups = compactStepOptimalGroups;
        RootProvenLowerBound = rootProvenLowerBound;
    }

    public int SearchedStates { get; }
    public int PendingStates { get; }
    public int PeakPendingStates { get; }
    public int OutputStates { get; }
    public int ExpandedOutputStates { get; }
    public int LowerBoundStates { get; }
    public int FeasibleTopSetStates { get; }
    public SearchDiagnostics Diagnostics { get; }

    // Per-phase wall-clock split (ms): phase 1 is the exact worst-case step search shared
    // with the default builder, phase 1b is the optional compact selection pass, and phase 2
    // materializes the strategy tree. Useful for spotting which phase dominates a given run.
    public long Phase1Milliseconds { get; }
    public long Phase1bMilliseconds { get; }
    public long Phase2Milliseconds { get; }

    // Total ComparisonOutcome instances constructed (Clone + ApplyOrder + Eliminate +
    // Normalize). Paired with Diagnostics.DuplicateOutcomeSkips this exposes how many
    // constructed outcomes were discarded as duplicates -- the dominant search cost.
    public int OutcomesConstructed { get; }

    // Raw m-element candidate groups enumerated through the symmetry-dedup pass before
    // canonical-pattern collapse (one canonicalization per count). On highly symmetric
    // early states this far exceeds the distinct groups actually explored -- e.g. the empty
    // root enumerates all C(n, m) combinations that collapse to a single pattern -- so it is
    // the primary signal for symmetry-aware group-generation optimizations.
    public int CandidateGroupsEnumerated { get; }

    // Compact-pass-only counters (zero unless the compact selection is enabled). The shared
    // SearchedStates/OutputStates totals do not otherwise reflect the compact pass's work.
    public int CompactStatesSolved { get; }
    public int CompactGroupsEnumerated { get; }
    public int CompactStepOptimalGroups { get; }

    // Best PROVEN lower bound on the root optimum (opt >= this). The L side of the L <= opt <= U
    // squeeze report; for a fully resolved build it equals MaxStep. See SearchProgressSnapshot.
    public int RootProvenLowerBound { get; }

    // Returns a copy with a (higher) proven lower bound, leaving every other counter untouched. Used to
    // close the squeeze on an already-materialized plan once a later pass proves a tighter bound.
    public SearchStatistics WithRootProvenLowerBound(int rootProvenLowerBound)
        => new SearchStatistics(
            SearchedStates,
            PendingStates,
            PeakPendingStates,
            OutputStates,
            ExpandedOutputStates,
            LowerBoundStates,
            FeasibleTopSetStates,
            Diagnostics,
            Phase1Milliseconds,
            Phase1bMilliseconds,
            Phase2Milliseconds,
            OutcomesConstructed,
            CandidateGroupsEnumerated,
            CompactStatesSolved,
            CompactGroupsEnumerated,
            CompactStepOptimalGroups,
            rootProvenLowerBound);
}

sealed class StrategyNode
{
    public StrategyNodeKind Kind { get; }
    public int StateId { get; }
    public int? Step { get; }
    public IReadOnlyList<int> Group { get; }
    public IReadOnlyList<int> TopSet { get; }
    public IReadOnlyList<StrategyBranch> Branches { get; }
    public FinalChoiceSummary? FinalChoice { get; }
    public IReadOnlyList<ItemRelabel> ReferenceRelabeling { get; }

    private StrategyNode(
        StrategyNodeKind kind,
        int stateId,
        int? step,
        IReadOnlyList<int>? group,
        IReadOnlyList<int>? topSet,
        IReadOnlyList<StrategyBranch>? branches,
        FinalChoiceSummary? finalChoice,
        IReadOnlyList<ItemRelabel>? referenceRelabeling = null)
    {
        Kind = kind;
        StateId = stateId;
        Step = step;
        Group = group ?? Array.Empty<int>();
        TopSet = topSet ?? Array.Empty<int>();
        Branches = branches ?? Array.Empty<StrategyBranch>();
        FinalChoice = finalChoice;
        ReferenceRelabeling = referenceRelabeling ?? Array.Empty<ItemRelabel>();
    }

    public static StrategyNode Decision(
        int stateId,
        int step,
        IReadOnlyList<int> group,
        IReadOnlyList<StrategyBranch> branches,
        FinalChoiceSummary? finalChoice = null)
        => new(StrategyNodeKind.Decision, stateId, step, group, null, branches, finalChoice);

    public static StrategyNode Terminal(int stateId, IReadOnlyList<int> topSet)
        => new(StrategyNodeKind.Terminal, stateId, null, null, topSet, null, null);

    public static StrategyNode Reference(int stateId, IReadOnlyList<ItemRelabel>? relabeling = null)
        => new(StrategyNodeKind.Reference, stateId, null, null, null, null, null, relabeling);
}

readonly struct ItemRelabel
{
    public ItemRelabel(int referencedItem, int currentItem)
    {
        ReferencedItem = referencedItem;
        CurrentItem = currentItem;
    }

    public int ReferencedItem { get; }
    public int CurrentItem { get; }
}

sealed class StrategyBranch
{
    public string OrderText { get; }
    public EquivalentOrderSummary? EquivalentOrders { get; }
    public StrategyEffect Effect { get; }
    public StrategyNode Next { get; }

    public StrategyBranch(string orderText, EquivalentOrderSummary? equivalentOrders, StrategyEffect effect, StrategyNode next)
    {
        OrderText = orderText;
        EquivalentOrders = equivalentOrders;
        Effect = effect;
        Next = next;
    }
}

sealed class EquivalentOrderSummary
{
    public EquivalentOrderSummary(int count, string patternText, string countFormula, string? legend = null)
    {
        Count = count;
        PatternText = patternText;
        CountFormula = countFormula;
        Legend = legend;
    }

    public int Count { get; }
    public string PatternText { get; }
    public string CountFormula { get; }

    // Non-null only for doomed-tail edges, which fold every ordering of an already-eliminated
    // tail into one brace set "{...}" and need a per-edge legend (symmetry-class aliases and the
    // "any order" note). When set, the count formula reads "a sym x b tail" instead of "N!".
    public string? Legend { get; }
}

sealed class StrategyEffect
{
    public IReadOnlyList<int> NewlyGuaranteedTop { get; }
    public IReadOnlyList<int> NewlyExcluded { get; }
    public IReadOnlyList<int> FixedCandidates { get; }
    public IReadOnlyList<int> PossibleCandidates { get; }

    public StrategyEffect(
        IReadOnlyList<int> newlyGuaranteedTop,
        IReadOnlyList<int> newlyExcluded,
        IReadOnlyList<int> fixedCandidates,
        IReadOnlyList<int> possibleCandidates)
    {
        NewlyGuaranteedTop = newlyGuaranteedTop;
        NewlyExcluded = newlyExcluded;
        FixedCandidates = fixedCandidates;
        PossibleCandidates = possibleCandidates;
    }
}

sealed class FinalChoiceSummary
{
    public FinalChoiceSummary(
        IReadOnlyList<int> fixedTopSet,
        IReadOnlyList<int> candidatePool,
        int remainingSlots)
    {
        FixedTopSet = fixedTopSet;
        CandidatePool = candidatePool;
        RemainingSlots = remainingSlots;
    }

    public IReadOnlyList<int> FixedTopSet { get; }
    public IReadOnlyList<int> CandidatePool { get; }
    public int RemainingSlots { get; }
}

readonly struct FeasibleTopSetInfo
{
    public FeasibleTopSetInfo(int count, ulong uniqueMask)
    {
        Count = count;
        UniqueMask = uniqueMask;
    }

    public int Count { get; }
    public ulong UniqueMask { get; }
}

readonly struct FeasibleTopSetSubproblemKey : IEquatable<FeasibleTopSetSubproblemKey>
{
    public FeasibleTopSetSubproblemKey(ulong candidateMask, int remainingSlots)
    {
        CandidateMask = candidateMask;
        RemainingSlots = remainingSlots;
    }

    public ulong CandidateMask { get; }
    public int RemainingSlots { get; }

    public bool Equals(FeasibleTopSetSubproblemKey other)
    {
        return CandidateMask == other.CandidateMask && RemainingSlots == other.RemainingSlots;
    }

    public override bool Equals(object? obj)
    {
        return obj is FeasibleTopSetSubproblemKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(CandidateMask, RemainingSlots);
    }
}

readonly struct BestGroupPattern
{
    public BestGroupPattern(int groupSize, IntSequenceKey pattern, int[]? colorSignature = null)
    {
        GroupSize = groupSize;
        Pattern = pattern;
        ColorSignature = colorSignature;
    }

    public int GroupSize { get; }
    public IntSequenceKey Pattern { get; }

    // Sorted multiset of the chosen group's per-item active colors (ComparisonState.GetActiveItemColors).
    // A cheap necessary condition for Pattern equality: any group materialized for this cache entry must
    // carry this exact sorted color multiset, letting ChooseGroup skip the expensive canonical-key check
    // for groups that cannot match. Null when the writer did not supply it (then no pre-filter is applied).
    public int[]? ColorSignature { get; }
}
