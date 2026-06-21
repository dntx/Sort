using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;

readonly struct IntSequenceKey : IEquatable<IntSequenceKey>, IComparable<IntSequenceKey>
{
    private readonly int[] _parts;
    private readonly int _hashCode;

    public IntSequenceKey(int[] parts)
    {
        _parts = parts;
        var hash = new HashCode();
        foreach (int part in parts)
            hash.Add(part);
        _hashCode = hash.ToHashCode();
    }

    public bool Equals(IntSequenceKey other)
    {
        if (_parts.Length != other._parts.Length)
            return false;

        for (int i = 0; i < _parts.Length; i++)
        {
            if (_parts[i] != other._parts[i])
                return false;
        }

        return true;
    }

    public int CompareTo(IntSequenceKey other)
    {
        int commonLength = Math.Min(_parts.Length, other._parts.Length);
        for (int i = 0; i < commonLength; i++)
        {
            int comparison = _parts[i].CompareTo(other._parts[i]);
            if (comparison != 0)
                return comparison;
        }

        return _parts.Length.CompareTo(other._parts.Length);
    }

    public override bool Equals(object? obj)
    {
        return obj is IntSequenceKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return _hashCode;
    }

    public static bool operator ==(IntSequenceKey left, IntSequenceKey right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(IntSequenceKey left, IntSequenceKey right)
    {
        return !left.Equals(right);
    }
}

readonly struct SearchStateKey : IEquatable<SearchStateKey>
{
    public SearchStateKey(int remainingSlots, IntSequenceKey stateKey)
    {
        RemainingSlots = remainingSlots;
        StateKey = stateKey;
    }

    public int RemainingSlots { get; }
    public IntSequenceKey StateKey { get; }

    public bool Equals(SearchStateKey other)
    {
        return RemainingSlots == other.RemainingSlots && StateKey.Equals(other.StateKey);
    }

    public override bool Equals(object? obj)
    {
        return obj is SearchStateKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(RemainingSlots, StateKey);
    }
}

readonly struct BuildStateKey : IEquatable<BuildStateKey>
{
    public BuildStateKey(ulong fixedTopMask, SearchStateKey searchKey)
    {
        FixedTopMask = fixedTopMask;
        SearchKey = searchKey;
    }

    public ulong FixedTopMask { get; }
    public SearchStateKey SearchKey { get; }

    public bool Equals(BuildStateKey other)
    {
        return FixedTopMask == other.FixedTopMask && SearchKey.Equals(other.SearchKey);
    }

    public override bool Equals(object? obj)
    {
        return obj is BuildStateKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(FixedTopMask, SearchKey);
    }
}

class ComparisonState
{
    private static readonly IntSequenceKey InactiveSignature = new(new[] { 0 });
    private readonly int _n;
    private readonly ulong _allMask;
    public ulong[] Ancestors { get; }
    public ulong[] Descendants { get; }
    public ulong ActiveMask { get; private set; }
    public int ActiveCount { get; private set; }
    private int[]? _structuralLabelsCache;
    private IntSequenceKey? _canonicalKeyCache;

    public ComparisonState(int n)
    {
        _n = n;
        _allMask = CreateFullMask(n);
        Ancestors = new ulong[n];
        Descendants = new ulong[n];
        ActiveMask = _allMask;
        ActiveCount = n;
    }

    private ComparisonState(int n, ulong[] ancestors, ulong[] descendants, ulong activeMask, int activeCount)
    {
        _n = n;
        _allMask = CreateFullMask(n);
        Ancestors = ancestors;
        Descendants = descendants;
        ActiveMask = activeMask;
        ActiveCount = activeCount;
    }

    public ComparisonState Clone()
    {
        return new ComparisonState(
            _n,
            (ulong[])Ancestors.Clone(),
            (ulong[])Descendants.Clone(),
            ActiveMask,
            ActiveCount);
    }

    private void InvalidateDerivedCaches()
    {
        _structuralLabelsCache = null;
        _canonicalKeyCache = null;
    }

    public void AddRelation(int greater, int lesser)
    {
        ulong greaterBit = Bit(greater);
        ulong lesserBit = Bit(lesser);
        if ((Ancestors[lesser] & greaterBit) != 0)
            return;

        InvalidateDerivedCaches();

        ulong newAncestorsForLesser = (Ancestors[greater] | greaterBit) & ~Ancestors[lesser] & _allMask;
        if (newAncestorsForLesser != 0)
        {
            foreach (int below in EnumerateBits(Descendants[lesser] | lesserBit))
                Ancestors[below] |= newAncestorsForLesser;
        }

        ulong newDescendantsForGreater = (Descendants[lesser] | lesserBit) & ~Descendants[greater] & _allMask;
        if (newDescendantsForGreater != 0)
        {
            foreach (int above in EnumerateBits(Ancestors[greater] | greaterBit))
                Descendants[above] |= newDescendantsForGreater;
        }
    }

    public void ApplyOrder(IReadOnlyList<int> sorted)
    {
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            for (int j = i + 1; j < sorted.Count; j++)
                AddRelation(sorted[i], sorted[j]);
        }
    }

    public void Eliminate(int k)
    {
        ulong removedMask = 0;
        foreach (int item in EnumerateBits(ActiveMask))
        {
            if (BitOperations.PopCount(Ancestors[item] & ActiveMask) >= k)
                removedMask |= Bit(item);
        }

        if (removedMask == 0)
            return;

        InvalidateDerivedCaches();
        ActiveMask &= ~removedMask;
        ActiveCount -= BitOperations.PopCount(removedMask);
    }

    public void Deactivate(ulong removedMask)
    {
        removedMask &= ActiveMask;
        if (removedMask == 0)
            return;

        InvalidateDerivedCaches();
        ActiveMask &= ~removedMask;
        ActiveCount -= BitOperations.PopCount(removedMask);
    }

    public int[] GetStructuralLabels()
    {
        if (_structuralLabelsCache is not null)
            return _structuralLabelsCache;

        int n = Ancestors.Length;
        var labels = Enumerable.Range(0, n)
            .Select(i => IsActive(i) ? 1 : 0)
            .ToArray();

        bool changed;
        do
        {
            changed = false;
            int classCount = labels.Max() + 1;
            var signatures = new IntSequenceKey[n];
            for (int i = 0; i < n; i++)
            {
                signatures[i] = IsActive(i)
                    ? BuildElementSignature(labels[i], Ancestors[i] & ActiveMask, Descendants[i] & ActiveMask, labels, classCount)
                    : InactiveSignature;
            }

            var orderedSignatures = signatures
                .Distinct()
                .OrderBy(signature => signature)
                .ToList();

            var signatureToColor = new Dictionary<IntSequenceKey, int>(orderedSignatures.Count);
            for (int index = 0; index < orderedSignatures.Count; index++)
                signatureToColor[orderedSignatures[index]] = index;

            var nextLabels = signatures.Select(signature => signatureToColor[signature]).ToArray();
            changed = !labels.SequenceEqual(nextLabels);
            labels = nextLabels;
        }
        while (changed);

        _structuralLabelsCache = labels;
        return labels;
    }

    public IntSequenceKey GetCanonicalKey()
    {
        if (_canonicalKeyCache is not null)
            return _canonicalKeyCache.Value;

        int n = Ancestors.Length;
        var labels = GetStructuralLabels();
        var activeClassIds = Enumerable.Range(0, n)
            .Where(IsActive)
            .Select(i => labels[i])
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var parts = new List<int> { activeClassIds.Count };

        foreach (int classId in activeClassIds)
        {
            var members = Enumerable.Range(0, n).Where(i => labels[i] == classId).ToList();
            int memberCount = members.Count;
            int representative = members[0];

            parts.Add(memberCount);
            parts.Add(IsActive(representative) ? 1 : 0);

            foreach (int otherClass in activeClassIds)
            {
                var counts = members
                    .Select(member => CountNeighborsWithLabel(Ancestors[member] & ActiveMask, labels, otherClass))
                    .OrderBy(x => x);

                parts.AddRange(counts);
            }

            foreach (int otherClass in activeClassIds)
            {
                var counts = members
                    .Select(member => CountNeighborsWithLabel(Descendants[member] & ActiveMask, labels, otherClass))
                    .OrderBy(x => x);

                parts.AddRange(counts);
            }
        }

        _canonicalKeyCache = new IntSequenceKey(parts.ToArray());
        return _canonicalKeyCache.Value;
    }

    public IntSequenceKey GetDisplayCanonicalKey(ulong fixedTopMask)
    {
        ulong combinedMask = ActiveMask | fixedTopMask;
        int n = Ancestors.Length;
        var labels = Enumerable.Range(0, n)
            .Select(i => (combinedMask & Bit(i)) == 0 ? 0 : 1)
            .ToArray();

        bool changed;
        do
        {
            changed = false;
            int classCount = labels.Max() + 1;
            var signatures = new IntSequenceKey[n];
            for (int i = 0; i < n; i++)
            {
                signatures[i] = (combinedMask & Bit(i)) == 0
                    ? new IntSequenceKey(new[] { 0 })
                    : BuildElementSignature(labels[i], Ancestors[i] & combinedMask, Descendants[i] & combinedMask, labels, classCount);
            }

            var orderedSignatures = signatures
                .Distinct()
                .OrderBy(signature => signature)
                .ToList();

            var signatureToColor = new Dictionary<IntSequenceKey, int>(orderedSignatures.Count);
            for (int index = 0; index < orderedSignatures.Count; index++)
                signatureToColor[orderedSignatures[index]] = index;

            var nextLabels = signatures.Select(signature => signatureToColor[signature]).ToArray();
            changed = !labels.SequenceEqual(nextLabels);
            labels = nextLabels;
        }
        while (changed);

        var activeClassIds = Enumerable.Range(0, n)
            .Where(i => (combinedMask & Bit(i)) != 0)
            .Select(i => labels[i])
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var parts = new List<int> { activeClassIds.Count };
        foreach (int classId in activeClassIds)
        {
            var members = Enumerable.Range(0, n).Where(i => labels[i] == classId).ToList();
            parts.Add(members.Count);
            parts.Add(1);

            foreach (int otherClass in activeClassIds)
            {
                var counts = members
                    .Select(member => CountNeighborsWithLabel(Ancestors[member] & combinedMask, labels, otherClass))
                    .OrderBy(x => x);
                parts.AddRange(counts);
            }

            foreach (int otherClass in activeClassIds)
            {
                var counts = members
                    .Select(member => CountNeighborsWithLabel(Descendants[member] & combinedMask, labels, otherClass))
                    .OrderBy(x => x);
                parts.AddRange(counts);
            }
        }

        return new IntSequenceKey(parts.ToArray());
    }

    private static IntSequenceKey BuildElementSignature(
        int currentLabel,
        ulong ancestorMask,
        ulong descendantMask,
        IReadOnlyList<int> labels,
        int classCount)
    {
        int[] parts = new int[1 + (2 * classCount)];
        parts[0] = currentLabel;
        AddNeighborCounts(parts, 1, ancestorMask, labels);
        AddNeighborCounts(parts, 1 + classCount, descendantMask, labels);
        return new IntSequenceKey(parts);
    }

    private static void AddNeighborCounts(int[] target, int offset, ulong mask, IReadOnlyList<int> labels)
    {
        foreach (int item in EnumerateBits(mask))
            target[offset + labels[item]]++;
    }

    public bool IsActive(int item)
    {
        return (ActiveMask & Bit(item)) != 0;
    }

    public bool HasAncestor(int item, int possibleAncestor)
    {
        return (Ancestors[item] & Bit(possibleAncestor)) != 0;
    }

    public int GetAncestorCount(int item)
    {
        return BitOperations.PopCount(Ancestors[item] & ActiveMask);
    }

    public int GetDescendantCount(int item)
    {
        return BitOperations.PopCount(Descendants[item] & ActiveMask);
    }

    public List<int> GetActiveItemsOrdered()
    {
        return MaskToOrderedList(ActiveMask);
    }

    public static List<int> MaskToOrderedList(ulong mask)
    {
        return EnumerateBits(mask).ToList();
    }

    private static ulong Bit(int item)
    {
        return 1UL << item;
    }

    private static ulong CreateFullMask(int n)
    {
        return n == 64 ? ulong.MaxValue : (1UL << n) - 1;
    }

    private static IEnumerable<int> EnumerateBits(ulong mask)
    {
        while (mask != 0)
        {
            int bit = BitOperations.TrailingZeroCount(mask);
            yield return bit;
            mask &= mask - 1;
        }
    }

    private static int CountNeighborsWithLabel(ulong mask, IReadOnlyList<int> labels, int targetLabel)
    {
        int count = 0;
        foreach (int item in EnumerateBits(mask))
        {
            if (labels[item] == targetLabel)
                count++;
        }

        return count;
    }
}

