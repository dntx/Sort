#!/usr/bin/env python3
"""AI-powered pull request reviewer backed by GitHub Models.

Reads a unified diff, asks a GitHub Models chat model to review it, posts the
review back onto the pull request, and exits non-zero when the model reports a
blocking (serious) issue so the required status check fails.
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

SYSTEM_PROMPT = """\
You are a meticulous senior software engineer performing a code review on a
GitHub pull request for a C#/.NET (WinForms) project. Review ONLY the provided
diff. Focus on correctness, potential bugs, security issues, resource/memory
leaks, concurrency problems, broken edge cases, and clear regressions.

Ignore pure style/formatting nits unless they cause real problems. Be concise
and specific, referencing file names and code where possible.

Classify the most severe problem you find using exactly one of these severities:
- BLOCK: a serious defect that must be fixed before merging (bug, data loss,
  security hole, crash, broken logic, clear regression).
- COMMENT: only minor or non-blocking suggestions/improvements.
- APPROVE: no meaningful issues found.

Respond in GitHub-flavored Markdown with these sections:
## Summary
A short paragraph describing what the change does.

## Findings
A bulleted list of issues. Prefix each with its severity in bold, e.g.
**[BLOCK]**, **[COMMENT]**. If there are none, write "No issues found."

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


def call_model(diff: str) -> str:
    token = os.environ["GITHUB_TOKEN"]
    pr_title = os.environ.get("PR_TITLE", "")
    pr_body = os.environ.get("PR_BODY", "")

    user_content = (
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

    try:
        review = call_model(diff)
    except urllib.error.HTTPError as err:
        print(f"GitHub Models request failed: {err.code} {err.read().decode('utf-8', 'replace')}")
        return 0  # Don't hard-fail the PR on a transient inference error.
    except Exception as err:  # noqa: BLE001
        print(f"Review generation failed: {err}")
        return 0

    verdict = parse_verdict(review)
    print(f"Verdict: {verdict}")
    print("----- Review -----")
    print(review)

    post_review(review, verdict)

    return 1 if verdict == "BLOCK" else 0


if __name__ == "__main__":
    sys.exit(main())
