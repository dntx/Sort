using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace TopKFinder;

partial class StrategyBuilder
{
    // Renders a genuine parent-automorphism orbit (e.g. two interchangeable sorted chains) as one
    // representative ordering plus a relabeling that maps it onto the other member(s). The pattern
    // engine cannot express such a cross-relabeling as a disjunction-free template, so without this
    // it would emit a misleading "(... | ...)" disjunction; here the equivalence is honest because
    // PartitionFamiliesIntoOrbits only unions families connected by a real active-poset automorphism.
    //
    // Under EnableProjectionOrbitMerging the line may also carry a principle-D projection orbit:
    // single orderings that mirror one another only after the items both eliminate this step are
    // removed. For those the relabeling is computed on the projected poset and the doomed "drop" set
    // is appended to the legend as "drop {...}" so the displayed symmetry stays honest.
    private EquivalentOrderSummary BuildRelabelingOrbitSummary(
        ComparisonState state,
        List<MergedFamilyOutcome> line,
        MergedFamilyOutcome representative)
    {
        ulong commonDrop = EnableProjectionOrbitMerging ? CommonEliminatedMask(state, line) : 0;
        IReadOnlyList<int> repOrder = representative.Family.RepresentativeOrderItems;
        IReadOnlyList<int> repProjected = ProjectionKernel.RestrictOrderByDropMask(repOrder, commonDrop);

        ComparisonState? projected = null;
        var legends = new List<string>();
        bool usedProjection = false;

        foreach (MergedFamilyOutcome member in line)
        {
            if (ReferenceEquals(member, representative))
                continue;

            IReadOnlyList<int> memberOrder = member.Family.RepresentativeOrderItems;

            // Prefer the genuine full-poset automorphism, so a parent-automorphism orbit renders
            // exactly as before. Only when no such automorphism exists do we fall back to the
            // principle-D projection (removing the commonly-doomed items), which requires the legend
            // to disclose the drop set.
            if (state.TryFindOrderAutomorphism(0, repOrder, memberOrder, out Dictionary<int, int>? map)
                && map is not null)
            {
                AddRelabelLegend(legends, map);
                continue;
            }

            if (commonDrop == 0)
                continue;

            projected ??= ProjectionKernel.CloneDeactivated(state, commonDrop);
            IReadOnlyList<int> memberProjected = ProjectionKernel.RestrictOrderByDropMask(memberOrder, commonDrop);
            if (repProjected.Count == memberProjected.Count
                && projected.TryFindOrderAutomorphism(0, repProjected, memberProjected, out Dictionary<int, int>? projMap)
                && projMap is not null)
            {
                usedProjection = true;
                AddRelabelLegend(legends, projMap);
            }
        }

        // When the only thing distinguishing the folded orderings is the internal order of the
        // dropped items (every member maps onto the representative by the identity once the common
        // drop set is removed, so no relabel legend was produced), the representative's rigid tail
        // chain "... > #2 > #3 > #7" is misleading: it asserts a single total order among items that
        // the fold declares interchangeable, contradicting the "equivalent forms" count. Collapse
        // those trailing dropped items into an any-order brace "{...}" plus the residual known
        // orderings, matching the doomed-tail rendering.
        string patternText = representative.Family.RepresentativeOrder;
        if (usedProjection && legends.Count == 0)
        {
            string? bracePattern = TryBuildProjectionDropBracePattern(state, line, commonDrop);
            if (bracePattern is not null)
                patternText = bracePattern;
        }

        if (usedProjection && commonDrop != 0)
        {
            string dropNote = "drop {" + string.Join(", ",
                ComparisonState.MaskToOrderedList(commonDrop).Select(item => $"#{item + 1}")) + "}";
            legends.Add(dropNote);
        }

        string? combinedLegend = legends.Count > 0
            ? string.Join(" ; ", legends)
            : null;
        return new EquivalentOrderSummary(
            line.Count,
            patternText,
            line.Count.ToString(),
            combinedLegend);
    }

