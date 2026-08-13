namespace TopKFinder;

partial class StrategyBuilder
{
    internal StrategyBuilderTestHooks TestHooks { get; } = new();

    internal sealed class StrategyBuilderTestHooks
    {
        public bool DisableProofTightenFeasibleReuse { get; set; }
        public bool DisableProofTightenInfeasibleReuse { get; set; }
        public bool DisableProofTightenBudgetFitReuse { get; set; }
        public bool DisableProofTightenCandidateGenerationReuse { get; set; }
    }
}
