using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

/// <summary>
/// Benchmarking utility: Early-stage validation of the Stage 1 experiment.
/// This file was used during active development to test the hypothesis that optimizing for min-step
/// (instead of min-edge) with a cheap proxy (children.Count) could be effective. The experiment showed
/// that the three-phase architecture achieves 2.53x average speedup while preserving solution quality.
/// 
/// Status: Experimental development tool. Can be kept for reference or removed after merge.
/// Tests the hypothesis that using cheap proxy for sorting but optimizing steps (not edges) works well.
/// </summary>
class TestStage1MinStepGreedy
{
    public static void RunStage1Test()
    {
        Console.WriteLine("=== Stage 1: Min-Step Greedy with Cheap Proxy ===\n");
        
        var testCases = new[]
        {
            (n: 8, m: 3, k: 3),
            (n: 10, m: 4, k: 4),
            (n: 12, m: 4, k: 4),
            (n: 12, m: 5, k: 5),
            (n: 15, m: 5, k: 5),
            (n: 20, m: 5, k: 5),
        };

        Console.WriteLine($"{"Case",-12} {"Steps",-8} {"Edges",-8} {"Time(ms)",-10} {"Status"}");
        Console.WriteLine(new string('=', 60));

        var results = new List<(string testCase, int steps, int edges, long ms, string status)>();

        foreach (var (n, m, k) in testCases)
        {
            string testName = $"{n},{m},{k}";
            
            try
            {
                var cts = new CancellationTokenSource();
                var builder = new StrategyBuilder(n, m, k, cts.Token);
                
                // Enable tightening (old architecture behavior)
                // With new optimization objective (min-step)
                builder.EnableFeasibleTightening = true;
                
                var sw = Stopwatch.StartNew();
                var plan = builder.BuildFeasibleCompactPlan();
                sw.Stop();

                results.Add((testName, plan.MaxStep, plan.TotalBranchEdges, sw.ElapsedMilliseconds, "OK"));
                
                Console.WriteLine($"{testName,-12} {plan.MaxStep,-8} {plan.TotalBranchEdges,-8} " +
                                $"{sw.ElapsedMilliseconds,-10} OK");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{testName,-12} ERROR: {ex.Message}");
                results.Add((testName, -1, -1, 0, $"ERROR: {ex.Message}"));
            }
        }

        Console.WriteLine("\n" + new string('=', 60));
        Console.WriteLine("Summary:");
        Console.WriteLine($"  Successful: {results.FindAll(r => r.steps > 0).Count}/{results.Count}");
        
        var successfulResults = results.FindAll(r => r.steps > 0);
        if (successfulResults.Count > 0)
        {
            var avgTime = 0.0;
            foreach (var (_, _, _, ms, _) in successfulResults)
                avgTime += ms;
            avgTime /= successfulResults.Count;
            
            Console.WriteLine($"  Average time: {avgTime:F0}ms");
        }
    }
}
