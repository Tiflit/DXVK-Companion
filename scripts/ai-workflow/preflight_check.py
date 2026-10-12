"""Read-only operator-invoked scope and PR hygiene preflight CLI.

Validates supplied issue body, PR body, and changed-files JSON using
existing repository validation libraries without network calls, subprocesses,
or state mutations.

Authority/approval, Issue identity/labels, live refs, CI freshness, and merge
readiness are NOT VERIFIED.
"""

from __future__ import annotations

import sys
sys.dont_write_bytecode = True

import argparse
import json
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

try:
    from .evaluate_scope import check_files, normalize_allowed_paths_section
    from .parse_contract import ContractParseError, parse_allowed_paths
    from .check_pr_hygiene import verify_pr_body
except ImportError:
    from evaluate_scope import check_files, normalize_allowed_paths_section
    from parse_contract import ContractParseError, parse_allowed_paths
    from check_pr_hygiene import verify_pr_body

NOT_VERIFIED_NOTICE = (
    "NOT VERIFIED: Authority/approval, Issue identity/labels, live refs, "
    "CI freshness, and merge readiness."
)

MAX_VIOLATION_LINES = 5


def print_not_verified_notice(file: Optional[Any] = None) -> None:
    """Prints standard non-verification notice across all CLI exit paths."""
    if file is None:
        file = sys.stdout
    print(file=file)
    print(NOT_VERIFIED_NOTICE, file=file)


class SanitizedArgumentParser(argparse.ArgumentParser):
    """ArgumentParser that prevents echoing raw arguments or host script paths in errors."""

    def __init__(self, *args: Any, **kwargs: Any) -> None:
        kwargs["prog"] = "preflight_check.py"
        kwargs["allow_abbrev"] = False
        super().__init__(*args, **kwargs)

    def error(self, message: str) -> None:
        print("ERROR: Invalid command-line arguments. Use --help for usage syntax.", file=sys.stderr)
        print_not_verified_notice(file=sys.stderr)
        sys.exit(2)

    def print_help(self, file: Optional[Any] = None) -> None:
        if file is None:
            file = sys.stdout
        super().print_help(file=file)
        print_not_verified_notice(file=file)


@dataclass
class PreflightResult:
    scope_valid: bool
    hygiene_valid: bool
    is_unconstrained: bool
    scope_diagnostics: List[str]
    hygiene_diagnostics: List[str]

    @property
    def is_valid(self) -> bool:
        return self.scope_valid and self.hygiene_valid


def sanitize_contract_error(err_msg: str) -> str:
    """Sanitizes ContractParseError messages to avoid echoing raw input or secrets."""
    low = err_msg.lower()
    if "whitespace or prose" in low:
        return "Contract format error: entry in '### Allowed paths' contains whitespace or prose."
    if "drive letters" in low or "backslashes" in low:
        return "Contract format error: entry contains Windows drive letters or backslashes (use forward slashes)."
    if "path traversal" in low or "../" in low:
        return "Contract format error: entry contains path traversal ('../')."
    if "absolute path" in low or "starting with '/'" in low:
        return "Contract format error: entry contains absolute path starting with '/'."
    if "loose opt-out synonym" in low:
        return "Contract format error: entry uses a loose synonym instead of standalone 'Unconstrained'."
    if "mixed 'unconstrained'" in low:
        return "Contract format error: mixed 'Unconstrained' and explicit path patterns."
    if "missing '### allowed paths'" in low or ("missing" in low and "allowed paths" in low):
        return "Contract structure error: missing '### Allowed paths' section."
    if "empty" in low and "allowed paths" in low:
        return "Contract structure error: '### Allowed paths' section is empty."
    if "no path entries" in low or "no valid path patterns" in low:
        return "Contract structure error: no valid path patterns found in '### Allowed paths'."
    return "Contract parse error: invalid allowed paths specification."


def sanitize_hygiene_diagnostic(diag: str) -> str:
    """Sanitizes PR hygiene diagnostics to avoid echoing raw input."""
    low = diag.lower()
    if "pr body is empty" in low:
        return "Hygiene error: PR body is empty."
    if "missing required section heading" in low:
        m = re.search(r"##\s+([A-Za-z ]+)", diag)
        heading = m.group(1).strip() if m else "required heading"
        return f"Hygiene error: missing required section heading '## {heading}'."
    if "conflicting primary issue" in low:
        return "Hygiene error: conflicting primary issue references found in PR body."
    if "only contextual issue references" in low:
        return "Hygiene error: only contextual issue references found; primary issue required."
    if "no primary issue reference" in low:
        return "Hygiene error: no primary issue reference found in PR body."
    return "Hygiene error: PR body metadata validation failed."


def validate_files_json_data(data: Any) -> Tuple[bool, Optional[str]]:
    """Validates the shape of changed-files JSON data."""
    if not isinstance(data, list):
        return False, "Changed files data must be a JSON array of file objects."

    for idx, item in enumerate(data):
        if not isinstance(item, dict):
            return False, f"Item at index {idx} is not a JSON object."
        filename = item.get("filename")
        if filename is None or not isinstance(filename, str) or not filename.strip():
            return False, f"Item at index {idx} is missing a non-empty string 'filename'."
        prev_filename = item.get("previous_filename")
        if prev_filename is not None and not isinstance(prev_filename, str):
            return False, f"Item at index {idx} has invalid 'previous_filename' (must be string or null)."

    return True, None


