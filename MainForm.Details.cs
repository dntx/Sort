using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

partial class MainForm
{
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
            PopulateTree(_feasiblePlan, _defaultPlan, _compactPlan, _compactImproved);
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
}
