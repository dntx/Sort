using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Finds the top-k elements from n numbers using only a sort operation
/// that accepts at most m numbers. Minimizes the number of sort calls by
/// leveraging transitivity: if A > B and B > C, then A > C without extra sorts.
/// 
/// An element is "eliminated" when it has >= k elements proven to be above it
/// (it cannot be in the top-k). The algorithm continues until only k candidates remain.
/// </summary>
class TopKFinder
{
    private readonly int _m;
    private int _sortCalls;

    // Transitive closure: ancestors[i] = set of all elements known > element i
    private HashSet<int>[]? _ancestors;
    // Transitive closure: descendants[i] = set of all elements known < element i
    private HashSet<int>[]? _descendants;

    public int SortCalls => _sortCalls;

    public TopKFinder(int m)
    {
        _m = m;
        _sortCalls = 0;
    }

    /// <summary>
    /// The only allowed operation: sort up to m elements, returns indices in descending value order.
    /// </summary>
    private List<int> SortOp(int[] values, List<int> indices)
    {
        if (indices.Count > _m)
            throw new InvalidOperationException($"Sort can accept at most {_m} elements, got {indices.Count}");
        if (indices.Count == 0)
            return new List<int>();

        _sortCalls++;
        var sorted = indices.OrderByDescending(i => values[i]).ToList();
        Console.WriteLine($"  Sort call #{_sortCalls}: sorting {indices.Count} elements -> " +
            $"[{string.Join(", ", sorted.Select(i => $"#{i}={values[i]}"))}]");
        return sorted;
    }

    /// <summary>
    /// Record that 'greater' > 'lesser' and propagate transitivity.
    /// </summary>
    private void AddRelation(int greater, int lesser)
    {
        if (_ancestors is null || _descendants is null)
            throw new InvalidOperationException("TopKFinder state has not been initialized.");

        if (_ancestors[lesser].Contains(greater))
            return; // already known

        // lesser gains greater and all of greater's ancestors
        var newAncestorsForLesser = new HashSet<int>(_ancestors[greater]) { greater };
        newAncestorsForLesser.ExceptWith(_ancestors[lesser]);

        if (newAncestorsForLesser.Count == 0) return;

        // All nodes that are at or below 'lesser' need these new ancestors
        var belowSet = new HashSet<int>(_descendants[lesser]) { lesser };
        foreach (int below in belowSet)
        {
            _ancestors[below].UnionWith(newAncestorsForLesser);
        }

        // All nodes that are at or above 'greater' gain 'lesser' and its descendants as descendants
        var newDescForGreater = new HashSet<int>(_descendants[lesser]) { lesser };
        newDescForGreater.ExceptWith(_descendants[greater]);

        if (newDescForGreater.Count == 0) return;

        var aboveSet = new HashSet<int>(_ancestors[greater]) { greater };
        foreach (int above in aboveSet)
        {
            _descendants[above].UnionWith(newDescForGreater);
        }
    }

    /// <summary>
    /// Find the top-k elements from the given values. Returns their indices.
    /// Uses transitivity-aware elimination to minimize sort calls.
    /// </summary>
    public List<int> FindTopK(int[] values, int k)
    {
        int n = values.Length;
        if (k >= n)
            return Enumerable.Range(0, n).ToList();

        // Initialize transitive closure structures
        _ancestors = new HashSet<int>[n];
        _descendants = new HashSet<int>[n];
        for (int i = 0; i < n; i++)
        {
            _ancestors[i] = new HashSet<int>();
            _descendants[i] = new HashSet<int>();
        }

        var active = new HashSet<int>(Enumerable.Range(0, n));

        // Fully adaptive: use strategic group selection from the start.
        // For elements with no known relationships yet, prioritize covering all elements.
        while (active.Count > k)
        {
            var group = ChooseGroup(active, k);
            var sorted = SortOp(values, group);

            for (int a = 0; a < sorted.Count - 1; a++)
                for (int b = a + 1; b < sorted.Count; b++)
                    AddRelation(sorted[a], sorted[b]);

            Eliminate(active, k);
        }

        return active.ToList();
    }

    /// <summary>
    /// Remove elements that have >= k ancestors (can't be in top-k).
    /// </summary>
    private void Eliminate(HashSet<int> active, int k)
    {
        if (_ancestors is null)
            throw new InvalidOperationException("TopKFinder state has not been initialized.");

        var toRemove = active.Where(i => _ancestors[i].Count >= k).ToList();
        foreach (var r in toRemove)
            active.Remove(r);
    }

    /// <summary>
    /// Choose the best group of m elements to sort next.
    /// Strategy:
    /// 1. If there are "unseen" elements (no relationships at all), prioritize covering them.
    /// 2. Otherwise, mix "strong" elements (many descendants) with "almost eliminated" elements
    ///    (ancestors.Count == k-1) to trigger eliminations via transitivity.
    /// </summary>
    private List<int> ChooseGroup(HashSet<int> active, int k)
    {
        if (_ancestors is null || _descendants is null)
            throw new InvalidOperationException("TopKFinder state has not been initialized.");

        var candidates = active.ToList();

        // Unseen elements: have no ancestors and no descendants (never been in a sort)
        var unseen = candidates
            .Where(i => _ancestors[i].Count == 0 && _descendants[i].Count == 0)
            .ToList();

        // If most elements are unseen, just cover them in groups
        if (unseen.Count >= _m)
            return unseen.Take(_m).ToList();

        // If some unseen remain, mix them with the strongest known leader
        if (unseen.Count > 0)
        {
            var earlyGroup = new List<int>();
            // Add a strong leader first
            var bestLeader = candidates
                .Where(i => !unseen.Contains(i))
                .OrderByDescending(i => _descendants[i].Count)
                .ThenBy(i => _ancestors[i].Count)
                .FirstOrDefault(-1);
            if (bestLeader >= 0)
                earlyGroup.Add(bestLeader);
            // Fill rest with unseen
            foreach (var u in unseen)
            {
                if (earlyGroup.Count >= _m) break;
                earlyGroup.Add(u);
            }
            // Fill any remaining slots with other candidates
            foreach (var c in candidates.Where(i => !earlyGroup.Contains(i))
                .OrderByDescending(i => _ancestors[i].Count))
            {
                if (earlyGroup.Count >= _m) break;
                earlyGroup.Add(c);
            }
            return earlyGroup.Take(_m).ToList();
        }

        // Almost eliminated: one more ancestor needed
        var almostEliminated = candidates
            .Where(i => _ancestors[i].Count == k - 1)
            .ToList();

        // Leaders: elements with most descendants (most proven wins)
        var leaders = candidates
            .OrderByDescending(i => _descendants[i].Count)
            .ThenBy(i => _ancestors[i].Count)
            .ToList();

        var group = new List<int>();
        var used = new HashSet<int>();

        // Pick a leader that can actually eliminate almost-eliminated elements
        foreach (var leader in leaders)
        {
            if (group.Count >= _m) break;
            bool canEliminate = almostEliminated.Any(ae =>
                !used.Contains(ae) && !_ancestors[ae].Contains(leader));
            if (canEliminate || group.Count == 0)
            {
                group.Add(leader);
                used.Add(leader);
            }
        }

        // Fill with almost-eliminated elements not already above'd by group members
        foreach (var ae in almostEliminated)
        {
            if (group.Count >= _m) break;
            if (used.Contains(ae)) continue;
            if (group.Any(g => !_ancestors[ae].Contains(g)))
            {
                group.Add(ae);
                used.Add(ae);
            }
        }

        // Fill remaining with unseen elements first (they need info)
        foreach (var u in unseen)
        {
            if (group.Count >= _m) break;
            if (!used.Contains(u))
            {
                group.Add(u);
                used.Add(u);
            }
        }

        // Then fill with elements closest to elimination
        var rest = candidates
            .Where(i => !used.Contains(i))
            .OrderByDescending(i => _ancestors[i].Count);

        foreach (var r in rest)
        {
            if (group.Count >= _m) break;
            group.Add(r);
            used.Add(r);
        }

        return group.Take(_m).ToList();
    }
}

class Program
{
    static void Main(string[] args)
    {
        Console.Write("Enter n (total numbers): ");
        int n = int.Parse(Console.ReadLine()!);
        Console.Write("Enter m (max sort size): ");
        int m = int.Parse(Console.ReadLine()!);
        Console.Write("Enter k (top-k to find): ");
        int k = int.Parse(Console.ReadLine()!);

        if (k > n) { Console.WriteLine("Error: k > n"); return; }
        if (m < 2) { Console.WriteLine("Error: m must be >= 2"); return; }

        // Generate random data
        var rand = new Random(42);
        int[] values = new int[n];
        for (int i = 0; i < n; i++)
            values[i] = rand.Next(1, 1000);

        Console.WriteLine($"\nInput array ({n} elements): [{string.Join(", ", values)}]");
        Console.WriteLine($"Parameters: n={n}, m={m}, k={k}");
        Console.WriteLine($"\n--- Finding top-{k} elements ---\n");

        var finder = new TopKFinder(m);
        var topKIndices = finder.FindTopK(values, k);

        // Verify correctness
        var topKValues = topKIndices.Select(i => values[i]).ToList();
        var actualTopK = values.OrderByDescending(x => x).Take(k).ToList();

        Console.WriteLine($"\n--- Results ---");
        Console.WriteLine($"Top-{k} found: [{string.Join(", ", topKValues.OrderByDescending(x => x))}]");
        Console.WriteLine($"Actual top-{k}: [{string.Join(", ", actualTopK)}]");
        Console.WriteLine($"Total sort calls: {finder.SortCalls}");

        // Verify: the multiset of top-k values should match
        bool correct = topKValues.OrderByDescending(x => x).SequenceEqual(actualTopK);
        Console.WriteLine($"Correct: {correct}");
    }
}
