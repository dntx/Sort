using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

partial class StrategyBuilder
{
    // Builds the "doomed-tail edge" view of a comparison step, in which every ordering that
    // differs only by permuting an already-eliminated tail collapses into a single edge whose
    // pattern carries that tail as an unordered brace set "{...}". This replaces the per-family
    // listing (which spells out each tail permutation as its own misleading branch) wherever the
    // tail is genuinely doomed.
    //
    // Returns null when the view does not apply, so the caller keeps the existing per-next-state
    // grouping:
    //   * some family has no doomed tail (its full order is meaningful), or
    //   * the tail is shorter than two items (nothing to fold), or
    //   * bucketing by the doomed prefix would not merge any families (e.g. a pure relabeling
    //     orbit that the existing "permute {...}" path renders better).
    private List<BranchSpec>? TryBuildDoomedTailSpecs(
        ComparisonState state,
        int remainingSlots,
        SelectedComparisonGroup chosenGroup)
    {
        IReadOnlyList<int> group = chosenGroup.Group;
        int n = group.Count;

        var familyOutcomes = new List<MergedFamilyOutcome>();
        foreach (MergedBranch merged in chosenGroup.Branches)
            familyOutcomes.AddRange(merged.FamilyOutcomes);

        if (familyOutcomes.Count == 0)
            return null;

        int[] outsideAncestors = ComputeOutsideAncestors(state, group);

        // Every family must expose a doomed tail of at least two items for the edge view to be
        // both honest (the tail order truly does not matter) and useful (there is something to
        // fold). A single counterexample falls the whole step back to the existing rendering.
        var prefixLengths = new int[familyOutcomes.Count];
        for (int i = 0; i < familyOutcomes.Count; i++)
        {
            int prefixLength = ComputeDoomedPrefixLength(
                familyOutcomes[i].Family.RepresentativeOrderItems, outsideAncestors, remainingSlots);
            if (n - prefixLength < 2)
                return null;
            prefixLengths[i] = prefixLength;
        }

        GroupSymmetryInfo symmetryInfo = BuildGroupSymmetryInfo(state, group);

        // Bucket families by their doomed-prefix class-id sequence. Two families share a bucket iff
        // their prefixes are identical up to symmetry-class relabeling and their tails are the
        // complement (always doomed), so all members of a bucket converge to one next state.
        var buckets = new Dictionary<string, DoomedTailBucket>();
        var bucketOrder = new List<string>();
        for (int i = 0; i < familyOutcomes.Count; i++)
        {
            MergedFamilyOutcome outcome = familyOutcomes[i];
            string key = BuildDoomedPrefixKey(
                outcome.Family.RepresentativeOrderItems, prefixLengths[i], symmetryInfo);
            if (!buckets.TryGetValue(key, out DoomedTailBucket? bucket))
            {
                bucket = new DoomedTailBucket(outcome, prefixLengths[i]);
                buckets[key] = bucket;
                bucketOrder.Add(key);
            }

            bucket.Add(outcome);
        }

        if (buckets.Count == familyOutcomes.Count)
            return null;

        var specs = new List<BranchSpec>(buckets.Count);
        foreach (string key in bucketOrder)
        {
            DoomedTailBucket bucket = buckets[key];
            EquivalentOrderSummary summary = BuildDoomedTailSummary(state, symmetryInfo, bucket);
            specs.Add(new BranchSpec(
                bucket.Representative.Family.RepresentativeOrder, bucket.Representative, summary));
        }

        return specs;
    }

    private int[] ComputeOutsideAncestors(ComparisonState state, IReadOnlyList<int> group)
    {
        ulong groupMask = 0;
        foreach (int item in group)
            groupMask |= 1UL << item;

        ulong outsideActiveMask = state.ActiveMask & ~groupMask;
        var outsideAncestors = new int[_n];
        foreach (int item in group)
            outsideAncestors[item] = BitOperations.PopCount(state.GetAncestorMask(item) & outsideActiveMask);

        return outsideAncestors;
    }

    // The doomed prefix is the shortest prefix after which every still-unplaced item is guaranteed
    // to be eliminated regardless of its final position (outsideAncestors + depth >= threshold),
    // matching the doomed-tail pruning in EnumerateSearchOrdersCore. The remaining items form the
    // doomed tail, whose ordering does not affect the next state.
    private static int ComputeDoomedPrefixLength(
        IReadOnlyList<int> order, int[] outsideAncestors, int eliminationThreshold)
    {
        for (int depth = 0; depth < order.Count; depth++)
        {
            bool doomed = true;
            for (int i = depth; i < order.Count; i++)
            {
                if (outsideAncestors[order[i]] + depth < eliminationThreshold)
                {
                    doomed = false;
                    break;
                }
            }

            if (doomed)
                return depth;
        }

        return order.Count;
    }

