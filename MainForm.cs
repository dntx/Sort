using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

class MainForm : Form
{
    private readonly TextBox _nTextBox;
    private readonly TextBox _mTextBox;
    private readonly TextBox _kTextBox;
    private readonly Button _runButton;
    private readonly Button _expandAllButton;
    private readonly Button _collapseAllButton;
    private readonly Label _summaryLabel;
    private readonly TreeView _treeView;
    private readonly RichTextBox _detailsTextBox;

    public MainForm()
    {
        Text = "Top-K Strategy Explorer";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1100, 760);
        Size = new Size(1200, 820);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 8),
        };

        _nTextBox = CreateInputTextBox("10");
        _mTextBox = CreateInputTextBox("3");
        _kTextBox = CreateInputTextBox("3");

        _runButton = new Button
        {
            Text = "Run",
            AutoSize = true,
            Height = 30,
            Margin = new Padding(12, 18, 0, 0),
        };
        _runButton.Click += (_, _) => RunStrategy();

        _expandAllButton = new Button
        {
            Text = "Expand All",
            AutoSize = true,
            Height = 30,
            Margin = new Padding(12, 18, 0, 0),
        };

        _collapseAllButton = new Button
        {
            Text = "Collapse All",
            AutoSize = true,
            Height = 30,
            Margin = new Padding(8, 18, 0, 0),
        };

        toolbar.Controls.Add(CreateLabeledInput("n", _nTextBox));
        toolbar.Controls.Add(CreateLabeledInput("m", _mTextBox));
        toolbar.Controls.Add(CreateLabeledInput("k", _kTextBox));
        toolbar.Controls.Add(_runButton);
        toolbar.Controls.Add(_expandAllButton);
        toolbar.Controls.Add(_collapseAllButton);

        _summaryLabel = new Label
        {
            AutoSize = true,
            Text = "Colors: state = blue, branch = black, in = green, out = red, cand = orange, result = dark green, goto = purple.",
            Margin = new Padding(0, 0, 0, 8),
        };

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 720,
        };

        _treeView = new TreeView
        {
            Dock = DockStyle.Fill,
            HideSelection = false,
            FullRowSelect = true,
            Font = new Font(FontFamily.GenericSansSerif, 10),
        };
        _treeView.AfterSelect += (_, e) => ShowNodeDetails(e.Node);
        _expandAllButton.Click += (_, _) => _treeView.ExpandAll();
        _collapseAllButton.Click += (_, _) => _treeView.CollapseAll();

        _detailsTextBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = new Font(FontFamily.GenericMonospace, 10),
            BackColor = SystemColors.Window,
        };

        split.Panel1.Controls.Add(_treeView);
        split.Panel2.Controls.Add(_detailsTextBox);

        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(_summaryLabel, 0, 1);
        layout.Controls.Add(split, 0, 2);

        Controls.Add(layout);
        AcceptButton = _runButton;
    }

    private static Control CreateLabeledInput(string labelText, TextBox textBox)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 12, 0),
        };

        panel.Controls.Add(new Label
        {
            Text = labelText,
            AutoSize = true,
            Margin = new Padding(0, 8, 6, 0),
        });
        panel.Controls.Add(textBox);
        return panel;
    }

    private static TextBox CreateInputTextBox(string initialValue)
    {
        return new TextBox
        {
            Width = 80,
            Text = initialValue,
            Margin = new Padding(0, 4, 0, 0),
        };
    }

    private void RunStrategy()
    {
        if (!Program.TryParseAndValidate(_nTextBox.Text, _mTextBox.Text, _kTextBox.Text, out int n, out int m, out int k, out string? error))
        {
            MessageBox.Show(this, error, "Invalid input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        UseWaitCursor = true;
        _runButton.Enabled = false;
        _expandAllButton.Enabled = false;
        _collapseAllButton.Enabled = false;

        try
        {
            var plan = StrategyBuilder.Generate(n, m, k);
            PopulateTree(plan);
            _summaryLabel.Text = $"n={n}, m={m}, k={k}. Colors: state = blue, branch = black, in = green, out = red, cand = orange, result = dark green, goto = purple.";
        }
        finally
        {
            _runButton.Enabled = true;
            _expandAllButton.Enabled = true;
            _collapseAllButton.Enabled = true;
            UseWaitCursor = false;
        }
    }

    private void PopulateTree(StrategyPlan plan)
    {
        _treeView.BeginUpdate();
        _treeView.Nodes.Clear();

        var root = new TreeNode($"n={plan.N}, m={plan.M}, k={plan.K}")
        {
            Tag = StrategyTextRenderer.Render(plan).TrimEnd(),
            NodeFont = new Font(_treeView.Font, FontStyle.Bold),
        };
        root.Nodes.Add(CreateStateNode(plan.Root, plan.K));
        _treeView.Nodes.Add(root);
        root.Expand();
        root.Nodes[0].Expand();

        _treeView.EndUpdate();
        _treeView.SelectedNode = root;
    }

    private TreeNode CreateStateNode(StrategyNode node, int k)
    {
        return node.Kind switch
        {
            StrategyNodeKind.Decision => CreateDecisionNode(node, k),
            StrategyNodeKind.Terminal => CreateTerminalNode(node, k),
            StrategyNodeKind.Reference => CreateReferenceNode(node),
            _ => throw new InvalidOperationException("Unknown node kind"),
        };
    }

    private TreeNode CreateDecisionNode(StrategyNode node, int k)
    {
        var treeNode = new TreeNode($"S{node.StateId} [step {node.Step}] sort({StrategyTextRenderer.FormatSet(node.Group)})")
        {
            ForeColor = Color.MidnightBlue,
            Tag = $"State S{node.StateId}\nStep: {node.Step}\nComparison group: ({StrategyTextRenderer.FormatSet(node.Group)})",
        };

        foreach (var branch in node.Branches)
        {
            var branchNode = new TreeNode(branch.OrderText)
            {
                ForeColor = Color.Black,
                Tag = BuildBranchDetails(branch),
            };

            branchNode.Nodes.Add(new TreeNode($"in {StrategyTextRenderer.FormatOptionalSet(branch.Effect.NewlyGuaranteedTop)}")
            {
                ForeColor = Color.ForestGreen,
                Tag = $"Newly confirmed in top-k: {StrategyTextRenderer.FormatOptionalSet(branch.Effect.NewlyGuaranteedTop)}",
            });

            branchNode.Nodes.Add(new TreeNode($"out {StrategyTextRenderer.FormatOptionalSet(branch.Effect.NewlyExcluded)}")
            {
                ForeColor = Color.Crimson,
                Tag = $"Newly excluded from top-k: {StrategyTextRenderer.FormatOptionalSet(branch.Effect.NewlyExcluded)}",
            });

            branchNode.Nodes.Add(new TreeNode($"cand ({StrategyTextRenderer.FormatSet(branch.Effect.Candidates)})")
            {
                ForeColor = Color.DarkOrange,
                Tag = $"Current candidates: ({StrategyTextRenderer.FormatSet(branch.Effect.Candidates)})",
            });

            branchNode.Nodes.Add(CreateStateNode(branch.Next, k));
            treeNode.Nodes.Add(branchNode);
        }

        return treeNode;
    }

    private TreeNode CreateTerminalNode(StrategyNode node, int k)
    {
        return new TreeNode($"S{node.StateId}: top {k} = ({StrategyTextRenderer.FormatSet(node.TopSet)})")
        {
            ForeColor = Color.DarkGreen,
            Tag = $"Result state S{node.StateId}\nTop {k} = ({StrategyTextRenderer.FormatSet(node.TopSet)})",
        };
    }

    private TreeNode CreateReferenceNode(StrategyNode node)
    {
        return new TreeNode($"→S{node.StateId}")
        {
            ForeColor = Color.Purple,
            Tag = $"Reference to previously expanded state S{node.StateId}",
        };
    }

    private static string BuildBranchDetails(StrategyBranch branch)
    {
        return $"{branch.OrderText}\n" +
            $"in  {StrategyTextRenderer.FormatOptionalSet(branch.Effect.NewlyGuaranteedTop)}\n" +
            $"out {StrategyTextRenderer.FormatOptionalSet(branch.Effect.NewlyExcluded)}\n" +
            $"cand ({StrategyTextRenderer.FormatSet(branch.Effect.Candidates)})";
    }

    private void ShowNodeDetails(TreeNode? node)
    {
        _detailsTextBox.Clear();
        if (node?.Tag is not string text)
            return;

        _detailsTextBox.Text = text;
    }
}
