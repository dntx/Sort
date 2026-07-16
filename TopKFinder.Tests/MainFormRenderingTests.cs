using System;
using System.Reflection;
using System.Windows.Forms;
using Xunit;

public sealed class MainFormRenderingTests
{
    private static readonly DisplayRenderEngine Engine = new();

    [Fact]
    public void BuildPlanDetails_EmitsDisplayEngineRenderedText()
    {
        StrategyPlan plan = new StrategyBuilder(9, 3, 3).BuildStepProofStage();

        string expectedText = Engine.RenderStrategyText(plan).TrimEnd();
        string actualText = InvokePrivateStatic<string>(typeof(MainForm), "BuildPlanDetails", plan);

        Assert.StartsWith(expectedText, actualText);
    }

    [Fact]
    public void BuildFeasibleOnlyDetails_EmitsDisplayEngineRenderedText()
    {
        StrategyPlan plan = new StrategyBuilder(9, 3, 3).BuildStepProofStage();

        string expectedText = Engine.RenderStrategyText(plan).TrimEnd();
        string actualText = InvokePrivateStatic<string>(typeof(MainForm), "BuildFeasibleOnlyDetails", plan);

        Assert.Contains(expectedText, actualText);
    }

    [Fact]
    public void OverviewMaterialization_EmitsDisplayEngineOverviewRows()
    {
        StrategyPlan plan = new StrategyBuilder(9, 3, 3).BuildStepProofStage();
        StrategyOverview expectedOverview = Engine.BuildOverview(plan);

        using var form = new MainForm();
        InvokePrivateInstanceVoid(form, "RebuildOverview", plan, null, null, false, false);

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
}
