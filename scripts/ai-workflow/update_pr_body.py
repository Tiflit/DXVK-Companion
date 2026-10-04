"""Opt-in pull request description update helper with review preservation and lost-update guards."""

from __future__ import annotations

import argparse
import datetime
import difflib
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

try:
    from .update_dashboard import (
        GitHubApiError,
        GitHubClient,
        is_valid_positive_int,
        is_valid_repo_name,
        sanitize_diagnostic,
        sanitize_display_text,
    )
except ImportError:
    from update_dashboard import (
        GitHubApiError,
        GitHubClient,
        is_valid_positive_int,
        is_valid_repo_name,
        sanitize_diagnostic,
        sanitize_display_text,
    )


RECORD_START_PATTERN = re.compile(r"<!--\s*AI-REVIEW-RECORD:\s*([A-Za-z0-9_.:-]+)\s*-->")
ANY_START_PATTERN = re.compile(r"<!--\s*AI-REVIEW-RECORD:(.*?)-->")
RECORD_END_PATTERN = re.compile(r"<!--\s*AI-REVIEW-RECORD-END\s*-->")
UNMARKED_REVIEW_HEADING_PATTERN = re.compile(
    r"^##\s+(?:(?:ChatGPT|Claude|Independent|Coordinator|Auditor|Reviewer)\s+(?:coordinator\s+)?(?:verification|review|audit|record)|Review\s+History|Verification\s+History).*?$",
    re.MULTILINE | re.IGNORECASE,
)


def mask_code_spans(text: str) -> str:
    """Masks inline code spans and fenced code blocks with whitespace of equal length."""
    def mask_match(m):
        return " " * len(m.group(0))

    masked = re.sub(r"```[\s\S]*?```", mask_match, text)
    masked = re.sub(r"`[^`\r\n]*`", mask_match, masked)
    return masked


class SecurityValidationError(Exception):
    """Raised when review markers are malformed, duplicated, modified, or deleted."""
    pass


class LostUpdateError(Exception):
    """Raised when remote PR body was modified concurrently or does not match expected base hash."""
    pass


@dataclass
class ReviewRecord:
    record_id: str
    full_block: str
    inner_content: str
    start_pos: int
    end_pos: int


def parse_and_validate_review_records(body: str) -> Dict[str, ReviewRecord]:
    """
    Parses and strictly validates review record markers in a markdown text.
    Validates:
    - Balanced open and close tags
    - No nesting
    - No duplicate record IDs
    - Valid record identifier format
    """
    records: Dict[str, ReviewRecord] = {}
    if not body:
        return records

    # Mask code spans so documentation/code examples do not trigger marker parsing
    masked_body = mask_code_spans(body)

    any_start_matches = list(ANY_START_PATTERN.finditer(masked_body))
    start_matches = list(RECORD_START_PATTERN.finditer(masked_body))
    end_matches = list(RECORD_END_PATTERN.finditer(masked_body))

    # Reject malformed start tags with invalid identifiers
    if len(any_start_matches) != len(start_matches):
        raise SecurityValidationError("Malformed review marker: invalid review record identifier syntax.")

    # Verify matching counts
    if len(start_matches) != len(end_matches):
        if len(start_matches) > len(end_matches):
            raise SecurityValidationError("Malformed review marker: unclosed AI-REVIEW-RECORD tag found.")
        else:
            raise SecurityValidationError("Malformed review marker: orphan AI-REVIEW-RECORD-END tag found.")

    # Match each start with its corresponding end and check for nesting
    for i, s_match in enumerate(start_matches):
        s_start, s_end = s_match.span()
        record_id = s_match.group(1).strip()

        if record_id in records:
            raise SecurityValidationError(f"Duplicate review record ID detected: '{record_id}'.")

        e_match = end_matches[i]
        e_start, e_end = e_match.span()

        if e_start < s_end:
            raise SecurityValidationError("Malformed review marker: AI-REVIEW-RECORD-END precedes AI-REVIEW-RECORD.")

        # Check if another start tag appears before this end tag (nesting)
        if i + 1 < len(start_matches):
            next_s_start = start_matches[i + 1].start()
            if next_s_start < e_end:
                raise SecurityValidationError("Malformed review marker: nested AI-REVIEW-RECORD tags are forbidden.")

        full_block = body[s_start:e_end]
        inner_content = body[s_end:e_start]

        records[record_id] = ReviewRecord(
            record_id=record_id,
            full_block=full_block,
            inner_content=inner_content,
            start_pos=s_start,
            end_pos=e_end,
        )

    return records


