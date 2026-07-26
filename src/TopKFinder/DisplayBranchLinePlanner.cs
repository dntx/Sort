using System;
using System.Collections.Generic;
using System.Linq;

namespace TopKFinder;

// Display-layer planner for deciding how one merged search bucket is exposed as rendered branch
// lines. It owns only the display-shaping policy: keep a whole bucket when the summary is honest,
// otherwise fall back through parent orbits / projection-merged orbits / per-family lines.
// Search-state-specific orbit construction and summary generation are supplied by the caller.
internal static class DisplayBranchLinePlanner
{
    internal readonly record struct PlannerBranchLine<T>(List<T> Members, bool ProjectionMerged);

    internal static List<PlannerBranchLine<T>> SplitMergedBucketIntoBranchLines<T>(
        List<T> families,
        Func<List<T>, bool> formsSingleMergedOrbit,
        Func<List<T>, List<List<T>>> partitionFamiliesIntoOrbits,
        Func<List<List<T>>, List<(List<T> Members, bool ProjectionMerged)>> mergeOrbitsByProjection,
        Func<T, int> getFamilyCount)
    {
        return ProjectionKernel.PlanProjectionBuckets(
                families,
                formsSingleMergedOrbit,
                partitionFamiliesIntoOrbits,
                mergeOrbitsByProjection,
                getFamilyCount)
            .Select(bucket => new PlannerBranchLine<T>(bucket.Members, bucket.ProjectionMerged))
            .ToList();
    }
}
