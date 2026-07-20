#!/usr/bin/env python3
"""AI-powered pull request reviewer backed by GitHub Models.

Reads a unified diff, asks a GitHub Models chat model to review it, posts the
review back onto the pull request, and exits non-zero when the model reports a
blocking (serious) issue so the required status check fails. Blocking findings
are held to a high bar to keep false positives low.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import unicodedata

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

MODELS_ENDPOINT = "https://models.github.ai/inference/chat/completions"


def _resolve_model_chain() -> list[str]:
    """Ordered list of models to try; each GitHub Models model has its own quota.

    On HTTP 429 the reviewer falls back to the next model in the chain, so the
    effective daily budget is the sum of every model's quota. Low rate-limit
    tier models (150 requests/day) are tried before high tier (50/day).
    Configure with REVIEW_MODELS (comma-separated) or a single REVIEW_MODEL.
    """
    raw = os.environ.get("REVIEW_MODELS", "").strip()
    if raw:
        chain = [m.strip() for m in raw.split(",") if m.strip()]
    else:
        single = os.environ.get("REVIEW_MODEL", "").strip()
        chain = [single] if single else []
    default_chain = [
        "openai/gpt-4.1",
        "openai/gpt-4o",
        "openai/gpt-4.1-mini",
        "openai/gpt-4o-mini",
    ]
    for model in default_chain:
        if model not in chain:
            chain.append(model)
    return chain


MODEL_CHAIN = _resolve_model_chain()
# Index of the model currently in use; advances as models get rate limited so
# batches after the first do not re-hit a model already known to be exhausted.
_active_model_index = 0
MAX_DIFF_CHARS = 60000
# Group diff sections into larger batches to minimise the number of model
# requests per review (fewer requests = less rate-limit pressure and faster
# runs). Kept comfortably under the endpoint payload limit that rejects very
# large single requests with HTTP 413.
MAX_BATCH_CHARS = 12000
# The structural pass sends the manifest plus a slice of the combined diff in a
# single request; keep that slice comfortably under the model endpoint's payload
# and per-request token limit (large single requests return HTTP 413). Notable
# new identifiers are extracted from the WHOLE diff separately, so naming checks
# still work even when this slice is truncated.
STRUCTURAL_DIFF_CHARS = 14000
# Transient rate limiting (HTTP 429) is retried with capped exponential backoff.
# The per-attempt delay is capped so a large server-provided Retry-After (e.g. a
# quota-window reset that can be many minutes) never stalls the whole review.
MODEL_MAX_RETRIES = 3
MODEL_RETRY_BASE_SECONDS = 4
MODEL_RETRY_MAX_SECONDS = 20
PR_METADATA_MAX_RETRIES = 3
PR_METADATA_RETRY_BASE_SECONDS = 2
PR_METADATA_RETRY_MAX_SECONDS = 10
REVIEW_PUBLISH_MAX_RETRIES = 3
REVIEW_PUBLISH_RETRY_BASE_SECONDS = 2
REVIEW_PUBLISH_RETRY_MAX_SECONDS = 10
EXCLUDED_REVIEW_PATHS = {".github/scripts/ai_review.py", ".github/workflows/ai-code-review.yml"}

SYSTEM_PROMPT = """\
You are a meticulous senior software engineer reviewing a GitHub pull request
for a C#/.NET 8 project. You are given the unified diff plus the PR title and
description. Review only what is shown and do not guess about code you cannot
see.

## Prime directive: precision over recall
A false BLOCK is far more costly than a missed nit. Only raise a BLOCK when you
can point to the exact changed lines that prove the defect. If a concern depends
on code you cannot see or on an assumption you cannot verify from the provided
files, DO NOT raise it as a BLOCK — at most mention it as a COMMENT or omit it.

## Hard anti-hallucination rules
- If a symbol/helper/member/variable is not literally present in the provided
  diff or in the file content you are reviewing, you may not BLOCK on it.
- Do not reuse identifiers from a different batch or an earlier review.
- If you cannot quote the exact changed line(s) that prove the failure path, it
  is not a BLOCK.
- Phrases like "could", "may", "might", "please confirm", "not shown", or
  "potential issue" are COMMENT-level language at most.
- For any BLOCK, you must be able to explain: (1) the exact changed line(s),
  (2) the exact runtime path from those lines to failure, and (3) why the failure
  is guaranteed or highly certain.

## Before writing any BLOCK finding, self-check ALL of these. If any fails, downgrade to COMMENT or drop it:
1. Verify, don't assume. Confirm the claim against the shown file content.
   If a field/variable/helper you are worried about is initialized or handled
   elsewhere in the file, the concern is void.
2. Respect the language & runtime facts:
   - C# value types (`Color`, `int`, `Rectangle`, `Size`, `DateTime`, enums)
     are NEVER null. Do not claim NullReferenceException on them.
   - `using var` / `using` already disposes the object.
   - WinForms UI event handlers (paint/draw, click, form events) run on the
     single UI thread. Do NOT invent multi-threaded race conditions or
     concurrent-dictionary problems unless the diff clearly introduces a thread,
     Task, timer callback, or async continuation touching shared state.
   - `int.MaxValue` as a width in `TextRenderer.DrawText`/measure calls is an
     idiomatic "don't wrap/clip" sentinel, not a bug.
   - A field initialized inline (e.g. `private X _f = new(...)`) is never null.
   - Shared/cached brushes/pens/fonts reused across paints are intentional and
     correct; only flag GDI leaks when an object is allocated per-call and never
     disposed.
3. Real, not hypothetical. "Could lead to…", "may become a bottleneck",
   "consider using ReadOnlySpan for fewer allocations" are NOT blocking.
   Micro-performance and style preferences are never BLOCK, and usually not
   worth a COMMENT either.
4. In scope. Only judge what this diff changes. Pre-existing patterns the PR
   didn't touch are out of scope.
5. Repo-specific anti-false-positive rules:
   - Do not BLOCK on `[STAThread]` spacing or indentation.
   - Do not BLOCK on `CancellationTokenSource` ownership when the code uses
     `using var` or an equivalent scoped ownership pattern.
   - Do not BLOCK on `NormalizeState(...)` making `remainingSlots` negative when
     the shown code is a normalization loop guarded by `remainingSlots > 0`.
   - Do not BLOCK on `Children.Count` / `children.Count` nullability unless the
     code itself proves the collection can be null at that point.
   - Do not BLOCK on elapsed-time / benchmark / stopwatch divisions unless the
     divisor is proven reachable as zero in the shown correctness path. In test
     or benchmark code, this is generally COMMENT-level only.

## Severities
- BLOCK: a concrete, verifiable defect introduced by this change that will
  cause wrong behavior, a crash, data loss, or a security hole.
- COMMENT: legitimate, non-blocking suggestions or genuine edge cases worth a
  second look.
- APPROVE: no meaningful issues.

Respond in GitHub-flavored Markdown with these sections:
## Summary
A short paragraph describing what the change does.

## Findings
A bulleted list. Prefix each with its severity in bold, e.g. **[BLOCK]**,
**[COMMENT]**. For every BLOCK, quote or cite the specific line(s) that prove
it. If there are none, write "No issues found."

At the very END of your response, output a final line by itself in exactly this
format (no extra text):
VERDICT: <BLOCK|COMMENT|APPROVE>
"""

STRUCTURAL_SYSTEM_PROMPT = """\
You are a senior software engineer performing a PR-LEVEL consistency and hygiene
review of a GitHub pull request for a C#/.NET 8 project. Unlike a line-by-line
code review, your job is to judge the change set AS A WHOLE against the PR's
stated intent. You are given: the PR title and description, a manifest of every
changed file (path, change status, category, and added/removed line counts), a
list of notable NEW identifiers/literals extracted from the added lines of the
WHOLE diff (CLI flags, mode/enum-like string values), and the combined diff
(which may be truncated).

Focus ONLY on the following four structural concerns. Do not repeat line-level
correctness/security findings — a separate reviewer handles those. Do NOT judge
test coverage at all: whether a change needs tests is decided by a separate,
deterministic automated check, so NEVER raise a "missing tests" finding here
(not even as a COMMENT).

1. DESCRIPTION ↔ CHANGE CONSISTENCY
   Does the actual diff match what the PR title/description claims? Flag when the
   description promises something the diff does not do, the diff does something
   material the description never mentions, or the description is empty/generic
   while the change is non-trivial.

2. UNEXPLAINED NEW SOURCE FILES
   For every `added` SOURCE/code file, is its purpose explained by the PR
   description (or made obvious by an accompanying test/doc)? Flag brand-new
   code files whose role is unclear and that the description does not mention.
   (Loose data/dump files and stray `.txt`/`.md` files are handled by a separate
   deterministic gate — do NOT flag those here.)

3. NAMING CONSISTENCY OF NEW PUBLIC IDENTIFIERS
   When the diff introduces a new user-facing value that belongs to an existing
   set — a CLI mode/flag value, enum member, command name, or option — verify it
   follows the SAME naming convention as the existing siblings visible in the
   diff or description (casing, hyphenation, single vs multi word). Example: if
   the existing modes are `exact` and `greedy` (single lowercase word) and the PR
   adds `three-phase` (hyphenated), that is an inconsistency worth flagging.
   Inspect the "Notable new identifiers/literals" list as well as the diff — the
   diff may be truncated, so that list is your most reliable source for new
   flags/mode values that appear later in the change set.

