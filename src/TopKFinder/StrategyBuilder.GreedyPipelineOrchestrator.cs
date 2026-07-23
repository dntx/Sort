using System;
using System.Diagnostics;

namespace TopKFinder;

partial class StrategyBuilder
{
    private sealed class GreedyPipelineOrchestrator
    {
        private readonly StrategyBuilder _owner;

        public GreedyPipelineOrchestrator(StrategyBuilder owner)
        {
            _owner = owner;
        }

        public StrategyPlan RunGreedyPipelineCore(
            Action<StageResult>? onStageCompleted = null,
            Action<string>? onStageStart = null)
        {
            var callbacks = new PipelineCallbacks(onStageCompleted, onStageStart);
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
                    StageResult stage = ExecuteProofTightenStage(budget);
                    PipelineStageProtocol.EmitStage(stage, callbacks);

                    if (stage.Outcome == StageOutcome.Tightened)
                    {
                        bestStep = stage.Plan!.MaxStep;
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
            StrategyPlan finalPlan = BuildEdgeCompactPlanAtBudget(bestStep)
                .WithRootProvenLowerBound(_owner._rootProvenLowerBound);
            edgeStopwatch.Stop();
            PipelineStageProtocol.EmitCompletedPlanStage(
                edgeCompactStageName,
                finalPlan,
                edgeStopwatch.Elapsed,
                callbacks);
            return finalPlan;
        }

        public StageResult ExecuteProofTightenStage(int budget)
        {
            _owner._progressScope = _owner._reportCombinedRunProgress
                ? ProgressScope.CompactFeasibleInCombinedRun
                : ProgressScope.DefaultStandalone;

            _owner._compactFeasibilityOnly = true;
            try
            {
                string stageName = StageNames.FormatProofTighten(budget);
                var stopwatch = Stopwatch.StartNew();
                (StageOutcome outcome, StrategyPlan? candidate) = ProbeAndClassify(budget);
                stopwatch.Stop();
                if (candidate is not null)
                    _owner._latestGreedyIncumbentPlan = candidate;
                return new StageResult(stageName, candidate, stopwatch.Elapsed, outcome);
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
        public (StageOutcome Outcome, StrategyPlan? Plan) ProbeAndClassify(int budget)
        {
            int configuredCap = _owner.CompactGreedyCandidateCap;
            int attemptCap = NormalizeGreedyCandidateCap(configuredCap);
            try
            {
                while (true)
                {
                    // Keep one user-facing knob (CompactGreedyCandidateCap) as the starting point, but
                    // internally escalate capped incomplete probes on the same budget until they either
                    // resolve conclusively or reach full enumeration.
                    _owner.CompactGreedyCandidateCap = attemptCap;

                    StrategyPlan? candidate = ProbeFeasibleCompact(budget);
                    if (candidate is null)
                    {
                        if (!_owner._lastProbeEnumerationCapped)
                            return (StageOutcome.ProvenInfeasible, null);

                        if (attemptCap == int.MaxValue)
                            return (StageOutcome.Incomplete, null);

                        attemptCap = NextGreedyCandidateCap(attemptCap);
                        continue;
                    }

                    // A complete probe that materializes a plan must honor the ceiling: the feasibility proxy proves
                    // a within-budget strategy exists and, with the tightest-budget pattern kept, materialization
                    // renders exactly that strategy. An overshoot means the proxy diverged from materialization -- a
                    // broken invariant we surface loudly instead of silently reporting an over-budget plan.
                    if (candidate.MaxStep > budget)
                        throw new InvalidOperationException(
                            $"Compact feasibility probe at budget {budget} materialized a plan whose realized MaxStep " +
                            $"{candidate.MaxStep} overshoots the ceiling. The tighter-budget-keep invariant should make " +
                            $"this unreachable; an overshoot indicates the feasibility proxy diverged from materialization.");

                    return (StageOutcome.Tightened, candidate);
                }
            }
            finally
            {
                _owner.CompactGreedyCandidateCap = configuredCap;
            }
        }

        public StrategyPlan? ProbeFeasibleCompact(int rootBudget)
        {
            return _owner.RunWithComparisonStateCancellation(() =>
            {
                _owner.PrepareFeasibleCompactProbe();

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

                    _owner._useCompact = true;
                    var root = _owner.BuildState(new ComparisonState(_owner._n), 0, _owner._k, 1);
                    _owner._phase2Milliseconds = stopwatch.ElapsedMilliseconds - _owner._phase1bMilliseconds;
                    stopwatch.Stop();
                    return _owner.CreatePlan(root, stopwatch.Elapsed, _owner._compactRootCost, isFeasibleUpperBound: true);
                }
                finally
                {
                    _owner._feasibleRootBudgetActive = -1;
                }
            });
        }

        public StrategyPlan BuildEdgeCompactPlanAtBudget(int rootBudget)
        {
            StrategyPlan? plan = ProbeFeasibleCompact(rootBudget);
            if (plan is not null)
                return plan;

            if (_owner._lastProbeEnumerationCapped
                && _owner._latestGreedyIncumbentPlan is not null
                && _owner._latestGreedyIncumbentPlan.MaxStep <= rootBudget)
            {
                return _owner._latestGreedyIncumbentPlan;
            }

            throw new InvalidOperationException(
                $"Greedy edge-compaction could not materialize a plan at the proven-feasible budget {rootBudget}.");
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
    }
}
