using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

static class StrategyTextRenderer
{
    private const int BannerBorderWidth = 20;

    private const string InLabel = "+";
    private const string OutLabel = "-";
    private const string FixedLabel = "fixed";
    private const string PossibleLabel = "possible";

    public static string Render(StrategyPlan plan)
    {
        using var writer = new StringWriter();
        Render(plan, writer);
        return writer.ToString();
    }

    public static void Render(StrategyPlan plan, TextWriter writer)
    {
        var stats = plan.SearchStatistics;
        var diag = stats.Diagnostics;

        // Summary: the problem and its answer first, so the key number is not buried.
        writer.WriteLine(Banner("summary"));
        writer.WriteLine($"n={plan.N}, m={plan.M}, k={plan.K}");
        if (plan.RequestedK != plan.K)
            writer.WriteLine($"requested k = {plan.RequestedK}; effective k = {plan.K} (dual reduction: k' = n-k)");
        writer.WriteLine($"worst-case steps = {plan.MaxStep}");
        writer.WriteLine($"total edges = {plan.TotalBranchEdges}");
        writer.WriteLine($"elapsed = {plan.Elapsed.TotalMilliseconds:F1} ms");
        writer.WriteLine($"phases: step = {stats.Phase1Milliseconds} ms, edge = {stats.Phase1bMilliseconds} ms, build = {stats.Phase2Milliseconds} ms");
        writer.WriteLine();

        // Diagnostics: internal search-engine telemetry, grouped so it does not crowd the summary.
        writer.WriteLine(Banner("diagnostics"));
        writer.WriteLine($"searched states = {stats.SearchedStates}");
        writer.WriteLine($"pending states = {stats.PendingStates} (peak {stats.PeakPendingStates})");
        writer.WriteLine($"output states = {stats.OutputStates} (expanded {stats.ExpandedOutputStates})");
        writer.WriteLine($"lower-bound states = {stats.LowerBoundStates}, feasible-top-set states = {stats.FeasibleTopSetStates}");
        writer.WriteLine($"outcomes constructed = {stats.OutcomesConstructed} (duplicate skips {diag.DuplicateOutcomeSkips}, merged collisions {diag.MergedOutcomeCollisions})");
        writer.WriteLine($"candidate groups enumerated = {stats.CandidateGroupsEnumerated} (symmetry-class representatives canonicalized before cross-class dedup)");
        writer.WriteLine($"lower-bound prunes = {diag.LowerBoundPrunes}");
        writer.WriteLine($"cache hits = exact {diag.ExactCacheHits}, lower-bound {diag.LowerBoundCacheHits}, feasible-top-set {diag.FeasibleTopSetCacheHits}, best-group-pattern {diag.BestGroupPatternCacheHits}");
        if (stats.CompactStatesSolved > 0)
            writer.WriteLine($"compact pass = {stats.CompactStatesSolved} states solved, {stats.CompactGroupsEnumerated} groups enumerated ({stats.CompactStepOptimalGroups} step-optimal)");
        writer.WriteLine();

        writer.WriteLine(Banner("legend"));
        WriteLegend(writer);
        writer.WriteLine();

        writer.WriteLine(Banner("strategy"));
        var depthIndex = StrategyDepthIndex.Build(plan.Root);
        RenderNode(plan.Root, plan.K, writer, 0, depthIndex);
    }

    private static string Banner(string label) => $"{new string('=', BannerBorderWidth)} {label} {new string('=', BannerBorderWidth)}";

