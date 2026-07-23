using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace TopKFinder;

partial class StrategyBuilder
{
    private readonly StrategyBuilderSession _session = new();

    private const int ProgressReportIntervalMs = 100;
    private const int IterativeDeepeningMinGroupSize = 5;
    private const int IterativeDeepeningMinRequestedTopCount = 5;
    private const int IterativeDeepeningMinNToMScale = 2;
    // Enable heavy display-line tie-break only on smaller active states; large states use
    // score+lex tie-break to avoid CountDisplayBranches becoming a stage-1 hotspot.
    private const int DisplayLineTieBreakMaxActiveCount = 17;
    private const int GreedyCandidateCapMinimum = 1;
    private const int GreedyCandidateCapGrowthFactor = 4;
    private const int AdaptiveDefaultCandidateCapMaxMultiplier = 4;
    private const int DefaultCancellationProbeMask = 63;

    private static class ProgressTuning
    {
        public static class CombinedRun
        {
            public const int FeasibleSpanPercent = 10;
            public const int DefaultSpanPercent = 60;
            public const int CompactPrimaryBasePercent = 60;
            public const int CompactPrimarySpanPercent = 40;
            public const int CompactFeasibleBasePercent = 10;
            public const int CompactFeasibleSpanPercent = 90;
        }

        public static class Asymptote
        {
            public const long MinimumRemainingMs = 500L;
            public const long InitialRemainingMs = 1000L;
            public const int ElapsedDivisor = 2;
            public const double FeasibleSoftCap = 0.99;
            public const double CompactFeasibleSoftCap = 0.999;
            public const double RawProgressSoftCapWithPending = 0.995;
        }

        public static class Ema
        {
            public const double SearchRateAlpha = 0.22;
            public const double PendingCostAlpha = 0.35;
            public const double PendingCostConservativeRiseAlpha = 0.45;
            public const double PendingCostConservativeFallAlpha = 0.04;
            public const double ProgressRiseAlpha = 0.25;
            public const double ProgressFallAlpha = 0.08;
            public const double ProofTightenCombinedProgressAlpha = 0.05;
        }

        public static class Pending
        {
            public const double CostBootstrapFloor = 64.0;
            public const long ZeroSettlingWindowMs = 400;
            public const int TailThreshold = 3;
            public const double TailInflationOnePending = 3.0;
            public const double TailInflationTwoPending = 2.1;
            public const double TailInflationThreePending = 1.5;
        }
    }
    private readonly int _n;
    private readonly int _m;
    private readonly int _requestedK;
    private readonly int _k;
    private readonly CancellationToken _cancellationToken;
    private readonly Action<SearchProgressSnapshot>? _progressCallback;
    private readonly bool _reportCombinedRunProgress;
    // Iterative deepening (IDA*-style bounded minimax) is enabled only in the deep, large-k regime
    // where measurement shows the tight global budget prunes enough deep nodes to outweigh the
    // re-exploration cost of multiple passes. Shallow/wide shapes keep the single-pass exact search
    // (GetMinWorstCaseStepsExact), which is byte-identical to the pre-ID algorithm, so they never
    // pay the re-exploration overhead. The threshold is an empirical heuristic, not a soundness
    // boundary: both code paths return the same exact MaxStep optimum. They do NOT necessarily
    // materialize the same tree -- among equally-optimal groups the bounded path can break ties
    // differently, so a gated (5,5) case may yield a different (still MaxStep-optimal) tree than the
    // single-pass path. See docs/core-algorithm.md sec 4.3.
    private readonly bool _useIterativeDeepening;
    // Test-only override of the iterative-deepening gate. When non-null it forces the search path
    // regardless of the (m, k, n) heuristic, letting a regression test run the SAME case under both
    // paths and assert they reach the same MaxStep optimum while iterative deepening constructs
    // strictly fewer outcomes. Null in production.
    internal bool? ForceIterativeDeepeningForTesting { get; set; }
    private CompactSolver? _compactSolver;
    private CompactSolver CompactSolverInstance => _compactSolver ??= new CompactSolver(this);
    private ProgressOrchestrator? _progressOrchestrator;
    private ProgressOrchestrator Progress => _progressOrchestrator ??= new ProgressOrchestrator(this);
    private SearchBoundsOrchestrator? _searchBoundsOrchestrator;
    private SearchBoundsOrchestrator SearchBounds => _searchBoundsOrchestrator ??= new SearchBoundsOrchestrator(this);
    private readonly Dictionary<IntSequenceKey, int> _stateIds = new();
    private readonly Dictionary<IntSequenceKey, ExpandedStateSnapshot> _expandedStates = new();
    // Active display-key recursion path while materializing a GreedyTighten tree. Used to reject
    // local overrides whose outcomes would reference an ancestor display state.
    private readonly HashSet<IntSequenceKey> _materializationDisplayPath = new();
    // Cross-instance canonical-key memo: maps a state's cheap raw structural fingerprint to its
    // expensive McKay canonical key. The same logical poset is reached via many search paths as
    // distinct ComparisonState instances (each with its own per-instance cache), so without this the
    // canonicalization is recomputed from scratch every time. Never cleared, so it accumulates across
    // the feasible/exact/compact phases of one builder. Sound because the raw fingerprint fully
    // determines the canonical key.
    private readonly Dictionary<RawStructureKey, IntSequenceKey> _canonicalKeyMemo = new();
    private readonly Stopwatch _progressStopwatch = Stopwatch.StartNew();
    private int _nextStateId = 1;
    private int _searchedStates;
    private int _pendingStates;
    private int _peakPendingStates;
    private long _lastProgressReportMs = -ProgressReportIntervalMs;
    private int _lastReportedVisitedStatesCount = 0;
    private long _feasiblePhase2StartMs = -1;
    private bool _feasiblePhaseSolved = false;
    private long _phase1Milliseconds;
    private long _phase1bMilliseconds;
    private long _phase2Milliseconds;
    private bool _recordRootIncumbents;
    private bool _rootSearchInitialized;
    private int _rootProvenLowerBound;
    private bool _phase1Solved;
    private bool _phase1bSolved;
    private StrategyPlan? _latestGreedyIncumbentPlan;
    private bool _progressEstimateInitialized;
    private double _progressEstimateEma01;
    private long _lastProgressSampleElapsedMs;
    private int _lastProgressSampleSearched;
    private bool _pendingCostEstimateInitialized;
    private double _pendingCostStatesPerPending;
    private double _pendingCostConservativeStatesPerPending;
    private int _pendingAtCostSample;
    private long _searchedSinceCostSample;
    private bool _searchRateEstimateInitialized;
    private double _searchRateStatesPerMs;
    private bool _pendingZeroSettling;
    private long _pendingZeroSinceMs;
    private int _pendingZeroSearchedAtStart;
    private int _cancellationProbeCounter;
    private bool _forceImmediateCancellationProbe;
    private ProgressScope _progressScope;
    private int _proofTightenInitialBudget = -1;
    private int _proofTightenCurrentBudget = -1;
    private int _proofTightenLowerBound = -1;
    private bool _proofTightenProgressEmaInitialized;
    private double _proofTightenProgressEma01;