def adopt_unmarked_review_records(body: str) -> Tuple[str, List[str]]:
    """
    Scans a PR description for candidate unmarked reviewer sections and wraps them in
    <!-- AI-REVIEW-RECORD: adopted-<slug> --> tags.
    Only adopts sections that are not already inside existing marked blocks.
    """
    if not body:
        return body, []

    existing_records = parse_and_validate_review_records(body)
    occupied_ranges = [(r.start_pos, r.end_pos) for r in existing_records.values()]

    def is_inside_occupied(pos: int) -> bool:
        for start, end in occupied_ranges:
            if start <= pos < end:
                return True
        return False

    matches = list(UNMARKED_REVIEW_HEADING_PATTERN.finditer(mask_code_spans(body)))
    if not matches:
        return body, []

    # Sort matches in reverse order to preserve string offsets during replacement
    adopted_ids: List[str] = []
    new_body = body

    # Split body into sections by headers or process matched ranges
    for idx, match in enumerate(reversed(matches)):
        heading_start = match.start()
        if is_inside_occupied(heading_start):
            continue

        heading_line = match.group(0).strip()
        slug = re.sub(r"[^A-Za-z0-9]+", "-", heading_line.lstrip("#").strip().lower()).strip("-")
        record_id = f"adopted-{slug}-{len(matches) - idx}"

        # Find the end of this section (next '## ' or end of text)
        next_heading = re.search(r"\n##\s+", new_body[match.end():])
        if next_heading:
            section_end = match.end() + next_heading.start()
        else:
            section_end = len(new_body)

        section_text = new_body[heading_start:section_end].strip()

        wrapped = f"<!-- AI-REVIEW-RECORD: {record_id} -->\n{section_text}\n<!-- AI-REVIEW-RECORD-END -->\n"
        new_body = new_body[:heading_start] + wrapped + new_body[section_end:]
        adopted_ids.append(record_id)

    adopted_ids.reverse()
    return new_body, adopted_ids


def compute_body_sha256(text: str) -> str:
    """Computes SHA256 hex digest of normalized UTF-8 string."""
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def merge_and_preserve_review_records(
    remote_body: str,
    proposed_body: str,
    adopt_unmarked: bool = False,
) -> Tuple[str, List[str]]:
    """
    Preserves all marked review records from the remote PR body into the proposed body.
    Rules:
    - If proposed_body contains review markers:
      - All remote review records must be present. Missing -> SecurityValidationError (accidental deletion).
      - All remote review records must be byte-for-byte identical. Modified -> SecurityValidationError (accidental modification).
      - New valid records with unique IDs in proposed_body are allowed.
    - If proposed_body contains NO review markers:
      - All remote review records are preserved and appended under a dedicated section.
    """
    effective_remote = remote_body
    adopted_ids: List[str] = []

    if adopt_unmarked:
        effective_remote, adopted_ids = adopt_unmarked_review_records(remote_body)

    remote_records = parse_and_validate_review_records(effective_remote)
    proposed_records = parse_and_validate_review_records(proposed_body)

    if proposed_records:
        # User supplied review records in the proposed body: strictly verify preservation
        for rid, remote_rec in remote_records.items():
            if rid not in proposed_records:
                raise SecurityValidationError(
                    f"Accidental deletion of protected review record '{rid}'. "
                    f"All existing review records must be preserved."
                )
            proposed_rec = proposed_records[rid]
            if proposed_rec.full_block.strip() != remote_rec.full_block.strip():
                raise SecurityValidationError(
                    f"Accidental modification of protected review record '{rid}'. "
                    f"Historical review records cannot be modified in-place."
                )
        final_body = proposed_body
    else:
        # User proposed only updated description without review markers: automatically preserve remote records
        if remote_records:
            history_blocks = [rec.full_block.strip() for rec in remote_records.values()]
            history_text = "\n\n".join(history_blocks)

            if "## Review & Verification History" in proposed_body:
                final_body = proposed_body.rstrip() + "\n\n" + history_text + "\n"
            else:
                final_body = (
                    proposed_body.rstrip()
                    + "\n\n## Review & Verification History\n\n"
                    + history_text
                    + "\n"
                )
        else:
            final_body = proposed_body

    # Re-validate the combined final output
    parse_and_validate_review_records(final_body)
    return final_body, list(remote_records.keys())


