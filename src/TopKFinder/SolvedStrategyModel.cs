using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace TopKFinder;

readonly record struct ProblemShape(int N, int M, int RequestedK, int K);

readonly record struct StrategyScore(int WorstCaseSteps, int? SearchEdgeCost = null);

readonly record struct BoundEvidence(
    int ProvenLowerBound,
    int? FeasibleUpperBound,
    bool IsProvenOptimal,
    bool WasCandidateEnumerationCapped,
    int? AttemptedCeiling = null);

enum SolvedStrategyStageKind
{
    StepProof,
    ExactEdgeCompact,
    GreedyFeasible,
    GreedyTighten,
    ProofTighten,
    GreedyEdgeCompact,
}

readonly record struct StageProvenance(SolvedStrategyStageKind Kind, string StageName);

sealed class SolvedGroupPattern
{
    public SolvedGroupPattern(BestGroupPattern pattern)
    {
        GroupSize = pattern.GroupSize;
        Pattern = pattern.Pattern.SnapshotCopy();
        ColorSignature = pattern.ColorSignature is null
            ? null
            : Array.AsReadOnly((int[])pattern.ColorSignature.Clone());
    }

    public SolvedGroupPattern(SolvedGroupPattern pattern)
    {
        GroupSize = pattern.GroupSize;
        Pattern = pattern.Pattern.SnapshotCopy();
        ColorSignature = pattern.ColorSignature is null
            ? null
            : Array.AsReadOnly(pattern.ColorSignature.ToArray());
    }

    public int GroupSize { get; }
    public IntSequenceKey Pattern { get; }
    public IReadOnlyList<int>? ColorSignature { get; }
}

sealed class SolvedStrategy
{
    public SolvedStrategy(
        ProblemShape problem,
        SearchStateKey rootKey,
        IReadOnlyDictionary<SearchStateKey, SolvedStrategyNode> nodes,
        StrategyScore score,
        BoundEvidence bounds,
        StageProvenance provenance,
        SearchStatistics searchStatistics)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(searchStatistics);

        Problem = problem;
        RootKey = rootKey;
        Nodes = FreezeNodes(nodes);
        Score = score;
        Bounds = bounds;
        Provenance = provenance;
        SearchStatistics = FreezeSearchStatistics(searchStatistics);

        SolvedStrategyValidator.Validate(this);
    }

    public ProblemShape Problem { get; }
    public SearchStateKey RootKey { get; }
    public IReadOnlyDictionary<SearchStateKey, SolvedStrategyNode> Nodes { get; }
    public StrategyScore Score { get; }
    public BoundEvidence Bounds { get; }
    public StageProvenance Provenance { get; }
    public SearchStatistics SearchStatistics { get; }

    private static IReadOnlyDictionary<SearchStateKey, SolvedStrategyNode> FreezeNodes(
        IReadOnlyDictionary<SearchStateKey, SolvedStrategyNode> nodes)
    {
        var frozen = new Dictionary<SearchStateKey, SolvedStrategyNode>(nodes.Count);
        foreach ((SearchStateKey key, SolvedStrategyNode node) in nodes)
        {
            ArgumentNullException.ThrowIfNull(node);
            frozen.Add(key.SnapshotCopy(), new SolvedStrategyNode(
                node.SelectedGroup,
                node.DistinctSuccessors,
                node.RemainingDepth));
        }

        return new ReadOnlyDictionary<SearchStateKey, SolvedStrategyNode>(frozen);
    }

    private static SearchStatistics FreezeSearchStatistics(SearchStatistics statistics)
    {
        SearchDiagnostics diagnostics = statistics.Diagnostics;
        var frozenDiagnostics = new SearchDiagnostics(
            Array.AsReadOnly(diagnostics.RootIncumbents.ToArray()),
            diagnostics.LowerBoundPrunes,
            diagnostics.DuplicateOutcomeSkips,
            diagnostics.MergedOutcomeCollisions,
            diagnostics.ExactCacheHits,
            diagnostics.LowerBoundCacheHits,
            diagnostics.FeasibleTopSetCacheHits,
            diagnostics.BestGroupPatternCacheHits);

        return new SearchStatistics(
            statistics.SearchedStates,
            statistics.PendingStates,
            statistics.PeakPendingStates,
            statistics.OutputStates,
            statistics.ExpandedOutputStates,
            statistics.LowerBoundStates,
            statistics.FeasibleTopSetStates,
            frozenDiagnostics,
            statistics.Phase1Milliseconds,
            statistics.Phase1bMilliseconds,
            statistics.Phase2Milliseconds,
            statistics.OutcomesConstructed,
            statistics.CandidateGroupsEnumerated,
            statistics.SearchTreeEdges,
            statistics.CompactStatesSolved,
            statistics.CompactGroupsEnumerated,
            statistics.CompactStepOptimalGroups,
            statistics.RootProvenLowerBound);
    }
}

