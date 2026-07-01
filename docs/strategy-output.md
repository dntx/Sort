# 分支等价 Pattern 渲染逻辑

本文用中文讲解本项目**输出层**的一块独立逻辑：如何把一条策略树分支所代表的**一族等价排序**，
渲染成一行人类易读的 `pattern: ...`。

这块逻辑与搜索/剪枝**完全正交**——[`core-algorithm.md`](./core-algorithm.md) 关注的是「如何尽量少
比较就找出 top-k」（minimax 搜索、对称约减、各种下界剪枝），它决定**策略树的形状与最优性**；而本文
关注的是「同一棵已经定好的树，如何把每条边的含义讲清楚」，它**只影响可读性，不影响任何搜索结果或步数**。
因此对本文涉及的代码做的任何改动都应是「渲染层改动」：物化树的结构、`MaxStep`、边数都不变，变的只是
`pattern:` 那一行文字。

> **默认开启的 principle-D 投影合并**：本文 §4.2 描述的「投影轨道合并」（`EnableProjectionOrbitMerging`）
> 现已**默认开启**，对 exact 与 greedy 两种模式都生效。它仍是纯渲染层改动——折叠后的树是搜索的
> **严格细化**（`CheckDisplaySearchParity` 处处无残差）、`MaxStep` 不变、搜索统计不变，只有**显示的边数**
> 会下降。该开关连同其下的 merge-OFF 旧分支会在功能完全落定后整体移除。

---

## 1. 为什么需要「pattern」

一次比较把最多 `m` 个元素排成一条全序链。对某个父状态选定一个比较组后，这 `m` 个元素的**不同排序结果**
会产生不同的子分支。但其中很多排序结果在语义上是**等价的**：

- 它们**收敛到同一个后继搜索状态**（top-k 判定的后续完全一样），或
- 它们之间仅差一个**对称类内部的重标号**（见 `core-algorithm.md` §5 McKay 规范形），或
- 排序链的**尾部元素全部已经确定出局**，它们之间谁先谁后根本不影响结果。

如果把每个具体排序都画成一条独立的边，输出会是一长串误导性的分支
（例如 `(#1 > #4 > #7 | #1 > #7 > #4 | #4 > #1 > #7 | ...)`）。pattern 渲染层的职责，就是把
这一族等价排序**折叠成一个紧凑、诚实、易读的形状**，并附上它代表了多少个具体排序（计数）。

---

## 2. 数据模型：`EquivalentOrderSummary`

每条分支 `StrategyBranch` 上挂一个可空的 `EquivalentOrders`（`EquivalentOrderSummary`，
`StrategyModel.cs`）。它有四个字段：

| 字段 | 含义 | 例子 |
|------|------|------|
| `Count` | 这条边代表多少个具体排序 | `6` |
| `CountFormula` | 计数的因式分解（讲清楚 6 是怎么来的） | `3!`、`2! sym x 2! tail` |
| `PatternText` | 形状本身 | `#2 > #3 > {#6, #10}` |
| `Legend` | 占位符图例，**仅 doomed-tail 边可能非空** | `A = {#6, #10}` |

文本渲染由 `StrategyTextRenderer` 完成（`StrategyTextRenderer.cs`）：

```
equivalent forms: {Count} = {CountFormula}        // FormatEquivalentFormsSummary
pattern: {PatternText}                             // FormatEquivalentPatternLine，Legend 为空时
pattern: {PatternText} ; {Legend}                  // FormatEquivalentPatternLine，Legend 非空时
```

即 **legend 不另起一行，而是用 ` ; ` 接在 pattern 行尾**，让全树每条占位符 pattern 读起来格式一致。

---

## 3. 记号速查

