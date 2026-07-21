using System.Collections.Generic;
using Xunit;

public sealed class GroupEnumerationServiceTests
{
    [Fact]
    public void BuildSortedColorSignature_SortsSelectedColorMultiset()
    {
        int[] colors = { 7, 3, 9, 1, 5 };
        var group = new List<int> { 2, 4, 1 };

        int[] signature = GroupEnumerationService.BuildSortedColorSignature(colors, group);

        Assert.Equal(new[] { 3, 5, 9 }, signature);
    }

    [Fact]
    public void GroupMatchesColorSignature_ReturnsTrueOnlyForMatchingMultiset()
    {
        int[] colors = { 7, 3, 9, 1, 5 };
        var group = new List<int> { 2, 4, 1 };

        Assert.True(GroupEnumerationService.GroupMatchesColorSignature(colors, group, new[] { 3, 5, 9 }));
        Assert.False(GroupEnumerationService.GroupMatchesColorSignature(colors, group, new[] { 1, 5, 9 }));
    }

    [Fact]
    public void BuildCheapGroupSignature_UsesSortedStructuralLabels()
    {
        int[] labels = { 10, 4, 8, 1, 6 };
        var group = new List<int> { 2, 4, 1 };

        IntSequenceKey actual = GroupEnumerationService.BuildCheapGroupSignature(labels, group);

        Assert.Equal(new IntSequenceKey(new[] { 4, 6, 8 }), actual);
    }

    [Fact]
    public void CompareGroupsLexicographically_UsesElementOrderThenLength()
    {
        Assert.True(GroupEnumerationService.CompareGroupsLexicographically(new[] { 1, 2, 4 }, new[] { 1, 3 }) < 0);
        Assert.True(GroupEnumerationService.CompareGroupsLexicographically(new[] { 1, 2 }, new[] { 1, 2, 0 }) < 0);
        Assert.Equal(0, GroupEnumerationService.CompareGroupsLexicographically(new[] { 2, 5 }, new[] { 2, 5 }));
    }

    [Fact]
    public void GroupScoringHelpers_HandleFreshAndComparablePairs()
    {
        var state = new ComparisonState(4);
        var group = new List<int> { 0, 1, 2 };

        Assert.Equal(3, GroupEnumerationService.CountFreshItems(state, group));
        Assert.Equal(3, GroupEnumerationService.CountUnresolvedPairs(state, group));
        Assert.Equal(0, GroupEnumerationService.CalculateUnrelatedScore(state, group));

        state.ApplyOrder(new[] { 0, 1, 2 });

        Assert.Equal(0, GroupEnumerationService.CountUnresolvedPairs(state, group));
        Assert.True(GroupEnumerationService.CalculateUnrelatedScore(state, group) < 0);
    }
}
