using System.Text;
using Xunit;

// On-demand GreedyTighten evaluation with timeout control and optional round-cap variants.
//
// Run:
//   $env:RUN_GREEDY_TIGHTEN_EVAL = "1"
//   dotnet test TopKFinder.PerfTests\TopKFinder.PerfTests.csproj --filter GreedyTightenEval
//
// Optional knobs:
//   GT_EVAL_NMAX (default 10)
//   GT_EVAL_CASE_TIMEOUT_SECONDS (default 20)
//   GT_EVAL_REPORT_PATH (default <repo>\greedy-tighten-eval-report.txt)
//   GT_EVAL_COMPARE_SINGLE_ROUND (default 1)
//   GT_EVAL_COMPARE_TWO_ROUNDS (default 1)
public sealed class GreedyTightenEvalTests
{
    [Fact]
    public void GreedyTightenEval()
    {
        if (Environment.GetEnvironmentVariable("RUN_GREEDY_TIGHTEN_EVAL") != "1")
            return;

        int nMax = ReadIntEnv("GT_EVAL_NMAX", 10);
        int caseTimeoutSeconds = ReadIntEnv("GT_EVAL_CASE_TIMEOUT_SECONDS", 20);
        bool compareSingleRound = ReadBoolEnv("GT_EVAL_COMPARE_SINGLE_ROUND", fallback: true);
        bool compareTwoRounds = ReadBoolEnv("GT_EVAL_COMPARE_TWO_ROUNDS", fallback: true);
        string reportPath = Environment.GetEnvironmentVariable("GT_EVAL_REPORT_PATH")
            ?? Path.Combine(FindRepoRoot(), "greedy-tighten-eval-report.txt");

        var caseTimeout = TimeSpan.FromSeconds(caseTimeoutSeconds);

        int scanned = 0;
        int skippedSlow = 0;
        int skippedTimeout = 0;

        int fullBelowOpt = 0;
        int fullWorseThanU = 0;
        int fullTouchdown = 0;
        long fullElapsedMs = 0;
        long fullHeightCalls = 0;
        long fullMemoHits = 0;
        long fullHeightUnderGroupCalls = 0;

        int singleCases = 0;
        int singleSameUPrimeAsFull = 0;
        int singleTouchdown = 0;
        long singleElapsedMs = 0;

        int twoCases = 0;
        int twoSameUPrimeAsFull = 0;
        int twoTouchdown = 0;
        long twoElapsedMs = 0;

        var caseLines = new List<string>();

        for (int n = 3; n <= nMax; n++)
        for (int m = 2; m <= n; m++)
        for (int k = 1; k <= n; k++)
        {
            if (m >= n && k >= n)
                continue;
            if (Program.IsPotentiallySlowSearch(n, m, k))
            {
                skippedSlow++;
                continue;
            }

            int optimum;
            int feasibleUpper;
            try
            {
                optimum = TestTimeoutHelper.RunWithTimeout(
                    $"BuildStepProofPlan({n},{m},{k})",
                    caseTimeout,
                    cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildStepProofPlan().MaxStep);

                feasibleUpper = TestTimeoutHelper.RunWithTimeout(
                    $"BuildGreedyFeasiblePlan({n},{m},{k})",
                    caseTimeout,
                    cancellationToken => new StrategyBuilder(n, m, k, cancellationToken).BuildGreedyFeasiblePlan().MaxStep);
            }
            catch (Xunit.Sdk.XunitException)
            {
                skippedTimeout++;
                continue;
            }

            EvalVariantResult full;
            try
            {
                full = EvaluateVariant(n, m, k, roundCap: null, caseTimeout);
            }
            catch (Xunit.Sdk.XunitException)
            {
                skippedTimeout++;
                continue;
            }

            scanned++;
            fullElapsedMs += full.ElapsedMs;
            fullHeightCalls += full.HeightCalls;
            fullMemoHits += full.MemoHits;
            fullHeightUnderGroupCalls += full.HeightUnderGroupCalls;

            if (full.MaxStep < optimum)
                fullBelowOpt++;
            if (full.MaxStep > feasibleUpper)
                fullWorseThanU++;
            if (full.MaxStep == optimum)
                fullTouchdown++;

            int singleMaxStep = -1;
            long singleMs = -1;
            if (compareSingleRound)
            {
                EvalVariantResult single = EvaluateVariant(n, m, k, roundCap: 1, caseTimeout);
                singleCases++;
                singleElapsedMs += single.ElapsedMs;
                singleMaxStep = single.MaxStep;
                singleMs = single.ElapsedMs;
                if (single.MaxStep == full.MaxStep)
                    singleSameUPrimeAsFull++;
                if (single.MaxStep == optimum)
                    singleTouchdown++;
            }

            int twoMaxStep = -1;
            long twoMs = -1;
            if (compareTwoRounds)
            {
                EvalVariantResult two = EvaluateVariant(n, m, k, roundCap: 2, caseTimeout);
                twoCases++;
                twoElapsedMs += two.ElapsedMs;
                twoMaxStep = two.MaxStep;
                twoMs = two.ElapsedMs;
                if (two.MaxStep == full.MaxStep)
                    twoSameUPrimeAsFull++;
                if (two.MaxStep == optimum)
                    twoTouchdown++;
            }

            caseLines.Add(
                $"({n},{m},{k}) opt={optimum} U={feasibleUpper} full(U'={full.MaxStep}, rounds={full.Rounds}, ms={full.ElapsedMs}, H={full.HeightCalls}, hits={full.MemoHits}, Hg={full.HeightUnderGroupCalls})" +
                (compareSingleRound ? $" single(U'={singleMaxStep}, ms={singleMs})" : string.Empty) +
                (compareTwoRounds ? $" two(U'={twoMaxStep}, ms={twoMs})" : string.Empty));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# GreedyTighten eval report (nMax={nMax})");
        sb.AppendLine($"scanned={scanned}, skippedSlow={skippedSlow}, skippedTimeout={skippedTimeout}");
        sb.AppendLine();

        sb.AppendLine("## Correctness guards (full rounds)");
        sb.AppendLine($"belowOpt={fullBelowOpt}");
        sb.AppendLine($"worseThanU={fullWorseThanU}");
        sb.AppendLine($"touchdown={fullTouchdown}/{scanned}");
        sb.AppendLine();

        sb.AppendLine("## Full rounds aggregate");
        sb.AppendLine($"elapsedMsTotal={fullElapsedMs}");
        sb.AppendLine($"heightCallsTotal={fullHeightCalls}");
        sb.AppendLine($"memoHitsTotal={fullMemoHits}");
        sb.AppendLine($"heightUnderGroupCallsTotal={fullHeightUnderGroupCalls}");
        sb.AppendLine();

        if (compareSingleRound)
        {
            sb.AppendLine("## Single-round compare (maxRounds=1)");
            sb.AppendLine($"sameUPrimeAsFull={singleSameUPrimeAsFull}/{singleCases}");
            sb.AppendLine($"touchdown={singleTouchdown}/{singleCases}");
            sb.AppendLine($"elapsedMsTotal={singleElapsedMs}");
            if (fullElapsedMs > 0)
                sb.AppendLine($"ratioVsFull={(double)singleElapsedMs / fullElapsedMs:0.00}x");
            sb.AppendLine();
        }

        if (compareTwoRounds)
        {
            sb.AppendLine("## Two-round compare (maxRounds=2)");
            sb.AppendLine($"sameUPrimeAsFull={twoSameUPrimeAsFull}/{twoCases}");
            sb.AppendLine($"touchdown={twoTouchdown}/{twoCases}");
            sb.AppendLine($"elapsedMsTotal={twoElapsedMs}");
            if (fullElapsedMs > 0)
                sb.AppendLine($"ratioVsFull={(double)twoElapsedMs / fullElapsedMs:0.00}x");
            sb.AppendLine();
        }

        sb.AppendLine("## Cases");
        foreach (string line in caseLines)
            sb.AppendLine(line);

        File.WriteAllText(reportPath, sb.ToString());

        Assert.True(fullBelowOpt == 0, $"GreedyTighten full rounds dropped below optimum in {fullBelowOpt} case(s). See {reportPath}");
        Assert.True(fullWorseThanU == 0, $"GreedyTighten full rounds was worse than feasible upper bound in {fullWorseThanU} case(s). See {reportPath}");
    }

    private static EvalVariantResult EvaluateVariant(int n, int m, int k, int? roundCap, TimeSpan timeout)
    {
        return TestTimeoutHelper.RunWithTimeout(
            $"BuildGreedyTightenPlan({n},{m},{k},roundCap={(roundCap?.ToString() ?? "full")})",
            timeout,
            cancellationToken =>
            {
                var builder = new StrategyBuilder(n, m, k, cancellationToken)
                {
                    GreedyTightenMaxRoundsForTesting = roundCap,
                };

                StrategyPlan plan = builder.BuildGreedyTightenPlan();
                return new EvalVariantResult(
                    plan.MaxStep,
                    (long)plan.Elapsed.TotalMilliseconds,
                    builder.GreedyTightenRounds,
                    builder.GreedyTightenHeightCalls,
                    builder.GreedyTightenHeightMemoHits,
                    builder.GreedyTightenHeightUnderGroupCalls);
            });
    }

    private static int ReadIntEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int v) && v > 0 ? v : fallback;
    }

    private static bool ReadBoolEnv(string name, bool fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TopKFinder.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private readonly record struct EvalVariantResult(
        int MaxStep,
        long ElapsedMs,
        int Rounds,
        int HeightCalls,
        int MemoHits,
        int HeightUnderGroupCalls);
}
