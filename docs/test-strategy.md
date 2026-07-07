# Test Strategy: Regression, Performance, and Correctness Guards

本文用中文系统地讲解本项目的测试体系：分层结构、各测试文件的职责、以「确定性计数器」作为
**机器无关的时间代理**的性能监控哲学，以及如何新增 / 收紧（ratchet）各类基线。它与
`docs/core-algorithm.md`（算法原理）配套——后者讲「算法怎么算」，本文讲「我们如何守住算法的
正确性与性能」。

---

## 1. 总体哲学：两层监控，确定性计数器为主、墙钟时间为辅

性能与正确性的回归防护分成**两层**，职责泾渭分明：

| 层 | 位置 | 机制 | 性质 | 职责 |
|---|---|---|---|---|
| **A. 确定性计数器上限** | `TopKFinder.Tests`（主要在 `StrategyRegressionTests.cs`） | 断言各种**工作量计数器** `<= 上限`，或结构量 `==` 快照 | 零噪声、**机器无关**、可复现 | **主防线**：真正拦截性能 / 正确性退化 |
| **B. 墙钟烟雾测试** | `TopKFinder.PerfTests/StrategyPerformanceTests.cs` | 跑若干次取中位数，断言 `中位数 ≤ X ms` | 有噪声、**机器相关** | **仅诊断**：只抓数量级爆炸或彻底卡死 |

### 1.1 为什么墙钟时间不可靠

CI 硬件通常比开发机慢数倍，且墙钟计时本身抖动很大。如果把「性能是否退化」绑定在墙钟预算上，要么
预算设得太紧→频繁误报（flaky），要么设得太松→真正的退化照样飘绿。因此墙钟层的预算被**刻意放宽**
（约为 CI 实测的 2 倍甚至更多），它**不负责**拦截增量退化。

### 1.2 核心思想：计数器是「时间代理」

> **运行时间本质上正比于「算法做了多少工作量」。与其测会抖动的墙钟时间，不如直接锁住「工作量」这个
> 确定性整数——它在任何机器上都是同一个值，且与时间强相关。**

于是「耗时有没有变长」这个噪声问题，被翻译成「**做的工作量有没有变多**」这个确定性问题。后者就是一个
**机器无关的时间代理（time proxy）**。其中最关键的代理量是 **`OutcomesConstructed`**——每个 outcome 的
构造（Clone + ApplyOrder + Eliminate + Normalize）是单状态的主导成本，因此它对真实耗时最敏感。

---

## 2. 测试项目布局

- **`TopKFinder.Tests`**：默认全程运行的单元 + 回归测试（约 350+ 个已展开用例，来自约 130 个
  `[Fact]`/`[Theory]` 方法，`[Theory]` 经 `InlineData` 展开后总数更多）。正确性 oracle 与确定性计数器
  护栏都在这里。
- **`TopKFinder.PerfTests`**：少量墙钟烟雾测试（默认运行）+ 若干**按需 / 环境变量门控**的重型诊断工具
  （默认不跑）。

两个测试项目都通过 `AssemblyInfo.cs` 的 `InternalsVisibleTo` 访问 `StrategyBuilder` 的 internal 成员
（包括测试钩子 `ForceIterativeDeepeningForTesting`）。

---

## 3. 确定性计数器护栏（A 层）详解

这些计数器都暴露在 `StrategyPlan.SearchStatistics`（见 `StrategyModel.cs`）上。

### 3.1 默认（default）搜索路径

`StrategyRegressionTests.cs` 中的四类「counter cap」theory，覆盖 `m<=4 / k<=3` 形状：

| 测试 | 锁定的计数器 | 含义 |
|---|---|---|
| `Default_StructuralBaselineRemainsStable` | MaxStep `==` + 根组 `==` + OutputStates / ExpandedOutputStates `<=` | 树的结构快照 |
| `Default_SearchedStateCountStaysWithinBaseline` | `SearchedStates <=` | 搜索访问的状态数 |
| `Default_OutcomesConstructedStaysWithinBaseline` | `OutcomesConstructed <=` | **主时间代理**：构造的 outcome 总数 |
| `Default_CandidateGroupsEnumeratedStaysWithinBaseline` | `CandidateGroupsEnumerated <=` | 对称性去重前枚举的候选组数（对称性优化的主信号） |
| `Default_DuplicateOutcomeSkipsStaysWithinBaseline` | `Diagnostics.DuplicateOutcomeSkips <=` | 被判重丢弃的 outcome 数 |

