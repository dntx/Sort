# Test Strategy: Regression, Performance, and Correctness Guards

本文用中文系统地讲解本项目的测试体系：分层结构、各测试文件的职责、以「确定性计数器」作为
**机器无关的时间代理**的性能监控哲学，以及如何新增 / 收紧（ratchet）各类基线。它与
`docs/core-algorithm.md`（算法原理）配套——后者讲「算法怎么算」，本文讲「我们如何守住算法的
正确性与性能」。

当前治理状态：

- Mainline B（test layering governance）已完成：required PR gate 保持 fast-only，`manual-slow-parity` 承担按需 slow parity，贡献者运行矩阵已文档化。
- Mainline C（performance baseline governance）基础设施已完成：focused counter lanes、bundled full audit、baseline drift 审批和 nightly deterministic audit 都已就位；后续主要是 ratchet maintenance。

---

## 1. 总体哲学：两层监控，确定性计数器为主、墙钟时间为辅

性能与正确性的回归防护分成**两层**，职责泾渭分明：

| 层 | 位置 | 机制 | 性质 | 职责 |
| --- | --- | --- | --- | --- |
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

- **`TopKFinder.Tests`**：默认全程运行的单元 + 回归测试（当前约 **440+** 个已展开用例；
  `dotnet test --list-tests` 会随 `[Theory]` 展开与基线增删而波动）。正确性 oracle 与确定性计数器
  护栏都在这里。
- **`TopKFinder.PerfTests`**：统一打上 `Trait("Category", "Slow")`，因此在仓库默认过滤
  （`Category!=Slow`）下**不会自动运行**；需要显式参数才会运行。项目内包含墙钟烟雾测试 + 若干
  **按需 / 环境变量门控**的重型诊断工具。

常用命令（本仓库）：

- 默认快测（不带参数）：`dotnet test`
- 仅慢测：`dotnet test --filter "Category=Slow"`
- 全量（关闭默认快测过滤）：`dotnet test -p:UseFastTestFilter=false`

两个测试项目都通过 `AssemblyInfo.cs` 的 `InternalsVisibleTo` 访问 `StrategyBuilder` 的 internal 成员
（包括测试钩子 `ForceIterativeDeepeningForTesting`）。

---

## 3. 确定性计数器护栏（A 层）详解

这些计数器都暴露在 `StrategyPlan.SearchStatistics`（见 `StrategyModel.cs`）上。

### 3.1 默认（default）搜索路径

`StrategyRegressionTests.cs` 中的四类「counter cap」theory，覆盖 `m<=4 / k<=3` 形状：

| 测试 | 锁定的计数器 | 含义 |
| --- | --- | --- |
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
>
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

### 3.4 Display/Search parity 矩阵分层（Fast vs Slow）

`DisplayToSearchExpanderTests` 的 projection-merging parity theory 每个 case 都会执行 layered/direct 两条路径，并在 `projectionMerging=false/true` 下各跑一次，单 case 计算量较高。

- 快速矩阵：保留在 `DisplayToSearchExpanderTests`，用于日常 focused 回归。
- 慢速矩阵：拆到 `SearchParitySlowMatrixTests`，并标记 `Trait("Category", "Slow")`，用于收口前或专项回归。

这样可以避免日常 focused 命令在未显式声明 slow 需求时反复触发高参数 case，降低“看起来卡住”但本质是重算例堆叠导致的长耗时。

### 3.5 夹逼报告（squeeze）的「已证明下界 L」一侧

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

### 4.1 本地中位数基准脚本（推荐用于优化前后对比）

仓库提供了一个固定 case 集合的本地基准脚本：

- `scripts/benchmark-greedy-stage1.ps1`

用途：

- 固定 warmup + 重复次数；
- 输出每个 case 的 steps/edges/states 与 `stage greedy-feasible` 的中位数时间；
- 标记结构量是否在重复运行中保持稳定（`StableStructure`）；
- 可选导出 CSV 便于做 PR 前后对比。

脚本支持通过参数定制 case 集合与回归门槛：

- `-CaseSpecs`: 用 `n,m,k;n,m,k` 的形式指定 case 集合，便于把慢 band 单独拿出来测。
- `-PerRunTimeoutSeconds`: 给每个 case 的单次运行设置硬超时，避免慢例把整批基准拖死。
- `-BaselineCsvPath`: 指定要对比的基线 CSV；不传时可以走仓库默认基线文件。
- `-BaselineOnly`: 只导出当前结果，不做 compare，适合先量 baseline 再锁定。
- `-RegressionTolerancePercent`: 允许的时间回退容忍度；配合 `-EnforceBaseline` 可让脚本在本地直接失败。

