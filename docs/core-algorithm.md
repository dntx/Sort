# Core Algorithm: Optimal Top-k Strategy Search

本文用中文系统地讲解本项目核心搜索算法的原理，包括问题定义、状态表示、归一化、极小化最坏步数的
minimax 搜索、对称性约减，以及三种剪枝下界（信息论下界、反链宽度下界、支配下界）。其中反链宽度
下界与 Dilworth 定理部分配有具体例子，面向没有相关理论背景的读者。

> 本文聚焦**搜索与剪枝**（决定策略树的形状与最优性）。输出层「如何把每条分支渲染成易读的
> `pattern: ...`」是一块正交的逻辑，单独见 [`strategy-output.md`](./strategy-output.md)。

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

```
a > b,  a > c,  b > d
```

但 `c` 与 `d`、`c` 与 `b` 之间还没比过。画成图（箭头表示「大于」，省略可由传递性推出的箭头）：

```
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

```
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
  （例如 `10,4,8`：8 → 10）。`BuildCompactPlan` **直接返回这个原始 compact 候选**（不再在 builder 内部跑第二遍
  default 兜底）。「永不劣于 default」的保证统一由**编排层的主线规则**承担：每个阶段算出一个解，只有当它
  **严格优于当前全局最优**（`StrategyPlan.IsStrictRefinementOver`：先比 MaxStep、再比边数，更小者胜）时才挂成
  新解，否则 do nothing。因此比 default 差的 compact 候选**永远不会被展示**（GUI 与 CLI 共用这一规则）。

**结论**：

| 阶段 | MaxStep | edge-count |
|---|---|---|
| **default** | 精确最优 | 某棵最优树的副产品，**非最小**；两条搜索路径可能各得一棵边数不同的合法最优树 |
| **compact** | 精确最优（只在等优组里挑） | 经专门优化；builder 返回的**原始候选**可能劣于 default，但编排层只在它**严格更优**时才展示，故**展示结果保证 ≤ default**，且因「代理最小化」而**仍非可证明的全局最小** |

> 换言之：compact 之后展示的边数是「带编排层安全裁决的启发式较优值」，把它称作「最优 edge-count」会过强。

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

> **跨阶段生命周期**：`_rootProvenLowerBound` 与 `_rootIncumbents`、`_rootSearchInitialized` 一样，都是
> **一次性 phase-1 求解的产物**，其生命周期由 `_phase1Solved`（**不**按 build 重置）界定。因此它们**不**在
> `ResetPerBuildTransientState` 里清零——否则随后的 compact build 会把已解出的 `L` 重置为 0，而 compact
> 阶段复用缓存、**不会重跑** IDA* driver 去重新记录，导致夹逼显示从「`opt = N (proven)`」回退成
> 「`? ≤ opt ≤ ?`」。其余按 build 重新统计的计数器（`searched` / `output` / cache 命中等）仍照常重置，
> 因为 compact 阶段会通过 `ObserveSearchState` / `VisitComparisonOutcomes` 重新填充它们。

### 4.6 构造式可行解（greedy 模式的 step 阶段）

精确 minimax 之所以会爆炸，是因为它在**每个状态**上对**所有**候选分组取 min、并且要**证明最优**。
greedy 模式把这两件昂贵的事都砍掉：只承诺一个分组（不做 min、不回溯、非最优），但仍展开所有对手分支，
于是整棵策略树是一个**单策略闭包**而非搜索树。

早期实现仍然靠**完整枚举**来挑这个分组（`EnumeratePrioritizedGroups → ~C(active, m)`），在大 `m` 上这个
「只为取第一名却枚举全部」的选择反而成了瓶颈（`25,10,10` 的 step 阶段约 49 s）。现在改成**构造式选择**
（`StrategyBuilder.Constructive.cs → ChooseConstructiveGroup`）：直接从当前偏序里以 `O(m·active²)` 现算分组，
**完全不枚举**。它是一场「保留偏序的锦标赛」——每一步竞争极大「前沿」（被证明更大的元素最少的那些项），
增量地拼出一个近似**反链**（两两互不可比、单次排序就能一次性解析最多对），既让新的 top 候选浮现，又把败者推向淘汰，
同时跳过所有已知关系。`25,10,10` 的 step 阶段因此从约 86 s 降到约 3 s。

```csharp
// StrategyBuilder.Constructive.cs -> ChooseConstructiveGroup（无枚举、无预解闭包）
// 增量反链：每次挑「与当前组内成员互不可比数最多」的活跃项，平局偏向前沿（祖先最少）。
List<int> group = ChooseConstructiveGroup(state, remainingSlots);  // O(m·active^2)
```

