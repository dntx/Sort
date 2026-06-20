using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

class MainForm : Form
{
    private enum ColorTheme
    {
        Dark,
        Light,
    }

    private sealed class ThemePalette
    {
        public required Color FormBackColor { get; init; }
        public required Color SurfaceBackColor { get; init; }
        public required Color InputBackColor { get; init; }
        public required Color ForeColor { get; init; }
        public required Color MutedForeColor { get; init; }
        public required Color BorderColor { get; init; }
        public required Color StateColor { get; init; }
        public required Color BranchColor { get; init; }
        public required Color InColor { get; init; }
        public required Color OutColor { get; init; }
        public required Color FixedColor { get; init; }
        public required Color PossibleColor { get; init; }
        public required Color ResultColor { get; init; }
        public required Color ReferenceColor { get; init; }
        public required Color OmittedColor { get; init; }
    }

    private static readonly ThemePalette DarkPalette = new()
    {
        FormBackColor = Color.FromArgb(18, 18, 18),
        SurfaceBackColor = Color.FromArgb(24, 24, 24),
        InputBackColor = Color.FromArgb(32, 32, 32),
        ForeColor = Color.White,
        MutedForeColor = Color.Gainsboro,
        BorderColor = Color.FromArgb(70, 70, 70),
        StateColor = Color.LightSkyBlue,
        BranchColor = Color.White,
        InColor = Color.LightGreen,
        OutColor = Color.LightCoral,
        FixedColor = Color.Gold,
        PossibleColor = Color.Khaki,
        ResultColor = Color.MediumSpringGreen,
        ReferenceColor = Color.Plum,
        OmittedColor = Color.Silver,
    };

    private static readonly ThemePalette LightPalette = new()
    {
        FormBackColor = SystemColors.Control,
        SurfaceBackColor = SystemColors.Window,
        InputBackColor = SystemColors.Window,
        ForeColor = SystemColors.ControlText,
        MutedForeColor = Color.DimGray,
        BorderColor = SystemColors.ControlDark,
        StateColor = Color.MidnightBlue,
        BranchColor = Color.Black,
        InColor = Color.ForestGreen,
        OutColor = Color.Crimson,
        FixedColor = Color.DarkOrange,
        PossibleColor = Color.Peru,
        ResultColor = Color.DarkGreen,
        ReferenceColor = Color.Purple,
        OmittedColor = Color.Gray,
    };

    private readonly TextBox _nTextBox;
    private readonly TextBox _mTextBox;
    private readonly TextBox _kTextBox;
    private readonly ComboBox _themeComboBox;
    private readonly Button _runButton;
    private readonly Button _expandAllButton;
    private readonly Button _collapseAllButton;
    private readonly Label _summaryLabel;
    private readonly TreeView _treeView;
    private readonly RichTextBox _detailsTextBox;
    private ThemePalette _palette = DarkPalette;
    private StrategyPlan? _currentPlan;

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

