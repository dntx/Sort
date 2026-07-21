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

partial class MainForm
{
    private async void RunStrategy()
    {
        if (!Program.TryParseAndValidate(_nTextBox.Text, _mTextBox.Text, _kTextBox.Text, out int n, out int m, out int k, out string? error))
        {
            MessageBox.Show(this, error, "Invalid input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _runCancellationSource?.Dispose();
        _runCancellationSource = new CancellationTokenSource();
        _stopEscalationSource?.Cancel();
        _stopEscalationSource?.Dispose();
        _stopEscalationSource = null;
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
        _compactImproved = false;
        _activePhase = 0;
        _proofTightenStages.Clear();
        _currentStageName = feasibleMode ? StageNames.GreedyFeasible : StageNames.StepProof;
        _stageStartMs = 0;
        ClearResultsView();
        ShowInitialStagePlaceholder(n, m, k, feasibleMode);
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
                // Greedy mode: GreedyFeasible gives an instant browsable strategy even on shapes exact
                // never resolves (e.g. 25,5,5), then ProofTighten + EdgeCompact refine it.
                GreedyPreparationResult prep = await Task.Run(
                    () => PublicPipelineOrchestrator.RunGreedyPreparation(builder, emitStages: false),
                    cancellationToken);
                StrategyPlan feasiblePlan = prep.EffectiveFeasiblePlan;

                _feasiblePlan = feasiblePlan;
                _latestProgress = CreateSnapshotFromPlan(feasiblePlan);
                PopulateTree(feasiblePlan, defaultPlan: null, compactPlan: null, compactImproved: false);
                _completedFeasibleStats = feasiblePlan.SearchStatistics;
                UpdateSummaryText(feasiblePlan, defaultPlan: null, compactPlan: null, compactImproved: false);
                UpdateStatsPanels();
                SetRunUiState(RunUiState.CompactComputingInteractive);

                Interlocked.Exchange(ref _activePhase, 2);
                _proofTightenStages.Clear();
                _currentStageName = NextProofTightenStageName(feasiblePlan, feasiblePlan.MaxStep);
                _stageStartMs = _runStopwatch?.ElapsedMilliseconds ?? 0;
                // Each edge stage is surfaced live. The callback runs on the worker thread; a synchronous
                // Invoke marshals it onto the UI thread AND blocks the worker until the handler returns,
                // which is what lets the optional per-stage modal pause the search until the user clicks OK.
                StrategyPlan feasibleCompactPlan = await Task.Run(
                    () => PublicPipelineOrchestrator.RunGreedyPipeline(
                        builder,
                        MarshalProofTightenStage,
                        emitPreparationStages: false,
                        preparationAlreadyApplied: true),
                    cancellationToken);
                _runStopwatch?.Stop();

                RemoveTrailingComputingPlaceholder();
                _compactPlan = feasibleCompactPlan;
                _compactImproved = feasibleCompactPlan.IsStrictRefinementOver(feasiblePlan);
                _latestProgress = CreateSnapshotFromPlan(feasibleCompactPlan);
                _completedCompactStats = feasibleCompactPlan.SearchStatistics;
                UpdateSummaryText(feasiblePlan, defaultPlan: feasiblePlan, compactPlan: feasibleCompactPlan, compactImproved: _compactImproved);
                UpdateStatsPanels();
                return;
            }

            // Exact mode: no feasible phase. Phase 1 is the proven-optimal StepProof plan, used as both
            // the incumbent and the displayed strategy; phase 2 is EdgeCompact. The exact plan is
            // MaxStep-optimal, so EdgeCompact only trims edges among equally optimal groups.
            Interlocked.Exchange(ref _activePhase, 1);
            StrategyPlan compactPlan = await Task.Run(
                () => PublicPipelineOrchestrator.RunExactPipeline(builder, MarshalExactStage),
                cancellationToken);
            _runStopwatch?.Stop();

            // Keep final references aligned with the exact facade return value even when the compact
            // stage produced no strict refinement and the displayed tree stayed on the default plan.
            _compactPlan = compactPlan;
        }
        catch (OperationCanceledException)
        {
            _runStopwatch?.Stop();
            string shownDefault = _defaultPlan is not null
                ? " Showing the completed step strategy."
                : _feasiblePlan is not null
                    ? " Showing the step upper-bound strategy."
                    : string.Empty;
            _statusLabel.Text = $"Stopped after {GetRunElapsedSeconds():F1} s.{shownDefault} {FormatSearchStatsSummary(_latestProgress, includeOutputStates: true)}. {FormatLiveDiagnosticsSummary(_latestProgress)}.";
            _detailsTextBox.Text = BuildLiveDiagnosticsText(_latestProgress);
            MarkResultsStopped();
        }
        catch (Exception ex)
        {
            _runStopwatch?.Stop();
            _statusLabel.Text = $"Run failed after {GetRunElapsedSeconds():F1} s. {FormatSearchStatsSummary(_latestProgress, includeOutputStates: true)}. {FormatLiveDiagnosticsSummary(_latestProgress)}.";
            _detailsTextBox.Text = BuildLiveDiagnosticsText(_latestProgress);
            MessageBox.Show(this, ex.Message, "Run failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _activePhase = 0;
            _elapsedTimer.Stop();
            UpdateElapsedLabel();
            SetRunningState(isRunning: false);
            _stopEscalationSource?.Cancel();
            _stopEscalationSource?.Dispose();
            _stopEscalationSource = null;
            _runCancellationSource?.Dispose();
            _runCancellationSource = null;
            _activeBuilder = null;
        }
    }


    // Synchronous marshaling shim: RunGreedyPipeline invokes this on the worker thread once per
    // stage. Control.Invoke hops to the UI thread AND blocks the worker until OnProofTightenStage
    // returns, so when the per-stage modal is enabled the search genuinely pauses until the user clicks OK.
    private void MarshalProofTightenStage(StageResult stage)
        => MarshalStage(stage, OnProofTightenStage);

    private void MarshalExactStage(StageResult stage)
        => MarshalStage(stage, OnExactStage);

    private void MarshalStage(StageResult stage, Action<StageResult> onStage)
    {
        if (!IsHandleCreated || IsDisposed)
            return;
        try
        {
            Invoke(() => onStage(stage));
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

    private void OnExactStage(StageResult stage)
    {
        if (!stage.HasPlan)
            return;

        if (string.Equals(stage.Name, StageNames.StepProof, StringComparison.Ordinal))
        {
            StrategyPlan defaultPlan = stage.Plan!;
            _defaultPlan = defaultPlan;
            _feasiblePlan = defaultPlan;
            _latestProgress = CreateSnapshotFromPlan(defaultPlan);
            PopulateTree(defaultPlan, defaultPlan, compactPlan: null, compactImproved: false);
            _completedDefaultStats = defaultPlan.SearchStatistics;
            UpdateSummaryText(defaultPlan, defaultPlan, compactPlan: null, compactImproved: false);
            UpdateStatsPanels();

            // The exact plan is on screen; the compact pass runs on a background thread, so the UI
            // thread is free: drop the wait cursor and keep tree navigation enabled for the rest of
            // the run (the user can browse the strategy while compact search continues).
            SetRunUiState(RunUiState.CompactComputingInteractive);

            // Phase 2: compact refinement.
            Interlocked.Exchange(ref _activePhase, 2);
            _currentStageName = StageNames.FormatExactEdgeCompact(defaultPlan.MaxStep);
            _stageStartMs = _runStopwatch?.ElapsedMilliseconds ?? 0;
            return;
        }

        if (_defaultPlan is null)
            return;

        StrategyPlan compactPlan = stage.Plan!;
        _compactPlan = compactPlan;
        _compactImproved = compactPlan.IsStrictRefinementOver(_defaultPlan);

        _latestProgress = CreateSnapshotFromPlan(compactPlan);
        FinalizeCompactInTree(_defaultPlan, compactPlan, _compactImproved);
        _completedCompactStats = compactPlan.SearchStatistics;
        UpdateSummaryText(_defaultPlan, _defaultPlan, compactPlan, _compactImproved);
        UpdateStatsPanels();
    }

    // Name of the next stage RunGreedyPipeline will emit given the best incumbent max-step so
    // far. Mirrors the V2 loop: it tightens to the next proof-tighten ceiling while that ceiling is still above
    // the proven analytic lower bound, otherwise the final "greedy-edge-compact@S" pass runs. Used to label
    // the transient "...: computing..." placeholder so it matches the stage name that actually lands.
    private static string NextProofTightenStageName(StrategyPlan feasiblePlan, int incumbentMaxStep)
        => PipelineStageProtocol.NextGreedyStageName(feasiblePlan, incumbentMaxStep);

    // Anytime greedy edge handler: invoked on the UI thread once per edge stage as the worker thread
    // produces it (each proof-tighten stage, then the final "greedy-edge-compact@S"
    // pass, or a no-solution/incomplete terminal stage). The first stage fills the computing
    // slot in place; every later stage is appended as a new tree + overview section, so the user watches
    // the strategy improve stage by stage. Each tree gets a unique scope ("edge0", "edge1", ...) so
    // their per-state navigation keys never collide.
    private void OnProofTightenStage(StageResult stage)
    {
        if (_feasiblePlan is null || _treeView.Nodes.Count == 0)
            return;

        _proofTightenStages.Add(stage);
        int index = _proofTightenStages.Count - 1;
        string scope = $"edge{index}";

        // A stage is "shown" as a full browsable tree only when it strictly improves the incumbent
        // (the best plan so far: the greedy-feasible plan, then any improving downstream stage). A stage
        // that has a solution but is no better is recorded and marked "no improvement" but rendered
        // only as a leaf note. Tightening
        // continues regardless, since the next ceiling is driven by max-steps, not edges.
        StrategyPlan incumbent = _compactPlan ?? _feasiblePlan;
        bool improved = PipelineStageProtocol.IsImprovement(stage, incumbent);

        // A follow-up stage always lands after every emitted stage except the terminal edge-compact
        // pass: after a proof-tighten stage -- whether it found a solution or proved/failed the
        // ceiling -- the worker next probes a deeper feasible ceiling or runs the final edge-compaction
        // pass. We announce that in-progress probe with a trailing "<next>: computing..." placeholder
        // so the tree/overview never look idle while it runs. The terminal EdgeCompact stage has nothing
        // after it, so it appends no placeholder.
        bool hasFollowUp = !IsEdgeCompactStageName(stage.Name);
        string? nextStageName = !hasFollowUp
            ? null
            : stage.IsTightened
                ? NextProofTightenStageName(_feasiblePlan, stage.Plan!.MaxStep)
            : StageNames.FormatGreedyEdgeCompact(_feasiblePlan.MaxStep); // Phase A ended (proven-infeasible/incomplete); only the edge-compaction pass remains

        _treeView.BeginUpdate();
        TreeNode root = _treeView.Nodes[0];
        // Replace the trailing in-progress placeholder (the initial second-stage slot, or the previous
        // proof-tighten "<name>: computing..." note) with the landed stage.
        TryRemoveTrailingComputingPlaceholder(root.Nodes);
        root.Nodes.Add(BuildStageTreeNode(stage, scope, improved));
        if (nextStageName is not null)
            root.Nodes.Add(CreateComputingPlaceholderNode(nextStageName));

        if (improved)
            _compactPlan = stage.Plan;

        // A proven-infeasible terminal (ProvenInfeasible, not a timeout) proves the incumbent is optimal:
        // close its squeeze (opt = incumbent.MaxStep) so the progression detail reports proven optimal.
        if (stage.Outcome == StageOutcome.ProvenInfeasible)
            MarkGreedyIncumbentProvenOptimal();

        StrategyPlan shown = _compactPlan ?? _feasiblePlan;
        root.Text = BuildRootLabel(_feasiblePlan, _feasiblePlan, shown);
        root.Tag = new LazyNodeDetails(() => BuildGreedyProgressionDetails(_feasiblePlan, _proofTightenStages));
        _treeView.EndUpdate();

        _overviewTree.BeginUpdate();
        TryRemoveTrailingComputingPlaceholder(_overviewTree.Nodes);
        _overviewTree.Nodes.Add(BuildStageOverviewNode(stage, scope, improved));
        if (nextStageName is not null)
            _overviewTree.Nodes.Add(BuildOverviewNoteNode(FormatComputingPlaceholderText(nextStageName)));
        _overviewTree.EndUpdate();

        // Reset the per-stage clock so the progress panel times the upcoming probe from zero, and label
        // it with the stage about to run. Done whenever a follow-up stage exists (after both improving
        // and non-improving feasible stages, and after a terminal that still leaves the compact pass).
        if (nextStageName is not null)
        {
            _currentStageName = nextStageName;
            _stageStartMs = _runStopwatch?.ElapsedMilliseconds ?? 0;
        }

        if (stage.HasPlan)
        {
            _latestProgress = CreateSnapshotFromPlan(stage.Plan!);
            if (improved)
                UpdateSummaryText(_feasiblePlan, defaultPlan: _feasiblePlan, compactPlan: stage.Plan, compactImproved: true);
        }
        UpdateStatsPanels();
        UpdateElapsedLabel();

        // Optional pause-on-each-stage: a modal blocks this UI-thread handler (and therefore the worker
        // thread waiting in Invoke) until the user acknowledges the stage.
        if (_pauseEachStageCheckBox.Checked)
        {
            string? marker = stage.HasPlan
                ? (!improved ? "no improvement" : null)
                : NoSolutionMarker(stage);
            ShowStageModal(FormatStageRootLabel(stage.Name, stage.Elapsed, stage.Plan, marker), stage.HasPlan);
        }
    }

    // Closes the squeeze on the greedy incumbent (the best plan so far) to a proven optimum after a
    // tightening probe proved the next ceiling infeasible: opt = incumbent.MaxStep. Rewrites the
    // incumbent plan reference (_compactPlan, or _feasiblePlan when no edge stage improved) and the
    // matching entry in _proofTightenStages so the rebuilt progression detail reports "proven optimal".
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
            for (int i = 0; i < _proofTightenStages.Count; i++)
            {
                if (ReferenceEquals(_proofTightenStages[i].Plan, incumbent))
                {
                    StageResult s = _proofTightenStages[i];
                    _proofTightenStages[i] = new StageResult(s.Name, proven, s.Elapsed, s.Outcome);
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

    private void ShowStageModal(string message, bool hasPlan)
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
                hasPlan ? MessageBoxIcon.Information : MessageBoxIcon.None);
        }
        finally
        {
            if (wasRunning)
                _runStopwatch!.Start();
        }
    }

}
