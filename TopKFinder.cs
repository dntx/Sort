using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

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

class ComparisonState
{
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
            if (BitOperations.PopCount(Ancestors[item]) >= k)
                removedMask |= Bit(item);
        }

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
                signatures[i] = BuildElementSignature(labels[i], Ancestors[i], Descendants[i], labels, classCount);
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
        int classCount = labels.Max() + 1;
        var parts = new List<int> { classCount };

        for (int classId = 0; classId < classCount; classId++)
        {
            var members = Enumerable.Range(0, n).Where(i => labels[i] == classId).ToList();
            int memberCount = members.Count;
            int representative = members[0];

            parts.Add(memberCount);
            parts.Add(IsActive(representative) ? 1 : 0);

            for (int otherClass = 0; otherClass < classCount; otherClass++)
            {
                var counts = members
                    .Select(member => CountNeighborsWithLabel(Ancestors[member], labels, otherClass))
                    .OrderBy(x => x);

                parts.AddRange(counts);
            }

            for (int otherClass = 0; otherClass < classCount; otherClass++)
            {
                var counts = members
                    .Select(member => CountNeighborsWithLabel(Descendants[member], labels, otherClass))
                    .OrderBy(x => x);

                parts.AddRange(counts);
            }
        }

        _canonicalKeyCache = new IntSequenceKey(parts.ToArray());
        return _canonicalKeyCache.Value;
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
        return BitOperations.PopCount(Ancestors[item]);
    }

    public int GetDescendantCount(int item)
    {
        return BitOperations.PopCount(Descendants[item]);
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

    public StrategyPlan(int n, int m, int k, StrategyNode root, TimeSpan elapsed)
    {
        N = n;
        M = m;
        K = k;
        Root = root;
        Elapsed = elapsed;
        MaxStep = GetMaxStep(root);
    }

    private static int GetMaxStep(StrategyNode node)
    {
        int selfStep = node.Step ?? 0;
        if (node.Branches.Count == 0)
            return selfStep;

        return Math.Max(selfStep, node.Branches.Max(branch => GetMaxStep(branch.Next)));
    }
}

sealed class StrategyNode
{
    public StrategyNodeKind Kind { get; }
    public int StateId { get; }
    public int? Step { get; }
    public IReadOnlyList<int> Group { get; }
    public IReadOnlyList<int> TopSet { get; }
    public IReadOnlyList<StrategyBranch> Branches { get; }
    public bool IsCompressedFinalComparison { get; }
    public int OmittedBranchCount { get; }

    private StrategyNode(
        StrategyNodeKind kind,
        int stateId,
        int? step,
        IReadOnlyList<int>? group,
        IReadOnlyList<int>? topSet,
        IReadOnlyList<StrategyBranch>? branches,
        bool isCompressedFinalComparison,
        int omittedBranchCount)
    {
        Kind = kind;
        StateId = stateId;
        Step = step;
        Group = group ?? Array.Empty<int>();
        TopSet = topSet ?? Array.Empty<int>();
        Branches = branches ?? Array.Empty<StrategyBranch>();
        IsCompressedFinalComparison = isCompressedFinalComparison;
        OmittedBranchCount = omittedBranchCount;
    }

    public static StrategyNode Decision(
        int stateId,
        int step,
        IReadOnlyList<int> group,
        IReadOnlyList<StrategyBranch> branches,
        bool isCompressedFinalComparison = false,
        int omittedBranchCount = 0)
        => new(StrategyNodeKind.Decision, stateId, step, group, null, branches, isCompressedFinalComparison, omittedBranchCount);

    public static StrategyNode Terminal(int stateId, IReadOnlyList<int> topSet)
        => new(StrategyNodeKind.Terminal, stateId, null, null, topSet, null, false, 0);

    public static StrategyNode Reference(int stateId)
        => new(StrategyNodeKind.Reference, stateId, null, null, null, null, false, 0);
}

sealed class StrategyBranch
{
    public string OrderText { get; }
    public IReadOnlyList<string> EquivalentOrderTexts { get; }
    public StrategyEffect Effect { get; }
    public StrategyNode Next { get; }

