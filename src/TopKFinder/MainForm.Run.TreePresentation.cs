using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

partial class MainForm
{
    private const string ComputingSuffix = ": computing...";

    private static string FormatComputingPlaceholderText(string stageName)
        => stageName + ComputingSuffix;

    private TreeNode CreateComputingPlaceholderNode(string stageName)
        => new(FormatComputingPlaceholderText(stageName)) { ForeColor = _palette.MutedForeColor };

    private static bool TryRemoveTrailingComputingPlaceholder(TreeNodeCollection nodes)
    {
        if (nodes.Count == 0 || !IsComputingPlaceholderText(nodes[nodes.Count - 1].Text))
            return false;

        nodes.RemoveAt(nodes.Count - 1);
        return true;
    }

    private static bool TryMarkTrailingComputingPlaceholderStopped(TreeNodeCollection nodes)
    {
        if (nodes.Count == 0)
            return false;

        TreeNode tail = nodes[nodes.Count - 1];
        if (!IsComputingPlaceholderText(tail.Text))
            return false;

        tail.Text = MarkComputingPlaceholderStopped(tail.Text);
        return true;
    }

    // A trailing tree/overview node ending in ": computing..." is a transient in-progress placeholder
    // (the initial second-stage slot, or a live proof-tighten "<name>: computing..." probe appended between
    // greedy tightening stages). Both are replaced in place once the stage they announce lands.
    private static bool IsComputingPlaceholderText(string text)
        => text.EndsWith(ComputingSuffix, StringComparison.Ordinal);

    // Rewrite a "<stage>: computing..." placeholder to "<stage>: stopped (not computed)" so a Stop
    // leaves no wording implying a computation is still running.
    private static string MarkComputingPlaceholderStopped(string text)
        => IsComputingPlaceholderText(text)
            ? text[..^ComputingSuffix.Length] + ": stopped (not computed)"
            : text;

    // On a user Stop, an interrupted stage leaves transient "computing.../in progress" placeholders on
    // screen (tree root suffix, the trailing compact slot, and the root details). Rewrite them to a
    // "stopped" wording so nothing still implies a computation is running. If the compact/edge stage had
    // already produced output, the placeholders were replaced during the run and there is nothing to fix.
    private void MarkResultsStopped()
    {
        if (_treeView.Nodes.Count == 0)
            return;

        TreeNode root = _treeView.Nodes[0];
        _treeView.BeginUpdate();
        bool markedTreePlaceholder = TryMarkTrailingComputingPlaceholderStopped(root.Nodes);
        if (markedTreePlaceholder)
        {
            root.Text = MarkLabelStopped(root.Text);
            if (root.Tag is string tag)
                root.Tag = MarkDetailsStopped(tag);
        }
        _treeView.EndUpdate();

        if (!markedTreePlaceholder)
            return;

        if (_overviewTree.Nodes.Count > 0)
        {
            _overviewTree.BeginUpdate();
            TryMarkTrailingComputingPlaceholderStopped(_overviewTree.Nodes);
            _overviewTree.EndUpdate();
        }
    }

    // Defensive cleanup after a normal (non-stopped) greedy run: RunGreedyPipeline always ends
    // by emitting the terminal EdgeCompact stage, whose handler appends no follow-up placeholder. But the
    // should-not-happen fallback (edgePlan null) returns without that final emission, which would leave
    // the last "edge compact ...: computing..." placeholder stranded. Drop any such trailing placeholder so a
    // finished run never shows a "computing..." node.
    private void RemoveTrailingComputingPlaceholder()
    {
        if (_treeView.Nodes.Count > 0)
        {
            TreeNode root = _treeView.Nodes[0];
            if (root.Nodes.Count > 0)
            {
                _treeView.BeginUpdate();
                TryRemoveTrailingComputingPlaceholder(root.Nodes);
                _treeView.EndUpdate();
            }
        }

        if (_overviewTree.Nodes.Count > 0)
        {
            _overviewTree.BeginUpdate();
            TryRemoveTrailingComputingPlaceholder(_overviewTree.Nodes);
            _overviewTree.EndUpdate();
        }
    }

    private static string MarkLabelStopped(string label)
    {
        int open = label.LastIndexOf(" (computing ", StringComparison.Ordinal);
        return open >= 0 ? label[..open] + " (stopped)" : label;
    }

