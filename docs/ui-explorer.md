# WinForms UI Explorer

本文说明桌面 UI（WinForms explorer）的交互模型与运行语义，帮助在不读源码的前提下理解界面行为。

## 1. 入口与运行模式

- 无命令行参数且无 stdin 重定向时启动桌面 UI。
- 模式与 CLI 一致：
  - `exact`：`step-proof → exact-edge-compact@S`
  - `greedy`：`greedy-feasible → (optional) greedy-tighten → proof-tighten≤N (0..n 次) → greedy-edge-compact@S`

实现归属：

- UI 与 CLI 共用 `PublicPipelineOrchestrator` 的阶段编排与阶段名契约。
- UI 侧的 `MainForm.Run.cs` 只负责线程切换、占位/树更新与交互，不再维护独立的并行编排路径。

## 2. 阶段时间线与占位

UI 使用与 CLI 相同的阶段名展示进度：

- `step-proof`
- `greedy-feasible`
- `greedy-tighten`（可选，root-probe 通过才运行）
- `proof-tighten≤N`
- `exact-edge-compact@S`（exact 终段）
- `greedy-edge-compact@S`（greedy 终段）

当阶段尚在运行时，树视图会显示 `computing...` 占位；阶段完成后替换为真实结果。greedy 流水线可能产生多个 tightening 阶段，按完成顺序追加。

## 3. 运行中可见信息

运行时会持续刷新：

- `searched / pending / output` 状态计数
- 当前最优 root 上界（incumbent）
- 下界剪枝、缓存命中等诊断计数
- 分阶段耗时（step / compact）

详情面板还会显示 root incumbent 时间线，用于观察最坏步数何时被首次找到、何时继续收紧。

### 大规模策略树下的惰性加载

为避免在阶段切换时阻塞 UI 线程（导致 elapsed/status 暂停刷新），大规模树的部分辅助信息采用按需加载：

- 状态跳转索引（用于 overview -> tree 的定位）在首次需要时构建。
- Overview 分区内容在展开对应 section 时构建，而不是一次性全量生成。
- 详情面板中的重型文本在选中节点后异步加载，加载中会显示 `Loading details...`。

上述改动不影响求解语义与结果，仅影响 UI 的响应性与信息加载时机。

## 4. 进度条设计与更新语义

进度条分为多个阶段带（stage bands），每个阶段占据一定比例的总进度：

- `greedy-feasible`：占 0-10%（可行阶段）
- `greedy-tighten`：占 10-20%（紧化阶段，若执行）
- `proof-tighten`：占 20-90%（证明阶段）
- `edge-compact`：占 90-100%（边界紧凑化）

**进度报告机制**：
- 每个阶段内部使用**时间基准的渐进式估计**：`progress = elapsed / (elapsed + remaining_estimate)`
- 系统周期性地轮询进度（间隔 100ms），确保在长时间运行的阶段（如 feasible 阶段）中持续显示更新
- 在大规模递归搜索期间（BuildState 递推），进度报告在每个递归深度被触发，而不只在阶段边界被触发

**可视化改进**:
- 通过在递归内部报告进度，消除了"进度跳跃后停滞"的问题
- feasible 阶段现在从 0% 平滑增长到 10%，而不是跳到 4-7% 后冻结
- 保守的剩余时间估计（最小 500ms）确保进度条永不完全停滞
- 所有阶段在完成时立即跳到其带的上界（e.g., feasible 完成→10%）

## 5. 停止与取消语义

- 点击 **Stop** 会请求取消当前搜索。
- `exact` 模式：通常是 all-or-nothing，取消时不承诺可展示部分最优树。
- `greedy` 模式：保留已完成阶段的最佳可行计划（上界），即使中途取消也会展示 best-so-far。

## 6. 代码入口索引

- 运行编排与阶段回调：`MainForm.Run.cs`
- 概览与统计展示：`MainForm.Overview.cs`
- 树视图渲染：`MainForm.Tree.cs`
- 详情面板：`MainForm.Details.cs`
- 控件初始化：`MainForm.Controls.cs`