示例：

```powershell
pwsh ./scripts/benchmark-greedy-stage1.ps1
pwsh ./scripts/benchmark-greedy-stage1.ps1 -WarmupRuns 1 -MeasuredRuns 7 -AsCsv
pwsh ./scripts/benchmark-greedy-stage1.ps1 -CaseSpecs "24,7,7;25,7,7;26,7,7;27,7,7;28,7,6" -PerRunTimeoutSeconds 120
pwsh ./scripts/benchmark-greedy-stage1.ps1 -BaselineOnly
pwsh ./scripts/benchmark-greedy-stage1.ps1 -BaselineCsvPath ./scripts/benchmark-greedy-stage1-baseline.csv
pwsh ./scripts/benchmark-greedy-stage1.ps1 -BaselineCsvPath ./scripts/benchmark-greedy-stage1-baseline.csv -RegressionTolerancePercent 3 -EnforceBaseline
```

新增能力：

- 基线固化：`-BaselineOnly` 会把当前结果写入 `scripts/benchmark-greedy-stage1-baseline.csv`（可用
  `-BaselineOutputPath` 改路径），写完即退出，不做 compare；
- 基线对比：传入 `-BaselineCsvPath`（或在仓库存在 `scripts/benchmark-greedy-stage1-baseline.csv` 时自动使用）后，
  脚本会输出每个 case 的 `BaselineMedianSeconds / CurrentMedianSeconds / DeltaSeconds / DeltaPercent / Status`；
- 回归判定：
  - 结构量（`Steps/Edges/States`）发生变化，直接标记 `FAIL_STRUCTURE_CHANGED`；
  - 结构不变但中位数变慢且超过 `-RegressionTolerancePercent`，标记 `FAIL_TIME_REGRESSION`；
- 门槛退出：加 `-EnforceBaseline` 时，只要存在失败 case，脚本会非零退出，便于本地 pre-PR gate。

GitHub Actions 侧提供了手动门槛工作流：

- `.github/workflows/manual-perf-gate.yml`（`workflow_dispatch`），可在 Actions 页面按需触发；
- 输入参数与脚本一致（warmup / measured / tolerance），并默认启用 `-EnforceBaseline`。

注意：墙钟时间仍会受机器负载影响，请优先使用「同机、同会话、同参数」的中位数做对比，
并继续以 A 层确定性计数器作为最终回归准绳。

---

## 4.2 Contributor Run Matrix（Fast / Slow / Perf）

为避免日常开发被重型 case 拖慢，仓库按以下规则分层：

- PR 必跑（required gate）：仅跑 fast 套件（`Category!=Slow`）+ 轻量 perf tests。
- Slow parity 矩阵：不进 required gate，改为按需触发。
- deterministic counter guardrails：按需触发 `manual-counter-guardrails`（机器无关计数器门槛）。
- perf baseline gate：按需触发 `manual-perf-gate`（对比基线 CSV）。

推荐命令：

```powershell
# Fast（默认日常回归）
dotnet test .\TopKFinder.Tests\TopKFinder.Tests.csproj --filter "Category!=Slow"

# Slow parity（收口前 / 专项回归）
dotnet test .\TopKFinder.Tests\TopKFinder.Tests.csproj --filter "Category=Slow"

# 手动计数器护栏（机器无关）
pwsh .\scripts\run-counter-guardrails.ps1 -Profile fast-default

# 计数器护栏 dry-run + 机器可读摘要
pwsh .\scripts\run-counter-guardrails.ps1 -Profile compact -ListOnly -SummaryJsonPath .\artifacts\counter-guardrails-summary.json

# 手动 perf gate（本地脚本）
pwsh .\scripts\run-perf-gate.ps1 -BaselineCsvPath .\scripts\benchmark-greedy-stage1-baseline.csv -RegressionTolerancePercent 5 -EnforceBaseline

# Perf gate dry-run（只打印命令，不执行）
pwsh .\scripts\run-perf-gate.ps1 -BaselineCsvPath .\scripts\benchmark-greedy-stage1-baseline.csv -ListOnly

# Perf gate dry-run + 机器可读摘要
pwsh .\scripts\run-perf-gate.ps1 -BaselineCsvPath .\scripts\benchmark-greedy-stage1-baseline.csv -ListOnly -SummaryJsonPath .\artifacts\perf-gate-summary.json
```

GitHub Actions 入口：