    private static string MarkDetailsStopped(string details)
        => details
            .Replace("next stage in progress", "next stage not run (stopped)")
            .Replace($"{StageNames.ExactEdgeCompactPattern} stage in progress", $"{StageNames.ExactEdgeCompactPattern} stage not run (stopped)")
            .Replace("proof-edge-compact@S stage in progress", $"{StageNames.ExactEdgeCompactPattern} stage not run (stopped)")
            .Replace("edge compact exact stage in progress", $"{StageNames.ExactEdgeCompactPattern} stage not run (stopped)");

    // Before the first stage returns a real plan, show an explicit in-progress placeholder so the tree
    // region is never visually empty during the initial compute.
    private void ShowInitialStagePlaceholder(int n, int m, int k, bool feasibleMode)
    {
        string stageName = feasibleMode ? StageNames.GreedyFeasible : StageNames.StepProof;
        string rootLabel = feasibleMode
            ? $"n={n}, m={m}, k={k} (computing {StageNames.GreedyFeasible} stage...)"
            : $"n={n}, m={m}, k={k} (computing {StageNames.StepProof} stage...)";
        string rootDetails = feasibleMode
            ? "Greedy-feasible stage in progress."
            : "Step-proof stage in progress.";

        _treeView.BeginUpdate();
        _treeView.Nodes.Clear();
        var root = new TreeNode(rootLabel)
        {
            Tag = new LazyNodeDetails(() => rootDetails),
            NodeFont = new Font(_treeView.Font, FontStyle.Bold),
            ForeColor = _palette.ForeColor,
        };
        root.Nodes.Add(CreateComputingPlaceholderNode(stageName));
        _treeView.Nodes.Add(root);
        root.Expand();
        _treeView.EndUpdate();
        _treeView.SelectedNode = root;

        _overviewTree.BeginUpdate();
        _overviewTree.Nodes.Clear();
        _overviewTree.Nodes.Add(BuildOverviewNoteNode(FormatComputingPlaceholderText(stageName)));
        _overviewTree.EndUpdate();
    }

    private void PopulateTree(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan, bool compactImproved)
    {
        _treeView.BeginUpdate();
        _treeView.Nodes.Clear();
        _stateNodesByKey.Clear();
        _referenceTargets.Clear();
        _lazyDecisions.Clear();
        _lazyOverviewSections.Clear();
        _jumpTargets.Clear();
        _jumpScopeRoots.Clear();
        _jumpScopeStrategyRoots.Clear();
        _indexedJumpScopes.Clear();
        _navigationHistory.Clear();
        _backButton.Enabled = false;

        string rootLabel = BuildRootLabel(feasiblePlan, defaultPlan, compactPlan);
        var rootDetails = new LazyNodeDetails(() => BuildRootDetails(feasiblePlan, defaultPlan, compactPlan, compactImproved));

        var root = new TreeNode(rootLabel)
        {
            Tag = rootDetails,
            NodeFont = new Font(_treeView.Font, FontStyle.Bold),
            ForeColor = _palette.ForeColor,
        };

        // Slot 0: the step strategy, named by mode -- "step-proof" once the exact pass finishes (it
        // replaces the placeholder in place), or "greedy-feasible" for the constructive feasible plan
        // in greedy mode.
        StrategyPlan stepPlan = defaultPlan ?? feasiblePlan;
        string stepStageName = defaultPlan is null ? StageNames.GreedyFeasible : StageNames.StepProof;
        root.Nodes.Add(CreatePlanTreeRoot(stepStageName, stepPlan, "default", stepPlan.Elapsed));

        // Slot 1: the second stage's live placeholder. In exact mode this is the min-edge
        // "exact-edge-compact@S" pass; in greedy mode it is whatever RunGreedyPipeline emits first --
        // a proof-tighten stage, or "greedy-edge-compact@S" directly when the greedy bound is
        // already at the lower bound.
        if (compactPlan is null)
        {
            string firstStageName = defaultPlan is null
                ? NextProofTightenStageName(feasiblePlan, feasiblePlan.MaxStep)
                : StageNames.FormatExactEdgeCompact(feasiblePlan.MaxStep);
            root.Nodes.Add(CreateComputingPlaceholderNode(firstStageName));
        }
        else if (compactImproved)
            root.Nodes.Add(CreatePlanTreeRoot(defaultPlan is null ? StageNames.FormatGreedyEdgeCompact(compactPlan.MaxStep) : StageNames.FormatExactEdgeCompact(compactPlan.MaxStep), compactPlan, "compact", compactPlan.Elapsed));
        else
            root.Nodes.Add(CreateNoSolutionTreeRoot(defaultPlan is null ? StageNames.FormatGreedyEdgeCompact(compactPlan.MaxStep) : StageNames.FormatExactEdgeCompact(compactPlan.MaxStep), compactPlan.Elapsed));

        _treeView.Nodes.Add(root);
        root.Expand();

        _treeView.EndUpdate();
        _treeView.SelectedNode = root;

        RebuildOverview(feasiblePlan, defaultPlan, compactPlan, compactImproved);
    }

