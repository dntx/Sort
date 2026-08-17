using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace TopKFinder;

partial class MainForm
{
    private const string SkippedNoImprovementMarker = "no improvement";
    private const string SearchWaitingSuffix = " [waiting to continue]";
    private const string SearchRunningSuffix = " [searching]";
    private const string TreeBuildingSuffix = ", building tree]";
    private const string TreeReadySuffix = ", tree ready]";
    private const string StoppedSuffix = " [stopped]";

    private static string FormatSearchRunningPlaceholderText(string stageName)
        => stageName + SearchRunningSuffix;

    private static string FormatSearchWaitingPlaceholderText(string stageName)
        => stageName + SearchWaitingSuffix;

    private static string FormatTreeBuildingPlaceholderText(string stageName, StageTimings timings)
        => $"{stageName} [{FormatShortSeconds(timings.Solve + timings.Freeze)} searched{TreeBuildingSuffix}";

    private static string FormatTreeReadyPlaceholderText(string stageName, StageTimings timings)
        => $"{stageName} [{FormatShortSeconds(timings.Solve + timings.Freeze)} searched, {FormatShortSeconds(timings.Materialize)} built{TreeReadySuffix}";

    private static string FormatStoppedPlaceholderText(string stageName)
        => stageName + StoppedSuffix;

    private static string FormatStoppedPlaceholderText(string stageName, string prefix)
        => $"{stageName} [{prefix}, stopped]";

    private static string FormatShortSeconds(TimeSpan elapsed)
        => FormatAdaptiveElapsed(elapsed);

    private TreeNode CreateSearchRunningPlaceholderNode(string stageName)
        => new(FormatSearchRunningPlaceholderText(stageName)) { ForeColor = _palette.MutedForeColor };

    private string FormatPendingStagePlaceholderText(string stageName)
        => _pauseEachStageForRun && _stagePauseCompletion is not null
            ? FormatSearchWaitingPlaceholderText(stageName)
            : FormatSearchRunningPlaceholderText(stageName);

    private TreeNode CreatePendingStagePlaceholderNode(string stageName)
        => new(FormatPendingStagePlaceholderText(stageName))
        {
            ForeColor = _palette.MutedForeColor,
        };

    private static bool IsSearchWaitingPlaceholderText(string text)
        => text.EndsWith(SearchWaitingSuffix, StringComparison.Ordinal);

    private static bool IsSearchRunningPlaceholderText(string text)
        => text.EndsWith(SearchRunningSuffix, StringComparison.Ordinal);

    private static bool IsTreeBuildingPlaceholderText(string text)
        => text.EndsWith(TreeBuildingSuffix, StringComparison.Ordinal);

    private static bool IsTreeReadyPlaceholderText(string text)
        => text.EndsWith(TreeReadySuffix, StringComparison.Ordinal);

    private static bool IsAnyStageStatusPlaceholderText(string text)
        => IsSearchWaitingPlaceholderText(text)
            || IsSearchRunningPlaceholderText(text)
            || IsTreeBuildingPlaceholderText(text)
            || IsTreeReadyPlaceholderText(text)
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

    private static bool TryExtractTreeStatusPlaceholderPrefix(string text, out string prefix)
    {
        prefix = string.Empty;
        int open = text.IndexOf(" [", StringComparison.Ordinal);
        if (open < 0)
            return false;

        int suffixLength;
        if (IsTreeBuildingPlaceholderText(text))
        {
            suffixLength = TreeBuildingSuffix.Length;
        }
        else if (IsTreeReadyPlaceholderText(text))
        {
            suffixLength = TreeReadySuffix.Length;
        }
        else
        {
            return false;
        }

        int start = open + 2;
        int length = text.Length - start - suffixLength;
        if (length <= 0)
            return false;

        prefix = text.Substring(start, length);
        return true;
    }

    private static bool IsStageRootNodeText(string text, string stageName)
        => text.StartsWith(stageName + ":", StringComparison.Ordinal);

    private int EnsureStageDisplayOrder(string stageName)
    {
        if (_stageDisplayOrder.TryGetValue(stageName, out int order))
            return order;

        order = _nextStageDisplayOrder++;
        _stageDisplayOrder[stageName] = order;
        return order;
    }

