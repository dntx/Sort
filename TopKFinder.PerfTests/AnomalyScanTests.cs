using System.Numerics;
using System.Text;
using Xunit;

// On-demand automated anomaly scanner. It sweeps a grid of (n, m, k) inputs, builds the default
// and compact strategy plans, and runs a battery of cheap, tree-computable invariant and
// heuristic checks to surface possible bugs or unreasonable output (the kind of "this branch
// exploded into 300+ edges and should be ~19" smell that previously had to be found by hand).
//
// It is gated behind the RUN_ANOMALY_SCAN environment variable so it never runs in the normal
// suite. To run:
//   $env:RUN_ANOMALY_SCAN = "1"
//   dotnet test TopKFinder.PerfTests\TopKFinder.PerfTests.csproj --filter AnomalyScan
// Optional knobs: SCAN_NMAX (default 12), SCAN_CASE_TIMEOUT_SECONDS (default 25),
//   SCAN_REPORT_PATH (default <repo>\anomaly-report.txt).
//
// Known findings (n <= 12 sweep, recorded so re-runs are not surprising):
//   * Every rendered CountFormula evaluates exactly to its displayed Count across all 550 cases,
//     so the factorial / hook-length equivalent-forms notation is globally self-consistent.
//   * CompactEdgesIncreased (Review, not a hard bug) fires for (12,3,7) and (10,4,8): the compact
//     pass occasionally materializes MORE displayed edges than the default plan. Root cause: the
//     compact DP's edge proxy (StrategyBuilder.Compact.cs) sums each child subtree's edge cost
//     independently and does not model the order-dependent Reference de-duplication that the
//     materializer applies (TopKFinder.cs: a state seen a second time becomes a Reference leaf).
//     Minimizing the proxy therefore does not strictly minimize the rendered edge count. Impact is
//     limited because Program.RunHeadless only displays the compact plan when it is strictly better
//     than the default, so a worse compact tree is silently discarded rather than shown.
public sealed class AnomalyScanTests
{
    [Fact]
    public void AnomalyScan()
    {
        if (Environment.GetEnvironmentVariable("RUN_ANOMALY_SCAN") != "1")
            return; // skipped in normal runs

        int nMax = ReadIntEnv("SCAN_NMAX", 12);
        int caseTimeoutSeconds = ReadIntEnv("SCAN_CASE_TIMEOUT_SECONDS", 25);
        string reportPath = Environment.GetEnvironmentVariable("SCAN_REPORT_PATH")
            ?? Path.Combine(FindRepoRoot(), "anomaly-report.txt");

        var caseTimeout = TimeSpan.FromSeconds(caseTimeoutSeconds);
        var findings = new List<Finding>();
        var branchCounts = new List<NodeStat>();
        var caseSummaries = new List<string>();
        int scanned = 0;
        int skipped = 0;

        for (int n = 3; n <= nMax; n++)
        for (int m = 2; m <= n; m++)
        for (int k = 1; k <= n; k++)
        {
            if (m >= n && k >= n)
                continue; // trivial / nothing to compare
            if (Program.IsPotentiallySlowSearch(n, m, k))
            {
                skipped++;
                continue;
            }

            StrategyPlan? def;
            try
            {
                def = RunWithTimeout(caseTimeout, ct => new StrategyBuilder(n, m, k, ct).BuildDefaultPlan());
            }
            catch (TimeoutException)
            {
                skipped++;
                continue;
            }
            catch (Exception ex)
            {
                findings.Add(new Finding(Severity.Bug, n, m, k, "DefaultBuildThrew",
                    $"BuildDefaultPlan threw {ex.GetType().Name}: {ex.Message}"));
                continue;
            }

            scanned++;
            CheckPlan(def, "default", findings, branchCounts);

            StrategyPlan? compact = null;
            try
            {
                compact = RunWithTimeout(caseTimeout, ct => new StrategyBuilder(n, m, k, ct).BuildCompactPlan());
            }
            catch (TimeoutException)
            {
                // compact too slow; default checks still recorded
            }
            catch (Exception ex)
            {
                findings.Add(new Finding(Severity.Bug, n, m, k, "CompactBuildThrew",
                    $"BuildCompactPlan threw {ex.GetType().Name}: {ex.Message}"));
            }

            if (compact is not null)
            {
                CheckPlan(compact, "compact", findings, branchCounts);

                // Compact may never be worse than default: it must keep the same worst-case depth
                // and never increase the number of displayed edges.
                if (compact.MaxStep != def.MaxStep)
                    findings.Add(new Finding(Severity.Bug, n, m, k, "CompactDepthChanged",
                        $"compact MaxStep {compact.MaxStep} != default MaxStep {def.MaxStep}"));
                if (compact.TotalBranchEdges > def.TotalBranchEdges)
                    findings.Add(new Finding(Severity.Review, n, m, k, "CompactEdgesIncreased",
                        $"compact TotalBranchEdges {compact.TotalBranchEdges} > default {def.TotalBranchEdges} " +
                        "(compact pass minimizes edges yet produced more than default)"));
            }

            caseSummaries.Add(
                $"{n},{m},{k}: maxStep={def.MaxStep} edges={def.TotalBranchEdges}" +
                (compact is not null ? $" compactEdges={compact.TotalBranchEdges}" : " compact=skipped"));
        }

        WriteReport(reportPath, nMax, scanned, skipped, findings, branchCounts, caseSummaries);

        int bugs = findings.Count(f => f.Severity == Severity.Bug);
        Assert.True(bugs == 0,
            $"Anomaly scan found {bugs} hard invariant violation(s). See {reportPath}.");
    }

