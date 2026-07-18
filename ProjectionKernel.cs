using System;
using System.Collections.Generic;
using System.Linq;

static class ProjectionKernel
{
    internal readonly record struct BranchLine<T>(List<T> Members, bool ProjectionMerged);

    internal readonly record struct ProjectionOutcomeData(IReadOnlyList<int> OrderItems, ulong EliminatedMask);

    internal enum ProjectionAutomorphismProbeEvent
    {
        ColorPrefilterSkip,
        AutomorphismCheck,
        ProjectedStateBuild,
        ProjectedStateCacheHit,
    }

    internal static List<BranchLine<T>> PlanBranchLines<T>(
        List<T> families,
        Func<List<T>, EquivalentOrderSummary?> buildSummary,
        Func<List<T>, List<List<T>>> partitionFamiliesIntoOrbits,
        Func<List<List<T>>, List<(List<T> Members, bool ProjectionMerged)>> mergeOrbitsByProjection,
        Func<T, int> getFamilyCount)
    {
        ArgumentNullException.ThrowIfNull(families);
        ArgumentNullException.ThrowIfNull(buildSummary);
        ArgumentNullException.ThrowIfNull(partitionFamiliesIntoOrbits);
        ArgumentNullException.ThrowIfNull(mergeOrbitsByProjection);
        ArgumentNullException.ThrowIfNull(getFamilyCount);

        if (families.Count == 1)
            return new List<BranchLine<T>> { new(families, false) };

        EquivalentOrderSummary? fullBucketSummary = buildSummary(families);
        if (IsSingleMergedOrbit(fullBucketSummary))
            return new List<BranchLine<T>> { new(families, false) };

        var lines = new List<BranchLine<T>>();
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

            bool isCleanTemplate = IsSingleMergedOrbit(combinedSummary);
            bool isRelabelingOrbit = orbit.TrueForAll(member => GetFamilyCountOrThrow(getFamilyCount, member) == 1);
            bool isProjectionQuotient = projectionMerged && orbit.Exists(member => GetFamilyCountOrThrow(getFamilyCount, member) > 1);
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

    internal static bool IsSingleMergedOrbit(EquivalentOrderSummary? summary)
    {
        return summary is null
            || !summary.PatternText.Contains(" | ", StringComparison.Ordinal);
    }

    internal static List<(List<T> Members, bool ProjectionMerged)> MergeProjectionOrbits<T>(
        List<List<T>> orbits,
        Func<T, T, bool> areProjectionEquivalent,
        Func<List<T>, bool> canFoldMultiFamilyComponent,
        Func<List<T>, List<T>> orderRepresentativeFirst,
        Func<T, int> getFamilyCount)
    {
        ArgumentNullException.ThrowIfNull(orbits);
        ArgumentNullException.ThrowIfNull(areProjectionEquivalent);
        ArgumentNullException.ThrowIfNull(canFoldMultiFamilyComponent);
        ArgumentNullException.ThrowIfNull(orderRepresentativeFirst);
        ArgumentNullException.ThrowIfNull(getFamilyCount);

        int n = orbits.Count;
        if (n <= 1)
            return orbits.Select(orbit => (orbit, false)).ToList();

        List<List<int>> components = BuildProjectionComponents(orbits, areProjectionEquivalent);

        var result = new List<(List<T>, bool)>();
        foreach (List<int> component in components)
        {
            if (component.Count == 1)
            {
                result.Add((orbits[component[0]], false));
                continue;
            }

            var flattened = new List<T>();
            foreach (int orbitIndex in component)
                flattened.AddRange(orbits[orbitIndex]);

            bool multiFamily = flattened.Any(outcome => GetFamilyCountOrThrow(getFamilyCount, outcome) > 1);
            bool fold = !multiFamily || canFoldMultiFamilyComponent(orderRepresentativeFirst(flattened));

            if (fold)
            {
                result.Add((flattened, true));
            }
            else
            {
                var componentOrbits = component.Select(orbitIndex => orbits[orbitIndex]).ToList();
                result.AddRange(MergeSingletonOrbitsByProjection(
                    componentOrbits,
                    areProjectionEquivalent,
                    getFamilyCount));
            }
        }

        return result;
    }

    internal static List<List<int>> BuildProjectionComponents<T>(
        List<List<T>> orbits,
        Func<T, T, bool> areProjectionEquivalent)
    {
        int n = orbits.Count;
        if (n == 0)
            return new List<List<int>>();

        var parent = new int[n];
        for (int i = 0; i < n; i++)
            parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (Find(i) == Find(j))
                    continue;

                if (areProjectionEquivalent(orbits[i][0], orbits[j][0]))
                    parent[Find(i)] = Find(j);
            }
        }

