using System;
using System.Windows.Forms;

class Program
{
    private const string UsageText = "Usage: TopKFinder <n> <m> <k>";

    private const string HelpText =
        "TopK Finder - generate a comparison strategy for finding the top-k of n numbers\n" +
        "using only a sort-at-most-m operation.\n" +
        "\n" +
        "Usage:\n" +
        "  TopKFinder                      Launch the desktop (WinForms) explorer.\n" +
        "  TopKFinder <n> <m> <k>          Run two-stage search: print the step strategy, then the edge refinement if improved.\n" +
        "  ... | TopKFinder                Read n, m, k from stdin (one value per line).\n" +
        "\n" +
        "Options:\n" +
        "  -h, --help      Show this help and exit.\n" +
        "  --mode <mode>   Search mode. exact (default) = exact + compact (proven optimal).\n" +
        "                  greedy = three-phase architecture: feasible + compact-for-step + compact-for-edge (interruptible with Ctrl+C).\n" +
        "\n" +
        "Arguments:\n" +
        "  n   total number of elements   (1 <= n <= 64)\n" +
        "  m   max sort capacity          (2 <= m <= n)\n" +
        "  k   how many top elements      (1 <= k <= n)\n" +
        "      if k > n/2, the solver automatically reduces to the dual k' = n-k\n" +
        "\n" +
        "Progress is written to stderr; the strategy is written to stdout, so you can\n" +
        "redirect them independently (e.g. TopKFinder 12 3 3 > tree.txt).\n" +
        "\n" +
        "Examples:\n" +
        "  TopKFinder 5 3 2\n" +
        "  TopKFinder 9 3 3\n" +
        "  (interactive) run with no args, or pipe n, m, k on three stdin lines";

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            if (IsHelpRequested(args))
            {
                Console.WriteLine(HelpText);
                return;
            }

            // Special benchmark mode to compare architectures
            if (args.Length > 0 && args[0] == "--benchmark-architectures")
            {
                CompareArchitectures.RunComparison();
                return;
            }

            // Stage 1 experiment: min-step greedy with tightening
            if (args.Length > 0 && args[0] == "--test-stage1")
            {
                TestStage1MinStepGreedy.RunStage1Test();
                return;
            }

            // Compare Stage 1 with original
            if (args.Length > 0 && args[0] == "--compare-stage1")
            {
                CompareStage1WithOriginal.RunComparison();
                return;
            }

            if (!TryParseCliArgs(args, out string? nText, out string? mText, out string? kText, out bool feasibleMode, out string? argError))
            {
                Console.WriteLine(argError);
                Console.WriteLine(UsageText);
                return;
            }

            if (!TryParseAndValidate(nText, mText, kText, out int nFromArgs, out int mFromArgs, out int kFromArgs, out string? errorFromArgs))
            {
                Console.WriteLine(errorFromArgs);
                return;
            }