    private static void CheckPlan(StrategyPlan plan, string planLabel, List<Finding> findings, List<NodeStat> branchCounts)
    {
        var allStateIds = new HashSet<int>();
        Traverse(plan.Root, node =>
        {
            if (node.Kind != StrategyNodeKind.Reference)
                allStateIds.Add(node.StateId);
        });

        Traverse(plan.Root, node =>
        {
            switch (node.Kind)
            {
                case StrategyNodeKind.Reference:
                    if (!allStateIds.Contains(node.StateId))
                        findings.Add(new Finding(Severity.Bug, plan.N, plan.M, plan.K, "DanglingReference",
                            $"[{planLabel}] reference to state {node.StateId} not materialized in plan"));
                    break;

                case StrategyNodeKind.Terminal:
                    if (node.TopSet.Count != plan.K)
                        findings.Add(new Finding(Severity.Bug, plan.N, plan.M, plan.K, "TerminalTopSetSize",
                            $"[{planLabel}] terminal state {node.StateId} TopSet has {node.TopSet.Count} items, expected k={plan.K}"));
                    break;

                case StrategyNodeKind.Decision:
                    CheckDecision(plan, planLabel, node, findings, branchCounts);
                    break;
            }
        });
    }

    private static void CheckDecision(StrategyPlan plan, string planLabel, StrategyNode node, List<Finding> findings, List<NodeStat> branchCounts)
    {
        int g = node.Group.Count;
        int branchCount = node.Branches.Count;
        branchCounts.Add(new NodeStat(plan.N, plan.M, plan.K, planLabel, node.StateId, g, branchCount));

        // A sort of g items can reveal at most g! distinct orderings, so a decision node can never
        // have more outcome branches than g!.
        BigInteger gFact = Factorial(g);
        if (g >= 1 && branchCount > gFact)
            findings.Add(new Finding(Severity.Bug, plan.N, plan.M, plan.K, "BranchCountExceedsFactorial",
                $"[{planLabel}] state {node.StateId} sorts {g} items but has {branchCount} branches (> {g}! = {gFact})"));

        // Duplicate sibling outcomes should have been merged.
        var seenOrder = new HashSet<string>();
        var seenPattern = new HashSet<string>();
        foreach (StrategyBranch branch in node.Branches)
        {
            if (!seenOrder.Add(branch.OrderText))
                findings.Add(new Finding(Severity.Bug, plan.N, plan.M, plan.K, "DuplicateSiblingOrder",
                    $"[{planLabel}] state {node.StateId} has duplicate sibling OrderText '{branch.OrderText}'"));

            if (branch.EquivalentOrders is { } eq)
            {
                // Validate the rendered count formula actually evaluates to the displayed count.
                if (TryEvaluateFormula(eq.CountFormula, out BigInteger value))
                {
                    if (value != eq.Count)
                        findings.Add(new Finding(Severity.Bug, plan.N, plan.M, plan.K, "FormulaCountMismatch",
                            $"[{planLabel}] state {node.StateId} branch '{branch.OrderText}': formula '{eq.CountFormula}' = {value} but Count = {eq.Count}"));
                }
                else
                {
                    findings.Add(new Finding(Severity.Review, plan.N, plan.M, plan.K, "FormulaUnparsed",
                        $"[{planLabel}] state {node.StateId} branch '{branch.OrderText}': could not parse formula '{eq.CountFormula}'"));
                }

                if (eq.Count < 1)
                    findings.Add(new Finding(Severity.Bug, plan.N, plan.M, plan.K, "NonPositiveEquivalentCount",
                        $"[{planLabel}] state {node.StateId} branch '{branch.OrderText}' has Count {eq.Count}"));

                string patternKey = eq.PatternText + " ||| " + (eq.Legend ?? "");
                if (!seenPattern.Add(patternKey))
                    findings.Add(new Finding(Severity.Review, plan.N, plan.M, plan.K, "DuplicateSiblingPattern",
                        $"[{planLabel}] state {node.StateId} has duplicate sibling pattern '{eq.PatternText}' (legend '{eq.Legend}')"));
            }
        }

        if (node.FinalChoice is { } fc)
        {
            if (fc.RemainingSlots < 0)
                findings.Add(new Finding(Severity.Bug, plan.N, plan.M, plan.K, "NegativeRemainingSlots",
                    $"[{planLabel}] state {node.StateId} FinalChoice RemainingSlots {fc.RemainingSlots}"));
            if (fc.FixedTopSet.Count + fc.RemainingSlots != plan.K)
                findings.Add(new Finding(Severity.Bug, plan.N, plan.M, plan.K, "FinalChoiceSlotMath",
                    $"[{planLabel}] state {node.StateId} FinalChoice fixed({fc.FixedTopSet.Count}) + remaining({fc.RemainingSlots}) != k={plan.K}"));
            if (fc.RemainingSlots > fc.CandidatePool.Count)
                findings.Add(new Finding(Severity.Bug, plan.N, plan.M, plan.K, "FinalChoicePoolTooSmall",
                    $"[{planLabel}] state {node.StateId} FinalChoice remaining({fc.RemainingSlots}) > pool({fc.CandidatePool.Count})"));
        }
    }

