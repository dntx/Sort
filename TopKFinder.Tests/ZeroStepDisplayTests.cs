using System;
using System.Reflection;
using Xunit;

public sealed class ZeroStepDisplayTests
{
    [Fact]
    public void ProgramFormatSqueeze_ZeroStepPlan_IsProvenOptimal()
    {
        StrategyPlan plan = CreateZeroStepDualReducedPlan();

        string text = InvokePrivateStatic<string>(typeof(Program), "FormatSqueeze", plan);

        Assert.Equal("max steps = 0 (proven optimal)", text);
    }

    [Fact]
    public void MainFormBuildRootLabel_UsesRequestedK_AndClosedZeroStepSqueeze()
    {
        StrategyPlan plan = CreateZeroStepDualReducedPlan();

        string text = InvokePrivateStatic<string>(typeof(MainForm), "BuildRootLabel", plan, plan, plan);

        Assert.Contains("n=2, m=2, k=2 (dual k'=0)", text);
        Assert.Contains("max steps = 0 (proven optimal)", text);
        Assert.DoesNotContain("? <= max steps <= 0", text);
    }

    private static StrategyPlan CreateZeroStepDualReducedPlan()
    {
        return new StrategyPlan(
            n: 2,
            m: 2,
            requestedK: 2,
            k: 0,
            root: StrategyNode.Terminal(1, Array.Empty<int>()),
            elapsed: TimeSpan.Zero,
            searchStatistics: CreateEmptySearchStatistics());
    }

    private static SearchStatistics CreateEmptySearchStatistics()
    {
        return new SearchStatistics(
            searchedStates: 0,
            pendingStates: 0,
            peakPendingStates: 0,
            outputStates: 0,
            expandedOutputStates: 0,
            lowerBoundStates: 0,
            feasibleTopSetStates: 0,
            diagnostics: new SearchDiagnostics(
                rootIncumbents: Array.Empty<SearchMilestone>(),
                lowerBoundPrunes: 0,
                duplicateOutcomeSkips: 0,
                mergedOutcomeCollisions: 0,
                exactCacheHits: 0,
                lowerBoundCacheHits: 0,
                feasibleTopSetCacheHits: 0,
                bestGroupPatternCacheHits: 0),
            phase1Milliseconds: 0,
            phase1bMilliseconds: 0,
            phase2Milliseconds: 0,
            outcomesConstructed: 0,
            candidateGroupsEnumerated: 0,
            searchTreeEdges: -1,
            compactStatesSolved: 0,
            compactGroupsEnumerated: 0,
            compactStepOptimalGroups: 0,
            rootProvenLowerBound: 0);
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
}