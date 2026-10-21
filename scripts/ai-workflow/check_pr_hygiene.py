"""Checks pull request description metadata against hygiene and task requirements."""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path
from typing import List, Optional, Sequence

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
    documentation_notes: List[str] = field(default_factory=list)


def safe_documentation_path(path: str) -> bool:
    """Portable literal documentation path; never opens files or resolves the host."""
    return bool(
        re.fullmatch(r"[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*", path)
        and all(part not in {".", ".."} and not part.endswith(".") for part in path.split("/"))
        and path.lower().endswith((".md", ".rst", ".txt", ".adoc"))
    )


def useful_explanation(text: str) -> bool:
    """Reject obvious placeholders, not a proof of semantic relevance."""
    words = re.findall(r"[A-Za-z]+", text)
    return bool(
        len(text.strip()) >= 20 and len(words) >= 4
        and not re.search(r"\b(todo|tbd|placeholder|lorem|ipsum)\b|<|>", text, re.I)
        and text.strip().lower().rstrip(".") not in {
            "no documentation changes are required", "no documentation changes are needed",
            "documentation is not applicable here", "all documentation is up to date",
        }
    )


def documentation_disposition(
    body: str, *, changed_files: Optional[Sequence[str]] = None,
    repository_files: Optional[Sequence[str]] = None,
) -> tuple[List[str], List[str]]:
    """Validate syntax; optional manifests must be complete, caller-acquired evidence.

    None means unavailable, whereas [] explicitly asserts a complete empty manifest.
    Repository evidence means paths present at the proposed head (not the base).
    """
    clean = re.sub(r"<!--.*?-->", "", body.replace("\r\n", "\n"), flags=re.S)
    clean = re.sub(r"^\s*(`{3,}|~{3,}).*?^\s*\1\s*$", "", clean, flags=re.M | re.S)
    sections = list(re.finditer(r"^##[ \t]+Documentation[ \t]*$", clean, re.M | re.I))
    error = "Documentation disposition: use documentation paths with specific impacts, or None — a specific reason."
    if len(sections) != 1:
        return [error], []
    rest = clean[sections[0].end():]
    end = re.search(r"^#{1,2}[ \t]+", rest, re.M)
    section = rest[:end.start()] if end else rest
    lines = [line.strip() for line in section.splitlines() if line.strip()]
    if len(lines) == 1 and re.match(r"^None\s+[—–-]\s+", lines[0], re.I):
        reason = re.split(r"\s+[—–-]\s+", lines[0], maxsplit=1)[1]
        return ([], ["Documentation semantics and freshness: NOT VERIFIED."]) if useful_explanation(reason) else ([error], [])
    paths = []
    for line in lines:
        match = re.fullmatch(r"(?:[-*][ \t]+)?(?:`([^`]+)`|([^\s`]+))[ \t]+[—–-][ \t]+(.+)", line)
        if not match:
            return [error], []
        path = match.group(1) or match.group(2)
        if not safe_documentation_path(path) or not useful_explanation(match.group(3)):
            return [error], []
        paths.append(path)
    if not paths:
        return [error], []
    errors, notes = [], ["Documentation semantics and freshness: NOT VERIFIED."]
    for manifest, label in ((changed_files, "changed-file inclusion"), (repository_files, "head existence")):
        if manifest is None:
            notes.append(f"Documentation {label}: NOT VERIFIED (complete evidence not supplied).")
        elif not isinstance(manifest, (list, tuple)) or any(not isinstance(p, str) for p in manifest):
            errors.append("Documentation evidence: invalid manifest shape.")
        elif any(path not in manifest for path in paths):
            errors.append(f"Documentation {label}: cited path absent from supplied complete evidence.")
        else:
            notes.append(f"Documentation {label}: matches supplied complete evidence (freshness not verified).")
    return errors, notes


def verify_pr_body(
    body: str, *, require_documentation: bool = False,
    changed_files: Optional[Sequence[str]] = None,
    repository_files: Optional[Sequence[str]] = None,
) -> HygieneResult:
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

    # Legacy library callers retain heading-only behavior. The CLI and offline
    # preflight explicitly adopt the prospective rule; no caller inspection.
    notes: List[str] = ["Documentation disposition: NOT VERIFIED (legacy heading-only API mode)."]
    if require_documentation:
        errors, notes = documentation_disposition(body, changed_files=changed_files, repository_files=repository_files)
        diagnostics.extend(errors)

    return HygieneResult(
        is_valid=(len(diagnostics) == 0),
        missing_sections=missing,
        primary_issue=primary_issue,
        diagnostics=diagnostics,
        documentation_notes=notes,
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
        body = args.pr_body_file.read_text(encoding="utf-8-sig")

    else:
        print("ERROR: Neither --pr-number nor a valid --pr-body-file was provided.", file=sys.stderr)
        return 1

    result = verify_pr_body(body, require_documentation=True)

    summary_file = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary_file:
        with open(summary_file, "a", encoding="utf-8") as f:
            f.write("### AI PR Hygiene Check\n\n")
            if result.is_valid:
                f.write(f"- PASS: Required metadata and documentation disposition syntax valid.\n")
                f.write(f"- Primary task Issue: #{result.primary_issue}\n")
            else:
                f.write(f"- FAIL: One or more required contract elements are missing:\n")
                for diag in result.diagnostics:
                    f.write(f"  - {diag}\n")
                f.write("\nRequired template sections: `## Primary Issue`, `## Summary`, `## Scope`, `## Verification`, `## Documentation`.\n")
            for note in result.documentation_notes:
                f.write(f"- {note}\n")

    for note in result.documentation_notes:
        print(note)

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