enum StrategyNodeKind
{
    Decision,
    Terminal,
    Reference,
}

sealed class StrategyPlan
{
    public int N { get; }
    public int M { get; }
    public int K { get; }
    public StrategyNode Root { get; }
    public TimeSpan Elapsed { get; }
    public int MaxStep { get; }
    public SearchStatistics SearchStatistics { get; }

    public StrategyPlan(int n, int m, int k, StrategyNode root, TimeSpan elapsed, SearchStatistics searchStatistics)
    {
        N = n;
        M = m;
        K = k;
        Root = root;
        Elapsed = elapsed;
        MaxStep = GetMaxStep(root);
        SearchStatistics = searchStatistics;
    }

    private static int GetMaxStep(StrategyNode node)
    {
        int selfStep = node.Step ?? 0;
        if (node.Branches.Count == 0)
            return selfStep;

        return Math.Max(selfStep, node.Branches.Max(branch => GetMaxStep(branch.Next)));
    }
}

readonly struct SearchProgressSnapshot
{
    public SearchProgressSnapshot(int searchedStates, int pendingStates, int peakPendingStates, int outputStates)
    {
        SearchedStates = searchedStates;
        PendingStates = pendingStates;
        PeakPendingStates = peakPendingStates;
        OutputStates = outputStates;
    }

    public int SearchedStates { get; }
    public int PendingStates { get; }
    public int PeakPendingStates { get; }
    public int OutputStates { get; }
}

