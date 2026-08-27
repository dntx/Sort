# Core Algorithm: Optimal Top-k Strategy Search

本文用中文系统地讲解本项目核心搜索算法的原理，包括问题定义、状态表示、归一化、极小化最坏步数的
minimax 搜索、对称性约减，以及三种剪枝下界（信息论下界、反链宽度下界、支配下界）。其中反链宽度
下界与 Dilworth 定理部分配有具体例子，面向没有相关理论背景的读者。

> 本文聚焦**搜索与剪枝**（决定策略树的形状与最优性）。输出层「如何把每条分支渲染成易读的
> `pattern: ...`」是一块正交的逻辑，单独见 [`output-rendering.md`](./output-rendering.md)。

相关文档：

- [`output-rendering.md`](./output-rendering.md)：分支等价折叠与 `pattern:` 渲染规则（输出层）。
- [`test-strategy.md`](./test-strategy.md)：回归基线、性能护栏、手动 perf gate 工作流与测试方法。
- [`ui-explorer.md`](./ui-explorer.md)：桌面 UI 阶段时间线、占位与取消语义。
- `.github/workflows/nightly-proof-tighten-gate.yml` 与 `.github/workflows/nightly-full-strategy-matrix.yml`：夜间巡检入口。

架构分层（简版）：

- `StrategyBuilder`：搜索与剪枝核心，生成 `SolvedStrategy` / `StrategyPlan`。
- `PublicPipelineOrchestrator`：组织 exact / greedy 阶段顺序与阶段结果。
- `DisplayRenderEngine`：只负责展示折叠与渲染，不改变求解语义。

本文只保留「为什么正确、为什么快」的核心原理。涉及历史演进、阶段编排细节、工作流与性能审计的记录，建议分别查看测试与 UI/流程文档。

---

## 1. 问题定义

给定 `n` 个互不相等的元素，我们想找出其中**最大的 `k` 个**（top-k）。唯一允许的操作是
**一次比较（comparison）**：选出最多 `m` 个元素，一次性把它们**完全排成一条全序链**
（即得到这 `m` 个之间确定的大小顺序）。

- **目标**：构造一个**自适应策略**（一棵决策树），使得在**最坏情况**下，确定出 top-k 所需的
  比较次数**最少**。
- 我们关心的是 top-k 这个**集合**，**不需要知道这 k 个元素内部的相对顺序**，也不需要知道落选
  元素之间的顺序。
- 记号约定：`n` = 元素总数，`m` = 单次比较能排序的元素个数，`k` = 要找的 top 数量。
  最难的目标算例是 `n,m,k = 25,5,5`。

这是一个**极小化最坏值（minimax）**问题：策略想让最坏情况尽量小，而「对手/自然」会沿着最坏的
比较结果分支走。

---

## 2. 状态表示：偏序集（poset）

### 2.1 偏序集直觉

每个搜索状态记录「目前已知的所有大小关系」。由于比较是逐步进行的，任意时刻我们只知道**部分**
元素对的顺序，其余元素对仍未比较。这种「有些对可比、有些对不可比」的结构就是**偏序集**
（partially ordered set）。

举例，已比出：

```text

a > b,  a > c,  b > d

```

但 `c` 与 `d`、`c` 与 `b` 之间还没比过。画成图（箭头表示「大于」，省略可由传递性推出的箭头）：

```text

    a
   / \
  b   c
  |
  d

```

两个核心概念：

- **链（chain）**：一组**两两可比**的元素，例如 `a > b > d`。一次比较的作用就是把所选元素
  排成一条链。
- **反链（antichain）**：一组**两两都不可比**的元素，例如 `{c, d}` 或 `{b, c}`。反链里的元素
  彼此「顺序信息完全缺失」。

### 2.2 代码中的状态

状态由 `ComparisonState` 表示，用位掩码（bitmask）高效维护每个元素的祖先集合
（`_ancestors`，比它大的元素）和后代集合（`_descendants`，比它小的元素）。关键字段：

