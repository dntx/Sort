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
    private static string BuildBranchDetails(StrategyBranch branch)
    {
        string details = branch.OrderText;
        string effectDetails = DisplayEngine.FormatEffectDetails(branch.Effect);
        if (!string.IsNullOrEmpty(effectDetails))
            details += "\n" + effectDetails;

        if (branch.EquivalentOrders is not null)
        {
            details += "\n" + DisplayEngine.FormatEquivalentDetails(branch.EquivalentOrders);
        }

        return details;
    }

    private static string BuildStateDetails(StrategyNode node)
    {
        string stepAndGroup =
            $"Step: {node.Step}\n" +
            $"Comparison group: ({DisplayEngine.FormatSet(node.Group)})";
        string details = node.FinalChoice is not null
            ? stepAndGroup
            : $"State S{node.StateId}\n" + stepAndGroup;

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

        _treeRowBackBrush.Dispose();
        _treeRowBackBrush = new SolidBrush(_treeView.BackColor);

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _treeRowBackBrush.Dispose();

        base.Dispose(disposing);
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
        string head = feasiblePlan.RequestedK == feasiblePlan.K
            ? $"n={feasiblePlan.N}, m={feasiblePlan.M}, k={feasiblePlan.K}"
            : $"n={feasiblePlan.N}, m={feasiblePlan.M}, k={feasiblePlan.RequestedK} (dual k'={feasiblePlan.K})";

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
                $"{head}, step max={defaultPlan.MaxStep}, elapsed={seconds:F3} s. Computing proof-edge-compact@S stage...";
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
        return $"fixed ({DisplayEngine.FormatSet(summary.FixedTopSet)}); choose {summary.RemainingSlots} of ({DisplayEngine.FormatSet(summary.CandidatePool)}) into top {k}";
    }

    private static string BuildCompressedFinalChoiceDetails(FinalChoiceSummary summary, int k)
    {
        return
            $"Fixed top-{k} members: ({DisplayEngine.FormatSet(summary.FixedTopSet)})\n" +
            $"Choose {summary.RemainingSlots} of ({DisplayEngine.FormatSet(summary.CandidatePool)}) to complete top {k}";
    }

    private void StopStrategy()
    {
        _stopButton.Enabled = false;
        if (_runCancellationSource is null)
            return;

        _runCancellationSource.Cancel();

        _stopEscalationSource?.Cancel();
        _stopEscalationSource?.Dispose();
        _stopEscalationSource = new CancellationTokenSource();
        _ = EscalateStopIfStillRunningAsync(_stopEscalationSource.Token);
    }

    private async Task EscalateStopIfStillRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested || _runCancellationSource is null)
            return;

        _activeBuilder?.EscalateCancellationChecks();

        if (!IsHandleCreated)
            return;

        BeginInvoke(new Action(() =>
        {
            if (_runCancellationSource is not null)
                _statusLabel.Text = "Stopping... still running after 3.0 s, escalating cancellation checks.";
        }));
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
        //   eta: <current stage remaining>
        // The stage clock counts from _stageStartMs (reset at every stage boundary), so it always
        // reports the running stage's own time rather than a cumulative figure.
        double totalSeconds = totalMs / 1000.0;
        double stageSeconds = Math.Max(0, totalMs - _stageStartMs) / 1000.0;

        double etaSeconds = EstimateLiveEtaSeconds(totalMs);
        string etaLineValue = etaSeconds >= 0 ? $"{etaSeconds:F3} s" : "-";

        string text =
            $"{totalSeconds:F3} s\n" +
            $"{_currentStageName}: {stageSeconds:F3} s\n" +
            $"progress: {_latestProgress.EstimatedProgress01 * 100.0:F1}%\n" +
            $"eta: {etaLineValue}";
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
        return new SearchProgressSnapshot(0, 0, 0, 0, 0, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, 0.0, 0);
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
        string feasibleText = DisplayEngine.RenderStrategyText(feasiblePlan).TrimEnd();
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
        string defaultText = DisplayEngine.RenderStrategyText(defaultPlan).TrimEnd();
        var lines = new List<string>
        {
            "Step result (proof-edge-compact@S stage in progress)",
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
        string defaultText = DisplayEngine.RenderStrategyText(defaultPlan).TrimEnd();
        string compactText = DisplayEngine.RenderStrategyText(compactPlan).TrimEnd();
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
        string rendered = DisplayEngine.RenderStrategyText(plan).TrimEnd();
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

    // ETA derived directly from the live elapsed clock and the displayed progress, in seconds, or -1
    // until progress rises above zero. This keeps the displayed elapsed/progress/eta trio
    // mathematically self-consistent and lets ETA count down on every elapsed tick rather than only
    // when a new progress snapshot arrives. Assuming roughly linear progress, total =
    // elapsed / progress, so remaining = elapsed * (1 - progress) / progress.
    private double EstimateLiveEtaSeconds(long liveElapsedMs)
    {
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
        if (node is null)
            return;

        int requestVersion = Interlocked.Increment(ref _detailsRequestVersion);
        switch (node.Tag)
        {
            case string text:
                _detailsTextBox.Text = text;
                return;
            case LazyNodeDetails lazy when lazy.TryGetCached(out string cached):
                _detailsTextBox.Text = cached;
                return;
            case LazyNodeDetails lazy:
                _detailsTextBox.Text = "Loading details...";
                _ = Task.Run(lazy.GetOrCreate).ContinueWith(t =>
                {
                    if (!IsHandleCreated || IsDisposed)
                        return;

                    if (t.IsFaulted)
                    {
                        string error = t.Exception?.GetBaseException().Message ?? "unknown error";
                        Debug.WriteLine($"Details load failed: {error}");
                        BeginInvoke(new Action(() =>
                        {
                            if (requestVersion != Volatile.Read(ref _detailsRequestVersion))
                                return;
                            if (!ReferenceEquals(_treeView.SelectedNode, node))
                                return;

                            _detailsTextBox.Text = $"Failed to load details: {error}";
                        }));
                        return;
                    }

                    BeginInvoke(new Action(() =>
                    {
                        if (requestVersion != Volatile.Read(ref _detailsRequestVersion))
                            return;
                        if (!ReferenceEquals(_treeView.SelectedNode, node))
                            return;

                        _detailsTextBox.Text = t.Result;
                    }));
                }, TaskScheduler.Default);
                return;
            default:
                return;
        }
    }
}