    // For a pure projection-drop fold (the folded orderings become identical once the commonly-
    // doomed items are removed), the surviving items keep a single fixed order while the dropped
    // items are interchangeable. Renders "<survivors> > {dropped} ; <residual>" when the fold is
    // faithfully described by that shape, and returns null otherwise so the caller falls back to
    // the rigid representative order. Three conditions must all hold:
    //   1. every folded member ranks all its dropped items strictly below all survivors (dropped
    //      items form a contiguous suffix) and shares the identical survivor prefix, so the fold is
    //      exactly { fixed prefix } x { arrangements of the dropped suffix };
    //   2. the dropped suffix has at least two items (otherwise there is nothing to fold);
    //   3. the number of linear extensions of the dropped items' induced sub-poset equals the fold
    //      count -- proving the brace + residual enumerates precisely the folded orderings and
    //      never claims more (or fewer) forms than the "equivalent forms" count states.
    private string? TryBuildProjectionDropBracePattern(
        ComparisonState state, List<MergedFamilyOutcome> line, ulong commonDrop)
    {
        if (commonDrop == 0)
            return null;

        List<int>? survivors = null;
        List<int>? dropped = null;
        foreach (MergedFamilyOutcome member in line)
        {
            if (!TrySplitSurvivorPrefix(member.Family.RepresentativeOrderItems, commonDrop,
                    out List<int> memberSurvivors, out List<int> memberDropped))
                return null; // a dropped item ranks above a survivor -- not a clean suffix

            if (survivors is null)
            {
                survivors = memberSurvivors;
                dropped = memberDropped;
            }
            else if (!survivors.SequenceEqual(memberSurvivors))
            {
                return null; // members disagree on the surviving prefix
            }
        }

        if (survivors is null || dropped is null || dropped.Count <= 1)
            return null; // nothing to collapse into an any-order brace

        // Honesty gate: the brace + residual represents exactly the linear extensions of the dropped
        // items' induced poset. Only emit it when that equals the number of orderings actually
        // folded, so the displayed shape never claims more (or fewer) forms than the count states.
        if (CountLinearExtensions(state, dropped) != line.Count)
            return null;

        var tokens = survivors.Select(item => $"#{item + 1}").ToList();
        tokens.Add(FormatBraceSet(dropped));
        string body = string.Join(" > ", tokens);

        string residual = BuildTailResidualConstraints(state, dropped);
        return residual.Length == 0 ? body : body + " ; " + residual;
    }

    // Splits an ordering into its surviving prefix and dropped suffix, requiring every dropped item
    // to rank strictly below every survivor. Returns false when a survivor appears after any dropped
    // item, i.e. the dropped items are not a contiguous suffix.
    private static bool TrySplitSurvivorPrefix(
        IReadOnlyList<int> order, ulong dropMask, out List<int> survivors, out List<int> dropped)
    {
        survivors = new List<int>();
        dropped = new List<int>();
        bool seenDropped = false;
        foreach (int item in order)
        {
            if ((dropMask & (1UL << item)) != 0)
            {
                seenDropped = true;
                dropped.Add(item);
            }
            else
            {
                if (seenDropped)
                    return false;
                survivors.Add(item);
            }
        }

        return true;
    }

    // Counts the linear extensions (topological orderings) of the sub-poset induced on the given
    // items by the active parent order. Memoized over the remaining-item bitmask; the item set is a
    // single sort group so it stays small.
    private static long CountLinearExtensions(ComparisonState state, IReadOnlyList<int> items)
    {
        ulong mask = 0;
        foreach (int item in items)
            mask |= 1UL << item;
        return CountLinearExtensions(state, mask, new Dictionary<ulong, long>());
    }

    private static long CountLinearExtensions(ComparisonState state, ulong remaining, Dictionary<ulong, long> memo)
    {
        if (remaining == 0)
            return 1;
        if (memo.TryGetValue(remaining, out long cached))
            return cached;

        long total = 0;
        ulong candidates = remaining;
        while (candidates != 0)
        {
            int next = BitOperations.TrailingZeroCount(candidates);
            candidates &= candidates - 1;
            // An item may lead only if none of its still-remaining ancestors precede it.
            if ((state.GetAncestorMask(next) & remaining) != 0)
                continue;
            total += CountLinearExtensions(state, remaining & ~(1UL << next), memo);
        }

        memo[remaining] = total;
        return total;
    }

    private static void AddRelabelLegend(List<string> legends, Dictionary<int, int> map)
    {
        string legend = FormatRelabelingMap(map);
        if (!string.IsNullOrEmpty(legend) && !legends.Contains(legend))
            legends.Add(legend);
    }

    private static ulong CommonEliminatedMask(ComparisonState state, List<MergedFamilyOutcome> line)
    {
        ulong common = ~0UL;
        foreach (MergedFamilyOutcome member in line)
            common &= EliminatedMask(state, member);
        return common;
    }