    private static void WriteLegend(TextWriter writer)
    {
        (string Token, string Description)[] entries =
        {
            ("#i", "item i (1-based labels; may be relabeled in references)"),
            ("#i ~ #j", "items #i through #j inclusive (a run of 4+ consecutive items)"),
            ("S{id} [step x/y] sort(...)", "decision state: do this sort at step x of at most y"),
            ("a > b > c", "the sort revealed a ranks above b above c"),
            ("a > b > c  (×N = ...)", "this branch stands for N symmetric orderings (e.g. ×6 = 3!)"),
            ("pattern: ...", "shape of those orderings; \"{...}\" = any order, \"A = {...}\" names a split block (members A1, A2 ...)"),
            ("S{id}: top k = (...)", "solved: the top-k set is fully determined"),
            ("→S{id} (+N steps) [map: ...]", "reuse state S{id}'s subtree (N more sorts); [map] relabels referenced→current"),
        };

        int width = entries.Max(e => e.Token.Length);
        foreach (var (token, description) in entries)
            writer.WriteLine($"{token.PadRight(width)}  {description}");

        writer.WriteLine();
        writer.WriteLine("+ ..., - ..., fixed ..., possible ...   per-outcome effect rows (empty rows are omitted):");
        writer.WriteLine("     +         newly guaranteed into the top-k");
        writer.WriteLine("     -         newly excluded from the top-k");
        writer.WriteLine("     fixed     already locked into the top-k");
        writer.WriteLine("     possible  still competing for the remaining slots");
    }

    public static string FormatReference(StrategyNode node, StrategyDepthIndex depthIndex)
    {
        string suffix = depthIndex.TryGetReferenceRemaining(node.StateId, out int remaining)
            ? $" {FormatRemainingSteps(remaining)}"
            : string.Empty;
        return $"→S{node.StateId}{suffix}{FormatRelabeling(node.ReferenceRelabeling)}";
    }

    public static string FormatRemainingSteps(int remaining)
        => $"(+{remaining} {(remaining == 1 ? "step" : "steps")})";

    public static string FormatRelabeling(IReadOnlyList<ItemRelabel> relabeling)
    {
        if (relabeling.Count == 0)
            return string.Empty;

        string pairs = string.Join(", ", relabeling.Select(r => $"#{r.ReferencedItem + 1}→#{r.CurrentItem + 1}"));
        return $" [map: {pairs}]";
    }

    private static void RenderNode(StrategyNode node, int k, TextWriter writer, int indent, StrategyDepthIndex depthIndex)
    {
        string prefix = new string(' ', indent * 2);

        switch (node.Kind)
        {
            case StrategyNodeKind.Terminal:
                writer.WriteLine($"{prefix}S{node.StateId}: top {k} = ({FormatSet(node.TopSet)})");
                return;
            case StrategyNodeKind.Reference:
                writer.WriteLine($"{prefix}{FormatReference(node, depthIndex)}");
                return;
            case StrategyNodeKind.Decision:
                int maxStep = depthIndex.SubtreeMaxStep(node);
                writer.WriteLine($"{prefix}S{node.StateId} [step {node.Step}/{maxStep}] sort({FormatSet(node.Group)})");
                if (node.FinalChoice is not null)
                {
                    writer.WriteLine($"{prefix}  {FormatCompressedFinalChoice(node.FinalChoice, k)}");
                    return;
                }

                foreach (var branch in node.Branches)
                {
                    writer.WriteLine($"{prefix}  {FormatBranchHeader(branch)}");
                    WriteEquivalentPatternOnly(branch, writer, indent + 2);
                    WriteEffectEntries(branch.Effect, writer, indent + 2);
                    RenderNode(branch.Next, k, writer, indent + 2, depthIndex);
                }

                return;
            default:
                throw new InvalidOperationException("Unknown node kind");
        }
    }

    public static string FormatSet(IEnumerable<int> items)
    {
        return ItemSetFormatter.FormatSet(items);
    }

    public static string FormatOptionalSet(IEnumerable<int> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? "()" : $"({FormatSet(list)})";
    }

    public static string FormatEffect(StrategyEffect effect)
    {
        var parts = GetNonEmptyEffectEntries(effect);
        return $"[{string.Join(", ", parts)}]";
    }

    public static string FormatBranchLead(StrategyBranch branch)
        => $"{branch.OrderText}: {FormatEffect(branch.Effect)}";

