using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

class ComparisonState
{
    public HashSet<int>[] Ancestors { get; }
    public HashSet<int>[] Descendants { get; }
    public HashSet<int> Active { get; }

    public ComparisonState(int n)
    {
        Ancestors = new HashSet<int>[n];
        Descendants = new HashSet<int>[n];
        for (int i = 0; i < n; i++)
        {
            Ancestors[i] = new HashSet<int>();
            Descendants[i] = new HashSet<int>();
        }

        Active = Enumerable.Range(0, n).ToHashSet();
    }

    private ComparisonState(HashSet<int>[] ancestors, HashSet<int>[] descendants, HashSet<int> active)
    {
        Ancestors = ancestors;
        Descendants = descendants;
        Active = active;
    }

    public ComparisonState Clone()
    {
        var ancestors = Ancestors.Select(set => new HashSet<int>(set)).ToArray();
        var descendants = Descendants.Select(set => new HashSet<int>(set)).ToArray();
        return new ComparisonState(ancestors, descendants, new HashSet<int>(Active));
    }

    public void AddRelation(int greater, int lesser)
    {
        if (Ancestors[lesser].Contains(greater))
            return;

        var newAncestorsForLesser = new HashSet<int>(Ancestors[greater]) { greater };
        newAncestorsForLesser.ExceptWith(Ancestors[lesser]);

        if (newAncestorsForLesser.Count > 0)
        {
            var belowSet = new HashSet<int>(Descendants[lesser]) { lesser };
            foreach (int below in belowSet)
                Ancestors[below].UnionWith(newAncestorsForLesser);
        }

        var newDescendantsForGreater = new HashSet<int>(Descendants[lesser]) { lesser };
        newDescendantsForGreater.ExceptWith(Descendants[greater]);

        if (newDescendantsForGreater.Count > 0)
        {
            var aboveSet = new HashSet<int>(Ancestors[greater]) { greater };
            foreach (int above in aboveSet)
                Descendants[above].UnionWith(newDescendantsForGreater);
        }
    }

    public void ApplyOrder(IReadOnlyList<int> sorted)
    {
        for (int i = 0; i < sorted.Count - 1; i++)
        {
            for (int j = i + 1; j < sorted.Count; j++)
                AddRelation(sorted[i], sorted[j]);
        }
    }

    public void Eliminate(int k)
    {
        var removed = Active.Where(i => Ancestors[i].Count >= k).ToList();
        foreach (int item in removed)
            Active.Remove(item);
    }

    public string GetKey()
    {
        var parts = new List<string>(Ancestors.Length + 1);
        parts.Add("A:" + string.Join(",", Active.OrderBy(x => x)));
        for (int i = 0; i < Ancestors.Length; i++)
            parts.Add($"{i}:{string.Join(",", Ancestors[i].OrderBy(x => x))}");
        return string.Join("|", parts);
    }

    public string GetCanonicalKey()
    {
        int n = Ancestors.Length;
        var colors = Enumerable.Range(0, n)
            .Select(i => Active.Contains(i) ? 1 : 0)
            .ToArray();

        bool changed;
        do
        {
            changed = false;
            var signatures = new string[n];
            for (int i = 0; i < n; i++)
            {
                string ancestors = string.Join(",", Ancestors[i].Select(a => colors[a]).OrderBy(x => x));
                string descendants = string.Join(",", Descendants[i].Select(d => colors[d]).OrderBy(x => x));
                signatures[i] = $"{colors[i]}|A:{ancestors}|D:{descendants}";
            }

            var signatureToColor = signatures
                .Distinct()
                .OrderBy(x => x, StringComparer.Ordinal)
                .Select((signature, index) => (signature, index))
                .ToDictionary(x => x.signature, x => x.index, StringComparer.Ordinal);

            var nextColors = signatures.Select(signature => signatureToColor[signature]).ToArray();
            changed = !colors.SequenceEqual(nextColors);
            colors = nextColors;
        }
        while (changed);

        var classIds = colors.Distinct().OrderBy(x => x).ToList();
        var parts = new List<string>();
        foreach (int classId in classIds)
        {
            var members = Enumerable.Range(0, n).Where(i => colors[i] == classId).ToList();
            int representative = members[0];
            string activeFlag = Active.Contains(representative) ? "1" : "0";

            var ancestorClassCounts = classIds
                .Select(otherClass => members
                    .Select(member => Ancestors[member].Count(a => colors[a] == otherClass))
                    .OrderBy(x => x))
                .ToList();

            var descendantClassCounts = classIds
                .Select(otherClass => members
                    .Select(member => Descendants[member].Count(d => colors[d] == otherClass))
                    .OrderBy(x => x))
                .ToList();

            parts.Add(
                $"C{classId}|N:{members.Count}|Active:{activeFlag}" +
                $"|Anc:{string.Join(";", ancestorClassCounts.Select(counts => string.Join(",", counts)))}" +
                $"|Desc:{string.Join(";", descendantClassCounts.Select(counts => string.Join(",", counts)))}");
        }

        return string.Join("||", parts);
    }
}

