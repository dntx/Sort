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
- `.github/workflows/nightly-proof-tighten-gate.yml` 与 `.github/workflows/nightly-full-strategy-matrix.yml`：夜间性能巡检与报警入口。
  - 其中 nightly proof-tighten gate 的默认超时为 `150s`，用于降低 hosted runner 抖动带来的压线误报。

实现注记（2026-07）：

- 对称等价分支的组合计数在大规模输入上可能超过 `Int32`（例如 `(20,19,19)` 对偶化后会触发 `19!` 级别计数）。
- 这类计数用于展示与汇总时采用**饱和语义**：超过上限时钳制到 `int.MaxValue`，避免 `BigInteger -> int` 转换溢出中断 greedy 流程。
- 该保护不改变搜索最优性目标（`MaxStep`），只避免大计数显示路径上的数值异常。
- 对 `m!` 这类“每步结果上界”只可作为**松上界**参与剪枝估计，不应直接用于大容量集合预分配；`(25,24,1)` 场景曾因预分配过大触发容量异常/内存异常，现已改为饱和上界 + 动态增长容器。
- 显示层去参数化：将仅表达固定谓词（如 `> 1`、`> 2`）的阈值别名常量内联为直接条件，保留预算/上限类常量（如采样上限）继续集中定义，以减少“同义参数”噪音且不改变行为。

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

### 4.2 树的「逐字节一致性」（仅限单趟精确路径）

在**单趟精确搜索路径**上，反链下界这类改动只是让上面的早停**更早触发**，但**第一个**在优先级顺序里达到最优的组无论如何都
会被选中（后来的等值组永远不会替换它），所以 `_bestGroupPatternCache` 不变 →
**最终物化出的策略树逐字节一致**。这一点由结构基线回归（13 个算例的 MaxStep + 根组 + 输出状态数
全部不变）验证。

> ⚠️ 注意：这条逐字节一致性**只对单趟精确路径成立**。下面 4.3 的迭代加深（有界）路径会沿优先级顺序在
> **等优最优组**之间选到不同的代表，因此它物化的树与单趟路径**可能不同**（MaxStep 仍最优，但边数 / 输出
> 状态数可能不一样）。详见 4.3 末尾。

### 4.3 迭代加深（IDA\*）：把下界变成真正的剪枝

上面 4.1 的两处剪枝有一个**先天局限**：它们要靠一个已经求得的「当前最优 `bestWorstCase`」来对照。
而分析型下界（尤其是反链下界，第 7 节）恰恰在**靠近根、还没有任何 incumbent** 的地方最大、最有用——
此时 `bestWorstCase` 还是 `int.MaxValue`，下界再紧也剪不掉任何东西。换句话说，无界搜索里「根附近的好
下界被浪费了」。

**迭代加深**用一个**全局预算 `budget`** 解决这个问题，把上界从一开始就压到每个节点上：

- 把核心递归改成**有界版本** `GetMinWorstCaseStepsBounded(state, rs, budget, depth)`：
  - 当最优值 `<= budget` 时返回**精确最优**；否则返回一个**严格大于 budget 的合法下界**（一次「失败」）。
  - 关键变化：`bestWorstCase` 从 `budget + 1`（失败哨兵）起步，并把 `bestWorstCase - 2` 作为**子节点的
    预算往下传**（alpha-beta 的 β 下传）——这正是让全局预算能在深层节点剪枝的机制。
  - **入口剪枝**：若已知下界（分析下界，或上一趟失败学到的下界）已经 `> budget`，无需搜索直接返回。
  - 新增 `_searchLowerBoundCache[key]`：记录每个状态从失败趟里学到的最好下界，作为跨趟的 IDA\* 置换记忆。
  - 精确缓存 `_minWorstCaseStepsCache` 和 `_bestGroupPatternCache` **只在完全求解（`<= budget`）时写入**，
    因此每个节点缓存的都是一个**合法的精确最优组**，物化出来的是一棵**合法的 MaxStep 最优策略树**。

- 外层驱动器 `GetMinWorstCaseSteps` 变成**迭代加深循环**：`budget` 从分析下界起步，每当一趟失败就把
  `budget` 跳到学到的下界再重试，直到 `result <= budget` 得到精确最优。

#### 何时启用：深 / 大 k 区域门控

迭代加深要多趟搜索，会带来**重复探索**的开销。实测发现它在不同形状上是**取舍**：

- **目标 (m=5, k=5) 大 n 家族**（通往 25,5,5 的方向）：搜索节点削减**随 n 增长**——
  `searched`：14,5,5 −28% → 17,5,5 −44% → 18,5,5 −59%；`outcomes` 同步下降约 −62%~−68%。
  因为这里反链下界足够紧、趟数少，而被剪掉的深层子树极其庞大。
- **浅 / 宽的小算例**（如 12,4,5、25,5,3）：下界松、趟数多，重复探索反而让计数上升。

因此用一个**经验门控** `_useIterativeDeepening = (_m >= 5 && _k >= 5 && _n >= 2 * _m)`：

- 代码里现已把这组阈值提炼成具名常量：`IterativeDeepeningMinGroupSize = 5`、`IterativeDeepeningMinRequestedTopCount = 5`、`IterativeDeepeningMinNToMScale = 2`。

- **命中**（深、大 k）→ 走迭代加深，吃到随规模增长的剪枝收益；
- **未命中**（浅、宽）→ 退回**单趟精确搜索** `GetMinWorstCaseStepsExact`，它与引入 IDA\* 之前的算法
  **逐字节一致**，不付任何重复探索代价。

> 这是一个**性能启发式而非正确性边界**：两条路径都返回**相同的精确 MaxStep 最优值**。门控阈值只决定
> 「用哪条路径算」，因此 229 个回归算例（计数上限、快照、dominance floor 全部落在精确路径）保持全绿、
> 零改动。
>
> **但要点醒一句**：被门控的 (5,5) 大算例物化出的策略树**未必与单趟精确路径逐字节一致**——迭代加深沿
> 优先级顺序会在**等优最优组**之间选到不同代表。实测 14,5,5 单趟 84 条边、迭代加深 85 条边；17,5,5 则
> 反过来（206 vs 200）。**两棵树都是合法的 MaxStep 最优策略**，只是 tie-break 不同。因此对门控算例，回归
> 测试断言的是 **MaxStep / 根组不变 + 各自路径的计数上限**（见 `StrategyRegressionTests` 中的 (5,5) 重型
> 监控与跨路径对照测试），而**不**断言「两路径同树」。

### 4.4 边数（edge-count）与 compact 阶段：最优性边界

承接 4.2/4.3：搜索的**唯一优化目标是 MaxStep**，**从不**以边数为目标。一棵被物化出来的策略树的边数，
只是「在所有 MaxStep 最优策略里，恰好被选中的那一棵」的**副产品**——所以谈某棵 default 树的边数「是否最优」
是没有意义的：14,5,5 既存在 84 条边的最优树、也存在 85 条边的最优树，**两者 MaxStep 同样最优，边数无优劣之分**。

> ⚠️ 因此 `StrategyRegressionTests` 中对边数 / 输出状态数的 `==` 断言，**锁的是「当前路径碰巧物化出的那棵树」
> 的可复现性（确定性快照），不是「最小边数」的最优性**。任何核心改动若意外改变了 tie-break，会以 diff 形式暴露。

**compact 阶段**（`StrategyBuilder.Compact.cs`）是一个**二级 DP**，专门来收紧边数：它**保持 MaxStep 为主目标**
（只在**等优组**里选择），并在此前提下**最小化「渲染出的分支边总数」**。但要注意它的**最优性边界**：

- **最小化的是一个代理量，而非真实渲染边数**：DP 把每个子状态的子树代价**独立累加**，**没有**建模物化阶段的
  **display-key 引用去重**——同一状态第二次被到达时会渲染成一个 **0 边的 Reference 叶子**。这层跨子树去重 DP
  看不到，所以它求得的「最小代理边数」与真实渲染边数之间存在偏差，**不等于可证明的全局最小边数**。
- **可能比 default 还差，交由编排层裁决**：正因为上述代理偏差，在个别状态上 compact 反而渲染出**更多**边
  （例如 `10,4,8`：8 → 10）。`BuildEdgeCompactStage` **直接返回这个原始 compact 候选**（不再在 builder 内部跑第二遍
  default 兜底）。「永不劣于 default」的保证统一由**编排层的主线规则**承担：每个阶段算出一个解，只有当它
  **严格优于当前全局最优**（`StrategyPlan.IsStrictRefinementOver`：先比 MaxStep、再比边数，更小者胜）时才挂成
  新解，否则 do nothing。因此比 default 差的 compact 候选**永远不会被展示**（GUI 与 CLI 共用这一规则）。

**结论**：