        var componentsByRoot = new Dictionary<int, List<int>>();
        var order = new List<int>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!componentsByRoot.TryGetValue(root, out List<int>? members))
            {
                members = new List<int>();
                componentsByRoot[root] = members;
                order.Add(root);
            }

            members.Add(i);
        }

        return order.Select(root => componentsByRoot[root]).ToList();
    }

    internal static bool TryProjectionAutomorphism(
        ComparisonState state,
        ProjectionOutcomeData a,
        ProjectionOutcomeData b,
        Dictionary<ulong, (ComparisonState State, int[] Colors)>? projectionCache = null,
        Action<ProjectionAutomorphismProbeEvent>? onProbeEvent = null)
    {
        ulong commonDrop = a.EliminatedMask & b.EliminatedMask;
        List<int> orderA = RestrictOrderByDropMask(a.OrderItems, commonDrop);
        List<int> orderB = RestrictOrderByDropMask(b.OrderItems, commonDrop);
        if (orderA.Count != orderB.Count)
            return false;

        if (commonDrop == 0)
        {
            int[] activeColors = state.GetActiveItemColors();
            if (!OrdersHaveMatchingActiveColorSequence(activeColors, orderA, orderB))
            {
                onProbeEvent?.Invoke(ProjectionAutomorphismProbeEvent.ColorPrefilterSkip);
                return false;
            }

            onProbeEvent?.Invoke(ProjectionAutomorphismProbeEvent.AutomorphismCheck);
            return state.TryMapOrderByAutomorphism(0, orderA, orderB);
        }

        ComparisonState projected;
        int[] projectedColors;
        if (projectionCache is not null && projectionCache.TryGetValue(commonDrop, out var cached))
        {
            projected = cached.State;
            projectedColors = cached.Colors;
            onProbeEvent?.Invoke(ProjectionAutomorphismProbeEvent.ProjectedStateCacheHit);
        }
        else
        {
            projected = CloneDeactivated(state, commonDrop);
            projectedColors = projected.GetActiveItemColors();
            projectionCache?.Add(commonDrop, (projected, projectedColors));
            onProbeEvent?.Invoke(ProjectionAutomorphismProbeEvent.ProjectedStateBuild);
        }

        if (!OrdersHaveMatchingActiveColorSequence(projectedColors, orderA, orderB))
        {
            onProbeEvent?.Invoke(ProjectionAutomorphismProbeEvent.ColorPrefilterSkip);
            return false;
        }

        onProbeEvent?.Invoke(ProjectionAutomorphismProbeEvent.AutomorphismCheck);
        return projected.TryMapOrderByAutomorphism(0, orderA, orderB);
    }

    private static bool OrdersHaveMatchingActiveColorSequence(
        int[] activeColors,
        IReadOnlyList<int> orderA,
        IReadOnlyList<int> orderB)
    {
        for (int i = 0; i < orderA.Count; i++)
        {
            if (activeColors[orderA[i]] != activeColors[orderB[i]])
                return false;
        }

        return true;
    }

    internal static List<int> RestrictOrderByDropMask(IReadOnlyList<int> order, ulong dropMask)
    {
        var result = new List<int>(order.Count);
        foreach (int item in order)
        {
            if ((dropMask & (1UL << item)) == 0)
                result.Add(item);
        }

        return result;
    }

    internal static ComparisonState CloneDeactivated(ComparisonState state, ulong dropMask)
    {
        ComparisonState clone = state.Clone();
        clone.Deactivate(dropMask);
        return clone;
    }

    private static List<(List<T> Members, bool ProjectionMerged)> MergeSingletonOrbitsByProjection<T>(
        List<List<T>> orbits,
        Func<T, T, bool> areProjectionEquivalent,
        Func<T, int> getFamilyCount)
    {
        int n = orbits.Count;
        if (n <= 1)
            return orbits.Select(orbit => (orbit, false)).ToList();

        var parent = new int[n];
        for (int i = 0; i < n; i++)
            parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        bool IsSingleton(List<T> orbit)
            => orbit.Count == 1 && GetFamilyCountOrThrow(getFamilyCount, orbit[0]) == 1;

        for (int i = 0; i < n; i++)
        {
            if (!IsSingleton(orbits[i]))
                continue;

            for (int j = i + 1; j < n; j++)
            {
                if (!IsSingleton(orbits[j]) || Find(i) == Find(j))
                    continue;

                if (areProjectionEquivalent(orbits[i][0], orbits[j][0]))
                    parent[Find(i)] = Find(j);
            }
        }

        var byRoot = new Dictionary<int, List<T>>();
        var combinedCount = new Dictionary<int, int>();
        var order = new List<int>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!byRoot.TryGetValue(root, out List<T>? merged))
            {
                merged = new List<T>();
                byRoot[root] = merged;
                combinedCount[root] = 0;
                order.Add(root);
            }

            merged.AddRange(orbits[i]);
            combinedCount[root]++;
        }

        return order.Select(root => (byRoot[root], combinedCount[root] > 1)).ToList();
    }

    private static int GetFamilyCountOrThrow<T>(Func<T, int> getFamilyCount, T member)
    {
        try
        {
            return getFamilyCount(member);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "ProjectionKernel requires getFamilyCount to succeed for every branch member.",
                ex);
        }
    }
}