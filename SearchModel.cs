using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

enum SearchNodeKind
{
    Decision,
    Terminal,
    Reference,
}

sealed class SearchStrategy
{
    public SearchStrategy(int n, int m, int requestedK, int k, SearchNode root)
    {
        N = n;
        M = m;
        RequestedK = requestedK;
        K = k;
        Root = root;
    }

    public int N { get; }
    public int M { get; }
    public int RequestedK { get; }
    public int K { get; }
    public SearchNode Root { get; }
}

sealed class SearchNode
{
    public SearchNodeKind Kind { get; }
    public int StateId { get; }
    public int? Step { get; }
    public IReadOnlyList<int> Group { get; }
    public IReadOnlyList<int> TopSet { get; }
    public IReadOnlyList<SearchBranch> Branches { get; }
    public IReadOnlyList<ItemRelabel> ReferenceRelabeling { get; }

    private SearchNode(
        SearchNodeKind kind,
        int stateId,
        int? step,
        IReadOnlyList<int>? group,
        IReadOnlyList<int>? topSet,
        IReadOnlyList<SearchBranch>? branches,
        IReadOnlyList<ItemRelabel>? referenceRelabeling = null)
    {
        Kind = kind;
        StateId = stateId;
        Step = step;
        Group = group ?? Array.Empty<int>();
        TopSet = topSet ?? Array.Empty<int>();
        Branches = branches ?? Array.Empty<SearchBranch>();
        ReferenceRelabeling = referenceRelabeling ?? Array.Empty<ItemRelabel>();
    }

    public static SearchNode Decision(
        int stateId,
        int step,
        IReadOnlyList<int> group,
        IReadOnlyList<SearchBranch> branches)
        => new(SearchNodeKind.Decision, stateId, step, group, null, branches);

    public static SearchNode Terminal(int stateId, IReadOnlyList<int> topSet)
        => new(SearchNodeKind.Terminal, stateId, null, null, topSet, null);

    public static SearchNode Reference(int stateId, IReadOnlyList<ItemRelabel>? relabeling = null)
        => new(SearchNodeKind.Reference, stateId, null, null, null, null, relabeling);
}

sealed class SearchBranch
{
    public string OrderText { get; }
    public SearchEffect Effect { get; }
    public SearchNode Next { get; }

    public SearchBranch(string orderText, SearchEffect effect, SearchNode next)
    {
        OrderText = orderText;
        Effect = effect;
        Next = next;
    }
}

sealed class SearchEffect
{
    public IReadOnlyList<int> NewlyGuaranteedTop { get; }
    public IReadOnlyList<int> NewlyExcluded { get; }
    public IReadOnlyList<int> FixedCandidates { get; }
    public IReadOnlyList<int> PossibleCandidates { get; }

    public SearchEffect(
        IReadOnlyList<int> newlyGuaranteedTop,
        IReadOnlyList<int> newlyExcluded,
        IReadOnlyList<int> fixedCandidates,
        IReadOnlyList<int> possibleCandidates)
    {
        NewlyGuaranteedTop = newlyGuaranteedTop;
        NewlyExcluded = newlyExcluded;
        FixedCandidates = fixedCandidates;
        PossibleCandidates = possibleCandidates;
    }
}

static class SearchModelMapper
{
    public static SearchStrategy FromStrategyPlan(StrategyPlan plan)
    {
        var memo = new Dictionary<StrategyNode, SearchNode>(ReferenceComparer<StrategyNode>.Instance);
        SearchNode root = MapNode(plan.Root, memo);
        return new SearchStrategy(plan.N, plan.M, plan.RequestedK, plan.K, root);
    }

    private static SearchNode MapNode(StrategyNode source, Dictionary<StrategyNode, SearchNode> memo)
    {
        if (memo.TryGetValue(source, out SearchNode? cached))
            return cached;

        SearchNode mapped;
        switch (source.Kind)
        {
            case StrategyNodeKind.Terminal:
                mapped = SearchNode.Terminal(source.StateId, source.TopSet);
                break;
            case StrategyNodeKind.Reference:
                mapped = SearchNode.Reference(source.StateId, source.ReferenceRelabeling);
                break;
            default:
                mapped = SearchNode.Decision(
                    source.StateId,
                    source.Step ?? 0,
                    source.Group,
                    Array.Empty<SearchBranch>());
                break;
        }

        memo[source] = mapped;

        if (source.Kind != StrategyNodeKind.Decision)
            return mapped;

        var branches = new SearchBranch[source.Branches.Count];
        for (int i = 0; i < source.Branches.Count; i++)
        {
            StrategyBranch branch = source.Branches[i];
            branches[i] = new SearchBranch(
                branch.OrderText,
                new SearchEffect(
                    branch.Effect.NewlyGuaranteedTop,
                    branch.Effect.NewlyExcluded,
                    branch.Effect.FixedCandidates,
                    branch.Effect.PossibleCandidates),
                MapNode(branch.Next, memo));
        }

        SearchNode rebuilt = SearchNode.Decision(source.StateId, source.Step ?? 0, source.Group, branches);
        memo[source] = rebuilt;
        return rebuilt;
    }

    // Local reference comparer keeps mapping stable by object identity without requiring StrategyNode
    // to implement value equality.
    private sealed class ReferenceComparer<T> : IEqualityComparer<T>
        where T : class
    {
        public static ReferenceComparer<T> Instance { get; } = new();

        public bool Equals(T? x, T? y)
            => ReferenceEquals(x, y);

        public int GetHashCode(T obj)
            => RuntimeHelpers.GetHashCode(obj);
    }
}