| 阶段 | MaxStep | edge-count |
| --- | --- | --- |
| **default** | 精确最优 | 某棵最优树的副产品，**非最小**；两条搜索路径可能各得一棵边数不同的合法最优树 |
| **compact** | 精确最优（只在等优组里挑） | 经专门优化；builder 返回的**原始候选**可能劣于 default，但编排层只在它**严格更优**时才展示，故**展示结果保证 ≤ default**，且因「代理最小化」而**仍非可证明的全局最小** |

> 换言之：compact 之后展示的边数是「带编排层安全裁决的启发式较优值」，把它称作「最优 edge-count」会过强。
>
> **性能提示**：compact 是一个**独立且不可忽视的耗时阶段**——它对每个等优状态都要枚举候选组并递归求子树代价，
> **某些算例下其运行时间甚至超过 default 阶段**。因此后续任何性能优化 / 监控都应把 compact 阶段一并纳入考量，
> 而不能只盯着 default 搜索阶段（计时拆分见 `_phase1bMilliseconds`，统计量见 `_compactStatesSolved` /
> `_compactGroupsEnumerated`）。

### 4.5 夹逼报告（squeeze）：`L ≤ opt ≤ U` 的「已证明下界」一侧

对 `25,5,5` 这类**精确搜索短期内跑不完**的硬算例，即便无法得到精确解，也应当能给用户一个**有数学保证**的
答复：「最优值至少是 `L`」。迭代加深（4.3）恰好**天然产出**这样一个下界——驱动循环里的 `budget` 在**每一趟**
都是 opt 的**已被证明的合法下界**：

```csharp

// StrategyBuilder.SearchBounds.cs -> GetMinWorstCaseSteps（迭代加深驱动）
int budget = GetMinWorstCaseLowerBound(state, remainingSlots);
while (true)
{
    // 走到这一趟说明此前每一趟都已证明「不存在 ≤ budget-1 步的策略」
    // → budget 是 opt 的一个已证明下界（opt ≥ budget）
    if (_recordRootIncumbents)
        RecordRootProvenLowerBound(budget);
    int result = GetMinWorstCaseStepsBounded(state, remainingSlots, budget, depth: 0);
    if (result <= budget) { RecordRootProvenLowerBound(result); return result; }
    budget = result;   // 失败：跳到学到的更高下界再试
}

```

- **L 一侧（已证明下界）几乎免费**：每一趟失败都**严格证明**了「opt > budget」，于是 `budget` 单调上升且
  **始终 ≤ 真实 opt**。搜索若被中途取消，`_rootProvenLowerBound` 保留的就是「**已证明 opt ≥ L**」这一结论
  （`25,5,5` 上 L 会实时从 3 往上爬）。这一侧通过 `SearchStatistics.RootProvenLowerBound`、以及每次进度
  回调的 `SearchProgressSnapshot.RootProvenLowerBound` 暴露出来。
- **U 一侧（可行上界）= incumbent 或贪心可行解**：`RecordRootIncumbent` 记录的「当前找到的最好可行策略步数」是上界 `U`。
  但要注意：incumbent 只在某一趟**成功**（找到 ≤ budget 的解）时才更新，对硬算例那一趟成功 ≈ 整题已解完，
  所以**失败趟期间通常还没有有限的 U**。为在搜索早期（甚至**根本算不完**时）就拿到有意义的 U，本项目用一个
  **独立的、便宜的贪心构造器**单独造一棵可行（非最优）策略树——见 4.6。`25,5,5` 上它瞬间给出 `U = 9`，于是即便
  精确搜索跑不完，也能立刻显示 `6 ≤ opt ≤ 9`。
- **夹逼闭合**：一旦完整解出，最后一趟成功时 `budget == opt`，于是 `L == opt`；此时 `RootProvenLowerBound`
  恰等于 `MaxStep`，区间收拢成一个点。

> 实现要点：`RecordRootProvenLowerBound` 只在**phase-1 根搜索**（`_recordRootIncumbents == true`）期间记录，
> 且**单调取最大**；compact / optimality-gap 等其它调用方静默复用搜索、不污染该值。**单趟精确路径**（非门控的
> 浅 / 宽算例）不在每个节点压预算，没有「逐趟抬升的下界」可言，因此只在解出后把**精确结果**记为 `L`（这样既满足
> 「解出即 `L == opt`」，又**不额外调用** `GetMinWorstCaseLowerBound`、保持既有计数器逐字节不变）。
>
> **跨阶段生命周期**：`_rootProvenLowerBound` 与 `_rootIncumbents`、`_rootSearchInitialized` 一样，都是
> **一次性 phase-1 求解的产物**，其生命周期由 `_phase1Solved`（**不**按 build 重置）界定。因此它们**不**在
> `ResetPerBuildTransientState` 里清零——否则随后的 compact build 会把已解出的 `L` 重置为 0，而 compact
> 阶段复用缓存、**不会重跑** IDA* driver 去重新记录，导致夹逼显示从「`max steps = N (proven)`」回退成
> 「`? <= max steps <= ?`」。其余按 build 重新统计的计数器（`searched` / `output` / cache 命中等）仍照常重置，
> 因为 compact 阶段会通过 `ObserveSearchState` / `VisitComparisonOutcomes` 重新填充它们。

### 4.6 构造式可行解（greedy 模式的 step 阶段）

精确 minimax 之所以会爆炸，是因为它在**每个状态**上对**所有**候选分组取 min、并且要**证明最优**。
greedy 模式把这两件昂贵的事都砍掉：只承诺一个分组（不做 min、不回溯、非最优），但仍展开所有对手分支，
于是整棵策略树是一个**单策略闭包**而非搜索树。

早期实现仍然靠**完整枚举**来挑这个分组（`EnumeratePrioritizedGroups → ~C(active, m)`），在大 `m` 上这个
「只为取第一名却枚举全部」的选择反而成了瓶颈（`25,10,10` 的 step 阶段约 49 s）。现在改成**构造式选择**
（`StrategyBuilder.GreedyFeasible.cs → ChooseConstructiveGroup`）：直接从当前偏序里以 `O(m·active²)` 现算分组，
**完全不枚举**。它是一场「保留偏序的锦标赛」——每一步竞争极大「前沿」（被证明更大的元素最少的那些项），
增量地拼出一个近似**反链**（两两互不可比、单次排序就能一次性解析最多对），既让新的 top 候选浮现，又把败者推向淘汰，
同时跳过所有已知关系。`25,10,10` 的 step 阶段因此从约 86 s 降到约 3 s。

```csharp

// StrategyBuilder.GreedyFeasible.cs -> ChooseConstructiveGroup（无枚举、无预解闭包）
// 增量反链：每次挑「与当前组内成员互不可比数最多」的活跃项，平局偏向前沿（祖先最少）。
List<int> group = ChooseConstructiveGroup(state, remainingSlots);  // O(m·active^2)

```

物化（materialize）阶段完全复用既有路径：`ChooseGroup` 在 `_useConstructiveSelection == true` 时**当场**调用
`ChooseConstructiveGroup` 算出分组（无需 compact/精确那样的预算 pattern 缓存，因为选择器本身便宜且确定）。
这样造出的策略树**结构合法**、可直接展示，其 `MaxStep` 就是**可行上界 `U`**（注意：`U` 不是已证明最优，只是一个
**确实可达**的步数）。正确性（`U ≥ opt`）只需「严格进展」：每次排序都至少新增一条比较关系——只要所选分组含有一对
互不可比项即可，而 `ChooseConstructiveGroup` 保证了这一点（总链兜底见 `ForceUnresolvedPair`）。

> **`MaxStep` 必须计入 Reference 子树深度**：物化树里同一状态第二次到达会渲染成一个 **Reference 叶子**（不是回边），
> 它代表「复用目标状态子树、还要再 +N 步」。`StrategyPlan.MaxStep`（`GetMaxStep`）因此**解析 Reference 到其目标子树**
> 再累加剩余步数，而不是把它当 0 深度叶子——否则当某条最深路径以 Reference 收尾、且其目标在更浅处首次展开时，`MaxStep`
> 会**少算**真实最坏步数（例如 greedy-feasible `6,2,2`：真实 7、少算成 6，使可行上界假性低于最优）。这条对所有计划
> （exact / compact / feasible）都成立，回归见 `MaxStepReferenceDepthTests`。
>
> 同一口径也用于展示层：树节点标题 `Sx [step a/b]` 的 `b` 现在按 Reference 递归解析计算，
> 与 `MaxStep` 保持一致，避免出现根节点显示步数小于阶段 `max-step` 的不一致。
>
> **显示树结构异常时会 fail-fast**：`Reference` 本应只指向一个已展开的无环子树；若因显示 key 复用/重标号等异常导致出现
> `Reference` 解析环（例如 A 的引用链又回到 A），`GetMaxStep` 会抛 `InvalidOperationException`（malformed strategy tree），
> 而不是静默给出数值。这样可以避免无限递归/栈溢出，并尽早暴露上游构树错误。相关回归见
> `MaxStepReferenceDepthTests.MaxStep_ThrowsOnReferenceCycle`。

