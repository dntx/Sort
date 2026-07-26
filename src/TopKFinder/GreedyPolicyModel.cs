using System;
using System.Collections.Generic;

namespace TopKFinder;

sealed class GreedyPolicySolution
{
    public GreedyPolicySolution(
        int worstCaseSteps,
        int searchEdgeCost,
        IReadOnlyDictionary<SearchStateKey, GreedyPolicyNode> nodes,
        IReadOnlyCollection<SearchStateKey> finalChoiceKeys,
        TimeSpan solveElapsed)
    {
        WorstCaseSteps = worstCaseSteps;
        SearchEdgeCost = searchEdgeCost;
        Nodes = nodes;
        FinalChoiceKeys = finalChoiceKeys;
        SolveElapsed = solveElapsed;
    }

    public int WorstCaseSteps { get; }
    public int SearchEdgeCost { get; }
    public IReadOnlyDictionary<SearchStateKey, GreedyPolicyNode> Nodes { get; }
    public IReadOnlyCollection<SearchStateKey> FinalChoiceKeys { get; }
    public TimeSpan SolveElapsed { get; }
}

sealed record GreedyFeasibleStageArtifacts(
    SolvedStrategy Solution,
    StrategyPlan Plan,
    StageTimings Timings = default);

sealed class GreedyPolicyNode
{
    public GreedyPolicyNode(
        BestGroupPattern selectedGroup,
        IReadOnlyCollection<SearchStateKey> successors,
        int remainingDepth,
        int searchEdgeCost)
    {
        SelectedGroup = selectedGroup;
        Successors = successors;
        RemainingDepth = remainingDepth;
        SearchEdgeCost = searchEdgeCost;
    }

    public BestGroupPattern SelectedGroup { get; }
    public IReadOnlyCollection<SearchStateKey> Successors { get; }
    public int RemainingDepth { get; }
    public int SearchEdgeCost { get; }
}