- `ActiveMask` / `ActiveCount`：当前**活跃**（尚未决定命运）的元素集合及其个数。
- `GetDescendantMask(item)` / `GetAncestorMask`：某元素严格大于/小于的元素集合。
- `RemainingSlots`：top-k 中还剩多少个名额没被确定占据。

搜索状态的键是 `(RemainingSlots, 规范化后的偏序结构)`，用于置换缓存。

---

## 3. 归一化：什么是「active 元素」

每次扩展状态前，会先做**归一化**，把**命运已经确定的元素移出 active 集合**。这一步对理解后续
所有下界都至关重要。

### 3.1 移走「已确定出局」的元素 —— `Eliminate`

```csharp

// ComparisonState.Eliminate
if (BitOperations.PopCount(_ancestors[item] & ActiveMask) >= k)
    removedMask |= Bit(item);

```

如果一个元素已经被 `remainingSlots` 个活跃元素压在头上，它**绝不可能进 top-k** → 直接移走。

### 3.2 移走「已确定进 top-k」的元素 —— 归一化

如果一个元素「可能压住它的对手数量」已经少到**保证它必进 top-k**，它的命运也定了 → 移走，并占用
一个名额（`remainingSlots` 减一）。

### 3.3 结论：active = 悬而未决

> **active 集合 = 既没保证进、也没保证出的元素。**
> 一旦某元素确定进入 top-k，立即被移出 active，**之后我们再也不碰它，更不会去排它和其他 top-k
> 成员的顺序**。

这正好对应题目语义：**top-k 内部顺序不必知道**——代码从不去求这个顺序。

### 3.4 终止条件

真正的终止是 **`ActiveCount` 降到不再需要决策**：
`remainingSlots == 0`、或 active 元素已全部确定归属（`ActiveCount <= remainingSlots`）、或
剩余元素一步即可解决（`ActiveCount <= m`）。此时返回 0 或 1 步。

### 3.5 例子：top-k 已解决但内部根本没排序

设 `n=5, k=2, m=3`，到达状态：

```text

  a     b
 /|\   /|\
c d e c d e     ← a、b 各自压住 c,d,e；a 与 b 互不可比；c,d,e 互不可比

```

- `a`：压它的只有 `b`（1 个）< 2 → **保证进 top-2** → 移走。
- `b`：同理 → 移走。
- `c,d,e`：各被 a、b 两个压住，活跃祖先数 = 2 ≥ 2 → `Eliminate` 淘汰 → 移走。

结果 active 清空，搜索终止。注意：**我们从未比较 a vs b**，谁大永远不知道，但不影响结论
`top-2 = {a, b}`。这就是「top-k 内部不必排序」在代码中的体现。

---

## 4. 极小化最坏步数的 minimax 搜索

核心函数 `GetMinWorstCaseSteps(state, remainingSlots)`（见 `StrategyBuilder.SearchBounds.cs`）
返回从该状态出发、最坏情况下还需多少步。结构如下：

1. **归一化 + 终止判定**：先 `NormalizeState`，再检查上面的终止条件，命中则直接返回。

2. **置换缓存（transposition table）**：用规范键查 `_minWorstCaseStepsCache`，命中直接返回。
   不同比较顺序常常到达**同构**的状态，缓存避免重复求解。

3. **枚举候选比较组**：`EnumeratePrioritizedGroups` 按启发式优先级列出「这一步选哪 `m` 个元素
   来比较」的候选组（优先规则、对称的分组，使展示出的最优策略更自然）。

4. **对每个候选组求最坏分支**：一次比较会产生若干种可能的排序结果（outcomes）。对每个 outcome
   递归求 `1 + GetMinWorstCaseSteps(下一状态)`，取**最大值**作为这个组的最坏代价
   （minimax 里的「max」层 = 自然选最坏结果）。

