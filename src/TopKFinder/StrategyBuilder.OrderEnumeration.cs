using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

partial class StrategyBuilder
{
    private IEnumerable<OrderFamilyDescriptor> EnumerateFeasibleOrderFamilies(ComparisonState state, IReadOnlyList<int> group)
    {
        ProbeCancellation(0);
        GroupSymmetryInfo symmetryInfo = BuildGroupSymmetryInfo(state, group);
        if (symmetryInfo.Classes.All(@class => @class.Items.Length == 1))
        {
            ulong remainingMask = 0;
            foreach (int item in group)
                remainingMask |= 1UL << item;

            var current = new List<int>(group.Count);
            foreach (var order in EnumerateFeasibleOrders(state, remainingMask, group.Count, current))
                yield return OrderFamilyDescriptor.CreateSingleton(order);
            yield break;
        }

        foreach (var family in EnumerateSymmetricOrderFamilies(symmetryInfo))
            yield return family;
    }

    private IEnumerable<List<int>> EnumerateFeasibleOrders(
        ComparisonState state,
        ulong remainingMask,
        int total,
        List<int> current)
    {
        ProbeCancellation(0);
        if (current.Count == total)
        {
            yield return new List<int>(current);
            yield break;
        }

        // A candidate may be placed next only if no still-remaining item is one of its ancestors
        // (i.e. it is maximal among the remaining items). Bits are iterated ascending, matching
        // the original OrderBy(x => x) tie-break.
        ulong candidates = remainingMask;
        while (candidates != 0)
        {
            int next = BitOperations.TrailingZeroCount(candidates);
            candidates &= candidates - 1;
            if ((state.GetAncestorMask(next) & remainingMask) != 0)
                continue;

            current.Add(next);
            foreach (var order in EnumerateFeasibleOrders(state, remainingMask & ~(1UL << next), total, current))
                yield return order;
            current.RemoveAt(current.Count - 1);
        }
    }

    // Lean enumerator for the search and compact paths, which only need the set of distinct next
    // search-states (not the displayed order families). It produces one representative order per
    // distinct next state by combining three sound reductions, all without the LINQ-heavy
    // GroupSymmetryInfo construction or per-order descriptor allocations of the display path:
    //   1. feasibility: only place an item that is maximal among the still-unplaced group items;
    //   2. symmetry: within a class of items that are interchangeable under an automorphism of
    //      the active DAG, place members in increasing-id order only -- the permutations collapse
    //      to isomorphic next states with identical canonical keys. Classes cover both local
    //      symmetry (identical active ancestor/descendant masks, a trivial transposition) and
    //      block/orbit symmetry (e.g. the parallel chains 1>2>3, 4>5>6, 7>8>9 make the heads
    //      1,4,7 interchangeable via a chain-permuting automorphism), see BuildSearchClassPredecessors;
    //   3. doomed-tail pruning: once every still-unplaced item is guaranteed to be eliminated
    //      regardless of its position (outsideAncestors + position >= eliminationThreshold), all
    //      completions yield the identical next state (eliminated items are masked out of the key),
    //      so a single representative suffices.
    // The yielded list is a reused buffer; callers must consume it before requesting the next.
    private IEnumerable<IReadOnlyList<int>> EnumerateSearchOrders(
        ComparisonState state,
        IReadOnlyList<int> group,
        int eliminationThreshold)
    {
        ProbeCancellation(0);
        ulong groupMask = 0;
        foreach (int item in group)
            groupMask |= 1UL << item;

        ulong activeMask = state.ActiveMask;
        ulong outsideActiveMask = activeMask & ~groupMask;
        var outsideAncestors = new int[_n];
        foreach (int item in group)
            outsideAncestors[item] = BitOperations.PopCount(state.GetAncestorMask(item) & outsideActiveMask);

        // Each item is deferred until the nearest lower-id member of its symmetry class is placed,
        // which fixes a single increasing-id representative per class ordering.
        int[] previousInClass = BuildSearchClassPredecessors(state, group, activeMask);

        var current = new List<int>(group.Count);
        foreach (var order in EnumerateSearchOrdersCore(
            state, groupMask, group.Count, current, outsideAncestors, previousInClass, eliminationThreshold))
        {
            yield return order;
        }
    }