    // Squeeze on the optimum for a plan: L is the proven analytic lower bound
    // (RootProvenLowerBound), U is the achieved upper bound (MaxStep). When L == U the strategy is
    // in fact optimal (a proven floor met by an achievable strategy), even if it came from greedy.
    // Worded in "max steps" terms to match the rest of the UI, where the achieved/optimal quantity
    // is always the max-step count.
    private static string FormatPlanSqueeze(StrategyPlan plan)
    {
        int lower = plan.SearchStatistics.RootProvenLowerBound;
        int upper = plan.MaxStep;
        if (upper == 0)
            return "max steps = 0 (proven optimal)";
        if (lower > 0 && lower == upper)
            return $"max steps = {upper} (proven optimal)";

        string lowerText = lower > 0 ? lower.ToString() : "?";
        return $"{lowerText} <= max steps <= {upper}";
    }

    private static string FormatPlanInputs(StrategyPlan plan)
    {
        if (plan.RequestedK == plan.K)
            return $"n={plan.N}, m={plan.M}, k={plan.K}";
        return $"n={plan.N}, m={plan.M}, k={plan.RequestedK} (dual k'={plan.K})";
    }

    private static string BuildRootLabel(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan)
    {
        string head = FormatPlanInputs(feasiblePlan);
        if (defaultPlan is null)
            return $"{head}, {FormatPlanSqueeze(feasiblePlan)} (computing step...)";
        if (compactPlan is null)
        {
            double seconds = feasiblePlan.Elapsed.TotalSeconds + defaultPlan.Elapsed.TotalSeconds;
            return $"{head}, max steps={defaultPlan.MaxStep}, elapsed={seconds:F3} s (computing {StageNames.ExactEdgeCompactPattern} stage...)";
        }
        double totalSeconds = feasiblePlan.Elapsed.TotalSeconds + defaultPlan.Elapsed.TotalSeconds + compactPlan.Elapsed.TotalSeconds;
        // Lead with the optimality squeeze on the best plan: once the final tightening proves the next
        // step ceiling infeasible (the no-solution terminal), the incumbent's lower bound is closed to
        // its max-step and this reads "max steps = N (proven optimal)" -- the headline signal that the
        // search is done and the step count is provably best. While still tightening it reads
        // "L <= max steps <= U".
        return $"{head}, {FormatPlanSqueeze(compactPlan)}, total elapsed={totalSeconds:F3} s";
    }

    private static string BuildRootDetails(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan, bool compactImproved)
    {
        if (defaultPlan is null)
            return BuildFeasibleOnlyDetails(feasiblePlan);
        if (compactPlan is null)
            return BuildDefaultOnlyDetails(defaultPlan);
        return BuildTwoPhaseDetails(defaultPlan, compactPlan, compactImproved);
    }

