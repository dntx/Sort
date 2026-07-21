using System.Collections.Generic;

namespace TopKFinder;

partial class StrategyBuilder
{
    private Dictionary<SearchStateKey, int>? _constructiveDepthMemo
    {
        get => _session.ConstructiveDepthMemo;
        set => _session.ConstructiveDepthMemo = value;
    }

    private int _greedyScoreLowerBoundCacheReuseHits
    {
        get => _session.GreedyScoreLowerBoundCacheReuseHits;
        set => _session.GreedyScoreLowerBoundCacheReuseHits = value;
    }

    private int _constructiveDisplayLineTieBreakEvaluations
    {
        get => _session.ConstructiveDisplayLineTieBreakEvaluations;
        set => _session.ConstructiveDisplayLineTieBreakEvaluations = value;
    }

    internal int GreedyScoreLowerBoundCacheReuseHits => _greedyScoreLowerBoundCacheReuseHits;
    internal int ConstructiveDisplayLineTieBreakEvaluations => _constructiveDisplayLineTieBreakEvaluations;

    // Feasible step budget U threaded from the step phase to the edge phase within one combined run.
    // ExecuteGreedyFeasibleStage sets it to the MATERIALIZED MaxStep of the just-built step tree (the tightest
    // sound budget: the step plan itself witnesses a U-step solution, so the compact pass under this
    // ceiling can never need more than U). The edge phase (RunGreedyPipeline) reads it so it
    // never produces a plan worse than the step phase. -1 until a step plan is built; deliberately NOT
    // cleared by ResetPerBuildTransientState so it survives the step->edge build boundary on the same
    // builder. When the edge phase runs standalone (no prior step build) it falls back to the lean
    // ConstructiveRootUpperBound, which is sound but looser.
    private int _feasibleRootBudget
    {
        get => _session.FeasibleRootBudget;
        set => _session.FeasibleRootBudget = value;
    }

    // Total distinct canonical search states the step phase visited, captured at the end of
    // ExecuteGreedyFeasibleStage and (like _feasibleRootBudget) deliberately NOT cleared by
    // ResetPerBuildTransientState so it survives the step->edge boundary on the same builder. The edge
    // phase has no pending/searched signal, so this serves as the SCALE anchor for a self-correcting
    // asymptote (see EstimateProgress) that turns _compactStatesSolved into a live progress fraction --
    // it is only a rough scale (edge work can be many times larger or smaller than the step state
    // count), not a hard denominator. -1 until a step plan is built; the standalone edge phase (no
    // prior step build) leaves it -1 and keeps the pinned-progress behavior.
    private int _feasibleCompactStateEstimate
    {
        get => _session.FeasibleCompactStateEstimate;
        set => _session.FeasibleCompactStateEstimate = value;
    }

    // GreedyTighten override/memo state.
    private Dictionary<SearchStateKey, List<int>> _greedyTightenOverrides => _session.GreedyTightenOverrides;
    private Dictionary<SearchStateKey, ComparisonState> _greedyTightenOverrideAnchors => _session.GreedyTightenOverrideAnchors;
    private Dictionary<SearchStateKey, int> _greedyTightenSharedHeightMemo => _session.GreedyTightenSharedHeightMemo;
    private bool _useGreedyTightenSelection
    {
        get => _session.UseGreedyTightenSelection;
        set => _session.UseGreedyTightenSelection = value;
    }

    // GreedyTighten diagnostics (per ExecuteGreedyTightenStage run).
    private int _greedyTightenRounds { get => _session.GreedyTightenRounds; set => _session.GreedyTightenRounds = value; }
    private int _greedyTightenCommits { get => _session.GreedyTightenCommits; set => _session.GreedyTightenCommits = value; }
    private int _greedyTightenStatesVisited { get => _session.GreedyTightenStatesVisited; set => _session.GreedyTightenStatesVisited = value; }
    private int _greedyTightenCandidateGroupsTried { get => _session.GreedyTightenCandidateGroupsTried; set => _session.GreedyTightenCandidateGroupsTried = value; }
    private int _greedyTightenHeightCalls { get => _session.GreedyTightenHeightCalls; set => _session.GreedyTightenHeightCalls = value; }
    private int _greedyTightenHeightMemoHits { get => _session.GreedyTightenHeightMemoHits; set => _session.GreedyTightenHeightMemoHits = value; }
    private int _greedyTightenHeightUnderGroupCalls { get => _session.GreedyTightenHeightUnderGroupCalls; set => _session.GreedyTightenHeightUnderGroupCalls = value; }
    private int _greedyTightenCriticalShortCircuits { get => _session.GreedyTightenCriticalShortCircuits; set => _session.GreedyTightenCriticalShortCircuits = value; }
    private int _greedyTightenCommitCandidateRankSum { get => _session.GreedyTightenCommitCandidateRankSum; set => _session.GreedyTightenCommitCandidateRankSum = value; }
    private Dictionary<int, int> _greedyTightenVisitedDepthHistogram => _session.GreedyTightenVisitedDepthHistogram;
    private Dictionary<int, int> _greedyTightenCommitDepthHistogram => _session.GreedyTightenCommitDepthHistogram;

    internal int GreedyTightenRounds => _greedyTightenRounds;
    internal int GreedyTightenCommits => _greedyTightenCommits;
    internal int GreedyTightenStatesVisited => _greedyTightenStatesVisited;
    internal int GreedyTightenCandidateGroupsTried => _greedyTightenCandidateGroupsTried;
    internal int GreedyTightenHeightCalls => _greedyTightenHeightCalls;
    internal int GreedyTightenHeightMemoHits => _greedyTightenHeightMemoHits;
    internal int GreedyTightenHeightUnderGroupCalls => _greedyTightenHeightUnderGroupCalls;
    internal int GreedyTightenCriticalShortCircuits => _greedyTightenCriticalShortCircuits;
    internal int GreedyTightenAverageCommitCandidateRank => _greedyTightenCommits == 0
        ? 0
        : _greedyTightenCommitCandidateRankSum / _greedyTightenCommits;
    internal IReadOnlyDictionary<int, int> GreedyTightenVisitedDepthHistogram => _greedyTightenVisitedDepthHistogram;
    internal IReadOnlyDictionary<int, int> GreedyTightenCommitDepthHistogram => _greedyTightenCommitDepthHistogram;
}
