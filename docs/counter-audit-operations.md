# Counter Audit Operations

This page is the operational runbook for Mainline C deterministic counter governance.

It focuses on one review question: how do we run a full deterministic audit and understand exactly what changed?

Current Mainline C status:

- governance foundation is complete: focused manual lanes, bundled full audit, baseline drift approval, and nightly deterministic audit are all in place.
- follow-up work should usually be evidence-driven maintenance: either ratchet caps when real headroom appears or investigate nightly drift / delta signals.

Use this with:

- `scripts/run-counter-full-audit.ps1`
- `.github/workflows/manual-counter-full-audit.yml`
- `.github/workflows/nightly-counter-full-audit.yml`
- `docs/counter-guardrail-budgets.md`

## What The Full Audit Does

The full audit bundles four signals into one run:

1. Runs `full-counter-suite` through `scripts/run-counter-guardrails.ps1`.
2. Exports the exact matched test list for that selector set.
3. Diffs the matched test list against a repository baseline.
4. Runs unified default/compact/iterative snapshot collection and writes one combined summary.

Outputs land in one artifact directory, so reviewer triage does not require stitching together multiple manual lanes.

## Default Baseline

The repository baseline for matched test coverage is:

- `docs/counter-guardrails-full-counter-suite-baseline.txt`

This file is the expected `full-counter-suite` selector expansion at the time of baseline capture.

If audit output reports added or removed tests, treat that as selector drift until proven intentional.

## Local Commands

```powershell
# Full deterministic audit (tests + matched-test diff + snapshots)
pwsh .\scripts\run-counter-full-audit.ps1 -Configuration Release

# Preflight-only audit (selector coverage + matched-test diff only)
pwsh .\scripts\run-counter-full-audit.ps1 -Configuration Release -ListOnly

# Audit against a different matched-tests baseline file
pwsh .\scripts\run-counter-full-audit.ps1 -Configuration Release -MatchedTestsBaselinePath .\artifacts\candidate-baseline.txt
```

## Workflow Inputs

`manual-counter-full-audit` supports:

- `build_configuration`: `Release` or `Debug`
- `matched_tests_baseline_path`: repository path used for selector drift comparison
- `pull_request_number`: optional PR number for posting/updating an audit summary comment
- `list_only`: run guardrail preflight + matched-test diff only, skip test execution and snapshots

## Produced Files

The audit bundle writes:

- `counter-guardrails-summary.json`
- `counter-guardrails-matched-tests.txt`
- `counter-guardrails-matched-tests-diff.json`
- `counter-guardrails-matched-tests-diff.md`
- `counter-full-audit-summary.json`
- `counter-full-audit-summary.md`
- snapshot outputs under `snapshots/` when `list_only=false`
- unified snapshot summaries when `list_only=false`

In GitHub Actions, the full-audit workflow also appends the audit summary to the run summary page and can optionally update a PR comment when `pull_request_number` is provided.

## Baseline Drift Policy

If `docs/counter-guardrails-full-counter-suite-baseline.txt` changes in a PR, the PR body must include:

- `Counter baseline drift: <why the expected matched-test expansion changed>`

The `counter-baseline-drift-review` workflow enforces that explanation.

## Review Guidance

Start in this order:

1. Read `counter-full-audit-summary.md` for the single-run overview.
2. Check `counter-guardrails-matched-tests-diff.md` for selector drift.
3. If drift is zero, inspect snapshot summary for positive deltas.
4. If positive deltas exist, open the per-snapshot CSV/JSON rows and ratchet only when behavior remains correct.

## When To Use Which Lane

- Use `manual-counter-guardrails` for focused day-to-day profile checks.
- Use `manual-counter-full-audit` before major merges, after large algorithm changes, or when selector drift is suspected.
- Use `nightly-counter-full-audit` for unattended deterministic regression surveillance once manual audit coverage has stabilized.
- Use `manual-perf-gate` only for wall-clock smoke diagnostics; deterministic counters remain the primary regression signal.

## Maintainer Backlog (Current Open Items)

The governance foundation is complete. Remaining work is maintenance-oriented and evidence-driven.

### 1) Nightly deterministic audit observation (open)

What remains:

- Observe consecutive `nightly-counter-full-audit` runs on `main`.
- Confirm signal quality:
	- stable matched-tests drift behavior,
	- stable snapshot positive-delta behavior,
	- acceptable false-positive rate.
- If failures occur, classify first:
	- selector drift,
	- deterministic counter regression,
	- infrastructure/runtime flake.

Latest status note:

- Historical mismatch windows were reconciled by updating the full-counter-suite matched-tests baseline and aligning audit invocation to explicit `full-counter-suite` semantics.
- Keep this item open until a fresh post-reconciliation nightly window remains stable.

Exit criterion:

- A meaningful post-reconciliation run window is stable, and triage guidance has been validated by at least one real or simulated failure investigation.

### 2) Evidence-driven ratchet maintenance (open, ongoing)

What remains:

- Ratchet only when deterministic counters show real, repeatable headroom.
- Do not force cap changes when positive deltas remain zero.

Exit criterion:

- Ongoing maintenance stream; no single terminal milestone.

### 3) Optional unattended-output polish (optional)

Potential improvements:

- tighten nightly failure issue text,
- add concise triage checklist links,
- improve artifact naming/readability for faster incident response.

Exit criterion:

- Team accepts current output as sufficient, or adopts a revised template.

## Maintenance Resumption Order

When resuming maintenance work in a fresh session:

1. Check latest `nightly-counter-full-audit` outcomes first.
2. If no actionable signal exists, do not force code changes.
3. If actionable signal exists, fix the smallest root-cause slice.
4. Revisit deeper architecture changes only when they unblock concrete maintenance work.
