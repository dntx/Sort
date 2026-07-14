# Search/Display Decoupling - 6 PR Roadmap Handoff

Last updated: 2026-07-14
Current working branch: `pr5-compact-search-edges`
Current PR: #300

## Purpose
This file is the handoff source of truth for the 6-PR refactor track, so work can continue on another machine without relying on chat history.

## Status Snapshot
- Active work is **PR5** on branch `pr5-compact-search-edges`.
- Latest commits on this branch (newest first):
  - `cd92b2a` merge 1244 display-edge pin into compact baseline theory
  - `6f340ca` record greedy edge objective in final compact stage
  - `e2cfc69` cover greedy edge-compact stage with compact-work assertions
  - `ab2ae5c` promote search-tree edge metric into SearchStatistics
  - `3b90904` assert compact search-edge objective non-regression
  - `edb435d` strengthen greedy-tighten value-lock coverage
  - `b96517f` refactor compact objective to search-tree edge count

## PR5 (In Progress / Mostly Complete)
Goal:
- Move compact objective to **search-tree edge objective** and remove display-layer coupling.

Implemented in PR5:
- Compact DP objective switched to search-tree objective (`children.Count + recursive child costs`).
- Search objective surfaced as first-class metric: `SearchStatistics.SearchTreeEdges`.
- Exact-path regression lock added: compact search objective must not regress vs baseline step-optimal policy.
- Greedy edge-compact coverage added, including explicit `SearchTreeEdges` presence in final compact stage plan.
- Display-edge pin tests re-baselined to current behavior (now separate from search objective assertion).

Validation used:
- `dotnet build TopKFinder.csproj /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary`
- `dotnet test TopKFinder.Tests/TopKFinder.Tests.csproj --filter "FullyQualifiedName~Compact_PinsCurrentDisplayEdgeBaseline|FullyQualifiedName~Compact_PreservesMaxStepAndDoesNotRegressSearchEdges|FullyQualifiedName~Compact_WorkCountersStayWithinBaseline"`
- `dotnet test TopKFinder.Tests/TopKFinder.Tests.csproj --filter "FullyQualifiedName~GreedyPipeline_EdgeCompactStage_ReportsCompactWork"`

## Agreed 6-PR Plan (Original)
This section captures the previously agreed roadmap semantics.

### Architecture Choice
- Preferred path: Route A (gradual migration, lower risk, 6 PRs).
- Alternate path discussed: Route B (2-3 PR minimal split), but not selected for this track.

### PR1 - New data model (additive only)
Objective:
- Introduce new search/display model types in parallel with existing ones.

In-scope:
- Additive type definitions in `StrategyModel.cs` (or equivalent) such as `SearchNode`, `SearchBranch`, `SearchEffect`, `SearchStrategy`.
- No removal of existing `StrategyNode` / `StrategyBranch` yet.

Out-of-scope:
- No behavior switch.
- No renderer/path replacement.

Acceptance:
- Compiles with zero behavior changes.
- Existing tests pass unchanged.

### PR2 - Pure algorithm path in parallel
Objective:
- Add a pure search-tree build path without replacing current path.

In-scope:
- Add `BuildSearchTree(...)`-style path in builder (`TopKFinder.cs` and/or split file).
- Keep old `BuildState()` path intact.

Out-of-scope:
- No rendering pipeline migration yet.

Acceptance:
- New tests assert step parity: `BuildSearchTree().MaxStep == BuildStepProofStage().MaxStep` on selected shapes.

### PR3 - DisplayRenderEngine skeleton + parity guard
Objective:
- Introduce explicit display render engine with initial 1:1 mapping.

In-scope:
- New `DisplayRenderEngine` skeleton with no advanced folding in first step.
- Add parity tests to verify output equivalence against existing path.

Out-of-scope:
- No full folding logic migration yet.

Acceptance:
- Parity tests pass for selected canonical shapes.

### PR4 - Folding logic migration into render engine
Objective:
- Move display folding behavior out of search/build path into display layer.

In-scope:
- Migrate folding tracks (doomed-tail, symmetry orbit, projection merge) in stages.
- Keep parity tests active throughout migration.

Out-of-scope:
- No compact objective semantic switch (that is PR5).

Acceptance:
- Folding parity maintained for migrated tracks.
- Existing regression tests remain green or intentionally re-baselined with rationale.

### PR5 - Compact semantic clarification (current PR)
Objective:
- Switch compact objective from display-coupled counting to search-tree edge objective.

In-scope:
- Remove display-layer coupling from compact objective path.
- Use search-tree objective (`children.Count` recurrence).
- Add/adjust regression locks for search objective and compact work counters.
- Surface `SearchStatistics.SearchTreeEdges` and wire exact + greedy compact outputs.

Acceptance:
- `Compact_PreservesMaxStepAndDoesNotRegressSearchEdges` green.
- `Compact_PinsCurrentDisplayEdgeBaseline` green with pinned expectations.
- Greedy edge-compact coverage includes explicit edge-objective assertion.

### PR6 - Main-path switch + cleanup
Objective:
- Switch public pipeline to new layered model and remove obsolete legacy glue.

In-scope:
- Route public plan-building through separated search + display pipeline.
- Cleanup dead/legacy paths after parity confidence.

Known queued naming/task items:
- Unify exact/greedy edge-compact entry shape via clearer internal helper boundary.
- Rename exact vs greedy edge-compact paths to avoid implying identical algorithmic guarantees.

Acceptance:
- Full regression suite green after switch.
- Documentation updated for final architecture.

## Size/Risk Snapshot (agreed planning estimate)
- PR1: low risk, additive model definitions.
- PR2: low risk, parallel algorithm path.
- PR3: medium risk, renderer skeleton + parity checks.
- PR4: medium risk, staged fold migration.
- PR5: medium risk, compact objective semantic switch (current work).
- PR6: highest risk, final path switch + cleanup.

## Cross-Machine Resume Checklist
1. `git fetch origin`
2. `git checkout pr5-compact-search-edges`
3. `git pull --ff-only`
4. Open this file first: `docs/pr-roadmap-handoff.md`
5. Run focused regression commands listed in PR5 section.

## PR Workflow Guardrails
- Do not merge without explicit user instruction.
- Required checks: `required-pr-tests` and `ai-code-review`.
- If behavior/capability changes, include docs touch (for AI review gate consistency).
