using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

static class StrategyTextRenderer
{
    private const string InLabel = "in";
    private const string OutLabel = "out";
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
        writer.WriteLine($"n={plan.N}, m={plan.M}, k={plan.K}");
        writer.WriteLine($"elapsed = {plan.Elapsed.TotalMilliseconds:F1} ms");
        writer.WriteLine($"max step = {plan.MaxStep}");
        writer.WriteLine($"searched states = {plan.SearchStatistics.SearchedStates}");
        writer.WriteLine($"pending states = {plan.SearchStatistics.PendingStates} (peak {plan.SearchStatistics.PeakPendingStates})");
        writer.WriteLine($"output states = {plan.SearchStatistics.OutputStates} (expanded {plan.SearchStatistics.ExpandedOutputStates})");
        writer.WriteLine();
        RenderNode(plan.Root, plan.K, writer, 0);
    }

    private static void RenderNode(StrategyNode node, int k, TextWriter writer, int indent)
    {
        string prefix = new string(' ', indent * 2);

        switch (node.Kind)
        {
            case StrategyNodeKind.Terminal:
                writer.WriteLine($"{prefix}S{node.StateId}: top {k} = ({FormatSet(node.TopSet)})");
                return;
            case StrategyNodeKind.Reference:
                writer.WriteLine($"{prefix}→S{node.StateId}");
                return;
            case StrategyNodeKind.Decision:
                writer.WriteLine($"{prefix}S{node.StateId} [step {node.Step}] sort({FormatSet(node.Group)})");
                if (node.FinalChoice is not null)
                {
                    writer.WriteLine($"{prefix}  {FormatCompressedFinalChoice(node.FinalChoice, k)}");
                    return;
                }

                foreach (var branch in node.Branches)
                {
                    string effect = FormatEffect(branch.Effect);
                    if (branch.Next.Kind == StrategyNodeKind.Decision)
                    {
                        writer.WriteLine($"{prefix}  {branch.OrderText}: {effect}");
                        WriteEquivalentOrders(branch, writer, indent + 2);
                        RenderNode(branch.Next, k, writer, indent + 2);
                    }
                    else if (branch.Next.Kind == StrategyNodeKind.Terminal)
                    {
                        writer.WriteLine($"{prefix}  {branch.OrderText}: {effect} S{branch.Next.StateId}: top {k} = ({FormatSet(branch.Next.TopSet)})");
                        WriteEquivalentOrders(branch, writer, indent + 2);
                    }
                    else
                    {
                        writer.WriteLine($"{prefix}  {branch.OrderText}: {effect} →S{branch.Next.StateId}");
                        WriteEquivalentOrders(branch, writer, indent + 2);
                    }
                }

                return;
            default:
                throw new InvalidOperationException("Unknown node kind");
        }
    }

    public static string FormatSet(IEnumerable<int> items)
    {
        return string.Join(", ", items.Select(i => $"#{i + 1}"));
    }

    public static string FormatOptionalSet(IEnumerable<int> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? "-" : $"({FormatSet(list)})";
    }

    public static string FormatEffect(StrategyEffect effect)
    {
        return $"[{FormatInEntry(effect.NewlyGuaranteedTop)}, {FormatOutEntry(effect.NewlyExcluded)}, {FormatFixedEntry(effect.FixedCandidates)}, {FormatPossibleEntry(effect.PossibleCandidates)}]";
    }

    public static string FormatInEntry(IEnumerable<int> items) => $"{InLabel} {FormatOptionalSet(items)}";

    public static string FormatOutEntry(IEnumerable<int> items) => $"{OutLabel} {FormatOptionalSet(items)}";

    public static string FormatFixedEntry(IEnumerable<int> items) => $"{FixedLabel} {FormatOptionalSet(items)}";

    public static string FormatPossibleEntry(IEnumerable<int> items) => $"{PossibleLabel} {FormatOptionalSet(items)}";

    public static string FormatEffectDetails(StrategyEffect effect)
    {
        return string.Join("\n", new[]
        {
            FormatInEntry(effect.NewlyGuaranteedTop),
            FormatOutEntry(effect.NewlyExcluded),
            FormatFixedEntry(effect.FixedCandidates),
            FormatPossibleEntry(effect.PossibleCandidates),
        });
    }

    public static string FormatEquivalentFormsSummary(EquivalentOrderSummary summary)
        => $"equivalent forms: {summary.Count} = {summary.CountFormula}";

    public static string FormatEquivalentPatternLine(EquivalentOrderSummary summary)
        => $"pattern: {summary.PatternText}";

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

    private static string FormatCompressedFinalChoice(FinalChoiceSummary summary, int k)
    {
        return $"fixed ({FormatSet(summary.FixedTopSet)}); choose {summary.RemainingSlots} of ({FormatSet(summary.CandidatePool)}) into top {k}";
    }

    public static string FormatEquivalentPattern(EquivalentOrderSummary summary) => summary.PatternText;
}
