using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

partial class StrategyBuilder
{
    private IEnumerable<OrderFamilyDescriptor> EnumerateFeasibleOrderFamilies(
        ComparisonState state,
        IReadOnlyList<int> group,
        int eliminationThreshold,
        bool pruneDoomedTails)
    {
        ThrowIfCancellationRequested();
        GroupSymmetryInfo symmetryInfo = BuildGroupSymmetryInfo(state, group);
        if (symmetryInfo.Classes.All(@class => @class.Items.Length == 1))
        {
            ulong groupMask = 0;
            foreach (int item in group)
                groupMask |= 1UL << item;

            // For each group item, the number of its active ancestors that lie OUTSIDE the group.
            // After the group sort, an item placed at 0-indexed position p gains exactly p further
            // ancestors (every group item ranked above it), so its total active-ancestor count is
            // baseAncestors[item] + p. It is eliminated when that reaches the elimination threshold.
            int[]? baseAncestors = pruneDoomedTails ? BuildOutsideAncestorCounts(state, group, groupMask) : null;

            var current = new List<int>(group.Count);
            foreach (var order in EnumerateFeasibleOrders(
                state, groupMask, group.Count, current, baseAncestors, eliminationThreshold))
            {
                yield return OrderFamilyDescriptor.CreateSingleton(order);
            }

            yield break;
        }

        foreach (var family in EnumerateSymmetricOrderFamilies(symmetryInfo))
            yield return family;
    }

    private static int[] BuildOutsideAncestorCounts(ComparisonState state, IReadOnlyList<int> group, ulong groupMask)
    {
        ulong outsideActiveMask = state.ActiveMask & ~groupMask;
        int[] counts = new int[64];
        foreach (int item in group)
            counts[item] = BitOperations.PopCount(state.GetAncestorMask(item) & outsideActiveMask);
        return counts;
    }

    private IEnumerable<List<int>> EnumerateFeasibleOrders(
        ComparisonState state,
        ulong remainingMask,
        int total,
        List<int> current,
        int[]? baseAncestors,
        int eliminationThreshold)
    {
        ThrowIfCancellationRequested();
        if (current.Count == total)
        {
            yield return new List<int>(current);
            yield break;
        }

        // Search-path pruning: if every still-unplaced item is guaranteed to be eliminated no
        // matter where it lands (baseAncestors[item] + position >= threshold, with position at
        // least the current depth), then all completions of this prefix differ only in the order
        // of doomed items. Eliminated items are masked out of the next state's canonical key, so
        // every completion yields the identical next search-state; one representative suffices.
        bool doomed = false;
        if (baseAncestors is not null)
        {
            doomed = true;
            int depth = current.Count;
            ulong unplaced = remainingMask;
            while (unplaced != 0)
            {
                int item = BitOperations.TrailingZeroCount(unplaced);
                unplaced &= unplaced - 1;
                if (baseAncestors[item] + depth < eliminationThreshold)
                {
                    doomed = false;
                    break;
                }
            }
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
            foreach (var order in EnumerateFeasibleOrders(
                state, remainingMask & ~(1UL << next), total, current, baseAncestors, eliminationThreshold))
            {
                yield return order;
            }

            current.RemoveAt(current.Count - 1);

            // All remaining items are doomed, so the single completion just produced already
            // represents every ordering of the unplaced tail; skip the rest.
            if (doomed)
                break;
        }
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
