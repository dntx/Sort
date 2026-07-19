using System;
using System.Collections.Generic;
using Xunit;

public sealed class Phase2StatelessServicesTests
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

    [Fact]
    public void MapToReportedProgress_WhenCombinedOff_ReturnsLocalProgress()
    {
        double mapped = ProgressEstimationService.MapToReportedProgress(
            reportCombinedRunProgress: false,
            scope: ProgressScope.DefaultStandalone,
            localProgress01: 0.37,
            feasibleSpanPercent: 10,
            defaultSpanPercent: 60,
            compactPrimaryBasePercent: 60,
            compactPrimarySpanPercent: 40,
            compactFeasibleBasePercent: 10,
            compactFeasibleSpanPercent: 90);

        Assert.Equal(0.37, mapped, 6);
    }

    [Fact]
    public void MapToReportedProgress_WhenCombinedOn_UsesScopeBanding()
    {
        double feasibleMapped = ProgressEstimationService.MapToReportedProgress(
            reportCombinedRunProgress: true,
            scope: ProgressScope.FeasibleInCombinedRun,
            localProgress01: 0.5,
            feasibleSpanPercent: 10,
            defaultSpanPercent: 60,
            compactPrimaryBasePercent: 60,
            compactPrimarySpanPercent: 40,
            compactFeasibleBasePercent: 10,
            compactFeasibleSpanPercent: 90);

        double compactPrimaryMapped = ProgressEstimationService.MapToReportedProgress(
            reportCombinedRunProgress: true,
            scope: ProgressScope.CompactPrimaryInCombinedRun,
            localProgress01: 0.5,
            feasibleSpanPercent: 10,
            defaultSpanPercent: 60,
            compactPrimaryBasePercent: 60,
            compactPrimarySpanPercent: 40,
            compactFeasibleBasePercent: 10,
            compactFeasibleSpanPercent: 90);

        Assert.Equal(0.05, feasibleMapped, 6);
        Assert.Equal(0.8, compactPrimaryMapped, 6);
    }

    [Fact]
    public void EstimateAsymptoticProgress_MonotoneAndCapped()
    {
        double zero = ProgressEstimationService.EstimateAsymptoticProgress(
            elapsedInPhaseMs: 0,
            minimumRemainingMs: 500,
            initialRemainingMs: 1000,
            elapsedDivisor: 2,
            softCap: 0.99);

        double progressed = ProgressEstimationService.EstimateAsymptoticProgress(
            elapsedInPhaseMs: 1000,
            minimumRemainingMs: 500,
            initialRemainingMs: 1000,
            elapsedDivisor: 2,
            softCap: 0.99);

        Assert.Equal(0.0, zero, 6);
        Assert.InRange(progressed, 0.0, 0.99);
    }

    [Fact]
    public void EstimateSolvedVsScaleProgress_ComputesExpectedFractionWithCap()
    {
        double raw = ProgressEstimationService.EstimateSolvedVsScaleProgress(3, 3, 0.99);
        double capped = ProgressEstimationService.EstimateSolvedVsScaleProgress(1000, 1, 0.4);
        double zero = ProgressEstimationService.EstimateSolvedVsScaleProgress(1, 0, 0.99);

        Assert.Equal(0.5, raw, 6);
        Assert.Equal(0.4, capped, 6);
        Assert.Equal(0.0, zero, 6);
    }

    [Fact]
    public void ProgressScope_EnumValuesRemainStable()
    {
        Assert.Equal(0, (int)ProgressScope.DefaultStandalone);
        Assert.Equal(1, (int)ProgressScope.DefaultInCombinedRun);
        Assert.Equal(2, (int)ProgressScope.CompactPrimaryInCombinedRun);
        Assert.Equal(4, (int)ProgressScope.FeasibleInCombinedRun);
        Assert.Equal(8, (int)ProgressScope.CompactFeasibleInCombinedRun);
    }
}
