# WIP Handoff — 决策树"多边"显示的替代表示法讨论

> 这是一次设计讨论的存档，用于换台电脑继续。**尚未改任何生产代码**（渲染层/搜索层都没动）。
> 讨论围绕：greedy 模式下某些决策状态展开出巨量分支边（如 25,10,10 的 S3 有 **1260 条边 / ~14,725 行**），
> 人类无法 follow、无法判真伪、无法查下一步。目标是找一个更好的显示表示。

## 目标（用户三条硬需求）
1. 简化显示逻辑（行数）。
2. 人能轻松 follow 并**判断真伪/正确性**。
3. 给定一个**真实比较结果**（如 `a>b>c>d>e`），能**方便查到下一步怎么走**。

## 关键测量数据（都已用真实运行核对过）
| 状态 | 边数 | 不同效果签名(fixed/possible) | 不同后继状态 |
|---|---|---|---|
| 25,10,10 S3 (`sort(#2,#3,#11,#12,#20~#25)`) | 1260 | **112** | 85 |
| 11,5,5 S3 (`sort(#3,#4,#6,#10,#11)`) | 18 | **13** | 6 |

- 25,10,10 整份 greedy feasible 输出 23,805 行，S3 一个节点占 14,725 行(62%)。
- "位置槽填类符(A1,A2…按名次排)" **不压缩**（1260→1260），因为现有 `pattern:` 已经这么做了、名次位置本身有区别。
- 真正压缩到 112 的键是 **按"类折叠后的效果(in/out/possible)"归并**（1260→112, 11×）。
- 112 是**硬底**：真有 112 种本质不同的结果，再压就得丢掉可路由/可验的效果信息。
- 11,5,5 只有一个真对称对 `F={#10,#11}`，所以只 18→13；收益大小 = 对称类的大小与数量（大 fresh 块才显著）。

