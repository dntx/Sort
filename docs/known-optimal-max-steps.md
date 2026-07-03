# 已证明的最优 max steps 速查表（known-optimal reference）

本表记录一批 `(n, m, k)` 输入在 **exact 模式**下已证明的最优最坏步数（`max steps`，即
`StrategyPlan.MaxStep`）。这些值都来自 exact / 精确搜索给出的 **proven optimal** 结果，可作为
调研 greedy 上界紧度、回归基线、算法验证时的“标准答案”，无需再花时间重跑 exact。

## 使用说明

- **max steps** = 一个已证明最优的完整策略在最坏对手分支下的排序次数（sort 操作数）。
- **运行时间** = 该规模在 `--mode exact` 下实测的 wall-clock 耗时（本机 arm64 单次测量，仅供量级参考，
  与硬件强相关）。标 `待测` 表示该行的值已证明最优、但尚未在本机重新计时。
- **对偶对称**：`opt(n, m, k) == opt(n, m, n−k)`。求解器内部会把 `k > n/2` 自动折算为
  `k' = n−k`（见 `Program.RunHeadless` 的 `canonicalK`），所以本表里凡是 `k > n/2` 的行，其值与
  对应的 `n−k` 行一致。
- **来源**：
  - `tests`：来自 `TopKFinder.Tests/StrategyRegressionTests.cs` 的 `InlineData`（`BuildDefaultPlan`，
    已 pin 为回归基线）。
  - `exact-run`：通过 `TopKFinder <n> <m> <k> --mode exact` 实测，输出为
    `max steps = N (proven optimal)`。
- **复现单条**：
  ```
  TopKFinder <n> <m> <k> --mode exact
  ```
  头部若显示 `max steps = N (proven optimal)` 即为已证明最优；若显示 `L <= max steps <= U`
  则说明该规模 exact 未跑完，不能入表。

## 数据表

| n | m | k | max steps | 运行时间 | 来源 |
|---:|---:|---:|---:|---:|:--|
| 5 | 3 | 2 | 3 | 待测 | tests, exact-run |
| 6 | 2 | 2 | 7 | 待测 | tests |
| 6 | 3 | 2 | 3 | 待测 | exact-run |
| 6 | 3 | 3 | 3 | 待测 | exact-run |
| 7 | 3 | 3 | 4 | 待测 | exact-run |
| 8 | 2 | 3 | 10 | 待测 | tests |
| 8 | 3 | 3 | 5 | 待测 | exact-run |
| 8 | 3 | 4 | 5 | 待测 | tests |
| 8 | 4 | 2 | 3 | 待测 | tests |
| 8 | 4 | 3 | 3 | 待测 | exact-run |
| 9 | 3 | 3 | 6 | 待测 | tests, exact-run |
| 9 | 3 | 4 | 6 | 待测 | tests |
| 9 | 4 | 3 | 4 | 待测 | tests, exact-run |
| 9 | 5 | 4 | 3 | 待测 | exact-run |
| 10 | 3 | 3 | 6 | 待测 | exact-run |
| 10 | 3 | 5 | 6 | 待测 | tests |
| 10 | 3 | 6 | 7 | 待测 | tests |
| 10 | 4 | 3 | 4 | 待测 | exact-run |
| 10 | 4 | 4 | 4 | 待测 | exact-run |
| 10 | 4 | 5 | 5 | 待测 | exact-run |
| 10 | 5 | 4 | 3 | 待测 | exact-run |
| 11 | 3 | 3 | 7 | 待测 | exact-run |
| 11 | 4 | 4 | 5 | 待测 | exact-run |
| 11 | 5 | 4 | 4 | 待测 | exact-run |
| 11 | 5 | 5 | 4 | 待测 | exact-run |
| 12 | 3 | 3 | 7 | 待测 | tests, exact-run |
| 12 | 3 | 4 | 8 | 待测 | exact-run |
| 12 | 3 | 5 | 8 | 待测 | exact-run |
| 12 | 4 | 3 | 5 | 待测 | tests |
| 12 | 4 | 4 | 5 | 待测 | exact-run |
| 12 | 4 | 5 | 6 | 待测 | tests |
| 12 | 5 | 5 | 4 | 待测 | exact-run |
| 12 | 6 | 6 | 3 | 待测 | tests, exact-run |
| 13 | 4 | 4 | 6 | 待测 | exact-run |
| 13 | 4 | 5 | 6 | 待测 | exact-run |
| 13 | 5 | 5 | 5 | 待测 | exact-run |
| 14 | 4 | 4 | 6 | 待测 | exact-run |
| 14 | 4 | 5 | 6 | 待测 | exact-run |
| 14 | 4 | 6 | 7 | 待测 | exact-run |
| 14 | 5 | 5 | 5 | 待测 | tests, exact-run |
| 14 | 6 | 6 | 4 | 待测 | tests |
| 15 | 4 | 4 | 7 | 待测 | exact-run |
| 15 | 4 | 5 | 7 | 待测 | exact-run |
| 15 | 4 | 6 | 7 | 待测 | exact-run |
| 15 | 5 | 5 | 5 | 待测 | exact-run |
| 15 | 5 | 6 | 5 | 待测 | exact-run |
| 16 | 4 | 4 | 7 | 待测 | exact-run |
| 16 | 4 | 6 | 7 | 待测 | exact-run |
| 16 | 5 | 5 | 6 | 待测 | tests, exact-run |
| 16 | 5 | 6 | 6 | 待测 | exact-run |
| 17 | 5 | 5 | 6 | 待测 | tests |
| 18 | 5 | 5 | 6 | 待测 | tests |
| 20 | 5 | 5 | 7 | 38.4s | exact-run |
| 25 | 5 | 3 | 7 | 待测 | tests |

## 维护

- 新增行请只填 **exact 已证明最优** 的值（头部 `proven optimal`），并注明来源。
- **运行时间** 栏填本机 `--mode exact` 实测耗时（如 `38.4s`），未计时的行填 `待测`。该栏与硬件相关，
  仅供量级参考，不作为回归断言。
- 若某行同时被 `StrategyRegressionTests.cs` pin，请优先标 `tests`（那是会被 CI 校验的真源）。
- 25,5,5 等前沿规模 exact 尚无法在合理时间内证明最优，故不在本表（20,5,5 已于本机 exact 证明 opt=7，
  38.4s，已入表）。
