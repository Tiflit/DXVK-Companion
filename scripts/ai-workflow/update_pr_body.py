"""Opt-in pull request description and issue append helper with review preservation and lost-update guards."""

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
        sanitize_display_text,
    )
except ImportError:
    from update_dashboard import (
        GitHubApiError,
        GitHubClient,
        is_valid_positive_int,
        is_valid_repo_name,
        sanitize_display_text,
    )


SUPPORTED_RECORD_FAMILIES = ("REVIEW", "POST-MERGE", "ASSIGNMENT")
SUPPORTED_FAMILIES_PATTERN = r"(?:REVIEW|POST-MERGE|ASSIGNMENT)"

RECORD_START_PATTERN = re.compile(
    rf"<!--\s*AI-({SUPPORTED_FAMILIES_PATTERN})-RECORD:\s*([A-Za-z0-9_.:-]+)\s*-->"
)
ANY_START_PATTERN = re.compile(
    r"<!--\s*AI-([A-Za-z0-9_-]+)-RECORD:(.*?)-->"
)
RECORD_END_PATTERN = re.compile(
    rf"<!--\s*AI-({SUPPORTED_FAMILIES_PATTERN})-RECORD-END\s*-->"
)
ANY_END_PATTERN = re.compile(
    r"<!--\s*AI-([A-Za-z0-9_-]+)-RECORD-END\s*-->"
)

UNMARKED_REVIEW_HEADING_PATTERN = re.compile(
    r"^##\s+(?:(?:ChatGPT|Claude|Independent|Coordinator|Auditor|Reviewer)\s+(?:coordinator\s+)?(?:verification|review|audit|record)|Review\s+History|Verification\s+History).*?$",
    re.MULTILINE | re.IGNORECASE,
)

ISSUE_APPEND_SEPARATOR = "\n\n"

# Privacy detection patterns (fail closed)
WINDOWS_HOME_PATH_PATTERN = re.compile(
    r"(?:[A-Za-z]:[/\\]|(?<!\w)[/\\])(?:Users|Documents and Settings)[/\\][^\s\"'`|<>]+",
    re.IGNORECASE,
)
POSIX_HOME_PATH_PATTERN = re.compile(
    r"(?:^|[\s\"'`(<+\-])/(?:home|Users)/[^\s\"'`|<>]+",
    re.IGNORECASE,
)
CREDENTIAL_TOKEN_PATTERN = re.compile(
    r"\b(?:ghp_|github_pat_|gho_|ghu_|ghs_|ghr_)[A-Za-z0-9_]{8,}\b|\bbearer\s+[A-Za-z0-9_.-]{8,}\b",
    re.IGNORECASE,
)


def mask_code_spans(text: str) -> str:
    """Masks inline code spans and fenced code blocks with whitespace of equal length."""
    def mask_match(m):
        return " " * len(m.group(0))

    masked = re.sub(r"```[\s\S]*?```", mask_match, text)
    masked = re.sub(r"`[^`\r\n]*`", mask_match, masked)
    return masked


class SecurityValidationError(Exception):
    """Raised when record markers are malformed, duplicated, modified, or deleted."""
    pass


class LostUpdateError(Exception):
    """Raised when remote body was modified concurrently or does not match expected base hash."""
    pass


class PrivacyViolationError(Exception):
    """Raised when personal home paths or credential tokens are detected in proposed content."""
    pass


def scan_for_privacy_violations(text: str) -> List[Tuple[int, str]]:
    """
    Scans text for personal home paths and credential tokens.
    Returns a list of (line_number, violation_category) tuples.
    Does NOT return or echo matched sensitive text or tokens.
    """
    violations: List[Tuple[int, str]] = []
    if not text:
        return violations

    for line_num, line in enumerate(text.splitlines(), start=1):
        if WINDOWS_HOME_PATH_PATTERN.search(line):
            violations.append((line_num, "Windows personal-home path"))
        elif POSIX_HOME_PATH_PATTERN.search(line):
            violations.append((line_num, "POSIX personal-home path"))
        elif CREDENTIAL_TOKEN_PATTERN.search(line):
            violations.append((line_num, "GitHub credential or bearer token"))

    return violations


