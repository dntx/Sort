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
import time
import urllib.error
import urllib.request
import unicodedata

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

Focus ONLY on the following five structural concerns. Do not repeat line-level
correctness/security findings — a separate reviewer handles those.

1. DESCRIPTION ↔ CHANGE CONSISTENCY
   Does the actual diff match what the PR title/description claims? Flag when the
   description promises something the diff does not do, the diff does something
   material the description never mentions, or the description is empty/generic
   while the change is non-trivial.

2. UNEXPLAINED NEW FILES
   For every file whose status is `added`, is its purpose explained by the PR
   description (or made obvious by an accompanying test/doc)? Flag brand-new
   source files whose role is unclear and that the description does not mention.

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

4. MISSING TEST COVERAGE
   If the PR adds or materially changes production code (new functions, new
   branches, new modes/flags, new behavior) but the manifest shows NO test files
   changed, flag the missing tests. Use the manifest's "Test files changed"
   fact — do not guess.

5. MISSING DOCUMENTATION UPDATES
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
- If the manifest already shows a test file changed, DO NOT flag missing tests.
- If the manifest already shows a doc/README/*.md file changed, DO NOT flag
  missing docs.
- Pure refactors, pure formatting, pure dependency bumps, and comment-only
  changes need neither new tests nor doc updates — do not flag them.
- Changes that ONLY touch test files or ONLY touch docs need no extra tests/docs.
- Trivial production changes (a guard clause, a log line, a config value, a typo
  fix, < ~10 changed lines with no new branch) do not by themselves require new
  tests.
- Naming: only flag a NEW identifier that clearly joins an EXISTING set with a
  visible, established convention. Do not invent conventions or bikeshed style
  where no sibling pattern is shown.
- New files: a new test file, doc file, or an obviously-named file matching the
  described feature is self-explanatory — do not flag it.
- Do not flag the AI review infrastructure itself.

## Severities
Each of the five structural concerns above is a BLOCK-worthy hygiene gate: the
change set must not merge while any of them is genuinely violated. Raise a
finding as BLOCK when you can concretely demonstrate the violation from the
manifest, diff, title, or description:
- DESCRIPTION ↔ CHANGE mismatch: the diff does something material the
  description omits or contradicts, or the description is empty/generic for a
  non-trivial change. → BLOCK
- UNEXPLAINED NEW FILES: an `added` source file whose purpose the description
  does not explain and that no accompanying test/doc makes obvious. → BLOCK
- NAMING INCONSISTENCY: a new user-facing identifier that breaks the
  established convention of its existing siblings (e.g. `three-phase` joining
  `exact`/`greedy`). → BLOCK
- MISSING TESTS: production code added or materially changed but the manifest
  shows NO test files changed. → BLOCK
- MISSING DOCS: user-facing behavior added or changed but the manifest shows NO
  doc files changed. → BLOCK
- COMMENT: reserve for genuinely optional polish that does NOT fall into any of
  the five gates above.
- APPROVE: the change set is internally consistent and adequately covered — no
  gate is violated.

Only downgrade a gate from BLOCK to COMMENT (or drop it) when the precision
rules above void it — for example the manifest already shows a test/doc file
changed, the change is a pure refactor/formatting/comment-only edit, or it is a
trivial (<~10-line, no new branch) production tweak. When a gate genuinely
applies, it is a BLOCK, not advisory.

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
        if section_path(section) not in EXCLUDED_REVIEW_PATHS
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
        if section_path(section) in EXCLUDED_REVIEW_PATHS:
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



def filtered_review_diff(diff: str, max_chars: int = MAX_DIFF_CHARS) -> str:
    """Concatenate reviewable (non-infra) diff sections, truncated for the model."""
    sections = [
        section
        for section in split_diff_sections(diff)
        if section_path(section) not in EXCLUDED_REVIEW_PATHS
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
) -> str:
    """Merge the structural review and per-batch reviews into ONE consolidated
    review body. Batches are an internal chunking detail and are never exposed:
    findings are integrated into a single list, deduplicated across batches."""
    verdict_order = {"APPROVE": 0, "COMMENT": 1, "BLOCK": 2}
    all_verdicts = [verdict for _, _, verdict, _ in batch_reviews]
    if structural is not None:
        all_verdicts.append(structural[0])
    final_verdict = max(all_verdicts, key=lambda v: verdict_order[v]) if all_verdicts else "APPROVE"

    # Structural (PR-level) summary + findings.
    struct_summary, struct_findings = ("", "")
    if structural is not None:
        struct_summary, struct_findings = _split_summary_findings(structural[1])
    struct_bullets = [b for b in _parse_bullets(struct_findings) if not _is_noise_bullet(b)]

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
        [("structure", b) for b in struct_bullets]
        + [("code", b) for b in code_bullets]
    )
    blocking = [(src, b) for src, b in tagged if _bullet_severity(b) == "BLOCK"]
    comments = [(src, b) for src, b in tagged if _bullet_severity(b) != "BLOCK"]

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
    proc = subprocess.run(
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
        input=json.dumps(payload),
        text=True,
        capture_output=True,
    )

    keep_review_id: int | None = None
    keep_comment_id: str | None = None
    if proc.returncode == 0:
        try:
            keep_review_id = int(proc.stdout.strip())
        except ValueError:
            keep_review_id = None
    else:
        # Fall back to a plain issue comment if a formal review can't be posted
        # (e.g. the PR author cannot request changes on their own PR).
        print(f"Could not post review ({proc.stderr.strip()}); posting comment instead.")
        comment_proc = subprocess.run(
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
            input=json.dumps({"body": body}),
            text=True,
            capture_output=True,
        )
        if comment_proc.returncode == 0:
            keep_comment_id = comment_proc.stdout.strip() or None
        else:
            print(f"Failed to post fallback comment: {comment_proc.stderr.strip()}")

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
    pr_title = os.environ.get("PR_TITLE", "")
    pr_body = os.environ.get("PR_BODY", "")

    language_ok, language_review = validate_pr_metadata_language(pr_title, pr_body)
    if not language_ok:
        print("Verdict: BLOCK")
        print("----- Review -----")
        print(language_review)
        post_review(language_review, "BLOCK")
        return 1

    diff = read_diff()
    if not diff.strip():
        print("Empty diff — nothing to review.")
        return 0

    batches = build_diff_batches(diff)
    if not batches:
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
    # failure here must never abort the line-level review below.
    structural: tuple[str, str] | None = None
    raw_diff = read_raw_diff()
    manifest = build_change_manifest(raw_diff)
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

    review = combine_batch_reviews(batch_reviews, structural=structural)
    verdict_order = {"APPROVE": 0, "COMMENT": 1, "BLOCK": 2}
    candidate_verdicts = [item[2] for item in batch_reviews]
    if structural is not None:
        candidate_verdicts.append(structural[0])
    verdict = max(candidate_verdicts, key=lambda v: verdict_order[v])

    print(f"Verdict: {verdict}")
    print("----- Review -----")
    print(review)

    post_review(review, verdict)

    return 1 if verdict == "BLOCK" else 0


if __name__ == "__main__":
    sys.exit(main())
