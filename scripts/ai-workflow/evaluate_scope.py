"""Evaluates changed pull request files against Issue allowed paths contracts.

Includes a narrow automated guardrail in live GitHub API mode: primary task Issues
carrying the exact label 'ai-observation' are rejected before parsing allowed paths,
preventing implementation of unassigned observations even if the issue body contains
otherwise valid paths.

Note: The label guard is a narrow automated guardrail, not proof of assignment/approval
or a replacement for human governance. Local / file-based mode operates on issue body
text only and does not acquire or claim label metadata.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional

# Import contract parser from same directory
try:
    from .parse_contract import AllowedPaths, ContractParseError, extract_primary_issue, parse_allowed_paths
except ImportError:
    from parse_contract import AllowedPaths, ContractParseError, extract_primary_issue, parse_allowed_paths


def normalize_allowed_paths_section(text: str) -> str:
    """Normalizes Issue body so subsequent markdown headings (# or ##) act as section delimiters for parse_allowed_paths."""
    if not text:
        return text
    pattern = r"(^###\s+Allowed paths\s*[\r\n]+[\s\S]*?)(?=^#{1,2}\s+)"
    return re.sub(pattern, r"\1\n### Section Boundary\n", text, count=1, flags=re.MULTILINE | re.IGNORECASE)


@dataclass
class ScopeResult:
    is_valid: bool
    violations: List[str]
    allowed_patterns: List[str]
    is_unconstrained: bool = False


def matches_pattern(file_path: str, pattern: str) -> bool:
    """Matches a normalized repository path against an allowed pattern."""
    norm_file = file_path.replace("\\", "/").strip("/")
    norm_pattern = pattern.replace("\\", "/").strip()

    if norm_pattern.endswith("/**"):
        prefix = norm_pattern[:-3]
        if not prefix.endswith("/"):
            prefix += "/"
        return norm_file.startswith(prefix)

    if norm_pattern.endswith("/"):
        return norm_file.startswith(norm_pattern)

    # Exact match
    return norm_file == norm_pattern.strip("/")


def check_files(
    files: List[Dict[str, any]],
    patterns: List[str],
    is_unconstrained: bool = False,
) -> ScopeResult:
    """
    Checks changed files against allowed patterns.

    Renamed files check BOTH previous_filename and filename.
    """
    if is_unconstrained:
        return ScopeResult(is_valid=True, violations=[], allowed_patterns=[], is_unconstrained=True)

    violations: List[str] = []

    for file_info in files:
        filename = file_info.get("filename", "")
        previous_filename = file_info.get("previous_filename")

        # Check target filename
        if filename:
            if not any(matches_pattern(filename, p) for p in patterns):
                violations.append(filename)

        # Check previous filename for renames
        if previous_filename:
            if not any(matches_pattern(previous_filename, p) for p in patterns):
                rename_viol = f"{previous_filename} (renamed to {filename})"
                if rename_viol not in violations and previous_filename not in violations:
                    violations.append(rename_viol)

    return ScopeResult(
        is_valid=(len(violations) == 0),
        violations=violations,
        allowed_patterns=patterns,
        is_unconstrained=False,
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


def fetch_pr_files(repo: str, pr_number: int, token: str) -> List[Dict[str, any]]:
    files = []
    page = 1
    while True:
        url = f"https://api.github.com/repos/{repo}/pulls/{pr_number}/files?per_page=100&page={page}"
        batch = fetch_github_api(url, token)
        if not batch:
            break
        files.extend(batch)
        if len(batch) < 100:
            break
        page += 1
    return files


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Evaluate PR changed files against Issue scope contract.")
    parser.add_argument("--pr-number", type=int, default=int(os.environ.get("PR_NUMBER", 0)) if os.environ.get("PR_NUMBER") else None)
    parser.add_argument("--pr-body-file", type=Path, help="Path to file containing PR body text.")
    parser.add_argument("--issue-body-file", type=Path, help="Path to file containing Issue body text.")
    parser.add_argument("--files-json", type=Path, help="Path to JSON file containing PR changed files list.")
    args = parser.parse_args(argv)

    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    repo = os.environ.get("GH_REPO") or os.environ.get("GITHUB_REPOSITORY")

    summary_file = os.environ.get("GITHUB_STEP_SUMMARY")

    def write_summary(text: str) -> None:
        if summary_file:
            with open(summary_file, "a", encoding="utf-8") as sf:
                sf.write(text)

    # 1. Direct GitHub API mode
    if args.pr_number:
        if not token or not repo:
            print("ERROR: GH_TOKEN and GH_REPO must be set when --pr-number is specified.", file=sys.stderr)
            return 1
        try:
            pr_data = fetch_github_api(f"https://api.github.com/repos/{repo}/pulls/{args.pr_number}", token)
            pr_body = pr_data.get("body", "")
            primary_issue_num = extract_primary_issue(pr_body)
            issue_data = fetch_github_api(f"https://api.github.com/repos/{repo}/issues/{primary_issue_num}", token)

            # Automated Scope Guard: reject unassigned observations carrying 'ai-observation' label
            # before parsing allowed paths (even if the body contains valid paths).
            labels = [
                lbl.get("name") if isinstance(lbl, dict) else str(lbl)
                for lbl in issue_data.get("labels", [])
            ]
            if "ai-observation" in labels:
                msg = (
                    f"ERROR: Primary Issue #{primary_issue_num} carries label 'ai-observation'. "
                    "Observations are unassigned and implementation is not authorized."
                )
                print(msg, file=sys.stderr)
                write_summary("### AI Scope Check\n\n")
                write_summary(f"- Linked Issue: #{primary_issue_num}\n")
                write_summary("- Status: FAIL (Primary Issue carries 'ai-observation' label; implementation not authorized)\n")
                return 1

            allowed = parse_allowed_paths(normalize_allowed_paths_section(issue_data.get("body", "")))
            files_data = fetch_pr_files(repo, args.pr_number, token)
        except Exception as e:
            msg = f"ERROR evaluating scope for PR #{args.pr_number}: {e}"
            print(msg, file=sys.stderr)
            write_summary(f"### AI Scope Check\n\n- FAIL: {e}\n")
            return 1

        result = check_files(files_data, allowed.patterns, allowed.is_unconstrained)

        write_summary("### AI Scope Check\n\n")
        write_summary(f"- Linked Issue: #{primary_issue_num}\n")
        write_summary(f"- Total changed files: {len(files_data)}\n")

        if result.is_unconstrained:
            write_summary("- Status: PASS (Scope is explicitly Unconstrained)\n")
            print("INFO: Task scope is explicitly Unconstrained. All changes permitted.")
            return 0

        if result.is_valid:
            write_summary("- Status: PASS (All changed files within allowed scope)\n")
            print(f"PASS: All {len(files_data)} changed files match the allowed scope.")
            return 0
        else:
            write_summary("- Status: FAIL (Changed files outside allowed scope)\n\nViolations:\n")
            for v in result.violations:
                write_summary(f"  - `{v}`\n")
            print("FAIL: The following files violate the task scope contract:", file=sys.stderr)
            for v in result.violations:
                print(f"  - {v}", file=sys.stderr)
            return 1

    # 2. Local / file-based mode
    issue_body = ""
    if args.issue_body_file and args.issue_body_file.exists():
        issue_body = args.issue_body_file.read_text(encoding="utf-8-sig")
    else:
        print("ERROR: Neither GitHub API context nor --issue-body-file was provided.", file=sys.stderr)
        return 1

    try:
        allowed = parse_allowed_paths(normalize_allowed_paths_section(issue_body))
    except ContractParseError as e:
        print(f"ERROR: Contract parse error: {e}", file=sys.stderr)
        return 1

    files_data = []
    if args.files_json and args.files_json.exists():
        with open(args.files_json, "r", encoding="utf-8-sig") as f:
            files_data = json.load(f)

    else:
        print("ERROR: Files JSON file not provided or does not exist.", file=sys.stderr)
        return 1

    result = check_files(files_data, allowed.patterns, allowed.is_unconstrained)

    if result.is_unconstrained:
        print("INFO: Task scope is explicitly Unconstrained. All changes permitted.")
        return 0

    if result.is_valid:
        print(f"PASS: All {len(files_data)} changed files match the allowed scope.")
        return 0
    else:
        print("FAIL: The following files violate the task scope contract:", file=sys.stderr)
        for v in result.violations:
            print(f"  - {v}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