def create_prewrite_backup(pr_number: int, body: str, backup_dir: Optional[Path] = None) -> Path:
    """
    Writes a local pre-write recovery backup file.
    Does not leak private data or tokens.
    """
    target_dir = backup_dir or (Path.cwd() / ".ai-review-backups")
    target_dir.mkdir(parents=True, exist_ok=True)

    timestamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d_%H%M%S")
    backup_file = target_dir / f"pr_{pr_number}_body_backup_{timestamp}.md"
    backup_file.write_text(body, encoding="utf-8")
    return backup_file


class ExtendedGitHubClient(GitHubClient):
    """Extends GitHubClient with get_pr, get_issue, and patch_pr_body."""

    def get_pr(self, pr_number: int) -> Dict[str, Any]:
        return self._request("GET", f"repos/{self.repo}/pulls/{pr_number}")

    def get_issue(self, issue_number: int) -> Dict[str, Any]:
        return self._request("GET", f"repos/{self.repo}/issues/{issue_number}")

    def patch_pr_body(self, pr_number: int, body: str) -> Dict[str, Any]:
        """
        Updates the PR description via GitHub REST API PATCH /repos/{repo}/pulls/{number}.

        RESIDUAL RACE NOTICE:
        The GitHub REST API does not support conditional HTTP ETag (If-Match) on pull request updates.
        A narrow residual race window remains between the final pre-write GET check and this PATCH.
        """
        payload = {"body": body}
        return self._request("PATCH", f"repos/{self.repo}/pulls/{pr_number}", payload)



