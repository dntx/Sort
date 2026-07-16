// Lightweight display-layer facade for the refactor track.
//
// Initial skeleton: keep rendering behavior byte-for-byte by delegating to the
// existing renderers. Later PRs can migrate display-specific logic behind this
// type while parity tests keep output stable.
sealed class DisplayRenderEngine
{
    public StrategyOverview BuildOverview(StrategyPlan plan)
        => StrategyOverviewRenderer.Build(plan);

    public string RenderOverviewText(StrategyPlan plan)
        => StrategyOverviewRenderer.RenderText(plan);

    public string RenderStrategyText(StrategyPlan plan)
        => StrategyTextRenderer.Render(plan);
}