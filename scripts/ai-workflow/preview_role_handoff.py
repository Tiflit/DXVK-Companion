#!/usr/bin/env python3
"""Compact, read-only role handoff preview generator from existing handoff JSON."""

from __future__ import annotations

import sys
sys.dont_write_bytecode = True

import argparse
import json
import re
from pathlib import Path
from typing import Any, Dict, List, Optional


HEX_SHA_REGEX = re.compile(r"\A[0-9a-fA-F]{40}\Z")
REPO_PART_REGEX = re.compile(r"\A[a-zA-Z0-9_.-]+\Z")

VALID_ROLES = ("implementer", "coordinator", "reviewer")

ROLE_CONFIGS = {
    "implementer": {
        "title": "Implementer",
        "next_step": "Implement scoped changes strictly within assigned allowed paths under current explicit assignment.",
        "stop_boundary": "Commit and push scoped changes, refresh guarded PR metadata, inspect CI once, and yield without local test execution or merge.",
    },
    "coordinator": {
        "title": "Coordinator",
        "next_step": "Check revision-bound evidence, checkout provenance, and workflow CI status.",
        "stop_boundary": "Prepare verified handoff and arbitration notes; do not perform implementation or self-merge.",
    },
    "reviewer": {
        "title": "Reviewer",
        "next_step": "Inspect revision-bound code diff and verified CI evidence against task contract.",
        "stop_boundary": "Yield revision verification findings and checkpoint assessment; do not authorize scope expansion or merge.",
    },
}


class SanitizedArgumentParser(argparse.ArgumentParser):
    """ArgumentParser that prevents echoing raw arguments or host script paths in errors."""

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        kwargs["prog"] = "preview_role_handoff.py"
        kwargs["allow_abbrev"] = False
        super().__init__(*args, **kwargs)

    def error(self, message: str) -> None:
        sys.stderr.write("ERROR: Invalid command-line arguments. Use --help for usage syntax.\n")
        sys.exit(2)


def validate_snapshot_data(data: Any) -> Dict[str, Any]:
    """Validates required schema fields from consumed handoff JSON."""
    if not isinstance(data, dict):
        raise ValueError("Snapshot root must be a JSON object.")

    # 1. repo: must be owner/name with non-empty parts, no dot components
    repo = data.get("repo")
    if not isinstance(repo, str):
        raise ValueError("Field 'repo' must be a valid 'owner/name' string.")
    parts = repo.split("/")
    if len(parts) != 2:
        raise ValueError("Field 'repo' must be a valid 'owner/name' string.")
    owner, name = parts
    if owner in (".", "..") or name in (".", ".."):
        raise ValueError("Field 'repo' must not contain dot components '.' or '..'.")
    if not REPO_PART_REGEX.fullmatch(owner) or not REPO_PART_REGEX.fullmatch(name):
        raise ValueError("Field 'repo' must be a valid 'owner/name' string.")

    # 2. pr_number
    pr_number = data.get("pr_number")
    if type(pr_number) is not int or pr_number <= 0:
        raise ValueError("Field 'pr_number' must be a positive integer.")

    # 3. primary_issue_number
    primary_issue_number = data.get("primary_issue_number")
    if type(primary_issue_number) is not int or primary_issue_number <= 0:
        raise ValueError("Field 'primary_issue_number' must be a positive integer.")

    # 4. full 40-hex SHAs
    for key in ("live_pr_head_sha", "live_pr_base_sha", "live_default_branch_sha"):
        val = data.get(key)
        if not isinstance(val, str) or not HEX_SHA_REGEX.fullmatch(val):
            raise ValueError(f"Field '{key}' must be a 40-character hexadecimal SHA.")

    # Optional ci_evidence.failure_summary is deliberately not consumed. This offline preview
    # keeps fixed role/identity text and no network/auth/parser imports or failure-message expansion.
    # 5. task states: exact case-insensitive match against finite vocabulary {"open", "closed"}
    pr_state = data.get("pr_state")
    if not isinstance(pr_state, str) or pr_state.lower() not in ("open", "closed"):
        raise ValueError("Field 'pr_state' must be exactly 'open' or 'closed'.")

    pr_merged = data.get("pr_merged")
    if type(pr_merged) is not bool:
        raise ValueError("Field 'pr_merged' must be a boolean.")

    if pr_merged and pr_state.lower() != "closed":
        raise ValueError("Field 'pr_merged' true requires 'pr_state' to be 'closed'.")

    primary_issue_state = data.get("primary_issue_state")
    if not isinstance(primary_issue_state, str) or primary_issue_state.lower() not in ("open", "closed"):
        raise ValueError("Field 'primary_issue_state' must be exactly 'open' or 'closed'.")

    return {
        "repo": repo,
        "pr_number": pr_number,
        "primary_issue_number": primary_issue_number,
        "live_pr_head_sha": data["live_pr_head_sha"].lower(),
        "live_pr_base_sha": data["live_pr_base_sha"].lower(),
        "live_default_branch_sha": data["live_default_branch_sha"].lower(),
        "pr_state": pr_state.upper(),
        "pr_merged": pr_merged,
        "primary_issue_state": primary_issue_state.upper(),
    }


