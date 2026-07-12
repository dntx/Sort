using System.Collections.Generic;
using System.Linq;
using System.Text;

// A high-level, human-readable summary of a strategy. The detailed tree (see StrategyTextRenderer)
// shows every state and outcome; this overview collapses the *representative* main line into a few
// "rounds" so a reader can grasp the comparison structure at a glance -- e.g. "split 25 into 5
// groups of 5, sort each, then sort the leaders" -- instead of clicking through a deep tree.
//
// It follows the representative outcome (the first branch) at each step. Where a step genuinely
// forks into several distinct outcomes, the row is flagged so the reader knows to open the tree for
// the full detail. Each row carries the StateId it corresponds to so a UI can link a row back to
// the matching tree node.
sealed class StrategyOverview
{
    public StrategyOverview(IReadOnlyList<OverviewRow> rows)
    {
        Rows = rows;
    }

    public IReadOnlyList<OverviewRow> Rows { get; }
}

sealed class OverviewRow
{
    public OverviewRow(string headline, IReadOnlyList<string> details, int? linkStateId)
    {
        Headline = headline;
        Details = details;
        LinkStateId = linkStateId;
    }

    // The primary one-line description of this round/step.
    public string Headline { get; }

    // Optional indented sub-lines (group list, elimination summary, fork note).
    public IReadOnlyList<string> Details { get; }

    // The decision/terminal/reference state this row maps to, so a UI can focus the matching tree
    // node when the row is clicked. Null only if the plan has no states at all.
    public int? LinkStateId { get; }
}

static class StrategyOverviewRenderer
{
    public static StrategyOverview Build(StrategyPlan plan)
    {
        // Walk the representative spine: follow the first branch of each decision until a finish
        // node (a compressed final choice, a solved terminal, or a reference reuse).
        var spineNodes = new List<StrategyNode>();
        var spineBranches = new List<StrategyBranch>();
        StrategyNode node = plan.Root;
        while (node.Kind == StrategyNodeKind.Decision && node.FinalChoice is null && node.Branches.Count > 0)
        {
            spineNodes.Add(node);
            spineBranches.Add(node.Branches[0]);
            node = node.Branches[0].Next;
        }
        StrategyNode tail = node;

        var rows = new List<OverviewRow>();
        int roundNo = 0;
        int i = 0;
        while (i < spineNodes.Count)
        {
            bool single = spineNodes[i].Branches.Count == 1;
            int size = spineNodes[i].Group.Count;

            if (single && size == 2 && TryBuildAnchorRunSummary(spineNodes, spineBranches, i, out int runEndExclusive, out OverviewRow anchorRow, roundNo + 1))
            {
                rows.Add(anchorRow);
                roundNo++;
                i = runEndExclusive;
                continue;
            }

            if (single)
            {
                // Accumulate a maximal run of single-branch, same-size sorts on disjoint items.
                // This catches both the first "split all items" wave and later regular waves
                // (for example winners-only rounds) in one concise overview row.
                int j = i;
                var groups = new List<IReadOnlyList<int>>();
                var drops = new List<int>();
                var roundSeen = new HashSet<int>();
                while (j < spineNodes.Count
                       && spineNodes[j].Group.Count == size
                       && spineNodes[j].Branches.Count == 1
                       && spineNodes[j].Group.All(x => !roundSeen.Contains(x)))
                {
                    groups.Add(spineNodes[j].Group);
                    drops.Add(spineBranches[j].Effect.NewlyExcluded.Count);
                    foreach (int x in spineNodes[j].Group)
                        roundSeen.Add(x);
                    j++;
                }

                roundNo++;
                int stepLo = i + 1;
                int stepHi = j;
                var details = new List<string>();
                string headline;
                if (groups.Count == 1)
                {
                    headline = $"Round {roundNo} \u00b7 step {stepLo}: sort {StrategyTextRenderer.FormatOptionalSet(groups[0])}";
                }
                else
                {
                    headline = $"Round {roundNo} \u00b7 steps {stepLo}\u2013{stepHi}: sort {groups.Count} disjoint groups of {size}";
                    details.Add(string.Join("   ", groups.Select(StrategyTextRenderer.FormatOptionalSet)));
                }

                details.Add(SummarizeEliminations(drops, spineBranches[j - 1].Effect.PossibleCandidates.Count));
                rows.Add(new OverviewRow(headline, details, spineNodes[i].StateId));
                i = j;
            }
            else
            {
                roundNo++;
                StrategyBranch rep = spineBranches[i];
                StrategyEffect eff = rep.Effect;
                int stepNo = i + 1;

                string headline = $"Round {roundNo} \u00b7 step {stepNo}: sort {StrategyTextRenderer.FormatOptionalSet(spineNodes[i].Group)}";
                var details = new List<string>();

                var notes = new List<string>();
                if (eff.NewlyGuaranteedTop.Count > 0)
                    notes.Add($"{StrategyTextRenderer.FormatOptionalSet(eff.NewlyGuaranteedTop)} guaranteed into top-{plan.K}");
                if (eff.NewlyExcluded.Count > 0)
                    notes.Add($"excludes {StrategyTextRenderer.FormatOptionalSet(eff.NewlyExcluded)}");
                if (eff.PossibleCandidates.Count > 0)
                    notes.Add($"{eff.PossibleCandidates.Count} still competing");
                if (notes.Count > 0)
                    details.Add("\u2192 " + string.Join("; ", notes));

                if (spineNodes[i].Branches.Count > 1)
                {
                    details.Add(
                        $"branches into {spineNodes[i].Branches.Count} outcomes here \u2014 the representative is shown; open the tree for the rest");
                }

                rows.Add(new OverviewRow(headline, details, spineNodes[i].StateId));
                i++;
            }
        }

        rows.Add(BuildFinishRow(tail, plan.K, spineNodes.Count));
        return new StrategyOverview(rows);
    }