4. MISSING DOCUMENTATION UPDATES
   If the PR adds or changes user-facing behavior (a new CLI mode/flag, new
   command, changed output/interface) but the manifest shows NO doc files
   changed (README/docs/*.md), flag the missing documentation. Use the
   manifest's "Doc files changed" fact — do not guess.

## Precision rules (avoid false positives)
- Judge only from the provided manifest, diff, title, and description. Never
  assume files exist that are not listed.
- The change-file manifest and its code/test/doc classification are
  AUTHORITATIVE ground truth. Do NOT question, second-guess, or suggest
  "correcting" the manifest, and do NOT comment on how a file was classified
  (e.g. a root-level experiment file counted as code rather than a test is
  intentional). Simply use the manifest's facts.
- Test coverage is OUT OF SCOPE for this pass (a separate deterministic check
  owns it): never flag missing/insufficient tests here.
- If the manifest already shows a doc/README/*.md file changed, DO NOT flag
  missing docs.
- Pure refactors, pure formatting, pure dependency bumps, and comment-only
  changes need no doc updates — do not flag them.
- Mechanical file splits/extractions (including partial-class splits or method
  relocation into focused files) are pure refactors when behavior is preserved.
- Changes that ONLY touch test files or ONLY touch docs need no extra docs.
- Naming: only flag a NEW identifier that clearly joins an EXISTING set with a
  visible, established convention. Do not invent conventions or bikeshed style
  where no sibling pattern is shown.
- New files: a new test file, or an obviously-named code file that clearly
  matches the described feature/change, is self-explanatory — do not flag it.
- Do not flag the AI review infrastructure itself.

## Severities
Each of the four structural concerns above is a BLOCK-worthy hygiene gate: the
change set must not merge while any of them is genuinely violated. Raise a
finding as BLOCK when you can concretely demonstrate the violation from the
manifest, diff, title, or description:
- DESCRIPTION ↔ CHANGE mismatch: the diff does something material the
  description omits or contradicts, or the description is empty/generic for a
  non-trivial change. → BLOCK
- UNEXPLAINED NEW SOURCE FILE: an `added` code file whose purpose the
  description does not explain and that no accompanying test/doc makes obvious.
  → BLOCK
- NAMING INCONSISTENCY: a new user-facing identifier that breaks the
  established convention of its existing siblings (e.g. `three-phase` joining
  `exact`/`greedy`). → BLOCK
- MISSING DOCS: user-facing behavior added or changed but the manifest shows NO
  doc files changed. → BLOCK
- COMMENT: reserve for genuinely optional polish that does NOT fall into any of
  the four gates above.
- APPROVE: the change set is internally consistent and adequately covered — no
  gate is violated.

Only downgrade a gate from BLOCK to COMMENT (or drop it) when the precision
rules above void it — for example the manifest already shows a doc file changed,
or the change is a pure refactor/formatting/comment-only edit. When a gate
genuinely applies, it is a BLOCK, not advisory.

Respond in GitHub-flavored Markdown with these sections:
## Structural Review
A short paragraph on how well the change set matches its stated intent.

## Findings
A bulleted list. Prefix each with its severity in bold, e.g. **[BLOCK]** or
**[COMMENT]**. For every finding, name the specific file(s) or identifier
involved. If there are none, write "No issues found."

At the very END of your response, output a final line by itself in exactly this
format (no extra text):
VERDICT: <BLOCK|COMMENT|APPROVE>
"""


def read_diff() -> str:
    path = os.environ["DIFF_FILE"]
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        diff = fh.read()
    if len(diff) > MAX_DIFF_CHARS:
        diff = (
            diff[:MAX_DIFF_CHARS]
            + "\n\n[diff truncated for review — only the first part is shown]\n"
        )
    return diff


def _is_transient_gh_api_failure(stderr: str, stdout: str) -> bool:
    text = f"{stderr}\n{stdout}".lower()
    return (
        "http 5" in text
        or "no server is currently available" in text
        or "timed out" in text
        or "timeout" in text
    )


def _run_gh_api_with_retry(args: list[str], *, description: str) -> str:
    last_error = ""
    for attempt in range(1, PR_METADATA_MAX_RETRIES + 1):
        proc = subprocess.run(
            ["gh", "api", *args],
            text=True,
            capture_output=True,
        )
        if proc.returncode == 0:
            return proc.stdout

        stderr = proc.stderr.strip()
        stdout = proc.stdout.strip()
        last_error = stderr or stdout or f"gh api exited {proc.returncode}"
        is_transient = _is_transient_gh_api_failure(stderr, stdout)
        if not is_transient or attempt == PR_METADATA_MAX_RETRIES:
            break

        delay = min(
            PR_METADATA_RETRY_BASE_SECONDS * (2 ** (attempt - 1)),
            PR_METADATA_RETRY_MAX_SECONDS,
        )
        print(
            f"Transient GitHub API failure during {description} "
            f"(attempt {attempt}/{PR_METADATA_MAX_RETRIES}): {last_error}. "
            f"Retrying in {delay}s..."
        )
        time.sleep(delay)

    raise RuntimeError(f"Could not {description}: {last_error}")


def load_pr_metadata() -> dict[str, str]:
    """Load the PR title/body/base/head directly from GitHub with retries."""
    repo = (os.environ.get("GITHUB_REPOSITORY") or "").strip()
    pr_number = (os.environ.get("PR_NUMBER") or "").strip()
    if not repo:
        raise RuntimeError("GITHUB_REPOSITORY is required.")
    if not pr_number:
        raise RuntimeError("PR_NUMBER is required.")

    payload = _run_gh_api_with_retry(
        [f"repos/{repo}/pulls/{pr_number}"],
        description=f"load PR metadata for {repo}#{pr_number}",
    )
    try:
        data = json.loads(payload or "{}")
    except json.JSONDecodeError as err:
        raise RuntimeError(f"Could not parse PR metadata JSON: {err}") from err

    metadata = {
        "title": (data.get("title") or ""),
        "body": (data.get("body") or ""),
        "base_ref": (((data.get("base") or {}).get("ref")) or ""),
        "base_sha": (((data.get("base") or {}).get("sha")) or ""),
        "head_sha": (((data.get("head") or {}).get("sha")) or ""),
    }
    if not metadata["base_ref"] or not metadata["base_sha"] or not metadata["head_sha"]:
        raise RuntimeError("PR metadata response did not include base/head refs.")
    return metadata


def sync_pr_metadata_env(metadata: dict[str, str]) -> None:
    """Publish loaded PR metadata to env for downstream prompt builders."""
    os.environ["PR_TITLE"] = metadata.get("title", "")
    os.environ["PR_BODY"] = metadata.get("body", "")
    os.environ["PR_BASE_REF"] = metadata.get("base_ref", "")
    os.environ["PR_HEAD_SHA"] = metadata.get("head_sha", "")


def ensure_diff_file(base_ref: str, base_sha: str, head_sha: str) -> str:
    """Build the review diff locally when the workflow did not provide one."""
    existing = (os.environ.get("DIFF_FILE") or "").strip()
    if existing and os.path.exists(existing):
        return existing

    fetch_proc = subprocess.run(
        ["git", "fetch", "--no-tags", "origin", base_ref, base_sha, head_sha],
        text=True,
        capture_output=True,
    )
    if fetch_proc.returncode != 0:
        raise RuntimeError(
            "Could not fetch PR base/head refs for diff construction: "
            f"{fetch_proc.stderr.strip() or fetch_proc.stdout.strip()}"
        )

    diff_proc = subprocess.run(
        ["git", "diff", f"{base_sha}...{head_sha}"],
        text=True,
        capture_output=True,
    )
    if diff_proc.returncode != 0:
        raise RuntimeError(
            "Could not build PR diff: "
            f"{diff_proc.stderr.strip() or diff_proc.stdout.strip()}"
        )

    fd, path = tempfile.mkstemp(prefix="ai-review-", suffix=".diff")
    os.close(fd)
    with open(path, "w", encoding="utf-8", errors="replace") as fh:
        fh.write(diff_proc.stdout)
    os.environ["DIFF_FILE"] = path
    return path


def split_diff_sections(diff: str) -> list[str]:
    """Split a unified diff into per-file sections."""
    sections: list[str] = []
    current: list[str] = []
    for line in diff.splitlines(keepends=True):
        if line.startswith("diff --git ") and current:
            sections.append("".join(current))
            current = [line]
            continue
        current.append(line)
    if current:
        sections.append("".join(current))
    return sections


def section_path(section: str) -> str:
    """Extract the b-side path from a diff section."""
    for line in section.splitlines():
        if line.startswith("diff --git "):
            parts = line.split(" b/", 1)
            if len(parts) == 2:
                return parts[1].strip()
    return ""


def chunk_lines(text: str, max_chars: int) -> list[str]:
    """Split text into roughly size-bounded chunks without losing line breaks."""
    chunks: list[str] = []
    current: list[str] = []
    current_len = 0
    for line in text.splitlines(keepends=True):
        if current and current_len + len(line) > max_chars:
            chunks.append("".join(current))
            current = [line]
            current_len = len(line)
        else:
            current.append(line)
            current_len += len(line)
    if current:
        chunks.append("".join(current))
    return chunks


def build_diff_batches(diff: str) -> list[str]:
    """Group diff sections into batches that fit comfortably under the model limit."""
    sections = [
        section for section in split_diff_sections(diff)
        if is_line_reviewable(section_path(section))
    ]
    batches: list[str] = []
    current: list[str] = []
    current_len = 0

    def flush_current() -> None:
        nonlocal current, current_len
        if current:
            batches.append("".join(current))
            current = []
            current_len = 0

    for section in sections:
        if len(section) > MAX_BATCH_CHARS:
            flush_current()
            batches.extend(chunk_lines(section, MAX_BATCH_CHARS))
            continue

        if current and current_len + len(section) > MAX_BATCH_CHARS:
            flush_current()

        current.append(section)
        current_len += len(section)

    flush_current()
    return batches


def read_raw_diff() -> str:
    """Read the full, untruncated diff for file-level (manifest) analysis."""
    path = os.environ["DIFF_FILE"]
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        return fh.read()


DOC_EXTENSIONS = {"md", "rst", "txt", "adoc"}


def classify_path(path: str) -> str:
    """Classify a changed file as 'doc', 'test', or 'code'."""
    lower = path.lower()
    segments = lower.split("/")
    base = segments[-1]
    name, _, ext = base.rpartition(".")
    if not name:  # no extension
        name, ext = base, ""

    if ext in DOC_EXTENSIONS or "docs" in segments[:-1] or "readme" in name:
        return "doc"

    is_test = (
        any("test" in seg or "spec" in seg for seg in segments[:-1])
        or name.endswith(("test", "tests", "spec", "specs"))
        or name.startswith(("test_", "test-"))
        or ".test" in base
        or ".spec" in base
    )
    if is_test:
        return "test"
    return "code"


def is_line_reviewable(path: str) -> bool:
    """Whether a changed file should get the detailed LINE-LEVEL code review.

    The line-level pass (and the new-identifier extraction that feeds the
    structural pass) judge C#/code defects, so they only look at code and test
    files. Documentation and free-form data/text files (`.md`, `.txt`, dumps,
    logs) carry no code to review; deep-analyzing a large one (e.g. a multi-
    thousand-line output dump accidentally committed) only wastes model budget
    and manufactures false positives (phantom CLI flags, bogus
    NullReference/"diff truncated" comments).

    Such files are STILL listed in the change manifest, so the structural pass
    can flag an unexplained/irrelevant addition and BLOCK on it. In other words:
    once a mysterious file is spotted we block on its mere presence rather than
    burning requests dissecting its contents.
    """
    if path in EXCLUDED_REVIEW_PATHS:
        return False
    return classify_path(path) != "doc"


def section_status(section: str) -> str:
    """Determine the change status of a diff section."""
    for line in section.splitlines():
        if line.startswith("new file mode"):
            return "added"
        if line.startswith("deleted file mode"):
            return "deleted"
        if line.startswith("rename from") or line.startswith("rename to"):
            return "renamed"
    return "modified"


def section_line_counts(section: str) -> tuple[int, int]:
    """Count added/removed content lines in a diff section."""
    added = removed = 0
    for line in section.splitlines():
        if line.startswith("+++") or line.startswith("---"):
            continue
        if line.startswith("+"):
            added += 1
        elif line.startswith("-"):
            removed += 1
    return added, removed


def build_change_manifest(diff: str) -> list[dict]:
    """Build a per-file manifest (path, status, category, line counts)."""
    manifest: list[dict] = []
    for section in split_diff_sections(diff):
        path = section_path(section)
        if not path or path in EXCLUDED_REVIEW_PATHS:
            continue
        added, removed = section_line_counts(section)
        manifest.append(
            {
                "path": path,
                "status": section_status(section),
                "category": classify_path(path),
                "added": added,
                "removed": removed,
            }
        )
    return manifest


def _iter_changed_content_lines(section: str) -> list[str]:
    """Return added/removed content lines from a diff section (without +/-)."""
    lines: list[str] = []
    for line in section.splitlines():
        if line.startswith("+++") or line.startswith("---"):
            continue
        if line.startswith("+") or line.startswith("-"):
            lines.append(line[1:])
    return lines


def _split_changed_content_lines(section: str) -> tuple[list[str], list[str]]:
    """Return (added, removed) content lines from a diff section (without +/-)."""
    added: list[str] = []
    removed: list[str] = []
    for line in section.splitlines():
        if line.startswith("+++") or line.startswith("---"):
            continue
        if line.startswith("+"):
            added.append(line[1:])
        elif line.startswith("-"):
            removed.append(line[1:])
    return added, removed


# Access/visibility modifiers whose flip (e.g. private→internal to expose a
# helper to another assembly/UI) changes no behavior.
_ACCESS_MODIFIER_RE = re.compile(r"\b(?:public|private|protected|internal)\b")


def _normalize_csharp_visibility(line: str) -> str:
    """Strip access-modifier tokens and collapse whitespace for comparison."""
    return re.sub(r"\s+", " ", _ACCESS_MODIFIER_RE.sub("", line)).strip()


def _is_visibility_only_change(added: list[str], removed: list[str]) -> bool:
    """True when the substantive added/removed lines differ ONLY by visibility.

    Pairs the substantive changed lines after removing access modifiers; if the
    two multisets are then identical, the entire change was an access-modifier
    flip (e.g. `private static` → `internal static`) with no behavioral effect.
    """
    if not added and not removed:
        return False
    norm_added = sorted(_normalize_csharp_visibility(l) for l in added)
    norm_removed = sorted(_normalize_csharp_visibility(l) for l in removed)
    return norm_added == norm_removed


def _is_split_scaffolding_csharp(line: str) -> bool:
    """True for non-behavioral file-split scaffolding around moved code."""
    stripped = line.strip()
    return stripped.startswith("partial class ") or stripped.startswith("namespace ")


def _normalized_move_lines(lines: list[str]) -> list[str]:
    """Normalize substantive lines for cross-file move/split comparison."""
    normalized: list[str] = []
    for line in lines:
        if _is_split_scaffolding_csharp(line):
            continue
        normalized.append(_normalize_csharp_visibility(line))
    return normalized



def _is_comment_or_blank_csharp(line: str) -> bool:
    stripped = line.strip()
    if not stripped:
        return True
    return (
        stripped.startswith("//")
        or stripped.startswith("/*")
        or stripped.startswith("*/")
        or stripped.startswith("*")
    )


