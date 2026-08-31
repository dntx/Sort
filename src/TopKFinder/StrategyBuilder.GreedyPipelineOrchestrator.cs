using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TopKFinder;

partial class StrategyBuilder
{
    private readonly List<ProofTightenAttemptDiagnostics> _proofTightenAttemptTrace = new();
    internal IReadOnlyList<ProofTightenAttemptDiagnostics> ProofTightenAttemptTrace => _proofTightenAttemptTrace;
    private Dictionary<GroupSelectionHelper.CandidateGenerationRetryCacheKey,
        GroupSelectionHelper.CandidateGenerationRetryCacheEntry>? _proofTightenCandidateGenerationRetryCache;
    private int _proofTightenCandidateGenerationRetryHits;
    private Dictionary<BudgetCandidateRetryCacheKey, BudgetCandidateRetryCacheEntry>? _proofTightenStateContinuationCache;
    private int _proofTightenStateContinuationHits;

    private readonly record struct BudgetCandidateRetryCacheKey(
        RawStructureKey State,
        IntSequenceKey Candidates,
        int GroupSize,
        int BranchBudget);

    // A transition is cap-independent once it has been built: it captures the exact successor
    // states for one candidate group, and only records child results after they are conclusive.
    // Keeping it across cap epochs lets a retry resume the state-level proof work rather than
    // rebuilding and re-solving every already-seen candidate at that state.
    private sealed class BudgetCandidateRetryCacheEntry
    {
        private readonly Dictionary<IntSequenceKey, BudgetFitTransition> _transitions = new();

        public bool TryGetTransition(IReadOnlyList<int> group, out BudgetFitTransition transition)
            => _transitions.TryGetValue(new IntSequenceKey(CopyGroup(group)), out transition!);

        public void AddTransition(IReadOnlyList<int> group, BudgetFitTransition transition)
            => _transitions.Add(new IntSequenceKey(CopyGroup(group)), transition);

        private static int[] CopyGroup(IReadOnlyList<int> group)
        {
            var copy = new int[group.Count];
            for (int i = 0; i < group.Count; i++)
                copy[i] = group[i];
            return copy;
        }
    }

    private sealed class BudgetFitTransition
    {
        private readonly List<(ComparisonState State, int RemainingSlots)>? _children;
        private readonly ChildResult[]? _childResults;

        public enum ChildResult
        {
            Incomplete,
            Feasible,
            ProvenInfeasible,
        }

        public BudgetFitTransition(List<(ComparisonState State, int RemainingSlots)>? children)
        {
            _children = CloneChildren(children);
            _childResults = children is null ? null : new ChildResult[children.Count];
            _childRealSteps = children is null ? null : new int[children.Count];
        }

        public bool HasChildren => _children is not null;
        public int ChildCount => _children?.Count ?? 0;

        public (ComparisonState State, int RemainingSlots) CreateChild(int index)
        {
            var child = _children![index];
            return (child.State.Clone(), child.RemainingSlots);
        }

        public ChildResult GetChildResult(int index) => _childResults![index];

        public void MarkChildFeasible(int index, int realSteps)
        {
            _childResults![index] = ChildResult.Feasible;
            _childRealSteps![index] = realSteps;
        }

        public void MarkChildProvenInfeasible(int index)
            => _childResults![index] = ChildResult.ProvenInfeasible;

        public int GetChildRealSteps(int index) => _childRealSteps![index];

        private readonly int[]? _childRealSteps;

        private static List<(ComparisonState State, int RemainingSlots)>? CloneChildren(
            List<(ComparisonState State, int RemainingSlots)>? children)
        {
            if (children is null)
                return null;

            var clone = new List<(ComparisonState State, int RemainingSlots)>(children.Count);
            foreach (var child in children)
                clone.Add((child.State.Clone(), child.RemainingSlots));
            return clone;
        }
    }

    private sealed class GreedyPipelineOrchestrator
    {
        private readonly StrategyBuilder _owner;

        public GreedyPipelineOrchestrator(StrategyBuilder owner)
        {
            _owner = owner;
        }

