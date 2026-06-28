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

        _progressTextBox = CreateStatTextBox(
            "0.000 s\nstep-opt: ? <= opt <= ?\ndefault: -\ncompact: -\nprogress: 0.0%\neta: -",
            new Font(Font.FontFamily, 11, FontStyle.Bold));
        _statesTextBox = CreateStatTextBox(
            "(cumulative: default + compact)\nsearched: 0\npending: 0 (peak 0)\noutput: 0\nlower-bound: 0\nfeasible-top-set: 0");
        _workTextBox = CreateStatTextBox(
            "(cumulative: default + compact)\noutcomes: 0\nduplicate skips: 0\nmerged collisions: 0\nprunes: 0\ncache: 0/0/0/0\n[compact] -");
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
        innerSplit.Panel1.Controls.Add(CreateTreeRegion(_treeView, _treeExpandButton, _treeCollapseButton));

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
        ApplyTheme(ColorTheme.Dark);
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

        // The builder is shared across both phases so the compact pass reuses the phase-1 search
        // caches the default pass already populated.
        var builder = new StrategyBuilder(
            n,
            m,
            k,
            cancellationToken,
            snapshot => progress.Report(snapshot),
            reportCombinedRunProgress: true);
        try
        {
            // Phase 1: the default plan is already MaxStep-optimal (the compact pass only trims
            // edges among equally-optimal groups), so render it as soon as it is ready instead of
            // blocking the whole view on the compact refinement.
            StrategyPlan defaultPlan = await Task.Run(() => builder.BuildDefaultPlan(), cancellationToken);
            Interlocked.Exchange(ref _phase1ElapsedMs, _runStopwatch?.ElapsedMilliseconds ?? 0);

            _defaultPlan = defaultPlan;
            _latestProgress = CreateSnapshotFromPlan(defaultPlan);
            PopulateTree(defaultPlan, compactPlan: null, compactImproved: false);
            _completedDefaultStats = defaultPlan.SearchStatistics;
            UpdateSummaryText(defaultPlan, compactPlan: null, compactImproved: false);
            UpdateStatsPanels();

            // The default plan is fully rendered; the compact pass now runs on a background thread.
            // Free the UI: drop the wait cursor and re-enable tree navigation while it computes.
            SetRunUiState(RunUiState.CompactComputingInteractive);

            // Phase 2: compact refinement.
            Interlocked.Exchange(ref _activePhase, 2);
            StrategyPlan compactPlan = await Task.Run(() => builder.BuildCompactPlan(), cancellationToken);
            _runStopwatch?.Stop();

            _compactPlan = compactPlan;
            _compactImproved =
                compactPlan.MaxStep == defaultPlan.MaxStep &&
                compactPlan.TotalBranchEdges < defaultPlan.TotalBranchEdges;

            _latestProgress = CreateSnapshotFromPlan(compactPlan);
            FinalizeCompactInTree(defaultPlan, compactPlan, _compactImproved);
            _completedCompactStats = compactPlan.SearchStatistics;
            UpdateSummaryText(defaultPlan, compactPlan, _compactImproved);
            UpdateStatsPanels();
        }
        catch (OperationCanceledException)
        {
            _runStopwatch?.Stop();
            string shownDefault = _defaultPlan is not null
                ? " Showing the completed default strategy."
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
            _activePhase = 0;
            _elapsedTimer.Stop();
            UpdateElapsedLabel();
            SetRunningState(isRunning: false);
            _runCancellationSource?.Dispose();
            _runCancellationSource = null;
        }
    }

    private void PopulateTree(StrategyPlan defaultPlan, StrategyPlan? compactPlan, bool compactImproved)
    {
        _treeView.BeginUpdate();
        _treeView.Nodes.Clear();
        _stateNodesByKey.Clear();
        _referenceTargets.Clear();
        _navigationHistory.Clear();
        _backButton.Enabled = false;

        string rootLabel;
        string rootDetails;
        if (compactPlan is null)
        {
            rootLabel =
                $"n={defaultPlan.N}, m={defaultPlan.M}, k={defaultPlan.K}, " +
                $"default elapsed={defaultPlan.Elapsed.TotalMilliseconds:F1} ms (computing compact refinement...)";
            rootDetails = BuildDefaultOnlyDetails(defaultPlan);
        }
        else
        {
            double totalElapsedMs = defaultPlan.Elapsed.TotalMilliseconds + compactPlan.Elapsed.TotalMilliseconds;
            rootLabel = $"n={defaultPlan.N}, m={defaultPlan.M}, k={defaultPlan.K}, two-phase elapsed={totalElapsedMs:F1} ms";
            rootDetails = BuildTwoPhaseDetails(defaultPlan, compactPlan, compactImproved);
        }

        var root = new TreeNode(rootLabel)
        {
            Tag = rootDetails,
            NodeFont = new Font(_treeView.Font, FontStyle.Bold),
            ForeColor = _palette.ForeColor,
        };
        root.Nodes.Add(CreatePlanTreeRoot("default", defaultPlan, "default"));
        if (compactPlan is null)
            root.Nodes.Add(new TreeNode("compact refinement: computing...") { ForeColor = _palette.MutedForeColor });
        else if (compactImproved)
            root.Nodes.Add(CreatePlanTreeRoot("compact", compactPlan, "compact"));
        else
            root.Nodes.Add(new TreeNode("compact refinement: no better result (total edges unchanged or worse)") { ForeColor = _palette.MutedForeColor });
        _treeView.Nodes.Add(root);
        root.Expand();

        _treeView.EndUpdate();
        _treeView.SelectedNode = root;

        RebuildOverview(defaultPlan, compactPlan, compactImproved);
    }

    // Incrementally folds the finished compact result into the already-rendered default tree instead
    // of rebuilding from scratch. The default subtree (root.Nodes[0]) and its navigation map entries
    // are left untouched, so a user mid-browse keeps their expand/scroll/selection state. Only the
    // transient "compact refinement: computing..." placeholder (root.Nodes[1]) is replaced -- either
    // with the compact subtree (a sibling of default, scoped "compact" so its state keys never
    // collide with default's) when it improved, or with a "no better result" note when it did not.
    private void FinalizeCompactInTree(StrategyPlan defaultPlan, StrategyPlan compactPlan, bool compactImproved)
    {
        // Defensive fallback: if the tree was cleared/rebuilt out from under us (e.g. a theme switch
        // mid-compact), there is no default root to extend, so do a full rebuild.
        if (_treeView.Nodes.Count == 0)
        {
            PopulateTree(defaultPlan, compactPlan, compactImproved);
            return;
        }

        _treeView.BeginUpdate();

        TreeNode root = _treeView.Nodes[0];
        double totalElapsedMs = defaultPlan.Elapsed.TotalMilliseconds + compactPlan.Elapsed.TotalMilliseconds;
        root.Text = $"n={defaultPlan.N}, m={defaultPlan.M}, k={defaultPlan.K}, two-phase elapsed={totalElapsedMs:F1} ms";
        root.Tag = BuildTwoPhaseDetails(defaultPlan, compactPlan, compactImproved);

        // Replace only the placeholder/compact slot (everything after the default subtree).
        while (root.Nodes.Count > 1)
            root.Nodes.RemoveAt(root.Nodes.Count - 1);
        if (compactImproved)
            root.Nodes.Add(CreatePlanTreeRoot("compact", compactPlan, "compact"));
        else
            root.Nodes.Add(new TreeNode("compact refinement: no better result (total edges unchanged or worse)") { ForeColor = _palette.MutedForeColor });

        _treeView.EndUpdate();

        FinalizeCompactInOverview(compactPlan, compactImproved);
    }

    // Wraps a tree in a panel with a small top toolbar holding that tree's own Expand/Collapse
    // buttons, so each of the two tree regions (overview and strategy) is controlled independently.
    private static Panel CreateTreeRegion(TreeView tree, Button expandButton, Button collapseButton)
    {
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(2, 2, 2, 2),
            Margin = Padding.Empty,
        };
        toolbar.Controls.Add(expandButton);
        toolbar.Controls.Add(collapseButton);

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

    // Renders the overview panel so it mirrors the tree one-to-one: a collapsible "default strategy"
    // section node plus, depending on the compact phase, a "compact strategy" section node (when it
    // improved), a "no better result" note (when it did not), or a "computing..." placeholder (still
    // running). Each section is an independent root, so the two strategies' overviews can be browsed
    // and collapsed separately. This is the full-rebuild path used for the initial render and theme
    // switches.
    private void RebuildOverview(StrategyPlan defaultPlan, StrategyPlan? compactPlan, bool compactImproved)
    {
        _overviewTree.BeginUpdate();
        _overviewTree.Nodes.Clear();

        AppendOverviewSection(defaultPlan, "default", "default strategy overview");
        if (compactPlan is null)
            AppendOverviewNote("compact strategy overview: computing...");
        else if (compactImproved)
            AppendOverviewSection(compactPlan, "compact", "compact strategy overview");
        else
            AppendOverviewNote("compact strategy overview: no better result (total edges unchanged or worse)");

        _overviewTree.EndUpdate();
    }

    // Incrementally folds the finished compact result into the overview, mirroring the tree update:
    // the default section node (and the user's expand/scroll state over it) is left untouched, and
    // only the trailing "computing..." placeholder root is replaced with the compact section node or
    // a "no better result" note.
    private void FinalizeCompactInOverview(StrategyPlan compactPlan, bool compactImproved)
    {
        _overviewTree.BeginUpdate();

        // Drop the "computing..." placeholder root appended during the default render.
        if (_overviewTree.Nodes.Count > 0)
            _overviewTree.Nodes.RemoveAt(_overviewTree.Nodes.Count - 1);

        if (compactImproved)
            AppendOverviewSection(compactPlan, "compact", "compact strategy overview");
        else
            AppendOverviewNote("compact strategy overview: no better result (total edges unchanged or worse)");

        _overviewTree.EndUpdate();
    }

    private void AppendOverviewSection(StrategyPlan plan, string scope, string title)
    {
        var sectionNode = new TreeNode(title)
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

        _overviewTree.Nodes.Add(sectionNode);
        sectionNode.Expand();
    }

    private void AppendOverviewNote(string text)
    {
        _overviewTree.Nodes.Add(new TreeNode(text)
        {
            ForeColor = _palette.MutedForeColor,
        });
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

    private TreeNode CreatePlanTreeRoot(string label, StrategyPlan plan, string scope)
    {
        StrategyDepthIndex depthIndex = StrategyDepthIndex.Build(plan.Root);
        var planNode = new TreeNode(
            $"{label}: elapsed={plan.Elapsed.TotalMilliseconds:F1} ms, worst-case steps={plan.MaxStep}, edges={plan.TotalBranchEdges}, output={plan.SearchStatistics.OutputStates}")
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
        else if (_defaultPlan is not null)
        {
            PopulateTree(_defaultPlan, compactPlan: null, compactImproved: false);
            if (_runCancellationSource is null)
                UpdateSummaryText(_defaultPlan, compactPlan: null, compactImproved: false);
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

    private void UpdateSummaryText(StrategyPlan defaultPlan, StrategyPlan? compactPlan, bool compactImproved)
    {
        if (compactPlan is null)
        {
            _statusLabel.Text =
                $"n={defaultPlan.N}, m={defaultPlan.M}, k={defaultPlan.K}, " +
                $"default elapsed={defaultPlan.Elapsed.TotalMilliseconds:F1} ms, " +
                $"worst-case steps={defaultPlan.MaxStep}. Computing compact refinement...";
            return;
        }

        double totalElapsedMs = defaultPlan.Elapsed.TotalMilliseconds + compactPlan.Elapsed.TotalMilliseconds;
        string compactText = compactImproved
            ? $"compact reduced total edges {defaultPlan.TotalBranchEdges} -> {compactPlan.TotalBranchEdges}"
            : $"compact produced no better result (default total edges {defaultPlan.TotalBranchEdges}, compact {compactPlan.TotalBranchEdges})";
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
        _treeExpandButton.Enabled = interactive;
        _treeCollapseButton.Enabled = interactive;
        _overviewExpandButton.Enabled = interactive;
        _overviewCollapseButton.Enabled = interactive;
        _backButton.Enabled = interactive && _navigationHistory.Count > 0;
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

        string etaLineValue = _latestProgress.EstimatedRemainingMilliseconds >= 0
            ? $"{_latestProgress.EstimatedRemainingMilliseconds / 1000.0:F1} s"
            : "-";
        string progressLine = $"progress: {_latestProgress.EstimatedProgress01 * 100.0:F1}%";
        string etaLine = $"eta: {etaLineValue}";
        string boundLine = FormatSqueeze(_latestProgress);
        string text = $"{GetElapsedSeconds():F3} s\n{boundLine}\n{defaultLine}\n{compactLine}\n{progressLine}\n{etaLine}";
        SetStatText(_progressTextBox, text);
    }

    private void UpdateSearchProgress(SearchProgressSnapshot snapshot)
    {
        _latestProgress = snapshot;
        UpdateStatsPanels();
        string incumbent = snapshot.LatestRootIncumbent is null
            ? "incumbent: -"
            : $"incumbent: <= {snapshot.LatestRootIncumbent.BestWorstCaseSteps}";
        string etaText = snapshot.EstimatedRemainingMilliseconds >= 0
            ? $"{snapshot.EstimatedRemainingMilliseconds / 1000.0:F1} s"
            : "-";
        _statusLabel.Text = $"Running (phase {GetPhaseLabel()})... elapsed: {GetElapsedSeconds():F1} s, searched: {snapshot.SearchedStates}, {FormatSqueeze(snapshot)}, {incumbent}, " +
            $"progress: {snapshot.EstimatedProgress01 * 100.0:F1}%, eta: {etaText}.";
        _detailsTextBox.Text = BuildLiveDiagnosticsText(snapshot);
    }

    // Updates the three live stat panels (States / Work / Progress) from the latest snapshot.
    // Each metric lives in exactly one panel so the panels do not duplicate one another.
    private void UpdateStatsPanels()
    {
        SearchProgressSnapshot p = _latestProgress;

        SetStatText(_statesTextBox,
            "(cumulative: default + compact)\n" +
            $"searched: {p.SearchedStates}\n" +
            $"pending: {p.PendingStates} (peak {p.PeakPendingStates})\n" +
            $"output: {p.OutputStates}\n" +
            $"lower-bound: {p.LowerBoundStates}\n" +
            $"feasible-top-set: {p.FeasibleTopSetStates}");

        string compactText = p.CompactStatesSolved > 0
            ? $"[compact] {p.CompactStatesSolved} solved, {p.CompactGroupsEnumerated} groups ({p.CompactStepOptimalGroups} opt)"
            : "[compact] -";
        SetStatText(_workTextBox,
            "(cumulative: default + compact)\n" +
            $"outcomes: {p.OutcomesConstructed} (cand groups {p.CandidateGroupsEnumerated})\n" +
            $"duplicate skips: {p.DuplicateOutcomeSkips}\n" +
            $"merged collisions: {p.MergedOutcomeCollisions}\n" +
            $"prunes: {p.LowerBoundPrunes}\n" +
            $"cache: {p.ExactCacheHits}/{p.LowerBoundCacheHits}/{p.FeasibleTopSetCacheHits}/{p.BestGroupPatternCacheHits}\n" +
            compactText);
    }
    private static string FormatSearchStatsSummary(SearchProgressSnapshot snapshot, bool includeOutputStates)
    {
        string outputText = includeOutputStates ? $", output={snapshot.OutputStates}" : string.Empty;
        return $"searched={snapshot.SearchedStates}, pending={snapshot.PendingStates}, peak pending={snapshot.PeakPendingStates}{outputText}";
    }

    private static SearchProgressSnapshot CreateInitialProgressSnapshot()
    {
        return new SearchProgressSnapshot(0, 0, 0, 0, 0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.0, -1, 0);
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
            1.0,
            0,
            plan.SearchStatistics.RootProvenLowerBound);
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

    private static string BuildDefaultOnlyDetails(StrategyPlan defaultPlan)
    {
        string defaultText = StrategyTextRenderer.Render(defaultPlan).TrimEnd();
        var lines = new List<string>
        {
            "Default result (compact refinement in progress)",
            $"default elapsed: {defaultPlan.Elapsed.TotalMilliseconds:F1} ms",
            $"default total edges: {defaultPlan.TotalBranchEdges}",
            $"default output states: {defaultPlan.SearchStatistics.OutputStates}",
            $"worst-case steps: {defaultPlan.MaxStep}",
            string.Empty,
            "----- default -----",
            defaultText,
        };

        return string.Join(Environment.NewLine, lines);
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
            $"default total edges: {defaultPlan.TotalBranchEdges}",
            $"compact total edges: {compactPlan.TotalBranchEdges}",
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
        return $"{FormatSqueeze(snapshot)}, {incumbentText}, milestones: {snapshot.RootIncumbentCount}, prunes: {snapshot.LowerBoundPrunes}, cache hits: {snapshot.ExactCacheHits}/{snapshot.LowerBoundCacheHits}/{snapshot.FeasibleTopSetCacheHits}/{snapshot.BestGroupPatternCacheHits}";
    }

    // Formats the squeeze on the optimal max-step count: L is the proven lower bound
    // (RootProvenLowerBound), U is the best incumbent worst-case steps. Either side may be unknown
    // ("?") early on; when both are known and equal the optimum is proven exactly. The value is a
    // global result of the phase-1 solve and stays fixed through the compact phase, so it is shown
    // as "step-opt" rather than tagged to a single phase.
    private static string FormatSqueeze(SearchProgressSnapshot snapshot)
    {
        int lower = snapshot.RootProvenLowerBound;
        int? upper = snapshot.LatestRootIncumbent?.BestWorstCaseSteps;
        if (lower > 0 && upper is int u && lower == u)
            return $"step-opt = {lower} (proven)";

        string lowerText = lower > 0 ? lower.ToString() : "?";
        string upperText = upper?.ToString() ?? "?";
        return $"step-opt: {lowerText} <= opt <= {upperText}";
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
