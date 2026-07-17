using System;
using System.Collections.Generic;
using System.Linq;

// Lightweight display-layer facade for the refactor track.
//
// Initial skeleton: keep rendering behavior byte-for-byte by delegating to the
// existing renderers. Later PRs can migrate display-specific logic behind this
// type while parity tests keep output stable.
sealed class DisplayRenderEngine
{
    internal readonly record struct BranchLine<T>(List<T> Members, bool ProjectionMerged);

    internal readonly record struct ProjectionOutcomeData(IReadOnlyList<int> OrderItems, ulong EliminatedMask);

    public StrategyOverview BuildOverview(StrategyPlan plan)
        => StrategyOverviewRenderer.Build(plan);

    public string RenderOverviewText(StrategyPlan plan)
        => StrategyOverviewRenderer.RenderText(plan);

    public string RenderStrategyText(StrategyPlan plan)
        => StrategyTextRenderer.Render(plan);

    public string FormatSet(IEnumerable<int> items)
        => StrategyTextRenderer.FormatSet(items);

    public string FormatOptionalSet(IEnumerable<int> items)
        => StrategyTextRenderer.FormatOptionalSet(items);

    public string FormatInEntry(IEnumerable<int> items)
        => StrategyTextRenderer.FormatInEntry(items);

    public string FormatOutEntry(IEnumerable<int> items)
        => StrategyTextRenderer.FormatOutEntry(items);

    public string FormatFixedEntry(IEnumerable<int> items)
        => StrategyTextRenderer.FormatFixedEntry(items);

    public string FormatPossibleEntry(IEnumerable<int> items)
        => StrategyTextRenderer.FormatPossibleEntry(items);

    public string FormatEffectDetails(StrategyEffect effect)
        => StrategyTextRenderer.FormatEffectDetails(effect);

    public string FormatEquivalentPatternLine(EquivalentOrderSummary summary)
        => StrategyTextRenderer.FormatEquivalentPatternLine(summary);

    public string FormatEquivalentDetails(EquivalentOrderSummary summary)
        => StrategyTextRenderer.FormatEquivalentDetails(summary);

    public string FormatRemainingSteps(int remaining)
        => StrategyTextRenderer.FormatRemainingSteps(remaining);

    public string FormatRelabeling(IReadOnlyList<ItemRelabel> relabeling)
        => StrategyTextRenderer.FormatRelabeling(relabeling);

    // Internal facade: StrategyBuilder should call display-folding behavior via this
    // render entrypoint rather than reaching helper implementations directly.
    internal static List<BranchLine<T>> PlanBranchLines<T>(
        List<T> families,
        Func<List<T>, EquivalentOrderSummary?> buildSummary,
        Func<List<T>, List<List<T>>> partitionFamiliesIntoOrbits,
        Func<List<List<T>>, List<(List<T> Members, bool ProjectionMerged)>> mergeOrbitsByProjection,
        Func<T, int> getFamilyCount)
    {
        List<DisplayBranchLinePlanner.DisplayBranchLine<T>> planned = DisplayBranchLinePlanner.SplitMergedBucketIntoBranchLines(
            families,
            buildSummary,
            partitionFamiliesIntoOrbits,
            mergeOrbitsByProjection,
            getFamilyCount);
        return planned.Select(line => new BranchLine<T>(line.Members, line.ProjectionMerged)).ToList();
    }

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
        ProjectionOutcomeData a,
        ProjectionOutcomeData b,
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
            new DisplayProjectionOrbitMerger.ProjectionOutcomeData(a.OrderItems, a.EliminatedMask),
            new DisplayProjectionOrbitMerger.ProjectionOutcomeData(b.OrderItems, b.EliminatedMask),
            projectionCache,
            bridged);
    }

    internal static List<int> RestrictOrderByDropMask(IReadOnlyList<int> order, ulong dropMask)
        => DisplayProjectionOrbitMerger.RestrictOrderByDropMask(order, dropMask);

    internal static ComparisonState CloneDeactivated(ComparisonState state, ulong dropMask)
        => DisplayProjectionOrbitMerger.CloneDeactivated(state, dropMask);
}