def _is_substantive_csharp_change(line: str, *, test_file: bool) -> bool:
    """Heuristic for whether a changed C# line is behavior-relevant."""
    stripped = line.strip()
    if _is_comment_or_blank_csharp(stripped):
        return False
    if stripped in {"{", "}"}:
        return False
    if stripped.startswith("using "):
        return False
    # Keep namespace-only edits non-substantive for tests so comment/header
    # churn does not satisfy the "test coverage changed" gate.
    if test_file and stripped.startswith("namespace "):
        return False
    return True


def _is_core_algorithm_code_path(path: str) -> bool:
    lower = path.lower()
    if not lower.endswith(".cs"):
        return False
    base = lower.rsplit("/", 1)[-1]
    return (
        base in {"strategybuilder.core.cs"}
        or base == "comparisonstate.cs"
        or base.startswith("strategybuilder.")
    )


_NO_TEST_DECLARATION_RE = re.compile(
    r"\b(?:no|without|skip(?:ped|ping)?|omit(?:ted|ting)?|not\s+adding)\b"
    r"[^\n.]{0,40}\b(?:test|tests|coverage)\b",
    re.IGNORECASE,
)
_NO_TEST_REASON_HINTS = {
    "refactor",
    "mechanical",
    "relocate",
    "relocates",
    "relocated",
    "relocation",
    "move",
    "moved",
    "split",
    "file split",
    "extract",
    "extraction",
    "structural refactor",
    "structural extraction",
    "structural split",
    "non-functional",
    "no behavior change",
    "does not change behavior",
    "doesn't change behavior",
    "behavior-preserving",
    "does not introduce or modify algorithm behavior",
    "does not modify algorithm behavior",
    "no algorithmic decision path",
    "timing-only",
    "behavior-preserving",
    "existing test",
    "already covered",
    "covered by",
    "no observable change",
    "no user-visible",
    "documentation only",
    "docs only",
}
_NO_TEST_EVIDENCE_HINTS = {
    "existing test",
    "existing tests",
    "guarded by",
    "existing regression tests",
    "regression tests",
    "remain applicable",
    "already covered",
    "covered by",
    "coverage",
    "tests",
    "verified",
    "validated",
    "manual verification",
    "local verification",
    "passing",
    "passed",
    "dotnet build",
    "dotnet test",
    "invariant",
    "proof",
    "behavior unchanged",
    "behavior-preserving",
    "mechanical split",
    "rename only",
}
_NO_TEST_SECTION_HEADER_RE = re.compile(
    r"^\s{0,3}#{1,6}\s*(why\s+no(?:\s+new|\s+additional)?\s+tests?|test\s+rationale|no-?test\s+rationale)\b",
    re.IGNORECASE,
)


def _extract_no_test_section(pr_body: str) -> str:
    """Extract a markdown section dedicated to test-omission rationale."""
    lines = (pr_body or "").splitlines()
    if not lines:
        return ""

    start = -1
    for idx, line in enumerate(lines):
        if _NO_TEST_SECTION_HEADER_RE.match(line):
            start = idx + 1
            break
    if start < 0:
        return ""

    section: list[str] = []
    for line in lines[start:]:
        if line.lstrip().startswith("#"):
            break
        section.append(line)
    return "\n".join(section).strip()


def _normalize_review_text(text: str) -> str:
    """Normalise freeform review text for robust phrase matching."""
    normalized = " ".join((text or "").lower().split())
    normalized = normalized.replace("behaviour", "behavior")
    normalized = normalized.replace("behavior preserving", "behavior-preserving")
    return normalized


_NO_TEST_FIELD_RE = re.compile(r"^\s*[-*]?\s*(reason|evidence)\s*:\s*(.*\S)\s*$", re.IGNORECASE)
_NO_TEST_EVIDENCE_RE = re.compile(
    r"\b(?:[A-Z][A-Za-z0-9_]*Tests?|dotnet\s+(?:build|test)|pass(?:es|ed|ing)?|"
    r"guarded\s+by|covered\s+by|verified|validated|parity|invariant|local\s+verification|manual\s+verification)\b",
    re.IGNORECASE,
)


