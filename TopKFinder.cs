using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;

partial class StrategyBuilder
{
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
    private readonly Dictionary<IntSequenceKey, int> _stateIds = new();
    private readonly Dictionary<IntSequenceKey, ExpandedStateSnapshot> _expandedStates = new();
    // Active display-key recursion path while materializing a GreedyTighten tree. Used to reject
    // local overrides whose outcomes would reference an ancestor display state.
    private readonly HashSet<IntSequenceKey> _materializationDisplayPath = new();
    private readonly HashSet<SearchStateKey> _visitedSearchStates = new();
    private readonly Dictionary<SearchStateKey, int> _minWorstCaseStepsCache = new();
    private readonly Dictionary<SearchStateKey, int> _lowerBoundStepsCache = new();
    // Iterative-deepening transposition memo: the best lower bound on a state's exact cost learned
    // from passes that failed to resolve it under their budget. Lets a later node/pass prune a state
    // immediately when this learned bound already exceeds the current budget.
    private readonly Dictionary<SearchStateKey, int> _searchLowerBoundCache = new();
    private readonly Dictionary<SearchStateKey, FeasibleTopSetInfo> _feasibleTopSetCache = new();
    private readonly Dictionary<SearchStateKey, BestGroupPattern> _bestGroupPatternCache = new();
    // Cross-instance canonical-key memo: maps a state's cheap raw structural fingerprint to its
    // expensive McKay canonical key. The same logical poset is reached via many search paths as
    // distinct ComparisonState instances (each with its own per-instance cache), so without this the
    // canonicalization is recomputed from scratch every time. Never cleared, so it accumulates across
    // the feasible/exact/compact phases of one builder. Sound because the raw fingerprint fully
    // determines the canonical key.
    private readonly Dictionary<RawStructureKey, IntSequenceKey> _canonicalKeyMemo = new();
    private readonly Stopwatch _progressStopwatch = Stopwatch.StartNew();
    private readonly List<SearchMilestone> _rootIncumbents = new();
    private int _nextStateId = 1;
    private int _searchedStates;
    private int _pendingStates;
    private int _peakPendingStates;
    private long _lastProgressReportMs = -ProgressReportIntervalMs;
    private int _lastReportedVisitedStatesCount = 0;
    private long _feasiblePhase2StartMs = -1;  // When BuildState recursion began in feasible stage
    private bool _feasiblePhaseSolved = false;  // Mark when feasible stage materialization completes
    private int _lowerBoundPrunes;
    private int _duplicateOutcomeSkips;
    private int _mergedOutcomeCollisions;
    private int _exactCacheHits;
    private int _lowerBoundCacheHits;
    private int _feasibleTopSetCacheHits;
    private int _bestGroupPatternCacheHits;
    private int _outcomesConstructed;
    private int _candidateGroupsEnumerated;
    private long _phase1Milliseconds;
    private long _phase1bMilliseconds;
    private long _phase2Milliseconds;
    // Set true only around the phase-1 iterative-deepening driver so root incumbents are recorded
    // for the progress UI; other callers (optimality-gap, compact) reuse the search silently.
    private bool _recordRootIncumbents;
    // First-top-level-entry latch for the single-pass exact search path (matches the pre-ID
    // algorithm): root incumbents are recorded only for the first (phase-1) search of a build.
    private bool _rootSearchInitialized;
    // Best proven lower bound on the root optimum (opt >= this). Lifted each failed iterative-
    // deepening pass; recorded only during the phase-1 root search. The L side of the squeeze.
    // Like the phase-1 incumbents and the _rootSearchInitialized latch, this is a product of the
    // once-only phase-1 solve (memoized by _phase1Solved) and is therefore NOT cleared by
    // ResetPerBuildTransientState; otherwise the later compact build would reset it to 0 and the
    // squeeze display would regress from "opt = N (proven)" back to "? <= opt <= ?".
    private int _rootProvenLowerBound;
    private bool _phase1Solved;
    private bool _phase1bSolved;
    private bool _compactPatternCacheReadyForMaterialization;
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
    // Outer-loop budget context for proof-tighten progress estimation (CompactFeasibleInCombinedRun
    // scope). Set at the start of RunGreedyPipeline's Phase A tighten loop; updated each iteration
    // so EstimateProgress can fold in the L < step < U range alongside the per-stage signal.
    // -1 when outside a proof-tighten loop.
    private int _proofTightenInitialBudget = -1;   // first budget tried (= U - 1)
    private int _proofTightenCurrentBudget = -1;   // budget currently being probed
    private int _proofTightenLowerBound = -1;       // proven lower bound L at loop start
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

    // The fixed default cap is a good BASE knob, but the actual candidate-generation surface is the
    // current state's active width times the chosen group size. When callers leave the default in
    // place, scale that base cap up by a small bounded multiplier on wider states to reduce needless
    // proof-tighten retries; any explicit non-default override remains exact.
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

    public StrategyPlan BuildStepProofStage()
    {
        _progressScope = _reportCombinedRunProgress
            ? ProgressScope.DefaultInCombinedRun
            : ProgressScope.DefaultStandalone;
        return BuildPlan(useCompactSelection: false);
    }

    // Transitional exact-stage entrypoint: build the exact tree once, project it to the search model,
    // and then project the public display plan back from that search tree. The solver itself is still
    // display-materialization based, but callers now observe the exact path as search -> display.
    public (SearchTree SearchTree, DisplayTree DisplayTree) BuildDisplayTreeAndExpandedSearch()
        => BuildExactSearchProjection();

    // Search-model entrypoint used by public callers; it now shares the same exact search projection
    // as the display entrypoint instead of re-running a separate bridge.
    public SearchStrategy BuildSearchTree()
        => BuildSearchTreeFromExactSolverState();

    public StrategyPlan RunExactPipeline(
        Action<StageResult>? onStageCompleted = null,
        Action<string>? onStageStart = null)
        => PublicPipelineOrchestrator.RunExactPipeline(this, onStageCompleted, onStageStart);

    public StrategyPlan BuildEdgeCompactStage()
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

    internal (SearchTree SearchTree, DisplayTree DisplayTree) BuildExactSearchProjection()
    {
        DisplayTree sourceDisplayTree = BuildStepProofStage();
        SearchTree searchTree = BuildSearchTreeFromSolverState();
        return (searchTree, sourceDisplayTree);
    }

