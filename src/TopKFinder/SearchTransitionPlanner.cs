using System;
using System.Collections.Generic;
using System.Linq;

namespace TopKFinder;

partial class StrategyBuilder
{
    private SearchTransitionPlanner? _transitionPlanner;
    private SearchTransitionPlanner TransitionPlanner => _transitionPlanner ??= new SearchTransitionPlanner(CreateTransitionPlannerDependencies());

    private SearchTransitionPlanner.Dependencies CreateTransitionPlannerDependencies()
    {
        return new SearchTransitionPlanner.Dependencies(
            ThrowIfCancellationRequested,
            (state, remainingSlots, group) => TryBuildDoomedTailSpecs(state, remainingSlots, group),
            (state, remainingSlots, group) => TryBuildSearchDoomedTailSpecs(state, remainingSlots, group),
            PlanTransitionBranchLinesForMergedBranch,
            CollectLineFamilies,
            BuildEquivalentOrderSummary,
            MergedOrderingsFormSingleOrbit,
            SelectOrbitRepresentative,
            BuildRelabelingOrbitSummary,
            BuildProjectionQuotientSummary,
            BuildComparisonEffect,
            BuildSearchComparisonEffect);
    }
}

internal sealed class SearchTransitionPlanner
{
    internal sealed record Dependencies(
        Action ThrowIfCancellationRequested,
        Func<ComparisonState, int, StrategyBuilder.SelectedComparisonGroup, List<StrategyBuilder.BranchSpec>?> TryBuildDoomedTailSpecs,
        Func<ComparisonState, int, StrategyBuilder.SelectedComparisonGroup, List<StrategyBuilder.SearchBranchSpec>?> TryBuildSearchDoomedTailSpecs,
        Func<ComparisonState, StrategyBuilder.MergedBranch, IEnumerable<StrategyBuilder.PlannedBranchLine>> PlanBranchLinesForMergedBranch,
        Func<List<StrategyBuilder.MergedFamilyOutcome>, List<StrategyBuilder.OrderFamilyDescriptor>> CollectLineFamilies,
        Func<List<StrategyBuilder.OrderFamilyDescriptor>, EquivalentOrderSummary?> BuildEquivalentOrderSummary,
        Func<EquivalentOrderSummary?, bool> MergedOrderingsFormSingleOrbit,
        Func<List<StrategyBuilder.MergedFamilyOutcome>, StrategyBuilder.MergedFamilyOutcome> SelectOrbitRepresentative,
        Func<ComparisonState, List<StrategyBuilder.MergedFamilyOutcome>, StrategyBuilder.MergedFamilyOutcome, EquivalentOrderSummary> BuildRelabelingOrbitSummary,
        Func<ComparisonState, List<StrategyBuilder.MergedFamilyOutcome>, StrategyBuilder.MergedFamilyOutcome, EquivalentOrderSummary?> BuildProjectionQuotientSummary,
        Func<ComparisonState, ulong, ComparisonState, ulong, StrategyEffect> BuildComparisonEffect,
        Func<ComparisonState, ulong, ComparisonState, ulong, SearchEffect> BuildSearchComparisonEffect);

    private readonly Dependencies _dependencies;

    public SearchTransitionPlanner(Dependencies dependencies)
    {
        _dependencies = dependencies;
    }

    public IReadOnlyList<StrategyBuilder.TransitionSpec> BuildDisplayTransitionSpecs(
        ComparisonState state,
        ulong fixedTopMask,
        int remainingSlots,
        StrategyBuilder.SelectedComparisonGroup chosenGroup)
    {
        return BuildTransitionSpecsFromBranchSpecs(
            state,
            fixedTopMask,
            BuildBranchSpecs(state, remainingSlots, chosenGroup));
    }

    public IReadOnlyList<StrategyBuilder.SearchTransitionSpec> BuildSearchTransitionSpecs(
        ComparisonState state,
        ulong fixedTopMask,
        int remainingSlots,
        StrategyBuilder.SelectedComparisonGroup chosenGroup)
    {
        return BuildTransitionSpecsFromSearchBranchSpecs(
            state,
            fixedTopMask,
            BuildSearchBranchSpecs(state, remainingSlots, chosenGroup));
    }