sealed class SearchStatistics
{
    public SearchStatistics(
        int searchedStates,
        int pendingStates,
        int peakPendingStates,
        int outputStates,
        int expandedOutputStates,
        int lowerBoundStates,
        int feasibleTopSetStates)
    {
        SearchedStates = searchedStates;
        PendingStates = pendingStates;
        PeakPendingStates = peakPendingStates;
        OutputStates = outputStates;
        ExpandedOutputStates = expandedOutputStates;
        LowerBoundStates = lowerBoundStates;
        FeasibleTopSetStates = feasibleTopSetStates;
    }

    public int SearchedStates { get; }
    public int PendingStates { get; }
    public int PeakPendingStates { get; }
    public int OutputStates { get; }
    public int ExpandedOutputStates { get; }
    public int LowerBoundStates { get; }
    public int FeasibleTopSetStates { get; }
}

sealed class StrategyNode
{
    public StrategyNodeKind Kind { get; }
    public int StateId { get; }
    public int? Step { get; }
    public IReadOnlyList<int> Group { get; }
    public IReadOnlyList<int> TopSet { get; }
    public IReadOnlyList<StrategyBranch> Branches { get; }
    public FinalChoiceSummary? FinalChoice { get; }

    private StrategyNode(
        StrategyNodeKind kind,
        int stateId,
        int? step,
        IReadOnlyList<int>? group,
        IReadOnlyList<int>? topSet,
        IReadOnlyList<StrategyBranch>? branches,
        FinalChoiceSummary? finalChoice)
    {
        Kind = kind;
        StateId = stateId;
        Step = step;
        Group = group ?? Array.Empty<int>();
        TopSet = topSet ?? Array.Empty<int>();
        Branches = branches ?? Array.Empty<StrategyBranch>();
        FinalChoice = finalChoice;
    }

    public static StrategyNode Decision(
        int stateId,
        int step,
        IReadOnlyList<int> group,
        IReadOnlyList<StrategyBranch> branches,
        FinalChoiceSummary? finalChoice = null)
        => new(StrategyNodeKind.Decision, stateId, step, group, null, branches, finalChoice);

    public static StrategyNode Terminal(int stateId, IReadOnlyList<int> topSet)
        => new(StrategyNodeKind.Terminal, stateId, null, null, topSet, null, null);

    public static StrategyNode Reference(int stateId)
        => new(StrategyNodeKind.Reference, stateId, null, null, null, null, null);
}

sealed class StrategyBranch
{
    public string OrderText { get; }
    public EquivalentOrderSummary? EquivalentOrders { get; }
    public StrategyEffect Effect { get; }
    public StrategyNode Next { get; }

    public StrategyBranch(string orderText, EquivalentOrderSummary? equivalentOrders, StrategyEffect effect, StrategyNode next)
    {
        OrderText = orderText;
        EquivalentOrders = equivalentOrders;
        Effect = effect;
        Next = next;
    }
}

sealed class EquivalentOrderSummary
{
    public EquivalentOrderSummary(int count, string patternText, string countFormula)
    {
        Count = count;
        PatternText = patternText;
        CountFormula = countFormula;
    }

    public int Count { get; }
    public string PatternText { get; }
    public string CountFormula { get; }
}

sealed class StrategyEffect
{
    public IReadOnlyList<int> NewlyGuaranteedTop { get; }
    public IReadOnlyList<int> NewlyExcluded { get; }
    public IReadOnlyList<int> FixedCandidates { get; }
    public IReadOnlyList<int> PossibleCandidates { get; }

