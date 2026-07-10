# WinForms UI Explorer

本文说明桌面 UI（WinForms explorer）的交互模型与运行语义，帮助在不读源码的前提下理解界面行为。

## 1. 入口与运行模式

- 无命令行参数且无 stdin 重定向时启动桌面 UI。
- 模式与 CLI 一致：
  - `exact`：`step-proof → edge-compact@S`
  - `greedy`：`greedy-feasible → proof-tighten≤N (0..n 次) → edge-compact@S`

## 2. 阶段时间线与占位

UI 使用与 CLI 相同的阶段名展示进度：

- `step-proof`
- `greedy-feasible`
- `proof-tighten≤N`
- `edge-compact@S`

当阶段尚在运行时，树视图会显示 `computing...` 占位；阶段完成后替换为真实结果。greedy 流水线可能产生多个 tightening 阶段，按完成顺序追加。

## 3. 运行中可见信息

运行时会持续刷新：

- `searched / pending / output` 状态计数
- 当前最优 root 上界（incumbent）
- 下界剪枝、缓存命中等诊断计数
- 分阶段耗时（step / compact）

详情面板还会显示 root incumbent 时间线，用于观察最坏步数何时被首次找到、何时继续收紧。

## 4. 停止与取消语义

- 点击 **Stop** 会请求取消当前搜索。
- `exact` 模式：通常是 all-or-nothing，取消时不承诺可展示部分最优树。
- `greedy` 模式：保留已完成阶段的最佳可行计划（上界），即使中途取消也会展示 best-so-far。

## 5. 代码入口索引

- 运行编排与阶段回调：`MainForm.Run.cs`
- 概览与统计展示：`MainForm.Overview.cs`
- 树视图渲染：`MainForm.Tree.cs`
- 详情面板：`MainForm.Details.cs`
- 控件初始化：`MainForm.Controls.cs`
