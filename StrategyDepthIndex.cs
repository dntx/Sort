using System;
using System.Collections.Generic;

sealed class StrategyDepthIndex
{
    private readonly Dictionary<StrategyNode, int> _subtreeMaxStep = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, StrategyNode> _referenceTargets = new();

    public static StrategyDepthIndex Build(StrategyNode root)
    {
        var index = new StrategyDepthIndex();
        index.Visit(root);
        return index;
    }

    public int SubtreeMaxStep(StrategyNode decisionNode)
        => _subtreeMaxStep.TryGetValue(decisionNode, out int max) ? max : decisionNode.Step ?? 0;

    public bool TryGetReferenceRemaining(int stateId, out int remainingSteps)
    {
        if (_referenceTargets.TryGetValue(stateId, out StrategyNode? target))
        {
            remainingSteps = _subtreeMaxStep[target] - (target.Step ?? 0);
            return true;
        }

        remainingSteps = 0;
        return false;
    }

    private int Visit(StrategyNode node)
    {
        if (node.Kind != StrategyNodeKind.Decision)
            return 0;

        if (_subtreeMaxStep.TryGetValue(node, out int cached))
            return cached;

        int max = node.Step ?? 0;
        foreach (var branch in node.Branches)
            max = Math.Max(max, Visit(branch.Next));

        _subtreeMaxStep[node] = max;

        if (node.FinalChoice is null)
            _referenceTargets[node.StateId] = node;

        return max;
    }
}