        public StrategyPlan RunGreedyPipelineCore(
            Action<StageResult>? onStageCompleted = null,
            Action<string>? onStageStart = null,
            bool materializeStages = true,
            Action<StageCompletion>? onStageBoundary = null)
        {
            var callbacks = new PipelineCallbacks(onStageCompleted, onStageStart, onStageBoundary);
            _owner._progressScope = _owner._reportCombinedRunProgress
                ? ProgressScope.CompactFeasibleInCombinedRun
                : ProgressScope.DefaultStandalone;

            // The step ceiling U comes from the greedy feasible plan. Production callers (Program.cs /
            // MainForm.cs) build it first and reuse this builder, so _feasibleRootBudget is already set;
            // standalone callers (e.g. tests invoking RunGreedyPipeline directly) have not, so
            // establish it here. ExecuteGreedyFeasibleStage deliberately does not clear _feasibleRootBudget, so this
            // never double-builds when the caller already ran the step phase.
            if (_owner._feasibleRootBudget < 0)
                _owner.ExecuteGreedyFeasibleStage();

            int U = _owner._feasibleRootBudget;
            int provenLowerBound = Math.Max(1, _owner._rootProvenLowerBound);

            // Phase A: proof tightening to find the smallest feasible step S.
            _owner._compactFeasibilityOnly = true;
            int bestStep = U;
            int budget = U - 1;
            _owner._proofTightenInitialBudget = budget;
            _owner._proofTightenCurrentBudget = budget;
            _owner._proofTightenLowerBound = provenLowerBound;
            _owner._proofTightenProgressEmaInitialized = false;
            _owner._proofTightenProgressEma01 = 0.0;
            try
            {
                while (budget >= provenLowerBound)
                {
                    _owner._cancellationToken.ThrowIfCancellationRequested();
                    _owner._proofTightenCurrentBudget = budget;
                    string stageName = StageNames.FormatProofTighten(budget);
                    callbacks.Start(stageName);
                    ProofTightenStageArtifacts artifacts = ExecuteProofTightenStageWithSolution(budget, materializeStages);
                    StageResult stage = artifacts.Result;
                    string nextStageName = stage.Outcome == StageOutcome.Tightened
                        && artifacts.Solution is not null
                        && artifacts.Solution.Score.WorstCaseSteps - 1 >= provenLowerBound
                            ? StageNames.FormatProofTighten(artifacts.Solution.Score.WorstCaseSteps - 1)
                            : StageNames.FormatGreedyEdgeCompact(
                                artifacts.Solution?.Score.WorstCaseSteps ?? bestStep);
                    PipelineStageProtocol.EmitStage(stage, callbacks, nextStageName);

                    if (stage.Outcome == StageOutcome.Tightened)
                    {
                        if (artifacts.Solution is null)
                        {
                            throw new InvalidOperationException(
                                "A tightened proof stage must carry its frozen compact solution.");
                        }

                        bestStep = artifacts.Solution.Score.WorstCaseSteps;
                        budget = bestStep - 1; // realized max-step may already be below the attempted ceiling
                        continue;
                    }

                    // ProvenInfeasible / Incomplete both stop tightening. Only a complete-
                    // enumeration infeasibility proof closes the squeeze to a proven optimum.
                    if (stage.Outcome == StageOutcome.ProvenInfeasible)
                        _owner.RecordRootProvenLowerBound(budget + 1);
                    break;
                }
            }
            finally
            {
                _owner._compactFeasibilityOnly = false;
                _owner._proofTightenInitialBudget = -1;
                _owner._proofTightenCurrentBudget = -1;
                _owner._proofTightenLowerBound = -1;
                _owner._proofTightenProgressEmaInitialized = false;
                _owner._proofTightenProgressEma01 = 0.0;
            }

            // Phase B: one edge-compaction pass at the determined step S.
            string edgeCompactStageName = StageNames.FormatGreedyEdgeCompact(bestStep);
            callbacks.Start(edgeCompactStageName);
            var edgeStopwatch = Stopwatch.StartNew();
            CompactPlanResult edgeResult = BuildEdgeCompactPlanAtBudget(bestStep, materializeStages);
            StrategyPlan? finalPlan = materializeStages
                ? edgeResult.Plan!.WithRootProvenLowerBound(_owner._rootProvenLowerBound)
                : null;
            edgeStopwatch.Stop();
            StageTimings edgeTimings = StageTimings.FromTotal(
                edgeStopwatch.Elapsed,
                edgeResult.Timings.Freeze,
                edgeResult.Timings.Materialize);
            PipelineStageProtocol.EmitStage(
                new StageResult(
                    edgeCompactStageName,
                    finalPlan,
                    edgeStopwatch.Elapsed,
                    edgeResult.Solution is null ? StageOutcome.Incomplete : StageOutcome.Completed,
                    edgeResult.Solution,
                    edgeTimings),
                callbacks);
            return finalPlan!;
        }

