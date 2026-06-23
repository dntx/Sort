using System;
using System.Collections.Generic;
using System.Numerics;

// Measurement-only instrumentation (opt-in via EnableDominanceMetric).
//
// It quantifies how often a "dominance" / subsumption relation between the state currently being
// solved and an already-solved state would yield a useful bound on the worst-case step count:
//
//   * If an already-solved state a has a relation that EMBEDS INTO the current state (a carries no
//     more information than the current state), then cost(current) <= cost(a)  -> an UPPER bound.
//   * If the current state embeds into a (the current state carries no more information than a),
//     then cost(current) >= cost(a)  -> a LOWER bound.
//
// To keep the measurement provably sound we restrict to the clean monotone axis: the same number of
// active items and the same remaining slots, comparing only the known-relation edge sets under a
// verified bijection. Isomorphic states never reach this probe (they hit the exact cache), so every
// reported bound is genuinely BEYOND what the canonical-key cache already captures.
//
// The headline metric is DominanceExactDeterminations: cases where a lower and an upper bound
// coincide, pinning the exact cost so the state could have been resolved without any search.
partial class StrategyBuilder
{
    public bool EnableDominanceMetric { get; set; }

    private readonly List<DominanceEntry> _dominanceLibrary = new();

    private int _dominanceProbes;
    private int _dominanceLowerFound;
    private int _dominanceUpperFound;
    private int _dominanceLowerTightenings;
    private long _dominanceLowerSlack;
    private int _dominanceExactDeterminations;
    private int _dominanceBudgetExhaustions;
    private int _dominanceUnsoundObservations;

    public int DominanceProbes => _dominanceProbes;
    public int DominanceLowerFound => _dominanceLowerFound;
    public int DominanceUpperFound => _dominanceUpperFound;
    public int DominanceLowerTightenings => _dominanceLowerTightenings;
    public long DominanceLowerSlack => _dominanceLowerSlack;
    public int DominanceExactDeterminations => _dominanceExactDeterminations;
    public int DominanceBudgetExhaustions => _dominanceBudgetExhaustions;
    public int DominanceUnsoundObservations => _dominanceUnsoundObservations;
    public int DominanceLibrarySize => _dominanceLibrary.Count;

    private const int DominanceProbeBudget = 20000;

    private int _dominanceProbeBudgetRemaining;

    private readonly struct DominanceProbeResult
    {
        public DominanceProbeResult(bool hasLower, int lower, bool hasUpper, int upper)
        {
            HasLower = hasLower;
            Lower = lower;
            HasUpper = hasUpper;
            Upper = upper;
        }

        public bool HasLower { get; }
        public int Lower { get; }
        public bool HasUpper { get; }
        public int Upper { get; }
    }

    private sealed class DominanceEntry
    {
        public DominanceEntry(int remainingSlots, LocalRelation relation, int cost)
        {
            RemainingSlots = remainingSlots;
            Relation = relation;
            Cost = cost;
        }

        public int RemainingSlots { get; }
        public LocalRelation Relation { get; }
        public int Cost { get; }
    }

    // Active items relabeled to dense local indices [0, Count), with the greater-than relation as
    // per-item bitmasks plus cached ancestor/descendant degrees for cheap necessary-condition checks.
    private sealed class LocalRelation
    {
        public LocalRelation(int count, ulong[] greater, ulong[] less, int[] ancestorCount, int[] descendantCount, int edgeCount)
        {
            Count = count;
            Greater = greater;
            Less = less;
            AncestorCount = ancestorCount;
            DescendantCount = descendantCount;
            EdgeCount = edgeCount;
        }

        public int Count { get; }
        public ulong[] Greater { get; }
        public ulong[] Less { get; }
        public int[] AncestorCount { get; }
        public int[] DescendantCount { get; }
        public int EdgeCount { get; }
    }

    private LocalRelation BuildLocalRelation(ComparisonState state)
    {
        List<int> items = state.GetActiveItemsOrdered();
        int count = items.Count;
        var indexOf = new Dictionary<int, int>(count);
        for (int i = 0; i < count; i++)
            indexOf[items[i]] = i;

        var greater = new ulong[count];
        var less = new ulong[count];
        var ancestorCount = new int[count];
        var descendantCount = new int[count];
        int edgeCount = 0;

        for (int i = 0; i < count; i++)
        {
            ulong descendants = state.GetDescendantMask(items[i]) & state.ActiveMask;
            ulong d = descendants;
            while (d != 0)
            {
                int b = BitOperations.TrailingZeroCount(d);
                d &= d - 1;
                int j = indexOf[b];
                greater[i] |= 1UL << j;
                edgeCount++;
            }

            ulong ancestors = state.GetAncestorMask(items[i]) & state.ActiveMask;
            ulong a = ancestors;
            while (a != 0)
            {
                int b = BitOperations.TrailingZeroCount(a);
                a &= a - 1;
                int j = indexOf[b];
                less[i] |= 1UL << j;
            }

            ancestorCount[i] = BitOperations.PopCount(ancestors);
            descendantCount[i] = BitOperations.PopCount(descendants);
        }

        return new LocalRelation(count, greater, less, ancestorCount, descendantCount, edgeCount);
    }

