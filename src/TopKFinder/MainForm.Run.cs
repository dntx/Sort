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
            }
            else
            {
                await RunExactModeAsync(request);
            }
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
        builder.GreedyTightenEnabledForTesting = _enableGtCheckBox.Checked;

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
        ResetRunTimeline();
        RecordRunTimeline("run/start", $"mode={(request.FeasibleMode ? "greedy" : "exact")}, n={request.N}, m={request.M}, k={request.K}");
        _feasibleMode = request.FeasibleMode;
        _initialRootProvenLowerBound = 0;
        _latestProgress = CreateInitialProgressSnapshot();
        _completedDefaultStats = null;
        _completedCompactStats = null;
        _completedFeasibleStats = null;
        _feasiblePlan = null;
        _defaultPlan = null;
        _compactPlan = null;
        _greedyFeasibleStage = null;
        _greedyTightenStage = null;
        _materializedStepDisplayStage = null;
        _materializedCompactDisplayStage = null;
        _incumbentStage = null;
        _frozenGreedyStageComparisonBaseline = null;
        _greedyIncumbentImproved = false;
        _compactImproved = false;
        _activePhase = 0;
        _proofTightenStages.Clear();
        _readyGreedyEdgeStages.Clear();
        _materializingGreedyEdgeStageNames.Clear();
        _greedyEdgeTreeMaterializationTasks.Clear();
        _stageDisplayOrder.Clear();
        _nextStageDisplayOrder = 0;
        _solverWorkStopped = false;
        _stagePauseCompletion = null;
        _pausedStageName = null;
        _stagePausePresentationReady = false;
        ResetPresentationInfrastructure();
        _pauseEachStageForRun = _pauseEachStageCheckBox.Checked;
        _currentStageName = request.FeasibleMode ? StageNames.GreedyFeasible : StageNames.StepProof;
        EnsureStageDisplayOrder(_currentStageName);
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
        // Greedy mode: GreedyFeasible establishes the initial upper bound, GreedyTighten may lower
        // that upper bound, then ProofTighten + EdgeCompact refine the result further.
        GreedyPreparationResult prep = await Task.Run(
            () => PublicPipelineOrchestrator.RunGreedyPreparation(
                request.Builder,
                onStageCompleted: MarshalGreedyPreparationStage,
                onStageStart: MarshalStageSearchStart,
                emitStages: true,
                materialize: false),
            request.CancellationToken);
        // Unify callback timing across modes: flush posted UI callbacks before consuming
        // stage metadata in the mode transition code.
        await FlushUiCallbacksAsync();

        _greedyIncumbentImproved = prep.GreedyTightenImproved;
        if (_greedyFeasibleStage is { } initialStage)
            _incumbentStage ??= initialStage;
        if (prep.GreedyTightenImproved
            && prep.GreedyTightenSolution is not null
            && _greedyTightenStage is { } greedyTightenStage)
            _incumbentStage = greedyTightenStage;

        Interlocked.Exchange(ref _activePhase, 2);
        _proofTightenStages.Clear();
        _materializingGreedyEdgeStageNames.Clear();
        _greedyEdgeTreeMaterializationTasks.Clear();
        string proofStartStageName = NextProofTightenStageName(
            prep.EffectiveFeasibleSolution.Score.WorstCaseSteps);
        RecordRunTimeline("pipeline/proof-tighten-scheduled", proofStartStageName);

        // Each edge stage is surfaced live. The callback runs on the worker thread; a synchronous
        // Invoke marshals it onto the UI thread AND blocks the worker until the handler returns,
        // which is what lets the optional per-stage modal pause the search until the user clicks OK.
        _ = await Task.Run(
            () =>
            {
                RecordRunTimeline("worker/proof-tighten-started", proofStartStageName);
                PublicPipelineOrchestrator.RunGreedyPipelineDeferred(
                    request.Builder,
                    MarshalProofTightenStage,
                    MarshalStageSearchStart,
                    preparationAlreadyApplied: true);
                return 0;
            },
            request.CancellationToken);
        _solverWorkStopped = true;
        await DrainPresentationTasksAsync();
        _runStopwatch?.Stop();

        StrategyPlan selectedPlan = _incumbentStage?.MaterializedPlan ?? _feasiblePlan
            ?? throw new InvalidOperationException("Expected a materialized greedy incumbent before final UI reconciliation.");
        _compactPlan = selectedPlan;
        _compactImproved = _greedyIncumbentImproved;
        _latestProgress = CreateSnapshotFromPlan(selectedPlan);
        _completedCompactStats = selectedPlan.SearchStatistics;
        StrategyPlan baselineFeasible = _feasiblePlan ?? selectedPlan;
        UpdateSummaryText(baselineFeasible, defaultPlan: baselineFeasible, compactPlan: selectedPlan, compactImproved: _compactImproved);
        UpdateStatsPanels();
    }

    private void DisplayInitialGreedyStageTree(StageResult stage)
    {
        RecordRunTimeline("ui/display-greedy-feasible-tree/start", stage.Name);
        if (stage.MaterializedPlan is not StrategyPlan feasiblePlan)
            return;

        _feasiblePlan = feasiblePlan;
        if (_incumbentStage is { HasPlan: false, Solution: { } incumbentSolution }
            && ReferenceEquals(incumbentSolution, stage.Solution))
        {
            _incumbentStage = stage;
        }

        _greedyFeasibleStage = stage;
        _materializedStepDisplayStage = stage;
        _latestProgress = CreateSnapshotFromPlan(feasiblePlan);
        PopulateTree(feasiblePlan, defaultPlan: null, compactPlan: null, compactImproved: false);
        _completedFeasibleStats = feasiblePlan.SearchStatistics;
        UpdateSummaryText(feasiblePlan, defaultPlan: null, compactPlan: null, compactImproved: false);
        UpdateStatsPanels();
        RemoveStageStatusPlaceholder(stage.Name);
        SetRunUiState(RunUiState.CompactComputingInteractive);
        RecordRunTimeline("ui/display-greedy-feasible-tree/done", stage.Name);

        if (_readyGreedyEdgeStages.Count == 0)
            return;

        List<StageResult> readyStages = _readyGreedyEdgeStages.ToList();
        _readyGreedyEdgeStages.Clear();
        foreach (StageResult readyStage in readyStages)
        {
            if (_materializingGreedyEdgeStageNames.Contains(readyStage.Name))
            {
                UpsertReadyGreedyEdgeStage(readyStage);
                continue;
            }

            OnProofTightenStage(readyStage);
        }
    }

    private async Task RunExactModeAsync(RunRequest request)
    {
        // Exact mode: no feasible phase. Phase 1 is the proven-optimal StepProof plan, used as both
        // the incumbent and the displayed strategy; phase 2 is EdgeCompact. The exact plan is
        // MaxStep-optimal, so EdgeCompact only trims edges among equally optimal groups.
        Interlocked.Exchange(ref _activePhase, 1);
        await Task.Run(
            () => PublicPipelineOrchestrator.RunExactPipelineDeferred(request.Builder, MarshalExactStage, MarshalStageSearchStart),
            request.CancellationToken);
        await FlushUiCallbacksAsync();
        _solverWorkStopped = true;
        await DrainPresentationTasksAsync();
        _runStopwatch?.Stop();
    }

    private Task FlushUiCallbacksAsync()
    {
        if (!CanAcceptStageCallback())
            return Task.CompletedTask;

        var flushed = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(() => flushed.TrySetResult(null));
        }
        catch (ObjectDisposedException)
        {
            flushed.TrySetResult(null);
        }
        catch (InvalidOperationException)
        {
            flushed.TrySetResult(null);
        }

        return flushed.Task;
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
        _materializingGreedyEdgeStageNames.Clear();
        _greedyEdgeTreeMaterializationTasks.Clear();
        ClearPresentationStageCache();
    }

    private static PresentationStageCacheKey BuildPresentationStageCacheKey(StageResult stage)
        => new(
            stage.Solution ?? throw new InvalidOperationException("Stage solution is required for presentation cache key."),
            stage.Name,
            stage.Timings.Solve,
            stage.Timings.Freeze,
            stage.Outcome,
            stage.Incomplete);

    private void ClearPresentationStageCache()
    {
        _presentationStageCache.Clear();
        _presentationStageCacheLru.Clear();
        _presentationStageCacheNodes.Clear();
        _frozenStageImprovementDecisions.Clear();
        _frozenGreedyStageComparisonBaseline = null;
    }

    // Improvement labels for greedy edge stages must remain stable across deferred materialization
    // re-entry. Freeze the decision on first UI ingress and reuse it for later callbacks.
    private bool GetOrCreateFrozenStageImprovementDecision(StageResult stage)
    {
        if (stage.Solution is null)
            return false;

        PresentationStageCacheKey key = BuildPresentationStageCacheKey(stage);
        if (_frozenStageImprovementDecisions.TryGetValue(key, out bool cachedDecision))
            return cachedDecision;

        StageResult? baseline = _frozenGreedyStageComparisonBaseline;
        if (!baseline.HasValue
            && _incumbentStage is { Solution: not null } incumbent)
        {
            baseline = incumbent;
            _frozenGreedyStageComparisonBaseline = incumbent;
        }

        bool improved;
        if (!baseline.HasValue)
        {
            // Defensive fallback: if no comparable baseline exists yet, treat the first solved stage
            // as accepted so it can establish the progression baseline without a spurious marker.
            improved = true;
        }
        else
        {
            improved = PipelineStageProtocol.IsImprovement(stage, baseline.Value);
        }

        _frozenStageImprovementDecisions[key] = improved;

        if (improved)
            _frozenGreedyStageComparisonBaseline = stage;

        return improved;
    }

    // A stage needs display materialization only when it is a solved improvement that does not already
    // carry a materialized plan. Non-improving solved stages are intentionally shown as notes.
    private static bool ShouldMaterializeStageForDisplay(StageResult stage, bool improved)
        => improved && !stage.HasPlan && stage.Solution is not null;

    private StageResult? GetCachedPresentationStageResult(StageResult stage)
    {
        if (stage.Solution is null)
            return null;

        PresentationStageCacheKey key = BuildPresentationStageCacheKey(stage);
        if (!_presentationStageCache.TryGetValue(key, out StageResult cached))
            return null;

        if (_presentationStageCacheNodes.TryGetValue(key, out LinkedListNode<PresentationStageCacheKey>? node))
        {
            _presentationStageCacheLru.Remove(node);
            _presentationStageCacheLru.AddLast(node);
        }

        return cached;
    }

    private bool IsPresentationStageCached(StageResult stage)
        => GetCachedPresentationStageResult(stage).HasValue;

    private void CachePresentationStageResult(StageResult stage, StageResult materialized)
    {
        if (stage.Solution is null)
            return;

        PresentationStageCacheKey key = BuildPresentationStageCacheKey(stage);
        if (_presentationStageCache.ContainsKey(key))
        {
            _presentationStageCache[key] = materialized;
            if (_presentationStageCacheNodes.TryGetValue(key, out LinkedListNode<PresentationStageCacheKey>? existingNode))
            {
                _presentationStageCacheLru.Remove(existingNode);
                _presentationStageCacheLru.AddLast(existingNode);
            }
            return;
        }

        if (_presentationStageCache.Count >= PresentationStageCacheCapacity)
        {
            LinkedListNode<PresentationStageCacheKey>? oldest = _presentationStageCacheLru.First;
            if (oldest is not null)
            {
                _presentationStageCacheLru.RemoveFirst();
                _presentationStageCache.Remove(oldest.Value);
                _presentationStageCacheNodes.Remove(oldest.Value);
            }
        }

        _presentationStageCache[key] = materialized;
        LinkedListNode<PresentationStageCacheKey> node = _presentationStageCacheLru.AddLast(key);
        _presentationStageCacheNodes[key] = node;
    }

    private void StartStageTreeMaterialization(
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

        _activePresentationTask = MaterializeStageTreeAsync(
            stage,
            apply,
            generation,
            requestVersion,
            requestToken);
    }

    private void InvalidateActivePresentationRequest()
    {
        _activePresentationRequestSource?.Cancel();
        _activePresentationRequestSource?.Dispose();
        _activePresentationRequestSource = null;

        // Bump request version so any posted UI-apply checks reject older completions
        // even if cancellation is observed after materialization completes.
        _presentationRequestVersion++;
    }

    private async Task DrainPresentationTasksAsync()
    {
        while (true)
        {
            Task? task = _activePresentationTask;
            if (task is null && _greedyEdgeTreeMaterializationTasks.Count > 0)
                task = Task.WhenAll(_greedyEdgeTreeMaterializationTasks.ToArray());

            if (task is null)
                break;

            await task;
            if (ReferenceEquals(_activePresentationTask, task))
                _activePresentationTask = null;

            _greedyEdgeTreeMaterializationTasks.RemoveAll(t => t.IsCompleted);

            if (_activePresentationTask is null && _greedyEdgeTreeMaterializationTasks.Count == 0)
                break;
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
        _solverWorkStopped = false;
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
        _stagePauseCompletion?.TrySetCanceled();
        _stagePauseCompletion = null;
        _pausedStageName = null;
        _stagePausePresentationReady = false;
        _materializingGreedyEdgeStageNames.Clear();
        _greedyEdgeTreeMaterializationTasks.Clear();
        ClearPresentationStageCache();

        _activeBuilder = null;
    }


    // Synchronous marshaling shim: RunGreedyPipeline invokes this on the worker thread once per
    // stage. Control.Invoke hops to the UI thread AND blocks the worker until OnProofTightenStage
    // returns, so when the per-stage modal is enabled the search genuinely pauses until the user clicks OK.
    private void MarshalProofTightenStage(StageResult stage)
        => MarshalStageToUiThread(stage, OnProofTightenStage);

    private void MarshalExactStage(StageResult stage)
        => MarshalStageToUiThread(stage, OnExactStage);

    private void MarshalStageSearchStart(string stageName)
    {
        if (!CanAcceptStageCallback())
            return;

        int expectedGeneration = _presentationGeneration;
        void apply()
        {
            if (expectedGeneration != _presentationGeneration)
                return;

            OnStageSearchStarted(stageName);
        }

        try
        {
            BeginInvoke((MethodInvoker)apply);
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

    private void MarshalGreedyPreparationStage(StageResult stage)
        => MarshalStageToUiThread(stage, OnGreedyPreparationStage);

    private void OnGreedyPreparationStage(StageResult stage)
    {
        if (!_feasibleMode)
            return;

        // Greedy preparation emits two callbacks in order: feasible, then tighten (completed or skipped).
        if (_greedyFeasibleStage is null)
        {
            RecordRunTimeline("pipeline/greedy-preparation-complete", $"{stage.Name}, solve={stage.Timings.Solve.TotalMilliseconds:F1} ms");
            _incumbentStage = stage;
            _greedyFeasibleStage = stage;

            // Start tree materialization as soon as feasible search completes so it can overlap downstream search.
            MarkStageTreeBuilding(stage);
            RecordRunTimeline("presentation/initial-greedy-tree-materialization-started", stage.Name);
            StartStageTreeMaterialization(stage, DisplayInitialGreedyStageTree);
            return;
        }

        if (_greedyTightenStage is not null)
            return;

        _greedyTightenStage = stage;
        _greedyIncumbentImproved = stage.Solution is not null
            && _greedyFeasibleStage?.Solution is not null
            && stage.Solution.Score.IsStrictRefinementOver(_greedyFeasibleStage.Value.Solution!.Score);

        if (_frozenGreedyStageComparisonBaseline is null
            && _incumbentStage is { Solution: not null } incumbent)
        {
            _frozenGreedyStageComparisonBaseline = incumbent;
        }

        RecordRunTimeline("pipeline/greedy-preparation-complete", stage.Skipped
            ? $"skipped, solve={stage.Timings.Solve.TotalMilliseconds:F1} ms"
            : $"{stage.Name}, solve={stage.Timings.Solve.TotalMilliseconds:F1} ms");

        // Keep greedy-tighten on the same UI lifecycle as proof-tighten stages so all greedy edge
        // stages share one buffering/materialization/display path.
        OnProofTightenStage(stage);
    }

    private void OnStageSearchStarted(string stageName)
    {
        // Simplified run headline semantics: once a new stage starts searching, treat the previous
        // one as finished for the single "current stage" clock and progress header.
        RecordRunTimeline("ui/stage-search-started", stageName);
        EnsureStageDisplayOrder(stageName);
        _currentStageName = stageName;
        _stageStartMs = _runStopwatch?.ElapsedMilliseconds ?? 0;
        UpdateInitialRootSearchStage(stageName);
        EnsureLatestStageSearchPlaceholder(stageName);
        UpdateElapsedLabel();
    }

    private void MarshalStageToUiThread(StageResult stage, Action<StageResult> onStage)
    {
        if (!CanAcceptStageCallback())
            return;

        int expectedGeneration = _presentationGeneration;
        TaskCompletionSource<object?>? pauseCompletion = null;
        void apply()
        {
            if (expectedGeneration != _presentationGeneration)
                return;

            BeginStagePause(stage);
            onStage(stage);
            pauseCompletion = _stagePauseCompletion;
        }

        try
        {
            if (_pauseEachStageForRun)
            {
                Invoke((MethodInvoker)apply);
                if (pauseCompletion is not null && _runCancellationSource is { } cancellationSource)
                    pauseCompletion.Task.WaitAsync(cancellationSource.Token).GetAwaiter().GetResult();
            }
            else
            {
                // In normal mode do not block the solver thread on UI work.
                BeginInvoke((MethodInvoker)apply);
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

    private void BeginStagePause(StageResult stage)
    {
        if (!_pauseEachStageForRun)
            return;

        _stagePauseCompletion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pausedStageName = stage.Name;
        _stagePausePresentationReady = false;
        SetRunUiState(RunUiState.StagePaused);
        _statusLabel.Text = FormatStagePauseSummary(stage, presentationReady: false);
    }

    private void MarkStagePausePresentationReady(StageResult stage)
    {
        if (!_pauseEachStageForRun
            || !string.Equals(_pausedStageName, stage.Name, StringComparison.Ordinal))
            return;

        EnsureNextGreedyStageWaitingPlaceholder(stage);
        _stagePausePresentationReady = true;
        _runStopwatch?.Stop();
        SetRunUiState(RunUiState.StagePaused);
        _statusLabel.Text = FormatStagePauseSummary(stage, presentationReady: true);
    }

    private void EnsureNextGreedyStageWaitingPlaceholder(StageResult stage)
    {
        if (!_feasibleMode
            || !stage.Name.StartsWith(StageNames.ProofTightenPrefix, StringComparison.Ordinal)
            || _feasiblePlan is null)
            return;

        int incumbentMaxStep = _incumbentStage?.Solution?.Score.WorstCaseSteps
            ?? _compactPlan?.MaxStep
            ?? _feasiblePlan.MaxStep;
        string nextStageName = stage.Outcome == StageOutcome.Tightened
            ? NextProofTightenStageNameForPresentation(_feasiblePlan, incumbentMaxStep)
            : StageNames.FormatGreedyEdgeCompact(incumbentMaxStep);
        EnsureNextStageWaitingPlaceholder(nextStageName);
    }

    private void ContinuePausedStage()
    {
        if (!_stagePausePresentationReady || _stagePauseCompletion is null)
            return;

        TaskCompletionSource<object?> completion = _stagePauseCompletion;
        _stagePauseCompletion = null;
        _pausedStageName = null;
        _stagePausePresentationReady = false;
        _continueStageButton.Enabled = false;
        _runStopwatch?.Start();
        SetRunUiState(RunUiState.Running);
        completion.TrySetResult(null);
    }

    private static string FormatStagePauseSummary(StageResult stage, bool presentationReady)
    {
        SolvedStrategy? solution = stage.Solution;
        StrategyPlan? plan = stage.MaterializedPlan;
        string maxSteps = solution is null ? "N/A" : solution.Score.WorstCaseSteps.ToString();
        int? edges = plan?.TotalBranchEdges
            ?? solution?.Score.SearchEdgeCost
            ?? solution?.SearchStatistics.SearchTreeEdges;
        string edgeText = edges?.ToString() ?? (presentationReady ? "N/A" : "pending");
        string stateText = solution is null
            ? "N/A"
            : $"searched {solution.SearchStatistics.SearchedStates}, output {solution.SearchStatistics.OutputStates}";
        string phase = presentationReady ? "rendered; review the result, then Continue" : "search complete; rendering result";
        return $"{stage.Name}: {phase}. max steps={maxSteps}, edges={edgeText}, states={stateText}, result={stage.Outcome}.";
    }

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

            MarkStageTreeBuilding(stage);
            StartStageTreeMaterialization(stage, DisplayStepProofStageTree);
            return;
        }

        if (!_exactStepStageMaterialized)
        {
            _pendingExactCompactStage = stage;
            return;
        }

        StartStageTreeMaterialization(stage, DisplayExactCompactStageTree);
    }

    private async Task MaterializeStageTreeAsync(
        StageResult stage,
        Action<StageResult> apply,
        int generation,
        int requestVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            RecordRunTimeline("presentation/stage-tree-materialization-started", stage.Name);
            StageResult materialized;
            if (GetCachedPresentationStageResult(stage) is { } cached)
            {
                materialized = cached;
            }
            else
            {
                TimeSpan priorElapsed = stage.Timings.Solve + stage.Timings.Freeze;
                StrategyPlan plan = await Task.Run(
                    () => StrategyBuilder.MaterializeSolvedStrategy(stage.Solution!, priorElapsed, cancellationToken),
                    cancellationToken);

                TimeSpan materialize = plan.Elapsed - priorElapsed;
                if (materialize < TimeSpan.Zero)
                    materialize = TimeSpan.Zero;

                materialized = new StageResult(
                    stage.Name,
                    plan,
                    stage.Timings.Solve + stage.Timings.Freeze + materialize,
                    stage.Outcome,
                    stage.Solution,
                    new StageTimings(stage.Timings.Solve, stage.Timings.Freeze, materialize));

                CachePresentationStageResult(stage, materialized);
            }

            RecordRunTimeline("presentation/stage-tree-materialization-finished", $"{stage.Name} ({materialized.Timings.Materialize.TotalMilliseconds:F1} ms tree build)");

            if (!CanAcceptStageCallback()
                || generation != _presentationGeneration
                || requestVersion != _presentationRequestVersion)
                return;

            var applyCompleted = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            BeginInvoke(() =>
            {
                try
                {
                    RecordRunTimeline("ui/display-stage-tree/start", stage.Name);
                    if (cancellationToken.IsCancellationRequested
                        || !CanAcceptStageCallback()
                        || generation != _presentationGeneration
                        || requestVersion != _presentationRequestVersion)
                        return;

                    apply(materialized);
                    MarkStagePausePresentationReady(materialized);
                }
                finally
                {
                    RecordRunTimeline("ui/display-stage-tree/done", stage.Name);
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

    private void DisplayStepProofStageTree(StageResult stage)
    {
        StrategyPlan defaultPlan = stage.MaterializedPlan!;
        _incumbentStage = stage;
        _materializedStepDisplayStage = stage;
        _defaultPlan = defaultPlan;
        _feasiblePlan = defaultPlan;
        _latestProgress = CreateSnapshotFromPlan(defaultPlan);
        PopulateTree(defaultPlan, defaultPlan, compactPlan: null, compactImproved: false);
        _completedDefaultStats = defaultPlan.SearchStatistics;
        UpdateSummaryText(defaultPlan, defaultPlan, compactPlan: null, compactImproved: false);
        UpdateStatsPanels();
        RemoveStageStatusPlaceholder(stage.Name);

        // The exact plan is now browsable while compact search may still be running.
        SetRunUiState(RunUiState.CompactComputingInteractive);

        _exactStepStageMaterialized = true;
        if (_pendingExactCompactStage is { } pendingCompact)
        {
            _pendingExactCompactStage = null;
            StartStageTreeMaterialization(pendingCompact, DisplayExactCompactStageTree);
        }
    }

    private void DisplayExactCompactStageTree(StageResult stage)
    {
        if (_defaultPlan is null)
            return;

        StrategyPlan compactPlan = stage.MaterializedPlan!;
        _materializedCompactDisplayStage = stage;
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
        RemoveStageStatusPlaceholder(stage.Name);
    }

    // Name of the next stage RunGreedyPipeline will emit given the best incumbent max-step so
    // far. Mirrors the V2 loop: it tightens to the next proof-tighten ceiling while that ceiling is still above
    // the proven analytic lower bound, otherwise the final "greedy-edge-compact@S" pass runs. Used to label
    // the transient stage-status placeholder so it matches the stage name that actually lands.
    private string NextProofTightenStageName(int incumbentMaxStep)
        => PipelineStageProtocol.NextGreedyStageName(
            _greedyFeasibleStage?.Solution
                ?? throw new InvalidOperationException("Greedy stage naming requires the initial solved strategy."),
            incumbentMaxStep);

    private string NextProofTightenStageNameForPresentation(
        StrategyPlan feasiblePlan,
        int incumbentMaxStep)
    {
        if (_greedyFeasibleStage?.Solution is { } solution)
            return PipelineStageProtocol.NextGreedyStageName(solution, incumbentMaxStep);

        int lower = Math.Max(1, feasiblePlan.SearchStatistics.RootProvenLowerBound);
        int nextBudget = incumbentMaxStep - 1;
        return nextBudget >= lower
            ? StageNames.FormatProofTighten(nextBudget)
            : StageNames.FormatGreedyEdgeCompact(incumbentMaxStep);
    }

    // Anytime greedy edge handler: invoked on the UI thread once per edge stage as the worker thread
    // produces it (each proof-tighten stage, then the final "greedy-edge-compact@S"
    // pass, or a no-solution/incomplete terminal stage). The first stage fills the ready/running slot
    // in place; every later stage is appended as a new tree + overview section, so the user watches
    // the strategy improve stage by stage. Each tree gets a unique scope ("edge0", "edge1", ...) so
    // their per-state navigation keys never collide.
    private void OnProofTightenStage(StageResult stage)
    {
        bool improved = GetOrCreateFrozenStageImprovementDecision(stage);

        // Objective incumbent should advance as soon as an improving solved stage is known,
        // even if tree materialization is still pending.
        if (improved && stage.Solution is not null)
        {
            _incumbentStage = stage;
            _greedyIncumbentImproved = true;
        }

        // A proven-infeasible terminal closes the incumbent squeeze even when this stage is rendered
        // as a search-only summary and returns before the normal tree-update path below.
        if (stage.Outcome == StageOutcome.ProvenInfeasible)
        {
            MarkGreedyIncumbentProvenOptimal();
            RefreshGreedyRootAfterProvenOptimal();
        }

        bool needsDeferredMaterialization = ShouldMaterializeStageForDisplay(stage, improved);

        if (_feasiblePlan is null)
        {
            UpsertReadyGreedyEdgeStage(stage);

            if (TryRenderSearchOnlySummaryStage(stage))
                return;

            if (stage.HasPlan)
            {
                MarkStageTreeReady(stage);
                return;
            }

            if (stage.Solution is null)
                return;

            if (needsDeferredMaterialization)
            {
                MarkStageTreeBuilding(stage);
                StartGreedyEdgeTreeMaterialization(stage);
            }

            return;
        }

        if (_treeView.Nodes.Count == 0)
            return;

        if (TryRenderSearchOnlySummaryStage(stage))
            return;

        if (needsDeferredMaterialization)
        {
            MarkStageTreeBuilding(stage);
            StartGreedyEdgeTreeMaterialization(stage);
            return;
        }

        _proofTightenStages.Add(stage);
        int index = _proofTightenStages.Count - 1;
        string scope = $"edge{index}";

        // A stage is "shown" as a full browsable tree only when it strictly improves the incumbent
        // (the best plan so far: the greedy-feasible plan, then any improving downstream stage). A stage
        // that has a solution but is no better is recorded and marked "no improvement" but shown
        // only as a leaf note. Tightening continues regardless, since the next ceiling is driven by
        // max-steps, not edges.

        _treeView.BeginUpdate();
        TreeNode root = _treeView.Nodes[0];
        InsertOrReplaceStageNode(root.Nodes, BuildStageTreeNode(stage, scope, improved), stage.Name);

        if (improved)
        {
            _compactPlan = stage.MaterializedPlan;
            _incumbentStage = stage;
            _greedyIncumbentImproved = true;
        }

        RefreshGreedyRootAfterProvenOptimal();
        _treeView.EndUpdate();

        _overviewTree.BeginUpdate();
        InsertOrReplaceStageNode(_overviewTree.Nodes, BuildStageOverviewNode(stage, scope, improved), stage.Name);
        _overviewTree.EndUpdate();

        RemoveStageStatusPlaceholder(stage.Name);

        if (stage.HasPlan)
        {
            _latestProgress = CreateSnapshotFromPlan(stage.MaterializedPlan!);
            if (improved)
                UpdateSummaryText(_feasiblePlan, defaultPlan: _feasiblePlan, compactPlan: stage.MaterializedPlan, compactImproved: true);
        }
        UpdateStatsPanels();
        UpdateElapsedLabel();
        MarkStagePausePresentationReady(stage);
    }

    private void RefreshGreedyRootAfterProvenOptimal()
    {
        if (_feasiblePlan is null || _treeView.Nodes.Count == 0)
            return;

        TreeNode root = _treeView.Nodes[0];
        StrategyPlan shown = _compactPlan ?? _feasiblePlan;
        root.Text = BuildDisplayedRootLabel(_feasiblePlan, _feasiblePlan, shown);
        if (_greedyFeasibleStage.HasValue)
        {
            root.Tag = new LazyNodeDetails(() => BuildGreedyProgressionDetails(
                _greedyFeasibleStage.Value,
                _greedyTightenStage,
                _proofTightenStages));
        }
    }

    private bool TryRenderSearchOnlySummaryStage(StageResult stage)
    {
        if (stage.PresentationMode != StagePresentationMode.SearchOnlySummary)
            return false;

        ShowSearchOnlySummaryStage(stage);
        RemoveStageStatusPlaceholder(stage.Name);
        UpdateElapsedLabel();
        MarkStagePausePresentationReady(stage);
        return true;
    }

    private void StartGreedyEdgeTreeMaterialization(StageResult stage)
    {
        if (!_materializingGreedyEdgeStageNames.Add(stage.Name))
            return;

        int generation = _presentationGeneration;
        int requestVersion = _presentationRequestVersion;
        CancellationToken cancellationToken = _presentationCancellationSource?.Token ?? CancellationToken.None;

        Task task = MaterializeStageTreeAsync(
            stage,
            OnGreedyEdgeStageTreeReady,
            generation,
            requestVersion,
            cancellationToken);
        _greedyEdgeTreeMaterializationTasks.Add(task);
        _ = ObserveGreedyEdgeTreeMaterializationAsync(stage.Name, task);
    }

    private async Task ObserveGreedyEdgeTreeMaterializationAsync(string stageName, Task task)
    {
        try
        {
            await task;
        }
        finally
        {
            if (!CanAcceptStageCallback())
            {
                _materializingGreedyEdgeStageNames.Remove(stageName);
                _greedyEdgeTreeMaterializationTasks.Remove(task);
            }
            else
            {
                try
                {
                    BeginInvoke(() =>
                    {
                        _materializingGreedyEdgeStageNames.Remove(stageName);
                        _greedyEdgeTreeMaterializationTasks.Remove(task);

                        if (_feasiblePlan is null)
                            return;

                        if (TryTakeReadyGreedyEdgeStage(stageName, out StageResult readyStage))
                            OnProofTightenStage(readyStage);
                    });
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
        }
    }

    private void OnGreedyEdgeStageTreeReady(StageResult stage)
    {
        UpsertReadyGreedyEdgeStage(stage);
        MarkStageTreeReady(stage);

        if (_feasiblePlan is null)
            return;

        if (TryTakeReadyGreedyEdgeStage(stage.Name, out StageResult readyStage))
            OnProofTightenStage(readyStage);
    }

    private void UpsertReadyGreedyEdgeStage(StageResult stage)
    {
        for (int i = 0; i < _readyGreedyEdgeStages.Count; i++)
        {
            if (string.Equals(_readyGreedyEdgeStages[i].Name, stage.Name, StringComparison.Ordinal))
            {
                _readyGreedyEdgeStages[i] = stage;
                return;
            }
        }

        _readyGreedyEdgeStages.Add(stage);
    }

    private bool TryTakeReadyGreedyEdgeStage(string stageName, out StageResult stage)
    {
        for (int i = 0; i < _readyGreedyEdgeStages.Count; i++)
        {
            if (!string.Equals(_readyGreedyEdgeStages[i].Name, stageName, StringComparison.Ordinal))
                continue;

            stage = _readyGreedyEdgeStages[i];
            _readyGreedyEdgeStages.RemoveAt(i);
            return true;
        }

        stage = default;
        return false;
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
        StrategyPlan? incumbent = incumbentStage.MaterializedPlan
            ?? _compactPlan
            ?? _feasiblePlan;
        if (incumbent is null)
            return;

        int provenLower = incumbentStage.Solution.Score.WorstCaseSteps;
        if (incumbent.SearchStatistics.RootProvenLowerBound >= provenLower)
            return;

        StageResult provenStage = incumbentStage.WithProvenLowerBound(provenLower);
        StrategyPlan proven = incumbent.WithRootProvenLowerBound(provenLower);
        if (!provenStage.HasPlan)
        {
            provenStage = new StageResult(
                provenStage.Name,
                proven,
                provenStage.Elapsed,
                provenStage.Outcome,
                provenStage.Solution,
                provenStage.Timings,
                provenStage.PresentationMode);
        }

        if (_compactPlan is not null)
        {
            for (int i = 0; i < _proofTightenStages.Count; i++)
            {
                if (ReferenceEquals(_proofTightenStages[i].MaterializedPlan, incumbent))
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
        _frozenGreedyStageComparisonBaseline = provenStage;
    }

}
