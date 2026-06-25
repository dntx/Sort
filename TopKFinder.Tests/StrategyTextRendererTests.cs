using Xunit;

public sealed class StrategyTextRendererTests
{
    [Fact]
    public void FormatEffectDetails_OmitsEmptyEffectSets()
    {
        var effect = new StrategyEffect(
            newlyGuaranteedTop: new[] { 0 },
            newlyExcluded: System.Array.Empty<int>(),
            fixedCandidates: new[] { 1, 2 },
            possibleCandidates: System.Array.Empty<int>());

        string details = StrategyTextRenderer.FormatEffectDetails(effect);

        Assert.Contains("+ (#1)", details);
        Assert.Contains("fixed (#2, #3)", details);
        Assert.DoesNotContain("- ()", details);
        Assert.DoesNotContain("possible ()", details);
    }

    [Fact]
    public void FormatEffectDetails_ReturnsEmptyWhenAllEffectSetsAreEmpty()
    {
        var effect = new StrategyEffect(
            newlyGuaranteedTop: System.Array.Empty<int>(),
            newlyExcluded: System.Array.Empty<int>(),
            fixedCandidates: System.Array.Empty<int>(),
            possibleCandidates: System.Array.Empty<int>());

        string details = StrategyTextRenderer.FormatEffectDetails(effect);

        Assert.Equal(string.Empty, details);
    }
}
