static class StageNames
{
    public const string StepProof = "step-proof";
    public const string GreedyFeasible = "greedy-feasible";
    public const string GreedyTighten = "greedy-tighten";
    public const string ProofTightenPrefix = "proof-tighten\u2264";
    public const string ProofTightenPattern = "proof-tighten\u2264N";
    public const string ExactEdgeCompactPrefix = "exact-edge-compact@";
    public const string ExactEdgeCompactPattern = "exact-edge-compact@S";
    public const string GreedyEdgeCompactPrefix = "greedy-edge-compact@";
    public const string GreedyEdgeCompactPattern = "greedy-edge-compact@S";

    public static string FormatProofTighten(int budget)
        => $"{ProofTightenPrefix}{budget}";

    public static string FormatExactEdgeCompact(int step)
        => $"{ExactEdgeCompactPrefix}{step}";

    public static string FormatGreedyEdgeCompact(int step)
        => $"{GreedyEdgeCompactPrefix}{step}";
}