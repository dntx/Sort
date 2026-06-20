using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

class ComparisonState
{
    private readonly int _n;
    private readonly ulong _allMask;
    public ulong[] Ancestors { get; }
    public ulong[] Descendants { get; }
    public ulong ActiveMask { get; private set; }
    public int ActiveCount { get; private set; }
    private int[]? _structuralLabelsCache;
    private string? _canonicalKeyCache;

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

    public string GetKey()
    {
        var parts = new List<string>(Ancestors.Length + 1);
        parts.Add($"A:{ActiveMask:X16}");
        for (int i = 0; i < Ancestors.Length; i++)
            parts.Add($"{i}:{Ancestors[i]:X16}");
        return string.Join("|", parts);
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
            var signatures = new string[n];
            for (int i = 0; i < n; i++)
            {
                string ancestors = BuildNeighborSignature(Ancestors[i], labels);
                string descendants = BuildNeighborSignature(Descendants[i], labels);
                signatures[i] = $"{labels[i]}|A:{ancestors}|D:{descendants}";
            }

            var signatureToColor = signatures
                .Distinct()
                .OrderBy(x => x, StringComparer.Ordinal)
                .Select((signature, index) => (signature, index))
                .ToDictionary(x => x.signature, x => x.index, StringComparer.Ordinal);

            var nextLabels = signatures.Select(signature => signatureToColor[signature]).ToArray();
            changed = !labels.SequenceEqual(nextLabels);
            labels = nextLabels;
        }
        while (changed);

        _structuralLabelsCache = labels;
        return labels;
    }

    public string GetCanonicalKey()
    {
        if (_canonicalKeyCache is not null)
            return _canonicalKeyCache;

        int n = Ancestors.Length;
        var labels = GetStructuralLabels();
        var classIds = labels.Distinct().OrderBy(x => x).ToList();
        var parts = new List<string>();
        foreach (int classId in classIds)
        {
            var members = Enumerable.Range(0, n).Where(i => labels[i] == classId).ToList();
            int representative = members[0];
            string activeFlag = IsActive(representative) ? "1" : "0";

            var ancestorClassCounts = classIds
                .Select(otherClass => members
                    .Select(member => CountNeighborsWithLabel(Ancestors[member], labels, otherClass))
                    .OrderBy(x => x))
                .ToList();

            var descendantClassCounts = classIds
                .Select(otherClass => members
                    .Select(member => CountNeighborsWithLabel(Descendants[member], labels, otherClass))
                    .OrderBy(x => x))
                .ToList();

            parts.Add(
                $"C{classId}|N:{members.Count}|Active:{activeFlag}" +
                $"|Anc:{string.Join(";", ancestorClassCounts.Select(counts => string.Join(",", counts)))}" +
                $"|Desc:{string.Join(";", descendantClassCounts.Select(counts => string.Join(",", counts)))}");
        }

        _canonicalKeyCache = string.Join("||", parts);
        return _canonicalKeyCache;
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

    private static string BuildNeighborSignature(ulong mask, IReadOnlyList<int> labels)
    {
        var neighborLabels = new List<int>();
        foreach (int item in EnumerateBits(mask))
            neighborLabels.Add(labels[item]);

        neighborLabels.Sort();
        return string.Join(",", neighborLabels);
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
    public StrategyEffect Effect { get; }
    public StrategyNode Next { get; }

    public StrategyBranch(string orderText, StrategyEffect effect, StrategyNode next)
    {
        OrderText = orderText;
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

class StrategyBuilder
{
    private readonly int _n;
    private readonly int _m;
    private readonly int _k;
    private readonly Dictionary<string, int> _stateIds = new();
    private readonly HashSet<string> _expandedStates = new();
    private readonly Dictionary<string, int> _minWorstCaseStepsCache = new();
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
        string key = state.GetCanonicalKey();
        int stateId = GetStateId(key);

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
        var groupedBranches = new SortedDictionary<string, BranchInfo>(StringComparer.Ordinal);
        foreach (var order in EnumerateFeasibleOrders(state, group))
        {
            var next = state.Clone();
            next.ApplyOrder(order);
            next.Eliminate(_k);

            string nextKey = next.GetCanonicalKey();
            if (!groupedBranches.TryGetValue(nextKey, out BranchInfo? branch))
                groupedBranches[nextKey] = new BranchInfo(next, FormatOrder(order));
        }

        return groupedBranches.Values
            .OrderBy(v => v.RepresentativeOrder, StringComparer.Ordinal)
            .Select(v => new StrategyBranch(
                v.RepresentativeOrder,
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
        string currentKey = state.GetCanonicalKey();
        var labels = state.GetStructuralLabels();

        List<int>? bestGroup = null;
        (int negWorstCaseSteps, int negFreshItems, int negUnrelatedScore, int negGroupSize, int distinctStates, int totalReduction, int unresolvedPairs) bestScore =
            (int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue, int.MinValue);

        for (int groupSize = 2; groupSize <= maxGroupSize; groupSize++)
        {
            var seenGroupPatterns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in EnumerateCombinations(candidates, groupSize))
            {
                if (!seenGroupPatterns.Add(GetGroupPattern(group, labels)))
                    continue;

                var nextStateKeys = new HashSet<string>(StringComparer.Ordinal);
                int worstCaseSteps = 0;
                int totalReduction = 0;
                bool isUseful = false;

                foreach (var order in EnumerateFeasibleOrders(state, group))
                {
                    var next = state.Clone();
                    next.ApplyOrder(order);
                    next.Eliminate(_k);

                    string nextKey = next.GetCanonicalKey();
                    if (nextKey == currentKey)
                        continue;

                    isUseful = true;
                    int reduction = state.ActiveCount - next.ActiveCount;
                    totalReduction += reduction;
                    nextStateKeys.Add(nextKey);

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
        if (state.ActiveCount <= _k)
            return 0;

        var possibleCandidates = GetPossibleCandidates(state);
        if (possibleCandidates.Count > GetRemainingSlots(state) && possibleCandidates.Count <= _m)
            return 1;

        string key = state.GetCanonicalKey();
        if (_minWorstCaseStepsCache.TryGetValue(key, out int cached))
            return cached;

        var candidates = state.GetActiveItemsOrdered();
        int maxGroupSize = Math.Min(_m, candidates.Count);
        var labels = state.GetStructuralLabels();
        int bestWorstCase = int.MaxValue;

        for (int groupSize = 2; groupSize <= maxGroupSize; groupSize++)
        {
            var seenGroupPatterns = new HashSet<string>(StringComparer.Ordinal);
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

                    string nextKey = next.GetCanonicalKey();
                    if (nextKey == key)
                        continue;

                    isUseful = true;
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

    private static string GetGroupPattern(IReadOnlyList<int> group, IReadOnlyList<int> labels)
    {
        return string.Join(",", group.Select(i => labels[i]).OrderBy(x => x));
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

    private int GetStateId(string key)
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

        public BranchInfo(ComparisonState nextState, string representativeOrder)
        {
            NextState = nextState;
            RepresentativeOrder = representativeOrder;
        }
    }
}
