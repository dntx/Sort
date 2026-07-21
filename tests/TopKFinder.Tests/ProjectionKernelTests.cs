using System.Collections.Generic;
using System.Linq;
using Xunit;
using TopKFinder;

public sealed class ProjectionKernelTests
{
    [Fact]
    public void PlanBranchLines_ThrowsWhenGetFamilyCountIsNull()
    {
        List<int> families =
        [
            1,
            2,
        ];

        Assert.Throws<ArgumentNullException>(() => ProjectionKernel.PlanBranchLines(
            families,
            members => new EquivalentOrderSummary(members.Count, "x | y", members.Count.ToString()),
            members => new List<List<int>> { members },
            parentOrbits => parentOrbits.Select(orbit => (orbit, false)).ToList(),
            null!));
    }

    [Fact]
    public void PlanBranchLines_WrapsGetFamilyCountExceptionsWithClearMessage()
    {
        List<int> families =
        [
            1,
            2,
            3,
        ];

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => ProjectionKernel.PlanBranchLines(
            families,
            members => new EquivalentOrderSummary(members.Count, "x | y", members.Count.ToString()),
            _ =>
            [
                [1, 2, 3],
            ],
            parentOrbits => parentOrbits.Select(orbit => (orbit, true)).ToList(),
            family => family == 2 ? throw new InvalidOperationException("getFamilyCount delegate failure") : 1));