5. **在所有组里取最小**（minimax 里的「min」层 = 策略选最好的一组），得到该状态的最优步数。

6. 记录最优组的模式到 `_bestGroupPatternCache`（用于回放/展示策略树），写入缓存，返回。

### 4.1 Alpha-beta 剪枝

搜索用了两处经典的 alpha-beta 思想，依赖**下界**（见第 6 节）来提前砍掉没希望的分支：

- **分支下界剪枝**（`_lowerBoundPrunes`）：在遍历某组的各 outcome 时，先算该 outcome 的
  **乐观下界** `1 + GetMinWorstCaseLowerBound(outcome)`。如果它已经 `>=` 当前已知最优
  `bestWorstCase`，那这个组不可能比已有的更好，**立即停止展开该组**：

  ```csharp

  int branchLowerBound = 1 + GetMinWorstCaseLowerBound(outcome.NextState, ...);
  if (branchLowerBound >= bestWorstCase) { _lowerBoundPrunes++; ...; return false; }

  ```

- **状态下界早停**（early-break）：在比较各组之前，先算整个状态的下界 `stateLowerBound`。一旦
  某组达到了这个**已被证明的下界下限**，就不可能再有更好的组，**直接跳出循环**：

  ```csharp

  int stateLowerBound = GetMinWorstCaseLowerBound(state, remainingSlots);
  ...
  if (bestWorstCase <= stateLowerBound) break;

  ```

> **下界越紧，这两处剪枝就触发得越早、砍掉的分支越多。** 这正是引入反链下界（第 6.2 节）的动机。

### 4.2 迭代加深（IDA*）与门控

单趟精确搜索在「尚无 incumbent」时，根附近下界难以直接转化为剪枝。为此实现了有界搜索
`GetMinWorstCaseStepsBounded(...)`：给定预算 `budget`，若能在预算内完成则返回精确值；否则返回一个
`> budget` 的新下界。外层通过逐步提高预算直到收敛到精确最优。

工程上采用门控：在深/大 `k` 区域优先启用 IDA*，在浅/宽区域退回单趟精确搜索。两条路径都以
`MaxStep` 最优为准，差异仅在性能与等优 tie-break（同为最优但树形可能不同）。

### 4.3 compact 阶段的定位

compact 是二级优化：

- 主目标不变：仍保证 `MaxStep` 最优。
- 次目标优化：在等优解中尽量降低展示边数。

因此，边数不是全局证明最优目标，而是在不损失 `MaxStep` 的前提下做启发式收紧。最终是否替换 incumbent，
由编排层按「先比 `MaxStep`、再比边数」统一裁决。

### 4.4 夹逼区间（squeeze）

为了在难例上提供可解释进度，系统持续维护：

- `L`：已证明下界（来自下界分析与有界失败结果）。
- `U`：当前可行上界（来自已物化可行策略）。

任何时刻都有 `L ≤ opt ≤ U`。当区间闭合（`L == U`）时，即得到已证明最优值。

### 4.5 greedy-feasible 与 proof-tighten（简述）

- `greedy-feasible`：快速构造一棵合法可行树，给出上界 `U`，强调速度与可中断性。
- `proof-tighten`：在预算收紧下做可行性证明，把 `U` 向下压，直到命中下界或证明某预算不可行。
- `greedy-edge-compact`：在最终步数上做一次边数收紧。

这条流程的核心价值是「先给可行解，再逐步给证明」。详细阶段事件、UI 呈现与工作流约定不在本文展开。

### 4.6 proof-tighten 的候选枚举策略

compact 求解器对每个状态的候选比较组使用 `CandidateEnumerationPolicy` 控制枚举范围：

