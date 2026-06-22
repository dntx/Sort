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
    private readonly Label _searchStatsLabel;
    private readonly Label _diagnosticsLabel;
    private readonly Label _summaryLabel;
    private readonly TreeView _treeView;
    private readonly RichTextBox _detailsTextBox;
    private readonly System.Windows.Forms.Timer _elapsedTimer;
    private ThemePalette _palette = DarkPalette;
    private StrategyPlan? _currentPlan;
    private Stopwatch? _runStopwatch;
    private CancellationTokenSource? _runCancellationSource;
    private SearchProgressSnapshot _latestProgress;

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
            RowCount = 2,
            Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var headerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 3,
            Margin = new Padding(0, 0, 0, 12),
        };
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

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
            Margin = new Padding(0, 4, 8, 0),
        };
        _runButton.Click += (_, _) => RunStrategy();

        _stopButton = new Button
        {
            Text = "Stop",
            AutoSize = true,
            Height = 30,
            Margin = new Padding(0, 4, 8, 0),
            Enabled = false,
        };
        _stopButton.Click += (_, _) => StopStrategy();

        _expandAllButton = new Button
        {
            Text = "Expand All",
            AutoSize = true,
            Height = 30,
            Margin = new Padding(0, 4, 8, 0),
        };

        _collapseAllButton = new Button
        {
            Text = "Collapse All",
            AutoSize = true,
            Height = 30,
            Margin = new Padding(0, 4, 0, 0),
        };

        _elapsedLabel = new Label
        {
            AutoSize = true,
            Margin = Padding.Empty,
            Text = "0.0 s",
            Font = new Font(Font.FontFamily, 14, FontStyle.Bold),
        };
        _searchStatsLabel = new Label
        {
            AutoSize = true,
            Margin = Padding.Empty,
            Text = "searched: 0\npending: 0\npeak: 0\noutput: 0",
        };
        _diagnosticsLabel = new Label
        {
            AutoSize = true,
            Margin = Padding.Empty,
            Text = "incumbent: -\nmilestones: 0\nprunes: 0\ncache: 0/0/0/0",
        };

        var inputsPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        inputsPanel.Controls.Add(CreateLabeledInput("n", _nTextBox));
        inputsPanel.Controls.Add(CreateLabeledInput("m", _mTextBox));
        inputsPanel.Controls.Add(CreateLabeledInput("k", _kTextBox));
        inputsPanel.Controls.Add(CreateLabeledInput("theme", _themeComboBox));

        var actionsPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        actionsPanel.Controls.Add(_runButton);
        actionsPanel.Controls.Add(_stopButton);
        actionsPanel.Controls.Add(_expandAllButton);
        actionsPanel.Controls.Add(_collapseAllButton);

        var controlsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = Padding.Empty,
        };
        controlsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        controlsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        controlsLayout.Controls.Add(CreateSectionPanel("Inputs", inputsPanel), 0, 0);
        controlsLayout.Controls.Add(CreateSectionPanel("Actions", actionsPanel), 1, 0);

        var statsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            Margin = Padding.Empty,
        };
        statsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        statsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        statsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        statsLayout.Controls.Add(CreateSectionPanel("Elapsed", _elapsedLabel), 0, 0);
        statsLayout.Controls.Add(CreateSectionPanel("Search", _searchStatsLabel), 1, 0);
        statsLayout.Controls.Add(CreateSectionPanel("Diagnostics", _diagnosticsLabel), 2, 0);

        _summaryLabel = new Label
        {
            AutoSize = true,
            Margin = Padding.Empty,
            Text = "Ready.",
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

        headerLayout.Controls.Add(controlsLayout, 0, 0);
        headerLayout.Controls.Add(statsLayout, 0, 1);
        headerLayout.Controls.Add(CreateSectionPanel("Status", _summaryLabel), 0, 2);

        layout.Controls.Add(headerLayout, 0, 0);
        layout.Controls.Add(split, 0, 1);

        Controls.Add(layout);
        AcceptButton = _runButton;
        _elapsedTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _elapsedTimer.Tick += (_, _) => UpdateElapsedLabel();
        _detailsTextBox.Text = BuildIdleDetailsText();
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

    private static Panel CreateSectionPanel(string title, Control body)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 8, 8),
        };

        var sectionLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
        };
        sectionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sectionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sectionLayout.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Margin = Padding.Empty,
            Font = new Font(FontFamily.GenericSansSerif, 9f, FontStyle.Bold),
        }, 0, 0);

        body.Margin = new Padding(0, 8, 0, 0);
        sectionLayout.Controls.Add(body, 0, 1);
        panel.Controls.Add(sectionLayout);
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
        IProgress<SearchProgressSnapshot> progress = new Progress<SearchProgressSnapshot>(UpdateSearchProgress);
        _latestProgress = CreateInitialProgressSnapshot();
        _runStopwatch = Stopwatch.StartNew();
        UpdateElapsedLabel();
        UpdateSearchStatsLabel();
        UpdateDiagnosticsLabel();
        _elapsedTimer.Start();
        SetRunningState(isRunning: true);
        _summaryLabel.Text = $"Running n={n}, m={m}, k={k}...";
        _detailsTextBox.Text = BuildLiveDiagnosticsText(_latestProgress);

        try
        {
            var plan = await Task.Run(() => StrategyBuilder.Generate(n, m, k, cancellationToken, snapshot => progress.Report(snapshot)), cancellationToken);
            _runStopwatch?.Stop();
            _currentPlan = plan;
            _latestProgress = new SearchProgressSnapshot(
                (long)plan.Elapsed.TotalMilliseconds,
                plan.SearchStatistics.SearchedStates,
                plan.SearchStatistics.PendingStates,
                plan.SearchStatistics.PeakPendingStates,
                plan.SearchStatistics.OutputStates,
                plan.SearchStatistics.Diagnostics.RootIncumbents.LastOrDefault(),
                plan.SearchStatistics.Diagnostics.RootIncumbents.Count,
                plan.SearchStatistics.Diagnostics.LowerBoundPrunes,
                plan.SearchStatistics.Diagnostics.DuplicateOutcomeSkips,
                plan.SearchStatistics.Diagnostics.MergedOutcomeCollisions,
                plan.SearchStatistics.Diagnostics.ExactCacheHits,
                plan.SearchStatistics.Diagnostics.LowerBoundCacheHits,
                plan.SearchStatistics.Diagnostics.FeasibleTopSetCacheHits,
                plan.SearchStatistics.Diagnostics.BestGroupPatternCacheHits);
            PopulateTree(plan);
            UpdateSummaryText(plan);
            UpdateSearchStatsLabel();
            UpdateDiagnosticsLabel();
        }
        catch (OperationCanceledException)
        {
            _runStopwatch?.Stop();
            _summaryLabel.Text = $"Stopped after {GetElapsedSeconds():F1} s. {FormatSearchStatsSummary(_latestProgress, includeOutputStates: true)}. {FormatLiveDiagnosticsSummary(_latestProgress)}.";
            _detailsTextBox.Text = BuildLiveDiagnosticsText(_latestProgress);
        }
        catch (Exception ex)
        {
            _runStopwatch?.Stop();
            _summaryLabel.Text = $"Run failed after {GetElapsedSeconds():F1} s. {FormatSearchStatsSummary(_latestProgress, includeOutputStates: true)}. {FormatLiveDiagnosticsSummary(_latestProgress)}.";
            _detailsTextBox.Text = BuildLiveDiagnosticsText(_latestProgress);
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

        var root = new TreeNode(
            $"n={plan.N}, m={plan.M}, k={plan.K}, elapsed={plan.Elapsed.TotalMilliseconds:F1} ms, max step={plan.MaxStep}, searched={plan.SearchStatistics.SearchedStates}, peak pending={plan.SearchStatistics.PeakPendingStates}")
        {
            Tag = BuildPlanDetails(plan),
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

            if (branch.EquivalentOrders is not null)
            {
                string equivalentText = StrategyTextRenderer.FormatEquivalentFormsSummary(branch.EquivalentOrders);
                string patternText = StrategyTextRenderer.FormatEquivalentPatternLine(branch.EquivalentOrders);

                branchNode.Nodes.Add(new TreeNode(equivalentText)
                {
                    ForeColor = _palette.MutedForeColor,
                    Tag = StrategyTextRenderer.FormatEquivalentDetails(branch.EquivalentOrders),
                });

                branchNode.Nodes.Add(new TreeNode(patternText)
                {
                    ForeColor = _palette.MutedForeColor,
                    Tag = StrategyTextRenderer.FormatEquivalentDetails(branch.EquivalentOrders),
                });
            }

            branchNode.Nodes.Add(new TreeNode(StrategyTextRenderer.FormatInEntry(branch.Effect.NewlyGuaranteedTop))
            {
                ForeColor = _palette.InColor,
                Tag = $"Newly confirmed in top-k: {StrategyTextRenderer.FormatOptionalSet(branch.Effect.NewlyGuaranteedTop)}",
            });

            branchNode.Nodes.Add(new TreeNode(StrategyTextRenderer.FormatOutEntry(branch.Effect.NewlyExcluded))
            {
                ForeColor = _palette.OutColor,
                Tag = $"Newly excluded from top-k: {StrategyTextRenderer.FormatOptionalSet(branch.Effect.NewlyExcluded)}",
            });

            branchNode.Nodes.Add(new TreeNode(StrategyTextRenderer.FormatFixedEntry(branch.Effect.FixedCandidates))
            {
                ForeColor = _palette.FixedColor,
                Tag = $"Current fixed top-k candidates: {StrategyTextRenderer.FormatOptionalSet(branch.Effect.FixedCandidates)}",
            });

            branchNode.Nodes.Add(new TreeNode(StrategyTextRenderer.FormatPossibleEntry(branch.Effect.PossibleCandidates))
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
        string details = $"{branch.OrderText}\n{StrategyTextRenderer.FormatEffectDetails(branch.Effect)}";

        if (branch.EquivalentOrders is not null)
        {
            details += "\n" + StrategyTextRenderer.FormatEquivalentDetails(branch.EquivalentOrders);
        }

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
            _summaryLabel.Text = "Ready.";
            _detailsTextBox.Text = BuildIdleDetailsText();
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
        _summaryLabel.Text =
            $"n={plan.N}, m={plan.M}, k={plan.K}, elapsed={plan.Elapsed.TotalMilliseconds:F1} ms, max step={plan.MaxStep}, " +
            $"{FormatSearchStatsSummary(plan.SearchStatistics)}, {FormatDiagnosticsSummary(plan.SearchStatistics.Diagnostics)}.";
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
        _elapsedLabel.Text = $"{GetElapsedSeconds():F1} s";
    }

    private void UpdateSearchProgress(SearchProgressSnapshot snapshot)
    {
        _latestProgress = snapshot;
        UpdateSearchStatsLabel();
        UpdateDiagnosticsLabel();
        _summaryLabel.Text = $"Running... {FormatSearchStatsSummary(snapshot, includeOutputStates: true)}. {FormatLiveDiagnosticsSummary(snapshot)}.";
        _detailsTextBox.Text = BuildLiveDiagnosticsText(snapshot);
    }

    private void UpdateSearchStatsLabel()
    {
        _searchStatsLabel.Text =
            $"searched: {_latestProgress.SearchedStates}\n" +
            $"pending: {_latestProgress.PendingStates}\n" +
            $"peak: {_latestProgress.PeakPendingStates}\n" +
            $"output: {_latestProgress.OutputStates}";
    }

    private void UpdateDiagnosticsLabel()
    {
        _diagnosticsLabel.Text = BuildDiagnosticsLabelText(_latestProgress);
    }

    private static string FormatSearchStatsSummary(SearchStatistics statistics)
    {
        return $"searched={statistics.SearchedStates}, pending={statistics.PendingStates}, peak pending={statistics.PeakPendingStates}, output states={statistics.OutputStates}";
    }

    private static string FormatSearchStatsSummary(SearchProgressSnapshot snapshot, bool includeOutputStates)
    {
        string outputText = includeOutputStates ? $", output={snapshot.OutputStates}" : string.Empty;
        return $"searched={snapshot.SearchedStates}, pending={snapshot.PendingStates}, peak pending={snapshot.PeakPendingStates}{outputText}";
    }

    private static SearchProgressSnapshot CreateInitialProgressSnapshot()
    {
        return new SearchProgressSnapshot(0, 0, 0, 0, 0, null, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private static string BuildPlanDetails(StrategyPlan plan)
    {
        string rendered = StrategyTextRenderer.Render(plan).TrimEnd();
        string diagnostics = BuildDiagnosticsDetails(plan.SearchStatistics.Diagnostics);
        return diagnostics.Length == 0 ? rendered : $"{rendered}\n\n{diagnostics}";
    }

    private static string BuildDiagnosticsDetails(SearchDiagnostics diagnostics)
    {
        var lines = new System.Collections.Generic.List<string>
        {
            "Search diagnostics:",
            $"  root incumbents = {diagnostics.RootIncumbents.Count}",
            $"  lower-bound prunes = {diagnostics.LowerBoundPrunes}",
            $"  duplicate outcome skips = {diagnostics.DuplicateOutcomeSkips}",
            $"  merged outcome collisions = {diagnostics.MergedOutcomeCollisions}",
            $"  cache hits = exact {diagnostics.ExactCacheHits}, lower-bound {diagnostics.LowerBoundCacheHits}, feasible-top-set {diagnostics.FeasibleTopSetCacheHits}, best-group-pattern {diagnostics.BestGroupPatternCacheHits}",
        };

        if (diagnostics.RootIncumbents.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Root incumbent timeline:");
            foreach (SearchMilestone milestone in diagnostics.RootIncumbents)
            {
                lines.Add(
                    $"  {milestone.ElapsedMilliseconds / 1000.0:F1}s: max step <= {milestone.BestWorstCaseSteps} via {milestone.ComparisonGroupText} " +
                    $"(searched {milestone.SearchedStates}, pending {milestone.PendingStates}, peak {milestone.PeakPendingStates}, output {milestone.OutputStates}, prunes {milestone.LowerBoundPrunes})");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildLiveDiagnosticsText(SearchProgressSnapshot snapshot)
    {
        var lines = new System.Collections.Generic.List<string>
        {
            "Live search diagnostics",
            $"elapsed: {snapshot.ElapsedMilliseconds / 1000.0:F1} s",
            $"searched states: {snapshot.SearchedStates}",
            $"pending states: {snapshot.PendingStates} (peak {snapshot.PeakPendingStates})",
            $"output states: {snapshot.OutputStates}",
            $"lower-bound prunes: {snapshot.LowerBoundPrunes}",
            $"duplicate outcome skips: {snapshot.DuplicateOutcomeSkips}",
            $"merged outcome collisions: {snapshot.MergedOutcomeCollisions}",
            $"cache hits: exact {snapshot.ExactCacheHits}, lower-bound {snapshot.LowerBoundCacheHits}, feasible-top-set {snapshot.FeasibleTopSetCacheHits}, best-group-pattern {snapshot.BestGroupPatternCacheHits}",
            $"root incumbents found: {snapshot.RootIncumbentCount}",
        };

        if (snapshot.LatestRootIncumbent is null)
        {
            lines.Add("latest incumbent: not found yet");
        }
        else
        {
            SearchMilestone latest = snapshot.LatestRootIncumbent;
            lines.Add($"latest incumbent: max step <= {latest.BestWorstCaseSteps} via {latest.ComparisonGroupText}");
            lines.Add(
                $"latest incumbent stats: t={latest.ElapsedMilliseconds / 1000.0:F1}s, searched={latest.SearchedStates}, pending={latest.PendingStates}, peak={latest.PeakPendingStates}, output={latest.OutputStates}, prunes={latest.LowerBoundPrunes}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatDiagnosticsSummary(SearchDiagnostics diagnostics)
    {
        string incumbentText = diagnostics.RootIncumbents.Count == 0
            ? "incumbent=-"
            : $"incumbent<={diagnostics.RootIncumbents[^1].BestWorstCaseSteps}";
        return $"{incumbentText}, root milestones={diagnostics.RootIncumbents.Count}, prunes={diagnostics.LowerBoundPrunes}";
    }

    private static string FormatLiveDiagnosticsSummary(SearchProgressSnapshot snapshot)
    {
        string incumbentText = snapshot.LatestRootIncumbent is null
            ? "incumbent: -"
            : $"incumbent: <= {snapshot.LatestRootIncumbent.BestWorstCaseSteps}";
        return $"{incumbentText}, milestones: {snapshot.RootIncumbentCount}, prunes: {snapshot.LowerBoundPrunes}, cache hits: {snapshot.ExactCacheHits}/{snapshot.LowerBoundCacheHits}/{snapshot.FeasibleTopSetCacheHits}/{snapshot.BestGroupPatternCacheHits}";
    }

    private static string BuildDiagnosticsLabelText(SearchProgressSnapshot snapshot)
    {
        string incumbentText = snapshot.LatestRootIncumbent is null
            ? "incumbent: -"
            : $"incumbent: <= {snapshot.LatestRootIncumbent.BestWorstCaseSteps}";
        return
            $"{incumbentText}\n" +
            $"milestones: {snapshot.RootIncumbentCount}\n" +
            $"prunes: {snapshot.LowerBoundPrunes}\n" +
            $"cache: {snapshot.ExactCacheHits}/{snapshot.LowerBoundCacheHits}/{snapshot.FeasibleTopSetCacheHits}/{snapshot.BestGroupPatternCacheHits}";
    }

    private static string BuildIdleDetailsText()
    {
        return string.Join(Environment.NewLine, new[]
        {
            "Top-K Strategy Explorer",
            string.Empty,
            "1. Adjust n, m, k and theme in the Inputs section.",
            "2. Use Run / Stop / Expand All / Collapse All from the Actions section.",
            "3. Watch the Search and Diagnostics panels for live progress.",
            string.Empty,
            "Tree legend:",
            "- state: comparison node",
            "- branch: outcome branch",
            "- in / out / fixed / possible: branch effect categories",
            "- result: terminal top-k set",
            "- reference: previously expanded state",
        });
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
