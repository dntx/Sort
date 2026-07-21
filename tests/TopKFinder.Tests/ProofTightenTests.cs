using System;
using System.Diagnostics;
using Xunit;

namespace TopKFinder.Tests
{
    /// <summary>
    /// Test coverage for greedy mode (min-step objective).
    ///
    /// Greedy mode is feasibility-first:
    /// - Establish a feasible upper bound U via the constructive greedy plan.
    /// - Tighten the max-step with feasibility-only compact probes (feasible≤U-1, feasible≤U-2, …)
    ///   until a ceiling proves infeasible or the candidate cap truncates enumeration; this yields the
    ///   smallest feasible step S found.
    /// - Run one min-edge compact pass at S to minimize edges without changing the step count.
    ///
    /// The pass is anytime/interruptible: cancelling still surfaces the best (smallest max-step) plan
    /// found so far, and the solution is never worse than the greedy baseline.
    /// </summary>
    public class ProofTightenTests
    {
        /// <summary>
        /// Validates that greedy mode produces feasible solutions with positive steps and non-negative edges.
        /// </summary>
        [Theory]
        [InlineData(8, 3, 3)]
        [InlineData(10, 4, 4)]
        [InlineData(12, 4, 4)]
        [InlineData(12, 5, 5)]
        [InlineData(15, 5, 5)]
        public void Greedy_ProducesFeasibleSolutions(int n, int m, int k)
        {
            var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(300));
            var builder = new StrategyBuilder(n, m, k, cts.Token);
            
            var plan = builder.RunGreedyPipeline();
            
            Assert.NotNull(plan);
            Assert.True(plan.MaxStep > 0, $"Solution for ({n},{m},{k}) should have positive steps");
            Assert.True(plan.TotalBranchEdges >= 0, $"Solution for ({n},{m},{k}) should have non-negative edges");
        }

        /// <summary>
        /// Verifies greedy mode respects cancellation/timeout settings.
        /// This ensures the algorithm doesn't hang or consume infinite resources.
        /// </summary>
        [Theory]
        [InlineData(8, 3, 3)]
        [InlineData(10, 4, 4)]
        public void Greedy_RespectsTimeout(int n, int m, int k)
        {
            var timeout = TimeSpan.FromSeconds(60);
            var sw = Stopwatch.StartNew();
            var cts = new System.Threading.CancellationTokenSource(timeout);
            
            var builder = new StrategyBuilder(n, m, k, cts.Token);
            var plan = builder.RunGreedyPipeline();
            
            sw.Stop();
            Assert.True(sw.Elapsed <= timeout.Add(TimeSpan.FromSeconds(5)), 
                $"Solution for ({n},{m},{k}) should complete within timeout (took {sw.ElapsedMilliseconds}ms)");
        }

        /// <summary>
        /// Basic sanity check for the greedy build pipeline.
        /// Verifies that RunGreedyPipeline completes end-to-end.
        /// </summary>
        [Fact]
        public void Greedy_BuildPipelineCompletes()
        {
            var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
            var builder = new StrategyBuilder(8, 3, 3, cts.Token);
            
            // Main entry point for greedy compact solving.
            var plan = builder.RunGreedyPipeline();
            
            Assert.NotNull(plan);
            Assert.Equal(8, plan.N);
            Assert.Equal(3, plan.M);
            Assert.Equal(3, plan.RequestedK);
            Assert.Equal(3, plan.K);
            Assert.NotNull(plan.Root);
        }
    }
}
