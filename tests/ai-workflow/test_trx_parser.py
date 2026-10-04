import io
import os
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ai-workflow"))

import parse_trx


class TestTrxParser(unittest.TestCase):
    """Regression tests for parsing Visual Studio TRX test files and safe artifact handling."""

    def test_parse_valid_all_pass_trx(self):
        trx_xml = """<?xml version="1.0" encoding="utf-8"?>
<TestRun id="12345" name="TestRun" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed">
    <Counters total="96" executed="96" passed="96" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAbbreviated="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
  </ResultSummary>
</TestRun>
"""
        totals = parse_trx.parse_trx_content(trx_xml, source_name="phase-a-tests.trx")
        self.assertEqual(totals.status, "passed")
        self.assertEqual(totals.total, 96)
        self.assertEqual(totals.passed, 96)
        self.assertEqual(totals.failed, 0)
        self.assertEqual(totals.skipped, 0)
        self.assertEqual(totals.source_name, "phase-a-tests.trx")

    def test_parse_trx_with_failures_and_skipped(self):
        trx_xml = """<?xml version="1.0" encoding="utf-8"?>
<TestRun id="12345" name="TestRun" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Failed">
    <Counters total="100" executed="95" passed="90" failed="5" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAbbreviated="0" notRunnable="2" notExecuted="3" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
  </ResultSummary>
</TestRun>
"""
        totals = parse_trx.parse_trx_content(trx_xml, source_name="phase-a-tests.trx")
        self.assertEqual(totals.status, "failed")
        self.assertEqual(totals.total, 100)
        self.assertEqual(totals.passed, 90)
        self.assertEqual(totals.failed, 5)
        self.assertEqual(totals.skipped, 5)  # 2 notRunnable + 3 notExecuted

    def test_parse_missing_trx_returns_explicit_unavailable(self):
        totals = parse_trx.parse_trx_file(None)
        self.assertEqual(totals.status, "unavailable")
        self.assertIn("not found", totals.unavailable_reason.lower())

    def test_parse_malformed_xml_returns_unavailable_without_crash(self):
        bad_xml = "<TestRun><UnclosedTag>"
        totals = parse_trx.parse_trx_content(bad_xml, source_name="corrupt.trx")
        self.assertEqual(totals.status, "unavailable")
        self.assertIn("malformed", totals.unavailable_reason.lower())

    def test_zip_slip_rejection(self):
        # Create an in-memory zip archive with path traversal
        zip_buf = io.BytesIO()
        with zipfile.ZipFile(zip_buf, "w") as zf:
            zf.writestr("../../evil.txt", "malicious payload")
        zip_bytes = zip_buf.getvalue()

        with tempfile.TemporaryDirectory() as temp_dir:
            with self.assertRaises(parse_trx.SecurityValidationError) as cm:
                parse_trx.safe_extract_zip(zip_bytes, Path(temp_dir))
            self.assertIn("traversal", str(cm.exception).lower())
            # Ensure file was not extracted outside
            parent_evil = Path(temp_dir).parent / "evil.txt"
            self.assertFalse(parent_evil.exists())

    def test_oversized_artifact_rejection(self):
        # Create an archive reporting uncompressed size over maximum allowed limit
        zip_buf = io.BytesIO()
        with zipfile.ZipFile(zip_buf, "w") as zf:
            # Write a small file but test with low limit
            zf.writestr("test.trx", "A" * 1024)
        zip_bytes = zip_buf.getvalue()

        with tempfile.TemporaryDirectory() as temp_dir:
            with self.assertRaises(parse_trx.SecurityValidationError) as cm:
                # Set max_uncompressed_bytes to 500 bytes
                parse_trx.safe_extract_zip(zip_bytes, Path(temp_dir), max_uncompressed_bytes=500)
            self.assertIn("exceeds size limit", str(cm.exception).lower())

    def test_safe_extract_json_from_zip_valid_and_bounds(self):
        # Tests safe_extract_json_from_zip (F1, F6)
        zip_buf = io.BytesIO()
        with zipfile.ZipFile(zip_buf, "w") as zf:
            zf.writestr("build-provenance.json", '{"head_sha": "abc", "run_id": "123"}')
        zip_bytes = zip_buf.getvalue()

        parsed = parse_trx.safe_extract_json_from_zip(zip_bytes, "build-provenance.json")
        self.assertIsNotNone(parsed)
        self.assertEqual(parsed.get("head_sha"), "abc")

        # Missing target returns None
        self.assertIsNone(parse_trx.safe_extract_json_from_zip(zip_bytes, "other.json"))

        # Bounded size check
        with self.assertRaises(parse_trx.SecurityValidationError):
            parse_trx.safe_extract_json_from_zip(zip_bytes, "build-provenance.json", max_uncompressed_bytes=10)


if __name__ == "__main__":
    unittest.main()

