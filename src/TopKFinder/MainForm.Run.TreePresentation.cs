using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TopKFinder;

partial class MainForm
{
    private const string SearchRunningSuffix = " [search: running]";
    private const string DisplayRunningSuffix = " [display: running]";
    private const string StoppedSuffix = " [stopped]";

    private static string FormatSearchRunningPlaceholderText(string stageName)
        => stageName + SearchRunningSuffix;

    private static string FormatDisplayRunningPlaceholderText(string stageName)
        => stageName + DisplayRunningSuffix;

    private static string FormatStoppedPlaceholderText(string stageName)
        => stageName + StoppedSuffix;

    private TreeNode CreateSearchRunningPlaceholderNode(string stageName)
        => new(FormatSearchRunningPlaceholderText(stageName)) { ForeColor = _palette.MutedForeColor };

    private static bool IsSearchRunningPlaceholderText(string text)
        => text.EndsWith(SearchRunningSuffix, StringComparison.Ordinal);

    private static bool IsDisplayRunningPlaceholderText(string text)
        => text.EndsWith(DisplayRunningSuffix, StringComparison.Ordinal);

    private static bool IsAnyStageStatusPlaceholderText(string text)
        => IsSearchRunningPlaceholderText(text)
            || IsDisplayRunningPlaceholderText(text)
            || text.EndsWith(StoppedSuffix, StringComparison.Ordinal);

    private static bool IsStageStatusPlaceholderForStage(string text, string stageName)
    {
        if (text.Length <= stageName.Length)
            return false;

        if (!text.StartsWith(stageName, StringComparison.Ordinal))
            return false;

        return IsAnyStageStatusPlaceholderText(text);
    }

    private static string PlaceholderStageName(string text)
    {
        int split = text.IndexOf(" [", StringComparison.Ordinal);
        return split > 0 ? text[..split] : text;
    }

    private static bool IsStageRootNodeText(string text, string stageName)
        => text.StartsWith(stageName + ":", StringComparison.Ordinal);

    private static int StageStatusRank(string text)
    {
        if (IsSearchRunningPlaceholderText(text))
            return 1;
        if (IsDisplayRunningPlaceholderText(text))
            return 2;
        if (text.EndsWith(StoppedSuffix, StringComparison.Ordinal))
            return 3;
        return 0;
    }