    // Collapses an item->item automorphism into a compact relabeling note. An involution (the common
    // chain-swap case) renders as range pairs "#a~#b <-> #c~#d" listing each unordered swap once;
    // any other map renders as directional "#a->#b" entries.
    private static string FormatRelabelingMap(Dictionary<int, int> map)
    {
        bool isInvolution = map.All(kv => map.TryGetValue(kv.Value, out int back) && back == kv.Key);
        if (isInvolution)
        {
            var pairs = map
                .Where(kv => kv.Key < kv.Value)
                .Select(kv => (Low: kv.Key, High: kv.Value))
                .OrderBy(pair => pair.Low)
                .ToList();

            var runs = new List<(int LowStart, int LowEnd, int HighStart, int HighEnd)>();
            foreach ((int low, int high) in pairs)
            {
                if (runs.Count > 0)
                {
                    var last = runs[^1];
                    if (low == last.LowEnd + 1 && high == last.HighEnd + 1)
                    {
                        runs[^1] = (last.LowStart, low, last.HighStart, high);
                        continue;
                    }
                }
                runs.Add((low, low, high, high));
            }

            return string.Join(", ", runs.Select(run =>
                $"({FormatItemRange(run.LowStart, run.LowEnd)}) \u2194 ({FormatItemRange(run.HighStart, run.HighEnd)})"));
        }

        var moved = map
            .Where(kv => kv.Key != kv.Value)
            .OrderBy(kv => kv.Key)
            .Select(kv => $"#{kv.Key + 1}\u2192#{kv.Value + 1}");
        return string.Join(", ", moved);
    }

    private static string FormatItemRange(int startItem, int endItem)
        => startItem == endItem ? $"#{startItem + 1}" : $"#{startItem + 1} ~ #{endItem + 1}";

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

        int totalCount = 0;
        foreach (OrderFamilyDescriptor family in orderFamilies)
            totalCount = SaturatingAdd(totalCount, family.Count);
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