    private static void Traverse(StrategyNode node, Action<StrategyNode> visit)
    {
        visit(node);
        foreach (StrategyBranch branch in node.Branches)
            Traverse(branch.Next, visit);
    }

    private static void WriteReport(string path, int nMax, int scanned, int skipped, List<Finding> findings, List<NodeStat> branchCounts, List<string> caseSummaries)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Anomaly scan report (nMax={nMax})");
        sb.AppendLine($"scanned cases: {scanned}, skipped (slow/timeout): {skipped}");
        sb.AppendLine();

        var bugs = findings.Where(f => f.Severity == Severity.Bug).ToList();
        var reviews = findings.Where(f => f.Severity == Severity.Review).ToList();

        sb.AppendLine($"## HARD invariant violations: {bugs.Count}");
        foreach (var group in bugs.GroupBy(f => f.Kind).OrderByDescending(gr => gr.Count()))
        {
            sb.AppendLine($"### {group.Key} ({group.Count()})");
            foreach (Finding f in group.Take(50))
                sb.AppendLine($"  ({f.N},{f.M},{f.K}) {f.Detail}");
        }
        sb.AppendLine();

        sb.AppendLine($"## Soft review items: {reviews.Count}");
        foreach (var group in reviews.GroupBy(f => f.Kind).OrderByDescending(gr => gr.Count()))
        {
            sb.AppendLine($"### {group.Key} ({group.Count()})");
            foreach (Finding f in group.Take(50))
                sb.AppendLine($"  ({f.N},{f.M},{f.K}) {f.Detail}");
        }
        sb.AppendLine();

