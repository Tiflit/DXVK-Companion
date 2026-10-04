"""Unit and regression tests for opt-in PR-body update helper with review preservation."""

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
    ReviewRecord,
    SecurityValidationError,
    adopt_unmarked_review_records,
    compute_body_sha256,
    create_prewrite_backup,
    merge_and_preserve_review_records,
    parse_and_validate_review_records,
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
    """Tests covering review preservation, lost-update guards, preview mode, and record adoption."""

    def setUp(self):
        self.repo = "Tiflit/DXVK-Companion"
        self.pr_number = 31
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

    # 1. Parsing & Validation of Markers
    def test_parse_valid_records(self):
        records = parse_and_validate_review_records(self.sample_remote_body)
        self.assertIn("chatgpt-2026-10-04-rev1", records)
        self.assertIn("Verified test suite", records["chatgpt-2026-10-04-rev1"].inner_content)

    def test_reject_unclosed_marker(self):
        bad_text = "<!-- AI-REVIEW-RECORD: rec1 -->\nSome review\nNo end tag."
        with self.assertRaises(SecurityValidationError) as cm:
            parse_and_validate_review_records(bad_text)
        self.assertIn("unclosed", str(cm.exception).lower())

    def test_reject_orphan_end_marker(self):
        bad_text = "Some review\n<!-- AI-REVIEW-RECORD-END -->"
        with self.assertRaises(SecurityValidationError) as cm:
            parse_and_validate_review_records(bad_text)
        self.assertIn("orphan", str(cm.exception).lower())

    def test_reject_nested_markers(self):
        bad_text = (
            "<!-- AI-REVIEW-RECORD: outer -->\n"
            "<!-- AI-REVIEW-RECORD: inner -->\n"
            "Nested content\n"
            "<!-- AI-REVIEW-RECORD-END -->\n"
            "<!-- AI-REVIEW-RECORD-END -->"
        )
        with self.assertRaises(SecurityValidationError) as cm:
            parse_and_validate_review_records(bad_text)
        self.assertIn("nested", str(cm.exception).lower())

    def test_reject_duplicate_record_ids(self):
        bad_text = (
            "<!-- AI-REVIEW-RECORD: dup1 -->\nReview 1\n<!-- AI-REVIEW-RECORD-END -->\n"
            "<!-- AI-REVIEW-RECORD: dup1 -->\nReview 2\n<!-- AI-REVIEW-RECORD-END -->\n"
        )
        with self.assertRaises(SecurityValidationError) as cm:
            parse_and_validate_review_records(bad_text)
        self.assertIn("duplicate", str(cm.exception).lower())

    # 2. Review Record Preservation
    def test_automatic_preservation_when_proposed_body_has_no_markers(self):
        proposed = (
            "## Primary Issue\nFixes #31\n\n"
            "## Summary\nUpdated summary.\n\n"
            "## Scope\nscripts/ai-workflow/**\n\n"
            "## Verification\nPassed.\n\n"
            "## Documentation\nUpdated.\n"
        )
        merged, preserved_ids = merge_and_preserve_review_records(
            remote_body=self.sample_remote_body,
            proposed_body=proposed,
        )
        self.assertIn("chatgpt-2026-10-04-rev1", preserved_ids)
        self.assertIn(self.sample_review_block, merged)
        self.assertIn("## Review & Verification History", merged)
        self.assertIn("Updated summary", merged)

    def test_reject_accidental_deletion_when_markers_present(self):
        proposed = (
            "## Summary\nUpdated.\n\n"
            "<!-- AI-REVIEW-RECORD: new-review -->\nNew review.\n<!-- AI-REVIEW-RECORD-END -->\n"
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

    # 3. Unmarked Record Adoption
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

    # 4. Preview Mode (Zero Writes by Default)
    @patch("urllib.request.urlopen")
    def test_preview_mode_default_zero_writes(self, mock_urlopen):
        mock_urlopen.return_value = MockHttpResponse({
            "number": 31,
            "body": self.sample_remote_body,
        })

        test_args = [
            "update_pr_body.py",
            "--pr", "31",
            "--body", "## Primary Issue\nFixes #31\n\n## Summary\nNew summary\n\n## Scope\nscripts/\n\n## Verification\nDone\n\n## Documentation\nDone",
        ]
        with patch.object(sys, "argv", test_args):
            exit_code = update_pr_body.main()
            self.assertEqual(exit_code, 0)

        # Confirm only GET request was made (no PATCH or POST)
        for call_args in mock_urlopen.call_args_list:
            req = call_args[0][0]
            method = req.get_method() if hasattr(req, "get_method") else "GET"
            self.assertEqual(method, "GET")

    # 5. Write Mode & Verification
    @patch("urllib.request.urlopen")
    def test_write_mode_with_backup_and_post_verification(self, mock_urlopen):
        new_body_content = "## Primary Issue\nFixes #31\n\n## Summary\nNew summary\n\n## Scope\nscripts/\n\n## Verification\nDone\n\n## Documentation\nDone"
        merged_body, _ = merge_and_preserve_review_records(self.sample_remote_body, new_body_content)

        call_count = 0
        def fake_urlopen(req, *args, **kwargs):
            nonlocal call_count
            call_count += 1
            method = req.get_method() if hasattr(req, "get_method") else "GET"
            if method == "PATCH":
                return MockHttpResponse({"number": 31, "body": merged_body})
            if call_count == 1:
                # Initial GET
                return MockHttpResponse({"number": 31, "body": self.sample_remote_body})
            elif call_count == 2:
                # Pre-write verification GET
                return MockHttpResponse({"number": 31, "body": self.sample_remote_body})
            else:
                # Post-write verification GET
                return MockHttpResponse({"number": 31, "body": merged_body})

        mock_urlopen.side_effect = fake_urlopen

        with tempfile.TemporaryDirectory() as tmp_backup_dir:
            test_args = [
                "update_pr_body.py",
                "--pr", "31",
                "--token", "dummy-token",
                "--body", new_body_content,
                "--backup-dir", tmp_backup_dir,
                "--write",
            ]
            with patch.object(sys, "argv", test_args):
                exit_code = update_pr_body.main()
                self.assertEqual(exit_code, 0)

            # Check that local backup was written
            backup_files = list(Path(tmp_backup_dir).glob("*.md"))
            self.assertEqual(len(backup_files), 1)
            self.assertEqual(backup_files[0].read_text(encoding="utf-8"), self.sample_remote_body)

    # 6. Lost-Update / Concurrent Modification Guard
    @patch("urllib.request.urlopen")
    def test_concurrent_modification_detected_and_aborted(self, mock_urlopen):
        new_body_content = "## Summary\nNew summary"
        concurrently_modified_body = self.sample_remote_body + "\n<!-- Concurrent edit by human -->"

        call_count = 0
        def fake_urlopen(req, *args, **kwargs):
            nonlocal call_count
            call_count += 1
            if call_count == 1:
                # Initial read
                return MockHttpResponse({"number": 31, "body": self.sample_remote_body})
            elif call_count == 2:
                # Pre-write verification read detects remote change!
                return MockHttpResponse({"number": 31, "body": concurrently_modified_body})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        with tempfile.TemporaryDirectory() as tmp_backup_dir:
            test_args = [
                "update_pr_body.py",
                "--pr", "31",
                "--token", "dummy-token",
                "--body", new_body_content,
                "--backup-dir", tmp_backup_dir,
                "--write",
            ]
            with patch.object(sys, "argv", test_args):
                exit_code = update_pr_body.main()
                self.assertEqual(exit_code, 1)

    @patch("urllib.request.urlopen")
    def test_expected_base_hash_mismatch_refuses_write(self, mock_urlopen):
        mock_urlopen.return_value = MockHttpResponse({
            "number": 31,
            "body": self.sample_remote_body,
        })

        test_args = [
            "update_pr_body.py",
            "--pr", "31",
            "--body", "## Summary\nNew summary",
            "--expected-base-hash", "0000000000000000000000000000000000000000000000000000000000000000",
        ]
        with patch.object(sys, "argv", test_args):
            exit_code = update_pr_body.main()
            self.assertEqual(exit_code, 1)


if __name__ == "__main__":
    unittest.main()
