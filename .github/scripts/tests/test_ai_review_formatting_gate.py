import importlib.util
from pathlib import Path


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