def _extract_no_test_fields(text: str) -> tuple[str, str]:
    """Extract explicit Reason/Evidence bullets from a no-test explanation."""
    reason_parts: list[str] = []
    evidence_parts: list[str] = []
    active_field: str | None = None

    for raw_line in (text or "").splitlines():
        line = raw_line.strip()
        if not line:
            active_field = None
            continue

        match = _NO_TEST_FIELD_RE.match(line)
        if match:
            active_field = match.group(1).lower()
            value = match.group(2).strip()
            if active_field == "reason":
                reason_parts.append(value)
            else:
                evidence_parts.append(value)
            continue

        if active_field == "reason":
            reason_parts.append(line.lstrip("-* "))
        elif active_field == "evidence":
            evidence_parts.append(line.lstrip("-* "))

    return " ".join(reason_parts).strip(), " ".join(evidence_parts).strip()


def _has_reasonable_no_test_reason(text: str) -> bool:
    normalized = _normalize_review_text(text)
    return bool(normalized) and any(hint in normalized for hint in _NO_TEST_REASON_HINTS)


def _has_reasonable_no_test_evidence(text: str) -> bool:
    normalized = _normalize_review_text(text)
    return bool(normalized) and (
        any(hint in normalized for hint in _NO_TEST_EVIDENCE_HINTS)
        or bool(_NO_TEST_EVIDENCE_RE.search(text or ""))
    )


def _has_reasonable_no_test_explanation(pr_body: str) -> bool:
    """Heuristic: PR description explicitly and concretely justifies no new tests."""
    raw = pr_body or ""
    if not raw.strip():
        return False

    section_text = _extract_no_test_section(raw)
    has_explicit_no_test_section = bool(section_text)
    candidate = section_text if section_text else raw
    normalized = _normalize_review_text(candidate)
    if not (has_explicit_no_test_section or _NO_TEST_DECLARATION_RE.search(normalized)):
        return False

    reason_text, evidence_text = _extract_no_test_fields(candidate)
    has_reason = _has_reasonable_no_test_reason(reason_text or candidate)
    has_evidence = _has_reasonable_no_test_evidence(evidence_text or candidate)
    return has_reason and has_evidence


def detect_core_algorithm_test_gap(diff: str, pr_body: str = "") -> str | None:
    """Return a BLOCK finding when core algorithm changes lack substantive tests."""
    changed_core_files: set[str] = set()
    changed_test_files: set[str] = set()
    substantive_test_files: set[str] = set()
    core_added_norm: list[str] = []
    core_removed_norm: list[str] = []
    saw_core_added_file = False

    for section in split_diff_sections(diff):
        path = section_path(section)
        if not path or path in EXCLUDED_REVIEW_PATHS:
            continue

        category = classify_path(path)
        changed_lines = _iter_changed_content_lines(section)

        if category == "test":
            changed_test_files.add(path)
            if any(_is_substantive_csharp_change(line, test_file=True) for line in changed_lines):
                substantive_test_files.add(path)
            continue

        if category != "code" or not _is_core_algorithm_code_path(path):
            continue
        if section_status(section) == "added":
            saw_core_added_file = True
        added_lines, removed_lines = _split_changed_content_lines(section)
        added_sub = [l for l in added_lines if _is_substantive_csharp_change(l, test_file=False)]
        removed_sub = [l for l in removed_lines if _is_substantive_csharp_change(l, test_file=False)]
        if not added_sub and not removed_sub:
            continue
        # A pure access-modifier flip (e.g. private→internal to expose a helper
        # to the UI) changes no behavior, so it does not require new tests.
        if _is_visibility_only_change(added_sub, removed_sub):
            continue
        core_added_norm.extend(_normalized_move_lines(added_sub))
        core_removed_norm.extend(_normalized_move_lines(removed_sub))
        changed_core_files.add(path)

    # A mechanical split/move of existing core-algorithm code into new partial or
    # focused files (same substantive lines removed and added overall) is a
    # reorganization-only change and does not require new tests.
    if (
        changed_core_files
        and saw_core_added_file
        and sorted(core_added_norm) == sorted(core_removed_norm)
    ):
        return None

    if not changed_core_files or substantive_test_files:
        return None

    # Allow an explicit PR-level waiver when the author clearly explains why
    # adding a new test would be non-meaningful for this specific change.
    if _has_reasonable_no_test_explanation(pr_body):
        return None

    core_list = ", ".join(f"`{p}`" for p in sorted(changed_core_files))
    if changed_test_files:
        return (
            f"- **[BLOCK]** Core algorithm files changed ({core_list}) but no substantive test "
            "updates were detected. Current test-file edits appear to be non-functional "
            "(e.g. comments/format/import/header only). Add or modify real test assertions/cases "
            "that guard this algorithm change. If a meaningful test is not appropriate, explain "
            "the reason in the PR description instead of adding a no-op test. Recommended format: "
            "add a `Why no test` section stating (1) why behavior risk is low and (2) what existing "
            "coverage/verification already guards the change."
        )
    return (
        f"- **[BLOCK]** Core algorithm files changed ({core_list}) but no test files with "
        "substantive test logic updates were detected. Add or modify real test assertions/cases "
        "that guard this algorithm change. If a meaningful test is not appropriate, explain "
        "the reason in the PR description instead of adding a no-op test. Recommended format: "
        "add a `Why no test` section stating (1) why behavior risk is low and (2) what existing "
        "coverage/verification already guards the change."
    )


# Extensions that are almost never source/config and usually indicate an
# accidental commit (build output, logs, scratch dumps) when added as loose
# files outside a docs/ area.
_SUSPICIOUS_DATA_EXTENSIONS = {
    "txt", "log", "out", "csv", "tsv", "dat", "tmp", "bak", "orig", "dump",
}
_DOC_LIKE_EXTENSIONS = {"md", "rst", "adoc"}
# Well-known root documentation stems that are always legitimate additions.
_STANDARD_DOC_STEMS = {
    "readme", "changelog", "license", "licence", "contributing",
    "code_of_conduct", "security", "notice", "authors", "copying",
}

_FORMAT_INTENT_RE = re.compile(
    r"\b(?:format(?:ting)?|whitespace|style(?:-only)?|lint(?:ing)?|cleanup)\b",
    re.IGNORECASE,
)


def _has_explicit_formatting_intent(pr_title: str, pr_body: str) -> bool:
    """True when PR metadata explicitly says formatting/styling is intentional."""
    text = f"{pr_title or ''}\n{pr_body or ''}"
    return bool(_FORMAT_INTENT_RE.search(text))


def _is_format_only_csharp_section(section: str) -> bool:
    """True when a C# diff section changes only whitespace/newline layout.

    This includes blank-line churn and stray whitespace-only edits that do not
    change any substantive code. Safety: if changed lines include quote
    characters, skip this heuristic to avoid misclassifying string-literal text
    edits as formatting-only.
    """
    added, removed = _split_changed_content_lines(section)
    if not added and not removed:
        return False

    def _normalize_nonempty_lines(lines: list[str]) -> list[str]:
        normalized: list[str] = []
        for line in lines:
            stripped = line.strip()
            if not stripped:
                continue
            normalized.append(re.sub(r"\s+", "", line))
        return normalized

    changed = [line for line in (added + removed) if line.strip()]
    if not changed:
        return True
    if any('"' in line or "'" in line for line in changed):
        return False

    norm_added = _normalize_nonempty_lines(added)
    norm_removed = _normalize_nonempty_lines(removed)
    if not norm_added and not norm_removed:
        return True
    return norm_added == norm_removed


def _iter_diff_hunks(section: str) -> list[tuple[str, str]]:
    """Return (hunk header, hunk body) pairs from a single diff section."""
    hunks: list[tuple[str, str]] = []
    current_header = ""
    current_lines: list[str] = []
    for line in section.splitlines(keepends=True):
        if line.startswith("@@"):
            if current_header:
                hunks.append((current_header, "".join(current_lines)))
            current_header = line.strip()
            current_lines = []
            continue
        if current_header:
            current_lines.append(line)
    if current_header:
        hunks.append((current_header, "".join(current_lines)))
    return hunks


def detect_unexplained_format_only_code_changes(diff: str, pr_title: str, pr_body: str) -> str | None:
    """BLOCK formatting-only C# file changes unless the PR explicitly says so."""
    if _has_explicit_formatting_intent(pr_title, pr_body):
        return None

    format_only_hunks_by_path: dict[str, list[str]] = {}
    for section in split_diff_sections(diff):
        path = section_path(section)
        if not path or path in EXCLUDED_REVIEW_PATHS:
            continue
        if classify_path(path) != "code" or not path.lower().endswith(".cs"):
            continue
        if section_status(section) in {"added", "deleted", "renamed"}:
            continue

        hunk_headers: list[str] = []
        for header, hunk_body in _iter_diff_hunks(section):
            if _is_format_only_csharp_section(hunk_body):
                hunk_headers.append(header)

        if hunk_headers:
            format_only_hunks_by_path[path] = hunk_headers

    if not format_only_hunks_by_path:
        return None

    file_hunk_list = ", ".join(
        f"`{path}` ({'; '.join(hunks)})"
        for path, hunks in sorted(format_only_hunks_by_path.items())
    )
    return (
        f"- **[BLOCK]** Unexplained formatting-only code change(s) detected: {file_hunk_list}. "
        "These edits only alter whitespace/line-wrapping and are not described as "
        "a formatting/style change in the PR title/description. Remove the unrelated "
        "formatting-only code edits, or explicitly state that formatting is intentional."
    )


def _is_docs_location(path: str) -> bool:
    """True when the file lives under a docs/ (or doc/) directory."""
    segments = path.lower().split("/")[:-1]
    return any(seg in {"docs", "doc"} for seg in segments)