    public StrategyBuilder(
        int n,
        int m,
        int k,
        CancellationToken cancellationToken = default,
        Action<SearchProgressSnapshot>? progressCallback = null,
        bool reportCombinedRunProgress = false)
    {
        _n = n;
        _m = m;
        _requestedK = k;
        _k = k > n - k ? n - k : k;
        _useIterativeDeepening = ShouldUseIterativeDeepening();
        _cancellationToken = cancellationToken;
        _progressCallback = progressCallback;
        _reportCombinedRunProgress = reportCombinedRunProgress;
        _progressScope = ProgressScope.DefaultStandalone;
    }

    private bool ShouldUseIterativeDeepening()
        => _m >= IterativeDeepeningMinGroupSize
            && _k >= IterativeDeepeningMinRequestedTopCount
            && _n >= IterativeDeepeningMinNToMScale * _m;

    private static int ScaleDefaultCandidateCap(int configuredCap, int defaultCap, int activeCount, int groupSize)
    {
        if (configuredCap != defaultCap)
            return configuredCap;

        if (activeCount <= 0 || groupSize <= 0)
            return defaultCap;

        long searchSurface = (long)activeCount * groupSize;
        long surfaceUnits = (searchSurface + defaultCap - 1L) / defaultCap;
        long multiplier = Math.Clamp(surfaceUnits, 1L, (long)AdaptiveDefaultCandidateCapMaxMultiplier);
        long scaledCap = defaultCap * multiplier;
        return scaledCap >= int.MaxValue ? int.MaxValue : (int)scaledCap;
    }

    public StrategyPlan ExecuteStepProofStage()
    {
        _progressScope = _reportCombinedRunProgress
            ? ProgressScope.DefaultInCombinedRun
            : ProgressScope.DefaultStandalone;
        return BuildPlan(useCompactSelection: false);
    }

    // Exact-stage entrypoint: materialize display/search artifacts in one solver session.
    public (SearchTree SearchTree, DisplayTree DisplayTree) ProjectDisplayAndSearchTrees()
        => BuildExactProjectionFromCurrentSession();

    // Search-model entrypoint used by public callers. It shares the same exact solver caches
    // as the display entrypoint while staying independent from display materialization.
    public SearchStrategy ProjectSearchTree()
        => ProjectSearchTreeFromStandaloneExactSession();

    public StrategyPlan RunExactPipeline(
        Action<StageResult>? onStageCompleted = null,
        Action<string>? onStageStart = null)
        => PublicPipelineOrchestrator.RunExactPipeline(this, onStageCompleted, onStageStart);