sealed class SolvedStrategyNode
{
    public SolvedStrategyNode(
        BestGroupPattern selectedGroup,
        IEnumerable<SearchStateKey> distinctSuccessors,
        int remainingDepth)
    {
        ArgumentNullException.ThrowIfNull(distinctSuccessors);

        SelectedGroup = new SolvedGroupPattern(selectedGroup);
        DistinctSuccessors = Array.AsReadOnly(
            distinctSuccessors
                .Distinct()
                .OrderBy(key => key.RemainingSlots)
                .ThenBy(key => key.StateKey)
                .Select(key => key.SnapshotCopy())
                .ToArray());
        RemainingDepth = remainingDepth;
    }

    internal SolvedStrategyNode(
        SolvedGroupPattern selectedGroup,
        IEnumerable<SearchStateKey> distinctSuccessors,
        int remainingDepth)
    {
        SelectedGroup = new SolvedGroupPattern(selectedGroup);
        DistinctSuccessors = Array.AsReadOnly(
            distinctSuccessors
                .Distinct()
                .OrderBy(key => key.RemainingSlots)
                .ThenBy(key => key.StateKey)
                .Select(key => key.SnapshotCopy())
                .ToArray());
        RemainingDepth = remainingDepth;
    }

    public SolvedGroupPattern SelectedGroup { get; }
    public IReadOnlyList<SearchStateKey> DistinctSuccessors { get; }
    public int RemainingDepth { get; }
}

static class SolvedStrategyValidator
{
    public static void Validate(SolvedStrategy solution)
    {
        ArgumentNullException.ThrowIfNull(solution);

        if (solution.Score.WorstCaseSteps < 0)
            throw new ArgumentOutOfRangeException(nameof(solution), "Worst-case steps cannot be negative.");

        if (solution.Bounds.ProvenLowerBound < 0)
            throw new ArgumentOutOfRangeException(nameof(solution), "The proven lower bound cannot be negative.");

        if (solution.Bounds.FeasibleUpperBound is int upperBound
            && solution.Bounds.ProvenLowerBound > upperBound)
        {
            throw new ArgumentException("The proven lower bound cannot exceed the feasible upper bound.", nameof(solution));
        }

        if (solution.Score.WorstCaseSteps == 0)
        {
            if (solution.Nodes.Count != 0)
                throw new ArgumentException("A zero-step solution cannot contain decision nodes.", nameof(solution));
            return;
        }

        if (!solution.Nodes.TryGetValue(solution.RootKey, out SolvedStrategyNode? root))
            throw new ArgumentException("A positive-depth solution must contain its root decision node.", nameof(solution));

        if (root.RemainingDepth != solution.Score.WorstCaseSteps)
            throw new ArgumentException("Root remaining depth must equal the solution worst-case steps.", nameof(solution));

        var reachable = new HashSet<SearchStateKey>();
        var pending = new Stack<SearchStateKey>();
        pending.Push(solution.RootKey);

        while (pending.Count > 0)
        {
            SearchStateKey key = pending.Pop();
            if (!reachable.Add(key))
                continue;

            SolvedStrategyNode node = solution.Nodes[key];
            if (node.RemainingDepth <= 0)
                throw new ArgumentException("Decision-node remaining depth must be positive.", nameof(solution));
            if (node.DistinctSuccessors.Count == 0)
                throw new ArgumentException("A decision node must have at least one successor.", nameof(solution));

            int maxChildDepth = 0;
            foreach (SearchStateKey successor in node.DistinctSuccessors)
            {
                if (!solution.Nodes.TryGetValue(successor, out SolvedStrategyNode? child))
                    continue;

                if (child.RemainingDepth >= node.RemainingDepth)
                    throw new ArgumentException("Successor depth must strictly decrease.", nameof(solution));

                maxChildDepth = Math.Max(maxChildDepth, child.RemainingDepth);
                pending.Push(successor);
            }

            if (node.RemainingDepth != maxChildDepth + 1)
                throw new ArgumentException("Node depth must equal one plus its deepest decision successor.", nameof(solution));
        }

        if (reachable.Count != solution.Nodes.Count)
            throw new ArgumentException("The snapshot contains decision nodes that are not reachable from the root.", nameof(solution));
    }
}