def detect_suspicious_added_files(diff: str) -> str | None:
    """Deterministically BLOCK on loose, unexplained data/doc files added in a PR.

    Whether an added file's *purpose* matches the description is a judgment call
    left to the structural model, but certain additions are almost always
    accidental — build/output or log/scratch dumps, or stray text/markdown files
    dropped at the repo root outside any docs area. Detecting these
    deterministically guarantees a BLOCK ("remove or explain") instead of relying
    on the model, whose relevance judgment is inconsistent. Their contents are
    never fed to the deep line-level review (see is_line_reviewable), so once
    spotted we block on their mere presence rather than analyzing them.
    """
    suspicious: list[str] = []
    for section in split_diff_sections(diff):
        path = section_path(section)
        if not path or path in EXCLUDED_REVIEW_PATHS:
            continue
        if section_status(section) != "added":
            continue
        segments = path.lower().split("/")
        # Infra/config/tooling paths are legitimate.
        if segments[0] in {".github", ".vscode", ".config"}:
            continue
        # Code and test files are structurally legitimate; unexplained NEW source
        # files are judged by the structural model, not here.
        if classify_path(path) in {"code", "test"}:
            continue
        base = segments[-1]
        stem, _, ext = base.rpartition(".")
        if not stem:  # no extension
            stem, ext = base, ""
        # Docs inside a docs/ area, or standard root docs, are legitimate.
        if _is_docs_location(path) or stem in _STANDARD_DOC_STEMS:
            continue
        if ext in _SUSPICIOUS_DATA_EXTENSIONS or ext in _DOC_LIKE_EXTENSIONS:
            suspicious.append(path)

    if not suspicious:
        return None
    file_list = ", ".join(f"`{p}`" for p in sorted(suspicious))
    return (
        f"- **[BLOCK]** Unexplained/irrelevant new file(s) added: {file_list}. "
        "These are loose data/dump or stray text/markdown files that are not part "
        "of the normal source/test/docs structure and are a common sign of an "
        "accidental commit. Remove the unrelated file(s); or, if they are "
        "intentional, move them to an appropriate location (e.g. `docs/`) and "
        "explain their purpose in the PR description."
    )


def format_change_manifest(manifest: list[dict]) -> str:
    """Render the manifest plus test/doc coverage facts for the structural pass."""
    counts = {"code": 0, "test": 0, "doc": 0}
    lines: list[str] = []
    for entry in manifest:
        counts[entry["category"]] += 1
        lines.append(
            f"- [{entry['category']}] {entry['status']}: {entry['path']} "
            f"(+{entry['added']}/-{entry['removed']})"
        )
    summary = (
        f"Changed files: {len(manifest)} "
        f"(code={counts['code']}, test={counts['test']}, doc={counts['doc']}).\n"
        f"Test files changed: {'yes' if counts['test'] else 'NO'}. "
        f"Doc files changed: {'yes' if counts['doc'] else 'NO'}."
    )
    return summary + "\n\n" + "\n".join(lines)


# Heuristics for surfacing new user-facing identifiers even when the diff sent to
# the structural pass is truncated. Scans ADDED lines of the whole diff for CLI
# flags and short mode/enum-like string literals.
_FLAG_RE = re.compile(r"--[a-zA-Z][\w-]{1,40}")
_TOKEN_LITERAL_RE = re.compile(r'"([A-Za-z][A-Za-z0-9_-]{1,30})"')


def extract_new_identifiers(diff: str, limit: int = 40) -> list[str]:
    """Collect notable new CLI flags / mode-like string literals from added lines."""
    flags: set[str] = set()
    literals: set[str] = set()
    for section in split_diff_sections(diff):
        if not is_line_reviewable(section_path(section)):
            continue
        for line in section.splitlines():
            # Only added content lines (skip the "+++" file header).
            if not line.startswith("+") or line.startswith("+++"):
                continue
            body = line[1:]
            for flag in _FLAG_RE.findall(body):
                flags.add(flag)
            for lit in _TOKEN_LITERAL_RE.findall(body):
                # Keep tokens that look like mode/enum values: contain a hyphen,
                # or are a single all-lower word (e.g. exact, greedy, three-phase).
                if "-" in lit or lit.islower():
                    literals.add(lit)
    ordered = sorted(flags) + sorted(literals - flags)
    return ordered[:limit]


def format_new_identifiers(identifiers: list[str]) -> str:
    if not identifiers:
        return "Notable new identifiers/literals (from added lines): (none detected)"
    return (
        "Notable new identifiers/literals (from added lines of the whole diff):\n"
        + ", ".join(f"`{t}`" for t in identifiers)
    )


def _contains_non_english_letters(text: str) -> bool:
    """Return True when text contains letters outside the Latin script.

    English-only enforcement is intentionally strict for PR metadata: if title
    or description includes letters from non-Latin scripts (e.g. Chinese,
    Japanese, Korean, Cyrillic), the PR is blocked.
    """
    for ch in text:
        if not ch.isalpha():
            continue
        # Fast path: plain ASCII letters are always accepted.
        if "A" <= ch <= "Z" or "a" <= ch <= "z":
            continue
        name = unicodedata.name(ch, "")
        if "LATIN" not in name:
            return True
    return False


def validate_pr_metadata_language(pr_title: str, pr_body: str) -> tuple[bool, str]:
    """Validate that PR title and description are written in English only."""
    fields = [
        ("title", pr_title or ""),
        ("description", pr_body or ""),
    ]
    invalid = [field for field, value in fields if _contains_non_english_letters(value)]
    if not invalid:
        return True, ""

    field_text = " and ".join(invalid)
    review = (
        "## Summary\n"
        "This PR is blocked by metadata language policy.\n\n"
        "## Findings\n"
        f"- **[BLOCK]** Pull request {field_text} must be written in English only. "
        "Non-English language content was detected.\n\n"
        "VERDICT: BLOCK"
    )
    return False, review


def validate_pr_description_format(pr_body: str) -> tuple[bool, str]:
    """Validate PR description formatting for common broken JSON-escaped bodies.

    A frequent formatting mistake is posting a JSON-escaped string as-is, which
    leaves literal "\\n" tokens in the description instead of real newlines.
    That makes Markdown sections/bullets render as one long line and should
    block until corrected.
    """
    body = pr_body or ""
    escaped_newlines = body.count("\\n")
    has_real_newline = "\n" in body
    # Treat this as malformed only when the description appears broadly escaped,
    # not when it contains an occasional literal "\\n" inside prose/code.
    looks_json_escaped = escaped_newlines >= 2 and not has_real_newline
    if not looks_json_escaped:
        return True, ""

    review = (
        "## Summary\n"
        "This PR is blocked by description formatting policy.\n\n"
        "## Findings\n"
        "- **[BLOCK]** Pull request description appears to contain literal escaped "
        "newlines (`\\n`) instead of real line breaks. This usually means the "
        "description was pasted as a JSON-escaped string and Markdown sections/"
        "lists will not render correctly. Please rewrite the description with "
        "actual newlines.\n\n"
        "VERDICT: BLOCK"
    )
    return False, review


def validate_branch_is_based_on_latest_base(pr_base_ref: str, pr_head_sha: str) -> tuple[bool, str]:
    """Validate that the PR head already contains the latest base-branch tip."""
    base_ref = (pr_base_ref or "").strip()
    head_sha = (pr_head_sha or "").strip()
    if not base_ref or not head_sha:
        return True, ""

    remote_ref = f"origin/{base_ref}"
    base_proc = subprocess.run(
        ["git", "rev-parse", remote_ref],
        text=True,
        capture_output=True,
    )
    if base_proc.returncode != 0:
        raise RuntimeError(
            f"Could not resolve {remote_ref}: {base_proc.stderr.strip() or base_proc.stdout.strip()}"
        )
    latest_base_sha = base_proc.stdout.strip()

    merge_base_proc = subprocess.run(
        ["git", "merge-base", "--is-ancestor", latest_base_sha, head_sha],
        text=True,
        capture_output=True,
    )
    if merge_base_proc.returncode == 0:
        return True, ""
    if merge_base_proc.returncode != 1:
        raise RuntimeError(
            "Could not compare PR head against latest base branch: "
            f"{merge_base_proc.stderr.strip() or merge_base_proc.stdout.strip()}"
        )

    review = (
        "## Summary\n"
        f"This PR is blocked because it is not based on the latest `{base_ref}` branch head.\n\n"
        "## Findings\n"
        f"- **[BLOCK]** PR head `{head_sha}` does not contain the current `{base_ref}` tip `{latest_base_sha}`. "
        f"Rebase or merge the latest `{base_ref}` into this branch, then rerun the review.\n\n"
        "VERDICT: BLOCK"
    )
    return False, review



def filtered_review_diff(diff: str, max_chars: int = MAX_DIFF_CHARS) -> str:
    """Concatenate reviewable (non-infra, non-data) diff sections, truncated for the model."""
    sections = [
        section
        for section in split_diff_sections(diff)
        if is_line_reviewable(section_path(section))
    ]
    combined = "".join(sections)
    if len(combined) > max_chars:
        combined = (
            combined[:max_chars]
            + "\n\n[diff truncated for review — only the first part is shown]\n"
        )
    return combined


def _post_once(model: str, payload: bytes, token: str) -> str:
    """Single POST to the models endpoint for a specific model."""
    request = urllib.request.Request(
        MODELS_ENDPOINT,
        data=payload,
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json",
            "Accept": "application/json",
        },
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=180) as response:
        data = json.loads(response.read().decode("utf-8"))
    return data["choices"][0]["message"]["content"].strip()


