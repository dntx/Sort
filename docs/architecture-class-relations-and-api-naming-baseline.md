# Architecture Class Relations And API Naming Baseline (Authoritative)

This document is the authoritative baseline for the upcoming architecture and API naming migration.
If chat history and code comments diverge, follow this file.

Date: 2026-07-18
Owner decision snapshot:

- Dependency direction is strict: display may depend on search; search must not depend on display.
- Transition policy: one-iteration transition window.
- Naming direction: pipeline uses Run*; single-stage execution uses Execute*.
- Priority: structural cleanliness over minimal short-term churn.

---

## 1. Scope

This baseline governs two tracks:

1. Class relation adjustments (layer boundaries and dependencies).
2. Public API naming adjustments (stage and pipeline naming consistency).

Out of scope for this baseline:

- Algorithm behavior changes.
- Performance tuning unrelated to boundary/naming cleanup.
- UI interaction redesign.

---

## 2. Layer Boundary Contract

Target layer direction:

- Search layer: solves strategy semantics and stage feasibility/optimality.
- Public orchestration layer: sequences stages for shared callers (CLI/UI).
- Display layer: presentation only (overview/text/UI rendering and display-focused folding).

Allowed dependency direction:

- Orchestration -> Search
- Display -> Search
- UI/CLI host -> Orchestration and Display

Forbidden dependency direction:

- Search -> Display

Interpretation rule:

- Any search-side type that imports display facade APIs is a contract violation.
- Shared algorithmic helpers used by both search and display must move to a neutral kernel module.

---

## 3. Current Violations To Remove First

The following call paths represent known search->display coupling and must be removed first:

- StrategyBuilder.Transitions -> DisplayRenderEngine.PlanBranchLines / TryProjectionAutomorphism
- StrategyBuilder.ProjectionQuotient -> DisplayRenderEngine.MergeProjectionOrbits
- StrategyBuilder.ProjectionPairingProbe -> DisplayRenderEngine.BuildProjectionComponents / RestrictOrderByDropMask / CloneDeactivated
- StrategyBuilder.RelabelingOrbit -> DisplayRenderEngine.RestrictOrderByDropMask / CloneDeactivated

Refactoring rule:

- Move projection/orbit/planning primitives into a neutral module (example name: ProjectionKernel).
- Keep display-specific formatting and rendering concerns in display classes.

---

## 4. API Naming Standard

### 4.1 Naming categories

- Run*: multi-stage orchestration/pipeline progression.
- Execute*: one stage execution.
- Project*: projection/mapping between models.
- Render*: text/UI presentation output.

### 4.2 Primary renames

- BuildStepProofStage -> ExecuteStepProofStage
- BuildEdgeCompactStage -> ExecuteEdgeCompactStage
- BuildGreedyFeasibleStage -> ExecuteGreedyFeasibleStage
- BuildProofTightenStage -> ExecuteProofTightenStage
- RunExactPipeline -> RunExactPipeline (unchanged)
- RunGreedyPipeline -> RunGreedyPipeline (unchanged)
- BuildSearchTree -> ProjectSearchTree
- BuildDisplayTreeAndExpandedSearch -> ProjectDisplayAndSearchTrees

### 4.3 Compatibility window

- Keep old public names as forwarding wrappers for one iteration only.
- Mark forwarding wrappers with clear deprecation comments.
- Remove wrappers at the end of the transition iteration.

---

## 5. StrategyBuilder Decomposition Plan (2A)

Phase strategy is mandatory: decompose into distinct classes while preserving behavior.

Phase 1 (this baseline cycle):

1. Keep a thin StrategyBuilder facade for external compatibility.
2. Extract independent classes for:
   - stage execution coordination,
   - projection/orbit kernel access,
   - progress estimation,
   - cache policy surfaces.
3. Rewire internal calls through extracted classes.
4. Ensure no new search->display dependency is introduced.

Phase 2 (follow-up cycle):

1. Shrink or remove broad partial-class split where practical.
2. Promote extracted classes as primary implementation units.
3. Remove temporary forwarding and obsolete names.

---

## 6. Delivery Sequence

Required order:

1. Boundary cleanup first (remove search->display dependency).
2. Introduce neutral kernel abstractions.
3. Add new API names and forwarding wrappers.
4. Migrate call sites (CLI/UI/tests/internal orchestration).
5. Remove legacy wrappers after one iteration.

Reason for order:

- Naming cleanup should not lock in bad dependency direction.
- Boundary correctness is the prerequisite for stable API semantics.

---

## 7. Checklists

### 7.1 Boundary checklist

- [ ] No StrategyBuilder file calls DisplayRenderEngine helpers for search semantics.
- [ ] Shared projection/orbit helpers live in neutral module, not display facade.
- [ ] Display layer consumes neutral helpers but does not own search semantics.

### 7.2 Naming checklist

- [ ] Execute*Stage names exist and are used by primary call paths.
- [ ] Run*Pipeline names remain orchestrator entrypoints.
- [ ] Project* names are used for cross-model projection entrypoints.
- [ ] Render* names remain display-only.

### 7.3 Transition checklist

- [ ] Forwarding wrappers exist with deprecation notice.
- [ ] All first-party call sites migrated to new names.
- [ ] Old names removed at transition close.

---

## 8. Verification Gates

Before removing wrappers or declaring completion:

1. Build passes.
2. Required test suite passes.
3. CLI and UI pipeline behavior remains equivalent.
4. No dependency regression (search->display remains forbidden).

---

## 9. Working Agreement

From this point forward:

- New work that conflicts with this baseline is blocked until aligned.
- Incremental PRs should reference this document and mark checklist progress.
- If a decision changes, update this file in the same PR as the code change.
