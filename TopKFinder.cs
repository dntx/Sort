using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
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
        foreach (var orderFamily in EnumerateFeasibleOrderFamilies(state, group))
        {
            ThrowIfCancellationRequested();
            var next = state.Clone();
            next.ApplyOrder(orderFamily.RepresentativeOrderItems);
            next.Eliminate(remainingSlots);

            ulong nextFixedTopMask = fixedTopMask;
            int nextRemainingSlots = remainingSlots;
            NormalizeState(next, ref nextFixedTopMask, ref nextRemainingSlots);

            IntSequenceKey nextKey = GetDisplayStateKey(next, nextFixedTopMask);
            if (!groupedBranches.TryGetValue(nextKey, out BranchInfo? branch))
            {
                groupedBranches[nextKey] = new BranchInfo(next, nextFixedTopMask, nextRemainingSlots, orderFamily);
            }
            else
            {
                branch.OrderFamilies.Add(orderFamily);
            }
        }

        return groupedBranches.Values
            .OrderBy(v => v.RepresentativeOrder, StringComparer.Ordinal)
            .Select(v => new StrategyBranch(
                v.RepresentativeOrder,
                BuildEquivalentOrderSummary(v.OrderFamilies),
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

            foreach (var orderFamily in EnumerateFeasibleOrderFamilies(state, group))
            {
                ThrowIfCancellationRequested();
                var next = state.Clone();
                next.ApplyOrder(orderFamily.RepresentativeOrderItems);
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

                foreach (var orderFamily in EnumerateFeasibleOrderFamilies(state, group))
                {
                    ThrowIfCancellationRequested();
                    var next = state.Clone();
                    next.ApplyOrder(orderFamily.RepresentativeOrderItems);
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

    private IEnumerable<OrderFamilyDescriptor> EnumerateFeasibleOrderFamilies(ComparisonState state, IReadOnlyList<int> group)
    {
        ThrowIfCancellationRequested();
        GroupSymmetryInfo symmetryInfo = BuildGroupSymmetryInfo(state, group);
        if (symmetryInfo.Classes.All(@class => @class.Items.Length == 1))
        {
            var remaining = new HashSet<int>(group);
            var current = new List<int>(group.Count);

            foreach (var order in EnumerateFeasibleOrders(state, remaining, current))
                yield return OrderFamilyDescriptor.CreateSingleton(order);
            yield break;
        }

        foreach (var family in EnumerateSymmetricOrderFamilies(symmetryInfo))
            yield return family;
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

    private GroupSymmetryInfo BuildGroupSymmetryInfo(ComparisonState state, IReadOnlyList<int> group)
    {
        ulong activeMask = state.ActiveMask;
        var groupedItems = group
            .GroupBy(item => new SymmetrySignature(state.Ancestors[item] & activeMask, state.Descendants[item] & activeMask))
            .OrderBy(grouping => grouping.Min())
            .ToList();

        var classes = groupedItems
            .Select((grouping, index) =>
            {
                int[] items = grouping.OrderBy(item => item).ToArray();
                return new GroupSymmetryClass(index, items, state.Ancestors[items[0]] & activeMask);
            })
            .ToList();

        var itemToClassIndex = new Dictionary<int, int>(group.Count);
        foreach (var @class in classes)
        {
            foreach (int item in @class.Items)
                itemToClassIndex[item] = @class.Index;
        }

        return new GroupSymmetryInfo(classes, itemToClassIndex);
    }

    private IEnumerable<OrderFamilyDescriptor> EnumerateSymmetricOrderFamilies(GroupSymmetryInfo symmetryInfo)
    {
        ThrowIfCancellationRequested();
        BigInteger multiplicity = BigInteger.One;
        foreach (var @class in symmetryInfo.Classes)
            multiplicity *= Factorial(@class.Items.Length);

        ulong remainingMask = 0;
        foreach (var @class in symmetryInfo.Classes)
        {
            foreach (int item in @class.Items)
                remainingMask |= 1UL << item;
        }

        var nextItemIndices = symmetryInfo.Classes.Select(_ => 0).ToArray();
        var remainingCounts = symmetryInfo.Classes.Select(@class => @class.Items.Length).ToArray();
        var classSequence = new List<int>(symmetryInfo.Classes.Sum(@class => @class.Items.Length));
        var representativeOrder = new List<int>(classSequence.Capacity);

        foreach (var family in EnumerateSymmetricOrderFamilies(
            symmetryInfo,
            remainingMask,
            nextItemIndices,
            remainingCounts,
            classSequence,
            representativeOrder,
            multiplicity))
        {
            yield return family;
        }
    }

    private IEnumerable<OrderFamilyDescriptor> EnumerateSymmetricOrderFamilies(
        GroupSymmetryInfo symmetryInfo,
        ulong remainingMask,
        int[] nextItemIndices,
        int[] remainingCounts,
        List<int> classSequence,
        List<int> representativeOrder,
        BigInteger multiplicity)
    {
        ThrowIfCancellationRequested();
        if (remainingMask == 0)
        {
            yield return OrderFamilyDescriptor.CreateSymmetric(
                representativeOrder,
                BuildSymmetricFamilyPatternText(symmetryInfo, classSequence),
                BuildMultiplicityFormula(symmetryInfo.Classes.Select(@class => @class.Items.Length)),
                checked((int)multiplicity),
                symmetryInfo.Classes.Select(@class => (IReadOnlyList<int>)@class.Items).ToList(),
                classSequence.ToArray());
            yield break;
        }

        foreach (var @class in symmetryInfo.Classes)
        {
            if (remainingCounts[@class.Index] == 0 || (@class.AncestorMask & remainingMask) != 0)
                continue;

            int item = @class.Items[nextItemIndices[@class.Index]];
            nextItemIndices[@class.Index]++;
            remainingCounts[@class.Index]--;
            classSequence.Add(@class.Index);
            representativeOrder.Add(item);

            foreach (var family in EnumerateSymmetricOrderFamilies(
                symmetryInfo,
                remainingMask & ~(1UL << item),
                nextItemIndices,
                remainingCounts,
                classSequence,
                representativeOrder,
                multiplicity))
            {
                yield return family;
            }

            representativeOrder.RemoveAt(representativeOrder.Count - 1);
            classSequence.RemoveAt(classSequence.Count - 1);
            remainingCounts[@class.Index]++;
            nextItemIndices[@class.Index]--;
        }
    }

    private static string BuildSymmetricFamilyPatternText(GroupSymmetryInfo symmetryInfo, IReadOnlyList<int> classSequence)
    {
        if (symmetryInfo.Classes.Count == 1)
            return $"permute {FormatBraceSet(symmetryInfo.Classes[0].Items)}";

        return BuildPermutationTemplateText(
            symmetryInfo.Classes.Select(@class => (IReadOnlyList<int>)@class.Items).ToList(),
            classSequence);
    }

    private static string BuildMultiplicityFormula(IEnumerable<int> classSizes)
    {
        string formula = string.Join(" x ", classSizes
            .Where(size => size > 1)
            .Select(size => $"{size}!"));
        return string.IsNullOrEmpty(formula) ? "1" : formula;
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
        IReadOnlyList<OrderFamilyDescriptor> orderFamilies)
    {
        if (orderFamilies.Count == 0)
            return null;

        int totalCount = orderFamilies.Sum(family => family.Count);
        if (totalCount <= 1)
            return null;

        string patternText = orderFamilies.Count == 1
            ? orderFamilies[0].PatternText
            : "(" + string.Join(" | ", orderFamilies.Select(family => family.PatternText)) + ")";
        string countFormula = $"{CombineFormulaParts(orderFamilies.Select(family => family.CountFormula).ToList())} - 1";
        return new EquivalentOrderSummary(totalCount - 1, patternText, countFormula);
    }

    private static string FormatBraceSet(IEnumerable<int> items)
    {
        return "{" + string.Join(", ", items.Select(i => $"#{i + 1}")) + "}";
    }

    private static EquivalentPatternSummary BuildEquivalentPatternSummary(
        IReadOnlyList<IReadOnlyList<int>> orders,
        IReadOnlyList<int> remainingItems,
        IReadOnlyDictionary<int, int> representativePositions)
    {
        if (remainingItems.Count == 0)
            return new EquivalentPatternSummary(string.Empty, "1", BigInteger.One);

        BigInteger fullPermutationCount = Factorial(remainingItems.Count);
        if (orders.Count == (int)fullPermutationCount)
        {
            if (remainingItems.Count == 1)
            {
                int item = remainingItems[0];
                return new EquivalentPatternSummary($"#{item + 1}", "1", BigInteger.One);
            }

            return new EquivalentPatternSummary(
                $"permute {FormatBraceSet(remainingItems)}",
                $"{remainingItems.Count}!",
                fullPermutationCount);
        }

        int commonPrefixLength = GetCommonPrefixLength(orders);
        int commonSuffixLength = GetCommonSuffixLength(orders, commonPrefixLength);
        if (commonPrefixLength > 0 || commonSuffixLength > 0)
        {
            var prefixItems = orders[0].Take(commonPrefixLength).ToArray();
            var suffixItems = commonSuffixLength == 0
                ? Array.Empty<int>()
                : orders[0].Skip(orders[0].Count - commonSuffixLength).ToArray();
            var middleOrders = orders
                .Select(order => order
                    .Skip(commonPrefixLength)
                    .Take(order.Count - commonPrefixLength - commonSuffixLength)
                    .ToArray())
                .ToList();
            var middleItems = orders[0]
                .Skip(commonPrefixLength)
                .Take(orders[0].Count - commonPrefixLength - commonSuffixLength)
                .ToArray();

            EquivalentPatternSummary middleSummary = BuildEquivalentPatternSummary(middleOrders, middleItems, representativePositions);
            string patternText = JoinPatternSegments(
                prefixItems.Select(item => $"#{item + 1}")
                    .Concat(string.IsNullOrEmpty(middleSummary.PatternText) ? Array.Empty<string>() : new[] { middleSummary.PatternText })
                    .Concat(suffixItems.Select(item => $"#{item + 1}")));
            return new EquivalentPatternSummary(patternText, middleSummary.TotalCountFormula, middleSummary.TotalCount);
        }

        EquivalentPatternSummary? permutationTemplateSummary = TryBuildPermutationTemplateSummary(orders, remainingItems, representativePositions);
        if (permutationTemplateSummary is not null)
            return permutationTemplateSummary;

        EquivalentPatternSummary? independentBlockSummary = TryBuildIndependentBlockSummary(orders, remainingItems, representativePositions);
        if (independentBlockSummary is not null)
            return independentBlockSummary;

        EquivalentPatternSummary? partialIndependentBlockSummary = TryBuildPartialIndependentBlockSummary(orders, representativePositions);
        if (partialIndependentBlockSummary is not null)
            return partialIndependentBlockSummary;

        EquivalentPatternSummary? anchoredPermutationSummary = TryBuildAnchoredPermutationSummary(orders, remainingItems);
        if (anchoredPermutationSummary is not null)
            return anchoredPermutationSummary;

        EquivalentPatternSummary? windowPermutationFamilySummary = TryBuildWindowPermutationFamilySummary(orders, representativePositions);
        if (windowPermutationFamilySummary is not null)
            return windowPermutationFamilySummary;

        var groups = orders
            .GroupBy(order => order[0])
            .OrderBy(group => representativePositions[group.Key])
            .ToList();

        if (groups.Count == 1)
        {
            var onlyGroup = groups[0];
            int item = onlyGroup.Key;
            var nextRemaining = remainingItems.Where(candidate => candidate != item).ToArray();
            var childOrders = onlyGroup.Select(order => order.Skip(1).ToArray()).ToList();
            EquivalentPatternSummary childSummary = BuildEquivalentPatternSummary(childOrders, nextRemaining, representativePositions);
            string itemText = $"#{item + 1}";
            string patternText = JoinPatternSegments(new[] { itemText, childSummary.PatternText });
            return new EquivalentPatternSummary(patternText, childSummary.TotalCountFormula, childSummary.TotalCount);
        }

        var patternParts = new List<string>(groups.Count);
        var formulaParts = new List<string>(groups.Count);
        BigInteger totalCount = BigInteger.Zero;
        foreach (var group in groups)
        {
            int item = group.Key;
            var nextRemaining = remainingItems.Where(candidate => candidate != item).ToArray();
            var childOrders = group.Select(order => order.Skip(1).ToArray()).ToList();
            EquivalentPatternSummary childSummary = BuildEquivalentPatternSummary(childOrders, nextRemaining, representativePositions);
            string itemText = $"#{item + 1}";
            patternParts.Add(JoinPatternSegments(new[] { itemText, childSummary.PatternText }));
            formulaParts.Add(childSummary.TotalCountFormula);
            totalCount += childSummary.TotalCount;
        }

        string pattern = "(" + string.Join(" | ", patternParts) + ")";
        string formula = CombineFormulaParts(formulaParts);
        return new EquivalentPatternSummary(pattern, formula, totalCount);
    }

    private static int GetCommonPrefixLength(IReadOnlyList<IReadOnlyList<int>> orders)
    {
        int length = 0;
        while (length < orders[0].Count && orders.All(order => order[length] == orders[0][length]))
            length++;
        return length;
    }

    private static EquivalentPatternSummary? TryBuildPermutationTemplateSummary(
        IReadOnlyList<IReadOnlyList<int>> orders,
        IReadOnlyList<int> remainingItems,
        IReadOnlyDictionary<int, int> representativePositions)
    {
        if (remainingItems.Count < 4)
            return null;

        PermutationTemplateCandidate? bestCandidate = null;
        foreach (var partition in EnumeratePartitions(remainingItems.OrderBy(item => representativePositions[item]).ToArray()))
        {
            int multiBlockCount = partition.Count(block => block.Count > 1);
            if (multiBlockCount == 0)
                continue;

            if (multiBlockCount == 1 && partition.Count == 1)
                continue;

            var blockLookup = new Dictionary<int, int>();
            for (int blockIndex = 0; blockIndex < partition.Count; blockIndex++)
            {
                foreach (int item in partition[blockIndex])
                    blockLookup[item] = blockIndex;
            }

            var template = orders[0].Select(item => blockLookup[item]).ToArray();
            if (!orders.All(order => order.Select(item => blockLookup[item]).SequenceEqual(template)))
                continue;

            var permutationCounts = new int[partition.Count];
            bool valid = true;
            foreach (int blockIndex in Enumerable.Range(0, partition.Count))
            {
                var uniqueProjections = new HashSet<string>(StringComparer.Ordinal);
                foreach (var order in orders)
                {
                    var projection = order.Where(item => blockLookup[item] == blockIndex);
                    uniqueProjections.Add(string.Join(",", projection));
                }

                int expectedCount = partition[blockIndex].Count <= 1 ? 1 : (int)Factorial(partition[blockIndex].Count);
                if (uniqueProjections.Count != expectedCount)
                {
                    valid = false;
                    break;
                }

                permutationCounts[blockIndex] = expectedCount;
            }

            if (!valid)
                continue;

            BigInteger expectedTotal = BigInteger.One;
            foreach (int count in permutationCounts)
                expectedTotal *= count;
            if (orders.Count != (int)expectedTotal)
                continue;

            var combinationKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var order in orders)
            {
                var keyParts = new string[partition.Count];
                for (int blockIndex = 0; blockIndex < partition.Count; blockIndex++)
                    keyParts[blockIndex] = string.Join(",", order.Where(item => blockLookup[item] == blockIndex));
                combinationKeys.Add(string.Join("|", keyParts));
            }

            if (combinationKeys.Count != orders.Count)
                continue;

            var candidate = new PermutationTemplateCandidate(partition, template, permutationCounts);
            if (bestCandidate is null || candidate.CompareTo(bestCandidate) > 0)
                bestCandidate = candidate;
        }

        if (bestCandidate is null)
            return null;

        string patternText = BuildPermutationTemplateText(bestCandidate.Partition, bestCandidate.Template);
        string formula = string.Join(" x ", bestCandidate.PermutationCounts
            .Where(count => count > 1)
            .Select(count => FactorialNotationFromCount(count)));
        if (string.IsNullOrEmpty(formula))
            formula = "1";

        BigInteger totalCount = BigInteger.One;
        foreach (int count in bestCandidate.PermutationCounts)
            totalCount *= count;

        return new EquivalentPatternSummary(patternText, formula, totalCount);
    }

    private static EquivalentPatternSummary? TryBuildIndependentBlockSummary(
        IReadOnlyList<IReadOnlyList<int>> orders,
        IReadOnlyList<int> remainingItems,
        IReadOnlyDictionary<int, int> representativePositions)
    {
        int orderLength = orders[0].Count;
        string fullItemKey = string.Join(",", remainingItems.OrderBy(item => item));

        for (int splitPosition = 1; splitPosition < orderLength; splitPosition++)
        {
            var prefixItems = orders
                .SelectMany(order => order.Take(splitPosition))
                .Distinct()
                .OrderBy(item => representativePositions[item])
                .ToArray();
            var suffixItems = orders
                .SelectMany(order => order.Skip(splitPosition))
                .Distinct()
                .OrderBy(item => representativePositions[item])
                .ToArray();

            if (prefixItems.Length != splitPosition || suffixItems.Length != orderLength - splitPosition)
                continue;

            if (prefixItems.Intersect(suffixItems).Any())
                continue;

            if (string.Join(",", prefixItems.Concat(suffixItems).OrderBy(item => item)) != fullItemKey)
                continue;

            var uniquePrefixes = orders
                .Select(order => order.Take(splitPosition).ToArray())
                .Distinct(ArraySequenceComparer.Instance)
                .ToList();
            var uniqueSuffixes = orders
                .Select(order => order.Skip(splitPosition).ToArray())
                .Distinct(ArraySequenceComparer.Instance)
                .ToList();

            if (orders.Count != uniquePrefixes.Count * uniqueSuffixes.Count)
                continue;

            var fullPairs = new HashSet<string>(orders.Select(order => $"{string.Join(",", order.Take(splitPosition))}|{string.Join(",", order.Skip(splitPosition))}"), StringComparer.Ordinal);
            bool hasAllCombinations = uniquePrefixes.All(prefix =>
                uniqueSuffixes.All(suffix =>
                    fullPairs.Contains($"{string.Join(",", prefix)}|{string.Join(",", suffix)}")));
            if (!hasAllCombinations)
                continue;

            EquivalentPatternSummary prefixSummary = BuildEquivalentPatternSummary(uniquePrefixes, prefixItems, representativePositions);
            EquivalentPatternSummary suffixSummary = BuildEquivalentPatternSummary(uniqueSuffixes, suffixItems, representativePositions);
            string patternText = JoinPatternSegments(new[] { prefixSummary.PatternText, suffixSummary.PatternText });
            string formula = MultiplyFormulas(prefixSummary.TotalCountFormula, suffixSummary.TotalCountFormula);
            return new EquivalentPatternSummary(patternText, formula, prefixSummary.TotalCount * suffixSummary.TotalCount);
        }

        return null;
    }

    private static int GetCommonSuffixLength(IReadOnlyList<IReadOnlyList<int>> orders, int prefixLength)
    {
        int length = 0;
        while (prefixLength + length < orders[0].Count &&
            orders.All(order => order[order.Count - 1 - length] == orders[0][orders[0].Count - 1 - length]))
        {
            length++;
        }

        return length;
    }

    private static EquivalentPatternSummary? TryBuildAnchoredPermutationSummary(
        IReadOnlyList<IReadOnlyList<int>> orders,
        IReadOnlyList<int> remainingItems)
    {
        int orderLength = orders[0].Count;
        for (int anchorPosition = 0; anchorPosition < orderLength; anchorPosition++)
        {
            int anchorItem = orders[0][anchorPosition];
            if (!orders.All(order => order[anchorPosition] == anchorItem))
                continue;

            var poolItems = remainingItems.Where(item => item != anchorItem).ToArray();
            var reducedOrders = orders
                .Select(order => order.Where((_, index) => index != anchorPosition).ToArray())
                .ToList();
            if (!IsCompletePermutationSet(reducedOrders, poolItems))
                continue;

            string patternText = BuildAnchoredPermutationPattern(poolItems, anchorItem, anchorPosition, poolItems.Length - anchorPosition);
            string formula = poolItems.Length <= 1 ? "1" : $"{poolItems.Length}!";
            return new EquivalentPatternSummary(patternText, formula, Factorial(poolItems.Length));
        }

        return null;
    }

    private static EquivalentPatternSummary? TryBuildPartialIndependentBlockSummary(
        IReadOnlyList<IReadOnlyList<int>> orders,
        IReadOnlyDictionary<int, int> representativePositions)
    {
        int orderLength = orders[0].Count;
        if (orders.Count < 4 || orderLength < 2)
            return null;

        var candidates = new List<PartialPatternFamilyCandidate>();
        for (int splitPosition = 1; splitPosition < orderLength; splitPosition++)
        {
            var groupedOrders = orders
                .Select((order, index) => new
                {
                    Order = order,
                    Index = index,
                    PrefixItems = order.Take(splitPosition).OrderBy(item => representativePositions[item]).ToArray(),
                    SuffixItems = order.Skip(splitPosition).OrderBy(item => representativePositions[item]).ToArray()
                })
                .GroupBy(x => $"{string.Join(",", x.PrefixItems)}|{string.Join(",", x.SuffixItems)}");

            foreach (var groupedOrder in groupedOrders)
            {
                var members = groupedOrder.ToList();
                if (members.Count < 4)
                    continue;

                int[] prefixItems = members[0].PrefixItems;
                int[] suffixItems = members[0].SuffixItems;
                if (prefixItems.Intersect(suffixItems).Any())
                    continue;

                var uniquePrefixes = members
                    .Select(member => member.Order.Take(splitPosition).ToArray())
                    .Distinct(ArraySequenceComparer.Instance)
                    .ToList();
                var uniqueSuffixes = members
                    .Select(member => member.Order.Skip(splitPosition).ToArray())
                    .Distinct(ArraySequenceComparer.Instance)
                    .ToList();

                if (uniquePrefixes.Count <= 1 || uniqueSuffixes.Count <= 1)
                    continue;

                if (members.Count != uniquePrefixes.Count * uniqueSuffixes.Count)
                    continue;

                var fullPairs = new HashSet<string>(members.Select(member =>
                    $"{string.Join(",", member.Order.Take(splitPosition))}|{string.Join(",", member.Order.Skip(splitPosition))}"), StringComparer.Ordinal);
                bool hasAllCombinations = uniquePrefixes.All(prefix =>
                    uniqueSuffixes.All(suffix =>
                        fullPairs.Contains($"{string.Join(",", prefix)}|{string.Join(",", suffix)}")));
                if (!hasAllCombinations)
                    continue;

                EquivalentPatternSummary prefixSummary = BuildEquivalentPatternSummary(uniquePrefixes, prefixItems, representativePositions);
                EquivalentPatternSummary suffixSummary = BuildEquivalentPatternSummary(uniqueSuffixes, suffixItems, representativePositions);
                string patternText = JoinPatternSegments(new[] { prefixSummary.PatternText, suffixSummary.PatternText });
                string formula = MultiplyFormulas(prefixSummary.TotalCountFormula, suffixSummary.TotalCountFormula);
                candidates.Add(new PartialPatternFamilyCandidate(
                    members.Select(member => member.Index).ToArray(),
                    members.Min(member => member.Index),
                    patternText,
                    formula));
            }
        }

        return BuildPartialPatternFamilySummary(orders, candidates);
    }

    private static EquivalentPatternSummary? TryBuildWindowPermutationFamilySummary(
        IReadOnlyList<IReadOnlyList<int>> orders,
        IReadOnlyDictionary<int, int> representativePositions)
    {
        int orderLength = orders[0].Count;
        if (orders.Count < 4 || orderLength < 4)
            return null;

        var candidates = new List<WindowPermutationFamilyCandidate>();
        for (int start = 0; start < orderLength - 1; start++)
        {
            for (int width = 2; start + width <= orderLength; width++)
            {
                var groupedByOutside = orders
                    .Select((order, index) => new
                    {
                        Order = order,
                        Index = index,
                        Key = $"{string.Join(",", order.Take(start))}|{string.Join(",", order.Skip(start + width))}"
                    })
                    .GroupBy(x => x.Key);

                foreach (var outsideGroup in groupedByOutside)
                {
                    var members = outsideGroup.ToList();
                    int expectedCount = (int)Factorial(width);
                    if (members.Count != expectedCount)
                        continue;

                    int[] sortedWindowItems = members[0].Order
                        .Skip(start)
                        .Take(width)
                        .OrderBy(item => representativePositions[item])
                        .ToArray();
                    string expectedItemKey = string.Join(",", sortedWindowItems);

                    var uniqueWindowOrders = new HashSet<string>(StringComparer.Ordinal);
                    bool valid = true;
                    foreach (var member in members)
                    {
                        int[] windowItems = member.Order.Skip(start).Take(width).ToArray();
                        if (string.Join(",", windowItems.OrderBy(item => representativePositions[item])) != expectedItemKey)
                        {
                            valid = false;
                            break;
                        }

                        uniqueWindowOrders.Add(string.Join(",", windowItems));
                    }

                    if (!valid || uniqueWindowOrders.Count != expectedCount)
                        continue;

                    string candidatePatternText = JoinPatternSegments(
                        members[0].Order.Take(start).Select(item => $"#{item + 1}")
                            .Concat(new[] { $"permute {FormatBraceSet(sortedWindowItems)}" })
                            .Concat(members[0].Order.Skip(start + width).Select(item => $"#{item + 1}")));

                    candidates.Add(new WindowPermutationFamilyCandidate(
                        members.Select(member => member.Index).ToArray(),
                        members.Min(member => member.Index),
                        width,
                        candidatePatternText,
                        $"{width}!"));
                }
            }
        }

        return BuildPartialPatternFamilySummary(
            orders,
            candidates.Select(candidate => new PartialPatternFamilyCandidate(
                candidate.OrderIndices,
                candidate.FirstOrderIndex,
                candidate.PatternText,
                candidate.Formula)).ToList());
    }

    private static EquivalentPatternSummary? BuildPartialPatternFamilySummary(
        IReadOnlyList<IReadOnlyList<int>> orders,
        IReadOnlyList<PartialPatternFamilyCandidate> candidates)
    {
        if (candidates.Count == 0)
            return null;

        var selectedCandidates = new List<PartialPatternFamilyCandidate>();
        var coveredOrderIndices = new HashSet<int>();
        foreach (var candidate in candidates
            .OrderByDescending(candidate => candidate.Savings)
            .ThenByDescending(candidate => candidate.OrderIndices.Count)
            .ThenBy(candidate => candidate.FirstOrderIndex))
        {
            if (candidate.OrderIndices.Any(index => coveredOrderIndices.Contains(index)))
                continue;

            selectedCandidates.Add(candidate);
            foreach (int index in candidate.OrderIndices)
                coveredOrderIndices.Add(index);
        }

        if (selectedCandidates.Count == 0)
            return null;

        var components = new List<WindowPermutationFamilyComponent>();
        foreach (var candidate in selectedCandidates)
        {
            components.Add(new WindowPermutationFamilyComponent(
                candidate.FirstOrderIndex,
                candidate.PatternText,
                candidate.Formula,
                candidate.OrderIndices.Count));
        }

        for (int index = 0; index < orders.Count; index++)
        {
            if (coveredOrderIndices.Contains(index))
                continue;

            components.Add(new WindowPermutationFamilyComponent(
                index,
                FormatOrder(orders[index]),
                "1",
                1));
        }

        if (components.Count >= orders.Count)
            return null;

        components.Sort((left, right) => left.FirstOrderIndex.CompareTo(right.FirstOrderIndex));
        string patternText = "(" + string.Join(" | ", components.Select(component => component.PatternText)) + ")";
        string formula = CombineFormulaParts(components.Select(component => component.Formula).ToList());
        return new EquivalentPatternSummary(patternText, formula, orders.Count);
    }

    private static bool IsCompletePermutationSet(
        IReadOnlyList<IReadOnlyList<int>> orders,
        IReadOnlyList<int> items)
    {
        BigInteger permutationCount = Factorial(items.Count);
        if (orders.Count != (int)permutationCount)
            return false;

        string expectedKey = string.Join(",", items.OrderBy(x => x));
        var uniqueOrders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var order in orders)
        {
            if (string.Join(",", order.OrderBy(x => x)) != expectedKey)
                return false;

            uniqueOrders.Add(string.Join(",", order));
        }

        return uniqueOrders.Count == orders.Count;
    }

    private static string BuildAnchoredPermutationPattern(
        IReadOnlyList<int> poolItems,
        int anchorItem,
        int beforeCount,
        int afterCount)
    {
        string poolText = FormatBraceSet(poolItems);
        string anchorText = $"#{anchorItem + 1}";
        if (beforeCount == 0)
            return $"{anchorText} > {FormatPermutationSegment(poolText, afterCount, poolItems.Count, isRemaining: false)}";

        if (afterCount == 0)
            return $"{FormatPermutationSegment(poolText, beforeCount, poolItems.Count, isRemaining: false)} > {anchorText}";

        return $"{FormatPermutationSegment(poolText, beforeCount, poolItems.Count, isRemaining: false)} > {anchorText} > {FormatPermutationSegment(poolText, afterCount, poolItems.Count, isRemaining: true)}";
    }

    private static string FormatPermutationSegment(string poolText, int count, int poolSize, bool isRemaining)
    {
        if (count <= 0)
            return string.Empty;

        if (count == poolSize)
            return $"permute {poolText}";

        if (!isRemaining && count > 1)
            return $"{count} of {poolText} in any order";

        if (!isRemaining)
            return $"1 of {poolText}";

        if (count == 1)
            return $"remaining 1 of {poolText}";

        return $"remaining {count} of {poolText} in any order";
    }

    private static string JoinPatternSegments(IEnumerable<string> segments)
    {
        var definitions = new List<string>();
        var bodies = new List<string>();
        int nextAliasIndex = 0;

        foreach (string segment in segments.Where(segment => !string.IsNullOrEmpty(segment)))
        {
            ParsedPatternSegment parsed = ParsePatternSegment(segment);
            if (parsed.Definitions.Count == 0)
            {
                bodies.Add(parsed.Body);
                continue;
            }

            var aliasMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var definition in parsed.Definitions)
            {
                string newAlias = GetAliasName(nextAliasIndex++);
                aliasMap[definition.Alias] = newAlias;
                definitions.Add($"{newAlias}={definition.Expression}");
            }

            bodies.Add(RewritePatternAliases(parsed.Body, aliasMap));
        }

        if (definitions.Count == 0)
            return string.Join(" > ", bodies);

        return $"{string.Join(", ", definitions)}; {string.Join(" > ", bodies)}";
    }

    private static ParsedPatternSegment ParsePatternSegment(string segment)
    {
        int separatorIndex = segment.IndexOf(';');
        if (separatorIndex < 0)
            return new ParsedPatternSegment(Array.Empty<PatternAliasDefinition>(), segment);

        string definitionText = segment[..separatorIndex].Trim();
        string body = segment[(separatorIndex + 1)..].Trim();
        var definitions = SplitTopLevelCommaSeparated(definitionText)
            .Select(part =>
            {
                int equalsIndex = part.IndexOf('=');
                return new PatternAliasDefinition(
                    part[..equalsIndex].Trim(),
                    part[(equalsIndex + 1)..].Trim());
            })
            .ToList();
        return new ParsedPatternSegment(definitions, body);
    }

    private static List<string> SplitTopLevelCommaSeparated(string text)
    {
        var parts = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '{')
                depth++;
            else if (ch == '}')
                depth--;
            else if (ch == ',' && depth == 0)
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        parts.Add(text[start..].Trim());
        return parts.Where(part => part.Length > 0).ToList();
    }

    private static string RewritePatternAliases(string body, IReadOnlyDictionary<string, string> aliasMap)
    {
        string rewritten = body;
        foreach (var pair in aliasMap)
        {
            rewritten = Regex.Replace(
                rewritten,
                $@"\b{Regex.Escape(pair.Key)}(?=\d)",
                pair.Value);
        }

        return rewritten;
    }

    private static string GetAliasName(int index)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        string alias = string.Empty;
        int value = index;
        do
        {
            alias = alphabet[value % alphabet.Length] + alias;
            value = value / alphabet.Length - 1;
        }
        while (value >= 0);

        return alias;
    }

    private static string CombineFormulaParts(IReadOnlyList<string> formulaParts)
    {
        var normalizedParts = formulaParts
            .Select(SimplifyFormula)
            .ToList();

        if (normalizedParts.All(formula => formula == normalizedParts[0]))
        {
            if (normalizedParts[0] == "1")
                return normalizedParts.Count.ToString();

            return normalizedParts.Count == 1
                ? normalizedParts[0]
                : $"{normalizedParts.Count} x {ParenthesizeSum(normalizedParts[0])}";
        }

        return string.Join(" + ", normalizedParts.Select(ParenthesizeSum));
    }

    private static string MultiplyFormulas(string left, string right)
    {
        left = SimplifyFormula(left);
        right = SimplifyFormula(right);

        if (left == "1")
            return right;
        if (right == "1")
            return left;

        return $"{ParenthesizeSum(left)} x {ParenthesizeSum(right)}";
    }

    private static string BuildPermutationTemplateText(
        IReadOnlyList<IReadOnlyList<int>> partition,
        IReadOnlyList<int> template)
    {
        var aliases = new string[partition.Count];
        for (int blockIndex = 0; blockIndex < partition.Count; blockIndex++)
            aliases[blockIndex] = partition[blockIndex].Count > 1 ? ((char)('A' + blockIndex)).ToString() : string.Empty;

        var definitions = partition
            .Select((block, index) => (block, index))
            .Where(x => x.block.Count > 1)
            .Select(x => $"{aliases[x.index]}=permute{FormatBraceSet(x.block)}")
            .ToList();

        var seenCounts = new int[partition.Count];
        var tokens = new List<string>(template.Count);
        foreach (int blockIndex in template)
        {
            seenCounts[blockIndex]++;
            if (partition[blockIndex].Count == 1)
            {
                tokens.Add($"#{partition[blockIndex][0] + 1}");
            }
            else
            {
                tokens.Add($"{aliases[blockIndex]}{seenCounts[blockIndex]}");
            }
        }

        return $"{string.Join(", ", definitions)}; {string.Join(" > ", tokens)}";
    }

    private static string FactorialNotationFromCount(int count)
    {
        int n = 1;
        int factorial = 1;
        while (factorial < count)
        {
            n++;
            factorial *= n;
        }

        return factorial == count ? $"{n}!" : count.ToString();
    }

    private static string ParenthesizeSum(string formula)
    {
        return formula.Contains(" + ", StringComparison.Ordinal) ? $"({formula})" : formula;
    }

    private static string SimplifyFormula(string formula)
    {
        if (formula == "1!" || formula == "1")
            return "1";

        return formula
            .Replace("1! x ", string.Empty, StringComparison.Ordinal)
            .Replace(" x 1!", string.Empty, StringComparison.Ordinal)
            .Replace("1 x ", string.Empty, StringComparison.Ordinal)
            .Replace(" x 1", string.Empty, StringComparison.Ordinal);
    }

    private static BigInteger Factorial(int value)
    {
        BigInteger result = BigInteger.One;
        for (int i = 2; i <= value; i++)
            result *= i;
        return result;
    }

    private sealed class EquivalentPatternSummary
    {
        public EquivalentPatternSummary(string patternText, string totalCountFormula, BigInteger totalCount)
        {
            PatternText = patternText;
            TotalCountFormula = totalCountFormula;
            TotalCount = totalCount;
        }

        public string PatternText { get; }
        public string TotalCountFormula { get; }
        public BigInteger TotalCount { get; }
    }

    private sealed class ArraySequenceComparer : IEqualityComparer<IReadOnlyList<int>>
    {
        public static ArraySequenceComparer Instance { get; } = new();

        public bool Equals(IReadOnlyList<int>? x, IReadOnlyList<int>? y)
        {
            if (ReferenceEquals(x, y))
                return true;
            if (x is null || y is null || x.Count != y.Count)
                return false;

            for (int i = 0; i < x.Count; i++)
            {
                if (x[i] != y[i])
                    return false;
            }

            return true;
        }

        public int GetHashCode(IReadOnlyList<int> items)
        {
            var hash = new HashCode();
            foreach (int item in items)
                hash.Add(item);
            return hash.ToHashCode();
        }
    }

    private static IEnumerable<List<List<int>>> EnumeratePartitions(IReadOnlyList<int> items)
    {
        var blocks = new List<List<int>>();
        foreach (var partition in EnumeratePartitions(items, 0, blocks))
            yield return partition;
    }

    private static IEnumerable<List<List<int>>> EnumeratePartitions(
        IReadOnlyList<int> items,
        int index,
        List<List<int>> blocks)
    {
        if (index == items.Count)
        {
            yield return blocks.Select(block => block.ToList()).ToList();
            yield break;
        }

        int item = items[index];
        for (int blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            blocks[blockIndex].Add(item);
            foreach (var partition in EnumeratePartitions(items, index + 1, blocks))
                yield return partition;
            blocks[blockIndex].RemoveAt(blocks[blockIndex].Count - 1);
        }

        blocks.Add(new List<int> { item });
        foreach (var partition in EnumeratePartitions(items, index + 1, blocks))
            yield return partition;
        blocks.RemoveAt(blocks.Count - 1);
    }

    private sealed class PermutationTemplateCandidate
    {
        public PermutationTemplateCandidate(
            IReadOnlyList<IReadOnlyList<int>> partition,
            IReadOnlyList<int> template,
            IReadOnlyList<int> permutationCounts)
        {
            Partition = partition.Select(block => (IReadOnlyList<int>)block.ToList()).ToList();
            Template = template.ToArray();
            PermutationCounts = permutationCounts.ToArray();
        }

        public IReadOnlyList<IReadOnlyList<int>> Partition { get; }
        public IReadOnlyList<int> Template { get; }
        public IReadOnlyList<int> PermutationCounts { get; }

        public int CompareTo(PermutationTemplateCandidate other)
        {
            int thisMulti = Partition.Count(block => block.Count > 1);
            int otherMulti = other.Partition.Count(block => block.Count > 1);
            if (thisMulti != otherMulti)
                return thisMulti.CompareTo(otherMulti);

            int thisMax = Partition.Max(block => block.Count);
            int otherMax = other.Partition.Max(block => block.Count);
            if (thisMax != otherMax)
                return thisMax.CompareTo(otherMax);

            return other.Partition.Count.CompareTo(Partition.Count);
        }
    }

    private sealed record PatternAliasDefinition(string Alias, string Expression);

    private sealed record ParsedPatternSegment(
        IReadOnlyList<PatternAliasDefinition> Definitions,
        string Body);

    private sealed record WindowPermutationFamilyCandidate(
        IReadOnlyList<int> OrderIndices,
        int FirstOrderIndex,
        int Width,
        string PatternText,
        string Formula)
    {
        public int Savings => OrderIndices.Count - 1;
    }

    private sealed record WindowPermutationFamilyComponent(
        int FirstOrderIndex,
        string PatternText,
        string Formula,
        int Count);

    private sealed record PartialPatternFamilyCandidate(
        IReadOnlyList<int> OrderIndices,
        int FirstOrderIndex,
        string PatternText,
        string Formula)
    {
        public int Savings => OrderIndices.Count - 1;
    }

    private readonly record struct SymmetrySignature(ulong AncestorMask, ulong DescendantMask);

    private sealed class GroupSymmetryClass
    {
        public GroupSymmetryClass(int index, int[] items, ulong ancestorMask)
        {
            Index = index;
            Items = items;
            AncestorMask = ancestorMask;
        }

        public int Index { get; }
        public int[] Items { get; }
        public ulong AncestorMask { get; }
    }

    private sealed class GroupSymmetryInfo
    {
        public GroupSymmetryInfo(
            IReadOnlyList<GroupSymmetryClass> classes,
            IReadOnlyDictionary<int, int> itemToClassIndex)
        {
            Classes = classes;
            ItemToClassIndex = itemToClassIndex;
        }

        public IReadOnlyList<GroupSymmetryClass> Classes { get; }
        public IReadOnlyDictionary<int, int> ItemToClassIndex { get; }
    }

    private sealed class OrderFamilyDescriptor
    {
        private OrderFamilyDescriptor(
            IReadOnlyList<int> representativeOrderItems,
            string representativeOrder,
            string patternText,
            string countFormula,
            int count,
            IReadOnlyList<IReadOnlyList<int>>? partitionBlocks,
            IReadOnlyList<int>? template)
        {
            RepresentativeOrderItems = representativeOrderItems;
            RepresentativeOrder = representativeOrder;
            PatternText = patternText;
            CountFormula = countFormula;
            Count = count;
            PartitionBlocks = partitionBlocks;
            Template = template;
        }

        public IReadOnlyList<int> RepresentativeOrderItems { get; }
        public string RepresentativeOrder { get; }
        public string PatternText { get; }
        public string CountFormula { get; }
        public int Count { get; }
        public IReadOnlyList<IReadOnlyList<int>>? PartitionBlocks { get; }
        public IReadOnlyList<int>? Template { get; }

        public static OrderFamilyDescriptor CreateSingleton(IReadOnlyList<int> order)
        {
            int[] copied = order.ToArray();
            string orderText = FormatOrder(copied);
            return new OrderFamilyDescriptor(copied, orderText, orderText, "1", 1, null, null);
        }

        public static OrderFamilyDescriptor CreateSymmetric(
            IReadOnlyList<int> representativeOrder,
            string patternText,
            string countFormula,
            int count,
            IReadOnlyList<IReadOnlyList<int>> partitionBlocks,
            IReadOnlyList<int> template)
        {
            int[] copied = representativeOrder.ToArray();
            return new OrderFamilyDescriptor(
                copied,
                FormatOrder(copied),
                patternText,
                countFormula,
                count,
                partitionBlocks.Select(block => (IReadOnlyList<int>)block.ToArray()).ToList(),
                template.ToArray());
        }
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
        public string RepresentativeOrder { get; }
        public List<OrderFamilyDescriptor> OrderFamilies { get; }

        public BranchInfo(ComparisonState nextState, ulong nextFixedTopMask, int nextRemainingSlots, OrderFamilyDescriptor representativeFamily)
        {
            NextState = nextState;
            NextFixedTopMask = nextFixedTopMask;
            NextRemainingSlots = nextRemainingSlots;
            RepresentativeOrder = representativeFamily.RepresentativeOrder;
            OrderFamilies = new List<OrderFamilyDescriptor> { representativeFamily };
        }
    }
}
