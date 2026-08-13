# Test gates

Long-running tests and manual diagnostics must not silently pass in required PR CI.

## Categories

- **Required PR**: deterministic correctness and differential tests. These have no `RUN_*` gate and run on every code PR.
- **Nightly**: long-running regression or performance gates marked with `Trait("Category", "Nightly")` and selected by a scheduled or manual workflow.
- **Manual diagnostic**: report or dump generators marked with `Trait("Category", "Manual")`. They require an explicit command and are not correctness gates.

Required PR CI runs `TopKFinder.PerfTests` with `Category!=Nightly&Category!=Manual`.

## Test hooks

Production-default behavior is always enabled. Differential tests may disable an optimization through `StrategyBuilder.TestHooks` to compare the optimized path with its baseline. A hook is acceptable only while an always-run PR test exercises both values.

Product settings must not be added to `TestHooks`, and test hooks must not be read from environment variables or application configuration.

## Environment gates

Every `RUN_*` variable read by test code must be registered in `tests/test-gates.json` as either:

- `nightly`, with at least one workflow that activates it; or
- `manual-diagnostic`, with an explicit invocation command.

`scripts/check-test-gate-registry.ps1` rejects unregistered and stale gates. Required PR CI runs this audit whenever code changes.

New correctness or performance regression tests should prefer explicit category selection over an environment-variable early return. Environment gates remain only for legacy scenario parameterization and manual diagnostics until those tools move to dedicated runners.
