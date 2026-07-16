using System;
using System.Collections.Generic;

// Display-layer planner for deciding how one merged search bucket is exposed as rendered branch
// lines. It owns only the display-shaping policy: keep a whole bucket when the summary is honest,
// otherwise fall back through parent orbits / projection-merged orbits / per-family lines.
// Search-state-specific orbit construction and summary generation are supplied by the caller.
internal static class DisplayBranchLinePlanner
{
    internal readonly record struct DisplayBranchLine<T>(List<T> Members, bool ProjectionMerged);

    internal static List<DisplayBranchLine<T>> SplitMergedBucketIntoBranchLines<T>(
        List<T> families,
        Func<List<T>, EquivalentOrderSummary?> buildSummary,
        Func<List<T>, List<List<T>>> partitionFamiliesIntoOrbits,
        Func<List<List<T>>, List<(List<T> Members, bool ProjectionMerged)>> mergeOrbitsByProjection,
        Func<T, int> getFamilyCount)
    {
        if (families.Count == 1)
            return new List<DisplayBranchLine<T>> { new(families, false) };

        EquivalentOrderSummary? fullBucketSummary = buildSummary(families);
        if (MergedOrderingsFormSingleOrbit(fullBucketSummary))
            return new List<DisplayBranchLine<T>> { new(families, false) };

        var lines = new List<DisplayBranchLine<T>>();
        List<List<T>> parentOrbits = partitionFamiliesIntoOrbits(families);
        List<(List<T> Members, bool ProjectionMerged)> orbits = mergeOrbitsByProjection(parentOrbits);

        foreach ((List<T> orbit, bool projectionMerged) in orbits)
        {
            if (orbit.Count == 1)
            {
                lines.Add(new(orbit, false));
                continue;
            }

            EquivalentOrderSummary? combinedSummary = buildSummary(orbit);

            bool isCleanTemplate = MergedOrderingsFormSingleOrbit(combinedSummary);
            bool isRelabelingOrbit = orbit.TrueForAll(member => getFamilyCount(member) == 1);
            bool isProjectionQuotient = projectionMerged && orbit.Exists(member => getFamilyCount(member) > 1);
            if (isCleanTemplate || isRelabelingOrbit || isProjectionQuotient)
                lines.Add(new(orbit, projectionMerged));
            else
            {
                foreach (T outcome in orbit)
                    lines.Add(new(new List<T> { outcome }, false));
            }
        }

        return lines;
    }

    internal static bool MergedOrderingsFormSingleOrbit(EquivalentOrderSummary? combinedSummary)
    {
        return combinedSummary is null
            || !combinedSummary.PatternText.Contains(" | ", StringComparison.Ordinal);
    }
}