    // Mainline-A seam: keep a dedicated exact-search core that does not depend on display
    // materialization, so search-first callers can remain independent from display projection.
    private SearchTree BuildSearchTreeFromExactSolverState()
    {
        ComparisonState.SetThreadCancellationToken(_cancellationToken);
        try
        {
            ResetPerBuildTransientState();
            EnsurePhase1Solved();
            _useCompact = false;
            return BuildSearchTreeFromSolverState();
        }
        finally
        {
            ComparisonState.SetThreadCancellationToken(default);
        }
    }

    // Transitional solver-sourced search builder: phase-1 group selection still comes from the
    // same exact caches, but the search tree is now materialized directly from solver state
    // recursion instead of mapping from an already-materialized display tree.
    private SearchTree BuildSearchTreeFromSolverState()
    {
        var expandedStates = new Dictionary<IntSequenceKey, ExpandedStateSnapshot>();
        var displayPath = new HashSet<IntSequenceKey>();
        SearchNode root = BuildSearchState(new ComparisonState(_n), 0, _k, 1, expandedStates, displayPath);
        return new SearchStrategy(
            _n,
            _m,
            _requestedK,
            _k,
            root);
    }

    private SearchNode BuildSearchState(
        ComparisonState state,
        ulong fixedTopMask,
        int remainingSlots,
        int step,
        Dictionary<IntSequenceKey, ExpandedStateSnapshot> expandedStates,
        HashSet<IntSequenceKey> displayPath)
    {
        ThrowIfCancellationRequested();
        NormalizeState(state, ref fixedTopMask, ref remainingSlots);

        IntSequenceKey displayKey = GetDisplayStateKey(state, fixedTopMask);
        int stateId = GetStateId(displayKey);

        if (remainingSlots == 0)
            return SearchNode.Terminal(stateId, ComparisonState.MaskToOrderedList(fixedTopMask));

        if (TryGetDeterminedTopSet(state, remainingSlots, out ulong determinedTopMask))
            return SearchNode.Terminal(stateId, ComparisonState.MaskToOrderedList(fixedTopMask | determinedTopMask));

        if (state.ActiveCount <= remainingSlots)
            return SearchNode.Terminal(stateId, ComparisonState.MaskToOrderedList(fixedTopMask | state.ActiveMask));

        var possibleCandidates = GetPossibleCandidates(state);
        if (state.ActiveCount <= _m)
            return SearchNode.Decision(stateId, step, possibleCandidates, Array.Empty<SearchBranch>());

        if (expandedStates.TryGetValue(displayKey, out ExpandedStateSnapshot snapshot))
        {
            IReadOnlyList<ItemRelabel>? relabeling =
                snapshot.State.TryBuildDisplayRelabeling(snapshot.FixedTopMask, state, fixedTopMask);
            return SearchNode.Reference(stateId, relabeling);
        }

        expandedStates.Add(displayKey, new ExpandedStateSnapshot(state.Clone(), fixedTopMask));
        if (!displayPath.Add(displayKey))
            throw new InvalidOperationException("Search materialization detected a recursive display-state expansion path.");

        try
        {
            SelectedComparisonGroup chosenGroup = ChooseGroup(state, fixedTopMask, remainingSlots, default);
            IReadOnlyList<SearchBranch> branches = BuildSearchBranches(
                state,
                fixedTopMask,
                remainingSlots,
                chosenGroup,
                step + 1,
                expandedStates,
                displayPath);
            return SearchNode.Decision(stateId, step, chosenGroup.Group, branches);
        }
        finally
        {
            displayPath.Remove(displayKey);
        }
    }

    private IReadOnlyList<SearchBranch> BuildSearchBranches(
        ComparisonState state,
        ulong fixedTopMask,
        int remainingSlots,
        SelectedComparisonGroup chosenGroup,
        int nextStep,
        Dictionary<IntSequenceKey, ExpandedStateSnapshot> expandedStates,
        HashSet<IntSequenceKey> displayPath)
    {
        return BuildSearchTransitionSpecs(state, fixedTopMask, remainingSlots, chosenGroup)
            .Select(spec => new SearchBranch(
                spec.OrderText,
                spec.Effect,
                BuildSearchState(
                    spec.NextState,
                    spec.NextFixedTopMask,
                    spec.NextRemainingSlots,
                    nextStep,
                    expandedStates,
                    displayPath)))
            .ToList();
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
    // this thread each time a downstream stage becomes available: once per successful tightening ceiling
    // ("proof-tighten≤N", carrying the smaller plan), once for the terminal ceiling that stops tightening
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
        _progressScope = _reportCombinedRunProgress
            ? ProgressScope.CompactFeasibleInCombinedRun
            : ProgressScope.DefaultStandalone;

        // The step ceiling U comes from the greedy feasible plan. Production callers (Program.cs /
        // MainForm.cs) build it first and reuse this builder, so _feasibleRootBudget is already set;
        // standalone callers (e.g. tests invoking RunGreedyPipeline directly) have not, so
        // establish it here. BuildGreedyFeasibleStage deliberately does not clear _feasibleRootBudget, so this
        // never double-builds when the caller already ran the step phase.
        if (_feasibleRootBudget < 0)
            BuildGreedyFeasibleStage();

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
                string stageName = FormatProofTightenStageName(budget);
                onStageStart?.Invoke(stageName);
                StageResult stage = BuildProofTightenStage(budget);
                onStageCompleted?.Invoke(stage);

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
        string edgeCompactStageName = FormatGreedyEdgeCompactStageName(bestStep);
        onStageStart?.Invoke(edgeCompactStageName);
        var edgeStopwatch = Stopwatch.StartNew();
        StrategyPlan finalPlan = BuildEdgeCompactPlanAtBudget(bestStep)
            .WithRootProvenLowerBound(_rootProvenLowerBound);
        edgeStopwatch.Stop();
        onStageCompleted?.Invoke(new StageResult(
            edgeCompactStageName, finalPlan, edgeStopwatch.Elapsed, StageOutcome.Completed));
        return finalPlan;
    }

    private static string FormatProofTightenStageName(int budget)
        => $"proof-tighten\u2264{budget}";

    internal const string ExactEdgeCompactStagePrefix = "exact-edge-compact@";
    internal const string GreedyEdgeCompactStagePrefix = "greedy-edge-compact@";

    internal static string FormatExactEdgeCompactStageName(int step)
        => $"{ExactEdgeCompactStagePrefix}{step}";