## 探讨过的表示法
- **现状**：每条边 = 一个具体全序 `a>b>c>d>e` + `equivalent forms` + `pattern:` + 效果 + 后继。优点：效果**预计算好挂在每条排列下**，零计算查表、可验证。缺点：行数爆炸。
- **方案 A（按后继状态归并 DAG）**：1260→85 行。**否决**：不可路由——同一抽象后继(如 S8)在不同结果下要 sort 的具体对子不同(sort(#5,#10) vs sort(#4,#7))，A 丢了重标号。
- **方案 B（判定谓词/冠军亚军）**：能定位到抽象状态，但(winner,runner-up)不足以定出具体比较组(11,5,5 组7 vs 组8 同为#3,#4 领先却下一步不同)。
- **`next = sort(possible)` 规则**：可路由但要读者自己算 in/out/possible（易错）。用户正确指出这牺牲了现状"预计算挂在排列下"的核心价值。
- **B+（最终方向）= 按效果聚合 + 类符(F1/F2…)写成 pattern 形态**：
  - 主体就是把现状的 `pattern:` 提上来当主角（不写 `a>b>c>d>e`）。
  - 额外收益仅来自"pattern 不同但效果相同"时再并一层（这才是 1260→112 / 18→13 的来源）。
  - 每组挂 `possible → sort(possible)`，可路由；`Σ计数 = 总数` 做真伪校验；类内成员折叠诚实。

## 诚实性边界（重要，勿破坏）
- 现有文档(`docs/strategy-output.md`)刻意**不合并纯收敛**(后继同构但父态无自同构)以免误导。
- B+ 的"同效果合并"要成立，必须以**类折叠效果签名**作为证明：同签名 ⟺ 对 top-k 后果真的相同。
- 不能把 `#2,#3,#11,#12` 并成类：`#11` 拖着还活的隐藏 `#13~#19`，`#2` 尾巴已死，命运不同，硬并会算错 in/out。这就是 112 硬底的来源。

## 11,5,5 S3 的 B+ 最终样子（回来可直接参考）
```
S3 [step 3/4] sort(#3, #4, #6, #10, #11)      18 orderings -> 13 outcomes
  F = {#10,#11}(fresh pair), G = {#7,#8}(hidden under #6); fixed {#1,#2}
  rule: possible(...) empty => solved; else sort(possible)
  ── SOLVED ──────────────── 8 orderings, 4 outcomes
    x3  F1 > F2 > #3 > #4 > #6      top5 = #1,#2,#3, F1,F2
    x1  F1 > F2 > #6 > #3 > #4      top5 = #1,#2,#6, F1,F2
    x2  F1 > #3 > #4 > #6 > F2      top5 = #1,#2,#3,#4, F1
    x2  F1 > #3 > #6 > #4 > F2      top5 = #1,#2,#3,#6, F1
  ── +1 SORT, 1 slot ─────── 6 orderings, 6 outcomes
    x1  F1 > #6 > F2 > #3 > #4      possible {G1,F2}  -> sort(G1,F2)
    x1  F1 > #6 > #3 > #4 > F2      possible {#3,G1}  -> sort(#3,G1)
    x1  #3 > #4 > F1 > #6 > F2      possible {#5,F1}  -> sort(#5,F1)
    x1  #3 > #4 > #6 > F1 > F2      possible {#5,#6}  -> sort(#5,#6)
    x1  #3 > #6 > F1 > #4 > F2      possible {G1,F1}  -> sort(G1,F1)
    x1  #3 > #6 > #4 > F1 > F2      possible {#4,G1}  -> sort(#4,G1)
  ── +1 SORT, 2 slots ────── 4 orderings, 3 outcomes
    x1  #6 > F1 > F2 > #3 > #4      possible {G1,G2,F1,F2}  -> sort(G1,G2,F1,F2)
    x2  #6 > F1 > #3 > #4 > F2  (also #6 > #3 > F1 > #4 > F2)
                                   possible {#3,G1,G2,F1}  -> sort(#3,G1,G2,F1)
    x1  #6 > #3 > #4 > F1 > F2      possible {#3,#4,G1,G2}  -> sort(#3,#4,G1,G2)
  ✓ 8 + 6 + 4 = 18
```
那个 `x2` 组是关键示例：两种类形状(F1 在第2 vs 第3位)但 possible 相同 → 只有"按效果"才并、"按位置类模式"不并。

## 如何复现数据（回来可重跑）
```powershell
# 构建
dotnet build TopKFinder.csproj -c Release
# 若 exe 被占用: Stop-Process -Name TopKFinder -Force
# 完整 greedy(约3分钟) 会先出 feasible plan(steps=5,edges=6202) 再进 compact
.\bin\Release\net8.0-windows\TopKFinder.exe 25 10 10 --mode greedy 1>out.txt 2>err.txt
```
- 想只看**原始 feasible plan**（含 1260 边的 S3，跳过慢的 compact）：写个临时 xUnit 测试调
  `new StrategyBuilder(25,10,10).BuildFeasiblePlan()` 再 `StrategyTextRenderer.Render(plan)` 写文件。
  11,5,5 样本同理：`new StrategyBuilder(11,5,5).BuildFeasiblePlan()`。
- 工作区已留样本文件：`f_11_5_5.txt`(11,5,5 feasible 渲染)、`s3out.txt`(25,10,10 feasible 渲染, 23805 行)。

## 相关代码位置
- 渲染层：`StrategyTextRenderer.cs`（`RenderNode` 的 `foreach (var branch in node.Branches)` = 分桶/改格式的落点）。
- pattern 引擎/类符 `A={...}`：`StrategyBuilder.EquivalentOrders.cs`、`StrategyBuilder.ProjectionQuotient.cs`、`StrategyBuilder.Transitions.cs`。
- 诚实性守护：`TopKFinder.Tests/DisplaySearchParityTests.cs`(`CheckDisplaySearchParity`)、`TopKFinder.PerfTests/RelabelingOrbitFoldingTests.cs`。
- 已有的默认开启折叠开关参考：`EnableProjectionOrbitMerging`(principle-D 投影合并)。

## 下一步（待用户拍板）
建议分两步、纯渲染层、`DisplaySearchParity` 守护：
1. **只改格式不合并**：现状每条边 → pattern-first 一行(把 `pattern:` 提上来、`A`→类符、结局压行尾)。零风险、行数不变、`Σ` 天然守恒。做成一个渲染开关（仿 `EnableProjectionOrbitMerging` 默认关）。用 11,5,5 当回归样板。
2. **叠加"同效果合并"**：把 pattern 不同但(fixed,possible)类折叠后相同的行再并一层（18→13、1260→112）。需新的诚实性断言：组内所有成员的类折叠效果签名一致，且 `Σ计数=父边总数`。
- 备选：只读**探针**先验证 25,10,10 S3 确能收敛到 112 组且分组诚实，再决定是否进渲染层。

## 工作区临时文件状态
- 保留：`f_11_5_5.txt`、`s3out.txt`（样本数据，git untracked）。
- 本文件：`HANDOFF-display-representation.md`（**要在另一台机器继续，请 commit + push**；或看 repo memory `/memories/repo/display-representation-discussion.md`）。
- 之前所有临时脚本/测试(`_*.ps1`, `TempRender*.cs`, `TempDump*.cs`, `dump_*.txt`, `g_*.txt`)均已删除。
