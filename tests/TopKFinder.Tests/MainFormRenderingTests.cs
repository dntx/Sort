using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;
using TopKFinder;

public sealed class MainFormRenderingTests
{
    private static readonly DisplayRenderEngine Engine = new();

    [Fact]
    public void BuildPlanDetails_EmitsDisplayEngineRenderedText()
    {
        StrategyPlan plan = new StrategyBuilder(9, 3, 3).ExecuteStepProofStage();

        string expectedText = Engine.RenderStrategyText(plan).TrimEnd();
        string actualText = InvokePrivateStatic<string>(typeof(MainForm), "BuildPlanDetails", plan);

        Assert.StartsWith(expectedText, actualText);
    }

    [Fact]
    public void BuildFeasibleOnlyDetails_EmitsDisplayEngineRenderedText()
    {
        StrategyPlan plan = new StrategyBuilder(9, 3, 3).ExecuteStepProofStage();

        string expectedText = Engine.RenderStrategyText(plan).TrimEnd();
        string actualText = InvokePrivateStatic<string>(typeof(MainForm), "BuildFeasibleOnlyDetails", plan);

        Assert.Contains(expectedText, actualText);
    }

    [Fact]
    public void OverviewMaterialization_EmitsDisplayEngineOverviewRows()
    {
        StrategyPlan plan = new StrategyBuilder(9, 3, 3).ExecuteStepProofStage();
        StrategyOverview expectedOverview = Engine.BuildOverview(plan);

        using var form = new MainForm();
        InvokePrivateInstanceVoid(form, "RebuildOverview", plan, null, null, false);

        TreeView overviewTree = GetPrivateField<TreeView>(form, "_overviewTree");
        Assert.True(overviewTree.Nodes.Count > 0);
        TreeNode sectionNode = overviewTree.Nodes[0];

        InvokePrivateInstanceVoid(form, "MaterializeOverviewSection", sectionNode);

        Assert.Equal(expectedOverview.Rows.Count, sectionNode.Nodes.Count);
        for (int i = 0; i < expectedOverview.Rows.Count; i++)
        {
            OverviewRow expectedRow = expectedOverview.Rows[i];
            TreeNode actualNode = sectionNode.Nodes[i];
            Assert.Equal(expectedRow.Headline, actualNode.Text);
            Assert.Equal(expectedRow.Details.Count, actualNode.Nodes.Count);
            for (int j = 0; j < expectedRow.Details.Count; j++)
            {
                Assert.Equal(expectedRow.Details[j], actualNode.Nodes[j].Text);
            }
        }
    }

