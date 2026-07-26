using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TopKFinder;

sealed record ExactStepProofStageArtifacts(
    SolvedStrategy Solution,
    StrategyPlan? Plan,
    StageTimings Timings);

partial class StrategyBuilder
{
    internal ExactStepProofStageArtifacts BuildExactStepProofStageArtifacts(bool materialize = true)
    {
        var stopwatch = Stopwatch.StartNew();
        InitializeExactSolverSession(useFeasibleBudget: false);
        _phase1Milliseconds = stopwatch.ElapsedMilliseconds;
        TimeSpan solveElapsed = stopwatch.Elapsed;

        SolvedStrategy solution = CreateExactSolvedStrategy();
        _phase1bMilliseconds = stopwatch.ElapsedMilliseconds - _phase1Milliseconds;
        TimeSpan freezeElapsed = stopwatch.Elapsed - solveElapsed;

        StrategyPlan? plan = null;
        _useCompact = false;
        if (materialize)
        {
            StrategyNode root = BuildState(
                new ComparisonState(_n),
                0,
                _k,
                1,
                new MaterializationContext(Solution: solution));
            plan = CreatePlan(root, stopwatch.Elapsed);
            if (solution.Score.WorstCaseSteps != plan.MaxStep)
            {
                throw new InvalidOperationException(
                    "Exact solved-strategy depth must equal the materialized plan MaxStep.");
            }
        }
        _phase2Milliseconds = stopwatch.ElapsedMilliseconds - _phase1Milliseconds - _phase1bMilliseconds;
        stopwatch.Stop();
        ReportProgress(force: true);

        return new ExactStepProofStageArtifacts(
            solution,
            plan,
            new StageTimings(
                solveElapsed,
                freezeElapsed,
                materialize
                    ? stopwatch.Elapsed - solveElapsed - freezeElapsed
                    : TimeSpan.Zero));
    }

    private SolvedStrategy CreateExactSolvedStrategy()
    {
        var rootState = new ComparisonState(_n);
        ulong ignoredFixedTopMask = 0;
        int remainingSlots = _k;
        NormalizeState(rootState, ref ignoredFixedTopMask, ref remainingSlots);
        SearchStateKey rootKey = GetSearchStateKey(rootState, remainingSlots);

        var nodes = new Dictionary<SearchStateKey, SolvedStrategyNode>();
        int worstCaseSteps = FreezeExactState(rootState, remainingSlots, nodes);
        int searchEdgeCost = SolvedStrategyScoreService.ComputeSearchEdgeCost(rootKey, nodes);

        return new SolvedStrategy(
            new ProblemShape(_n, _m, _requestedK, _k),
            rootKey,
            nodes,
            new StrategyScore(worstCaseSteps, searchEdgeCost),
            new BoundEvidence(
                worstCaseSteps,
                worstCaseSteps,
                IsProvenOptimal: true,
                WasCandidateEnumerationCapped: false),
            new StageProvenance(SolvedStrategyStageKind.StepProof, StageNames.StepProof),
            CreateSearchStatistics());
    }

    private int FreezeExactState(
        ComparisonState state,
        int remainingSlots,
        Dictionary<SearchStateKey, SolvedStrategyNode> nodes)
    {
        ThrowIfCancellationRequested();
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);

        if (remainingSlots == 0)
            return 0;

        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        if ((_feasibleTopSetCache.TryGetValue(key, out FeasibleTopSetInfo topSetInfo) && topSetInfo.Count == 1)
            || state.ActiveCount <= remainingSlots)
        {
            return 0;
        }

        if (nodes.TryGetValue(key, out SolvedStrategyNode? existing))
            return existing.RemainingDepth;

        if (state.ActiveCount <= _m)
        {
            nodes.Add(key, SolvedStrategyNode.FinalChoice());
            return 1;
        }

        if (!_minWorstCaseStepsCache.TryGetValue(key, out int remainingDepth)
            || !_bestGroupPatternCache.TryGetValue(key, out BestGroupPattern selectedPattern))
        {
            throw new InvalidOperationException(
                "Exact phase 1 must populate depth and group-pattern caches for every selected state.");
        }

        List<int> selectedGroup = ReplayExactPattern(state, selectedPattern);
        var successorKeys = new HashSet<SearchStateKey>();
        var successors = new List<ComparisonOutcome>();
        foreach (ComparisonOutcome outcome in EnumerateSnapshotOutcomes(state, remainingSlots, selectedGroup))
        {
            if (outcome.NextSearchKey.Equals(key) || !successorKeys.Add(outcome.NextSearchKey))
                continue;
            successors.Add(outcome);
        }

        nodes.Add(key, new SolvedStrategyNode(selectedPattern, successorKeys, remainingDepth));
        foreach (ComparisonOutcome successor in successors)
            FreezeExactState(successor.NextState, successor.NextRemainingSlots, nodes);

        return remainingDepth;
    }

    private List<int> ReplayExactPattern(ComparisonState state, BestGroupPattern pattern)
    {
        List<int> candidates = state.GetActiveItemsOrdered();
        int[]? activeColors = pattern.ColorSignature is null ? null : state.GetActiveItemColors();

        foreach (List<int> group in CombinatoricsService.EnumerateCombinations(
            candidates,
            pattern.GroupSize,
            () => ProbeCancellation(0)))
        {
            if (activeColors is not null
                && !GroupEnumerationService.GroupMatchesColorSignature(activeColors, group, pattern.ColorSignature!))
            {
                continue;
            }

            if (GetGroupPattern(state, group) == pattern.Pattern)
                return group;
        }

        throw new InvalidOperationException(
            "Exact cached group pattern did not match its frozen search state.");
    }
}