            RunHeadless(nFromArgs, mFromArgs, kFromArgs, feasibleMode);
            return;
        }

        bool hasRedirectedInput = Console.IsInputRedirected && Console.In.Peek() != -1;

        if (!hasRedirectedInput)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            return;
        }

        Console.Write("Enter n (total numbers): ");
        string? nTextStdin = Console.ReadLine();
        Console.Write("Enter m (max sort size): ");
        string? mTextStdin = Console.ReadLine();
        Console.Write("Enter k (top-k to find): ");
        string? kTextStdin = Console.ReadLine();

        if (!TryParseAndValidate(nTextStdin, mTextStdin, kTextStdin, out int n, out int m, out int k, out string? error))
        {
            Console.WriteLine(error);
            return;
        }

        RunHeadless(n, m, k, feasibleMode: false);
    }

    public static bool IsHelpRequested(string[] args)
    {
        return Array.Exists(args, a => a == "--help" || a == "-h");
    }

    public static bool TryParseCliArgs(
        string[] args,
        out string? nText,
        out string? mText,
        out string? kText,
        out bool feasibleMode,
        out string? error)
    {
        nText = null;
        mText = null;
        kText = null;
        feasibleMode = false;
        error = null;

        var positionals = new System.Collections.Generic.List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--mode")
            {
                if (i + 1 >= args.Length)
                {
                    error = "Error: --mode requires a value (exact or greedy)";
                    return false;
                }
                string value = args[++i];
                if (string.Equals(value, "greedy", StringComparison.OrdinalIgnoreCase))
                    feasibleMode = true;
                else if (string.Equals(value, "exact", StringComparison.OrdinalIgnoreCase))
                    feasibleMode = false;
                else
                {
                    error = $"Error: unknown mode '{value}' (expected exact or greedy)";
                    return false;
                }
            }
            else if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                error = $"Error: unknown option '{arg}'";
                return false;
            }
            else
            {
                positionals.Add(arg);
            }
        }

        if (positionals.Count != 3)
        {
            error = $"Error: expected 3 positional arguments (n m k) but got {positionals.Count}";
            return false;
        }

        nText = positionals[0];
        mText = positionals[1];
        kText = positionals[2];
        return true;
    }

    private static void RunHeadless(int n, int m, int k, bool feasibleMode)
    {
        int canonicalK = Math.Min(k, n - k);
        if (canonicalK != k)
            Console.Error.WriteLine($"note: k={k} > n/2; solving the dual problem with k'={canonicalK}.");

        bool showProgress = !Console.IsErrorRedirected;
        long lastEmitMs = -1;
        int lastLineLength = 0;

        void ReportProgress(SearchProgressSnapshot snapshot)
        {
            if (!showProgress)
            {
                return;
            }

            if (lastEmitMs >= 0 && snapshot.ElapsedMilliseconds - lastEmitMs < 250)
            {
                return;
            }

            lastEmitMs = snapshot.ElapsedMilliseconds;
            string progressText = $"{snapshot.EstimatedProgress01 * 100.0:F1}%";
            string etaText = snapshot.EstimatedRemainingMilliseconds >= 0
                ? $"{snapshot.EstimatedRemainingMilliseconds / 1000.0:F1}s"
                : "-";
            string line = $"searching... elapsed={snapshot.ElapsedMilliseconds / 1000.0:F1}s " +
                $"searched={snapshot.SearchedStates} pending={snapshot.PendingStates} output={snapshot.OutputStates} " +
                $"progress: {progressText} eta: {etaText}";
            Console.Error.Write("\r" + line.PadRight(lastLineLength));
            lastLineLength = line.Length;
        }

        void ClearProgressLine()
        {
            if (showProgress && lastLineLength > 0)
                Console.Error.Write("\r" + new string(' ', lastLineLength) + "\r");
        }

        using var cancellation = new System.Threading.CancellationTokenSource();
        // Ctrl+C requests a graceful stop instead of killing the process: the greedy path catches the
        // cancellation and still prints the best-so-far progression and tree. e.Cancel = true keeps the
        // process alive so that output can be flushed. A second Ctrl+C (after the source is already
        // cancelling) is left to the default handler for a hard exit.
        ConsoleCancelEventHandler? cancelHandler = null;
        cancelHandler = (_, e) =>
        {
            if (cancellation.IsCancellationRequested)
                return;
            e.Cancel = true;
            cancellation.Cancel();
            if (showProgress)
                Console.Error.WriteLine("\ncancelling... (finishing the current step, then printing best-so-far)");
        };
        Console.CancelKeyPress += cancelHandler;

        var builder = new StrategyBuilder(
            n,
            m,
            k,
            cancellation.Token,
            ReportProgress,
            reportCombinedRunProgress: true);

        try
        {
            RunHeadlessCore(builder, feasibleMode, ClearProgressLine);
        }
        catch (OperationCanceledException)
        {
            // Safety net for a cancellation raised outside the per-phase handlers (e.g. during the fast
            // initial greedy plan): nothing meaningful to show, so just note the interruption.
            ClearProgressLine();
            Console.WriteLine("interrupted (no result).");
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static void RunHeadlessCore(StrategyBuilder builder, bool feasibleMode, Action clearProgressLine)
    {
        void ClearProgressLine() => clearProgressLine();

        if (feasibleMode)
        {
            // Greedy mode: a fast greedy feasible plan gives a valid bound, then the compact stage
            // uses U as its step ceiling, minimizes edges under U, and may pick up a smaller real
            // step for free. Fast/interruptible, not proven optimal.
            StrategyPlan feasiblePlan = builder.BuildFeasiblePlan();
            ClearProgressLine();

            // The anytime stages (baseline "compact", each "compact≤N" tightening, and a terminal
            // no-solution ceiling) are produced incrementally for the GUI. The CLI is a batch tool,
            // so printing every intermediate tree is just noise: collect the stages, then print a
            // one-line progression summary followed by only the final (best) tree. A stage that has a
            // solution but does not strictly improve the incumbent (e.g. compact baseline = same steps,
            // more edges than greedy) is flagged "no improvement" and never becomes the final tree.
            var stageSummaries = new System.Collections.Generic.List<string>
            {
                FormatStageSummary("greedy", feasiblePlan),
            };
            StrategyPlan incumbentPlan = feasiblePlan;
            string finalName = "greedy";
            StrategyPlan finalPlan = feasiblePlan;
            void CollectEdgeStage(GreedyEdgeStage stage)
            {
                if (!stage.HasSolution)
                {
                    if (stage.Outcome == GreedyEdgeStageOutcome.NoSolution)
                        {
                            // Proven infeasible at this ceiling (complete enumeration) => the incumbent is
                            // optimal (opt = incumbent.MaxStep). Close its squeeze so the final tree reports
                            // proven optimal.
                            stageSummaries.Add($"{stage.Name}: no solution");
                            finalPlan = finalPlan.WithRootProvenLowerBound(finalPlan.MaxStep);
                            incumbentPlan = finalPlan;
                        }
                        else
                        {
                            // Probe finished but the greedy candidate cap truncated the group enumeration, so
                            // "no group fit" is not a proof. Leave the squeeze open (no proven-optimal claim);
                            // the incumbent simply stands.
                            stageSummaries.Add($"{stage.Name}: search incomplete (candidate cap reached)");
                        }
                        return;
                }

                if (stage.Plan!.IsStrictRefinementOver(incumbentPlan))
                {
                    stageSummaries.Add(FormatStageSummary(stage.Name, stage.Plan));
                    incumbentPlan = stage.Plan;
                    finalName = stage.Name;
                    finalPlan = stage.Plan;
                }
                else
                {
                    stageSummaries.Add($"{FormatStageSummary(stage.Name, stage.Plan)}: no improvement");
                }
            }

            bool interrupted = false;
            try
            {
                builder.BuildFeasibleCompactPlan(CollectEdgeStage);
            }
            catch (OperationCanceledException)
            {
                // User pressed Ctrl+C mid-tightening. The stages collected so far are still valid and the
                // best plan found so far is the current finalPlan, so print that instead of losing all
                // output. The squeeze stays open (nothing was proven), so no proven-optimal claim.
                interrupted = true;
            }
            ClearProgressLine();

            if (interrupted)
                stageSummaries.Add("interrupted");

            Console.WriteLine($"progression: {string.Join(" -> ", stageSummaries)}");
            Console.WriteLine();
            string header = interrupted ? $"{finalName} ({FormatSqueeze(finalPlan)}) [interrupted]" : $"{finalName} ({FormatSqueeze(finalPlan)})";
            Console.WriteLine($"==================== {header} ====================");
            Console.WriteLine(interrupted
                ? "(best strategy found before interruption; not proven optimal)"
                : "(a valid strategy that achieves the upper bound; not proven optimal)");
            Console.Write(StrategyOverviewRenderer.RenderText(finalPlan));
            Console.WriteLine();
            Console.Write(StrategyTextRenderer.Render(finalPlan));
            return;
        }

        // Exact mode: no feasible phase. The exact search (exact) proves the optimum, then the
        // compact phase (compact) trims displayed edges among equally optimal groups. A Ctrl+C here has
        // no partial tree to show (the exact search is all-or-nothing), so report the interruption.
        StrategyPlan defaultPlan;
        try
        {
            defaultPlan = builder.BuildDefaultPlan();
        }
        catch (OperationCanceledException)
        {
            ClearProgressLine();
            Console.WriteLine("interrupted before the exact search proved an optimum (no result).");
            return;
        }
        ClearProgressLine();
        Console.WriteLine($"==================== exact ({FormatSqueeze(defaultPlan)}) ====================");
        Console.Write(StrategyOverviewRenderer.RenderText(defaultPlan));
        Console.WriteLine();
        Console.Write(StrategyTextRenderer.Render(defaultPlan));

        StrategyPlan incumbent = defaultPlan;
        StrategyPlan compactPlan;
        try
        {
            compactPlan = builder.BuildCompactPlan();
        }
        catch (OperationCanceledException)
        {
            // Interrupted while trimming edges; the proven-optimal exact tree above already printed.
            return;
        }
        bool compactImproved = compactPlan.IsStrictRefinementOver(incumbent);
        if (!compactImproved)
            return;

        ClearProgressLine();
        Console.WriteLine();
        Console.WriteLine("==================== compact ====================");
        Console.Write(StrategyOverviewRenderer.RenderText(compactPlan));
        Console.WriteLine();
        Console.Write(StrategyTextRenderer.Render(compactPlan));
    }

    // One-line descriptor for a single greedy stage in the progression summary: stage name plus its
    // worst-case steps and displayed edge count, e.g. "compact≤5(steps=5, edges=44)".
    private static string FormatStageSummary(string name, StrategyPlan plan)
        => $"{name}(steps={plan.MaxStep}, edges={plan.TotalBranchEdges})";

    // Squeeze on the optimum for a feasible plan: L is the proven analytic lower bound
    // (RootProvenLowerBound), U is the achieved feasible upper bound (MaxStep). When L == U the
    // greedy strategy is in fact optimal (a proven floor met by an achievable strategy). Worded in
    // "max steps" terms to match the rest of the output.
    private static string FormatSqueeze(StrategyPlan plan)
    {
        int lower = plan.SearchStatistics.RootProvenLowerBound;
        int upper = plan.MaxStep;
        if (lower > 0 && lower == upper)
            return $"max steps = {upper} (proven optimal)";

        string lowerText = lower > 0 ? lower.ToString() : "?";
        return $"{lowerText} <= max steps <= {upper}";
    }

    public static bool TryParseAndValidate(
        string? nText,
        string? mText,
        string? kText,
        out int n,
        out int m,
        out int k,
        out string? error)
    {
        n = 0;
        m = 0;
        k = 0;

        if (!int.TryParse(nText, out n))
        {
            error = "Error: n must be an integer";
            return false;
        }

        if (!int.TryParse(mText, out m))
        {
            error = "Error: m must be an integer";
            return false;
        }

        if (!int.TryParse(kText, out k))
        {
            error = "Error: k must be an integer";
            return false;
        }

        if (n <= 0)
        {
            error = "Error: n must be positive";
            return false;
        }

        if (n > 64)
        {
            error = "Error: n must be <= 64";
            return false;
        }

        if (k <= 0 || k > n)
        {
            error = "Error: k must satisfy 1 <= k <= n";
            return false;
        }

        if (m < 2)
        {
            error = "Error: m must be >= 2";
            return false;
        }

        if (m > n)
        {
            error = "Error: m must be <= n";
            return false;
        }

        error = null;
        return true;
    }

    // Heuristic guard for the GUI: the exact minimax search is exponential, so some
    // valid inputs take ten-plus seconds to minutes. This flags those (and only those)
    // so the GUI can warn before starting. Calibrated against measured Release run times
    // over a probe grid. Three observations drive the rule:
    //   1. A very small or very large top set (min(k, n-k) <= 2) keeps the combinatorial
    //      state space tiny, so it stays fast regardless of n (e.g. 18,2,2 ~ 0.2 s).
    //   2. When a single sort already isolates at least k items (k < m) the tree is shallow
    //      and the search is fast (e.g. 16,5,4 ~ 0.1 s).
    //   3. Among the remaining "hard shape" inputs, runtime is dominated by n and is worst
    //      for mid-range m: a large sort (m > n/3) makes the tree shallow and fast even at
    //      n=18 (18,7,7 ~ 1.4 s, 18,9,9 ~ 0.5 s), while mid-range m explodes
    //      (18,6,6 ~ 13 s, 18,4,4 ~ 20 s, 20,4,6 > 60 s). Every hard-shape input with n <= 17
    //      finished under ~8 s, while n >= 18 with m <= n/3 ran from ~13 s upward.
    // The rule below flags only that measured-slow region (roughly ten seconds or more),
    // intentionally staying conservative to avoid nuisance warnings on faster inputs.
    public const int SlowSearchSizeThreshold = 18;

    public static bool IsPotentiallySlowSearch(int n, int m, int k)
    {
        int boundaryItems = Math.Min(k, n - k);
        if (boundaryItems <= 2)
            return false;
        if (k < m)
            return false;
        if (m >= n)
            return false;

        return n >= SlowSearchSizeThreshold && m * 3 <= n;
    }
}
