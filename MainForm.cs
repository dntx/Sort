using System;
using System.Collections.Generic;
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
    private readonly Button _backButton;
    private readonly Button _toggleDetailsButton;
    private readonly Label _elapsedLabel;
    private readonly Label _statesLabel;
    private readonly Label _workLabel;
    private readonly StatusStrip _statusStrip;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly TreeView _treeView;
    private readonly ListView _overviewList;
    private readonly RichTextBox _detailsTextBox;
    private readonly System.Windows.Forms.Timer _elapsedTimer;
    private ThemePalette _palette = DarkPalette;
    private StrategyPlan? _defaultPlan;
    private StrategyPlan? _compactPlan;
    private bool _compactImproved;
    private Stopwatch? _runStopwatch;
    private CancellationTokenSource? _runCancellationSource;
    private readonly Dictionary<string, TreeNode> _stateNodesByKey = new();
    private readonly Dictionary<TreeNode, string> _referenceTargets = new();
    private readonly Stack<TreeNode> _navigationHistory = new();
    private SearchProgressSnapshot _latestProgress;
    private SearchStatistics? _completedDefaultStats;
    private SearchStatistics? _completedCompactStats;
    private int _activePhase;
    private long _phase1ElapsedMs = -1;

    public MainForm()
    {
        Text = "Top-K Strategy Explorer";
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
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
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 12),
        };
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, StatsRowHeight));

        _nTextBox = CreateInputTextBox("9");
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

        _backButton = new Button
        {
            Text = "Back",
            AutoSize = true,
            Height = 30,
            Enabled = false,
            Margin = new Padding(8, 4, 0, 0),
        };

        _toggleDetailsButton = new Button
        {
            Text = "Show Details",
            AutoSize = true,
            Height = 30,
            Margin = new Padding(8, 4, 0, 0),
        };

        _elapsedLabel = new Label
        {
            AutoSize = true,
            Margin = Padding.Empty,
            Text = "0.000 s\ndefault: -\ncompact: -",
            Font = new Font(Font.FontFamily, 11, FontStyle.Bold),
        };
        _statesLabel = new Label
        {
            AutoSize = true,
            Margin = Padding.Empty,
            Text = "searched: 0\npending: 0 (peak 0)\noutput: 0\nlower-bound: 0\nfeasible-top-set: 0",
        };
        _workLabel = new Label
        {
            AutoSize = true,
            Margin = Padding.Empty,
            Text = "outcomes: 0\nduplicate skips: 0\nmerged collisions: 0\nprunes: 0\ncache: 0/0/0/0\ncompact: -",
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
        actionsPanel.Controls.Add(_backButton);
        actionsPanel.Controls.Add(_toggleDetailsButton);

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
            AutoSize = false,
            Height = StatsRowHeight,
            ColumnCount = 3,
            Margin = Padding.Empty,
        };
        statsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        statsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 29));
        statsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));

        var progressBody = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            Margin = Padding.Empty,
        };
        progressBody.Controls.Add(_elapsedLabel, 0, 0);

        statsLayout.Controls.Add(CreateSectionPanel("Progress", progressBody, fillCell: true), 0, 0);
        statsLayout.Controls.Add(CreateSectionPanel("States", _statesLabel, fillCell: true), 1, 0);
        statsLayout.Controls.Add(CreateSectionPanel("Work", _workLabel, fillCell: true), 2, 0);

        _statusStrip = new StatusStrip
        {
            SizingGrip = false,
        };
        _statusLabel = new ToolStripStatusLabel
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "Ready.",
        };
        _statusStrip.Items.Add(_statusLabel);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
        };

        _treeView = new TreeView
        {
            Dock = DockStyle.Fill,
            HideSelection = false,
            FullRowSelect = true,
            Font = new Font(FontFamily.GenericSansSerif, 10),
        };
        _treeView.AfterSelect += (_, e) => ShowNodeDetails(e.Node);
        _treeView.NodeMouseDoubleClick += (_, e) => TryJumpToReferenceTarget(e.Node);
        _treeView.MouseDown += TreeView_MouseDown;
        _treeView.KeyDown += TreeView_KeyDown;
        _treeView.ContextMenuStrip = CreateTreeContextMenu();
        _expandAllButton.Click += (_, _) => _treeView.ExpandAll();
        _collapseAllButton.Click += (_, _) => _treeView.CollapseAll();
        _backButton.Click += (_, _) => NavigateBack();

        _overviewList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            HeaderStyle = ColumnHeaderStyle.None,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            Font = new Font(FontFamily.GenericSansSerif, 9),
        };
        _overviewList.Columns.Add("Overview", -2, HorizontalAlignment.Left);
        _overviewList.SelectedIndexChanged += (_, _) => JumpFromOverviewSelection();
        _overviewList.Resize += (_, _) => ResizeOverviewColumn();

        var innerSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Panel2Collapsed = true,
        };
        innerSplit.Panel1.Controls.Add(_treeView);

        _detailsTextBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = new Font(FontFamily.GenericMonospace, 10),
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
        };
        innerSplit.Panel2.Controls.Add(_detailsTextBox);

        _toggleDetailsButton.Click += (_, _) =>
        {
            innerSplit.Panel2Collapsed = !innerSplit.Panel2Collapsed;
            _toggleDetailsButton.Text = innerSplit.Panel2Collapsed ? "Show Details" : "Hide Details";
        };

        split.Panel1.Controls.Add(_overviewList);
        split.Panel2.Controls.Add(innerSplit);

        headerLayout.Controls.Add(controlsLayout, 0, 0);
        headerLayout.Controls.Add(statsLayout, 0, 1);

        layout.Controls.Add(headerLayout, 0, 0);
        layout.Controls.Add(split, 0, 1);

        Controls.Add(layout);
        Controls.Add(_statusStrip);
        Shown += (_, _) =>
        {
            int progressColumn = _elapsedLabel.PreferredHeight + 6;
            int statsBody = Math.Max(
                progressColumn,
                Math.Max(_statesLabel.PreferredHeight, _workLabel.PreferredHeight));
            int statsHeight = statsBody + StatsRowChrome;
            headerLayout.RowStyles[1].Height = statsHeight;
            statsLayout.Height = statsHeight;

            split.Panel1MinSize = 200;
            split.Panel2MinSize = 360;
            int overviewWidth = (int)(split.Width * 4.0 / 9.0);
            split.SplitterDistance = Math.Clamp(overviewWidth, split.Panel1MinSize, split.Width - split.Panel2MinSize);

            innerSplit.Panel1MinSize = 240;
            innerSplit.Panel2MinSize = 160;
            int treeWidth = (int)(innerSplit.Width * 0.6);
            innerSplit.SplitterDistance = Math.Clamp(
                treeWidth, innerSplit.Panel1MinSize, Math.Max(innerSplit.Panel1MinSize, innerSplit.Width - innerSplit.Panel2MinSize));
        };
        AcceptButton = _runButton;
        _elapsedTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _elapsedTimer.Tick += (_, _) => UpdateElapsedLabel();
        _detailsTextBox.Text = BuildIdleDetailsText();
        ApplyTheme(ColorTheme.Dark);
    }

    private const int StatsRowHeight = 150;
    private const int StatsRowChrome = 84;

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

    private static Panel CreateSectionPanel(string title, Control body, bool fillCell = false)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoSize = !fillCell,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 8, 8),
        };

        var sectionLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = !fillCell,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
        };
        sectionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        sectionLayout.RowStyles.Add(fillCell
            ? new RowStyle(SizeType.Percent, 100f)
            : new RowStyle(SizeType.AutoSize));
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

        if (Program.IsPotentiallySlowSearch(n, m, k))
        {
            DialogResult choice = MessageBox.Show(
                this,
                $"Solving n={n}, m={m}, k={k} may take a long time (roughly ten seconds or more, up to minutes) because the search grows quickly for these parameters.\n\n" +
                "You can press Stop at any time once it starts.\n\nContinue?",
                "Large search",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (choice != DialogResult.Yes)
                return;
        }

        _runCancellationSource?.Dispose();
        _runCancellationSource = new CancellationTokenSource();
        CancellationToken cancellationToken = _runCancellationSource.Token;
        IProgress<SearchProgressSnapshot> progress = new Progress<SearchProgressSnapshot>(UpdateSearchProgress);
        _latestProgress = CreateInitialProgressSnapshot();
        _completedDefaultStats = null;
        _completedCompactStats = null;
        _defaultPlan = null;
        _compactPlan = null;
        _compactImproved = false;
        _activePhase = 1;
        Interlocked.Exchange(ref _phase1ElapsedMs, -1);
        ClearResultsView();
        _runStopwatch = Stopwatch.StartNew();
        UpdateElapsedLabel();
        UpdateStatsPanels();
        _elapsedTimer.Start();
        SetRunningState(isRunning: true);
        _statusLabel.Text = $"Running n={n}, m={m}, k={k}...";
        _detailsTextBox.Text = BuildLiveDiagnosticsText(_latestProgress);

        try
        {
            var result = await Task.Run(() =>
            {
                var builder = new StrategyBuilder(n, m, k, cancellationToken, snapshot => progress.Report(snapshot));
                StrategyPlan defaultPlan = builder.BuildDefaultPlan();
                Interlocked.Exchange(ref _phase1ElapsedMs, _runStopwatch?.ElapsedMilliseconds ?? 0);
                Interlocked.Exchange(ref _activePhase, 2);
                StrategyPlan compactPlan = builder.BuildCompactPlan();
                return (defaultPlan, compactPlan);
            }, cancellationToken);
            _runStopwatch?.Stop();

            _defaultPlan = result.defaultPlan;
            _compactPlan = result.compactPlan;
            _compactImproved =
                result.compactPlan.MaxStep == result.defaultPlan.MaxStep &&
                result.compactPlan.SearchStatistics.OutputStates < result.defaultPlan.SearchStatistics.OutputStates;

            _latestProgress = CreateSnapshotFromPlan(result.compactPlan);
            PopulateTree(result.defaultPlan, result.compactPlan, _compactImproved);
            _completedDefaultStats = result.defaultPlan.SearchStatistics;
            _completedCompactStats = result.compactPlan.SearchStatistics;
            UpdateSummaryText(result.defaultPlan, result.compactPlan, _compactImproved);
            UpdateStatsPanels();
        }
        catch (OperationCanceledException)
        {
            _runStopwatch?.Stop();
            _statusLabel.Text = $"Stopped after {GetElapsedSeconds():F1} s. {FormatSearchStatsSummary(_latestProgress, includeOutputStates: true)}. {FormatLiveDiagnosticsSummary(_latestProgress)}.";
            _detailsTextBox.Text = BuildLiveDiagnosticsText(_latestProgress);
        }
        catch (Exception ex)
        {
            _runStopwatch?.Stop();
            _statusLabel.Text = $"Run failed after {GetElapsedSeconds():F1} s. {FormatSearchStatsSummary(_latestProgress, includeOutputStates: true)}. {FormatLiveDiagnosticsSummary(_latestProgress)}.";
            _detailsTextBox.Text = BuildLiveDiagnosticsText(_latestProgress);
            MessageBox.Show(this, ex.Message, "Run failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _activePhase = 0;
            _elapsedTimer.Stop();
            UpdateElapsedLabel();
            SetRunningState(isRunning: false);
            _runCancellationSource?.Dispose();
            _runCancellationSource = null;
        }
    }

    private void PopulateTree(StrategyPlan defaultPlan, StrategyPlan compactPlan, bool compactImproved)
    {
        _treeView.BeginUpdate();
        _treeView.Nodes.Clear();
        _stateNodesByKey.Clear();
        _referenceTargets.Clear();
        _navigationHistory.Clear();
        _backButton.Enabled = false;

        double totalElapsedMs = defaultPlan.Elapsed.TotalMilliseconds + compactPlan.Elapsed.TotalMilliseconds;
        var root = new TreeNode(
            $"n={defaultPlan.N}, m={defaultPlan.M}, k={defaultPlan.K}, two-phase elapsed={totalElapsedMs:F1} ms")
        {
            Tag = BuildTwoPhaseDetails(defaultPlan, compactPlan, compactImproved),
            NodeFont = new Font(_treeView.Font, FontStyle.Bold),
            ForeColor = _palette.ForeColor,
        };
        root.Nodes.Add(CreatePlanTreeRoot("default", defaultPlan, "default"));
        if (compactImproved)
            root.Nodes.Add(CreatePlanTreeRoot("compact", compactPlan, "compact"));
        else
            root.Nodes.Add(new TreeNode("compact refinement: no better result (output states unchanged or worse)") { ForeColor = _palette.MutedForeColor });
        _treeView.Nodes.Add(root);
        root.Expand();
        root.Nodes[0].Expand();

        _treeView.EndUpdate();
        _treeView.SelectedNode = root;

        PopulateOverview(defaultPlan);
    }

    // Resets the result surfaces (overview list, tree, navigation state, details) so a fresh Run
    // does not leave the previous parameters' output on screen while the new search is in flight or
    // if it is cancelled / fails before producing a plan.
    private void ClearResultsView()
    {
        _overviewList.Items.Clear();

        _treeView.BeginUpdate();
        _treeView.Nodes.Clear();
        _treeView.EndUpdate();

        _stateNodesByKey.Clear();
        _referenceTargets.Clear();
        _navigationHistory.Clear();
        _backButton.Enabled = false;
    }

    private void PopulateOverview(StrategyPlan defaultPlan)
    {
        _overviewList.BeginUpdate();
        _overviewList.Items.Clear();
        foreach (OverviewRow row in StrategyOverviewRenderer.Build(defaultPlan).Rows)
        {
            string? key = row.LinkStateId is int id ? $"default:{id}" : null;
            var headlineItem = new ListViewItem(row.Headline) { Tag = key };
            _overviewList.Items.Add(headlineItem);
            foreach (string detail in row.Details)
            {
                _overviewList.Items.Add(new ListViewItem("        " + detail)
                {
                    Tag = key,
                    ForeColor = _palette.MutedForeColor,
                });
            }
        }

        if (_overviewList.Columns.Count > 0)
            ResizeOverviewColumn();
        _overviewList.EndUpdate();
    }

    private void ResizeOverviewColumn()
    {
        if (_overviewList.Columns.Count == 0)
            return;

        _overviewList.Columns[0].Width = _overviewList.Items.Count > 0 ? -1 : _overviewList.ClientSize.Width;
        if (_overviewList.Columns[0].Width < _overviewList.ClientSize.Width)
            _overviewList.Columns[0].Width = _overviewList.ClientSize.Width;
    }

    private void JumpFromOverviewSelection()
    {
        if (_overviewList.SelectedItems.Count == 0)
            return;

        if (_overviewList.SelectedItems[0].Tag is not string targetStateKey)
            return;

        if (!_stateNodesByKey.TryGetValue(targetStateKey, out TreeNode? targetNode))
            return;

        targetNode.EnsureVisible();
        _treeView.SelectedNode = targetNode;
    }

    private TreeNode CreatePlanTreeRoot(string label, StrategyPlan plan, string scope)
    {
        StrategyDepthIndex depthIndex = StrategyDepthIndex.Build(plan.Root);
        var planNode = new TreeNode(
            $"{label}: elapsed={plan.Elapsed.TotalMilliseconds:F1} ms, worst-case steps={plan.MaxStep}, output={plan.SearchStatistics.OutputStates}")
        {
            Tag = BuildPlanDetails(plan),
            NodeFont = new Font(_treeView.Font, FontStyle.Bold),
            ForeColor = _palette.ForeColor,
        };
        planNode.Nodes.Add(CreateStateNode(plan.Root, plan.K, scope, depthIndex));
        return planNode;
    }

    private TreeNode CreateStateNode(StrategyNode node, int k, string scope, StrategyDepthIndex depthIndex)
    {
        return node.Kind switch
        {
            StrategyNodeKind.Decision => CreateDecisionNode(node, k, scope, depthIndex),
            StrategyNodeKind.Terminal => CreateTerminalNode(node, k, scope),
            StrategyNodeKind.Reference => CreateReferenceNode(node, scope, depthIndex),
            _ => throw new InvalidOperationException("Unknown node kind"),
        };
    }

    private TreeNode CreateDecisionNode(StrategyNode node, int k, string scope, StrategyDepthIndex depthIndex)
    {
        int maxStep = depthIndex.SubtreeMaxStep(node);
        string headerText = $"S{node.StateId} [step {node.Step}/{maxStep}] sort({StrategyTextRenderer.FormatSet(node.Group)})";

        var treeNode = new TreeNode(headerText)
        {
            ForeColor = _palette.StateColor,
            Tag = BuildStateDetails(node),
        };
        _stateNodesByKey[$"{scope}:{node.StateId}"] = treeNode;

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

            if (branch.Effect.NewlyGuaranteedTop.Count > 0)
            {
                branchNode.Nodes.Add(new TreeNode(StrategyTextRenderer.FormatInEntry(branch.Effect.NewlyGuaranteedTop))
                {
                    ForeColor = _palette.InColor,
                    Tag = $"Newly confirmed in top-k: {StrategyTextRenderer.FormatOptionalSet(branch.Effect.NewlyGuaranteedTop)}",
                });
            }

            if (branch.Effect.NewlyExcluded.Count > 0)
            {
                branchNode.Nodes.Add(new TreeNode(StrategyTextRenderer.FormatOutEntry(branch.Effect.NewlyExcluded))
                {
                    ForeColor = _palette.OutColor,
                    Tag = $"Newly excluded from top-k: {StrategyTextRenderer.FormatOptionalSet(branch.Effect.NewlyExcluded)}",
                });
            }

            if (branch.Effect.FixedCandidates.Count > 0)
            {
                branchNode.Nodes.Add(new TreeNode(StrategyTextRenderer.FormatFixedEntry(branch.Effect.FixedCandidates))
                {
                    ForeColor = _palette.FixedColor,
                    Tag = $"Current fixed top-k candidates: {StrategyTextRenderer.FormatOptionalSet(branch.Effect.FixedCandidates)}",
                });
            }

            if (branch.Effect.PossibleCandidates.Count > 0)
            {
                branchNode.Nodes.Add(new TreeNode(StrategyTextRenderer.FormatPossibleEntry(branch.Effect.PossibleCandidates))
                {
                    ForeColor = _palette.PossibleColor,
                    Tag = $"Current possible top-k candidates: {StrategyTextRenderer.FormatOptionalSet(branch.Effect.PossibleCandidates)}",
                });
            }

            branchNode.Nodes.Add(CreateStateNode(branch.Next, k, scope, depthIndex));
            treeNode.Nodes.Add(branchNode);
        }

        return treeNode;
    }

    private TreeNode CreateTerminalNode(StrategyNode node, int k, string scope)
    {
        var treeNode = new TreeNode($"S{node.StateId}: top {k} = ({StrategyTextRenderer.FormatSet(node.TopSet)})")
        {
            ForeColor = _palette.ResultColor,
            Tag = $"Result state S{node.StateId}\nTop {k} = ({StrategyTextRenderer.FormatSet(node.TopSet)})",
        };
        _stateNodesByKey[$"{scope}:{node.StateId}"] = treeNode;
        return treeNode;
    }

    private TreeNode CreateReferenceNode(StrategyNode node, string scope, StrategyDepthIndex depthIndex)
    {
        string label = depthIndex.TryGetReferenceRemaining(node.StateId, out int remaining)
            ? $"->S{node.StateId} {StrategyTextRenderer.FormatRemainingSteps(remaining)}"
            : $"->S{node.StateId}";
        label += StrategyTextRenderer.FormatRelabeling(node.ReferenceRelabeling);

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

        if (!_stateNodesByKey.TryGetValue(targetStateKey, out TreeNode? targetNode))
            return;

        _navigationHistory.Push(node);
        _backButton.Enabled = true;

        targetNode.EnsureVisible();
        _treeView.SelectedNode = targetNode;
        _treeView.Focus();
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

    private static string BuildBranchDetails(StrategyBranch branch)
    {
        string details = branch.OrderText;
        string effectDetails = StrategyTextRenderer.FormatEffectDetails(branch.Effect);
        if (!string.IsNullOrEmpty(effectDetails))
            details += "\n" + effectDetails;

        if (branch.EquivalentOrders is not null)
        {
            details += "\n" + StrategyTextRenderer.FormatEquivalentDetails(branch.EquivalentOrders);
        }

        return details;
    }

    private static string BuildStateDetails(StrategyNode node)
    {
        string details = node.FinalChoice is not null
            ? $"Step: {node.Step}\n" +
              $"Comparison group: ({StrategyTextRenderer.FormatSet(node.Group)})"
            : $"State S{node.StateId}\n" +
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
        _statusStrip.BackColor = _palette.SurfaceBackColor;
        _statusStrip.ForeColor = _palette.ForeColor;
        _statusLabel.ForeColor = _palette.ForeColor;

        if (_defaultPlan is not null && _compactPlan is not null)
        {
            PopulateTree(_defaultPlan, _compactPlan, _compactImproved);
            if (_runCancellationSource is null)
                UpdateSummaryText(_defaultPlan, _compactPlan, _compactImproved);
        }
        else if (_runCancellationSource is null)
        {
            _statusLabel.Text = "Ready.";
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
            case ListView listView:
                listView.BackColor = _palette.SurfaceBackColor;
                listView.ForeColor = _palette.ForeColor;
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

    private void UpdateSummaryText(StrategyPlan defaultPlan, StrategyPlan compactPlan, bool compactImproved)
    {
        double totalElapsedMs = defaultPlan.Elapsed.TotalMilliseconds + compactPlan.Elapsed.TotalMilliseconds;
        string compactText = compactImproved
            ? $"compact improved output states {defaultPlan.SearchStatistics.OutputStates} -> {compactPlan.SearchStatistics.OutputStates}"
            : $"compact produced no better result (default output states {defaultPlan.SearchStatistics.OutputStates}, compact {compactPlan.SearchStatistics.OutputStates})";
        _statusLabel.Text =
            $"n={defaultPlan.N}, m={defaultPlan.M}, k={defaultPlan.K}, total elapsed={totalElapsedMs:F1} ms, " +
            $"worst-case steps={defaultPlan.MaxStep}, {compactText}.";
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
        _backButton.Enabled = !isRunning && _navigationHistory.Count > 0;
    }

    private void UpdateElapsedLabel()
    {
        string defaultLine;
        string compactLine;
        int phase = Volatile.Read(ref _activePhase);
        long phase1Ms = Interlocked.Read(ref _phase1ElapsedMs);
        long totalMs = (long)((_runStopwatch?.Elapsed.TotalMilliseconds) ?? 0);

        if (_completedDefaultStats is { } defaultStats)
        {
            defaultLine = $"default: {(defaultStats.Phase1Milliseconds + defaultStats.Phase2Milliseconds) / 1000.0:F3} s";
        }
        else if (_runStopwatch is not null && phase >= 1)
        {
            long liveDefaultMs = phase1Ms >= 0 ? phase1Ms : totalMs;
            defaultLine = $"default: {liveDefaultMs / 1000.0:F3} s";
        }
        else
        {
            defaultLine = "default: -";
        }

        if (_completedCompactStats is { } compactStats)
        {
            compactLine = $"compact: {(compactStats.Phase1bMilliseconds + compactStats.Phase2Milliseconds) / 1000.0:F3} s";
        }
        else if (_runStopwatch is not null && phase >= 2 && phase1Ms >= 0)
        {
            long liveCompactMs = Math.Max(0, totalMs - phase1Ms);
            compactLine = $"compact: {liveCompactMs / 1000.0:F3} s";
        }
        else
        {
            compactLine = "compact: -";
        }

        string text = $"{GetElapsedSeconds():F3} s\n{defaultLine}\n{compactLine}";
        _elapsedLabel.Text = text;
    }

    private void UpdateSearchProgress(SearchProgressSnapshot snapshot)
    {
        _latestProgress = snapshot;
        UpdateStatsPanels();
        string incumbent = snapshot.LatestRootIncumbent is null
            ? "incumbent -"
            : $"incumbent <= {snapshot.LatestRootIncumbent.BestWorstCaseSteps}";
        _statusLabel.Text = $"Running (phase {GetPhaseLabel()})... {GetElapsedSeconds():F1} s, searched {snapshot.SearchedStates}, {incumbent}.";
        _detailsTextBox.Text = BuildLiveDiagnosticsText(snapshot);
    }

    // Updates the three live stat panels (States / Work / Progress) from the latest snapshot.
    // Each metric lives in exactly one panel so the panels do not duplicate one another.
    private void UpdateStatsPanels()
    {
        SearchProgressSnapshot p = _latestProgress;

        _statesLabel.Text =
            $"searched: {p.SearchedStates}\n" +
            $"pending: {p.PendingStates} (peak {p.PeakPendingStates})\n" +
            $"output: {p.OutputStates}\n" +
            $"lower-bound: {p.LowerBoundStates}\n" +
            $"feasible-top-set: {p.FeasibleTopSetStates}";

        string compactText = p.CompactStatesSolved > 0
            ? $"compact: {p.CompactStatesSolved} solved, {p.CompactGroupsEnumerated} groups ({p.CompactStepOptimalGroups} opt)"
            : "compact: -";
        _workLabel.Text =
            $"outcomes: {p.OutcomesConstructed} (cand groups {p.CandidateGroupsEnumerated})\n" +
            $"duplicate skips: {p.DuplicateOutcomeSkips}\n" +
            $"merged collisions: {p.MergedOutcomeCollisions}\n" +
            $"prunes: {p.LowerBoundPrunes}\n" +
            $"cache: {p.ExactCacheHits}/{p.LowerBoundCacheHits}/{p.FeasibleTopSetCacheHits}/{p.BestGroupPatternCacheHits}\n" +
            compactText;
    }
    private static string FormatSearchStatsSummary(SearchProgressSnapshot snapshot, bool includeOutputStates)
    {
        string outputText = includeOutputStates ? $", output={snapshot.OutputStates}" : string.Empty;
        return $"searched={snapshot.SearchedStates}, pending={snapshot.PendingStates}, peak pending={snapshot.PeakPendingStates}{outputText}";
    }

    private static SearchProgressSnapshot CreateInitialProgressSnapshot()
    {
        return new SearchProgressSnapshot(0, 0, 0, 0, 0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    }

    private static SearchProgressSnapshot CreateSnapshotFromPlan(StrategyPlan plan)
    {
        return new SearchProgressSnapshot(
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
            plan.SearchStatistics.Diagnostics.BestGroupPatternCacheHits,
            plan.SearchStatistics.OutcomesConstructed,
            plan.SearchStatistics.CandidateGroupsEnumerated,
            plan.SearchStatistics.LowerBoundStates,
            plan.SearchStatistics.FeasibleTopSetStates,
            plan.SearchStatistics.CompactStatesSolved,
            plan.SearchStatistics.CompactGroupsEnumerated,
            plan.SearchStatistics.CompactStepOptimalGroups);
    }

    private string GetPhaseLabel()
    {
        return _activePhase switch
        {
            1 => "1/2 default exact+build",
            2 => "2/2 compact refinement",
            _ => "-",
        };
    }

    private static string BuildTwoPhaseDetails(StrategyPlan defaultPlan, StrategyPlan compactPlan, bool compactImproved)
    {
        string defaultText = StrategyTextRenderer.Render(defaultPlan).TrimEnd();
        string compactText = StrategyTextRenderer.Render(compactPlan).TrimEnd();
        double totalElapsedMs = defaultPlan.Elapsed.TotalMilliseconds + compactPlan.Elapsed.TotalMilliseconds;
        var lines = new List<string>
        {
            "Two-phase result",
            $"total elapsed: {totalElapsedMs:F1} ms",
            $"default output states: {defaultPlan.SearchStatistics.OutputStates}",
            $"compact output states: {compactPlan.SearchStatistics.OutputStates}",
            compactImproved
                ? "compact improvement: yes"
                : "compact improvement: no",
            string.Empty,
            "----- default -----",
            defaultText,
        };

        if (compactImproved)
        {
            lines.Add(string.Empty);
            lines.Add("----- compact refinement -----");
            lines.Add(compactText);
        }

        return string.Join(Environment.NewLine, lines);
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
                    $"  {milestone.ElapsedMilliseconds / 1000.0:F1}s: worst-case steps <= {milestone.BestWorstCaseSteps} via {milestone.ComparisonGroupText} " +
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
            "(see the States / Work / Progress panels for live counters)",
            string.Empty,
        };

        if (snapshot.LatestRootIncumbent is null)
        {
            lines.Add("latest incumbent: not found yet");
        }
        else
        {
            SearchMilestone latest = snapshot.LatestRootIncumbent;
            lines.Add($"latest incumbent: worst-case steps <= {latest.BestWorstCaseSteps} via {latest.ComparisonGroupText}");
            lines.Add(
                $"  found at t={latest.ElapsedMilliseconds / 1000.0:F1}s, searched={latest.SearchedStates}, output={latest.OutputStates}, prunes={latest.LowerBoundPrunes}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatLiveDiagnosticsSummary(SearchProgressSnapshot snapshot)
    {
        string incumbentText = snapshot.LatestRootIncumbent is null
            ? "incumbent: -"
            : $"incumbent: <= {snapshot.LatestRootIncumbent.BestWorstCaseSteps}";
        return $"{incumbentText}, milestones: {snapshot.RootIncumbentCount}, prunes: {snapshot.LowerBoundPrunes}, cache hits: {snapshot.ExactCacheHits}/{snapshot.LowerBoundCacheHits}/{snapshot.FeasibleTopSetCacheHits}/{snapshot.BestGroupPatternCacheHits}";
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