    public List<StrategyBuilder.BranchSpec> BuildBranchSpecs(
        ComparisonState state,
        int remainingSlots,
        StrategyBuilder.SelectedComparisonGroup chosenGroup)
    {
        return BuildOrderedBranchSpecsWithDoomedTailFallback(
            state,
            chosenGroup,
            tryBuildDoomedTailSpecs: group => _dependencies.TryBuildDoomedTailSpecs(state, remainingSlots, group),
            line => BuildBranchSpecForLine(state, line.Members, line.ProjectionMerged),
            spec => spec.OrderText);
    }

    public List<StrategyBuilder.SearchBranchSpec> BuildSearchBranchSpecs(
        ComparisonState state,
        int remainingSlots,
        StrategyBuilder.SelectedComparisonGroup chosenGroup)
    {
        return BuildOrderedBranchSpecsWithDoomedTailFallback(
            state,
            chosenGroup,
            tryBuildDoomedTailSpecs: group => _dependencies.TryBuildSearchDoomedTailSpecs(state, remainingSlots, group),
            line => BuildSearchBranchSpecForLine(state, line.Members, line.ProjectionMerged),
            spec => spec.OrderText);
    }

    private StrategyBuilder.SearchBranchSpec BuildSearchBranchSpecForLine(
        ComparisonState state,
        List<StrategyBuilder.MergedFamilyOutcome> line,
        bool projectionMerged)
    {
        StrategyBuilder.MergedFamilyOutcome representative =
            SelectSearchRepresentativeForLine(state, line, projectionMerged);

        return new StrategyBuilder.SearchBranchSpec(
            representative.Family.RepresentativeOrder,
            representative.NextState,
            representative.NextFixedTopMask,
            representative.NextRemainingSlots);
    }

    private StrategyBuilder.MergedFamilyOutcome SelectSearchRepresentativeForLine(
        ComparisonState state,
        List<StrategyBuilder.MergedFamilyOutcome> line,
        bool projectionMerged)
    {
        if (line.Count <= 1)
            return line[0];

        List<StrategyBuilder.OrderFamilyDescriptor> families = _dependencies.CollectLineFamilies(line);
        bool allSingleton = HasOnlySingletonFamilies(families);
        if (TrySelectProjectionQuotientRepresentativeForLine(
                state,
                line,
                projectionMerged,
                allSingleton,
                out StrategyBuilder.MergedFamilyOutcome quotientRepresentative,
                out _))
        {
            return quotientRepresentative;
        }

        if (!allSingleton)
            return line[0];

        if (projectionMerged)
            return _dependencies.SelectOrbitRepresentative(line);

        EquivalentOrderSummary? summary = _dependencies.BuildEquivalentOrderSummary(families);
        if (ShouldFoldSingletonOrbitRepresentative(summary))
            return _dependencies.SelectOrbitRepresentative(line);

        return line[0];
    }

    private bool ShouldFoldSingletonOrbitRepresentative(EquivalentOrderSummary? summary)
    {
        if (summary is null)
            return false;

        return !_dependencies.MergedOrderingsFormSingleOrbit(summary);
    }

    private static bool HasOnlySingletonFamilies(List<StrategyBuilder.OrderFamilyDescriptor> families)
    {
        return families.All(family => family.Count == 1);
    }

    private StrategyBuilder.BranchSpec BuildRelabelRepresentativeBranchSpec(
        ComparisonState state,
        List<StrategyBuilder.MergedFamilyOutcome> line)
    {
        StrategyBuilder.MergedFamilyOutcome representative = _dependencies.SelectOrbitRepresentative(line);
        EquivalentOrderSummary relabelSummary =
            _dependencies.BuildRelabelingOrbitSummary(state, line, representative);
        return new StrategyBuilder.BranchSpec(
            representative.Family.RepresentativeOrder,
            representative,
            relabelSummary);
    }

