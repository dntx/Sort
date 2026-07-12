using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

partial class StrategyBuilder
{
    private const int PermutationTemplateMinRemainingItems = 4;
    private const int PartialPatternFamilyMinOrders = 4;
    private const int WindowPermutationFamilyMinOrderLength = 4;
    private const int InterleavedChainTrackMinItems = 4;
    private const int InterleavedChainTrackMaxItems = 8;

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

        EquivalentPatternSummary? orderedBlockPermutationSummary =
            TryBuildOrderedBlockPermutationSummary(orders, remainingItems, representativePositions);
        if (orderedBlockPermutationSummary is not null)
            return orderedBlockPermutationSummary;

        EquivalentPatternSummary? orderedChainTrackPermutationSummary =
            TryBuildOrderedChainTrackPermutationSummary(orders, remainingItems, representativePositions);
        if (orderedChainTrackPermutationSummary is not null)
            return orderedChainTrackPermutationSummary;

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
        if (remainingItems.Count < PermutationTemplateMinRemainingItems)
            return null;

        PermutationTemplateCandidate? bestCandidate = null;
        foreach (var partition in EnumeratePartitions(
            remainingItems.OrderBy(item => representativePositions[item]).ToArray(), orders.Count))
        {
            int multiBlockCount = partition.Count(block => block.Count > 1);
            if (multiBlockCount == 0)
                continue;

            if (multiBlockCount == 1 && partition.Count == 1)
                continue;

            // Necessary condition, identical to the orders.Count == expectedTotal gate below: a
            // block of size s that permutes fully contributes s! orderings, so the product of the
            // block factorials must equal the order count. Checking it here from block sizes alone
            // skips the expensive per-order projection/template work for the overwhelming majority
            // of partitions (Bell(n) grows fast; only a tiny fraction can ever match).
            BigInteger blockPermutationProduct = BigInteger.One;
            foreach (var block in partition)
            {
                if (block.Count > 1)
                    blockPermutationProduct *= Factorial(block.Count);
                if (blockPermutationProduct > orders.Count)
                    break;
            }
            if (blockPermutationProduct != orders.Count)
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
        if (orders.Count < PartialPatternFamilyMinOrders || orderLength <= 1)
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
                if (members.Count < PartialPatternFamilyMinOrders)
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
        if (orders.Count < PartialPatternFamilyMinOrders || orderLength < WindowPermutationFamilyMinOrderLength)
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

    // Detects an "ordered-block permutation": the remaining items split into chains (blocks) whose
    // internal order is identical in every ordering, and the orderings are exactly the p! ways of
    // arranging those blocks. The other templates only permute items WITHIN fixed-position blocks,
    // never the multi-item blocks themselves, so such orderings otherwise fall through to the " | "
    // disjunction and get split into one branch each (e.g. 10,4,8 S3: {#3>#4>#7>#8, #7>#8>#3>#4}).
    // Rendered as an inline brace set of ordered chains, "{#3 > #4, #7 > #8}" (= these blocks in any
    // order). Requires p >= 2 blocks, at least one block of size >= 2 (an all-singleton partition is
    // the plain full permutation handled earlier), and all p! arrangements present exactly once.
    private static EquivalentPatternSummary? TryBuildOrderedBlockPermutationSummary(
        IReadOnlyList<IReadOnlyList<int>> orders,
        IReadOnlyList<int> remainingItems,
        IReadOnlyDictionary<int, int> representativePositions)
    {
        if (orders.Count <= 1 || orders[0].Count <= 1)
            return null;

        // a -> b is a block-internal edge iff b immediately follows a in EVERY ordering. An item with
        // no such fixed successor is a block tail; one with no fixed predecessor is a block head.
        var fixedSuccessor = new Dictionary<int, int>();
        var hasFixedPredecessor = new HashSet<int>();
        foreach (int a in remainingItems)
        {
            int? candidate = null;
            bool fixedEdge = true;
            foreach (IReadOnlyList<int> order in orders)
            {
                int pos = -1;
                for (int i = 0; i < order.Count; i++)
                    if (order[i] == a) { pos = i; break; }
                int? next = pos >= 0 && pos + 1 < order.Count ? order[pos + 1] : null;
                if (next is null) { fixedEdge = false; break; }
                if (candidate is null)
                    candidate = next;
                else if (candidate != next) { fixedEdge = false; break; }
            }

            if (fixedEdge && candidate is not null)
            {
                fixedSuccessor[a] = candidate.Value;
                hasFixedPredecessor.Add(candidate.Value);
            }
        }

        // Assemble blocks by following fixed-successor chains from each head.
        var blocks = new List<List<int>>();
        foreach (int item in remainingItems)
        {
            if (hasFixedPredecessor.Contains(item))
                continue;

            var block = new List<int> { item };
            int current = item;
            while (fixedSuccessor.TryGetValue(current, out int next))
            {
                block.Add(next);
                current = next;
            }
            blocks.Add(block);
        }

        int p = blocks.Count;
        if (p < 2 || blocks.All(block => block.Count < 2))
            return null;
        if (orders.Count != (int)Factorial(p))
            return null;

        // The orderings must be EXACTLY the p! arrangements of the blocks (internal order fixed).
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (List<int> permutation in EnumerateIndexPermutations(p))
            expected.Add(string.Join(",", permutation.SelectMany(blockIndex => blocks[blockIndex])));
        var actual = new HashSet<string>(orders.Select(order => string.Join(",", order)), StringComparer.Ordinal);
        if (!expected.SetEquals(actual))
            return null;

        string inner = string.Join(", ", blocks
            .OrderBy(block => representativePositions[block[0]])
            .Select(block => string.Join(" > ", block.Select(item => $"#{item + 1}"))));
        return new EquivalentPatternSummary("{" + inner + "}", $"{p}!", Factorial(p));
    }

    // Generalizes the ordered-block permutation to INTERLEAVED chains: the items form k equal-length
    // ordered chains (each chain's internal order is identical in every ordering) woven into k fixed
    // position-tracks, and the orderings are exactly the k! ways of assigning the interchangeable
    // chains to those tracks. TryBuildOrderedBlockPermutationSummary already covers the contiguous
    // case (tracks = consecutive runs, rendered "{#3 > #4, #7 > #8}"); this catches the case where the
    // tracks interleave (e.g. 9,4,7 S3: {#3>#7>#4>#8, #7>#3>#8>#4} = chains #3>#4 and #7>#8 woven as
    // A B A B / B A B A), which otherwise falls through to the " | " disjunction and splits into one
    // branch per ordering. Rendered as "{#3 > #4, #7 > #8} interleaved as #3 > #7 > #4 > #8": the
    // listed ordered chains, in any order, woven into the shown representative frame.
    private static EquivalentPatternSummary? TryBuildOrderedChainTrackPermutationSummary(
        IReadOnlyList<IReadOnlyList<int>> orders,
        IReadOnlyList<int> remainingItems,
        IReadOnlyDictionary<int, int> representativePositions)
    {
        int n = remainingItems.Count;
        if (orders.Count <= 1 || n < InterleavedChainTrackMinItems || n > InterleavedChainTrackMaxItems)
            return null;

        List<List<int>>? bestChains = null;
        int bestK = 0;
        int bestSpan = int.MaxValue;
        string? bestChainKey = null;

        for (int s = 2; s <= n / 2; s++)
        {
            if (n % s != 0)
                continue;
            int k = n / s;
            if (k < 2 || orders.Count != (int)Factorial(k))
                continue;

            foreach (List<List<int>> tracks in EnumerateEqualTrackPartitions(n, s))
            {
                // Only interleaved track layouts are new here; contiguous ones are handled (and
                // rendered as concatenation) by the ordered-block detector that runs earlier.
                if (tracks.All(IsContiguousTrack))
                    continue;

                List<int[]>? chains = TryResolveChainTracks(orders, tracks, k);
                if (chains is null)
                    continue;

                var ordered = chains
                    .OrderBy(chain => representativePositions[chain[0]])
                    .Select(chain => chain.ToList())
                    .ToList();
                int span = ordered.Sum(chain => chain.Max() - chain.Min());
                string chainKey = string.Join("|", ordered.Select(chain => string.Join(",", chain)));
                if (span < bestSpan || (span == bestSpan && string.CompareOrdinal(chainKey, bestChainKey) < 0))
                {
                    bestChains = ordered;
                    bestK = k;
                    bestSpan = span;
                    bestChainKey = chainKey;
                }
            }
        }

        if (bestChains is null)
            return null;

        string chainSetText = "{" + string.Join(", ", bestChains
            .Select(chain => string.Join(" > ", chain.Select(item => $"#{item + 1}")))) + "}";
        string frameText = string.Join(" > ", orders[0].Select(item => $"#{item + 1}"));
        return new EquivalentPatternSummary(
            $"{chainSetText} interleaved as {frameText}",
            $"{bestK}!",
            Factorial(bestK));
    }

    private static bool IsContiguousTrack(List<int> track)
    {
        for (int i = 1; i < track.Count; i++)
        {
            if (track[i] != track[i - 1] + 1)
                return false;
        }

        return true;
    }

    // Validates that every ordering places the SAME k ordered chains into the given tracks (one chain
    // per track, a bijection) and that all k! distinct chain->track assignments appear exactly once.
    // Returns the k chains (taken from the representative ordering) on success, otherwise null.
    private static List<int[]>? TryResolveChainTracks(
        IReadOnlyList<IReadOnlyList<int>> orders,
        List<List<int>> tracks,
        int k)
    {
        var chainSet = new HashSet<string>(StringComparer.Ordinal);
        var repChains = new List<int[]>(k);
        foreach (List<int> track in tracks)
        {
            int[] content = track.Select(pos => orders[0][pos]).ToArray();
            repChains.Add(content);
            chainSet.Add(string.Join(",", content));
        }

        if (chainSet.Count != k)
            return null;

        var assignmentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (IReadOnlyList<int> order in orders)
        {
            var contents = new List<string>(k);
            var localSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (List<int> track in tracks)
            {
                string content = string.Join(",", track.Select(pos => order[pos]));
                contents.Add(content);
                localSet.Add(content);
            }

            if (!localSet.SetEquals(chainSet))
                return null;

            assignmentKeys.Add(string.Join("|", contents));
        }

        if (assignmentKeys.Count != orders.Count)
            return null;

        return repChains;
    }

    // Enumerates the unordered partitions of positions 0..n-1 into n/s tracks, each of size s, with
    // every track sorted ascending. Uses the restricted-growth rule (a position joins an existing
    // not-full track or starts the next track) so each unordered partition is produced exactly once.
    private static IEnumerable<List<List<int>>> EnumerateEqualTrackPartitions(int n, int s)
    {
        int k = n / s;
        var tracks = new List<List<int>>();
        return Recurse(0);

        IEnumerable<List<List<int>>> Recurse(int pos)
        {
            if (pos == n)
            {
                if (tracks.Count == k)
                    yield return tracks.Select(track => new List<int>(track)).ToList();
                yield break;
            }

            for (int i = 0; i < tracks.Count; i++)
            {
                if (tracks[i].Count >= s)
                    continue;

                tracks[i].Add(pos);
                foreach (List<List<int>> result in Recurse(pos + 1))
                    yield return result;
                tracks[i].RemoveAt(tracks[i].Count - 1);
            }

            if (tracks.Count < k)
            {
                tracks.Add(new List<int> { pos });
                foreach (List<List<int>> result in Recurse(pos + 1))
                    yield return result;
                tracks.RemoveAt(tracks.Count - 1);
            }
        }
    }

    private static IEnumerable<List<int>> EnumerateIndexPermutations(int count)
    {
        var indices = Enumerable.Range(0, count).ToList();
        return Permute(indices, 0);

        static IEnumerable<List<int>> Permute(List<int> items, int start)
        {
            if (start >= items.Count - 1)
            {
                yield return new List<int>(items);
                yield break;
            }

            for (int i = start; i < items.Count; i++)
            {
                (items[start], items[i]) = (items[i], items[start]);
                foreach (List<int> permutation in Permute(items, start + 1))
                    yield return permutation;
                (items[start], items[i]) = (items[i], items[start]);
            }
        }
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

    // Enumerates set partitions of items, pruning any branch whose running product of completed
    // block factorials already exceeds targetFactorialProduct. A fully-permuting block of size s
    // contributes s! orderings and blocks only grow as later items are assigned, so the running
    // product is a lower bound on every descendant partition's product. Callers that need
    // partitions whose block-factorial product equals a target order count can therefore never
    // miss a match, while the explosive majority of partitions (Bell(n) grows super-exponentially)
    // are skipped before they are even built. Exposed to the test assembly so the pruning can be
    // locked by a deterministic count-based test (EquivalentOrderPartitionPruningTests).
    internal static IEnumerable<List<List<int>>> EnumeratePartitions(IReadOnlyList<int> items, int targetFactorialProduct)
    {
        var blocks = new List<List<int>>();
        foreach (var partition in EnumeratePartitions(items, 0, blocks, BigInteger.One, targetFactorialProduct))
            yield return partition;
    }

    private static IEnumerable<List<List<int>>> EnumeratePartitions(
        IReadOnlyList<int> items,
        int index,
        List<List<int>> blocks,
        BigInteger currentFactorialProduct,
        int targetFactorialProduct)
    {
        if (index == items.Count)
        {
            yield return blocks.Select(block => block.ToList()).ToList();
            yield break;
        }

        int item = items[index];
        for (int blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            int currentSize = blocks[blockIndex].Count;
            // Growing a block from size c to c+1 multiplies its factorial contribution by (c+1).
            BigInteger nextProduct = currentFactorialProduct * (currentSize + 1);
            if (nextProduct <= targetFactorialProduct)
            {
                blocks[blockIndex].Add(item);
                foreach (var partition in EnumeratePartitions(items, index + 1, blocks, nextProduct, targetFactorialProduct))
                    yield return partition;
                blocks[blockIndex].RemoveAt(blocks[blockIndex].Count - 1);
            }
        }

        // A fresh singleton block contributes 1! = 1, leaving the running product unchanged.
        blocks.Add(new List<int> { item });
        foreach (var partition in EnumeratePartitions(items, index + 1, blocks, currentFactorialProduct, targetFactorialProduct))
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

}
