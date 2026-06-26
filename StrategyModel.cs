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
    public int K { get; }
    public StrategyNode Root { get; }
    public TimeSpan Elapsed { get; }
    public int MaxStep { get; }
    public SearchStatistics SearchStatistics { get; }

    public StrategyPlan(int n, int m, int k, StrategyNode root, TimeSpan elapsed, SearchStatistics searchStatistics)
    {
        N = n;
        M = m;
        K = k;
        Root = root;
        Elapsed = elapsed;
        MaxStep = GetMaxStep(root);
        SearchStatistics = searchStatistics;
    }

    private static int GetMaxStep(StrategyNode node)
    {
        int selfStep = node.Step ?? 0;
        if (node.Branches.Count == 0)
            return selfStep;

        return Math.Max(selfStep, node.Branches.Max(branch => GetMaxStep(branch.Next)));
    }
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
        int compactStepOptimalGroups)
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
        int compactStepOptimalGroups)
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
    public BestGroupPattern(int groupSize, IntSequenceKey pattern)
    {
        GroupSize = groupSize;
        Pattern = pattern;
    }

    public int GroupSize { get; }
    public IntSequenceKey Pattern { get; }
}
