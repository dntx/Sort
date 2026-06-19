# TopK Finder

Find the top-k elements from n numbers using **only** a sort operation that accepts at most m elements per call. The goal is to minimize the number of sort invocations.

## Problem

Given n numbers, find the largest k numbers (order among them doesn't matter). The only allowed operation is a `Sort(list)` function that:
- Accepts at most m numbers
- Returns them in sorted order

Minimize the total number of `Sort` calls.

## Algorithm

The current implementation uses a **transitivity-aware elimination strategy**.

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

Input array (10 elements): [668, 141, 126, 523, 169, 263, 724, 513, 174, 761]
Parameters: n=10, m=4, k=3

--- Finding top-3 elements ---
  Sort call #1: sorting 4 elements -> [#0=668, #3=523, #1=141, #2=126]
  Sort call #2: sorting 4 elements -> [#6=724, #7=513, #5=263, #4=169]
  Sort call #3: sorting 4 elements -> [#9=761, #0=668, #8=174, #1=141]
  Sort call #4: sorting 4 elements -> [#9=761, #6=724, #0=668, #3=523]

--- Results ---
Top-3 found: [761, 724, 668]
Correct: True
Total sort calls: 4
```

## Requirements

- .NET 8.0 SDK
