using System;
using System.Collections.Generic;

namespace TopKFinder;

sealed class GreedyPolicySolution
{
    public GreedyPolicySolution(
        int worstCaseSteps,
        IReadOnlyDictionary<SearchStateKey, GreedyPolicyNode> nodes,
        TimeSpan solveElapsed)
    {
        WorstCaseSteps = worstCaseSteps;
        Nodes = nodes;
        SolveElapsed = solveElapsed;
    }

    public int WorstCaseSteps { get; }
    public IReadOnlyDictionary<SearchStateKey, GreedyPolicyNode> Nodes { get; }
    public TimeSpan SolveElapsed { get; }
}

sealed class GreedyPolicyNode
{
    public GreedyPolicyNode(
        BestGroupPattern selectedGroup,
        IReadOnlyCollection<SearchStateKey> successors,
        int remainingDepth)
    {
        SelectedGroup = selectedGroup;
        Successors = successors;
        RemainingDepth = remainingDepth;
    }

    public BestGroupPattern SelectedGroup { get; }
    public IReadOnlyCollection<SearchStateKey> Successors { get; }
    public int RemainingDepth { get; }
}