    private static string BuildDoomedPrefixKey(
        IReadOnlyList<int> orderItems, int prefixLength, GroupSymmetryInfo symmetryInfo)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < prefixLength; i++)
        {
            builder.Append(symmetryInfo.ItemToClassIndex[orderItems[i]]);
            builder.Append(',');
        }

        return builder.ToString();
    }

    private EquivalentOrderSummary BuildDoomedTailSummary(
        ComparisonState state, GroupSymmetryInfo symmetryInfo, DoomedTailBucket bucket)
    {
        IReadOnlyList<int> items = bucket.Representative.Family.RepresentativeOrderItems;
        int prefixLength = bucket.PrefixLength;
        int count = bucket.TotalCount;

        // Assign A, B, C... to the symmetry classes with more than one member, in class order.
        var classLetters = new Dictionary<int, string>();
        int letterIndex = 0;
        foreach (GroupSymmetryClass symmetryClass in symmetryInfo.Classes)
        {
            if (symmetryClass.Items.Length > 1)
                classLetters[symmetryClass.Index] = GetAliasName(letterIndex++);
        }

        // Subscripts run left-to-right across the prefix and then the (id-sorted) tail, so each
        // class member is named exactly once, e.g. A1 > ... > {A2, A3, ...}.
        var classSubscript = new Dictionary<int, int>();
        var prefixClassSlots = new Dictionary<int, int>();
        var prefixTokens = new List<string>(prefixLength);
        for (int i = 0; i < prefixLength; i++)
        {
            int item = items[i];
            int classIndex = symmetryInfo.ItemToClassIndex[item];
            if (classLetters.TryGetValue(classIndex, out string? letter))
            {
                prefixTokens.Add(letter + NextSubscript(classSubscript, classIndex));
                prefixClassSlots[classIndex] = prefixClassSlots.GetValueOrDefault(classIndex) + 1;
            }
            else
            {
                prefixTokens.Add($"#{item + 1}");
            }
        }

        var tailItems = new List<int>();
        for (int i = prefixLength; i < items.Count; i++)
            tailItems.Add(items[i]);
        tailItems.Sort();

        var tailTokens = new List<string>(tailItems.Count);
        foreach (int item in tailItems)
        {
            int classIndex = symmetryInfo.ItemToClassIndex[item];
            tailTokens.Add(classLetters.TryGetValue(classIndex, out string? letter)
                ? letter + NextSubscript(classSubscript, classIndex)
                : $"#{item + 1}");
        }

        string braceSet = "{" + string.Join(", ", tailTokens) + "}";
        string body = prefixTokens.Count > 0
            ? string.Join(" > ", prefixTokens) + " > " + braceSet
            : braceSet;
        string residual = BuildTailResidualConstraints(state, tailItems);
        string patternText = residual.Length == 0 ? body : body + " ; " + residual;

        long symmetryFactor = 1;
        var symParts = new List<string>();
        foreach (GroupSymmetryClass symmetryClass in symmetryInfo.Classes)
        {
            if (!prefixClassSlots.TryGetValue(symmetryClass.Index, out int slotsUsed))
                continue;

            int classSize = symmetryClass.Items.Length;
            symmetryFactor *= PartialPermutations(classSize, slotsUsed);

            int symDenominator = classSize - slotsUsed;
            symParts.Add(symDenominator <= 1 ? $"{classSize}!" : $"{classSize}!/{symDenominator}!");
        }

        long tailFactor = count / symmetryFactor;
        string symFormula = symParts.Count == 0 ? "1" : string.Join(" x ", symParts);
        string tailFormula = BuildTailFactorFormula(state, tailItems, tailFactor);

        var formulaParts = new List<string>();
        if (symFormula != "1")
            formulaParts.Add($"{symFormula} sym");
        formulaParts.Add($"{tailFormula} tail");
        string countFormula = string.Join(" x ", formulaParts);

        var legendParts = new List<string>();
        foreach (GroupSymmetryClass symmetryClass in symmetryInfo.Classes)
        {
            if (symmetryClass.Items.Length > 1)
            {
                legendParts.Add(
                    $"{classLetters[symmetryClass.Index]} \u2208 permute {FormatBraceSet(symmetryClass.Items)}");
            }
        }

        string legend = string.Join(", ", legendParts);

        var (normalizedPattern, normalizedLegend) = NormalizeEquivalentPattern(patternText, legend);
        return new EquivalentOrderSummary(count, normalizedPattern, countFormula, normalizedLegend);
    }

    private static int NextSubscript(Dictionary<int, int> classSubscript, int classIndex)
    {
        int next = classSubscript.GetValueOrDefault(classIndex) + 1;
        classSubscript[classIndex] = next;
        return next;
    }

    // Lists the known orderings that still constrain the otherwise-unordered tail, e.g. "#1 > #2"
    // when #1 is a (transitively-reduced) ancestor of #2 and both fall in the doomed tail.
    private string BuildTailResidualConstraints(ComparisonState state, IReadOnlyList<int> tailItems)
    {
        ulong tailMask = 0;
        foreach (int item in tailItems)
            tailMask |= 1UL << item;

        var covers = new List<string>();
        foreach (int lower in tailItems)
        {
            ulong ancestorsInTail = state.GetAncestorMask(lower) & tailMask;
            ulong unfilteredAncestorsInTail = ancestorsInTail;
            while (unfilteredAncestorsInTail != 0)
            {
                int higher = BitOperations.TrailingZeroCount(unfilteredAncestorsInTail);
                unfilteredAncestorsInTail &= unfilteredAncestorsInTail - 1;

                // Keep only Hasse covers: drop higher>lower when some middle item in the tail sits
                // strictly between them, since that pair is implied transitively.
                ulong betweenMask = state.GetDescendantMask(higher) & state.GetAncestorMask(lower) & tailMask;
                if (betweenMask == 0)
                    covers.Add($"#{higher + 1} > #{lower + 1}");
            }
        }

        covers.Sort(StringComparer.Ordinal);
        return string.Join(", ", covers);
    }

    // Renders the tail multiplicity as a hook-length expression L! / D, where D is the product of
    // each tail element's down-set size (its descendants within the tail, plus itself). This is
    // exact whenever the residual order is a forest (every element has at most one immediate
    // ancestor inside the tail). When the order branches upward (an element sits below two
    // incomparable ancestors) the hook-length formula no longer applies, so we fall back to the
    // plain integer count to avoid printing a wrong closed form.
    //
    // Each connected component contributes its own factor: a pure chain of length c renders as the
    // factorial "c!" (its hook lengths c, c-1, ..., 1 multiply to c!), matching the symmetry
    // factor's factorial style; a branching tree renders as its integer hook-length product.
    private string BuildTailFactorFormula(
        ComparisonState state, IReadOnlyList<int> tailItems, long tailFactor)
    {
        int tailLength = tailItems.Count;
        ulong tailMask = 0;
        foreach (int item in tailItems)
            tailMask |= 1UL << item;

        var parent = new Dictionary<int, int>();
        foreach (int item in tailItems)
            parent[item] = item;

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        var childCount = new Dictionary<int, int>();
        var hook = new Dictionary<int, int>();
        foreach (int item in tailItems)
        {
            ulong ancestorsInTail = state.GetAncestorMask(item) & tailMask;
            int immediateAncestors = 0;
            ulong remaining = ancestorsInTail;
            while (remaining != 0)
            {
                int higher = BitOperations.TrailingZeroCount(remaining);
                remaining &= remaining - 1;

                ulong betweenMask = state.GetDescendantMask(higher) & state.GetAncestorMask(item) & tailMask;
                if (betweenMask != 0)
                    continue;

                immediateAncestors++;
                childCount[higher] = childCount.GetValueOrDefault(higher) + 1;
                parent[Find(item)] = Find(higher);
            }

            if (immediateAncestors > 1)
                return tailFactor.ToString();

            hook[item] = 1 + BitOperations.PopCount(state.GetDescendantMask(item) & tailMask);
        }

        var components = new Dictionary<int, List<int>>();
        foreach (int item in tailItems)
        {
            int root = Find(item);
            if (!components.TryGetValue(root, out List<int>? members))
                components[root] = members = new List<int>();
            members.Add(item);
        }

        var factors = new List<string>();
        foreach (List<int> members in components.Values)
        {
            if (members.Count <= 1)
                continue;

            bool isChain = members.All(member => childCount.GetValueOrDefault(member) <= 1);
            if (isChain)
            {
                factors.Add($"{members.Count}!");
            }
            else
            {
                // A branching component's hook-length product is an arbitrary integer (not a clean
                // factorial), so it is multiplied out directly. Guard the rare large-tail case
                // where that product would overflow by falling back to the plain integer count.
                try
                {
                    long product = 1;
                    foreach (int member in members)
                        product = checked(product * hook[member]);
                    factors.Add(product.ToString());
                }
                catch (OverflowException)
                {
                    return tailFactor.ToString();
                }
            }
        }

        if (factors.Count == 0)
            return $"{tailLength}!";

        factors.Sort(StringComparer.Ordinal);
        string denominator = factors.Count == 1 ? factors[0] : "(" + string.Join(" x ", factors) + ")";
        return $"{tailLength}!/{denominator}";
    }

    private static long PartialPermutations(int total, int taken)
    {
        long result = 1;
        for (int i = 0; i < taken; i++)
            result *= total - i;
        return result;
    }

    private sealed class DoomedTailBucket
    {
        public DoomedTailBucket(MergedFamilyOutcome representative, int prefixLength)
        {
            Representative = representative;
            PrefixLength = prefixLength;
        }

        public MergedFamilyOutcome Representative { get; }
        public int PrefixLength { get; }
        public int TotalCount { get; private set; }

        public void Add(MergedFamilyOutcome outcome) => TotalCount += outcome.Family.Count;
    }
}