- `required-pr-tests`：PR 自动触发（fast only）。
- `manual-slow-parity`：手动触发 slow parity 矩阵。
- `manual-counter-guardrails`：手动触发确定性计数器护栏（counter-cap tests）。
- `manual-counter-full-audit`：手动触发 full-counter-suite + matched-tests drift diff + unified snapshots 的组合审计。
- `nightly-counter-full-audit`：夜间自动跑 full deterministic audit，持续监控 matched-tests drift 与 snapshot 回归。
- `manual-perf-gate`：手动触发 baseline 回归门槛。
- `counter-baseline-drift-review`：PR 自动触发；如果 matched-tests baseline 文件变化，则要求 PR body 解释 drift。

`manual-perf-gate` 支持 `list_only=true`，用于只验证参数与命令链路，不执行基准。
`manual-perf-gate` 也支持 `baseline_csv_path` 输入，可在不改脚本默认值的前提下切换对比基线文件。
`manual-perf-gate` 还支持 `build_configuration` 输入（默认 `Release`），可在手动 lane 中显式切换 Debug/Release 基准构建配置。

`manual-counter-guardrails` 与 `manual-perf-gate` 都会上传 machine-readable summary artifact，便于后续做自动报表或历史对比。
`manual-perf-gate` 还支持 `collect_benchmark_rows=true`，会额外上传当次运行的每个 case 明细 CSV（`perf-gate-benchmark-rows` artifact），用于复盘中位数样本与结构稳定性。

`manual-counter-guardrails` 支持 profile 输入（`workflow_dispatch`）：

- `fast-default`：默认路径计数器护栏（searched/outcomes/candidate-groups/duplicate-skips）。
- `iterative-frontier`：迭代加深门控前沿护栏（含与 exact 路径对比）。
- `compact`：compact 阶段计数器护栏（work/searched/outcomes/duplicate-skips）。
- `full-counter-suite`：合并运行 `*StaysWithinBaseline` + 关键 iterative 前沿用例。

建议：PR 日常开发优先 `fast-default`；涉及 ID 门控改动时补跑 `iterative-frontier`；涉及 compact 逻辑时补跑 `compact`；收口前或专项巡检跑 `full-counter-suite`。

`manual-counter-full-audit` 适合收口前或怀疑 selector 漂移时使用：一次输出 `full-counter-suite` 结果、matched-tests baseline diff、snapshot 汇总与单份总览摘要。
它还会把审计摘要写入 workflow summary，并可选更新指定 PR 的审计评论。
当 coverage 基本稳定后，可改用 `nightly-counter-full-audit` 做无人值守巡检，把 drift / positive delta 检查从人工触发转成日常监控。

profile 语义、shape 锚点与 cap ratchet 规则见 `docs/counter-guardrail-budgets.md`。
`scripts/run-counter-guardrails.ps1` 会在执行前打印 profile 对应的方法选择器，支持 `-ListOnly` 做 dry-run 检查。
完整操作步骤与产物说明见 `docs/counter-audit-operations.md`。

如果 PR 修改了 `docs/counter-guardrails-full-counter-suite-baseline.txt`，请在 PR body 中加入：`Counter baseline drift: <explanation>`。

Lane 决策表（先选信号，再选车道）：

| 目标 | 首选车道 | 核心信号 | 何时升级 |
| --- | --- | --- | --- |
| 日常 PR 回归 | `required-pr-tests` + `fast-default` | 快速确定性计数器 + 关键 fast 功能 | 触及 ID/compact 逻辑时升级到对应 profile |
| 迭代加深（ID）改动验证 | `iterative-frontier` | ID 前沿计数器上限 + 对 exact 的收益约束 | 收口前补跑 `full-counter-suite` |
| compact 改动验证 | `compact` | compact work/searched/outcomes/duplicate-skips | 收口前补跑 `full-counter-suite` |
| 机器无关性能回归审计 | `full-counter-suite` | 全量 deterministic counter caps | 大改动前后都跑一遍并做 ratchet 记录 |
| 墙钟性能烟雾诊断 | `manual-perf-gate` | 中位数时间对比（机器相关） | 仅用于诊断，不替代计数器护栏 |

---

## 5. 其它正确性 / 单元测试