        sb.AppendLine("## Top-30 decision nodes by branch count (branch-explosion smell)");
        foreach (NodeStat s in branchCounts.OrderByDescending(s => s.BranchCount).Take(30))
            sb.AppendLine($"  ({s.N},{s.M},{s.K}) [{s.PlanLabel}] state {s.StateId}: sorts {s.GroupSize} items -> {s.BranchCount} branches");
        sb.AppendLine();

        sb.AppendLine("## Per-case summary");
        foreach (string line in caseSummaries)
            sb.AppendLine("  " + line);

        File.WriteAllText(path, sb.ToString());
    }

    // ----- tolerant BigInteger formula evaluator -----
    // Handles the rendered count formulas: integer literals, postfix '!', '/', '+', and ' x ' as
    // multiplication, with parentheses. Word labels ("sym", "tail") are stripped first.
    private static bool TryEvaluateFormula(string formula, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (string.IsNullOrWhiteSpace(formula))
            return false;

        string expr = formula
            .Replace(" sym", " ")
            .Replace(" tail", " ")
            .Replace(" x ", " * ");

        // Reject anything with leftover letters (unexpected label) so it surfaces as "unparsed".
        if (expr.Any(char.IsLetter))
            return false;

        try
        {
            int pos = 0;
            value = ParseExpr(expr, ref pos);
            SkipWs(expr, ref pos);
            return pos == expr.Length;
        }
        catch
        {
            return false;
        }
    }

    private static BigInteger ParseExpr(string s, ref int pos)
    {
        BigInteger acc = ParseTerm(s, ref pos);
        while (true)
        {
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == '+')
            {
                pos++;
                acc += ParseTerm(s, ref pos);
            }
            else
            {
                return acc;
            }
        }
    }

    private static BigInteger ParseTerm(string s, ref int pos)
    {
        BigInteger acc = ParseFactor(s, ref pos);
        while (true)
        {
            SkipWs(s, ref pos);
            if (pos < s.Length && (s[pos] == '*' || s[pos] == '/'))
            {
                char op = s[pos];
                pos++;
                BigInteger rhs = ParseFactor(s, ref pos);
                if (op == '*')
                {
                    acc *= rhs;
                }
                else
                {
                    if (rhs == 0 || acc % rhs != 0)
                        throw new FormatException("non-exact division");
                    acc /= rhs;
                }
            }
            else
            {
                return acc;
            }
        }
    }

    private static BigInteger ParseFactor(string s, ref int pos)
    {
        SkipWs(s, ref pos);
        BigInteger val;
        if (pos < s.Length && s[pos] == '(')
        {
            pos++;
            val = ParseExpr(s, ref pos);
            SkipWs(s, ref pos);
            if (pos >= s.Length || s[pos] != ')')
                throw new FormatException("missing )");
            pos++;
        }
        else
        {
            int start = pos;
            while (pos < s.Length && char.IsDigit(s[pos]))
                pos++;
            if (pos == start)
                throw new FormatException("expected number");
            val = BigInteger.Parse(s.AsSpan(start, pos - start));
        }

        // postfix factorial(s)
        while (true)
        {
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == '!')
            {
                pos++;
                if (val < 0 || val > 10000)
                    throw new FormatException("factorial out of range");
                val = Factorial((int)val);
            }
            else
            {
                return val;
            }
        }
    }

    private static void SkipWs(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos]))
            pos++;
    }

    private static BigInteger Factorial(int n)
    {
        BigInteger r = BigInteger.One;
        for (int i = 2; i <= n; i++)
            r *= i;
        return r;
    }

    private static int ReadIntEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int v) && v > 0 ? v : fallback;
    }

    private static T RunWithTimeout<T>(TimeSpan timeout, Func<CancellationToken, T> action)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return action(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TopKFinder.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private enum Severity { Bug, Review }

    private sealed record Finding(Severity Severity, int N, int M, int K, string Kind, string Detail);

    private sealed record NodeStat(int N, int M, int K, string PlanLabel, int StateId, int GroupSize, int BranchCount);
}