| 记号 | 含义 |
|------|------|
| `#i` | 第 `i` 个元素（1-based 标签；引用里可能被重标号） |
| `#i ~ #j` | 连续区间 `#i..#j`（**仅当连续游程 ≥ 4 个**才用这种缩写，见 `FormatSet`） |
| `a > b > c` | 一条确定的全序链：`a` 高于 `b` 高于 `c` |
| `{...}` | **任意顺序**：花括号内的元素彼此顺序不限 |
| `A1, A2, ...` | 同一个**对称类** `A` 的占位成员（它们可互换，落在 pattern 的不同位置） |
| `A = {...}` | 图例：定义对称类 `A` 的成员集合 |
| `x > y`（接在 ` ; ` 之后） | **residual 约束**：折叠成 `{...}` 的尾部里仍然成立的已知顺序（Hasse 覆盖边） |
| `(#a) ↔ (#b)` | **relabel 镜像**：`#a` 与 `#b` 互为父状态自同构（或投影后自同构）的镜像，可整体互换 |
| `drop {...}` | **投影合并披露**：这条折叠边的代表与其镜像「共同注定出局」的元素集——丢掉它们后两者才等价（§4.2） |
| `A1 > {A2, #7}` | **结构商记号**：投影后对称类成员 `A2` 与单叶 `#7` 落入同一 brace（多族投影合并的代表形状） |
| `drop tail(A2)` | 结构商里的**协变 drop**：每个折叠成员各自丢掉其 `A2` 子块的 doomed 尾部后才互换 |
| `N!` / `c!` | 阶乘形式的计数因子（一条长 `c` 的链有 `c!` 种线性扩展） |
| `... sym x ... tail` | doomed-tail 计数 = 对称因子 × 尾部因子 |
| `p! x q`（投影商计数） | 投影商计数 = 块内排列 `p!` × 投影镜像数 `q`（如 `4 = 2! x 2`） |

一个核心约定：**`{...}` 永远只表示「任意顺序」**。无论它来自全排列、对称类，还是 doomed 尾部，
读者看到花括号就知道「这些元素之间顺序无所谓」。

---

## 4. 两条渲染轨道

一个比较步的分支由 `BuildBranchSpecs`（`StrategyBuilder.Transitions.cs`）和 compact 版本
（`StrategyBuilder.Compact.cs`）生成。它**优先尝试 doomed-tail 轨道**，失败才回退到通用轨道：

```
TryBuildDoomedTailSpecs(...)  // 轨道 A：尾部已出局 → 折叠成 {...}
  ├─ 非 null → 直接用这组 doomed-tail 边
  └─ null    → 轨道 B：BuildEquivalentOrderSummary(...) 通用 pattern 引擎
```

### 轨道 B：通用 pattern 引擎（`StrategyBuilder.EquivalentOrders.cs`）

`BuildEquivalentOrderSummary`（~L568）处理「多个排序收敛到同一后继状态」的折叠：

- 若所有族都是单例（每个具体排序各算 1 个），交给**整体 pattern 引擎**
  `BuildEquivalentPatternSummary`（~L809），它识别常见形状并压缩：
  - 全排列 → `permute {...}`；
  - 公共前后缀 → 固定链 + 中间块；
  - 独立块、锚定排列、有序块排列、链轨道排列等更复杂模板
    （`TryBuild*Summary` 一系列方法，~L946 起）。
- 否则用 `(... | ... | ...)` 的析取兜底。
- 结果统一经过 `SplitPlaceholderLegend` + `NormalizeEquivalentPattern`（见 §8）。

引擎内部先用一种可解析的中间形式 `<alias>=permute{...}, ...; <body>`，最后才翻译成展示用的
`{...}` / `A = {...}` 记号。

#### 4.1 轨道 B 的同构折叠：跨链 relabel 等价（父态自同构）

通用引擎只看排序、看不到父状态 P，所以遇到「两条排序互为父状态自同构的镜像」时，它无法用一个
无析取的模板表达，只能退化成 `(... | ...)`。但这类镜像**确实**是真等价：在把一个分桶切成展示行时，
`SplitMergedBucketIntoBranchLines`（`StrategyBuilder.Transitions.cs`）先用
`PartitionFamiliesIntoOrbits` 按父状态自同构（`ComparisonState.TryFindOrderAutomorphism`，
`fixedTopMask: 0`）把族并成轨道——只有被一个真实的活跃偏序自同构连起来的族才会同轨道。