| 文件 | 职责 |
| --- | --- |
| `ComparisonStateTests.cs` | 偏序集状态：传递闭包、祖先 / 后代计数等底层不变量 |
| `ExactPipelineTests.cs` | exact 公共流水线契约：阶段顺序 / 阶段命名 / 返回计划一致性（`step-proof -> exact-edge-compact@S`） |
| `FreeSymmetryClassTests.cs` | 对称性感知组生成的核心不变量：按 free-symmetry-class 枚举一个代表 == 扫描全部 m-子集得到的轨道集合 |
| `DominanceMetricTests.cs` | phase-1 支配（subsumption）下界剪枝的**正确性**（下界 bracket 真值）与**有效性**（剪枝确实触发） |
| `StrategyOverviewTests.cs` | `StrategyOverview` 概览汇总的正确性 |
| `StrategyTextRendererTests.cs` | 文本渲染器的格式化逻辑 |
| `InputValidationTests.cs` / `CliArgsTests.cs` | 输入校验与命令行参数解析 |

补充（稳定性）：

- `GreedyPipelineTests.ProofTighten_FirstProbeCompletesQuickly_14_2_4` 为 m=2 前瞻性能 canary。
- 该 canary 保持 10 秒门槛，同时在超时时允许一次重试，以吸收偶发机器负载抖动并降低误报。

---

## 6. 按需门控的重型诊断工具（默认不跑）

`TopKFinder.PerfTests` 下有若干用环境变量门控、平时不进套件的工具，用于人工排查：

> 注：这些测试类都标记为 `Category=Slow`。若不带 `--filter` 直接执行 `dotnet test`，它们会被仓库默认过滤跳过。

| 工具 | 门控变量 | 用途 |
| --- | --- | --- |
| `AnomalyScanTests.cs` | `RUN_ANOMALY_SCAN` | 扫一格 (n,m,k)，对 default / compact 计划跑一批廉价不变量检查，自动嗅出「某分支爆成 300+ 边」之类异常 |
| `DominanceReuseStatsTests.cs` | `RUN_DOMINANCE_STATS` | 统计 dominance floor 复用命中与覆盖率，定位下界复用退化 |
| `GapTreeDumpTests.cs` | `RUN_GAP_DUMP` | 把 default 计划与 gap oracle 证明的边最优计划并排渲染，肉眼对比为何真最优边数更少 |
| `OrderedBlockHonestyTests.cs` | `RUN_BLOCK_HONESTY` | 有序块置换检测器的诚实性 / 完整性扫描：确认 sibling 合并都由真实 parent-state automorphism 支撑 |
| `ProofTightenPerfGateTests.cs` | `RUN_PROOF_TIGHTEN_GATE` | 针对历史敏感形状 `20,2,6` 的 greedy `proof-tighten<=U-1` 首探针 gate：默认只看超时（抓卡死/数量级爆炸），可选叠加 `OutcomesConstructed / CandidateGroupsEnumerated / SearchedStates` 的确定性上限（机器无关，便于 ratchet） |

`ProofTightenPerfGateTests` 使用说明：

- 本地触发：

```powershell
$env:RUN_PROOF_TIGHTEN_GATE = "1"
dotnet test TopKFinder.PerfTests\TopKFinder.PerfTests.csproj --filter ProofTightenPerfGateTests
```

- 可选环境变量：
  - `PROOF_TIGHTEN_TIMEOUT_SECONDS`（默认 200）
  - `PROOF_TIGHTEN_OUTCOMES_CAP`（默认 0，0=关闭）
  - `PROOF_TIGHTEN_CANDIDATES_CAP`（默认 0，0=关闭）
  - `PROOF_TIGHTEN_SEARCHED_STATES_CAP`（默认 0，0=关闭）

- 建议流程：先用 timeout-only 观察稳定性，再在同机多次测量后把确定性 cap 锁进 CI（优先锁 `OutcomesConstructed`）。
- GitHub Actions 手动工作流：`.github/workflows/manual-proof-tighten-gate.yml`（`workflow_dispatch`）。

夜间自动巡检（推荐）：

- 工作流：`.github/workflows/nightly-proof-tighten-gate.yml`
- 触发：每天一次（`16:00 UTC`，即中国时区 `00:00`）
- 行为：拆成两个夜间 gate
  - `StrategyMatrixTests` 的 smoke 矩阵，覆盖 `6,2,2`、`10,2,5`、`12,4,4` 的 exact / greedy / greedy-tighten / proof-tighten / greedy-full 代表行
  - 单点 `ProofTightenPerfGateTests`，继续盯住历史敏感的 `20,2,6` 首探针
  - proof-tighten 夜间门槛默认超时设为 `150s`（`PROOF_TIGHTEN_TIMEOUT_SECONDS`），用于吸收 hosted runner 抖动，避免在接近完成时的偶发压线误报