    public StrategyPlan ExecuteEdgeCompactStage()
    {
        // Returns the raw compact candidate: the compact DP keeps the optimal worst-case step count
        // (so MaxStep always matches default) and, among equally-optimal groups, minimizes a per-state
        // displayed-edge proxy. That proxy does not model the materializer's display-key Reference
        // de-duplication, so on rare shapes the compact selection can render MORE branch edges than
        // default (e.g. 10,4,8: 8 -> 10). This builder no longer guards against that internally --
        // the orchestrator's mainline rule (keep a phase's plan only when it strictly improves on the
        // global best) is the single place that decides whether the compact plan is shown, so a
        // worse-than-default compact candidate is simply never used.
        _progressScope = _reportCombinedRunProgress
            ? ProgressScope.CompactPrimaryInCombinedRun
            : ProgressScope.DefaultStandalone;
        return BuildPlan(useCompactSelection: true, useFeasibleBudget: false);
    }

    // Greedy mode: proof tightening followed by a single edge-compaction pass.
    //
    //   Phase A (ProofTighten): starting from the feasible upper bound U (the greedy step plan's MaxStep,
    //     threaded in via _feasibleRootBudget), probe ceilings U-1, U-2, ... with FEASIBILITY-ONLY
    //     compact solves (first solvable group in children-count-proxy order wins; no edge counting) to
    //     find the smallest feasible step S. Each probe is cheap relative to a full min-edge pass, and --
    //     crucially -- this avoids the wasted min-edge work at intermediate ceilings whose step is later
    //     superseded (the original architecture ran an edge-minimizing baseline at U first, which was
    //     discarded whenever tightening lowered the step below U).
    //   Phase B (EdgeCompact): run ONE min-edge compact pass at the determined step S to produce the
    //     final edge-minimized tree.
    //
    // Fast and interruptible, not proven optimal. onStageCompleted, when supplied, is invoked synchronously on
    // this thread each time a downstream stage becomes available: once per successful proof-tighten stage
    // (carrying the smaller plan), once for the terminal ceiling that stops tightening
    // (a no-solution/incomplete stage whose plan is null), and finally once for the edge-compaction pass
    // ("greedy-edge-compact@S"). This drives an anytime UI/CLI that surfaces the full progression as it is found; a
    // user who no longer wants to wait cancels (GUI Stop / CLI Ctrl+C), which propagates out with the
    // best plan found so far already surfaced via onStageCompleted.
    public StrategyPlan RunGreedyPipeline(
        Action<StageResult>? onStageCompleted = null,
        Action<string>? onStageStart = null)
        => RunGreedyPipelineCore(onStageCompleted, onStageStart);

    internal StrategyPlan RunGreedyPipelineCore(
        Action<StageResult>? onStageCompleted = null,
        Action<string>? onStageStart = null)
    {
        var callbacks = new PipelineCallbacks(onStageCompleted, onStageStart);
        _progressScope = _reportCombinedRunProgress
            ? ProgressScope.CompactFeasibleInCombinedRun
            : ProgressScope.DefaultStandalone;

        // The step ceiling U comes from the greedy feasible plan. Production callers (Program.cs /
        // MainForm.cs) build it first and reuse this builder, so _feasibleRootBudget is already set;
        // standalone callers (e.g. tests invoking RunGreedyPipeline directly) have not, so
        // establish it here. ExecuteGreedyFeasibleStage deliberately does not clear _feasibleRootBudget, so this
        // never double-builds when the caller already ran the step phase.
        if (_feasibleRootBudget < 0)
            ExecuteGreedyFeasibleStage();

        int U = _feasibleRootBudget;
        int provenLowerBound = Math.Max(1, _rootProvenLowerBound);

        // Phase A: proof tightening to find the smallest feasible step S.
        _compactFeasibilityOnly = true;
        int bestStep = U;
        int budget = U - 1;
        _proofTightenInitialBudget = budget;
        _proofTightenCurrentBudget = budget;
        _proofTightenLowerBound = provenLowerBound;
        _proofTightenProgressEmaInitialized = false;
        _proofTightenProgressEma01 = 0.0;
        try
        {
            while (budget >= provenLowerBound)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _proofTightenCurrentBudget = budget;
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
                    RecordRootProvenLowerBound(budget + 1);
                break;
            }
        }
        finally
        {
            _compactFeasibilityOnly = false;
            _proofTightenInitialBudget = -1;
            _proofTightenCurrentBudget = -1;
            _proofTightenLowerBound = -1;
            _proofTightenProgressEmaInitialized = false;
            _proofTightenProgressEma01 = 0.0;
        }

