import importlib.util
import json
import os
from pathlib import Path
import subprocess
from unittest import mock


SCRIPT_PATH = Path(__file__).resolve().parents[1] / "ai_review.py"
SPEC = importlib.util.spec_from_file_location("ai_review", SCRIPT_PATH)
ai_review = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ai_review)


def test_detects_whitespace_only_csharp_changes_as_formatting_gate():
    diff = """diff --git a/StrategyBuilder.Compact.cs b/StrategyBuilder.Compact.cs
index 1111111..2222222 100644
--- a/StrategyBuilder.Compact.cs
+++ b/StrategyBuilder.Compact.cs
@@ -1,2 +1,3 @@
-    public void Foo()
+    public void Foo()
+
"""

    finding = ai_review.detect_unexplained_format_only_code_changes(diff, "Feature work", "Implement feature")

    assert finding is not None
    assert "Unexplained formatting-only code change" in finding
    assert "StrategyBuilder.Compact.cs" in finding


def test_detects_format_only_hunk_when_section_has_other_real_changes():
    diff = """diff --git a/StrategyBuilder.Compact.cs b/StrategyBuilder.Compact.cs
index 1111111..2222222 100644
--- a/StrategyBuilder.Compact.cs
+++ b/StrategyBuilder.Compact.cs
@@ -36,8 +36,9 @@ private int GetCompactGreedyCandidateCap(int activeCount, int groupSize)
 
     internal int GetCompactGreedyCandidateCapForTesting(int activeCount, int groupSize)
         => GetCompactGreedyCandidateCap(activeCount, groupSize);
-    
-    
+
+
+
     private int _compactGroupsEnumerated;
@@ -286,7 +287,7 @@ private int SolveCompact(ComparisonState state, int remainingSlots, int feasible
                 continue;
 
             // Only now pay for the heavy display enumeration that yields the exact edge count.
-            int edgeCount = CountDisplayBranches(state, remainingSlots, group);
+            int edgeCount = children.Count;
             int groupCost = edgeCount + branchCostSum;
             if (groupCost < bestCost)
"""

    finding = ai_review.detect_unexplained_format_only_code_changes(
        diff,
        "Refactor compact objective to search-tree edge count",
        "Functional change only",
    )

    assert finding is not None
    assert "Unexplained formatting-only code change" in finding
    assert "StrategyBuilder.Compact.cs" in finding
    assert "@@ -36,8 +36,9 @@" in finding


def test_combined_review_upgrades_blocking_bullets_to_block_verdict():
    review = """## Summary
Looks mostly fine.

_Verdict: **COMMENT**_

## Findings
- **[BLOCK]** This is a blocking issue.

VERDICT: COMMENT
"""

    combined = ai_review.combine_batch_reviews(
        [(1, 1, "COMMENT", review)],
        structural=None,
        policy_findings=None,
    )

    assert "## 🚫 Blocking (must fix)" in combined
    assert "VERDICT: BLOCK" in combined
    assert ai_review.parse_verdict(combined) == "BLOCK"


def test_load_pr_metadata_retries_transient_gh_api_failures():
    metadata_json = json.dumps(
        {
            "title": "Fix review infra",
            "body": "Retry metadata in script",
            "base": {"ref": "main", "sha": "base123"},
            "head": {"sha": "head456"},
        }
    )
    responses = [
        subprocess.CompletedProcess(
            ["gh", "api"],
            1,
            stdout="",
            stderr="HTTP 503: No server is currently available to service your request.",
        ),
        subprocess.CompletedProcess(["gh", "api"], 0, stdout=metadata_json, stderr=""),
    ]

    with mock.patch.dict(
        ai_review.os.environ,
        {"GITHUB_REPOSITORY": "dntx/Sort", "PR_NUMBER": "389"},
        clear=False,
    ):
        with mock.patch.object(ai_review.subprocess, "run", side_effect=responses) as run_mock:
            with mock.patch.object(ai_review.time, "sleep") as sleep_mock:
                metadata = ai_review.load_pr_metadata()

    assert metadata == {
        "title": "Fix review infra",
        "body": "Retry metadata in script",
        "base_ref": "main",
        "base_sha": "base123",
        "head_sha": "head456",
    }
    assert run_mock.call_count == 2
    sleep_mock.assert_called_once()


def test_ensure_diff_file_builds_diff_when_workflow_does_not_supply_one():
    responses = [
        subprocess.CompletedProcess(["git", "fetch"], 0, stdout="", stderr=""),
        subprocess.CompletedProcess(["git", "diff"], 0, stdout="diff --git a/a b/a\n", stderr=""),
    ]

    with mock.patch.dict(ai_review.os.environ, {}, clear=False):
        ai_review.os.environ.pop("DIFF_FILE", None)
        with mock.patch.object(ai_review.subprocess, "run", side_effect=responses) as run_mock:
            path = ai_review.ensure_diff_file("main", "base123", "head456")

        try:
            assert Path(path).exists()
            assert Path(path).read_text(encoding="utf-8") == "diff --git a/a b/a\n"
            assert ai_review.os.environ["DIFF_FILE"] == path
            assert run_mock.call_count == 2
        finally:
            if os.path.exists(path):
                os.remove(path)