    internal static string FormatGreedyEdgeCompactStageName(int step)
        => $"{GreedyEdgeCompactStagePrefix}{step}";

    public StageResult BuildProofTightenStage(int budget)
    {
        _progressScope = _reportCombinedRunProgress
            ? ProgressScope.CompactFeasibleInCombinedRun
            : ProgressScope.DefaultStandalone;

        _compactFeasibilityOnly = true;
        try
        {
            string stageName = FormatProofTightenStageName(budget);
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
        ComparisonState.SetThreadCancellationToken(_cancellationToken);
        ResetPerBuildTransientState();
        ResetCompactState();
        _compactEnumerationCapped = false;
        _lastProbeEnumerationCapped = false;

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
            return new StrategyPlan(
                _n, _m, _requestedK, _k, root, stopwatch.Elapsed, CreateSearchStatistics(_compactRootCost),
                isFeasibleUpperBound: true);
        }
        finally
        {
            _feasibleRootBudgetActive = -1;
            ComparisonState.SetThreadCancellationToken(default);
        }
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
        ComparisonState.SetThreadCancellationToken(_cancellationToken);
        try
        {
            ResetPerBuildTransientState();
            var stopwatch = Stopwatch.StartNew();
            ReportProgress(force: true);

            _compactUsesFeasibleBudget = useFeasibleBudget;
            if (!useFeasibleBudget)
                EnsurePhase1Solved();
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
            return new StrategyPlan(
                _n,
                _m,
                _requestedK,
                _k,
                root,
                stopwatch.Elapsed,
                CreateSearchStatistics(searchTreeEdges),
                isFeasibleUpperBound: feasible);
        }
        finally
        {
            ComparisonState.SetThreadCancellationToken(default);
        }
    }

    private readonly record struct MaterializationContext(
        bool ForceFixedConstructiveSelection = false);

    private StrategyNode BuildState(
        ComparisonState state,
        ulong fixedTopMask,
        int remainingSlots,
        int step,
        MaterializationContext context = default)
    {
        ThrowIfCancellationRequested();
        ThrottledReportProgressDuringFeasibleBuild();
        NormalizeState(state, ref fixedTopMask, ref remainingSlots);
        ObserveSearchState(state, remainingSlots);

        IntSequenceKey displayKey = GetDisplayStateKey(state, fixedTopMask);
        int stateId = GetStateId(displayKey);

        if (remainingSlots == 0)
            return StrategyNode.Terminal(stateId, ComparisonState.MaskToOrderedList(fixedTopMask));

        if (TryGetDeterminedTopSet(state, remainingSlots, out ulong determinedTopMask))
            return StrategyNode.Terminal(stateId, ComparisonState.MaskToOrderedList(fixedTopMask | determinedTopMask));

        if (state.ActiveCount <= remainingSlots)
            return StrategyNode.Terminal(stateId, ComparisonState.MaskToOrderedList(fixedTopMask | state.ActiveMask));

        var possibleCandidates = GetPossibleCandidates(state);
        if (state.ActiveCount <= _m)
        {
            return StrategyNode.Decision(
                stateId,
                step,
                possibleCandidates,
                Array.Empty<StrategyBranch>(),
                new FinalChoiceSummary(
                    ComparisonState.MaskToOrderedList(fixedTopMask),
                    possibleCandidates,
                    remainingSlots));
        }

        if (_expandedStates.TryGetValue(displayKey, out ExpandedStateSnapshot snapshot))
        {
            IReadOnlyList<ItemRelabel>? relabeling =
                snapshot.State.TryBuildDisplayRelabeling(snapshot.FixedTopMask, state, fixedTopMask);
            return StrategyNode.Reference(stateId, relabeling);
        }

        _expandedStates.Add(displayKey, new ExpandedStateSnapshot(state.Clone(), fixedTopMask));

        bool trackingDisplayPath = _useGreedyTightenSelection;
        if (trackingDisplayPath && !_materializationDisplayPath.Add(displayKey))
        {
            throw new InvalidOperationException(
                "GreedyTighten materialization detected a recursive display-state expansion path.");
        }

        try
        {
            SelectedComparisonGroup chosenGroup = ChooseGroup(
                state,
                fixedTopMask,
                remainingSlots,
                context);
            var branches = BuildBranches(
                state,
                fixedTopMask,
                remainingSlots,
                chosenGroup,
                step + 1,
                context);
            return StrategyNode.Decision(stateId, step, chosenGroup.Group, branches);
        }
        finally
        {
            if (trackingDisplayPath)
                _materializationDisplayPath.Remove(displayKey);
        }
    }

    private List<int> GetPossibleCandidates(ComparisonState state)
    {
        return state.GetActiveItemsOrdered();
    }

