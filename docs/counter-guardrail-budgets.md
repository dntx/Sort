# Counter Guardrail Budgets

This document is the ratchet map for deterministic counter guardrails in `TopKFinder.Tests/StrategyRegressionTests.cs`.

Use this with:
- `scripts/run-counter-guardrails.ps1`
- `.github/workflows/manual-counter-guardrails.yml`

Runner behavior:
- prints selected method selectors for the chosen profile before execution.
- supports dry-run listing via `-ListOnly`.
- runs a preflight `--list-tests` count check; `full-counter-suite` enforces a minimum matched-test threshold to catch selector drift.
- `manual-counter-guardrails` workflow supports optional `collect_snapshots=true`, which runs `scripts/collect-all-counter-snapshots.ps1` and uploads combined summary + per-snapshot row artifacts.
- `manual-counter-guardrails` workflow supports `build_configuration` (`Release`/`Debug`) and passes it through to both guardrail tests and snapshot collectors.
- `manual-counter-guardrails` workflow supports `list_only=true`, which runs guardrail preflight/selector validation only and skips snapshot collection.
- `manual-counter-guardrails` workflow uploads `counter-guardrails-matched-tests` artifact for every run so selector/profile coverage can be audited directly.
- `manual-counter-full-audit` workflow runs the full deterministic audit bundle: `full-counter-suite`, matched-tests export + baseline diff, unified snapshots, and one combined summary report.

Snapshot utility:
- `scripts/collect-default-counter-snapshot.ps1` collects deterministic default-path counters for ratchet anchor shapes and emits JSON/CSV with cap deltas.
- `scripts/collect-compact-counter-snapshot.ps1` collects compact-path counters (including compact-specific work counters) via reflection and emits JSON/CSV with cap deltas.
- `scripts/collect-iterative-counter-snapshot.ps1` collects iterative-frontier counters and verifies structural anchors before reporting cap deltas.
- `scripts/collect-all-counter-snapshots.ps1` runs all three collectors and writes a combined summary (`counter-snapshot-summary.json/.md`).

## Profiles

| Profile | Primary test methods | Purpose |
| --- | --- | --- |
| `fast-default` | `Default_SearchedStateCountStaysWithinBaseline`, `Default_OutcomesConstructedStaysWithinBaseline`, `Default_CandidateGroupsEnumeratedStaysWithinBaseline`, `Default_DuplicateOutcomeSkipsStaysWithinBaseline` | Daily deterministic guard for default path work |
| `iterative-frontier` | `Default_IterativeDeepeningBaselineRemainsStable`, `Default_IterativeDeepening_BeatsExactPath` | ID gate/frontier stability and expected win over exact path |
| `compact` | `Compact_WorkCountersStayWithinBaseline`, `Compact_SearchedStateCountStaysWithinBaseline`, `Compact_OutcomesConstructedStaysWithinBaseline`, `Compact_DuplicateOutcomeSkipsStaysWithinBaseline` | Compact-phase deterministic work guardrails |
| `full-counter-suite` | `StaysWithinBaseline` + iterative frontier pair above | Full deterministic counter audit before major merges |

Preflight matched-test minimums (guarding against selector drift):

- `fast-default`: 60
- `iterative-frontier`: 6
- `compact`: 25
- `full-counter-suite`: 80

## Shape Coverage Anchors

These shape families are intentionally present in counter-cap tests and should remain represented when ratcheting:

- Default path heavy/default diversity:
  - `16,4,4`, `20,5,4`, `25,5,3`
  - mixed dual/edge cases like `8,4,2`, `10,3,6`, `6,2,2`
- Iterative frontier:
  - `(14|16|17|18),5,5`
  - `(12|14),6,6`
- Compact phase:
  - includes heavier compact rows such as `10,2,4`
  - tie/anomaly guard rows such as `8,4,2`, `10,3,5`, `13,4,3`

## Ratchet Protocol

1. Run the relevant profile before change and after change on the same machine/session.
2. If a deterministic counter decreases while behavior remains correct, ratchet the corresponding cap down.
3. If a deterministic counter increases:
   - treat as regression by default,
   - only keep the increase with an explicit documented trade-off in PR description.
4. For large algorithm changes, run `full-counter-suite` and record notable deltas in PR notes.

## Local Commands

```powershell
# Daily default deterministic guard
pwsh .\scripts\run-counter-guardrails.ps1 -Profile fast-default

# ID-focused changes
pwsh .\scripts\run-counter-guardrails.ps1 -Profile iterative-frontier

# Compact-focused changes
pwsh .\scripts\run-counter-guardrails.ps1 -Profile compact

# Pre-merge deterministic full audit
pwsh .\scripts\run-counter-guardrails.ps1 -Profile full-counter-suite

# Dry-run (show selectors only)
pwsh .\scripts\run-counter-guardrails.ps1 -Profile compact -ListOnly

# Dry-run + write matched test list for selector audit
pwsh .\scripts\run-counter-guardrails.ps1 -Profile compact -ListOnly -MatchedTestsPath .\artifacts\counter-guardrails-matched-tests.txt

# Full deterministic audit bundle
pwsh .\scripts\run-counter-full-audit.ps1 -Configuration Release

# Preflight-only audit bundle
pwsh .\scripts\run-counter-full-audit.ps1 -Configuration Release -ListOnly

# Collect default-path counter snapshot + ratchet opportunities
pwsh .\scripts\collect-default-counter-snapshot.ps1 -Configuration Release

# Collect compact-path counter snapshot + ratchet opportunities
pwsh .\scripts\collect-compact-counter-snapshot.ps1 -Configuration Release

# Collect iterative-frontier counter snapshot + ratchet opportunities
pwsh .\scripts\collect-iterative-counter-snapshot.ps1 -Configuration Release

# Collect all snapshots + combined summary
pwsh .\scripts\collect-all-counter-snapshots.ps1 -Configuration Release
```