- 报警：任一 job 失败时自动创建或更新带标签 `perf-gate,nightly-performance-gates` 的 issue，附上 run 链接与 commit
- 本地 smoke：把 `STRATEGY_MATRIX_CASE_SET=smoke`，即可跳过最重的 `20,2,6` 行做快速验证；如果要看完整矩阵，可手动把 case set 切到 `full`

这样可以把慢例 gate 从日间 PR 流程里解耦出来：白天保持 required checks 轻量，夜间用 smoke 矩阵加关键单点 probe 持续做回归巡检，full 矩阵保留给手动触发。

full nightly 报警（一步一步）:

1. 先跑一次 full baseline seed（只需要第一次，之后按需重做）
  - 在 GitHub Actions 手动运行 `.github/workflows/manual-seed-full-strategy-baseline.yml`
  - 默认参数可直接用：`case_set=full`、`exclude_keys=greedy-full:20,2,6`、`timeout_seconds=240`、`warmup_runs=0`、`measured_runs=1`
  - 说明：通过 `exclude_keys`（分号分隔）排除慢例；默认仅排除 `greedy-full:20,2,6`，保留其余 full 行（包括 `20,2,6` 的 `greedy-feasible / greedy-tighten / proof-tighten-first`）
  - 该 workflow 会自动生成并提交 `scripts/strategy-matrix-baseline-full.csv`，并自动创建 PR
  - 合并该 PR 后，full nightly 才有可比较的基线

2. 启用 full nightly compare + 报警
  - 工作流：`.github/workflows/nightly-full-strategy-matrix.yml`
  - 触发：每天 `18:00 UTC`（中国时区 `02:00`）+ 支持手动触发
  - 行为：运行 `StrategyMatrixTests` 的 `full` case-set，并通过 `STRATEGY_MATRIX_EXCLUDE_KEYS` 排除慢例后与 `scripts/strategy-matrix-baseline-full.csv` 比较
  - 报警：失败时自动创建/更新标签 `perf-gate,nightly-full-matrix` 的 issue（标题包含日期，便于按天追踪）

3. 后续维护（推荐）
  - 当算法有明显性能变化时，再手动跑一次 seed workflow，生成新的 baseline PR
  - 审核并合并后，nightly 会自动基于新基线继续报警

`StrategyMatrixTests` 的标准用法是“基线历史 + 中位数比较”：

- `STRATEGY_MATRIX_BASELINE_ONLY=1` 时，把当前结果写成基线 CSV。
- 正常 nightly 比较时，若 baseline CSV 对同一个 key 有多行，测试会取这些历史行的中位数作为比较基线。
- 这样就能把“最近 5 次 accepted 夜跑”的中位数自然纳入门限，而不是拿一次原始采样做门限。

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

## 9. PR 中 "Why no test" 说明约定（适用于核心算法改动）

当核心算法文件发生实质改动，但你判断「新增测试没有信息增益」时，不必硬加 no-op 测试；
可以在 PR 描述里写清楚原因与证据。

建议直接加一个小节标题：

- `## Why no test`

并至少包含两点：

1. **原因（Reason）**：为什么这次改动的行为风险低（例如机械拆分、纯重构、无可观察行为变化）。
2. **证据（Evidence）**：现有哪些覆盖/验证已经约束了该改动（例如已有测试集、不变量、手工验证路径）。

可复用模板：

```md
## Why no test
- Reason: <why adding a new test is low value for this change>
- Evidence: <which existing tests/invariants/manual verification already cover it>
```

通过示例：

```md
## Why no test
- Reason: This PR is a behavior-preserving mechanical split of StrategyBuilder helpers.
- Evidence: Existing StrategyRegressionTests and GreedyPipelineTests cover the same call paths; local run shows identical MaxStep and counters on affected shapes.
```

不通过示例（会被视为说明不足）：

```md
## Why no test
- No test needed.
```

或：

```md
No tests added.
```

上面两类都缺少可审阅的 reason/evidence，reviewer 仍可要求补测试或补完整说明。

## 10. 已证明最优值速查表

`docs/optimal-max-steps.md` 汇总了一批 `(n, m, k)` 在 exact 模式下**已证明的最优
max steps**，来源于 `StrategyRegressionTests.cs` 的 `InlineData` 基线与 `--mode exact` 实测。
调研 greedy 上界紧度、验证算法改动、或需要“标准答案”时，可直接查表，避免重复跑耗时的 exact
搜索。新增行请只填 exact 已证明（`proven optimal`）的值并注明来源。