    private bool TryBuildProjectionQuotientSummaryForLine(
        ComparisonState state,
        List<StrategyBuilder.MergedFamilyOutcome> line,
        out StrategyBuilder.MergedFamilyOutcome representative,
        out EquivalentOrderSummary? quotientSummary)
    {
        representative = _dependencies.SelectOrbitRepresentative(line);
        quotientSummary = _dependencies.BuildProjectionQuotientSummary(state, line, representative);
        return quotientSummary is not null;
    }

    private bool TrySelectProjectionQuotientRepresentativeForLine(
        ComparisonState state,
        List<StrategyBuilder.MergedFamilyOutcome> line,
        bool projectionMerged,
        bool allSingleton,
        out StrategyBuilder.MergedFamilyOutcome representative,
        out EquivalentOrderSummary? quotientSummary)
    {
        if (!projectionMerged || allSingleton)
        {
            representative = line[0];
            quotientSummary = null;
            return false;
        }

        return TryBuildProjectionQuotientSummaryForLine(
            state,
            line,
            out representative,
            out quotientSummary);
    }

    private IReadOnlyList<StrategyBuilder.TransitionSpec> BuildTransitionSpecsFromBranchSpecs(
        ComparisonState state,
        ulong fixedTopMask,
        IReadOnlyList<StrategyBuilder.BranchSpec> branchSpecs)
    {
        IReadOnlyList<StrategyBuilder.TransitionTargetFields> targets = BuildTransitionTargetFields(branchSpecs);

        return BuildTransitionSpecsFromTargets(
            targets,
            (index, target) => new StrategyBuilder.TransitionSpec(
                target.OrderText,
                branchSpecs[index].Summary,
                _dependencies.BuildComparisonEffect(state, fixedTopMask, target.NextState, target.NextFixedTopMask),
                target.NextState,
                target.NextFixedTopMask,
                target.NextRemainingSlots));
    }

    private IReadOnlyList<StrategyBuilder.SearchTransitionSpec> BuildTransitionSpecsFromSearchBranchSpecs(
        ComparisonState state,
        ulong fixedTopMask,
        IReadOnlyList<StrategyBuilder.SearchBranchSpec> branchSpecs)
    {
        IReadOnlyList<StrategyBuilder.TransitionTargetFields> targets = BuildTransitionTargetFields(branchSpecs);

        return BuildTransitionSpecsFromTargets(
            targets,
            (_, target) => new StrategyBuilder.SearchTransitionSpec(
                target.OrderText,
                _dependencies.BuildSearchComparisonEffect(state, fixedTopMask, target.NextState, target.NextFixedTopMask),
                target.NextState,
                target.NextFixedTopMask,
                target.NextRemainingSlots));
    }

    private static IReadOnlyList<StrategyBuilder.TransitionTargetFields> BuildTransitionTargetFields<TSpec>(
        IReadOnlyList<TSpec> branchSpecs,
        Func<TSpec, string> getOrderText,
        Func<TSpec, ComparisonState> getNextState,
        Func<TSpec, ulong> getNextFixedTopMask,
        Func<TSpec, int> getNextRemainingSlots)
    {
        return branchSpecs
            .Select(spec => new StrategyBuilder.TransitionTargetFields(
                getOrderText(spec),
                getNextState(spec),
                getNextFixedTopMask(spec),
                getNextRemainingSlots(spec)))
            .ToList();
    }

    private static IReadOnlyList<StrategyBuilder.TransitionTargetFields> BuildTransitionTargetFields(
        IReadOnlyList<StrategyBuilder.BranchSpec> branchSpecs)
    {
        return BuildTransitionTargetFields(
            branchSpecs,
            spec => spec.OrderText,
            spec => spec.Outcome.NextState,
            spec => spec.Outcome.NextFixedTopMask,
            spec => spec.Outcome.NextRemainingSlots);
    }

    private static IReadOnlyList<StrategyBuilder.TransitionTargetFields> BuildTransitionTargetFields(
        IReadOnlyList<StrategyBuilder.SearchBranchSpec> branchSpecs)
    {
        return BuildTransitionTargetFields(
            branchSpecs,
            spec => spec.OrderText,
            spec => spec.NextState,
            spec => spec.NextFixedTopMask,
            spec => spec.NextRemainingSlots);
    }

