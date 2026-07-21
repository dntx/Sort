using System;
using System.Collections.Generic;
using System.Linq;

partial class StrategyBuilder
{
    private const int FormatItemSetMinRangeLength = 4;

    internal sealed class SelectedComparisonGroup
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

    private static string FormatItemSet(IEnumerable<int> items)
    {
        List<int> sorted = items.ToList();
        sorted.Sort();

        var segments = new List<string>();
        int i = 0;
        while (i < sorted.Count)
        {
            int runStart = i;
            while (i + 1 < sorted.Count && sorted[i + 1] == sorted[i] + 1)
                i++;

            int runLength = i - runStart + 1;
            if (runLength >= FormatItemSetMinRangeLength)
            {
                segments.Add($"#{sorted[runStart] + 1} ~ #{sorted[i] + 1}");
            }
            else
            {
                for (int j = runStart; j <= i; j++)
                    segments.Add($"#{sorted[j] + 1}");
            }

            i++;
        }

        return string.Join(", ", segments);
    }
}