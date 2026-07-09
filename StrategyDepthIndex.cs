using System;
using System.Collections.Generic;

sealed class StrategyDepthIndex
{
    private readonly Dictionary<StrategyNode, int> _subtreeMaxStep = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<StrategyNode, int> _remainingSorts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, StrategyNode> _referenceTargets = new();

    public static StrategyDepthIndex Build(StrategyNode root)
    {
        var index = new StrategyDepthIndex();
        index.CollectReferenceTargets(root);
        index.ResolveSubtreeMaxStep(root, new Dictionary<StrategyNode, int>(ReferenceEqualityComparer.Instance),
            new HashSet<StrategyNode>(ReferenceEqualityComparer.Instance));
        return index;
    }

    public int SubtreeMaxStep(StrategyNode decisionNode)
        => _subtreeMaxStep.TryGetValue(decisionNode, out int max) ? max : decisionNode.Step ?? 0;

    public bool TryGetReferenceRemaining(int stateId, out int remainingSteps)
    {
        if (_referenceTargets.TryGetValue(stateId, out StrategyNode? target))
        {
            remainingSteps = _remainingSorts.TryGetValue(target, out int remaining)
                ? remaining
                : 0;
            return true;
        }

        remainingSteps = 0;
        return false;
    }

    private void CollectReferenceTargets(StrategyNode node)
    {
        if (node.Kind != StrategyNodeKind.Decision)
            return;

        if (node.Branches.Count > 0)
            _referenceTargets[node.StateId] = node;

        foreach (var branch in node.Branches)
            CollectReferenceTargets(branch.Next);
    }

    private int ResolveSubtreeMaxStep(
        StrategyNode node, Dictionary<StrategyNode, int> memo, HashSet<StrategyNode> visiting)
    {
        if (memo.TryGetValue(node, out int cached))
            return cached;
        if (!visiting.Add(node))
            throw new InvalidOperationException(
                $"Strategy tree is malformed: reference cycle detected at state S{node.StateId}. " +
                "A valid strategy tree's reference graph must be acyclic.");

        int remaining;
        switch (node.Kind)
        {
            case StrategyNodeKind.Terminal:
                remaining = 0;
                break;
            case StrategyNodeKind.Reference:
                remaining = _referenceTargets.TryGetValue(node.StateId, out StrategyNode? target)
                    ? ResolveSubtreeMaxStep(target, memo, visiting)
                    : 0;
                break;
            default:
                if (node.Branches.Count == 0)
                {
                    remaining = 1;
                    break;
                }

                int maxChild = 0;
                foreach (var branch in node.Branches)
                    maxChild = Math.Max(maxChild, ResolveSubtreeMaxStep(branch.Next, memo, visiting));
                remaining = 1 + maxChild;
                break;
        }

        visiting.Remove(node);
        memo[node] = remaining;
        if (node.Kind == StrategyNodeKind.Decision)
        {
            _remainingSorts[node] = remaining;
            _subtreeMaxStep[node] = Math.Max(node.Step ?? 0, (node.Step ?? 0) - 1 + remaining);
        }
        return remaining;
    }
}
