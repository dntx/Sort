using System.Collections.Generic;
using System.Linq;
using Xunit;

public sealed class ProjectionKernelTests
{
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
