using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

static class StrategyTextRenderer
{
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
                foreach (var branch in node.Branches)
                {
                    string effect = FormatEffect(branch.Effect);
                    if (branch.Next.Kind == StrategyNodeKind.Decision)
                    {
                        writer.WriteLine($"{prefix}  {branch.OrderText}: {effect}");
                        RenderNode(branch.Next, k, writer, indent + 2);
                    }
                    else if (branch.Next.Kind == StrategyNodeKind.Terminal)
                    {
                        writer.WriteLine($"{prefix}  {branch.OrderText}: {effect} S{branch.Next.StateId}: top {k} = ({FormatSet(branch.Next.TopSet)})");
                    }
                    else
                    {
                        writer.WriteLine($"{prefix}  {branch.OrderText}: {effect} →S{branch.Next.StateId}");
                    }
                }

                if (node.IsCompressedFinalComparison && node.OmittedBranchCount > 0)
                    writer.WriteLine($"{prefix}  ... {node.OmittedBranchCount} other final outcome(s) omitted; analogous.");

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
        return $"[in {FormatOptionalSet(effect.NewlyGuaranteedTop)}, out {FormatOptionalSet(effect.NewlyExcluded)}, cand fixed {FormatOptionalSet(effect.FixedCandidates)}, possible {FormatOptionalSet(effect.PossibleCandidates)}]";
    }
}
