using System;
using System.Collections.Generic;
using Xunit;

namespace TopKFinder.Tests;

public sealed class SolvedStrategyModelTests
{
    [Fact]
    public void Constructor_FreezesRootReachableStrategyAndStatistics()
    {
        int[] rootParts = [1, 2];
        int[] childParts = [3, 4];
        int[] terminalParts = [5, 6];
        int[] groupParts = [7, 8];
        int[] colorSignature = [0, 1];
        SearchStateKey rootKey = Key(2, rootParts);
        SearchStateKey childKey = Key(1, childParts);
        SearchStateKey terminalKey = Key(0, terminalParts);
        var rootSuccessors = new List<SearchStateKey> { terminalKey, childKey, childKey };
        var nodes = new Dictionary<SearchStateKey, SolvedStrategyNode>
        {
            [rootKey] = Node(2, rootSuccessors, groupParts, colorSignature),
            [childKey] = Node(1, [terminalKey], [9], [1]),
        };
        var milestones = new List<SearchMilestone>
        {
            new(2, "0,1", 1, 2, 0, 1, 2, 0),
        };

        SolvedStrategy solution = CreateSolution(rootKey, nodes, 2, CreateStatistics(milestones));

        nodes.Clear();
        rootSuccessors.Clear();
        rootParts[0] = 99;
        childParts[0] = 99;
        terminalParts[0] = 99;
        groupParts[0] = 99;
        colorSignature[0] = 99;
        milestones.Clear();

        Assert.Equal(2, solution.Nodes.Count);
        SolvedStrategyNode root = solution.Nodes[Key(2, [1, 2])];
        Assert.Equal(2, root.DistinctSuccessors.Count);
        Assert.Equal(new IntSequenceKey([7, 8]), root.SelectedGroup!.Pattern);
        Assert.Equal([0, 1], root.SelectedGroup.ColorSignature);
        Assert.Single(solution.SearchStatistics.Diagnostics.RootIncumbents);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<SearchStateKey, SolvedStrategyNode>)solution.Nodes).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<SearchStateKey>)root.DistinctSuccessors).Add(terminalKey));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<int>)root.SelectedGroup.ColorSignature!).Add(2));
    }

    [Fact]
    public void Constructor_RejectsRootDepthMismatch()
    {
        SearchStateKey rootKey = Key(1, [1]);
        var nodes = new Dictionary<SearchStateKey, SolvedStrategyNode>
        {
            [rootKey] = Node(1, [Key(0, [2])]),
        };

        Assert.Throws<ArgumentException>(() => CreateSolution(rootKey, nodes, worstCaseSteps: 2));
    }

    [Fact]
    public void Constructor_RejectsNonDecreasingSuccessorDepth()
    {
        SearchStateKey rootKey = Key(2, [1]);
        SearchStateKey childKey = Key(1, [2]);
        var nodes = new Dictionary<SearchStateKey, SolvedStrategyNode>
        {
            [rootKey] = Node(2, [childKey]),
            [childKey] = Node(2, [Key(0, [3])]),
        };

        Assert.Throws<ArgumentException>(() => CreateSolution(rootKey, nodes, worstCaseSteps: 2));
    }

    [Fact]
    public void Constructor_RejectsUnreachableDecisionNode()
    {
        SearchStateKey rootKey = Key(1, [1]);
        var nodes = new Dictionary<SearchStateKey, SolvedStrategyNode>
        {
            [rootKey] = Node(1, [Key(0, [2])]),
            [Key(1, [3])] = Node(1, [Key(0, [4])]),
        };

        Assert.Throws<ArgumentException>(() => CreateSolution(rootKey, nodes, worstCaseSteps: 1));
    }

    [Fact]
    public void Constructor_AcceptsFinalChoiceAsOneStepLeaf()
    {
        SearchStateKey rootKey = Key(1, [1]);
        var nodes = new Dictionary<SearchStateKey, SolvedStrategyNode>
        {
            [rootKey] = SolvedStrategyNode.FinalChoice(),
        };

        SolvedStrategy solution = CreateSolution(rootKey, nodes, worstCaseSteps: 1);

        SolvedStrategyNode root = solution.Nodes[rootKey];
        Assert.Equal(SolvedStrategyNodeKind.FinalChoice, root.Kind);
        Assert.Equal(1, root.RemainingDepth);
        Assert.Null(root.SelectedGroup);
        Assert.Empty(root.DistinctSuccessors);
    }

    private static SolvedStrategy CreateSolution(
        SearchStateKey rootKey,
        IReadOnlyDictionary<SearchStateKey, SolvedStrategyNode> nodes,
        int worstCaseSteps,
        SearchStatistics? statistics = null)
    {
        return new SolvedStrategy(
            new ProblemShape(6, 2, 2, 2),
            rootKey,
            nodes,
            new StrategyScore(worstCaseSteps),
            new BoundEvidence(0, worstCaseSteps, false, false),
            new StageProvenance(SolvedStrategyStageKind.GreedyFeasible, StageNames.GreedyFeasible),
            statistics ?? CreateStatistics([]));
    }

    private static SolvedStrategyNode Node(
        int remainingDepth,
        IEnumerable<SearchStateKey> successors,
        int[]? groupParts = null,
        int[]? colorSignature = null)
    {
        return new SolvedStrategyNode(
            new BestGroupPattern(2, new IntSequenceKey(groupParts ?? [0]), colorSignature),
            successors,
            remainingDepth);
    }

    private static SearchStateKey Key(int remainingSlots, int[] parts)
        => new(remainingSlots, new IntSequenceKey(parts));

    private static SearchStatistics CreateStatistics(IReadOnlyList<SearchMilestone> milestones)
    {
        return new SearchStatistics(
            searchedStates: 2,
            pendingStates: 0,
            peakPendingStates: 1,
            outputStates: 2,
            expandedOutputStates: 2,
            lowerBoundStates: 0,
            feasibleTopSetStates: 0,
            new SearchDiagnostics(milestones, 0, 0, 0, 0, 0, 0, 0),
            phase1Milliseconds: 1,
            phase1bMilliseconds: 0,
            phase2Milliseconds: 0,
            outcomesConstructed: 2,
            candidateGroupsEnumerated: 1,
            searchTreeEdges: null,
            compactStatesSolved: 0,
            compactGroupsEnumerated: 0,
            compactStepOptimalGroups: 0,
            rootProvenLowerBound: 0);
    }
}