典型例子是 20,10,10 可行解的 S3：活跃偏序是两条等价长链（`#1~#10` 与 `#11~#20`），链交换
`#i ↔ #i+10` 是真自同构，于是每条排序都有一个镜像收敛到同一个规范子状态。对这种**全单例**轨道，
`BuildBranchSpecForLine` 选字典序最小的排序当代表，并交给 `BuildRelabelingOrbitSummary`
（`StrategyBuilder.EquivalentOrders.cs`）渲染成一行 + 一句 relabel 图例，例如：

```
equivalent forms: 2 = 2
pattern: #1 > #11 > ... > #5 ; (#1 ~ #10) ↔ (#11 ~ #20)
```

图例由自同构见证映射 run-collapse 而来：对合（chain-swap）压成 `(#a ~ #b) ↔ (#c ~ #d)` 区间对，
非对合退化为有方向的 `#a→#b`。只有轨道里**所有族都是单例**才走这条折叠；任一族自带内部排列
（`Family.Count > 1`）则保持旧的按族拆行，避免把内部对称误并进 relabel 图例。

> 诚实性边界：仅「父状态自同构连起来」的镜像才折叠；纯收敛（下一状态同构但父状态**无**自同构）
> 不会被 `TryFindOrderAutomorphism` 接受、也就不会并轨道，仍各自成行。回归网由
> `TopKFinder.PerfTests/RelabelingOrbitFoldingTests.cs` 用计数断言守护（20,10,10 可行解的
> `CheckPlanFalseSplits` 必须为 0）。

#### 4.2 投影轨道合并（principle-D，默认开启）

上面 §4.1 的 relabel 折叠只认**父状态本身**的自同构。但有一类更弱的等价它抓不到：两条兄弟排序
在父态下**并非**镜像，可一旦把它们**这一步共同淘汰掉的元素**（doomed「drop」集）从偏序里抠掉，
剩下的「投影」偏序就互为自同构了。把它们各画一行同样是误导——丢掉的元素本来就出局，谁先谁后无所谓。

`SplitMergedBucketIntoBranchLines`（`StrategyBuilder.Transitions.cs`）在 §4.1 的父态轨道划分之后，
再跑一遍 `MergeOrbitsByProjection`（`StrategyBuilder.ProjectionQuotient.cs`）：用并查集把**所有**父态
轨道按「投影自同构」(`TryProjectionAutomorphism`——抠掉公共淘汰集后存在父态自同构) 并成连通分量。
每个分量按形状走一条渲染轨道：

- **单序分量**（成员都是单例排序）：选字典序最小的当代表，交给 §4.1 的 `BuildRelabelingOrbitSummary`，
  并把公共淘汰集 `CommonEliminatedMask` 作为 `drop {...}` 披露补在行尾，例如 9,4,4 的 S3：

  ```
  pattern: #1 > #6 > #9 > #2 ; (#1) ↔ (#9) ; drop {#2, #3, #4, #8}
  ```

  > 若两条排序在父态下**本来就**互为镜像（无需投影），则不带 drop，仍是干净的 `{#1, #9} > #2 > #6`。

- **多族分量**（某成员自带内部排列，`Family.Count > 1`）：父态/投影都无法用一个无析取模板表达，
  交给 `BuildProjectionQuotientSummary` 渲染成**结构商**记号，例如 7,3,2 的 S3：

  ```
  equivalent forms: 4 = 2! x 2
  pattern: A1 > {A2, #7} ; A = {#1 > #2, #4 > #5} ; drop tail(A2)
  ```

  其中块 `A` 携带各自的尾链，`{A2, #7}` 是投影后落进同一 brace 的成员，`drop tail(A2)` 是协变的
  结构性 drop（不是写死的元素集）。通用 pattern 引擎无法直接吐出 `{A2, #7}`：投影前 `#7` 是自由叶、
  `#4` 是链头，分属不同对称类，所以必须用这个专用结构渲染器。

