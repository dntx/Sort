using System.Reflection;
using TopKFinder;
using Xunit;

public sealed class SearchTransitionPlannerStructureTests
{
    [Fact]
    public void StrategyBuilder_UsesTopLevelPlannerWithoutOwnerBackReference()
    {
        Assert.Null(typeof(StrategyBuilder).GetNestedType(
            "SearchTransitionPlanner",
            BindingFlags.NonPublic | BindingFlags.Public));

        Type plannerType = typeof(SearchTransitionPlanner);
        Assert.False(plannerType.IsNested);
        Assert.Equal(typeof(StrategyBuilder).Assembly, plannerType.Assembly);

        FieldInfo plannerField = typeof(StrategyBuilder).GetField(
            "_transitionPlanner",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(plannerType, plannerField.FieldType);

        var builder = new StrategyBuilder(6, 3, 3);
        Assert.Null(plannerField.GetValue(builder));

        builder.ProjectSearchTree();

        object? plannerInstance = plannerField.GetValue(builder);
        Assert.NotNull(plannerInstance);
        Assert.IsType<SearchTransitionPlanner>(plannerInstance);

        FieldInfo[] instanceFields = plannerType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.DoesNotContain(instanceFields, field => field.FieldType == typeof(StrategyBuilder));
    }
}