    private SelectedComparisonGroup ChooseGroup(
        ComparisonState state,
        ulong fixedTopMask,
        int remainingSlots,
        MaterializationContext context)
    {
        ThrowIfCancellationRequested();

        // The constructive feasible plan computes its group directly from the current partial order
        // (cheap, O(m*active^2)), so unlike greedy/compact it needs no precomputed pattern cache.
        if (context.ForceFixedConstructiveSelection)
        {
            List<int> constructiveGroup = ChooseConstructiveGroup(
                state,
                remainingSlots,
                forceFixedCandidateSelection: true);
            return new SelectedComparisonGroup(
                constructiveGroup,
                BuildMergedComparisonOutcomes(state, fixedTopMask, remainingSlots, constructiveGroup));
        }

        // GreedyTighten materializes its tightened tree via the committed override map, falling back to
        // the same constructive selector for un-edited states (so an empty map == greedy-feasible).
        if (_useGreedyTightenSelection)
        {
            SearchStateKey key = GetSearchStateKey(state, remainingSlots);
            List<int> tightenGroup = CurrentGreedyTightenGroup(state, remainingSlots, key);
            if (!GroupAvoidsDisplayBackEdge(state, fixedTopMask, remainingSlots, tightenGroup))
            {
                // Drop malformed local edits whose outcomes would reference an ancestor display state.
                _greedyTightenOverrides.Remove(key);
                _greedyTightenOverrideAnchors.Remove(key);
                tightenGroup = ChooseConstructiveGroup(state, remainingSlots);
                if (!GroupAvoidsDisplayBackEdge(state, fixedTopMask, remainingSlots, tightenGroup))
                {
                    throw new InvalidOperationException(
                        "GreedyTighten materialization found no display-progress group at the current state.");
                }
            }

            return new SelectedComparisonGroup(
                tightenGroup,
                BuildMergedComparisonOutcomes(state, fixedTopMask, remainingSlots, tightenGroup));
        }

        var candidates = state.GetActiveItemsOrdered();
        SearchStateKey currentKey = GetSearchStateKey(state, remainingSlots);

        // Phase 1 solves the optimal worst-case for every reachable state and caches the
        // chosen comparison-group pattern, so phase 2 always finds a populated entry here.
        // The compact PoC overrides the choice with its size-minimizing pattern when enabled.
        BestGroupPattern cachedPattern;
        if (_useCompact)
        {
            if (!_compactPatternCacheReadyForMaterialization)
            {
                throw new InvalidOperationException(
                    "Compact phase 1b must finish with a complete group-pattern cache before phase 2 materialization.");
            }

            if (!_compactGroupPatternCache.TryGetValue(currentKey, out BestGroupPattern compactPattern))
            {
                throw new InvalidOperationException(
                    "Compact phase 1b must populate the group-pattern cache for every state materialized in phase 2.");
            }

            cachedPattern = compactPattern;
        }
        else if (!_bestGroupPatternCache.TryGetValue(currentKey, out cachedPattern))
        {
            throw new InvalidOperationException(
                "Phase 1 must populate the best-group pattern cache for every state materialized in phase 2.");
        }

        int[]? colorSignature = cachedPattern.ColorSignature;
        int[]? activeColors = colorSignature is null ? null : state.GetActiveItemColors();

        foreach (var group in EnumerateCombinations(candidates, cachedPattern.GroupSize))
        {
            if (activeColors is not null && !GroupMatchesColorSignature(activeColors, group, colorSignature!))
                continue;

            if (GetGroupPattern(state, group) == cachedPattern.Pattern)
            {
                _bestGroupPatternCacheHits++;
                return new SelectedComparisonGroup(group, BuildMergedComparisonOutcomes(state, fixedTopMask, remainingSlots, group));
            }
        }

        throw new InvalidOperationException(
            "Cached best-group pattern did not match any candidate combination in the current state.");
    }

    private IReadOnlyList<MergedBranch> BuildMergedComparisonOutcomes(ComparisonState state, ulong fixedTopMask, int remainingSlots, IReadOnlyList<int> group)
    {
        return VisitComparisonOutcomes(
            state,
            fixedTopMask,
            remainingSlots,
            group,
            currentKey: null,
            collectMergedBranches: true,
            onUsefulOutcome: _ => true).MergedBranches;
    }

    // True iff every materialized outcome of `group` stays off the active display recursion path.
    // A child whose display key is already on the path would become a reference back-edge to an
    // ancestor (including self), i.e. a malformed reference cycle.
    private bool GroupAvoidsDisplayBackEdge(
        ComparisonState state,
        ulong fixedTopMask,
        int remainingSlots,
        IReadOnlyList<int> group)
    {
        bool anyOutcome = false;
        foreach (ComparisonOutcome outcome in EnumerateDisplayOutcomes(state, remainingSlots, group))
        {
            anyOutcome = true;
            IntSequenceKey nextDisplayKey = GetDisplayStateKey(
                outcome.NextState,
                fixedTopMask | outcome.AddedFixedTopMask);
            if (_materializationDisplayPath.Contains(nextDisplayKey))
                return false;
        }

        return anyOutcome;
    }

    private static IntSequenceKey GetGroupPattern(ComparisonState state, IReadOnlyList<int> group)
    {
        ulong mask = 0;
        for (int i = 0; i < group.Count; i++)
            mask |= 1UL << group[i];
        return state.GetGroupCanonicalKey(mask);
    }

    // Builds a BestGroupPattern carrying both the canonical group pattern and a cheap color
    // pre-filter signature (the sorted multiset of the group's per-item active colors). ChooseGroup
    // uses the signature to skip the expensive canonical-key check for groups that cannot match.
    private static BestGroupPattern MakeGroupPattern(ComparisonState state, IReadOnlyList<int> group)
    {
        int[] colors = state.GetActiveItemColors();
        return new BestGroupPattern(group.Count, GetGroupPattern(state, group), BuildSortedColorSignature(colors, group));
    }

    private static int[] BuildSortedColorSignature(int[] colors, IReadOnlyList<int> group)
    {
        var signature = new int[group.Count];
        for (int i = 0; i < group.Count; i++)
            signature[i] = colors[group[i]];
        Array.Sort(signature);
        return signature;
    }

    // Necessary condition for GetGroupPattern(state, group) == target pattern: the group's sorted
    // color multiset must equal the cached signature. Allocation-free (group size is tiny).
    private static bool GroupMatchesColorSignature(int[] colors, IReadOnlyList<int> group, int[] target)
    {
        int count = group.Count;
        if (target.Length != count)
            return false;

        Span<int> signature = stackalloc int[count];
        for (int i = 0; i < count; i++)
            signature[i] = colors[group[i]];

        for (int i = 1; i < count; i++)
        {
            int value = signature[i];
            int j = i - 1;
            while (j >= 0 && signature[j] > value)
            {
                signature[j + 1] = signature[j];
                j--;
            }

            signature[j + 1] = value;
        }

        for (int i = 0; i < count; i++)
            if (signature[i] != target[i])
                return false;

        return true;
    }

    private IEnumerable<List<int>> EnumerateDistinctGroups(
        ComparisonState state,
        IReadOnlyList<int> candidates,
        int groupSize,
        int generationCap = int.MaxValue)
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
        GenerateClassRepresentatives(state, classes, suffixCapacity, 0, groupSize, prefix, collected, generationCap);

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
            ProbeCancellation();
            IntSequenceKey cheap = CheapGroupSignature(labels, group);
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
            ProbeCancellation();
            if (bucket.Count == 1)
            {
                ordered.Add(bucket[0]);
                continue;
            }

            var representatives = new Dictionary<IntSequenceKey, List<int>>();
            foreach (List<int> group in bucket)
            {
                ProbeCancellation();
                IntSequenceKey pattern = GetGroupPattern(state, group);
                if (!representatives.TryGetValue(pattern, out List<int>? existing) ||
                    CompareGroupsLexicographically(group, existing) < 0)
                {
                    representatives[pattern] = group;
                }
            }