**诚实性（两道闸）**：
1. 全局-drop 闸 `ComponentIsSingleGlobalDropOrbit`（`StrategyBuilder.ProjectionPairingProbe.cs`）证明分量内
   每个族都映到代表上（确为单一全局 drop 轨道，杜绝传递性泄漏）；
2. 结构渲染器 `BuildProjectionQuotientSummary` **只接受它能忠实描述的标准形状**，否则返回 null。

任一闸不过的多族分量**回退到单序合并**（`MergeSingletonOrbitsByProjection`），所以投影合并
**绝不差于**仅单序合并、也绝不差于不合并——folded 边数 ≤ 不 fold 的边数。

**与搜索的关系**：投影合并只折叠「本就收敛到同一规范搜索后继」的排序，所以它是搜索的**严格细化**——
`CheckDisplaySearchParity`（`StrategyBuilder.OptimalityGap.cs`）处处无残差。

> **已知 compact 不一致（待修，TODO）**：compact 选择的目标函数 `CountDisplayBranches`
> （`StrategyBuilder.Compact.cs`）用 `fixedTopMask: 0` 估算合并边数，而合并行为依赖真实的 fixed/doomed
> 上下文，于是 compact 的 DP 目标 ≠ 最终渲染边数，个别形状下 compact 树会渲染出比 merge-OFF 更多的边
> （如 10,3,4：9 → 11）。修复方向是把真实 `fixedTopMask` 传进目标函数，使 DP 目标 = 渲染边数，再收紧
> 相关基线。default/exact 渲染层不受此影响。


### 轨道 A：doomed-tail 边（`StrategyBuilder.DoomedTailEdges.cs`）

这是本文的重点，下面几节专门讲。

---

## 5. 什么是「doomed tail」（已注定出局的尾部）

`ComputeDoomedPrefixLength`（`StrategyBuilder.DoomedTailEdges.cs` ~L209）把一个排序拆成
**前缀** + **doomed 尾部**：从某个深度 `depth` 起，**剩下的所有元素都已经不可能进入 top-k**
（它们外部已确定的祖先数 `outsideAncestors + depth` 已达到淘汰阈值），那么这段尾部就是 doomed 的。

关键洞察：**doomed 尾部内部谁先谁后，对 top-k 判定毫无影响**。所以把 `#6 > #10` 和 `#10 > #6`
画成两条不同的边是误导的——它们应该折叠成一条边，尾部写成 `{#6, #10}`「任意顺序」。

要采用这条轨道，要求**每个族**都有长度 ≥ 2 的 doomed 尾部（`n - prefixLength < 2` 就放弃），
否则全步回退到轨道 B（`TryBuildDoomedTailSpecs` ~L40–49）。然后把族按「doomed 前缀的对称类序列」
分桶（`BuildDoomedPrefixKey`），同桶的族收敛到同一后继状态，再按父状态自同构把桶并成决策轨道
（`PartitionDoomedBucketsIntoOrbits`），最后每个桶/轨道交给 `BuildDoomedTailSummary` 渲染。

---

## 6. `BuildDoomedTailSummary`：把尾部折成花括号

`BuildDoomedTailSummary`（~L244）把一个 doomed-tail 桶渲染成 `EquivalentOrderSummary`。流程：

1. **给对称类分配字母**：成员数 > 1 的类按类序拿到 `A, B, C...`（~L252–258）。单成员项不分字母，
   直接用 `#id`。
2. **前缀 token**：逐个把前缀元素渲染成 `A1`（属于多成员类）或 `#id`（单例）。下标
   （`NextSubscript`）从左到右、先前缀后尾部地递增，保证每个类成员只命名一次，
   形如 `A1 > ... > {A2, A3}`。
3. **尾部排序**：doomed 尾部按 id 排序。
4. **剥离 forced-first head**（见 §7）。
5. **尾部 token + body 拼装**：缩小后的尾部若 ≥ 2 项渲染成 `{...}` 花括号，否则作为链节点接上。
6. **residual 约束**（见 §6.1）。
7. **计数公式**（见 §6.2）。
8. **归一化**（`NormalizeEquivalentPattern`，见 §8）。

