#!/usr/bin/env python3
"""AI-powered pull request reviewer backed by GitHub Models.

Reads a unified diff, asks a GitHub Models chat model to review it, posts the
review back onto the pull request, and exits non-zero when the model reports a
blocking (serious) issue so the required status check fails. Blocking findings
are held to a high bar to keep false positives low.
"""

import json
import os
import subprocess
import sys
import urllib.error
import urllib.request

MODELS_ENDPOINT = "https://models.github.ai/inference/chat/completions"
MODEL = os.environ.get("REVIEW_MODEL", "openai/gpt-4o")
MAX_DIFF_CHARS = 60000
MAX_BATCH_CHARS = 5000

SYSTEM_PROMPT = """\
You are a meticulous senior software engineer reviewing a GitHub pull request
for a C#/.NET 8 (WinForms) project. You are given the unified diff plus the PR
title and description. Review only what is shown and do not guess about code
you cannot see.

## Your prime directive: precision over recall
A false BLOCK is far more costly than a missed nit. Only raise a BLOCK when you
can point to the exact lines that prove the defect. If a concern depends on code
you cannot see or on an assumption you cannot verify from the provided files,
DO NOT raise it as a BLOCK — at most mention it as a COMMENT phrased as a
question, or omit it.

## Before writing any BLOCK finding, self-check ALL of these. If any fails, downgrade to COMMENT or drop it:
1. Verify, don't assume. Confirm the claim against the full file content
   provided — do not speculate about initialization, disposal, null-ness,
   or data structures you cannot see. If a field/variable you're worried about
   is initialized or handled elsewhere in the provided file, the concern is void.
2. Respect the language & runtime facts:
   - C# value types (struct) such as `Color`, `int`, `Rectangle`, `Size`,
     `DateTime`, and any `enum` are NEVER null. Do not claim NullReferenceException
     on them.
   - WinForms UI event handlers (paint/draw, click, form events) run on the
     single UI thread. Do NOT invent multi-threaded race conditions or
     concurrent-dictionary problems unless the diff clearly starts a thread,
     Task, timer callback, or async continuation touching shared state.
   - `int.MaxValue` as a width in `TextRenderer.DrawText`/measure calls is an
     idiomatic "don't wrap/clip" sentinel, not a bug.
   - A field initialized inline (e.g. `private X _f = new(...)`) is never null.
   - Shared/cached brushes/pens/fonts reused across paints are intentional and
     correct; only flag GDI leaks when an object is allocated per-call and never
     disposed.
3. Real, not hypothetical. "Could lead to…", "may become a bottleneck",
   "consider using ReadOnlySpan for fewer allocations" are NOT blocking. Micro-
   performance and style preferences are never BLOCK, and usually not worth a
   COMMENT either.
4. In scope. Only judge what this diff changes. Pre-existing patterns the PR
   didn't touch are out of scope.

## Severities (pick the single most severe real issue for the verdict):
- BLOCK: a concrete, verifiable defect introduced by this change that will cause
  wrong behavior, a crash, data loss, or a security hole — provable from the
  shown lines. Examples: dereferencing a reference type that is provably null
  here, an inverted condition, an unawaited async call losing exceptions, a
  resource allocated per-call and never disposed, an off-by-one over a shown
  bound, SQL/OS command injection from untrusted input.
- COMMENT: legitimate, non-blocking suggestions (missing tests for non-trivial
  new logic, unclear naming with real ambiguity, a genuine edge case worth a
  second look). Keep these few and high-value.
- APPROVE: no meaningful issues.

Ignore pure style/formatting. Be concise and reference file + code.

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
    sections = split_diff_sections(diff)
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
    return batches or [diff]


def call_model(diff: str, batch_index: int, batch_total: int) -> str:
    token = os.environ["GITHUB_TOKEN"]
    pr_title = os.environ.get("PR_TITLE", "")
    pr_body = os.environ.get("PR_BODY", "")

    user_content = (
        f"Batch {batch_index} of {batch_total}\n\n"
        f"Pull request title: {pr_title}\n\n"
        f"Pull request description:\n{pr_body or '(none)'}\n\n"
        f"Here is the unified diff to review:\n\n```diff\n{diff}\n```"
    )

    payload = json.dumps(
        {
            "model": MODEL,
            "temperature": 0.1,
            "messages": [
                {"role": "system", "content": SYSTEM_PROMPT},
                {"role": "user", "content": user_content},
            ],
        }
    ).encode("utf-8")

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


def combine_batch_reviews(batch_reviews: list[tuple[int, int, str, str]]) -> str:
    """Combine per-batch reviews into a single PR review body."""
    verdict_order = {"APPROVE": 0, "COMMENT": 1, "BLOCK": 2}
    final_verdict = max((verdict for _, _, verdict, _ in batch_reviews), key=lambda v: verdict_order[v])

    summary_lines = [
        f"Reviewed in {len(batch_reviews)} batch(es).",
        f"Final verdict: {final_verdict}.",
    ]
    if len(batch_reviews) > 1:
        counts = {key: 0 for key in verdict_order}
        for _, _, verdict, _ in batch_reviews:
            counts[verdict] += 1
        summary_lines.append(
            "Batch verdicts: "
            + ", ".join(f"{name}={counts[name]}" for name in ("BLOCK", "COMMENT", "APPROVE"))
            + "."
        )

    parts = ["## Summary\n" + " ".join(summary_lines), "", "## Findings"]
    for index, total, verdict, review in batch_reviews:
        parts.append(f"### Batch {index}/{total} — {verdict}")
        parts.append(strip_verdict_line(review))
        parts.append("")

    parts.append(f"VERDICT: {final_verdict}")
    return "\n".join(parts).strip()


BOT_LOGIN = "github-actions[bot]"


def dismiss_stale_change_requests(repo: str, pr_number: str) -> None:
    """Dismiss this bot's previous CHANGES_REQUESTED reviews.

    When an earlier run blocked the PR and a later push resolved the issues,
    the stale REQUEST_CHANGES review would otherwise keep blocking the merge.
    """
    proc = subprocess.run(
        ["gh", "api", f"repos/{repo}/pulls/{pr_number}/reviews", "--paginate"],
        text=True,
        capture_output=True,
    )
    if proc.returncode != 0:
        print(f"Could not list reviews to dismiss stale change-requests: {proc.stderr.strip()}")
        return

    try:
        reviews = json.loads(proc.stdout)
    except json.JSONDecodeError:
        # --paginate can concatenate multiple JSON arrays; merge them.
        reviews = []
        for chunk in proc.stdout.replace("][", "]\n[").splitlines():
            chunk = chunk.strip()
            if chunk:
                reviews.extend(json.loads(chunk))

    for review in reviews:
        user = (review.get("user") or {}).get("login")
        if user == BOT_LOGIN and review.get("state") == "CHANGES_REQUESTED":
            review_id = review["id"]
            dismiss = subprocess.run(
                [
                    "gh",
                    "api",
                    f"repos/{repo}/pulls/{pr_number}/reviews/{review_id}/dismissals",
                    "--method",
                    "PUT",
                    "--input",
                    "-",
                ],
                input=json.dumps(
                    {
                        "message": "Resolved in a newer revision — dismissed by automated review.",
                        "event": "DISMISS",
                    }
                ),
                text=True,
                capture_output=True,
            )
            if dismiss.returncode == 0:
                print(f"Dismissed stale CHANGES_REQUESTED review {review_id}.")
            else:
                print(f"Failed to dismiss review {review_id}: {dismiss.stderr.strip()}")


def post_review(review_body: str, verdict: str) -> None:
    repo = os.environ["GITHUB_REPOSITORY"]
    pr_number = os.environ["PR_NUMBER"]

    header = {
        "BLOCK": "## ⛔ AI Code Review — Changes requested\n\n"
        "This review found a serious issue that blocks merging.\n\n",
        "COMMENT": "## 💬 AI Code Review\n\n",
        "APPROVE": "## ✅ AI Code Review — Looks good\n\n",
    }[verdict]

    body = header + review_body + "\n\n*🤖 Automated review via GitHub Models.*"

    event = "REQUEST_CHANGES" if verdict == "BLOCK" else "COMMENT"

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
        ],
        input=json.dumps(payload),
        text=True,
        capture_output=True,
    )
    if proc.returncode != 0:
        # Fall back to a plain issue comment if a formal review can't be posted
        # (e.g. the PR author cannot request changes on their own PR).
        print(f"Could not post review ({proc.stderr.strip()}); posting comment instead.")
        subprocess.run(
            [
                "gh",
                "api",
                f"repos/{repo}/issues/{pr_number}/comments",
                "--method",
                "POST",
                "--input",
                "-",
            ],
            input=json.dumps({"body": body}),
            text=True,
            check=True,
        )

    if verdict != "BLOCK":
        try:
            dismiss_stale_change_requests(repo, pr_number)
        except Exception as err:  # noqa: BLE001
            # Dismissal is best-effort and must never fail an otherwise-passing run.
            print(f"Skipping stale-review dismissal due to error: {err}")


def main() -> int:
    diff = read_diff()
    if not diff.strip():
        print("Empty diff — nothing to review.")
        return 0

    batches = build_diff_batches(diff)
    batch_reviews: list[tuple[int, int, str, str]] = []

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

    review = combine_batch_reviews(batch_reviews)
    verdict_order = {"APPROVE": 0, "COMMENT": 1, "BLOCK": 2}
    verdict = max((item[2] for item in batch_reviews), key=lambda v: verdict_order[v])

    print(f"Verdict: {verdict}")
    print("----- Review -----")
    print(review)

    post_review(review, verdict)

    return 1 if verdict == "BLOCK" else 0


if __name__ == "__main__":
    sys.exit(main())