def generate_role_preview(validated: Dict[str, Any], role: str) -> str:
    """Generates compact role preview (<=180 words) using validated identifiers and fixed text only."""
    role_key = role.lower()
    if role_key not in ROLE_CONFIGS:
        raise ValueError(f"Invalid role: {role}")

    cfg = ROLE_CONFIGS[role_key]
    repo = validated["repo"]
    issue_num = validated["primary_issue_number"]
    pr_num = validated["pr_number"]
    issue_state = validated["primary_issue_state"]
    pr_state = "MERGED" if validated["pr_merged"] else validated["pr_state"]
    head_sha = validated["live_pr_head_sha"]
    base_sha = validated["live_pr_base_sha"]
    default_sha = validated["live_default_branch_sha"]

    is_inactive = validated["pr_merged"] or pr_state == "CLOSED" or issue_state == "CLOSED"

    if is_inactive:
        lines = [
            "Role Handoff Preview (Historical snapshot, unverified-current):",
            f"Task: https://github.com/{repo}/issues/{issue_num} ({issue_state})",
            f"PR: https://github.com/{repo}/pull/{pr_num} ({pr_state})",
            f"Revisions: Head {head_sha} | Base {base_sha} | Default {default_sha}",
            f"Role: {cfg['title']}",
            "Status: Task is closed or merged (inactive).",
            "Prerequisites: Acquire adopted kernel, AGENTS.md, relevant register, and current authenticated task activation. Check holds and remote task/revision state before action.",
            "Authority: This preview grants no push, execution, publication, approval, or merge authority.",
            "Next Step: Directs only reconciliation or closeout under separate current authority; never restart implementation.",
            "Stop Boundary: Stop immediately. Do not execute code or resume implementation.",
        ]
    else:
        lines = [
            "Role Handoff Preview (Historical snapshot, unverified-current):",
            f"Task: https://github.com/{repo}/issues/{issue_num} ({issue_state})",
            f"PR: https://github.com/{repo}/pull/{pr_num} ({pr_state})",
            f"Revisions: Head {head_sha} | Base {base_sha} | Default {default_sha}",
            f"Role: {cfg['title']}",
            "Prerequisites: Acquire adopted kernel, AGENTS.md, relevant register, and current authenticated task activation. Check holds and remote task/revision state before action.",
            "Authority: This preview grants no push, execution, publication, approval, or merge authority.",
            f"Next Step: {cfg['next_step']}",
            f"Stop Boundary: {cfg['stop_boundary']}",
        ]

    preview_text = "\n".join(lines)
    word_count = len(preview_text.split())
    if word_count > 180:
        raise ValueError(f"Generated preview exceeds 180-word budget ({word_count} words).")
    return preview_text


def parse_args(argv: Optional[List[str]] = None) -> argparse.Namespace:
    parser = SanitizedArgumentParser(
        description="Generate compact read-only role handoff preview from existing handoff JSON.",
    )
    parser.add_argument(
        "--snapshot-file",
        required=True,
        type=Path,
        help="Path to handoff snapshot JSON file.",
    )
    parser.add_argument(
        "--role",
        required=True,
        choices=VALID_ROLES,
        type=str.lower,
        help="Target agent role (implementer, coordinator, reviewer).",
    )
    return parser.parse_args(argv)


def main(argv: Optional[List[str]] = None) -> int:
    args = parse_args(argv)

    snapshot_path = args.snapshot_file
    if not snapshot_path.exists():
        sys.stderr.write("ERROR: Input --snapshot-file does not exist.\n")
        return 1
    if not snapshot_path.is_file():
        sys.stderr.write("ERROR: Input --snapshot-file is not a regular file.\n")
        return 1

    try:
        raw_text = snapshot_path.read_text(encoding="utf-8-sig")
    except Exception:
        sys.stderr.write("ERROR: Failed to read --snapshot-file.\n")
        return 1

    try:
        data = json.loads(raw_text)
    except Exception:
        sys.stderr.write("ERROR: Snapshot file does not contain valid JSON syntax.\n")
        return 1

    try:
        validated = validate_snapshot_data(data)
        preview = generate_role_preview(validated, args.role)
    except ValueError as e:
        sys.stderr.write(f"ERROR: Snapshot schema validation failed: {e}\n")
        return 1

    sys.stdout.write(preview + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