def sanitize_privacy_text(text: Optional[str]) -> str:
    """
    Sanitizes diagnostic messages, diff lines, and paths to prevent leaking
    Windows personal-home paths (both / and \\), POSIX home paths, and credentials/tokens.
    Replaces matched substrings with redaction placeholders.
    """
    if not text:
        return ""
    clean = str(text)
    clean = WINDOWS_HOME_PATH_PATTERN.sub("[REDACTED_PATH]", clean)

    def _redact_posix(m):
        raw = m.group(0)
        leading = ""
        for ch in raw:
            if ch in " \t\"'`(<+-":
                leading += ch
            else:
                break
        return leading + "[REDACTED_PATH]"

    clean = POSIX_HOME_PATH_PATTERN.sub(_redact_posix, clean)
    clean = CREDENTIAL_TOKEN_PATTERN.sub("[REDACTED_TOKEN]", clean)
    clean = re.sub(r"\?[\w=&-]+", "?[REDACTED_QUERY]", clean)
    return clean


def is_privacy_sensitive_text(text: Optional[str]) -> bool:
    """Returns True if text contains Windows personal paths, POSIX personal paths, or credentials/tokens."""
    if not text:
        return False
    return (
        bool(WINDOWS_HOME_PATH_PATTERN.search(text))
        or bool(POSIX_HOME_PATH_PATTERN.search(text))
        or bool(CREDENTIAL_TOKEN_PATTERN.search(text))
    )


def format_safe_filename_diagnostic(path: Union[Path, str]) -> str:
    """
    Returns a safe representation of a filename for diagnostic messages.
    If the path or filename contains sensitive user paths or tokens,
    it returns '<redacted-filename>' without echoing sensitive content.
    """
    p_str = str(path)
    name = Path(path).name if hasattr(path, "name") or isinstance(path, (str, Path)) else str(path)
    if is_privacy_sensitive_text(p_str) or is_privacy_sensitive_text(name):
        return "<redacted-filename>"
    return name


def sanitize_diff_line(diff_line: str) -> str:
    """
    Sanitizes a unified-diff line while preserving diff structural prefixes.
    For diff change lines ('-', '+') and context lines (' '), strips the leading
    indicator, sanitizes the line payload, and restores the indicator.
    Header lines ('---', '+++', '@@') are preserved as-is.
    """
    if diff_line.startswith(("---", "+++", "@@")):
        return diff_line
    if diff_line.startswith(("-", "+", " ")):
        prefix = diff_line[0]
        payload = diff_line[1:]
        return prefix + sanitize_privacy_text(payload)
    return sanitize_privacy_text(diff_line)


def sanitize_diagnostic(msg: Any) -> str:
    """Sanitizes diagnostic and error messages to prevent leaking paths, tokens, or raw queries."""
    if msg is None:
        return ""
    return sanitize_privacy_text(str(msg)).strip()


def format_safe_backup_display(backup_path: Path) -> str:
    """Formats backup confirmation path safely without leaking personal home directories."""
    try:
        rel_path = backup_path.relative_to(Path.cwd())
        display_str = str(rel_path)
    except ValueError:
        display_str = str(backup_path)
    return sanitize_privacy_text(display_str)


@dataclass
class ReviewRecord:
    record_id: str
    full_block: str
    inner_content: str
    start_pos: int
    end_pos: int
    family: str = "REVIEW"