| 策略 | 候选范围 | 当前用途 |
| --- | --- | --- |
| `Progressive` | 使用有限 cap；若本轮因 cap 截断而无法判定，则扩大 cap 后在同一预算重试 | `proof-tighten` 默认策略 |
| `Capped` | 使用有限 cap；到达 cap 后结束本轮，不自动扩大 | `greedy-edge-compact` |
| `Full` | 使用 `int.MaxValue`，枚举当前状态的全部候选代表 | 可由 CLI 或 UI 选择 |

`Progressive` 的 cap 序列通常为 `128 -> 512 -> 2048 -> ...`。每一轮都必须区分：

- **完整且无解**：所有候选都已枚举，可以报告 `ProvenInfeasible`；
- **被 cap 截断**：仍有候选未尝试，只能报告 `Incomplete`，不能据此证明预算不可行；
- **找到可行解**：立即返回当前预算内的策略，不再枚举剩余候选。

候选生成器会在 retry 之间保留原始代表的生成进度，并在每次扩容后重新提供当前累计的候选代表集合。
这是一种枚举 continuation，目的是避免从原始组合的开头重新生成；它不改变候选集合的正确性，也不改变
状态求解的 minimax 语义。当前实现仍可能对已经看到的候选再次执行部分 child/outcome 评估，因此 progressive
并不保证在无解证明场景中优于一次性 `Full`。无解场景必须最终覆盖所有候选，progressive 额外承担多轮调度和
重复评估成本；它的主要收益场景是较小 cap 就能找到可行解并提前返回。

因此，`Full` 与 `Progressive` 的区别是**候选枚举调度**，不是 BFS/DFS 的区别，也不是是否使用状态 memo、
lower bound 或对称性约减的区别。用户选择 `Full` 时，proof-tighten 使用 `int.MaxValue`，使 cap 不再截断；
仍保留上述状态归一化、剪枝、memo 和对称性约减。`Progressive` 是默认模式，适合希望可行场景尽早返回的运行。

CLI 使用 `--proof-tighten-mode progressive|full` 选择模式；桌面 UI 在 Inputs 区域提供同名下拉框。
该设置只影响 greedy 流程中的 proof-tighten，不改变 exact 流程或 greedy-edge-compact 的 capped policy。

---

## 5. 对称性约减：McKay 风格规范形

很多状态在「重命名元素」意义下是**同构**的，会展开出完全一样的搜索子树。如果不识别这种同构，
就会重复求解、爆炸式增长。

本项目用 **individualization-refinement（McKay 风格规范标号）** 给每个偏序集计算一个**完全规范
不变量**（complete invariant）：

- 两个状态同构 ⟺ 它们的规范键相同 → 用作置换缓存的键，自动合并所有同构状态。
- 单纯的 1-WL 颜色细化**不是**完全不变量（不同构的偏序集可能得到相同细化），会造成缓存污染并破坏
  步数正确性；individualization-refinement 通过「逐个个体化 + 细化」区分所有非同构状态。
- 还能给「候选比较组」算规范键（`GetGroupCanonicalKey`），把会生成同构子树的组合并，进一步约减。

相关代码：`ComparisonState.GetCanonicalKey`、`GetDisplayCanonicalKey`、`GetGroupCanonicalKey`；
内部实现位于 `ComparisonState.Algorithms.cs`（`ComputeCanonicalForm`、`RefineCanonicalColoring`、`CanonicalizeRecursive`、`TryFindOrderAutomorphism`）。

### 5.1 工程实现要点

- `ComparisonState` 负责状态与对外入口，规范化与 automorphism 的主要算法实现在
  `ComparisonState.Algorithms.cs`。
- `GetCanonicalKey` 作为置换缓存主键，是跨路径去重的核心热点；实现上增加了跨实例记忆化以减少重复规范化。
- `GetGroupCanonicalKey` 的跨实例缓存命中率较低，当前不作为主优化方向。

### 5.2 性能优化结论（提炼）

