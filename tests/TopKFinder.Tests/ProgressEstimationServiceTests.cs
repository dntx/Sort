using Xunit;
using TopKFinder;

public sealed class ProgressEstimationServiceTests
{
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