def parse_and_validate_review_records(body: str) -> Dict[str, ReviewRecord]:
    """
    Parses and strictly validates record markers in markdown text.
    Validates:
    - Supported record families (REVIEW, POST-MERGE, ASSIGNMENT)
    - Balanced open and close tags of the same family
    - No nesting
    - No duplicate record IDs
    - Valid record identifier format
    """
    records: Dict[str, ReviewRecord] = {}
    if not body:
        return records

    masked_body = mask_code_spans(body)

    any_start_matches = list(ANY_START_PATTERN.finditer(masked_body))
    start_matches = list(RECORD_START_PATTERN.finditer(masked_body))
    any_end_matches = list(ANY_END_PATTERN.finditer(masked_body))
    end_matches = list(RECORD_END_PATTERN.finditer(masked_body))

    # Reject unsupported families or malformed start tags
    if len(any_start_matches) != len(start_matches):
        for m in any_start_matches:
            fam = m.group(1)
            if fam not in SUPPORTED_RECORD_FAMILIES:
                raise SecurityValidationError(f"Unsupported record marker family: 'AI-{fam}-RECORD'.")
        raise SecurityValidationError("Malformed review marker: invalid review record identifier syntax.")

    # Reject unsupported families or malformed end tags
    if len(any_end_matches) != len(end_matches):
        for m in any_end_matches:
            fam = m.group(1)
            if fam not in SUPPORTED_RECORD_FAMILIES:
                raise SecurityValidationError(f"Unsupported record marker family: 'AI-{fam}-RECORD-END'.")
        raise SecurityValidationError("Malformed review marker: invalid record end tag.")

    if len(start_matches) != len(end_matches):
        if len(start_matches) > len(end_matches):
            raise SecurityValidationError("Malformed review marker: unclosed record tag found.")
        else:
            raise SecurityValidationError("Malformed review marker: orphan record end tag found.")

    for i, s_match in enumerate(start_matches):
        s_start, s_end = s_match.span()
        s_family = s_match.group(1)
        record_id = s_match.group(2).strip()

        if record_id in records:
            raise SecurityValidationError(f"Duplicate review record ID detected: '{record_id}'.")

        e_match = end_matches[i]
        e_start, e_end = e_match.span()
        e_family = e_match.group(1)

        if s_family != e_family:
            raise SecurityValidationError(
                f"Mismatched record marker tags: AI-{s_family}-RECORD was closed with AI-{e_family}-RECORD-END."
            )

        if e_start < s_end:
            raise SecurityValidationError("Malformed review marker: record end precedes start tag.")

        if i + 1 < len(start_matches):
            next_s_start = start_matches[i + 1].start()
            if next_s_start < e_end:
                raise SecurityValidationError("Malformed review marker: nested record tags are forbidden.")

        full_block = body[s_start:e_end]
        inner_content = body[s_end:e_start]

        records[record_id] = ReviewRecord(
            record_id=record_id,
            full_block=full_block,
            inner_content=inner_content,
            start_pos=s_start,
            end_pos=e_end,
            family=s_family,
        )

    return records


def parse_and_validate_append_input(append_text: str) -> ReviewRecord:
    """
    Parses and strictly validates that the append input contains exactly one bounded activity record.
    Requires the input to consist solely of the single bounded block plus optional surrounding whitespace.
    """
    if not append_text or not append_text.strip():
        raise SecurityValidationError("Append input is empty; exactly one bounded activity-record block is required.")

    records = parse_and_validate_review_records(append_text)
    if len(records) == 0:
        raise SecurityValidationError("Append input must contain exactly one bounded attributed activity-record block.")
    if len(records) > 1:
        raise SecurityValidationError(f"Append input contains multiple activity-record blocks ({len(records)}); exactly one is permitted.")
    record = next(iter(records.values()))

    # Ensure no unmarked prefix or suffix payload exists outside the bounded block
    prefix = append_text[:record.start_pos]
    suffix = append_text[record.end_pos:]
    if prefix.strip() or suffix.strip():
        raise SecurityValidationError(
            "Append input must consist solely of the single bounded activity-record block (surrounding unmarked text is forbidden)."
        )

    return record


def prepare_issue_candidate(remote_body: Optional[str], append_text: str) -> str:
    """
    Constructs candidate Issue body by appending the new activity block to the unchanged remote body.
    Preserves all existing remote text, line endings, and whitespace character-for-character.
    """
    clean_append = append_text.strip()
    if not remote_body:
        return clean_append
    return f"{remote_body}{ISSUE_APPEND_SEPARATOR}{clean_append}"


