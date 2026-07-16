using System;
using System.Collections.Generic;

// Lightweight display-layer facade for the refactor track.
//
// Initial skeleton: keep rendering behavior byte-for-byte by delegating to the
// existing renderers. Later PRs can migrate display-specific logic behind this
// type while parity tests keep output stable.
sealed class DisplayRenderEngine
{
    public StrategyOverview BuildOverview(StrategyPlan plan)
        => StrategyOverviewRenderer.Build(plan);

    public string RenderOverviewText(StrategyPlan plan)
        => StrategyOverviewRenderer.RenderText(plan);

    public string RenderStrategyText(StrategyPlan plan)
        => StrategyTextRenderer.Render(plan);

    // Internal PR4 facade: StrategyBuilder should call display-folding behavior via this
    // render entrypoint rather than reaching helper implementations directly.
    internal static List<DisplayBranchLinePlanner.DisplayBranchLine<T>> PlanBranchLines<T>(
        List<T> families,
        Func<List<T>, EquivalentOrderSummary?> buildSummary,
        Func<List<T>, List<List<T>>> partitionFamiliesIntoOrbits,
        Func<List<List<T>>, List<(List<T> Members, bool ProjectionMerged)>> mergeOrbitsByProjection,
        Func<T, int> getFamilyCount)
        => DisplayBranchLinePlanner.SplitMergedBucketIntoBranchLines(
            families,
            buildSummary,
            partitionFamiliesIntoOrbits,
            mergeOrbitsByProjection,
            getFamilyCount);

    internal static bool IsSingleMergedOrbit(EquivalentOrderSummary? summary)
        => DisplayBranchLinePlanner.MergedOrderingsFormSingleOrbit(summary);

    internal enum ProjectionAutomorphismProbeEvent
    {
        ColorPrefilterSkip,
        AutomorphismCheck,
        ProjectedStateBuild,
        ProjectedStateCacheHit,
    }

    internal static List<(List<T> Members, bool ProjectionMerged)> MergeProjectionOrbits<T>(
        List<List<T>> orbits,
        Func<T, T, bool> areProjectionEquivalent,
        Func<List<T>, bool> canFoldMultiFamilyComponent,
        Func<List<T>, List<T>> orderRepresentativeFirst,
        Func<T, int> getFamilyCount)
        => DisplayProjectionOrbitMerger.MergeOrbitsByProjection(
            orbits,
            areProjectionEquivalent,
            canFoldMultiFamilyComponent,
            orderRepresentativeFirst,
            getFamilyCount);

    internal static List<List<int>> BuildProjectionComponents<T>(
        List<List<T>> orbits,
        Func<T, T, bool> areProjectionEquivalent)
        => DisplayProjectionOrbitMerger.BuildProjectionComponents(orbits, areProjectionEquivalent);

    internal static bool TryProjectionAutomorphism(
        ComparisonState state,
        DisplayProjectionOrbitMerger.ProjectionOutcomeData a,
        DisplayProjectionOrbitMerger.ProjectionOutcomeData b,
        Dictionary<ulong, (ComparisonState State, int[] Colors)>? projectionCache = null,
        Action<ProjectionAutomorphismProbeEvent>? onProbeEvent = null)
    {
        Action<DisplayProjectionOrbitMerger.ProjectionAutomorphismProbeEvent>? bridged = null;
        if (onProbeEvent is not null)
        {
            bridged = evt =>
            {
                onProbeEvent(evt switch
                {
                    DisplayProjectionOrbitMerger.ProjectionAutomorphismProbeEvent.ColorPrefilterSkip => ProjectionAutomorphismProbeEvent.ColorPrefilterSkip,
                    DisplayProjectionOrbitMerger.ProjectionAutomorphismProbeEvent.AutomorphismCheck => ProjectionAutomorphismProbeEvent.AutomorphismCheck,
                    DisplayProjectionOrbitMerger.ProjectionAutomorphismProbeEvent.ProjectedStateBuild => ProjectionAutomorphismProbeEvent.ProjectedStateBuild,
                    DisplayProjectionOrbitMerger.ProjectionAutomorphismProbeEvent.ProjectedStateCacheHit => ProjectionAutomorphismProbeEvent.ProjectedStateCacheHit,
                    _ => throw new ArgumentOutOfRangeException(nameof(evt), evt, null)
                });
            };
        }

        return DisplayProjectionOrbitMerger.TryProjectionAutomorphism(
            state,
            a,
            b,
            projectionCache,
            bridged);
    }

    internal static List<int> RestrictOrderByDropMask(IReadOnlyList<int> order, ulong dropMask)
        => DisplayProjectionOrbitMerger.RestrictOrderByDropMask(order, dropMask);

    internal static ComparisonState CloneDeactivated(ComparisonState state, ulong dropMask)
        => DisplayProjectionOrbitMerger.CloneDeactivated(state, dropMask);
}