### 3.2 迭代加深（IDA\*）门控前沿

门控 `_useIterativeDeepening = (_m>=5 && _k>=5 && _n>=2*_m)` 命中的深 / 大 k 区域（通往 25,5,5 的方向），
**正是 25,5,5 真正会走的搜索路径**。在加入专门测试前，这整条路径**没有任何正确性 oracle 和性能护栏**。

- **`Default_IterativeDeepeningBaselineRemainsStable`**：锁定 ID 路径自己物化的树形
  （MaxStep / 根组 / 边数 / 输出状态）+ 工作量计数器（searched / outcomes / candidate groups）。其中
  `OutcomesConstructed` 被明确指定为这条前沿的**主时间代理**。覆盖**两个门控家族**：
  - **重型 (5,5)**：14/16/17/18,5,5（通往 25,5,5）；
  - **(6,6)**：12/14,6,6——注意 (6,6) 也会触发门控（`min(k,n-k)>=5, n>=2m`），所以 ID 代码路径在
    **第二个 m 值**上也被覆盖，防止某个 m-specific 的 ID 退化漏网（这两个算例很轻量但价值在于覆盖面）。
- **`Default_IterativeDeepening_BeatsExactPath`**（17,5,5）：用 `ForceIterativeDeepeningForTesting` 强制
  同一算例分别走 ID 路径与单趟精确路径，断言**同 MaxStep** 且 ID 的 outcomes / searched **严格更少**——
  证明门控带来的剪枝收益是真实的。

> ⚠️ 这里**不**断言「两路径同树」：ID 路径会在等优最优组之间挑不同代表，故边数可能不同（14,5,5: 84↔85；
> 17,5,5: 206↔200），但两棵都是合法的 MaxStep 最优树。详见 `docs/core-algorithm.md` §4.3/§4.4。

> 时间代理并非只在门控前沿：默认（精确）路径的 `Default_OutcomesConstructedStaysWithinBaseline` 等三个
> counter-cap theory 已横跨 **m=2/3/4** 多种形状，并补入了 m=4 的 k>m（12,4,5）、重型 m=4（16,4,4）与
> m=5,k=4（20,5,4）等形状，让时间代理覆盖一个更完整的形状谱，而不只是 (5,5)。

### 3.3 compact 阶段（常被忽视、却可能最耗时）

compact 是一个跑在 phase 1 之上的**二级 DP**（`StrategyBuilder.Compact.cs`），在保持 MaxStep 的前提下
最小化显示边数。**它有时是整个 build 的主导成本，甚至比 default 搜索还慢**，但长期没有工作量护栏。

- **`Compact_WorkCountersStayWithinBaseline`**（7 个算例，含最重的 10,2,4）：锁定 compact 专属计数器
  `CompactStatesSolved` / `CompactGroupsEnumerated` / `CompactStepOptimalGroups`。这是 compact 阶段的
  第一道机器无关性能护栏。
- 另有 `Compact_PreservesMaxStepAndDoesNotRegressEdges`、`Compact_ShrinksTreesWithRedundantSolutions`
  等测试守护 compact 的**正确性**（保持 MaxStep、边数不劣于 default、在已知冗余算例上确实变小）。

> compact 的最优性边界（最小化的是边数**代理量**、原始候选可能比 default 差，由编排层「严格更优才展示」裁决）见
> `docs/core-algorithm.md` §4.4。

### 3.4 夹逼报告（squeeze）的「已证明下界 L」一侧

迭代加深驱动循环的全局预算在每一趟都是 opt 的**已证明合法下界**，通过
`SearchStatistics.RootProvenLowerBound` 与每次进度回调的 `SearchProgressSnapshot.RootProvenLowerBound`
暴露给 GUI/CLI（详见 `docs/core-algorithm.md` §4.5）。三道护栏锁定它的关键不变量：

- **`Default_RootProvenLowerBound_EqualsMaxStepWhenSolved`**（5 形状，含门控的 14,5,5）：任何**完整解出**的
  build 必有 `RootProvenLowerBound == MaxStep`——夹逼闭合成一个点。
- **`Default_RootProvenLowerBound_RisesMonotonicallyAndStaysValid`**（强制 ID 的 17,5,5）：沿进度快照断言
  下界**单调不降**、**始终 ≤ 真实 opt**（永远是合法下界）、且最终**等于 MaxStep**。
