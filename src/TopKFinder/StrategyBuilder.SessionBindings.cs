using System.Collections.Generic;

namespace TopKFinder;

partial class StrategyBuilder
{
    // Session-backed state/cache bindings are centralized here to keep core solver flow readable.
    private bool _useCompact { get => _session.Compact.UseCompact; set => _session.Compact.UseCompact = value; }
    private bool _compactUsesFeasibleBudget { get => _session.Compact.CompactUsesFeasibleBudget; set => _session.Compact.CompactUsesFeasibleBudget = value; }
    private bool _compactFeasibilityOnly { get => _session.Compact.CompactFeasibilityOnly; set => _session.Compact.CompactFeasibilityOnly = value; }
    private bool _compactEnumerationCapped { get => _session.Compact.CompactEnumerationCapped; set => _session.Compact.CompactEnumerationCapped = value; }
    private bool _lastProbeEnumerationCapped { get => _session.Compact.LastProbeEnumerationCapped; set => _session.Compact.LastProbeEnumerationCapped = value; }
    private bool EnableFeasibleTightening { get => _session.Compact.EnableFeasibleTightening; set => _session.Compact.EnableFeasibleTightening = value; }
    private int _feasibleRootBudgetActive { get => _session.Compact.FeasibleRootBudgetActive; set => _session.Compact.FeasibleRootBudgetActive = value; }
    private int _compactRootCost { get => _session.Compact.CompactRootCost; set => _session.Compact.CompactRootCost = value; }
    private bool _compactPatternCacheReadyForMaterialization { get => _session.Compact.CompactPatternCacheReadyForMaterialization; set => _session.Compact.CompactPatternCacheReadyForMaterialization = value; }

    private HashSet<SearchStateKey> _visitedSearchStates => _session.VisitedSearchStates;
    private Dictionary<SearchStateKey, int> _minWorstCaseStepsCache => _session.MinWorstCaseStepsCache;
    private Dictionary<SearchStateKey, int> _lowerBoundStepsCache => _session.LowerBoundStepsCache;
    // Iterative-deepening transposition memo: the best lower bound on a state's exact cost learned
    // from passes that failed to resolve it under their budget. Lets a later node/pass prune a state
    // immediately when this learned bound already exceeds the current budget.
    private Dictionary<SearchStateKey, int> _searchLowerBoundCache => _session.SearchLowerBoundCache;
    private Dictionary<SearchStateKey, FeasibleTopSetInfo> _feasibleTopSetCache => _session.FeasibleTopSetCache;
    private Dictionary<SearchStateKey, BestGroupPattern> _bestGroupPatternCache => _session.BestGroupPatternCache;
    private Dictionary<SearchStateKey, BestGroupPattern> _compactGroupPatternCache => _session.CompactGroupPatternCache;
    private Dictionary<SearchStateKey, int> _compactGroupPatternTightestBudget => _session.CompactGroupPatternTightestBudget;
    private Dictionary<(SearchStateKey Key, int Budget), int> _compactCostMemo => _session.CompactCostMemo;
    private Dictionary<SearchStateKey, int> _compactRealStepsMemo => _session.CompactRealStepsMemo;

    private List<SearchMilestone> _rootIncumbents => _session.RootIncumbents;

    private int _lowerBoundPrunes { get => _session.LowerBoundPrunes; set => _session.LowerBoundPrunes = value; }
    private int _duplicateOutcomeSkips { get => _session.DuplicateOutcomeSkips; set => _session.DuplicateOutcomeSkips = value; }
    private int _mergedOutcomeCollisions { get => _session.MergedOutcomeCollisions; set => _session.MergedOutcomeCollisions = value; }
    private int _exactCacheHits { get => _session.ExactCacheHits; set => _session.ExactCacheHits = value; }
    private int _lowerBoundCacheHits { get => _session.LowerBoundCacheHits; set => _session.LowerBoundCacheHits = value; }
    private int _feasibleTopSetCacheHits { get => _session.FeasibleTopSetCacheHits; set => _session.FeasibleTopSetCacheHits = value; }
    private int _bestGroupPatternCacheHits { get => _session.BestGroupPatternCacheHits; set => _session.BestGroupPatternCacheHits = value; }
    private int _outcomesConstructed { get => _session.OutcomesConstructed; set => _session.OutcomesConstructed = value; }
    private int _candidateGroupsEnumerated { get => _session.CandidateGroupsEnumerated; set => _session.CandidateGroupsEnumerated = value; }
    private int _compactStatesSolved { get => _session.CompactStatesSolved; set => _session.CompactStatesSolved = value; }
    private int _compactGroupsEnumerated { get => _session.CompactGroupsEnumerated; set => _session.CompactGroupsEnumerated = value; }
    private int _compactStepOptimalGroups { get => _session.CompactStepOptimalGroups; set => _session.CompactStepOptimalGroups = value; }
}