    private static bool HasStageRootNode(TreeNodeCollection nodes, string stageName)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (IsStageRootNodeText(nodes[i].Text, stageName))
                return true;
        }

        return false;
    }

    private static void RemoveStageStatusPlaceholders(TreeNodeCollection nodes, string stageName)
    {
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            TreeNode node = nodes[i];
            if (IsAnyStageStatusPlaceholderText(node.Text)
                && IsStageStatusPlaceholderForStage(node.Text, stageName))
            {
                nodes.RemoveAt(i);
            }
        }
    }

    private void UpsertStagePlaceholder(TreeNodeCollection nodes, string stageName, string placeholderText)
    {
        if (HasStageRootNode(nodes, stageName))
            return;

        int incomingRank = StageStatusRank(placeholderText);
        for (int i = 0; i < nodes.Count; i++)
        {
            TreeNode node = nodes[i];
            if (!IsAnyStageStatusPlaceholderText(node.Text))
                continue;

            if (IsStageStatusPlaceholderForStage(node.Text, stageName))
            {
                int currentRank = StageStatusRank(node.Text);
                if (incomingRank >= currentRank)
                    node.Text = placeholderText;
                node.ForeColor = _palette.MutedForeColor;
                return;
            }
        }

        nodes.Add(new TreeNode(placeholderText) { ForeColor = _palette.MutedForeColor });
    }

    private void EnsureLatestStageSearchPlaceholder(string stageName)
    {
        if (_treeView.Nodes.Count == 0)
            return;

        TreeNode root = _treeView.Nodes[0];
        _treeView.BeginUpdate();
        UpsertStagePlaceholder(root.Nodes, stageName, FormatSearchRunningPlaceholderText(stageName));
        _treeView.EndUpdate();

        _overviewTree.BeginUpdate();
        UpsertStagePlaceholder(_overviewTree.Nodes, stageName, FormatSearchRunningPlaceholderText(stageName));
        _overviewTree.EndUpdate();
    }

    private void MarkStageDisplayInProgress(string stageName)
    {
        if (_treeView.Nodes.Count == 0)
            return;

        TreeNode root = _treeView.Nodes[0];
        _treeView.BeginUpdate();
        UpsertStagePlaceholder(root.Nodes, stageName, FormatDisplayRunningPlaceholderText(stageName));
        _treeView.EndUpdate();

        _overviewTree.BeginUpdate();
        UpsertStagePlaceholder(_overviewTree.Nodes, stageName, FormatDisplayRunningPlaceholderText(stageName));
        _overviewTree.EndUpdate();
    }

    private void RemoveStageStatusPlaceholder(string stageName)
    {
        if (_treeView.Nodes.Count == 0)
            return;

        TreeNode root = _treeView.Nodes[0];
        _treeView.BeginUpdate();
        RemoveStageStatusPlaceholders(root.Nodes, stageName);
        _treeView.EndUpdate();

        _overviewTree.BeginUpdate();
        RemoveStageStatusPlaceholders(_overviewTree.Nodes, stageName);
        _overviewTree.EndUpdate();
    }

    private static bool TryRemoveTrailingComputingPlaceholder(TreeNodeCollection nodes)
    {
        if (nodes.Count == 0 || !IsAnyStageStatusPlaceholderText(nodes[nodes.Count - 1].Text))
            return false;

        nodes.RemoveAt(nodes.Count - 1);
        return true;
    }

    private static bool TryMarkTrailingComputingPlaceholderStopped(TreeNodeCollection nodes)
    {
        if (nodes.Count == 0)
            return false;

        TreeNode tail = nodes[nodes.Count - 1];
        if (!IsAnyStageStatusPlaceholderText(tail.Text))
            return false;

        tail.Text = FormatStoppedPlaceholderText(PlaceholderStageName(tail.Text));
        return true;
    }

    // A trailing tree/overview status node is a transient in-progress placeholder
    // (the initial second-stage slot, or a live proof-tighten "<name> [search: ...]" probe appended between
    // greedy tightening stages). Both are replaced in place once the stage they announce lands.

    // On a user Stop, an interrupted stage leaves transient search/display placeholders on
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
    // the last edge-compact pending/running placeholder stranded. Drop any such trailing placeholder so a
    // finished run never shows a running/pending node.
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
        int searchOpen = label.LastIndexOf(" (search ", StringComparison.Ordinal);
        if (searchOpen >= 0)
            return label[..searchOpen] + " (stopped)";

        int computingOpen = label.LastIndexOf(" (computing ", StringComparison.Ordinal);
        return computingOpen >= 0 ? label[..computingOpen] + " (stopped)" : label;
    }

    private static string MarkDetailsStopped(string details)
        => details
            .Replace("next stage in progress", "next stage not run (stopped)")
            .Replace("next stage search running", "next stage not run (stopped)")
            .Replace($"{StageNames.ExactEdgeCompactPattern} stage in progress", $"{StageNames.ExactEdgeCompactPattern} stage not run (stopped)")
            .Replace($"{StageNames.ExactEdgeCompactPattern} search running", $"{StageNames.ExactEdgeCompactPattern} stage not run (stopped)")
            .Replace("proof-edge-compact@S stage in progress", $"{StageNames.ExactEdgeCompactPattern} stage not run (stopped)")
            .Replace("proof-edge-compact@S search running", $"{StageNames.ExactEdgeCompactPattern} stage not run (stopped)")
            .Replace("edge compact exact stage in progress", $"{StageNames.ExactEdgeCompactPattern} stage not run (stopped)")
            .Replace("Greedy-feasible stage in progress.", "Greedy-feasible stage not run (stopped).")
            .Replace("Step-proof stage in progress.", "Step-proof stage not run (stopped).")
            .Replace("Greedy-feasible search running.", "Greedy-feasible stage not run (stopped).")
            .Replace("Step-proof search running.", "Step-proof stage not run (stopped).");

    // Before the first stage returns a real plan, show an explicit in-progress placeholder so the tree
    // region is never visually empty during the initial compute.
    private void ShowInitialStagePlaceholder(int n, int m, int k, bool feasibleMode)
    {
        string stageName = feasibleMode ? StageNames.GreedyFeasible : StageNames.StepProof;
        string rootLabel = feasibleMode
            ? $"n={n}, m={m}, k={k} (search {StageNames.GreedyFeasible} stage...)"
            : $"n={n}, m={m}, k={k} (search {StageNames.StepProof} stage...)";
        string rootDetails = feasibleMode
            ? "Greedy-feasible search running."
            : "Step-proof search running.";

        _treeView.BeginUpdate();
        _treeView.Nodes.Clear();
        var root = new TreeNode(rootLabel)
        {
            Tag = new LazyNodeDetails(() => rootDetails),
            NodeFont = new Font(_treeView.Font, FontStyle.Bold),
            ForeColor = _palette.ForeColor,
        };
        root.Nodes.Add(CreateSearchRunningPlaceholderNode(stageName));
        _treeView.Nodes.Add(root);
        root.Expand();
        _treeView.EndUpdate();
        _treeView.SelectedNode = root;

        _overviewTree.BeginUpdate();
        _overviewTree.Nodes.Clear();
        _overviewTree.Nodes.Add(BuildOverviewNoteNode(FormatSearchRunningPlaceholderText(stageName)));
        _overviewTree.EndUpdate();
    }

    private void PopulateTree(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan, bool compactImproved)
    {
        PrepareTreeForFreshPopulation();

        TreeNode root = CreatePopulatedRootTreeNode(feasiblePlan, defaultPlan, compactPlan, compactImproved);

        RebuildOverview(feasiblePlan, defaultPlan, compactPlan, compactImproved);
    }

    private TreeNode CreatePopulatedRootTreeNode(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan, bool compactImproved)
    {
        _treeView.BeginUpdate();

        var root = new TreeNode(BuildRootLabel(feasiblePlan, defaultPlan, compactPlan))
        {
            Tag = new LazyNodeDetails(() => BuildRootDetails(feasiblePlan, defaultPlan, compactPlan, compactImproved)),
            NodeFont = new Font(_treeView.Font, FontStyle.Bold),
            ForeColor = _palette.ForeColor,
        };
        root.Nodes.Add(BuildStepTreeSlotNode(feasiblePlan, defaultPlan));
        root.Nodes.Add(BuildCompactTreeSlotNode(feasiblePlan, defaultPlan, compactPlan, compactImproved));
        _treeView.Nodes.Add(root);
        root.Expand();

        _treeView.EndUpdate();
        _treeView.SelectedNode = root;
        return root;
    }

    private void PrepareTreeForFreshPopulation()
    {
        ResetExplorerState(clearTreeNodes: true, clearOverviewNodes: false);
    }

    private TreeNode BuildStepTreeSlotNode(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan)
    {
        StrategyPlan stepPlan = defaultPlan ?? feasiblePlan;
        string stepStageName = defaultPlan is null ? StageNames.GreedyFeasible : StageNames.StepProof;
        return CreatePlanTreeRoot(stepStageName, stepPlan, DefaultExplorerScope, stepPlan.Elapsed);
    }

    private TreeNode BuildCompactTreeSlotNode(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan, bool compactImproved)
    {
        if (compactPlan is null)
            return CreateSearchRunningPlaceholderNode(FirstCompactTreeStageName(feasiblePlan, defaultPlan));

        string compactStageName = FormatCompactStageName(defaultPlan is null, compactPlan.MaxStep);
        return compactImproved
            ? CreatePlanTreeRoot(compactStageName, compactPlan, CompactExplorerScope, compactPlan.Elapsed)
            : CreateNoSolutionTreeRoot(compactStageName, compactPlan.Elapsed);
    }

    private string FirstCompactTreeStageName(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan)
        => defaultPlan is null
            ? NextProofTightenStageNameForPresentation(feasiblePlan, feasiblePlan.MaxStep)
            : StageNames.FormatExactEdgeCompact(feasiblePlan.MaxStep);

    private static string FormatCompactStageName(bool greedyMode, int maxStep)
        => greedyMode
            ? StageNames.FormatGreedyEdgeCompact(maxStep)
            : StageNames.FormatExactEdgeCompact(maxStep);

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
            return $"{head}, {FormatPlanSqueeze(feasiblePlan)} (search step-proof stage...)";
        if (compactPlan is null)
        {
            double seconds = feasiblePlan.Elapsed.TotalSeconds + defaultPlan.Elapsed.TotalSeconds;
            return $"{head}, max steps={defaultPlan.MaxStep}, elapsed={seconds:F3} s (search {StageNames.ExactEdgeCompactPattern} stage...)";
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
    // Only the transient compact pending/running placeholder (root.Nodes[1]) is replaced -- either with
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
        UpdateTreeRootForFinalCompact(root, defaultPlan, compactPlan, compactImproved);
        ReplaceCompactTreeSlot(root, compactPlan, compactImproved);

        _treeView.EndUpdate();

        FinalizeCompactInOverview(compactPlan, compactImproved);
    }

    private void UpdateTreeRootForFinalCompact(TreeNode root, StrategyPlan defaultPlan, StrategyPlan compactPlan, bool compactImproved)
    {
        root.Text = BuildRootLabel(_feasiblePlan!, defaultPlan, compactPlan);
        root.Tag = new LazyNodeDetails(() => BuildTwoPhaseDetails(defaultPlan, compactPlan, compactImproved));
    }

    private void ReplaceCompactTreeSlot(TreeNode root, StrategyPlan compactPlan, bool compactImproved)
    {
        // Replace only the trailing compact slot (everything after the single step slot).
        while (root.Nodes.Count > 1)
            root.Nodes.RemoveAt(root.Nodes.Count - 1);

        string compactStageName = FormatCompactStageName(_defaultPlan is null, compactPlan.MaxStep);
        root.Nodes.Add(compactImproved
            ? CreatePlanTreeRoot(compactStageName, compactPlan, CompactExplorerScope, compactPlan.Elapsed)
            : CreateNoSolutionTreeRoot(compactStageName, compactPlan.Elapsed));
    }

    private TreeNode BuildStageTreeNode(StageResult stage, string scope, bool improved)
        => improved
            ? CreatePlanTreeRoot(stage.Name, stage.MaterializedPlan!, scope, stage.Elapsed)
            : stage.Solution is not null && !stage.HasPlan
                ? CreateNoSolutionTreeRoot(stage.Name, stage.Elapsed, "no improvement")
            : stage.HasPlan
                ? CreateNoImprovementTreeRoot(stage.Name, stage.MaterializedPlan!, stage.Elapsed)
                : CreateNoSolutionTreeRoot(stage.Name, stage.Elapsed, NoSolutionMarker(stage));

    private TreeNode BuildStageOverviewNode(StageResult stage, string scope, bool improved)
        => improved
            ? BuildOverviewSectionNode(stage.MaterializedPlan!, scope, stage.Name, stage.Elapsed)
            : stage.Solution is not null && !stage.HasPlan
                ? BuildOverviewNoteNode(FormatStageRootLabel(stage.Name, stage.Elapsed, plan: null, marker: "no improvement"))
            : BuildOverviewNoteNode(FormatStageRootLabel(
                stage.Name,
                stage.Elapsed,
                stage.MaterializedPlan,
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
    private static string BuildGreedyProgressionDetails(StageResult initialStage, List<StageResult> stages)
    {
        StrategyPlan stepPlan = initialStage.MaterializedPlan!;
        var lines = new List<string>
        {
            "GreedyFeasible result (anytime: improving stages are shown as trees)",
            $"greedy-feasible: {FormatPlanSqueeze(stepPlan)}, total edges={stepPlan.TotalBranchEdges}",
        };
        StageResult incumbent = initialStage;
        foreach (StageResult stage in stages)
        {
            if (stage.MaterializedPlan is { } p)
            {
                if (PipelineStageProtocol.IsImprovement(stage, incumbent))
                {
                    lines.Add($"{stage.Name}: {FormatPlanSqueeze(p)}, total edges={p.TotalBranchEdges}");
                    incumbent = stage;
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
