# 交接文档 — 投影商泛化 & 测试提速（2026-07-01）

> 换机器继续用。这份在 git 分支 `context-handoff` 上；在家 `git fetch origin && git checkout context-handoff` 即可看到。
> 更完整的历史在仓库本地记忆 `/memories/repo/`（若不跨机同步，以本文件 + git 历史为准）。

## 当前 main 状态（tip = 367c62f）
已合并：
- **#158** shape-A 投影商泛化（叶子块 + 带尾 partner）+ 「块/partner 活跃链互斥」守卫（AI 评审提的）
- **#160 → #161** 测试超时修复：#153 删了 greedy tightening 的时间预算导致 `FeasibleCompactPlanTests` 无上限跑十几分钟；#161 恢复 tightening（默认开，是被测功能本身）并删掉两个又慢又没覆盖的大算例 (16,5,5)/(25,5,5)，保留小算例（tightening 快且真正把步数从 U 压到 optimum）
- **#164** `.gitignore` 忽略 `TestResults/`、`*.trx`
- **#165** GUI computing 占位符（别人/你的 UI 工作）

已放弃：
- **#167（已 CLOSED）** 把 `StrategyRegressionTests` 拆成 7 个并行类。本地 8 核 245→202s 有效，但 **GitHub CI runner 核太少，线上仍 2m+ 无改善**，不值得，已关闭删分支。→ 测试并行化这条路结束。给 CI 提速要换方向（减重算例计算量 / 换大 runner）。

## 剩余优化路线图（投影商，优先级从高到低）
1. **shape B**（下一个要做的，风险最低偏中）——见下节
2. shape C（heads=4，2+2，注意有「固定前缀 + 尾部对称块」如 (10,4,4) `#3 > #8` 是固定不对称）
3. shape D（heads=5，3+2，如 (12,5,5)）
4. 更脏的多-drop 分量（(10,3,4) `[#3>#10>#5|#3>#5>#10]` drop 同时含 partner+块成员；≥3 轨道 (11,4,3)/(11,4,4)）
5. **TODO #1**（难）：compact 目标函数 `CountDisplayBranches` 用 `fixedTopMask:0` 分桶，导致 (10,3,4) compact 显示 11 而实际可达 9。不损正确性（orchestrator 丢弃更差 compact），真修需 order-aware DAG 模拟。
6. **TODO #3**（最后做）：删掉 `EnableProjectionOrbitMerging` 开关 + 所有 merge-OFF 回退/对照测试/baseline。

## shape B 具体情况（下一步）
和 canonical/shape-A 一样是 3 头 = 对称块 {b1,b2} + partner p，但**分量形状不同**：
- canonical/shape-A：分量 = 「块成员排第 1」（partner 可在中/尾）。
- **shape B：分量 = `{b1,b2} > p`（块整体压在 partner 之上，partner 恒为最后一名/唯一最小）。**

reject 例子（probe 记录）：**(8,3,3)** 两条代表序 `[#2 > #5 > #7 | #5 > #2 > #7]`，drop `{#7,#8}`，目标折成 `{#2, #5} > #7`。
- 为什么现在被拒：`#2>#5>#7` 与 `#5>#2>#7` 在父偏序下**并不对称**，只有丢掉共同注定出局的 `{#7,#8}`（partner #7 + 其尾 #8）后才互换。裸 brace `{#2,#5} > #7` 会**不诚实**（谎称 #2、#5 可自由换）。
- 现状：`BuildProjectionQuotientSummary`（`StrategyBuilder.ProjectionQuotient.cs`）只做 `A1 > {A2, #p}`（block-member-on-top），**没有** shape B 的「block-over-partner + drop 披露」分支。
- 要设计的记号：形如 `{b1, b2} > #p ; drop {...}`（或协变 `drop tail(...)`）。计数大概 `4 = 2! x 2`（待验证）。

### 恢复 shape B 的第一步（照 shape A 的成功流程）
先做「worked example」再动代码：跑定向 probe / 渲染，把**确切的 before（原始各分支）+ 现存拆分记号 + 拟合并记号 + 确切 drop 集**列出来给 dntx 确认记号，**然后**才实现。
注意：默认渲染里 (8,3,3) `sort(#2,#5,#7)` 那个 `{#2,#7}`/partner `#5` 的节点是「不同后继、真不可折」，**不是** shape B 的那个节点——真正的 shape B reject 节点还没在树里逐一定位，需重跑 probe（`EnableProjectionPairingProbe` 或临时 reject-dump）。

## shape A 实现范式（照抄用于 B/C/D）
`BuildProjectionQuotientSummary`：只接受能忠实描述的形状，否则返回 null → 回退诚实拆分。多族分量折叠需两道闸：(a) `ComponentIsSingleGlobalDropOrbit`（诚实性，`ProjectionPairingProbe.cs`）+ (b) 结构渲染器能表达。测试范式：`ProjectionOrbitMergeTests.cs` 用 `Build(n,m,k, projectionMerging)` 对比 on/off，断言 MaxStep 不变、边数下降、off 不出现商记号。文档：`docs/strategy-output.md` §4.2。

## 关键约定 / 偏好
- **用中文交流。**
- **绝不自行挂 auto-merge**，除非 dntx 明确确认/下令。开完 PR 停下。
- PowerShell 无 heredoc：多行提交信息写临时文件 `.git/COMMIT_MSG_*.txt` 再 `git commit -F`。
- gh CLI 可用（账号 dntx）；github-pull-request_* / gitkraken MCP 工具因 EMU 受限，用 gh CLI。
- 仓库用 squash 合并；分支保护要求 up-to-date + `required-tests` **且** `review`（AI 评审）两个必需检查通过。
- AI 评审坑：diff 过大时会 413 fail-open（假通过）；首轮 BLOCK 会留下 `CHANGES_REQUESTED` 评审，需 dismiss（`gh api -X PUT repos/dntx/Sort/pulls/N/reviews/ID/dismissals`）。#162/#163 已改善。
- 杀孤儿测试进程：`Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | ? {$_.CommandLine -match 'testhost|vstest|TopKFinder.Tests'} | % { Stop-Process -Id $_.ProcessId -Force }`

## 记忆文件（完整历史）
- `/memories/repo/projection-orbit-merging.md` — 投影商全过程（含 shape A DONE、TODO #1 refined root-cause、TODO #3 清理清单、B/C/D 泛化备注）
- `/memories/repo/test-suite-timing.md` — 测试墙钟模型、#153 回归、孤儿进程坑、并行拆分结论
- `/memories/preferences.md` — 中文 + 不自行 auto-merge
