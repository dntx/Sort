import importlib.util
from pathlib import Path


SCRIPT_PATH = Path(__file__).resolve().parents[1] / "ai_review.py"
SPEC = importlib.util.spec_from_file_location("ai_review", SCRIPT_PATH)
ai_review = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ai_review)


def test_explicit_why_no_test_section_accepts_reason_and_evidence() -> None:
    pr_body = """## Summary
- split projection/materialization helpers into focused files

## Why no test
- Reason: this PR is a behavior-preserving structural refactor. It only relocates existing StrategyBuilder logic without changing algorithmic selection or user-visible behavior.
- Evidence: the moved call paths are already guarded by ExactPipelineTests, GreedyPipelineTests, and DisplaySearchParityTests. Local verification also includes dotnet build and passing focused test runs after the split.
"""

    assert ai_review._has_reasonable_no_test_explanation(pr_body) is True


def test_combine_batch_reviews_drops_self_negating_structural_block() -> None:
    structural_review = """## Structural Review
The PR description matches the change set.

## Findings
- **[BLOCK]** UNEXPLAINED NEW SOURCE FILE: None. All new source files (`PipelineStageProtocol.cs`, `StrategyBuilder.HelperTypes.cs`) are explicitly explained in the PR description.

VERDICT: BLOCK
"""

    review = ai_review.combine_batch_reviews(
        [(1, 1, "APPROVE", "## Summary\nLooks good.\n\n## Findings\nNo issues found.\n\nVERDICT: APPROVE")],
        structural=("BLOCK", structural_review),
        policy_findings=[],
    )

    assert "UNEXPLAINED NEW SOURCE FILE: None." not in review
    assert "VERDICT: APPROVE" in review


def test_combine_batch_reviews_drops_plural_and_missing_docs_none_blocks() -> None:
    structural_review = """## Structural Review
The PR description matches the change set.

## Findings
- **[BLOCK]** MISSING DOCS**: None. The PR does not introduce or modify user-facing behavior, so no documentation updates are required.
- **[BLOCK]** UNEXPLAINED NEW SOURCE FILES**: None. The new files are well-named and their purposes are clear from the description and the diff.

VERDICT: BLOCK
"""

    review = ai_review.combine_batch_reviews(
        [(1, 1, "APPROVE", "## Summary\nLooks good.\n\n## Findings\nNo issues found.\n\nVERDICT: APPROVE")],
        structural=("BLOCK", structural_review),
        policy_findings=[],
    )

    assert "MISSING DOCS**: None." not in review
    assert "UNEXPLAINED NEW SOURCE FILES**: None." not in review
    assert "VERDICT: APPROVE" in review