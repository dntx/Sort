using System;
using System.Collections.Generic;
using System.Linq;

partial class StrategyBuilder
{
    private SearchTransitionPlanner? _transitionPlanner;
    private SearchTransitionPlanner TransitionPlanner => _transitionPlanner ??= new SearchTransitionPlanner(this);

    private sealed class SearchTransitionPlanner
    {
        private readonly StrategyBuilder _owner;

        public SearchTransitionPlanner(StrategyBuilder owner)
        {
            _owner = owner;
        }

        public IReadOnlyList<TransitionSpec> BuildDisplayTransitionSpecs(
            ComparisonState state,
            ulong fixedTopMask,
            int remainingSlots,
            SelectedComparisonGroup chosenGroup)
        {
            return BuildTransitionSpecsFromBranchSpecs(
                state,
                fixedTopMask,
                BuildBranchSpecs(state, remainingSlots, chosenGroup));
        }

        public IReadOnlyList<SearchTransitionSpec> BuildSearchTransitionSpecs(
            ComparisonState state,
            ulong fixedTopMask,
            int remainingSlots,
            SelectedComparisonGroup chosenGroup)
        {
            return BuildTransitionSpecsFromSearchBranchSpecs(
                state,
                fixedTopMask,
                BuildSearchBranchSpecs(state, remainingSlots, chosenGroup));
        }

        public List<BranchSpec> BuildBranchSpecs(
            ComparisonState state,
            int remainingSlots,
            SelectedComparisonGroup chosenGroup)
        {
            return BuildOrderedBranchSpecsWithDoomedTailFallback(
                state,
                chosenGroup,
                tryBuildDoomedTailSpecs: group => _owner.TryBuildDoomedTailSpecs(state, remainingSlots, group),
                line => BuildBranchSpecForLine(state, line.Members, line.ProjectionMerged),
                spec => spec.OrderText);
        }

        public List<SearchBranchSpec> BuildSearchBranchSpecs(
            ComparisonState state,
            int remainingSlots,
            SelectedComparisonGroup chosenGroup)
        {
            return BuildOrderedBranchSpecsWithDoomedTailFallback(
                state,
                chosenGroup,
                tryBuildDoomedTailSpecs: group => _owner.TryBuildSearchDoomedTailSpecs(state, remainingSlots, group),
                line => BuildSearchBranchSpecForLine(state, line.Members, line.ProjectionMerged),
                spec => spec.OrderText);
        }

        private SearchBranchSpec BuildSearchBranchSpecForLine(
            ComparisonState state,
            List<MergedFamilyOutcome> line,
            bool projectionMerged)
        {
            MergedFamilyOutcome representative =
                SelectSearchRepresentativeForLine(state, line, projectionMerged);

            return new SearchBranchSpec(
                representative.Family.RepresentativeOrder,
                representative.NextState,
                representative.NextFixedTopMask,
                representative.NextRemainingSlots);
        }

        private MergedFamilyOutcome SelectSearchRepresentativeForLine(
            ComparisonState state,
            List<MergedFamilyOutcome> line,
            bool projectionMerged)
        {
            if (line.Count <= 1)
                return line[0];

            List<OrderFamilyDescriptor> families = CollectLineFamilies(line);
            bool allSingleton = HasOnlySingletonFamilies(families);
            if (TrySelectProjectionQuotientRepresentativeForLine(
                    state,
                    line,
                    projectionMerged,
                    allSingleton,
                    out MergedFamilyOutcome quotientRepresentative,
                    out _))
            {
                return quotientRepresentative;
            }

            if (!allSingleton)
                return line[0];

            if (projectionMerged)
                return SelectOrbitRepresentative(line);

            EquivalentOrderSummary? summary = StrategyBuilder.BuildEquivalentOrderSummary(families);
            if (ShouldFoldSingletonOrbitRepresentative(summary))
                return SelectOrbitRepresentative(line);

            return line[0];
        }

