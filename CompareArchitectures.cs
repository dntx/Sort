// Simple comparison of old (with tightening) vs new (three-phase) architectures
// Compile with: csc CompareArchitectures.cs TopKFinder.cs ... (or use dotnet)
// This should be called from tests or a main entry point

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

class CompareArchitectures
{
    public static void RunComparison()
    {
        var testCases = new[]
        {
            (n: 8, m: 3, k: 3),
            (n: 10, m: 4, k: 4),
            (n: 12, m: 4, k: 4),
            (n: 12, m: 5, k: 5),
            (n: 15, m: 5, k: 5),
            (n: 20, m: 5, k: 5)
        };

        Console.WriteLine("Comparing Old (tightening) vs New (three-phase) architectures");
        Console.WriteLine("==================================================================");
        Console.WriteLine($"{"Case",-12} {"OldSteps",-10} {"NewSteps",-10} {"OldEdges",-10} {"NewEdges",-10} {"OldTime(ms)",-12} {"NewTime(ms)",-12} {"Speedup",-8}");
        Console.WriteLine(new string('=', 104));

        var results = new List<(string testCase, int oldSteps, int newSteps, int oldEdges, int newEdges, long oldMs, long newMs)>();

        foreach (var (n, m, k) in testCases)
        {
            string testName = $"{n},{m},{k}";
            
            // Test old architecture (with tightening)
            var cts1 = new CancellationTokenSource();
            var builder1 = new StrategyBuilder(n, m, k, cts1.Token);
            var sw1 = Stopwatch.StartNew();
            var oldPlan = builder1.BuildFeasibleCompactPlan();
            sw1.Stop();
            
            // Test new architecture (three-phase)
            var cts2 = new CancellationTokenSource();
            var builder2 = new StrategyBuilder(n, m, k, cts2.Token);
            var sw2 = Stopwatch.StartNew();
            var newPlan = builder2.BuildThreePhasePlan();
            sw2.Stop();

            results.Add((testName, oldPlan.MaxStep, newPlan.MaxStep, 
                        oldPlan.TotalBranchEdges, newPlan.TotalBranchEdges,
                        sw1.ElapsedMilliseconds, sw2.ElapsedMilliseconds));
            
            double speedup = oldPlan.MaxStep == newPlan.MaxStep && newPlan.MaxStep > 0
                ? (double)sw1.ElapsedMilliseconds / (double)sw2.ElapsedMilliseconds
                : 0;
            
            Console.WriteLine($"{testName,-12} {oldPlan.MaxStep,-10} {newPlan.MaxStep,-10} " +
                            $"{oldPlan.TotalBranchEdges,-10} {newPlan.TotalBranchEdges,-10} " +
                            $"{sw1.ElapsedMilliseconds,-12} {sw2.ElapsedMilliseconds,-12} " +
                            $"{speedup:F2}x");
        }

        Console.WriteLine();
        Console.WriteLine("Summary:");
        int betterSteps = 0, sameSteps = 0, worseSteps = 0;
        double totalSpeedup = 0;
        int speedupCount = 0;

        foreach (var (_, oldSteps, newSteps, _, _, oldMs, newMs) in results)
        {
            if (newSteps < oldSteps) betterSteps++;
            else if (newSteps == oldSteps) sameSteps++;
            else worseSteps++;
            
            if (oldSteps == newSteps && oldSteps > 0)
            {
                totalSpeedup += (double)oldMs / (double)newMs;
                speedupCount++;
            }
        }

        Console.WriteLine($"  Better steps: {betterSteps}, Same: {sameSteps}, Worse: {worseSteps}");
        if (speedupCount > 0)
            Console.WriteLine($"  Average speedup (when steps equal): {totalSpeedup / speedupCount:F2}x");
    }
}