        public StageResult ExecuteProofTightenStage(int budget)
            => ExecuteProofTightenStageWithSolution(budget).Result;

        public ProofTightenStageArtifacts ExecuteProofTightenStageWithSolution(
            int budget,
            bool materialize = true)
        {
            _owner._progressScope = _owner._reportCombinedRunProgress
                ? ProgressScope.CompactFeasibleInCombinedRun
                : ProgressScope.DefaultStandalone;

            _owner._compactFeasibilityOnly = true;
            try
            {
                string stageName = StageNames.FormatProofTighten(budget);
                var stopwatch = Stopwatch.StartNew();
                CompactProbeArtifacts probe = ProbeAndClassify(budget, materialize);
                stopwatch.Stop();
                if (probe.Plan is not null)
                    _owner._latestGreedyIncumbentPlan = probe.Plan;
                StageTimings timings = StageTimings.FromTotal(
                    stopwatch.Elapsed,
                    probe.Timings.Freeze,
                    probe.Timings.Materialize);
                var result = new StageResult(
                    stageName,
                    probe.Plan,
                    timings.Total,
                    probe.Outcome,
                    probe.Solution,
                    timings);
                return new ProofTightenStageArtifacts(result);
            }
            finally
            {
                _owner._compactFeasibilityOnly = false;
            }
        }

