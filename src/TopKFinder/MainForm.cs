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

namespace TopKFinder;

partial class MainForm : Form
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

    // Cached brush for the tree row background, recreated whenever the theme changes so DrawNode does not
    // allocate a brush per paint. Selected rows use the shared SystemBrushes.Highlight instead.
    private SolidBrush _treeRowBackBrush = new(DarkPalette.SurfaceBackColor);
    private StrategyPlan? _feasiblePlan;
    private StrategyPlan? _defaultPlan;
    private StrategyPlan? _compactPlan;
    private bool _compactImproved;
    private bool _feasibleMode;
    private Stopwatch? _runStopwatch;
    private CancellationTokenSource? _runCancellationSource;
    private CancellationTokenSource? _stopEscalationSource;
    private StrategyBuilder? _activeBuilder;
    private static readonly DisplayRenderEngine DisplayEngine = new();
    private int _detailsRequestVersion;
    private SearchProgressSnapshot _latestProgress;
    private SearchStatistics? _completedDefaultStats;
    private SearchStatistics? _completedCompactStats;
    private SearchStatistics? _completedFeasibleStats;
    private int _activePhase;
    // Anytime greedy edge state (UI thread only): every edge stage as it arrives (baseline compact,
    // each tightening, plus a terminal no-solution stage). The current-stage name and the run-clock ms
    // at which the current stage began drive the per-stage timing/labels in the progress panel.
    private readonly List<StageResult> _proofTightenStages = new();
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

        // Search mode matches the UI labels: exact (proven) and greedy (fast).
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
            "outcomes: 0\nduplicate skips: 0\nmerged collisions: 0\nprunes: 0\ncache: 0/0/0/0\n[edge-compact] -");
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
            DrawMode = TreeViewDrawMode.OwnerDrawText,
            Font = new Font(FontFamily.GenericSansSerif, 10),
        };
        _treeView.AfterSelect += (_, e) => ShowNodeDetails(e.Node);
        _treeView.DrawNode += TreeView_DrawNode;
        _treeView.BeforeExpand += (_, e) => { if (e.Node is { } n) MaterializeDecision(n); };
        _treeView.NodeMouseDoubleClick += (_, e) => TryJumpToReferenceTarget(e.Node);
        _treeView.MouseDown += TreeView_MouseDown;
        _treeView.KeyDown += TreeView_KeyDown;
        _treeView.ContextMenuStrip = CreateTreeContextMenu();
        _treeExpandButton.Click += (_, _) => ExpandEntireTree();
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
        _overviewTree.BeforeExpand += (_, e) => { if (e.Node is { } n) MaterializeOverviewSection(n); };
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

}