### 6.1 Residual 约束（`BuildTailResidualConstraints` ~L403）

折成 `{...}` 表示「任意顺序」，但尾部里**有些顺序其实仍然成立**（某两个尾部元素之间已经比出过
大小）。这些约束以 **Hasse 覆盖边**的形式补在 pattern 后面，用 ` ; ` 隔开：

```
{#1, #2, #25} > {...}   →   ... ; #1 > #2
```

只保留**覆盖边**：若两元素之间还夹着第三个尾部元素，则该顺序由传递性隐含，不重复列出
（~L419–423）。覆盖边按字典序排序后用 `, ` 连接。

### 6.2 计数公式（`... sym x ... tail`）

doomed-tail 边的计数被分解为**对称因子 × 尾部因子**：

- **对称因子**（`sym`）：前缀里用掉的对称类槽位贡献的排列数。类大小 `c`、用掉 `s` 个槽位时，
  因子是 `c!/(c-s)!`（`s=c` 或 `c-1` 时简记 `c!`），见 ~L354–366。
- **尾部因子**（`tail`）：缩小后尾部的线性扩展数，由 **hook-length 公式**渲染
  （`BuildTailFactorFormula` ~L441）：`L! / D`，`D` 是每个尾部元素「下集大小」的乘积。
  - 尾部是**森林**（每个元素在尾部内至多一个直接祖先）时该公式精确：纯链长 `c` 渲染成 `c!`，
    分叉树渲染成整数 hook 乘积。
  - 尾部**向上分叉**（一个元素压在两个不可比祖先之下）时 hook-length 不再适用，**回退**到直接打印
    整数计数，避免印出错误的闭式。

例：26,5,3 某边原尾部 `{#6,#7,#11}` 是链 `6/3 = 2`；剥离 `#6` 后尾部 `{#7,#11}` 是 `2! = 2`——
计数不变（见 §7）。

---

## 7. Peel forced-first head（把「必为首」的尾部元素提成链节点）

这是一项**纯可读性简化**（`BuildDoomedTailSummary` ~L285–349）。

**问题**：doomed 尾部里若有一个元素**支配了尾部其余所有元素**（它是其余每一个的祖先），那它在尾部里
**必然排第一**。但旧渲染会把它埋进 `{...}`，再用 residual 把它的顺序重新声明一遍，读起来很别扭：

```
{#6, #10} > {#2, #3} ; #2 > #3
#2 > #3 > {#6, A1, A2} ; #6 > #11, #6 > #7
```

**简化**：反复「剥离」任何**支配其余所有剩余尾部元素的单例项**，把它提到前面作为一个链节点，直到没有
可剥离项或尾部只剩 1 项。缩小后的尾部 ≥ 2 项才写 `{...}`，否则直接作链节点（避免出现 `{#11}` 这种
单元素花括号）。residual 和计数公式都在**缩小后的尾部**上重算。

```
{#6, #10} > {#2, #3} ; #2 > #3       →   {#6, #10} > #2 > #3
#2 > #3 > {#6, A1, A2} ; #6 > #11, #6 > #7   →   #2 > #3 > #6 > {#7, #11}
```

**为什么只剥单例**（~L300，跳过多成员类成员）：
- 多成员对称类的成员**不可能支配自己的同类兄弟**（它们彼此不可比），所以限定单例不会漏掉真正的
  forced head；
- 更重要的是，这样**完全不碰** `prefixClassSlots` / 对称因子 / 下标记账，把改动局部化、低风险。

**为什么计数不变**：剥离一个「必为首」的元素 `h`，尾部线性扩展数满足 `#ext(tail) == #ext(tail − h)`；
`Count`（= 桶总数）与对称因子都不依赖这次剥离，所以 `tailFactor = Count / 对称因子` 不变，仍等于
缩小后尾部的线性扩展数。因此这是**纯文字层面的等价改写**。

**向后兼容**：没有任何元素支配其余所有元素时，`remainingTail == tailItems`，residual 与 body 与
旧逻辑**逐字节相同**——对其它所有边零影响。