- **1-ply 前瞻收紧 `U`**（`ChooseConstructiveGroupLookahead`）：纯贪心反链每步只看「本步信息量最大」，会偏爱**全新孤立项**
  （与一切互不可比、关系最少），因而催生互不相连的多条链，事后合并代价高；而更优的走法常常是**跨边界桥接**（把边界项
  与新项一起排序）。物化时的分组选择因此改为一层前瞻：枚举一小组候选分组（基础反链挑选 + 每个种子项各一个反链，再加
  一个有界 swap 邻域），对每个候选只看**下一层所有结果分支**，用一个廉价打分来排序：先比较各分支里**最坏的解析下界**
  `GetMinWorstCaseLowerBound`，再比较**最坏 active 数**，最后比较**平均 active 数**。也就是优先选出「下一步看起来最容易继续压缩」
  的候选，而不是做递归 rollout。这个评分保持了选择器的多项式成本，同时把基础反链经常漏掉的跨边界桥接带了回来；
  其效果由 `FeasiblePlan_LookaheadPinsRawUpperBound` 等回归测试固定。

- **display-line 并列打破的重操作门控**：1-ply 候选分数并列时，历史实现会调用 `CountDisplayBranches` 作为额外 tie-break，
  但该路径会进入分支合并/轨道划分，代价在大 active 状态上明显放大。现在仅在 `m >= 3` 且 `activeCount <= 16`
  时启用此重型 tie-break；更大状态直接保留「分数 -> 字典序」并列规则。该改动只影响并列打破时机，不改变
  可行性/正确性语义，目标是削减长耗时 case（如 `25,5,10`）的 stage-1 热点开销。

- **候选打分早停（incumbent-dominance short-circuit）**：在 1-ply 候选评分里，当前候选按结果分支累积
  `maxChildLowerBound / maxChildActiveCount / DistinctSuccessorCount` 三个前缀键；这三者在遍历过程中都是
  单调不减。若前缀已经严格劣于当前 incumbent 分数，则该候选后续不可能翻盘，立即停止该候选的结果遍历。
  这不会改变分数定义和最终选组（仅减少不必要的评分工作量），并通过 A/B 回归测试（同案关闭早停对照）
  锁定「启用早停时 OutcomesConstructed 不上升」。

- **`m == 2` 视为单独的 pairwise regime，不走上述 lookahead**：当前 1-ply 前瞻是为**真正的组排序**（`m >= 3`）设计的，
  依赖「一步能产生很多 outcome、一次能把一个反链压成更长的链」这一结构。`m=2` 时这些前提同时退化：
  一步只有 **2 个** outcome，本质上就是一次二元比较；反链宽度每步最多只降 **1**（宽度下界为
  `ceil((w - 1) / (m - 1))`，故 `m=2` 时每步只能把 `w` 减一）；动作空间也从“选一个 group”退化成“选一条未决边”。
  于是 lookahead 的**即时信号**明显变弱，但每个候选仍要支付完整的下界评估成本（`GetMinWorstCaseLowerBound`），性价比很差。
  实测上这不仅体现在速度：把 lookahead 全关后按 `m` 分桶扫描小算例，退化率呈现明显分界：`m=2` 约 **68%**、`m=3` 约 **47%**、
  `m=4` 约 **26%**、`m=5` 约 **21%**，说明 `m=2` 的确是一个**定性不同**的 regime，而 `m=3` 起 lookahead 仍有稳定收益。
  因此实现里明确在 `m==2` 时直接回退到基础反链启发式，把它视作 pairwise edge-selection 问题；`m>=3` 仍保留现有的 group-level lookahead。

- **夹逼**：`L = GetMinWorstCaseLowerBound(root, k)`（解析下界，与精确搜索**无关**、极便宜；`25,5,5 → 6`），经
  `RecordRootProvenLowerBound` 写入；`U =` 构造树的 `MaxStep`。于是 `L ≤ opt ≤ U`。若 `L == U` 则该可行解
  **恰好达到了已证明下界**，即**已证明最优**（显示 `max steps = U (proven optimal)`）。