物化（materialize）阶段完全复用既有路径：`ChooseGroup` 在 `_useConstructiveSelection == true` 时**当场**调用
`ChooseConstructiveGroup` 算出分组（无需 compact/精确那样的预算 pattern 缓存，因为选择器本身便宜且确定）。
这样造出的策略树**结构合法**、可直接展示，其 `MaxStep` 就是**可行上界 `U`**（注意：`U` 不是已证明最优，只是一个
**确实可达**的步数）。正确性（`U ≥ opt`）只需「严格进展」：每次排序都至少新增一条比较关系——只要所选分组含有一对
互不可比项即可，而 `ChooseConstructiveGroup` 保证了这一点（总链兜底见 `ForceUnresolvedPair`）。

- **夹逼**：`L = GetMinWorstCaseLowerBound(root, k)`（解析下界，与精确搜索**无关**、极便宜；`25,5,5 → 6`），经
  `RecordRootProvenLowerBound` 写入；`U = ` 构造树的 `MaxStep`。于是 `L ≤ opt ≤ U`。若 `L == U` 则该可行解
  **恰好达到了已证明下界**，即**已证明最优**（显示 `opt = U (proven optimal)`）。
- **两种模式、各若干阶段**：编排层提供两条互斥的流水线，CLI 用 `--mode exact|greedy`、GUI 用下拉框切换。每个阶段都有一个
  统一的**阶段名**（也是 CLI 标题、GUI 树根与进度面板共用的标签）：exact 模式为 `exact → compact`；greedy 模式为
  `greedy → compact → compact≤N → …`（`compact≤N` 中的 `N` 是该次收紧的**目标步数上限**，并非已达到的步数）。
  收紧到某个 `N` 被证明不可行时，单独呈现一个标 **`no solution`** 的终止阶段。
  - **exact 模式（默认）**：`exact` = 精确求解 `BuildDefaultPlan`（已证明最优），`compact` = compact `BuildCompactPlan`。
    **不跑可行 feasible**。exact 的首阶段已是步最优，故其 compact 永远无法再降低步数，**没有**向下收紧阶段。
  - **greedy 模式（快速）**：`greedy` = 构造式 feasible `BuildFeasiblePlan`（可行上界 `U`），`compact` = 有界 compact
    `BuildFeasibleCompactPlan`（以 `U` 为步数上限收紧边数，可顺带「免费」拿到更小步数）。快速、可中断、非证明最优。
    edge 阶段的根预算优先取**step 阶段物化得到的 `U`**（同一个 builder 实例先跑 step、再跑 edge，编排层正是这样复用的）——
    这是最紧且可靠的预算：step 树本身就是一个 `U` 步解的见证，所以 compact 在该上限下绝不会需要超过 `U` 步，从而保证
    **edge 计划不会比 step 更差**。若 edge 阶段被独立调用（builder 上没有先跑 step），则回退到**精简版**上界
    `ConstructiveRootUpperBound`（不做物化去重、计满整条最长路径，因此 `≥` 物化 `U`，可靠但略松）。在大 `m`
    形状（如 `25,10,10`）上，单个状态可能有上千个不同的步最优分组，过去 edge 阶段（`EnumerateDistinctGroups`）
    会把它们**全部生成 + McKay 去重**，于是几乎卡死。现在生成本身带一个 per-state 上限
    `CompactGreedyCandidateCap`（默认 128，见 `GenerateClassRepresentatives` 的 `generationCap`）：先把 step 阶段的
    构造式分组作为种子第一个评估（保证有界内必有可行解），再生成至多 `cap` 个代表参与「子节点最少」的贪心挑选。
    分组数 `≤ cap` 的状态因此与穷举**逐字节相同**（小/中形状毫无变化），只有分组数超过 `cap` 的大 `m` 状态被截断——
    用一点边数紧凑度换取**有界、可中断**的运行时间（`25,10,10` 由「出不来」降到约 23 s）。`int.MaxValue` 恢复原先
    的完整穷举，精确（exact）模式与最优性审计仍走未截断路径。
  - **向下预算收紧（默认开启、带时间预算、可中断）**：step 阶段给出的 `U` 系统性地偏大（实测几乎总是 `opt+1`），
    而这个偏大的 `U` 又顺带把 edge 阶段的边数也撑大。于是 edge 阶段在以 `U` 为上限跑完一遍**基线 compact**
    （`BuildFeasibleCompactPlan`）后，会自动尝试**收紧**：依次用 `U−1, U−2, …` 作为根预算重跑 compact
    （`TightenFeasibleCompact → ProbeFeasibleCompact`），直到某个预算**不可行**（根处 `SolveCompactSelection`
    返回 `int.MaxValue`，此时不物化、直接判负，避免 `BuildState` 抛错）或触达**已证明下界 `L`** 为止；每拿到一个更小
    `U` 的可行解就采纳为新的最优。由于可行解永远不可能优于真实 `opt`，所以 `opt−1` 必然不可行——这保证收紧后的
    `U` 仍满足 `U ≥ opt`、绝不会假性低于最优（见 `FeasibleCompactPlanTests`）。每次重跑前用
    `ResetCompactSelectionState` 清掉 compact 专属缓存（`_compactGroupPatternCache` / `_compactCostMemo` /
    `_compactRealStepsMemo` / `_phase1bSolved`），让搜索在新的天花板下重新求解；跨阶段的 `_rootProvenLowerBound`
    则**刻意保留**，使收紧后的 edge 计划仍带着 `L`，从而在 `L == U` 时显示 `opt = U (proven optimal)`。
    **软时间预算** = `max(2000ms, 基线耗时 × 4)`，通过把截止时刻塞进既有的 `ThrowIfCancellationRequested`
    检查点实现（无需额外计时线程）；`_tighteningDeadlineHit` 把「预算到点」与「用户真正取消」区分开——前者停止收紧、
    保留当前最优，后者照常向上传播。这样小/中形状几乎瞬间把 `U` 收到最优（如 `8,3,3: 6→5`、`14,5,5: 6→5`、
    `13,4,4: 7→6`），大形状（如 `25,10,10`）在预算内把 `U` 由 5 收到 4、边数 4685→3668；少数 `U−1` 可行但极慢的
    形状会触达时间预算并**保留基线**（无回退、无正确性风险）。`EnableFeasibleTightening = false` 可整体关闭。
  - **Anytime 呈现**：`BuildFeasibleCompactPlan(onStage)` 接受一个回调，在**每次**产出一个阶段结果时**同步**触发——
    回调参数是 `GreedyEdgeStage`（阶段名 + 该阶段**自身**耗时 + 可空的计划，计划为 `null` 表示 `no solution`）。
    先是基线 `compact`，随后每次成功收紧各一个 `compact≤N`，最后可能是一个 `no solution` 终止阶段；每个成功阶段都比
    上一个严格更优（要么步数更小、要么边数更少）。**CLI 与 GUI 在此分道**：CLI 是批处理工具，逐棵打印中间树太啰嗦，
    故只收集各阶段、打印一行 `progression: greedy(steps=, edges=) -> compact(...) -> compact≤N(...) -> compact≤M: no solution`
    总结，随后**只打印最终（最优）那一棵树**；GUI 才用 anytime 增量呈现：用**同步 `Control.Invoke`**（而非
    `Progress<T>`）把回调从工作线程 marshal 回 UI 线程——Invoke 会阻塞工作线程直到处理
    完成，这正是「每阶段弹窗暂停」（默认关闭的 `pause each stage` 开关）得以真正暂停搜索的机制。**弹窗期间一律停止计时**：
    GUI 端在 `MessageBox.Show` 前后 `_runStopwatch.Stop()/Start()`（续计、不重置），引擎端在 `TightenFeasibleCompact`
    的回调 `onStage.Invoke` 前后 `Stop()/Start()` 那条**软时间预算**秒表——于是用户停留在对话框里的时间既不计入总
    `elapsed`、也不计入本阶段时钟，更不会偷偷吃掉收紧的时间预算（点 OK 后预算从暂停处继续）。每个阶段**新增一棵树**
    （含 `no solution` 终止阶段），树根与 overview 用统一标签 `阶段名: elapsed=…, max steps=…, edges=…, output=…`
    （`elapsed` 为该阶段自身耗时、秒、3 位小数；无解时标 `no solution`）。进度面板恒为四行：总 `elapsed` 秒数、
    `阶段名: 本阶段秒数`、`progress: 本阶段百分数`、`eta: 本阶段剩余秒数`。GUI 的各开关 / 参数（n/m/k、模式、主题、
    pause each stage）持久化到 `%APPDATA%/Sort/settings.json`，下次启动沿用上次设置。