- 热路径优化集中在三类：减少分配、减少不必要的规范化调用、在 automorphism 前做便宜预筛。
- 1-WL 颜色细化被用于“必要条件过滤”，用于跳过明显不可能同构的比较对；它不单独承担完整规范判定。
- 这些优化不改变搜索语义，只降低常数开销。正确性仍由 canonical key 与回归测试保障。

---

## 6. 下界（lower bounds）

`GetMinWorstCaseLowerBound` 返回该状态最优步数的一个**合法下界**（保证 `≤` 真实最优），供
alpha-beta 剪枝使用。它取多个下界的**最大值**（谁更紧用谁）：

```csharp

// 1) 信息论下界
//    while (distinguishable < info.Count) { steps++; distinguishable *= maxOutcomesPerStep; }
// 2) 反链宽度下界
steps = Math.Max(steps, GetAntichainLowerBound(state));
// 3) 宽度约束的信息论下界（复用反链计算得到的 width）
steps = Math.Max(steps, GetWidthLimitedInformationLowerBoundSteps(info.Count, state.ActiveCount, width));
// 4) 可判定性下界（determinability floor，见 7.7）
steps = Math.Max(steps, 2);
// 5) 支配下界（放最后：它最贵，且能吃到前几步抬高的 seed 做便宜预筛）
steps = ApplyDominanceLowerBound(state, remainingSlots, steps);

```

**计算顺序**：`max` 与顺序无关，返回值恒等；但把便宜的下界排在**贵的支配下界之前**能省计算——`ApplyDominanceLowerBound` 会跳过「cost ≤ 当前 best」的库条目（见 6.4），所以先让 floor 把 best 抬到 2，可令所有 `cost ≤ 2` 的条目在跑昂贵的回溯嵌入之前就被丢弃。这一重排是保值的（`maxStep`/边数/缓存值都不变），支配抬界次数相应下降（少做了 floor 已能免费覆盖的那部分）。

### 6.1 信息论下界

一次比较最多产生 `maxOutcomesPerStep` 种不同结果，所以 `s` 步最多区分
`maxOutcomesPerStep^s` 种情形。要区分 `info.Count` 个可行 top-k 集合，至少需要
`s` 使得 `maxOutcomesPerStep^s ≥ info.Count`。这个下界在**深处**（可行集合少时）较紧，但在
**根附近**很松——恰恰是最需要剪枝的地方。

### 6.2 反链宽度下界（本项目新增）

这是与信息论下界**互补**的解析下界，在**根附近最强**。下面详细讲。

### 6.3 宽度约束的信息论下界

通用信息论界假设一次 `g=min(m, activeCount)` 元比较最多有 `g!` 种排列结果。若当前 active
偏序宽度为 `w<g`，任何当前或未来的 `g` 元诱导偏序宽度也不超过 `w`，不可能产生 `g!` 种结果。
令 `c=min(w,g)`，把 `g` 个元素尽量平均地分到 `c` 条链上；忽略链间关系后得到一个对所有未来
比较都成立的结果数上界：

```text
B_width = g! / product(chainLength_i!)
```

因此可将信息论下界加强为 `ceil(log(info.Count) / log(B_width))`。计算使用 log-domain，且复用
反链下界已经算出的 width，不展开比较组或 outcome。实测状态“两条各 10 个元素的已知链，m=3，
k=10”中，可行 top-k 集合数为 11：通用信息论界和现有综合静态界均为 2，宽度约束后每步最多
3 种结果，新界为 3，而 exact optimum 为 4。

### 6.4 支配下界（dominance）

如果当前状态能**嵌入**到一个**已解出**的状态中（结构上「更难或相当」），就可以继承那个状态的步数
作为下界。见 `StrategyBuilder.Dominance.cs` 与 `ApplyDominanceLowerBound`。

---

## 7. 反链宽度下界详解

### 7.1 宽度 = 还差多少信息

定义**宽度（width）`w` = 最大反链的大小**，即当前 active 偏序集中「两两互不可比」的元素最多
有多少个。