    // Incrementally folds the finished compact result into the already-rendered tree instead of
    // rebuilding from scratch. The step subtree (root.Nodes[0]) -- along with its navigation map
    // entries -- is left untouched, so a user mid-browse keeps their expand/scroll/selection state.
    // Only the transient "compact: computing..." placeholder (root.Nodes[1]) is replaced -- either with
    // the compact subtree (a sibling scoped "compact" so its state keys never collide) when it improved,
    // or with a "no solution" note when it did not.
    private void FinalizeCompactInTree(StrategyPlan defaultPlan, StrategyPlan compactPlan, bool compactImproved)
    {
        // Defensive fallback: if the tree was cleared/rebuilt out from under us (e.g. a theme switch
        // mid-compact), there is no tree to extend, so do a full rebuild from the cached plans.
        if (_treeView.Nodes.Count == 0 || _feasiblePlan is null)
        {
            if (_feasiblePlan is not null)
                PopulateTree(_feasiblePlan, defaultPlan, compactPlan, compactImproved);
            return;
        }

        _treeView.BeginUpdate();

        TreeNode root = _treeView.Nodes[0];
        root.Text = BuildRootLabel(_feasiblePlan, defaultPlan, compactPlan);
        root.Tag = new LazyNodeDetails(() => BuildTwoPhaseDetails(defaultPlan, compactPlan, compactImproved));

        // Replace only the trailing compact slot (everything after the single step slot).
        while (root.Nodes.Count > 1)
            root.Nodes.RemoveAt(root.Nodes.Count - 1);
        string compactStageName = _defaultPlan is null
            ? StageNames.FormatGreedyEdgeCompact(compactPlan.MaxStep)
            : StageNames.FormatExactEdgeCompact(compactPlan.MaxStep);
        if (compactImproved)
            root.Nodes.Add(CreatePlanTreeRoot(compactStageName, compactPlan, "compact", compactPlan.Elapsed));
        else
            root.Nodes.Add(CreateNoSolutionTreeRoot(compactStageName, compactPlan.Elapsed));

        _treeView.EndUpdate();

        FinalizeCompactInOverview(compactPlan, compactImproved);
    }

    private TreeNode BuildStageTreeNode(StageResult stage, string scope, bool improved)
        => improved
            ? CreatePlanTreeRoot(stage.Name, stage.Plan!, scope, stage.Elapsed)
            : stage.HasPlan
                ? CreateNoImprovementTreeRoot(stage.Name, stage.Plan!, stage.Elapsed)
                : CreateNoSolutionTreeRoot(stage.Name, stage.Elapsed, NoSolutionMarker(stage));

    private TreeNode BuildStageOverviewNode(StageResult stage, string scope, bool improved)
        => improved
            ? BuildOverviewSectionNode(stage.Plan!, scope, stage.Name, stage.Elapsed)
            : BuildOverviewNoteNode(FormatStageRootLabel(
                stage.Name,
                stage.Elapsed,
                stage.Plan,
                stage.HasPlan ? "no improvement" : NoSolutionMarker(stage)));

    // Leaf note for a solution-less stage: null means "no solution" (a proven-infeasible ceiling),
    // otherwise the reason the incumbent merely stands -- "search incomplete (candidate cap reached)"
    // (the greedy cap truncated the enumeration, so infeasibility is unproven).
    private static string? NoSolutionMarker(StageResult stage)
        => stage.Incomplete ? "search incomplete (candidate cap reached)"
            : null;

    // Root-node detail text for greedy mode: the step plan followed by the full edge progression
    // (compact baseline -> each tightening -> any no-solution stage), so the detail pane mirrors the
    // stacked trees.
    private static string BuildGreedyProgressionDetails(StrategyPlan stepPlan, List<StageResult> stages)
    {
        var lines = new List<string>
        {
            "GreedyFeasible result (anytime: improving stages are shown as trees)",
            $"greedy-feasible: {FormatPlanSqueeze(stepPlan)}, total edges={stepPlan.TotalBranchEdges}",
        };
        StrategyPlan incumbent = stepPlan;
        foreach (StageResult stage in stages)
        {
            if (stage.Plan is { } p)
            {
                if (p.IsStrictRefinementOver(incumbent))
                {
                    lines.Add($"{stage.Name}: {FormatPlanSqueeze(p)}, total edges={p.TotalBranchEdges}");
                    incumbent = p;
                }
                else
                {
                    lines.Add($"{stage.Name}: max steps={p.MaxStep}, total edges={p.TotalBranchEdges} (no improvement)");
                }
            }
            else if (stage.Incomplete)
            {
                lines.Add($"{stage.Name}: search incomplete (candidate cap reached; infeasibility unproven, best plan kept)");
            }
            else
            {
                lines.Add($"{stage.Name}: no solution (no better strategy at this step ceiling)");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }
}
