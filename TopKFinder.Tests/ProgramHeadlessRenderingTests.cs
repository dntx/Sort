using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

public sealed class ProgramHeadlessRenderingTests
{
    private static readonly object ConsoleLock = new();
    private static readonly DisplayRenderEngine Engine = new();

    [Fact]
    public void RunHeadlessCore_ExactStageOne_EmitsDisplayEngineRenderedOutput()
    {
        const int n = 9;
        const int m = 3;
        const int k = 3;

        StrategyPlan plan = new StrategyBuilder(n, m, k).BuildStepProofStage();
        string squeeze = InvokePrivateStatic<string>(typeof(Program), "FormatSqueeze", plan);

        string expected =
            $"==================== step-proof ({squeeze}) ===================={Environment.NewLine}" +
            Engine.RenderOverviewText(plan) +
            Environment.NewLine +
            Engine.RenderStrategyText(plan);

        var builder = new StrategyBuilder(n, m, k);
        string actual = CaptureStdout(() =>
            InvokePrivateStaticVoid(
                typeof(Program),
                "RunHeadlessCore",
                builder,
                Program.Mode.Exact,
                1,
                (Action)(() => { })));

        Assert.Equal(NormalizeTimings(expected), NormalizeTimings(actual));
    }

    private static string CaptureStdout(Action action)
    {
        lock (ConsoleLock)
        {
            TextWriter originalOut = Console.Out;
            using var writer = new StringWriter();
            try
            {
                Console.SetOut(writer);
                action();
                return writer.ToString();
            }
            finally
            {
                Console.SetOut(originalOut);
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

    private static void InvokePrivateStaticVoid(Type type, string methodName, params object?[] args)
    {
        MethodInfo method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Missing private static method {type.Name}.{methodName}");
        method.Invoke(null, args);
    }

    private static string NormalizeTimings(string text)
    {
        string normalized = Regex.Replace(text, @"elapsed = [0-9]+\.[0-9]+ ms", "elapsed = <ms>");
        normalized = Regex.Replace(normalized, @"phases: step = [0-9]+ ms, edge = [0-9]+ ms, build = [0-9]+ ms", "phases: step = <ms>, edge = <ms>, build = <ms>");
        return normalized;
    }
}