- `w = 1`：不存在任何不可比的对 → 所有 active 元素两两可比。
- `w` 越大：互不知道顺序的未定元素越多，离解决越远。

为什么宽度能度量进度？因为**只要还有两个 active 元素 `x、y` 互不可比，它俩里至少有一个仍悬而
未决**——当前信息下既可能「x 进 y 出」也可能「y 进 x 出」，无法拍板。所以「消除 active 集合里
所有互不可比的对」（把未定反链塌缩掉）是「全部决定完」的**必要条件**。

> 注意：这里的「width → 1」**不是要把所有元素全排序**，而是「不再有互不可比的未定对」这一
> **必要条件**的松弛。真正终止是 active 清空（width = 0）。我们用一个**松弛过的必要条件**来凑
> 一个**合法（可能偏松）的下界**，这对剪枝是安全的。

### 7.2 对手能保留至少 `w - (m - 1)` 的宽度

一次比较把最多 `m` 个元素排成一条链，这 `m` 个元素**两两变得可比**。

取当前一条大小为 `w` 的最大反链。对任意待比较组，都存在一个与当前偏序相容的线性扩展；对手选择
该线性扩展在比较组上诱导出的结果。删除反链中落入比较组的至多 `m-1` 个元素后，其余元素在新偏序
中仍可保持为反链。因此总存在一个合法结果，其宽度至少为 `w-(m-1)`。

注意，这不是说每个 outcome 的宽度都只能下降 `m-1`；传递闭包可能让某些 outcome 一次新增很多
关系。下界成立依赖 minimax 中对手能够选择上述保留大反链的结果。

### 7.3 下界公式

要把宽度从 `w` 降到 `1`，需减少 `w - 1`；每步最多减 `m - 1`：

```text

还需步数 ≥ ceil((w - 1) / (m - 1))

```

代码（整数版 ceil）：

```csharp

private int GetAntichainLowerBound(ComparisonState state)
{
    if (_m <= 1) return 0;
    int width = GetActivePosetWidth(state);
    if (width <= 1) return 0;
    return (width - 1 + (_m - 1) - 1) / (_m - 1);   // = ceil((w-1)/(m-1))
}

```

**例子**：`m=3`，当前 `w=6`。一步最多 6→4，两步→2，三步才可能 ≤1。
公式 `ceil((6-1)/(3-1)) = ceil(5/2) = 3`。✅
（这正是 `25,5,5` 根下界从 3 提升到 6 的来源之一。）

**为什么是合法下界**：它是**乐观估计**——假设每步都恰好缩 `m-1` 且毫不浪费。真实搜索只会更慢，
所以真实步数一定 `≥` 此值，拿来剪枝安全。

**为什么在根附近最强**：越靠近根，已知关系越少 → 不可比对越多 → `w` 越大 → 此下界越大；而信息论
下界在根部最松。两者正好互补。

### 7.4 与 top-k 语义自洽

下界作用在 **active（未定）子偏序集**上，而非全部 `n` 个元素。top-k 成员一旦确定立即移出 active
（第 3 节），所以**从不排序 top-k 内部**。下界度量的是「为把未定元素区分到能拍板，至少要塌缩多少
反链」，而非「把所有东西排成一条线」。

回到 `n=5, k=2, m=3` 的初始状态（5 个互不可比）：`w=5`，下界
`ceil((5-1)/(3-1)) = 2`——找 5 个里的前 2 名、每次只能排 3 个，至少 2 次，正确，且**完全不要求**
得到 top-2 的内部顺序。

### 7.5 实测结论（提炼）

从已有实验可归纳出三点：

1. 反链宽度下界整体上“接近真实值但略松”，常见差距为 `+1` 或 `+2`。
2. 宽度越大，传统宽度界越可能偏松；这也是后续强化下界的主要方向。
3. 以“可证明且低成本”为前提的强化并不容易，很多看似自然的 `+1` 规则并不 sound。