        // Runs one feasibility probe at the given step ceiling and classifies it into the single typed
        // outcome the tightening driver consumes. Keeping this classification here (separate from the
        // driver's control flow) guarantees every probe yields exactly one {outcome, plan} result, so the
        // driver can never stop without emitting a stage. The realized plan is carried for Tightened (meets
        // the ceiling, an improvement); it is null for the plan-less ProvenInfeasible / Incomplete outcomes.
        //
        // budget == bestStep - 1 at every call site, so `MaxStep <= budget` is exactly `MaxStep < bestStep`
        // (a strict improvement over the incumbent). A returned plan whose MaxStep exceeds the budget would be
        // an overshoot; since the tighter-budget-keep fix (PR #223) the compact proxy and the materialized
        // tree agree, so that case is an internal invariant violation and throws rather than being reported.
        public CompactProbeArtifacts ProbeAndClassify(int budget, bool materialize = true)
        {
            int configuredCap = _owner.CompactGreedyCandidateCap;
            int attemptCap = NormalizeGreedyCandidateCap(_owner.CompactGreedyCandidateCap);
            int attempt = 0;
            _owner._proofTightenCandidateGenerationRetryCache = new Dictionary<
                GroupSelectionHelper.CandidateGenerationRetryCacheKey,
                GroupSelectionHelper.CandidateGenerationRetryCacheEntry>();
            _owner._proofTightenCandidateGenerationRetryHits = 0;
            _owner._proofTightenStateContinuationCache = new Dictionary<
                BudgetCandidateRetryCacheKey,
                BudgetCandidateRetryCacheEntry>();
            _owner._proofTightenStateContinuationHits = 0;
            _owner._proofTightenAttemptTrace.Clear();
            try
            {
                while (true)
                {
                    _owner.CompactGreedyCandidateCap = attemptCap;
                    attempt++;
                    int retryHitsBefore = _owner._proofTightenCandidateGenerationRetryHits;
                    int stateContinuationHitsBefore = _owner._proofTightenStateContinuationHits;
                    var stopwatch = Stopwatch.StartNew();
                    CompactStageArtifacts? candidate = ProbeFeasibleCompactCore(
                        budget,
                        SolvedStrategyStageKind.ProofTighten,
                        StageNames.FormatProofTighten(budget),
                        materialize,
                        progressiveRetry: attempt > 1);
                    stopwatch.Stop();
                    bool enumerationCapped = candidate is null && _owner._lastProbeEnumerationCapped;
                    string outcome = candidate is not null
                        ? "tightened"
                        : enumerationCapped ? "incomplete" : "proven-infeasible";
                    _owner._proofTightenAttemptTrace.Add(new ProofTightenAttemptDiagnostics(
                        attempt,
                        budget,
                        attemptCap,
                        stopwatch.Elapsed,
                        outcome,
                        enumerationCapped,
                        _owner._compactStatesSolved,
                        _owner._compactGroupsEnumerated,
                        _owner._compactStepOptimalGroups,
                        _owner._outcomesConstructed,
                        _owner._candidateGroupsEnumerated,
                        _owner._proofTightenCandidateGenerationRetryHits - retryHitsBefore,
                        _owner._proofTightenStateContinuationHits - stateContinuationHitsBefore));
                    string logLine =
                        $"[proof-tighten] budget={budget}, attempt={attempt}, cap={attemptCap}, " +
                        $"elapsed={stopwatch.Elapsed.TotalMilliseconds:F1}ms, outcome={outcome}, " +
                        $"capped={enumerationCapped}, states={_owner._compactStatesSolved}, " +
                        $"groups={_owner._compactGroupsEnumerated}, fit-groups={_owner._compactStepOptimalGroups}, " +
                        $"outcomes={_owner._outcomesConstructed}, raw-candidates={_owner._candidateGroupsEnumerated}, " +
                        $"reused-candidate-generation={_owner._proofTightenCandidateGenerationRetryHits - retryHitsBefore}, " +
                        $"reused-state-transitions={_owner._proofTightenStateContinuationHits - stateContinuationHitsBefore}";
                    Debug.WriteLine(logLine);
                    if (Console.IsErrorRedirected)
                        Console.Error.WriteLine(logLine);

                    if (candidate is null && enumerationCapped && attemptCap < int.MaxValue)
                    {
                        attemptCap = NextGreedyCandidateCap(attemptCap);
                        continue;
                    }

                    if (candidate is null)
                        return new CompactProbeArtifacts(
                            enumerationCapped ? StageOutcome.Incomplete : StageOutcome.ProvenInfeasible,
                            Solution: null,
                            Plan: null);

                    if (candidate.Solution.Score.WorstCaseSteps > budget)
                        throw new InvalidOperationException(
                            $"Compact feasibility probe at budget {budget} materialized a plan whose realized MaxStep " +
                            $"{candidate.Solution.Score.WorstCaseSteps} overshoots the ceiling.");

                    return new CompactProbeArtifacts(
                        StageOutcome.Tightened,
                        candidate.Solution,
                        candidate.Plan,
                        candidate.Timings);
                }
            }
            finally
            {
                _owner.CompactGreedyCandidateCap = configuredCap;
                _owner._proofTightenCandidateGenerationRetryCache = null;
                _owner._proofTightenCandidateGenerationRetryHits = 0;
                _owner._proofTightenStateContinuationCache = null;
                _owner._proofTightenStateContinuationHits = 0;
            }
        }

        public CompactStageArtifacts? ProbeFeasibleCompact(
            int rootBudget,
            SolvedStrategyStageKind stageKind,
            string stageName,
            bool materialize = true)
            => ProbeFeasibleCompactCore(rootBudget, stageKind, stageName, materialize);

