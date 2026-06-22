# TopK Finder

Generate a **comparison strategy** for finding the top-k elements from n numbers when the only allowed operation is sorting at most m elements at a time.

## Problem

Given n numbers, find the largest k numbers (order among them doesn't matter). The only allowed operation is a `Sort(list)` function that:
- Accepts at most m numbers
- Returns them in sorted order

Instead of printing one concrete run on random data, the program now prints the **decision tree itself**:

- which indices to compare first
- what the possible sort results are
- which group should be compared next under each result
- and only one representative for branches that are symmetric up to relabeling

## Algorithm

The implementation uses a **transitivity-aware elimination strategy**.

When a sort returns:

```text
a > b > c
```

the program records all implied relations:

- `a > b`
- `a > c`
- `b > c`

and then keeps propagating them transitively. For example, if we already know `x > a`, then after sorting `[a, b, c]` we also learn `x > b` and `x > c` without paying for another sort.

An element is eliminated as soon as there are at least `k` elements proven to be larger than it, because it can no longer belong to the top-k set.

### Group selection heuristic

Each round, the program chooses up to `m` active candidates and prefers:

1. **Unseen elements**, so every sort adds new information.
2. **Strong leaders**, which already dominate many others.
3. **Almost eliminated elements**, which need just one more proven larger element to be removed.

This usually beats the earlier batch/tournament approach because it reuses previously learned order relations instead of restarting from scratch.

To print the strategy tree, the program symbolically enumerates all sort outcomes consistent with the currently known partial order, then recursively prints the next comparison for each branch.
Symmetric branches are merged by canonicalizing the comparison state, so equivalent cases are not repeated.

## Usage

```bash
dotnet run
```

Input (one per line):
```
n   # total number of elements
m   # max sort capacity
k   # how many top elements to find
```

### Example

```
$ echo "10
4
3" | dotnet run

n=10, m=4, k=3

比较方案：
状态 S1: 比较 #1, #2, #3, #4
  如果结果是 #1 > #2 > #3 > #4：
    ...
```

For small inputs such as `n=8, m=3, k=3`, the full strategy tree is readable. For larger inputs, the tree can grow quickly because each sort may have multiple feasible outcomes.

### Desktop UI

Running without redirected input opens the WinForms explorer. During a run it now shows live:

- searched / pending / output state counts
- the current best root worst-case bound as incumbents improve
- lower-bound pruning and cache-hit counters

After the run finishes, the details pane also includes a root-incumbent timeline so you can see when the search first found `x` steps, then improved to a smaller bound.

## Requirements

- .NET 8.0 SDK
