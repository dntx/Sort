using System;

namespace TopKFinder.Tests;

public sealed record CompactCounterSnapshotRow(
    int Searched,
    int Outcomes,
    int Duplicate,
    int CompactStatesSolved,
    int CompactGroupsEnumerated,
    int CompactStepOptimalGroups);

public sealed record CompactCounterSnapshotExportRow(
    int N,
    int M,
    int K,
    int Searched,
    int SearchedCap,
    int SearchedDelta,
    int Outcomes,
    int OutcomesCap,
    int OutcomesDelta,
    int Duplicate,
    int DuplicateCap,
    int DuplicateDelta,
    int CompactStatesSolved,
    int CompactStatesCap,
    int CompactStatesDelta,
    int CompactGroupsEnumerated,
    int CompactGroupsCap,
    int CompactGroupsDelta,
    int CompactStepOptimalGroups,
    int CompactStepOptimalCap,
    int CompactStepOptimalDelta);

public sealed record IterativeCounterSnapshotRow(
    int MaxStep,
    int RootGroupCount,
    int TotalEdges,
    int OutputStates,
    int ExpandedOutputStates,
    int Searched,
    int Outcomes,
    int CandidateGroups);

public sealed record IterativeCounterSnapshotExportRow(
    int N,
    int M,
    int K,
    int MaxStep,
    int RootGroupCount,
    int TotalEdges,
    int OutputStates,
    int ExpandedOutputStates,
    int Searched,
    int SearchedCap,
    int SearchedDelta,
    int Outcomes,
    int OutcomesCap,
    int OutcomesDelta,
    int CandidateGroups,
    int CandidateCap,
    int CandidateDelta);

public static class CounterSnapshotExportHelpers
{
    private sealed record CompactCase(int N, int M, int K, int SearchedCap, int OutcomesCap, int DuplicateCap, int CompactStatesCap, int CompactGroupsCap, int CompactStepOptimalCap);

    private sealed record IterativeCase(int N, int M, int K, int MaxStep, int RootGroupCount, int TotalEdges, int OutputStates, int ExpandedOutputStates, int SearchedCap, int OutcomesCap, int CandidateCap);

    private static readonly CompactCase[] CompactCases =
    {
        new(9, 3, 3, 159, 5047, 711, 77, 1214, 366),
        new(11, 3, 3, 511, 14860, 1569, 129, 2762, 645),
        new(12, 4, 4, 471, 16867, 4687, 46, 1395, 165),
        new(10, 3, 4, 1081, 45433, 4821, 321, 11055, 2772),
        new(12, 4, 3, 130, 3955, 1605, 36, 639, 175),
        new(12, 3, 3, 538, 8346, 599, 8, 145, 9),
        new(8, 4, 2, 7, 26, 12, 2, 5, 5),
        new(10, 3, 5, 623, 9656, 622, 5, 69, 5),
        new(13, 4, 3, 138, 1456, 367, 7, 118, 16),
        new(12, 3, 4, 5962, 233774, 18563, 677, 39691, 5770),
        new(10, 2, 4, 17104, 471864, 469, 4118, 120336, 29291),
    };

    private static readonly IterativeCase[] IterativeCases =
    {
        new(14, 5, 5, 5, 5, 72, 36, 8, 174, 2768, 7474),
        new(16, 5, 5, 6, 5, 122, 29, 12, 1633, 66249, 73060),
        new(17, 5, 5, 6, 5, 135, 40, 13, 1309, 42641, 67024),
        new(18, 5, 5, 6, 5, 227, 66, 14, 1758, 78787, 88908),
        new(12, 6, 6, 3, 6, 16, 17, 2, 25, 66, 65),
        new(14, 6, 6, 4, 6, 92, 23, 3, 45, 404, 2341),
    };

    public static IReadOnlyList<CompactCounterSnapshotExportRow> BuildCompactSnapshotRows()
        => CompactCases.Select(caseItem =>
        {
            CompactCounterSnapshotRow current = GetCompactCounterSnapshot(caseItem.N, caseItem.M, caseItem.K);
            return new CompactCounterSnapshotExportRow(
                caseItem.N,
                caseItem.M,
                caseItem.K,
                current.Searched,
                caseItem.SearchedCap,
                caseItem.SearchedCap - current.Searched,
                current.Outcomes,
                caseItem.OutcomesCap,
                caseItem.OutcomesCap - current.Outcomes,
                current.Duplicate,
                caseItem.DuplicateCap,
                caseItem.DuplicateCap - current.Duplicate,
                current.CompactStatesSolved,
                caseItem.CompactStatesCap,
                caseItem.CompactStatesCap - current.CompactStatesSolved,
                current.CompactGroupsEnumerated,
                caseItem.CompactGroupsCap,
                caseItem.CompactGroupsCap - current.CompactGroupsEnumerated,
                current.CompactStepOptimalGroups,
                caseItem.CompactStepOptimalCap,
                caseItem.CompactStepOptimalCap - current.CompactStepOptimalGroups);
        }).ToArray();

    public static IReadOnlyList<IterativeCounterSnapshotExportRow> BuildIterativeSnapshotRows()
        => IterativeCases.Select(caseItem =>
        {
            IterativeCounterSnapshotRow current = GetIterativeCounterSnapshot(caseItem.N, caseItem.M, caseItem.K);
            return new IterativeCounterSnapshotExportRow(
                caseItem.N,
                caseItem.M,
                caseItem.K,
                current.MaxStep,
                caseItem.RootGroupCount,
                caseItem.TotalEdges,
                current.OutputStates,
                caseItem.ExpandedOutputStates,
                current.Searched,
                caseItem.SearchedCap,
                caseItem.SearchedCap - current.Searched,
                current.Outcomes,
                caseItem.OutcomesCap,
                caseItem.OutcomesCap - current.Outcomes,
                current.CandidateGroups,
                caseItem.CandidateCap,
                caseItem.CandidateCap - current.CandidateGroups);
        }).ToArray();

    public static CompactCounterSnapshotRow GetCompactCounterSnapshot(int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k);
        StrategyPlan plan = builder.ExecuteEdgeCompactStage();
        SearchStatistics stats = plan.SearchStatistics;

        return new CompactCounterSnapshotRow(
            stats.SearchedStates,
            stats.OutcomesConstructed,
            stats.Diagnostics.DuplicateOutcomeSkips,
            stats.CompactStatesSolved,
            stats.CompactGroupsEnumerated,
            stats.CompactStepOptimalGroups);
    }

    public static IterativeCounterSnapshotRow GetIterativeCounterSnapshot(int n, int m, int k)
    {
        var builder = new StrategyBuilder(n, m, k)
        {
            ForceIterativeDeepeningForTesting = true
        };

        StrategyPlan plan = builder.ExecuteStepProofStage();
        SearchStatistics stats = plan.SearchStatistics;

        return new IterativeCounterSnapshotRow(
            plan.MaxStep,
            plan.Root.Group.Count,
            plan.TotalBranchEdges,
            stats.OutputStates,
            stats.ExpandedOutputStates,
            stats.SearchedStates,
            stats.OutcomesConstructed,
            stats.CandidateGroupsEnumerated);
    }
}