# Counter Guardrail Budgets

This document is the ratchet map for deterministic counter guardrails in `TopKFinder.Tests/StrategyRegressionTests.cs`.

Use this with:
- `scripts/run-counter-guardrails.ps1`
- `.github/workflows/manual-counter-guardrails.yml`

## Profiles

| Profile | Primary test methods | Purpose |
| --- | --- | --- |
| `fast-default` | `Default_SearchedStateCountStaysWithinBaseline`, `Default_OutcomesConstructedStaysWithinBaseline`, `Default_CandidateGroupsEnumeratedStaysWithinBaseline`, `Default_DuplicateOutcomeSkipsStaysWithinBaseline` | Daily deterministic guard for default path work |
| `iterative-frontier` | `Default_IterativeDeepeningBaselineRemainsStable`, `Default_IterativeDeepening_BeatsExactPath` | ID gate/frontier stability and expected win over exact path |
| `compact` | `Compact_WorkCountersStayWithinBaseline`, `Compact_SearchedStateCountStaysWithinBaseline`, `Compact_OutcomesConstructedStaysWithinBaseline`, `Compact_DuplicateOutcomeSkipsStaysWithinBaseline` | Compact-phase deterministic work guardrails |
| `full-counter-suite` | `*StaysWithinBaseline` + iterative frontier pair above | Full deterministic counter audit before major merges |

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
```