- **两种模式、各若干阶段**：编排层提供两条互斥的流水线，CLI 用 `--mode exact|greedy`、GUI 用下拉框切换。每个阶段都有一个
  统一的**阶段名**（也是 CLI 标题、GUI 树根与进度面板共用的标签）：exact 模式为 `step-proof → edge-compact@S`；greedy 模式为
  `greedy-feasible → (optional) greedy-tighten → proof-tighten≤N → proof-tighten≤N-1 → … → edge-compact@S`（`proof-tighten≤N` 中的 `N` 是该次收紧的**目标步数上限**，并非已达到的步数；
  最后的 `edge-compact@S` 是在收紧确定的最小可行步数 `S` 上跑的**唯一一遍 min-edge 边数紧凑**）。
  收紧到某个 `N` 被证明不可行时，单独呈现一个标 **`no solution`** 的终止阶段；若收紧探测因贪心候选帽子截断而无法证明，则呈现一个
  标 **`search incomplete (candidate cap reached)`** 的终止阶段。收紧本身**不设时间上限**，会一直跑到证明为止或被用户取消
  （GUI 的 Stop 按钮 / CLI 的 Ctrl+C）。
  - **exact 模式（默认）**：`step-proof` = 精确求解 `BuildStepProofStage`（已证明最优），`edge-compact@S` = compact `BuildEdgeCompactStage`。
    **不跑可行 feasible**。step-proof 的首阶段已是步最优，故其 edge-compact 永远无法再降低步数，**没有**向下收紧阶段。
  - **greedy 模式（快速，feasibility-first）**：`greedy-feasible` = 构造式 feasible `BuildGreedyFeasibleStage`（可行上界 `U`）。随后可选
    `greedy-tighten` 预阶段：先做 root-only 微探针（`ShouldRunGreedyTightenByRootProbe`），仅当探针判断根层存在降高机会时才运行单轮
    `BuildGreedyTightenPlan`；若其结果严格优于 `greedy-feasible`，则用 `OverrideGreedyPipelineUpperBound` 把 proof-tighten 的起始上界
    改为更紧的 `U'`。再进入
    `RunGreedyPipeline` 分两段：**Phase A** 用**只判可行、不计边数**的 compact 探测依次以 `U−1, U−2, …` 为根预算
    收紧步数（每个成功的更小步数解发一个 `proof-tighten≤N` 阶段）；**Phase B** 在收紧确定的最小可行步数 `S` 上跑**唯一一遍
    min-edge** compact（`ProbeFeasibleCompact(S)`，发一个 `edge-compact@S` 阶段）以最小化边数。若这一步恰好被
    `CompactGreedyCandidateCap` 截断，本轮 probe 留下的 partial phase-1b cache 会先被标记为**不可物化并立即丢弃**；
    编排层不会再做更重的 uncapped 重跑，而是**保守地保留最近一个完整 incumbent plan**，把最终 edge 阶段记为
    `no improvement`。也就是说，greedy 模式宁可放弃这最后一次边数优化机会，也不把尾部 runtime 升格成一次大枚举。快速、可中断、非证明最优。
    进度估计方面，`proof-tighten` 阶段把**外层收紧区间** `U→L`（最差需逐档探测 `U-L` 层）与**当前层内工作量**（`solved/(solved+scale)` 渐近分数）融合，
    使进度条既能反映每一层内部推进，也能在预算一次跨多档时体现整体收紧进展。为减小跨预算档位切换时的体感跳变，
    融合值在该阶段额外经过一个轻量 EMA 平滑（当前参数 `alpha = 0.05`，只影响展示，不影响求解/判定语义）。
    这样安排是刻意的：min-edge 只在**最终步数** `S` 上做一次，避免在中途会被收紧丢弃的 `U`、`U−1`… 各层白算一遍边数
    （旧架构在 `U` 层先跑一遍完整 min-edge 基线、随后又被步数收紧作废，纯属浪费）。
    Phase A / Phase B 的根预算优先取**step 阶段物化得到的 `U`**（同一个 builder 实例先跑 step、再跑 compact，编排层正是这样复用的）——
    这是最紧且可靠的预算：step 树本身就是一个 `U` 步解的见证，所以 compact 在该上限下绝不会需要超过 `U` 步，从而保证
    **compact 计划不会比 step 更差**。若 `RunGreedyPipeline` 被独立调用（builder 上没有先跑 step），则内部先跑一遍
    `BuildGreedyFeasibleStage()` 自行确立 `U`。在大 `m`
    形状（如 `25,10,10`）上，单个状态可能有上千个不同的步最优分组，过去 compact 阶段（`EnumerateDistinctGroups`）
    会把它们**全部生成 + McKay 去重**，于是几乎卡死。现在生成本身带一个 per-state 上限
    `CompactGreedyCandidateCap`（默认 128，见 `GenerateClassRepresentatives` 的 `generationCap`）：先把 step 阶段的
    构造式分组作为种子第一个评估（保证有界内必有可行解），再生成至多 `cap` 个代表参与「子节点最少」的贪心挑选。
    该默认值现通过 `DefaultCompactGreedyCandidateCap = 128` 集中命名，便于后续统一调参；当调用方保持默认值时，运行期会按
    当前状态的 `activeCount * groupSize` 搜索面把有效 cap 温和放大到最多 `4x` 基线，以减少宽状态上的 probe 重试。显式设置
    非默认值则保持精确覆盖，不参与自适应放大。
    分组数 `≤ cap` 的状态因此与穷举**逐字节相同**（小/中形状毫无变化），只有分组数超过 `cap` 的大 `m` 状态被截断——
    用一点边数紧凑度换取**有界、可中断**的运行时间（`25,10,10` 由「出不来」降到约 23 s）。`int.MaxValue` 恢复原先
    的完整穷举，精确（exact）模式与最优性审计仍走未截断路径。
  - **向下预算收紧（默认开启、无时间上限、可中断）**：step 阶段给出的 `U` 系统性地偏大（实测几乎总是 `opt+1`）。
    于是 Phase A 从 `U` 开始，依次用 `U−1, U−2, …` 作为根预算跑**只判可行**的 compact 探测
    （`ProbeFeasibleCompact`，其 `SolveBudgetFeasibility` **跳过** `CountDisplayBranches` 边计数、只返回可行步数），
    直到某个预算**不可行**（根处 `SolveCompact` 返回 `int.MaxValue`，此时不物化、直接判负，避免 `BuildState` 抛错）
    或触达**已证明下界 `L`** 为止；每拿到一个更小 `S` 的可行解就采纳为新的最优、发一个 `proof-tighten≤N` 阶段。
    **注意 compact 组选择是 budget-无关的启发式**（`_compactGroupPatternCache` 只按状态键
    缓存，同一状态在不同预算下求解会「最后写入者获胜」），个别形状下一次 probe 物化出的树可能**并未真正满足它的天花板**；
    因此收紧循环以**物化 `MaxStep` 为地面真值**设了守卫：
    只接受 `MaxStep` **严格更小**的候选，任何未变优的结果一律停止收紧、绝不据此把天花板抬回 `MaxStep−1`（否则会在
    `U−1 → 更大` 之间反复震荡且再也停不下来）。由于真正被采纳的可行解永远不可能优于真实 `opt`，所以 `opt−1` 必然不可行——这保证收紧后的
    `S` 仍满足 `S ≥ opt`、绝不会假性低于最优（见 `ProofTightenPlanTests`）。每次重跑前用
    `ResetCompactState` 清掉 compact 专属缓存（`_compactGroupPatternCache` / `_compactCostMemo` /
    `_compactRealStepsMemo` / `_phase1bSolved`），让搜索在新的天花板下重新求解；跨阶段的 `_rootProvenLowerBound`
    则**刻意保留**，使收紧后的 compact 计划仍带着 `L`。**当某个预算 `N` 被证明不可行时**，把 `_rootProvenLowerBound`
    提升到 `N+1`（`RecordRootProvenLowerBound(budget + 1)`，恰等于 incumbent 的 `MaxStep`）——搜索此时已证明
    `opt ≥ incumbent.MaxStep`，而 incumbent 又达到了它，于是 `L == S` 的挤压收敛为 `max steps = S (proven optimal)`。
    编排层（CLI / GUI）在收到「证明不可行」终止阶段时，用 `WithRootProvenLowerBound` 把当前 incumbent 计划的挤压闭合（见下「Anytime 呈现」）。
    **⚠️ 可靠性（soundness）前提**：上述「不可行 ⇒ 证明最优」**仅在可行性探测是完备预言机时才是严格证明**。而 feasibility-only 探测的
    `SolveBudgetFeasibility` 每个状态最多枚举 `CompactGreedyCandidateCap` 个分组（不完备的带帽贪心）；一旦帽子**真的截断**了
    某状态的枚举，「没有分组塞得进预算」就**并非**「不存在 ≤N 的策略」的证明——某个没被试到的分组可能其实可行。因此
    `ProbeAndClassify` 会在同一个预算 `N` 上自动扩容重试（起始于 `CompactGreedyCandidateCap`，按固定倍率 `x4` 递增直到
    `int.MaxValue`）：只要本轮判负且检测到截断（`_lastProbeEnumerationCapped = true`）就继续放大帽子重跑，直到得到
    **未截断**的结论。于是常见场景会从“帽子截断的未定”收敛到真正的 `NoSolution`（可提升 `L`、闭合挤压）或 `Tightened`。
    只有在已经扩到 `int.MaxValue` 仍保持截断（理论极端）时才会发 `Incomplete` 终止阶段——像超时一样保留 incumbent、**不**提升 `L`、
    **不**声称已证明最优（挤压保持 `L <= max steps <= S` 开区间）。误差方向仍是单边保守：截断最多导致漏证最优，绝不会假性宣称最优；
    incumbent 计划本身始终是合法可达策略。
    **无时间上限**：收紧循环只在触达 `L`、证明 `NoSolution`、遇到 `Incomplete`、候选未变优、或**用户取消**时停止。
    早先版本设过一个「软时间预算」（`max(2000ms, 基线×4)`）让 CLI 能自行停下，但其唯一目的就是「到点停」，不如让搜索
    跑到底、由用户在等够时手动停（GUI 的 Stop / CLI 的 Ctrl+C），故已整体移除。用户取消经由 `ThrowIfCancellationRequested`
    抛出 `OperationCanceledException` 向上传播，最优计划已通过 `onStage` 逐阶段呈现、不会丢失。这样小/中形状几乎瞬间把
    `U` 收到最优（如 `8,3,3: 6→5`、`14,5,5: 6→5`、`13,4,4: 7→6`），大形状（如 `25,10,10`）会一直收紧直到证明或被取消；
    少数 `U−1` 可行但极慢的形状会持续运行，用户可随时停止（无回退、无正确性风险）。`EnableFeasibleTightening = false` 可整体关闭。
    为减少后续算法演进时「取消检查点漏加」导致的 stop 延迟回退，热循环统一走 `ProbeCancellation(...)`：
    默认使用节流探针（低开销），延迟敏感的递归路径用 `ProbeCancellation(0)`（每步检查）保持响应性。
    对外行为由回归测试守门：`GreedyPipeline_CancelAfterTwoSeconds_StopsPromptly_20_2_6` 模拟运行 2 秒后取消，
    约束取消到退出的延迟不回到分钟级卡顿（anti-regression guardrail，而非严格性能基准）。
  - **Anytime 呈现**：`RunGreedyPipeline(onStageCompleted)` 接受一个回调，在**每次**产出一个阶段结果时**同步**触发——
    回调参数是 `StageResult`（阶段名 + 该阶段**自身**耗时 + 可空的计划 + `Outcome` 枚举）。**强输出契约**：每个被
    `onStageStart` 宣布的探测都**恰好**由一次 `onStageCompleted` 完成、携带一个 `{Outcome, Plan}` 整体——driver 任何分支都不会
    「有 candidate 却不发结果」。`Outcome` 区分**四种**互斥结局：
    `Tightened`（realize 出满足天花板的计划、严格优于 incumbent，是唯一会继续收紧的结局）、
    `ProvenInfeasible`（探测在**完整枚举**下跑完、证明该天花板无解 ⇒ incumbent 即最优、闭合挤压）、
    `Incomplete`（探测跑完但**贪心候选帽子 `CompactGreedyCandidateCap` 截断了某状态的分组枚举**，「没有分组能塞进预算」
    因此**并非无解的证明**——可能存在没被枚举到的分组其实可行；incumbent 照常保留、**不**闭合挤压）、
    `Completed`（在收紧确定的最小可行步数 `S` 上的**最终 min-edge 阶段**，并非步数收紧探测；总是物化并携带最终返回的计划，
    是终止阶段、其后再无阶段）。**关于 overshoot**：曾经有第五种结局 `Overshot`（compact 可行性 proxy 判为可行、但物化出的
    `MaxStep` 越过天花板）。自 PR #223「保留最紧 budget 的 pattern」修复后，proxy 与物化不再背离——完整（未被帽子截断）探测
    只要 realize 出计划就必然满足天花板，因此 overshoot 已降级为**内部不变量被破坏**：`ProbeAndClassify` 在遇到越界计划时
    直接抛 `InvalidOperationException`，而非作为结局上报（`(20,4,6)` 曾是它的实测样例，现已收紧至步数 14）。便捷属性：
    `IsTightened`＝是否可继续收紧（仅 `Tightened`）；
    `HasPlan`＝是否附带物化树（`Tightened` 与 `Completed` 均为真，仅供显示，**不**代表改进）；`ProvesOptimal`＝`ProvenInfeasible`；
    `IsCompleted`＝`Completed`。`Tightened`/`Completed` 携带计划，`ProvenInfeasible`/`Incomplete` 计划为 `null`。先是若干 `proof-tighten≤N` 收紧阶段（每次成功各一个），
    随后是在最小可行步数 `S` 上的**唯一一遍 min-edge** `edge-compact@S` 阶段，中途可能穿插一个 `no solution` 或 `search incomplete` 终止阶段。
    **呈现以「是否
    严格优于当前 incumbent」为准**（incumbent 初值为 `greedy-feasible` 可行解，按 `IsStrictRefinementOver`＝先比步数再比边数）：
    只有严格更优的阶段才被画成完整的可浏览树并更新 incumbent；**有解但不更优**的阶段（例如最终 `edge-compact@S` 步数与边数都与
    上一 `proof-tighten≤S` 阶段相同、无法再降边，见 `20,5,5`）记录下来、标 **`no improvement`**，但只渲染成一行注记、不画那棵重复的树；
    收紧**仍照常继续**（下一个天花板由步数决定）。收到 **`no solution`（证明不可行）** 终止时，编排层把当前
    incumbent 计划用 `WithRootProvenLowerBound(incumbent.MaxStep)` 闭合挤压（CLI 改写 `finalPlan`；GUI 走
    `MarkGreedyIncumbentProvenOptimal`，同时改写 `_compactPlan`/`_feasiblePlan` 与 `_proofTightenStages` 里对应那一项），
    于是详情面板显示 `max steps = S (proven optimal)`；**`search incomplete (candidate cap reached)`**
    （即 `Incomplete`）终止则只标注、不闭合（文案强调是帽子截断导致的「没算完」，属「未证明」）。**CLI 与 GUI 在此分道**：CLI 是批处理工具，逐棵打印中间树
    太啰嗦，故只收集各阶段、打印一行
    `progression: greedy-feasible(steps=, edges=) -> proof-tighten≤N(...) -> … -> edge-compact@S(...)[: no improvement][ -> proof-tighten≤M: no solution|search incomplete (candidate cap reached)]`
    总结，随后**只打印最终（最优=incumbent）那一棵树**（若没有任何阶段更优，最终树就是 `greedy-feasible` 本身）。**CLI 的 Ctrl+C**：
    `RunHeadless` 挂一个 `Console.CancelKeyPress` 处理器（`e.Cancel = true` 阻止进程被硬杀、转而取消一个
    `CancellationTokenSource`，其 token 传入 builder），greedy 构建外包一层 `try/catch OperationCanceledException`——
    收到取消时在 progression 末尾追加 `-> interrupted`、标题加 `[interrupted]`，照常打印**已找到的最优树**（挤压保持开区间、
    不声称最优），而非丢掉全部输出；exact 模式无部分树可展示，取消时只打印一行 `interrupted`。GUI 才用 anytime
    增量呈现：用**同步 `Control.Invoke`**（而非
    `Progress<T>`）把回调从工作线程 marshal 回 UI 线程——Invoke 会阻塞工作线程直到处理
    完成，这正是「每阶段弹窗暂停」（默认关闭的 `pause each stage` 开关）得以真正暂停搜索的机制。**弹窗期间一律停止计时**：
    GUI 端在 `MessageBox.Show` 前后 `_runStopwatch.Stop()/Start()`（续计、不重置）——于是用户停留在对话框里的时间不计入总
    `elapsed`、也不计入本阶段时钟（引擎侧已无收紧时间预算，无需再暂停任何预算秒表）。每个**更优**阶段**新增一棵树**
    （`no improvement` / `no solution` / `search incomplete` 阶段只新增一行注记），树根与 overview 用统一标签
    `阶段名: elapsed=…, max steps=…, edges=…, output=…`
    （`elapsed` 为该阶段自身耗时、秒、3 位小数；不更优时标 `no improvement`，证明无解时标 `no solution`，
    帽子截断致未算完时标 `search incomplete (candidate cap reached)`）。
    树形区域的**总根节点**以最优挤压（`FormatPlanSqueeze`）打头——`n=…, m=…, k=…, <squeeze>, total elapsed=…`——
    其中 `<squeeze>` 在最终 `no solution` 终止后闭合为 **`max steps = S (proven optimal)`**（最显眼的「搜索完成、步数已证明最优」信号），
    收紧途中则为 `L <= max steps <= U`；若是对偶约减后的退化实例（如 `k' = 0`），头部仍显示用户请求的
    `k`，并附 `dual k'=...` 注记，同时把挤压直接闭合为 `max steps = 0 (proven optimal)`，不再显示
    `? <= max steps <= 0`。旧的 `(compact lowered from N)` 注记已移除（用处不大）。total elapsed 也用秒（3 位小数）。
    overview 的 round 折叠规则按「连续、单分支、同组大小、且各步分组彼此不重叠」聚合；因此除首轮
    `steps 1–X` 的全量分组外，后续复用旧元素但仍呈现规则分块的波次（如 `20,2,6` 的 `steps 11–15`）也会被折叠成单个 round。
    GUI 的「`<stage>: computing...`」占位提示在实现上统一为一套生命周期（生成 / 识别 / 替换 / stop 改写）：不仅用于
    `proof-tighten≤N` 与 `edge-compact@S` 之间的探测过渡，也用于首阶段（`greedy-feasible` / `step-proof`）尚未产出首棵树时的
    初始占位，保证树区与 overview 在整个运行期都不会出现空白且文案行为一致。
    进度面板恒为四行：总 `elapsed` 秒数、
    `阶段名: 本阶段秒数`、`progress: 本阶段百分数`、第四行 **`eta`**（UI 直接由 `elapsed` 与 `progress` 按
    `remaining = elapsed * (1-progress) / progress` 推导的剩余时间）。早先版本在 `proof-tighten≤N`
    收紧阶段把第四行改标 `time remaining`、显示「距离软时间预算 timeout 还有多久」；软预算移除后该行统一为 `eta`。
    GUI 的各开关 / 参数（n/m/k、模式、主题、
    pause each stage）持久化到 `%APPDATA%/Sort/settings.json`，下次启动沿用上次设置。