class StrategyPrinter
{
    private readonly int _n;
    private readonly int _m;
    private readonly int _k;
    private readonly TextWriter _writer;
    private readonly Dictionary<string, int> _stateIds = new();
    private readonly HashSet<string> _expandedStates = new();
    private int _nextStateId = 1;

    public StrategyPrinter(int n, int m, int k, TextWriter? writer = null)
    {
        _n = n;
        _m = m;
        _k = k;
        _writer = writer ?? Console.Out;
    }

    public void Print()
    {
        var initial = new ComparisonState(_n);
        _writer.WriteLine($"n={_n}, m={_m}, k={_k}");
        _writer.WriteLine();
        PrintState(initial, 0, 1);
    }

    public static string Generate(int n, int m, int k)
    {
        using var writer = new StringWriter();
        var printer = new StrategyPrinter(n, m, k, writer);
        printer.Print();
        return writer.ToString();
    }

    private string? GetInlineResult(ComparisonState state)
    {
        string key = state.GetCanonicalKey();
        int stateId = GetStateId(key);

        if (state.Active.Count <= _k)
            return $"S{stateId}: top {_k} = ({FormatSet(state.Active.OrderBy(x => x))})";

        if (_expandedStates.Contains(key))
            return $"→S{stateId}";

        return null;
    }

    private HashSet<int> GetGuaranteedTopSet(ComparisonState state)
    {
        return Enumerable.Range(0, _n)
            .Where(i => state.Active.Contains(i) && _n - 1 - state.Descendants[i].Count <= _k - 1)
            .ToHashSet();
    }

    private string FormatComparisonEffect(ComparisonState before, ComparisonState after)
    {
        var newlyGuaranteedTop = GetGuaranteedTopSet(after)
            .Except(GetGuaranteedTopSet(before))
            .OrderBy(x => x)
            .ToList();

        var newlyExcluded = before.Active
            .Except(after.Active)
            .OrderBy(x => x)
            .ToList();

        string candidates = $"({FormatSet(after.Active.OrderBy(x => x))})";
        return $"[in {FormatOptionalSet(newlyGuaranteedTop)}, out {FormatOptionalSet(newlyExcluded)}, cand {candidates}]";
    }

    private void PrintState(ComparisonState state, int indent, int step)
    {
        string key = state.GetCanonicalKey();
        int stateId = GetStateId(key);
        string prefix = new string(' ', indent * 2);

        if (state.Active.Count <= _k)
        {
            _writer.WriteLine($"{prefix}S{stateId}: top {_k} = ({FormatSet(state.Active.OrderBy(x => x))})");
            return;
        }

        if (_expandedStates.Contains(key))
        {
            _writer.WriteLine($"{prefix}→S{stateId}");
            return;
        }

        _expandedStates.Add(key);

        var group = ChooseGroup(state);
        _writer.WriteLine($"{prefix}S{stateId} [step {step}] sort({FormatSet(group)})");

        var groupedBranches = new SortedDictionary<string, BranchInfo>(StringComparer.Ordinal);

        foreach (var order in EnumerateFeasibleOrders(state, group))
        {
            var next = state.Clone();
            next.ApplyOrder(order);
            next.Eliminate(_k);

            string nextKey = next.GetCanonicalKey();
            if (!groupedBranches.TryGetValue(nextKey, out BranchInfo? branch))
            {
                branch = new BranchInfo(next, FormatOrder(order));
                groupedBranches[nextKey] = branch;
            }
        }

        foreach (var entry in groupedBranches.Values.OrderBy(v => v.RepresentativeOrder, StringComparer.Ordinal))
        {
            string effect = FormatComparisonEffect(state, entry.NextState);
            string? inline = GetInlineResult(entry.NextState);
            if (inline != null)
            {
                _writer.WriteLine($"{prefix}  {entry.RepresentativeOrder}: {effect} {inline}");
            }
            else
            {
                _writer.WriteLine($"{prefix}  {entry.RepresentativeOrder}: {effect}");
                PrintState(entry.NextState, indent + 2, step + 1);
            }
        }
    }

    private List<int> ChooseGroup(ComparisonState state)
    {
        var candidates = state.Active.OrderBy(x => x).ToList();
        int maxGroupSize = Math.Min(_m, candidates.Count);
        string currentKey = state.GetCanonicalKey();

        List<int>? bestGroup = null;
        (int minReduction, int negGroupSize, int distinctStates, int totalReduction, int unresolvedPairs) bestScore =
            (-1, int.MinValue, -1, -1, -1);

        for (int groupSize = 2; groupSize <= maxGroupSize; groupSize++)
        {
            foreach (var group in EnumerateCombinations(candidates, groupSize))
            {
                var nextStateKeys = new HashSet<string>(StringComparer.Ordinal);
                int minReduction = int.MaxValue;
                int totalReduction = 0;

                foreach (var order in EnumerateFeasibleOrders(state, group))
                {
                    var next = state.Clone();
                    next.ApplyOrder(order);
                    next.Eliminate(_k);

                    int reduction = state.Active.Count - next.Active.Count;
                    minReduction = Math.Min(minReduction, reduction);
                    totalReduction += reduction;
                    nextStateKeys.Add(next.GetCanonicalKey());
                }

                int unresolvedPairs = CountUnresolvedPairs(state, group);
                // Prefer smaller groups only when this comparison can finish the job;
                // otherwise prefer larger groups for more information gain.
                bool canFinish = state.Active.Count - minReduction <= _k;
                int negGroupSize = canFinish ? -group.Count : 0;
                var score = (minReduction, negGroupSize, nextStateKeys.Count, totalReduction, unresolvedPairs);

                bool isUseful = nextStateKeys.Any(key => key != currentKey);
                if (!isUseful)
                    continue;

                if (bestGroup is null || score.CompareTo(bestScore) > 0)
                {
                    bestGroup = group;
                    bestScore = score;
                }
            }
        }

        if (bestGroup is not null)
            return bestGroup;

        return candidates.Take(maxGroupSize).ToList();
    }