        // Phase B: one edge-compaction pass at the determined step S.
        string edgeCompactStageName = StageNames.FormatGreedyEdgeCompact(bestStep);
        callbacks.Start(edgeCompactStageName);
        var edgeStopwatch = Stopwatch.StartNew();
        StrategyPlan finalPlan = BuildEdgeCompactPlanAtBudget(bestStep)
            .WithRootProvenLowerBound(_rootProvenLowerBound);
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
        _progressScope = _reportCombinedRunProgress
            ? ProgressScope.CompactFeasibleInCombinedRun
            : ProgressScope.DefaultStandalone;

        _compactFeasibilityOnly = true;
        try
        {
            string stageName = StageNames.FormatProofTighten(budget);
            var stopwatch = Stopwatch.StartNew();
            (StageOutcome outcome, StrategyPlan? candidate) = ProbeAndClassify(budget);
            stopwatch.Stop();
            if (candidate is not null)
                _latestGreedyIncumbentPlan = candidate;
            return new StageResult(stageName, candidate, stopwatch.Elapsed, outcome);
        }
        finally
        {
            _compactFeasibilityOnly = false;
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
    private (StageOutcome Outcome, StrategyPlan? Plan) ProbeAndClassify(int budget)
    {
        int configuredCap = CompactGreedyCandidateCap;
        int attemptCap = NormalizeGreedyCandidateCap(configuredCap);
        try
        {
            while (true)
            {
                // Keep one user-facing knob (CompactGreedyCandidateCap) as the starting point, but
                // internally escalate capped incomplete probes on the same budget until they either
                // resolve conclusively or reach full enumeration.
                CompactGreedyCandidateCap = attemptCap;

                StrategyPlan? candidate = ProbeFeasibleCompact(budget);
                if (candidate is null)
                {
                    if (!_lastProbeEnumerationCapped)
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
            CompactGreedyCandidateCap = configuredCap;
        }
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

    // Runs a single compact pass at a fixed root ceiling, returning the materialized plan or null if the
    // ceiling is infeasible (root solve yields the unsolvable sentinel). Resets the per-budget compact
    // caches first. Progress snapshots flow normally so the bar/ETA track the current tightening probe.
    private StrategyPlan? ProbeFeasibleCompact(int rootBudget)
    {
        return RunWithComparisonStateCancellation(() =>
        {
            PrepareFeasibleCompactProbe();

            var stopwatch = Stopwatch.StartNew();
            _compactUsesFeasibleBudget = true;
            _feasibleRootBudgetActive = rootBudget;
            try
            {
                EnsureCompactSolved();
                _phase1bMilliseconds = stopwatch.ElapsedMilliseconds;
                if (_compactRootCost == int.MaxValue)
                {
                    // Record whether the cap truncated any state's enumeration during this probe. When set,
                    // "no group fit within budget" is not a proof of infeasibility (an untried group might
                    // have fit), so the caller must not close the squeeze / claim proven optimality.
                    _lastProbeEnumerationCapped = _compactEnumerationCapped;
                    ResetCompactState();
                    return null;
                }

                _useCompact = true;
                var root = BuildState(new ComparisonState(_n), 0, _k, 1);
                _phase2Milliseconds = stopwatch.ElapsedMilliseconds - _phase1bMilliseconds;
                stopwatch.Stop();
                return CreatePlan(root, stopwatch.Elapsed, _compactRootCost, isFeasibleUpperBound: true);
            }
            finally
            {
                _feasibleRootBudgetActive = -1;
            }
        });
    }

    private StrategyPlan BuildEdgeCompactPlanAtBudget(int rootBudget)
    {
        StrategyPlan? plan = ProbeFeasibleCompact(rootBudget);
        if (plan is not null)
            return plan;

        if (_lastProbeEnumerationCapped
            && _latestGreedyIncumbentPlan is not null
            && _latestGreedyIncumbentPlan.MaxStep <= rootBudget)
        {
            return _latestGreedyIncumbentPlan;
        }

        throw new InvalidOperationException(
            $"Greedy edge-compaction could not materialize a plan at the proven-feasible budget {rootBudget}.");
    }

    private StrategyPlan BuildPlan(bool useCompactSelection, bool useFeasibleBudget = false)
    {
        return RunWithComparisonStateCancellation(
            () => BuildPlanWithinSession(useCompactSelection, useFeasibleBudget, initializeSession: true));
    }

    private StrategyPlan BuildPlanWithinSession(
        bool useCompactSelection,
        bool useFeasibleBudget,
        bool initializeSession)
    {
        var stopwatch = Stopwatch.StartNew();
        if (initializeSession)
            InitializeExactSolverSession(useFeasibleBudget);
        _phase1Milliseconds = stopwatch.ElapsedMilliseconds;

        if (useCompactSelection)
            EnsureCompactSolved();
        _phase1bMilliseconds = stopwatch.ElapsedMilliseconds - _phase1Milliseconds;

        // Phase 2: materialize the strategy tree, reusing the cached group patterns.
        _useCompact = useCompactSelection;
        var root = BuildState(new ComparisonState(_n), 0, _k, 1);
        _phase2Milliseconds = stopwatch.ElapsedMilliseconds - _phase1Milliseconds - _phase1bMilliseconds;
        stopwatch.Stop();
        ReportProgress(force: true);
        bool feasible = useFeasibleBudget;
        int? searchTreeEdges = useCompactSelection ? _compactRootCost : null;
        return CreatePlan(root, stopwatch.Elapsed, searchTreeEdges, feasible);
    }

    private StrategyPlan CreatePlan(
        StrategyNode root,
        TimeSpan elapsed,
        int? searchTreeEdges = null,
        bool isFeasibleUpperBound = false)
        => new StrategyPlan(
            _n,
            _m,
            _requestedK,
            _k,
            root,
            elapsed,
            CreateSearchStatistics(searchTreeEdges),
            isFeasibleUpperBound: isFeasibleUpperBound);

    // Shared exact-session bootstrap used by both display and search entrypoints.
    // Mainline-A objective: keep phase-1 cache initialization semantics in one place.
    private void InitializeExactSolverSession(bool useFeasibleBudget)
    {
        ResetPerBuildTransientState();
        ReportProgress(force: true);

        _compactUsesFeasibleBudget = useFeasibleBudget;
        if (!useFeasibleBudget)
            EnsurePhase1Solved();
    }

    private readonly record struct MaterializationContext(
        bool ForceFixedConstructiveSelection = false);

    private StrategyNode BuildState(
        ComparisonState state,
        ulong fixedTopMask,
        int remainingSlots,
        int step,
        MaterializationContext context = default)
        => Materialization.BuildState(state, fixedTopMask, remainingSlots, step, context);

    private List<int> GetPossibleCandidates(ComparisonState state)
        => Materialization.GetPossibleCandidates(state);

    private SelectedComparisonGroup ChooseGroup(
        ComparisonState state,
        ulong fixedTopMask,
        int remainingSlots,
        MaterializationContext context)
        => Materialization.ChooseGroup(state, fixedTopMask, remainingSlots, context);

    private static IntSequenceKey GetGroupPattern(ComparisonState state, IReadOnlyList<int> group)
        => GroupSelectionHelper.GetGroupPattern(state, group);

    // Builds a BestGroupPattern carrying both the canonical group pattern and a cheap color
    // pre-filter signature (the sorted multiset of the group's per-item active colors). ChooseGroup
    // uses the signature to skip the expensive canonical-key check for groups that cannot match.
    private static BestGroupPattern MakeGroupPattern(ComparisonState state, IReadOnlyList<int> group)
        => GroupSelectionHelper.MakeGroupPattern(state, group);

    private IEnumerable<List<int>> EnumerateDistinctGroups(
        ComparisonState state,
        IReadOnlyList<int> candidates,
        int groupSize,
        int generationCap = int.MaxValue)
        => GroupSelectionHelper.EnumerateDistinctGroups(this, state, candidates, groupSize, generationCap);

    private IEnumerable<List<int>> EnumeratePrioritizedGroups(
        ComparisonState state,
        int remainingSlots,
        IReadOnlyList<int> candidates,
        int groupSize)
        => GroupSelectionHelper.EnumeratePrioritizedGroups(this, state, remainingSlots, candidates, groupSize);

    private HeuristicGroupScore BuildHeuristicGroupScore(ComparisonState state, int remainingSlots, IReadOnlyList<int> group)
        => GroupSelectionHelper.BuildHeuristicGroupScore(state, remainingSlots, group);

    private sealed class GroupSelectionHelper
    {
        private static void GenerateClassRepresentatives(
            StrategyBuilder owner,
            ComparisonState state,
            List<List<int>> classes,
            int[] suffixCapacity,
            int classIndex,
            int remaining,
            List<int> prefix,
            List<List<int>> collected,
            int generationCap)
        {
            owner.ProbeCancellation();

            if (collected.Count >= generationCap)
                return;

            if (remaining == 0)
            {
                owner.ThrowIfCancellationRequested();
                owner._candidateGroupsEnumerated++;
                var group = new List<int>(prefix);
                group.Sort();
                collected.Add(group);
                return;
            }

            // Prune branches that can no longer reach the required group size.
            if (classIndex == classes.Count || suffixCapacity[classIndex] < remaining)
                return;

            List<int> cls = classes[classIndex];
            int maxTake = Math.Min(cls.Count, remaining);
            for (int take = 0; take <= maxTake; take++)
            {
                for (int j = 0; j < take; j++)
                    prefix.Add(cls[j]);

                GenerateClassRepresentatives(
                    owner,
                    state,
                    classes,
                    suffixCapacity,
                    classIndex + 1,
                    remaining - take,
                    prefix,
                    collected,
                    generationCap);

                prefix.RemoveRange(prefix.Count - take, take);

                if (collected.Count >= generationCap)
                {
                    // Stopped short of trying the remaining (larger-`take`) siblings at this level
                    // because the cap filled up: the enumeration is genuinely truncated. Flag it so
                    // a probe that concludes infeasible under a finite cap is reported as incomplete,
                    // not a proof.
                    if (generationCap != int.MaxValue)
                        owner._compactEnumerationCapped = true;
                    return;
                }
            }
        }

        public static IntSequenceKey GetGroupPattern(ComparisonState state, IReadOnlyList<int> group)
        {
            ulong mask = 0;
            for (int i = 0; i < group.Count; i++)
                mask |= 1UL << group[i];
            return state.GetGroupCanonicalKey(mask);
        }

        // Builds a BestGroupPattern carrying both the canonical group pattern and a cheap color
        // pre-filter signature (the sorted multiset of the group's per-item active colors). ChooseGroup
        // uses the signature to skip the expensive canonical-key check for groups that cannot match.
        public static BestGroupPattern MakeGroupPattern(ComparisonState state, IReadOnlyList<int> group)
        {
            int[] colors = state.GetActiveItemColors();
            return new BestGroupPattern(
                group.Count,
                GetGroupPattern(state, group),
                GroupEnumerationService.BuildSortedColorSignature(colors, group));
        }

        public static IEnumerable<List<int>> EnumerateDistinctGroups(
            StrategyBuilder owner,
            ComparisonState state,
            IReadOnlyList<int> candidates,
            int groupSize,
            int generationCap)
        {
            // Exploit the active poset's automorphisms to avoid enumerating all C(active, groupSize)
            // combinations. Active items are partitioned into "free symmetry classes" (items with
            // identical active-restricted ancestor and descendant sets); every within-class permutation
            // is an automorphism, so all size-a selections from a class lie in one orbit and the class's
            // a smallest items canonically represent them. We therefore build a single candidate per
            // per-class count vector and canonically de-duplicate across classes, keeping the
            // lexicographically smallest member of each orbit. This produces exactly one representative
            // per orbit - identical to scanning every combination - but builds far fewer candidates on
            // symmetric states (e.g. a single candidate at the fully symmetric root instead of C(n, m)).
            //
            // generationCap bounds how many raw representatives we generate before the (cap-bounded) orbit
            // dedup and sort. The default int.MaxValue is the exact, complete enumeration used by the exact
            // compact DP and the optimality-gap audit; the greedy edge phase passes a finite cap so a single
            // large-m state cannot generate (and then FitChildren over) thousands of groups -- the
            // materialized generation and McKay dedup over the full set is what makes that phase hang.
            List<List<int>> classes = state.GetFreeSymmetryClasses();

            var suffixCapacity = new int[classes.Count + 1];
            for (int c = classes.Count - 1; c >= 0; c--)
                suffixCapacity[c] = suffixCapacity[c + 1] + classes[c].Count;

            var collected = new List<List<int>>();
            var prefix = new List<int>(groupSize);
            GenerateClassRepresentatives(owner, state, classes, suffixCapacity, 0, groupSize, prefix, collected, generationCap);

            // Orbit de-duplication via a cheap pre-filter. The full group canonical key
            // (GetGroupPattern -> McKay) is the only sound way to merge two groups that lie in the same
            // automorphism orbit, but it is expensive and dominates the search cost. Color-refinement
            // structural labels are an automorphism invariant, so two groups in the same orbit always
            // share the same sorted multiset of member labels. We bucket groups by that cheap signature:
            // groups with distinct signatures are provably in different orbits and need no canonical key,
            // so McKay only runs to disambiguate groups that collide on the cheap signature.
            int[] labels = state.GetStructuralLabels();
            var buckets = new Dictionary<IntSequenceKey, List<List<int>>>();
            foreach (var group in collected)
            {
                owner.ProbeCancellation();
                IntSequenceKey cheap = GroupEnumerationService.BuildCheapGroupSignature(labels, group);
                if (!buckets.TryGetValue(cheap, out List<List<int>>? bucket))
                {
                    bucket = new List<List<int>>();
                    buckets[cheap] = bucket;
                }

                bucket.Add(group);
            }

            var ordered = new List<List<int>>(buckets.Count);
            foreach (List<List<int>> bucket in buckets.Values)
            {
                owner.ProbeCancellation();
                if (bucket.Count == 1)
                {
                    ordered.Add(bucket[0]);
                    continue;
                }

                var representatives = new Dictionary<IntSequenceKey, List<int>>();
                foreach (List<int> group in bucket)
                {
                    owner.ProbeCancellation();
                    IntSequenceKey pattern = GetGroupPattern(state, group);
                    if (!representatives.TryGetValue(pattern, out List<int>? existing) ||
                        GroupEnumerationService.CompareGroupsLexicographically(group, existing) < 0)
                    {
                        representatives[pattern] = group;
                    }
                }

                ordered.AddRange(representatives.Values);
            }

            ordered.Sort(GroupEnumerationService.CompareGroupsLexicographically);
            return ordered;
        }

        public static IEnumerable<List<int>> EnumeratePrioritizedGroups(
            StrategyBuilder owner,
            ComparisonState state,
            int remainingSlots,
            IReadOnlyList<int> candidates,
            int groupSize)
        {
            var scoredGroups = new List<(List<int> Group, HeuristicGroupScore Score)>();
            foreach (var group in EnumerateDistinctGroups(owner, state, candidates, groupSize, int.MaxValue))
            {
                owner.ThrowIfCancellationRequested();
                scoredGroups.Add((group, BuildHeuristicGroupScore(state, remainingSlots, group)));
            }

            foreach (var entry in scoredGroups.OrderByDescending(entry => entry.Score))
                yield return entry.Group;
        }

        public static HeuristicGroupScore BuildHeuristicGroupScore(ComparisonState state, int remainingSlots, IReadOnlyList<int> group)
        {
            var components = BranchSelectionScoringService.BuildScoreComponents(state, remainingSlots, group);
            return new HeuristicGroupScore(
                components.GuaranteedTopHits,
                components.FreshItems,
                components.UnrelatedScore,
                components.UnresolvedPairs,
                components.GroupSize);
        }
    }

    private int GetStateId(IntSequenceKey key)
    {
        ProbeCancellation(0);
        if (_stateIds.TryGetValue(key, out int id))
            return id;

        id = _nextStateId++;
        _stateIds[key] = id;
        return id;
    }

    // Compatibility seam for partial files: keep call sites stable while routing key construction
    // through the extracted stateless key service.
    private SearchStateKey GetSearchStateKey(ComparisonState state, int remainingSlots)
    {
        return SearchStateKeyService.BuildSearchStateKey(state, remainingSlots, _canonicalKeyMemo);
    }

    // Compatibility seam for partial files: preserve existing helper name while delegating.
    private static IntSequenceKey GetDisplayStateKey(ComparisonState state, ulong fixedTopMask)
    {
        return SearchStateKeyService.GetDisplayStateKey(state, fixedTopMask);
    }

    private void NormalizeState(ComparisonState state, ref ulong fixedTopMask, ref int remainingSlots)
    {
        while (remainingSlots > 0)
        {
            ulong guaranteedTopMask = GetGuaranteedTopMask(state, remainingSlots);
            if (guaranteedTopMask == 0)
                break;

            fixedTopMask |= guaranteedTopMask;
            remainingSlots -= BitOperations.PopCount(guaranteedTopMask);
            state.Deactivate(guaranteedTopMask);
        }
    }

    private static string FormatOrder(IEnumerable<int> items)
    {
        return string.Join(" > ", items.Select(i => $"#{i + 1}"));
    }

    private void EnterSearchState()
    {
        _pendingStates++;
        _peakPendingStates = Math.Max(_peakPendingStates, _pendingStates);
        ReportProgress();
    }

    private void ExitSearchState()
    {
        _pendingStates--;
        ReportProgress();
    }

    private SearchStatistics CreateSearchStatistics(int? searchTreeEdges = null)
    {
        _searchedStates = _visitedSearchStates.Count;
        return new SearchStatistics(
            _searchedStates,
            _pendingStates,
            _peakPendingStates,
            _stateIds.Count,
            _expandedStates.Count,
            _lowerBoundStepsCache.Count,
            _feasibleTopSetCache.Count,
            new SearchDiagnostics(
                _rootIncumbents.ToArray(),
                _lowerBoundPrunes,
                _duplicateOutcomeSkips,
                _mergedOutcomeCollisions,
                _exactCacheHits,
                _lowerBoundCacheHits,
                _feasibleTopSetCacheHits,
                _bestGroupPatternCacheHits),
            _phase1Milliseconds,
            _phase1bMilliseconds,
            _phase2Milliseconds,
            _outcomesConstructed,
            _candidateGroupsEnumerated,
            searchTreeEdges,
            _compactStatesSolved,
            _compactGroupsEnumerated,
            _compactStepOptimalGroups,
            _rootProvenLowerBound);
    }

    private void ReportProgress(bool force = false)
    {
        Progress.ReportProgress(force);
    }

    // For feasible-stage incremental progress: periodically check visited-state count and report
    // if enough time has passed since the last report. Called from BuildState to provide smooth
    // progress updates during the possibly-slow recursive materialization of the greedy tree.
    internal void ThrottledReportProgressDuringFeasibleBuild()
    {
        Progress.ThrottledReportProgressDuringFeasibleBuild();
    }

    private double MapToReportedProgress(double localProgress01)
    {
        return Progress.MapToReportedProgress(localProgress01);
    }

    private double EstimateProgress(long elapsedMs)
    {
        return Progress.EstimateProgress(elapsedMs);
    }

    private void ObserveSearchState(ComparisonState state, int remainingSlots)
    {
        _visitedSearchStates.Add(SearchStateKeyService.BuildSearchStateKey(state, remainingSlots, _canonicalKeyMemo));
    }

    private void RecordRootIncumbent(int bestWorstCaseSteps, IReadOnlyList<int> group)
    {
        Progress.RecordRootIncumbent(bestWorstCaseSteps, group);
    }

    // Monotonically lifts the root proven lower bound (the L side of the squeeze). Called only
    // during the phase-1 root search; ignores non-increasing values so it stays monotone even
    // though the single-pass path reports the analytic bound before the exact result.
    private void RecordRootProvenLowerBound(int provenLowerBound)
    {
        Progress.RecordRootProvenLowerBound(provenLowerBound);
    }
    private void ThrowIfCancellationRequested()
    {
        _cancellationToken.ThrowIfCancellationRequested();
    }

    private T RunWithComparisonStateCancellation<T>(Func<T> action)
    {
        ComparisonState.SetThreadCancellationToken(_cancellationToken);
        try
        {
            return action();
        }
        finally
        {
            ComparisonState.SetThreadCancellationToken(default);
        }
    }

    // Shared throttled cancellation probe for hot loops. Call this in frequently-executed paths
    // instead of open-coding per-loop counters so probe cadence stays consistent as algorithms evolve.
    private void ProbeCancellation(int throttleMask = DefaultCancellationProbeMask)
    {
        if (_cancellationToken.IsCancellationRequested)
            _cancellationToken.ThrowIfCancellationRequested();

        if (_forceImmediateCancellationProbe)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        if (throttleMask <= 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        if ((++_cancellationProbeCounter & throttleMask) == 0)
            _cancellationToken.ThrowIfCancellationRequested();
    }

    // Escalates cancellation probing to immediate checks on every probe call. Used by the UI
    // Stop flow after a grace period when a soft cancellation has not completed yet.
    internal void EscalateCancellationChecks()
    {
        _forceImmediateCancellationProbe = true;
    }

    private void EnsurePhase1Solved()
    {
        if (_phase1Solved)
            return;

        // Phase 1: solve the exact minimum worst-case cost for every reachable state,
        // caching the optimal comparison-group pattern per state along the way.
        _recordRootIncumbents = true;
        try
        {
            _ = GetMinWorstCaseSteps(new ComparisonState(_n), _k);
        }
        finally
        {
            _recordRootIncumbents = false;
        }
        _phase1Solved = true;
    }

    private void EnsureCompactSolved()
    {
        if (_phase1bSolved && _compactPatternCacheReadyForMaterialization)
            return;

        // Optional phase 1b: among equally-optimal groups, choose the ones that minimize the
        // materialized subtree size (a proxy for displayed output states). The root budget is the
        // proven optimum for exact mode, or the constructive feasible upper bound U for feasible mode:
        // the materialized U threaded from the step phase when present (tightest, keeps the edge plan
        // no worse than step), else the sound-but-looser lean ConstructiveRootUpperBound.
        int rootBudget = _compactUsesFeasibleBudget
            ? (_feasibleRootBudgetActive >= 0
                ? _feasibleRootBudgetActive
                : (_feasibleRootBudget >= 0 ? _feasibleRootBudget : ConstructiveRootUpperBound()))
            : int.MaxValue;
        _compactRootCost = SolveCompact(new ComparisonState(_n), _k, rootBudget);
        _phase1bSolved = true;
        _compactPatternCacheReadyForMaterialization = _compactRootCost != int.MaxValue;
    }

    private void ResetPerBuildTransientState()
    {
        _stateIds.Clear();
        _expandedStates.Clear();
        _materializationDisplayPath.Clear();
        _nextStateId = 1;

        _session.ResetPerBuildTransientState();
        _searchedStates = 0;
        _lastReportedVisitedStatesCount = 0;
        _feasiblePhase2StartMs = -1;
        _feasiblePhaseSolved = false;
        _pendingStates = 0;
        _peakPendingStates = 0;
        _progressEstimateInitialized = false;
        _progressEstimateEma01 = 0.0;
        _lastProgressSampleElapsedMs = -1;
        _lastProgressSampleSearched = 0;
        _pendingCostEstimateInitialized = false;
        _pendingCostStatesPerPending = 0.0;
        _pendingCostConservativeStatesPerPending = 0.0;
        _pendingAtCostSample = -1;
        _searchedSinceCostSample = 0;
        _searchRateEstimateInitialized = false;
        _searchRateStatesPerMs = 0.0;
        _pendingZeroSettling = false;
        _pendingZeroSinceMs = 0;
        _pendingZeroSearchedAtStart = 0;
        _cancellationProbeCounter = 0;
        _forceImmediateCancellationProbe = false;
    }

}
