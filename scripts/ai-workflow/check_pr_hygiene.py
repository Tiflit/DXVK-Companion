"""Checks pull request description metadata against hygiene and task requirements."""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import List, Optional

try:
    from .parse_contract import ContractParseError, extract_primary_issue
except ImportError:
    from parse_contract import ContractParseError, extract_primary_issue


REQUIRED_HEADINGS = [
    "Summary",
    "Scope",
    "Verification",
    "Documentation",
]


@dataclass
class HygieneResult:
    is_valid: bool
    missing_sections: List[str]
    primary_issue: Optional[int]
    diagnostics: List[str]


def verify_pr_body(body: str) -> HygieneResult:
    """Verifies that the PR body contains required sections and an unambiguous primary issue reference."""
    missing: List[str] = []
    diagnostics: List[str] = []

    if not body or not body.strip():
        return HygieneResult(
            is_valid=False,
            missing_sections=REQUIRED_HEADINGS + ["Primary Issue"],
            primary_issue=None,
            diagnostics=["PR body is empty."],
        )

    # Check for required section headings
    for heading in REQUIRED_HEADINGS:
        pattern = rf"^##\s+{re.escape(heading)}\s*$"
        if not re.search(pattern, body, re.MULTILINE | re.IGNORECASE):
            missing.append(heading)
            diagnostics.append(f"Missing required section heading '## {heading}'.")

    # Check for primary issue reference
    primary_issue: Optional[int] = None
    try:
        primary_issue = extract_primary_issue(body)
    except ContractParseError as e:
        missing.append("Primary Issue")
        diagnostics.append(f"Primary issue error: {e}")

    return HygieneResult(
        is_valid=(len(missing) == 0),
        missing_sections=missing,
        primary_issue=primary_issue,
        diagnostics=diagnostics,
    )


def fetch_github_api(url: str, token: str) -> any:
    req = urllib.request.Request(
        url,
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github.v3+json",
            "User-Agent": "DXVK-Companion-AI-Workflow",
        },
    )
    with urllib.request.urlopen(req) as resp:
        return json.loads(resp.read().decode("utf-8"))


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify PR metadata against hygiene contract.")
    parser.add_argument("--pr-number", type=int, default=int(os.environ.get("PR_NUMBER", 0)) if os.environ.get("PR_NUMBER") else None)
    parser.add_argument("--pr-body-file", type=Path, help="Path to file containing PR body text.")
    args = parser.parse_args()

    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    repo = os.environ.get("GH_REPO") or os.environ.get("GITHUB_REPOSITORY")

    body = ""
    if args.pr_number:
        if not token or not repo:
            print("ERROR: GH_TOKEN and GH_REPO must be set when --pr-number is specified.", file=sys.stderr)
            return 1
        try:
            pr_data = fetch_github_api(f"https://api.github.com/repos/{repo}/pulls/{args.pr_number}", token)
            body = pr_data.get("body", "")
        except Exception as e:
            print(f"ERROR: Could not fetch PR #{args.pr_number} metadata: {e}", file=sys.stderr)
            return 1
    elif args.pr_body_file and args.pr_body_file.exists():
        body = args.pr_body_file.read_text(encoding="utf-8")
    else:
        print("ERROR: Neither --pr-number nor a valid --pr-body-file was provided.", file=sys.stderr)
        return 1

    result = verify_pr_body(body)

    summary_file = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_file:
        with open(summary_file, "a", encoding="utf-8") as f:
            f.write("### AI PR Hygiene Check\n\n")
            if result.is_valid:
                f.write(f"- PASS: All required sections present.\n")
                f.write(f"- Primary task Issue: #{result.primary_issue}\n")
            else:
                f.write(f"- FAIL: One or more required contract elements are missing:\n")
                for diag in result.diagnostics:
                    f.write(f"  - {diag}\n")
                f.write("\nRequired template sections: `## Primary Issue`, `## Summary`, `## Scope`, `## Verification`, `## Documentation`.\n")

    if result.is_valid:
        print(f"PASS: PR metadata valid. Primary issue: #{result.primary_issue}")
        return 0
    else:
        print("FAIL: PR metadata hygiene check failed:", file=sys.stderr)
        for diag in result.diagnostics:
            print(f"  - {diag}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
