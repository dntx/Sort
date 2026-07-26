using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TopKFinder;

sealed record CompactStageArtifacts(
    SolvedStrategy Solution,
    StrategyPlan Plan,
    StageTimings Timings);
sealed record CompactProbeArtifacts(
    StageOutcome Outcome,
    SolvedStrategy? Solution,
    StrategyPlan? Plan,
    StageTimings Timings = default);
sealed record ProofTightenStageArtifacts(StageResult Result)
{
    public SolvedStrategy? Solution => Result.Solution;
}
sealed record CompactPlanResult(
    SolvedStrategy? Solution,
    StrategyPlan Plan,
    StageTimings Timings = default);

partial class StrategyBuilder
{
    private SolvedStrategy CreateCompactSolvedStrategy(
        SolvedStrategyStageKind stageKind,
        string stageName,
        bool isProvenOptimal,
        bool wasCandidateEnumerationCapped,
        bool includeSearchEdgeCost)
    {
        var rootState = new ComparisonState(_n);
        ulong ignoredFixedTopMask = 0;
        int remainingSlots = _k;
        NormalizeState(rootState, ref ignoredFixedTopMask, ref remainingSlots);
        SearchStateKey rootKey = GetSearchStateKey(rootState, remainingSlots);

        var nodes = new Dictionary<SearchStateKey, SolvedStrategyNode>();
        int worstCaseSteps = FreezeCompactState(rootState, remainingSlots, nodes);
        int searchEdgeCost = SolvedStrategyScoreService.ComputeSearchEdgeCost(rootKey, nodes);
        if (includeSearchEdgeCost && searchEdgeCost != _compactRootCost)
        {
            throw new InvalidOperationException(
                "Frozen compact strategy cost must equal the compact DP root cost.");
        }

        return new SolvedStrategy(
            new ProblemShape(_n, _m, _requestedK, _k),
            rootKey,
            nodes,
            new StrategyScore(worstCaseSteps, searchEdgeCost),
            new BoundEvidence(
                isProvenOptimal ? worstCaseSteps : _rootProvenLowerBound,
                worstCaseSteps,
                isProvenOptimal,
                wasCandidateEnumerationCapped),
            new StageProvenance(stageKind, stageName),
            CreateSearchStatistics(_compactRootCost));
    }

    private int FreezeCompactState(
        ComparisonState state,
        int remainingSlots,
        Dictionary<SearchStateKey, SolvedStrategyNode> nodes)
    {
        ThrowIfCancellationRequested();
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);

        if (remainingSlots == 0
            || TryGetDeterminedTopSet(state, remainingSlots, out _)
            || state.ActiveCount <= remainingSlots)
        {
            return 0;
        }

        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        if (nodes.TryGetValue(key, out SolvedStrategyNode? existing))
            return existing.RemainingDepth;

        if (state.ActiveCount <= _m)
        {
            nodes.Add(key, SolvedStrategyNode.FinalChoice());
            return 1;
        }

        if (!_compactGroupPatternCache.TryGetValue(key, out BestGroupPattern selectedPattern))
        {
            throw new InvalidOperationException(
                "Compact solve must populate a group pattern for every root-reachable decision state.");
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

        int maxChildDepth = 0;
        foreach (ComparisonOutcome successor in successors)
        {
            int childDepth = FreezeCompactState(
                successor.NextState,
                successor.NextRemainingSlots,
                nodes);
            maxChildDepth = Math.Max(maxChildDepth, childDepth);
        }

        int remainingDepth = maxChildDepth + 1;
        nodes.Add(key, new SolvedStrategyNode(selectedPattern, successorKeys, remainingDepth));
        return remainingDepth;
    }

    private StrategyPlan MaterializeCompactSolution(
        SolvedStrategy solution,
        Stopwatch stopwatch,
        int? searchTreeEdges,
        bool isFeasibleUpperBound)
    {
        StrategyNode root = BuildState(
            new ComparisonState(_n),
            0,
            _k,
            1,
            new MaterializationContext(Solution: solution));
        stopwatch.Stop();
        StrategyPlan plan = CreatePlan(
            root,
            stopwatch.Elapsed,
            searchTreeEdges,
            isFeasibleUpperBound);

        if (solution.Score.WorstCaseSteps != plan.MaxStep)
        {
            throw new InvalidOperationException(
                "Compact solved-strategy depth must equal the materialized plan MaxStep.");
        }

        return plan;
    }

    internal StrategyPlan MaterializeCompactSolutionForTesting(SolvedStrategy solution)
    {
        _stateIds.Clear();
        _expandedStates.Clear();
        _materializationDisplayPath.Clear();
        _nextStateId = 1;
        var stopwatch = Stopwatch.StartNew();
        return MaterializeCompactSolution(
            solution,
            stopwatch,
            solution.Score.SearchEdgeCost,
            isFeasibleUpperBound: !solution.Bounds.IsProvenOptimal);
    }
}