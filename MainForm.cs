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
    private readonly ComboBox _modeComboBox;
    private readonly CheckBox _pauseEachStageCheckBox;
    private readonly Button _runButton;
    private readonly Button _stopButton;
    private readonly Button _treeExpandButton;
    private readonly Button _treeCollapseButton;
    private readonly Button _overviewExpandButton;
    private readonly Button _overviewCollapseButton;
    private readonly Button _backButton;
    private readonly Button _toggleDetailsButton;
    private readonly TextBox _progressTextBox;
    private readonly TextBox _statesTextBox;
    private readonly TextBox _workTextBox;
    private readonly StatusStrip _statusStrip;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly TreeView _treeView;
    private readonly TreeView _overviewTree;
    private readonly RichTextBox _detailsTextBox;
    private readonly System.Windows.Forms.Timer _elapsedTimer;
    private ThemePalette _palette = DarkPalette;
    private StrategyPlan? _feasiblePlan;
    private StrategyPlan? _defaultPlan;
    private StrategyPlan? _compactPlan;
    private bool _exactImproved;
    private bool _compactImproved;
    private bool _feasibleMode;
    private Stopwatch? _runStopwatch;
    private CancellationTokenSource? _runCancellationSource;
    // The builder of the in-flight run, polled (thread-safe) by the progress panel during compact<=N
    // tightening so the fourth line can show the exact time left until the soft budget times out (the
    // progress-based ETA is unreliable for those probes). Set on the UI thread before the run, cleared
    // when it ends.
    private volatile StrategyBuilder? _activeBuilder;
    private readonly Dictionary<string, TreeNode> _stateNodesByKey = new();
    private readonly Dictionary<TreeNode, string> _referenceTargets = new();
    private readonly Stack<TreeNode> _navigationHistory = new();
    private SearchProgressSnapshot _latestProgress;
    private SearchStatistics? _completedDefaultStats;
    private SearchStatistics? _completedCompactStats;
    private SearchStatistics? _completedFeasibleStats;
    private int _activePhase;
    // Anytime greedy edge state (UI thread only): every edge stage as it arrives (baseline compact,
    // each tightening, plus a terminal no-solution stage). The current-stage name and the run-clock ms
    // at which the current stage began drive the per-stage timing/labels in the progress panel.
    private readonly List<GreedyEdgeStage> _greedyEdgeStages = new();
    private string _currentStageName = "-";
    private long _stageStartMs;

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
            Margin = new Padding(0, 0, 0, -1),
        };
        headerLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, StatsRowHeight));

        _nTextBox = CreateInputTextBox("25");
        _mTextBox = CreateInputTextBox("5");
        _kTextBox = CreateInputTextBox("5");
        _themeComboBox = new ComboBox
        {
            Width = 140,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0, 4, 0, 0),
        };
        _themeComboBox.Items.AddRange(Enum.GetNames<ColorTheme>());
        _themeComboBox.SelectedItem = ColorTheme.Dark.ToString();
        _themeComboBox.SelectedIndexChanged += (_, _) => ApplyTheme(ParseSelectedTheme());

        // Search mode: B (default) = exact + compact (proven optimal); A = feasible + compact
        // (fast, interruptible, not proven optimal).
        _modeComboBox = new ComboBox
        {
            Width = 280,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0, 4, 0, 0),
        };
        _modeComboBox.Items.AddRange(new object[] { "exact (proven)", "greedy (fast)" });
        _modeComboBox.SelectedIndex = 0;

        // When checked, the run pauses after each new stage tree appears (a modal shows that stage's
        // summary and the search blocks until OK). Default off so runs are uninterrupted.
        _pauseEachStageCheckBox = new CheckBox
        {
            Text = "pause each stage",
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
        };

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

        _treeExpandButton = new Button
        {
            Text = "Expand",
            AutoSize = true,
            Height = 26,
            Margin = new Padding(0, 0, 6, 0),
        };

        _treeCollapseButton = new Button
        {
            Text = "Collapse",
            AutoSize = true,
            Height = 26,
            Margin = new Padding(0, 0, 0, 0),
        };

        _overviewExpandButton = new Button
        {
            Text = "Expand",
            AutoSize = true,
            Height = 26,
            Margin = new Padding(0, 0, 6, 0),
        };

        _overviewCollapseButton = new Button
        {
            Text = "Collapse",
            AutoSize = true,
            Height = 26,
            Margin = new Padding(0, 0, 0, 0),
        };

        _backButton = new Button
        {
            Text = "Back",
            AutoSize = true,
            Height = 26,
            Enabled = false,
            Margin = new Padding(12, 0, 0, 0),
        };

        _toggleDetailsButton = new Button
        {
            Text = "Show Details",
            AutoSize = true,
            Height = 30,
            Margin = new Padding(8, 4, 0, 0),
        };

        _progressTextBox = CreateStatTextBox(
            "0.000 s\n-: 0.000 s\nprogress: 0.0%\neta: -",
            new Font(Font.FontFamily, 11, FontStyle.Bold));
        _statesTextBox = CreateStatTextBox(
            "searched: 0\npending: 0 (peak 0)\noutput: 0\nlower-bound: 0\ntop-set: 0");
        _workTextBox = CreateStatTextBox(
            "outcomes: 0\nduplicate skips: 0\nmerged collisions: 0\nprunes: 0\ncache: 0/0/0/0\n[compact] -");
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
        inputsPanel.Controls.Add(CreateLabeledInput("mode", _modeComboBox));
        inputsPanel.Controls.Add(CreateLabeledInput("theme", _themeComboBox));
        inputsPanel.Controls.Add(_pauseEachStageCheckBox);

        var actionsPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        actionsPanel.Controls.Add(_runButton);
        actionsPanel.Controls.Add(_stopButton);
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

        statsLayout.Controls.Add(CreateSectionPanel("Progress", _progressTextBox, fillCell: true), 0, 0);
        statsLayout.Controls.Add(CreateSectionPanel("States", _statesTextBox, fillCell: true), 1, 0);
        statsLayout.Controls.Add(CreateSectionPanel("Work", _workTextBox, fillCell: true), 2, 0);

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
            SplitterWidth = 6,
            Margin = Padding.Empty,
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
        _treeExpandButton.Click += (_, _) => _treeView.ExpandAll();
        _treeCollapseButton.Click += (_, _) => _treeView.CollapseAll();
        _backButton.Click += (_, _) => NavigateBack();

        _overviewTree = new TreeView
        {
            Dock = DockStyle.Fill,
            HideSelection = false,
            ShowLines = true,
            ShowPlusMinus = true,
            ShowRootLines = true,
            Font = new Font(FontFamily.GenericSansSerif, 9),
        };
        _overviewTree.AfterSelect += (_, _) => JumpFromOverviewSelection();
        _overviewTree.ContextMenuStrip = CreateOverviewContextMenu();
        _overviewTree.KeyDown += OverviewTree_KeyDown;
        _overviewExpandButton.Click += (_, _) => _overviewTree.ExpandAll();
        _overviewCollapseButton.Click += (_, _) => _overviewTree.CollapseAll();

        var innerSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            Panel2Collapsed = true,
            SplitterWidth = 6,
        };
        innerSplit.Panel1.Controls.Add(CreateTreeRegion(_treeView, _treeExpandButton, _treeCollapseButton, _backButton));

        _detailsTextBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Font = new Font(FontFamily.GenericMonospace, 10),
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
        };
        var detailsContextMenu = new ContextMenuStrip();
        var detailsCopy = new ToolStripMenuItem("Copy") { ShortcutKeyDisplayString = "Ctrl+C" };
        detailsCopy.Click += (_, _) => SetClipboardText(_detailsTextBox.SelectedText);
        var detailsCopyAll = new ToolStripMenuItem("Copy all");
        detailsCopyAll.Click += (_, _) => SetClipboardText(_detailsTextBox.Text);
        detailsContextMenu.Items.Add(detailsCopy);
        detailsContextMenu.Items.Add(detailsCopyAll);
        detailsContextMenu.Opening += (_, _) =>
            detailsCopy.Enabled = _detailsTextBox.SelectionLength > 0;
        _detailsTextBox.ContextMenuStrip = detailsContextMenu;
        innerSplit.Panel2.Controls.Add(_detailsTextBox);

        _toggleDetailsButton.Click += (_, _) =>
        {
            innerSplit.Panel2Collapsed = !innerSplit.Panel2Collapsed;
            _toggleDetailsButton.Text = innerSplit.Panel2Collapsed ? "Show Details" : "Hide Details";
        };

        split.Panel1.Controls.Add(CreateTreeRegion(_overviewTree, _overviewExpandButton, _overviewCollapseButton));
        split.Panel2.Controls.Add(innerSplit);

        headerLayout.Controls.Add(controlsLayout, 0, 0);
        headerLayout.Controls.Add(statsLayout, 0, 1);

        layout.Controls.Add(headerLayout, 0, 0);
        layout.Controls.Add(split, 0, 1);

        Controls.Add(layout);
        Controls.Add(_statusStrip);
        Shown += (_, _) =>
        {
            int progressColumn = MeasureStatHeight(_progressTextBox) + 6;
            int statsBody = Math.Max(
                progressColumn,
                Math.Max(MeasureStatHeight(_statesTextBox), MeasureStatHeight(_workTextBox)));
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
        LoadSettings();
        ApplyTheme(ParseSelectedTheme());
        FormClosing += (_, _) => SaveSettings();
    }

    private const int StatsRowHeight = 150;
    private const int StatsRowChrome = 84;

    // Read-only, borderless, selectable text box used for the live stat panels so users can
    // select and copy the metric text (a plain Label is not selectable).
    private static TextBox CreateStatTextBox(string text, Font? font = null)
    {
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            WordWrap = false,
            ScrollBars = ScrollBars.None,
            TabStop = false,
            Cursor = Cursors.IBeam,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        if (font is not null)
            box.Font = font;
        box.Text = NormalizeStatLines(text);
        return box;
    }

    private static void SetStatText(TextBox box, string text)
    {
        // Skip the refresh while the user is actively selecting text in this box, so a
        // selection made mid-run is not cleared by the periodic update and can be copied.
        // Updates resume automatically once the selection is cleared or focus moves away.
        if (box.Focused && box.SelectionLength > 0)
            return;

        string normalized = NormalizeStatLines(text);
        if (box.Text != normalized)
            box.Text = normalized;
    }

    private static string NormalizeStatLines(string text)
        => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private static int MeasureStatHeight(TextBox box)
        => TextRenderer.MeasureText(box.Text, box.Font).Height + 6;

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
            Margin = new Padding(0, 0, -1, -1),
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
        bool feasibleMode = _modeComboBox.SelectedIndex == 1;
        _feasibleMode = feasibleMode;
        CancellationToken cancellationToken = _runCancellationSource.Token;
        IProgress<SearchProgressSnapshot> progress = new Progress<SearchProgressSnapshot>(UpdateSearchProgress);
        _latestProgress = CreateInitialProgressSnapshot();
        _completedDefaultStats = null;
        _completedCompactStats = null;
        _completedFeasibleStats = null;
        _feasiblePlan = null;
        _defaultPlan = null;
        _compactPlan = null;
        _exactImproved = false;
        _compactImproved = false;
        _activePhase = 0;
        _greedyEdgeStages.Clear();
        _currentStageName = feasibleMode ? "greedy" : "exact";
        _stageStartMs = 0;
        ClearResultsView();
        _runStopwatch = Stopwatch.StartNew();
        UpdateElapsedLabel();
        UpdateStatsPanels();
        _elapsedTimer.Start();
        SetRunningState(isRunning: true);
        _statusLabel.Text = $"Running n={n}, m={m}, k={k}...";
        _detailsTextBox.Text = BuildLiveDiagnosticsText(_latestProgress);

        // The builder is shared across all phases so the default/compact passes reuse the search
        // caches the earlier passes already populated.
        var builder = new StrategyBuilder(
            n,
            m,
            k,
            cancellationToken,
            snapshot => progress.Report(snapshot),
            reportCombinedRunProgress: true);
        _activeBuilder = builder;
        try
        {
            if (feasibleMode)
            {
                // Greedy mode: a fast greedy feasible plan (greedy) gives an instant browsable strategy
                // even on shapes exact never resolves (e.g. 25,5,5), then a budget-bounded compact
                // pass trims displayed edges under the feasible ceiling U.
                StrategyPlan feasiblePlan = await Task.Run(() => builder.BuildFeasiblePlan(), cancellationToken);
                _feasiblePlan = feasiblePlan;
                _latestProgress = CreateSnapshotFromPlan(feasiblePlan);
                PopulateTree(feasiblePlan, defaultPlan: null, compactPlan: null, exactImproved: false, compactImproved: false);
                _completedFeasibleStats = feasiblePlan.SearchStatistics;
                UpdateSummaryText(feasiblePlan, defaultPlan: null, compactPlan: null, compactImproved: false);
                UpdateStatsPanels();
                SetRunUiState(RunUiState.CompactComputingInteractive);

                Interlocked.Exchange(ref _activePhase, 2);
                _greedyEdgeStages.Clear();
                _currentStageName = "compact";
                _stageStartMs = _runStopwatch?.ElapsedMilliseconds ?? 0;
                // Each edge stage is surfaced live. The callback runs on the worker thread; a synchronous
                // Invoke marshals it onto the UI thread AND blocks the worker until the handler returns,
                // which is what lets the optional per-stage modal pause the search until the user clicks OK.
                StrategyPlan feasibleCompactPlan = await Task.Run(
                    () => builder.BuildFeasibleCompactPlan(MarshalEdgeStage),
                    cancellationToken);
                _runStopwatch?.Stop();

                _compactPlan = feasibleCompactPlan;
                _compactImproved = feasibleCompactPlan.IsStrictRefinementOver(feasiblePlan);
                _latestProgress = CreateSnapshotFromPlan(feasibleCompactPlan);
                _completedCompactStats = feasibleCompactPlan.SearchStatistics;
                UpdateSummaryText(feasiblePlan, defaultPlan: feasiblePlan, compactPlan: feasibleCompactPlan, compactImproved: _compactImproved);
                UpdateStatsPanels();
                return;
            }

            // Exact mode: no feasible phase. Phase 1 is the proven-optimal exact plan (exact), used as
            // both the incumbent and the displayed strategy; phase 2 is the compact refinement
            // (compact). The exact plan is MaxStep-optimal, so compact only trims edges among equally
            // optimal groups.
            Interlocked.Exchange(ref _activePhase, 1);
            StrategyPlan defaultPlan = await Task.Run(() => builder.BuildDefaultPlan(), cancellationToken);

            _defaultPlan = defaultPlan;
            _feasiblePlan = defaultPlan;
            _exactImproved = true;
            StrategyPlan incumbent = defaultPlan;
            _latestProgress = CreateSnapshotFromPlan(defaultPlan);
            PopulateTree(defaultPlan, defaultPlan, compactPlan: null, exactImproved: true, compactImproved: false);
            _completedDefaultStats = defaultPlan.SearchStatistics;
            UpdateSummaryText(defaultPlan, defaultPlan, compactPlan: null, compactImproved: false);
            UpdateStatsPanels();

            // The exact plan is on screen; the compact pass runs on a background thread, so the UI
            // thread is free: drop the wait cursor and keep tree navigation enabled for the rest of
            // the run (the user can browse the strategy while compact search continues).
            SetRunUiState(RunUiState.CompactComputingInteractive);

            // Phase 2: compact refinement.
            Interlocked.Exchange(ref _activePhase, 2);
            _currentStageName = "compact";
            _stageStartMs = _runStopwatch?.ElapsedMilliseconds ?? 0;
            StrategyPlan compactPlan = await Task.Run(() => builder.BuildCompactPlan(), cancellationToken);
            _runStopwatch?.Stop();

            _compactPlan = compactPlan;
            _compactImproved = compactPlan.IsStrictRefinementOver(incumbent);

            _latestProgress = CreateSnapshotFromPlan(compactPlan);
            FinalizeCompactInTree(defaultPlan, compactPlan, _compactImproved);
            _completedCompactStats = compactPlan.SearchStatistics;
            UpdateSummaryText(defaultPlan, defaultPlan, compactPlan, _compactImproved);
            UpdateStatsPanels();
        }
        catch (OperationCanceledException)
        {
            _runStopwatch?.Stop();
            string shownDefault = _defaultPlan is not null
                ? " Showing the completed step strategy."
                : _feasiblePlan is not null
                    ? " Showing the step upper-bound strategy."
                    : string.Empty;
            _statusLabel.Text = $"Stopped after {GetElapsedSeconds():F1} s.{shownDefault} {FormatSearchStatsSummary(_latestProgress, includeOutputStates: true)}. {FormatLiveDiagnosticsSummary(_latestProgress)}.";
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
            _activeBuilder = null;
            _activePhase = 0;
            _elapsedTimer.Stop();
            UpdateElapsedLabel();
            SetRunningState(isRunning: false);
            _runCancellationSource?.Dispose();
            _runCancellationSource = null;
        }
    }

    private void PopulateTree(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan, bool exactImproved, bool compactImproved)
    {
        _treeView.BeginUpdate();
        _treeView.Nodes.Clear();
        _stateNodesByKey.Clear();
        _referenceTargets.Clear();
        _navigationHistory.Clear();
        _backButton.Enabled = false;

        string rootLabel = BuildRootLabel(feasiblePlan, defaultPlan, compactPlan);
        string rootDetails = BuildRootDetails(feasiblePlan, defaultPlan, compactPlan, exactImproved, compactImproved);

        var root = new TreeNode(rootLabel)
        {
            Tag = rootDetails,
            NodeFont = new Font(_treeView.Font, FontStyle.Bold),
            ForeColor = _palette.ForeColor,
        };

        // Slot 0: the step strategy, named by mode -- "exact" once the exact pass finishes (it replaces
        // the placeholder in place), or "greedy" for the constructive feasible plan in greedy mode.
        StrategyPlan stepPlan = defaultPlan ?? feasiblePlan;
        string stepStageName = defaultPlan is null ? "greedy" : "exact";
        root.Nodes.Add(CreatePlanTreeRoot(stepStageName, stepPlan, "default", stepPlan.Elapsed));

        // Slot 1: "compact" -- minimizes displayed edges at the fixed step ceiling (placeholder until done).
        if (compactPlan is null)
            root.Nodes.Add(new TreeNode("compact: computing...") { ForeColor = _palette.MutedForeColor });
        else if (compactImproved)
            root.Nodes.Add(CreatePlanTreeRoot("compact", compactPlan, "compact", compactPlan.Elapsed));
        else
            root.Nodes.Add(CreateNoSolutionTreeRoot("compact", compactPlan.Elapsed));

        _treeView.Nodes.Add(root);
        root.Expand();

        _treeView.EndUpdate();
        _treeView.SelectedNode = root;

        RebuildOverview(feasiblePlan, defaultPlan, compactPlan, exactImproved, compactImproved);
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
        if (lower > 0 && lower == upper)
            return $"max steps = {upper} (proven optimal)";

        string lowerText = lower > 0 ? lower.ToString() : "?";
        return $"{lowerText} <= max steps <= {upper}";
    }

    private static string BuildRootLabel(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan)
    {
        string head = $"n={feasiblePlan.N}, m={feasiblePlan.M}, k={feasiblePlan.K}";
        if (defaultPlan is null)
            return $"{head}, {FormatPlanSqueeze(feasiblePlan)} (computing step...)";
        if (compactPlan is null)
        {
            double seconds = feasiblePlan.Elapsed.TotalSeconds + defaultPlan.Elapsed.TotalSeconds;
            return $"{head}, max steps={defaultPlan.MaxStep}, elapsed={seconds:F3} s (computing compact stage...)";
        }
        double totalSeconds = feasiblePlan.Elapsed.TotalSeconds + defaultPlan.Elapsed.TotalSeconds + compactPlan.Elapsed.TotalSeconds;
        // Lead with the optimality squeeze on the best plan: once the final tightening proves the next
        // step ceiling infeasible (the no-solution terminal), the incumbent's lower bound is closed to
        // its max-step and this reads "max steps = N (proven optimal)" -- the headline signal that the
        // search is done and the step count is provably best. While still tightening it reads
        // "L <= max steps <= U".
        return $"{head}, {FormatPlanSqueeze(compactPlan)}, total elapsed={totalSeconds:F3} s";
    }

    private static string BuildRootDetails(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan, bool exactImproved, bool compactImproved)
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
                PopulateTree(_feasiblePlan, defaultPlan, compactPlan, _exactImproved, compactImproved);
            return;
        }

        _treeView.BeginUpdate();

        TreeNode root = _treeView.Nodes[0];
        root.Text = BuildRootLabel(_feasiblePlan, defaultPlan, compactPlan);
        root.Tag = BuildTwoPhaseDetails(defaultPlan, compactPlan, compactImproved);

        // Replace only the trailing compact slot (everything after the single step slot).
        while (root.Nodes.Count > 1)
            root.Nodes.RemoveAt(root.Nodes.Count - 1);
        if (compactImproved)
            root.Nodes.Add(CreatePlanTreeRoot("compact", compactPlan, "compact", compactPlan.Elapsed));
        else
            root.Nodes.Add(CreateNoSolutionTreeRoot("compact", compactPlan.Elapsed));

        _treeView.EndUpdate();

        FinalizeCompactInOverview(compactPlan, compactImproved);
    }

    // Synchronous marshaling shim: BuildFeasibleCompactPlan invokes this on the worker thread once per
    // edge stage. Control.Invoke hops to the UI thread AND blocks the worker until OnGreedyEdgeStage
    // returns, so when the per-stage modal is enabled the search genuinely pauses until the user clicks OK.
    private void MarshalEdgeStage(GreedyEdgeStage stage)
    {
        if (!IsHandleCreated || IsDisposed)
            return;
        try
        {
            Invoke(() => OnGreedyEdgeStage(stage));
        }
        catch (ObjectDisposedException)
        {
            // Form closed mid-run; nothing to update.
        }
        catch (InvalidOperationException)
        {
            // Handle destroyed during shutdown.
        }
    }

    // Anytime greedy edge handler: invoked on the UI thread once per edge stage as the worker thread
    // produces it (baseline "compact" first, then each "compact<=N" tightening, finally a no-solution
    // terminal stage). The baseline fills the "compact" slot in place; every later stage is appended as
    // a new tree + overview section, so the user watches the strategy improve stage by stage. Each tree
    // gets a unique scope ("edge0", "edge1", ...) so their per-state navigation keys never collide.
    private void OnGreedyEdgeStage(GreedyEdgeStage stage)
    {
        if (_feasiblePlan is null || _treeView.Nodes.Count == 0)
            return;

        bool isBaseline = _greedyEdgeStages.Count == 0;
        _greedyEdgeStages.Add(stage);
        int index = _greedyEdgeStages.Count - 1;
        string scope = $"edge{index}";

        // A stage is "shown" as a full browsable tree only when it strictly improves the incumbent
        // (the best plan so far: the greedy feasible plan, then any improving compact stage). A stage
        // that has a solution but is no better (e.g. compact baseline = same steps, more edges than
        // greedy) is recorded and marked "no improvement" but rendered only as a leaf note. Tightening
        // continues regardless, since the next ceiling is driven by max-steps, not edges.
        StrategyPlan incumbent = _compactPlan ?? _feasiblePlan;
        bool improved = stage.HasSolution && stage.Plan!.IsStrictRefinementOver(incumbent);

        _treeView.BeginUpdate();
        TreeNode root = _treeView.Nodes[0];
        if (isBaseline)
        {
            // Replace the trailing "compact: computing..." placeholder (everything after the step slot).
            while (root.Nodes.Count > 1)
                root.Nodes.RemoveAt(root.Nodes.Count - 1);
        }
        root.Nodes.Add(BuildStageTreeNode(stage, scope, improved));

        if (improved)
            _compactPlan = stage.Plan;

        // A proven-infeasible terminal (NoSolution, not a timeout) proves the incumbent is optimal:
        // close its squeeze (opt = incumbent.MaxStep) so the progression detail reports proven optimal.
        if (stage.Outcome == GreedyEdgeStageOutcome.NoSolution)
            MarkGreedyIncumbentProvenOptimal();

        StrategyPlan shown = _compactPlan ?? _feasiblePlan;
        root.Text = BuildRootLabel(_feasiblePlan, _feasiblePlan, shown);
        root.Tag = BuildGreedyProgressionDetails(_feasiblePlan, _greedyEdgeStages);
        _treeView.EndUpdate();

        _overviewTree.BeginUpdate();
        if (isBaseline && _overviewTree.Nodes.Count > 0)
            _overviewTree.Nodes.RemoveAt(_overviewTree.Nodes.Count - 1);
        _overviewTree.Nodes.Add(BuildStageOverviewNode(stage, scope, improved));
        _overviewTree.EndUpdate();

        if (stage.HasSolution)
        {
            _latestProgress = CreateSnapshotFromPlan(stage.Plan!);
            if (improved)
                UpdateSummaryText(_feasiblePlan, defaultPlan: _feasiblePlan, compactPlan: stage.Plan, compactImproved: true);
            // The next tightening probe targets one below this stage's max-step. Reset the per-stage
            // clock so the progress panel times the upcoming probe from zero. This happens whether or
            // not the stage improved edges, because tightening continues either way.
            _currentStageName = stage.Plan!.MaxStep > 1 ? $"compact\u2264{stage.Plan.MaxStep - 1}" : "compact";
            _stageStartMs = _runStopwatch?.ElapsedMilliseconds ?? 0;
        }
        UpdateStatsPanels();
        UpdateElapsedLabel();

        // Optional pause-on-each-stage: a modal blocks this UI-thread handler (and therefore the worker
        // thread waiting in Invoke) until the user acknowledges the stage.
        if (_pauseEachStageCheckBox.Checked)
        {
            string? marker = stage.HasSolution
                ? (!improved ? "no improvement" : null)
                : (stage.TimedOut ? "timed out" : null);
            ShowStageModal(FormatStageRootLabel(stage.Name, stage.Elapsed, stage.Plan, marker), stage.HasSolution);
        }
    }

    // Closes the squeeze on the greedy incumbent (the best plan so far) to a proven optimum after a
    // tightening probe proved the next ceiling infeasible: opt = incumbent.MaxStep. Rewrites the
    // incumbent plan reference (_compactPlan, or _feasiblePlan when no edge stage improved) and the
    // matching entry in _greedyEdgeStages so the rebuilt progression detail reports "proven optimal".
    private void MarkGreedyIncumbentProvenOptimal()
    {
        if (_feasiblePlan is null)
            return;

        StrategyPlan incumbent = _compactPlan ?? _feasiblePlan;
        int provenLower = incumbent.MaxStep;
        if (incumbent.SearchStatistics.RootProvenLowerBound >= provenLower)
            return;

        StrategyPlan proven = incumbent.WithRootProvenLowerBound(provenLower);
        if (_compactPlan is not null)
        {
            for (int i = 0; i < _greedyEdgeStages.Count; i++)
            {
                if (ReferenceEquals(_greedyEdgeStages[i].Plan, incumbent))
                {
                    GreedyEdgeStage s = _greedyEdgeStages[i];
                    _greedyEdgeStages[i] = new GreedyEdgeStage(s.Name, proven, s.Elapsed, s.Outcome);
                    break;
                }
            }
            _compactPlan = proven;
        }
        else
        {
            _feasiblePlan = proven;
        }
    }

    private TreeNode BuildStageTreeNode(GreedyEdgeStage stage, string scope, bool improved)
        => improved
            ? CreatePlanTreeRoot(stage.Name, stage.Plan!, scope, stage.Elapsed)
            : stage.HasSolution
                ? CreateNoImprovementTreeRoot(stage.Name, stage.Plan!, stage.Elapsed)
                : CreateNoSolutionTreeRoot(stage.Name, stage.Elapsed, stage.TimedOut ? "timed out" : null);

    private TreeNode BuildStageOverviewNode(GreedyEdgeStage stage, string scope, bool improved)
        => improved
            ? BuildOverviewSectionNode(stage.Plan!, scope, stage.Name, stage.Elapsed)
            : BuildOverviewNoteNode(FormatStageRootLabel(
                stage.Name,
                stage.Elapsed,
                stage.Plan,
                stage.HasSolution ? "no improvement" : (stage.TimedOut ? "timed out" : null)));

    private void ShowStageModal(string message, bool hasSolution)
    {
        // Pause the run clock while the modal is up: the time the user spends in the dialog must
        // count toward neither the total elapsed nor the current stage's clock. Stopwatch.Start()
        // resumes (does not reset), so accumulated time is preserved and the next stage still times
        // from zero. The 100ms elapsed-timer keeps ticking inside the modal's message loop, but with
        // the stopwatch stopped it simply renders a frozen value.
        bool wasRunning = _runStopwatch?.IsRunning ?? false;
        if (wasRunning)
            _runStopwatch!.Stop();
        try
        {
            MessageBox.Show(
                this,
                message,
                "Stage complete",
                MessageBoxButtons.OK,
                hasSolution ? MessageBoxIcon.Information : MessageBoxIcon.None);
        }
        finally
        {
            if (wasRunning)
                _runStopwatch!.Start();
        }
    }

    // Root-node detail text for greedy mode: the step plan followed by the full edge progression
    // (compact baseline -> each tightening -> any no-solution stage), so the detail pane mirrors the
    // stacked trees.
    private static string BuildGreedyProgressionDetails(StrategyPlan stepPlan, List<GreedyEdgeStage> stages)
    {
        var lines = new List<string>
        {
            "Greedy result (anytime: improving stages are shown as trees)",
            $"greedy: {FormatPlanSqueeze(stepPlan)}, total edges={stepPlan.TotalBranchEdges}",
        };
        StrategyPlan incumbent = stepPlan;
        foreach (GreedyEdgeStage stage in stages)
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
            else if (stage.TimedOut)
            {
                lines.Add($"{stage.Name}: time out (probe abandoned on the time budget; best plan kept)");
            }
            else
            {
                lines.Add($"{stage.Name}: no solution (no better strategy at this step ceiling)");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

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
        _navigationHistory.Clear();
        _backButton.Enabled = false;
    }

    // Renders the overview panel so it mirrors the tree one-to-one: a step section (named by mode --
    // "exact"/"greedy") and a "compact" section ("computing..." placeholder until the compact stage
    // finishes). Each section is an independent root, so the strategies' overviews can be browsed and
    // collapsed separately. This is the full-rebuild path used for the initial render and theme switches.
    private void RebuildOverview(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan, bool exactImproved, bool compactImproved)
    {
        _overviewTree.BeginUpdate();
        _overviewTree.Nodes.Clear();

        StrategyPlan stepPlan = defaultPlan ?? feasiblePlan;
        string stepStageName = defaultPlan is null ? "greedy" : "exact";
        _overviewTree.Nodes.Add(BuildOverviewSectionNode(stepPlan, "default", stepStageName, stepPlan.Elapsed));

        if (compactPlan is null)
            _overviewTree.Nodes.Add(BuildOverviewNoteNode("compact: computing..."));
        else if (compactImproved)
            _overviewTree.Nodes.Add(BuildOverviewSectionNode(compactPlan, "compact", "compact", compactPlan.Elapsed));
        else
            _overviewTree.Nodes.Add(BuildOverviewNoteNode(FormatStageRootLabel("compact", compactPlan.Elapsed, plan: null)));

        _overviewTree.EndUpdate();
    }

    // Incrementally folds the finished compact result into the overview, mirroring the tree update:
    // the step section (0) -- and the user's expand/scroll state over it -- is left untouched, and
    // only the trailing compact placeholder root (1) is replaced.
    private void FinalizeCompactInOverview(StrategyPlan compactPlan, bool compactImproved)
    {
        _overviewTree.BeginUpdate();

        // Drop the trailing compact "computing..." placeholder root.
        if (_overviewTree.Nodes.Count > 0)
            _overviewTree.Nodes.RemoveAt(_overviewTree.Nodes.Count - 1);

        if (compactImproved)
            _overviewTree.Nodes.Add(BuildOverviewSectionNode(compactPlan, "compact", "compact", compactPlan.Elapsed));
        else
            _overviewTree.Nodes.Add(BuildOverviewNoteNode(FormatStageRootLabel("compact", compactPlan.Elapsed, plan: null)));

        _overviewTree.EndUpdate();
    }

    private TreeNode BuildOverviewSectionNode(StrategyPlan plan, string scope, string stageName, TimeSpan elapsed)
    {
        var sectionNode = new TreeNode(FormatStageRootLabel(stageName, elapsed, plan))
        {
            NodeFont = new Font(_overviewTree.Font, FontStyle.Bold),
            ForeColor = _palette.ForeColor,
        };

        foreach (OverviewRow row in StrategyOverviewRenderer.Build(plan).Rows)
        {
            string? key = row.LinkStateId is int id ? $"{scope}:{id}" : null;
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

        sectionNode.Expand();
        return sectionNode;
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

        if (!_stateNodesByKey.TryGetValue(targetStateKey, out TreeNode? targetNode))
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

    // The single unified stage-root label used by BOTH the strategy tree plan roots and the overview
    // section roots: "<stage>: elapsed=<s>.3f s, max steps=<n>, edges=<n>, output=<n>", optionally
    // suffixed with a marker (e.g. "no improvement"). When there is no plan the body collapses to the
    // marker note ("no solution" by default, or e.g. "timed out"). elapsed is the stage's own wall time
    // in seconds.
    private static string FormatStageRootLabel(string stageName, TimeSpan elapsed, StrategyPlan? plan, string? marker = null)
    {
        string elapsedText = $"elapsed={elapsed.TotalSeconds:F3} s";
        if (plan is null)
            return $"{stageName}: {elapsedText}, {marker ?? "no solution"}";
        string body = $"{stageName}: {elapsedText}, max steps={plan.MaxStep}, edges={plan.TotalBranchEdges}, output={plan.SearchStatistics.OutputStates}";
        return marker is null ? body : $"{body}, {marker}";
    }

    private TreeNode CreatePlanTreeRoot(string stageName, StrategyPlan plan, string scope, TimeSpan elapsed)
    {
        StrategyDepthIndex depthIndex = StrategyDepthIndex.Build(plan.Root);
        var planNode = new TreeNode(FormatStageRootLabel(stageName, elapsed, plan))
        {
            Tag = BuildPlanDetails(plan),
            NodeFont = new Font(_treeView.Font, FontStyle.Bold),
            ForeColor = _palette.ForeColor,
        };
        planNode.Nodes.Add(CreateStateNode(plan.Root, plan.K, scope, depthIndex));
        return planNode;
    }

    // A terminal stage that found no better strategy: a single bold leaf carrying the unified label with
    // the given marker ("no solution" when proven infeasible, "timed out" when the probe was abandoned on
    // the soft deadline) and no child strategy subtree.
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

        foreach (var branch in node.Branches)
        {
            string branchHeader = branch.OrderText;
            if (branch.EquivalentOrders is not null)
                branchHeader += $"  (×{branch.EquivalentOrders.Count})";

            var branchNode = new TreeNode(branchHeader)
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
        _stateNodesByKey.TryAdd($"{scope}:{node.StateId}", treeNode);
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

    // Persisted GUI settings: the inputs (n/m/k), mode + theme selections, and the per-stage pause
    // toggle, stored as JSON under %APPDATA%/Sort/settings.json so the form reopens exactly as the
    // user last left it.
    private sealed class GuiSettings
    {
        public string N { get; set; } = "25";
        public string M { get; set; } = "5";
        public string K { get; set; } = "5";
        public int ModeIndex { get; set; }
        public string Theme { get; set; } = nameof(ColorTheme.Dark);
        public bool PauseEachStage { get; set; }
    }

    private static string SettingsFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sort", "settings.json");

    // Applies any previously-saved settings to the controls. Best-effort: a missing or corrupt file
    // leaves the built-in defaults in place.
    private void LoadSettings()
    {
        try
        {
            string path = SettingsFilePath;
            if (!File.Exists(path))
                return;

            GuiSettings? settings = JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(path));
            if (settings is null)
                return;

            _nTextBox.Text = settings.N;
            _mTextBox.Text = settings.M;
            _kTextBox.Text = settings.K;
            if (settings.ModeIndex >= 0 && settings.ModeIndex < _modeComboBox.Items.Count)
                _modeComboBox.SelectedIndex = settings.ModeIndex;
            if (_themeComboBox.Items.Contains(settings.Theme))
                _themeComboBox.SelectedItem = settings.Theme;
            _pauseEachStageCheckBox.Checked = settings.PauseEachStage;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Ignore unreadable/corrupt settings and fall back to defaults.
        }
    }

    // Writes the current control state back to disk. Best-effort: failures (e.g. a read-only profile)
    // are swallowed so closing the form never throws.
    private void SaveSettings()
    {
        try
        {
            var settings = new GuiSettings
            {
                N = _nTextBox.Text,
                M = _mTextBox.Text,
                K = _kTextBox.Text,
                ModeIndex = _modeComboBox.SelectedIndex,
                Theme = ParseSelectedTheme().ToString(),
                PauseEachStage = _pauseEachStageCheckBox.Checked,
            };

            string path = SettingsFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore failures to persist settings.
        }
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

        if (_feasiblePlan is not null)
        {
            PopulateTree(_feasiblePlan, _defaultPlan, _compactPlan, _exactImproved, _compactImproved);
            if (_runCancellationSource is null)
                UpdateSummaryText(_feasiblePlan, _defaultPlan, _compactPlan, _compactImproved);
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
            case TextBox statBox when statBox.ReadOnly:
                statBox.BackColor = _palette.FormBackColor;
                statBox.ForeColor = _palette.ForeColor;
                statBox.BorderStyle = BorderStyle.None;
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
            splitContainer.BackColor = _palette.BorderColor;
            splitContainer.Panel1.BackColor = _palette.SurfaceBackColor;
            splitContainer.Panel1.ForeColor = _palette.ForeColor;
            splitContainer.Panel2.BackColor = _palette.SurfaceBackColor;
            splitContainer.Panel2.ForeColor = _palette.ForeColor;
        }

        foreach (Control child in control.Controls)
            ApplyThemeToControlTree(child);
    }

    private void UpdateSummaryText(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan, bool compactImproved)
    {
        string head = $"n={feasiblePlan.N}, m={feasiblePlan.M}, k={feasiblePlan.K}";

        if (defaultPlan is null)
        {
            _statusLabel.Text =
                $"{head}, {FormatPlanSqueeze(feasiblePlan)} (not proven optimal). Computing step...";
            return;
        }

        if (compactPlan is null)
        {
            double seconds = feasiblePlan.Elapsed.TotalSeconds + defaultPlan.Elapsed.TotalSeconds;
            _statusLabel.Text =
                $"{head}, step max={defaultPlan.MaxStep}, elapsed={seconds:F3} s. Computing compact stage...";
            return;
        }

        double totalElapsedSeconds = feasiblePlan.Elapsed.TotalSeconds + defaultPlan.Elapsed.TotalSeconds + compactPlan.Elapsed.TotalSeconds;
        string compactText;
        if (compactPlan.MaxStep < defaultPlan.MaxStep)
            compactText = $"compact lowered max steps {defaultPlan.MaxStep} -> {compactPlan.MaxStep} (edges {defaultPlan.TotalBranchEdges} -> {compactPlan.TotalBranchEdges})";
        else if (compactImproved)
            compactText = $"compact reduced total edges {defaultPlan.TotalBranchEdges} -> {compactPlan.TotalBranchEdges}";
        else
            compactText = $"compact produced no better result (step total edges {defaultPlan.TotalBranchEdges}, compact {compactPlan.TotalBranchEdges})";
        _statusLabel.Text =
            $"{head}, total elapsed={totalElapsedSeconds:F3} s, " +
            $"max steps={compactPlan.MaxStep}, {compactText}.";
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

    private enum RunUiState
    {
        Idle,
        Running,
        CompactComputingInteractive,
    }

    private void SetRunningState(bool isRunning)
    {
        SetRunUiState(isRunning ? RunUiState.Running : RunUiState.Idle);
    }

    // Three-state UI model. While phase 1 (default) runs the whole form shows the wait cursor and
    // all result-interaction buttons are disabled. Once the default plan is on screen but the
    // compact refinement is still computing on a background thread, the UI thread is free, so we
    // drop the wait cursor and re-enable tree navigation -- only Run stays disabled (a new search
    // cannot start) while Stop can still cancel the compact phase.
    private void SetRunUiState(RunUiState state)
    {
        bool running = state != RunUiState.Idle;
        bool interactive = state != RunUiState.Running;

        UseWaitCursor = state == RunUiState.Running;
        _runButton.Enabled = !running;
        _stopButton.Enabled = running;
        _modeComboBox.Enabled = !running;
        _treeExpandButton.Enabled = interactive;
        _treeCollapseButton.Enabled = interactive;
        _overviewExpandButton.Enabled = interactive;
        _overviewCollapseButton.Enabled = interactive;
        _backButton.Enabled = interactive && _navigationHistory.Count > 0;
    }

    private void UpdateElapsedLabel()
    {
        long totalMs = (long)((_runStopwatch?.Elapsed.TotalMilliseconds) ?? 0);

        // Four-line panel, all per the current stage except the first line (cumulative total):
        //   <total> s
        //   <stage name>: <stage-own elapsed> s
        //   progress: <current stage %>
        //   eta / time remaining: <current stage remaining>
        // The stage clock counts from _stageStartMs (reset at every stage boundary), so it always
        // reports the running stage's own time rather than a cumulative figure.
        double totalSeconds = totalMs / 1000.0;
        double stageSeconds = Math.Max(0, totalMs - _stageStartMs) / 1000.0;

        // During a compact<=N tightening probe the fourth line is the EXACT time left until the soft
        // budget times out -- polled live from the engine deadline -- rather than the unreliable
        // progress-based ETA. Outside tightening (no active deadline) fall back to the ETA estimate.
        bool isTightening = _currentStageName.StartsWith("compact\u2264", StringComparison.Ordinal);
        TimeSpan? budgetRemaining = isTightening ? _activeBuilder?.TighteningTimeRemaining : null;

        string etaLabel;
        string etaLineValue;
        if (budgetRemaining is { } remaining)
        {
            etaLabel = "time remaining";
            etaLineValue = $"{remaining.TotalSeconds:F3} s";
        }
        else
        {
            etaLabel = "eta";
            double etaSeconds = EstimateLiveEtaSeconds(totalMs);
            etaLineValue = etaSeconds >= 0 ? $"{etaSeconds:F3} s" : "-";
        }

        string text =
            $"{totalSeconds:F3} s\n" +
            $"{_currentStageName}: {stageSeconds:F3} s\n" +
            $"progress: {_latestProgress.EstimatedProgress01 * 100.0:F1}%\n" +
            $"{etaLabel}: {etaLineValue}";
        SetStatText(_progressTextBox, text);
    }

    private void UpdateSearchProgress(SearchProgressSnapshot snapshot)
    {
        _latestProgress = snapshot;
        UpdateStatsPanels();
        string incumbent = snapshot.LatestRootIncumbent is null
            ? "incumbent: -"
            : $"incumbent: <= {snapshot.LatestRootIncumbent.BestWorstCaseSteps}";
        double statusEtaSeconds = EstimateLiveEtaSeconds((long)(GetElapsedSeconds() * 1000));
        string etaText = statusEtaSeconds >= 0 ? $"{statusEtaSeconds:F1} s" : "-";
        _statusLabel.Text = $"Running (phase {GetPhaseLabel()})... elapsed: {GetElapsedSeconds():F1} s, searched: {snapshot.SearchedStates}, {FormatSqueeze(snapshot)}, {incumbent}, " +
            $"progress: {snapshot.EstimatedProgress01 * 100.0:F1}%, eta: {etaText}.";
        _detailsTextBox.Text = BuildLiveDiagnosticsText(snapshot);
    }

    // Updates the three live stat panels (States / Work / Progress) from the latest snapshot.
    // Each metric lives in exactly one panel so the panels do not duplicate one another.
    private void UpdateStatsPanels()
    {
        SearchProgressSnapshot p = _latestProgress;

        // During the edge phase the step counters (searched/pending/output/...) are frozen at 0, so
        // repurpose the States panel to surface the compact solve's live progress instead of a dead
        // all-zero block. The "solved / ~estimate (pct%)" denominator comes from the step phase's
        // distinct-state count (CompactStateEstimate); when unknown (-1) we just show the raw count.
        if (Volatile.Read(ref _activePhase) == 2)
        {
            string solvedLine = p.CompactStateEstimate > 0
                ? $"compact solved: {p.CompactStatesSolved} ({EdgeLocalFraction(p) * 100.0:F1}%)"
                : $"compact solved: {p.CompactStatesSolved}";
            SetStatText(_statesTextBox,
                solvedLine + "\n" +
                $"compact groups: {p.CompactGroupsEnumerated} ({p.CompactStepOptimalGroups} opt)\n" +
                $"(step) output: {p.OutputStates}\n" +
                $"(step) lower-bound: {p.LowerBoundStates}\n" +
                $"(step) top-set: {p.FeasibleTopSetStates}");
        }
        else
        {
            SetStatText(_statesTextBox,
                $"searched: {p.SearchedStates}\n" +
                $"pending: {p.PendingStates} (peak {p.PeakPendingStates})\n" +
                $"output: {p.OutputStates}\n" +
                $"lower-bound: {p.LowerBoundStates}\n" +
                $"top-set: {p.FeasibleTopSetStates}");
        }

        string edgeText = p.CompactStatesSolved > 0
            ? $"[compact] {p.CompactStatesSolved} solved, {p.CompactGroupsEnumerated} groups ({p.CompactStepOptimalGroups} opt)"
            : "[compact] -";
        SetStatText(_workTextBox,
            $"outcomes: {p.OutcomesConstructed} (cand groups {p.CandidateGroupsEnumerated})\n" +
            $"duplicate skips: {p.DuplicateOutcomeSkips}\n" +
            $"merged collisions: {p.MergedOutcomeCollisions}\n" +
            $"prunes: {p.LowerBoundPrunes}\n" +
            $"cache: {p.ExactCacheHits}/{p.LowerBoundCacheHits}/{p.FeasibleTopSetCacheHits}/{p.BestGroupPatternCacheHits}\n" +
            edgeText);
    }

    // Local 0..1 fraction of the edge phase's compact solve, mirroring the engine's own self-correcting
    // asymptote (TopKFinder.EstimateProgress): solved / (solved + scale), where the scale is the step
    // phase's state-count estimate. Strictly increasing, always below 1, and never stuck even when the
    // scale badly under/over-shoots the true edge work.
    private static double EdgeLocalFraction(SearchProgressSnapshot p)
    {
        if (p.CompactStateEstimate <= 0)
            return 0.0;
        double scale = p.CompactStateEstimate;
        double fraction = p.CompactStatesSolved / (p.CompactStatesSolved + scale);
        return Math.Min(fraction, 0.999);
    }
    private static string FormatSearchStatsSummary(SearchProgressSnapshot snapshot, bool includeOutputStates)
    {
        string outputText = includeOutputStates ? $", output={snapshot.OutputStates}" : string.Empty;
        return $"searched={snapshot.SearchedStates}, pending={snapshot.PendingStates}, peak pending={snapshot.PeakPendingStates}{outputText}";
    }

    private static SearchProgressSnapshot CreateInitialProgressSnapshot()
    {
        return new SearchProgressSnapshot(0, 0, 0, 0, 0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, 0.0, -1, 0);
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
            plan.SearchStatistics.CompactStepOptimalGroups,
            plan.SearchStatistics.CompactStatesSolved,
            1.0,
            0,
            plan.SearchStatistics.RootProvenLowerBound);
    }

    // The label shown in the status bar's "Running (phase ...)" text: the current stage name, with a
    // 1/2 or 2/2 prefix so the user sees overall progress through the two-phase run.
    private string GetPhaseLabel()
    {
        int phase = Volatile.Read(ref _activePhase);
        string prefix = phase >= 2 ? "2/2" : "1/2";
        return $"{prefix} {_currentStageName}";
    }

    private static string BuildFeasibleOnlyDetails(StrategyPlan feasiblePlan)
    {
        string feasibleText = StrategyTextRenderer.Render(feasiblePlan).TrimEnd();
        var lines = new List<string>
        {
            "Step strategy (greedy upper bound; next stage in progress)",
            $"squeeze: {FormatPlanSqueeze(feasiblePlan)}  (not proven optimal)",
            $"step elapsed: {feasiblePlan.Elapsed.TotalSeconds:F3} s",
            $"step total edges: {feasiblePlan.TotalBranchEdges}",
            $"step output states: {feasiblePlan.SearchStatistics.OutputStates}",
            $"max steps (upper bound): {feasiblePlan.MaxStep}",
            string.Empty,
            "----- step -----",
            feasibleText,
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildDefaultOnlyDetails(StrategyPlan defaultPlan)
    {
        string defaultText = StrategyTextRenderer.Render(defaultPlan).TrimEnd();
        var lines = new List<string>
        {
            "Step result (compact stage in progress)",
            $"step elapsed: {defaultPlan.Elapsed.TotalSeconds:F3} s",
            $"step total edges: {defaultPlan.TotalBranchEdges}",
            $"step output states: {defaultPlan.SearchStatistics.OutputStates}",
            $"max steps: {defaultPlan.MaxStep}",
            string.Empty,
            "----- step -----",
            defaultText,
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildTwoPhaseDetails(StrategyPlan defaultPlan, StrategyPlan compactPlan, bool compactImproved)
    {
        string defaultText = StrategyTextRenderer.Render(defaultPlan).TrimEnd();
        string compactText = StrategyTextRenderer.Render(compactPlan).TrimEnd();
        double totalElapsedSeconds = defaultPlan.Elapsed.TotalSeconds + compactPlan.Elapsed.TotalSeconds;
        var lines = new List<string>
        {
            "Two-stage result",
            $"total elapsed: {totalElapsedSeconds:F3} s",
            $"step total edges: {defaultPlan.TotalBranchEdges}",
            $"compact total edges: {compactPlan.TotalBranchEdges}",
            $"step output states: {defaultPlan.SearchStatistics.OutputStates}",
            $"compact output states: {compactPlan.SearchStatistics.OutputStates}",
            compactImproved
                ? "compact improvement: yes"
                : "compact improvement: no",
            string.Empty,
            "----- step -----",
            defaultText,
        };

        if (compactImproved)
        {
            lines.Add(string.Empty);
            lines.Add("----- compact -----");
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
            $"  cache hits = exact {diagnostics.ExactCacheHits}, lower-bound {diagnostics.LowerBoundCacheHits}, top-set {diagnostics.FeasibleTopSetCacheHits}, best-group-pattern {diagnostics.BestGroupPatternCacheHits}",
        };

        if (diagnostics.RootIncumbents.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Root incumbent timeline:");
            foreach (SearchMilestone milestone in diagnostics.RootIncumbents)
            {
                lines.Add(
                    $"  {milestone.ElapsedMilliseconds / 1000.0:F1}s: max steps <= {milestone.BestWorstCaseSteps} via {milestone.ComparisonGroupText} " +
                    $"(searched {milestone.SearchedStates}, pending {milestone.PendingStates}, peak {milestone.PeakPendingStates}, output {milestone.OutputStates}, prunes {milestone.LowerBoundPrunes})");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private string BuildLiveDiagnosticsText(SearchProgressSnapshot snapshot)
    {
        var lines = new System.Collections.Generic.List<string>
        {
            "Live search diagnostics",
            $"elapsed: {snapshot.ElapsedMilliseconds / 1000.0:F1} s",
            $"{FormatSqueeze(snapshot)}",
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
            lines.Add($"latest incumbent: max steps <= {latest.BestWorstCaseSteps} via {latest.ComparisonGroupText}");
            lines.Add(
                $"  found at t={latest.ElapsedMilliseconds / 1000.0:F1}s, searched={latest.SearchedStates}, output={latest.OutputStates}, prunes={latest.LowerBoundPrunes}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private string FormatLiveDiagnosticsSummary(SearchProgressSnapshot snapshot)
    {
        string incumbentText = snapshot.LatestRootIncumbent is null
            ? "incumbent: -"
            : $"incumbent: <= {snapshot.LatestRootIncumbent.BestWorstCaseSteps}";
        return $"{FormatSqueeze(snapshot)}, {incumbentText}, milestones: {snapshot.RootIncumbentCount}, prunes: {snapshot.LowerBoundPrunes}, cache hits: {snapshot.ExactCacheHits}/{snapshot.LowerBoundCacheHits}/{snapshot.FeasibleTopSetCacheHits}/{snapshot.BestGroupPatternCacheHits}";
    }

    // Formats the squeeze on the optimal max-step count: L is the proven lower bound
    // (RootProvenLowerBound), U is the best incumbent worst-case steps. Either side may be unknown
    // ("?") early on; when both are known and equal the optimum is proven exactly. The value is a
    // global result of the phase-1 solve and stays fixed through the compact phase, so it is shown
    // as "step-opt" rather than tagged to a single phase.
    private string FormatSqueeze(SearchProgressSnapshot snapshot)
    {
        int lower = snapshot.RootProvenLowerBound;
        int? incumbent = snapshot.LatestRootIncumbent?.BestWorstCaseSteps;
        // The greedy feasible plan (phase 0) already gives a valid achievable upper bound, so even
        // before the exact search produces an incumbent the U side is known: take the tighter of the
        // two whenever both are present.
        int? feasibleUpper = _feasiblePlan?.MaxStep;
        int? upper = (incumbent, feasibleUpper) switch
        {
            (int a, int b) => Math.Min(a, b),
            (int a, null) => a,
            (null, int b) => b,
            _ => (int?)null,
        };
        if (lower > 0 && upper is int u && lower == u)
            return $"max steps = {lower} (proven)";

        string lowerText = lower > 0 ? lower.ToString() : "?";
        string upperText = upper?.ToString() ?? "?";
        return $"{lowerText} <= max steps <= {upperText}";
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

    // ETA derived from the live elapsed clock and the latest reported progress, in seconds, or -1
    // when no usable estimate exists yet. Recomputing from the live clock (instead of echoing the
    // snapshot's frozen remaining estimate) keeps the three displayed quantities -- elapsed,
    // progress, eta -- mutually consistent and lets eta count down on every elapsed tick rather than
    // only when a new progress snapshot arrives. Assuming roughly linear progress, total =
    // elapsed / progress, so remaining = elapsed * (1 - progress) / progress. The engine's own
    // remaining estimate still gates visibility: a negative value means "no usable estimate yet".
    private double EstimateLiveEtaSeconds(long liveElapsedMs)
    {
        if (_latestProgress.EstimatedRemainingMilliseconds < 0)
            return -1;
        double progress = _latestProgress.EstimatedProgress01;
        if (progress <= 0.0)
            return -1;
        if (progress >= 1.0)
            return 0.0;
        return liveElapsedMs * (1.0 - progress) / progress / 1000.0;
    }

    private void ShowNodeDetails(TreeNode? node)
    {
        _detailsTextBox.Clear();
        if (node?.Tag is not string text)
            return;

        _detailsTextBox.Text = text;
    }
}
