using System;
using System.Diagnostics;
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
    public void MarshalStageToUiThread_PauseEnabled_BlocksWorkerCallback()
    {
        using var form = new MainForm();
        _ = form.Handle;
        SetPrivateField(form, "_pauseEachStageForRun", true);

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

        Assert.True(stopwatch.ElapsedMilliseconds >= 100);
        Assert.True(callbackRan);
    }

    [Fact]
    public async Task MaterializeExactStageAsync_StaleRequestVersion_DoesNotApply()
    {
        StageResult stage = CreateDeferredExactStepStage();

        using var form = new MainForm();
        _ = form.Handle;
        SetPrivateField(form, "_presentationGeneration", 7);
        SetPrivateField(form, "_presentationRequestVersion", 5);

        bool applied = false;
        Task task = InvokePrivateInstance<Task>(
            form,
            "MaterializeExactStageAsync",
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
    public async Task MaterializeExactStageAsync_CurrentRequestVersion_Applies()
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
            "MaterializeExactStageAsync",
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
    public async Task MaterializeExactStageAsync_OnlyCurrentRequestApplies()
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
            "MaterializeExactStageAsync",
            stage,
            (Action<StageResult>)(_ => staleApplied = true),
            11,
            8,
            CancellationToken.None);
        Task current = InvokePrivateInstance<Task>(
            form,
            "MaterializeExactStageAsync",
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
    public async Task QueueStageMaterialization_NewRequestCancelsPriorRequest()
    {
        StageResult stage = CreateDeferredExactStepStage();

        using var form = new MainForm();
        _ = form.Handle;
        InvokePrivateInstanceVoid(form, "ResetPresentationInfrastructure");

        InvokePrivateInstanceVoid(
            form,
            "QueueStageMaterialization",
            stage,
            (Action<StageResult>)(_ => { }));
        CancellationTokenSource firstRequest = GetPrivateField<CancellationTokenSource>(form, "_activePresentationRequestSource");

        InvokePrivateInstanceVoid(
            form,
            "QueueStageMaterialization",
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
    public void OnProofTightenStage_NonMaterializingStage_InvalidatesOlderPresentationRequest()
    {
        StageResult deferredStage = CreateDeferredExactStepStage();
        StrategyPlan feasiblePlan = new StrategyBuilder(8, 3, 3).ExecuteStepProofStage();

        using var form = new MainForm();
        _ = form.Handle;
        SetPrivateField(form, "_feasiblePlan", feasiblePlan);
        SetPrivateField(form, "_incumbentStage", deferredStage);
        SetPrivateField(form, "_initialGreedyStage", deferredStage);
        InvokePrivateInstanceVoid(form, "ShowInitialStagePlaceholder", 8, 3, 3, true);

        InvokePrivateInstanceVoid(
            form,
            "QueueStageMaterialization",
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
        Assert.True(pendingRequest.IsCancellationRequested);
        Assert.True(requestVersionAfter > requestVersionBefore);
    }

    [Fact]
    public void OnProofTightenStage_BuffersUntilInitialGreedyStageMaterialized()
    {
        StrategyPlan feasiblePlan = new StrategyBuilder(8, 3, 3).ExecuteStepProofStage();
        SolvedStrategy feasibleSolution = CreateDeferredExactStepStage().Solution!;

        using var form = new MainForm();
        _ = form.Handle;

        SetPrivateField(form, "_feasiblePlan", null);
        SetPrivateField(form, "_initialGreedyStage", new StageResult(
            StageNames.GreedyFeasible,
            materializedPlan: null,
            TimeSpan.FromMilliseconds(1),
            StageOutcome.Completed,
            feasibleSolution,
            StageTimings.Legacy(TimeSpan.FromMilliseconds(1))));

        var incoming = new StageResult(
            StageNames.FormatProofTighten(feasiblePlan.MaxStep - 1),
            feasiblePlan,
            TimeSpan.FromMilliseconds(2),
            StageOutcome.Tightened,
            feasibleSolution,
            StageTimings.Legacy(TimeSpan.FromMilliseconds(2)));

        InvokePrivateInstanceVoid(form, "OnProofTightenStage", incoming);

        List<StageResult> buffered = GetPrivateField<List<StageResult>>(form, "_pendingGreedyEdgeStages");
        List<StageResult> landed = GetPrivateField<List<StageResult>>(form, "_proofTightenStages");
        Assert.Single(buffered);
        Assert.Empty(landed);

        InvokePrivateInstanceVoid(form, "ApplyMaterializedInitialGreedyStage", new StageResult(
            StageNames.GreedyFeasible,
            feasiblePlan,
            TimeSpan.FromMilliseconds(1),
            StageOutcome.Completed,
            feasibleSolution,
            StageTimings.Legacy(TimeSpan.FromMilliseconds(1))));

        Assert.Empty(buffered);
        Assert.Single(landed);
        Assert.Equal(incoming.Name, landed[0].Name);
    }

    [Fact]
    public void ApplyMaterializedInitialGreedyStage_NullPlan_ReturnsWithoutThrowing()
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

        InvokePrivateInstanceVoid(form, "ApplyMaterializedInitialGreedyStage", stage);

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