    private bool TryExtractListedStageName(string text, out string stageName)
    {
        int statusSplit = text.IndexOf(" [", StringComparison.Ordinal);
        int rootSplit = text.IndexOf(':');

        // Prefer explicit root-stage prefix (<stage>: ...) when it appears before any
        // status suffix so labels like "greedy-tighten: [...]" normalize correctly.
        if (rootSplit > 0 && (statusSplit < 0 || rootSplit < statusSplit))
        {
            stageName = text[..rootSplit];
            return true;
        }

        if (statusSplit > 0)
        {
            stageName = text[..statusSplit];
            if (stageName.EndsWith(":", StringComparison.Ordinal))
                stageName = stageName[..^1];
            return true;
        }

        if (rootSplit > 0)
        {
            stageName = text[..rootSplit];
            return true;
        }

        stageName = string.Empty;
        return false;
    }

    private static int StageStatusRank(string text)
    {
        if (IsSearchWaitingPlaceholderText(text))
            return 1;
        if (IsSearchRunningPlaceholderText(text))
            return 2;
        if (IsTreeBuildingPlaceholderText(text))
            return 3;
        if (IsTreeReadyPlaceholderText(text))
            return 4;
        if (text.EndsWith(StoppedSuffix, StringComparison.Ordinal))
            return 5;
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

    private int FindStageInsertIndex(TreeNodeCollection nodes, string stageName)
    {
        int targetOrder = EnsureStageDisplayOrder(stageName);
        for (int i = 0; i < nodes.Count; i++)
        {
            TreeNode node = nodes[i];
            if (!TryExtractListedStageName(node.Text, out string listedStageName))
                continue;

            int listedOrder = EnsureStageDisplayOrder(listedStageName);
            if (listedOrder > targetOrder)
                return i;
        }

        return nodes.Count;
    }

    private void InsertOrReplaceStageNode(TreeNodeCollection nodes, TreeNode stageNode, string stageName)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (IsStageRootNodeText(nodes[i].Text, stageName))
            {
                nodes.RemoveAt(i);
                nodes.Insert(i, stageNode);
                return;
            }
        }

        int insertIndex = FindStageInsertIndex(nodes, stageName);
        if (insertIndex < nodes.Count)
            nodes.Insert(insertIndex, stageNode);
        else
            nodes.Add(stageNode);
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

        TreeNode placeholderNode = new(placeholderText) { ForeColor = _palette.MutedForeColor };
        int insertIndex = FindStageInsertIndex(nodes, stageName);
        if (insertIndex < nodes.Count)
            nodes.Insert(insertIndex, placeholderNode);
        else
            nodes.Add(placeholderNode);
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

    private void EnsureNextStageWaitingPlaceholder(string stageName)
    {
        if (_treeView.Nodes.Count == 0)
            return;

        string placeholderText = FormatSearchWaitingPlaceholderText(stageName);
        TreeNode root = _treeView.Nodes[0];
        _treeView.BeginUpdate();
        UpsertStagePlaceholder(root.Nodes, stageName, placeholderText);
        _treeView.EndUpdate();

        _overviewTree.BeginUpdate();
        UpsertStagePlaceholder(_overviewTree.Nodes, stageName, placeholderText);
        _overviewTree.EndUpdate();
    }

    private void MarkStageTreeBuilding(StageResult stage)
    {
        MarkStageTreeBuilding(stage.Name, stage.Timings.Solve + stage.Timings.Freeze);
    }

    private void MarkStageTreeBuilding(string stageName, TimeSpan searchElapsed)
    {
        StageTimings timings = StageTimings.Legacy(searchElapsed);
        if (_treeView.Nodes.Count == 0)
            return;

        TreeNode root = _treeView.Nodes[0];
        _treeView.BeginUpdate();
        UpsertStagePlaceholder(root.Nodes, stageName, FormatTreeBuildingPlaceholderText(stageName, timings));
        _treeView.EndUpdate();

        _overviewTree.BeginUpdate();
        UpsertStagePlaceholder(_overviewTree.Nodes, stageName, FormatTreeBuildingPlaceholderText(stageName, timings));
        _overviewTree.EndUpdate();
    }

