using System;
using System.Windows.Forms;

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 3)
        {
            if (!TryParseAndValidate(args[0], args[1], args[2], out int nFromArgs, out int mFromArgs, out int kFromArgs, out string? errorFromArgs))
            {
                Console.WriteLine(errorFromArgs);
                return;
            }

            Console.Write(StrategyTextRenderer.Render(StrategyBuilder.Generate(nFromArgs, mFromArgs, kFromArgs)));
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
        string? nText = Console.ReadLine();
        Console.Write("Enter m (max sort size): ");
        string? mText = Console.ReadLine();
        Console.Write("Enter k (top-k to find): ");
        string? kText = Console.ReadLine();

        if (!TryParseAndValidate(nText, mText, kText, out int n, out int m, out int k, out string? error))
        {
            Console.WriteLine(error);
            return;
        }

        Console.Write(StrategyTextRenderer.Render(StrategyBuilder.Generate(n, m, k)));
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
}
