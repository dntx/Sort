using System;
using System.Windows.Forms;

class Program
{
    private const string UsageText = "Usage: TopKFinder <n> <m> <k> [--compact]";

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            if (!TryParseCliArgs(args, out string? nText, out string? mText, out string? kText, out bool compact, out string? argError))
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

            RunHeadless(nFromArgs, mFromArgs, kFromArgs, compact);
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

        RunHeadless(n, m, k, compact: false);
    }

    public static bool TryParseCliArgs(
        string[] args,
        out string? nText,
        out string? mText,
        out string? kText,
        out bool compact,
        out string? error)
    {
        nText = null;
        mText = null;
        kText = null;
        compact = false;
        error = null;

        var positionals = new System.Collections.Generic.List<string>();

        foreach (string arg in args)
        {
            if (arg == "--compact" || arg == "-c")
            {
                compact = true;
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

    private static void RunHeadless(int n, int m, int k, bool compact)
    {
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
            string line = $"searching... elapsed={snapshot.ElapsedMilliseconds / 1000.0:F1}s " +
                $"searched={snapshot.SearchedStates} pending={snapshot.PendingStates} output={snapshot.OutputStates}";
            Console.Error.Write("\r" + line.PadRight(lastLineLength));
            lastLineLength = line.Length;
        }

        StrategyPlan plan = compact
            ? StrategyBuilder.GenerateCompact(n, m, k, System.Threading.CancellationToken.None, ReportProgress)
            : StrategyBuilder.Generate(n, m, k, System.Threading.CancellationToken.None, ReportProgress);

        if (showProgress && lastLineLength > 0)
        {
            Console.Error.Write("\r" + new string(' ', lastLineLength) + "\r");
        }

        Console.Write(StrategyTextRenderer.Render(plan));
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
    // valid inputs take many seconds to minutes. This flags those so the GUI can warn
    // before starting. The score below was calibrated against measured run times:
    //   score = (n - m) * min(k, n-k) / m
    // Across a probe grid every configuration scoring < 11 finished in <= ~1.2 s, while
    // every configuration scoring >= 11 took >= ~1.4 s and grew quickly from there
    // (e.g. 16,5,5 ~ 8 s). Inputs that resolve a very small or very large top set
    // (min(k, n-k) <= 1) or where a single sort nearly covers everything (m close to n)
    // stay cheap regardless of n.
    public const double SlowSearchScoreThreshold = 11.0;

    public static bool IsPotentiallySlowSearch(int n, int m, int k)
    {
        int boundaryItems = Math.Min(k, n - k);
        if (boundaryItems <= 1)
            return false;
        if (m >= n)
            return false;

        double score = (double)(n - m) * boundaryItems / m;
        return score >= SlowSearchScoreThreshold;
    }
}
