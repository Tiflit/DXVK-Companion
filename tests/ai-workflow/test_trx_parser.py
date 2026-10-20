import io
import os
import sys
import tempfile
import unittest
import zipfile
from unittest.mock import patch
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ai-workflow"))

import parse_trx


class TestFailureProjection(unittest.TestCase):
    @staticmethod
    def xml(count=1, namespace=True, message="Assert.Equal failure", name="Synthetic.Test", counters=None):
        import xml.etree.ElementTree as xml
        root = xml.Element("TestRun", {"xmlns": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"} if namespace else {})
        results = xml.SubElement(root, "Results")
        for index in range(count):
            result = xml.SubElement(results, "UnitTestResult", testName=f"{name}{index}", outcome="Failed")
            info = xml.SubElement(xml.SubElement(result, "Output"), "ErrorInfo")
            if message is not None:
                xml.SubElement(info, "Message").text = message
            xml.SubElement(info, "StackTrace").text = "STACK_ONLY_SENTINEL"
        xml.SubElement(xml.SubElement(root, "ResultSummary"), "Counters", **(counters or {"total": str(count + 3), "passed": "3", "failed": str(count)}))
        return xml.tostring(root, encoding="unicode")

    def test_namespaced_and_plain_records_cap_does_not_change_counters(self):
        for namespace in (False, True):
            with self.subTest(namespace=namespace):
                totals = parse_trx.parse_trx_content(self.xml(8, namespace))
                self.assertEqual((totals.total, totals.passed, totals.failed, totals.skipped), (11, 3, 8, 0))
                self.assertEqual(len(totals.failure_details), 5)
                self.assertEqual(totals.failure_details_observed, 8)
                summary = parse_trx.build_failure_summary(totals, "101", "2", "303")
                self.assertTrue(summary["entries_truncated"])
                rendered = "\n".join(parse_trx.render_failure_summary(summary, "101", "2"))
                self.assertIn("showing 5 of 8", rendered)
                self.assertIn("run 101, attempt 2, artifact 303", rendered)
                self.assertIn("Additional failure records omitted", rendered)
                self.assertNotIn("STACK_ONLY_SENTINEL", rendered)

    def test_missing_empty_and_multiline_message_details_are_explicit(self):
        for message in (None, "", "\n\t"):
            totals = parse_trx.parse_trx_content(self.xml(message=message))
            self.assertEqual(totals.failure_details[0].message, "Message unavailable.")
        totals = parse_trx.parse_trx_content(self.xml(message="Assertion failed\n at PRIVATE_STACK_SENTINEL\nSecond detail"))
        self.assertEqual(totals.failure_details[0].message, "Assertion failed")
        self.assertTrue(totals.failure_details[0].message_truncated)
        self.assertNotIn("PRIVATE_STACK_SENTINEL", str(totals))

    def test_no_details_does_not_guess_failures_from_names_or_change_status(self):
        document = self.xml().replace('outcome="Failed"', 'outcome="Passed"')
        totals = parse_trx.parse_trx_content(document)
        self.assertEqual(totals.failed, 1)
        summary = parse_trx.build_failure_summary(totals, 101, 2, 303)
        self.assertEqual(summary["status"], "unavailable")
        self.assertEqual(summary["reason"], "no_failure_records")
        self.assertEqual(summary["entries"], [])
        # Real result evidence cannot silently change counters even if they disagree.
        totals = parse_trx.parse_trx_content(self.xml(counters={"total": "3", "passed": "3", "failed": "0"}))
        self.assertEqual((totals.status, totals.failed), ("passed", 0))
        self.assertEqual(totals.failure_details_observed, 1)

    def test_privacy_in_names_messages_sources_and_serialized_projection(self):
        payloads = [r"C:\Users\Synthetic Owner\PrivateSentinel\data", "/home/synthetic/PrivateSentinel/data",
                    r"\\PrivateSentinelHost\PrivateSentinelShare\file", "ghp_PrivateSentinelToken0123456789",
                    "Bearer PrivateSentinelBearer0123456789", "https://PrivateSentinel.invalid/path?key=PrivateSentinelQuery",
                    "Hostname=PrivateSentinelMachine", "synthetic@PrivateSentinel.invalid"]
        for payload in payloads:
            with self.subTest(kind=payload[:10]):
                totals = parse_trx.parse_trx_content(self.xml(name=payload, message=payload), source_name=payload)
                summary = parse_trx.build_failure_summary(totals, 101, 2, 303)
                self.assertNotIn("PrivateSentinel", str(summary))
                self.assertNotIn("PrivateSentinel", "\n".join(parse_trx.render_failure_summary(summary, 101, 2)))

    def test_long_fields_and_markup_are_bounded_after_redaction(self):
        totals = parse_trx.parse_trx_content(self.xml(name="Test" * 100, message="[failure](https://example.invalid) <b>message</b> " + "word " * 200))
        detail = totals.failure_details[0]
        self.assertLessEqual(len(detail.name), 120)
        self.assertLessEqual(len(detail.message), 240)
        self.assertTrue(detail.name_truncated)
        self.assertTrue(detail.message_truncated)
        self.assertNotIn("https://", detail.message)
        totals = parse_trx.parse_trx_content(self.xml(message="x" * 65537 + "PrivateSentinel"))
        self.assertEqual(totals.failure_details[0].message, "Field omitted: size limit exceeded.")

    def test_parse_errors_never_echo_numeric_xml_or_filesystem_exception_payload(self):
        totals = parse_trx.parse_trx_content(self.xml(counters={"total": "PrivateSentinel"}), source_name=r"C:\Users\Synthetic\PrivateSentinel\result.trx")
        self.assertEqual(totals.status, "unavailable")
        self.assertNotIn("PrivateSentinel", str(totals))
        totals = parse_trx.parse_trx_content("<TestRun><PrivateSentinel>")
        self.assertEqual(totals.unavailable_reason, "Malformed XML in TRX file.")
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "result.trx"
            path.write_text("unused", encoding="utf-8")
            with patch.object(Path, "read_text", side_effect=OSError("PrivateSentinel")):
                totals = parse_trx.parse_trx_file(path)
            self.assertEqual(totals.unavailable_reason, "Error reading TRX file.")

    def test_mismatched_or_missing_attribution_never_emits_failure_payload(self):
        totals = parse_trx.parse_trx_content(self.xml(name="STALE_PAYLOAD"))
        for run, attempt, artifact in ((None, 2, 303), (101, None, 303), (101, 2, None)):
            self.assertEqual(parse_trx.build_failure_summary(totals, run, attempt, artifact)["entries"], [])
        summary = parse_trx.build_failure_summary(totals, 101, 1, 303)
        for run, attempt in ((102, 1), (101, 2)):
            rendered = "\n".join(parse_trx.render_failure_summary(summary, run, attempt))
            self.assertIn("UNAVAILABLE", rendered)
            self.assertNotIn("STALE_PAYLOAD", rendered)
        self.assertIsNone(parse_trx.build_failure_summary(parse_trx.TrxTotals("passed", 3, 3, 0, 0, "result.trx"), 101, 1, 303))


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