- **`Default_RootProvenLowerBound_SurvivesCancellation`**（14,5,5）：用进度回调在**首个正下界**出现时**确定性地**
  触发取消（不依赖墙钟时间），断言取消路径仍给出一个 `1 ≤ L ≤ opt` 的合法下界——这正是硬算例（如 25,5,5）
  跑不完时仍能诚实报告「opt ≥ L」的依据。

---

## 4. 墙钟烟雾测试（B 层）

`StrategyPerformanceTests.cs`：对 6,2,2 / 10,9,9 / 9,3,3 / 12,5,5 / 12,3,3 等少数算例跑中位数计时，
预算宽松。它**仅用于诊断**数量级爆炸或卡死，文件头注释已明确把真正的拦截职责指回 A 层计数器。

---

## 5. 其它正确性 / 单元测试

| 文件 | 职责 |
|---|---|
| `ComparisonStateTests.cs` | 偏序集状态：传递闭包、祖先 / 后代计数等底层不变量 |
| `FreeSymmetryClassTests.cs` | 对称性感知组生成的核心不变量：按 free-symmetry-class 枚举一个代表 == 扫描全部 m-子集得到的轨道集合 |
| `DominanceMetricTests.cs` | phase-1 支配（subsumption）下界剪枝的**正确性**（下界 bracket 真值）与**有效性**（剪枝确实触发） |
| `SlowSearchHeuristicTests.cs` | 静态分类器 `Program.IsPotentiallySlowSearch` 对「慢形状」的判定（**不**构建树） |
| `StrategyOverviewTests.cs` | `StrategyOverview` 概览汇总的正确性 |
| `StrategyTextRendererTests.cs` | 文本渲染器的格式化逻辑 |
| `InputValidationTests.cs` / `CliArgsTests.cs` | 输入校验与命令行参数解析 |

---

## 6. 按需门控的重型诊断工具（默认不跑）

`TopKFinder.PerfTests` 下有若干用环境变量门控、平时不进套件的工具，用于人工排查：

| 工具 | 门控变量 | 用途 |
|---|---|---|
| `AnomalyScanTests.cs` | `RUN_ANOMALY_SCAN` | 扫一格 (n,m,k)，对 default / compact 计划跑一批廉价不变量检查，自动嗅出「某分支爆成 300+ 边」之类异常 |
| `GapTreeDumpTests.cs` | `RUN_GAP_DUMP` | 把 default 计划与 gap oracle 证明的边最优计划并排渲染，肉眼对比为何真最优边数更少 |
| `OrderedBlockHonestyTests.cs` | `RUN_BLOCK_HONESTY` | 有序块置换检测器的诚实性 / 完整性扫描：确认 sibling 合并都由真实 parent-state automorphism 支撑 |

---

## 7. 如何新增 / 收紧基线（ratchet）

确定性计数器护栏的工作流：

1. **先量基线**：写一个临时测量 harness（构建目标算例、打印计数器），跑一次拿到当前真实值。**切勿
   拍脑袋填数。**
2. **以当前值作为上限**：counter cap 用 `<=`（工作量），结构量用 `==`（快照）。
3. **方向是「棘轮」**：当某个优化**减少**了工作量，就把上限**下调**到新值，把收益**锁住**；任何让计数
   **上升**的改动都会让测试变红 → 视为性能退化，除非是有意 / 有记录的权衡。
4. **删除临时 harness**：基线锁定后删掉测量代码，保持套件干净。

> 注意构建陷阱：仓库**没有 .sln**，`dotnet build` 只会编译 `TopKFinder.csproj`。跑测试前必须显式
> `dotnet build TopKFinder.Tests -c Release`（以及 PerfTests），否则会用到过期 dll。

---

## 8. 与算法文档的同步约定

当核心算法（搜索 / minimax、下界、归一化、规范形 / 对称性、支配、compact 搜索）发生改动时：

- 同步更新 `docs/core-algorithm.md`；
- 若改动影响工作量计数，**在同一 PR 内**更新相应的计数器上限（ratchet）并量出新基线；
- 若新增了核心算法区域的能力，考虑是否需要在本文档补充对应的护栏说明。

## 9. 已证明最优值速查表

`docs/known-optimal-max-steps.md` 汇总了一批 `(n, m, k)` 在 exact 模式下**已证明的最优
max steps**，来源于 `StrategyRegressionTests.cs` 的 `InlineData` 基线与 `--mode exact` 实测。
调研 greedy 上界紧度、验证算法改动、或需要“标准答案”时，可直接查表，避免重复跑耗时的 exact
搜索。新增行请只填 exact 已证明（`proven optimal`）的值并注明来源。

