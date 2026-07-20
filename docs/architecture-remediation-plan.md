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
- Batch A2: Done.
- Batch A3: Done.
- Batch B1.2: Done. SearchTransitionPlanner is now an internal top-level seam with injected dependencies, with no public API or user-facing behavior change.
- Batch B2.1: Done. State key types were extracted from ComparisonState into StateKeyTypes.cs with no behavioral change.
- Batch B2.2: Done. Canonicalization and automorphism helpers were extracted to ComparisonState.Algorithms.cs; ComparisonState now primarily owns state, caches, mutation entrypoints, and delegating calls.

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
- Batch: B2.2
- Status: Done
- Changed files:
  - ComparisonState.cs
  - ComparisonState.Algorithms.cs
  - TopKFinder.Tests/ComparisonStateTests.cs
- Behavior impact: Equivalent (pure helper extraction only; canonicalization, active-item colors, and order-automorphism behavior remain unchanged)
- Verification commands:
  - dotnet build d:/Code/Sort2/TopKFinder.csproj
  - dotnet test ./TopKFinder.Tests/TopKFinder.Tests.csproj --filter "ComparisonStateTests|FreeSymmetryClassTests|SearchStateKeyServiceTests|ProjectionOrbitMergeTests"
- Verification result:
  - build: succeeded
  - tests: implementation session reported passing before PR 400 check-in
  - local acceptance review: relevant files and tests showed no diagnostics
- Risks/notes:
  - ComparisonState now delegates canonicalization and automorphism logic to ComparisonState.Algorithms.cs while retaining state ownership and cache invalidation.
  - Repo documentation sync was completed after PR 400 merge to match the session plan.

- Batch: B2.1
- Status: Done
- Changed files:
  - StateKeyTypes.cs
  - ComparisonState.cs
- Behavior impact: Equivalent (key-type extraction only; no canonicalization or search behavior change)
- Verification commands:
  - dotnet build d:/Code/Sort2/TopKFinder.csproj
  - dotnet test ./TopKFinder.Tests/TopKFinder.Tests.csproj --filter "ComparisonStateTests|SearchStateKeyServiceTests|FreeSymmetryClassTests|StrategyBuilderSessionTests"
- Verification result:
  - build: succeeded
  - tests: implementation session reported passing before PR 399 check-in
  - local acceptance review: relevant files and tests showed no diagnostics
- Risks/notes:
  - IntSequenceKey, RawStructureKey, SearchStateKey, and BuildStateKey were moved out of ComparisonState into StateKeyTypes.cs.
  - This batch intentionally avoided algorithm extraction; that follow-up was completed in B2.2.

- Batch: B1.2
- Status: Done
- Changed files:
  - SearchTransitionPlanner.cs
  - StrategyBuilder.Transitions.cs
  - StrategyBuilder.HelperTypes.cs
  - StrategyBuilder.OrderEnumeration.cs
  - TopKFinder.Tests/SearchTransitionPlannerStructureTests.cs
- Behavior impact: Equivalent (internal transition-planner extraction only; StrategyBuilder still exposes the same public entrypoints and transition behavior)
- Verification commands:
  - dotnet build d:/Code/Sort2/TopKFinder.csproj
  - dotnet test ./TopKFinder.Tests/TopKFinder.Tests.csproj --filter "DisplaySearchParityTests|ProjectionKernelTests|ProjectionOrbitMergeTests|ProgramHeadlessRenderingTests|SearchTransitionPlannerStructureTests|StrategyRegressionTests"
- Verification result:
  - build: succeeded (0 warnings, 0 errors)
  - tests: execution currently blocked in this environment by application control policy when test code loads TopKFinder.dll (`System.IO.FileLoadException`, `0x800711C7`)
- Risks/notes:
  - This batch is developer-facing only. It promotes a previously nested planner to an internal top-level type and narrows its dependency surface from a coarse StrategyBuilder owner reference to injected callbacks.
  - No README or user documentation change is required because the refactor does not change CLI/UI behavior or public API surface.

- Batch: A2
- Status: Done
- Changed files:
  - Program.cs
  - MainForm.Run.cs
  - PublicPipelineOrchestrator.cs
  - PipelineStageProtocol.cs
  - README.md
- Behavior impact: Equivalent (entry orchestration and stage protocol exits are consolidated; algorithm/output semantics unchanged)
- Verification commands:
  - dotnet build d:/Code/Sort2/TopKFinder.csproj
  - dotnet test ./TopKFinder.Tests/TopKFinder.Tests.csproj --filter "CliArgsTests|ProgramHeadlessRenderingTests|MainFormRenderingTests|ExactPipelineTests|GreedyPipelineTests"
- Verification result:
  - build: succeeded (0 warnings, 0 errors)
  - tests: passed (65/65)
- Risks/notes:
  - A2 adds explicit public documentation for shared entry orchestration boundaries and protocol ownership in README.

- Batch: A1
- Status: Done
- Changed files:
  - ItemSetFormatter.cs (new; neutral shared formatter for item-set text formatting used across core/display boundary)
  - StrategyBuilder.Core.cs
  - StrategyBuilder.RelabelingOrbit.cs
  - StrategyTextRenderer.cs
- Behavior impact: Equivalent (set-format output rules remain unchanged; the shared formatter is extracted to ItemSetFormatter.cs and renderer delegates to it)
- Verification commands:
  - rg --line-number "StrategyTextRenderer\\." StrategyBuilder*.cs
  - dotnet build d:/Code/Sort2/TopKFinder.csproj
  - dotnet test ./TopKFinder.Tests/TopKFinder.Tests.csproj --filter "DisplayRenderEngineTests|StrategyTextRendererTests|ProgramHeadlessRenderingTests|MainFormRenderingTests"
- Verification result:
  - rg: no matches
  - build: succeeded (0 warnings, 0 errors)
  - tests: passed (19/19)
- Risks/notes:
  - ItemSetFormatter.cs is intentionally the single shared implementation of set formatting; if formatting rules change in future, update this shared formatter to keep core and display behavior aligned.

## Next Action
Decide whether to stop after B2.2 or dispatch a smaller B2.3 follow-up for any remaining ComparisonState state/cache split that still provides clear value.