        private CompactStageArtifacts? ProbeFeasibleCompactCore(
            int rootBudget,
            SolvedStrategyStageKind stageKind,
            string stageName,
            bool materialize = true,
            bool progressiveRetry = false)
        {
            return _owner.RunWithComparisonStateCancellation(() =>
            {
                bool feasibilityOnly = _owner._compactFeasibilityOnly;
                _owner.PrepareFeasibleCompactProbe(progressiveRetry);
                _owner._compactFeasibilityOnly = feasibilityOnly;

                var stopwatch = Stopwatch.StartNew();
                _owner._compactUsesFeasibleBudget = true;
                _owner._feasibleRootBudgetActive = rootBudget;
                try
                {
                    _owner.EnsureCompactSolved();
                    _owner._phase1bMilliseconds = stopwatch.ElapsedMilliseconds;
                    if (_owner._compactRootCost == int.MaxValue)
                    {
                        // Record whether the cap truncated any state's enumeration during this probe. When set,
                        // "no group fit within budget" is not a proof of infeasibility (an untried group might
                        // have fit), so the caller must not close the squeeze / claim proven optimality.
                        _owner._lastProbeEnumerationCapped = _owner._compactEnumerationCapped;
                        _owner.ResetCompactState();
                        return null;
                    }

                    TimeSpan solveElapsed = stopwatch.Elapsed;
                    SolvedStrategy solution = _owner.CreateCompactSolvedStrategy(
                        stageKind,
                        stageName,
                        isProvenOptimal: false,
                        wasCandidateEnumerationCapped: _owner._compactEnumerationCapped,
                        includeSearchEdgeCost: stageKind == SolvedStrategyStageKind.GreedyEdgeCompact);
                    TimeSpan freezeElapsed = stopwatch.Elapsed - solveElapsed;
                    StrategyPlan? plan = materialize
                        ? _owner.MaterializeCompactSolution(
                            solution,
                            stopwatch,
                            _owner._compactRootCost,
                            isFeasibleUpperBound: true)
                        : null;
                    _owner._phase2Milliseconds = stopwatch.ElapsedMilliseconds - _owner._phase1bMilliseconds;
                    return new CompactStageArtifacts(
                        solution,
                        plan!,
                        new StageTimings(
                            solveElapsed,
                            freezeElapsed,
                            materialize
                                ? stopwatch.Elapsed - solveElapsed - freezeElapsed
                                : TimeSpan.Zero));
                }
                finally
                {
                    _owner._feasibleRootBudgetActive = -1;
                }
            });
        }

        private static int NormalizeGreedyCandidateCap(int cap)
            => cap <= 0 ? GreedyCandidateCapMinimum : cap;

        private static int NextGreedyCandidateCap(int current)
        {
            if (current >= int.MaxValue)
                return int.MaxValue;

            long grown = (long)current * GreedyCandidateCapGrowthFactor;
            return grown >= int.MaxValue ? int.MaxValue : (int)grown;
        }

        public CompactPlanResult BuildEdgeCompactPlanAtBudget(
            int rootBudget,
            bool materialize = true)
        {
            CompactStageArtifacts? artifacts = ProbeFeasibleCompact(
                rootBudget,
                SolvedStrategyStageKind.GreedyEdgeCompact,
                StageNames.FormatGreedyEdgeCompact(rootBudget),
                materialize);
            if (artifacts is not null)
                return new CompactPlanResult(
                    artifacts.Solution,
                    artifacts.Plan!,
                    artifacts.Timings);

            if (_owner._lastProbeEnumerationCapped
                && (!materialize
                    || (_owner._latestGreedyIncumbentPlan is not null
                        && _owner._latestGreedyIncumbentPlan.MaxStep <= rootBudget)))
            {
                return new CompactPlanResult(
                    Solution: null,
                    materialize ? _owner._latestGreedyIncumbentPlan : null);
            }

            throw new InvalidOperationException(
                $"Greedy edge-compaction could not materialize a plan at the proven-feasible budget {rootBudget}.");
        }

    }

    internal readonly record struct ProofTightenAttemptDiagnostics(
        int Attempt,
        int Budget,
        int CandidateCap,
        TimeSpan Elapsed,
        string Outcome,
        bool EnumerationCapped,
        int CompactStatesSolved,
        int CompactGroupsEnumerated,
        int CompactStepOptimalGroups,
        int OutcomesConstructed,
        int CandidateGroupsEnumerated,
        int ReusedCandidateGenerationEntries,
        int ReusedStateTransitions);
}
