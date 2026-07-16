# 6-PR Refactor Roadmap

Last updated: 2026-07-16

This document is the source-of-truth for the 6-PR refactor track that decouples search-tree construction from display/render behavior.

## Purpose

The goal is to make the system easier to evolve, reason about, and validate by separating:

- search semantics: how the strategy tree is built and evaluated
- display semantics: how the same search result is rendered, folded, and presented

The long-term target is a cleaner layered architecture, while keeping behavior stable during the transition.

## Guiding Principles

1. Preserve existing behavior during the migration.
2. Prefer additive changes first, then switch over in a controlled way.
3. Keep parity and regression tests active throughout the migration.
4. Treat this document as the canonical roadmap for the refactor track.
5. Any behavior change must be documented here and reflected in the relevant tests.

## Implementation Reality Check (2026-07-16)

This section is written as the expected repository state after the PR updating this file is merged to main.

1. PR1 status: landed on main
    - Added: `SearchNode` / `SearchBranch` / `SearchEffect` / `SearchStrategy`, plus
       `SearchModelMapper`.
   - Existing `StrategyNode` / `StrategyBranch` path remains primary; no behavior switch yet.

2. PR2 status: landed on main
   - Added a parallel `BuildSearchTree()` entry point that projects the existing step-proof plan into `SearchStrategy`.
   - The underlying build pipeline still runs through `BuildState`-based logic; this PR is additive and does not switch the primary path yet.

3. PR3 status: landed on main
   - Added a lightweight `DisplayRenderEngine` skeleton as the explicit display-path facade.
   - Added parity-guard coverage to lock `DisplayRenderEngine` output equivalence with the existing renderer path for targeted cases.

4. PR4 status: landed on main
   - Display folding planning / projection-orbit merge / projection-automorphism helper logic now routes through `DisplayRenderEngine`-owned display-layer helpers.
   - `StrategyBuilder.*` still owns search-state-specific adapters and shape renderers, but the display-path control flow is no longer centered in the build logic.

5. PR5 status: landed on main
   - Compact objective now uses search-tree edge semantics and surfaces `SearchStatistics.SearchTreeEdges`.
   - This is an existing main-branch baseline status, not a change introduced by this PR1 document/update PR.

6. PR6 status: not landed
   - Public pipeline has not switched to a new layered search-model + display-model architecture.
   - Legacy glue has not been removed because the new layered architecture is not yet present.
   - The naming split between exact-edge-compact@S and greedy-edge-compact@S has not been fully rolled out in public-facing labels/docs.

## Pending Memo (Actionable)

This section is a working reminder of unfinished items already identified in the reality-check snapshot.

1. Public main-path switch (PR6 core)
   - [ ] Route the public build/render pipeline through an explicit layered flow: search-model stage -> display-model stage.
   - [ ] Keep `BuildSearchTree()` as the canonical entry point for the search layer (instead of parallel-only status).

2. Legacy glue cleanup
   - [ ] Remove obsolete compatibility/control-flow glue that exists only for the pre-layered migration path.
   - [ ] Keep only adapters that are still required by tests or externally supported APIs.

3. Public naming rollout (exact vs greedy compact)
   - [x] Replace remaining user-facing `edge-compact@S` labels with the agreed naming split:
   - exact pipeline terminal compact stage: `exact-edge-compact@S`
   - greedy pipeline terminal compact stage: `greedy-edge-compact@S`
   - [x] Apply the split consistently across CLI/help text, UI stage labels, overview/detail panes, and docs.
   - [x] Re-baseline/update tests that intentionally pin stage labels.

4. Documentation closure
   - [ ] After PR6 lands, update this roadmap's reality-check section to reflect landed status.
   - [ ] Add a concise architecture note documenting long-term ownership and boundaries between search and display layers.

## How to Read This Roadmap

- Sections "PR1..PR6" below remain the intended target plan.
- The "Implementation Reality Check" section is the authoritative current-state snapshot.
- When code lands, update both the PR scope text (if needed) and the status snapshot above.

## Decisions Already Agreed

1. Search vs display boundary
   - The display layer owns equivalence rules, branch-pattern generation, and human-oriented presentation optimizations.
   - The search layer remains the purely mathematical/search-oriented side of the pipeline.

2. PR3/PR4 scope
   - PR3 should be lightweight and functional enough to establish the new display path without over-committing to complex behavior.
   - Complex display work is intentionally deferred to PR4.

3. Naming and terminology
   - There is no separate "display-edge compact" naming; compact paths remain under the search-edge compact framing.
   - The important distinction is between exact edge compact and greedy edge compact.
   - Canonical naming rule: use "exact-edge-compact@S" for the exact-mode compact stage and "greedy-edge-compact@S" for the greedy pipeline's terminal compact stage.
   - PR6 must propagate this naming rule consistently across CLI/help text, UI stage labels, and docs.

## 6-PR Roadmap