- `StrategyPlan.IsFeasibleUpperBound == true` 标记这棵树是「可行上界」而非「精确最优」，CLI / GUI 据此渲染相应的
  首阶段（`greedy-feasible`）区域。

#### Overview 的规整链折叠（`m=2`）

`StrategyOverviewRenderer` 在渲染 representative spine 时，除了已有的「同尺寸 disjoint wave」折叠外，
新增了对 `m=2` 常见「锚点-挑战者」规整链的汇总：当出现连续单分支 pair 比较，且同一锚点持续与不同挑战者比较（长度 ≥ 3），
overview 会把该段折叠为一行 `compare (#anchor) against N challengers`，并附一行挑战者区间与淘汰摘要。

例如 `25,2,1` 的 greedy-feasible representative 路径会从 23 条逐步行压缩为：

```text
Round 1 · steps 1–23: compare (#1) against 23 challengers
    challengers: (#2 ~ #24)
    each sort drops its bottom 1 → 2 still in contention
Finish · step 24: choose 1 of (#1, #25) for the last slot
```

该折叠只影响 overview 展示，不改变策略树结构与 step/edge 语义。

### 4.7 GreedyTighten（Phase 0）：可行树的局部改造收紧（已实现，已接入生产管线，root-probe gated）

> ✅ **状态：已实现并接入 greedy 生产流水线**，以 root-probe gate 控制是否执行。当前接入策略是：`greedy-feasible` 后先做
> `ShouldRunGreedyTightenByRootProbe`，仅在 probe 通过时运行单轮 `BuildGreedyTightenPlan`；且仅当 GT 结果严格优于
> `greedy-feasible` 才覆盖后续 proof-tighten 的起始上界（`OverrideGreedyPipelineUpperBound`）。该阶段本身仍**无证明语义**（只有
> `ProofTighten` 的完整枚举 no-solution 才证明最优）。

**流水线位置**：可选插在 `greedy-feasible(U)` 与 `proof-tighten≤N` 之间：

```text
greedy-feasible(U) → (optional) greedy-tighten → proof-tighten≤N → edge-compact@S
```

`greedy-feasible` 用单层构造式选择器给出可行上界 `U`；`GreedyTighten` 在**已有可行树**上做**局部分组替换**，把最长路径
（`MaxStep`）尽量压低到 `U'`（最好直接到 `opt`）；随后 `ProofTighten` 从 `U'−1` 继续严格收紧并证明。

**目标（为什么要有它）**：`ProofTighten` 的枚举探测很贵，越靠近 `opt` 越贵。GreedyTighten 想用**廉价的局部改造**先把上界降下来：

