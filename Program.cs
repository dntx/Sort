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
        "  TopKFinder <n> <m> <k>          Run two-phase search: print default, then compact if improved.\n" +
        "  ... | TopKFinder                Read n, m, k from stdin (one value per line).\n" +
        "\n" +
        "Options:\n" +
        "  -h, --help      Show this help and exit.\n" +
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

            if (!TryParseCliArgs(args, out string? nText, out string? mText, out string? kText, out string? argError))
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

            RunHeadless(nFromArgs, mFromArgs, kFromArgs);
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

        RunHeadless(n, m, k);
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
        out string? error)
    {
        nText = null;
        mText = null;
        kText = null;
        error = null;

        var positionals = new System.Collections.Generic.List<string>();

        foreach (string arg in args)
        {
            if (arg.StartsWith("-", StringComparison.Ordinal))
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

    private static void RunHeadless(int n, int m, int k)
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

        var builder = new StrategyBuilder(
            n,
            m,
            k,
            System.Threading.CancellationToken.None,
            ReportProgress,
            reportCombinedRunProgress: true);

        // Phase 0: greedy feasible strategy. Instant even on shapes the exact search never
        // resolves (e.g. 25,5,5), so it always gives the user a real strategy plus a squeeze
        // L <= opt <= U before the (possibly unbounded) exact search begins.
        StrategyPlan feasiblePlan = builder.BuildFeasiblePlan();
        ClearProgressLine();
        Console.WriteLine($"==================== feasible upper bound ({FormatSqueeze(feasiblePlan)}) ====================");
        Console.WriteLine("(a valid strategy that achieves the upper bound; not proven optimal)");
        Console.Write(StrategyOverviewRenderer.RenderText(feasiblePlan));
        Console.WriteLine();
        Console.Write(StrategyTextRenderer.Render(feasiblePlan));
        Console.WriteLine();

        StrategyPlan defaultPlan = builder.BuildDefaultPlan();
        ClearProgressLine();
        Console.WriteLine("==================== exact optimal ====================");
        Console.Write(StrategyOverviewRenderer.RenderText(defaultPlan));
        Console.WriteLine();
        Console.Write(StrategyTextRenderer.Render(defaultPlan));

        StrategyPlan compactPlan = builder.BuildCompactPlan();
        bool compactImproved =
            compactPlan.MaxStep == defaultPlan.MaxStep &&
            compactPlan.TotalBranchEdges < defaultPlan.TotalBranchEdges;
        if (!compactImproved)
            return;

        ClearProgressLine();
        Console.WriteLine();
        Console.WriteLine("==================== compact refinement ====================");
        Console.Write(StrategyOverviewRenderer.RenderText(compactPlan));
        Console.WriteLine();
        Console.Write(StrategyTextRenderer.Render(compactPlan));
    }

    // Squeeze on the optimum for a feasible plan: L is the proven analytic lower bound
    // (RootProvenLowerBound), U is the achieved feasible upper bound (MaxStep). When L == U the
    // greedy strategy is in fact optimal (a proven floor met by an achievable strategy).
    private static string FormatSqueeze(StrategyPlan plan)
    {
        int lower = plan.SearchStatistics.RootProvenLowerBound;
        int upper = plan.MaxStep;
        if (lower > 0 && lower == upper)
            return $"opt = {upper} (proven optimal)";

        string lowerText = lower > 0 ? lower.ToString() : "?";
        return $"{lowerText} <= opt <= {upper}";
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
