using Xunit;

// Cap the number of test classes (collections) that xUnit runs concurrently.
//
// StrategyRegressionTests was split into 7 sibling classes so xUnit's class-level parallelism
// overlaps their previously-sequential work. A handful of those tests run genuinely ultra-heavy,
// allocation-bound solves (e.g. the 17,5,5 exact path ~1.04M outcomes, and the 10,2,4 compact
// build). Those ops are memory-bandwidth bound and do NOT speed up when overlapped -- run too many
// at once on a typical dev box and each individual op inflates past the per-operation
// RegressionTestTimeout (90s), even though it finishes comfortably with a couple of cores to
// itself. Capping the concurrent-class count keeps the parallel speed-up for the light majority
// while ensuring no single heavy solve is starved into a spurious timeout.
[assembly: CollectionBehavior(MaxParallelThreads = 3)]
