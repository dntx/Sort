# Maintainers Open Items (Authoritative)

This document is the single source of truth for:

- unfinished work items,
- pending clarification/decision points,
- and the expected update workflow for future sessions.

If information in chat history and this file diverges, follow this file.

---

## 1. Context And Scope

This repository has gone through a large refactor/governance program with three major tracks:

- Mainline A: search-first boundary closure
- Mainline B: test layering governance
- Mainline C: deterministic performance baseline governance

The original roadmap file `docs/refactor-roadmap.md` has been intentionally removed.
Its useful status notes were folded into long-lived docs:

- `docs/core-algorithm.md`
- `docs/architecture-class-relations-and-api-naming-baseline.md`
- `docs/test-strategy.md`
- `docs/counter-audit-operations.md`

This file now tracks only what is still open or needs explicit owner decisions.

---

## 2. Current Baseline (As Of 2026-07-18)

### 2.1 Governance/tooling state

Implemented and merged:

- Manual deterministic guardrails lane (`manual-counter-guardrails`)
- Manual bundled full deterministic audit (`manual-counter-full-audit`)
- Baseline drift approval check (`counter-baseline-drift-review`)
- Nightly unattended deterministic full audit (`nightly-counter-full-audit`)
- Manual perf smoke lane (`manual-perf-gate`) kept as diagnostics-only

### 2.2 Counter coverage state

Deterministic snapshot-to-theory coverage has been substantially closed for:

- default counters,
- compact counters,
- iterative-frontier counters.

Recent work closed remaining snapshot-only rows in default/compact coverage.

### 2.3 Intentional maintenance posture

Mainline C is in maintenance mode:

- no mandatory large refactor remains,
- future changes should be evidence-driven (nightly signal -> targeted change).

---

## 3. Unfinished Work Items

The items below are not blockers, but are the current open backlog.

### 3.1 Observe and operate nightly deterministic audit

Status: Open (operational)

What remains:

- Observe `nightly-counter-full-audit` for several consecutive runs.
- Confirm signal quality:
  - stable matched-tests drift behavior,
  - stable snapshot positive-delta behavior,
  - acceptable false-positive rate.
- If failures occur, triage by category:
  - selector drift,
  - deterministic counter regression,
  - infra flake/runtime failure.

Exit criterion:

- Nightly lane demonstrates stable behavior over a meaningful run window, and handling guidance is validated by at least one real or simulated failure investigation.

### 3.2 Evidence-driven ratchet maintenance

Status: Open (as-needed)

What remains:

- Only ratchet when real headroom appears in deterministic counters from current baselines.
- Do not force cap changes when positive deltas are zero.

Exit criterion:

- N/A (ongoing maintenance stream).

### 3.3 Optional output polish for unattended runs

Status: Optional

Potential improvements:

- tighten/shorten nightly issue comment templates,
- add concise triage checklist links in failure issues,
- improve artifact naming/readability for faster incident response.

Exit criterion:

- Team agrees current output is sufficient or adopts a revised template.

---

## 4. Pending Clarifications / Decisions

These are architecture-level questions intentionally left open.

### 4.1 Search model purity vs pragmatic shared-reference model

Status: Resolved (2026-07-18)

Decision:

- Do NOT force a fully expanded explicit search tree.
- Keep the reference-capable/shared-subtree model as intentional design.
- Treat SearchStrategy as conceptually implicit (state -> best-group function via caches),
  while allowing explicit nodes where needed for projection/testing tooling.

Rationale and context:

- Fully expanding repeated subtrees turns a DAG-like structure into a pure tree and can cause large blow-ups.
- Example class: if `s3` transitions to the same subtree as `s2`, no-reuse expansion duplicates an entire subtree of size `M`.
- On realistic top-k shapes this can amplify memory/time significantly (often multi-x, potentially order-of-magnitude scale),
  so full expansion is not an acceptable default representation.

Operational interpretation:

- Search phase should remain cache-driven first (implicit strategy semantics).
- Display/materialization phase may construct explicit trees, but must continue using display-key-based sharing/reference semantics
  to avoid unnecessary expansion.