def request_chat_completion(messages: list[dict]) -> str:
    """POST a chat completion, rotating across the model chain on HTTP 429.

    Each model in ``MODEL_CHAIN`` has its own quota, so on a 429 we immediately
    fall back to the next model instead of waiting. A model that succeeds
    becomes "sticky" for subsequent calls (batches) so we don't re-hit models
    already known to be exhausted. Only when every model in the chain is rate
    limited do we apply a capped backoff and retry the whole chain, up to
    ``MODEL_MAX_RETRIES`` passes.
    """
    global _active_model_index
    token = os.environ["GITHUB_TOKEN"]

    def payload_for(model: str) -> bytes:
        return json.dumps(
            {"model": model, "temperature": 0.1, "messages": messages}
        ).encode("utf-8")

    for attempt in range(MODEL_MAX_RETRIES):
        for offset in range(len(MODEL_CHAIN)):
            index = (_active_model_index + offset) % len(MODEL_CHAIN)
            model = MODEL_CHAIN[index]
            try:
                result = _post_once(model, payload_for(model), token)
                _active_model_index = index  # stick with the working model
                return result
            except urllib.error.HTTPError as err:
                if err.code == 429:
                    print(f"Rate limited (429) on model '{model}'; trying next model.")
                    continue
                raise
        # Every model in the chain was rate limited in this pass (a non-429
        # error would have propagated above); back off and retry the chain.
        if attempt < MODEL_MAX_RETRIES - 1:
            delay = min(
                MODEL_RETRY_BASE_SECONDS * (2 ** attempt), MODEL_RETRY_MAX_SECONDS
            )
            print(
                f"All models rate limited; backing off {delay:.0f}s "
                f"(pass {attempt + 1}/{MODEL_MAX_RETRIES})."
            )
            time.sleep(delay)

    raise urllib.error.HTTPError(
        MODELS_ENDPOINT, 429, "All models rate limited (429).", {}, None
    )



def call_structural_model(manifest_text: str, identifiers_text: str, diff: str) -> str:
    """Run the PR-level structural/consistency review pass."""
    pr_title = os.environ.get("PR_TITLE", "")
    pr_body = os.environ.get("PR_BODY", "")

    user_content = (
        f"Pull request title: {pr_title}\n\n"
        f"Pull request description:\n{pr_body or '(none)'}\n\n"
        f"Changed-file manifest:\n{manifest_text}\n\n"
        f"{identifiers_text}\n\n"
        f"Combined diff (may be truncated):\n\n```diff\n{diff}\n```"
    )

    return request_chat_completion(
        [
            {"role": "system", "content": STRUCTURAL_SYSTEM_PROMPT},
            {"role": "user", "content": user_content},
        ]
    )


def call_model(diff: str, batch_index: int, batch_total: int) -> str:
    pr_title = os.environ.get("PR_TITLE", "")
    pr_body = os.environ.get("PR_BODY", "")

    user_content = (
        f"Batch {batch_index} of {batch_total}\n\n"
        f"Pull request title: {pr_title}\n\n"
        f"Pull request description:\n{pr_body or '(none)'}\n\n"
        f"Here is the unified diff to review:\n\n```diff\n{diff}\n```"
    )

    return request_chat_completion(
        [
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": user_content},
        ]
    )


def parse_verdict(review: str) -> str:
    verdict = "COMMENT"
    for line in reversed(review.splitlines()):
        stripped = line.strip()
        if stripped.upper().startswith("VERDICT:"):
            value = stripped.split(":", 1)[1].strip().upper()
            if value in {"BLOCK", "COMMENT", "APPROVE"}:
                verdict = value
            break
    return verdict


def strip_verdict_line(review: str) -> str:
    lines = review.rstrip().splitlines()
    if lines and lines[-1].strip().upper().startswith("VERDICT:"):
        lines.pop()
    return "\n".join(lines).strip()


_BULLET_RE = re.compile(r"^\s*[-*]\s+")
_SEVERITY_TAG_RE = re.compile(r"\*\*\[[A-Za-z]+\]\*\*|\[[A-Za-z]+\]")


def _drop_leading_heading(text: str) -> str:
    """Remove leading blank lines and a single leading markdown heading."""
    lines = text.splitlines()
    while lines and not lines[0].strip():
        lines.pop(0)
    if lines and lines[0].strip().startswith("#"):
        lines.pop(0)
    while lines and not lines[0].strip():
        lines.pop(0)
    return "\n".join(lines).strip()


def _split_summary_findings(review_text: str) -> tuple[str, str]:
    """Split a model review into (summary prose, findings block)."""
    text = strip_verdict_line(review_text)
    lines = text.splitlines()
    idx = next(
        (i for i, l in enumerate(lines) if l.strip().lower().startswith("## findings")),
        None,
    )
    if idx is None:
        return _drop_leading_heading(text), ""
    summary = _drop_leading_heading("\n".join(lines[:idx]))
    rest = lines[idx + 1 :]
    cut = next((i for i, l in enumerate(rest) if l.strip().startswith("## ")), len(rest))
    findings = "\n".join(rest[:cut]).strip()
    return summary, findings


def _parse_bullets(findings_text: str) -> list[str]:
    """Split a findings block into individual bullet items (multi-line aware)."""
    items: list[str] = []
    current: list[str] = []
    for line in findings_text.splitlines():
        if _BULLET_RE.match(line):
            if current:
                items.append("\n".join(current).rstrip())
            current = [line.strip()]
        elif current:
            current.append(line.rstrip())
    if current:
        items.append("\n".join(current).rstrip())
    return [i for i in items if i.strip()]


def _norm_bullet_key(bullet: str) -> str:
    """Normalise a bullet for de-duplication across batches."""
    stripped = _SEVERITY_TAG_RE.sub("", bullet)
    return re.sub(r"[^a-z0-9]+", " ", stripped.lower()).strip()[:90]


def _is_noise_bullet(bullet: str) -> bool:
    low = bullet.lower()
    return "no issues found" in low or "**[approve]**" in low or "[approve]" in low


_SELF_NEGATING_STRUCTURAL_BULLET_RE = re.compile(r":\s*none\.", re.IGNORECASE)


def _is_self_negating_structural_bullet(bullet: str) -> bool:
    """True for contradictory structural bullets like '...: None.'"""
    normalized = " ".join((bullet or "").split())
    if not _SELF_NEGATING_STRUCTURAL_BULLET_RE.search(normalized):
        return False

    stripped = _BULLET_MARKER_RE.sub("", normalized, count=1)
    stripped = _LEADING_SEVERITY_RE.sub("", stripped)
    lowered = stripped.lower()
    return bool(re.match(r"^[a-z][a-z\s/*_-]+:\s*none\.", lowered))


_EMPTY_DESCRIPTION_STRUCTURAL_RE = re.compile(
    r"\b(?:pr|pull request)\s+description\b[^\n]*\b(?:empty|missing|absent|not provided|completely absent)\b",
    re.IGNORECASE,
)


def _is_false_empty_description_structural_bullet(bullet: str, pr_body: str) -> bool:
    """Drop structural bullets claiming description is empty when it is not."""
    if not (pr_body or "").strip():
        return False
    normalized = " ".join((bullet or "").split())
    return bool(_EMPTY_DESCRIPTION_STRUCTURAL_RE.search(normalized))


_BULLET_MARKER_RE = re.compile(r"^\s*[-*]\s+")
_LEADING_SEVERITY_RE = re.compile(
    r"^\**\[(?:BLOCK|COMMENT|APPROVE)\]\**\s*", re.IGNORECASE
)


def _bullet_severity(bullet: str) -> str:
    """Classify a bullet as BLOCK or COMMENT from its leading severity tag."""
    first = bullet.splitlines()[0] if bullet.strip() else ""
    match = re.search(r"\[(BLOCK|COMMENT|APPROVE)\]", first, re.IGNORECASE)
    return match.group(1).upper() if match else "COMMENT"


def _reformat_bullet(bullet: str, source: str, number: int | None) -> str:
    """Re-render a finding bullet with a source tag and (for blocks) a B# label.

    The original inline severity tag is stripped: blocking items get a
    ``**[B<n>]**`` prefix, comments drop the tag entirely (they live under the
    Comments section). Continuation lines (e.g. quoted code) are preserved.
    """
    lines = bullet.splitlines()
    if not lines:
        return bullet
    first = _BULLET_MARKER_RE.sub("", lines[0], count=1)
    first = _LEADING_SEVERITY_RE.sub("", first)
    label = f"**[B{number}]** " if number is not None else ""
    src = f"({source}) " if source else ""
    new_first = f"- {label}{src}{first}".rstrip()
    return "\n".join([new_first, *lines[1:]])