def get_default_github_token() -> Optional[str]:
    """Retrieves GitHub token from environment variables or local gh CLI auth."""
    token = os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN")
    if token:
        return token
    try:
        res = subprocess.run(["gh", "auth", "token"], capture_output=True, text=True, timeout=3)
        if res.returncode == 0 and res.stdout.strip():
            return res.stdout.strip()
    except Exception:
        pass
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description="Safely update PR description with review preservation and lost-update checks.")
    parser.add_argument("--pr", type=int, required=True, help="Target Pull Request number.")
    parser.add_argument("--body-file", type=Path, default=None, help="Path to file containing new PR body text.")
    parser.add_argument("--body", type=str, default=None, help="Inline string containing new PR body text.")
    parser.add_argument("--write", action="store_true", help="Explicit opt-in flag to execute write (defaults to preview only).")
    parser.add_argument("--adopt-unmarked", action="store_true", help="Opt-in route to adopt candidate unmarked reviewer sections.")
    parser.add_argument("--expected-base-hash", type=str, default=None, help="Expected SHA256 hex digest of remote PR body before update.")
    parser.add_argument("--backup-dir", type=Path, default=None, help="Directory to store pre-write local recovery backup file.")
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", "Tiflit/DXVK-Companion"), help="Repository owner/repo.")
    parser.add_argument("--token", default=get_default_github_token(), help="GitHub API token.")
    args = parser.parse_args()

    if not is_valid_repo_name(args.repo):
        print(f"ERROR: Invalid repository name: '{args.repo}'", file=sys.stderr)
        return 1

    if not is_valid_positive_int(args.pr):
        print(f"ERROR: Invalid PR number: '{args.pr}'", file=sys.stderr)
        return 1

    # Read proposed body
    if args.body_file:
        if not args.body_file.is_file():
            print(f"ERROR: Body file not found: '{args.body_file}'", file=sys.stderr)
            return 1
        proposed_body = args.body_file.read_text(encoding="utf-8")
    elif args.body is not None:
        proposed_body = args.body
    else:
        print("ERROR: Either --body-file or --body must be specified.", file=sys.stderr)
        return 1

    client = ExtendedGitHubClient(token=args.token, repo=args.repo)

    # 1. Fetch current remote PR body
    try:
        remote_pr_data = client.get_pr(args.pr)
    except Exception as e:
        print(f"ERROR: Failed to fetch PR #{args.pr} from GitHub API: {e}", file=sys.stderr)
        return 1

    remote_body = remote_pr_data.get("body", "") or ""
    current_hash = compute_body_sha256(remote_body)

    # 2. Check expected base hash if provided
    if args.expected_base_hash:
        expected = args.expected_base_hash.strip().lower()
        if current_hash.lower() != expected:
            print(
                f"ERROR: Lost-update check failed! Remote PR body hash ({current_hash[:12]}) "
                f"does not match expected base hash ({expected[:12]}). Aborting.",
                file=sys.stderr,
            )
            return 1

    # 3. Merge and preserve review records
    try:
        final_body, preserved_ids = merge_and_preserve_review_records(
            remote_body=remote_body,
            proposed_body=proposed_body,
            adopt_unmarked=args.adopt_unmarked,
        )
    except SecurityValidationError as e:
        print(f"ERROR: Review preservation error: {e}", file=sys.stderr)
        return 1

    final_hash = compute_body_sha256(final_body)

    # 4. Preview Mode (Default)
    if not args.write:
        print(f"[PREVIEW] PR #{args.pr} body update (zero writes committed):")
        print(f"- Current Remote Body Hash: `{current_hash}`")
        print(f"- Proposed Target Body Hash: `{final_hash}`")
        print(f"- Preserved Review Records ({len(preserved_ids)}): {', '.join(preserved_ids) if preserved_ids else 'none'}")
        if args.adopt_unmarked:
            print("- Unmarked Record Adoption: ENABLED")
        print("")
        print("--- DIFF PREVIEW ---")
        diff = difflib.unified_diff(
            remote_body.splitlines(keepends=True),
            final_body.splitlines(keepends=True),
            fromfile=f"PR-{args.pr}-current",
            tofile=f"PR-{args.pr}-proposed",
            n=3,
        )
        sys.stdout.writelines(diff)
        print("--- END DIFF PREVIEW ---")
        print("\nTo commit this change, re-run with explicit --write flag.")
        return 0

    # 5. Write Mode (Opt-in)
    if not client.token:
        print("ERROR: GitHub token (GITHUB_TOKEN or GH_TOKEN) required for --write.", file=sys.stderr)
        return 1

    # 5a. Save pre-write local recovery backup
    try:
        backup_path = create_prewrite_backup(args.pr, remote_body, args.backup_dir)
        print(f"Pre-write local recovery backup saved to: {backup_path}")
    except Exception as e:
        print(f"ERROR saving recovery backup: {e}", file=sys.stderr)
        return 1

    # 5b. Pre-write check: re-read latest remote body to detect concurrent modifications
    try:
        prewrite_data = client.get_pr(args.pr)
        prewrite_body = prewrite_data.get("body", "") or ""
        prewrite_hash = compute_body_sha256(prewrite_body)
        if prewrite_hash != current_hash:
            print(
                f"ERROR: Concurrent modification detected! Remote PR body changed between initial fetch "
                f"({current_hash[:12]}) and pre-write verification ({prewrite_hash[:12]}). Aborting write.",
                file=sys.stderr,
            )
            return 1
    except Exception as e:
        print(f"ERROR during pre-write verification check: {e}", file=sys.stderr)
        return 1

    # 5c. Execute PATCH write
    print(f"Writing updated body to PR #{args.pr}...")
    try:
        client.patch_pr_body(args.pr, final_body)
    except Exception as e:
        print(f"ERROR: Failed to update PR body: {e}", file=sys.stderr)
        return 1

    # 5d. Post-write verification
    try:
        post_data = client.get_pr(args.pr)
        post_body = post_data.get("body", "") or ""
        post_hash = compute_body_sha256(post_body)
        if post_hash != final_hash:
            print(
                f"WARNING: Discrepancy detected after write! Verified hash ({post_hash[:12]}) "
                f"does not match target hash ({final_hash[:12]}).",
                file=sys.stderr,
            )
            return 1
        print(f"SUCCESS: PR #{args.pr} body updated and verified matching target hash `{final_hash[:12]}`.")
    except Exception as e:
        print(f"WARNING: Post-write verification failed: {e}", file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