1. **主要收益**：当 `U'` 触到 `opt`，`ProofTighten` 只需跑一次 `opt−1` 的不可行证明即可收口，跳过 `U−1…opt` 之间所有可行性探测。
2. **次要收益**：即便 `U' > opt`，`ProofTighten` 也从更低起点开始，省掉上端便宜的松探测。
3. **独立收益**：在 `ProofTighten` 跑不完的大 `m` 形状上，GreedyTighten 仍改善展示出的可行树（更小 max-step、更好的 anytime 结果）。
4. **成本刻意压低**：只碰最深路径、每状态候选 `cap=128`、memo 高度、不做完整枚举——下行风险有界。
  对应代码中的默认值现集中为 `DefaultGreedyTightenCandidateCap = 128`，与 compact 阶段的 cap 分开命名；保持默认值时同样按
  `activeCount * groupSize` 搜索面做最多 `4x` 的温和自适应放大，而显式 override 仍按给定值精确执行。

**反证条件（发生什么就证明不该加入，预先登记）**：在代表性算例集上实测，命中任一即判死：

1. **几乎不收紧**：绝大多数算例 `U' == U`。
2. **触底率太低**：`opt < U` 的算例里 `U'` 极少等于 `opt`（只降到 `U'>opt` 只能跳过上端便宜探测，昂贵的 opt / opt−1 探测照跑——即旧 `D_g` precheck 的失败模式）。
3. **净变慢**：开启后 greedy 模式端到端总耗时整体变差（改造成本 > 省下的探测时间）。
4. **两头不讨好**：既不帮 `ProofTighten` 更快收口，也不改善跑不完形状的展示树。

**多轮结构**：

- **一轮内**：按「关键路径后序 + AND 短路剪枝 + 成功即提交」遍历**当前最深路径集合**上的状态。
- 某次提交**使根深度下降** → 本轮立即结束 → 进入**下一轮更紧的 greedy-tighten**，**从头重算**（新的最深路径集合等全部重算）。
- 某次提交**未改变根深度** → 仍在本轮：最深路径集合里其它支不变，只更新**被改这一支自身的祖先链**（有些祖先掉出最深路径集合、有些仍在，在高度逐级回传时算出）。
- **整体停止**：某一整轮扫完没有任何提交能让根下降时，Phase 0 收敛。

**遍历（关键）**：关键路径后序——后序 DFS（叶→根），每层只递归进入**当前最高孩子集合**；父状态 `P` 要降高须其所有最高孩子都降（**AND** 关系）；任一关键孩子降不下来即**短路**，不试其关键兄弟，向父返回失败。

**单状态改造规则**：对状态 `S`，记当前子树高度 `L`；生成候选（排除当前 `group0`）；用轻量启发式分**仅排序**；按序尝试（`cap=128`）：真实重算 `L'`，若 `L' < L` 立即接受+提交+把高度逐级回传到根+停试该状态；否则回滚试下一个；全失败则回父层。**接受语义**：局部子树高度严格下降即接受（不要求根先降）；局部提交**累积且永久**，后续状态在含先前提交的新基线上继续；根高度只决定是否结束本轮。

**子树高度重算（点 1 决策）**：

- **后代策略**：GreedyTighten 一次只**覆写被编辑状态自身的分组**，`S` 以下的后代仍走 greedy-feasible 的构造式选择器。用**全局覆写表** `override: stateKey → group` 统一表达；任意状态分组 = 有 override 用 override、否则贪心。
- **度量一致性**：阶段内部搜索/比较统一用**精简构造深度**（lean depth，`height(state)=1+max(height(child))`，预算无关、按状态 key memo，与后代策略一致）；仅在本轮结束、把 `U'` 交给 `ProofTighten` 前，对已提交的树**物化一次**算真实 `MaxStep`。
- **落地形态（选 B）**：搜索期不物化，只跑 memo 化高度 DP + override 字典；最终才物化一次用于展示/交接。memo 失效只沿含 override 的祖先链发生。
- **物化安全护栏（已落地）**：在 `GreedyTighten` 物化路径上维护当前 display-key 递归栈；若某个 override 分组的 outcome 会回指到栈上祖先 display state（形成 back-edge），该 override 会被丢弃并回退到 greedy-feasible 的构造式分组。若回退后仍无法保持 display 进展，则 fail-fast 抛异常，避免产出 malformed reference graph。

**实现分期**：阶段 A 先落**主体框架**（多轮 / 遍历 / 单状态改造 / 接受语义 / override 表 + 高度 DP / 与 ProofTighten 衔接），候选来源第一版只用现成的 antichain/构造式候选枚举 + 占位排序；阶段 B 再做**候选来源多样化（seed 变体 / 1-swap 扰动 / 桥接）+ 评分器调优**（效率与收效的主要调优点）；除 `cap=128` 外 v1 不加额外预算控制，按实测再定。

**默认轮数（已实现，实测定档）**：驱动器**默认只跑单轮**（`DefaultGreedyTightenMaxRounds = 1`）。修复同构覆写串味 bug（override/高度 memo 以规范键为键、组按具体标号存取，见 PR #216）后的重基线（`nMax=10`，320 例）显示：单轮在 305/320 例达到与无界多轮相同的收紧 `U'`，成本仅约 0.47x；多轮平均只多收紧约 1 例却成本翻倍。多轮循环与跨轮 override 持久化仍保留，通过测试/评测开关 `GreedyTightenMaxRoundsForTesting`（设更大值或 `int.MaxValue` 跑无界）驱动，供后续调优使用。

**soundness 校验（独立于精确搜索）**：GreedyTighten 只保证可行上界（`U' >= opt`）。在 `n <= 10` 用精确 `opt`（`BuildStepProofStage`）对照即可验证，但更大形状下精确搜索不可行。为此提供独立校验 `ValidateGreedyTightenPolicyDepthForTesting`：物化后从根**重放已提交策略**（不复用高度 memo），逐状态断言分组有未决对（progress）、对抗路径无环（必然终止）、每条路径都停在受信任的 top-k 终止条件，并重算最坏深度；返回深度 == 计划的 `MaxStep` 即证明该 `MaxStep` 是一棵**真实合法策略**的最坏步数（因而是 opt 的可靠上界）。这把 GreedyTighten 的正确性锁到精确搜索够不到的规模（例如 `20,4,6`：GT 给出并被验证的合法 `U'=14`）。

**当前定位（2026-07-10）**：已并入 greedy 生产管线，但通过 root-probe 做保守 gate，避免在明显无收益形状上支付 GT 成本。流水线层面的回归测试同时锁定两类行为：

1. 有收益路径：当提供更紧的可行上界种子时，proof-tighten 首个预算更紧，且最终结果不劣于基线。
2. 无收益路径：probe 判 skip 时，起始预算与最终结果均与基线一致。

这使 GT 的引入从“总是执行的额外开销”变成“按形状触发的可选预收紧步骤”。

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

相关代码：`ComparisonState.ComputeCanonicalForm`、`GetCanonicalKey`、`GetGroupCanonicalKey`。

### 5.1 规范键的跨实例记忆化（`GetCanonicalKey` 已落地；`GetGroupCanonicalKey` 已探测无收益）

McKay 规范化是搜索的实测**头号 wall-time 成本**（量化中 exact 模式 ~72% 的墙钟耗在 canonical 计算上）。同一个逻辑偏序会沿不同搜索路径被反复到达，每次都是一个**新的** `ComparisonState` 实例（各自带自己的 per-instance 缓存），因此规范化会被从头重算。

- **搜索键 `GetCanonicalKey`（已落地）**：在 builder 层增加一个「廉价原始结构指纹 → 昂贵规范键」的记忆表。指纹是 `(ActiveMask, {各 active 顶点的 ancestors&ActiveMask})`——`ComputeCanonicalForm` 只读这些量（descendants 是 ancestors 的转置），故指纹**完全决定**规范键，记忆化无损。该表在一个 builder 实例内跨阶段（feasible/exact/compact）永不清空，命中的是**跨实例**重算。实测 greedy 20,5,5 提速 ~32%、exact 16/18/19,5,5 提速 15–23%，`maxStep`/边数不变。相关代码：`RawStructureKey`、`GetRawStructureKey`、`_canonicalKeyMemo`。

- **组键 `GetGroupCanonicalKey`（已探测，结论：无收益）**：同样想法在这里**不划算**。组键的 McKay 计算发生在 `EnumerateDistinctGroups` 的**消歧**环节——先用廉价 signature 分桶，只对同桶冲突的组才跑 McKay，这些组按设计就是要区分开（产生不同键）。因此用 `(结构指纹, groupMask)` 做键的跨实例可命中率**天然极低且随规模下降**：实测 exact 16,5,5 为 16.0%、18,5,5 为 10.8%、19,5,5 仅 4.7%、greedy 20,5,5 仅 3.5%。在真正耗时的规模上 ≤5% 的可省调用很可能被每次查表的指纹构造 + 字典查找开销抵消乃至反超，故**不引入**该缓存。

- **实例内 mask 记忆化（已落地）**：`ComparisonState` 现在对 `GetDisplayCanonicalKey(fixedTopMask)` 与 `GetGroupCanonicalKey(groupMask)` 做实例级按 mask 缓存（状态发生变更时统一失效）。这不是跨实例缓存，不引入 raw-key 构造成本；收益来自同一状态在展示合并/组模式回放里对相同 mask 的重复请求。对 `20,2,6 --mode greedy --stage 1` 的 A/B，阶段耗时约 `148.93s -> 48.06s`，语义与步数不变。

