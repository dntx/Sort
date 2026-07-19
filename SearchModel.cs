using System;
using System.Collections.Generic;

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