    public StrategyEffect(
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

sealed class FinalChoiceSummary
{
    public FinalChoiceSummary(
        IReadOnlyList<int> fixedTopSet,
        IReadOnlyList<int> candidatePool,
        int remainingSlots)
    {
        FixedTopSet = fixedTopSet;
        CandidatePool = candidatePool;
        RemainingSlots = remainingSlots;
    }

    public IReadOnlyList<int> FixedTopSet { get; }
    public IReadOnlyList<int> CandidatePool { get; }
    public int RemainingSlots { get; }
}

readonly struct FeasibleTopSetInfo
{
    public FeasibleTopSetInfo(int count)
    {
        Count = count;
    }

    public int Count { get; }
}

readonly struct BestGroupPattern
{
    public BestGroupPattern(int groupSize, IntSequenceKey pattern)
    {
        GroupSize = groupSize;
        Pattern = pattern;
    }

    public int GroupSize { get; }
    public IntSequenceKey Pattern { get; }
}

class StrategyBuilder
{
    private const int ProgressReportIntervalMs = 100;
    private readonly int _n;
    private readonly int _m;
    private readonly int _k;
    private readonly CancellationToken _cancellationToken;
    private readonly Action<SearchProgressSnapshot>? _progressCallback;
    private readonly Dictionary<IntSequenceKey, int> _stateIds = new();
    private readonly HashSet<IntSequenceKey> _expandedStates = new();
    private readonly HashSet<SearchStateKey> _visitedSearchStates = new();
    private readonly Dictionary<SearchStateKey, int> _minWorstCaseStepsCache = new();
    private readonly Dictionary<SearchStateKey, int> _lowerBoundStepsCache = new();
    private readonly Dictionary<SearchStateKey, FeasibleTopSetInfo> _feasibleTopSetCache = new();
    private readonly Dictionary<SearchStateKey, BestGroupPattern> _bestGroupPatternCache = new();
    private readonly Stopwatch _progressStopwatch = Stopwatch.StartNew();
    private int _nextStateId = 1;
    private int _searchedStates;
    private int _pendingStates;
    private int _peakPendingStates;
    private long _lastProgressReportMs = -ProgressReportIntervalMs;

    public StrategyBuilder(int n, int m, int k, CancellationToken cancellationToken = default, Action<SearchProgressSnapshot>? progressCallback = null)
    {
        _n = n;
        _m = m;
        _k = k;
        _cancellationToken = cancellationToken;
        _progressCallback = progressCallback;
    }

    public StrategyPlan Build()
    {
        var stopwatch = Stopwatch.StartNew();
        ReportProgress(force: true);
        var initial = new ComparisonState(_n);
        var root = BuildState(initial, 0, _k, 1);
        stopwatch.Stop();
        ReportProgress(force: true);
        return new StrategyPlan(_n, _m, _k, root, stopwatch.Elapsed, CreateSearchStatistics());
    }

    public static StrategyPlan Generate(int n, int m, int k)
    {
        return new StrategyBuilder(n, m, k).Build();
    }

    public static StrategyPlan Generate(int n, int m, int k, CancellationToken cancellationToken)
    {
        return new StrategyBuilder(n, m, k, cancellationToken).Build();
    }

    public static StrategyPlan Generate(int n, int m, int k, CancellationToken cancellationToken, Action<SearchProgressSnapshot> progressCallback)
    {
        return new StrategyBuilder(n, m, k, cancellationToken, progressCallback).Build();
    }

    private StrategyNode BuildState(ComparisonState state, ulong fixedTopMask, int remainingSlots, int step)
    {
        ThrowIfCancellationRequested();
        NormalizeState(state, ref fixedTopMask, ref remainingSlots);
        ObserveSearchState(state, remainingSlots);

        IntSequenceKey displayKey = GetDisplayStateKey(state, fixedTopMask);
        int stateId = GetStateId(displayKey);

        if (remainingSlots == 0)
            return StrategyNode.Terminal(stateId, ComparisonState.MaskToOrderedList(fixedTopMask));

        if (TryGetDeterminedTopSet(state, remainingSlots, out ulong determinedTopMask))
            return StrategyNode.Terminal(stateId, ComparisonState.MaskToOrderedList(fixedTopMask | determinedTopMask));

        if (state.ActiveCount <= remainingSlots)
            return StrategyNode.Terminal(stateId, ComparisonState.MaskToOrderedList(fixedTopMask | state.ActiveMask));

        var possibleCandidates = GetPossibleCandidates(state);
        if (state.ActiveCount <= _m)
        {
            return StrategyNode.Decision(
                stateId,
                step,
                possibleCandidates,
                Array.Empty<StrategyBranch>(),
                new FinalChoiceSummary(
                    ComparisonState.MaskToOrderedList(fixedTopMask),
                    possibleCandidates,
                    remainingSlots));
        }

        if (_expandedStates.Contains(displayKey))
            return StrategyNode.Reference(stateId);

        _expandedStates.Add(displayKey);

        var group = ChooseGroup(state, remainingSlots);
        var branches = BuildBranches(state, fixedTopMask, remainingSlots, group, step + 1);

        return StrategyNode.Decision(stateId, step, group, branches);
    }

    private List<StrategyBranch> BuildBranches(ComparisonState state, ulong fixedTopMask, int remainingSlots, IReadOnlyList<int> group, int nextStep)
    {
        ThrowIfCancellationRequested();
        var groupedBranches = new Dictionary<IntSequenceKey, BranchInfo>();
        foreach (var order in EnumerateFeasibleOrders(state, group))
        {
            ThrowIfCancellationRequested();
            var next = state.Clone();
            next.ApplyOrder(order);
            next.Eliminate(remainingSlots);

            ulong nextFixedTopMask = fixedTopMask;
            int nextRemainingSlots = remainingSlots;
            NormalizeState(next, ref nextFixedTopMask, ref nextRemainingSlots);

            IntSequenceKey nextKey = GetDisplayStateKey(next, nextFixedTopMask);
            if (!groupedBranches.TryGetValue(nextKey, out BranchInfo? branch))
            {
                groupedBranches[nextKey] = new BranchInfo(next, nextFixedTopMask, nextRemainingSlots, order);
            }
            else
            {
                branch.EquivalentOrders.Add(order.ToArray());
            }
        }

        return groupedBranches.Values
            .OrderBy(v => v.RepresentativeOrder, StringComparer.Ordinal)
            .Select(v => new StrategyBranch(
                v.RepresentativeOrder,
                BuildEquivalentOrderSummary(v.RepresentativeOrderItems, v.EquivalentOrders),
                BuildComparisonEffect(state, fixedTopMask, v.NextState, v.NextFixedTopMask),
                BuildState(v.NextState, v.NextFixedTopMask, v.NextRemainingSlots, nextStep)))
            .ToList();
    }

    private StrategyEffect BuildComparisonEffect(ComparisonState before, ulong beforeFixedTopMask, ComparisonState after, ulong afterFixedTopMask)
    {
        var newlyGuaranteedTop = ComparisonState.MaskToOrderedList(afterFixedTopMask & ~beforeFixedTopMask);
        var newlyExcluded = ComparisonState.MaskToOrderedList(before.ActiveMask & ~after.ActiveMask & ~afterFixedTopMask);
        var fixedCandidates = ComparisonState.MaskToOrderedList(afterFixedTopMask);
        var possibleCandidates = after.GetActiveItemsOrdered();

        return new StrategyEffect(newlyGuaranteedTop, newlyExcluded, fixedCandidates, possibleCandidates);
    }

    private ulong GetGuaranteedTopMask(ComparisonState state, int remainingSlots)
    {
        ThrowIfCancellationRequested();
        ulong mask = 0;
        for (int i = 0; i < _n; i++)
        {
            if (state.IsActive(i) && state.ActiveCount - 1 - state.GetDescendantCount(i) <= remainingSlots - 1)
                mask |= 1UL << i;
        }

        return mask;
    }

    private List<int> GetPossibleCandidates(ComparisonState state)
    {
        return state.GetActiveItemsOrdered();
    }

    private List<int> ChooseGroup(ComparisonState state, int remainingSlots)
    {
        ThrowIfCancellationRequested();
        var candidates = state.GetActiveItemsOrdered();
        int groupSize = Math.Min(_m, candidates.Count);
        SearchStateKey currentKey = GetSearchStateKey(state, remainingSlots);
        var labels = state.GetStructuralLabels();

        if (_bestGroupPatternCache.TryGetValue(currentKey, out BestGroupPattern cachedPattern))
        {
            foreach (var group in EnumerateCombinations(candidates, cachedPattern.GroupSize))
            {
                if (GetGroupPattern(group, labels) == cachedPattern.Pattern)
                    return group;
            }
        }

        List<int>? bestGroup = null;
        (int negWorstCaseSteps, int negFreshItems, int negUnrelatedScore, int negGroupSize, int distinctStates, int totalReduction, int unresolvedPairs) bestScore =
            (int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue);

        ThrowIfCancellationRequested();
        // Under the current cost model, a size-m comparison weakly dominates any smaller
        // non-terminal comparison because it costs the same one step and reveals a superset
        // of ordering information.
        var seenGroupPatterns = new HashSet<IntSequenceKey>();
        foreach (var group in EnumerateCombinations(candidates, groupSize))
        {
            ThrowIfCancellationRequested();
            if (!seenGroupPatterns.Add(GetGroupPattern(group, labels)))
                continue;

            var nextStateKeys = new HashSet<SearchStateKey>();
            int worstCaseSteps = 0;
            int totalReduction = 0;
            bool isUseful = false;
            int bestKnownWorstCase = bestGroup is null ? int.MaxValue : -bestScore.negWorstCaseSteps;

            foreach (var order in EnumerateFeasibleOrders(state, group))
            {
                ThrowIfCancellationRequested();
                var next = state.Clone();
                next.ApplyOrder(order);
                next.Eliminate(remainingSlots);

                ulong ignoredFixedTopMask = 0;
                int nextRemainingSlots = remainingSlots;
                NormalizeState(next, ref ignoredFixedTopMask, ref nextRemainingSlots);

                SearchStateKey nextKey = GetSearchStateKey(next, nextRemainingSlots);
                if (nextKey.Equals(currentKey))
                    continue;

                isUseful = true;
                int reduction = state.ActiveCount - next.ActiveCount;
                totalReduction += reduction;
                nextStateKeys.Add(nextKey);

                int branchLowerBound = 1 + GetMinWorstCaseLowerBound(next, nextRemainingSlots);
                if (branchLowerBound > bestKnownWorstCase)
                {
                    worstCaseSteps = branchLowerBound;
                    break;
                }

                int branchSteps = 1 + GetMinWorstCaseSteps(next, nextRemainingSlots);
                worstCaseSteps = Math.Max(worstCaseSteps, branchSteps);
            }

            if (!isUseful)
                continue;

            int freshItems = group.Count(i => state.GetAncestorCount(i) == 0 && state.GetDescendantCount(i) == 0);
            int unrelatedScore = -group.Sum(i => state.GetAncestorCount(i) + state.GetDescendantCount(i));
            int unresolvedPairs = CountUnresolvedPairs(state, group);
            var score = (-worstCaseSteps, freshItems, unrelatedScore, group.Count, nextStateKeys.Count, totalReduction, unresolvedPairs);

            if (bestGroup is null || score.CompareTo(bestScore) > 0)
            {
                bestGroup = group;
                bestScore = score;
            }
        }

        if (bestGroup is not null)
        {
            _bestGroupPatternCache[currentKey] = new BestGroupPattern(bestGroup.Count, GetGroupPattern(bestGroup, labels));
            return bestGroup;
        }

        return candidates.Take(groupSize).ToList();
    }

    private int GetMinWorstCaseSteps(ComparisonState state, int remainingSlots)
    {
        ThrowIfCancellationRequested();
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);
        ObserveSearchState(state, remainingSlots);

        if (remainingSlots == 0)
            return 0;

        if (TryGetDeterminedTopSet(state, remainingSlots, out _))
            return 0;

        if (state.ActiveCount <= remainingSlots)
            return 0;

        if (state.ActiveCount <= _m)
            return 1;

        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        if (_minWorstCaseStepsCache.TryGetValue(key, out int cached))
            return cached;

        EnterSearchState();

        var candidates = state.GetActiveItemsOrdered();
        int groupSize = Math.Min(_m, candidates.Count);
        var labels = state.GetStructuralLabels();
        int bestWorstCase = int.MaxValue;
        try
        {
            ThrowIfCancellationRequested();
            var seenGroupPatterns = new HashSet<IntSequenceKey>();
            foreach (var group in EnumerateCombinations(candidates, groupSize))
            {
                ThrowIfCancellationRequested();
                if (!seenGroupPatterns.Add(GetGroupPattern(group, labels)))
                    continue;

                int groupWorstCase = 0;
                bool isUseful = false;

                foreach (var order in EnumerateFeasibleOrders(state, group))
                {
                    ThrowIfCancellationRequested();
                    var next = state.Clone();
                    next.ApplyOrder(order);
                    next.Eliminate(remainingSlots);

                    ulong nextIgnoredFixedTopMask = 0;
                    int nextRemainingSlots = remainingSlots;
                    NormalizeState(next, ref nextIgnoredFixedTopMask, ref nextRemainingSlots);

                    SearchStateKey nextKey = GetSearchStateKey(next, nextRemainingSlots);
                    if (nextKey.Equals(key))
                        continue;

                    isUseful = true;
                    int branchLowerBound = 1 + GetMinWorstCaseLowerBound(next, nextRemainingSlots);
                    if (branchLowerBound >= bestWorstCase)
                    {
                        groupWorstCase = branchLowerBound;
                        break;
                    }

                    int branchSteps = 1 + GetMinWorstCaseSteps(next, nextRemainingSlots);
                    groupWorstCase = Math.Max(groupWorstCase, branchSteps);

                    if (groupWorstCase >= bestWorstCase)
                        break;
                }

                if (isUseful)
                    bestWorstCase = Math.Min(bestWorstCase, groupWorstCase);
            }
        }
        finally
        {
            ExitSearchState();
        }

        if (bestWorstCase == int.MaxValue)
            bestWorstCase = 0;

        _minWorstCaseStepsCache[key] = bestWorstCase;
        return bestWorstCase;
    }

    private int GetMinWorstCaseLowerBound(ComparisonState state, int remainingSlots)
    {
        ThrowIfCancellationRequested();
        ulong ignoredFixedTopMask = 0;
        NormalizeState(state, ref ignoredFixedTopMask, ref remainingSlots);
        ObserveSearchState(state, remainingSlots);

        if (remainingSlots == 0)
            return 0;

        if (TryGetDeterminedTopSet(state, remainingSlots, out _))
            return 0;

        if (state.ActiveCount <= remainingSlots)
            return 0;

        if (state.ActiveCount <= _m)
            return 1;

        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        if (_lowerBoundStepsCache.TryGetValue(key, out int cached))
            return cached;

        FeasibleTopSetInfo info = GetFeasibleTopSetInfo(state, remainingSlots);
        int maxOutcomesPerStep = GetMaxOutcomesPerStep(state);
        int distinguishable = 1;
        int steps = 0;
        while (distinguishable < info.Count)
        {
            ThrowIfCancellationRequested();
            steps++;
            checked
            {
                distinguishable *= maxOutcomesPerStep;
            }
        }

        _lowerBoundStepsCache[key] = steps;
        return steps;
    }

    private bool TryGetDeterminedTopSet(ComparisonState state, int remainingSlots, out ulong topMask)
    {
        ThrowIfCancellationRequested();
        FeasibleTopSetInfo info = GetFeasibleTopSetInfo(state, remainingSlots);
        if (info.Count == 1 && TryGetUniqueTopSetMask(state, remainingSlots, out ulong uniqueMask))
        {
            topMask = uniqueMask;
            return true;
        }

        topMask = 0;
        return false;
    }

    private FeasibleTopSetInfo GetFeasibleTopSetInfo(ComparisonState state, int remainingSlots)
    {
        ThrowIfCancellationRequested();
        SearchStateKey key = GetSearchStateKey(state, remainingSlots);
        _visitedSearchStates.Add(key);
        if (_feasibleTopSetCache.TryGetValue(key, out FeasibleTopSetInfo cached))
            return cached;

        int count = CountFeasibleTopSets(state, remainingSlots);
        FeasibleTopSetInfo info = new(count);

        _feasibleTopSetCache[key] = info;
        return info;
    }

    private bool TryGetUniqueTopSetMask(ComparisonState state, int remainingSlots, out ulong uniqueMask)
    {
        ThrowIfCancellationRequested();
        uniqueMask = 0;
        bool found = false;
        var possibleCandidates = GetPossibleCandidates(state);
        foreach (var combination in EnumerateCombinations(possibleCandidates, remainingSlots))
        {
            ThrowIfCancellationRequested();
            ulong candidateMask = 0;
            foreach (int item in combination)
                candidateMask |= 1UL << item;

            if (!IsFeasibleTopSet(state, candidateMask))
                continue;

            if (found)
            {
                uniqueMask = 0;
                return false;
            }

            uniqueMask = candidateMask;
            found = true;
        }

        return found;
    }

    private int CountFeasibleTopSets(ComparisonState state, int remainingSlots)
    {
        ThrowIfCancellationRequested();
        int count = 0;
        var possibleCandidates = GetPossibleCandidates(state);
        foreach (var combination in EnumerateCombinations(possibleCandidates, remainingSlots))
        {
            ThrowIfCancellationRequested();
            ulong candidateMask = 0;
            foreach (int item in combination)
                candidateMask |= 1UL << item;

            if (IsFeasibleTopSet(state, candidateMask))
                count++;
        }

        return count;
    }

    private bool IsFeasibleTopSet(ComparisonState state, ulong candidateMask)
    {
        ThrowIfCancellationRequested();
        foreach (int item in ComparisonState.MaskToOrderedList(candidateMask))
        {
            ulong activeAncestorsOutsideSet = state.Ancestors[item] & state.ActiveMask & ~candidateMask;
            if (activeAncestorsOutsideSet != 0)
                return false;
        }

        return true;
    }

    private int GetMaxOutcomesPerStep(ComparisonState state)
    {
        int maxGroupSize = Math.Min(_m, state.ActiveCount);
        int outcomes = 1;
        for (int i = 2; i <= maxGroupSize; i++)
            outcomes *= i;
        return outcomes;
    }

    private static IntSequenceKey GetGroupPattern(IReadOnlyList<int> group, IReadOnlyList<int> labels)
    {
        return new IntSequenceKey(group.Select(i => labels[i]).OrderBy(x => x).ToArray());
    }

    private IEnumerable<List<int>> EnumerateFeasibleOrders(ComparisonState state, IReadOnlyList<int> group)
    {
        ThrowIfCancellationRequested();
        var remaining = new HashSet<int>(group);
        var current = new List<int>(group.Count);

        foreach (var order in EnumerateFeasibleOrders(state, remaining, current))
            yield return order;
    }

    private IEnumerable<List<int>> EnumerateFeasibleOrders(
        ComparisonState state,
        HashSet<int> remaining,
        List<int> current)
    {
        ThrowIfCancellationRequested();
        if (remaining.Count == 0)
        {
            yield return new List<int>(current);
            yield break;
        }

        var nextChoices = remaining
            .Where(candidate => remaining.All(other => other == candidate || !state.HasAncestor(candidate, other)))
            .OrderBy(x => x)
            .ToList();

        foreach (int next in nextChoices)
        {
            ThrowIfCancellationRequested();
            remaining.Remove(next);
            current.Add(next);

            foreach (var order in EnumerateFeasibleOrders(state, remaining, current))
                yield return order;

            current.RemoveAt(current.Count - 1);
            remaining.Add(next);
        }
    }

    private static int CountUnresolvedPairs(ComparisonState state, IReadOnlyList<int> group)
    {
        int count = 0;
        for (int i = 0; i < group.Count - 1; i++)
        {
            for (int j = i + 1; j < group.Count; j++)
            {
                int a = group[i];
                int b = group[j];
                if (!state.HasAncestor(a, b) && !state.HasAncestor(b, a))
                    count++;
            }
        }

        return count;
    }

    private IEnumerable<List<int>> EnumerateCombinations(IReadOnlyList<int> items, int count)
    {
        ThrowIfCancellationRequested();
        var current = new List<int>(count);
        foreach (var combination in EnumerateCombinations(items, count, 0, current))
            yield return combination;
    }

    private IEnumerable<List<int>> EnumerateCombinations(
        IReadOnlyList<int> items,
        int count,
        int start,
        List<int> current)
    {
        ThrowIfCancellationRequested();
        if (current.Count == count)
        {
            yield return new List<int>(current);
            yield break;
        }

        for (int i = start; i <= items.Count - (count - current.Count); i++)
        {
            ThrowIfCancellationRequested();
            current.Add(items[i]);
            foreach (var combination in EnumerateCombinations(items, count, i + 1, current))
                yield return combination;
            current.RemoveAt(current.Count - 1);
        }
    }

    private int GetStateId(IntSequenceKey key)
    {
        ThrowIfCancellationRequested();
        if (_stateIds.TryGetValue(key, out int id))
            return id;

        id = _nextStateId++;
        _stateIds[key] = id;
        return id;
    }

    private SearchStateKey GetSearchStateKey(ComparisonState state, int remainingSlots)
    {
        return new SearchStateKey(remainingSlots, state.GetCanonicalKey());
    }

    private IntSequenceKey GetDisplayStateKey(ComparisonState state, ulong fixedTopMask)
    {
        return state.GetDisplayCanonicalKey(fixedTopMask);
    }

    private void NormalizeState(ComparisonState state, ref ulong fixedTopMask, ref int remainingSlots)
    {
        while (remainingSlots > 0)
        {
            ulong guaranteedTopMask = GetGuaranteedTopMask(state, remainingSlots);
            if (guaranteedTopMask == 0)
                break;

            fixedTopMask |= guaranteedTopMask;
            remainingSlots -= BitOperations.PopCount(guaranteedTopMask);
            state.Deactivate(guaranteedTopMask);
        }
    }

    private static string FormatOrder(IEnumerable<int> items)
    {
        return string.Join(" > ", items.Select(i => $"#{i + 1}"));
    }

    private static EquivalentOrderSummary? BuildEquivalentOrderSummary(
        IReadOnlyList<int> representativeOrder,
        IReadOnlyList<IReadOnlyList<int>> equivalentOrders)
    {
        if (equivalentOrders.Count == 0)
            return null;

        var allOrders = new List<IReadOnlyList<int>>(equivalentOrders.Count + 1) { representativeOrder };
        allOrders.AddRange(equivalentOrders);

        var positionMasks = new Dictionary<int, ulong>();
        foreach (var order in allOrders)
        {
            for (int position = 0; position < order.Count; position++)
            {
                int item = order[position];
                positionMasks[item] = positionMasks.GetValueOrDefault(item) | (1UL << position);
            }
        }

        var representativePositions = representativeOrder
            .Select((item, index) => (item, index))
            .ToDictionary(x => x.item, x => x.index);

        var positionSets = Enumerable.Range(0, representativeOrder.Count)
            .Select(position => allOrders
                .Select(order => order[position])
                .Distinct()
                .OrderBy(item => representativePositions[item])
                .ToList())
            .ToList();

        string patternText = BuildEquivalentPatternText(positionSets);
        string formula = BuildEquivalentCountFormula(positionSets);
        return new EquivalentOrderSummary(equivalentOrders.Count, patternText, formula);
    }

    private static string BuildEquivalentPatternText(IReadOnlyList<List<int>> positionSets)
    {
        var tokens = new List<string>();
        var seenPoolOccurrences = new Dictionary<string, int>();
        var totalPoolOccurrences = positionSets
            .Where(set => set.Count > 1)
            .GroupBy(FormatBraceSet)
            .ToDictionary(group => group.Key, group => group.Count());

        int position = 0;
        while (position < positionSets.Count)
        {
            var currentSet = positionSets[position];
            if (currentSet.Count == 1)
            {
                tokens.Add($"#{currentSet[0] + 1}");
                position++;
                continue;
            }

            string poolKey = FormatBraceSet(currentSet);
            int segmentLength = 1;
            while (position + segmentLength < positionSets.Count &&
                positionSets[position + segmentLength].SequenceEqual(currentSet))
            {
                segmentLength++;
            }

            int seenBefore = seenPoolOccurrences.GetValueOrDefault(poolKey);
            int totalOccurrences = totalPoolOccurrences[poolKey];
            int remainingAfter = totalOccurrences - seenBefore - segmentLength;
            tokens.Add(FormatPoolSegment(currentSet, segmentLength, seenBefore, remainingAfter));
            seenPoolOccurrences[poolKey] = seenBefore + segmentLength;
            position += segmentLength;
        }

        return string.Join(" > ", tokens);
    }

    private static string BuildEquivalentCountFormula(IReadOnlyList<List<int>> positionSets)
    {
        var terms = new List<string>();
        var seenPoolOccurrences = new Dictionary<string, int>();
        var totalPoolOccurrences = positionSets
            .Where(set => set.Count > 1)
            .GroupBy(FormatBraceSet)
            .ToDictionary(group => group.Key, group => group.Count());

        int position = 0;
        while (position < positionSets.Count)
        {
            var currentSet = positionSets[position];
            if (currentSet.Count == 1)
            {
                position++;
                continue;
            }

            string poolKey = FormatBraceSet(currentSet);
            int segmentLength = 1;
            while (position + segmentLength < positionSets.Count &&
                positionSets[position + segmentLength].SequenceEqual(currentSet))
            {
                segmentLength++;
            }

            int seenBefore = seenPoolOccurrences.GetValueOrDefault(poolKey);
            int totalOccurrences = totalPoolOccurrences[poolKey];
            int available = totalOccurrences - seenBefore;
            terms.Add(FormatSegmentFactor(available, segmentLength));
            seenPoolOccurrences[poolKey] = seenBefore + segmentLength;
            position += segmentLength;
        }

        return $"{string.Join(" x ", terms)} - 1";
    }

    private static string FormatPoolSegment(IReadOnlyList<int> items, int segmentLength, int seenBefore, int remainingAfter)
    {
        string setText = FormatBraceSet(items);
        if (segmentLength == items.Count && seenBefore == 0 && remainingAfter == 0)
            return $"permute {setText}";

        if (seenBefore == 0 && remainingAfter == 0)
            return $"{segmentLength} of {setText} in any order";

        if (seenBefore == 0)
            return segmentLength == 1
                ? $"1 of {setText}"
                : $"{segmentLength} of {setText} in any order";

        if (remainingAfter == 0)
            return $"remaining {segmentLength} of {setText} in any order";

        return $"{segmentLength} of remaining {setText} in any order";
    }

    private static string FormatSegmentFactor(int available, int segmentLength)
    {
        if (segmentLength == available)
            return $"{available}!";

        return string.Join(" x ", Enumerable.Range(0, segmentLength).Select(offset => (available - offset).ToString()));
    }

    private static string FormatBraceSet(IEnumerable<int> items)
    {
        return "{" + string.Join(", ", items.Select(i => $"#{i + 1}")) + "}";
    }

    private void EnterSearchState()
    {
        _pendingStates++;
        _peakPendingStates = Math.Max(_peakPendingStates, _pendingStates);
        ReportProgress();
    }

    private void ExitSearchState()
    {
        _pendingStates--;
        ReportProgress();
    }

    private SearchStatistics CreateSearchStatistics()
    {
        _searchedStates = _visitedSearchStates.Count;
        return new SearchStatistics(
            _searchedStates,
            _pendingStates,
            _peakPendingStates,
            _stateIds.Count,
            _expandedStates.Count,
            _lowerBoundStepsCache.Count,
            _feasibleTopSetCache.Count);
    }

    private void ReportProgress(bool force = false)
    {
        if (_progressCallback is null)
            return;

        _searchedStates = _visitedSearchStates.Count;
        long elapsedMs = _progressStopwatch.ElapsedMilliseconds;
        if (!force && elapsedMs - _lastProgressReportMs < ProgressReportIntervalMs)
            return;

        _lastProgressReportMs = elapsedMs;
        _progressCallback(new SearchProgressSnapshot(
            _searchedStates,
            _pendingStates,
            _peakPendingStates,
            _stateIds.Count));
    }

    private void ObserveSearchState(ComparisonState state, int remainingSlots)
    {
        _visitedSearchStates.Add(GetSearchStateKey(state, remainingSlots));
    }

    private void ThrowIfCancellationRequested()
    {
        _cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class BranchInfo
    {
        public ComparisonState NextState { get; }
        public ulong NextFixedTopMask { get; }
        public int NextRemainingSlots { get; }
        public IReadOnlyList<int> RepresentativeOrderItems { get; }
        public string RepresentativeOrder { get; }
        public List<IReadOnlyList<int>> EquivalentOrders { get; }

        public BranchInfo(ComparisonState nextState, ulong nextFixedTopMask, int nextRemainingSlots, IReadOnlyList<int> representativeOrderItems)
        {
            NextState = nextState;
            NextFixedTopMask = nextFixedTopMask;
            NextRemainingSlots = nextRemainingSlots;
            RepresentativeOrderItems = representativeOrderItems.ToArray();
            RepresentativeOrder = FormatOrder(RepresentativeOrderItems);
            EquivalentOrders = new List<IReadOnlyList<int>>();
        }
    }
}