### PR1 — New data model (additive only)

Objective:
- Introduce new search/display model types in parallel with the existing model.

Scope:
- Additive type definitions such as SearchNode, SearchBranch, SearchEffect, SearchStrategy.
- Keep the existing StrategyNode / StrategyBranch path intact.

Out of scope:
- No behavior switch.
- No renderer/path replacement.

Acceptance:
- Compiles with zero behavior changes.
- Existing tests remain green.

### PR2 — Pure algorithm path in parallel

Objective:
- Add a pure search-tree build path without replacing the current path.

Scope:
- Introduce a search-tree-oriented build path (for example, BuildSearchTree-style logic).
- Keep the current BuildState path intact.

Out of scope:
- No rendering pipeline migration yet.

Acceptance:
- Selected parity tests confirm that the new path preserves step-optimal behavior.

### PR3 — DisplayRenderEngine skeleton + parity guard

Objective:
- Introduce an explicit display render engine with initial 1:1 mapping.

Scope:
- Introduce a DisplayRenderEngine skeleton.
- Keep parity checks in place to verify equivalence with the current path.
- Keep this PR intentionally lightweight: it should be useful, but it does not need to force all complex display behavior to be perfect up front.

Out of scope:
- No full folding logic migration yet.
- Complex display semantics are deferred to PR4.

Acceptance:
- The new display path is usable and preserves current behavior for the targeted cases.
- Parity checks are in place, but the focus is on establishing the skeleton rather than over-engineering the first step.

### PR4 — Folding logic migration into render engine

Objective:
- Move display folding behavior out of the search/build logic into the display layer.

Scope:
- Migrate folding-related behaviors such as doomed-tail, symmetry-orbit, and projection merge handling.
- Keep parity tests active through the migration.
- This PR is where the more complex display-oriented behavior is handled in a deliberate, careful way.

Out of scope:
- No compact objective semantic switch yet.

Acceptance:
- Folding parity is preserved for migrated tracks.
- Existing regressions remain green or are intentionally re-baselined with rationale.
- The richer display logic is validated in a more detailed parity pass here.

### PR5 — Compact semantic clarification (landed on main)

Objective:
- Switch the compact objective from display-coupled counting to a search-tree edge objective.

Scope:
- Remove display-layer coupling from the compact objective path.
- Use a search-tree-based objective (for example, child count recurrence).
- Surface first-class search metrics such as SearchStatistics.SearchTreeEdges.
- Add regression locks for search objective and compact work counters.

Acceptance:
- Compact objective preserves max-step behavior while not regressing search-tree edge performance.
- Compact work counters remain stable relative to the agreed baseline.
- Greedy edge-compact coverage explicitly asserts the new objective.

### PR6 — Main-path switch + cleanup

Objective:
- Switch the public pipeline to the new layered model and remove obsolete legacy glue.

Scope:
- Route the public plan-building flow through separated search + display stages.
- Remove obsolete or dead compatibility paths.
- Apply the edge-compact naming split consistently in public-facing surfaces (exact vs greedy).

Acceptance:
- Full regression suite is green after the switch.
- Documentation reflects the final architecture.
- Public naming is consistent: exact-mode compact stage is labeled as exact-edge-compact@S, and greedy-mode terminal compact stage as greedy-edge-compact@S.

## Working Conventions

- Keep PRs focused and incremental.
- Each PR should have a clear acceptance bar.
- If a PR changes externally visible behavior, document the change here and in the relevant tests.
- Avoid introducing speculative abstractions that are not yet exercised by tests.

## Open Questions / Decisions to Settle

The following decisions have been clarified for now:

1. Boundary definition
   - Display layer covers equivalence rules, branch-pattern generation, and human-oriented presentation optimizations.
   - Search layer remains the purely mathematical/search-oriented side of the pipeline.

2. PR3/PR4 scope
   - PR3 should be lightweight and functional enough to establish the new display path, without over-committing to complex behavior.
   - Complex display work is intentionally deferred to PR4.

3. Acceptance criteria for PR3/PR4
   - PR3 can use a lightweight parity strategy for the targeted cases.
   - PR4 should revisit parity more carefully once the richer display logic is moved over.

4. Naming and terminology
   - There should be no separate "display-edge compact" naming; all compact paths remain under the search-edge compact framing.
   - The important distinction is between exact edge compact and greedy edge compact.
   - Canonical naming rule: use "exact-edge-compact@S" (exact mode) and "greedy-edge-compact@S" (greedy mode terminal stage), and keep this split consistent across UI/CLI/docs.

5. Long-term ownership
   - Once PR6 lands, the search-layer and display-layer interfaces should be documented as first-class architecture boundaries in the repository.

## Resumption Checklist

When picking up work again:

1. Pull latest main.
2. Switch to the active PR branch.
3. Read this document first.
4. Verify the current PR’s acceptance bar before changing scope.
5. Keep the refactor track grounded in the roadmap above.
