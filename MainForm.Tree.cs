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
    // The single unified stage-root label used by BOTH the strategy tree plan roots and the overview
    // section roots: "<stage>: elapsed=<s>.3f s, max steps=<n>, edges=<n>, output=<n>", optionally
    // suffixed with a marker (e.g. "no improvement"). When there is no plan the body collapses to the
    // marker note ("no solution" by default, or e.g. "search incomplete (candidate cap reached)").
    // elapsed is the stage's own wall time in seconds.
    private static string FormatStageRootLabel(string stageName, TimeSpan elapsed, StrategyPlan? plan, string? marker = null)
    {
        string elapsedText = $"elapsed={elapsed.TotalSeconds:F3} s";
        if (plan is null)
            return $"{stageName}: {elapsedText}, {marker ?? "no solution"}";
        string body = $"{stageName}: {elapsedText}, max steps={plan.MaxStep}, edges={plan.TotalBranchEdges}, states={plan.SearchStatistics.OutputStates}";
        return marker is null ? body : $"{body}, {marker}";
    }

    private static bool IsEdgeCompactStageName(string stageName)
        => stageName.StartsWith(StrategyBuilder.ExactEdgeCompactStagePrefix, StringComparison.Ordinal)
            || stageName.StartsWith(StrategyBuilder.GreedyEdgeCompactStagePrefix, StringComparison.Ordinal);

    private TreeNode CreatePlanTreeRoot(string stageName, StrategyPlan plan, string scope, TimeSpan elapsed)
    {
        var depthIndex = new LazyDepthIndex(plan.Root);
        var planNode = new TreeNode(FormatStageRootLabel(stageName, elapsed, plan))
        {
            Tag = new LazyNodeDetails(() => BuildPlanDetails(plan)),
            NodeFont = new Font(_treeView.Font, FontStyle.Bold),
            ForeColor = _palette.ForeColor,
        };
        TreeNode stateRoot = CreateStateNode(plan.Root, plan.K, scope, depthIndex);
        _jumpScopeRoots[scope] = stateRoot;
        _jumpScopeStrategyRoots[scope] = plan.Root;
        _indexedJumpScopes.Remove(scope);
        planNode.Nodes.Add(stateRoot);
        return planNode;
    }

    // A terminal stage that found no better strategy: a single bold leaf carrying the unified label with
    // the given marker ("no solution" when proven infeasible, "search incomplete (candidate cap reached)"
    // when the greedy cap truncated the enumeration) and no child strategy subtree.
    private TreeNode CreateNoSolutionTreeRoot(string stageName, TimeSpan elapsed, string? marker = null)
    {
        return new TreeNode(FormatStageRootLabel(stageName, elapsed, plan: null, marker))
        {
            NodeFont = new Font(_treeView.Font, FontStyle.Bold),
            ForeColor = _palette.MutedForeColor,
        };
    }

    // A stage that produced a valid strategy that does NOT strictly improve on the incumbent (e.g. the
    // compact baseline lands on the same max-step but more edges than greedy). It is recorded and marked
    // "no improvement" but, like a no-solution stage, shown only as a single leaf note -- the worse tree
    // is not drawn. Tightening still continues past it.
    private TreeNode CreateNoImprovementTreeRoot(string stageName, StrategyPlan plan, TimeSpan elapsed)
    {
        return new TreeNode(FormatStageRootLabel(stageName, elapsed, plan, "no improvement"))
        {
            NodeFont = new Font(_treeView.Font, FontStyle.Bold),
            ForeColor = _palette.MutedForeColor,
        };
    }

    private TreeNode CreateStateNode(StrategyNode node, int k, string scope, LazyDepthIndex depthIndex)
    {
        return node.Kind switch
        {
            StrategyNodeKind.Decision => CreateDecisionNode(node, k, scope, depthIndex),
            StrategyNodeKind.Terminal => CreateTerminalNode(node, k, scope),
            StrategyNodeKind.Reference => CreateReferenceNode(node, scope, depthIndex),
            _ => throw new InvalidOperationException("Unknown node kind"),
        };
    }

    private TreeNode CreateDecisionNode(StrategyNode node, int k, string scope, LazyDepthIndex depthIndex)
    {
        // Keep initial rendering cheap: avoid full depth-index construction here.
        string headerText = $"S{node.StateId} [step {node.Step}] sort({DisplayEngine.FormatSet(node.Group)})";
        if (node.FinalChoice is null)
            headerText += node.Branches.Count == 1 ? " (1 edge)" : $" ({node.Branches.Count} edges)";

        var treeNode = new TreeNode(headerText)
        {
            ForeColor = _palette.StateColor,
            Tag = BuildStateDetails(node),
        };
        // Keep the first (representative-spine) occurrence: the same canonical StateId can be fully
        // expanded at several positions with different relabelings, and the overview links by StateId
        // to the representative main line, which DFS visits (and inserts) first.
        _stateNodesByKey.TryAdd($"{scope}:{node.StateId}", treeNode);

        if (node.FinalChoice is not null)
        {
            treeNode.Nodes.Add(new TreeNode(BuildCompressedFinalChoiceText(node.FinalChoice, k))
            {
                ForeColor = _palette.ResultColor,
                Tag = BuildCompressedFinalChoiceDetails(node.FinalChoice, k),
            });
            return treeNode;
        }

        if (node.Branches.Count > 0)
        {
            // Defer the (potentially large) branch subtree: an empty placeholder gives the node its
            // expander, and MaterializeDecision builds the real branch children on demand.
            treeNode.Nodes.Add(new TreeNode());
            _lazyDecisions[treeNode] = new LazyDecision(node, k, scope, depthIndex);
        }

        return treeNode;
    }

    // Builds the immediate branch children of a lazily-deferred decision node. Idempotent and a no-op for
    // any node that is not a pending lazy decision (leaves, already-materialized nodes), so it is safe to
    // call from BeforeExpand, jump/copy path walks, and "expand all".
    private void MaterializeDecision(TreeNode treeNode)
    {
        if (!_lazyDecisions.TryGetValue(treeNode, out LazyDecision info))
            return;

        _lazyDecisions.Remove(treeNode);
        _treeView.BeginUpdate();
        treeNode.Nodes.Clear(); // drop the placeholder
        foreach (StrategyBranch branch in info.Node.Branches)
            treeNode.Nodes.Add(CreateBranchNode(branch, info.K, info.Scope, info.DepthIndex));
        _treeView.EndUpdate();
    }

    private TreeNode CreateBranchNode(StrategyBranch branch, int k, string scope, LazyDepthIndex depthIndex)
    {
        string branchHeader = branch.OrderText;
        if (branch.EquivalentOrders is not null)
            branchHeader += $"  (×{branch.EquivalentOrders.Count} = {branch.EquivalentOrders.CountFormula})";

        // Record which order-text tokens this outcome resolves so DrawNode can tint those "#n" tokens:
        // doomed items (newly excluded) in the exclusion color and secured items (newly guaranteed into
        // top-k) in the inclusion color. Restrict to labels that actually appear in the order text --
        // items resolved outside this branch's shown order have nothing to highlight.
        HashSet<int> orderLabels = ParseOrderLabels(branch.OrderText);
        Dictionary<int, bool>? colored = null;
        void MarkColored(IReadOnlyList<int> items, bool doomed)
        {
            foreach (int item in items)
            {
                int label = item + 1;
                if (orderLabels.Contains(label))
                    (colored ??= new Dictionary<int, bool>())[label] = doomed;
            }
        }

        MarkColored(branch.Effect.NewlyGuaranteedTop, doomed: false);
        MarkColored(branch.Effect.NewlyExcluded, doomed: true);

        var branchNode = new BranchTreeNode(branchHeader)
        {
            ForeColor = _palette.BranchColor,
            Tag = BuildBranchDetails(branch),
            ColoredTokens = colored,
        };

        if (branch.EquivalentOrders is not null)
        {
            // The count and its formula live in the branch header (×N = formula), so this child row
            // only carries the pattern/legend. The hover Tag still exposes the full two-line detail.
            branchNode.Nodes.Add(new TreeNode(DisplayEngine.FormatEquivalentPatternLine(branch.EquivalentOrders))
            {
                ForeColor = _palette.MutedForeColor,
                Tag = DisplayEngine.FormatEquivalentDetails(branch.EquivalentOrders),
            });
        }

        if (branch.Effect.NewlyGuaranteedTop.Count > 0)
        {
            branchNode.Nodes.Add(new TreeNode(DisplayEngine.FormatInEntry(branch.Effect.NewlyGuaranteedTop))
            {
                ForeColor = _palette.InColor,
                Tag = $"Newly confirmed in top-k: {DisplayEngine.FormatOptionalSet(branch.Effect.NewlyGuaranteedTop)}",
            });
        }

        if (branch.Effect.NewlyExcluded.Count > 0)
        {
            branchNode.Nodes.Add(new TreeNode(DisplayEngine.FormatOutEntry(branch.Effect.NewlyExcluded))
            {
                ForeColor = _palette.OutColor,
                Tag = $"Newly excluded from top-k: {DisplayEngine.FormatOptionalSet(branch.Effect.NewlyExcluded)}",
            });
        }

        if (branch.Effect.FixedCandidates.Count > 0)
        {
            branchNode.Nodes.Add(new TreeNode(DisplayEngine.FormatFixedEntry(branch.Effect.FixedCandidates))
            {
                ForeColor = _palette.FixedColor,
                Tag = $"Current fixed top-k candidates: {DisplayEngine.FormatOptionalSet(branch.Effect.FixedCandidates)}",
            });
        }

        if (branch.Effect.PossibleCandidates.Count > 0)
        {
            branchNode.Nodes.Add(new TreeNode(DisplayEngine.FormatPossibleEntry(branch.Effect.PossibleCandidates))
            {
                ForeColor = _palette.PossibleColor,
                Tag = $"Current possible top-k candidates: {DisplayEngine.FormatOptionalSet(branch.Effect.PossibleCandidates)}",
            });
        }

        // The next state node is always added LAST; the jump/copy path walks rely on that position.
        branchNode.Nodes.Add(CreateStateNode(branch.Next, k, scope, depthIndex));
        return branchNode;
    }

    // Parses the 1-based "#n" labels present in a branch's order text, which is always a " > "-joined
    // chain of "#n" tokens. Done once per branch so token lookups are O(1) set membership rather than a
    // repeated substring scan.
    private static HashSet<int> ParseOrderLabels(string orderText)
    {
        var labels = new HashSet<int>();
        int i = 0;
        while (i < orderText.Length)
        {
            if (orderText[i] == '#' && i + 1 < orderText.Length && char.IsDigit(orderText[i + 1]))
            {
                int j = i + 1;
                while (j < orderText.Length && char.IsDigit(orderText[j]))
                    j++;

                if (int.TryParse(orderText.AsSpan(i + 1, j - i - 1), out int label))
                    labels.Add(label);
                i = j;
                continue;
            }

            i++;
        }

        return labels;
    }

    // Owner-drawn text so a branch header's resolved "#n" tokens can be tinted: doomed items (newly
    // excluded) in the exclusion color and secured items (newly guaranteed into top-k) in the inclusion
    // color, while the rest keeps the branch color. Every node is drawn here (not just colored ones): the
    // system's default text draw in OwnerDrawText mode clips to a too-narrow bounds and truncates the tail
    // of long labels, so we render all text ourselves into a wide rectangle and paint the row background to
    // match, which avoids that truncation.
    private void TreeView_DrawNode(object? sender, DrawTreeNodeEventArgs e)
    {
        if (e.Node is not { } node || string.IsNullOrEmpty(node.Text) || e.Bounds.Height <= 0)
        {
            e.DrawDefault = true;
            return;
        }

        IReadOnlyDictionary<int, bool>? colored = (node as BranchTreeNode)?.ColoredTokens;

        // Fill the row from the label's left edge to the control's right edge (the +/- glyphs and lines
        // drawn by the system sit to the left of e.Bounds.Left, so they are preserved). Painting the full
        // width both erases stale pixels and gives selected rows a consistent highlight behind wide text.
        // Both brushes are shared (no per-paint allocation): the system highlight brush and a cached brush
        // recreated on theme change.
        bool selected = (e.State & TreeNodeStates.Selected) != 0;
        int rightEdge = Math.Max(e.Bounds.Right, _treeView.ClientSize.Width);
        var rowRect = new Rectangle(e.Bounds.Left, e.Bounds.Top, rightEdge - e.Bounds.Left, e.Bounds.Height);
        e.Graphics.FillRectangle(selected ? SystemBrushes.Highlight : _treeRowBackBrush, rowRect);

        Font font = node.NodeFont ?? _treeView.Font;
        Color baseColor = selected
            ? SystemColors.HighlightText
            : (node.ForeColor.IsEmpty ? _treeView.ForeColor : node.ForeColor);

        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding
            | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;

        IEnumerable<(string Text, bool? Doomed)> segments = colored is null
            ? new[] { (node.Text, (bool?)null) }
            : SplitColoredSegments(node.Text, colored);

        int x = e.Bounds.Left;
        foreach ((string text, bool? doomed) in segments)
        {
            Color color = doomed switch
            {
                true => _palette.OutColor,
                false => _palette.InColor,
                null => baseColor,
            };
            var origin = new Rectangle(x, e.Bounds.Top, int.MaxValue, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, text, font, origin, color, flags);
            x += TextRenderer.MeasureText(e.Graphics, text, font, Size.Empty, flags).Width;
        }
    }

    // Splits header text into runs, flagging each "#n" token whose label is in coloredLabels with its role
    // (true -> doomed/exclusion color, false -> secured/inclusion color). Non-token text and unresolved
    // tokens accumulate into plain runs (null role). Single pass with lazy yielding: a plain run is
    // emitted only when a colored token interrupts it or the text ends, so no intermediate list is built.
    private static IEnumerable<(string Text, bool? Doomed)> SplitColoredSegments(string text, IReadOnlyDictionary<int, bool> coloredLabels)
    {
        int plainStart = 0;
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '#' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                int j = i + 1;
                while (j < text.Length && char.IsDigit(text[j]))
                    j++;

                if (int.TryParse(text.AsSpan(i + 1, j - i - 1), out int label) && coloredLabels.TryGetValue(label, out bool doomed))
                {
                    if (i > plainStart)
                        yield return (text.Substring(plainStart, i - plainStart), null);

                    yield return (text.Substring(i, j - i), doomed);
                    i = j;
                    plainStart = j;
                    continue;
                }

                i = j;
                continue;
            }

            i++;
        }

        if (plainStart < text.Length)
            yield return (text.Substring(plainStart), null);
    }

    private TreeNode CreateTerminalNode(StrategyNode node, int k, string scope)
    {
        var treeNode = new TreeNode($"S{node.StateId}: top {k} = ({DisplayEngine.FormatSet(node.TopSet)})")
        {
            ForeColor = _palette.ResultColor,
            Tag = $"Result state S{node.StateId}\nTop {k} = ({DisplayEngine.FormatSet(node.TopSet)})",
        };
        _stateNodesByKey.TryAdd($"{scope}:{node.StateId}", treeNode);
        return treeNode;
    }

    private TreeNode CreateReferenceNode(StrategyNode node, string scope, LazyDepthIndex depthIndex)
    {
        string label = depthIndex.Value.TryGetReferenceRemaining(node.StateId, out int remaining)
            ? $"->S{node.StateId} {DisplayEngine.FormatRemainingSteps(remaining)}"
            : $"->S{node.StateId}";
        label += DisplayEngine.FormatRelabeling(node.ReferenceRelabeling);

        string tag = $"Reference to previously expanded state S{node.StateId}";
        if (node.ReferenceRelabeling.Count > 0)
        {
            string pairs = string.Join(", ",
                node.ReferenceRelabeling.Select(r => $"#{r.ReferencedItem + 1}->#{r.CurrentItem + 1}"));
            tag += $"\nMap S{node.StateId}'s item numbers to the current ones: {pairs}";
        }
        tag += $"\nDouble-click to jump to state S{node.StateId}.";

        var treeNode = new TreeNode(label)
        {
            ForeColor = _palette.ReferenceColor,
            Tag = tag,
        };
        _referenceTargets[treeNode] = $"{scope}:{node.StateId}";
        return treeNode;
    }

    private void TryJumpToReferenceTarget(TreeNode node)
    {
        if (!_referenceTargets.TryGetValue(node, out string? targetStateKey))
            return;

        if (ResolveStateNode(targetStateKey) is not TreeNode targetNode)
            return;

        _navigationHistory.Push(node);
        _backButton.Enabled = true;

        targetNode.EnsureVisible();
        _treeView.SelectedNode = targetNode;
        _treeView.Focus();
    }

    // Resolves a "scope:stateId" key to its tree node, materializing the path to it if the target's
    // subtree has not been expanded yet. Direct hits (already materialized, e.g. an ancestor was
    // expanded) return immediately; otherwise the recorded branch path from the plan's root state node
    // is walked, materializing each decision node along the way so the target node comes into existence.
    private TreeNode? ResolveStateNode(string key)
    {
        if (_stateNodesByKey.TryGetValue(key, out TreeNode? existing))
            return existing;

        int separator = key.IndexOf(':');
        if (separator > 0)
            EnsureJumpTargetsIndexed(key[..separator]);

        if (!_jumpTargets.TryGetValue(key, out JumpTarget target))
            return null;

        _treeView.BeginUpdate();
        TreeNode current = target.ScopeRoot;
        foreach (int branchIndex in target.BranchPath)
        {
            MaterializeDecision(current);
            if (branchIndex >= current.Nodes.Count)
            {
                _treeView.EndUpdate();
                return null;
            }

            TreeNode branchNode = current.Nodes[branchIndex];
            if (branchNode.Nodes.Count == 0)
            {
                _treeView.EndUpdate();
                return null;
            }

            // The next state node is always the branch node's last child (effect leaves precede it).
            current = branchNode.Nodes[branchNode.Nodes.Count - 1];
        }

        _treeView.EndUpdate();
        return current;
    }

    // Records, for every jumpable state (decision/terminal) in a plan, the branch-index path from the
    // plan's root state node down to it. Cheap: walks the StrategyNode tree only, allocating no tree
    // nodes. Mirrors the true-tree DFS order so the first-occurrence semantics match _stateNodesByKey.
    private void IndexJumpTargets(StrategyNode node, string scope, TreeNode scopeRoot, List<int> path)
    {
        if (node.Kind is StrategyNodeKind.Decision or StrategyNodeKind.Terminal)
            _jumpTargets.TryAdd($"{scope}:{node.StateId}", new JumpTarget(scopeRoot, path.ToArray()));

        if (node.Kind == StrategyNodeKind.Decision && node.FinalChoice is null)
        {
            for (int i = 0; i < node.Branches.Count; i++)
            {
                path.Add(i);
                IndexJumpTargets(node.Branches[i].Next, scope, scopeRoot, path);
                path.RemoveAt(path.Count - 1);
            }
        }
    }

    private void EnsureJumpTargetsIndexed(string scope)
    {
        if (_indexedJumpScopes.Contains(scope))
            return;

        if (!_jumpScopeRoots.TryGetValue(scope, out TreeNode? scopeRoot)
            || !_jumpScopeStrategyRoots.TryGetValue(scope, out StrategyNode? strategyRoot))
            return;

        IndexJumpTargets(strategyRoot, scope, scopeRoot, new List<int>());
        _indexedJumpScopes.Add(scope);
    }

    // Fully materializes every deferred decision node, then expands the whole tree. Used by the "expand
    // all" button, where the user has explicitly asked to see everything.
    private void ExpandEntireTree()
    {
        _treeView.BeginUpdate();
        while (_lazyDecisions.Count > 0)
        {
            foreach (TreeNode node in _lazyDecisions.Keys.ToList())
                MaterializeDecision(node);
        }

        _treeView.ExpandAll();
        _treeView.EndUpdate();
    }

    // Recursively materializes all deferred decision nodes under (and including) the given node, so a
    // subtree copy captures the full strategy rather than an unexpanded placeholder.
    private void MaterializeSubtree(TreeNode node)
    {
        MaterializeDecision(node);
        foreach (TreeNode child in node.Nodes)
            MaterializeSubtree(child);
    }

    private void NavigateBack()
    {
        if (_navigationHistory.Count == 0)
            return;

        TreeNode previous = _navigationHistory.Pop();
        _backButton.Enabled = _navigationHistory.Count > 0;

        previous.EnsureVisible();
        _treeView.SelectedNode = previous;
        _treeView.Focus();
    }

    private ContextMenuStrip CreateTreeContextMenu()
    {
        var menu = new ContextMenuStrip();

        var copyText = new ToolStripMenuItem("Copy text") { ShortcutKeyDisplayString = "Ctrl+C" };
        copyText.Click += (_, _) => CopySelectedNodeText();

        var copySubtree = new ToolStripMenuItem("Copy subtree");
        copySubtree.Click += (_, _) => CopySelectedNodeSubtree();

        menu.Items.Add(copyText);
        menu.Items.Add(copySubtree);

        menu.Opening += (_, e) =>
        {
            bool hasNode = _treeView.SelectedNode is not null;
            copyText.Enabled = hasNode;
            copySubtree.Enabled = hasNode;
            if (!hasNode)
                e.Cancel = true;
        };

        return menu;
    }

    private void TreeView_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            TreeNode? node = _treeView.GetNodeAt(e.X, e.Y);
            if (node is not null)
                _treeView.SelectedNode = node;
        }
    }

    private void TreeView_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C)
        {
            CopySelectedNodeText();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void CopySelectedNodeText()
    {
        if (_treeView.SelectedNode is { } node)
            SetClipboardText(node.Text);
    }

    private void CopySelectedNodeSubtree()
    {
        if (_treeView.SelectedNode is not { } node)
            return;

        MaterializeSubtree(node);
        var builder = new System.Text.StringBuilder();
        AppendNodeSubtree(node, 0, builder);
        SetClipboardText(builder.ToString().TrimEnd());
    }

    private static void AppendNodeSubtree(TreeNode node, int indent, System.Text.StringBuilder builder)
    {
        builder.Append(' ', indent * 2).AppendLine(node.Text);
        foreach (TreeNode child in node.Nodes)
            AppendNodeSubtree(child, indent + 1, builder);
    }

    private void SetClipboardText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        try
        {
            Clipboard.SetText(text);
            _statusLabel.Text = "Copied to clipboard.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Copy failed: {ex.Message}";
        }
    }

}
