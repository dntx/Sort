using System;
using System.Collections.Generic;

partial class StrategyBuilder
{
    private sealed class SelectedComparisonGroup
    {
        public SelectedComparisonGroup(IReadOnlyList<int> group, IReadOnlyList<MergedBranch> branches)
        {
            Group = group;
            Branches = branches;
        }

        public IReadOnlyList<int> Group { get; }
        public IReadOnlyList<MergedBranch> Branches { get; }
    }

    private readonly struct ExpandedStateSnapshot
    {
        public ExpandedStateSnapshot(ComparisonState state, ulong fixedTopMask)
        {
            State = state;
            FixedTopMask = fixedTopMask;
        }

        public ComparisonState State { get; }
        public ulong FixedTopMask { get; }
    }

    private static bool TryTrackExpandedState(
        IntSequenceKey expansionKey,
        ComparisonState state,
        ulong fixedTopMask,
        Dictionary<IntSequenceKey, ExpandedStateSnapshot> expandedStates,
        HashSet<IntSequenceKey>? expansionPath,
        string cycleMessage)
    {
        expandedStates.Add(expansionKey, new ExpandedStateSnapshot(state.Clone(), fixedTopMask));
        if (expansionPath is null)
            return false;

        if (!expansionPath.Add(expansionKey))
            throw new InvalidOperationException(cycleMessage);

        return true;
    }

    private sealed class OutcomeTraversalSummary
    {
        public OutcomeTraversalSummary(
            IReadOnlyList<MergedBranch> mergedBranches,
            bool isUseful)
        {
            MergedBranches = mergedBranches;
            IsUseful = isUseful;
        }

        public IReadOnlyList<MergedBranch> MergedBranches { get; }
        public bool IsUseful { get; }
    }

    private readonly record struct HeuristicGroupScore(
        int GuaranteedTopHits,
        int FreshItems,
        int UnrelatedScore,
        int UnresolvedPairs,
        int GroupSize) : IComparable<HeuristicGroupScore>
    {
        // Among groups that achieve the optimal worst-case (the solver only ever caches an
        // optimal group), prefer the most independent/symmetric comparison: more fresh items
        // and fewer existing order relations. This keeps the worst-case step count optimal
        // while producing smaller, more symmetric, and easier-to-verify strategy trees.
        public int CompareTo(HeuristicGroupScore other)
        {
            int result = FreshItems.CompareTo(other.FreshItems);
            if (result != 0)
                return result;

            result = UnrelatedScore.CompareTo(other.UnrelatedScore);
            if (result != 0)
                return result;

            result = GuaranteedTopHits.CompareTo(other.GuaranteedTopHits);
            if (result != 0)
                return result;

            result = UnresolvedPairs.CompareTo(other.UnresolvedPairs);
            if (result != 0)
                return result;

            return GroupSize.CompareTo(other.GroupSize);
        }
    }
}