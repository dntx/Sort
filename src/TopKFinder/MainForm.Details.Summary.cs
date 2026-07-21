using System;
using System.Collections.Generic;

partial class MainForm
{
    private void UpdateSummaryText(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan, bool compactImproved)
    {
        string head = feasiblePlan.RequestedK == feasiblePlan.K
            ? $"n={feasiblePlan.N}, m={feasiblePlan.M}, k={feasiblePlan.K}"
            : $"n={feasiblePlan.N}, m={feasiblePlan.M}, k={feasiblePlan.RequestedK} (dual k'={feasiblePlan.K})";

        if (defaultPlan is null)
        {
            _statusLabel.Text =
                $"{head}, {FormatPlanSqueeze(feasiblePlan)} (not proven optimal). Computing step...";
            return;
        }

        if (compactPlan is null)
        {
            double seconds = feasiblePlan.Elapsed.TotalSeconds + defaultPlan.Elapsed.TotalSeconds;
            _statusLabel.Text =
                $"{head}, step max={defaultPlan.MaxStep}, elapsed={seconds:F3} s. Computing {StageNames.ExactEdgeCompactPattern} stage...";
            return;
        }

        double totalElapsedSeconds = feasiblePlan.Elapsed.TotalSeconds + defaultPlan.Elapsed.TotalSeconds + compactPlan.Elapsed.TotalSeconds;
        string compactText;
        if (compactPlan.MaxStep < defaultPlan.MaxStep)
            compactText = $"compact lowered max steps {defaultPlan.MaxStep} -> {compactPlan.MaxStep} (edges {defaultPlan.TotalBranchEdges} -> {compactPlan.TotalBranchEdges})";
        else if (compactImproved)
            compactText = $"compact reduced total edges {defaultPlan.TotalBranchEdges} -> {compactPlan.TotalBranchEdges}";
        else
            compactText = $"compact produced no better result (step total edges {defaultPlan.TotalBranchEdges}, compact {compactPlan.TotalBranchEdges})";
        _statusLabel.Text =
            $"{head}, total elapsed={totalElapsedSeconds:F3} s, " +
            $"max steps={compactPlan.MaxStep}, {compactText}.";
    }

    private static string BuildFeasibleOnlyDetails(StrategyPlan feasiblePlan)
    {
        string feasibleText = DisplayEngine.RenderStrategyText(feasiblePlan).TrimEnd();
        var lines = new List<string>
        {
            "Step strategy (greedy upper bound; next stage in progress)",
            $"squeeze: {FormatPlanSqueeze(feasiblePlan)}  (not proven optimal)",
            $"step elapsed: {feasiblePlan.Elapsed.TotalSeconds:F3} s",
            $"step total edges: {feasiblePlan.TotalBranchEdges}",
            $"step output states: {feasiblePlan.SearchStatistics.OutputStates}",
            $"max steps (upper bound): {feasiblePlan.MaxStep}",
            string.Empty,
            "----- step -----",
            feasibleText,
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildDefaultOnlyDetails(StrategyPlan defaultPlan)
    {
        string defaultText = DisplayEngine.RenderStrategyText(defaultPlan).TrimEnd();
        var lines = new List<string>
        {
            $"Step result ({StageNames.ExactEdgeCompactPattern} stage in progress)",
            $"step elapsed: {defaultPlan.Elapsed.TotalSeconds:F3} s",
            $"step total edges: {defaultPlan.TotalBranchEdges}",
            $"step output states: {defaultPlan.SearchStatistics.OutputStates}",
            $"max steps: {defaultPlan.MaxStep}",
            string.Empty,
            "----- step -----",
            defaultText,
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildTwoPhaseDetails(StrategyPlan defaultPlan, StrategyPlan compactPlan, bool compactImproved)
    {
        string defaultText = DisplayEngine.RenderStrategyText(defaultPlan).TrimEnd();
        string compactText = DisplayEngine.RenderStrategyText(compactPlan).TrimEnd();
        double totalElapsedSeconds = defaultPlan.Elapsed.TotalSeconds + compactPlan.Elapsed.TotalSeconds;
        var lines = new List<string>
        {
            "Two-stage result",
            $"total elapsed: {totalElapsedSeconds:F3} s",
            $"step total edges: {defaultPlan.TotalBranchEdges}",
            $"compact total edges: {compactPlan.TotalBranchEdges}",
            $"step output states: {defaultPlan.SearchStatistics.OutputStates}",
            $"compact output states: {compactPlan.SearchStatistics.OutputStates}",
            compactImproved
                ? "compact improvement: yes"
                : "compact improvement: no",
            string.Empty,
            "----- step -----",
            defaultText,
        };

        if (compactImproved)
        {
            lines.Add(string.Empty);
            lines.Add("----- compact -----");
            lines.Add(compactText);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildPlanDetails(StrategyPlan plan)
    {
        string rendered = DisplayEngine.RenderStrategyText(plan).TrimEnd();
        string diagnostics = BuildDiagnosticsDetails(plan.SearchStatistics.Diagnostics);
        return diagnostics.Length == 0 ? rendered : $"{rendered}\n\n{diagnostics}";
    }

    private static string BuildIdleDetailsText()
    {
        return string.Join(Environment.NewLine, new[]
        {
            "Top-K Strategy Explorer",
            string.Empty,
            "1. Adjust n, m, k and theme in the Inputs section.",
            "2. Use Run / Stop / Expand All / Collapse All from the Actions section.",
            "3. Watch the States / Work / Progress panels for live progress.",
            string.Empty,
            "Tree legend:",
            "- state: comparison node",
            "- branch: outcome branch",
            "- in / out / fixed / possible: branch effect categories",
            "- result: terminal top-k set",
            "- reference: previously expanded state",
        });
    }
}