        _nTextBox = CreateInputTextBox("4");
        _mTextBox = CreateInputTextBox("3");
        _kTextBox = CreateInputTextBox("3");
        _themeComboBox = new ComboBox
        {
            Width = 140,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0, 4, 0, 0),
        };
        _themeComboBox.Items.AddRange(Enum.GetNames<ColorTheme>());
        _themeComboBox.SelectedItem = ColorTheme.Dark.ToString();
        _themeComboBox.SelectedIndexChanged += (_, _) => ApplyTheme(ParseSelectedTheme());

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
        toolbar.Controls.Add(CreateLabeledInput("theme", _themeComboBox));
        toolbar.Controls.Add(_runButton);
        toolbar.Controls.Add(_expandAllButton);
        toolbar.Controls.Add(_collapseAllButton);

        _summaryLabel = new Label
        {
            AutoSize = true,
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
        };

        split.Panel1.Controls.Add(_treeView);
        split.Panel2.Controls.Add(_detailsTextBox);

        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(_summaryLabel, 0, 1);
        layout.Controls.Add(split, 0, 2);

        Controls.Add(layout);
        AcceptButton = _runButton;
        ApplyTheme(ColorTheme.Dark);
    }

    private static Control CreateLabeledInput(string labelText, Control inputControl)
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
        panel.Controls.Add(inputControl);
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
            _currentPlan = plan;
            PopulateTree(plan);
            UpdateSummaryText(plan);
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

        var root = new TreeNode($"n={plan.N}, m={plan.M}, k={plan.K}, elapsed={plan.Elapsed.TotalMilliseconds:F1} ms, max step={plan.MaxStep}")
        {
            Tag = StrategyTextRenderer.Render(plan).TrimEnd(),
            NodeFont = new Font(_treeView.Font, FontStyle.Bold),
            ForeColor = _palette.ForeColor,
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
            ForeColor = _palette.StateColor,
            Tag = BuildStateDetails(node),
        };

        foreach (var branch in node.Branches)
        {
            var branchNode = new TreeNode(branch.OrderText)
            {
                ForeColor = _palette.BranchColor,
                Tag = BuildBranchDetails(branch),
            };

            if (branch.EquivalentOrderTexts.Count > 0)
            {
                string equivalentText = branch.EquivalentOrderTexts.Count == 1
                    ? $"equivalent form {branch.EquivalentOrderTexts[0]}"
                    : $"equivalent forms {branch.EquivalentOrderTexts.Count}";

                branchNode.Nodes.Add(new TreeNode(equivalentText)
                {
                    ForeColor = _palette.MutedForeColor,
                    Tag = $"Equivalent merged branch forms: {string.Join("; ", branch.EquivalentOrderTexts)}",
                });
            }

            branchNode.Nodes.Add(new TreeNode($"in {StrategyTextRenderer.FormatOptionalSet(branch.Effect.NewlyGuaranteedTop)}")
            {
                ForeColor = _palette.InColor,
                Tag = $"Newly confirmed in top-k: {StrategyTextRenderer.FormatOptionalSet(branch.Effect.NewlyGuaranteedTop)}",
            });

            branchNode.Nodes.Add(new TreeNode($"out {StrategyTextRenderer.FormatOptionalSet(branch.Effect.NewlyExcluded)}")
            {
                ForeColor = _palette.OutColor,
                Tag = $"Newly excluded from top-k: {StrategyTextRenderer.FormatOptionalSet(branch.Effect.NewlyExcluded)}",
            });

            branchNode.Nodes.Add(new TreeNode($"cand fixed {StrategyTextRenderer.FormatOptionalSet(branch.Effect.FixedCandidates)}")
            {
                ForeColor = _palette.FixedColor,
                Tag = $"Current fixed top-k candidates: {StrategyTextRenderer.FormatOptionalSet(branch.Effect.FixedCandidates)}",
            });

            branchNode.Nodes.Add(new TreeNode($"cand possible {StrategyTextRenderer.FormatOptionalSet(branch.Effect.PossibleCandidates)}")
            {
                ForeColor = _palette.PossibleColor,
                Tag = $"Current possible top-k candidates: {StrategyTextRenderer.FormatOptionalSet(branch.Effect.PossibleCandidates)}",
            });

            branchNode.Nodes.Add(CreateStateNode(branch.Next, k));
            treeNode.Nodes.Add(branchNode);
        }

        if (node.IsCompressedFinalComparison && node.OmittedBranchCount > 0)
        {
            treeNode.Nodes.Add(new TreeNode($"... {node.OmittedBranchCount} other final outcome(s) omitted; analogous")
            {
                ForeColor = _palette.OmittedColor,
                Tag = "This is the last-step possible-candidate comparison. Only one representative outcome is shown; the omitted outcomes can be derived analogously.",
            });
        }

        return treeNode;
    }

    private TreeNode CreateTerminalNode(StrategyNode node, int k)
    {
        return new TreeNode($"S{node.StateId}: top {k} = ({StrategyTextRenderer.FormatSet(node.TopSet)})")
        {
            ForeColor = _palette.ResultColor,
            Tag = $"Result state S{node.StateId}\nTop {k} = ({StrategyTextRenderer.FormatSet(node.TopSet)})",
        };
    }

    private TreeNode CreateReferenceNode(StrategyNode node)
    {
        return new TreeNode($"->S{node.StateId}")
        {
            ForeColor = _palette.ReferenceColor,
            Tag = $"Reference to previously expanded state S{node.StateId}",
        };
    }

    private static string BuildBranchDetails(StrategyBranch branch)
    {
        string details = $"{branch.OrderText}\n" +
            $"in  {StrategyTextRenderer.FormatOptionalSet(branch.Effect.NewlyGuaranteedTop)}\n" +
            $"out {StrategyTextRenderer.FormatOptionalSet(branch.Effect.NewlyExcluded)}\n" +
            $"cand fixed    {StrategyTextRenderer.FormatOptionalSet(branch.Effect.FixedCandidates)}\n" +
            $"cand possible {StrategyTextRenderer.FormatOptionalSet(branch.Effect.PossibleCandidates)}";

        if (branch.EquivalentOrderTexts.Count > 0)
            details += $"\nmerged equivalent forms: {string.Join("; ", branch.EquivalentOrderTexts)}";

        return details;
    }

    private static string BuildStateDetails(StrategyNode node)
    {
        string details =
            $"State S{node.StateId}\n" +
            $"Step: {node.Step}\n" +
            $"Comparison group: ({StrategyTextRenderer.FormatSet(node.Group)})";

        if (node.IsCompressedFinalComparison && node.OmittedBranchCount > 0)
        {
            details += "\n" +
                $"Compressed final comparison: yes\n" +
                $"Omitted analogous outcomes: {node.OmittedBranchCount}";
        }

        return details;
    }

    private ColorTheme ParseSelectedTheme()
    {
        return Enum.TryParse<ColorTheme>(_themeComboBox.SelectedItem?.ToString(), out var theme)
            ? theme
            : ColorTheme.Dark;
    }

    private void ApplyTheme(ColorTheme theme)
    {
        _palette = theme == ColorTheme.Dark ? DarkPalette : LightPalette;
        BackColor = _palette.FormBackColor;
        ForeColor = _palette.ForeColor;
        ApplyThemeToControlTree(this);

        if (_currentPlan is not null)
        {
            PopulateTree(_currentPlan);
            UpdateSummaryText(_currentPlan);
        }
        else
        {
            _summaryLabel.Text = $"theme={theme}. Colors: state, branch, in, out, cand fixed, cand possible, result, goto.";
        }
    }

    private void ApplyThemeToControlTree(Control control)
    {
        switch (control)
        {
            case TreeView treeView:
                treeView.BackColor = _palette.SurfaceBackColor;
                treeView.ForeColor = _palette.ForeColor;
                treeView.LineColor = _palette.BorderColor;
                break;
            case RichTextBox richTextBox:
                richTextBox.BackColor = _palette.SurfaceBackColor;
                richTextBox.ForeColor = _palette.ForeColor;
                break;
            case TextBox textBox:
                textBox.BackColor = _palette.InputBackColor;
                textBox.ForeColor = _palette.ForeColor;
                textBox.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox comboBox:
                comboBox.BackColor = _palette.InputBackColor;
                comboBox.ForeColor = _palette.ForeColor;
                comboBox.FlatStyle = FlatStyle.Flat;
                break;
            case Button button:
                button.UseVisualStyleBackColor = false;
                button.BackColor = _palette.InputBackColor;
                button.ForeColor = _palette.ForeColor;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = _palette.BorderColor;
                break;
            case Label label:
                label.ForeColor = _palette.ForeColor;
                label.BackColor = Color.Transparent;
                break;
            default:
                control.BackColor = _palette.FormBackColor;
                control.ForeColor = _palette.ForeColor;
                break;
        }

        if (control is SplitContainer splitContainer)
        {
            splitContainer.Panel1.BackColor = _palette.FormBackColor;
            splitContainer.Panel1.ForeColor = _palette.ForeColor;
            splitContainer.Panel2.BackColor = _palette.FormBackColor;
            splitContainer.Panel2.ForeColor = _palette.ForeColor;
        }

        foreach (Control child in control.Controls)
            ApplyThemeToControlTree(child);
    }

    private void UpdateSummaryText(StrategyPlan plan)
    {
        _summaryLabel.Text = $"n={plan.N}, m={plan.M}, k={plan.K}, elapsed={plan.Elapsed.TotalMilliseconds:F1} ms, max step={plan.MaxStep}, theme={ParseSelectedTheme()}. Colors: state, branch, in, out, cand fixed, cand possible, result, goto.";
    }

    private void ShowNodeDetails(TreeNode? node)
    {
        _detailsTextBox.Clear();
        if (node?.Tag is not string text)
            return;

        _detailsTextBox.Text = text;
    }
}