def combine_batch_reviews(
    batch_reviews: list[tuple[int, int, str, str]],
    structural: tuple[str, str] | None = None,
    policy_findings: list[str] | None = None,
    pr_body: str = "",
) -> str:
    """Merge the structural review and per-batch reviews into ONE consolidated
    review body. Batches are an internal chunking detail and are never exposed:
    findings are integrated into a single list, deduplicated across batches."""
    verdict_order = {"APPROVE": 0, "COMMENT": 1, "BLOCK": 2}
    all_verdicts = [verdict for _, _, verdict, _ in batch_reviews]
    struct_summary, struct_findings = ("", "")
    effective_structural_verdict = "APPROVE"
    if structural is not None:
        effective_structural_verdict = structural[0]
        struct_summary, struct_findings = _split_summary_findings(structural[1])
    struct_bullets = [
        b
        for b in _parse_bullets(struct_findings)
        if (
            not _is_noise_bullet(b)
            and not _is_self_negating_structural_bullet(b)
            and not _is_false_empty_description_structural_bullet(b, pr_body)
        )
    ]
    if effective_structural_verdict != "APPROVE" and not struct_bullets:
        effective_structural_verdict = "APPROVE"

    if structural is not None:
        all_verdicts.append(effective_structural_verdict)
    if policy_findings:
        all_verdicts.append("BLOCK")
    final_verdict = max(all_verdicts, key=lambda v: verdict_order[v]) if all_verdicts else "APPROVE"

    # Merge code-level findings from every batch, dropping noise and duplicates.
    code_bullets: list[str] = []
    seen: set[str] = set()
    batch_summaries: list[str] = []
    for _, _, _, review in batch_reviews:
        summary, findings = _split_summary_findings(review)
        if summary:
            batch_summaries.append(summary)
        for bullet in _parse_bullets(findings):
            if _is_noise_bullet(bullet):
                continue
            key = _norm_bullet_key(bullet)
            if key in seen:
                continue
            seen.add(key)
            code_bullets.append(bullet)

    # Overall narrative: prefer the holistic structural summary; else the batch
    # summaries joined into one paragraph.
    overall_summary = struct_summary or " ".join(batch_summaries).strip()

    # Tag every finding with its source, then split by severity so all blocking
    # items surface at the top regardless of source or arrival order.
    tagged = (
        [("policy", b) for b in (policy_findings or [])]
        + [("structure", b) for b in struct_bullets]
        + [("code", b) for b in code_bullets]
    )
    blocking = [(src, b) for src, b in tagged if _bullet_severity(b) == "BLOCK"]
    comments = [(src, b) for src, b in tagged if _bullet_severity(b) != "BLOCK"]

    # Keep the merge-gating verdict aligned with the visible findings: if the
    # consolidated review surfaced any blocking bullets, the posted review must
    # be a request for changes.
    if blocking:
        final_verdict = "BLOCK"

    parts: list[str] = ["## Summary"]
    if overall_summary:
        parts.append(overall_summary)
    verdict_line = f"\n_Verdict: **{final_verdict}**_"
    if blocking or comments:
        comment_word = "comment" if len(comments) == 1 else "comments"
        verdict_line += (
            f" — 🚫 {len(blocking)} blocking · 💬 {len(comments)} {comment_word}"
        )
    parts.append(verdict_line)
    parts.append("")

    if not blocking and not comments:
        parts.append("## Findings")
        parts.append("No issues found.")
    else:
        if blocking:
            parts.append("## 🚫 Blocking (must fix)")
            for index, (src, bullet) in enumerate(blocking, start=1):
                parts.append(_reformat_bullet(bullet, src, index))
            parts.append("")
        if comments:
            parts.append("<details>")
            parts.append(
                f"<summary>💬 Comments ({len(comments)}) — non-blocking</summary>"
            )
            parts.append("")  # blank line so the markdown list renders inside <details>
            for src, bullet in comments:
                parts.append(_reformat_bullet(bullet, src, None))
            parts.append("")
            parts.append("</details>")

    parts.append("")
    parts.append(f"VERDICT: {final_verdict}")
    return "\n".join(parts).strip()


# github-actions surfaces as "github-actions[bot]" over REST but plain
# "github-actions" over GraphQL; accept both so bot-authored reviews are
# recognised regardless of the API used to list them.
BOT_LOGINS = {"github-actions[bot]", "github-actions"}


def _is_bot_login(login: str | None) -> bool:
    return (login or "") in BOT_LOGINS


# Footer appended to every posted review/comment body, plus a hidden HTML marker
# so the reviewer can reliably recognise its OWN issue comments (the fallback
# path used when a formal review cannot be posted) and collapse stale ones.
AI_REVIEW_FOOTER = "*🤖 Automated review via GitHub Models.*"
AI_REVIEW_MARKER = "<!-- ai-code-review -->"


def _is_ai_review_comment(body: str | None) -> bool:
    text = body or ""
    return AI_REVIEW_MARKER in text or AI_REVIEW_FOOTER in text


def load_bot_reviews(repo: str, pr_number: str) -> list[dict]:
    """List a PR's reviews via GraphQL, including databaseId and isMinimized.

    GraphQL is used (instead of the REST reviews endpoint) so each review carries
    ``isMinimized`` — letting consolidation skip reviews it has already hidden and
    stay idempotent across re-runs.
    """
    owner, _, name = repo.partition("/")
    query = (
        "query($owner: String!, $name: String!, $number: Int!) {"
        " repository(owner: $owner, name: $name) {"
        " pullRequest(number: $number) {"
        " reviews(first: 100) {"
        " nodes { id databaseId state isMinimized author { login } } } } } }"
    )
    proc = subprocess.run(
        [
            "gh",
            "api",
            "graphql",
            "-f",
            f"query={query}",
            "-F",
            f"owner={owner}",
            "-F",
            f"name={name}",
            "-F",
            f"number={pr_number}",
        ],
        text=True,
        capture_output=True,
    )
    if proc.returncode != 0:
        print(f"Could not list reviews for consolidation: {proc.stderr.strip()}")
        return []

    try:
        data = json.loads(proc.stdout or "{}")
    except json.JSONDecodeError:
        return []

    nodes = (
        (((data.get("data") or {}).get("repository") or {}).get("pullRequest") or {})
        .get("reviews", {})
        .get("nodes", [])
    )
    return nodes or []


def _hide_review(review_node_id: str) -> bool:
    """Minimize (collapse) a review OR issue comment via GraphQL so it is hidden.

    The REST ``dismissals`` endpoint only accepts reviews whose state is
    ``APPROVED`` or ``CHANGES_REQUESTED``; a ``COMMENTED`` review (and a plain
    issue comment) cannot be dismissed and would otherwise pile up visibly on
    the PR. ``minimizeComment`` works on any Minimizable node, so it is used to
    hide the ones dismissal cannot reach (and to fully collapse dismissed ones).
    """
    if not review_node_id:
        return False
    query = (
        "mutation($id: ID!) {"
        " minimizeComment(input: {subjectId: $id, classifier: OUTDATED}) {"
        " minimizedComment { isMinimized } } }"
    )
    proc = subprocess.run(
        ["gh", "api", "graphql", "-f", f"query={query}", "-F", f"id={review_node_id}"],
        text=True,
        capture_output=True,
    )
    return proc.returncode == 0


def dismiss_previous_bot_reviews(repo: str, pr_number: str, keep_review_id: int | None = None) -> None:
    """Hide every prior bot review so ONLY the latest review remains visible.

    Regardless of state, each earlier bot review is collapsed via
    ``minimizeComment``; reviews that still carry a merge verdict
    (``CHANGES_REQUESTED``/``APPROVED``) are additionally dismissed first so the
    stale verdict no longer affects mergeability. Already-minimized reviews are
    skipped to keep re-runs idempotent and quiet.
    """
    for review in load_bot_reviews(repo, pr_number):
        database_id = review.get("databaseId")
        if database_id == keep_review_id:
            continue
        if not _is_bot_login((review.get("author") or {}).get("login")):
            continue

        state = review.get("state")
        node_id = review.get("id", "")

        # Drop any lingering merge verdict (approval or change request) so only
        # the latest review governs mergeability.
        if state in {"CHANGES_REQUESTED", "APPROVED"}:
            dismiss = subprocess.run(
                [
                    "gh",
                    "api",
                    f"repos/{repo}/pulls/{pr_number}/reviews/{database_id}/dismissals",
                    "--method",
                    "PUT",
                    "--input",
                    "-",
                ],
                input=json.dumps(
                    {
                        "message": "Replaced by a newer automated review.",
                        "event": "DISMISS",
                    }
                ),
                text=True,
                capture_output=True,
            )
            if dismiss.returncode == 0:
                print(f"Dismissed prior bot review {database_id}.")
            else:
                print(f"Failed to dismiss review {database_id}: {dismiss.stderr.strip()}")

        # Collapse the review body so only the latest review stays visible. Skip
        # ones already minimized to avoid redundant calls / noisy errors.
        if review.get("isMinimized"):
            continue
        if _hide_review(node_id):
            print(f"Hid prior bot review {database_id} (state={state}).")
        else:
            print(f"Could not minimize review {database_id} (state={state}).")


def load_bot_issue_comments(repo: str, pr_number: str) -> list[dict]:
    """List a PR's issue comments via GraphQL (id, isMinimized, author, body).

    These are distinct from reviews: the reviewer falls back to a plain issue
    comment when it cannot post a formal review (e.g. the PR author cannot
    request changes on their own PR), and those comments must be consolidated
    too so only the latest AI-review artifact stays visible.
    """
    owner, _, name = repo.partition("/")
    query = (
        "query($owner: String!, $name: String!, $number: Int!) {"
        " repository(owner: $owner, name: $name) {"
        " pullRequest(number: $number) {"
        " comments(first: 100) {"
        " nodes { id isMinimized author { login } body } } } } }"
    )
    proc = subprocess.run(
        [
            "gh",
            "api",
            "graphql",
            "-f",
            f"query={query}",
            "-F",
            f"owner={owner}",
            "-F",
            f"name={name}",
            "-F",
            f"number={pr_number}",
        ],
        text=True,
        capture_output=True,
    )
    if proc.returncode != 0:
        print(f"Could not list issue comments for consolidation: {proc.stderr.strip()}")
        return []

    try:
        data = json.loads(proc.stdout or "{}")
    except json.JSONDecodeError:
        return []

    nodes = (
        (((data.get("data") or {}).get("repository") or {}).get("pullRequest") or {})
        .get("comments", {})
        .get("nodes", [])
    )
    return nodes or []


def hide_previous_bot_comments(repo: str, pr_number: str, keep_comment_id: str | None = None) -> None:
    """Collapse stale AI-review issue comments so only the latest one remains.

    Only the reviewer's own comments (identified by the AI-review marker/footer)
    are touched; human comments are left alone. Already-minimized comments are
    skipped so re-runs stay idempotent. ``keep_comment_id`` is the GraphQL node
    id of a freshly posted fallback comment to preserve.
    """
    for comment in load_bot_issue_comments(repo, pr_number):
        node_id = comment.get("id", "")
        if node_id and node_id == keep_comment_id:
            continue
        if not _is_bot_login((comment.get("author") or {}).get("login")):
            continue
        if not _is_ai_review_comment(comment.get("body")):
            continue
        if comment.get("isMinimized"):
            continue
        if _hide_review(node_id):
            print(f"Hid prior AI-review comment {node_id}.")
        else:
            print(f"Could not minimize comment {node_id}.")