def check_issue_idempotency(remote_body: Optional[str], append_record: ReviewRecord) -> Tuple[bool, bool]:
    """
    Checks remote body for existing record with the same ID.
    Returns (is_duplicate_identical, is_conflicting).
    Fails closed if existing remote body contains malformed or duplicate record markers.
    """
    if not remote_body:
        return False, False

    # Fail closed on malformed existing records (do NOT catch SecurityValidationError)
    remote_records = parse_and_validate_review_records(remote_body)

    if append_record.record_id in remote_records:
        existing = remote_records[append_record.record_id]
        if existing.full_block.strip() == append_record.full_block.strip():
            return True, False
        else:
            return False, True

    return False, False


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

    adopted_ids: List[str] = []
    new_body = body

    for idx, match in enumerate(reversed(matches)):
        heading_start = match.start()
        if is_inside_occupied(heading_start):
            continue

        heading_line = match.group(0).strip()
        slug = re.sub(r"[^A-Za-z0-9]+", "-", heading_line.lstrip("#").strip().lower()).strip("-")
        record_id = f"adopted-{slug}-{len(matches) - idx}"

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
    """
    effective_remote = remote_body
    adopted_ids: List[str] = []

    if adopt_unmarked:
        effective_remote, adopted_ids = adopt_unmarked_review_records(remote_body)

    remote_records = parse_and_validate_review_records(effective_remote)
    proposed_records = parse_and_validate_review_records(proposed_body)

    if proposed_records:
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

    parse_and_validate_review_records(final_body)
    return final_body, list(remote_records.keys())


def create_prewrite_backup(
    target_type_or_number: Any,
    number_or_body: Any,
    body_or_backup_dir: Any = None,
    backup_dir: Optional[Path] = None,
) -> Path:
    """
    Writes a local pre-write recovery backup file.
    Supports:
      create_prewrite_backup(pr_number: int, body: str, backup_dir: Optional[Path] = None)
      create_prewrite_backup(target_type: str, number: int, body: str, backup_dir: Optional[Path] = None)
    Does not leak private data or tokens.
    """
    if isinstance(target_type_or_number, int):
        target_type = "pr"
        number = target_type_or_number
        body_text = str(number_or_body)
        target_backup_dir = body_or_backup_dir
    else:
        target_type = str(target_type_or_number).lower()
        number = int(number_or_body)
        body_text = str(body_or_backup_dir)
        target_backup_dir = backup_dir

    target_dir = target_backup_dir or (Path.cwd() / ".ai-review-backups")
    target_dir.mkdir(parents=True, exist_ok=True)

    timestamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%d_%H%M%S")
    backup_file = target_dir / f"{target_type}_{number}_body_backup_{timestamp}.md"
    backup_file.write_text(body_text, encoding="utf-8")
    return backup_file


class ExtendedGitHubClient(GitHubClient):
    """Extends GitHubClient with get_pr, get_issue, patch_pr_body, and patch_issue_body."""

    def get_pr(self, pr_number: int) -> Dict[str, Any]:
        return self._request("GET", f"repos/{self.repo}/pulls/{pr_number}")

    def get_issue(self, issue_number: int) -> Dict[str, Any]:
        return self._request("GET", f"repos/{self.repo}/issues/{issue_number}")

    def patch_pr_body(self, pr_number: int, body: str) -> Dict[str, Any]:
        payload = {"body": body}
        return self._request("PATCH", f"repos/{self.repo}/pulls/{pr_number}", payload)

    def patch_issue_body(self, issue_number: int, body: str) -> Dict[str, Any]:
        payload = {"body": body}
        return self._request("PATCH", f"repos/{self.repo}/issues/{issue_number}", payload)


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
    parser = argparse.ArgumentParser(
        description="Safely update PR description or append Issue activity records with review preservation and lost-update checks."
    )
    parser.add_argument("--pr", type=int, default=None, help="Target Pull Request number.")
    parser.add_argument("--issue", type=int, default=None, help="Target Issue number.")
    parser.add_argument("--append-file", type=Path, default=None, help="Path to file containing activity-record block to append (Issue mode only).")
    parser.add_argument("--body-file", type=Path, default=None, help="Path to file containing new PR body text (PR mode only).")
    parser.add_argument("--body", type=str, default=None, help="Inline string containing new PR body text (PR mode only).")
    parser.add_argument("--write", action="store_true", help="Explicit opt-in flag to execute write (defaults to preview only).")
    parser.add_argument("--adopt-unmarked", action="store_true", help="Opt-in route to adopt candidate unmarked reviewer sections (PR mode only).")
    parser.add_argument("--expected-base-hash", type=str, default=None, help="Expected SHA256 hex digest of remote body before update.")
    parser.add_argument("--backup-dir", type=Path, default=None, help="Directory to store pre-write local recovery backup file.")
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", "Tiflit/DXVK-Companion"), help="Repository owner/repo.")
    parser.add_argument("--token", default=get_default_github_token(), help="GitHub API token.")
    args = parser.parse_args()

    if not is_valid_repo_name(args.repo):
        print(f"ERROR: Invalid repository name: '{args.repo}'", file=sys.stderr)
        return 1

    # Validate target specification (must have exactly one of --pr or --issue)
    if args.pr is not None and args.issue is not None:
        print("ERROR: Mutually exclusive target options: specify either --pr or --issue, not both.", file=sys.stderr)
        return 1
    if args.pr is None and args.issue is None:
        print("ERROR: Exactly one target must be specified: either --pr or --issue.", file=sys.stderr)
        return 1

    client = ExtendedGitHubClient(token=args.token, repo=args.repo)

    # ==========================
    # ISSUE MODE
    # ==========================
    if args.issue is not None:
        if not is_valid_positive_int(args.issue):
            print(f"ERROR: Invalid Issue number: '{args.issue}'", file=sys.stderr)
            return 1

        if args.body_file is not None or args.body is not None:
            print("ERROR: Replacement inputs (--body-file, --body) are not permitted in Issue mode; use --append-file.", file=sys.stderr)
            return 1
        if args.adopt_unmarked:
            print("ERROR: --adopt-unmarked is only supported in PR mode.", file=sys.stderr)
            return 1
        if args.append_file is None:
            print("ERROR: --append-file is required in Issue mode.", file=sys.stderr)
            return 1

        if not args.append_file.is_file():
            safe_name = format_safe_filename_diagnostic(args.append_file)
            print(f"ERROR: Append file not found: '{safe_name}'", file=sys.stderr)
            return 1

        try:
            append_text = args.append_file.read_text(encoding="utf-8-sig")
        except Exception as e:
            print(f"ERROR: Could not read append file: {sanitize_diagnostic(str(e))}", file=sys.stderr)
            return 1

        # Early privacy scan on append_text before parsing, conflict checking, or echoing
        append_violations = scan_for_privacy_violations(append_text)
        if append_violations:
            for line_num, category in append_violations:
                print(f"ERROR: Privacy violation detected in append input on line {line_num}: {category}.", file=sys.stderr)
            return 1

        # Validate that append input contains exactly one bounded activity-record block
        try:
            append_record = parse_and_validate_append_input(append_text)
        except SecurityValidationError as e:
            print(f"ERROR: Append validation error: {sanitize_diagnostic(str(e))}", file=sys.stderr)
            return 1

        # Fetch current remote Issue body
        try:
            remote_issue_data = client.get_issue(args.issue)
        except Exception as e:
            print(f"ERROR: Failed to fetch Issue #{args.issue} from GitHub API: {sanitize_diagnostic(str(e))}", file=sys.stderr)
            return 1

        # Reject Issue target if API response indicates it is a Pull Request
        if "pull_request" in remote_issue_data:
            print(f"ERROR: Target Issue #{args.issue} is a Pull Request; use --pr instead.", file=sys.stderr)
            return 1

        remote_body = remote_issue_data.get("body", "") or ""
        current_hash = compute_body_sha256(remote_body)

        # In write mode, require a valid 64-hex --expected-base-hash
        if args.write:
            if not args.expected_base_hash:
                print("ERROR: --expected-base-hash is required for Issue writes.", file=sys.stderr)
                return 1
            if not re.match(r"^[0-9a-fA-F]{64}$", args.expected_base_hash.strip()):
                print("ERROR: Invalid --expected-base-hash format: must be a 64-character hexadecimal SHA256 string.", file=sys.stderr)
                return 1

        # Check expected base hash if provided
        if args.expected_base_hash:
            expected = args.expected_base_hash.strip().lower()
            if current_hash.lower() != expected:
                print(
                    f"ERROR: Lost-update check failed! Remote Issue body hash ({current_hash[:12]}) "
                    f"does not match expected base hash ({expected[:12]}). Aborting.",
                    file=sys.stderr,
                )
                return 1

        # Check for idempotent no-op or conflicting record reuse (fails closed on malformed remote history)
        try:
            is_noop, is_conflict = check_issue_idempotency(remote_body, append_record)
        except SecurityValidationError as e:
            print(f"ERROR: Remote Issue #{args.issue} contains malformed record markers: {sanitize_diagnostic(str(e))}", file=sys.stderr)
            return 1

        if is_conflict:
            sanitized_id = sanitize_privacy_text(append_record.record_id)
            print(f"ERROR: Conflicting record ID reuse: '{sanitized_id}' already exists in Issue #{args.issue} with different content.", file=sys.stderr)
            return 1

        if is_noop:
            # Privacy check applies to the target body even on retries!
            target_violations = scan_for_privacy_violations(remote_body)
            if target_violations:
                for line_num, category in target_violations:
                    print(f"ERROR: Privacy violation detected on line {line_num}: {category}.", file=sys.stderr)
                return 1

            if not args.write:
                print(f"[PREVIEW] Issue #{args.issue} body update is an idempotent NO-OP:")
                print(f"- Current Remote Body Hash: `{current_hash}`")
                print(f"- Record '{sanitize_privacy_text(append_record.record_id)}' is already present in remote body with identical content.")
                print("- Zero writes required.")
                return 0
            else:
                # In write mode, must perform a verification re-read of remote state before reporting success!
                try:
                    verify_data = client.get_issue(args.issue)
                    verify_body = verify_data.get("body", "") or ""
                    verify_hash = compute_body_sha256(verify_body)
                    if verify_hash != current_hash:
                        print(
                            f"ERROR: Concurrent modification detected during no-op verification! Remote Issue body changed since initial check. Aborting.",
                            file=sys.stderr,
                        )
                        return 1
                    # Verify record is still present and valid
                    verify_records = parse_and_validate_review_records(verify_body)
                    if (
                        append_record.record_id not in verify_records
                        or verify_records[append_record.record_id].full_block.strip() != append_record.full_block.strip()
                    ):
                        print(
                            f"ERROR: Idempotent no-op verification failed: record '{sanitize_privacy_text(append_record.record_id)}' disappeared or changed during verification read. Aborting.",
                            file=sys.stderr,
                        )
                        return 1
                    # Check privacy on verify_body as well
                    v_violations = scan_for_privacy_violations(verify_body)
                    if v_violations:
                        for line_num, category in v_violations:
                            print(f"ERROR: Privacy violation detected on line {line_num}: {category}.", file=sys.stderr)
                        return 1
                except SecurityValidationError as e:
                    print(f"ERROR: Remote Issue #{args.issue} became malformed during verification read: {sanitize_diagnostic(str(e))}", file=sys.stderr)
                    return 1
                except Exception as e:
                    print(
                        f"ERROR: Failed to verify idempotent no-op state: could not re-fetch Issue #{args.issue} ({sanitize_diagnostic(str(e))}). Completion is unverified.",
                        file=sys.stderr,
                    )
                    return 1

                print(f"SUCCESS: Record '{sanitize_privacy_text(append_record.record_id)}' is already present in Issue #{args.issue} with identical content. Idempotent no-op verified; zero writes committed.")
                return 0

        # Construct candidate whole body using validated single record block
        final_body = prepare_issue_candidate(remote_body, append_record.full_block)
        final_hash = compute_body_sha256(final_body)

        # Validate candidate body for privacy leaks (fail closed before output or PATCH)
        privacy_violations = scan_for_privacy_violations(final_body)
        if privacy_violations:
            for line_num, category in privacy_violations:
                print(f"ERROR: Privacy violation detected on line {line_num}: {category}.", file=sys.stderr)
            return 1

        # Preview Mode
        if not args.write:
            print(f"[PREVIEW] Issue #{args.issue} body update (zero writes committed):")
            print(f"- Current Remote Body Hash: `{current_hash}`")
            print(f"- Proposed Target Body Hash: `{final_hash}`")
            print(f"- Appended Activity Record: `{sanitize_privacy_text(append_record.record_id)}` ({append_record.family})")
            print("")
            print("--- DIFF PREVIEW ---")
            diff = difflib.unified_diff(
                remote_body.splitlines(keepends=True),
                final_body.splitlines(keepends=True),
                fromfile=f"Issue-{args.issue}-current",
                tofile=f"Issue-{args.issue}-proposed",
                n=3,
            )
            for diff_line in diff:
                sys.stdout.write(sanitize_diff_line(diff_line))
            print("--- END DIFF PREVIEW ---")
            if is_privacy_sensitive_text(str(args.append_file)) or is_privacy_sensitive_text(args.append_file.name):
                print(
                    f"\nTo commit this change, supply your local file and re-run with: "
                    f"python scripts/ai-workflow/update_pr_body.py --issue {args.issue} --append-file <path-to-append-file> --expected-base-hash {current_hash} --write"
                )
            else:
                print(f"\nTo commit this change, re-run with: python scripts/ai-workflow/update_pr_body.py --issue {args.issue} --append-file {args.append_file.name} --expected-base-hash {current_hash} --write")
            return 0

        # Write Mode
        if not client.token:
            print("ERROR: GitHub token (GITHUB_TOKEN or GH_TOKEN) required for --write.", file=sys.stderr)
            return 1

        # Local recovery backup
        try:
            backup_path = create_prewrite_backup("issue", args.issue, remote_body, args.backup_dir)
            safe_backup_display = format_safe_backup_display(backup_path)
            print(f"Pre-write local recovery backup saved to: {safe_backup_display}")
        except Exception as e:
            print(f"ERROR saving recovery backup: {sanitize_diagnostic(str(e))}", file=sys.stderr)
            return 1

        # Second GET immediately before PATCH (concurrency check)
        try:
            prewrite_data = client.get_issue(args.issue)
            prewrite_body = prewrite_data.get("body", "") or ""
            prewrite_hash = compute_body_sha256(prewrite_body)
            if prewrite_hash != current_hash:
                print(
                    f"ERROR: Concurrent modification detected! Remote Issue body changed between initial fetch "
                    f"({current_hash[:12]}) and pre-write verification ({prewrite_hash[:12]}). Aborting write.",
                    file=sys.stderr,
                )
                return 1
        except Exception as e:
            print(f"ERROR during pre-write verification check: {sanitize_diagnostic(str(e))}", file=sys.stderr)
            return 1

        # Execute PATCH write
        print(f"Writing updated body to Issue #{args.issue}...")
        try:
            client.patch_issue_body(args.issue, final_body)
        except Exception as e:
            print(f"ERROR: Failed to update Issue body: {sanitize_diagnostic(str(e))}", file=sys.stderr)
            return 1

        # Post-write verification
        try:
            post_data = client.get_issue(args.issue)
            post_body = post_data.get("body", "") or ""
            post_hash = compute_body_sha256(post_body)
            if post_hash != final_hash:
                print(
                    f"WARNING: Discrepancy detected after write! Verified hash ({post_hash[:12]}) "
                    f"does not match target hash ({final_hash[:12]}). A write may already have occurred on GitHub, but completion is unverified.",
                    file=sys.stderr,
                )
                return 1
            print(f"SUCCESS: Issue #{args.issue} body updated and verified matching target hash `{final_hash[:12]}`.")
        except Exception as e:
            print(
                f"WARNING: Post-write verification failed: could not re-fetch Issue body ({sanitize_diagnostic(str(e))}). A write may already have occurred on GitHub, but completion is unverified.",
                file=sys.stderr,
            )
            return 1

        return 0

    # ==========================
    # PULL REQUEST MODE
    # ==========================
    if not is_valid_positive_int(args.pr):
        print(f"ERROR: Invalid PR number: '{args.pr}'", file=sys.stderr)
        return 1

    if args.append_file is not None:
        print("ERROR: --append-file is not permitted in PR mode; use --body-file or --body.", file=sys.stderr)
        return 1

    if args.body_file:
        if not args.body_file.is_file():
            safe_name = format_safe_filename_diagnostic(args.body_file)
            print(f"ERROR: Body file not found: '{safe_name}'", file=sys.stderr)
            return 1
        try:
            proposed_body = args.body_file.read_text(encoding="utf-8-sig")
        except Exception as e:
            print(f"ERROR: Could not read body file: {sanitize_diagnostic(str(e))}", file=sys.stderr)
            return 1
    elif args.body is not None:
        proposed_body = args.body
    else:
        print("ERROR: Either --body-file or --body must be specified in PR mode.", file=sys.stderr)
        return 1

    # Early privacy validation on proposed body
    proposed_violations = scan_for_privacy_violations(proposed_body)
    if proposed_violations:
        for line_num, category in proposed_violations:
            print(f"ERROR: Privacy violation detected on line {line_num}: {category}.", file=sys.stderr)
        return 1

    # Fetch current remote PR body
    try:
        remote_pr_data = client.get_pr(args.pr)
    except Exception as e:
        print(f"ERROR: Failed to fetch PR #{args.pr} from GitHub API: {sanitize_diagnostic(str(e))}", file=sys.stderr)
        return 1

    remote_body = remote_pr_data.get("body", "") or ""
    current_hash = compute_body_sha256(remote_body)

    # Check expected base hash if provided
    if args.expected_base_hash:
        expected = args.expected_base_hash.strip().lower()
        if current_hash.lower() != expected:
            print(
                f"ERROR: Lost-update check failed! Remote PR body hash ({current_hash[:12]}) "
                f"does not match expected base hash ({expected[:12]}). Aborting.",
                file=sys.stderr,
            )
            return 1

    # Merge and preserve review records
    try:
        final_body, preserved_ids = merge_and_preserve_review_records(
            remote_body=remote_body,
            proposed_body=proposed_body,
            adopt_unmarked=args.adopt_unmarked,
        )
    except SecurityValidationError as e:
        print(f"ERROR: Review preservation error: {sanitize_diagnostic(str(e))}", file=sys.stderr)
        return 1

    final_hash = compute_body_sha256(final_body)

    # Validate candidate body for privacy leaks (fail closed before output or PATCH)
    privacy_violations = scan_for_privacy_violations(final_body)
    if privacy_violations:
        for line_num, category in privacy_violations:
            print(f"ERROR: Privacy violation detected on line {line_num}: {category}.", file=sys.stderr)
        return 1

    # Preview Mode
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
        for diff_line in diff:
            sys.stdout.write(sanitize_diff_line(diff_line))
        print("--- END DIFF PREVIEW ---")
        print("\nTo commit this change, re-run with explicit --write flag.")
        return 0

    # Write Mode
    if not client.token:
        print("ERROR: GitHub token (GITHUB_TOKEN or GH_TOKEN) required for --write.", file=sys.stderr)
        return 1

    # Local recovery backup
    try:
        backup_path = create_prewrite_backup("pr", args.pr, remote_body, args.backup_dir)
        safe_backup_display = format_safe_backup_display(backup_path)
        print(f"Pre-write local recovery backup saved to: {safe_backup_display}")
    except Exception as e:
        print(f"ERROR saving recovery backup: {sanitize_diagnostic(str(e))}", file=sys.stderr)
        return 1

    # Second GET immediately before PATCH
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
        print(f"ERROR during pre-write verification check: {sanitize_diagnostic(str(e))}", file=sys.stderr)
        return 1

    # Execute PATCH write
    print(f"Writing updated body to PR #{args.pr}...")
    try:
        client.patch_pr_body(args.pr, final_body)
    except Exception as e:
        print(f"ERROR: Failed to update PR body: {sanitize_diagnostic(str(e))}", file=sys.stderr)
        return 1

    # Post-write verification
    try:
        post_data = client.get_pr(args.pr)
        post_body = post_data.get("body", "") or ""
        post_hash = compute_body_sha256(post_body)
        if post_hash != final_hash:
            print(
                f"WARNING: Discrepancy detected after write! Verified hash ({post_hash[:12]}) "
                f"does not match target hash ({final_hash[:12]}). A write may already have occurred on GitHub, but completion is unverified.",
                file=sys.stderr,
            )
            return 1
        print(f"SUCCESS: PR #{args.pr} body updated and verified matching target hash `{final_hash[:12]}`.")
    except Exception as e:
        print(
            f"WARNING: Post-write verification failed: could not re-fetch PR body ({sanitize_diagnostic(str(e))}). A write may already have occurred on GitHub, but completion is unverified.",
            file=sys.stderr,
        )
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
