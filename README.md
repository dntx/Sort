# TopK Finder

Find the top-k elements from n numbers using **only** a sort operation that accepts at most m elements per call. The goal is to minimize the number of sort invocations.

## Problem

Given n numbers, find the largest k numbers (order among them doesn't matter). The only allowed operation is a `Sort(list)` function that:
- Accepts at most m numbers
- Returns them in sorted order

Minimize the total number of `Sort` calls.

## Algorithm

### Case 1: k < m (Recursive Tournament)

1. Divide candidates into groups of m
2. Sort each group, keep only the top-k from each (eliminating m−k per group)
3. Recurse on the reduced candidate set until ≤ m remain
4. One final sort yields the answer

**Complexity**: Each round eliminates a fraction of candidates. Converges in O(log<sub>m/k</sub>(n/m)) rounds.

### Case 2: k ≥ m (Iterative Batching)

1. Find top-(m−1) using the recursive tournament above
2. Remove those from the candidate pool
3. Repeat until k elements have been identified

**Complexity**: ⌈k/(m−1)⌉ iterations of the tournament approach.

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
  Sort call #3: sorting 4 elements -> [#6=724, #0=668, #3=523, #1=141]
  Sort call #4: sorting 4 elements -> [#9=761, #7=513, #5=263, #8=174]
  Sort call #5: sorting 4 elements -> [#9=761, #6=724, #0=668, #3=523]
  Sort call #6: sorting 4 elements -> [#9=761, #6=724, #0=668, #7=513]
  Sort call #7: sorting 4 elements -> [#9=761, #6=724, #0=668, #5=263]

--- Results ---
Top-3 found: [761, 724, 668]
Correct: True
Total sort calls: 7
```

## Requirements

- .NET 8.0 SDK
