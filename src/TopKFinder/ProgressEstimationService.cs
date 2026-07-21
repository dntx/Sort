using System;

namespace TopKFinder;

static class ProgressEstimationService
{
    internal static double MapToReportedProgress(
        bool reportCombinedRunProgress,
        ProgressScope scope,
        double localProgress01,
        int feasibleSpanPercent,
        int defaultSpanPercent,
        int compactPrimaryBasePercent,
        int compactPrimarySpanPercent,
        int compactFeasibleBasePercent,
        int compactFeasibleSpanPercent)
    {
        if (!reportCombinedRunProgress)
            return localProgress01;

        (double progressBase, double progressSpan) = scope switch
        {
            ProgressScope.FeasibleInCombinedRun => (0.0, feasibleSpanPercent / 100.0),
            ProgressScope.DefaultInCombinedRun => (0.0, defaultSpanPercent / 100.0),
            ProgressScope.CompactPrimaryInCombinedRun => (compactPrimaryBasePercent / 100.0, compactPrimarySpanPercent / 100.0),
            ProgressScope.CompactFeasibleInCombinedRun => (compactFeasibleBasePercent / 100.0, compactFeasibleSpanPercent / 100.0),
            _ => (0.0, 1.0),
        };

        double localFraction = Math.Clamp(localProgress01, 0.0, 1.0);
        return Math.Clamp(progressBase + (localFraction * progressSpan), 0.0, 1.0);
    }

    internal static double EstimateAsymptoticProgress(
        long elapsedInPhaseMs,
        long minimumRemainingMs,
        long initialRemainingMs,
        int elapsedDivisor,
        double softCap)
    {
        if (elapsedInPhaseMs <= 0)
            return 0.0;

        long remainingEstimate = Math.Max(
            minimumRemainingMs,
            initialRemainingMs - elapsedInPhaseMs / elapsedDivisor);
        double fraction = elapsedInPhaseMs / (double)(elapsedInPhaseMs + remainingEstimate);
        return Math.Min(fraction, softCap);
    }

    internal static double EstimateSolvedVsScaleProgress(int solvedStates, int scaleStates, double softCap)
    {
        if (scaleStates <= 0)
            return 0.0;

        double fraction = solvedStates / (double)(solvedStates + scaleStates);
        return Math.Min(fraction, softCap);
    }
}