- `StrategyPlan.IsFeasibleUpperBound == true` 标记这棵树是「可行上界」而非「精确最优」，CLI / GUI 据此渲染相应的
  首阶段（`greedy`）区域。

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

---

## 6. 下界（lower bounds）

`GetMinWorstCaseLowerBound` 返回该状态最优步数的一个**合法下界**（保证 `≤` 真实最优），供
alpha-beta 剪枝使用。它取多个下界的**最大值**（谁更紧用谁）：

```csharp
// 1) 信息论下界
//    while (distinguishable < info.Count) { steps++; distinguishable *= maxOutcomesPerStep; }
// 2) 反链宽度下界
steps = Math.Max(steps, GetAntichainLowerBound(state));
// 3) 支配下界
steps = ApplyDominanceLowerBound(state, remainingSlots, steps);
```

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

```
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

```
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

```
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

```
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

```
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
|------|--------|-------|
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
|------|-------------|
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
| 构造式可行解（greedy 模式 step / 可行上界 U） | `StrategyBuilder.Constructive.cs` → `BuildFeasiblePlan`、`ChooseConstructiveGroup`、`ConstructiveRootUpperBound`；`StrategyPlan.IsFeasibleUpperBound` |
| 回归 / 计数监控 | `TopKFinder.Tests/StrategyRegressionTests.cs`、`DominanceMetricTests.cs` |
