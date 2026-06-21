using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    };

    private readonly TextBox _nTextBox;
    private readonly TextBox _mTextBox;
    private readonly TextBox _kTextBox;
    private readonly ComboBox _themeComboBox;
    private readonly Button _runButton;
    private readonly Button _stopButton;
    private readonly Button _expandAllButton;
    private readonly Button _collapseAllButton;
    private readonly Label _elapsedLabel;
    private readonly Label _summaryLabel;
    private readonly TreeView _treeView;
    private readonly RichTextBox _detailsTextBox;
    private readonly System.Windows.Forms.Timer _elapsedTimer;
    private ThemePalette _palette = DarkPalette;
    private StrategyPlan? _currentPlan;
    private Stopwatch? _runStopwatch;
    private CancellationTokenSource? _runCancellationSource;

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

        _stopButton = new Button
        {
            Text = "Stop",
            AutoSize = true,
            Height = 30,
            Margin = new Padding(8, 18, 0, 0),
            Enabled = false,
        };
        _stopButton.Click += (_, _) => StopStrategy();

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

        _elapsedLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(12, 24, 0, 0),
            Text = "elapsed: 0.0 s",
        };

        toolbar.Controls.Add(CreateLabeledInput("n", _nTextBox));
        toolbar.Controls.Add(CreateLabeledInput("m", _mTextBox));
        toolbar.Controls.Add(CreateLabeledInput("k", _kTextBox));
        toolbar.Controls.Add(CreateLabeledInput("theme", _themeComboBox));
        toolbar.Controls.Add(_runButton);
        toolbar.Controls.Add(_stopButton);
        toolbar.Controls.Add(_expandAllButton);
        toolbar.Controls.Add(_collapseAllButton);
        toolbar.Controls.Add(_elapsedLabel);

        _summaryLabel = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
            Text = $"Ready. theme={ParseSelectedTheme()}",
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
        _elapsedTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _elapsedTimer.Tick += (_, _) => UpdateElapsedLabel();
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

    private async void RunStrategy()
    {
        if (!Program.TryParseAndValidate(_nTextBox.Text, _mTextBox.Text, _kTextBox.Text, out int n, out int m, out int k, out string? error))
        {
            MessageBox.Show(this, error, "Invalid input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _runCancellationSource?.Dispose();
        _runCancellationSource = new CancellationTokenSource();
        CancellationToken cancellationToken = _runCancellationSource.Token;
        _runStopwatch = Stopwatch.StartNew();
        UpdateElapsedLabel();
        _elapsedTimer.Start();
        SetRunningState(isRunning: true);
        _summaryLabel.Text = $"Running n={n}, m={m}, k={k}... theme={ParseSelectedTheme()}";

        try
        {
            var plan = await Task.Run(() => StrategyBuilder.Generate(n, m, k, cancellationToken), cancellationToken);
            _runStopwatch?.Stop();
            _currentPlan = plan;
            PopulateTree(plan);
            UpdateSummaryText(plan);
        }
        catch (OperationCanceledException)
        {
            _runStopwatch?.Stop();
            _summaryLabel.Text = $"Stopped after {GetElapsedSeconds():F1} s. theme={ParseSelectedTheme()}";
        }
        catch (Exception ex)
        {
            _runStopwatch?.Stop();
            _summaryLabel.Text = $"Run failed after {GetElapsedSeconds():F1} s. theme={ParseSelectedTheme()}";
            MessageBox.Show(this, ex.Message, "Run failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _elapsedTimer.Stop();
            UpdateElapsedLabel();
            SetRunningState(isRunning: false);
            _runCancellationSource?.Dispose();
            _runCancellationSource = null;
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

        if (node.FinalChoice is not null)
        {
            treeNode.Nodes.Add(new TreeNode(BuildCompressedFinalChoiceText(node.FinalChoice, k))
            {
                ForeColor = _palette.ResultColor,
                Tag = BuildCompressedFinalChoiceDetails(node.FinalChoice, k),
            });
            return treeNode;
        }

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

        if (node.FinalChoice is not null)
        {
            int k = node.FinalChoice.FixedTopSet.Count + node.FinalChoice.RemainingSlots;
            details += "\n" +
                "Compressed final choice: yes\n" +
                BuildCompressedFinalChoiceDetails(node.FinalChoice, k);
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
            if (_runCancellationSource is null)
                UpdateSummaryText(_currentPlan);
        }
        else if (_runCancellationSource is null)
        {
            _summaryLabel.Text = $"Ready. theme={theme}";
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

    private static string BuildCompressedFinalChoiceText(FinalChoiceSummary summary, int k)
    {
        return $"fixed ({StrategyTextRenderer.FormatSet(summary.FixedTopSet)}); choose {summary.RemainingSlots} of ({StrategyTextRenderer.FormatSet(summary.CandidatePool)}) into top {k}";
    }

    private static string BuildCompressedFinalChoiceDetails(FinalChoiceSummary summary, int k)
    {
        return
            $"Fixed top-{k} members: ({StrategyTextRenderer.FormatSet(summary.FixedTopSet)})\n" +
            $"Choose {summary.RemainingSlots} of ({StrategyTextRenderer.FormatSet(summary.CandidatePool)}) to complete top {k}";
    }

    private void StopStrategy()
    {
        _stopButton.Enabled = false;
        _runCancellationSource?.Cancel();
    }

    private void SetRunningState(bool isRunning)
    {
        UseWaitCursor = isRunning;
        _runButton.Enabled = !isRunning;
        _stopButton.Enabled = isRunning;
        _expandAllButton.Enabled = !isRunning;
        _collapseAllButton.Enabled = !isRunning;
    }

    private void UpdateElapsedLabel()
    {
        _elapsedLabel.Text = $"elapsed: {GetElapsedSeconds():F1} s";
    }

    private double GetElapsedSeconds()
    {
        return _runStopwatch?.Elapsed.TotalSeconds ?? 0;
    }

    private void ShowNodeDetails(TreeNode? node)
    {
        _detailsTextBox.Clear();
        if (node?.Tag is not string text)
            return;

        _detailsTextBox.Text = text;
    }
}