### 7.6 可判定性下界（determinability floor）

一个关键补强是：当状态已归一化、尚未终止，且 `activeCount > m` 时，可直接得到下界 `opt ≥ 2`。

直观上，一步比较最多只排序 `m` 个元素，至少会留下一些活跃元素未被充分判定，因此单步不可能保证完成。
该规则是 `O(1)` 的，并与宽度界、信息论界互补。

实现上，在 `GetMinWorstCaseLowerBound` 中统一取：

- 现有下界结果
- `2`（当满足上述条件）

二者的最大值。这样不会改变正确性，只会增强剪枝力度。

### 7.7 边界说明

本文将下界强化控制在“已验证 sound”的范围：

- 优先采用可证明、可维护、可低成本计算的规则。
- 对尚无统一闭式保证的推断，不直接纳入生产下界。

因此，下界体系的目标是“稳定提升剪枝效率”，而不是在文档层面追求更激进但难以证明的公式。

---

## 8. Dilworth 定理与最大反链的计算

第 7 节需要算「最大反链 `w`」。直接枚举所有子集是指数级的。**Dilworth 定理**把它转化为可高效
求解的问题。

### 8.1 定理

> **Dilworth 定理**：在任何有限偏序集中，
> **最大反链的大小** = **把整个偏序集划分成若干条链所需的最少链数**。

直觉：

- **`≤` 方向（容易）**：最大反链有 `w` 个两两不可比的元素，它们不可能有任意两个在同一条链里
  → 至少要 `w` 条链才能分开装下 → 链数 `≥ w`。
- **`≥` 方向（定理精髓）**：Dilworth 证明你**总能恰好**用 `w` 条链盖住整个集合，一条不多。

两方向合起来：**最少链数 = 最大反链 = `w`**。

### 8.2 小例子

```text
    a
   / \
  b   c
  |
  d
```

- 最大反链：`{c, d}`，大小 **2**。
- 最少链覆盖：`a>b>d` 与 `c`，共 **2** 条。
- 相等 ✅。

### 8.3 转化为二部匹配

经典结论：**最少链覆盖 = 元素总数 − 最大二部匹配**。

构造：每个元素复制成左右两份；若 `x` 在偏序中**严格大于** `y`，就连边 `x_L —— y_R`
（含义：「让 y 紧接在 x 下面，串进同一条链」）。

上例「严格大于」关系（含传递）：`a>b, a>c, a>d, b>d`，连边：

```text

a_L —— b_R
a_L —— c_R
a_L —— d_R
b_L —— d_R

```

**最大匹配**：挑出尽量多、**两两不共享端点**的边，例如 `a_L—b_R` 与 `b_L—d_R`，大小 **2**
（再无法在不冲突下加入第三条）。

- 每条匹配边 `x_L—y_R` 表示「把 y 接到 x 下面」，使两条链合并、链数减一。
- `a_L—b_R` → `a>b`；`b_L—d_R` → 接成 `a>b>d`；`c` 没被接走 → 自成一链。

于是：

```text

最少链数 = 元素总数 − 最大匹配 = 4 − 2 = 2
由 Dilworth：最大反链 w = 最少链数 = 2 ✅

```

对应代码：

```csharp

width = ActiveCount - maxBipartiteMatching;   // GetActivePosetWidth

```

`maxBipartiteMatching` 用标准的**增广路径（augmenting path）**算法求
（辅助函数 `TryAugmentMatching`）。

### 8.4 完整链路

```text

当前 active 偏序集
      │  ① 复制成左右两列，按"严格大于"连边
      ▼
   二部图
      │  ② 求最大匹配（增广路径）
      ▼
最大匹配数 M
      │  ③ 最少链覆盖 = ActiveCount − M   （匹配 ↔ 链合并）
      ▼
最少链数
      │  ④ Dilworth：最少链数 = 最大反链
      ▼
   宽度 w
      │  ⑤ 每步最多缩 (m−1)，要缩到 1
      ▼
下界 = ceil((w−1)/(m−1))   →  并入 minimax 的 lower bound 用于剪枝

```

