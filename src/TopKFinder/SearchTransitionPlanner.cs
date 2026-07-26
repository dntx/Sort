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
            PlanDisplayTransitionLinesForChosenGroup,
            PlanSearchTransitionTargetsForChosenGroup,
            BuildDisplayBranchSpecForPlanner);
    }
}

internal sealed class SearchTransitionPlanner
{
    internal sealed record Dependencies(
        Action ThrowIfCancellationRequested,
        Func<ComparisonState, int, StrategyBuilder.SelectedComparisonGroup, List<StrategyBuilder.BranchSpec>?> TryBuildDoomedTailSpecs,
        Func<ComparisonState, int, StrategyBuilder.SelectedComparisonGroup, List<StrategyBuilder.TransitionTargetFields>?> TryBuildSearchDoomedTailSpecs,
        Func<ComparisonState, StrategyBuilder.SelectedComparisonGroup, List<StrategyBuilder.PlannedBranchLine>> PlanDisplayBranchLinesForChosenGroup,
        Func<ComparisonState, StrategyBuilder.SelectedComparisonGroup, List<StrategyBuilder.TransitionTargetFields>> PlanSearchTransitionTargetsForChosenGroup,
        Func<ComparisonState, List<StrategyBuilder.MergedFamilyOutcome>, bool, StrategyBuilder.BranchSpec> BuildDisplayBranchSpecForLine);

    private readonly Dependencies _dependencies;

    public SearchTransitionPlanner(Dependencies dependencies)
    {
        _dependencies = dependencies;
    }

    public List<StrategyBuilder.BranchSpec> BuildBranchSpecs(
        ComparisonState state,
        int remainingSlots,
        StrategyBuilder.SelectedComparisonGroup chosenGroup)
    {
        return BuildOrderedBranchSpecsWithDoomedTailFallback(
            chosenGroup,
            tryBuildDoomedTailSpecs: group => _dependencies.TryBuildDoomedTailSpecs(state, remainingSlots, group),
            planBranchLinesForChosenGroup: group => _dependencies.PlanDisplayBranchLinesForChosenGroup(state, group),
            buildSpec: line => _dependencies.BuildDisplayBranchSpecForLine(state, line.Members, line.ProjectionMerged));
    }

    public List<StrategyBuilder.TransitionTargetFields> BuildSearchTransitionTargets(
        ComparisonState state,
        int remainingSlots,
        StrategyBuilder.SelectedComparisonGroup chosenGroup)
    {
        return BuildOrderedTargetsWithDoomedTailFallback(
            chosenGroup,
            tryBuildDoomedTailSpecs: group => _dependencies.TryBuildSearchDoomedTailSpecs(state, remainingSlots, group),
            planTargetsForChosenGroup: group => _dependencies.PlanSearchTransitionTargetsForChosenGroup(state, group));
    }

    private List<StrategyBuilder.BranchSpec> BuildOrderedBranchSpecsWithDoomedTailFallback(
        StrategyBuilder.SelectedComparisonGroup chosenGroup,
        Func<StrategyBuilder.SelectedComparisonGroup, List<StrategyBuilder.BranchSpec>?> tryBuildDoomedTailSpecs,
        Func<StrategyBuilder.SelectedComparisonGroup, List<StrategyBuilder.PlannedBranchLine>> planBranchLinesForChosenGroup,
        Func<StrategyBuilder.PlannedBranchLine, StrategyBuilder.BranchSpec> buildSpec)
    {
        _dependencies.ThrowIfCancellationRequested();

        List<StrategyBuilder.BranchSpec>? doomedTailSpecs = tryBuildDoomedTailSpecs(chosenGroup);
        if (doomedTailSpecs is not null)
        {
            return doomedTailSpecs
                .OrderBy(spec => spec.OrderText, StringComparer.Ordinal)
                .ToList();
        }

        var specs = new List<StrategyBuilder.BranchSpec>();
        foreach (var line in planBranchLinesForChosenGroup(chosenGroup))
            specs.Add(buildSpec(line));

        return specs
            .OrderBy(spec => spec.OrderText, StringComparer.Ordinal)
            .ToList();
    }

    private List<StrategyBuilder.TransitionTargetFields> BuildOrderedTargetsWithDoomedTailFallback(
        StrategyBuilder.SelectedComparisonGroup chosenGroup,
        Func<StrategyBuilder.SelectedComparisonGroup, List<StrategyBuilder.TransitionTargetFields>?> tryBuildDoomedTailSpecs,
        Func<StrategyBuilder.SelectedComparisonGroup, List<StrategyBuilder.TransitionTargetFields>> planTargetsForChosenGroup)
    {
        _dependencies.ThrowIfCancellationRequested();

        List<StrategyBuilder.TransitionTargetFields>? doomedTailSpecs = tryBuildDoomedTailSpecs(chosenGroup);
        if (doomedTailSpecs is not null)
        {
            return doomedTailSpecs
                .OrderBy(target => target.OrderText, StringComparer.Ordinal)
                .ToList();
        }

        return planTargetsForChosenGroup(chosenGroup)
            .OrderBy(target => target.OrderText, StringComparer.Ordinal)
            .ToList();
    }

}