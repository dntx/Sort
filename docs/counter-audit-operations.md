# Counter Audit Operations

This page is the operational runbook for Mainline C deterministic counter governance.

It focuses on one review question: how do we run a full deterministic audit and understand exactly what changed?

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
