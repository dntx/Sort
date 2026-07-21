using System;
using System.Windows.Forms;

namespace TopKFinder;

class Program
{
    private static readonly DisplayRenderEngine DisplayEngine = new();

    public enum Mode
    {
        Exact,
        Greedy,
    }

    private sealed class StageLimitReachedException : Exception
    {
    }

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
        "  --mode <mode>   Search mode. exact (proven) = exact + compact (proven optimal).\n" +
        "                  greedy (fast) = feasible bound, then min-step tightening, then one min-edge pass (interruptible with Ctrl+C).\n" +
        "  --stage <n>     Stop after stage n (1-based).\n" +
        "                  exact: 1=step-proof, 2=exact-edge-compact@S.\n" +
        "                  greedy: 1=greedy-feasible, 2+=continue along proof-tighten progression.\n" +
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

            if (!TryParseCliArgs(args, out string? nText, out string? mText, out string? kText, out Mode mode, out int? stageLimit, out string? argError))
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

            RunHeadless(nFromArgs, mFromArgs, kFromArgs, mode, stageLimit);
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

        RunHeadless(n, m, k, Mode.Exact, stageLimit: null);
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
        out Mode mode,
        out int? stageLimit,
        out string? error)
    {
        nText = null;
        mText = null;
        kText = null;
        mode = Mode.Exact;
        stageLimit = null;
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
                    mode = Mode.Greedy;
                else if (string.Equals(value, "exact", StringComparison.OrdinalIgnoreCase))
                    mode = Mode.Exact;
                else
                {
                    error = $"Error: unknown mode '{value}' (expected exact or greedy)";
                    return false;
                }
            }
            else if (arg == "--stage")
            {
                if (i + 1 >= args.Length)
                {
                    error = "Error: --stage requires a positive integer value";
                    return false;
                }

                string value = args[++i];
                if (!int.TryParse(value, out int parsed) || parsed <= 0)
                {
                    error = $"Error: invalid stage '{value}' (expected a positive integer)";
                    return false;
                }

                stageLimit = parsed;
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

    private static void RunHeadless(int n, int m, int k, Mode mode, int? stageLimit)
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

            if (lastEmitMs >= 0 && snapshot.ElapsedMilliseconds - lastEmitMs < 1000)
            {
                return;
            }

            lastEmitMs = snapshot.ElapsedMilliseconds;
            string progressText = $"{snapshot.EstimatedProgress01 * 100.0:F1}%";
            string etaText = snapshot.EstimatedProgress01 > 0.0
                ? $"{snapshot.ElapsedMilliseconds * (1.0 - snapshot.EstimatedProgress01) / snapshot.EstimatedProgress01 / 1000.0:F1}s"
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
            RunHeadlessCore(builder, mode, stageLimit, ClearProgressLine);
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

    private static void RunHeadlessCore(StrategyBuilder builder, Mode mode, int? stageLimit, Action clearProgressLine)
    {
        void ClearProgressLine() => clearProgressLine();

        // Live per-stage status goes to stderr (the same stream as the progress line) so stdout keeps
        // only the final progression summary and tree. Clearing the progress line first stops leftover
        // "searching..." text from mangling the status line.
        void WriteStageStatus(string text)
        {
            ClearProgressLine();
            Console.Error.WriteLine(text);
        }

        if (mode == Mode.Greedy)
        {
            // Greedy mode: GreedyFeasible gives a valid upper bound, then ProofTighten lowers the
            // step ceiling when it can, and EdgeCompact minimizes edges at the final step.
            WriteStageStatus("stage greedy-feasible: started");
            GreedyPreparationResult prep = PublicPipelineOrchestrator.RunGreedyPreparation(builder, emitStages: false);
            StrategyPlan feasiblePlan = prep.EffectiveFeasiblePlan;
            StrategyPlan baseFeasiblePlan = prep.BaseFeasiblePlan;
            WriteStageStatus($"stage greedy-feasible: steps={feasiblePlan.MaxStep}, " +
                $"edges={feasiblePlan.TotalBranchEdges} ({prep.GreedyFeasibleElapsed.TotalSeconds:F2}s)");

            // Optional GT pre-step (root-probe gated): only run single-round GreedyTighten when the
            // root micro-probe sees a possible root-height drop.
            bool gtProbeRun = prep.GreedyTightenProbeRun;
            StrategyPlan? gtPlan = prep.GreedyTightenPlan;
            bool gtImproved = prep.GreedyTightenImproved;
            if (gtProbeRun && gtPlan is not null)
            {
                WriteStageStatus("stage greedy-tighten: started (root probe passed)");
                WriteStageStatus($"stage greedy-tighten: steps={gtPlan.MaxStep}, " +
                    $"edges={gtPlan.TotalBranchEdges} ({prep.GreedyTightenElapsed.TotalSeconds:F2}s)");
            }

            // The anytime stages (each "proof-tighten≤N" tightening, a terminal no-solution ceiling,
            // and the final "greedy-edge-compact@S" stage are produced incrementally for the GUI. The CLI is a batch tool,
            // so printing every intermediate tree is just noise: collect the stages, then print a
            // one-line progression summary followed by only the final (best) tree. A stage that has a
            // solution but does not strictly improve the incumbent is flagged "no improvement" and never
            // becomes the final tree.
            var stageSummaries = new System.Collections.Generic.List<string>
            {
                FormatStageSummary(StageNames.GreedyFeasible, baseFeasiblePlan),
            };
            if (gtProbeRun)
            {
                if (gtPlan is not null && gtImproved)
                    stageSummaries.Add(FormatStageSummary(StageNames.GreedyTighten, gtPlan));
                else if (gtPlan is not null)
                    stageSummaries.Add($"{FormatStageSummary(StageNames.GreedyTighten, gtPlan)}: no improvement");
            }
            else
            {
                stageSummaries.Add($"{StageNames.GreedyTighten}: skipped (root probe)");
            }
            int emittedStages = 1 + (gtProbeRun ? 1 : 0);
            StrategyPlan incumbentPlan = baseFeasiblePlan;
            string finalName = StageNames.GreedyFeasible;
            StrategyPlan finalPlan = baseFeasiblePlan;

            if (gtPlan is not null && gtImproved)
            {
                incumbentPlan = gtPlan;
                finalName = StageNames.GreedyTighten;
                finalPlan = gtPlan;
            }

            if (PipelineStageProtocol.ReachedStageLimit(emittedStages, stageLimit))
            {
                ClearProgressLine();
                Console.WriteLine($"progression: {string.Join(" -> ", stageSummaries)}");
                Console.WriteLine();
                Console.WriteLine($"==================== {finalName} ({FormatSqueeze(finalPlan)}) ====================");
                Console.WriteLine("(a valid strategy that achieves the upper bound; not proven optimal)");
                Console.Write(DisplayEngine.RenderOverviewText(finalPlan));
                Console.WriteLine();
                Console.Write(DisplayEngine.RenderStrategyText(finalPlan));
                return;
            }

            void CollectEdgeStage(StageResult stage)
            {
                emittedStages++;

                if (!stage.HasPlan)
                {
                    string noSolutionMarker = PipelineStageProtocol.NoSolutionMarker(stage);
                    if (stage.Outcome == StageOutcome.ProvenInfeasible)
                        {
                            // Proven infeasible at this ceiling (complete enumeration) => the incumbent is
                            // optimal (opt = incumbent.MaxStep). Close its squeeze so the final tree reports
                            // proven optimal.
                            stageSummaries.Add($"{stage.Name}: {noSolutionMarker}");
                            WriteStageStatus($"stage {stage.Name}: {noSolutionMarker} " +
                                $"({stage.Elapsed.TotalSeconds:F2}s)");
                            finalPlan = finalPlan.WithRootProvenLowerBound(finalPlan.MaxStep);
                            incumbentPlan = finalPlan;
                        }
                        else
                        {
                            // Probe finished but the greedy candidate cap truncated the group enumeration, so
                            // "no group fit" is not a proof. Leave the squeeze open (no proven-optimal claim);
                            // the incumbent simply stands.
                            stageSummaries.Add($"{stage.Name}: {noSolutionMarker}");
                            WriteStageStatus($"stage {stage.Name}: search incomplete, candidate cap reached " +
                                $"({stage.Elapsed.TotalSeconds:F2}s)");
                        }

                        if (PipelineStageProtocol.ReachedStageLimit(emittedStages, stageLimit))
                            throw new StageLimitReachedException();

                        return;
                }

                if (stage.Plan!.IsStrictRefinementOver(incumbentPlan))
                {
                    stageSummaries.Add(FormatStageSummary(stage.Name, stage.Plan));
                    WriteStageStatus($"stage {stage.Name}: steps={stage.Plan.MaxStep}, " +
                        $"edges={stage.Plan.TotalBranchEdges} ({stage.Elapsed.TotalSeconds:F2}s)");
                    incumbentPlan = stage.Plan;
                    finalName = stage.Name;
                    finalPlan = stage.Plan;
                }
                else
                {
                    stageSummaries.Add($"{FormatStageSummary(stage.Name, stage.Plan)}: no improvement");
                    WriteStageStatus($"stage {stage.Name}: steps={stage.Plan.MaxStep}, " +
                        $"edges={stage.Plan.TotalBranchEdges} ({stage.Elapsed.TotalSeconds:F2}s), no improvement");
                }

                if (PipelineStageProtocol.ReachedStageLimit(emittedStages, stageLimit))
                    throw new StageLimitReachedException();
            }

            void StartEdgeStage(string name) => WriteStageStatus($"stage {name}: started");

            bool interrupted = false;
            bool stageLimited = false;
            try
            {
                PublicPipelineOrchestrator.RunGreedyPipeline(
                    builder,
                    CollectEdgeStage,
                    StartEdgeStage,
                    emitPreparationStages: false,
                    preparationAlreadyApplied: true);
            }
            catch (StageLimitReachedException)
            {
                stageLimited = true;
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
            else if (stageLimited)
                stageSummaries.Add("stage limit reached");

            Console.WriteLine($"progression: {string.Join(" -> ", stageSummaries)}");
            Console.WriteLine();
            string header = interrupted ? $"{finalName} ({FormatSqueeze(finalPlan)}) [interrupted]" : $"{finalName} ({FormatSqueeze(finalPlan)})";
            Console.WriteLine($"==================== {header} ====================");
            Console.WriteLine(interrupted
                ? "(best strategy found before interruption; not proven optimal)"
                : "(a valid strategy that achieves the upper bound; not proven optimal)");
            Console.Write(DisplayEngine.RenderOverviewText(finalPlan));
            Console.WriteLine();
            Console.Write(DisplayEngine.RenderStrategyText(finalPlan));
            return;
        }

        // Exact mode: no feasible phase. StepProof proves the optimum step, then EdgeCompact trims
        // displayed edges among equally optimal groups.
        StrategyPlan? defaultPlan = null;
        StrategyPlan? compactPlan = null;
        bool compactImproved = false;
        bool exactStageLimited = false;
        try
        {
            PublicPipelineOrchestrator.RunExactPipeline(
                builder,
                stage =>
                {
                    if (string.Equals(stage.Name, StageNames.StepProof, StringComparison.Ordinal))
                    {
                        StrategyPlan stepPlan = stage.Plan!;
                        defaultPlan = stepPlan;
                        WriteStageStatus($"stage step-proof: steps={stepPlan.MaxStep}, " +
                            $"edges={stepPlan.TotalBranchEdges} ({stage.Elapsed.TotalSeconds:F2}s)");
                        Console.WriteLine($"==================== step-proof ({FormatSqueeze(stepPlan)}) ====================");
                        Console.Write(DisplayEngine.RenderOverviewText(stepPlan));
                        Console.WriteLine();
                        Console.Write(DisplayEngine.RenderStrategyText(stepPlan));

                        if (PipelineStageProtocol.ReachedStageLimit(1, stageLimit))
                            throw new StageLimitReachedException();

                        return;
                    }

                    StrategyPlan exactCompact = stage.Plan!;
                    compactPlan = exactCompact;
                    compactImproved = defaultPlan is not null && exactCompact.IsStrictRefinementOver(defaultPlan);
                    if (!compactImproved)
                    {
                        WriteStageStatus($"stage {stage.Name}: steps={exactCompact.MaxStep}, " +
                            $"edges={exactCompact.TotalBranchEdges} ({stage.Elapsed.TotalSeconds:F2}s), no improvement");
                    }
                    else
                    {
                        WriteStageStatus($"stage {stage.Name}: steps={exactCompact.MaxStep}, " +
                            $"edges={exactCompact.TotalBranchEdges} ({stage.Elapsed.TotalSeconds:F2}s)");
                    }
                },
                name => WriteStageStatus($"stage {name}: started"));
        }
        catch (StageLimitReachedException)
        {
            exactStageLimited = true;
        }
        catch (OperationCanceledException)
        {
            if (defaultPlan is null)
            {
                ClearProgressLine();
                Console.WriteLine("interrupted before the exact search proved an optimum (no result).");
            }

            // Interrupted while trimming edges; the proven-optimal exact tree above already printed.
            return;
        }

        if (exactStageLimited)
            return;
        if (defaultPlan is null || compactPlan is null || !compactImproved)
            return;

        string edgeCompactStageName = StageNames.FormatExactEdgeCompact(defaultPlan.MaxStep);
        Console.WriteLine();
        Console.WriteLine($"==================== {edgeCompactStageName} ====================");
        Console.Write(DisplayEngine.RenderOverviewText(compactPlan));
        Console.WriteLine();
        Console.Write(DisplayEngine.RenderStrategyText(compactPlan));
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
        if (upper == 0)
            return "max steps = 0 (proven optimal)";
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

}