def read_file_safely(path_str: str, arg_name: str) -> Tuple[Optional[str], Optional[str]]:
    """Safely reads a regular file with utf-8-sig encoding and sanitized reporting."""
    try:
        p = Path(path_str)
        if not p.exists():
            return None, f"Input {arg_name} does not exist."
        if not p.is_file():
            return None, f"Input {arg_name} is not a regular file."
        content = p.read_text(encoding="utf-8-sig")
        return content, None
    except UnicodeDecodeError:
        return None, f"Input {arg_name} could not be decoded as UTF-8."
    except Exception:
        return None, f"Input {arg_name} could not be read."


def run_preflight(
    issue_body: str,
    pr_body: str,
    files_data: List[Dict[str, Any]],
) -> PreflightResult:
    """Executes scope and hygiene checks on supplied texts and file manifest."""
    scope_diags: List[str] = []
    scope_valid = True
    is_unconstrained = False

    # 1. Scope evaluation
    try:
        normalized_issue = normalize_allowed_paths_section(issue_body)
        allowed = parse_allowed_paths(normalized_issue)
        is_unconstrained = allowed.is_unconstrained

        if not is_unconstrained:
            violating_indexes: List[int] = []
            for idx, file_info in enumerate(files_data):
                single_check = check_files([file_info], allowed.patterns, is_unconstrained=False)
                if not single_check.is_valid:
                    violating_indexes.append(idx)

            if violating_indexes:
                scope_valid = False
                total_violations = len(violating_indexes)
                displayed = violating_indexes[:MAX_VIOLATION_LINES]
                for idx in displayed:
                    scope_diags.append(
                        f"Scope violation: item at manifest index {idx} violates allowed contract paths."
                    )
                if total_violations > MAX_VIOLATION_LINES:
                    truncated = total_violations - MAX_VIOLATION_LINES
                    scope_diags.append(
                        f"... and {truncated} additional scope violation(s) truncated."
                    )
    except ContractParseError as e:
        scope_valid = False
        scope_diags.append(sanitize_contract_error(str(e)))

    # 2. PR hygiene evaluation
    hygiene_res = verify_pr_body(pr_body)
    hygiene_valid = hygiene_res.is_valid
    hygiene_diags = [sanitize_hygiene_diagnostic(d) for d in hygiene_res.diagnostics]

    return PreflightResult(
        scope_valid=scope_valid,
        hygiene_valid=hygiene_valid,
        is_unconstrained=is_unconstrained,
        scope_diagnostics=scope_diags,
        hygiene_diagnostics=hygiene_diags,
    )


def print_report(result: PreflightResult) -> None:
    print("=== Preflight Validation Report ===")
    if result.scope_valid:
        scope_status = "PASS (Unconstrained)" if result.is_unconstrained else "PASS"
    else:
        scope_status = "FAIL"
    print(f"Scope check:   {scope_status}")
    if not result.scope_valid:
        for d in result.scope_diagnostics:
            print(f"  - {d}")

    print(f"PR hygiene:    {'PASS' if result.hygiene_valid else 'FAIL'}")
    if not result.hygiene_valid:
        for d in result.hygiene_diagnostics:
            print(f"  - {d}")

    print("-----------------------------------")
    print(f"Overall:       {'PASS' if result.is_valid else 'FAIL'}")
    print_not_verified_notice(file=sys.stdout)
    print("===================================")


def main(argv: Optional[List[str]] = None) -> int:
    parser = SanitizedArgumentParser(
        description="Read-only operator-invoked scope and PR hygiene preflight.",
    )
    parser.add_argument(
        "--issue-body-file",
        required=True,
        help="Path to file containing Issue description text.",
    )
    parser.add_argument(
        "--pr-body-file",
        required=True,
        help="Path to file containing Pull Request body text.",
    )
    parser.add_argument(
        "--files-json",
        required=True,
        help="Path to JSON file containing changed files list.",
    )

    args = parser.parse_args(argv)

    # 1. Read files safely with utf-8-sig
    issue_body, err = read_file_safely(args.issue_body_file, "--issue-body-file")
    if err:
        print(f"ERROR: {err}", file=sys.stderr)
        print_not_verified_notice(file=sys.stderr)
        return 1

    pr_body, err = read_file_safely(args.pr_body_file, "--pr-body-file")
    if err:
        print(f"ERROR: {err}", file=sys.stderr)
        print_not_verified_notice(file=sys.stderr)
        return 1

    files_content, err = read_file_safely(args.files_json, "--files-json")
    if err:
        print(f"ERROR: {err}", file=sys.stderr)
        print_not_verified_notice(file=sys.stderr)
        return 1

    # 2. Parse and validate JSON
    try:
        files_data = json.loads(files_content)
    except json.JSONDecodeError:
        print("ERROR: Input --files-json does not contain valid JSON syntax.", file=sys.stderr)
        print_not_verified_notice(file=sys.stderr)
        return 1

    is_valid_shape, shape_err = validate_files_json_data(files_data)
    if not is_valid_shape:
        print(f"ERROR: Input --files-json has invalid schema: {shape_err}", file=sys.stderr)
        print_not_verified_notice(file=sys.stderr)
        return 1

    # 3. Run preflight
    result = run_preflight(issue_body, pr_body, files_data)
    print_report(result)

    return 0 if result.is_valid else 1


if __name__ == "__main__":
    sys.exit(main())