            var (singletonPattern, singletonLegend) = SplitPlaceholderLegend(summary.PatternText);
            (singletonPattern, singletonLegend) = NormalizeEquivalentPattern(singletonPattern, singletonLegend);
            return new EquivalentOrderSummary(totalCount, singletonPattern, summary.TotalCountFormula, singletonLegend);
        }

        string patternText = orderFamilies.Count == 1
            ? orderFamilies[0].PatternText
            : "(" + string.Join(" | ", orderFamilies.Select(family => family.PatternText)) + ")";
        string countFormula = CombineFormulaParts(orderFamilies.Select(family => family.CountFormula).ToList());
        var (displayPattern, displayLegend) = SplitPlaceholderLegend(patternText);
        (displayPattern, displayLegend) = NormalizeEquivalentPattern(displayPattern, displayLegend);
        return new EquivalentOrderSummary(totalCount, displayPattern, countFormula, displayLegend);
    }

    // The pattern engine composes placeholder sub-blocks using an internal, parseable form,
    // "<alias>=permute{...}, ...; <body>" (definitions first so nesting can re-alias them). For
    // display we move that legend to the end of the body and switch to the doomed-tail notation,
    // "<body> ; A \u2208 permute {...}", so every placeholder pattern across the tree reads the same
    // way. Aliases are renumbered sequentially from A in order of first appearance. Patterns with
    // no definitions (a single "permute {...}" block) or a " | " disjunction are left untouched.
    private static (string PatternText, string? Legend) SplitPlaceholderLegend(string pattern)
    {
        if (!pattern.Contains(';') || pattern.Contains(" | ", StringComparison.Ordinal))
            return (pattern, null);

        ParsedPatternSegment parsed = ParsePatternSegment(pattern);
        if (parsed.Definitions.Count == 0)
            return (pattern, null);

        var aliasMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var legendParts = new List<string>(parsed.Definitions.Count);
        for (int i = 0; i < parsed.Definitions.Count; i++)
        {
            PatternAliasDefinition definition = parsed.Definitions[i];
            string newAlias = GetAliasName(i);
            aliasMap[definition.Alias] = newAlias;
            string expression = definition.Expression.Replace("permute{", "permute {", StringComparison.Ordinal);
            legendParts.Add($"{newAlias} \u2208 {expression}");
        }

        // Single-pass rewrite so chained renamings (e.g. B->A, C->B) never cascade onto each other.
        string body = Regex.Replace(
            parsed.Body,
            @"\b[A-Z]+(?=\d)",
            match => aliasMap.TryGetValue(match.Value, out string? mapped) ? mapped : match.Value);

        return (body, string.Join(", ", legendParts));
    }

    private sealed class PlaceholderClass
    {
        public string Alias = string.Empty;
        public int[] Items = Array.Empty<int>();
        public bool Inlined;
    }

    // Rewrites a placeholder pattern into the unified "inline-set" notation: "{...}" always means
    // "these items in any order". A class whose placeholders occupy a contiguous, ordered run
    // (A1 > A2 > ... > An) is folded inline into "{items}"; only a class whose members land in
    // non-adjacent slots keeps placeholders, defined by "A = {items}" in the legend. The word
    // "permute" is dropped everywhere (a leftover "permute {...}" block becomes a bare "{...}").
    // Surviving classes are renumbered A, B, ... by first appearance, and a pattern with no
    // surviving placeholders carries no legend. " | " disjunctions are preserved untouched.
    private static (string PatternText, string? Legend) NormalizeEquivalentPattern(string patternText, string? legend)
    {
        string[] segments = patternText.Split(" ; ", StringSplitOptions.None);
        string body = segments[0];
        string trailing = segments.Length > 1
            ? " ; " + string.Join(" ; ", segments.Skip(1))
            : string.Empty;

        List<PlaceholderClass> classes = ParseLegendClasses(legend);
        List<string> tokens = body.Length == 0
            ? new List<string>()
            : body.Split(" > ").ToList();

        foreach (PlaceholderClass cls in classes)
        {
            if (cls.Items.Length <= 1)
                continue;

            int start = FindContiguousPlaceholderRun(tokens, cls.Alias, cls.Items.Length);
            if (start >= 0)
            {
                tokens.RemoveRange(start, cls.Items.Length);
                tokens.Insert(start, FormatBraceSet(cls.Items));
                cls.Inlined = true;
                continue;
            }

            // A whole class sitting alone inside one any-order brace ("{A1, A2, A3}") is already a
            // contiguous block, so substitute its items directly ("{#7, #13, #19}").
            int braceIndex = tokens.FindIndex(token => IsWholeClassBrace(token, cls.Alias, cls.Items.Length));
            if (braceIndex >= 0)
            {
                tokens[braceIndex] = FormatBraceSet(cls.Items);
                cls.Inlined = true;
            }
        }

        body = string.Join(" > ", tokens)
            .Replace("permute {", "{", StringComparison.Ordinal)
            .Replace("permute{", "{", StringComparison.Ordinal);

        List<PlaceholderClass> survivors = classes.Where(cls => !cls.Inlined).ToList();
        if (survivors.Count == 0)
            return (body + trailing, null);

        var aliasMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(body, @"\b[A-Z]+(?=\d)"))
        {
            string alias = match.Value;
            if (!aliasMap.ContainsKey(alias) && survivors.Any(cls => cls.Alias == alias))
                aliasMap[alias] = GetAliasName(aliasMap.Count);
        }

        body = Regex.Replace(
            body,
            @"\b[A-Z]+(?=\d)",
            match => aliasMap.TryGetValue(match.Value, out string? mapped) ? mapped : match.Value);

        IEnumerable<string> legendParts = survivors
            .Where(cls => aliasMap.ContainsKey(cls.Alias))
            .OrderBy(cls => aliasMap[cls.Alias], StringComparer.Ordinal)
            .Select(cls => $"{aliasMap[cls.Alias]} = {FormatBraceSet(cls.Items)}");

        return (body + trailing, string.Join(", ", legendParts));
    }

    private static List<PlaceholderClass> ParseLegendClasses(string? legend)
    {
        var result = new List<PlaceholderClass>();
        if (string.IsNullOrEmpty(legend))
            return result;

        foreach (Match match in Regex.Matches(legend, @"([A-Z]+)\s*\u2208\s*permute\s*\{([^}]*)\}"))
            result.Add(new PlaceholderClass { Alias = match.Groups[1].Value, Items = ParseBraceItems(match.Groups[2].Value) });

        return result;
    }

    private static int[] ParseBraceItems(string inner)
    {
        var items = new List<int>();
        foreach (Match match in Regex.Matches(inner, @"#(\d+)(?:\s*~\s*#(\d+))?"))
        {
            int low = int.Parse(match.Groups[1].Value) - 1;
            if (match.Groups[2].Success)
            {
                int high = int.Parse(match.Groups[2].Value) - 1;
                for (int value = low; value <= high; value++)
                    items.Add(value);
            }
            else
            {
                items.Add(low);
            }
        }

        return items.ToArray();
    }

    private static int FindContiguousPlaceholderRun(List<string> tokens, string alias, int length)
    {
        for (int start = 0; start + length <= tokens.Count; start++)
        {
            bool matched = true;
            for (int offset = 0; offset < length; offset++)
            {
                if (tokens[start + offset] != alias + (offset + 1))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
                return start;
        }

        return -1;
    }

    // True when "token" is an any-order brace whose only contents are exactly the "length"
    // placeholder members of "alias" (e.g. "{A1, A2, A3}" for a 3-member class A).
    private static bool IsWholeClassBrace(string token, string alias, int length)
    {
        if (token.Length <= 1 || token[0] != '{' || token[^1] != '}')
            return false;

        string[] members = token.Substring(1, token.Length - 2).Split(", ");
        if (members.Length != length)
            return false;

        foreach (string member in members)
        {
            Match match = Regex.Match(member, @"^([A-Z]+)\d+$");
            if (!match.Success || match.Groups[1].Value != alias)
                return false;
        }

        return true;
    }

    private static string FormatBraceSet(IEnumerable<int> items)
    {
        return "{" + FormatItemSet(items) + "}";
    }

}