    [Fact]
    public void DeferredSolutionOnlyStage_RendersNoImprovementMarkerInTree()
    {
        StageResult stage = CreateDeferredExactStepStage();
        Assert.False(stage.HasPlan);
        Assert.NotNull(stage.Solution);

        using var form = new MainForm();
        TreeNode node = InvokePrivateInstance<TreeNode>(
            form,
            "BuildStageTreeNode",
            stage,
            "edge0",
            false);

        Assert.Contains("no improvement", node.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("no solution", node.Text, StringComparison.Ordinal);
        Assert.True(node.NodeFont is null || node.NodeFont.Style == FontStyle.Regular);
    }

    [Fact]
    public void InfeasibleStage_RendersNoSolutionWithoutBoldFont()
    {
        var stage = new StageResult(
            "proof-tighten<=3",
            materializedPlan: null,
            elapsed: TimeSpan.FromMilliseconds(1),
            outcome: StageOutcome.ProvenInfeasible,
            solution: null,
            timings: StageTimings.Legacy(TimeSpan.FromMilliseconds(1)));

        using var form = new MainForm();
        TreeNode node = InvokePrivateInstance<TreeNode>(
            form,
            "BuildStageTreeNode",
            stage,
            "edge0",
            false);

        Assert.Contains("no solution", node.Text, StringComparison.Ordinal);
        Assert.True(node.NodeFont is null || node.NodeFont.Style == FontStyle.Regular);
    }

    [Fact]
    public void DeferredSolutionOnlyStage_RendersNoImprovementMarkerInOverview()
    {
        StageResult stage = CreateDeferredExactStepStage();
        Assert.False(stage.HasPlan);
        Assert.NotNull(stage.Solution);

        using var form = new MainForm();
        TreeNode node = InvokePrivateInstance<TreeNode>(
            form,
            "BuildStageOverviewNode",
            stage,
            "edge0",
            false);

        Assert.Contains("no improvement", node.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("no solution", node.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MarshalStageToUiThread_PauseDisabled_DoesNotBlockWorkerCallback()
    {
        using var form = new MainForm();
        _ = form.Handle;
        SetPrivateField(form, "_pauseEachStageForRun", false);

        bool callbackRan = false;
        var stage = new StageResult("proof-tighten<=3", materializedPlan: null, TimeSpan.Zero, StageOutcome.Tightened, CreateDeferredExactStepStage().Solution);
        var stopwatch = Stopwatch.StartNew();
        InvokePrivateInstanceVoid(
            form,
            "MarshalStageToUiThread",
            stage,
            (Action<StageResult>)(_ =>
            {
                Thread.Sleep(120);
                callbackRan = true;
            }));
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 100);
        Assert.False(callbackRan);

        Application.DoEvents();
        Assert.True(callbackRan);
    }

    [Fact]
    public void MarshalStageToUiThread_PauseEnabled_WaitsForRenderAndContinue()
    {
        using var form = new MainForm();
        _ = form.Handle;
        SetPrivateField(form, "_pauseEachStageForRun", true);
        SetPrivateField(form, "_runCancellationSource", new CancellationTokenSource());

        bool callbackRan = false;
        var stage = new StageResult("proof-tighten<=3", materializedPlan: null, TimeSpan.Zero, StageOutcome.Tightened, CreateDeferredExactStepStage().Solution);
        Task worker = Task.Run(() => InvokePrivateInstanceVoid(
            form,
            "MarshalStageToUiThread",
            stage,
            (Action<StageResult>)(_ => callbackRan = true)));

        Assert.True(PumpMessagesUntil(() => callbackRan, TimeSpan.FromSeconds(2)));
        Assert.False(worker.IsCompleted);
        Button continueButton = GetPrivateField<Button>(form, "_continueStageButton");
        Assert.False(continueButton.Enabled);

        InvokePrivateInstanceVoid(form, "MarkStagePausePresentationReady", stage);
        Assert.True(continueButton.Enabled);
        Assert.False(worker.IsCompleted);

        InvokePrivateInstanceVoid(form, "ContinuePausedStage");
        Assert.True(PumpMessagesUntil(() => worker.IsCompleted, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void MarshalStageToUiThread_PauseEnabled_StopReleasesWorker()
    {
        using var form = new MainForm();
        _ = form.Handle;
        SetPrivateField(form, "_pauseEachStageForRun", true);
        var cancellationSource = new CancellationTokenSource();
        SetPrivateField(form, "_runCancellationSource", cancellationSource);

        bool callbackRan = false;
        var stage = new StageResult("proof-tighten<=3", materializedPlan: null, TimeSpan.Zero, StageOutcome.Tightened, CreateDeferredExactStepStage().Solution);
        Task worker = Task.Run(() =>
        {
            try
            {
                InvokePrivateInstanceVoid(
                    form,
                    "MarshalStageToUiThread",
                    stage,
                    (Action<StageResult>)(_ => callbackRan = true));
            }
            catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException)
            {
            }
        });

        Assert.True(PumpMessagesUntil(() => callbackRan, TimeSpan.FromSeconds(2)));
        cancellationSource.Cancel();
        Assert.True(PumpMessagesUntil(() => worker.IsCompleted, TimeSpan.FromSeconds(2)));
        Assert.Equal(TaskStatus.RanToCompletion, worker.Status);
    }

    private static bool PumpMessagesUntil(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.Elapsed < timeout)
        {
            Application.DoEvents();
            Thread.Yield();
        }

        Application.DoEvents();
        return condition();
    }

    [Fact]
    public void MarshalStageToUiThread_StaleGeneration_DropsCallback()
    {
        using var form = new MainForm();
        _ = form.Handle;
        SetPrivateField(form, "_pauseEachStageForRun", false);
        SetPrivateField(form, "_presentationGeneration", 10);

        bool callbackRan = false;
        var stage = new StageResult("proof-tighten<=3", materializedPlan: null, TimeSpan.Zero, StageOutcome.Tightened, CreateDeferredExactStepStage().Solution);
        InvokePrivateInstanceVoid(
            form,
            "MarshalStageToUiThread",
            stage,
            (Action<StageResult>)(_ => callbackRan = true));

        // Simulate a new run generation before queued callback dispatch.
        SetPrivateField(form, "_presentationGeneration", 11);
        Application.DoEvents();

        Assert.False(callbackRan);
    }

    [Fact]
    public async Task MaterializeStageTreeAsync_StaleRequestVersion_DoesNotApply()
    {
        StageResult stage = CreateDeferredExactStepStage();

        using var form = new MainForm();
        _ = form.Handle;
        SetPrivateField(form, "_presentationGeneration", 7);
        SetPrivateField(form, "_presentationRequestVersion", 5);

        bool applied = false;
        Task task = InvokePrivateInstance<Task>(
            form,
            "MaterializeStageTreeAsync",
            stage,
            (Action<StageResult>)(_ => applied = true),
            7,
            4,
            CancellationToken.None);

        await task;
        Application.DoEvents();

        Assert.False(applied);
    }

    [Fact]
    public async Task MaterializeStageTreeAsync_CurrentRequestVersion_Applies()
    {
        StageResult stage = CreateDeferredExactStepStage();

        using var form = new MainForm();
        _ = form.Handle;
        SetPrivateField(form, "_presentationGeneration", 8);
        SetPrivateField(form, "_presentationRequestVersion", 6);
        InvokePrivateInstanceVoid(form, "CachePresentationStageResult", stage, stage);

        bool applied = false;
        Task task = InvokePrivateInstance<Task>(
            form,
            "MaterializeStageTreeAsync",
            stage,
            (Action<StageResult>)(_ => applied = true),
            8,
            6,
            CancellationToken.None);

        PumpUiUntilTaskCompletes(task, timeoutMs: 1000);
        await task;

        Assert.True(applied);
    }

    [Fact]
    public async Task MaterializeStageTreeAsync_OnlyCurrentRequestApplies()
    {
        StageResult stage = CreateDeferredExactStepStage();

        using var form = new MainForm();
        _ = form.Handle;
        SetPrivateField(form, "_presentationGeneration", 11);
        SetPrivateField(form, "_presentationRequestVersion", 9);
        InvokePrivateInstanceVoid(form, "CachePresentationStageResult", stage, stage);

        bool staleApplied = false;
        bool currentApplied = false;

        Task stale = InvokePrivateInstance<Task>(
            form,
            "MaterializeStageTreeAsync",
            stage,
            (Action<StageResult>)(_ => staleApplied = true),
            11,
            8,
            CancellationToken.None);
        Task current = InvokePrivateInstance<Task>(
            form,
            "MaterializeStageTreeAsync",
            stage,
            (Action<StageResult>)(_ => currentApplied = true),
            11,
            9,
            CancellationToken.None);

        Task combined = Task.WhenAll(stale, current);
        PumpUiUntilTaskCompletes(combined, timeoutMs: 1000);
        await combined;

        Assert.False(staleApplied);
        Assert.True(currentApplied);
    }

    [Fact]
    public void ShowNodeDetails_StaleLazyCompletion_DoesNotOverrideLatestSelection()
    {
        using var form = new MainForm();
        _ = form.Handle;

        TreeView treeView = GetPrivateField<TreeView>(form, "_treeView");
        RichTextBox details = GetPrivateField<RichTextBox>(form, "_detailsTextBox");
        using var gate = new ManualResetEventSlim(false);

        var staleNode = new TreeNode("stale")
        {
            Tag = CreateLazyNodeDetails(() =>
            {
                gate.Wait();
                return "stale-details";
            }),
        };
        var latestNode = new TreeNode("latest")
        {
            Tag = "latest-details",
        };

        treeView.Nodes.Add(staleNode);
        treeView.Nodes.Add(latestNode);

        treeView.SelectedNode = staleNode;
        InvokePrivateInstanceVoid(form, "ShowNodeDetails", staleNode);
        Assert.Equal("Loading details...", details.Text);

        treeView.SelectedNode = latestNode;
        InvokePrivateInstanceVoid(form, "ShowNodeDetails", latestNode);
        Assert.Equal("latest-details", details.Text);

        gate.Set();
        PumpUiUntil(() => details.Text == "latest-details", timeoutMs: 1000);

        Assert.Equal("latest-details", details.Text);
    }

    [Fact]
    public async Task StartStageTreeMaterialization_NewRequestCancelsPriorRequest()
    {
        StageResult stage = CreateDeferredExactStepStage();

        using var form = new MainForm();
        _ = form.Handle;
        InvokePrivateInstanceVoid(form, "ResetPresentationInfrastructure");

        InvokePrivateInstanceVoid(
            form,
            "StartStageTreeMaterialization",
            stage,
            (Action<StageResult>)(_ => { }));
        CancellationTokenSource firstRequest = GetPrivateField<CancellationTokenSource>(form, "_activePresentationRequestSource");

        InvokePrivateInstanceVoid(
            form,
            "StartStageTreeMaterialization",
            stage,
            (Action<StageResult>)(_ => { }));
        CancellationTokenSource secondRequest = GetPrivateField<CancellationTokenSource>(form, "_activePresentationRequestSource");

        // Force any in-flight materialization to short-circuit before UI apply so drain is deterministic in tests.
        SetPrivateField(form, "_presentationRequestVersion", int.MaxValue);
        Task drain = InvokePrivateInstance<Task>(form, "DrainPresentationTasksAsync");
        await drain;

        Assert.NotSame(firstRequest, secondRequest);
        Assert.True(firstRequest.IsCancellationRequested);
        Assert.False(secondRequest.IsCancellationRequested);
    }

    [Fact]
    public void OnProofTightenStage_NonMaterializingStage_DoesNotInvalidateOlderPresentationRequest()
    {
        StageResult deferredStage = CreateDeferredExactStepStage();
        StrategyPlan feasiblePlan = new StrategyBuilder(8, 3, 3).ExecuteStepProofStage();

        using var form = new MainForm();
        _ = form.Handle;
        SetPrivateField(form, "_feasiblePlan", feasiblePlan);
        SetPrivateField(form, "_incumbentStage", deferredStage);
        SetPrivateField(form, "_greedyFeasibleStage", deferredStage);
        InvokePrivateInstanceVoid(form, "ShowInitialStagePlaceholder", 8, 3, 3, true);

        InvokePrivateInstanceVoid(
            form,
            "StartStageTreeMaterialization",
            deferredStage,
            (Action<StageResult>)(_ => { }));
        CancellationTokenSource pendingRequest = GetPrivateField<CancellationTokenSource>(form, "_activePresentationRequestSource");
        int requestVersionBefore = GetPrivateField<int>(form, "_presentationRequestVersion");

        var terminalNoPlanStage = new StageResult(
            StageNames.FormatProofTighten(feasiblePlan.MaxStep - 1),
            materializedPlan: null,
            elapsed: TimeSpan.FromMilliseconds(1),
            outcome: StageOutcome.Incomplete,
            solution: null);

        InvokePrivateInstanceVoid(form, "OnProofTightenStage", terminalNoPlanStage);

        int requestVersionAfter = GetPrivateField<int>(form, "_presentationRequestVersion");
        Assert.False(pendingRequest.IsCancellationRequested);
        Assert.Equal(requestVersionBefore, requestVersionAfter);
    }

    [Fact]
    public void OnProofTightenStage_Reentry_KeepsFrozenImprovementDecision()
    {
        var builder = new StrategyBuilder(8, 3, 3);
        GreedyPreparationResult prep = PublicPipelineOrchestrator.RunGreedyPreparation(
            builder,
            emitStages: false,
            materialize: true);

        StrategyPlan baselinePlan = prep.BaseFeasiblePlan
            ?? throw new InvalidOperationException("Expected greedy feasible plan.");
        SolvedStrategy baselineSolution = prep.BaseFeasibleSolution;
        StageResult baselineStage = new(
            StageNames.GreedyFeasible,
            baselinePlan,
            baselinePlan.Elapsed,
            StageOutcome.Completed,
            baselineSolution,
            StageTimings.Legacy(baselinePlan.Elapsed));

        StageResult proofStage = builder.ExecuteProofTightenStage(baselinePlan.MaxStep - 1);
        Assert.True(proofStage.Solution is not null);
        Assert.True(PipelineStageProtocol.IsImprovement(proofStage, baselineStage));

        using var form = new MainForm();
        _ = form.Handle;
        InvokePrivateInstanceVoid(form, "ShowInitialStagePlaceholder", 8, 3, 3, true);
        SetPrivateField(form, "_feasiblePlan", baselinePlan);
        SetPrivateField(form, "_greedyFeasibleStage", baselineStage);
        SetPrivateField(form, "_incumbentStage", baselineStage);

        InvokePrivateInstanceVoid(form, "OnProofTightenStage", proofStage);

        TreeView tree = GetPrivateField<TreeView>(form, "_treeView");
        TreeNode root = tree.Nodes[0];
        Assert.Contains(root.Nodes.Cast<TreeNode>(), node =>
            node.Text.StartsWith(proofStage.Name + ":", StringComparison.Ordinal)
            && !node.Text.Contains("no improvement", StringComparison.Ordinal));

        // Recompute would flip to false because strict improvement over itself is false.
        SetPrivateField(form, "_incumbentStage", proofStage);

        InvokePrivateInstanceVoid(form, "OnProofTightenStage", proofStage);

        root = tree.Nodes[0];
        Assert.Contains(root.Nodes.Cast<TreeNode>(), node =>
            node.Text.StartsWith(proofStage.Name + ":", StringComparison.Ordinal)
            && !node.Text.Contains("no improvement", StringComparison.Ordinal));
    }

    [Fact]
    public void OnProofTightenStage_BufferedDecisions_KeepProofImprovedAndEdgeNoImprovement()
    {
        var builder = new StrategyBuilder(8, 3, 3);
        GreedyPreparationResult prep = PublicPipelineOrchestrator.RunGreedyPreparation(
            builder,
            emitStages: false,
            materialize: true);

        StrategyPlan feasiblePlan = prep.BaseFeasiblePlan
            ?? throw new InvalidOperationException("Expected greedy feasible plan.");
        StageResult feasibleStage = new(
            StageNames.GreedyFeasible,
            feasiblePlan,
            feasiblePlan.Elapsed,
            StageOutcome.Completed,
            prep.BaseFeasibleSolution,
            StageTimings.Legacy(feasiblePlan.Elapsed));

        StageResult proofStage = builder.ExecuteProofTightenStage(feasiblePlan.MaxStep - 1);
        Assert.True(proofStage.HasPlan);
        Assert.True(PipelineStageProtocol.IsImprovement(proofStage, feasibleStage));

        CompactPlanResult edgeResult = builder.BuildEdgeCompactPlanAtBudget(proofStage.MaterializedPlan!.MaxStep);
        StageResult edgeStage = new(
            StageNames.FormatGreedyEdgeCompact(proofStage.MaterializedPlan.MaxStep),
            edgeResult.Plan,
            edgeResult.Plan?.Elapsed ?? edgeResult.Timings.Total,
            edgeResult.Solution is null ? StageOutcome.Incomplete : StageOutcome.Completed,
            edgeResult.Solution,
            edgeResult.Timings);

        Assert.True(edgeStage.Solution is not null);
        Assert.False(PipelineStageProtocol.IsImprovement(edgeStage, proofStage));

        using var form = new MainForm();
        _ = form.Handle;
        InvokePrivateInstanceVoid(form, "ShowInitialStagePlaceholder", 8, 3, 3, true);
        SetPrivateField(form, "_feasiblePlan", null);
        SetPrivateField(form, "_greedyFeasibleStage", feasibleStage);
        SetPrivateField(form, "_incumbentStage", feasibleStage);

        // First ingress happens in search order while feasible tree is still unavailable.
        InvokePrivateInstanceVoid(form, "OnProofTightenStage", proofStage);
        InvokePrivateInstanceVoid(form, "OnProofTightenStage", edgeStage);

        // Simulate incumbent drift before buffered replay; frozen decisions must stay stable.
        SetPrivateField(form, "_incumbentStage", edgeStage);

        InvokePrivateInstanceVoid(form, "DisplayInitialGreedyStageTree", feasibleStage);

        TreeView tree = GetPrivateField<TreeView>(form, "_treeView");
        TreeNode root = tree.Nodes[0];

        Assert.Contains(root.Nodes.Cast<TreeNode>(), node =>
            node.Text.StartsWith(proofStage.Name + ":", StringComparison.Ordinal)
            && !node.Text.Contains("no improvement", StringComparison.Ordinal));

        Assert.Contains(root.Nodes.Cast<TreeNode>(), node =>
            node.Text.StartsWith(edgeStage.Name + ":", StringComparison.Ordinal)
            && node.Text.Contains("no improvement", StringComparison.Ordinal));
    }

    [Fact]
    public void OnProofTightenStage_DeferredImprovement_AdvancesIncumbentImmediately()
    {
        var builder = new StrategyBuilder(8, 3, 3);
        GreedyPreparationResult prep = PublicPipelineOrchestrator.RunGreedyPreparation(
            builder,
            emitStages: false,
            materialize: true);

        StrategyPlan feasiblePlan = prep.BaseFeasiblePlan
            ?? throw new InvalidOperationException("Expected greedy feasible plan.");
        StageResult feasibleStage = new(
            StageNames.GreedyFeasible,
            feasiblePlan,
            feasiblePlan.Elapsed,
            StageOutcome.Completed,
            prep.BaseFeasibleSolution,
            StageTimings.Legacy(feasiblePlan.Elapsed));

        StageResult proofMaterialized = builder.ExecuteProofTightenStage(feasiblePlan.MaxStep - 1);
        Assert.True(proofMaterialized.Solution is not null);
        Assert.True(PipelineStageProtocol.IsImprovement(proofMaterialized, feasibleStage));

        StageResult proofDeferred = new(
            proofMaterialized.Name,
            materializedPlan: null,
            elapsed: proofMaterialized.Elapsed,
            outcome: proofMaterialized.Outcome,
            solution: proofMaterialized.Solution,
            timings: proofMaterialized.Timings);

        using var form = new MainForm();
        _ = form.Handle;
        InvokePrivateInstanceVoid(form, "ShowInitialStagePlaceholder", 8, 3, 3, true);
        SetPrivateField(form, "_feasiblePlan", feasiblePlan);
        SetPrivateField(form, "_greedyFeasibleStage", feasibleStage);
        SetPrivateField(form, "_incumbentStage", feasibleStage);

        InvokePrivateInstanceVoid(form, "OnProofTightenStage", proofDeferred);

        StageResult? incumbent = GetPrivateField<StageResult?>(form, "_incumbentStage");
        Assert.True(incumbent.HasValue);
        Assert.Equal(proofDeferred.Name, incumbent.Value.Name);
    }

    [Fact]
    public void OnProofTightenStage_NoImprovement_DoesNotQueueBufferedMaterialization()
    {
        using var form = new MainForm();
        _ = form.Handle;

        StrategyPlan feasiblePlan = new StrategyBuilder(8, 3, 3).ExecuteStepProofStage();
        SolvedStrategy baselineSolution = CreateDeferredExactStepStage().Solution
            ?? throw new InvalidOperationException("Expected deferred stage solution.");

        StageResult baselineStage = new(
            StageNames.GreedyFeasible,
            feasiblePlan,
            feasiblePlan.Elapsed,
            StageOutcome.Completed,
            baselineSolution,
            StageTimings.Legacy(feasiblePlan.Elapsed));

        InvokePrivateInstanceVoid(form, "ShowInitialStagePlaceholder", 8, 3, 3, true);
        SetPrivateField(form, "_feasiblePlan", null);
        SetPrivateField(form, "_greedyFeasibleStage", baselineStage);
        SetPrivateField(form, "_incumbentStage", baselineStage);
        SetPrivateField(form, "_frozenGreedyStageComparisonBaseline", baselineStage);

        StageResult nonImprovingStage = new(
            StageNames.FormatGreedyEdgeCompact(feasiblePlan.MaxStep),
            materializedPlan: null,
            elapsed: TimeSpan.FromMilliseconds(5),
            outcome: StageOutcome.Completed,
            solution: baselineSolution,
            timings: StageTimings.Legacy(TimeSpan.FromMilliseconds(5)));

        // Ensure the stage is not an improvement against the baseline.
        Assert.False(PipelineStageProtocol.IsImprovement(nonImprovingStage, baselineStage));

        InvokePrivateInstanceVoid(form, "OnProofTightenStage", nonImprovingStage);

        HashSet<string> inFlight = GetPrivateField<HashSet<string>>(form, "_materializingGreedyEdgeStageNames");
        List<Task> bufferedTasks = GetPrivateField<List<Task>>(form, "_greedyEdgeTreeMaterializationTasks");
        Assert.DoesNotContain(nonImprovingStage.Name, inFlight);
        Assert.Empty(bufferedTasks);

        InvokePrivateInstanceVoid(form, "DisplayInitialGreedyStageTree", baselineStage);
        TreeView tree = GetPrivateField<TreeView>(form, "_treeView");
        TreeNode root = tree.Nodes[0];
        Assert.Contains(root.Nodes.Cast<TreeNode>(), node =>
            node.Text.StartsWith(nonImprovingStage.Name + ":", StringComparison.Ordinal)
            && node.Text.Contains("no improvement", StringComparison.Ordinal)
            && !node.Text.Contains("tree skipped", StringComparison.Ordinal));
        TreeNode renderedNode = root.Nodes.Cast<TreeNode>().Single(node =>
            node.Text.StartsWith(nonImprovingStage.Name + ":", StringComparison.Ordinal));
        Assert.Contains(": [", renderedNode.Text, StringComparison.Ordinal);
        Assert.True(renderedNode.NodeFont is null || renderedNode.NodeFont.Style == FontStyle.Regular);
    }

    [Fact]
    public void OnProofTightenStage_BuffersUntilInitialGreedyStageMaterialized()
    {
        StrategyPlan feasiblePlan = new StrategyBuilder(8, 3, 3).ExecuteStepProofStage();
        SolvedStrategy feasibleSolution = CreateDeferredExactStepStage().Solution!;

        using var form = new MainForm();
        _ = form.Handle;
        InvokePrivateInstanceVoid(form, "ShowInitialStagePlaceholder", 8, 3, 3, true);

        SetPrivateField(form, "_feasiblePlan", null);
        SetPrivateField(form, "_greedyFeasibleStage", new StageResult(
            StageNames.GreedyFeasible,
            materializedPlan: null,
            TimeSpan.FromMilliseconds(1),
            StageOutcome.Completed,
            feasibleSolution,
            StageTimings.Legacy(TimeSpan.FromMilliseconds(1))));

        var incoming = new StageResult(
            StageNames.FormatProofTighten(feasiblePlan.MaxStep - 1),
            materializedPlan: null,
            TimeSpan.FromMilliseconds(2),
            StageOutcome.Tightened,
            feasibleSolution,
            StageTimings.Legacy(TimeSpan.FromMilliseconds(2)));

        InvokePrivateInstanceVoid(form, "OnStageSearchStarted", incoming.Name);
        TreeView tree = GetPrivateField<TreeView>(form, "_treeView");
        TreeNode root = tree.Nodes[0];
        Assert.Contains(root.Nodes.Cast<TreeNode>(), node =>
            node.Text.StartsWith(incoming.Name, StringComparison.Ordinal)
            && (node.Text.Contains("searching", StringComparison.Ordinal)
                || node.Text.Contains("searched, building tree", StringComparison.Ordinal)
                || node.Text.Contains("tree ready", StringComparison.Ordinal)));

        InvokePrivateInstanceVoid(form, "OnProofTightenStage", incoming);

        List<StageResult> buffered = GetPrivateField<List<StageResult>>(form, "_readyGreedyEdgeStages");
        List<StageResult> landed = GetPrivateField<List<StageResult>>(form, "_proofTightenStages");
        List<Task> materializationTasks = GetPrivateField<List<Task>>(form, "_greedyEdgeTreeMaterializationTasks");
        Assert.Single(buffered);
        Assert.Empty(landed);
        Assert.Single(materializationTasks);

        Assert.Contains(root.Nodes.Cast<TreeNode>(), node =>
            node.Text.StartsWith(incoming.Name, StringComparison.Ordinal)
            && (node.Text.Contains("searched, building tree", StringComparison.Ordinal)
                || node.Text.Contains("tree ready", StringComparison.Ordinal)));

        PumpUiUntilTaskCompletes(materializationTasks[0], timeoutMs: 10_000);
        Assert.Contains(root.Nodes.Cast<TreeNode>(), node =>
            node.Text.StartsWith(incoming.Name, StringComparison.Ordinal)
            && node.Text.Contains("tree ready", StringComparison.Ordinal));

        InvokePrivateInstanceVoid(form, "DisplayInitialGreedyStageTree", new StageResult(
            StageNames.GreedyFeasible,
            feasiblePlan,
            TimeSpan.FromMilliseconds(1),
            StageOutcome.Completed,
            feasibleSolution,
            StageTimings.Legacy(TimeSpan.FromMilliseconds(1))));

        PumpUiUntil(
            () => landed.Count == 1,
            timeoutMs: 2000);

        root = tree.Nodes[0];

        Assert.Empty(buffered);
        Assert.Single(landed);
        Assert.Equal(incoming.Name, landed[0].Name);
        PumpUiUntil(
            () => root.Nodes.Cast<TreeNode>().Any(node =>
                node.Text.StartsWith(incoming.Name, StringComparison.Ordinal)
                && !node.Text.Contains("tree ready", StringComparison.Ordinal)),
            timeoutMs: 2000);
        Assert.DoesNotContain(root.Nodes.Cast<TreeNode>(), node =>
            node.Text.StartsWith(incoming.Name, StringComparison.Ordinal)
            && node.Text.Contains("tree ready", StringComparison.Ordinal));
    }

    [Fact]
    public void DisplayInitialGreedyStageTree_NullPlan_ReturnsWithoutThrowing()
    {
        using var form = new MainForm();
        _ = form.Handle;

        SetPrivateField(form, "_feasiblePlan", null);

        var stage = new StageResult(
            StageNames.GreedyFeasible,
            materializedPlan: null,
            TimeSpan.FromMilliseconds(1),
            StageOutcome.Completed,
            CreateDeferredExactStepStage().Solution,
            StageTimings.Legacy(TimeSpan.FromMilliseconds(1)));

        InvokePrivateInstanceVoid(form, "DisplayInitialGreedyStageTree", stage);

        Assert.Null(GetPrivateFieldValue(form, "_feasiblePlan"));
    }

    [Fact]
    public void PresentationStageCache_EvictsOldestEntryWhenCapacityExceeded()
    {
        using var form = new MainForm();
        InvokePrivateInstanceVoid(form, "ResetPresentationInfrastructure");

        StageResult first = CreateDeferredExactStepStage();
        InvokePrivateInstanceVoid(form, "CachePresentationStageResult", first, first);

        for (int i = 0; i < 8; i++)
        {
            StageResult stage = CreateDeferredExactStepStage();
            InvokePrivateInstanceVoid(form, "CachePresentationStageResult", stage, stage);
        }

        bool firstStillCached = InvokePrivateInstance<bool>(form, "IsPresentationStageCached", first);
        Assert.False(firstStillCached);
    }

    [Fact]
    public void PresentationStageCache_RecentAccessProtectsEntryFromEviction()
    {
        using var form = new MainForm();
        InvokePrivateInstanceVoid(form, "ResetPresentationInfrastructure");

        StageResult first = CreateDeferredExactStepStage();
        StageResult second = CreateDeferredExactStepStage();
        InvokePrivateInstanceVoid(form, "CachePresentationStageResult", first, first);
        InvokePrivateInstanceVoid(form, "CachePresentationStageResult", second, second);

        for (int i = 0; i < 6; i++)
        {
            StageResult stage = CreateDeferredExactStepStage();
            InvokePrivateInstanceVoid(form, "CachePresentationStageResult", stage, stage);
        }

        bool firstCachedBeforeTouch = InvokePrivateInstance<bool>(form, "IsPresentationStageCached", first);
        Assert.True(firstCachedBeforeTouch);

        StageResult ninth = CreateDeferredExactStepStage();
        InvokePrivateInstanceVoid(form, "CachePresentationStageResult", ninth, ninth);

        bool firstStillCached = InvokePrivateInstance<bool>(form, "IsPresentationStageCached", first);
        bool secondStillCached = InvokePrivateInstance<bool>(form, "IsPresentationStageCached", second);
        Assert.True(firstStillCached);
        Assert.False(secondStillCached);
    }

    [Fact]
    public void ResetPresentationInfrastructure_ClearsPresentationStageCache()
    {
        using var form = new MainForm();
        InvokePrivateInstanceVoid(form, "ResetPresentationInfrastructure");

        StageResult stage = CreateDeferredExactStepStage();
        InvokePrivateInstanceVoid(form, "CachePresentationStageResult", stage, stage);
        bool cachedBeforeReset = InvokePrivateInstance<bool>(form, "IsPresentationStageCached", stage);
        Assert.True(cachedBeforeReset);

        InvokePrivateInstanceVoid(form, "ResetPresentationInfrastructure");
        bool cachedAfterReset = InvokePrivateInstance<bool>(form, "IsPresentationStageCached", stage);
        Assert.False(cachedAfterReset);
    }

    [Fact]
    public void MarkGreedyIncumbentProvenOptimal_IncumbentWithoutPlan_DoesNotThrow()
    {
        using var form = new MainForm();
        _ = form.Handle;

        StrategyPlan feasiblePlan = new StrategyBuilder(8, 3, 3).ExecuteGreedyFeasibleStage();
        StageResult deferredStage = CreateDeferredExactStepStage();
        var incumbentWithoutPlan = new StageResult(
            StageNames.FormatProofTighten(feasiblePlan.MaxStep - 1),
            materializedPlan: null,
            elapsed: TimeSpan.FromMilliseconds(1),
            outcome: StageOutcome.Tightened,
            solution: deferredStage.Solution,
            timings: StageTimings.Legacy(TimeSpan.FromMilliseconds(1)));

        SetPrivateField(form, "_feasiblePlan", feasiblePlan);
        SetPrivateField(form, "_incumbentStage", incumbentWithoutPlan);

        InvokePrivateInstanceVoid(form, "MarkGreedyIncumbentProvenOptimal");

        StageResult? updatedIncumbent = GetPrivateField<StageResult?>(form, "_incumbentStage");
        Assert.True(updatedIncumbent.HasValue);
        Assert.NotNull(updatedIncumbent.Value.Solution);
    }

    [Fact]
    public void StopStrategy_WhenSolverRunning_CancelsSolverOnly()
    {
        using var form = new MainForm();
        _ = form.Handle;

        using var runCts = new CancellationTokenSource();
        using var presentationCts = new CancellationTokenSource();
        SetPrivateField(form, "_runCancellationSource", runCts);
        SetPrivateField(form, "_presentationCancellationSource", presentationCts);
        SetPrivateField(form, "_solverWorkStopped", false);

        InvokePrivateInstanceVoid(form, "StopStrategy");

        Assert.True(runCts.IsCancellationRequested);
        Assert.False(presentationCts.IsCancellationRequested);
    }

    [Fact]
    public void StopStrategy_AfterSolverStopped_CancelsPresentation()
    {
        using var form = new MainForm();
        _ = form.Handle;

        using var runCts = new CancellationTokenSource();
        using var presentationCts = new CancellationTokenSource();
        var presentationTask = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        SetPrivateField(form, "_runCancellationSource", runCts);
        SetPrivateField(form, "_presentationCancellationSource", presentationCts);
        SetPrivateField(form, "_activePresentationTask", presentationTask.Task);
        SetPrivateField(form, "_solverWorkStopped", true);

        InvokePrivateInstanceVoid(form, "StopStrategy");

        Assert.False(runCts.IsCancellationRequested);
        Assert.True(presentationCts.IsCancellationRequested);

        presentationTask.TrySetResult(null);
    }

    [Fact]
    public void StopStrategy_AfterSolverStopped_NoPresentation_RepeatedStopsDoNotThrow()
    {
        using var form = new MainForm();
        _ = form.Handle;

        using var runCts = new CancellationTokenSource();
        SetPrivateField(form, "_runCancellationSource", runCts);
        SetPrivateField(form, "_solverWorkStopped", true);
        SetPrivateField(form, "_activePresentationTask", null);
        SetPrivateField(form, "_stopEscalationSource", new CancellationTokenSource());

        InvokePrivateInstanceVoid(form, "StopStrategy");
        InvokePrivateInstanceVoid(form, "StopStrategy");

        Assert.Null(GetPrivateFieldValue(form, "_stopEscalationSource"));
    }

    [Fact]
    public void InitializeRunUi_GreedyMode_TracksInitialStageOnlyUntilCallbacksArrive()
    {
        using var form = new MainForm();
        _ = form.Handle;

        var request = CreateRunRequest(
            n: 30,
            m: 10,
            k: 15,
            feasibleMode: true,
            builder: new StrategyBuilder(30, 10, 15),
            cancellationToken: CancellationToken.None);

        InvokePrivateInstanceVoid(form, "InitializeRunUi", request);

        var stageOrder = GetPrivateField<Dictionary<string, int>>(form, "_stageDisplayOrder");
        Assert.True(stageOrder.ContainsKey(StageNames.GreedyFeasible));
        Assert.False(stageOrder.ContainsKey(StageNames.GreedyTighten));
    }

    [Fact]
    public void GreedyTightenSummaryNode_StaysBeforeProofPlaceholder()
    {
        using var form = new MainForm();
        _ = form.Handle;

        var request = CreateRunRequest(
            n: 30,
            m: 10,
            k: 15,
            feasibleMode: true,
            builder: new StrategyBuilder(30, 10, 15),
            cancellationToken: CancellationToken.None);
        InvokePrivateInstanceVoid(form, "InitializeRunUi", request);

        var skippedTighten = new StageResult(
            StageNames.GreedyTighten,
            materializedPlan: null,
            elapsed: TimeSpan.FromMilliseconds(5600),
            outcome: StageOutcome.Skipped,
            solution: null,
            timings: StageTimings.Legacy(TimeSpan.FromMilliseconds(5600)));
        SetPrivateField(form, "_greedyTightenStage", skippedTighten);
        SetPrivateField(form, "_feasiblePlan", new StrategyBuilder(8, 3, 3).ExecuteStepProofStage());
        InvokePrivateInstanceVoid(form, "OnProofTightenStage", skippedTighten);
        InvokePrivateInstanceVoid(form, "OnStageSearchStarted", StageNames.FormatProofTighten(6));

        TreeView tree = GetPrivateField<TreeView>(form, "_treeView");
        TreeNode root = tree.Nodes[0];

        int tightenIndex = -1;
        int proofIndex = -1;
        for (int i = 0; i < root.Nodes.Count; i++)
        {
            string text = root.Nodes[i].Text;
            if (text.StartsWith(StageNames.GreedyTighten, StringComparison.Ordinal))
                tightenIndex = i;
            if (text.StartsWith(StageNames.ProofTightenPrefix, StringComparison.Ordinal))
                proofIndex = i;
        }

        Assert.True(tightenIndex >= 0, "Expected greedy-tighten stage node to be present.");
        Assert.True(proofIndex >= 0, "Expected proof-tighten placeholder to be present.");
        Assert.True(tightenIndex < proofIndex, "Expected greedy-tighten to remain before proof-tighten.");
        Assert.DoesNotContain(root.Nodes.Cast<TreeNode>(), node =>
            string.Equals(node.Text, StageNames.GreedyTighten + " [searching]", StringComparison.Ordinal));
    }

    [Fact]
    public void InitialTrees_UseActualCurrentStageForPendingCompactSlot()
    {
        using var form = new MainForm();
        _ = form.Handle;

        var request = CreateRunRequest(
            n: 25,
            m: 3,
            k: 3,
            feasibleMode: true,
            builder: new StrategyBuilder(25, 3, 3),
            cancellationToken: CancellationToken.None);
        InvokePrivateInstanceVoid(form, "InitializeRunUi", request);
        InvokePrivateInstanceVoid(form, "OnStageSearchStarted", StageNames.GreedyTighten);

        StrategyPlan feasiblePlan = new StrategyBuilder(8, 3, 3).ExecuteStepProofStage();
        SolvedStrategy solution = CreateDeferredExactStepStage().Solution
            ?? throw new InvalidOperationException("Expected deferred exact step solution.");
        InvokePrivateInstanceVoid(form, "DisplayInitialGreedyStageTree", new StageResult(
            StageNames.GreedyFeasible,
            feasiblePlan,
            feasiblePlan.Elapsed,
            StageOutcome.Completed,
            solution,
            StageTimings.Legacy(feasiblePlan.Elapsed)));

        TreeView tree = GetPrivateField<TreeView>(form, "_treeView");
        TreeNode root = tree.Nodes[0];
        Assert.Contains(root.Nodes.Cast<TreeNode>(), node =>
            node.Text == StageNames.GreedyTighten + " [searching]");
        Assert.DoesNotContain(root.Nodes.Cast<TreeNode>(), node =>
            node.Text.StartsWith(StageNames.ProofTightenPrefix, StringComparison.Ordinal));

        TreeView overview = GetPrivateField<TreeView>(form, "_overviewTree");
        Assert.Contains(overview.Nodes.Cast<TreeNode>(), node =>
            node.Text == StageNames.GreedyTighten + " [searching]");
        Assert.DoesNotContain(overview.Nodes.Cast<TreeNode>(), node =>
            node.Text.StartsWith(StageNames.ProofTightenPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void InitialTrees_PausedWithNextStageMetadata_ShowWaitingPlaceholder()
    {
        using var form = new MainForm();
        _ = form.Handle;
        SetPrivateField(form, "_pauseEachStageForRun", true);
        SetPrivateField(form, "_nextStageName", StageNames.GreedyTighten);

        StrategyPlan feasiblePlan = new StrategyBuilder(8, 3, 3).ExecuteStepProofStage();
        StageResult stage = new(
            StageNames.GreedyFeasible,
            feasiblePlan,
            feasiblePlan.Elapsed,
            StageOutcome.Completed,
            CreateDeferredExactStepStage().Solution,
            StageTimings.Legacy(feasiblePlan.Elapsed));
        InvokePrivateInstanceVoid(form, "BeginStagePause", stage);
        InvokePrivateInstanceVoid(form, "DisplayInitialGreedyStageTree", stage);

        string expected = StageNames.GreedyTighten + " [waiting to continue]";
        TreeView tree = GetPrivateField<TreeView>(form, "_treeView");
        Assert.Contains(tree.Nodes[0].Nodes.Cast<TreeNode>(), node => node.Text == expected);

        TreeView overview = GetPrivateField<TreeView>(form, "_overviewTree");
        Assert.Contains(overview.Nodes.Cast<TreeNode>(), node => node.Text == expected);
    }

    [Fact]
    public void InitialTrees_PausedWithoutGreedyTightenMetadata_DoNotShowGreedyTightenPlaceholder()
    {
        using var form = new MainForm();
        _ = form.Handle;
        SetPrivateField(form, "_pauseEachStageForRun", true);
        SetPrivateField(form, "_nextStageName", StageNames.FormatProofTighten(4));

        StrategyPlan feasiblePlan = new StrategyBuilder(8, 3, 3).ExecuteStepProofStage();
        StageResult stage = new(
            StageNames.GreedyFeasible,
            feasiblePlan,
            feasiblePlan.Elapsed,
            StageOutcome.Completed,
            CreateDeferredExactStepStage().Solution,
            StageTimings.Legacy(feasiblePlan.Elapsed));
        InvokePrivateInstanceVoid(form, "BeginStagePause", stage);
        InvokePrivateInstanceVoid(form, "DisplayInitialGreedyStageTree", stage);

        TreeView tree = GetPrivateField<TreeView>(form, "_treeView");
        Assert.DoesNotContain(tree.Nodes[0].Nodes.Cast<TreeNode>(), node =>
            node.Text.StartsWith(StageNames.GreedyTighten, StringComparison.Ordinal));
    }

    [Fact]
    public void FinalStageWithoutNextStage_DoesNotPauseForContinue()
    {
        using var form = new MainForm();
        _ = form.Handle;
        SetPrivateField(form, "_pauseEachStageForRun", true);
        SetPrivateField(form, "_nextStageName", null);

        var stage = new StageResult(
            StageNames.FormatGreedyEdgeCompact(4),
            materializedPlan: null,
            elapsed: TimeSpan.FromMilliseconds(10),
            outcome: StageOutcome.Completed,
            solution: null,
            timings: StageTimings.Legacy(TimeSpan.FromMilliseconds(10)));

        InvokePrivateInstanceVoid(form, "BeginStagePause", stage);

        Assert.Null(GetPrivateField<TaskCompletionSource<object?>?>(form, "_stagePauseCompletion"));
        Assert.Null(GetPrivateField<string?>(form, "_pausedStageName"));
    }

    [Fact]
    public void GreedyTighten_WithMaterializedPlan_RendersTreeInsteadOfSearchOnlySummary()
    {
        using var form = new MainForm();
        _ = form.Handle;

        StrategyPlan feasiblePlan = new StrategyBuilder(8, 3, 3).ExecuteStepProofStage();
        StrategyPlan improvedPlan = new StrategyBuilder(9, 3, 3).ExecuteStepProofStage();
        SolvedStrategy solution = CreateDeferredExactStepStage().Solution
            ?? throw new InvalidOperationException("Expected deferred stage solution.");

        InvokePrivateInstanceVoid(form, "ShowInitialStagePlaceholder", 8, 3, 3, true);
        SetPrivateField(form, "_feasiblePlan", feasiblePlan);
        SetPrivateField(form, "_greedyFeasibleStage", new StageResult(
            StageNames.GreedyFeasible,
            feasiblePlan,
            feasiblePlan.Elapsed,
            StageOutcome.Completed,
            solution,
            StageTimings.Legacy(feasiblePlan.Elapsed)));
        SetPrivateField(form, "_incumbentStage", new StageResult(
            StageNames.GreedyFeasible,
            feasiblePlan,
            feasiblePlan.Elapsed,
            StageOutcome.Completed,
            solution,
            StageTimings.Legacy(feasiblePlan.Elapsed)));

        var stage = new StageResult(
            StageNames.GreedyTighten,
            improvedPlan,
            improvedPlan.Elapsed,
            StageOutcome.Completed,
            solution,
            StageTimings.Legacy(improvedPlan.Elapsed));

        InvokePrivateInstanceVoid(form, "OnProofTightenStage", stage);

        TreeView tree = GetPrivateField<TreeView>(form, "_treeView");
        TreeNode root = tree.Nodes[0];
        Assert.Contains(root.Nodes.Cast<TreeNode>(), node =>
            node.Text.StartsWith(StageNames.GreedyTighten + ":", StringComparison.Ordinal));
        Assert.DoesNotContain(root.Nodes.Cast<TreeNode>(), node =>
            node.Text.Contains("search-only", StringComparison.Ordinal));
    }

    [Fact]
    public void OnProofTightenStage_DoesNotInventNextStageSearchingPlaceholder()
    {
        using var form = new MainForm();
        _ = form.Handle;

        StrategyPlan feasiblePlan = new StrategyBuilder(8, 3, 3).ExecuteStepProofStage();
        SolvedStrategy solution = CreateDeferredExactStepStage().Solution
            ?? throw new InvalidOperationException("Expected deferred stage solution.");

        TreeView tree = GetPrivateField<TreeView>(form, "_treeView");
        var root = new TreeNode("root");
        root.Nodes.Add(new TreeNode("stable-node"));
        tree.Nodes.Add(root);

        TreeView overview = GetPrivateField<TreeView>(form, "_overviewTree");
        overview.Nodes.Add(new TreeNode("stable-node"));

        SetPrivateField(form, "_feasiblePlan", feasiblePlan);
        SetPrivateField(form, "_greedyFeasibleStage", new StageResult(
            StageNames.GreedyFeasible,
            feasiblePlan,
            feasiblePlan.Elapsed,
            StageOutcome.Completed,
            solution,
            StageTimings.Legacy(feasiblePlan.Elapsed)));
        SetPrivateField(form, "_incumbentStage", new StageResult(
            StageNames.GreedyFeasible,
            feasiblePlan,
            feasiblePlan.Elapsed,
            StageOutcome.Completed,
            solution,
            StageTimings.Legacy(feasiblePlan.Elapsed)));

        var stage = new StageResult(
            StageNames.GreedyTighten,
            materializedPlan: null,
            elapsed: TimeSpan.FromMilliseconds(10),
            outcome: StageOutcome.Skipped,
            solution: null,
            timings: StageTimings.Legacy(TimeSpan.FromMilliseconds(10)),
            presentationMode: StagePresentationMode.SearchOnlySummary);

        InvokePrivateInstanceVoid(form, "OnProofTightenStage", stage);

        Assert.DoesNotContain(tree.Nodes[0].Nodes.Cast<TreeNode>(), node =>
            node.Text.EndsWith(" [searching]", StringComparison.Ordinal));
        Assert.DoesNotContain(overview.Nodes.Cast<TreeNode>(), node =>
            node.Text.EndsWith(" [searching]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GreedyPreparationStages_AreHandledByUnifiedStageCallback()
    {
        using var form = new MainForm();
        _ = form.Handle;

        var request = CreateRunRequest(
            n: 30,
            m: 10,
            k: 15,
            feasibleMode: true,
            builder: new StrategyBuilder(30, 10, 15),
            cancellationToken: CancellationToken.None);
        InvokePrivateInstanceVoid(form, "InitializeRunUi", request);

        SolvedStrategy solution = CreateDeferredExactStepStage().Solution
            ?? throw new InvalidOperationException("Expected deferred stage solution.");

        var feasibleStage = new StageResult(
            StageNames.GreedyFeasible,
            materializedPlan: null,
            elapsed: TimeSpan.FromMilliseconds(300),
            outcome: StageOutcome.Completed,
            solution,
            timings: StageTimings.Legacy(TimeSpan.FromMilliseconds(300)));
        InvokePrivateInstanceVoid(form, "OnGreedyPreparationStage", feasibleStage);

        StageResult? recordedFeasible = GetPrivateField<StageResult?>(form, "_greedyFeasibleStage");
        StageResult? incumbent = GetPrivateField<StageResult?>(form, "_incumbentStage");
        Assert.True(recordedFeasible.HasValue);
        Assert.Equal(StageNames.GreedyFeasible, recordedFeasible.Value.Name);
        Assert.True(incumbent.HasValue);
        Assert.Equal(StageNames.GreedyFeasible, incumbent.Value.Name);

        var tightenStage = new StageResult(
            StageNames.GreedyTighten,
            materializedPlan: null,
            elapsed: TimeSpan.FromMilliseconds(5600),
            outcome: StageOutcome.Skipped,
            solution: null,
            timings: StageTimings.Legacy(TimeSpan.FromMilliseconds(5600)));
        InvokePrivateInstanceVoid(form, "OnGreedyPreparationStage", tightenStage);

        StageResult? recordedTighten = GetPrivateField<StageResult?>(form, "_greedyTightenStage");
        Assert.True(recordedTighten.HasValue);
        Assert.Equal(StageNames.GreedyTighten, recordedTighten.Value.Name);

        List<StageResult> buffered = GetPrivateField<List<StageResult>>(form, "_readyGreedyEdgeStages");
        Assert.Contains(buffered, stage => string.Equals(stage.Name, StageNames.GreedyTighten, StringComparison.Ordinal));

        // This test validates callback routing/state updates, not background materialization throughput.
        // Reset presentation infra to cancel in-flight work, then verify drain is non-blocking.
        InvokePrivateInstanceVoid(form, "ResetPresentationInfrastructure");
        Task drain = InvokePrivateInstance<Task>(form, "DrainPresentationTasksAsync");
        PumpUiUntilTaskCompletes(drain, timeoutMs: 1000);
        await drain;
    }

    [Fact]
    public void GreedyPreparation_ImprovingTightenStage_IsDisplayedAsImprovement()
    {
        var builder = new StrategyBuilder(25, 8, 3)
        {
            GreedyTightenEnabledForTesting = true,
        };
        GreedyPreparationResult preparation = PublicPipelineOrchestrator.RunGreedyPreparation(
            builder,
            emitStages: false,
            materialize: true);

        StrategyPlan feasiblePlan = preparation.BaseFeasiblePlan
            ?? throw new InvalidOperationException("Expected greedy feasible plan.");
        StrategyPlan tightenPlan = preparation.GreedyTightenPlan
            ?? throw new InvalidOperationException("Expected improving greedy-tighten plan.");
        SolvedStrategy tightenSolution = preparation.GreedyTightenSolution
            ?? throw new InvalidOperationException("Expected greedy-tighten solution.");

        Assert.True(tightenPlan.MaxStep < feasiblePlan.MaxStep);

        using var form = new MainForm();
        _ = form.Handle;
        InvokePrivateInstanceVoid(form, "ShowInitialStagePlaceholder", 25, 8, 3, true);
        SetPrivateField(form, "_feasibleMode", true);

        var feasibleStage = new StageResult(
            StageNames.GreedyFeasible,
            feasiblePlan,
            feasiblePlan.Elapsed,
            StageOutcome.Completed,
            preparation.BaseFeasibleSolution,
            preparation.GreedyFeasibleTimings);
        SetPrivateField(form, "_feasiblePlan", feasiblePlan);
        SetPrivateField(form, "_greedyFeasibleStage", feasibleStage);
        SetPrivateField(form, "_incumbentStage", feasibleStage);

        var tightenStage = new StageResult(
            StageNames.GreedyTighten,
            tightenPlan,
            tightenPlan.Elapsed,
            StageOutcome.Completed,
            tightenSolution,
            preparation.GreedyTightenTimings,
            StagePresentationMode.Auto);
        InvokePrivateInstanceVoid(form, "OnGreedyPreparationStage", tightenStage);

        TreeView tree = GetPrivateField<TreeView>(form, "_treeView");
        Assert.Contains(tree.Nodes[0].Nodes.Cast<TreeNode>(), node =>
            node.Text.StartsWith(StageNames.GreedyTighten + ":", StringComparison.Ordinal)
            && !node.Text.Contains("no improvement", StringComparison.Ordinal));
    }

    [Fact]
    public void DisplayInitialGreedyStageTree_ReplaysBufferedSearchOnlyGreedyTighten()
    {
        using var form = new MainForm();
        _ = form.Handle;

        StrategyPlan feasiblePlan = new StrategyBuilder(8, 3, 3).ExecuteStepProofStage();
        SolvedStrategy solution = CreateDeferredExactStepStage().Solution
            ?? throw new InvalidOperationException("Expected deferred stage solution.");

        InvokePrivateInstanceVoid(form, "ShowInitialStagePlaceholder", 8, 3, 3, true);
        SetPrivateField(form, "_feasiblePlan", null);

        var tightenStage = new StageResult(
            StageNames.GreedyTighten,
            materializedPlan: null,
            elapsed: TimeSpan.FromMilliseconds(5600),
            outcome: StageOutcome.Completed,
            solution,
            timings: StageTimings.Legacy(TimeSpan.FromMilliseconds(5600)),
            presentationMode: StagePresentationMode.SearchOnlySummary);

        InvokePrivateInstanceVoid(form, "OnProofTightenStage", tightenStage);

        List<StageResult> buffered = GetPrivateField<List<StageResult>>(form, "_readyGreedyEdgeStages");
        Assert.Contains(buffered, stage => string.Equals(stage.Name, StageNames.GreedyTighten, StringComparison.Ordinal));

        InvokePrivateInstanceVoid(form, "DisplayInitialGreedyStageTree", new StageResult(
            StageNames.GreedyFeasible,
            feasiblePlan,
            feasiblePlan.Elapsed,
            StageOutcome.Completed,
            solution,
            StageTimings.Legacy(feasiblePlan.Elapsed)));

        TreeView tree = GetPrivateField<TreeView>(form, "_treeView");
        TreeNode root = tree.Nodes[0];
        Assert.Contains(root.Nodes.Cast<TreeNode>(), node =>
            node.Text.StartsWith(StageNames.GreedyTighten, StringComparison.Ordinal));
    }

    [Fact]
    public void ProvenInfeasibleSearchOnlyStage_ClosesRootSqueezeBeforeReturning()
    {
        using var form = new MainForm();
        _ = form.Handle;

        StrategyPlan feasiblePlan = new StrategyBuilder(8, 3, 3).ExecuteStepProofStage();
        SolvedStrategy solution = CreateDeferredExactStepStage().Solution
            ?? throw new InvalidOperationException("Expected deferred stage solution.");

        TreeView tree = GetPrivateField<TreeView>(form, "_treeView");
        tree.Nodes.Add(new TreeNode("root"));

        SetPrivateField(form, "_feasiblePlan", feasiblePlan);
        SetPrivateField(form, "_incumbentStage", new StageResult(
            StageNames.GreedyFeasible,
            feasiblePlan,
            feasiblePlan.Elapsed,
            StageOutcome.Completed,
            solution,
            StageTimings.Legacy(feasiblePlan.Elapsed)));

        var stage = new StageResult(
            "proof-tighten<=4",
            materializedPlan: null,
            elapsed: TimeSpan.FromMilliseconds(10),
            outcome: StageOutcome.ProvenInfeasible,
            solution: null,
            timings: StageTimings.Legacy(TimeSpan.FromMilliseconds(10)),
            presentationMode: StagePresentationMode.SearchOnlySummary);

        InvokePrivateInstanceVoid(form, "OnProofTightenStage", stage);

        Assert.Contains("proven optimal", tree.Nodes[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProvenInfeasibleSearchOnlyStage_WhenPaused_ShowsEdgeCompactWaitingPlaceholder()
    {
        using var form = new MainForm();
        _ = form.Handle;

        StrategyPlan feasiblePlan = new StrategyBuilder(8, 3, 3).ExecuteStepProofStage();
        SolvedStrategy solution = CreateDeferredExactStepStage().Solution
            ?? throw new InvalidOperationException("Expected deferred stage solution.");
        var incumbent = new StageResult(
            StageNames.GreedyFeasible,
            feasiblePlan,
            feasiblePlan.Elapsed,
            StageOutcome.Completed,
            solution,
            StageTimings.Legacy(feasiblePlan.Elapsed));
        var stage = new StageResult(
            StageNames.FormatProofTighten(feasiblePlan.MaxStep - 1),
            materializedPlan: null,
            elapsed: TimeSpan.FromMilliseconds(10),
            outcome: StageOutcome.ProvenInfeasible,
            solution: null,
            timings: StageTimings.Legacy(TimeSpan.FromMilliseconds(10)),
            presentationMode: StagePresentationMode.SearchOnlySummary);

        InvokePrivateInstanceVoid(form, "ShowInitialStagePlaceholder", 8, 3, 3, true);
        SetPrivateField(form, "_feasibleMode", true);
        SetPrivateField(form, "_pauseEachStageForRun", true);
        SetPrivateField(form, "_feasiblePlan", feasiblePlan);
        SetPrivateField(form, "_incumbentStage", incumbent);
        SetPrivateField(form, "_nextStageName", StageNames.FormatGreedyEdgeCompact(feasiblePlan.MaxStep));
        InvokePrivateInstanceVoid(form, "BeginStagePause", stage);
        InvokePrivateInstanceVoid(form, "OnProofTightenStage", stage);

        string expected = StageNames.FormatGreedyEdgeCompact(feasiblePlan.MaxStep) + " [waiting to continue]";
        TreeView tree = GetPrivateField<TreeView>(form, "_treeView");
        Assert.Contains(tree.Nodes[0].Nodes.Cast<TreeNode>(), node => node.Text == expected);

        TreeView overview = GetPrivateField<TreeView>(form, "_overviewTree");
        Assert.Contains(overview.Nodes.Cast<TreeNode>(), node => node.Text == expected);
    }

    private static StageResult CreateDeferredExactStepStage()
    {
        StrategyBuilder builder = new(8, 3, 3);
        StageResult? first = null;
        PublicPipelineOrchestrator.RunExactPipelineDeferred(
            builder,
            stage =>
            {
                if (first is null)
                    first = stage;
            });

        return first ?? throw new InvalidOperationException("Deferred exact pipeline did not emit a stage.");
    }

    private static object CreateRunRequest(
        int n,
        int m,
        int k,
        bool feasibleMode,
        StrategyBuilder builder,
        CancellationToken cancellationToken)
    {
        Type requestType = typeof(MainForm).GetNestedType("RunRequest", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing nested type MainForm.RunRequest");
        return Activator.CreateInstance(
            requestType,
            n,
            m,
            k,
            feasibleMode,
            builder,
            cancellationToken)
            ?? throw new InvalidOperationException("Failed to construct MainForm.RunRequest");
    }

    private static T InvokePrivateStatic<T>(Type type, string methodName, params object?[] args)
    {
        MethodInfo method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Missing private static method {type.Name}.{methodName}");
        object? value = method.Invoke(null, args);
        return value is T typed
            ? typed
            : throw new InvalidOperationException($"{type.Name}.{methodName} returned unexpected value");
    }

    private static object CreateLazyNodeDetails(Func<string> factory)
    {
        Type lazyType = typeof(MainForm).GetNestedType("LazyNodeDetails", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing nested type MainForm.LazyNodeDetails");
        ConstructorInfo constructor = lazyType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            new[] { typeof(Func<string>) },
            modifiers: null)
            ?? throw new InvalidOperationException("Missing LazyNodeDetails(Func<string>) constructor");
        return constructor.Invoke(new object[] { factory });
    }

    private static void PumpUiUntil(Func<bool> condition, int timeoutMs)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            Application.DoEvents();
            if (condition())
                return;
            Thread.Sleep(10);
        }

        Application.DoEvents();
    }

    private static void PumpUiUntilTaskCompletes(Task task, int timeoutMs)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!task.IsCompleted && stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }

        Application.DoEvents();
        Assert.True(task.IsCompleted, $"Task did not complete after pumping UI messages for {timeoutMs} ms.");
    }

    private static void InvokePrivateInstanceVoid(object instance, string methodName, params object?[] args)
    {
        Type type = instance.GetType();
        MethodInfo method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing private instance method {type.Name}.{methodName}");
        method.Invoke(instance, args);
    }

    private static T InvokePrivateInstance<T>(object instance, string methodName, params object?[] args)
    {
        Type type = instance.GetType();
        MethodInfo method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing private instance method {type.Name}.{methodName}");
        object? value = method.Invoke(instance, args);
        return value is T typed
            ? typed
            : throw new InvalidOperationException($"{type.Name}.{methodName} returned unexpected value");
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        Type type = instance.GetType();
        FieldInfo field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing private field {type.Name}.{fieldName}");
        object? value = field.GetValue(instance);
        return value is T typed
            ? typed
            : throw new InvalidOperationException($"{type.Name}.{fieldName} returned unexpected value");
    }

    private static object? GetPrivateFieldValue(object instance, string fieldName)
    {
        Type type = instance.GetType();
        FieldInfo field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing private field {type.Name}.{fieldName}");
        return field.GetValue(instance);
    }

    private static void SetPrivateField(object instance, string fieldName, object? value)
    {
        Type type = instance.GetType();
        FieldInfo field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing private field {type.Name}.{fieldName}");
        field.SetValue(instance, value);
    }
}