- This is a deliberate engineering trade-off, not unfinished work.

### 4.2 Display projection contract strictness

Status: Resolved (2026-07-18)

Decision:

- Keep the current facade + parity-tests approach as the default.
- Do NOT force a large formal display-mapping-object refactor at this stage.
- Allow incremental contract tightening only when it clearly improves readability/maintainability
  or closes a real verification gap discovered in practice.

Rationale and context:

- The governing principle is: algorithm/search must remain logically rigorous and maintainable,
  while display/result trees should optimize for human usability and inspectability.
- Current code already has a workable split with parity tests guarding behavior.
- A mandatory large projection-contract rewrite now would add complexity/cost without a proven
  operational pain point.

Operational interpretation:

- Prefer small, local refactors that make naming/layer boundaries clearer.
- Preserve and strengthen parity tests as the primary safety net.
- Introduce stricter explicit projection metadata only when a concrete debugging or review
  workflow requires it.

### 4.3 Nightly policy breadth

Status: Resolved (2026-07-18)

Decision:

- Keep nightly deterministic audit policy at current scope for now.
- Expand nightly breadth only if observed evidence shows blind spots (missed regressions) or
  poor triage quality.

Rationale and context:

- The governing principle is clarity and maintainability over framework expansion.
- Unnecessary nightly expansion increases CI cost/noise and can reduce human usability of signals.
- Current nightly lane already provides unattended drift/delta surveillance; next changes should be
  evidence-driven, not preemptive.

Operational interpretation:

- First optimize signal quality and triage ergonomics in the existing nightly lane.
- Add more nightly lanes/escalation rules only with a concrete failure mode and success criterion.

---

## 5. Recommended Decision Order

When resuming in a new session, use this order:

1. Check latest `nightly-counter-full-audit` outcomes first.
2. If no actionable signal: do not force code changes.
3. If there is actionable signal: fix the smallest root-cause slice.
4. Revisit architecture clarifications (4.1 / 4.2) only if they block concrete maintenance work.

---

## 6. Operational Commands (Quick Start)

Typical commands used in maintenance sessions:

```powershell
# sync
# (assumes origin/main exists)
git checkout main
git pull --ff-only

# deterministic full audit (local)
pwsh .\scripts\run-counter-full-audit.ps1 -Configuration Release

# preflight-only audit (local)
pwsh .\scripts\run-counter-full-audit.ps1 -Configuration Release -ListOnly

# focused snapshot scans
pwsh .\scripts\collect-default-counter-snapshot.ps1 -Configuration Release
pwsh .\scripts\collect-compact-counter-snapshot.ps1 -Configuration Release
pwsh .\scripts\collect-iterative-counter-snapshot.ps1 -Configuration Release

# unified snapshot summary
pwsh .\scripts\collect-all-counter-snapshots.ps1 -Configuration Release
```

---

## 7. Update Rules For This File

Mandatory rules:

- Update this file in the same PR whenever:
  - a listed unfinished item changes status,
  - a pending decision is resolved,
  - or new cross-session context is introduced.
- Keep entries factual and short.
- Do not duplicate deep implementation details that already live in:
  - `docs/core-algorithm.md`
  - `docs/test-strategy.md`
  - `docs/counter-audit-operations.md`

Change log convention:

- Add a one-line dated note under the relevant section when status changes.
- Prefer appending over rewriting history unless information is wrong.

Recent decision log:

- 2026-07-18: Section 4.1 resolved in favor of reference-capable/implicit search strategy semantics; fully expanded explicit trees are not the target default due to DAG-to-tree blow-up risk.
- 2026-07-18: Section 4.2 resolved: keep facade + parity-tests as default projection contract strategy; tighten incrementally only when concrete pain/verification gaps appear.
- 2026-07-18: Section 4.3 resolved: keep current nightly deterministic audit scope; expand only on evidence.

---

## 8. Ownership Note

If ownership is unclear in a future session, treat this as repo-maintainer-owned backlog.
Any contributor proposing changes should:

1. reference this file in their PR description,
2. state which item(s) they are addressing,
3. and update status here before merge.