    private IEnumerable<IReadOnlyList<int>> EnumerateSearchOrdersCore(
        ComparisonState state,
        ulong remainingMask,
        int total,
        List<int> current,
        int[] outsideAncestors,
        int[] previousInClass,
        int eliminationThreshold)
    {
        ProbeCancellation(0);
        if (current.Count == total)
        {
            yield return current;
            yield break;
        }

        int depth = current.Count;
        bool doomed = true;
        ulong unplaced = remainingMask;
        while (unplaced != 0)
        {
            int item = BitOperations.TrailingZeroCount(unplaced);
            unplaced &= unplaced - 1;
            if (outsideAncestors[item] + depth < eliminationThreshold)
            {
                doomed = false;
                break;
            }
        }

        ulong candidates = remainingMask;
        while (candidates != 0)
        {
            int next = BitOperations.TrailingZeroCount(candidates);
            candidates &= candidates - 1;
            if ((state.GetAncestorMask(next) & remainingMask) != 0)
                continue;

            int previous = previousInClass[next];
            if (previous >= 0 && (remainingMask & (1UL << previous)) != 0)
                continue;

            current.Add(next);
            foreach (var order in EnumerateSearchOrdersCore(
                state, remainingMask & ~(1UL << next), total, current, outsideAncestors, previousInClass, eliminationThreshold))
            {
                yield return order;
            }

            current.RemoveAt(current.Count - 1);

            // Every still-unplaced item is doomed, so the single completion just produced already
            // represents every ordering of the unplaced tail; skip the remaining candidates.
            if (doomed)
                break;
        }
    }

    // Computes, for each group item, the nearest lower-id member of its symmetry class (or -1),
    // which the lean enumerator uses to fix one increasing-id representative per class ordering.
    // Two group items share a class iff there is an automorphism of the active DAG that swaps them
    // while fixing every other group item -- a sufficient condition for all orderings differing
    // only by permuting class members to collapse to isomorphic next states (identical canonical
    // keys). This subsumes the local case (identical active ancestor/descendant masks, a trivial
    // transposition) and additionally captures block/orbit symmetry such as the interchangeable
    // heads 1,4,7 of the parallel chains in 9,3,3, which have different descendant sets and so are
    // not locally symmetric but are related by a chain-permuting automorphism.
    [ThreadStatic] private static int[]? _classParentScratch;