    private IEnumerable<List<int>> EnumerateFeasibleOrders(ComparisonState state, IReadOnlyList<int> group)
    {
        var remaining = new HashSet<int>(group);
        var current = new List<int>(group.Count);

        foreach (var order in EnumerateFeasibleOrders(state, remaining, current))
            yield return order;
    }

    private IEnumerable<List<int>> EnumerateFeasibleOrders(
        ComparisonState state,
        HashSet<int> remaining,
        List<int> current)
    {
        if (remaining.Count == 0)
        {
            yield return new List<int>(current);
            yield break;
        }

        var nextChoices = remaining
            .Where(candidate => remaining.All(other => other == candidate || !state.Ancestors[candidate].Contains(other)))
            .OrderBy(x => x)
            .ToList();

        foreach (int next in nextChoices)
        {
            remaining.Remove(next);
            current.Add(next);

            foreach (var order in EnumerateFeasibleOrders(state, remaining, current))
                yield return order;

            current.RemoveAt(current.Count - 1);
            remaining.Add(next);
        }
    }

    private static int CountUnresolvedPairs(ComparisonState state, IReadOnlyList<int> group)
    {
        int count = 0;
        for (int i = 0; i < group.Count - 1; i++)
        {
            for (int j = i + 1; j < group.Count; j++)
            {
                int a = group[i];
                int b = group[j];
                if (!state.Ancestors[a].Contains(b) && !state.Ancestors[b].Contains(a))
                    count++;
            }
        }

        return count;
    }

    private static IEnumerable<List<int>> EnumerateCombinations(IReadOnlyList<int> items, int count)
    {
        var current = new List<int>(count);
        foreach (var combination in EnumerateCombinations(items, count, 0, current))
            yield return combination;
    }

    private static IEnumerable<List<int>> EnumerateCombinations(
        IReadOnlyList<int> items,
        int count,
        int start,
        List<int> current)
    {
        if (current.Count == count)
        {
            yield return new List<int>(current);
            yield break;
        }

        for (int i = start; i <= items.Count - (count - current.Count); i++)
        {
            current.Add(items[i]);
            foreach (var combination in EnumerateCombinations(items, count, i + 1, current))
                yield return combination;
            current.RemoveAt(current.Count - 1);
        }
    }

    private int GetStateId(string key)
    {
        if (_stateIds.TryGetValue(key, out int id))
            return id;

        id = _nextStateId++;
        _stateIds[key] = id;
        return id;
    }

    private static string FormatSet(IEnumerable<int> items)
    {
        return string.Join(", ", items.Select(i => $"#{i + 1}"));
    }

    private static string FormatOptionalSet(IEnumerable<int> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? "-" : $"({FormatSet(list)})";
    }

    private static string FormatOrder(IEnumerable<int> items)
    {
        return string.Join(" > ", items.Select(i => $"#{i + 1}"));
    }

    private sealed class BranchInfo
    {
        public ComparisonState NextState { get; }
        public string RepresentativeOrder { get; }

        public BranchInfo(ComparisonState nextState, string representativeOrder)
        {
            NextState = nextState;
            RepresentativeOrder = representativeOrder;
        }
    }
}

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 3)
        {
            if (!TryParseAndValidate(args[0], args[1], args[2], out int nFromArgs, out int mFromArgs, out int kFromArgs, out string? errorFromArgs))
            {
                Console.WriteLine(errorFromArgs);
                return;
            }

            Console.Write(StrategyPrinter.Generate(nFromArgs, mFromArgs, kFromArgs));
            return;
        }

        if (!Console.IsInputRedirected)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            return;
        }

        Console.Write("Enter n (total numbers): ");
        string? nText = Console.ReadLine();
        Console.Write("Enter m (max sort size): ");
        string? mText = Console.ReadLine();
        Console.Write("Enter k (top-k to find): ");
        string? kText = Console.ReadLine();

        if (!TryParseAndValidate(nText, mText, kText, out int n, out int m, out int k, out string? error))
        {
            Console.WriteLine(error);
            return;
        }

        Console.Write(StrategyPrinter.Generate(n, m, k));
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

        error = null;
        return true;
    }
}