---

## 8. 归一化：`NormalizeEquivalentPattern`

两条轨道的输出最后都过一遍归一化（`StrategyBuilder.EquivalentOrders.cs`）：

- **`SplitPlaceholderLegend`**（~L620）：把引擎内部的 `<alias>=permute{...}, ...; <body>` 拆成
  「body + 图例」，图例移到尾部并改成 doomed-tail 同款记号。它在**第一个 `;`** 处切分，所以
  residual 的 ` ; x > y` 会留在 body 内、不被误当成图例。
- **`NormalizeEquivalentPattern`**（~L663）：统一成 inline-set 记号——
  - 占位符占据**连续有序游程** `A1 > A2 > ... > An` 时，折叠成 inline 的 `{items}`；
  - **整个类单独待在一个花括号**里（`{A1, A2, A3}`）时，直接把成员替换进去（`{#7, #13, #19}`）并
    **丢弃图例**；
  - 只有成员落在**不相邻**位置的类才保留占位符，并在图例里用 `A = {...}` 定义；
  - 到处去掉 `permute` 一词（残留的 `permute {...}` 变成裸 `{...}`）；
  - 幸存的类按首次出现重新编号 `A, B, ...`；没有幸存占位符就不带图例。

这套归一化保证：whole-class-alone 的花括号（如 `{#3, A1, A2}` 里 A 类独占一个 brace）会被内联成
`{#7, #11}` 并省掉图例，所以 §7 的例子最终干净地呈现为 `#2 > #3 > #6 > {#7, #11}`。

---

## 9. 一个端到端例子（12,4,3 compact，step 4）

同一个比较步 `sort(#2, #3, #6, #10)` 的三条边，展示三种形状：

```
#2 > #3 > #6 > #10:  pattern: #2 > #3 > {#6, #10}            // 前缀定死，尾部 {#6,#10} 自由
#2 > #6 > #3 > #10:  pattern: #2 > A1 > {#3, A2} ; A = {#6, #10}   // A 类被拆到两处 → 保留占位符 + 图例
#6 > #10 > #2 > #3:  pattern: {#6, #10} > #2 > #3            // #2 支配 {#3} → #2、#3 被剥成链节点
```

第三条正是 §7 的 peel：原本会渲染成 `{#6, #10} > {#2, #3} ; #2 > #3`，剥离后变成
`{#6, #10} > #2 > #3`（`#2` 支配 `#3`，先剥 `#2`，尾部只剩 `#3` 不再加花括号），计数公式仍是
`2! sym x 1! tail`，无图例。

---

## 10. 测试

可读性改写是**等价改写**，所以由回归快照与专项断言守护，而非靠算法测试：

- `TopKFinder.Tests/StrategyRegressionTests.cs`
  - `N25M6K3_FifthStepRendersNineteenDoomedTailEdges` / `N25M6K3_DoomedTailEdgeCarriesExpectedPatternAndFormula`：
    pin doomed-tail 边的 pattern、计数公式、图例，并断言边数之和 = 全排列数（折叠诚实）。
  - `N12M4K3_CompactForcedTailHeadPeelsIntoLeadingChain`：pin §7 的 peel 结果，并断言一个「类被拆开」的
    兄弟边仍保留 `{...}` + 图例。
  - 多个 `..._RenderedTextMatchesSnapshot` 快照对小算例的整段文本做逐字节比对。
- `TopKFinder.Tests/ProjectionOrbitMergeTests.cs`：钉 §4.2 投影合并的两种形态——单序合并的
  `drop {...}` 披露（9,4,4 / 8,3,3）与多族结构商（7,3,2：`A1 > {A2, #7} ; ... ; drop tail(A2)`），
  并断言 `MaxStep` 不变、边数下降、且干净对称 brace（`{#1, #5} > #2`）不被误改。
- `TopKFinder.Tests/ProjectionPairingProbeTests.cs`：测量型探针（`EnableProjectionPairingProbe`，不影响渲染），
  扫描小算例量化合并节省并断言 0 诚实性泄漏。
