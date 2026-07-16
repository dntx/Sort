using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

partial class MainForm
{
    // Wraps a tree in a panel with a small top toolbar holding that tree's own buttons (Expand /
    // Collapse, plus Back on the strategy tree), so each of the two tree regions is controlled
    // independently.
    private static Panel CreateTreeRegion(TreeView tree, params Button[] buttons)
    {
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(2, 2, 2, 2),
            Margin = Padding.Empty,
        };
        foreach (Button button in buttons)
            toolbar.Controls.Add(button);

        tree.Dock = DockStyle.Fill;

        var region = new Panel { Dock = DockStyle.Fill };
        // Add the fill control first, then the top toolbar, so the toolbar docks above the tree.
        region.Controls.Add(tree);
        region.Controls.Add(toolbar);
        return region;
    }

    // Resets the result surfaces (overview list, tree, navigation state, details) so a fresh Run
    // does not leave the previous parameters' output on screen while the new search is in flight or
    // if it is cancelled / fails before producing a plan.
    private void ClearResultsView()
    {
        _overviewTree.Nodes.Clear();

        _treeView.BeginUpdate();
        _treeView.Nodes.Clear();
        _treeView.EndUpdate();

        _stateNodesByKey.Clear();
        _referenceTargets.Clear();
        _lazyDecisions.Clear();
        _lazyOverviewSections.Clear();
        _jumpTargets.Clear();
        _navigationHistory.Clear();
        _backButton.Enabled = false;
    }

    // Renders the overview panel so it mirrors the tree one-to-one: a step section (named by mode --
    // "step-proof"/"greedy-feasible") and an "edge-compact@S" section ("computing..." placeholder until the edge-compact stage
    // finishes). Each section is an independent root, so the strategies' overviews can be browsed and
    // collapsed separately. This is the full-rebuild path used for the initial render and theme switches.
    private void RebuildOverview(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan, bool exactImproved, bool compactImproved)
    {
        _overviewTree.BeginUpdate();
        _overviewTree.Nodes.Clear();

        StrategyPlan stepPlan = defaultPlan ?? feasiblePlan;
        string stepStageName = defaultPlan is null ? "greedy-feasible" : "step-proof";
        _overviewTree.Nodes.Add(BuildOverviewSectionNode(stepPlan, "default", stepStageName, stepPlan.Elapsed));

        if (compactPlan is null)
        {
            string firstStageName = defaultPlan is null
                ? NextProofTightenStageName(feasiblePlan, feasiblePlan.MaxStep)
                : StrategyBuilder.FormatEdgeCompactStageName(feasiblePlan.MaxStep);
            _overviewTree.Nodes.Add(BuildOverviewNoteNode(FormatComputingPlaceholderText(firstStageName)));
        }
        else if (compactImproved)
            _overviewTree.Nodes.Add(BuildOverviewSectionNode(compactPlan, "compact", StrategyBuilder.FormatEdgeCompactStageName(compactPlan.MaxStep), compactPlan.Elapsed));
        else
            _overviewTree.Nodes.Add(BuildOverviewNoteNode(FormatStageRootLabel(StrategyBuilder.FormatEdgeCompactStageName(compactPlan.MaxStep), compactPlan.Elapsed, plan: null)));

        _overviewTree.EndUpdate();
    }

    // Incrementally folds the finished compact result into the overview, mirroring the tree update:
    // the step section (0) -- and the user's expand/scroll state over it -- is left untouched, and
    // only the trailing compact placeholder root (1) is replaced.
    private void FinalizeCompactInOverview(StrategyPlan compactPlan, bool compactImproved)
    {
        _overviewTree.BeginUpdate();

        // Drop the trailing edge-compact "computing..." placeholder root.
        if (_overviewTree.Nodes.Count > 0)
            _overviewTree.Nodes.RemoveAt(_overviewTree.Nodes.Count - 1);

        if (compactImproved)
            _overviewTree.Nodes.Add(BuildOverviewSectionNode(compactPlan, "compact", StrategyBuilder.FormatEdgeCompactStageName(compactPlan.MaxStep), compactPlan.Elapsed));
        else
            _overviewTree.Nodes.Add(BuildOverviewNoteNode(FormatStageRootLabel(StrategyBuilder.FormatEdgeCompactStageName(compactPlan.MaxStep), compactPlan.Elapsed, plan: null)));

        _overviewTree.EndUpdate();
    }

    private TreeNode BuildOverviewSectionNode(StrategyPlan plan, string scope, string stageName, TimeSpan elapsed)
    {
        var sectionNode = new TreeNode(FormatStageRootLabel(stageName, elapsed, plan))
        {
            NodeFont = new Font(_overviewTree.Font, FontStyle.Bold),
            ForeColor = _palette.ForeColor,
        };

        // Defer expensive overview row construction until the section is actually expanded.
        sectionNode.Nodes.Add(new TreeNode());
        _lazyOverviewSections[sectionNode] = new LazyOverviewSection(plan, scope);
        return sectionNode;
    }

    private void MaterializeOverviewSection(TreeNode sectionNode)
    {
        if (!_lazyOverviewSections.TryGetValue(sectionNode, out LazyOverviewSection lazy))
            return;

        _lazyOverviewSections.Remove(sectionNode);
        _overviewTree.BeginUpdate();
        sectionNode.Nodes.Clear();
        foreach (OverviewRow row in DisplayEngine.BuildOverview(lazy.Plan).Rows)
        {
            string? key = row.LinkStateId is int id ? $"{lazy.Scope}:{id}" : null;
            var headlineNode = new TreeNode(row.Headline)
            {
                Tag = key,
                ForeColor = _palette.ForeColor,
            };
            foreach (string detail in row.Details)
            {
                headlineNode.Nodes.Add(new TreeNode(detail)
                {
                    Tag = key,
                    ForeColor = _palette.MutedForeColor,
                });
            }
            sectionNode.Nodes.Add(headlineNode);
        }
        _overviewTree.EndUpdate();
    }

    private TreeNode BuildOverviewNoteNode(string text)
    {
        return new TreeNode(text)
        {
            ForeColor = _palette.MutedForeColor,
        };
    }

    private void JumpFromOverviewSelection()
    {
        if (_overviewTree.SelectedNode?.Tag is not string targetStateKey)
            return;

        if (ResolveStateNode(targetStateKey) is not TreeNode targetNode)
            return;

        targetNode.EnsureVisible();
        _treeView.SelectedNode = targetNode;
    }

    private ContextMenuStrip CreateOverviewContextMenu()
    {
        var menu = new ContextMenuStrip();

        var copySelected = new ToolStripMenuItem("Copy") { ShortcutKeyDisplayString = "Ctrl+C" };
        copySelected.Click += (_, _) => CopyOverviewSelection();

        var copyAll = new ToolStripMenuItem("Copy all");
        copyAll.Click += (_, _) => CopyOverviewAll();

        menu.Items.Add(copySelected);
        menu.Items.Add(copyAll);

        menu.Opening += (_, e) =>
        {
            if (_overviewTree.Nodes.Count == 0)
            {
                e.Cancel = true;
                return;
            }

            copySelected.Enabled = _overviewTree.SelectedNode is not null;
            copyAll.Enabled = true;
        };

        return menu;
    }

    private void OverviewTree_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C)
        {
            CopyOverviewSelection();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    // Copies the selected node and its descendants (each level indented by four spaces), so copying
    // a section root yields that whole strategy's overview and copying a row yields its detail lines.
    private void CopyOverviewSelection()
    {
        if (_overviewTree.SelectedNode is not TreeNode selected)
            return;

        var builder = new System.Text.StringBuilder();
        AppendOverviewNodeText(builder, selected, depth: 0);
        SetClipboardText(builder.ToString().TrimEnd());
    }

    private void CopyOverviewAll()
    {
        if (_overviewTree.Nodes.Count == 0)
            return;

        var builder = new System.Text.StringBuilder();
        foreach (TreeNode root in _overviewTree.Nodes)
            AppendOverviewNodeText(builder, root, depth: 0);
        SetClipboardText(builder.ToString().TrimEnd());
    }

    private static void AppendOverviewNodeText(System.Text.StringBuilder builder, TreeNode node, int depth)
    {
        builder.Append(' ', depth * 4).AppendLine(node.Text);
        foreach (TreeNode child in node.Nodes)
            AppendOverviewNodeText(builder, child, depth + 1);
    }

}