    public static string FormatBranchHeader(StrategyBranch branch)
    {
        string header = branch.OrderText;
        if (branch.EquivalentOrders is not null)
            header += $"  (×{branch.EquivalentOrders.Count} = {branch.EquivalentOrders.CountFormula})";
        return header;
    }

    public static string FormatInEntry(IEnumerable<int> items) => $"{InLabel} {FormatOptionalSet(items)}";

    public static string FormatOutEntry(IEnumerable<int> items) => $"{OutLabel} {FormatOptionalSet(items)}";

    public static string FormatFixedEntry(IEnumerable<int> items) => $"{FixedLabel} {FormatOptionalSet(items)}";

    public static string FormatPossibleEntry(IEnumerable<int> items) => $"{PossibleLabel} {FormatOptionalSet(items)}";

    public static string FormatEffectDetails(StrategyEffect effect)
    {
        return string.Join("\n", GetNonEmptyEffectEntries(effect));
    }

    public static string FormatEquivalentFormsSummary(EquivalentOrderSummary summary)
        => $"equivalent forms: {summary.Count} = {summary.CountFormula}";

    public static string FormatEquivalentPatternLine(EquivalentOrderSummary summary)
        => string.IsNullOrEmpty(summary.Legend)
            ? $"pattern: {summary.PatternText}"
            : $"pattern: {summary.PatternText} ; {summary.Legend}";

    public static string FormatEquivalentDetails(EquivalentOrderSummary summary)
        => $"{FormatEquivalentFormsSummary(summary)}\n{FormatEquivalentPatternLine(summary)}";

    private static void WriteEquivalentOrders(StrategyBranch branch, TextWriter writer, int indent)
    {
        if (branch.EquivalentOrders is null)
            return;

        string prefix = new string(' ', indent * 2);
        writer.WriteLine($"{prefix}{FormatEquivalentFormsSummary(branch.EquivalentOrders)}");
        writer.WriteLine($"{prefix}{FormatEquivalentPatternLine(branch.EquivalentOrders)}");
    }

    private static void WriteEquivalentPatternOnly(StrategyBranch branch, TextWriter writer, int indent)
    {
        if (branch.EquivalentOrders is null)
            return;

        string prefix = new string(' ', indent * 2);
        writer.WriteLine($"{prefix}{FormatEquivalentPatternLine(branch.EquivalentOrders)}");
    }

    private static void WriteEffectEntries(StrategyEffect effect, TextWriter writer, int indent)
    {
        string prefix = new string(' ', indent * 2);
        if (effect.NewlyGuaranteedTop.Count > 0)
            writer.WriteLine($"{prefix}{FormatInEntry(effect.NewlyGuaranteedTop)}");
        if (effect.NewlyExcluded.Count > 0)
            writer.WriteLine($"{prefix}{FormatOutEntry(effect.NewlyExcluded)}");
        if (effect.FixedCandidates.Count > 0)
            writer.WriteLine($"{prefix}{FormatFixedEntry(effect.FixedCandidates)}");
        if (effect.PossibleCandidates.Count > 0)
            writer.WriteLine($"{prefix}{FormatPossibleEntry(effect.PossibleCandidates)}");
    }

    private static string FormatCompressedFinalChoice(FinalChoiceSummary summary, int k)
    {
        return $"fixed ({FormatSet(summary.FixedTopSet)}); choose {summary.RemainingSlots} of ({FormatSet(summary.CandidatePool)}) into top {k}";
    }

    private static List<string> GetNonEmptyEffectEntries(StrategyEffect effect)
    {
        var parts = new List<string>(4);
        if (effect.NewlyGuaranteedTop.Count > 0)
            parts.Add(FormatInEntry(effect.NewlyGuaranteedTop));
        if (effect.NewlyExcluded.Count > 0)
            parts.Add(FormatOutEntry(effect.NewlyExcluded));
        if (effect.FixedCandidates.Count > 0)
            parts.Add(FormatFixedEntry(effect.FixedCandidates));
        if (effect.PossibleCandidates.Count > 0)
            parts.Add(FormatPossibleEntry(effect.PossibleCandidates));
        return parts;
    }
}
