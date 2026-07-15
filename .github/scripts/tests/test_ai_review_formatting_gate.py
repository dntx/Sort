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