    private static bool TryBuildAnchorRunSummary(
        IReadOnlyList<StrategyNode> spineNodes,
        IReadOnlyList<StrategyBranch> spineBranches,
        int start,
        out int endExclusive,
        out OverviewRow row,
        int roundNo)
    {
        endExclusive = start;
        row = null!;

        IReadOnlyList<int> group = spineNodes[start].Group;
        if (group.Count != 2)
            return false;

        int leftAnchor = group[0];
        int rightAnchor = group[1];

        int leftLen = ComputeAnchorRunLength(spineNodes, start, leftAnchor);
        int rightLen = ComputeAnchorRunLength(spineNodes, start, rightAnchor);

        int anchor = leftLen >= rightLen ? leftAnchor : rightAnchor;
        int runLen = leftLen >= rightLen ? leftLen : rightLen;
        if (runLen <= 2)
            return false;

        var challengers = new List<int>(runLen);
        var drops = new List<int>(runLen);
        for (int j = start; j < start + runLen; j++)
        {
            IReadOnlyList<int> pair = spineNodes[j].Group;
            challengers.Add(pair[0] == anchor ? pair[1] : pair[0]);
            drops.Add(spineBranches[j].Effect.NewlyExcluded.Count);
        }

        int stepLo = start + 1;
        int stepHi = start + runLen;
        string span = stepLo == stepHi ? $"step {stepLo}" : $"steps {stepLo}\u2013{stepHi}";

        var details = new List<string>
        {
            $"challengers: {StrategyTextRenderer.FormatOptionalSet(challengers)}",
            SummarizeEliminations(drops, spineBranches[start + runLen - 1].Effect.PossibleCandidates.Count),
        };

        row = new OverviewRow(
            $"Round {roundNo} \u00b7 {span}: compare {StrategyTextRenderer.FormatOptionalSet(new[] { anchor })} against {runLen} challengers",
            details,
            spineNodes[start].StateId);
        endExclusive = start + runLen;
        return true;
    }

    private static int ComputeAnchorRunLength(IReadOnlyList<StrategyNode> spineNodes, int start, int anchor)
    {
        var challengers = new HashSet<int>();
        int j = start;
        while (j < spineNodes.Count)
        {
            StrategyNode node = spineNodes[j];
            if (node.Branches.Count != 1 || node.Group.Count != 2)
                break;

            IReadOnlyList<int> pair = node.Group;
            if (pair[0] != anchor && pair[1] != anchor)
                break;

            int challenger = pair[0] == anchor ? pair[1] : pair[0];
            if (!challengers.Add(challenger))
                break;

            j++;
        }

        return j - start;
    }

    public static string RenderText(StrategyPlan plan)
    {
        StrategyOverview overview = Build(plan);
        var sb = new StringBuilder();
        sb.AppendLine("==================== overview ====================");
        sb.AppendLine($"n={plan.N}, m={plan.M}, k={plan.K}  \u00b7  worst case {plan.MaxStep} steps");
        if (plan.RequestedK != plan.K)
            sb.AppendLine($"requested k = {plan.RequestedK}; solved via dual reduction to k'={plan.K}");
        sb.AppendLine("representative main line (forks flagged); see the strategy section below for full detail");
        sb.AppendLine();
        foreach (OverviewRow row in overview.Rows)
        {
            sb.AppendLine(row.Headline);
            foreach (string detail in row.Details)
                sb.AppendLine("    " + detail);
        }

        return sb.ToString();
    }

    private static string SummarizeEliminations(IReadOnlyList<int> drops, int remaining)
    {
        if (drops.All(d => d == 0))
            return $"no items can be eliminated yet \u2192 {remaining} still in contention";

        bool uniform = drops.All(d => d == drops[0]);
        return uniform
            ? $"each sort drops its bottom {drops[0]} \u2192 {remaining} still in contention"
            : $"eliminates the losers \u2192 {remaining} still in contention";
    }

    private static OverviewRow BuildFinishRow(StrategyNode tail, int k, int spineLength)
    {
        switch (tail.Kind)
        {
            case StrategyNodeKind.Decision when tail.FinalChoice is not null:
                FinalChoiceSummary fc = tail.FinalChoice;
                int stepNo = tail.Step ?? spineLength + 1;
                string locked = fc.FixedTopSet.Count > 0
                    ? $"top-{k} so far {StrategyTextRenderer.FormatOptionalSet(fc.FixedTopSet)}; "
                    : string.Empty;
                string slots = fc.RemainingSlots == 1 ? "slot" : "slots";
                return new OverviewRow(
                    $"Finish \u00b7 step {stepNo}: {locked}choose {fc.RemainingSlots} of {StrategyTextRenderer.FormatOptionalSet(fc.CandidatePool)} for the last {slots}",
                    System.Array.Empty<string>(),
                    tail.StateId);

            case StrategyNodeKind.Terminal:
                return new OverviewRow(
                    $"Finish: top {k} = {StrategyTextRenderer.FormatOptionalSet(tail.TopSet)}",
                    System.Array.Empty<string>(),
                    tail.StateId);

            default:
                return new OverviewRow(
                    $"Finish: reuse state S{tail.StateId}",
                    System.Array.Empty<string>(),
                    tail.StateId);
        }
    }
}