            ordered.AddRange(representatives.Values);
        }

        ordered.Sort(CompareGroupsLexicographically);
        return ordered;
    }

    private static IntSequenceKey CheapGroupSignature(int[] labels, IReadOnlyList<int> group)
    {
        var values = new int[group.Count];
        for (int i = 0; i < group.Count; i++)
            values[i] = labels[group[i]];
        Array.Sort(values);
        return new IntSequenceKey(values);
    }

    private void GenerateClassRepresentatives(
        ComparisonState state,
        List<List<int>> classes,
        int[] suffixCapacity,
        int classIndex,
        int remaining,
        List<int> prefix,
        List<List<int>> collected,
        int generationCap = int.MaxValue)
    {
        ProbeCancellation();

        if (collected.Count >= generationCap)
            return;

        if (remaining == 0)
        {
            ThrowIfCancellationRequested();
            _candidateGroupsEnumerated++;
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
                state, classes, suffixCapacity, classIndex + 1, remaining - take, prefix, collected, generationCap);

            prefix.RemoveRange(prefix.Count - take, take);

            if (collected.Count >= generationCap)
            {
                // Stopped short of trying the remaining (larger-`take`) siblings at this level because
                // the cap filled up: the enumeration is genuinely truncated. Flag it so a probe that
                // concludes infeasible under a finite cap is reported as incomplete, not a proof.
                if (generationCap != int.MaxValue)
                    _compactEnumerationCapped = true;
                return;
            }
        }
    }

    private static int CompareGroupsLexicographically(List<int> a, List<int> b)
    {
        int min = Math.Min(a.Count, b.Count);
        for (int i = 0; i < min; i++)
        {
            int cmp = a[i].CompareTo(b[i]);
            if (cmp != 0)
                return cmp;
        }

        return a.Count.CompareTo(b.Count);
    }

    private IEnumerable<List<int>> EnumeratePrioritizedGroups(
        ComparisonState state,
        int remainingSlots,
        IReadOnlyList<int> candidates,
        int groupSize)
    {
        var scoredGroups = new List<(List<int> Group, HeuristicGroupScore Score)>();
        foreach (var group in EnumerateDistinctGroups(state, candidates, groupSize))
        {
            ThrowIfCancellationRequested();
            scoredGroups.Add((group, BuildHeuristicGroupScore(state, remainingSlots, group)));
        }

        foreach (var entry in scoredGroups.OrderByDescending(entry => entry.Score))
            yield return entry.Group;
    }

    private static HeuristicGroupScore BuildHeuristicGroupScore(ComparisonState state, int remainingSlots, IReadOnlyList<int> group)
    {
        int guaranteedTopHits = 0;
        for (int i = 0; i < group.Count; i++)
        {
            if (state.ActiveCount - 1 - state.GetDescendantCount(group[i]) <= remainingSlots - 1)
                guaranteedTopHits++;
        }

        return new HeuristicGroupScore(
            guaranteedTopHits,
            CountFreshItems(state, group),
            CalculateUnrelatedScore(state, group),
            CountUnresolvedPairs(state, group),
            group.Count);
    }

    private static int CountFreshItems(ComparisonState state, IReadOnlyList<int> group)
    {
        int count = 0;
        for (int i = 0; i < group.Count; i++)
        {
            int item = group[i];
            if (state.GetAncestorCount(item) == 0 && state.GetDescendantCount(item) == 0)
                count++;
        }

        return count;
    }

    private static int CalculateUnrelatedScore(ComparisonState state, IReadOnlyList<int> group)
    {
        int sum = 0;
        for (int i = 0; i < group.Count; i++)
        {
            int item = group[i];
            sum += state.GetAncestorCount(item) + state.GetDescendantCount(item);
        }

        return -sum;
    }

    private static int CountUnresolvedPairs(ComparisonState state, IReadOnlyList<int> group)
    {
        int count = 0;
        for (int i = 0; i < group.Count - 1; i++)
        {
            for (int j = i + 1; j < group.Count; j++)
            {
                int a = group[i];
                int b = group[j];
                if (!state.HasAncestor(a, b) && !state.HasAncestor(b, a))
                    count++;
            }
        }

        return count;
    }

    private IEnumerable<List<int>> EnumerateCombinations(IReadOnlyList<int> items, int count)
    {
        ProbeCancellation(0);
        var current = new List<int>(count);
        foreach (var combination in EnumerateCombinations(items, count, 0, current))
            yield return combination;
    }

    private IEnumerable<List<int>> EnumerateCombinations(
        IReadOnlyList<int> items,
        int count,
        int start,
        List<int> current)
    {
        ProbeCancellation(0);
        if (current.Count == count)
        {
            yield return new List<int>(current);
            yield break;
        }

        for (int i = start; i <= items.Count - (count - current.Count); i++)
        {
            ProbeCancellation(0);
            current.Add(items[i]);
            foreach (var combination in EnumerateCombinations(items, count, i + 1, current))
                yield return combination;
            current.RemoveAt(current.Count - 1);
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

    private SearchStateKey GetSearchStateKey(ComparisonState state, int remainingSlots)
    {
        return new SearchStateKey(remainingSlots, GetCanonicalKeyMemoized(state));
    }

    private IntSequenceKey GetCanonicalKeyMemoized(ComparisonState state)
    {
        RawStructureKey rawKey = state.GetRawStructureKey();
        if (_canonicalKeyMemo.TryGetValue(rawKey, out IntSequenceKey cached))
            return cached;

        IntSequenceKey canonical = state.GetCanonicalKey();
        _canonicalKeyMemo[rawKey] = canonical;
        return canonical;
    }

    private IntSequenceKey GetDisplayStateKey(ComparisonState state, ulong fixedTopMask)
    {
        return state.GetDisplayCanonicalKey(fixedTopMask);
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
        if (_progressCallback is null)
            return;

        _searchedStates = _visitedSearchStates.Count;
        long elapsedMs = _progressStopwatch.ElapsedMilliseconds;
        if (!force && elapsedMs - _lastProgressReportMs < ProgressReportIntervalMs)
            return;

        _lastProgressReportMs = elapsedMs;
            double localProgress = EstimateProgress(elapsedMs);
            double estimatedProgress01 = MapToReportedProgress(localProgress);
        _progressCallback(new SearchProgressSnapshot(
            elapsedMs,
            _searchedStates,
            _pendingStates,
            _peakPendingStates,
            _stateIds.Count,
            _rootIncumbents.Count == 0 ? null : _rootIncumbents[^1],
            _rootIncumbents.Count,
            _lowerBoundPrunes,
            _duplicateOutcomeSkips,
            _mergedOutcomeCollisions,
            _exactCacheHits,
            _lowerBoundCacheHits,
            _feasibleTopSetCacheHits,
            _bestGroupPatternCacheHits,
            _outcomesConstructed,
            _candidateGroupsEnumerated,
            _lowerBoundStepsCache.Count,
            _feasibleTopSetCache.Count,
            _compactStatesSolved,
            _compactGroupsEnumerated,
            _compactStepOptimalGroups,
            _feasibleCompactStateEstimate,
            estimatedProgress01,
            _rootProvenLowerBound));
    }

    // For feasible-stage incremental progress: periodically check visited-state count and report
    // if enough time has passed since the last report. Called from BuildState to provide smooth
    // progress updates during the possibly-slow recursive materialization of the greedy tree.
    internal void ThrottledReportProgressDuringFeasibleBuild()
    {
        if (_progressCallback is null || _progressScope != ProgressScope.FeasibleInCombinedRun)
            return;

        long elapsedMs = _progressStopwatch.ElapsedMilliseconds;
        if (elapsedMs - _lastProgressReportMs < ProgressReportIntervalMs)
            return;

        // Only report if the visited-state count has actually changed, to avoid redundant calls.
        int currentVisitedCount = _visitedSearchStates.Count;
        if (currentVisitedCount <= _lastReportedVisitedStatesCount)
            return;

        _lastReportedVisitedStatesCount = currentVisitedCount;
        _searchedStates = currentVisitedCount;
        _lastProgressReportMs = elapsedMs;

        double localProgress = EstimateProgress(elapsedMs);
        double estimatedProgress01 = MapToReportedProgress(localProgress);
        _progressCallback(new SearchProgressSnapshot(
            elapsedMs,
            _searchedStates,
            _pendingStates,
            _peakPendingStates,
            _stateIds.Count,
            _rootIncumbents.Count == 0 ? null : _rootIncumbents[^1],
            _rootIncumbents.Count,
            _lowerBoundPrunes,
            _duplicateOutcomeSkips,
            _mergedOutcomeCollisions,
            _exactCacheHits,
            _lowerBoundCacheHits,
            _feasibleTopSetCacheHits,
            _bestGroupPatternCacheHits,
            _outcomesConstructed,
            _candidateGroupsEnumerated,
            _lowerBoundStepsCache.Count,
            _feasibleTopSetCache.Count,
            _compactStatesSolved,
            _compactGroupsEnumerated,
            _compactStepOptimalGroups,
            _feasibleCompactStateEstimate,
            estimatedProgress01,
            _rootProvenLowerBound));
    }

    private double MapToReportedProgress(double localProgress01)
    {
        if (!_reportCombinedRunProgress)
            return localProgress01;

        // The combined-run bar is split into two visible stages: step then edge. The ratio differs
        // per mode. Exact mode: step (exact solve) gets 60%, edge (compact) gets 40%. Greedy mode:
        // step (feasible bound) gets 10%, so edge (feasible-compact) gets the remaining 90%.
        (double progressBase, double progressSpan) = _progressScope switch
        {
            ProgressScope.FeasibleInCombinedRun => (0.0, ProgressTuning.CombinedRun.FeasibleSpanPercent / 100.0),
            ProgressScope.DefaultInCombinedRun => (0.0, ProgressTuning.CombinedRun.DefaultSpanPercent / 100.0),
            ProgressScope.CompactPrimaryInCombinedRun => (ProgressTuning.CombinedRun.CompactPrimaryBasePercent / 100.0, ProgressTuning.CombinedRun.CompactPrimarySpanPercent / 100.0),
            ProgressScope.CompactFeasibleInCombinedRun => (ProgressTuning.CombinedRun.CompactFeasibleBasePercent / 100.0, ProgressTuning.CombinedRun.CompactFeasibleSpanPercent / 100.0),
            _ => (0.0, 1.0),
        };

        // All phases now report incremental progress based on their own work signal:
        // - FeasibleInCombinedRun: grows from 0% to 100% of its 10% band as states are visited
        // - DefaultInCombinedRun: grows from 0% to 100% of its 60% band as states are searched
        // - CompactFeasibleInCombinedRun: grows from 0% to 100% of its 90% band as states are solved
        // This ensures the display never looks stuck; progress monotonically increases whenever work happens.
        double localFraction = Math.Clamp(localProgress01, 0.0, 1.0);
        return Math.Clamp(progressBase + (localFraction * progressSpan), 0.0, 1.0);
    }

    private double EstimateProgress(long elapsedMs)
    {
        // Greedy-mode feasible (step) phase: use a self-correcting asymptote like the edge phase.
        // progress = elapsed / (elapsed + remaining_estimate), which smoothly grows from 0% toward 100%
        // without plateauing. We conservatively assume at least 500ms of remaining work ahead.
        if (_progressScope == ProgressScope.FeasibleInCombinedRun)
        {
            if (_feasiblePhaseSolved)
                return 1.0;

            if (_feasiblePhase2StartMs < 0)
                return 0.0;

            long elapsedInPhase2 = elapsedMs - _feasiblePhase2StartMs;
            if (elapsedInPhase2 <= 0)
                return 0.0;

            // Use the same asymptote as the edge phase: estimate remaining work conservatively.
            // The remaining estimate (min 500ms) ensures the bar rises continuously.
            long remainingEstimate = Math.Max(
                ProgressTuning.Asymptote.MinimumRemainingMs,
                ProgressTuning.Asymptote.InitialRemainingMs - elapsedInPhase2 / ProgressTuning.Asymptote.ElapsedDivisor);
            double fraction = elapsedInPhase2 / (double)(elapsedInPhase2 + remainingEstimate);
            return Math.Min(fraction, ProgressTuning.Asymptote.FeasibleSoftCap);
        }

        // Greedy-mode edge phase: the compact solve has no pending/searched signal (those counters are
        // reset and never touched here), so drive progress off _compactStatesSolved. Once the solve
        // finishes (_phase1bSolved) the remaining phase-2 materialization is effectively instant, so
        // report a full local fraction. MapToReportedProgress bands this into the edge stage's
        // 10%..100% slice for the displayed progress.
        if (_progressScope == ProgressScope.CompactFeasibleInCombinedRun)
        {
            if (_phase1bSolved)
                return 1.0;

            // Per-stage fraction: fraction = solved / (solved + scale) asymptote.
            // Falls back to 0 when the scale anchor is unavailable (standalone edge with no prior step).
            double stageFraction = 0.0;
            if (_feasibleCompactStateEstimate > 0)
            {
                double scale = _feasibleCompactStateEstimate;
                stageFraction = _compactStatesSolved / (_compactStatesSolved + scale);
                stageFraction = Math.Min(stageFraction, ProgressTuning.Asymptote.CompactFeasibleSoftCap);
            }

            // Blend with the outer L < step < U loop structure: in the worst case every budget level
            // from U-1 down to L is tried one by one, for totalRange = (U-1) - L + 1 = U - L stages.
            // completedStages is the number of budget levels already resolved; each successful tighten
            // can jump by more than 1, so the bar leaps forward whenever the step drops sharply.
            if (_proofTightenInitialBudget >= 0 && _proofTightenCurrentBudget >= 0)
            {
                int totalRange = _proofTightenInitialBudget - _proofTightenLowerBound + 1;
                if (totalRange > 0)
                {
                    int completedStages = _proofTightenInitialBudget - _proofTightenCurrentBudget;
                    double rawCombined = (completedStages + stageFraction) / totalRange;
                    rawCombined = Math.Clamp(rawCombined, 0.0, ProgressTuning.Asymptote.CompactFeasibleSoftCap);

                    // Slight smoothing for budget-boundary jumps: preserve trend but make abrupt
                    // "next proof ceiling" transitions visually less hard.
                    if (!_proofTightenProgressEmaInitialized)
                    {
                        _proofTightenProgressEmaInitialized = true;
                        _proofTightenProgressEma01 = rawCombined;
                    }
                    else
                    {
                        _proofTightenProgressEma01 +=
                            ProgressTuning.Ema.ProofTightenCombinedProgressAlpha *
                            (rawCombined - _proofTightenProgressEma01);
                    }

                    return _proofTightenProgressEma01;
                }
            }

            return stageFraction;
        }

        if (_searchedStates <= 0)
            return 0.0;

        if (_lastProgressSampleElapsedMs >= 0)
        {
            int deltaSearched = Math.Max(0, _searchedStates - _lastProgressSampleSearched);
            long deltaElapsedMs = Math.Max(0, elapsedMs - _lastProgressSampleElapsedMs);
            if (deltaElapsedMs > 0 && deltaSearched > 0)
            {
                double observedSearchRate = deltaSearched / (double)deltaElapsedMs;
                if (!_searchRateEstimateInitialized)
                {
                    _searchRateEstimateInitialized = true;
                    _searchRateStatesPerMs = observedSearchRate;
                }
                else
                {
                    _searchRateStatesPerMs += ProgressTuning.Ema.SearchRateAlpha * (observedSearchRate - _searchRateStatesPerMs);
                }
            }

            if (_pendingAtCostSample < 0)
                _pendingAtCostSample = _pendingStates;

            _searchedSinceCostSample += deltaSearched;

            int consumedPending = _pendingAtCostSample - _pendingStates;
            if (consumedPending > 0 && _searchedSinceCostSample > 0)
            {
                // Download-like adaptive throughput: estimate how many searched states are
                // needed to consume one pending state, then update continuously.
                double observedCostPerPending = _searchedSinceCostSample / (double)consumedPending;
                if (!_pendingCostEstimateInitialized)
                {
                    _pendingCostEstimateInitialized = true;
                    _pendingCostStatesPerPending = observedCostPerPending;
                    _pendingCostConservativeStatesPerPending = observedCostPerPending;
                }
                else
                {
                    _pendingCostStatesPerPending += ProgressTuning.Ema.PendingCostAlpha * (observedCostPerPending - _pendingCostStatesPerPending);

                    double conservativeAlpha =
                        observedCostPerPending >= _pendingCostConservativeStatesPerPending
                            ? ProgressTuning.Ema.PendingCostConservativeRiseAlpha
                            : ProgressTuning.Ema.PendingCostConservativeFallAlpha;
                    _pendingCostConservativeStatesPerPending +=
                        conservativeAlpha * (observedCostPerPending - _pendingCostConservativeStatesPerPending);
                }

                _searchedSinceCostSample = 0;
                _pendingAtCostSample = _pendingStates;
            }
            else if (_pendingStates > _pendingAtCostSample)
            {
                _pendingAtCostSample = _pendingStates;
            }

            if (_pendingStates > 0 && _searchedSinceCostSample > 0 && _pendingCostEstimateInitialized)
            {
                double noDrainFloor = _searchedSinceCostSample / (double)_pendingStates;
                _pendingCostStatesPerPending = Math.Max(_pendingCostStatesPerPending, noDrainFloor);
                _pendingCostConservativeStatesPerPending = Math.Max(_pendingCostConservativeStatesPerPending, noDrainFloor);
            }
        }

        _lastProgressSampleElapsedMs = elapsedMs;
        _lastProgressSampleSearched = _searchedStates;

        if (!_pendingCostEstimateInitialized)
        {
            _pendingCostEstimateInitialized = true;
            _pendingCostStatesPerPending = _pendingStates > 0
                ? Math.Max(ProgressTuning.Pending.CostBootstrapFloor, _searchedStates / (double)_pendingStates)
                : ProgressTuning.Pending.CostBootstrapFloor;
            _pendingCostConservativeStatesPerPending = _pendingCostStatesPerPending;
        }

        bool isDefaultScope =
            _progressScope is ProgressScope.DefaultStandalone or ProgressScope.DefaultInCombinedRun;
        double costPerPending = Math.Max(1.0, _pendingCostStatesPerPending);
        if (isDefaultScope)
            costPerPending = Math.Max(costPerPending, _pendingCostConservativeStatesPerPending);

        int effectivePending = _pendingStates;
        if (_pendingStates == 0)
        {
            if (!_pendingZeroSettling)
            {
                _pendingZeroSettling = true;
                _pendingZeroSinceMs = elapsedMs;
                _pendingZeroSearchedAtStart = _searchedStates;
            }

            bool zeroSettled =
                elapsedMs - _pendingZeroSinceMs >= ProgressTuning.Pending.ZeroSettlingWindowMs &&
                _searchedStates == _pendingZeroSearchedAtStart;
            if (zeroSettled)
            {
                _progressEstimateInitialized = true;
                _progressEstimateEma01 = 1.0;
                return 1.0;
            }

            // Avoid instant "100%" spikes when pending briefly touches zero mid-search.
            effectivePending = 1;
        }
        else
        {
            _pendingZeroSettling = false;
        }

        if (isDefaultScope && effectivePending <= ProgressTuning.Pending.TailThreshold)
        {
            // In default phase, tiny pending counts are often heavy tails rather than near-finish.
            // Apply a conservative inflation so progress does not pin near 100% too early.
            double inflation = effectivePending switch
            {
                1 => ProgressTuning.Pending.TailInflationOnePending,
                2 => ProgressTuning.Pending.TailInflationTwoPending,
                _ => ProgressTuning.Pending.TailInflationThreePending,
            };
            costPerPending *= inflation;
        }

        double estimatedRemainingSearchStates = effectivePending * costPerPending;
        double estimatedTotal = _searchedStates + estimatedRemainingSearchStates;
        if (estimatedTotal <= 0)
            return 0.0;

        double rawProgress = Math.Clamp(_searchedStates / estimatedTotal, 0.0, 1.0);
        if (effectivePending > 0)
            rawProgress = Math.Min(rawProgress, ProgressTuning.Asymptote.RawProgressSoftCapWithPending);

        if (!_progressEstimateInitialized)
        {
            _progressEstimateInitialized = true;
            _progressEstimateEma01 = rawProgress;
        }
        else
        {
            double alpha = rawProgress >= _progressEstimateEma01
                ? ProgressTuning.Ema.ProgressRiseAlpha
                : ProgressTuning.Ema.ProgressFallAlpha;
            _progressEstimateEma01 += alpha * (rawProgress - _progressEstimateEma01);
        }

        double progress = Math.Clamp(_progressEstimateEma01, 0.0, 1.0);
        return progress;
    }

    private void ObserveSearchState(ComparisonState state, int remainingSlots)
    {
        _visitedSearchStates.Add(GetSearchStateKey(state, remainingSlots));
    }

    private void RecordRootIncumbent(int bestWorstCaseSteps, IReadOnlyList<int> group)
    {
        _searchedStates = _visitedSearchStates.Count;
        _rootIncumbents.Add(new SearchMilestone(
            bestWorstCaseSteps,
            $"sort({StrategyTextRenderer.FormatSet(group)})",
            _progressStopwatch.ElapsedMilliseconds,
            _searchedStates,
            _pendingStates,
            _peakPendingStates,
            _stateIds.Count,
            _lowerBoundPrunes));
        ReportProgress(force: true);
    }

    // Monotonically lifts the root proven lower bound (the L side of the squeeze). Called only
    // during the phase-1 root search; ignores non-increasing values so it stays monotone even
    // though the single-pass path reports the analytic bound before the exact result.
    private void RecordRootProvenLowerBound(int provenLowerBound)
    {
        if (provenLowerBound <= _rootProvenLowerBound)
            return;
        _rootProvenLowerBound = provenLowerBound;
        ReportProgress(force: true);
    }
    private void ThrowIfCancellationRequested()
    {
        _cancellationToken.ThrowIfCancellationRequested();
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

        _visitedSearchStates.Clear();
        _searchedStates = 0;
        _lastReportedVisitedStatesCount = 0;
        _feasiblePhase2StartMs = -1;
        _feasiblePhaseSolved = false;
        _pendingStates = 0;
        _peakPendingStates = 0;

        _lowerBoundPrunes = 0;
        _duplicateOutcomeSkips = 0;
        _mergedOutcomeCollisions = 0;
        _exactCacheHits = 0;
        _lowerBoundCacheHits = 0;
        _feasibleTopSetCacheHits = 0;
        _bestGroupPatternCacheHits = 0;
        _greedyScoreLowerBoundCacheReuseHits = 0;
        _outcomesConstructed = 0;
        _candidateGroupsEnumerated = 0;
        _compactStatesSolved = 0;
        _compactGroupsEnumerated = 0;
        _compactStepOptimalGroups = 0;
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

    private sealed class SelectedComparisonGroup
    {
        public SelectedComparisonGroup(IReadOnlyList<int> group, IReadOnlyList<MergedBranch> branches)
        {
            Group = group;
            Branches = branches;
        }

        public IReadOnlyList<int> Group { get; }
        public IReadOnlyList<MergedBranch> Branches { get; }
    }

    private readonly struct ExpandedStateSnapshot
    {
        public ExpandedStateSnapshot(ComparisonState state, ulong fixedTopMask)
        {
            State = state;
            FixedTopMask = fixedTopMask;
        }

        public ComparisonState State { get; }
        public ulong FixedTopMask { get; }
    }

    private sealed class OutcomeTraversalSummary
    {
        public OutcomeTraversalSummary(
            IReadOnlyList<MergedBranch> mergedBranches,
            bool isUseful)
        {
            MergedBranches = mergedBranches;
            IsUseful = isUseful;
        }

        public IReadOnlyList<MergedBranch> MergedBranches { get; }
        public bool IsUseful { get; }
    }

    private readonly record struct HeuristicGroupScore(
        int GuaranteedTopHits,
        int FreshItems,
        int UnrelatedScore,
        int UnresolvedPairs,
        int GroupSize) : IComparable<HeuristicGroupScore>
    {
        // Among groups that achieve the optimal worst-case (the solver only ever caches an
        // optimal group), prefer the most independent/symmetric comparison: more fresh items
        // and fewer existing order relations. This keeps the worst-case step count optimal
        // while producing smaller, more symmetric, and easier-to-verify strategy trees.
        public int CompareTo(HeuristicGroupScore other)
        {
            int result = FreshItems.CompareTo(other.FreshItems);
            if (result != 0)
                return result;

            result = UnrelatedScore.CompareTo(other.UnrelatedScore);
            if (result != 0)
                return result;

            result = GuaranteedTopHits.CompareTo(other.GuaranteedTopHits);
            if (result != 0)
                return result;

            result = UnresolvedPairs.CompareTo(other.UnresolvedPairs);
            if (result != 0)
                return result;

            return GroupSize.CompareTo(other.GroupSize);
        }
    }

    private enum ProgressScope
    {
        DefaultStandalone = 0,
        DefaultInCombinedRun = 1,
        CompactPrimaryInCombinedRun = 2,
        FeasibleInCombinedRun = 4,
        CompactFeasibleInCombinedRun = 8,
    }

}

