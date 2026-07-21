using System.Collections.Generic;

static class BranchSelectionScoringService
{
    internal static (int GuaranteedTopHits, int FreshItems, int UnrelatedScore, int UnresolvedPairs, int GroupSize)
        BuildScoreComponents(ComparisonState state, int remainingSlots, IReadOnlyList<int> group)
    {
        int guaranteedTopHits = 0;
        for (int i = 0; i < group.Count; i++)
        {
            if (state.ActiveCount - 1 - state.GetDescendantCount(group[i]) <= remainingSlots - 1)
                guaranteedTopHits++;
        }

        return (
            guaranteedTopHits,
            GroupEnumerationService.CountFreshItems(state, group),
            GroupEnumerationService.CalculateUnrelatedScore(state, group),
            GroupEnumerationService.CountUnresolvedPairs(state, group),
            group.Count);
    }
}