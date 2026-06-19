using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Finds the top-k elements from n numbers using only a sort operation
/// that accepts at most m numbers. Minimizes the number of sort calls.
/// </summary>
class TopKFinder
{
    private readonly int _m;
    private int _sortCalls;

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
    /// Find the top-k elements from the given values. Returns their indices.
    /// </summary>
    public List<int> FindTopK(int[] values, int k)
    {
        int n = values.Length;
        if (k >= n)
            return Enumerable.Range(0, n).ToList();

        var allIndices = Enumerable.Range(0, n).ToList();

        if (k < _m)
            return FindTopKSmall(values, allIndices, k);
        else
            return FindTopKLarge(values, allIndices, k);
    }

    /// <summary>
    /// Recursive tournament: finds top-k from candidates where k < m.
    /// Each sort eliminates (m - k) >= 1 elements, guaranteeing convergence.
    /// </summary>
    private List<int> FindTopKSmall(int[] values, List<int> candidates, int k)
    {
        if (candidates.Count <= k)
            return new List<int>(candidates);

        if (candidates.Count <= _m)
        {
            var sorted = SortOp(values, candidates);
            return sorted.Take(k).ToList();
        }

        // k < m guaranteed here, so each full group eliminates (m - k) >= 1 element
        var nextCandidates = new List<int>();
        for (int i = 0; i < candidates.Count; i += _m)
        {
            int groupSize = Math.Min(_m, candidates.Count - i);
            var group = candidates.GetRange(i, groupSize);

            if (groupSize <= k)
            {
                nextCandidates.AddRange(group);
            }
            else
            {
                var sorted = SortOp(values, group);
                nextCandidates.AddRange(sorted.Take(k));
            }
        }

        return FindTopKSmall(values, nextCandidates, k);
    }

    /// <summary>
    /// Case k >= m: Find top-(m-1) repeatedly and accumulate results.
    /// </summary>
    private List<int> FindTopKLarge(int[] values, List<int> candidates, int k)
    {
        var result = new List<int>();
        var remaining = new List<int>(candidates);
        int batchSize = _m - 1; // ensures k < m for recursive calls

        while (result.Count < k)
        {
            int needed = Math.Min(batchSize, k - result.Count);

            if (remaining.Count <= needed)
            {
                result.AddRange(remaining);
                break;
            }

            var topBatch = FindTopKSmall(values, remaining, needed);
            result.AddRange(topBatch);

            var foundSet = new HashSet<int>(topBatch);
            remaining = remaining.Where(i => !foundSet.Contains(i)).ToList();
        }

        return result;
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

        // Print theoretical analysis
        Console.WriteLine($"\n--- Complexity Analysis ---");
        if (k <= m && k < m)
        {
            int rounds = 0;
            int candidates = n;
            int totalSorts = 0;
            while (candidates > m)
            {
                int groups = (candidates + m - 1) / m;
                totalSorts += groups;
                candidates = Math.Min(k * groups, candidates - 1);
                rounds++;
                if (rounds > 100) break;
            }
            totalSorts++; // final sort
            Console.WriteLine($"Estimated sorts (k<m recursive): ~{totalSorts} sorts in {rounds + 1} rounds");
        }
        else
        {
            int batches = (k + m - 1) / m;
            Console.WriteLine($"Strategy: {batches} batches of finding top-{Math.Min(m, k)}");
        }
    }
}
