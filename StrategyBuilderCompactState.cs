sealed class StrategyBuilderCompactState
{
    public bool UseCompact;
    public bool CompactUsesFeasibleBudget;
    public bool CompactFeasibilityOnly;
    public bool CompactEnumerationCapped;
    public bool LastProbeEnumerationCapped;
    public bool EnableFeasibleTightening = true;
    public int FeasibleRootBudgetActive = -1;
    public int CompactRootCost = int.MaxValue;
    public bool CompactPatternCacheReadyForMaterialization;

    public void ResetCompactProbeState()
    {
        CompactEnumerationCapped = false;
        CompactUsesFeasibleBudget = false;
        FeasibleRootBudgetActive = -1;
        CompactRootCost = int.MaxValue;
        CompactPatternCacheReadyForMaterialization = false;
        CompactFeasibilityOnly = false;
        UseCompact = false;
    }
}