        private static bool ShouldFoldSingletonOrbitRepresentative(EquivalentOrderSummary? summary)
        {
            if (summary is null)
                return false;

            return !MergedOrderingsFormSingleOrbit(summary);
        }

        private static bool HasOnlySingletonFamilies(List<OrderFamilyDescriptor> families)
        {
            return families.All(family => family.Count == 1);
        }

        private BranchSpec BuildRelabelRepresentativeBranchSpec(
            ComparisonState state,
            List<MergedFamilyOutcome> line)
        {
            MergedFamilyOutcome representative = SelectOrbitRepresentative(line);
            EquivalentOrderSummary relabelSummary =
                _owner.BuildRelabelingOrbitSummary(state, line, representative);
            return new BranchSpec(
                representative.Family.RepresentativeOrder,
                representative,
                relabelSummary);
        }

        private bool TryBuildProjectionQuotientSummaryForLine(
            ComparisonState state,
            List<MergedFamilyOutcome> line,
            out MergedFamilyOutcome representative,
            out EquivalentOrderSummary? quotientSummary)
        {
            representative = SelectOrbitRepresentative(line);
            quotientSummary = _owner.BuildProjectionQuotientSummary(state, line, representative);
            return quotientSummary is not null;
        }

        private bool TrySelectProjectionQuotientRepresentativeForLine(
            ComparisonState state,
            List<MergedFamilyOutcome> line,
            bool projectionMerged,
            bool allSingleton,
            out MergedFamilyOutcome representative,
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

        private IReadOnlyList<TransitionSpec> BuildTransitionSpecsFromBranchSpecs(
            ComparisonState state,
            ulong fixedTopMask,
            IReadOnlyList<BranchSpec> branchSpecs)
        {
            IReadOnlyList<TransitionTargetFields> targets = BuildTransitionTargetFields(branchSpecs);

            return BuildTransitionSpecsFromTargets(
                targets,
                (index, target) => new TransitionSpec(
                    target.OrderText,
                    branchSpecs[index].Summary,
                    _owner.BuildComparisonEffect(state, fixedTopMask, target.NextState, target.NextFixedTopMask),
                    target.NextState,
                    target.NextFixedTopMask,
                    target.NextRemainingSlots));
        }

        private IReadOnlyList<SearchTransitionSpec> BuildTransitionSpecsFromSearchBranchSpecs(
            ComparisonState state,
            ulong fixedTopMask,
            IReadOnlyList<SearchBranchSpec> branchSpecs)
        {
            IReadOnlyList<TransitionTargetFields> targets = BuildTransitionTargetFields(branchSpecs);

            return BuildTransitionSpecsFromTargets(
                targets,
                (_, target) => new SearchTransitionSpec(
                    target.OrderText,
                    _owner.BuildSearchComparisonEffect(state, fixedTopMask, target.NextState, target.NextFixedTopMask),
                    target.NextState,
                    target.NextFixedTopMask,
                    target.NextRemainingSlots));
        }

        private static IReadOnlyList<TransitionTargetFields> BuildTransitionTargetFields<TSpec>(
            IReadOnlyList<TSpec> branchSpecs,
            Func<TSpec, string> getOrderText,
            Func<TSpec, ComparisonState> getNextState,
            Func<TSpec, ulong> getNextFixedTopMask,
            Func<TSpec, int> getNextRemainingSlots)
        {
            return branchSpecs
                .Select(spec => new TransitionTargetFields(
                    getOrderText(spec),
                    getNextState(spec),
                    getNextFixedTopMask(spec),
                    getNextRemainingSlots(spec)))
                .ToList();
        }

        private static IReadOnlyList<TransitionTargetFields> BuildTransitionTargetFields(
            IReadOnlyList<BranchSpec> branchSpecs)
        {
            return BuildTransitionTargetFields(
                branchSpecs,
                spec => spec.OrderText,
                spec => spec.Outcome.NextState,
                spec => spec.Outcome.NextFixedTopMask,
                spec => spec.Outcome.NextRemainingSlots);
        }

        private static IReadOnlyList<TransitionTargetFields> BuildTransitionTargetFields(
            IReadOnlyList<SearchBranchSpec> branchSpecs)
        {
            return BuildTransitionTargetFields(
                branchSpecs,
                spec => spec.OrderText,
                spec => spec.NextState,
                spec => spec.NextFixedTopMask,
                spec => spec.NextRemainingSlots);
        }

        private static IReadOnlyList<TTransitionSpec> BuildTransitionSpecsFromTargets<TTransitionSpec>(
            IReadOnlyList<TransitionTargetFields> targets,
            Func<int, TransitionTargetFields, TTransitionSpec> buildSpec)
        {
            var specs = new List<TTransitionSpec>(targets.Count);
            for (int i = 0; i < targets.Count; i++)
                specs.Add(buildSpec(i, targets[i]));

            return specs;
        }

        private List<TSpec> BuildOrderedBranchSpecsWithDoomedTailFallback<TSpec>(
            ComparisonState state,
            SelectedComparisonGroup chosenGroup,
            Func<SelectedComparisonGroup, List<TSpec>?> tryBuildDoomedTailSpecs,
            Func<PlannedBranchLine, TSpec> buildSpec,
            Func<TSpec, string> getOrderText)
        {
            _owner.ThrowIfCancellationRequested();

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
            SelectedComparisonGroup chosenGroup,
            Func<PlannedBranchLine, TSpec> buildSpec,
            Func<TSpec, string> getOrderText)
        {
            var specs = new List<TSpec>();
            foreach (var line in PlanBranchLinesForChosenGroup(state, chosenGroup))
                specs.Add(buildSpec(line));

            return specs
                .OrderBy(getOrderText, StringComparer.Ordinal)
                .ToList();
        }

        private List<PlannedBranchLine> PlanBranchLinesForChosenGroup(
            ComparisonState state,
            SelectedComparisonGroup chosenGroup)
        {
            var plannedLines = new List<PlannedBranchLine>();
            foreach (MergedBranch merged in chosenGroup.Branches)
            {
                if (_owner.EnableProjectionPairingProbe)
                    _owner.RecordProjectionPairingBucket(state, merged.FamilyOutcomes);

                plannedLines.AddRange(_owner.SplitMergedBucketIntoBranchLines(state, merged.FamilyOutcomes));
            }

            return plannedLines;
        }

        private BranchSpec BuildBranchSpecForLine(
            ComparisonState state,
            List<MergedFamilyOutcome> line,
            bool projectionMerged)
        {
            List<OrderFamilyDescriptor> families = CollectLineFamilies(line);
            bool allSingleton = HasOnlySingletonFamilies(families);
            if (TrySelectProjectionQuotientRepresentativeForLine(
                    state,
                    line,
                    projectionMerged,
                    allSingleton,
                    out MergedFamilyOutcome quotientRepresentative,
                    out EquivalentOrderSummary? quotientSummary))
            {
                return new BranchSpec(
                    quotientRepresentative.Family.RepresentativeOrder,
                    quotientRepresentative,
                    quotientSummary);
            }

            if (allSingleton
                && projectionMerged)
            {
                return BuildRelabelRepresentativeBranchSpec(state, line);
            }

            EquivalentOrderSummary? summary = StrategyBuilder.BuildEquivalentOrderSummary(families);

            if (allSingleton
                && ShouldFoldSingletonOrbitRepresentative(summary))
            {
                return BuildRelabelRepresentativeBranchSpec(state, line);
            }

            MergedFamilyOutcome fallbackRepresentative = line[0];
            return new BranchSpec(
                fallbackRepresentative.Family.RepresentativeOrder,
                fallbackRepresentative,
                summary);
        }
    }
}