---

## 9. 实测效果与正确性保证

引入反链下界后，在默认搜索的「构造 outcome 数」上：

| 算例 | before | after |
| --- | --- | --- |
| 25,5,3 | 100562 | 759（−99.2%） |
| 12,4,4 | 12323 | 9809 |
| 11,3,3 | 6541 | 3532 |
| 9,3,3 | 1478 | 991 |
| 25,5,5 根下界 | 3 | 6 |

**正确性**由 229 个回归测试守护：

- **结构基线**（13 个算例）：MaxStep、根组、输出状态数全部不变 → 物化策略树逐字节一致。一个
  **不合法**（过紧）的下界会剪掉某个最优分支，从而抬高某算例的 MaxStep——而这并未发生，反证下界
  合法。
- **计数监控**：searched / outcomes / dupSkips / candGroups 等确定性计数被设为 `<=` 上限，锁定
  剪枝收益（绝大多数大幅下降；个别 `k>m` 小算例因早停顺序微调略升，属预期）。

---

## 10. 相关源码索引

| 主题 | 文件 / 符号 |
| --- | --- |
| minimax 主驱动、alpha-beta、早停 | `StrategyBuilder.SearchBounds.cs` → `GetMinWorstCaseSteps` |
| 单趟精确搜索（浅/宽路径） | `StrategyBuilder.SearchBounds.cs` → `GetMinWorstCaseStepsExact` |
| 迭代加深有界搜索（深/大 k 路径） | `StrategyBuilder.SearchBounds.cs` → `GetMinWorstCaseStepsBounded`、门控 `_useIterativeDeepening` |
| 综合下界 | `StrategyBuilder.SearchBounds.cs` → `GetMinWorstCaseLowerBound` |
| 夹逼报告（已证明下界 L） | `StrategyBuilder.SearchBounds.cs` → `RecordRootProvenLowerBound`；`SearchStatistics.RootProvenLowerBound` / `SearchProgressSnapshot.RootProvenLowerBound`；GUI `MainForm.FormatSqueeze` |
| 反链下界 / 宽度 / 匹配 | `GetAntichainLowerBound`、`GetActivePosetWidth`、`TryAugmentMatching` |
| 宽度约束的信息论下界 | `GetWidthLimitedInformationLowerBoundSteps`、`GetLogCapacityLowerBoundSteps` |
| 状态 / 偏序 / 归一化 | `ComparisonState.cs`（`Eliminate`、`Deactivate`、`ActiveMask`、`GetDescendantMask`） |
| 规范形 / 对称约减 | `ComparisonState.Algorithms.cs`（`ComputeCanonicalForm`、`RefineCanonicalColoring`、`CanonicalizeRecursive`、`TryFindOrderAutomorphism`）+ `ComparisonState.cs` 入口（`GetCanonicalKey`、`GetDisplayCanonicalKey`、`GetGroupCanonicalKey`、`TryMapOrderByAutomorphism`） |
| 支配下界 | `StrategyBuilder.Dominance.cs`、`ApplyDominanceLowerBound` |
| 紧凑搜索变体 | `StrategyBuilder.Compact.cs` |
| 构造式可行解（greedy 模式 step / 可行上界 U） | `StrategyBuilder.GreedyFeasible.cs` → `ExecuteGreedyFeasibleStage`、`ChooseConstructiveGroup`、`ConstructiveRootUpperBound`；`StrategyPlan.IsFeasibleUpperBound` |
| 回归 / 计数监控 | `tests/TopKFinder.Tests/StrategyRegressionTests.cs`、`DominanceMetricTests.cs` |

