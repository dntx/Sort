# Architecture Remediation Plan

## Scope
Refactor governance plan for architecture boundaries, naming consistency, and maintainability improvements.

## Workflow
1. Plan mode maintains the single source of truth.
2. Agent mode executes one batch at a time.
3. Every batch must return changed files, behavior impact, verification results, and status update.

## Status (2026-07-20)
- Implementation sync: Done. main already contains copilot/worktree-2026-07-19T13-02-57 via merge commit edec22e.
- Batch A1: Done in current worktree.
- Batch A2: Ready.
- Batch A3: Pending after A2 stabilization.

## Batch A1 - Remove Core to Display Reverse Dependency
### Goal
Core search logic must not directly depend on text rendering APIs.

### Agent Dispatch Card
1. Scope and boundaries
- Change only A1-related files unless tests require minimal touch-ups.
- Do not change algorithm semantics, stage behavior, or user-visible output meaning.
- Prefer structural data flow from core to display layer; formatting remains in display layer.

2. Required implementation points
- Remove direct StrategyTextRenderer usage from StrategyBuilder*.cs files.
- Replace core-side text formatting with structured payload or primitive data and map formatting in display layer.
- Keep behavior equivalent for current outputs.

3. Definition of Done
- grep check returns no direct StrategyTextRenderer usage under StrategyBuilder*.cs.
- Build succeeds.
- Affected renderer and regression tests pass.

4. Verification commands
- rg --line-number "StrategyTextRenderer\\." StrategyBuilder*.cs
- dotnet build d:/Code/Sort2/TopKFinder.csproj
- dotnet test ./TopKFinder.Tests/TopKFinder.Tests.csproj --filter "DisplayRenderEngineTests|StrategyTextRendererTests|ProgramHeadlessRenderingTests|MainFormRenderingTests"

5. Return format
- Changed files
- Behavior impact: equivalent or changed (with reason)
- Verification commands and results
- Final status for A1: Done or Blocked (with blocker)

### Target files
- StrategyBuilder.Core.cs
- StrategyBuilder.RelabelingOrbit.cs
- DisplayRenderEngine.cs
- StrategyTextRenderer.cs

### Acceptance
- No direct StrategyTextRenderer calls from StrategyBuilder* files.
- Behavior equivalent outputs and tests pass.

### Verification
- dotnet build d:/Code/Sort2/TopKFinder.csproj
- Run affected rendering and regression tests.

## Batch A2 - Consolidate Entry Orchestration
### Goal
Keep Program/MainForm at input/output boundary and move orchestration policy to shared protocol/orchestrator.

### Depends on
- Batch A1

### Target files
- Program.cs
- MainForm.Run.cs
- PublicPipelineOrchestrator.cs
- PipelineStageProtocol.cs

### Acceptance
- CLI and UI stage semantics are consistent.
- At least one duplicated orchestration branch removed from entry layer.

## Batch A3 - Exact/Greedy Naming Consistency
### Goal
Unify mode and stage wording to exact/greedy; remove legacy A/B language.

### Depends on
- Batch A2 (main flow)

### Target files
- MainForm.cs
- Program.cs
- StageNames.cs
- related tests

### Acceptance
- No A/B mode wording in user-facing text or comments.
- CLI help, UI labels, and tests are consistent.

## Batch Execution Record Template
- Batch:
- Status: Todo | InProgress | Blocked | Done
- Changed files:
- Behavior impact:
- Verification commands:
- Verification result:
- Risks/notes:

## Batch Execution Record (latest)
- Batch: A1
- Status: Done
- Changed files:
  - StrategyBuilder.Core.cs
  - StrategyBuilder.RelabelingOrbit.cs
  - StrategyBuilder.HelperTypes.cs
- Behavior impact: Equivalent (formatting output preserved while removing direct renderer dependency from core StrategyBuilder files)
- Verification commands:
  - rg --line-number "StrategyTextRenderer\\." StrategyBuilder*.cs
  - dotnet build d:/Code/Sort2/TopKFinder.csproj
  - dotnet test ./TopKFinder.Tests/TopKFinder.Tests.csproj --filter "DisplayRenderEngineTests|StrategyTextRendererTests|ProgramHeadlessRenderingTests|MainFormRenderingTests"
- Verification result:
  - rg: no matches
  - build: succeeded (0 warnings, 0 errors)
  - tests: passed (19/19)
- Risks/notes:
  - Core now uses a local item-set formatter helper; if display-layer set formatting rules change in future, keep this helper aligned intentionally.

## Next Action
Dispatch Batch A2 using the Agent Dispatch Card above.