    private static IReadOnlyList<TTransitionSpec> BuildTransitionSpecsFromTargets<TTransitionSpec>(
        IReadOnlyList<StrategyBuilder.TransitionTargetFields> targets,
        Func<int, StrategyBuilder.TransitionTargetFields, TTransitionSpec> buildSpec)
    {
        var specs = new List<TTransitionSpec>(targets.Count);
        for (int i = 0; i < targets.Count; i++)
            specs.Add(buildSpec(i, targets[i]));

        return specs;
    }

    private List<TSpec> BuildOrderedBranchSpecsWithDoomedTailFallback<TSpec>(
        ComparisonState state,
        StrategyBuilder.SelectedComparisonGroup chosenGroup,
        Func<StrategyBuilder.SelectedComparisonGroup, List<TSpec>?> tryBuildDoomedTailSpecs,
        Func<StrategyBuilder.PlannedBranchLine, TSpec> buildSpec,
        Func<TSpec, string> getOrderText)
    {
        _dependencies.ThrowIfCancellationRequested();

        List<TSpec>? doomedTailSpecs = tryBuildDoomedTailSpecs(chosenGroup);
        if (doomedTailSpecs is not null)
        {
            return doomedTailSpecs
                .OrderBy(getOrderText, StringComparer.Ordinal)
                .ToList();
        }

        return BuildPlannedBranchSpecsForChosenGroup(
            state,
            chosenGroup,
            buildSpec,
            getOrderText);
    }

    private List<TSpec> BuildPlannedBranchSpecsForChosenGroup<TSpec>(
        ComparisonState state,
        StrategyBuilder.SelectedComparisonGroup chosenGroup,
        Func<StrategyBuilder.PlannedBranchLine, TSpec> buildSpec,
        Func<TSpec, string> getOrderText)
    {
        var specs = new List<TSpec>();
        foreach (var line in PlanBranchLinesForChosenGroup(state, chosenGroup))
            specs.Add(buildSpec(line));

        return specs
            .OrderBy(getOrderText, StringComparer.Ordinal)
            .ToList();
    }

    private List<StrategyBuilder.PlannedBranchLine> PlanBranchLinesForChosenGroup(
        ComparisonState state,
        StrategyBuilder.SelectedComparisonGroup chosenGroup)
    {
        var plannedLines = new List<StrategyBuilder.PlannedBranchLine>();
        foreach (StrategyBuilder.MergedBranch merged in chosenGroup.Branches)
            plannedLines.AddRange(_dependencies.PlanBranchLinesForMergedBranch(state, merged));

        return plannedLines;
    }

    private StrategyBuilder.BranchSpec BuildBranchSpecForLine(
        ComparisonState state,
        List<StrategyBuilder.MergedFamilyOutcome> line,
        bool projectionMerged)
    {
        List<StrategyBuilder.OrderFamilyDescriptor> families = _dependencies.CollectLineFamilies(line);
        bool allSingleton = HasOnlySingletonFamilies(families);
        if (TrySelectProjectionQuotientRepresentativeForLine(
                state,
                line,
                projectionMerged,
                allSingleton,
                out StrategyBuilder.MergedFamilyOutcome quotientRepresentative,
                out EquivalentOrderSummary? quotientSummary))
        {
            return new StrategyBuilder.BranchSpec(
                quotientRepresentative.Family.RepresentativeOrder,
                quotientRepresentative,
                quotientSummary);
        }

        if (allSingleton
            && projectionMerged)
        {
            return BuildRelabelRepresentativeBranchSpec(state, line);
        }

        EquivalentOrderSummary? summary = _dependencies.BuildEquivalentOrderSummary(families);

        if (allSingleton
            && ShouldFoldSingletonOrbitRepresentative(summary))
        {
            return BuildRelabelRepresentativeBranchSpec(state, line);
        }

        StrategyBuilder.MergedFamilyOutcome fallbackRepresentative = line[0];
        return new StrategyBuilder.BranchSpec(
            fallbackRepresentative.Family.RepresentativeOrder,
            fallbackRepresentative,
            summary);
    }
}