- **RefineCanonicalColoring 热路径去冗余（已落地）**：`RefineCanonicalColoring` 内曾维护一个恒等映射 `order[i] = i`，随后回写 `nextLabels[order[perm[r]]]`。该映射在函数内从不改变，因此可等价替换为 `nextLabels[perm[r]]` 并移除 `order` 数组及对应写入。该改动不改变着色语义（严格等价），仅减少热循环中的数组分配/写入/一次索引间接访问，属低风险常数项优化。

- **RefineCanonicalColoring 循环内缓冲复用（已落地）**：进一步把 `RefineCanonicalColoring` 的循环内临时数组改为复用：`sig` 预分配到最坏宽度（individualization 后类数上界约 `2a`，对应签名宽度上界 `1+4a`），每轮仅清理本轮使用区间；`nextLabels` 与 `labels` 通过双缓冲交换避免重复 `new int[a]`。该改动不改变比较顺序、颜色分裂条件与收敛判定，仅去掉热路径分配与 GC 压力；针对 `20,2,6 --mode greedy --stage 1` 的同机 5 次采样，均值约 `14.21s -> 9.93s`，中位数约 `14.37s -> 10.06s`。

- **RefineCanonicalColoring 首轮颜色稠密化（已落地）**：`CanonicalizeRecursive` 的 individualization 会生成 `2*c` / `2*c+1` 形式的颜色，首轮可能出现较大标签间隙（例如上界接近 `2a` 但实际类数远少于该上界），从而放大签名宽度与清零成本。现在线程在 `RefineCanonicalColoring` 入口先做一次“保序稠密化”（仅压缩编号间隙，不改变颜色等价类与相对顺序），把首轮签名宽度收敛到真实类数附近。该改动不改变规范化语义，只减少首轮常数开销；在 `20,2,6 --mode greedy --stage 1` 同机 5 次采样中，均值约 `9.93s -> 9.44s`，中位数约 `10.06s -> 9.34s`。

- **展示轨道里的 1-WL 预筛（已落地）**：虽然 1-WL 颜色细化**不能**单独当作完整规范不变量，但它仍可安全地当作**必要条件**预筛。具体地，`StrategyBuilder.DoomedTailEdges.cs -> PartitionDoomedBucketsIntoOrbits` 在做 `O(buckets^2)` 的 `TryMapOrderByAutomorphism` 两两测试前，先比较 doomed prefix 的**逐位置 active 颜色序列**；任一 automorphism 都必须保持父态 active poset 的 1-WL 颜色，因此颜色序列不同的两条 prefix **不可能**处于同一 automorphism 轨道，可直接跳过昂贵的回溯匹配。这个预筛不改变轨道划分结果，只减少无效的 automorphism 调用；实测在 `24,10,10` 上把 automorphism 检查从约 `6.36M` 次降到约 `2.5K` 次，greedy feasible 阶段约 `2.02s -> 1.00s`，`U` 与渲染结果不变。

- **父态轨道划分里的 1-WL 预筛（已落地）**：同样的必要条件也用于 `StrategyBuilder.Transitions.cs -> PartitionFamiliesIntoOrbits`。在 merged bucket 的 parent-orbit 两两并查前，先比较两个代表排序在父态 active poset 上的**逐位置 active 颜色序列**；序列不同则不可能由父态 automorphism 互映，直接跳过 `TryMapOrderByAutomorphism`。这只减少无效 automorphism 回溯，不改变 orbit 分区与渲染语义。针对 gated `(m=5,k=5)` 大 case（`n=12..18`）的探针计数显示，该预筛在这些 case 上可拦截大量不可能配对（例如 `18,5,5` 里 parent-orbit prefilter skips = `2531`，真实 parent-orbit automorphism checks = `0`）。

- **投影轨道合并里的 1-WL 预筛（已落地）**：同样的必要条件也适用于 `StrategyBuilder.Transitions.cs -> TryProjectionAutomorphism`。在把两个 parent orbit 的代表送入投影自动同构回溯前，先比较它们在投影后状态里的逐位置 active 颜色序列；若序列不同，就不可能由任何投影自动同构互相映射，可直接返回 `false`，避免进入更贵的回溯匹配。这个过滤同样不改变合并结果，只减少无效的 projection-automorphism 调用。

- **投影态按 `commonDrop` 复用（已落地）**：`TryProjectionAutomorphism` 在 `commonDrop != 0` 时需要构造投影态（`Clone + Deactivate(commonDrop)`）并取投影态颜色。投影轨道扫描中同一个 `commonDrop` 会反复出现，因此在每个扫描批次内按 `commonDrop` 记忆化 `projected state + colors`，让后续比较直接复用，避免重复构造与重复着色。这个缓存只改变执行开销，不改变任何 automorphism 判定结果。

---

## 6. 下界（lower bounds）

`GetMinWorstCaseLowerBound` 返回该状态最优步数的一个**合法下界**（保证 `≤` 真实最优），供
alpha-beta 剪枝使用。它取多个下界的**最大值**（谁更紧用谁）：

```csharp

// 1) 信息论下界
//    while (distinguishable < info.Count) { steps++; distinguishable *= maxOutcomesPerStep; }
// 2) 反链宽度下界
steps = Math.Max(steps, GetAntichainLowerBound(state));
// 3) 可判定性下界（determinability floor，见 7.7）
steps = Math.Max(steps, 2);
// 4) 支配下界（放最后：它最贵，且能吃到前几步抬高的 seed 做便宜预筛）
steps = ApplyDominanceLowerBound(state, remainingSlots, steps);

```

**计算顺序**：`max` 与顺序无关，返回值恒等；但把便宜的下界排在**贵的支配下界之前**能省计算——`ApplyDominanceLowerBound` 会跳过「cost ≤ 当前 best」的库条目（见 6.3），所以先让 floor 把 best 抬到 2，可令所有 `cost ≤ 2` 的条目在跑昂贵的回溯嵌入之前就被丢弃。这一重排是保值的（`maxStep`/边数/缓存值都不变），支配抬界次数相应下降（少做了 floor 已能免费覆盖的那部分）。

### 6.1 信息论下界

一次比较最多产生 `maxOutcomesPerStep` 种不同结果，所以 `s` 步最多区分
`maxOutcomesPerStep^s` 种情形。要区分 `info.Count` 个可行 top-k 集合，至少需要
`s` 使得 `maxOutcomesPerStep^s ≥ info.Count`。这个下界在**深处**（可行集合少时）较紧，但在
**根附近**很松——恰恰是最需要剪枝的地方。

### 6.2 反链宽度下界（本项目新增）

这是与信息论下界**互补**的解析下界，在**根附近最强**。下面详细讲。

### 6.3 支配下界（dominance）

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

### 7.2 一次比较最多让宽度减少 `m - 1`

一次比较把最多 `m` 个元素排成一条链，这 `m` 个元素**两两变得可比**。

**关键观察**：任何一条反链与一条链**最多共享 1 个元素**（反链内两两不可比，链内两两可比，交集
不超过 1）。于是原本反链里落入这 `m` 个的元素，比较后最多只能保留 1 个 → **反链最多缩小
`m - 1`**。

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

### 7.5 下界差距诊断（20,5,5 ≤6 证明，实测）

为回答「现有下界离真值有多远、松弛是否有可利用的结构」，在 `20,5,5` 的 `≤6` 完整**证伪**过程中，
对每个进入 `SolveBudgetFeasibility` 的状态记录 `gap = opt(S) − LB(S)`（`opt` 由标准 exact
搜索复算）。样本去重后 **4978 个状态**：

| 深度（剩余预算） | 状态数 | gap=0 | gap=1 | gap=2 | gap≥3 |
| --- | --- | --- | --- | --- | --- |
| budget≤1 | 3605 | 0 | 60.8% | 39.2% | 0 |
| budget≤2 | 1197 | 5.5% | 75.9% | 18.6% | 0 |
| budget≤3 | 155 | 3.2% | 67.1% | 29.7% | 0 |
| 根链（≤4..7） | ~20 | — | 少量 | 主导 | 0 |

两条结论：

1. **下界处处很近但系统性偏松**：`gap` 永远只有 1 或 2，**从不 ≥3**，`gap=0`（恰好紧）极罕见（~71 个）。
   没有任何「离谱状态」，是全局一致的 `+1/+2` 松弛。

2. **松弛量随 `activeCount`/`width` 单调增长**（`gap × activeCount` 交叉表）：活跃数小（6–11）几乎都是
   `gap=1`；活跃数大（15–20）几乎都是 `gap=2`；交叉点在 `activeCount ≈ 13–15`。`gap × width` 同理：
   `gap=2` 随宽度增大而增多。**松弛是一个廉价可算的结构量的函数**——这正是强化下界（Direction A）的抓手。