        Assert.Contains("getFamilyCount", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void PlanBranchLines_ThrowsWhenMergeOrbitsByProjectionReturnsNull()
    {
        List<int> families =
        [
            1,
            2,
            3,
        ];

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => ProjectionKernel.PlanBranchLines(
            families,
            members => new EquivalentOrderSummary(members.Count, "x | y", members.Count.ToString()),
            _ =>
            [
                [1],
                [2],
                [3],
            ],
            _ => null!,
            _ => 1));

        Assert.Contains("mergeOrbitsByProjection", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanBranchLines_MatchesDisplayPlanner_ForMixedOrbitFallbackCase()
    {
        List<int> families =
        [
            1,
            2,
            3,
            4,
        ];

        EquivalentOrderSummary BuildSummary(List<int> members)
        {
            if (members.Count == 4)
                return new EquivalentOrderSummary(4, "x | y", "4");

            if (members[0] is 1 or 2)
                return new EquivalentOrderSummary(2, "permute {#1,#2}", "2");

            return new EquivalentOrderSummary(members.Count, "u | v", members.Count.ToString());
        }

        List<List<int>> PartitionFamiliesIntoOrbits(List<int> _) =>
        [
            [1, 2],
            [3, 4],
        ];

        List<(List<int> Members, bool ProjectionMerged)> MergeOrbitsByProjection(List<List<int>> parentOrbits) =>
            parentOrbits.Select(orbit => (orbit, false)).ToList();

        int GetFamilyCount(int family) => family <= 2 ? 1 : 2;

            List<ProjectionKernel.KernelBranchLine<int>> kernel = ProjectionKernel.PlanBranchLines(
            families,
            BuildSummary,
            PartitionFamiliesIntoOrbits,
            MergeOrbitsByProjection,
            GetFamilyCount);

            List<DisplayBranchLinePlanner.PlannerBranchLine<int>> planner = DisplayBranchLinePlanner.SplitMergedBucketIntoBranchLines(
            families,
            BuildSummary,
            PartitionFamiliesIntoOrbits,
            MergeOrbitsByProjection,
            GetFamilyCount);

        Assert.Equal(planner.Count, kernel.Count);
        for (int i = 0; i < planner.Count; i++)
        {
            Assert.Equal(planner[i].Members, kernel[i].Members);
            Assert.Equal(planner[i].ProjectionMerged, kernel[i].ProjectionMerged);
        }

        Assert.Equal(new[] { 1, 2 }, kernel[0].Members);
        Assert.False(kernel[0].ProjectionMerged);
        Assert.Equal(new[] { 3 }, kernel[1].Members);
        Assert.Equal(new[] { 4 }, kernel[2].Members);
    }

    [Fact]
    public void IsSingleMergedOrbit_DetectsDisjunctionSeparator()
    {
        var clean = new EquivalentOrderSummary(2, "permute {#1,#2}", "2");
        var split = new EquivalentOrderSummary(2, "#1 > #2 | #2 > #1", "2");

        Assert.True(ProjectionKernel.IsSingleMergedOrbit(clean));
        Assert.True(ProjectionKernel.IsSingleMergedOrbit(null));
        Assert.False(ProjectionKernel.IsSingleMergedOrbit(split));
    }

    [Fact]
    public void BuildProjectionComponents_GroupsTransitivelyEquivalentOrbits()
    {
        List<List<int>> orbits =
        [
            [1],
            [2],
            [3],
            [4],
        ];

        List<List<int>> components = ProjectionKernel.BuildProjectionComponents(
            orbits,
            (left, right) => (left, right) switch
            {
                (1, 2) or (2, 1) => true,
                (2, 3) or (3, 2) => true,
                (3, 4) or (4, 3) => true,
                _ => false,
            });

        Assert.Single(components);
        Assert.Equal(new[] { 0, 1, 2, 3 }, components[0]);
    }

    [Fact]
    public void MergeProjectionOrbits_MergesEquivalentSingletonOrbits()
    {
        List<List<int>> orbits =
        [
            [10],
            [20],
            [30],
        ];

        List<(List<int> Members, bool ProjectionMerged)> merged = ProjectionKernel.MergeProjectionOrbits(
            orbits,
            (left, right) => (left, right) switch
            {
                (10, 20) or (20, 10) => true,
                _ => false,
            },
            canFoldMultiFamilyComponent: _ => true,
            orderRepresentativeFirst: members => members,
            getFamilyCount: _ => 1);

        Assert.Equal(2, merged.Count);
        Assert.Equal(new[] { 10, 20 }, merged[0].Members);
        Assert.True(merged[0].ProjectionMerged);
        Assert.Equal(new[] { 30 }, merged[1].Members);
        Assert.False(merged[1].ProjectionMerged);
    }

    [Fact]
    public void MergeProjectionOrbits_FallsBackWhenMultiFamilyComponentCannotFold()
    {
        List<List<int>> orbits =
        [
            [1, 2],
            [3, 4],
        ];

        List<(List<int> Members, bool ProjectionMerged)> merged = ProjectionKernel.MergeProjectionOrbits(
            orbits,
            (_, _) => true,
            canFoldMultiFamilyComponent: _ => false,
            orderRepresentativeFirst: members => members,
            getFamilyCount: _ => 2);

        Assert.Equal(2, merged.Count);
        Assert.Equal(new[] { 1, 2 }, merged[0].Members);
        Assert.False(merged[0].ProjectionMerged);
        Assert.Equal(new[] { 3, 4 }, merged[1].Members);
        Assert.False(merged[1].ProjectionMerged);
    }

    [Fact]
    public void DisplayFacadeProjectionMerge_MatchesKernelProjectionMerge()
    {
        List<List<int>> orbits =
        [
            [7],
            [8],
            [9],
        ];

        List<(List<int> Members, bool ProjectionMerged)> kernel = ProjectionKernel.MergeProjectionOrbits(
            orbits,
            (left, right) => (left, right) switch
            {
                (7, 8) or (8, 7) => true,
                _ => false,
            },
            canFoldMultiFamilyComponent: _ => true,
            orderRepresentativeFirst: members => members,
            getFamilyCount: _ => 1);

        List<(List<int> Members, bool ProjectionMerged)> display = DisplayRenderEngine.MergeProjectionOrbits(
            orbits,
            (left, right) => (left, right) switch
            {
                (7, 8) or (8, 7) => true,
                _ => false,
            },
            canFoldMultiFamilyComponent: _ => true,
            orderRepresentativeFirst: members => members,
            getFamilyCount: _ => 1);

        Assert.Equal(kernel.Count, display.Count);
        for (int i = 0; i < kernel.Count; i++)
        {
            Assert.Equal(kernel[i].Members, display[i].Members);
            Assert.Equal(kernel[i].ProjectionMerged, display[i].ProjectionMerged);
        }
    }
}
