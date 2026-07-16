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
        _exactImproved = false;
        _compactImproved = false;
        _activePhase = 0;
        _proofTightenStages.Clear();
        _currentStageName = feasibleMode ? "greedy-feasible" : "step-proof";
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
                StrategyPlan feasiblePlan = await Task.Run(() => builder.BuildGreedyFeasibleStage(), cancellationToken);
                StrategyPlan baseFeasiblePlan = feasiblePlan;

                // Optional GT pre-step (root-probe gated): only run single-round GreedyTighten when
                // the root micro-probe sees a possible root-height drop.
                bool gtProbeRun = await Task.Run(() => builder.ShouldRunGreedyTightenByRootProbe(), cancellationToken);
                if (gtProbeRun)
                {
                    StrategyPlan gtPlan = await Task.Run(() => builder.BuildGreedyTightenPlan(), cancellationToken);
                    if (gtPlan.IsStrictRefinementOver(baseFeasiblePlan))
                    {
                        feasiblePlan = gtPlan;
                        builder.OverrideGreedyPipelineUpperBound(feasiblePlan.MaxStep);
                    }
                }

                _feasiblePlan = feasiblePlan;
                _latestProgress = CreateSnapshotFromPlan(feasiblePlan);
                PopulateTree(feasiblePlan, defaultPlan: null, compactPlan: null, exactImproved: false, compactImproved: false);
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
                    () => builder.RunGreedyPipeline(MarshalProofTightenStage),
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
            StrategyPlan defaultPlan = await Task.Run(() => builder.BuildStepProofStage(), cancellationToken);

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
            _currentStageName = StrategyBuilder.FormatEdgeCompactExactStageName();
            _stageStartMs = _runStopwatch?.ElapsedMilliseconds ?? 0;
            StrategyPlan compactPlan = await Task.Run(() => builder.BuildEdgeCompactStage(), cancellationToken);
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
            MarkResultsStopped();
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
            _stopEscalationSource?.Cancel();
            _stopEscalationSource?.Dispose();
            _stopEscalationSource = null;
            _runCancellationSource?.Dispose();
            _runCancellationSource = null;
            _activeBuilder = null;
        }
    }

    private const string ComputingSuffix = ": computing...";

    private static string FormatComputingPlaceholderText(string stageName)
        => stageName + ComputingSuffix;

    private TreeNode CreateComputingPlaceholderNode(string stageName)
        => new(FormatComputingPlaceholderText(stageName)) { ForeColor = _palette.MutedForeColor };

    private static bool TryRemoveTrailingComputingPlaceholder(TreeNodeCollection nodes)
    {
        if (nodes.Count == 0 || !IsComputingPlaceholderText(nodes[nodes.Count - 1].Text))
            return false;

        nodes.RemoveAt(nodes.Count - 1);
        return true;
    }

    private static bool TryMarkTrailingComputingPlaceholderStopped(TreeNodeCollection nodes)
    {
        if (nodes.Count == 0)
            return false;

        TreeNode tail = nodes[nodes.Count - 1];
        if (!IsComputingPlaceholderText(tail.Text))
            return false;

        tail.Text = MarkComputingPlaceholderStopped(tail.Text);
        return true;
    }

    // A trailing tree/overview node ending in ": computing..." is a transient in-progress placeholder
    // (the initial second-stage slot, or a live "proof-tighten<=N: computing..." probe appended between
    // greedy tightening stages). Both are replaced in place once the stage they announce lands.
    private static bool IsComputingPlaceholderText(string text)
        => text.EndsWith(ComputingSuffix, StringComparison.Ordinal);

    // Rewrite a "<stage>: computing..." placeholder to "<stage>: stopped (not computed)" so a Stop
    // leaves no wording implying a computation is still running.
    private static string MarkComputingPlaceholderStopped(string text)
        => IsComputingPlaceholderText(text)
            ? text[..^ComputingSuffix.Length] + ": stopped (not computed)"
            : text;

    // On a user Stop, an interrupted stage leaves transient "computing.../in progress" placeholders on
    // screen (tree root suffix, the trailing compact slot, and the root details). Rewrite them to a
    // "stopped" wording so nothing still implies a computation is running. If the compact/edge stage had
    // already produced output, the placeholders were replaced during the run and there is nothing to fix.
    private void MarkResultsStopped()
    {
        if (_treeView.Nodes.Count == 0)
            return;

        TreeNode root = _treeView.Nodes[0];
        _treeView.BeginUpdate();
        bool markedTreePlaceholder = TryMarkTrailingComputingPlaceholderStopped(root.Nodes);
        if (markedTreePlaceholder)
        {
            root.Text = MarkLabelStopped(root.Text);
            if (root.Tag is string tag)
                root.Tag = MarkDetailsStopped(tag);
        }
        _treeView.EndUpdate();

        if (!markedTreePlaceholder)
            return;

        if (_overviewTree.Nodes.Count > 0)
        {
            _overviewTree.BeginUpdate();
            TryMarkTrailingComputingPlaceholderStopped(_overviewTree.Nodes);
            _overviewTree.EndUpdate();
        }
    }

    // Defensive cleanup after a normal (non-stopped) greedy run: RunGreedyPipeline always ends
    // by emitting the terminal EdgeCompact stage, whose handler appends no follow-up placeholder. But the
    // should-not-happen fallback (edgePlan null) returns without that final emission, which would leave
    // the last "edge compact ...: computing..." placeholder stranded. Drop any such trailing placeholder so a
    // finished run never shows a "computing..." node.
    private void RemoveTrailingComputingPlaceholder()
    {
        if (_treeView.Nodes.Count > 0)
        {
            TreeNode root = _treeView.Nodes[0];
            if (root.Nodes.Count > 0)
            {
                _treeView.BeginUpdate();
                TryRemoveTrailingComputingPlaceholder(root.Nodes);
                _treeView.EndUpdate();
            }
        }

        if (_overviewTree.Nodes.Count > 0)
        {
            _overviewTree.BeginUpdate();
            TryRemoveTrailingComputingPlaceholder(_overviewTree.Nodes);
            _overviewTree.EndUpdate();
        }
    }

    private static string MarkLabelStopped(string label)
    {
        int open = label.LastIndexOf(" (computing ", StringComparison.Ordinal);
        return open >= 0 ? label[..open] + " (stopped)" : label;
    }

    private static string MarkDetailsStopped(string details)
        => details
            .Replace("next stage in progress", "next stage not run (stopped)")
            .Replace("edge compact exact stage in progress", "edge compact exact stage not run (stopped)");

    // Before the first stage returns a real plan, show an explicit in-progress placeholder so the tree
    // region is never visually empty during the initial compute.
    private void ShowInitialStagePlaceholder(int n, int m, int k, bool feasibleMode)
    {
        string stageName = feasibleMode ? "greedy-feasible" : "step-proof";
        string rootLabel = feasibleMode
            ? $"n={n}, m={m}, k={k} (computing greedy-feasible stage...)"
            : $"n={n}, m={m}, k={k} (computing step-proof stage...)";
        string rootDetails = feasibleMode
            ? "Greedy-feasible stage in progress."
            : "Step-proof stage in progress.";

        _treeView.BeginUpdate();
        _treeView.Nodes.Clear();
        var root = new TreeNode(rootLabel)
        {
            Tag = new LazyNodeDetails(() => rootDetails),
            NodeFont = new Font(_treeView.Font, FontStyle.Bold),
            ForeColor = _palette.ForeColor,
        };
        root.Nodes.Add(CreateComputingPlaceholderNode(stageName));
        _treeView.Nodes.Add(root);
        root.Expand();
        _treeView.EndUpdate();
        _treeView.SelectedNode = root;

        _overviewTree.BeginUpdate();
        _overviewTree.Nodes.Clear();
        _overviewTree.Nodes.Add(BuildOverviewNoteNode(FormatComputingPlaceholderText(stageName)));
        _overviewTree.EndUpdate();
    }

    private void PopulateTree(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan, bool exactImproved, bool compactImproved)
    {
        _treeView.BeginUpdate();
        _treeView.Nodes.Clear();
        _stateNodesByKey.Clear();
        _referenceTargets.Clear();
        _lazyDecisions.Clear();
        _lazyOverviewSections.Clear();
        _jumpTargets.Clear();
        _jumpScopeRoots.Clear();
        _jumpScopeStrategyRoots.Clear();
        _indexedJumpScopes.Clear();
        _navigationHistory.Clear();
        _backButton.Enabled = false;

        string rootLabel = BuildRootLabel(feasiblePlan, defaultPlan, compactPlan);
        var rootDetails = new LazyNodeDetails(() => BuildRootDetails(feasiblePlan, defaultPlan, compactPlan, exactImproved, compactImproved));

        var root = new TreeNode(rootLabel)
        {
            Tag = rootDetails,
            NodeFont = new Font(_treeView.Font, FontStyle.Bold),
            ForeColor = _palette.ForeColor,
        };

        // Slot 0: the step strategy, named by mode -- "step-proof" once the exact pass finishes (it
        // replaces the placeholder in place), or "greedy-feasible" for the constructive feasible plan
        // in greedy mode.
        StrategyPlan stepPlan = defaultPlan ?? feasiblePlan;
        string stepStageName = defaultPlan is null ? "greedy-feasible" : "step-proof";
        root.Nodes.Add(CreatePlanTreeRoot(stepStageName, stepPlan, "default", stepPlan.Elapsed));

        // Slot 1: the second stage's live placeholder. In exact mode this is the min-edge
        // "edge compact exact" pass; in greedy mode it is whatever RunGreedyPipeline emits first --
        // a "proof-tighten<=N" tightening stage, or "edge compact greedy" directly when the greedy bound is
        // already at the lower bound.
        if (compactPlan is null)
        {
            string firstStageName = defaultPlan is null
                ? NextProofTightenStageName(feasiblePlan, feasiblePlan.MaxStep)
                : StrategyBuilder.FormatEdgeCompactExactStageName();
            root.Nodes.Add(CreateComputingPlaceholderNode(firstStageName));
        }
        else if (compactImproved)
            root.Nodes.Add(CreatePlanTreeRoot(defaultPlan is null ? StrategyBuilder.FormatEdgeCompactGreedyStageName() : StrategyBuilder.FormatEdgeCompactExactStageName(), compactPlan, "compact", compactPlan.Elapsed));
        else
            root.Nodes.Add(CreateNoSolutionTreeRoot(defaultPlan is null ? StrategyBuilder.FormatEdgeCompactGreedyStageName() : StrategyBuilder.FormatEdgeCompactExactStageName(), compactPlan.Elapsed));

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
        if (upper == 0)
            return "max steps = 0 (proven optimal)";
        if (lower > 0 && lower == upper)
            return $"max steps = {upper} (proven optimal)";

        string lowerText = lower > 0 ? lower.ToString() : "?";
        return $"{lowerText} <= max steps <= {upper}";
    }

    private static string FormatPlanInputs(StrategyPlan plan)
    {
        if (plan.RequestedK == plan.K)
            return $"n={plan.N}, m={plan.M}, k={plan.K}";
        return $"n={plan.N}, m={plan.M}, k={plan.RequestedK} (dual k'={plan.K})";
    }

    private static string BuildRootLabel(StrategyPlan feasiblePlan, StrategyPlan? defaultPlan, StrategyPlan? compactPlan)
    {
        string head = FormatPlanInputs(feasiblePlan);
        if (defaultPlan is null)
            return $"{head}, {FormatPlanSqueeze(feasiblePlan)} (computing step...)";
        if (compactPlan is null)
        {
            double seconds = feasiblePlan.Elapsed.TotalSeconds + defaultPlan.Elapsed.TotalSeconds;
            return $"{head}, max steps={defaultPlan.MaxStep}, elapsed={seconds:F3} s (computing edge compact exact stage...)";
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
        root.Tag = new LazyNodeDetails(() => BuildTwoPhaseDetails(defaultPlan, compactPlan, compactImproved));

        // Replace only the trailing compact slot (everything after the single step slot).
        while (root.Nodes.Count > 1)
            root.Nodes.RemoveAt(root.Nodes.Count - 1);
        string compactStageName = _defaultPlan is null
            ? StrategyBuilder.FormatEdgeCompactGreedyStageName()
            : StrategyBuilder.FormatEdgeCompactExactStageName();
        if (compactImproved)
            root.Nodes.Add(CreatePlanTreeRoot(compactStageName, compactPlan, "compact", compactPlan.Elapsed));
        else
            root.Nodes.Add(CreateNoSolutionTreeRoot(compactStageName, compactPlan.Elapsed));

        _treeView.EndUpdate();

        FinalizeCompactInOverview(compactPlan, compactImproved);
    }

    // Synchronous marshaling shim: RunGreedyPipeline invokes this on the worker thread once per
    // stage. Control.Invoke hops to the UI thread AND blocks the worker until OnProofTightenStage
    // returns, so when the per-stage modal is enabled the search genuinely pauses until the user clicks OK.
    private void MarshalProofTightenStage(StageResult stage)
    {
        if (!IsHandleCreated || IsDisposed)
            return;
        try
        {
            Invoke(() => OnProofTightenStage(stage));
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

    // Name of the next stage RunGreedyPipeline will emit given the best incumbent max-step so
    // far. Mirrors the V2 loop: it tightens to "proof-tighten<=(step-1)" while that ceiling is still above
    // the proven analytic lower bound, otherwise the final "edge compact greedy" pass runs. Used to label
    // the transient "...: computing..." placeholder so it matches the stage name that actually lands
    // (proof-tightening stages surface as "proof-tighten<=N", the final min-edge pass as "edge compact greedy").
    private static string NextProofTightenStageName(StrategyPlan feasiblePlan, int incumbentMaxStep)
    {
        int lower = Math.Max(1, feasiblePlan.SearchStatistics.RootProvenLowerBound);
        int nextBudget = incumbentMaxStep - 1;
        return nextBudget >= lower ? $"proof-tighten\u2264{nextBudget}" : StrategyBuilder.FormatEdgeCompactGreedyStageName();
    }

    // Anytime greedy edge handler: invoked on the UI thread once per edge stage as the worker thread
    // produces it (each "proof-tighten<=N" proof-tightening stage, then the final "edge compact greedy"
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
        bool improved = stage.HasPlan && stage.Plan!.IsStrictRefinementOver(incumbent);

        // A follow-up stage always lands after every emitted stage except the terminal EdgeCompact
        // pass: after a "proof-tighten<=N" stage -- whether it found a solution or proved/failed the
        // ceiling -- the worker next probes a deeper feasible ceiling or runs the final edge-compaction
        // pass. We announce that in-progress probe with a trailing "<next>: computing..." placeholder
        // so the tree/overview never look idle while it runs. The terminal EdgeCompact stage has nothing
        // after it, so it appends no placeholder.
        bool hasFollowUp = !IsEdgeCompactStageName(stage.Name);
        string? nextStageName = !hasFollowUp
            ? null
            : stage.IsTightened
                ? NextProofTightenStageName(_feasiblePlan, stage.Plan!.MaxStep)
            : StrategyBuilder.FormatEdgeCompactGreedyStageName(); // Phase A ended (proven-infeasible/incomplete); only the edge-compaction pass remains

        _treeView.BeginUpdate();
        TreeNode root = _treeView.Nodes[0];
        // Replace the trailing in-progress placeholder (the initial second-stage slot, or the previous
        // probe's "proof-tighten<=N: computing..." note) with the landed stage.
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

    private TreeNode BuildStageTreeNode(StageResult stage, string scope, bool improved)
        => improved
            ? CreatePlanTreeRoot(stage.Name, stage.Plan!, scope, stage.Elapsed)
            : stage.HasPlan
                ? CreateNoImprovementTreeRoot(stage.Name, stage.Plan!, stage.Elapsed)
                : CreateNoSolutionTreeRoot(stage.Name, stage.Elapsed, NoSolutionMarker(stage));

    private TreeNode BuildStageOverviewNode(StageResult stage, string scope, bool improved)
        => improved
            ? BuildOverviewSectionNode(stage.Plan!, scope, stage.Name, stage.Elapsed)
            : BuildOverviewNoteNode(FormatStageRootLabel(
                stage.Name,
                stage.Elapsed,
                stage.Plan,
                stage.HasPlan ? "no improvement" : NoSolutionMarker(stage)));

    // Leaf note for a solution-less stage: null means "no solution" (a proven-infeasible ceiling),
    // otherwise the reason the incumbent merely stands -- "search incomplete (candidate cap reached)"
    // (the greedy cap truncated the enumeration, so infeasibility is unproven).
    private static string? NoSolutionMarker(StageResult stage)
        => stage.Incomplete ? "search incomplete (candidate cap reached)"
            : null;

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

    // Root-node detail text for greedy mode: the step plan followed by the full edge progression
    // (compact baseline -> each tightening -> any no-solution stage), so the detail pane mirrors the
    // stacked trees.
    private static string BuildGreedyProgressionDetails(StrategyPlan stepPlan, List<StageResult> stages)
    {
        var lines = new List<string>
        {
            "GreedyFeasible result (anytime: improving stages are shown as trees)",
            $"greedy-feasible: {FormatPlanSqueeze(stepPlan)}, total edges={stepPlan.TotalBranchEdges}",
        };
        StrategyPlan incumbent = stepPlan;
        foreach (StageResult stage in stages)
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
            else if (stage.Incomplete)
            {
                lines.Add($"{stage.Name}: search incomplete (candidate cap reached; infeasibility unproven, best plan kept)");
            }
            else
            {
                lines.Add($"{stage.Name}: no solution (no better strategy at this step ceiling)");
            }
        }
        return string.Join(Environment.NewLine, lines);
    }

}
