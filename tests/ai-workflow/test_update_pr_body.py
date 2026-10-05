"""Unit and regression tests for opt-in PR-body and Issue append helper with review preservation."""

from __future__ import annotations

import io
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ai-workflow"))

import update_pr_body
from update_pr_body import (
    LostUpdateError,
    PrivacyViolationError,
    ReviewRecord,
    SecurityValidationError,
    adopt_unmarked_review_records,
    check_issue_idempotency,
    compute_body_sha256,
    create_prewrite_backup,
    merge_and_preserve_review_records,
    parse_and_validate_append_input,
    parse_and_validate_review_records,
    prepare_issue_candidate,
    scan_for_privacy_violations,
)


class MockHttpResponse:
    """Mock HTTP response object."""

    def __init__(self, data, status: int = 200):
        self.status = status
        if isinstance(data, (dict, list)):
            self._bytes = json.dumps(data).encode("utf-8")
        elif isinstance(data, str):
            self._bytes = data.encode("utf-8")
        elif isinstance(data, bytes):
            self._bytes = data
        else:
            self._bytes = b""

    def read(self) -> bytes:
        return self._bytes

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        pass


class TestUpdatePrBody(unittest.TestCase):
    """Tests covering PR review preservation, Issue whole-body append, privacy scanning, and concurrency guards."""

    def setUp(self):
        self.repo = "Tiflit/DXVK-Companion"
        self.pr_number = 31
        self.issue_number = 42
        self.sample_review_block = (
            "<!-- AI-REVIEW-RECORD: chatgpt-2026-10-04-rev1 -->\n"
            "## ChatGPT coordinator verification\n"
            "Verified test suite: 92/92 passed. Result: PASS.\n"
            "<!-- AI-REVIEW-RECORD-END -->"
        )
        self.sample_remote_body = (
            "## Primary Issue\nFixes #31\n\n"
            "## Summary\nInitial summary.\n\n"
            "## Scope\nscripts/ai-workflow/**\n\n"
            "## Verification\n92 tests passed.\n\n"
            "## Documentation\nUpdated.\n\n"
            f"{self.sample_review_block}\n"
        )
        self.sample_issue_body = (
            "## Objective\n"
            "Extend helper with safe append-only Issue activity-record mode.\n\n"
            "## Assignment\n"
            "Next owner: Gemini for remaining dashboard/body verification; assignment ID chatgpt-20261005-issue40-closeout.\n\n"
            "## Stage A Resumption Checkpoint\n"
            "- Reviewed Head SHA: df261e2ec2191573cf7209da6b6bc2ddffa7d533\n"
            "- Base SHA: 54a886674f154d2ec4f0e0c0482f150ad63e1df0\n"
        )
        self.sample_append_block = (
            "<!-- AI-POST-MERGE-RECORD: gemini-20261005-issue42-postmerge -->\n"
            "## Post-Merge Verification & Activity Record — Issue #42\n"
            "Verified on main: d464aef5429404eec201202e4c8690370363d18a. Result: PASS.\n"
            "<!-- AI-POST-MERGE-RECORD-END -->"
        )

    # =========================================================================
    # 1. Parsing & Validation of Markers (Supported Families & Symmetry)
    # =========================================================================

    def test_parse_valid_records(self):
        records = parse_and_validate_review_records(self.sample_remote_body)
        self.assertIn("chatgpt-2026-10-04-rev1", records)
        self.assertIn("Verified test suite", records["chatgpt-2026-10-04-rev1"].inner_content)
        self.assertEqual(records["chatgpt-2026-10-04-rev1"].family, "REVIEW")

    def test_parse_supported_families(self):
        body = (
            "<!-- AI-REVIEW-RECORD: rev-1 -->\nReview text\n<!-- AI-REVIEW-RECORD-END -->\n\n"
            "<!-- AI-POST-MERGE-RECORD: pm-1 -->\nPost merge text\n<!-- AI-POST-MERGE-RECORD-END -->\n\n"
            "<!-- AI-ASSIGNMENT-RECORD: as-1 -->\nAssignment text\n<!-- AI-ASSIGNMENT-RECORD-END -->"
        )
        records = parse_and_validate_review_records(body)
        self.assertEqual(len(records), 3)
        self.assertEqual(records["rev-1"].family, "REVIEW")
        self.assertEqual(records["pm-1"].family, "POST-MERGE")
        self.assertEqual(records["as-1"].family, "ASSIGNMENT")

    def test_reject_unclosed_marker(self):
        bad_text = "<!-- AI-REVIEW-RECORD: rec1 -->\nSome review\nNo end tag."
        with self.assertRaises(SecurityValidationError) as cm:
            parse_and_validate_review_records(bad_text)
        self.assertIn("unclosed", str(cm.exception).lower())

    def test_reject_orphan_end_tag(self):
        bad_text = "Some review\n<!-- AI-REVIEW-RECORD-END -->"
        with self.assertRaises(SecurityValidationError) as cm:
            parse_and_validate_review_records(bad_text)
        self.assertIn("orphan", str(cm.exception).lower())

    def test_reject_nested_tags(self):
        nested_text = (
            "<!-- AI-REVIEW-RECORD: outer -->\n"
            "<!-- AI-REVIEW-RECORD: inner -->\n"
            "Inner text\n"
            "<!-- AI-REVIEW-RECORD-END -->\n"
            "<!-- AI-REVIEW-RECORD-END -->"
        )
        with self.assertRaises(SecurityValidationError) as cm:
            parse_and_validate_review_records(nested_text)
        self.assertIn("nested", str(cm.exception).lower())

    def test_reject_duplicate_record_ids(self):
        dup_text = (
            "<!-- AI-REVIEW-RECORD: dup1 -->\nReview 1\n<!-- AI-REVIEW-RECORD-END -->\n"
            "<!-- AI-REVIEW-RECORD: dup1 -->\nReview 2\n<!-- AI-REVIEW-RECORD-END -->\n"
        )
        with self.assertRaises(SecurityValidationError) as cm:
            parse_and_validate_review_records(dup_text)
        self.assertIn("duplicate", str(cm.exception).lower())

    def test_reject_malformed_identifier(self):
        bad_text = "<!-- AI-REVIEW-RECORD: invalid id with spaces -->\nText\n<!-- AI-REVIEW-RECORD-END -->"
        with self.assertRaises(SecurityValidationError) as cm:
            parse_and_validate_review_records(bad_text)
        self.assertIn("invalid review record identifier", str(cm.exception).lower())

    def test_reject_mismatched_family_tags(self):
        mismatched_text = (
            "<!-- AI-POST-MERGE-RECORD: pm-bad -->\n"
            "Content\n"
            "<!-- AI-REVIEW-RECORD-END -->"
        )
        with self.assertRaises(SecurityValidationError) as cm:
            parse_and_validate_review_records(mismatched_text)
        self.assertIn("mismatched record marker tags", str(cm.exception).lower())

    def test_reject_unsupported_family_tags(self):
        unsupported = (
            "<!-- AI-TELEMETRY-RECORD: tel-1 -->\n"
            "Data\n"
            "<!-- AI-TELEMETRY-RECORD-END -->"
        )
        with self.assertRaises(SecurityValidationError) as cm:
            parse_and_validate_review_records(unsupported)
        self.assertIn("unsupported record marker family", str(cm.exception).lower())

    # =========================================================================
    # 2. Privacy Scans (Fail Closed, Leak Prevention)
    # =========================================================================

    def test_privacy_scan_detects_windows_path_backslash(self):
        text = "Built at C:\\Users\\developer\\DXVK-Companion\\bin"
        violations = scan_for_privacy_violations(text)
        self.assertEqual(len(violations), 1)
        line_num, cat = violations[0]
        self.assertEqual(line_num, 1)
        self.assertEqual(cat, "Windows personal-home path")

    def test_privacy_scan_detects_windows_path_forward_slash(self):
        text = "Artifact: file:///c:/users/runner/work/out.txt"
        violations = scan_for_privacy_violations(text)
        self.assertEqual(len(violations), 1)
        self.assertEqual(violations[0][1], "Windows personal-home path")

    def test_privacy_scan_detects_posix_home_path(self):
        text = "Log at /home/developer/debug.log"
        violations = scan_for_privacy_violations(text)
        self.assertEqual(len(violations), 1)
        self.assertEqual(violations[0][1], "POSIX personal-home path")

    def test_privacy_scan_detects_credentials_and_tokens(self):
        text = "Config token: ghp_1234567890abcdef1234567890abcdef"
        violations = scan_for_privacy_violations(text)
        self.assertEqual(len(violations), 1)
        self.assertEqual(violations[0][1], "GitHub credential or bearer token")

    def test_privacy_scan_allows_generic_dev_paths(self):
        text = (
            "Worktree at D:\\dev\\DXVK-Companion-issue-42\n"
            "Secondary checkout at C:\\dev\\DXVK-Companion\n"
            "Repository docs/AI-DEVELOPMENT-WORKFLOW.md\n"
            "URL https://github.com/Tiflit/DXVK-Companion/issues/42\n"
        )
        violations = scan_for_privacy_violations(text)
        self.assertEqual(len(violations), 0)

    # =========================================================================
    # 3. PR Review Preservation & Modifications (Existing Regressions)
    # =========================================================================

    def test_merge_preserves_remote_records_when_proposed_has_no_markers(self):
        proposed = "## Summary\nNew proposed summary without review markers."
        merged, preserved_ids = merge_and_preserve_review_records(
            remote_body=self.sample_remote_body,
            proposed_body=proposed,
        )
        self.assertIn("chatgpt-2026-10-04-rev1", preserved_ids)
        self.assertIn("## Review & Verification History", merged)
        self.assertIn(self.sample_review_block, merged)

    def test_merge_with_existing_section_appends_cleanly(self):
        proposed = "## Summary\nNew summary.\n\n## Review & Verification History\n"
        merged, preserved_ids = merge_and_preserve_review_records(
            remote_body=self.sample_remote_body,
            proposed_body=proposed,
        )
        self.assertIn("chatgpt-2026-10-04-rev1", preserved_ids)
        self.assertEqual(merged.count("## Review & Verification History"), 1)
        self.assertIn(self.sample_review_block, merged)

    def test_allow_new_record_with_unique_id(self):
        new_block = (
            "<!-- AI-REVIEW-RECORD: new-review -->\nNew review.\n<!-- AI-REVIEW-RECORD-END -->\n"
        )
        proposed = f"## Summary\nUpdated.\n\n{self.sample_review_block}\n\n{new_block}"
        merged, preserved_ids = merge_and_preserve_review_records(
            remote_body=self.sample_remote_body,
            proposed_body=proposed,
        )
        self.assertIn("new-review", merged)
        self.assertIn("chatgpt-2026-10-04-rev1", merged)

    def test_reject_accidental_deletion_of_historical_record(self):
        proposed = (
            "## Summary\nUpdated.\n\n"
            "<!-- AI-REVIEW-RECORD: different-record -->\nOther review.\n<!-- AI-REVIEW-RECORD-END -->\n"
        )
        with self.assertRaises(SecurityValidationError) as cm:
            merge_and_preserve_review_records(
                remote_body=self.sample_remote_body,
                proposed_body=proposed,
            )
        self.assertIn("accidental deletion", str(cm.exception).lower())

    def test_reject_accidental_modification_of_historical_record(self):
        modified_block = (
            "<!-- AI-REVIEW-RECORD: chatgpt-2026-10-04-rev1 -->\n"
            "## ChatGPT coordinator verification\n"
            "Tampered review text.\n"
            "<!-- AI-REVIEW-RECORD-END -->"
        )
        proposed = f"## Summary\nUpdated.\n\n{modified_block}\n"
        with self.assertRaises(SecurityValidationError) as cm:
            merge_and_preserve_review_records(
                remote_body=self.sample_remote_body,
                proposed_body=proposed,
            )
        self.assertIn("accidental modification", str(cm.exception).lower())

    def test_adopt_unmarked_candidate_headings(self):
        unmarked_remote = (
            "## Primary Issue\nFixes #31\n\n"
            "## Summary\nSummary.\n\n"
            "## ChatGPT coordinator verification\n"
            "Verified test suite: 92 passed. Result: PASS.\n\n"
            "## Next Steps\nDo something."
        )
        adopted_body, adopted_ids = adopt_unmarked_review_records(unmarked_remote)
        self.assertEqual(len(adopted_ids), 1)
        self.assertTrue(adopted_ids[0].startswith("adopted-chatgpt-coordinator-verification"))
        self.assertIn(f"<!-- AI-REVIEW-RECORD: {adopted_ids[0]} -->", adopted_body)
        self.assertIn("<!-- AI-REVIEW-RECORD-END -->", adopted_body)

    def test_without_adopt_unmarked_headings_are_untouched(self):
        unmarked_remote = (
            "## Summary\nSummary.\n\n"
            "## ChatGPT coordinator verification\n"
            "Verified test suite.\n"
        )
        merged, preserved_ids = merge_and_preserve_review_records(
            remote_body=unmarked_remote,
            proposed_body="## Summary\nNew summary.\n",
            adopt_unmarked=False,
        )
        self.assertEqual(len(preserved_ids), 0)
        self.assertNotIn("<!-- AI-REVIEW-RECORD:", merged)

    # =========================================================================
    # 4. Issue Whole-Body Preservation & Single-Record Append
    # =========================================================================

    def test_issue_preserves_whole_body_including_unmarked_assignment(self):
        candidate = prepare_issue_candidate(self.sample_issue_body, self.sample_append_block)
        # Verify the entire original body is preserved character-for-character
        self.assertTrue(candidate.startswith(self.sample_issue_body))
        self.assertIn("chatgpt-20261005-issue40-closeout", candidate)
        self.assertIn("df261e2ec2191573cf7209da6b6bc2ddffa7d533", candidate)
        self.assertTrue(candidate.endswith(self.sample_append_block))
        self.assertIn("\n\n" + self.sample_append_block, candidate)

    def test_issue_preserves_trailing_whitespace_and_newlines(self):
        remote_body_with_trailing = self.sample_issue_body + "   \n\n"
        candidate = prepare_issue_candidate(remote_body_with_trailing, self.sample_append_block)
        self.assertTrue(candidate.startswith(remote_body_with_trailing))

    def test_issue_handles_null_and_empty_remote_body(self):
        cand_null = prepare_issue_candidate(None, self.sample_append_block)
        self.assertEqual(cand_null, self.sample_append_block)
        cand_empty = prepare_issue_candidate("", self.sample_append_block)
        self.assertEqual(cand_empty, self.sample_append_block)

    def test_append_input_requires_exactly_one_record_block(self):
        # Zero record blocks
        with self.assertRaises(SecurityValidationError) as cm:
            parse_and_validate_append_input("## Some Heading\nPlain text without markers.")
        self.assertIn("exactly one bounded", str(cm.exception).lower())

        # Multiple record blocks
        two_blocks = f"{self.sample_append_block}\n\n{self.sample_review_block}"
        with self.assertRaises(SecurityValidationError) as cm:
            parse_and_validate_append_input(two_blocks)
        self.assertIn("multiple activity-record blocks", str(cm.exception).lower())

        # Exactly one valid block
        rec = parse_and_validate_append_input(self.sample_append_block)
        self.assertEqual(rec.record_id, "gemini-20261005-issue42-postmerge")
        self.assertEqual(rec.family, "POST-MERGE")

    def test_append_rejects_conflicting_record_id_reuse(self):
        remote_with_block = f"{self.sample_issue_body}\n\n{self.sample_append_block}"
        conflicting_append = (
            "<!-- AI-POST-MERGE-RECORD: gemini-20261005-issue42-postmerge -->\n"
            "Different content!\n"
            "<!-- AI-POST-MERGE-RECORD-END -->"
        )
        rec = parse_and_validate_append_input(conflicting_append)
        is_noop, is_conflict = check_issue_idempotency(remote_with_block, rec)
        self.assertFalse(is_noop)
        self.assertTrue(is_conflict)

    def test_append_idempotent_noop_on_identical_record_id_and_content(self):
        remote_with_block = f"{self.sample_issue_body}\n\n{self.sample_append_block}"
        rec = parse_and_validate_append_input(self.sample_append_block)
        is_noop, is_conflict = check_issue_idempotency(remote_with_block, rec)
        self.assertTrue(is_noop)
        self.assertFalse(is_conflict)

    # =========================================================================
    # 5. CLI Route & Mode Mutex Tests
    # =========================================================================

    def test_target_validation_both_pr_and_issue_rejected(self):
        test_args = ["update_pr_body.py", "--pr", "31", "--issue", "42"]
        with patch.object(sys, "argv", test_args):
            with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                exit_code = update_pr_body.main()
                self.assertEqual(exit_code, 1)
                self.assertIn("mutually exclusive", mock_err.getvalue().lower())

    def test_target_validation_neither_pr_nor_issue_rejected(self):
        test_args = ["update_pr_body.py"]
        with patch.object(sys, "argv", test_args):
            with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                exit_code = update_pr_body.main()
                self.assertEqual(exit_code, 1)
                self.assertIn("exactly one target", mock_err.getvalue().lower())

    def test_issue_mode_rejects_body_and_body_file(self):
        with tempfile.NamedTemporaryFile("w", delete=False) as f:
            f.write("Some body")
            f_path = Path(f.name)
        try:
            test_args = ["update_pr_body.py", "--issue", "42", "--body", "inline body"]
            with patch.object(sys, "argv", test_args):
                with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 1)
                    self.assertIn("replacement inputs", mock_err.getvalue().lower())

            test_args2 = ["update_pr_body.py", "--issue", "42", "--body-file", str(f_path)]
            with patch.object(sys, "argv", test_args2):
                with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 1)
                    self.assertIn("replacement inputs", mock_err.getvalue().lower())
        finally:
            f_path.unlink()

    def test_issue_mode_rejects_adopt_unmarked(self):
        test_args = ["update_pr_body.py", "--issue", "42", "--adopt-unmarked"]
        with patch.object(sys, "argv", test_args):
            with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                exit_code = update_pr_body.main()
                self.assertEqual(exit_code, 1)
                self.assertIn("--adopt-unmarked is only supported in pr mode", mock_err.getvalue().lower())

    def test_issue_mode_requires_append_file(self):
        test_args = ["update_pr_body.py", "--issue", "42"]
        with patch.object(sys, "argv", test_args):
            with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                exit_code = update_pr_body.main()
                self.assertEqual(exit_code, 1)
                self.assertIn("--append-file is required in issue mode", mock_err.getvalue().lower())

    def test_pr_mode_rejects_append_file(self):
        with tempfile.NamedTemporaryFile("w", delete=False) as f:
            f.write(self.sample_append_block)
            f_path = Path(f.name)
        try:
            test_args = ["update_pr_body.py", "--pr", "31", "--append-file", str(f_path)]
            with patch.object(sys, "argv", test_args):
                with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 1)
                    self.assertIn("--append-file is not permitted in pr mode", mock_err.getvalue().lower())
        finally:
            f_path.unlink()

    @patch("urllib.request.urlopen")
    def test_issue_target_is_pull_request_rejected(self, mock_urlopen):
        # GitHub returns "pull_request": {...} when an issue is actually a PR
        mock_urlopen.return_value = MockHttpResponse({
            "number": 42,
            "body": self.sample_issue_body,
            "pull_request": {"url": "https://api.github.com/repos/Tiflit/DXVK-Companion/pulls/42"},
        })
        with tempfile.NamedTemporaryFile("w", delete=False) as f:
            f.write(self.sample_append_block)
            f_path = Path(f.name)
        try:
            test_args = ["update_pr_body.py", "--issue", "42", "--append-file", str(f_path)]
            with patch.object(sys, "argv", test_args):
                with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 1)
                    self.assertIn("target issue #42 is a pull request", mock_err.getvalue().lower())
        finally:
            f_path.unlink()

    # =========================================================================
    # 6. Privacy Fail-Closed CLI Output Safety
    # =========================================================================

    @patch("urllib.request.urlopen")
    def test_privacy_scan_fails_closed_in_cli_and_suppresses_sensitive_text(self, mock_urlopen):
        mock_urlopen.return_value = MockHttpResponse({
            "number": 42,
            "body": self.sample_issue_body,
        })
        private_secret_path = "C:\\Users\\alice_private\\Documents\\secret.key"
        leaking_append = (
            "<!-- AI-POST-MERGE-RECORD: leak-rec -->\n"
            f"Here is a private path: {private_secret_path}\n"
            "<!-- AI-POST-MERGE-RECORD-END -->"
        )
        with tempfile.NamedTemporaryFile("w", delete=False) as f:
            f.write(leaking_append)
            f_path = Path(f.name)
        try:
            test_args = ["update_pr_body.py", "--issue", "42", "--append-file", str(f_path)]
            with patch.object(sys, "argv", test_args):
                with patch("sys.stdout", new_callable=io.StringIO) as mock_out:
                    with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                        exit_code = update_pr_body.main()
                        self.assertEqual(exit_code, 1)
                        err_text = mock_err.getvalue()
                        out_text = mock_out.getvalue()
                        # Category must be reported
                        self.assertIn("Windows personal-home path", err_text)
                        # Sensitive private string must NEVER be echoed!
                        self.assertNotIn("alice_private", err_text)
                        self.assertNotIn("alice_private", out_text)
                        self.assertNotIn(private_secret_path, err_text)
                        self.assertNotIn(private_secret_path, out_text)
        finally:
            f_path.unlink()

    @patch("urllib.request.urlopen")
    def test_privacy_scan_enforced_in_pr_mode_before_diff(self, mock_urlopen):
        mock_urlopen.return_value = MockHttpResponse({
            "number": 31,
            "body": self.sample_remote_body,
        })
        leaking_pr_body = (
            "## Summary\nHere is /home/secret_user/test\n\n"
            "## Scope\nscripts/\n\n## Verification\nDone\n\n## Documentation\nDone"
        )
        test_args = ["update_pr_body.py", "--pr", "31", "--body", leaking_pr_body]
        with patch.object(sys, "argv", test_args):
            with patch("sys.stdout", new_callable=io.StringIO) as mock_out:
                with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 1)
                    self.assertIn("POSIX personal-home path", mock_err.getvalue())
                    self.assertNotIn("secret_user", mock_err.getvalue())
                    self.assertNotIn("DIFF PREVIEW", mock_out.getvalue())

    # =========================================================================
    # 7. Issue Write, Freshness, Lost Updates & Recovery
    # =========================================================================

    @patch("urllib.request.urlopen")
    def test_issue_preview_mode_zero_writes(self, mock_urlopen):
        mock_urlopen.return_value = MockHttpResponse({
            "number": 42,
            "body": self.sample_issue_body,
        })
        with tempfile.NamedTemporaryFile("w", delete=False) as f:
            f.write(self.sample_append_block)
            f_path = Path(f.name)
        try:
            test_args = ["update_pr_body.py", "--issue", "42", "--append-file", str(f_path)]
            with patch.object(sys, "argv", test_args):
                with patch("sys.stdout", new_callable=io.StringIO) as mock_out:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 0)
                    out = mock_out.getvalue()
                    self.assertIn("[PREVIEW] Issue #42 body update", out)
                    self.assertIn("Current Remote Body Hash", out)
                    self.assertIn("Proposed Target Body Hash", out)
                    self.assertIn("--- DIFF PREVIEW ---", out)
        finally:
            f_path.unlink()

    @patch("urllib.request.urlopen")
    def test_issue_write_requires_valid_64_hex_expected_base_hash(self, mock_urlopen):
        mock_urlopen.return_value = MockHttpResponse({
            "number": 42,
            "body": self.sample_issue_body,
        })
        with tempfile.NamedTemporaryFile("w", delete=False) as f:
            f.write(self.sample_append_block)
            f_path = Path(f.name)
        try:
            # Missing expected base hash
            test_args = ["update_pr_body.py", "--issue", "42", "--append-file", str(f_path), "--write", "--token", "tok"]
            with patch.object(sys, "argv", test_args):
                with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 1)
                    self.assertIn("--expected-base-hash is required", mock_err.getvalue())

            # Invalid format (not 64 hex)
            test_args2 = ["update_pr_body.py", "--issue", "42", "--append-file", str(f_path), "--write", "--token", "tok", "--expected-base-hash", "bad_hash_123"]
            with patch.object(sys, "argv", test_args2):
                with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 1)
                    self.assertIn("invalid --expected-base-hash format", mock_err.getvalue().lower())
        finally:
            f_path.unlink()

    @patch("urllib.request.urlopen")
    def test_issue_write_aborts_on_stale_expected_base_hash_zero_patch(self, mock_urlopen):
        mock_urlopen.return_value = MockHttpResponse({
            "number": 42,
            "body": self.sample_issue_body,
        })
        with tempfile.NamedTemporaryFile("w", delete=False) as f:
            f.write(self.sample_append_block)
            f_path = Path(f.name)
        try:
            stale_hash = "0" * 64
            test_args = [
                "update_pr_body.py",
                "--issue", "42",
                "--append-file", str(f_path),
                "--expected-base-hash", stale_hash,
                "--write",
                "--token", "tok",
            ]
            with patch.object(sys, "argv", test_args):
                with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 1)
                    self.assertIn("lost-update check failed", mock_err.getvalue().lower())

            # Confirm no PATCH request was made
            for call in mock_urlopen.call_args_list:
                req = call[0][0]
                method = req.get_method() if hasattr(req, "get_method") else "GET"
                self.assertNotEqual(method, "PATCH")
        finally:
            f_path.unlink()

    @patch("urllib.request.urlopen")
    def test_issue_write_aborts_on_prewrite_concurrency_detection_zero_patch(self, mock_urlopen):
        initial_body = self.sample_issue_body
        concurrent_body = self.sample_issue_body + "\n<!-- Concurrent edit by human -->"
        expected_hash = compute_body_sha256(initial_body)

        call_count = 0
        def fake_urlopen(req, *args, **kwargs):
            nonlocal call_count
            call_count += 1
            if call_count == 1:
                return MockHttpResponse({"number": 42, "body": initial_body})
            elif call_count == 2:
                # Pre-write verification read detects remote change!
                return MockHttpResponse({"number": 42, "body": concurrent_body})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        with tempfile.TemporaryDirectory() as tmp_backup_dir:
            with tempfile.NamedTemporaryFile("w", delete=False) as f:
                f.write(self.sample_append_block)
                f_path = Path(f.name)
            try:
                test_args = [
                    "update_pr_body.py",
                    "--issue", "42",
                    "--append-file", str(f_path),
                    "--expected-base-hash", expected_hash,
                    "--backup-dir", tmp_backup_dir,
                    "--write",
                    "--token", "tok",
                ]
                with patch.object(sys, "argv", test_args):
                    with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                        exit_code = update_pr_body.main()
                        self.assertEqual(exit_code, 1)
                        self.assertIn("concurrent modification detected", mock_err.getvalue().lower())

                for call in mock_urlopen.call_args_list:
                    req = call[0][0]
                    method = req.get_method() if hasattr(req, "get_method") else "GET"
                    self.assertNotEqual(method, "PATCH")
            finally:
                f_path.unlink()

    @patch("urllib.request.urlopen")
    def test_issue_write_success_flow_with_backup_and_readback(self, mock_urlopen):
        remote_body = self.sample_issue_body
        expected_hash = compute_body_sha256(remote_body)
        expected_final = prepare_issue_candidate(remote_body, self.sample_append_block)

        call_count = 0
        def fake_urlopen(req, *args, **kwargs):
            nonlocal call_count
            call_count += 1
            method = req.get_method() if hasattr(req, "get_method") else "GET"
            if method == "PATCH":
                return MockHttpResponse({"number": 42, "body": expected_final})
            if call_count == 1:
                return MockHttpResponse({"number": 42, "body": remote_body})
            elif call_count == 2:
                return MockHttpResponse({"number": 42, "body": remote_body})
            else:
                # Post-write read-back verification
                return MockHttpResponse({"number": 42, "body": expected_final})

        mock_urlopen.side_effect = fake_urlopen

        with tempfile.TemporaryDirectory() as tmp_backup_dir:
            with tempfile.NamedTemporaryFile("w", delete=False) as f:
                f.write(self.sample_append_block)
                f_path = Path(f.name)
            try:
                test_args = [
                    "update_pr_body.py",
                    "--issue", "42",
                    "--append-file", str(f_path),
                    "--expected-base-hash", expected_hash,
                    "--backup-dir", tmp_backup_dir,
                    "--write",
                    "--token", "tok",
                ]
                with patch.object(sys, "argv", test_args):
                    with patch("sys.stdout", new_callable=io.StringIO) as mock_out:
                        exit_code = update_pr_body.main()
                        self.assertEqual(exit_code, 0)
                        self.assertIn("SUCCESS: Issue #42 body updated", mock_out.getvalue())

                # Verify target-qualified backup was created
                backups = list(Path(tmp_backup_dir).glob("issue_42_body_backup_*.md"))
                self.assertEqual(len(backups), 1)
                self.assertEqual(backups[0].read_text(encoding="utf-8"), remote_body)
            finally:
                f_path.unlink()

    @patch("urllib.request.urlopen")
    def test_issue_write_post_write_mismatch_returns_nonzero(self, mock_urlopen):
        remote_body = self.sample_issue_body
        expected_hash = compute_body_sha256(remote_body)
        expected_final = prepare_issue_candidate(remote_body, self.sample_append_block)
        discrepant_final = expected_final + "\nUnexpected modification"

        call_count = 0
        def fake_urlopen(req, *args, **kwargs):
            nonlocal call_count
            call_count += 1
            method = req.get_method() if hasattr(req, "get_method") else "GET"
            if method == "PATCH":
                return MockHttpResponse({"number": 42, "body": expected_final})
            if call_count <= 2:
                return MockHttpResponse({"number": 42, "body": remote_body})
            else:
                # Post-write read-back returns discrepancy!
                return MockHttpResponse({"number": 42, "body": discrepant_final})

        mock_urlopen.side_effect = fake_urlopen

        with tempfile.TemporaryDirectory() as tmp_backup_dir:
            with tempfile.NamedTemporaryFile("w", delete=False) as f:
                f.write(self.sample_append_block)
                f_path = Path(f.name)
            try:
                test_args = [
                    "update_pr_body.py",
                    "--issue", "42",
                    "--append-file", str(f_path),
                    "--expected-base-hash", expected_hash,
                    "--backup-dir", tmp_backup_dir,
                    "--write",
                    "--token", "tok",
                ]
                with patch.object(sys, "argv", test_args):
                    with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                        exit_code = update_pr_body.main()
                        self.assertEqual(exit_code, 1)
                        err = mock_err.getvalue()
                        self.assertIn("discrepancy detected after write", err.lower())
                        self.assertIn("completion is unverified", err.lower())
            finally:
                f_path.unlink()

    @patch("urllib.request.urlopen")
    def test_issue_write_post_write_read_failure_returns_nonzero(self, mock_urlopen):
        remote_body = self.sample_issue_body
        expected_hash = compute_body_sha256(remote_body)

        call_count = 0
        def fake_urlopen(req, *args, **kwargs):
            nonlocal call_count
            call_count += 1
            method = req.get_method() if hasattr(req, "get_method") else "GET"
            if method == "PATCH":
                return MockHttpResponse({"number": 42, "body": "ok"})
            if call_count <= 2:
                return MockHttpResponse({"number": 42, "body": remote_body})
            else:
                # Post-write GET throws network error!
                raise OSError("Connection timed out")

        mock_urlopen.side_effect = fake_urlopen

        with tempfile.TemporaryDirectory() as tmp_backup_dir:
            with tempfile.NamedTemporaryFile("w", delete=False) as f:
                f.write(self.sample_append_block)
                f_path = Path(f.name)
            try:
                test_args = [
                    "update_pr_body.py",
                    "--issue", "42",
                    "--append-file", str(f_path),
                    "--expected-base-hash", expected_hash,
                    "--backup-dir", tmp_backup_dir,
                    "--write",
                    "--token", "tok",
                ]
                with patch.object(sys, "argv", test_args):
                    with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                        exit_code = update_pr_body.main()
                        self.assertEqual(exit_code, 1)
                        err = mock_err.getvalue()
                        self.assertIn("post-write verification failed", err.lower())
                        self.assertIn("completion is unverified", err.lower())
            finally:
                f_path.unlink()

    @patch("urllib.request.urlopen")
    def test_issue_idempotent_noop_in_write_mode_zero_patch(self, mock_urlopen):
        remote_with_block = f"{self.sample_issue_body}\n\n{self.sample_append_block}"
        current_hash = compute_body_sha256(remote_with_block)

        mock_urlopen.return_value = MockHttpResponse({
            "number": 42,
            "body": remote_with_block,
        })
        with tempfile.NamedTemporaryFile("w", delete=False) as f:
            f.write(self.sample_append_block)
            f_path = Path(f.name)
        try:
            test_args = [
                "update_pr_body.py",
                "--issue", "42",
                "--append-file", str(f_path),
                "--expected-base-hash", current_hash,
                "--write",
                "--token", "tok",
            ]
            with patch.object(sys, "argv", test_args):
                with patch("sys.stdout", new_callable=io.StringIO) as mock_out:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 0)
                    self.assertIn("idempotent no-op verified; zero writes committed", mock_out.getvalue().lower())

            # Verify NO PATCH was executed
            for call in mock_urlopen.call_args_list:
                req = call[0][0]
                method = req.get_method() if hasattr(req, "get_method") else "GET"
                self.assertNotEqual(method, "PATCH")
        finally:
            f_path.unlink()

    @patch("urllib.request.urlopen")
    def test_issue_handles_utf8_bom_in_append_file(self, mock_urlopen):
        mock_urlopen.return_value = MockHttpResponse({
            "number": 42,
            "body": self.sample_issue_body,
        })
        with tempfile.NamedTemporaryFile("wb", delete=False) as f:
            # Write UTF-8 BOM followed by append block
            f.write(b"\xef\xbb\xbf" + self.sample_append_block.encode("utf-8"))
            f_path = Path(f.name)
        try:
            test_args = ["update_pr_body.py", "--issue", "42", "--append-file", str(f_path)]
            with patch.object(sys, "argv", test_args):
                with patch("sys.stdout", new_callable=io.StringIO) as mock_out:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 0)
                    out = mock_out.getvalue()
                    self.assertIn("[PREVIEW] Issue #42 body update", out)
                    self.assertIn("gemini-20261005-issue42-postmerge", out)
        finally:
            f_path.unlink()

    # =========================================================================
    # 8. Review 1 Follow-ups (R1, R2, R3 Repairs)
    # =========================================================================

    def test_append_input_rejects_unmarked_prefix_or_suffix(self):
        # Prefix
        with self.assertRaises(SecurityValidationError) as cm:
            parse_and_validate_append_input("Unmarked prefix\n" + self.sample_append_block)
        self.assertIn("surrounding unmarked text is forbidden", str(cm.exception).lower())

        # Suffix
        with self.assertRaises(SecurityValidationError) as cm:
            parse_and_validate_append_input(self.sample_append_block + "\nUnmarked suffix")
        self.assertIn("surrounding unmarked text is forbidden", str(cm.exception).lower())

    @patch("urllib.request.urlopen")
    def test_issue_check_idempotency_fails_closed_on_malformed_remote_history(self, mock_urlopen):
        malformed_body = self.sample_issue_body + "\n<!-- AI-REVIEW-RECORD: unclosed -->\nSome text without end marker"
        mock_urlopen.return_value = MockHttpResponse({
            "number": 42,
            "body": malformed_body,
        })
        with tempfile.NamedTemporaryFile("w", delete=False) as f:
            f.write(self.sample_append_block)
            f_path = Path(f.name)
        try:
            test_args = ["update_pr_body.py", "--issue", "42", "--append-file", str(f_path)]
            with patch.object(sys, "argv", test_args):
                with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 1)
                    self.assertIn("malformed record markers", mock_err.getvalue().lower())

            for call in mock_urlopen.call_args_list:
                req = call[0][0]
                method = req.get_method() if hasattr(req, "get_method") else "GET"
                self.assertNotEqual(method, "PATCH")
        finally:
            f_path.unlink()

    @patch("urllib.request.urlopen")
    def test_issue_check_idempotency_fails_closed_on_duplicate_existing_ids_with_changed_append(self, mock_urlopen):
        duplicate_record = (
            "<!-- AI-ASSIGNMENT-RECORD: fixture -->\n"
            "Assignment 1\n"
            "<!-- AI-ASSIGNMENT-RECORD-END -->\n\n"
            "<!-- AI-ASSIGNMENT-RECORD: fixture -->\n"
            "Assignment 1\n"
            "<!-- AI-ASSIGNMENT-RECORD-END -->"
        )
        remote_body = f"{self.sample_issue_body}\n\n{duplicate_record}"
        changed_append = (
            "<!-- AI-ASSIGNMENT-RECORD: fixture -->\n"
            "Changed Assignment!\n"
            "<!-- AI-ASSIGNMENT-RECORD-END -->"
        )
        mock_urlopen.return_value = MockHttpResponse({
            "number": 42,
            "body": remote_body,
        })
        with tempfile.TemporaryDirectory() as tmp_backup_dir:
            with tempfile.NamedTemporaryFile("w", delete=False) as f:
                f.write(changed_append)
                f_path = Path(f.name)
            try:
                test_args = [
                    "update_pr_body.py",
                    "--issue", "42",
                    "--append-file", str(f_path),
                    "--expected-base-hash", compute_body_sha256(remote_body),
                    "--backup-dir", tmp_backup_dir,
                    "--write",
                    "--token", "tok",
                ]
                with patch.object(sys, "argv", test_args):
                    with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                        exit_code = update_pr_body.main()
                        self.assertEqual(exit_code, 1)
                        self.assertIn("malformed record markers", mock_err.getvalue().lower())

                for call in mock_urlopen.call_args_list:
                    req = call[0][0]
                    method = req.get_method() if hasattr(req, "get_method") else "GET"
                    self.assertNotEqual(method, "PATCH")
            finally:
                f_path.unlink()

    @patch("urllib.request.urlopen")
    def test_pr_preview_redacts_removed_private_path_in_diff(self, mock_urlopen):
        remote_leaking_body = (
            "## Summary\nOld summary with C:\\Users\\alice_secret\\passwords.txt\n\n"
            "## Scope\nscripts/\n\n## Verification\nDone\n\n## Documentation\nDone\n"
        )
        proposed_clean_body = (
            "## Summary\nClean summary without any private path.\n\n"
            "## Scope\nscripts/\n\n## Verification\nDone\n\n## Documentation\nDone\n"
        )
        mock_urlopen.return_value = MockHttpResponse({
            "number": 33,
            "body": remote_leaking_body,
        })
        test_args = ["update_pr_body.py", "--pr", "33", "--body", proposed_clean_body]
        with patch.object(sys, "argv", test_args):
            with patch("sys.stdout", new_callable=io.StringIO) as mock_out:
                exit_code = update_pr_body.main()
                self.assertEqual(exit_code, 0)
                out = mock_out.getvalue()
                self.assertIn("[REDACTED_PATH]", out)
                self.assertNotIn("alice_secret", out)
                self.assertNotIn("C:\\Users\\alice_secret", out)

    @patch("urllib.request.urlopen")
    def test_issue_append_with_token_shaped_id_fails_closed_without_echoing_token(self, mock_urlopen):
        mock_urlopen.return_value = MockHttpResponse({
            "number": 42,
            "body": self.sample_issue_body,
        })
        token_id = "ghp_0123456789abcdef0123456789abcdef"
        token_append = (
            f"<!-- AI-POST-MERGE-RECORD: {token_id} -->\n"
            "Post merge content\n"
            "<!-- AI-POST-MERGE-RECORD-END -->"
        )
        with tempfile.NamedTemporaryFile("w", delete=False) as f:
            f.write(token_append)
            f_path = Path(f.name)
        try:
            test_args = ["update_pr_body.py", "--issue", "42", "--append-file", str(f_path)]
            with patch.object(sys, "argv", test_args):
                with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 1)
                    err = mock_err.getvalue()
                    self.assertIn("GitHub credential or bearer token", err)
                    self.assertNotIn(token_id, err)
        finally:
            f_path.unlink()

    @patch("urllib.request.urlopen")
    def test_safe_backup_display_redacts_personal_home_path(self, mock_urlopen):
        remote_body = self.sample_issue_body
        expected_hash = compute_body_sha256(remote_body)
        expected_final = prepare_issue_candidate(remote_body, self.sample_append_block)

        call_count = 0
        def fake_urlopen(req, *args, **kwargs):
            nonlocal call_count
            call_count += 1
            method = req.get_method() if hasattr(req, "get_method") else "GET"
            if method == "PATCH":
                return MockHttpResponse({"number": 42, "body": expected_final})
            if call_count <= 2:
                return MockHttpResponse({"number": 42, "body": remote_body})
            return MockHttpResponse({"number": 42, "body": expected_final})

        mock_urlopen.side_effect = fake_urlopen

        with tempfile.NamedTemporaryFile("w", delete=False) as f:
            f.write(self.sample_append_block)
            f_path = Path(f.name)
        try:
            personal_backup_dir = Path("C:/Users/alice_private/custom_backups")
            with patch.object(update_pr_body, "create_prewrite_backup") as mock_backup:
                mock_backup.return_value = personal_backup_dir / "issue_42_backup.md"
                test_args = [
                    "update_pr_body.py",
                    "--issue", "42",
                    "--append-file", str(f_path),
                    "--expected-base-hash", expected_hash,
                    "--write",
                    "--token", "tok",
                ]
                with patch.object(sys, "argv", test_args):
                    with patch("sys.stdout", new_callable=io.StringIO) as mock_out:
                        exit_code = update_pr_body.main()
                        self.assertEqual(exit_code, 0)
                        out = mock_out.getvalue()
                        self.assertIn("Pre-write local recovery backup saved to:", out)
                        self.assertIn("[REDACTED_PATH]", out)
                        self.assertNotIn("alice_private", out)
        finally:
            f_path.unlink()

    def test_exception_diagnostics_sanitize_personal_paths_and_tokens(self):
        exc_with_win_slash = OSError("Cannot access C:/Users/secret_user/keys.pem")
        clean_win = update_pr_body.sanitize_diagnostic(str(exc_with_win_slash))
        self.assertIn("[REDACTED_PATH]", clean_win)
        self.assertNotIn("secret_user", clean_win)

        exc_with_posix = OSError("Cannot read /home/secret_user/test.txt")
        clean_posix = update_pr_body.sanitize_diagnostic(str(exc_with_posix))
        self.assertIn("[REDACTED_PATH]", clean_posix)
        self.assertNotIn("secret_user", clean_posix)

        exc_with_token = ValueError("Failed with token ghp_9876543210fedcba9876543210")
        clean_token = update_pr_body.sanitize_diagnostic(str(exc_with_token))
        self.assertIn("[REDACTED_TOKEN]", clean_token)
        self.assertNotIn("ghp_9876543210", clean_token)

    @patch("urllib.request.urlopen")
    def test_issue_noop_fails_closed_if_remote_body_contains_privacy_violation(self, mock_urlopen):
        remote_with_block_and_leak = (
            f"{self.sample_issue_body}\n"
            f"Here is /home/secret_user/passwords.txt\n\n"
            f"{self.sample_append_block}"
        )
        current_hash = compute_body_sha256(remote_with_block_and_leak)
        mock_urlopen.return_value = MockHttpResponse({
            "number": 42,
            "body": remote_with_block_and_leak,
        })
        with tempfile.NamedTemporaryFile("w", delete=False) as f:
            f.write(self.sample_append_block)
            f_path = Path(f.name)
        try:
            test_args = [
                "update_pr_body.py",
                "--issue", "42",
                "--append-file", str(f_path),
                "--expected-base-hash", current_hash,
                "--write",
                "--token", "tok",
            ]
            with patch.object(sys, "argv", test_args):
                with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 1)
                    err = mock_err.getvalue()
                    self.assertIn("POSIX personal-home path", err)
                    self.assertNotIn("secret_user", err)

            for call in mock_urlopen.call_args_list:
                req = call[0][0]
                method = req.get_method() if hasattr(req, "get_method") else "GET"
                self.assertNotEqual(method, "PATCH")
        finally:
            f_path.unlink()

    @patch("urllib.request.urlopen")
    def test_issue_noop_write_aborts_if_remote_body_modified_during_verification(self, mock_urlopen):
        remote_with_block = f"{self.sample_issue_body}\n\n{self.sample_append_block}"
        concurrent_changed_body = f"{remote_with_block}\n<!-- concurrent comment -->"
        current_hash = compute_body_sha256(remote_with_block)

        call_count = 0
        def fake_urlopen(req, *args, **kwargs):
            nonlocal call_count
            call_count += 1
            if call_count == 1:
                return MockHttpResponse({"number": 42, "body": remote_with_block})
            elif call_count == 2:
                # Verification GET discovers concurrent modification!
                return MockHttpResponse({"number": 42, "body": concurrent_changed_body})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        with tempfile.NamedTemporaryFile("w", delete=False) as f:
            f.write(self.sample_append_block)
            f_path = Path(f.name)
        try:
            test_args = [
                "update_pr_body.py",
                "--issue", "42",
                "--append-file", str(f_path),
                "--expected-base-hash", current_hash,
                "--write",
                "--token", "tok",
            ]
            with patch.object(sys, "argv", test_args):
                with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 1)
                    err = mock_err.getvalue()
                    self.assertIn("concurrent modification detected during no-op verification", err.lower())

            for call in mock_urlopen.call_args_list:
                req = call[0][0]
                method = req.get_method() if hasattr(req, "get_method") else "GET"
                self.assertNotEqual(method, "PATCH")
        finally:
            f_path.unlink()

    @patch("urllib.request.urlopen")
    def test_issue_noop_write_aborts_if_record_disappears_during_verification(self, mock_urlopen):
        remote_with_block = f"{self.sample_issue_body}\n\n{self.sample_append_block}"
        current_hash = compute_body_sha256(remote_with_block)

        call_count = 0
        def fake_urlopen(req, *args, **kwargs):
            nonlocal call_count
            call_count += 1
            if call_count == 1:
                return MockHttpResponse({"number": 42, "body": remote_with_block})
            elif call_count == 2:
                # Verification GET returns body where the record was removed
                return MockHttpResponse({"number": 42, "body": self.sample_issue_body})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        with tempfile.NamedTemporaryFile("w", delete=False) as f:
            f.write(self.sample_append_block)
            f_path = Path(f.name)
        try:
            test_args = [
                "update_pr_body.py",
                "--issue", "42",
                "--append-file", str(f_path),
                "--expected-base-hash", current_hash,
                "--write",
                "--token", "tok",
            ]
            with patch.object(sys, "argv", test_args):
                with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 1)
                    err = mock_err.getvalue()
                    self.assertIn("concurrent modification detected during no-op verification", err.lower())

            for call in mock_urlopen.call_args_list:
                req = call[0][0]
                method = req.get_method() if hasattr(req, "get_method") else "GET"
                self.assertNotEqual(method, "PATCH")
        finally:
            f_path.unlink()

    @patch("urllib.request.urlopen")
    def test_issue_noop_write_aborts_if_verification_get_fails(self, mock_urlopen):
        remote_with_block = f"{self.sample_issue_body}\n\n{self.sample_append_block}"
        current_hash = compute_body_sha256(remote_with_block)

        call_count = 0
        def fake_urlopen(req, *args, **kwargs):
            nonlocal call_count
            call_count += 1
            if call_count == 1:
                return MockHttpResponse({"number": 42, "body": remote_with_block})
            elif call_count == 2:
                # Verification GET throws network error
                raise OSError("Connection reset by peer")
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        with tempfile.NamedTemporaryFile("w", delete=False) as f:
            f.write(self.sample_append_block)
            f_path = Path(f.name)
        try:
            test_args = [
                "update_pr_body.py",
                "--issue", "42",
                "--append-file", str(f_path),
                "--expected-base-hash", current_hash,
                "--write",
                "--token", "tok",
            ]
            with patch.object(sys, "argv", test_args):
                with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 1)
                    err = mock_err.getvalue()
                    self.assertIn("failed to verify idempotent no-op state", err.lower())

            for call in mock_urlopen.call_args_list:
                req = call[0][0]
                method = req.get_method() if hasattr(req, "get_method") else "GET"
                self.assertNotEqual(method, "PATCH")
        finally:
            f_path.unlink()

    @patch("urllib.request.urlopen")
    def test_issue_noop_write_success_when_verified(self, mock_urlopen):
        remote_with_block = f"{self.sample_issue_body}\n\n{self.sample_append_block}"
        current_hash = compute_body_sha256(remote_with_block)

        call_count = 0
        def fake_urlopen(req, *args, **kwargs):
            nonlocal call_count
            call_count += 1
            return MockHttpResponse({"number": 42, "body": remote_with_block})

        mock_urlopen.side_effect = fake_urlopen

        with tempfile.NamedTemporaryFile("w", delete=False) as f:
            f.write(self.sample_append_block)
            f_path = Path(f.name)
        try:
            test_args = [
                "update_pr_body.py",
                "--issue", "42",
                "--append-file", str(f_path),
                "--expected-base-hash", current_hash,
                "--write",
                "--token", "tok",
            ]
            with patch.object(sys, "argv", test_args):
                with patch("sys.stdout", new_callable=io.StringIO) as mock_out:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 0)
                    out = mock_out.getvalue()
                    self.assertIn("idempotent no-op verified; zero writes committed", out.lower())

            self.assertEqual(call_count, 2)
            for call in mock_urlopen.call_args_list:
                req = call[0][0]
                method = req.get_method() if hasattr(req, "get_method") else "GET"
                self.assertNotEqual(method, "PATCH")
        finally:
            f_path.unlink()

    @patch("urllib.request.urlopen")
    def test_pr_preview_redacts_removed_posix_path_at_column_1_in_diff(self, mock_urlopen):
        remote_leaking_body = (
            "/home/alice_secret/private_repo_details.txt\n"
            "## Summary\nOld summary\n\n## Scope\nscripts/\n\n## Verification\nDone\n\n## Documentation\nDone\n"
        )
        proposed_clean_body = (
            "Clean replacement line\n"
            "## Summary\nOld summary\n\n## Scope\nscripts/\n\n## Verification\nDone\n\n## Documentation\nDone\n"
        )
        mock_urlopen.return_value = MockHttpResponse({
            "number": 33,
            "body": remote_leaking_body,
        })
        test_args = ["update_pr_body.py", "--pr", "33", "--body", proposed_clean_body]
        with patch.object(sys, "argv", test_args):
            with patch("sys.stdout", new_callable=io.StringIO) as mock_out:
                exit_code = update_pr_body.main()
                self.assertEqual(exit_code, 0)
                out = mock_out.getvalue()
                self.assertIn("-[REDACTED_PATH]", out)
                self.assertNotIn("alice_secret", out)
                self.assertNotIn("/home/alice_secret", out)

    def test_sanitize_diff_line_redacts_context_and_removed_lines(self):
        from update_pr_body import sanitize_diff_line

        # Removed POSIX line at column 1
        self.assertEqual(sanitize_diff_line("-/home/victim/secret.txt\n"), "-[REDACTED_PATH]\n")
        # Context POSIX line at column 1
        self.assertEqual(sanitize_diff_line(" /home/victim/secret.txt\n"), " [REDACTED_PATH]\n")
        # Added POSIX line at column 1
        self.assertEqual(sanitize_diff_line("+/home/victim/secret.txt\n"), "+[REDACTED_PATH]\n")

        # Removed Windows line at column 1
        self.assertEqual(sanitize_diff_line("-C:\\Users\\victim\\secret.txt\n"), "-[REDACTED_PATH]\n")
        # Context Windows line at column 1
        self.assertEqual(sanitize_diff_line(" C:\\Users\\victim\\secret.txt\n"), " [REDACTED_PATH]\n")

        # Unified diff structural headers preserved
        self.assertEqual(sanitize_diff_line("--- PR-33-current\n"), "--- PR-33-current\n")
        self.assertEqual(sanitize_diff_line("+++ PR-33-proposed\n"), "+++ PR-33-proposed\n")
        self.assertEqual(sanitize_diff_line("@@ -1,3 +1,3 @@\n"), "@@ -1,3 +1,3 @@\n")

    @patch("urllib.request.urlopen")
    def test_pr_preview_redacts_removed_windows_path_at_column_1_in_diff(self, mock_urlopen):
        remote_leaking_body = (
            "C:\\Users\\alice_secret\\private_repo_details.txt\n"
            "## Summary\nOld summary\n\n## Scope\nscripts/\n\n## Verification\nDone\n\n## Documentation\nDone\n"
        )
        proposed_clean_body = (
            "Clean replacement line\n"
            "## Summary\nOld summary\n\n## Scope\nscripts/\n\n## Verification\nDone\n\n## Documentation\nDone\n"
        )
        mock_urlopen.return_value = MockHttpResponse({
            "number": 33,
            "body": remote_leaking_body,
        })
        test_args = ["update_pr_body.py", "--pr", "33", "--body", proposed_clean_body]
        with patch.object(sys, "argv", test_args):
            with patch("sys.stdout", new_callable=io.StringIO) as mock_out:
                exit_code = update_pr_body.main()
                self.assertEqual(exit_code, 0)
                out = mock_out.getvalue()
                self.assertIn("-[REDACTED_PATH]", out)
                self.assertNotIn("alice_secret", out)
                self.assertNotIn("C:\\Users\\alice_secret", out)

    @patch("urllib.request.urlopen")
    def test_issue_preview_with_token_shaped_filename_does_not_echo_token_in_rerun_command(self, mock_urlopen):
        mock_urlopen.return_value = MockHttpResponse({
            "number": 42,
            "body": self.sample_issue_body,
        })
        token_filename = "ghp_0123456789abcdef0123456789abcdef.md"
        with tempfile.TemporaryDirectory() as tmpdir:
            token_path = Path(tmpdir) / token_filename
            token_path.write_text(self.sample_append_block, encoding="utf-8")

            test_args = ["update_pr_body.py", "--issue", "42", "--append-file", str(token_path)]
            with patch.object(sys, "argv", test_args):
                with patch("sys.stdout", new_callable=io.StringIO) as mock_out:
                    exit_code = update_pr_body.main()
                    self.assertEqual(exit_code, 0)
                    out = mock_out.getvalue()
                    self.assertIn("<path-to-append-file>", out)
                    self.assertNotIn("ghp_0123456789abcdef0123456789abcdef", out)

    def test_issue_missing_file_with_token_shaped_filename_does_not_echo_token_in_stderr(self):
        token_filename = "ghp_9876543210fedcba9876543210fedcba.md"
        missing_path = Path(tempfile.gettempdir()) / token_filename
        if missing_path.exists():
            missing_path.unlink()

        test_args = ["update_pr_body.py", "--issue", "42", "--append-file", str(missing_path)]
        with patch.object(sys, "argv", test_args):
            with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                exit_code = update_pr_body.main()
                self.assertEqual(exit_code, 1)
                err = mock_err.getvalue()
                self.assertIn("Append file not found", err)
                self.assertNotIn("ghp_9876543210fedcba9876543210fedcba", err)
                self.assertIn("<redacted-filename>", err)

    def test_pr_missing_file_with_token_shaped_filename_does_not_echo_token_in_stderr(self):
        token_filename = "ghp_9876543210fedcba9876543210fedcba.md"
        missing_path = Path(tempfile.gettempdir()) / token_filename
        if missing_path.exists():
            missing_path.unlink()

        test_args = ["update_pr_body.py", "--pr", "33", "--body-file", str(missing_path)]
        with patch.object(sys, "argv", test_args):
            with patch("sys.stderr", new_callable=io.StringIO) as mock_err:
                exit_code = update_pr_body.main()
                self.assertEqual(exit_code, 1)
                err = mock_err.getvalue()
                self.assertIn("Body file not found", err)
                self.assertNotIn("ghp_9876543210fedcba9876543210fedcba", err)
                self.assertIn("<redacted-filename>", err)


if __name__ == "__main__":
    unittest.main()

