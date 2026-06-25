using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

partial class StrategyBuilder
{
    private IEnumerable<OrderFamilyDescriptor> EnumerateFeasibleOrderFamilies(ComparisonState state, IReadOnlyList<int> group)
    {
        ThrowIfCancellationRequested();
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
        ThrowIfCancellationRequested();
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
        ThrowIfCancellationRequested();
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
        ThrowIfCancellationRequested();
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

    private int[] BuildSearchClassPredecessors(ComparisonState state, IReadOnlyList<int> group, ulong activeMask)
    {
        int[] parent = _classParentScratch is { Length: var len } buffer && len >= _n
            ? buffer
            : (_classParentScratch = new int[_n]);
        foreach (int item in group)
            parent[item] = item;

        // Local symmetry: identical active ancestor and descendant masks => the bare transposition
        // (i j) is already an automorphism, so these items are interchangeable in every context.
        for (int a = 0; a < group.Count; a++)
        {
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
                int x = BitOperations.TrailingZeroCount(items);
                items &= items - 1;
                int sx = map[x] >= 0 ? map[x] : x;
                ulong inner = activeMask;
                while (inner != 0)
                {
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
        var groupedItems = group
            .GroupBy(item => new SymmetrySignature(state.GetAncestorMask(item) & activeMask, state.GetDescendantMask(item) & activeMask))
            .OrderBy(grouping => grouping.Min())
            .ToList();

        var classes = groupedItems
            .Select((grouping, index) =>
            {
                int[] items = grouping.OrderBy(item => item).ToArray();
                return new GroupSymmetryClass(index, items, state.GetAncestorMask(items[0]) & activeMask);
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
                checked((int)multiplicity),
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

    private static EquivalentOrderSummary? BuildEquivalentOrderSummary(
        IReadOnlyList<OrderFamilyDescriptor> orderFamilies)
    {
        if (orderFamilies.Count == 0)
            return null;

        int totalCount = orderFamilies.Sum(family => family.Count);
        if (totalCount <= 1)
            return null;

        // When a branch merges several individually-distinct orders (all singleton families), the
        // items involved fall in different symmetry classes yet every ordering still collapses to
        // the same next state. Rendering each order separately produces a long disjunction such as
        // "(#1 > #4 > #7 | #1 > #7 > #4 | ...)". Run the concrete orders through the holistic
        // pattern engine instead so the common shapes (a full permutation, independent blocks, a
        // shared prefix/suffix, ...) collapse to a compact form, e.g. "permute {#1, #4, #7}".
        if (orderFamilies.Count > 1 && orderFamilies.All(family => family.Count == 1))
        {
            IReadOnlyList<int> representativeOrder = orderFamilies[0].RepresentativeOrderItems;
            var representativePositions = new Dictionary<int, int>(representativeOrder.Count);
            for (int index = 0; index < representativeOrder.Count; index++)
                representativePositions[representativeOrder[index]] = index;

            var orders = orderFamilies
                .Select(family => (IReadOnlyList<int>)family.RepresentativeOrderItems.ToArray())
                .ToList();

            EquivalentPatternSummary summary = BuildEquivalentPatternSummary(
                orders,
                representativeOrder.ToArray(),
                representativePositions);

            return new EquivalentOrderSummary(totalCount, summary.PatternText, summary.TotalCountFormula);
        }

        string patternText = orderFamilies.Count == 1
            ? orderFamilies[0].PatternText
            : "(" + string.Join(" | ", orderFamilies.Select(family => family.PatternText)) + ")";
        string countFormula = CombineFormulaParts(orderFamilies.Select(family => family.CountFormula).ToList());
        return new EquivalentOrderSummary(totalCount, patternText, countFormula);
    }

    private static string FormatBraceSet(IEnumerable<int> items)
    {
        return "{" + StrategyTextRenderer.FormatSet(items) + "}";
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
        int start = 0;
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '{' || ch == '(')
                depth++;
            else if (ch == '}' || ch == ')')
                depth--;
            else if (ch == ',' && depth == 0)
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        parts.Add(text[start..].Trim());
        return parts;
    }

    private static string RewritePatternAliases(string text, IReadOnlyDictionary<string, string> aliasMap)
    {
        string rewritten = text;
        foreach (var entry in aliasMap)
        {
            rewritten = Regex.Replace(
                rewritten,
                $@"\b{Regex.Escape(entry.Key)}(?=\d)",
                entry.Value);
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

    private static string BuildPermutationTemplateText(
        IReadOnlyList<IReadOnlyList<int>> partition,
        IReadOnlyList<int> template)
    {
        var aliases = new string[partition.Count];
        for (int blockIndex = 0; blockIndex < partition.Count; blockIndex++)
            aliases[blockIndex] = partition[blockIndex].Count > 1 ? GetAliasName(blockIndex) : string.Empty;

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

        string body = string.Join(" > ", tokens);
        return $"{string.Join(", ", definitions)}; {body}";
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

    private static BigInteger Factorial(int n)
    {
        BigInteger result = BigInteger.One;
        for (int i = 2; i <= n; i++)
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