    private void AddDominanceLibraryEntry(ComparisonState state, int remainingSlots, int cost)
    {
        if (state.ActiveCount <= _m || remainingSlots <= 0)
            return;

        _dominanceLibrary.Add(new DominanceEntry(remainingSlots, BuildLocalRelation(state), cost));
    }

    private DominanceProbeResult ProbeDominance(ComparisonState state, int remainingSlots)
    {
        LocalRelation current = BuildLocalRelation(state);
        bool hasLower = false;
        int lower = 0;
        bool hasUpper = false;
        int upper = 0;
        _dominanceProbeBudgetRemaining = DominanceProbeBudget;

        foreach (DominanceEntry entry in _dominanceLibrary)
        {
            if (entry.RemainingSlots != remainingSlots || entry.Relation.Count != current.Count)
                continue;

            LocalRelation other = entry.Relation;

            // Upper bound: the solved entry embeds into the current state (entry <= current in
            // information), so cost(current) <= entry.Cost.
            if (other.EdgeCount <= current.EdgeCount &&
                (!hasUpper || entry.Cost < upper) &&
                TryEmbedRelation(other, current))
            {
                hasUpper = true;
                upper = entry.Cost;
            }

            // Lower bound: the current state embeds into the solved entry (current <= entry in
            // information), so cost(current) >= entry.Cost.
            if (current.EdgeCount <= other.EdgeCount &&
                (!hasLower || entry.Cost > lower) &&
                TryEmbedRelation(current, other))
            {
                hasLower = true;
                lower = entry.Cost;
            }

            if (_dominanceProbeBudgetRemaining <= 0)
            {
                _dominanceBudgetExhaustions++;
                break;
            }
        }

        return new DominanceProbeResult(hasLower, lower, hasUpper, upper);
    }

    // Returns true if there is a bijection sigma from src items to dst items such that every
    // greater-than edge of src maps to a greater-than edge of dst (src's relation is contained in
    // dst's). Equal item counts make the injection a bijection. Degree necessary conditions prune the
    // search; budget exhaustion conservatively returns false (so the measurement under-counts).
    private bool TryEmbedRelation(LocalRelation src, LocalRelation dst)
    {
        int count = src.Count;
        if (count != dst.Count)
            return false;
        if (count == 0)
            return true;

        // Match harder-to-place (higher total degree) src vertices first.
        var order = new int[count];
        for (int i = 0; i < count; i++)
            order[i] = i;
        Array.Sort(order, (x, y) =>
            (src.AncestorCount[y] + src.DescendantCount[y]) - (src.AncestorCount[x] + src.DescendantCount[x]));

        var assign = new int[count];
        for (int i = 0; i < count; i++)
            assign[i] = -1;
        var used = new bool[count];

        return Backtrack(0);

        bool Backtrack(int depth)
        {
            if (_dominanceProbeBudgetRemaining-- <= 0)
                return false;
            if (depth == count)
                return true;

            int s = order[depth];
            for (int p = 0; p < count; p++)
            {
                if (used[p])
                    continue;
                if (dst.AncestorCount[p] < src.AncestorCount[s] || dst.DescendantCount[p] < src.DescendantCount[s])
                    continue;
                if (!IsConsistent(s, p))
                    continue;

                assign[s] = p;
                used[p] = true;
                if (Backtrack(depth + 1))
                    return true;
                used[p] = false;
                assign[s] = -1;
            }

            return false;
        }

        bool IsConsistent(int s, int p)
        {
            // For every already-assigned src vertex t, any src edge between s and t must be present
            // (in the same direction) between their dst images.
            ulong sGreater = src.Greater[s];
            ulong sLess = src.Less[s];
            ulong pGreater = dst.Greater[p];
            ulong pLess = dst.Less[p];

            for (int t = 0; t < count; t++)
            {
                int q = assign[t];
                if (q < 0)
                    continue;

                bool sOverT = (sGreater & (1UL << t)) != 0;
                if (sOverT && (pGreater & (1UL << q)) == 0)
                    return false;

                bool tOverS = (sLess & (1UL << t)) != 0;
                if (tOverS && (pLess & (1UL << q)) == 0)
                    return false;
            }

            return true;
        }
    }

    private void RecordDominanceProbe(DominanceProbeResult probe, int trueCost, ComparisonState state, int remainingSlots)
    {
        _dominanceProbes++;

        int analyticLowerBound = GetMinWorstCaseLowerBound(state, remainingSlots);

        if (probe.HasLower)
        {
            _dominanceLowerFound++;
            if (probe.Lower > trueCost)
                _dominanceUnsoundObservations++;
            if (probe.Lower > analyticLowerBound)
            {
                _dominanceLowerTightenings++;
                _dominanceLowerSlack += probe.Lower - analyticLowerBound;
            }
        }

        if (probe.HasUpper)
        {
            _dominanceUpperFound++;
            if (probe.Upper < trueCost)
                _dominanceUnsoundObservations++;
        }

        if (probe.HasLower && probe.HasUpper && probe.Lower == probe.Upper)
            _dominanceExactDeterminations++;
    }
}
