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

## PR1-PR4, PR6 Definitions
Note:
- The detailed semantics of PR1-PR4 were discussed previously but are not fully captured in persistent records.
- This section is intentionally structured as a fill-in template so the plan can be reconstructed once reviewed.

### PR1
- Objective:
- In-scope changes:
- Out-of-scope:
- Acceptance checks:
- Status:

### PR2
- Objective:
- In-scope changes:
- Out-of-scope:
- Acceptance checks:
- Status:

### PR3
- Objective:
- In-scope changes:
- Out-of-scope:
- Acceptance checks:
- Status:

### PR4
- Objective:
- In-scope changes:
- Out-of-scope:
- Acceptance checks:
- Status:

### PR6 (Queued after PR5)
Known queued items:
- Unify exact/greedy edge-compact entry shape via clearer internal helper boundary.
- Rename exact vs greedy edge-compact paths to avoid implying identical algorithmic guarantees.

Template:
- Objective:
- In-scope changes:
- Out-of-scope:
- Acceptance checks:
- Status:

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
