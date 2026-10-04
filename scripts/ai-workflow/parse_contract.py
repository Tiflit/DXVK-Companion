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


LOOSE_SYNONYMS = {
    "not yet constrained",
    "none",
    "any",
    "n/a",
    "open",
    "all",
    "tbd",
}


def strip_html_comments(text: str) -> str:
    """Strips HTML comments (<!-- ... -->) from text."""
    if not text:
        return ""
    return re.sub(r"<!--.*?-->", "", text, flags=re.DOTALL)


def extract_primary_issue(pr_body: str) -> int:
    """
    Extracts the unambiguous primary task Issue reference from a PR body.

    Supports:
    1. Dedicated '## Primary Issue' section (e.g. '#11', 'Fixes #11', 'Resolves: #11')
    2. Standard closing keywords anywhere in the body ('Fixes #11', 'Closes #11', 'Resolves #11')

    Rejects:
    - Naked digits without '#' prefix
    - HTML comments (e.g. comment examples in PR template)
    - Ambiguous or conflicting primary issue references
    - PRs that only contain contextual links (e.g. 'Relates to #7', 'Tracked in #8')
    - PRs missing any issue reference
    """
    if not pr_body or not pr_body.strip():
        raise ContractParseError("PR body is empty; no primary issue reference found.")

    cleaned_body = strip_html_comments(pr_body)

    # 1. Check for dedicated '## Primary Issue' section
    section_match = re.search(
        r"^##\s+Primary Issue\s*$(.*?)(?=^##\s+|\Z)",
        cleaned_body,
        re.MULTILINE | re.IGNORECASE | re.DOTALL,
    )
    if section_match:
        section_text = section_match.group(1).strip()
        # Look specifically for #<digits> or keyword + #<digits>
        # Rejects naked digits without '#'
        primary_matches = re.findall(
            r"(?:(?:fixes|closes|resolves|issue)[ :]+)?#(\d+)\b",
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
    closing_matches = re.findall(closing_pattern, cleaned_body, re.IGNORECASE)
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
    contextual_matches = re.findall(contextual_pattern, cleaned_body, re.IGNORECASE)
    if contextual_matches:
        raise ContractParseError(
            "Only contextual issue references were found (e.g. 'Relates to #...'). "
            "An unambiguous primary task issue reference (e.g. 'Fixes #X' or '## Primary Issue') is required."
        )

    raise ContractParseError(
        "No primary issue reference found in PR body. "
        "Include '## Primary Issue' with '#<number>' or 'Fixes #<number>'."
    )


def parse_allowed_paths(issue_body: str) -> AllowedPaths:
    """
    Parses the '### Allowed paths' section from an Issue description.

    Contract rules:
    - Explicit repository-relative paths or directory prefixes ending in '/**' or '/'
    - Standalone 'Unconstrained' keyword opts out for early investigation
    - Mixed 'Unconstrained' and explicit paths is strictly rejected (A1)
    - Whole-entry loose synonyms are rejected (F9); valid paths containing words like 'open' or 'all' are preserved
    - Missing, blank, or invalid paths raise ContractParseError
    """
    if not issue_body or not issue_body.strip():
        raise ContractParseError("Issue body is empty; missing '### Allowed paths' section.")

    cleaned_body = strip_html_comments(issue_body)

    match = re.search(
        r"^###\s+Allowed paths\s*$(.*?)(?=^###\s+|\Z)",
        cleaned_body,
        re.MULTILINE | re.IGNORECASE | re.DOTALL,
    )
    if not match:
        raise ContractParseError("Issue body is missing '### Allowed paths' section.")

    raw_section = match.group(1).strip()
    if not raw_section:
        raise ContractParseError("The '### Allowed paths' section is empty.")

    # Parse entries line by line
    raw_lines = raw_section.splitlines()
    entries: List[str] = []
    for line in raw_lines:
        stripped = line.strip()
        if not stripped:
            continue
        # Strip bullet points
        item = re.sub(r"^[*-]\s*", "", stripped).strip()
        # Strip enclosing backticks
        if item.startswith("`") and item.endswith("`") and len(item) >= 2:
            item = item[1:-1].strip()
        if item:
            entries.append(item)

    if not entries:
        raise ContractParseError("The '### Allowed paths' section contains no path entries.")

    # Check for whole-entry loose synonyms (F9)
    for entry in entries:
        norm_entry = entry.strip().lower()
        if norm_entry in LOOSE_SYNONYMS:
            raise ContractParseError(
                f"Loose opt-out synonym '{entry}' is not permitted. "
                "Use exact standalone 'Unconstrained' or explicit repository-relative paths."
            )

    # Check for Unconstrained entries (A1)
    unconstrained_entries = [e for e in entries if e.strip().lower() == "unconstrained"]
    if unconstrained_entries:
        if len(entries) > 1:
            raise ContractParseError(
                "Mixed 'Unconstrained' and explicit paths is not permitted. "
                "'Unconstrained' must be the sole entry in '### Allowed paths'."
            )
        return AllowedPaths(patterns=[], is_unconstrained=True)

    # Validate each explicit path pattern
    patterns: List[str] = []
    for item in entries:
        norm_item = item.strip()
        if re.search(r"\s", norm_item):
            raise ContractParseError(f"Invalid allowed path '{norm_item}': paths must not contain whitespace or prose.")
        if norm_item.startswith("/"):
            raise ContractParseError(f"Invalid allowed path '{norm_item}': absolute paths starting with '/' are not permitted.")
        if norm_item.startswith("../") or "/../" in norm_item or norm_item == "..":
            raise ContractParseError(f"Invalid allowed path '{norm_item}': path traversal '../' is not permitted.")
        if ":" in norm_item or "\\" in norm_item:
            raise ContractParseError(f"Invalid allowed path '{norm_item}': Windows drive letters and backslashes are not permitted. Use forward slashes.")

        patterns.append(norm_item)

    if not patterns:
        raise ContractParseError("No valid path patterns found in '### Allowed paths' section.")

    return AllowedPaths(patterns=patterns, is_unconstrained=False)

