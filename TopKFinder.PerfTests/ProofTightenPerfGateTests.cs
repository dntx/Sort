using System;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

// On-demand regression gate for the greedy proof-tighten stage on the historically sensitive
// (20,2,6) shape. This does NOT run in the default suite.
//
// Enable:
//   $env:RUN_PROOF_TIGHTEN_GATE = "1"
//   dotnet test TopKFinder.PerfTests\TopKFinder.PerfTests.csproj --filter ProofTightenPerfGateTests
//
// Optional knobs:
//   PROOF_TIGHTEN_TIMEOUT_SECONDS       (default 200)
//   PROOF_TIGHTEN_OUTCOMES_CAP          (default 0 = disabled)
//   PROOF_TIGHTEN_CANDIDATES_CAP        (default 0 = disabled)
//   PROOF_TIGHTEN_SEARCHED_STATES_CAP   (default 0 = disabled)
//
// Why this exists:
// - Wall-clock-only gates are noisy across machines.
// - This gate combines a coarse timeout sentinel (hang/explosion catcher) with optional deterministic
//   work counters (machine-independent, ratchet-friendly).
[Trait("Category", "Slow")]
public sealed class ProofTightenPerfGateTests
{
    private readonly ITestOutputHelper _output;

    public ProofTightenPerfGateTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void GreedyProofTighten_FirstProbe_20_2_6_CompletesWithinGate()
    {
        if (Environment.GetEnvironmentVariable("RUN_PROOF_TIGHTEN_GATE") != "1")
            return;

        int timeoutSeconds = ReadPositiveIntEnv("PROOF_TIGHTEN_TIMEOUT_SECONDS", 200);
        int outcomesCap = ReadNonNegativeIntEnv("PROOF_TIGHTEN_OUTCOMES_CAP", 0);
        int candidatesCap = ReadNonNegativeIntEnv("PROOF_TIGHTEN_CANDIDATES_CAP", 0);
        int searchedCap = ReadNonNegativeIntEnv("PROOF_TIGHTEN_SEARCHED_STATES_CAP", 0);

        int maxObservedSearched = 0;
        int lastPendingStates = -1;
        int lastRootProvenLowerBound = 0;
        long lastElapsedMilliseconds = 0;
        double lastEstimatedProgress01 = 0;

        (int FeasibleStep, int Budget, StageOutcome Outcome, bool HasPlan, int? PlanStep,
            int? Outcomes, int? Candidates, int? Searched, int MaxObservedSearched,
            int LastPendingStates, int LastRootProvenLowerBound, long LastElapsedMilliseconds,
            double LastEstimatedProgress01, double StageElapsedMs) result;

        try
        {
            result = TestTimeoutHelper.RunWithTimeout(
                "greedy proof-tighten first probe (20,2,6)",
                TimeSpan.FromSeconds(timeoutSeconds),
                cancellationToken =>
                {
                    var builder = new StrategyBuilder(
                        20,
                        2,
                        6,
                        cancellationToken,
                        snapshot =>
                        {
                            if (snapshot.SearchedStates > maxObservedSearched)
                                maxObservedSearched = snapshot.SearchedStates;
                            lastPendingStates = snapshot.PendingStates;
                            lastRootProvenLowerBound = snapshot.RootProvenLowerBound;
                            lastElapsedMilliseconds = snapshot.ElapsedMilliseconds;
                            lastEstimatedProgress01 = snapshot.EstimatedProgress01;
                        });

                    StrategyPlan feasible = builder.ExecuteGreedyFeasibleStage();
                    int budget = feasible.MaxStep - 1;
                    StageResult stage = builder.ExecuteProofTightenStage(budget);

                    int? outcomes = stage.Plan?.SearchStatistics.OutcomesConstructed;
                    int? candidates = stage.Plan?.SearchStatistics.CandidateGroupsEnumerated;
                    int? searched = stage.Plan?.SearchStatistics.SearchedStates;

                    return (
                        FeasibleStep: feasible.MaxStep,
                        Budget: budget,
                        Outcome: stage.Outcome,
                        HasPlan: stage.HasPlan,
                        PlanStep: stage.Plan?.MaxStep,
                        Outcomes: outcomes,
                        Candidates: candidates,
                        Searched: searched,
                        MaxObservedSearched: maxObservedSearched,
                        LastPendingStates: lastPendingStates,
                        LastRootProvenLowerBound: lastRootProvenLowerBound,
                        LastElapsedMilliseconds: lastElapsedMilliseconds,
                        LastEstimatedProgress01: lastEstimatedProgress01,
                        StageElapsedMs: stage.Elapsed.TotalMilliseconds);
                });
        }
        catch (XunitException ex) when (ex.Message.Contains("exceeded timeout", StringComparison.OrdinalIgnoreCase))
        {
            throw new XunitException(
                ex.Message +
                $" maxObservedSearched={maxObservedSearched}. " +
                    $"lastPending={lastPendingStates}. " +
                    $"lastRootProvenLowerBound={lastRootProvenLowerBound}. " +
                    $"lastElapsedMs={lastElapsedMilliseconds}. " +
                    $"lastEstimatedProgress01={lastEstimatedProgress01:F4}. " +
                "Set PROOF_TIGHTEN_TIMEOUT_SECONDS higher for baseline capture, " +
                "or keep this timeout to enforce the current gate.");
        }

        _output.WriteLine(
            "ProofTightenGate: " +
            $"feasibleStep={result.FeasibleStep}, " +
            $"budget={result.Budget}, " +
            $"outcome={result.Outcome}, " +
            $"hasPlan={result.HasPlan}, " +
            $"planStep={(result.PlanStep.HasValue ? result.PlanStep.Value.ToString() : "n/a")}, " +
            $"stageElapsedMs={result.StageElapsedMs:F1}, " +
            $"outcomes={(result.Outcomes.HasValue ? result.Outcomes.Value.ToString() : "n/a")}, " +
            $"candidates={(result.Candidates.HasValue ? result.Candidates.Value.ToString() : "n/a")}, " +
            $"searched={(result.Searched.HasValue ? result.Searched.Value.ToString() : "n/a")}, " +
            $"maxObservedSearched={result.MaxObservedSearched}, " +
            $"lastPending={result.LastPendingStates}, " +
            $"lastRootProvenLowerBound={result.LastRootProvenLowerBound}, " +
            $"lastElapsedMs={result.LastElapsedMilliseconds}, " +
            $"lastEstimatedProgress01={result.LastEstimatedProgress01:F4}");

        Assert.True(result.Budget >= 1, "proof-tighten budget must be positive");
        Assert.NotEqual(StageOutcome.Incomplete, result.Outcome);

        if (result.HasPlan)
        {
            Assert.True(result.PlanStep!.Value <= result.Budget,
                $"tightened plan step {result.PlanStep.Value} exceeded budget {result.Budget}");
        }

        if (outcomesCap > 0)
        {
            Assert.True(result.Outcomes.HasValue,
                "PROOF_TIGHTEN_OUTCOMES_CAP requires a materialized plan (Outcome=Tightened)");
            Assert.True(result.Outcomes!.Value <= outcomesCap,
                $"proof-tighten outcomes regressed to {result.Outcomes.Value} (cap {outcomesCap})");
        }

        if (candidatesCap > 0)
        {
            Assert.True(result.Candidates.HasValue,
                "PROOF_TIGHTEN_CANDIDATES_CAP requires a materialized plan (Outcome=Tightened)");
            Assert.True(result.Candidates!.Value <= candidatesCap,
                $"proof-tighten candidate groups regressed to {result.Candidates.Value} (cap {candidatesCap})");
        }

        if (searchedCap > 0)
        {
            Assert.True(result.Searched.HasValue,
                "PROOF_TIGHTEN_SEARCHED_STATES_CAP requires a materialized plan (Outcome=Tightened)");
            Assert.True(result.Searched!.Value <= searchedCap,
                $"proof-tighten searched states regressed to {result.Searched.Value} (cap {searchedCap})");
        }
    }

    private static int ReadPositiveIntEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (!int.TryParse(raw, out int parsed) || parsed <= 0)
            return fallback;
        return parsed;
    }

    private static int ReadNonNegativeIntEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (!int.TryParse(raw, out int parsed) || parsed < 0)
            return fallback;
        return parsed;
    }
}