    public StrategyBranch(string orderText, IReadOnlyList<string> equivalentOrderTexts, StrategyEffect effect, StrategyNode next)
    {
        OrderText = orderText;
        EquivalentOrderTexts = equivalentOrderTexts;
        Effect = effect;
        Next = next;
    }
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

readonly struct FeasibleTopSetInfo
{
    public FeasibleTopSetInfo(int count, ulong uniqueMask)
    {
        Count = count;
        UniqueMask = uniqueMask;
    }

    public int Count { get; }
    public ulong UniqueMask { get; }
}

class StrategyBuilder
{
    private readonly int _n;
    private readonly int _m;
    private readonly int _k;
    private readonly Dictionary<IntSequenceKey, int> _stateIds = new();
    private readonly HashSet<IntSequenceKey> _expandedStates = new();
    private readonly Dictionary<IntSequenceKey, int> _minWorstCaseStepsCache = new();
    private readonly Dictionary<IntSequenceKey, int> _lowerBoundStepsCache = new();
    private readonly Dictionary<IntSequenceKey, FeasibleTopSetInfo> _feasibleTopSetCache = new();
    private int _nextStateId = 1;

    public StrategyBuilder(int n, int m, int k)
    {
        _n = n;
        _m = m;
        _k = k;
    }

    public StrategyPlan Build()
    {
        var stopwatch = Stopwatch.StartNew();
        var initial = new ComparisonState(_n);
        var root = BuildState(initial, 1);
        stopwatch.Stop();
        return new StrategyPlan(_n, _m, _k, root, stopwatch.Elapsed);
    }

    public static StrategyPlan Generate(int n, int m, int k)
    {
        return new StrategyBuilder(n, m, k).Build();
    }

    private StrategyNode BuildState(ComparisonState state, int step)
    {
        IntSequenceKey key = state.GetCanonicalKey();
        int stateId = GetStateId(key);

        if (TryGetDeterminedTopSet(state, out List<int> determinedTopSet))
            return StrategyNode.Terminal(stateId, determinedTopSet);

        if (state.ActiveCount <= _k)
            return StrategyNode.Terminal(stateId, state.GetActiveItemsOrdered());

        var possibleCandidates = GetPossibleCandidates(state);
        if (possibleCandidates.Count > GetRemainingSlots(state) && possibleCandidates.Count <= _m)
        {
            var finalBranches = BuildBranches(state, possibleCandidates, step + 1);
            var representativeBranches = finalBranches.Take(1).ToList();
            return StrategyNode.Decision(
                stateId,
                step,
                possibleCandidates,
                representativeBranches,
                isCompressedFinalComparison: true,
                omittedBranchCount: Math.Max(0, finalBranches.Count - representativeBranches.Count));
        }

        if (_expandedStates.Contains(key))
            return StrategyNode.Reference(stateId);

        _expandedStates.Add(key);

        var group = ChooseGroup(state);
        var branches = BuildBranches(state, group, step + 1);

        return StrategyNode.Decision(stateId, step, group, branches);
    }

    private List<StrategyBranch> BuildBranches(ComparisonState state, IReadOnlyList<int> group, int nextStep)
    {
        var groupedBranches = new Dictionary<IntSequenceKey, BranchInfo>();
        foreach (var order in EnumerateFeasibleOrders(state, group))
        {
            var next = state.Clone();
            next.ApplyOrder(order);
            next.Eliminate(_k);

            IntSequenceKey nextKey = next.GetCanonicalKey();
            if (!groupedBranches.TryGetValue(nextKey, out BranchInfo? branch))
            {
                groupedBranches[nextKey] = new BranchInfo(next, FormatOrder(order));
            }
            else
            {
                branch.EquivalentOrders.Add(FormatOrder(order));
            }
        }

        return groupedBranches.Values
            .OrderBy(v => v.RepresentativeOrder, StringComparer.Ordinal)
            .Select(v => new StrategyBranch(
                v.RepresentativeOrder,
                v.EquivalentOrders.OrderBy(order => order, StringComparer.Ordinal).ToList(),
                BuildComparisonEffect(state, v.NextState),
                BuildState(v.NextState, nextStep)))
            .ToList();
    }

    private StrategyEffect BuildComparisonEffect(ComparisonState before, ComparisonState after)
    {
        ulong guaranteedTopBefore = GetGuaranteedTopMask(before);
        ulong guaranteedTopAfter = GetGuaranteedTopMask(after);

        var newlyGuaranteedTop = ComparisonState.MaskToOrderedList(guaranteedTopAfter & ~guaranteedTopBefore);
        var newlyExcluded = ComparisonState.MaskToOrderedList(before.ActiveMask & ~after.ActiveMask);
        var fixedCandidates = ComparisonState.MaskToOrderedList(guaranteedTopAfter);
        var possibleCandidates = ComparisonState.MaskToOrderedList(after.ActiveMask & ~guaranteedTopAfter);

        return new StrategyEffect(newlyGuaranteedTop, newlyExcluded, fixedCandidates, possibleCandidates);
    }