    private void MarkStageTreeReady(StageResult stage)
    {
        if (_treeView.Nodes.Count == 0)
            return;

        _treeView.BeginUpdate();
        TreeNode root = _treeView.Nodes[0];
        UpsertStagePlaceholder(root.Nodes, stage.Name, FormatTreeReadyPlaceholderText(stage.Name, stage.Timings));
        _treeView.EndUpdate();

        _overviewTree.BeginUpdate();
        UpsertStagePlaceholder(_overviewTree.Nodes, stage.Name, FormatTreeReadyPlaceholderText(stage.Name, stage.Timings));
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

    private bool TryMarkTrailingComputingPlaceholderStopped(TreeNodeCollection nodes)
    {
        if (nodes.Count == 0)
            return false;

        TreeNode tail = nodes[nodes.Count - 1];
        if (!IsAnyStageStatusPlaceholderText(tail.Text))
            return false;

        string stageName = PlaceholderStageName(tail.Text);
        tail.Text = TryExtractTreeStatusPlaceholderPrefix(tail.Text, out string prefix)
            ? FormatStoppedPlaceholderText(stageName, prefix)
            : IsSearchRunningPlaceholderText(tail.Text)
                ? FormatStoppedPlaceholderText(stageName, $"{FormatShortSeconds(GetCurrentStageElapsed())} searched")
                : FormatStoppedPlaceholderText(stageName);
        return true;
    }

    private TimeSpan GetCurrentStageElapsed()
    {
        long totalMs = _runStopwatch?.ElapsedMilliseconds ?? 0;
        long stageMs = Math.Max(0, totalMs - _stageStartMs);
        return TimeSpan.FromMilliseconds(stageMs);
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
    // the last edge-compact ready/running placeholder stranded. Drop any such trailing placeholder so a
    // finished run never shows a running/ready node.
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
        string rootLabel = BuildInitialRootSearchLabel(n, m, k, stageName);
        string rootDetails = BuildInitialRootSearchDetails(stageName);

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

    private static string BuildInitialRootSearchLabel(int n, int m, int k, string stageName)
        => $"n={n}, m={m}, k={k} (search {stageName} stage...)";

    private static string BuildInitialRootSearchDetails(string stageName)
        => $"{stageName} search running.";

    private void UpdateInitialRootSearchStage(string stageName)
    {
        if (_treeView.Nodes.Count == 0 || _feasiblePlan is not null)
            return;

        if (_treeView.Nodes[0] is not TreeNode root)
            return;

        if (!Program.TryParseAndValidate(_nTextBox.Text, _mTextBox.Text, _kTextBox.Text, out int n, out int m, out int k, out _))
            return;

        _treeView.BeginUpdate();
        root.Text = BuildInitialRootSearchLabel(n, m, k, stageName);
        root.Tag = new LazyNodeDetails(() => BuildInitialRootSearchDetails(stageName));
        _treeView.EndUpdate();
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

        var root = new TreeNode(BuildDisplayedRootLabel(feasiblePlan, defaultPlan, compactPlan))
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
        StageTimings? timings = defaultPlan is null ? _greedyFeasibleStage?.Timings : _materializedStepDisplayStage?.Timings;
        return CreatePlanTreeRoot(stepStageName, stepPlan, DefaultExplorerScope, stepPlan.Elapsed, timings);
    }

    private TreeNode BuildCompactTreeSlotNode(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan, bool compactImproved)
    {
        if (compactPlan is null)
            return CreatePendingStagePlaceholderNode(PendingCompactStageName(feasiblePlan, defaultPlan));

        string compactStageName = FormatCompactStageName(defaultPlan is null, compactPlan.MaxStep);
        StageTimings? timings = _materializedCompactDisplayStage?.Timings;
        return compactImproved
            ? CreatePlanTreeRoot(compactStageName, compactPlan, CompactExplorerScope, compactPlan.Elapsed, timings)
            : CreateNoSolutionTreeRoot(compactStageName, compactPlan.Elapsed, timings: timings);
    }

    private string PendingCompactStageName(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan)
    {
        if (_nextStageName is not null)
            return _nextStageName;

        string displayedStepStageName = defaultPlan is null
            ? StageNames.GreedyFeasible
            : StageNames.StepProof;

        if (!string.Equals(_currentStageName, displayedStepStageName, StringComparison.Ordinal)
            && !string.Equals(_currentStageName, "-", StringComparison.Ordinal))
            return _currentStageName;

        return defaultPlan is null
            ? PipelineStageProtocol.NextGreedyStageName(
                _greedyFeasibleStage?.Solution?.Bounds.ProvenLowerBound
                    ?? feasiblePlan.SearchStatistics.RootProvenLowerBound,
                _incumbentStage?.Solution?.Score.WorstCaseSteps ?? feasiblePlan.MaxStep)
            : StageNames.FormatExactEdgeCompact(feasiblePlan.MaxStep);
    }

    private static string FormatCompactStageName(bool greedyMode, int maxStep)
        => greedyMode
            ? StageNames.FormatGreedyEdgeCompact(maxStep)
            : StageNames.FormatExactEdgeCompact(maxStep);

    private void ShowSearchOnlySummaryStage(StageResult stage)
    {
        if (_treeView.Nodes.Count == 0)
            return;
        EnsureStageDisplayOrder(stage.Name);

        string marker = stage.Skipped
            ? "skipped"
            : _greedyIncumbentImproved && stage.Solution is not null
                ? $"search-only tightened to <= {stage.Solution.Score.WorstCaseSteps}"
                : "no improvement";

        // Reuse the same stage de-dup flow as the proof-tighten stages: remove transient
        // placeholders first, then insert/replace the concrete stage node.
        RemoveStageStatusPlaceholder(stage.Name);

        _treeView.BeginUpdate();
        TreeNode root = _treeView.Nodes[0];
        InsertOrReplaceStageNode(
            root.Nodes,
            CreateStageStatusNoteNode(stage.Name, stage.Elapsed, marker, stage.Timings),
            stage.Name);
        _treeView.EndUpdate();

        _overviewTree.BeginUpdate();
        InsertOrReplaceStageNode(
            _overviewTree.Nodes,
            BuildOverviewNoteNode(FormatStageStatusNoteLabel(stage.Name, stage.Elapsed, marker, stage.Timings)),
            stage.Name);
        _overviewTree.EndUpdate();
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

    private static string FormatStageStatusNoteLabel(string stageName, TimeSpan elapsed, string marker, StageTimings? timings = null)
        => $"{stageName}: [{FormatStageElapsedText(elapsed, timings)}, {marker}]";

    private TreeNode CreateStageStatusNoteNode(string stageName, TimeSpan elapsed, string marker, StageTimings? timings = null)
        => new TreeNode(FormatStageStatusNoteLabel(stageName, elapsed, marker, timings))
        {
            ForeColor = _palette.MutedForeColor,
        };

    private static string BuildRootLabel(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan)
    {
        string head = FormatPlanInputs(feasiblePlan);
        if (defaultPlan is null)
            return $"{head}, {FormatPlanSqueeze(feasiblePlan)} (search step-proof stage...)";
        if (compactPlan is null)
        {
            double seconds = ComputeFallbackTotalElapsedSeconds(feasiblePlan, defaultPlan, compactPlan: null);
            return $"{head}, max steps={defaultPlan.MaxStep}, elapsed={FormatAdaptiveElapsed(TimeSpan.FromSeconds(seconds))} (search {StageNames.ExactEdgeCompactPattern} stage...)";
        }
        double totalSeconds = ComputeFallbackTotalElapsedSeconds(feasiblePlan, defaultPlan, compactPlan);
        // Lead with the optimality squeeze on the best plan: once the final tightening proves the next
        // step ceiling infeasible (the no-solution terminal), the incumbent's lower bound is closed to
        // its max-step and this reads "max steps = N (proven optimal)" -- the headline signal that the
        // search is done and the step count is provably best. While still tightening it reads
        // "L <= max steps <= U".
        return $"{head}, {FormatPlanSqueeze(compactPlan)}, total elapsed={FormatAdaptiveElapsed(TimeSpan.FromSeconds(totalSeconds))}";
    }

    private string BuildDisplayedRootLabel(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan)
    {
        string head = FormatPlanInputs(feasiblePlan);
        if (defaultPlan is null)
            return $"{head}, {FormatPlanSqueeze(feasiblePlan)} (search step-proof stage...)";
        if (compactPlan is null)
        {
            double seconds = ComputeDisplayedTotalElapsedSeconds(feasiblePlan, defaultPlan, compactPlan: null);
            return $"{head}, max steps={defaultPlan.MaxStep}, elapsed={FormatAdaptiveElapsed(TimeSpan.FromSeconds(seconds))} (search {StageNames.ExactEdgeCompactPattern} stage...)";
        }

        double totalSeconds = ComputeDisplayedTotalElapsedSeconds(feasiblePlan, defaultPlan, compactPlan);
        return $"{head}, {FormatPlanSqueeze(compactPlan)}, total elapsed={FormatAdaptiveElapsed(TimeSpan.FromSeconds(totalSeconds))}";
    }

    private static string BuildRootDetails(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan, bool compactImproved)
    {
        if (defaultPlan is null)
            return BuildFeasibleOnlyDetails(feasiblePlan);
        if (compactPlan is null)
            return BuildDefaultOnlyDetails(defaultPlan);
        return BuildTwoPhaseDetails(defaultPlan, compactPlan, compactImproved);
    }

    // Incrementally folds the finished compact result into the already-displayed tree instead of
    // rebuilding from scratch. The step subtree (root.Nodes[0]) -- along with its navigation map
    // entries -- is left untouched, so a user mid-browse keeps their expand/scroll/selection state.
    // Only the transient compact ready/running placeholder (root.Nodes[1]) is replaced -- either with
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
        root.Text = BuildDisplayedRootLabel(_feasiblePlan!, defaultPlan, compactPlan);
        root.Tag = new LazyNodeDetails(() => BuildTwoPhaseDetails(defaultPlan, compactPlan, compactImproved));
    }

    private void ReplaceCompactTreeSlot(TreeNode root, StrategyPlan compactPlan, bool compactImproved)
    {
        // Replace only the trailing compact slot (everything after the single step slot).
        while (root.Nodes.Count > 1)
            root.Nodes.RemoveAt(root.Nodes.Count - 1);

        string compactStageName = FormatCompactStageName(_defaultPlan is null, compactPlan.MaxStep);
        StageTimings? timings = _materializedCompactDisplayStage?.Timings;
        root.Nodes.Add(compactImproved
            ? CreatePlanTreeRoot(compactStageName, compactPlan, CompactExplorerScope, compactPlan.Elapsed, timings)
            : CreateNoSolutionTreeRoot(compactStageName, compactPlan.Elapsed, timings: timings));
    }

    private TreeNode BuildStageTreeNode(StageResult stage, string scope, bool improved)
        => improved
            ? CreatePlanTreeRoot(stage.Name, stage.MaterializedPlan!, scope, stage.Elapsed, stage.Timings)
            : stage.Skipped
                ? CreateNoSolutionTreeRoot(stage.Name, stage.Elapsed, "skipped", stage.Timings)
            : stage.Solution is not null && !stage.HasPlan
                ? CreateNoSolutionTreeRoot(stage.Name, stage.Elapsed, SkippedNoImprovementMarker, stage.Timings)
            : stage.HasPlan
                ? CreateNoImprovementTreeRoot(stage.Name, stage.MaterializedPlan!, stage.Elapsed, stage.Timings)
                : CreateNoSolutionTreeRoot(stage.Name, stage.Elapsed, NoSolutionMarker(stage), stage.Timings);

    private TreeNode BuildStageOverviewNode(StageResult stage, string scope, bool improved)
        => improved
            ? BuildOverviewSectionNode(stage.MaterializedPlan!, scope, stage.Name, stage.Elapsed, stage.Timings)
            : stage.Skipped
                ? BuildOverviewNoteNode(FormatStageRootLabel(stage.Name, stage.Elapsed, plan: null, marker: "skipped", timings: stage.Timings))
            : stage.Solution is not null && !stage.HasPlan
                ? BuildOverviewNoteNode(FormatStageRootLabel(stage.Name, stage.Elapsed, plan: null, marker: SkippedNoImprovementMarker, timings: stage.Timings))
            : BuildOverviewNoteNode(FormatStageRootLabel(
                stage.Name,
                stage.Elapsed,
                stage.MaterializedPlan,
                stage.HasPlan ? "no improvement" : NoSolutionMarker(stage),
                stage.Timings));

    // Leaf note for a solution-less stage: null means "no solution" (a proven-infeasible ceiling),
    // otherwise the reason the incumbent merely stands -- "search incomplete (candidate cap reached)"
    // (the greedy cap truncated the enumeration, so infeasibility is unproven).
    private static string? NoSolutionMarker(StageResult stage)
        => stage.Skipped ? "skipped"
            : stage.Incomplete ? "search incomplete (candidate cap reached)"
            : null;

    // Root-node detail text for greedy mode: the step plan followed by the full edge progression
    // (compact baseline -> each tightening -> any no-solution stage), so the detail pane mirrors the
    // stacked trees.
    private static string BuildGreedyProgressionDetails(StageResult initialStage, StageResult? greedyTightenStage, List<StageResult> stages)
    {
        StrategyPlan stepPlan = initialStage.MaterializedPlan!;
        var lines = new List<string>
        {
            "Greedy progression (greedy-feasible -> greedy-tighten -> proof-tighten)",
            $"greedy-feasible: {FormatPlanSqueeze(stepPlan)}, total edges={stepPlan.TotalBranchEdges}",
        };
        StageResult incumbent = initialStage;

        if (greedyTightenStage is not null)
        {
            StageResult stage = greedyTightenStage.Value;
            if (stage.Skipped)
            {
                lines.Add($"{stage.Name}: skipped");
            }
            else if (stage.MaterializedPlan is { } p)
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
            else
            {
                lines.Add($"{stage.Name}: no solution (no better strategy at this step ceiling)");
            }
        }

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
            else if (stage.Skipped)
            {
                lines.Add($"{stage.Name}: skipped");
            }
            else if (stage.Solution is not null)
            {
                lines.Add($"{stage.Name}: {SkippedNoImprovementMarker}");
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