    private static int ClassFind(int[] parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]];
            x = parent[x];
        }

        return x;
    }

    private static void ClassUnion(int[] parent, int a, int b)
    {
        int ra = ClassFind(parent, a);
        int rb = ClassFind(parent, b);
        if (ra == rb)
            return;

        // Keep the lower id as the representative so predecessors stay well-defined.
        if (ra < rb)
            parent[rb] = ra;
        else
            parent[ra] = rb;
    }

    // Partitions the group into symmetry classes via union-find, merging two members whenever an
    // automorphism of the active DAG swaps them while fixing the rest of the group. Returns the
    // shared scratch parent array; callers must consume it before any other call reuses the scratch.
    private int[] BuildGroupSymmetryParents(ComparisonState state, IReadOnlyList<int> group, ulong activeMask)
    {
        ThrowIfCancellationRequested();
        int[] parent = _classParentScratch is { Length: var len } buffer && len >= _n
            ? buffer
            : (_classParentScratch = new int[_n]);
        foreach (int item in group)
            parent[item] = item;

        // Local symmetry: identical active ancestor and descendant masks => the bare transposition
        // (i j) is already an automorphism, so these items are interchangeable in every context.
        for (int a = 0; a < group.Count; a++)
        {
            ThrowIfCancellationRequested();
            int i = group[a];
            ulong iAnc = state.GetAncestorMask(i) & activeMask;
            ulong iDesc = state.GetDescendantMask(i) & activeMask;
            for (int b = a + 1; b < group.Count; b++)
            {
                int j = group[b];
                if ((state.GetAncestorMask(j) & activeMask) == iAnc &&
                    (state.GetDescendantMask(j) & activeMask) == iDesc)
                {
                    ClassUnion(parent, i, j);
                }
            }
        }

        // Block/orbit symmetry: for items not already merged locally, look for an automorphism of
        // the active DAG that swaps them while fixing the rest of the group. Two items can only be
        // related by such an automorphism if they share a stable WL color and relate identically to
        // every other group item with matching ancestor/descendant degrees; those cheap (cached)
        // tests gate the more expensive backtracking search so it runs only on genuine candidates.
        int[] labels = state.GetStructuralLabels();
        for (int a = 0; a < group.Count; a++)
        {
            ThrowIfCancellationRequested();
            int i = group[a];
            for (int b = a + 1; b < group.Count; b++)
            {
                int j = group[b];
                if (ClassFind(parent, i) == ClassFind(parent, j) || labels[i] != labels[j])
                    continue;

                if (!IsGroupSwapCandidate(state, activeMask, group, i, j))
                    continue;

                if (TryFindGroupFixingSwap(state, labels, activeMask, group, i, j))
                    ClassUnion(parent, i, j);
            }
        }

        return parent;
    }

    private int[] BuildSearchClassPredecessors(ComparisonState state, IReadOnlyList<int> group, ulong activeMask)
    {
        int[] parent = BuildGroupSymmetryParents(state, group, activeMask);

        var previousInClass = new int[_n];
        foreach (int item in group)
        {
            int root = ClassFind(parent, item);
            int previous = -1;
            foreach (int other in group)
            {
                if (other < item && other > previous && ClassFind(parent, other) == root)
                    previous = other;
            }

            previousInClass[item] = previous;
        }

        return previousInClass;
    }

    // Cheap necessary conditions for a group-fixing swap of i and j: matching ancestor/descendant
    // degrees (an automorphism preserves them) and identical relationships to every other group
    // item (the swap must fix those items). Rejects most non-symmetric pairs before backtracking.
    private bool IsGroupSwapCandidate(ComparisonState state, ulong activeMask, IReadOnlyList<int> group, int i, int j)
    {
        ulong ai = state.GetAncestorMask(i) & activeMask;
        ulong aj = state.GetAncestorMask(j) & activeMask;
        ulong di = state.GetDescendantMask(i) & activeMask;
        ulong dj = state.GetDescendantMask(j) & activeMask;
        if (BitOperations.PopCount(ai) != BitOperations.PopCount(aj) ||
            BitOperations.PopCount(di) != BitOperations.PopCount(dj))
        {
            return false;
        }

        foreach (int g in group)
        {
            if (g == i || g == j)
                continue;

            ulong gb = 1UL << g;
            if (((ai & gb) != 0) != ((aj & gb) != 0) || ((di & gb) != 0) != ((dj & gb) != 0))
                return false;
        }

        return true;
    }

    // Searches for an automorphism of the active DAG that swaps i and j while fixing every other
    // group item. The swap's support is confined to items related to i or j (their cones and shared
    // ancestors); every unrelated active item is left fixed. The candidate map is completed by
    // color-guided backtracking over the related items and then fully verified to preserve the
    // ancestor relation across all active pairs, so a success is a genuine automorphism -- making
    // the resulting class merge sound regardless of WL completeness. A node budget bounds the
    // search; exhausting it returns false (no merge), which only forgoes an optimization.
    private bool TryFindGroupFixingSwap(
        ComparisonState state,
        int[] labels,
        ulong activeMask,
        IReadOnlyList<int> group,
        int i,
        int j)
    {
        int n = _n;
        var map = new int[n];
        var inv = new int[n];
        Array.Fill(map, -1);
        Array.Fill(inv, -1);
        var assigned = new List<int>(n);

        bool TryAssign(int x, int y)
        {
            if (labels[x] != labels[y] || inv[y] != -1 || map[x] != -1)
                return false;

            ulong ancX = state.GetAncestorMask(x);
            ulong ancY = state.GetAncestorMask(y);
            foreach (int z in assigned)
            {
                int w = map[z];
                if (((ancX >> z) & 1) != ((ancY >> w) & 1) ||
                    ((state.GetAncestorMask(z) >> x) & 1) != ((state.GetAncestorMask(w) >> y) & 1))
                {
                    return false;
                }
            }

            map[x] = y;
            inv[y] = x;
            assigned.Add(x);
            return true;
        }

        void Undo()
        {
            int x = assigned[assigned.Count - 1];
            assigned.RemoveAt(assigned.Count - 1);
            inv[map[x]] = -1;
            map[x] = -1;
        }

        // Forced assignments: the swap plus every other group item fixed in place.
        if (!TryAssign(i, j) || !TryAssign(j, i))
            return false;

        foreach (int g in group)
        {
            if (g != i && g != j && !TryAssign(g, g))
                return false;
        }

        // Only items related to i or j may move; everything else stays fixed (verified at the end).
        ulong relatedMask = (state.GetAncestorMask(i) | state.GetDescendantMask(i) |
                             state.GetAncestorMask(j) | state.GetDescendantMask(j)) & activeMask;
        var pending = new List<int>(n);
        ulong remaining = relatedMask;
        while (remaining != 0)
        {
            int x = BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;
            if (map[x] == -1)
                pending.Add(x);
        }

        int budget = 1024;

        bool VerifyComplete()
        {
            ulong items = activeMask;
            while (items != 0)
            {
                ProbeCancellation();
                int x = BitOperations.TrailingZeroCount(items);
                items &= items - 1;
                int sx = map[x] >= 0 ? map[x] : x;
                ulong inner = activeMask;
                while (inner != 0)
                {
                    ProbeCancellation();
                    int y = BitOperations.TrailingZeroCount(inner);
                    inner &= inner - 1;
                    int sy = map[y] >= 0 ? map[y] : y;
                    if (((state.GetAncestorMask(x) >> y) & 1) != ((state.GetAncestorMask(sx) >> sy) & 1))
                        return false;
                }
            }

            return true;
        }

        bool Extend(int index)
        {
            ProbeCancellation();
            if (--budget < 0)
                return false;

            while (index < pending.Count && map[pending[index]] != -1)
                index++;

            if (index == pending.Count)
                return VerifyComplete();

            int x = pending[index];
            ulong candidates = relatedMask;
            while (candidates != 0)
            {
                ProbeCancellation();
                int y = BitOperations.TrailingZeroCount(candidates);
                candidates &= candidates - 1;
                if (TryAssign(x, y))
                {
                    if (Extend(index + 1))
                        return true;

                    Undo();
                }
            }

            return false;
        }

        return Extend(0);
    }

    private GroupSymmetryInfo BuildGroupSymmetryInfo(ComparisonState state, IReadOnlyList<int> group)
    {
        ulong activeMask = state.ActiveMask;
        int[] parent = BuildGroupSymmetryParents(state, group, activeMask);

        var classItems = new Dictionary<int, List<int>>();
        foreach (int item in group)
        {
            int root = ClassFind(parent, item);
            if (!classItems.TryGetValue(root, out var items))
            {
                items = new List<int>();
                classItems[root] = items;
            }

            items.Add(item);
        }

        var classes = classItems.Values
            .Select(items => items.OrderBy(item => item).ToArray())
            .OrderBy(items => items[0])
            .Select((items, index) => new GroupSymmetryClass(index, items, state.GetAncestorMask(items[0]) & activeMask))
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
        ProbeCancellation(0);
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
        ProbeCancellation(0);
        if (remainingMask == 0)
        {
            yield return OrderFamilyDescriptor.CreateSymmetric(
                representativeOrder,
                SaturatingToInt32(multiplicity),
                symmetryInfo,
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

    private static int SaturatingToInt32(BigInteger value)
    {
        if (value <= int.MinValue)
            return int.MinValue;
        if (value >= int.MaxValue)
            return int.MaxValue;
        return (int)value;
    }

    private static int SaturatingAdd(int left, int right)
    {
        long sum = (long)left + right;
        if (sum >= int.MaxValue)
            return int.MaxValue;
        if (sum <= int.MinValue)
            return int.MinValue;
        return (int)sum;
    }

    private static string BuildSymmetricFamilyPatternText(GroupSymmetryInfo symmetryInfo, IReadOnlyList<int> classSequence)
    {
        if (symmetryInfo.Classes.Count == 1)
            return $"permute {FormatBraceSet(symmetryInfo.Classes[0].Items)}";

        return BuildPermutationTemplateText(
            symmetryInfo.Classes.Select(@class => (IReadOnlyList<int>)@class.Items).ToList(),
            classSequence);
    }

    internal sealed class GroupSymmetryClass
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

    internal sealed class GroupSymmetryInfo
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

    internal sealed class OrderFamilyDescriptor
    {
        private readonly GroupSymmetryInfo? _symmetryInfo;
        private readonly int[]? _classSequence;
        private string? _representativeOrder;
        private string? _patternText;
        private string? _countFormula;

        private OrderFamilyDescriptor(
            IReadOnlyList<int> representativeOrderItems,
            int count,
            GroupSymmetryInfo? symmetryInfo,
            int[]? classSequence)
        {
            RepresentativeOrderItems = representativeOrderItems;
            Count = count;
            _symmetryInfo = symmetryInfo;
            _classSequence = classSequence;
        }

        public IReadOnlyList<int> RepresentativeOrderItems { get; }
        public int Count { get; }

        // The display strings below are only needed when materializing the strategy tree
        // (phase 2). Phase-1 search touches hundreds of thousands of families but only reads
        // RepresentativeOrderItems, so these are computed lazily to avoid that wasted work.
        public string RepresentativeOrder =>
            _representativeOrder ??= FormatOrder(RepresentativeOrderItems);

        public string PatternText =>
            _patternText ??= _symmetryInfo is null
                ? RepresentativeOrder
                : BuildSymmetricFamilyPatternText(_symmetryInfo, _classSequence!);

        public string CountFormula =>
            _countFormula ??= _symmetryInfo is null
                ? "1"
                : BuildMultiplicityFormula(_symmetryInfo.Classes.Select(@class => @class.Items.Length));

        public static OrderFamilyDescriptor CreateSingleton(IReadOnlyList<int> order)
        {
            return new OrderFamilyDescriptor(order.ToArray(), 1, null, null);
        }

        public static OrderFamilyDescriptor CreateSymmetric(
            IReadOnlyList<int> representativeOrder,
            int count,
            GroupSymmetryInfo symmetryInfo,
            int[] classSequence)
        {
            return new OrderFamilyDescriptor(representativeOrder.ToArray(), count, symmetryInfo, classSequence);
        }
    }
}
