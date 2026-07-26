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
    private readonly record struct RunRequest(
        int N,
        int M,
        int K,
        bool FeasibleMode,
        StrategyBuilder Builder,
        CancellationToken CancellationToken);

    private async void RunStrategy()
    {
        if (!TryCreateRunRequest(out RunRequest request, out string? error))
        {
            MessageBox.Show(this, error, "Invalid input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            InitializeRunUi(request);
            _activeBuilder = request.Builder;

            if (request.FeasibleMode)
            {
                await RunFeasibleModeAsync(request);
                return;
            }

            await RunExactModeAsync(request);
        }
        catch (OperationCanceledException)
        {
            HandleRunCanceled();
        }
        catch (Exception ex)
        {
            HandleRunFailed(ex);
        }
        finally
        {
            TeardownRunSession();
        }
    }

    private bool TryCreateRunRequest(out RunRequest request, out string? error)
    {
        request = default;
        error = null;

        if (!Program.TryParseAndValidate(_nTextBox.Text, _mTextBox.Text, _kTextBox.Text, out int n, out int m, out int k, out error))
            return false;

        ResetRunCancellationInfrastructure();

        bool feasibleMode = _modeComboBox.SelectedIndex == 1;
        CancellationToken cancellationToken = _runCancellationSource!.Token;
        IProgress<SearchProgressSnapshot> progress = new Progress<SearchProgressSnapshot>(UpdateSearchProgress);

        // The builder is shared across all phases so the default/compact passes reuse the search
        // caches the earlier passes already populated.
        StrategyBuilder builder = new(
            n,
            m,
            k,
            cancellationToken,
            snapshot => progress.Report(snapshot),
            reportCombinedRunProgress: true);

        request = new RunRequest(n, m, k, feasibleMode, builder, cancellationToken);
        return true;
    }

    private void ResetRunCancellationInfrastructure()
    {
        _runCancellationSource?.Dispose();
        _runCancellationSource = new CancellationTokenSource();

        _stopEscalationSource?.Cancel();
        _stopEscalationSource?.Dispose();
        _stopEscalationSource = null;
    }

    private void InitializeRunUi(RunRequest request)
    {
        _feasibleMode = request.FeasibleMode;
        _latestProgress = CreateInitialProgressSnapshot();
        _completedDefaultStats = null;
        _completedCompactStats = null;
        _completedFeasibleStats = null;
        _feasiblePlan = null;
        _defaultPlan = null;
        _compactPlan = null;
        _initialGreedyStage = null;
        _incumbentStage = null;
        _greedyIncumbentImproved = false;
        _compactImproved = false;
        _activePhase = 0;
        _proofTightenStages.Clear();
        ResetPresentationInfrastructure();
        _pauseEachStageForRun = _pauseEachStageCheckBox.Checked;
        _currentStageName = request.FeasibleMode ? StageNames.GreedyFeasible : StageNames.StepProof;
        _stageStartMs = 0;

        ClearResultsView();
        ShowInitialStagePlaceholder(request.N, request.M, request.K, request.FeasibleMode);

        _runStopwatch = Stopwatch.StartNew();
        UpdateElapsedLabel();
        UpdateStatsPanels();
        _elapsedTimer.Start();
        SetRunningState(isRunning: true);
        _statusLabel.Text = $"Running n={request.N}, m={request.M}, k={request.K}...";
        _detailsTextBox.Text = BuildLiveDiagnosticsText(_latestProgress);
    }

    private async Task RunFeasibleModeAsync(RunRequest request)
    {
        // Greedy mode: GreedyFeasible gives an instant browsable strategy even on shapes exact
        // never resolves (e.g. 25,5,5), then ProofTighten + EdgeCompact refine it.
        GreedyPreparationResult prep = await Task.Run(
            () => PublicPipelineOrchestrator.RunGreedyPreparation(request.Builder, emitStages: false),
            request.CancellationToken);
        StrategyPlan feasiblePlan = prep.EffectiveFeasiblePlan!;

        _feasiblePlan = feasiblePlan;
        _incumbentStage = new StageResult(
            prep.GreedyTightenImproved ? StageNames.GreedyTighten : StageNames.GreedyFeasible,
            feasiblePlan,
            prep.GreedyTightenImproved ? prep.GreedyTightenElapsed : prep.GreedyFeasibleElapsed,
            StageOutcome.Completed,
            prep.EffectiveFeasibleSolution,
            prep.GreedyTightenImproved ? prep.GreedyTightenTimings : prep.GreedyFeasibleTimings);
        _initialGreedyStage = _incumbentStage;
        _greedyIncumbentImproved = prep.GreedyTightenImproved;
        _latestProgress = CreateSnapshotFromPlan(feasiblePlan);
        PopulateTree(feasiblePlan, defaultPlan: null, compactPlan: null, compactImproved: false);
        _completedFeasibleStats = feasiblePlan.SearchStatistics;
        UpdateSummaryText(feasiblePlan, defaultPlan: null, compactPlan: null, compactImproved: false);
        UpdateStatsPanels();
        SetRunUiState(RunUiState.CompactComputingInteractive);

        Interlocked.Exchange(ref _activePhase, 2);
        _proofTightenStages.Clear();
        _currentStageName = NextProofTightenStageName(
            prep.EffectiveFeasibleSolution.Score.WorstCaseSteps);
        _stageStartMs = _runStopwatch?.ElapsedMilliseconds ?? 0;

        // Each edge stage is surfaced live. The callback runs on the worker thread; a synchronous
        // Invoke marshals it onto the UI thread AND blocks the worker until the handler returns,
        // which is what lets the optional per-stage modal pause the search until the user clicks OK.
        _ = await Task.Run(
            () =>
            {
                PublicPipelineOrchestrator.RunGreedyPipelineDeferred(
                    request.Builder,
                    MarshalProofTightenStage,
                    preparationAlreadyApplied: true);
                return 0;
            },
            request.CancellationToken);
        await DrainPresentationTasksAsync();
        _runStopwatch?.Stop();

        RemoveTrailingComputingPlaceholder();
        StrategyPlan selectedPlan = _incumbentStage?.Plan ?? feasiblePlan;
        _compactPlan = selectedPlan;
        _compactImproved = _greedyIncumbentImproved;
        _latestProgress = CreateSnapshotFromPlan(selectedPlan);
        _completedCompactStats = selectedPlan.SearchStatistics;
        UpdateSummaryText(feasiblePlan, defaultPlan: feasiblePlan, compactPlan: selectedPlan, compactImproved: _compactImproved);
        UpdateStatsPanels();
    }

    private async Task RunExactModeAsync(RunRequest request)
    {
        // Exact mode: no feasible phase. Phase 1 is the proven-optimal StepProof plan, used as both
        // the incumbent and the displayed strategy; phase 2 is EdgeCompact. The exact plan is
        // MaxStep-optimal, so EdgeCompact only trims edges among equally optimal groups.
        Interlocked.Exchange(ref _activePhase, 1);
        await Task.Run(
            () => PublicPipelineOrchestrator.RunExactPipelineDeferred(request.Builder, MarshalExactStage),
            request.CancellationToken);
        await DrainPresentationTasksAsync();
        _runStopwatch?.Stop();
    }

    private void ResetPresentationInfrastructure()
    {
        _presentationGeneration++;
        _presentationRequestVersion = 0;
        _presentationCancellationSource?.Cancel();
        _presentationCancellationSource?.Dispose();
        _presentationCancellationSource = new CancellationTokenSource();

        _activePresentationRequestSource?.Cancel();
        _activePresentationRequestSource?.Dispose();
        _activePresentationRequestSource = null;

        _activePresentationTask = null;
        _exactStepStageMaterialized = false;
        _pendingExactCompactStage = null;
    }

    private void QueueStageMaterialization(
        StageResult stage,
        Action<StageResult> apply)
    {
        _activePresentationRequestSource?.Cancel();
        _activePresentationRequestSource?.Dispose();

        CancellationToken parentToken = _presentationCancellationSource?.Token ?? CancellationToken.None;
        _activePresentationRequestSource = CancellationTokenSource.CreateLinkedTokenSource(parentToken);

        int requestVersion = ++_presentationRequestVersion;
        int generation = _presentationGeneration;
        CancellationToken requestToken = _activePresentationRequestSource.Token;

        _activePresentationTask = MaterializeExactStageAsync(
            stage,
            apply,
            generation,
            requestVersion,
            requestToken);
    }

    private async Task DrainPresentationTasksAsync()
    {
        while (true)
        {
            Task? task = _activePresentationTask;
            if (task is null)
                break;

            await task;
            if (ReferenceEquals(_activePresentationTask, task))
                _activePresentationTask = null;
        }
    }

    private void HandleRunCanceled()
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

    private void HandleRunFailed(Exception ex)
    {
        _runStopwatch?.Stop();
        _statusLabel.Text = $"Run failed after {GetRunElapsedSeconds():F1} s. {FormatSearchStatsSummary(_latestProgress, includeOutputStates: true)}. {FormatLiveDiagnosticsSummary(_latestProgress)}.";
        _detailsTextBox.Text = BuildLiveDiagnosticsText(_latestProgress);
        MessageBox.Show(this, ex.Message, "Run failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void TeardownRunSession()
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

        _presentationCancellationSource?.Cancel();
        _presentationCancellationSource?.Dispose();
        _presentationCancellationSource = null;

        _activePresentationRequestSource?.Cancel();
        _activePresentationRequestSource?.Dispose();
        _activePresentationRequestSource = null;

        _activePresentationTask = null;

        _activeBuilder = null;
    }


    // Synchronous marshaling shim: RunGreedyPipeline invokes this on the worker thread once per
    // stage. Control.Invoke hops to the UI thread AND blocks the worker until OnProofTightenStage
    // returns, so when the per-stage modal is enabled the search genuinely pauses until the user clicks OK.
    private void MarshalProofTightenStage(StageResult stage)
        => MarshalStageToUiThread(stage, OnProofTightenStage);

    private void MarshalExactStage(StageResult stage)
        => MarshalStageToUiThread(stage, OnExactStage);

    private void MarshalStageToUiThread(StageResult stage, Action<StageResult> onStage)
    {
        if (!CanAcceptStageCallback())
            return;

        try
        {
            if (_pauseEachStageForRun)
            {
                // In pause mode we preserve strict stage-by-stage blocking semantics.
                Invoke(() => onStage(stage));
            }
            else
            {
                // In normal mode do not block the solver thread on UI work.
                BeginInvoke(() => onStage(stage));
            }
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

    private bool CanAcceptStageCallback()
        => IsHandleCreated && !IsDisposed;

    private void OnExactStage(StageResult stage)
    {
        if (stage.Solution is null)
            return;

        if (string.Equals(stage.Name, StageNames.StepProof, StringComparison.Ordinal))
        {
            _incumbentStage = stage;
            _defaultPlan = null;
            _feasiblePlan = null;
            _compactPlan = null;

            string compactStageName = StageNames.FormatExactEdgeCompact(
                stage.Solution.Score.WorstCaseSteps);
            Interlocked.Exchange(ref _activePhase, 2);
            _currentStageName = compactStageName;
            _stageStartMs = _runStopwatch?.ElapsedMilliseconds ?? 0;

            QueueStageMaterialization(stage, ApplyMaterializedStepProofStage);
            return;
        }

        if (!_exactStepStageMaterialized)
        {
            _pendingExactCompactStage = stage;
            return;
        }

        QueueStageMaterialization(stage, ApplyMaterializedExactCompactStage);
    }

    private async Task MaterializeExactStageAsync(
        StageResult stage,
        Action<StageResult> apply,
        int generation,
        int requestVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            TimeSpan priorElapsed = stage.Timings.Solve + stage.Timings.Freeze;
            StrategyPlan plan = await Task.Run(
                () => StrategyBuilder.MaterializeSolvedStrategy(stage.Solution!, priorElapsed, cancellationToken),
                cancellationToken);

            TimeSpan materialize = plan.Elapsed - priorElapsed;
            if (materialize < TimeSpan.Zero)
                materialize = TimeSpan.Zero;

            var materialized = new StageResult(
                stage.Name,
                plan,
                stage.Timings.Solve + stage.Timings.Freeze + materialize,
                stage.Outcome,
                stage.Solution,
                new StageTimings(stage.Timings.Solve, stage.Timings.Freeze, materialize));

            if (!CanAcceptStageCallback()
                || generation != _presentationGeneration
                || requestVersion != _presentationRequestVersion)
                return;

            var applyCompleted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            BeginInvoke(() =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested
                        || !CanAcceptStageCallback()
                        || generation != _presentationGeneration
                        || requestVersion != _presentationRequestVersion)
                        return;

                    apply(materialized);
                }
                finally
                {
                    applyCompleted.TrySetResult(null);
                }
            });
            await applyCompleted.Task;
        }
        catch (OperationCanceledException)
        {
            // Stop/teardown cancelled presentation; no UI update should land.
        }
    }

    private void ApplyMaterializedStepProofStage(StageResult stage)
    {
        StrategyPlan defaultPlan = stage.Plan!;
        _incumbentStage = stage;
        _defaultPlan = defaultPlan;
        _feasiblePlan = defaultPlan;
        _latestProgress = CreateSnapshotFromPlan(defaultPlan);
        PopulateTree(defaultPlan, defaultPlan, compactPlan: null, compactImproved: false);
        _completedDefaultStats = defaultPlan.SearchStatistics;
        UpdateSummaryText(defaultPlan, defaultPlan, compactPlan: null, compactImproved: false);
        UpdateStatsPanels();

        // The exact plan is now browsable while compact search may still be running.
        SetRunUiState(RunUiState.CompactComputingInteractive);

        _exactStepStageMaterialized = true;
        if (_pendingExactCompactStage is { } pendingCompact)
        {
            _pendingExactCompactStage = null;
            QueueStageMaterialization(pendingCompact, ApplyMaterializedExactCompactStage);
        }
    }

    private void ApplyMaterializedExactCompactStage(StageResult stage)
    {
        if (_defaultPlan is null)
            return;

        StrategyPlan compactPlan = stage.Plan!;
        _compactPlan = compactPlan;
        _compactImproved = _incumbentStage.HasValue
            && PipelineStageProtocol.IsImprovement(stage, _incumbentStage.Value);
        if (_compactImproved)
            _incumbentStage = stage;

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
    private string NextProofTightenStageName(int incumbentMaxStep)
        => PipelineStageProtocol.NextGreedyStageName(
            _initialGreedyStage?.Solution
                ?? throw new InvalidOperationException("Greedy stage naming requires the initial solved strategy."),
            incumbentMaxStep);

    private string NextProofTightenStageNameForPresentation(
        StrategyPlan feasiblePlan,
        int incumbentMaxStep)
    {
        if (_initialGreedyStage?.Solution is { } solution)
            return PipelineStageProtocol.NextGreedyStageName(solution, incumbentMaxStep);

        int lower = Math.Max(1, feasiblePlan.SearchStatistics.RootProvenLowerBound);
        int nextBudget = incumbentMaxStep - 1;
        return nextBudget >= lower
            ? StageNames.FormatProofTighten(nextBudget)
            : StageNames.FormatGreedyEdgeCompact(incumbentMaxStep);
    }

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

        bool improved = _incumbentStage.HasValue
            && PipelineStageProtocol.IsImprovement(stage, _incumbentStage.Value);

        if (improved && !stage.HasPlan && stage.Solution is not null)
        {
            QueueStageMaterialization(stage, OnProofTightenStage);
            return;
        }

        _proofTightenStages.Add(stage);
        int index = _proofTightenStages.Count - 1;
        string scope = $"edge{index}";

        // A stage is "shown" as a full browsable tree only when it strictly improves the incumbent
        // (the best plan so far: the greedy-feasible plan, then any improving downstream stage). A stage
        // that has a solution but is no better is recorded and marked "no improvement" but rendered
        // only as a leaf note. Tightening
        // continues regardless, since the next ceiling is driven by max-steps, not edges.
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
                ? NextProofTightenStageName(stage.Solution!.Score.WorstCaseSteps)
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
        {
            _compactPlan = stage.Plan;
            _incumbentStage = stage;
            _greedyIncumbentImproved = true;
        }

        // A proven-infeasible terminal (ProvenInfeasible, not a timeout) proves the incumbent is optimal:
        // close its squeeze (opt = incumbent.MaxStep) so the progression detail reports proven optimal.
        if (stage.Outcome == StageOutcome.ProvenInfeasible)
            MarkGreedyIncumbentProvenOptimal();

        StrategyPlan shown = _compactPlan ?? _feasiblePlan;
        root.Text = BuildRootLabel(_feasiblePlan, _feasiblePlan, shown);
        root.Tag = new LazyNodeDetails(() => BuildGreedyProgressionDetails(
            _initialGreedyStage!.Value,
            _proofTightenStages));
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

        if (!_incumbentStage.HasValue || _incumbentStage.Value.Solution is null)
            return;

        StageResult incumbentStage = _incumbentStage.Value;
        StrategyPlan incumbent = incumbentStage.Plan!;
        int provenLower = incumbentStage.Solution.Score.WorstCaseSteps;
        if (incumbent.SearchStatistics.RootProvenLowerBound >= provenLower)
            return;

        StageResult provenStage = incumbentStage.WithProvenLowerBound(provenLower);
        StrategyPlan proven = provenStage.Plan!;
        if (_compactPlan is not null)
        {
            for (int i = 0; i < _proofTightenStages.Count; i++)
            {
                if (ReferenceEquals(_proofTightenStages[i].Plan, incumbent))
                {
                    StageResult s = _proofTightenStages[i];
                    _proofTightenStages[i] = new StageResult(
                        s.Name,
                        proven,
                        s.Elapsed,
                        s.Outcome,
                        s.Solution,
                        s.Timings);
                    break;
                }
            }
            _compactPlan = proven;
        }
        else
        {
            _feasiblePlan = proven;
        }

        _incumbentStage = provenStage;
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
