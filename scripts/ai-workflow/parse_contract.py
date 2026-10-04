"""Parser for primary task issue references and allowed paths contracts."""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import List


class ContractParseError(Exception):
    """Raised when a task contract or PR metadata fails validation."""
    pass


@dataclass
class AllowedPaths:
    patterns: List[str]
    is_unconstrained: bool = False


def extract_primary_issue(pr_body: str) -> int:
    """
    Extracts the unambiguous primary task Issue reference from a PR body.

    Supports:
    1. Dedicated '## Primary Issue' section (e.g. '#11', 'Fixes #11', 'Resolves: #11')
    2. Standard closing keywords anywhere in the body ('Fixes #11', 'Closes #11', 'Resolves #11')

    Rejects:
    - Ambiguous or conflicting primary issue references
    - PRs that only contain contextual links (e.g. 'Relates to #7', 'Tracked in #8')
    - PRs missing any issue reference
    """
    if not pr_body or not pr_body.strip():
        raise ContractParseError("PR body is empty; no primary issue reference found.")

    # 1. Check for dedicated '## Primary Issue' section
    section_match = re.search(
        r"^##\s+Primary Issue\s*$(.*?)(?=^##\s+|\Z)",
        pr_body,
        re.MULTILINE | re.IGNORECASE | re.DOTALL,
    )
    if section_match:
        section_text = section_match.group(1).strip()
        # Look for #number or (keyword) #number in the section
        primary_matches = re.findall(
            r"(?:(?:fixes|closes|resolves)[ :]+)?#?(\d+)\b",
            section_text,
            re.IGNORECASE,
        )
        valid_nums = [int(m) for m in primary_matches if m]
        unique_nums = sorted(list(set(valid_nums)))
        if len(unique_nums) == 1:
            return unique_nums[0]
        elif len(unique_nums) > 1:
            raise ContractParseError(
                f"Conflicting primary issue references in '## Primary Issue' section: {unique_nums}"
            )

    # 2. Check for closing keywords across whole body
    closing_pattern = r"\b(?:fixes|closes|resolves)[ :]+#(\d+)\b"
    closing_matches = re.findall(closing_pattern, pr_body, re.IGNORECASE)
    closing_nums = sorted(list(set(int(m) for m in closing_matches)))

    if len(closing_nums) == 1:
        return closing_nums[0]
    elif len(closing_nums) > 1:
        raise ContractParseError(
            f"Conflicting primary issue references found in PR body: {closing_nums}"
        )

    # 3. Check for contextual references to provide actionable diagnostic
    contextual_pattern = (
        r"\b(?:relates\s+to|tracked\s+in|see(?:\s+also)?|related(?:\s+foundation)?)[ :]+#(\d+)\b"
    )
    contextual_matches = re.findall(contextual_pattern, pr_body, re.IGNORECASE)
    if contextual_matches:
        raise ContractParseError(
            "Only contextual issue references were found (e.g. 'Relates to #...'). "
            "An unambiguous primary task issue reference (e.g. 'Fixes #X' or '## Primary Issue') is required."
        )

    raise ContractParseError(
        "No primary issue reference found in PR body. "
        "Include '## Primary Issue' or 'Fixes #<number>'."
    )


def parse_allowed_paths(issue_body: str) -> AllowedPaths:
    """
    Parses the '### Allowed paths' section from an Issue description.

    Contract rules:
    - Explicit repository-relative paths or directory prefixes ending in '/**' or '/'
    - Standalone 'Unconstrained' keyword opts out for early investigation
    - Missing, blank, loose synonyms, or invalid paths raise ContractParseError
    """
    if not issue_body or not issue_body.strip():
        raise ContractParseError("Issue body is empty; missing '### Allowed paths' section.")

    match = re.search(
        r"^###\s+Allowed paths\s*$(.*?)(?=^###\s+|\Z)",
        issue_body,
        re.MULTILINE | re.IGNORECASE | re.DOTALL,
    )
    if not match:
        raise ContractParseError("Issue body is missing '### Allowed paths' section.")

    raw_section = match.group(1).strip()
    if not raw_section:
        raise ContractParseError("The '### Allowed paths' section is empty.")

    # Check for standalone Unconstrained
    cleaned_full = re.sub(r"^[*-]\s*", "", raw_section).strip().strip("`").strip()
    if cleaned_full.lower() == "unconstrained":
        return AllowedPaths(patterns=[], is_unconstrained=True)

    # Reject loose synonyms
    loose_synonyms = ["not yet constrained", "none", "any", "n/a", "open", "all", "tbd"]
    for synonym in loose_synonyms:
        if re.search(rf"\b{re.escape(synonym)}\b", raw_section, re.IGNORECASE):
            raise ContractParseError(
                f"Loose opt-out synonym '{synonym}' is not permitted. "
                "Use exact standalone 'Unconstrained' or explicit repository-relative paths."
            )

    # Parse pattern lines
    patterns: List[str] = []
    lines = raw_section.splitlines()
    for line in lines:
        stripped = line.strip()
        if not stripped:
            continue
        # Strip bullet points
        item = re.sub(r"^[*-]\s*", "", stripped).strip()
        # Strip enclosing backticks
        if item.startswith("`") and item.endswith("`") and len(item) >= 2:
            item = item[1:-1].strip()

        if not item:
            continue

        # Check if item itself is unconstrained
        if item.lower() == "unconstrained":
            if len(lines) == 1 or len(patterns) == 0:
                return AllowedPaths(patterns=[], is_unconstrained=True)

        # Validate syntax
        if item.startswith("/"):
            raise ContractParseError(f"Invalid allowed path '{item}': absolute paths starting with '/' are not permitted.")
        if item.startswith("../") or "/../" in item or item == "..":
            raise ContractParseError(f"Invalid allowed path '{item}': path traversal '../' is not permitted.")
        if ":" in item or "\\" in item:
            raise ContractParseError(f"Invalid allowed path '{item}': Windows drive letters and backslashes are not permitted. Use forward slashes.")

        patterns.append(item)

    if not patterns:
        raise ContractParseError("No valid path patterns found in '### Allowed paths' section.")

    return AllowedPaths(patterns=patterns, is_unconstrained=False)
