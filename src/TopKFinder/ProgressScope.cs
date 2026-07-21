namespace TopKFinder;

enum ProgressScope
{
    DefaultStandalone = 0,
    DefaultInCombinedRun = 1,
    CompactPrimaryInCombinedRun = 2,
    FeasibleInCombinedRun = 4,
    CompactFeasibleInCombinedRun = 8,
}