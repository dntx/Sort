using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace TopKFinder;

sealed record CompactStageArtifacts(
    SolvedStrategy Solution,
    StrategyPlan? Plan,
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
    StrategyPlan? Plan,
    StageTimings Timings = default);

partial class StrategyBuilder
{
    internal static StrategyPlan MaterializeSolvedStrategy(
        SolvedStrategy solution,
        TimeSpan priorElapsed,
        CancellationToken cancellationToken = default)
    {
        var materializer = new StrategyBuilder(
            solution.Problem.N,
            solution.Problem.M,
            solution.Problem.RequestedK,
            cancellationToken);
        return materializer.MaterializeSolvedStrategyCore(solution, priorElapsed);
    }

    private StrategyPlan MaterializeSolvedStrategyCore(
        SolvedStrategy solution,
        TimeSpan priorElapsed)
    {
        var stopwatch = Stopwatch.StartNew();
        StrategyNode root = BuildState(
            new ComparisonState(_n),
            0,
            _k,
            1,
            new MaterializationContext(Solution: solution));
        stopwatch.Stop();

        SearchStatistics displayStatistics = CreateSearchStatistics(solution.Score.SearchEdgeCost);
        SearchStatistics statistics = MergeMaterializedStatistics(
            solution.SearchStatistics,
            displayStatistics,
            solution.Score.SearchEdgeCost,
            stopwatch.Elapsed);
        var plan = new StrategyPlan(
            _n,
            _m,
            _requestedK,
            _k,
            root,
            priorElapsed + stopwatch.Elapsed,
            statistics,
            isFeasibleUpperBound: !solution.Bounds.IsProvenOptimal);

        if (solution.Score.WorstCaseSteps != plan.MaxStep)
        {
            throw new InvalidOperationException(
                "Solved-strategy depth must equal the independently materialized plan MaxStep.");
        }

        return plan;
    }

    private static SearchStatistics MergeMaterializedStatistics(
        SearchStatistics solver,
        SearchStatistics display,
        int? searchTreeEdges,
        TimeSpan materializeElapsed)
        => new(
            solver.SearchedStates,
            solver.PendingStates,
            solver.PeakPendingStates,
            display.OutputStates,
            display.ExpandedOutputStates,
            solver.LowerBoundStates,
            solver.FeasibleTopSetStates,
            MergeMaterializedDiagnostics(
                solver.Diagnostics,
                display.Diagnostics,
                display.FeasibleTopSetStates),
            solver.Phase1Milliseconds,
            solver.Phase1bMilliseconds,
            (long)materializeElapsed.TotalMilliseconds,
            checked(solver.OutcomesConstructed + display.OutcomesConstructed),
            checked(solver.CandidateGroupsEnumerated + display.CandidateGroupsEnumerated),
            searchTreeEdges,
            solver.CompactStatesSolved,
            solver.CompactGroupsEnumerated,
            solver.CompactStepOptimalGroups,
            solver.RootProvenLowerBound);

    private static SearchDiagnostics MergeMaterializedDiagnostics(
        SearchDiagnostics solver,
        SearchDiagnostics display,
        int displayFeasibleTopSetStates)
        => new(
            solver.RootIncumbents,
            checked(solver.LowerBoundPrunes + display.LowerBoundPrunes),
            checked(solver.DuplicateOutcomeSkips + display.DuplicateOutcomeSkips),
            checked(solver.MergedOutcomeCollisions + display.MergedOutcomeCollisions),
            checked(solver.ExactCacheHits + display.ExactCacheHits),
            checked(solver.LowerBoundCacheHits + display.LowerBoundCacheHits),
            checked(
                solver.FeasibleTopSetCacheHits
                + display.FeasibleTopSetCacheHits
                + displayFeasibleTopSetStates),
            checked(solver.BestGroupPatternCacheHits + display.BestGroupPatternCacheHits));

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