    private ulong GetGuaranteedTopMask(ComparisonState state)
    {
        ulong mask = 0;
        for (int i = 0; i < _n; i++)
        {
            if (state.IsActive(i) && _n - 1 - state.GetDescendantCount(i) <= _k - 1)
                mask |= 1UL << i;
        }

        return mask;
    }

    private List<int> GetPossibleCandidates(ComparisonState state)
    {
        ulong guaranteedTop = GetGuaranteedTopMask(state);
        return ComparisonState.MaskToOrderedList(state.ActiveMask & ~guaranteedTop);
    }

    private int GetRemainingSlots(ComparisonState state)
    {
        return _k - BitOperations.PopCount(GetGuaranteedTopMask(state));
    }

    private List<int> ChooseGroup(ComparisonState state)
    {
        var candidates = state.GetActiveItemsOrdered();
        int maxGroupSize = Math.Min(_m, candidates.Count);
        IntSequenceKey currentKey = state.GetCanonicalKey();
        var labels = state.GetStructuralLabels();

        List<int>? bestGroup = null;
        (int negWorstCaseSteps, int negFreshItems, int negUnrelatedScore, int negGroupSize, int distinctStates, int totalReduction, int unresolvedPairs) bestScore =
            (int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue);

        for (int groupSize = 2; groupSize <= maxGroupSize; groupSize++)
        {
            var seenGroupPatterns = new HashSet<IntSequenceKey>();
            foreach (var group in EnumerateCombinations(candidates, groupSize))
            {
                if (!seenGroupPatterns.Add(GetGroupPattern(group, labels)))
                    continue;

                var nextStateKeys = new HashSet<IntSequenceKey>();
                int worstCaseSteps = 0;
                int totalReduction = 0;
                bool isUseful = false;
                int bestKnownWorstCase = bestGroup is null ? int.MaxValue : -bestScore.negWorstCaseSteps;

                foreach (var order in EnumerateFeasibleOrders(state, group))
                {
                    var next = state.Clone();
                    next.ApplyOrder(order);
                    next.Eliminate(_k);

                    IntSequenceKey nextKey = next.GetCanonicalKey();
                    if (nextKey == currentKey)
                        continue;

                    isUseful = true;
                    int reduction = state.ActiveCount - next.ActiveCount;
                    totalReduction += reduction;
                    nextStateKeys.Add(nextKey);

                    int branchLowerBound = 1 + GetMinWorstCaseLowerBound(next);
                    if (branchLowerBound > bestKnownWorstCase)
                    {
                        worstCaseSteps = branchLowerBound;
                        break;
                    }

                    int branchSteps = 1 + GetMinWorstCaseSteps(next);
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
        }

        if (bestGroup is not null)
            return bestGroup;

        return candidates.Take(maxGroupSize).ToList();
    }

    private int GetMinWorstCaseSteps(ComparisonState state)
    {
        if (TryGetDeterminedTopSet(state, out _))
            return 0;

        if (state.ActiveCount <= _k)
            return 0;

        var possibleCandidates = GetPossibleCandidates(state);
        if (possibleCandidates.Count > GetRemainingSlots(state) && possibleCandidates.Count <= _m)
            return 1;

        IntSequenceKey key = state.GetCanonicalKey();
        if (_minWorstCaseStepsCache.TryGetValue(key, out int cached))
            return cached;

        var candidates = state.GetActiveItemsOrdered();
        int maxGroupSize = Math.Min(_m, candidates.Count);
        var labels = state.GetStructuralLabels();
        int bestWorstCase = int.MaxValue;

        for (int groupSize = 2; groupSize <= maxGroupSize; groupSize++)
        {
            var seenGroupPatterns = new HashSet<IntSequenceKey>();
            foreach (var group in EnumerateCombinations(candidates, groupSize))
            {
                if (!seenGroupPatterns.Add(GetGroupPattern(group, labels)))
                    continue;

                int groupWorstCase = 0;
                bool isUseful = false;

                foreach (var order in EnumerateFeasibleOrders(state, group))
                {
                    var next = state.Clone();
                    next.ApplyOrder(order);
                    next.Eliminate(_k);

                    IntSequenceKey nextKey = next.GetCanonicalKey();
                    if (nextKey == key)
                        continue;

                    isUseful = true;
                    int branchLowerBound = 1 + GetMinWorstCaseLowerBound(next);
                    if (branchLowerBound >= bestWorstCase)
                    {
                        groupWorstCase = branchLowerBound;
                        break;
                    }

                    int branchSteps = 1 + GetMinWorstCaseSteps(next);
                    groupWorstCase = Math.Max(groupWorstCase, branchSteps);

                    if (groupWorstCase >= bestWorstCase)
                        break;
                }

                if (isUseful)
                    bestWorstCase = Math.Min(bestWorstCase, groupWorstCase);
            }
        }

        if (bestWorstCase == int.MaxValue)
            bestWorstCase = 0;

        _minWorstCaseStepsCache[key] = bestWorstCase;
        return bestWorstCase;
    }

    private int GetMinWorstCaseLowerBound(ComparisonState state)
    {
        if (TryGetDeterminedTopSet(state, out _))
            return 0;

        if (state.ActiveCount <= _k)
            return 0;

        var possibleCandidates = GetPossibleCandidates(state);
        if (possibleCandidates.Count > GetRemainingSlots(state) && possibleCandidates.Count <= _m)
            return 1;

        IntSequenceKey key = state.GetCanonicalKey();
        if (_lowerBoundStepsCache.TryGetValue(key, out int cached))
            return cached;

        FeasibleTopSetInfo info = GetFeasibleTopSetInfo(state);
        int maxOutcomesPerStep = GetMaxOutcomesPerStep(state);
        int distinguishable = 1;
        int steps = 0;
        while (distinguishable < info.Count)
        {
            steps++;
            checked
            {
                distinguishable *= maxOutcomesPerStep;
            }
        }

        _lowerBoundStepsCache[key] = steps;
        return steps;
    }

    private bool TryGetDeterminedTopSet(ComparisonState state, out List<int> topSet)
    {
        FeasibleTopSetInfo info = GetFeasibleTopSetInfo(state);
        if (info.Count == 1)
        {
            topSet = ComparisonState.MaskToOrderedList(info.UniqueMask);
            return true;
        }

        topSet = Array.Empty<int>().ToList();
        return false;
    }

    private FeasibleTopSetInfo GetFeasibleTopSetInfo(ComparisonState state)
    {
        IntSequenceKey key = state.GetCanonicalKey();
        if (_feasibleTopSetCache.TryGetValue(key, out FeasibleTopSetInfo cached))
            return cached;

        ulong guaranteedMask = GetGuaranteedTopMask(state);
        int guaranteedCount = BitOperations.PopCount(guaranteedMask);
        int remainingSlots = _k - guaranteedCount;

        FeasibleTopSetInfo info;
        if (remainingSlots == 0)
        {
            info = new FeasibleTopSetInfo(1, guaranteedMask);
        }
        else
        {
            var possibleCandidates = GetPossibleCandidates(state);
            int count = 0;
            ulong uniqueMask = 0;
            foreach (var combination in EnumerateCombinations(possibleCandidates, remainingSlots))
            {
                ulong candidateMask = guaranteedMask;
                foreach (int item in combination)
                    candidateMask |= 1UL << item;

                if (!IsFeasibleTopSet(state, candidateMask))
                    continue;

                count++;
                if (count == 1)
                    uniqueMask = candidateMask;
            }

            info = new FeasibleTopSetInfo(count, uniqueMask);
        }

        _feasibleTopSetCache[key] = info;
        return info;
    }

    private bool IsFeasibleTopSet(ComparisonState state, ulong candidateMask)
    {
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

    private static IEnumerable<List<int>> EnumerateCombinations(IReadOnlyList<int> items, int count)
    {
        var current = new List<int>(count);
        foreach (var combination in EnumerateCombinations(items, count, 0, current))
            yield return combination;
    }

    private static IEnumerable<List<int>> EnumerateCombinations(
        IReadOnlyList<int> items,
        int count,
        int start,
        List<int> current)
    {
        if (current.Count == count)
        {
            yield return new List<int>(current);
            yield break;
        }

        for (int i = start; i <= items.Count - (count - current.Count); i++)
        {
            current.Add(items[i]);
            foreach (var combination in EnumerateCombinations(items, count, i + 1, current))
                yield return combination;
            current.RemoveAt(current.Count - 1);
        }
    }

    private int GetStateId(IntSequenceKey key)
    {
        if (_stateIds.TryGetValue(key, out int id))
            return id;

        id = _nextStateId++;
        _stateIds[key] = id;
        return id;
    }

    private static string FormatOrder(IEnumerable<int> items)
    {
        return string.Join(" > ", items.Select(i => $"#{i + 1}"));
    }

    private sealed class BranchInfo
    {
        public ComparisonState NextState { get; }
        public string RepresentativeOrder { get; }
        public List<string> EquivalentOrders { get; }

        public BranchInfo(ComparisonState nextState, string representativeOrder)
        {
            NextState = nextState;
            RepresentativeOrder = representativeOrder;
            EquivalentOrders = new List<string>();
        }
    }

}
