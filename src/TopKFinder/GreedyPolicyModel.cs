using System;
using System.Collections.Generic;

namespace TopKFinder;

sealed class GreedyPolicySolution
{
    public GreedyPolicySolution(
        int worstCaseSteps,
        IReadOnlyDictionary<SearchStateKey, GreedyPolicyNode> nodes,
        IReadOnlyCollection<SearchStateKey> finalChoiceKeys,
        TimeSpan solveElapsed)
    {
        WorstCaseSteps = worstCaseSteps;
        Nodes = nodes;
        FinalChoiceKeys = finalChoiceKeys;
        SolveElapsed = solveElapsed;
    }

    public int WorstCaseSteps { get; }
    public IReadOnlyDictionary<SearchStateKey, GreedyPolicyNode> Nodes { get; }
    public IReadOnlyCollection<SearchStateKey> FinalChoiceKeys { get; }
    public TimeSpan SolveElapsed { get; }
}

sealed record GreedyFeasibleStageArtifacts(SolvedStrategy Solution, StrategyPlan Plan);

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
