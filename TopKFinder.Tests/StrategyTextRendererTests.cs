using Xunit;

public sealed class StrategyTextRendererTests
{
    [Fact]
    public void FormatBranchLead_UsesOrderTextAndBracketedEffect()
    {
        var branch = new StrategyBranch(
            "#2 > #1",
            equivalentOrders: null,
            effect: new StrategyEffect(
                newlyGuaranteedTop: new[] { 1 },
                newlyExcluded: new[] { 2 },
                fixedCandidates: new[] { 0, 1 },
                possibleCandidates: new[] { 3 }),
            next: StrategyNode.Terminal(stateId: 9, topSet: new[] { 0, 1 }));

        string text = StrategyTextRenderer.FormatBranchLead(branch);

        Assert.Equal("#2 > #1: [+ (#2), - (#3), fixed (#1, #2), possible (#4)]", text);
    }

    [Fact]
    public void FormatBranchLead_OmitsEmptyEffectSets()
    {
        var branch = new StrategyBranch(
            "#1 > #2",
            equivalentOrders: null,
            effect: new StrategyEffect(
                newlyGuaranteedTop: new[] { 0 },
                newlyExcluded: System.Array.Empty<int>(),
                fixedCandidates: System.Array.Empty<int>(),
                possibleCandidates: System.Array.Empty<int>()),
            next: StrategyNode.Terminal(stateId: 3, topSet: new[] { 0 }));

        string text = StrategyTextRenderer.FormatBranchLead(branch);

        Assert.Equal("#1 > #2: [+ (#1)]", text);
        Assert.DoesNotContain("- ()", text);
        Assert.DoesNotContain("fixed ()", text);
        Assert.DoesNotContain("possible ()", text);
    }

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
