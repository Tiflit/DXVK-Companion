"""Evaluates changed pull request files against Issue allowed paths contracts."""

from __future__ import annotations

import argparse
import json
import os
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional

# Import contract parser from same directory
try:
    from .parse_contract import AllowedPaths, ContractParseError, extract_primary_issue, parse_allowed_paths
except ImportError:
    from parse_contract import AllowedPaths, ContractParseError, extract_primary_issue, parse_allowed_paths


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


def main() -> int:
    parser = argparse.ArgumentParser(description="Evaluate PR changed files against Issue scope contract.")
    parser.add_argument("--pr-body-file", type=Path, help="Path to file containing PR body text.")
    parser.add_argument("--issue-body-file", type=Path, help="Path to file containing Issue body text.")
    parser.add_argument("--files-json", type=Path, help="Path to JSON file containing PR changed files list.")
    args = parser.parse_args()

    # Read issue body
    issue_body = ""
    if args.issue_body_file and args.issue_body_file.exists():
        issue_body = args.issue_body_file.read_text(encoding="utf-8")
    else:
        print("ERROR: Issue body file not provided or does not exist.", file=sys.stderr)
        return 1

    try:
        allowed = parse_allowed_paths(issue_body)
    except ContractParseError as e:
        print(f"ERROR: Contract parse error: {e}", file=sys.stderr)
        return 1

    # Read files list
    files_data = []
    if args.files_json and args.files_json.exists():
        with open(args.files_json, "r", encoding="utf-8") as f:
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