- `TopKFinder.PerfTests/OrderedBlockHonestyTests.cs`：跨小算例断言 residual 不出现「假分裂」，保证
  折叠后的 pattern 仍诚实地覆盖每一个真实排序。

判断原则：改了渲染逻辑后，**物化树结构 / `MaxStep` / 边数必须不变**，变的只能是 `pattern:` 文字；
若某快照变了，先确认新文字确实是「更易读且等价」的改进，再更新快照。

---

## 11. 相关源码索引

| 主题 | 文件 / 符号 |
|------|-------------|
| 数据模型 | `StrategyModel.cs` → `EquivalentOrderSummary` |
| 文本渲染 | `StrategyTextRenderer.cs` → `FormatEquivalentFormsSummary`、`FormatEquivalentPatternLine`、`FormatSet`（`#i ~ #j` 缩写） |
| 轨道选择 | `StrategyBuilder.Transitions.cs` → `BuildBranchSpecs`、`SplitMergedBucketIntoBranchLines`、`PartitionFamiliesIntoOrbits`；`StrategyBuilder.Compact.cs`（compact 版） |
| relabel 同构折叠 | `StrategyBuilder.Transitions.cs` → `BuildBranchSpecForLine`、`SelectOrbitRepresentative`；`StrategyBuilder.EquivalentOrders.cs` → `BuildRelabelingOrbitSummary`、`FormatRelabelingMap`；`ComparisonState.cs` → `TryFindOrderAutomorphism` |
| 投影轨道合并（principle-D） | `StrategyBuilder.Transitions.cs` → `SplitMergedBucketIntoBranchLines`（开关 `EnableProjectionOrbitMerging`，默认 true）；`StrategyBuilder.ProjectionQuotient.cs` → `MergeOrbitsByProjection`、`BuildProjectionQuotientSummary`、`FormatActiveChain`；`MergeSingletonOrbitsByProjection`、`TryProjectionAutomorphism`（回退） |
| 投影诚实性闸 / 探针 | `StrategyBuilder.ProjectionPairingProbe.cs` → `ComponentIsSingleGlobalDropOrbit`、`EnableProjectionPairingProbe`（测量只读） |
| 通用 pattern 引擎 | `StrategyBuilder.EquivalentOrders.cs` → `BuildEquivalentOrderSummary`、`BuildEquivalentPatternSummary`、`TryBuild*Summary` 系列 |
| 归一化 / 图例 | `StrategyBuilder.EquivalentOrders.cs` → `SplitPlaceholderLegend`、`NormalizeEquivalentPattern`、`FormatBraceSet` |
| 对称类信息 | `StrategyBuilder.EquivalentOrders.cs` → `BuildGroupSymmetryInfo`；`GroupSymmetryInfo` / `GroupSymmetryClass` |
| doomed-tail 检测/分桶/轨道 | `StrategyBuilder.DoomedTailEdges.cs` → `TryBuildDoomedTailSpecs`、`ComputeDoomedPrefixLength`、`BuildDoomedPrefixKey`、`PartitionDoomedBucketsIntoOrbits` |
| doomed-tail 渲染 | `StrategyBuilder.DoomedTailEdges.cs` → `BuildDoomedTailSummary`、`BuildTailResidualConstraints`、`BuildTailFactorFormula`、peel forced-first head（~L285–349） |
| 回归 / 诚实性测试 | `TopKFinder.Tests/StrategyRegressionTests.cs`、`TopKFinder.Tests/ProjectionOrbitMergeTests.cs`、`TopKFinder.Tests/ProjectionPairingProbeTests.cs`、`TopKFinder.PerfTests/OrderedBlockHonestyTests.cs`、`TopKFinder.PerfTests/RelabelingOrbitFoldingTests.cs` |

> 搜索 / 剪枝 / 最优性相关内容见 [`core-algorithm.md`](./core-algorithm.md)。本文只覆盖它未涉及的
> **分支等价 pattern 渲染（可读性）** 部分。