**可攻击性判断**：

- **高宽度处的 `+1` 可回收（值得做）**：宽度界 `⌈(w−1)/(m−1)⌉` 在宽度大时偏松（`20,5,5` 根 `w=20→5`，
  真值 7）。根因见 §7.6——它只建模「收窄反链」，忽略了「还要选出 `k` 个 top」这个并发约束。这类状态数量少
  但每个罩着巨大子树，剪枝性价比高。
- **深层小状态的 `+1` 不能靠前瞻关闭（陷阱）**：给一个 `budget=1` 状态证明 `opt≥2` 等价于「无单组可解」=
  一层 exact 前瞻，正好是该状态本就要做的 ~1127 次组试探，把它挪到父节点当界算，省下的等于花掉的，净收益零。
  这部分（占 96% 状态）只能靠真正的**解析 `O(poly)` 洞察**，属未解难题。

### 7.6 待强化方向：联合「收窄 + 选取」宽度界（已探测，结论：无近期工程解）

现有宽度界把每步的效力乐观地全部算作「反链缩 `m−1`」，但一次比较其实要同时服务两个目标：**收窄未定偏序**
与**逼近选出 `k` 个 top**。当宽度 `w` 远大于 `k` 与 `m` 时，二者互相牵制——不可能每步都把 `m−1` 的收窄
「纯赚」，因为分出的链还得贡献 top-k 的判定。诊断中根部 `gap=2` 与「高宽度⇒gap 更大」的单调性都曾指向此处
有 `+1` 可回收的解析空间。

**探测结论（三条独立方向收敛为同一判断）**：

1. **反链 `+1` 猜想（≥2 条最长反链 ⟹ 界 +1）——不 sound**。全枚举 `n≤5 × 全 m × 全 r`：10.3 万个
   「≥2 条最长反链」状态中 **93%（9.6 万）是反例**（opt 恰等于原界，+1 会 overshoot），且反例遍布所有 `m`。
   两条不同的最长反链可共享 ≥2 元素的公共核，单步仍能打满收窄，故猜想不成立。

2. **联合宽度界（方向 A）——残余缺口 = 开放的选择比较复杂度，无 sound 闭式**。在最纯粹的宽状态（全空
   poset，`w=n`）上全枚举 `n=4..9, m=2..5`：
   - **信息论界在宽状态上 100% 冗余**，唯一活跃的界是 `⌈(w−1)/(m−1)⌉`，它只为「收窄到 1 个最大值」付费，
     **完全不为「要选 r 个」付费**——正是病根。
   - 残余缺口 = opt − 界真实存在，且随 `n` 增大、随 `m` 减小（`m=2` 峰值缺口达 5，如 `n=9,m=2,r=4`：界 8、
     真 opt 13）；缺口只在中间 `r` 出现、关于 `r` 对称。
   - 这个缺口精确等于「在找到最大值之外还要选出 `r` 个的额外代价」，即一般 `(n,r)` 的**最坏情况选择比较复杂度**
     ——组合数学里至今的**开放问题**，无可 `O(poly)` 计算的闭式。多组拟合均失败或 overshoot，找不到可证 sound
     且严格强于宽度界的多项式公式。

3. **递归可判定性 floor（方向 B，`opt≥3` 泛化）——真实但无便宜 sound 触发**。窄状态（`width≤m`）在 `n≤6`
   已被生产界打满（gap 恒 0）；在「窄而高」的并行链状态（`n≤12`）确有 233 个可回收的 `opt=3`（如 `n=7,m=3,r=3`
   并行链 `3|2|2`），但 opt=2 vs 3 的边界依赖链长分布与 `r` 的精细交互，任何便宜的聚合量（activeCount、width、
   `#长链`）都无法 sound 地区分——属研究级问题。

**总判断**：残余缺口锚定在开放理论难题上，唯一「机械可行」的 sound 强化是预存小 `n` 的精确 opt 表当下界，但那
等于把 exact solver 塞进下界、性价比不划算且救不了大 `n` 根节点。已落地的 §7.7 可判定性 floor 是这轮唯一确凿的
结论性胜利；继续缩下界的解析探索到此归档。

### 7.7 可判定性下界（determinability floor，已落地）

§7.5 判断「深层小状态的 `+1` 不能靠前瞻关闭，只能靠解析 `O(poly)` 洞察」。这里给出正是这样一个洞察，
并且它是 `O(1)` 的：

**定理**：一个已归一化、非终止的状态若 `activeCount > m`，则 `opt ≥ 2`（单步不可判定）。

**证明**：`opt=1` 要求存在某组 `G`（`|G|=m`）使其**每一种**结果排列都得到已判定（top-`r` 唯一）状态。
因 `activeCount > m`，存在活跃元素 `f ∉ G`。把 `G` 排成链：`f` 的已知祖先放顶部、与 `f` 不可比的放中间、
`f` 的已知后代放底部。传递闭包不会给 `f` 新增任何关系（没有元素在其祖先之上、也没有在其后代之下，中间元素
与 `f` 仍不可比），故在该结果下 `f` 的祖先集仍 `< r`（不被淘汰）、后代集不变（不被强制入选），`f` 保持
严格未定——于是「含 `f` 的 top-`r`」与「不含 `f` 的 top-`r`」同时存在，状态未判定。故没有组能在所有结果下
判定该状态，`opt ≥ 2`。∎

归一化后走过 §7 各基准情形的状态必有 `activeCount > m`（`activeCount ≤ m` 已在基准情形返回 1），所以在
`GetMinWorstCaseLowerBound` 末尾对这些状态取 `max(steps, 2)` 即可，无需任何前瞻。它在活跃偏序近似为单链
（`width ≤ m`）时把界从 1 抬到 2，提前剪掉 `budget=1` 的叶层，连带砍掉巨大子树。

**作用域**：全局启用（greedy/feasible 计划与 proven-optimal 的 exact 搜索均适用）。该下界与所选计划无关，
恒为 sound；曾因 exact 回归基线假设界不变而临时门控，实测后确认它在两种模式下都净加速结论性求解，故去掉门控。

**实测 1 —— greedy/feasible（uncapped 完整证明，同机对比，`maxStep` 与基线完全一致）**：

| 规模 | 基线 | 加下界 | 加速 |
| --- | --- | --- | --- |
| 15,5,5 | 2.4s | 1.1s | 2.2× |
| 20,5,5 | 61.2s | 16.0s | 3.8× |
| 25,5,5 | 433.4s | 183.3s | 2.4× |

**实测 2 —— exact（`BuildStepProofStage` phase-1 纯搜索，Release，同 `maxStep`）**：

| 规模 | floor OFF | floor ON | 加速 |
| --- | --- | --- | --- |
| 14,5,5 | 566ms | 127ms | 4.5× |
| 15,5,5 | 790ms | 55ms | 14× |
| 16,5,5 | 1815ms | 436ms | 4.2× |
| 17,5,5 | 2342ms | 513ms | 4.6× |
| 18,5,5 | 4022ms | 679ms | 5.9× |
| 19,5,5 | 11082ms | 1086ms | 10.2× |

`maxStep`/边数在所有规模上均不变。较窄的 `m=3/4` 形状 wall-time 中性偏快（12,4,5 3.3×、12,4,4 2.4×、
13,4,3 2×；10,3,x 与 16,4,4 在噪声内）。注意：这些形状上少数 proxy 计数（searched states、candidate groups）
会小涨 1–8%——因为 floor 用粗糙的「2」替代了 dominance 的某些具体 raise，改变了等优 tie 的代表分支，
但真实工作量（outcomes/wall-time）中性或更好；相应回归基线（含 dominance `>=` 下限）已按实测同步更新。

这是自四轮排除（时间预算、把已有界改作状态剪枝、更强去重、渲染期等价类）以来第一个真正提升
**结论性**（更快地给出「找到更优解」或「证明不可行」）的改进，而非更快的 incomplete 退出。

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
| 状态 / 偏序 / 归一化 | `ComparisonState.cs`（`Eliminate`、`Deactivate`、`ActiveMask`、`GetDescendantMask`） |
| 规范形 / 对称约减 | `ComparisonState.ComputeCanonicalForm`、`GetCanonicalKey`、`GetGroupCanonicalKey` |
| 支配下界 | `StrategyBuilder.Dominance.cs`、`ApplyDominanceLowerBound` |
| 紧凑搜索变体 | `StrategyBuilder.Compact.cs` |
| 构造式可行解（greedy 模式 step / 可行上界 U） | `StrategyBuilder.GreedyFeasible.cs` → `BuildGreedyFeasibleStage`、`ChooseConstructiveGroup`、`ConstructiveRootUpperBound`；`StrategyPlan.IsFeasibleUpperBound` |
| 回归 / 计数监控 | `TopKFinder.Tests/StrategyRegressionTests.cs`、`DominanceMetricTests.cs` |