def post_review(review_body: str, verdict: str) -> None:
    repo = os.environ["GITHUB_REPOSITORY"]
    pr_number = os.environ["PR_NUMBER"]

    header = {
        "BLOCK": "## ⛔ AI Code Review — Changes requested\n\n"
        "This review found a serious issue that blocks merging.\n\n",
        "COMMENT": "## 💬 AI Code Review\n\n",
        "APPROVE": "## ✅ AI Code Review — Looks good\n\n",
    }[verdict]

    body = header + review_body + "\n\n" + AI_REVIEW_FOOTER + "\n\n" + AI_REVIEW_MARKER

    event = "REQUEST_CHANGES" if verdict == "BLOCK" else "APPROVE" if verdict == "APPROVE" else "COMMENT"

    payload = {"body": body, "event": event}

    def run_post_with_retry(cmd: list[str], request_body: dict, description: str) -> subprocess.CompletedProcess:
        last_proc: subprocess.CompletedProcess | None = None
        for attempt in range(1, REVIEW_PUBLISH_MAX_RETRIES + 1):
            proc = subprocess.run(
                cmd,
                input=json.dumps(request_body),
                text=True,
                capture_output=True,
            )
            if proc.returncode == 0:
                return proc

            last_proc = proc
            stderr = (proc.stderr or "").strip()
            stdout = (proc.stdout or "").strip()
            is_transient = _is_transient_gh_api_failure(stderr, stdout)
            if not is_transient or attempt == REVIEW_PUBLISH_MAX_RETRIES:
                break

            delay = min(
                REVIEW_PUBLISH_RETRY_BASE_SECONDS * (2 ** (attempt - 1)),
                REVIEW_PUBLISH_RETRY_MAX_SECONDS,
            )
            print(
                f"Transient failure while {description} "
                f"(attempt {attempt}/{REVIEW_PUBLISH_MAX_RETRIES}): "
                f"{stderr or stdout or f'gh api exited {proc.returncode}'}. "
                f"Retrying in {delay}s..."
            )
            time.sleep(delay)

        assert last_proc is not None
        return last_proc

    proc = run_post_with_retry(
        [
            "gh",
            "api",
            f"repos/{repo}/pulls/{pr_number}/reviews",
            "--method",
            "POST",
            "--input",
            "-",
            "--jq",
            ".id",
        ],
        payload,
        "posting pull-request review",
    )

    keep_review_id: int | None = None
    keep_comment_id: str | None = None
    published_any = False
    review_error = ""
    comment_error = ""
    if proc.returncode == 0:
        try:
            keep_review_id = int(proc.stdout.strip())
        except ValueError:
            keep_review_id = None
        published_any = True
    else:
        review_error = proc.stderr.strip() or proc.stdout.strip() or f"gh api exited {proc.returncode}"
        # Fall back to a plain issue comment if a formal review can't be posted
        # (e.g. the PR author cannot request changes on their own PR).
        print(f"Could not post review ({review_error}); posting comment instead.")
        comment_proc = run_post_with_retry(
            [
                "gh",
                "api",
                f"repos/{repo}/issues/{pr_number}/comments",
                "--method",
                "POST",
                "--input",
                "-",
                "--jq",
                ".node_id",
            ],
            {"body": body},
            "posting fallback issue comment",
        )
        if comment_proc.returncode == 0:
            keep_comment_id = comment_proc.stdout.strip() or None
            published_any = True
        else:
            comment_error = (
                comment_proc.stderr.strip()
                or comment_proc.stdout.strip()
                or f"gh api exited {comment_proc.returncode}"
            )
            print(f"Failed to post fallback comment: {comment_error}")

    if not published_any:
        raise RuntimeError(
            "Could not publish AI review artifact. "
            f"review_error={review_error!r}; fallback_error={comment_error!r}"
        )

    # Keep only the newest bot review; if the current post failed and the verdict
    # is BLOCK, preserve the old blocking review instead of accidentally unblocking.
    if keep_review_id is not None or verdict != "BLOCK":
        try:
            dismiss_previous_bot_reviews(repo, pr_number, keep_review_id=keep_review_id)
        except Exception as err:  # noqa: BLE001
            # Dismissal is best-effort and must never fail an otherwise-passing run.
            print(f"Skipping stale-review dismissal due to error: {err}")

    # Collapse stale AI-review issue comments (the fallback path) so only the
    # latest AI-review artifact stays visible. When a formal review was posted,
    # keep_comment_id is None so every prior AI-review comment is hidden.
    try:
        hide_previous_bot_comments(repo, pr_number, keep_comment_id=keep_comment_id)
    except Exception as err:  # noqa: BLE001
        # Best-effort: must never fail an otherwise-passing run.
        print(f"Skipping stale-comment hiding due to error: {err}")

def main() -> int:
    metadata = load_pr_metadata()
    pr_title = metadata["title"]
    pr_body = metadata["body"]
    pr_base_ref = metadata["base_ref"]
    pr_head_sha = metadata["head_sha"]

    sync_pr_metadata_env(metadata)

    ensure_diff_file(metadata["base_ref"], metadata["base_sha"], metadata["head_sha"])

    format_ok, format_review = validate_pr_description_format(pr_body)
    if not format_ok:
        print("Verdict: BLOCK")
        print("----- Review -----")
        print(format_review)
        post_review(format_review, "BLOCK")
        return 1

    language_ok, language_review = validate_pr_metadata_language(pr_title, pr_body)
    if not language_ok:
        print("Verdict: BLOCK")
        print("----- Review -----")
        print(language_review)
        post_review(language_review, "BLOCK")
        return 1

    branch_ok, branch_review = validate_branch_is_based_on_latest_base(pr_base_ref, pr_head_sha)
    if not branch_ok:
        print("Verdict: BLOCK")
        print("----- Review -----")
        print(branch_review)
        post_review(branch_review, "BLOCK")
        return 1

    diff = read_diff()
    if not diff.strip():
        print("Empty diff — nothing to review.")
        return 0

    raw_diff = read_raw_diff()
    manifest = build_change_manifest(raw_diff)

    batches = build_diff_batches(diff)
    if not batches and not manifest:
        review = (
            "## Summary\n"
            "This PR only changes the AI review infrastructure itself, so the reviewer\n"
            "skips reviewing its own implementation to avoid self-blocking.\n\n"
            "## Findings\n"
            "No issues found.\n\n"
            "VERDICT: APPROVE"
        )
        print("No reviewable files in diff; posting APPROVE for review-infra-only change.")
        print(review)
        post_review(review, "APPROVE")
        return 0
    batch_reviews: list[tuple[int, int, str, str]] = []

    # PR-level structural / consistency review (description match, unexplained new
    # files, naming consistency, missing tests, missing docs). Best-effort: a
    # failure here must never abort the line-level review below. This still runs
    # when there are no line-reviewable code batches (e.g. a PR that ONLY adds
    # stray doc/data files) so such junk-only changes are still BLOCKed.
    structural: tuple[str, str] | None = None
    policy_findings: list[str] = []
    core_algo_test_gap = detect_core_algorithm_test_gap(raw_diff, pr_body=pr_body)
    if core_algo_test_gap:
        print("Policy check: core algorithm test gap detected (forcing BLOCK).")
        policy_findings.append(core_algo_test_gap)
    suspicious_files = detect_suspicious_added_files(raw_diff)
    if suspicious_files:
        print("Policy check: suspicious/unexplained added files detected (forcing BLOCK).")
        policy_findings.append(suspicious_files)
    format_only_code_changes = detect_unexplained_format_only_code_changes(
        raw_diff,
        pr_title,
        pr_body,
    )
    if format_only_code_changes:
        print("Policy check: unexplained formatting-only code changes detected (forcing BLOCK).")
        policy_findings.append(format_only_code_changes)
    if manifest:
        try:
            structural_review = call_structural_model(
                format_change_manifest(manifest),
                format_new_identifiers(extract_new_identifiers(raw_diff)),
                filtered_review_diff(diff, STRUCTURAL_DIFF_CHARS),
            )
            structural_verdict = parse_verdict(structural_review)
            print(f"Structural review verdict: {structural_verdict}")
            structural = (structural_verdict, structural_review)
        except Exception as err:  # noqa: BLE001
            print(f"Structural review skipped due to error: {err}")

    for index, batch in enumerate(batches, start=1):
        try:
            review = call_model(batch, index, len(batches))
        except urllib.error.HTTPError as err:
            print(f"GitHub Models request failed on batch {index}/{len(batches)}: {err.code} {err.read().decode('utf-8', 'replace')}")
            return 1
        except Exception as err:  # noqa: BLE001
            print(f"Review generation failed on batch {index}/{len(batches)}: {err}")
            return 1

        verdict = parse_verdict(review)
        print(f"Batch {index}/{len(batches)} verdict: {verdict}")
        batch_reviews.append((index, len(batches), verdict, review))

    review = combine_batch_reviews(
        batch_reviews,
        structural=structural,
        policy_findings=policy_findings,
        pr_body=pr_body,
    )
    verdict = parse_verdict(review)

    print(f"Verdict: {verdict}")
    print("----- Review -----")
    print(review)

    post_review(review, verdict)

    return 1 if verdict == "BLOCK" else 0


if __name__ == "__main__":
    sys.exit(main())
