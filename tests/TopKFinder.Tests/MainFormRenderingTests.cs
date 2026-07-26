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
        var stage = new StageResult("proof-tighten<=3", plan: null, TimeSpan.Zero, StageOutcome.Tightened, CreateDeferredExactStepStage().Solution);
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
        var stage = new StageResult("proof-tighten<=3", plan: null, TimeSpan.Zero, StageOutcome.Tightened, CreateDeferredExactStepStage().Solution);
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
    public void MaterializeExactStageAsync_StaleRequestVersion_DoesNotApply()
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

        task.GetAwaiter().GetResult();
        Application.DoEvents();

        Assert.False(applied);
    }

    [Fact]
    public void QueueStageMaterialization_NewRequestCancelsPriorRequest()
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
        drain.GetAwaiter().GetResult();

        Assert.NotSame(firstRequest, secondRequest);
        Assert.True(firstRequest.IsCancellationRequested);
        Assert.False(secondRequest.IsCancellationRequested);
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

    private static void SetPrivateField(object instance, string fieldName, object? value)
    {
        Type type = instance.GetType();
        FieldInfo field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Missing private field {type.Name}.{fieldName}");
        field.SetValue(instance, value);
    }
}
