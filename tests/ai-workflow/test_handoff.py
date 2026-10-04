"""Unit and regression tests for compact task handoff generator."""

from __future__ import annotations

import io
import json
import os
import sys
import unittest
import zipfile
from pathlib import Path
from unittest.mock import MagicMock, patch

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ai-workflow"))

import generate_handoff
from parse_trx import TrxTotals


def create_provenance_zip(prov_dict: dict) -> bytes:
    """Helper to generate in-memory build-provenance ZIP."""
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("build-provenance.json", json.dumps(prov_dict))
    return buf.getvalue()


def create_trx_zip(total: int = 215, passed: int = 215, failed: int = 0) -> bytes:
    """Helper to generate in-memory TRX test results ZIP."""
    trx_xml = f"""<?xml version="1.0" encoding="utf-8"?>
<TestRun id="1" name="test" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="Completed">
    <Counters total="{total}" executed="{total}" passed="{passed}" failed="{failed}" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAbbreviated="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" help="0"/>
  </ResultSummary>
</TestRun>
"""
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("test_results.trx", trx_xml)
    return buf.getvalue()


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

    def read(self, amt: int = -1) -> bytes:
        if amt == -1 or amt >= len(self._bytes):
            res = self._bytes
            self._bytes = b""
            return res
        res = self._bytes[:amt]
        self._bytes = self._bytes[amt:]
        return res

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        pass


class TestHandoffGenerator(unittest.TestCase):
    """Tests for generate_handoff.py covering full identities, budget, decision preflight, evidence, and CLI."""

    def setUp(self):
        self.repo = "Tiflit/DXVK-Companion"
        self.head_sha = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678"
        self.base_sha = "b2c3d4e5f60718293a4b5c6d7e8f90123456789a"
        self.main_sha = "b2c3d4e5f60718293a4b5c6d7e8f90123456789a"

        self.sample_pr = {
            "number": 31,
            "title": "[AI] Reliable compact handoffs and review preservation",
            "state": "open",
            "merged": False,
            "body": "## Primary Issue\nFixes #31\n\n## Summary\nWorkflow improvements.\n",
            "head": {"sha": self.head_sha, "ref": "workflow/issue-31"},
            "base": {"sha": self.base_sha, "ref": "main"},
        }
        self.sample_issue = {
            "number": 31,
            "title": "[AI] Reliable compact handoffs, review preservation and decision preflight",
            "state": "open",
            "body": (
                "### Objective\nImprove workflow.\n\n"
                "### Decision Governance Block\n"
                "- **Decision required**: Option A vs Option B\n"
                "- **Proposed option**: Option A\n"
                "- **Status**: PENDING\n"
                "- **Source of explicit human approval**: None (approval pending)\n"
            ),
        }

    @patch("urllib.request.urlopen")
    def test_full_identities_and_synced_base(self, mock_urlopen):
        """Exercises retrieval of full 40-character SHAs and synced base branch."""
        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if f"/pulls/31" in url:
                return MockHttpResponse(self.sample_pr)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if f"/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if f"/issues/31" in url:
                return MockHttpResponse(self.sample_issue)
            if f"/actions/runs?head_sha=" in url:
                return MockHttpResponse({
                    "workflow_runs": [
                        {
                            "id": 12345678,
                            "name": "Build and Test",
                            "run_attempt": 1,
                            "conclusion": "success",
                            "head_sha": self.head_sha,
                        }
                    ]
                })
            if f"/actions/runs/12345678/artifacts" in url:
                return MockHttpResponse({"artifacts": []})
            if f"/actions/runs/12345678/jobs" in url or f"/actions/runs/12345678/attempts" in url:
                return MockHttpResponse({"jobs": []})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        client = generate_handoff.GitHubClient(token="mock-token", repo=self.repo)
        snap = generate_handoff.collect_handoff_snapshot(
            client=client,
            repo=self.repo,
            pr_number=31,
        )

        self.assertEqual(snap.live_pr_head_sha, self.head_sha)
        self.assertEqual(snap.live_pr_base_sha, self.base_sha)
        self.assertEqual(snap.live_default_branch_sha, self.main_sha)
        self.assertFalse(snap.base_has_moved)
        self.assertEqual(snap.primary_issue_number, 31)
        self.assertEqual(snap.ci_run_id, "12345678")
        self.assertEqual(snap.ci_run_conclusion, "success")

        md = generate_handoff.format_handoff_markdown(snap)
        self.assertIn(f"`{self.head_sha}`", md)
        self.assertIn(f"`{self.base_sha}`", md)
        self.assertIn("(synced)", md)
        self.assertIn("Build and Test", md or "")

    @patch("urllib.request.urlopen")
    def test_base_branch_movement_detection(self, mock_urlopen):
        """Verifies that base branch movement is detected when default branch has advanced."""
        moved_main_sha = "c3d4e5f60718293a4b5c6d7e8f90123456789a1b"

        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if f"/pulls/31" in url:
                return MockHttpResponse(self.sample_pr)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if f"/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": moved_main_sha}})
            if f"/issues/31" in url:
                return MockHttpResponse(self.sample_issue)
            if f"/actions/runs?head_sha=" in url:
                return MockHttpResponse({"workflow_runs": []})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        client = generate_handoff.GitHubClient(token="mock-token", repo=self.repo)
        snap = generate_handoff.collect_handoff_snapshot(
            client=client,
            repo=self.repo,
            pr_number=31,
        )

        self.assertTrue(snap.base_has_moved)
        self.assertEqual(snap.live_default_branch_sha, moved_main_sha)

        md = generate_handoff.format_handoff_markdown(snap)
        self.assertIn("BASE MOVED", md)
        self.assertIn(f"`{moved_main_sha}`", md)

    @patch("urllib.request.urlopen")
    def test_missing_evidence_reports_unavailable(self, mock_urlopen):
        """Verifies that missing CI runs or missing artifacts report UNAVAILABLE, never guess."""
        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if f"/pulls/31" in url:
                return MockHttpResponse(self.sample_pr)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if f"/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if f"/issues/31" in url:
                return MockHttpResponse(self.sample_issue)
            if f"/actions/runs?head_sha=" in url:
                return MockHttpResponse({"workflow_runs": []})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        client = generate_handoff.GitHubClient(token="mock-token", repo=self.repo)
        snap = generate_handoff.collect_handoff_snapshot(
            client=client,
            repo=self.repo,
            pr_number=31,
        )

        self.assertIsNone(snap.ci_run_id)
        self.assertIsNone(snap.tested_checkout_sha)
        self.assertIsNone(snap.trx_totals)

        md = generate_handoff.format_handoff_markdown(snap)
        self.assertIn("Triggering Run**: UNAVAILABLE", md)
        self.assertIn("Tested Checkout SHA**: UNAVAILABLE", md)
        self.assertIn("TRX Test Totals**: UNAVAILABLE", md)

    @patch("urllib.request.urlopen")
    def test_decision_block_extraction_and_preflight(self, mock_urlopen):
        """Verifies extraction of Decision Governance Block and blocking of policy implementation."""
        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if f"/pulls/31" in url:
                return MockHttpResponse(self.sample_pr)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if f"/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if f"/issues/31" in url:
                return MockHttpResponse(self.sample_issue)
            if f"/actions/runs?head_sha=" in url:
                return MockHttpResponse({"workflow_runs": []})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        client = generate_handoff.GitHubClient(token="mock-token", repo=self.repo)
        snap = generate_handoff.collect_handoff_snapshot(
            client=client,
            repo=self.repo,
            pr_number=31,
        )

        self.assertEqual(snap.decision_required, "Option A vs Option B")
        self.assertEqual(snap.decision_status, "PENDING")
        self.assertEqual(snap.decision_approval_source, "None (approval pending)")
        self.assertEqual(snap.next_owner, "Human")
        self.assertIn("Explicit decision required", snap.next_action)

    def test_word_budget_and_clean_truncation(self):
        """Verifies that generated markdown strictly conforms to the word budget."""
        snap = generate_handoff.HandoffSnapshot(
            capture_time_utc="2026-10-04 20:00:00 UTC",
            repo=self.repo,
            pr_number=31,
            pr_title="Compact handoff test",
            pr_state="open",
            pr_merged=False,
            live_pr_head_sha=self.head_sha,
            live_pr_base_sha=self.base_sha,
            pr_base_ref="main",
            repo_default_branch="main",
            live_default_branch_sha=self.main_sha,
            base_sync_status="synced",
            base_has_moved=False,
            primary_issue_number=31,
            primary_issue_title="Compact handoff issue",
            primary_issue_state="open",
            trx_totals=TrxTotals(status="passed", total=100, passed=100, failed=0, skipped=0, source_name="t.trx"),
        )

        md = generate_handoff.format_handoff_markdown(snap, max_words=300)
        word_count = generate_handoff.count_words_excluding_urls(md)
        self.assertLessEqual(word_count, 300)

        # Test truncation with small limit
        small_md = generate_handoff.format_handoff_markdown(snap, max_words=30)
        small_word_count = generate_handoff.count_words_excluding_urls(small_md)
        self.assertLessEqual(small_word_count, 30)
        self.assertIn("Handoff truncated to meet", small_md)

    def test_json_formatting_structure(self):
        """Verifies JSON output schema."""
        snap = generate_handoff.HandoffSnapshot(
            capture_time_utc="2026-10-04 20:00:00 UTC",
            repo=self.repo,
            pr_number=31,
            pr_title="Compact handoff test",
            pr_state="open",
            pr_merged=False,
            live_pr_head_sha=self.head_sha,
            live_pr_base_sha=self.base_sha,
            pr_base_ref="main",
            repo_default_branch="main",
            live_default_branch_sha=self.main_sha,
            base_sync_status="synced",
            base_has_moved=False,
            primary_issue_number=31,
            decision_required="Choose policy",
            decision_status="PENDING",
            decision_approval_source="None",
        )

        json_str = generate_handoff.format_handoff_json(snap)
        parsed = json.loads(json_str)

        self.assertEqual(parsed["pr_number"], 31)
        self.assertEqual(parsed["live_pr_head_sha"], self.head_sha)
        self.assertEqual(parsed["decision_governance"]["required"], "Choose policy")
        self.assertEqual(parsed["decision_governance"]["status"], "PENDING")

    # =========================================================================
    # R1: Provenance and TRX Artifact Attribution CLI Tests
    # =========================================================================

    @patch("urllib.request.build_opener")
    @patch("urllib.request.urlopen")
    def test_cli_execution_with_real_artifacts_and_jobs(self, mock_urlopen, mock_build_opener):
        """Exercises CLI end-to-end with matching provenance and attributable TRX artifact."""
        prov_bytes = create_provenance_zip({
            "run_id": "100",
            "run_attempt": "1",
            "head_sha": self.head_sha,
            "ref": "refs/heads/workflow/issue-31",
            "base_sha": self.base_sha,
        })
        trx_bytes = create_trx_zip(total=215, passed=215, failed=0)

        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if "/pulls/31" in url:
                return MockHttpResponse(self.sample_pr)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if "/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "/issues/31" in url:
                return MockHttpResponse(self.sample_issue)
            if "/actions/runs?head_sha=" in url:
                return MockHttpResponse({
                    "workflow_runs": [
                        {
                            "id": 100,
                            "name": "Build and Test",
                            "run_attempt": 1,
                            "conclusion": "success",
                            "head_sha": self.head_sha,
                        }
                    ]
                })
            if "/actions/runs/100/artifacts" in url:
                return MockHttpResponse({
                    "artifacts": [
                        {
                            "id": 1,
                            "name": "build-provenance",
                            "archive_download_url": "https://api.github.com/artifacts/prov.zip",
                        },
                        {
                            "id": 2,
                            "name": "phase-a-test-results",
                            "archive_download_url": "https://api.github.com/artifacts/trx.zip",
                            "created_at": "2026-10-04T12:02:00Z",
                        },
                    ]
                })
            if "/actions/runs/100/attempts/1/jobs" in url or "/actions/runs/100/jobs" in url:
                return MockHttpResponse({
                    "jobs": [
                        {
                            "id": 10,
                            "run_attempt": 1,
                            "started_at": "2026-10-04T12:00:00Z",
                            "completed_at": "2026-10-04T12:05:00Z",
                        }
                    ]
                })
            if "prov.zip" in url:
                return MockHttpResponse(prov_bytes)
            if "trx.zip" in url:
                return MockHttpResponse(trx_bytes)
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen
        mock_opener = MagicMock()
        mock_opener.open.side_effect = fake_urlopen
        mock_build_opener.return_value = mock_opener

        stdout_capture = io.StringIO()
        with patch("sys.stdout", stdout_capture):
            exit_code = generate_handoff.main(["--pr", "31", "--issue", "31", "--token", "mock-token"])

        self.assertEqual(exit_code, 0)
        output = stdout_capture.getvalue()
        self.assertIn(f"Tested Checkout SHA**: `{self.head_sha}`", output)
        self.assertIn("TRX Test Totals**: 215 passed, 0 failed, 0 skipped (total 215)", output)
        self.assertIn("Triggering Run**: ID `100` (attempt 1)", output)

    @patch("urllib.request.build_opener")
    @patch("urllib.request.urlopen")
    def test_cli_mismatched_provenance_rejected(self, mock_urlopen, mock_build_opener):
        """Verifies that mismatched run/attempt in build-provenance is rejected as uncertain."""
        # Triggering run is 10/attempt 2, but provenance recorded 999/attempt 1
        mismatched_prov_bytes = create_provenance_zip({
            "run_id": "999",
            "run_attempt": "1",
            "head_sha": "9999999999999999999999999999999999999999",
            "ref": "refs/heads/other",
        })

        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if "/pulls/31" in url:
                return MockHttpResponse(self.sample_pr)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if "/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "/issues/31" in url:
                return MockHttpResponse(self.sample_issue)
            if "/actions/runs?head_sha=" in url:
                return MockHttpResponse({
                    "workflow_runs": [
                        {
                            "id": 10,
                            "name": "Build and Test",
                            "run_attempt": 2,
                            "conclusion": "success",
                            "head_sha": self.head_sha,
                        }
                    ]
                })
            if "/actions/runs/10/artifacts" in url:
                return MockHttpResponse({
                    "artifacts": [
                        {
                            "id": 1,
                            "name": "build-provenance",
                            "archive_download_url": "https://api.github.com/artifacts/prov.zip",
                        }
                    ]
                })
            if "/actions/runs/10/attempts/2/jobs" in url:
                return MockHttpResponse({"jobs": []})
            if "prov.zip" in url:
                return MockHttpResponse(mismatched_prov_bytes)
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen
        mock_opener = MagicMock()
        mock_opener.open.side_effect = fake_urlopen
        mock_build_opener.return_value = mock_opener

        stdout_capture = io.StringIO()
        with patch("sys.stdout", stdout_capture):
            exit_code = generate_handoff.main(["--pr", "31", "--token", "mock-token"])

        self.assertEqual(exit_code, 0)
        output = stdout_capture.getvalue()
        self.assertIn("Tested Checkout SHA**: UNAVAILABLE / UNPROVEN", output)
        self.assertIn("Provenance run ID mismatch", output)

    @patch("urllib.request.urlopen")
    def test_cli_non_build_and_test_workflow_declined(self, mock_urlopen):
        """Verifies that non-'Build and Test' workflow runs are not substituted as build evidence."""
        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if "/pulls/31" in url:
                return MockHttpResponse(self.sample_pr)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if "/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "/issues/31" in url:
                return MockHttpResponse(self.sample_issue)
            if "/actions/runs?head_sha=" in url:
                return MockHttpResponse({
                    "workflow_runs": [
                        {
                            "id": 555,
                            "name": "AI Workflow Tests",
                            "run_attempt": 1,
                            "conclusion": "success",
                            "head_sha": self.head_sha,
                        }
                    ]
                })
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        stdout_capture = io.StringIO()
        with patch("sys.stdout", stdout_capture):
            exit_code = generate_handoff.main(["--pr", "31", "--token", "mock-token"])

        self.assertEqual(exit_code, 0)
        output = stdout_capture.getvalue()
        self.assertIn("Triggering Run**: UNAVAILABLE", output)
        self.assertIn("No 'Build and Test' workflow run found", output)

    # =========================================================================
    # R2: Review Routing and Record Parsing CLI Tests
    # =========================================================================

    @patch("urllib.request.urlopen")
    def test_cli_review_routing_changes_required_on_current_head_routes_to_gemini(self, mock_urlopen):
        """Verifies that CHANGES REQUIRED on the current head routes to Gemini focused revision."""
        pr_with_changes_req = dict(self.sample_pr)
        pr_with_changes_req["body"] = (
            "## Primary Issue\nFixes #31\n\n"
            "<!-- AI-REVIEW-RECORD: chatgpt-20261004-pr31-rev1 -->\n"
            "## ChatGPT coordinator verification\n\n"
            f"**Result: CHANGES REQUIRED.** Reviewed head `{self.head_sha}`\n"
            "<!-- AI-REVIEW-RECORD-END -->\n"
        )
        # Issue without pending decisions so routing isolates review result
        clean_issue = dict(self.sample_issue)
        clean_issue["body"] = "### Objective\nStandard task without pending decisions.\n"

        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if "/pulls/31" in url:
                return MockHttpResponse(pr_with_changes_req)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if "/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "/issues/31" in url:
                return MockHttpResponse(clean_issue)
            if "/actions/runs?head_sha=" in url:
                return MockHttpResponse({
                    "workflow_runs": [
                        {"id": 1, "name": "Build and Test", "run_attempt": 1, "conclusion": "success", "head_sha": self.head_sha}
                    ]
                })
            if "/actions/runs/1/artifacts" in url:
                return MockHttpResponse({"artifacts": []})
            if "/actions/runs/1/jobs" in url:
                return MockHttpResponse({"jobs": []})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        stdout_capture = io.StringIO()
        with patch("sys.stdout", stdout_capture):
            exit_code = generate_handoff.main(["--pr", "31", "--token", "mock-token"])

        self.assertEqual(exit_code, 0)
        output = stdout_capture.getvalue()
        self.assertIn("Next Owner**: **Gemini**", output)
        self.assertIn("Focused revision addressing reviewer findings", output)
        self.assertIn("CHANGES REQUIRED", output)

    @patch("urllib.request.urlopen")
    def test_cli_review_routing_stale_review_on_older_head_routes_to_chatgpt(self, mock_urlopen):
        """Verifies that an existing review on an older head routes to ChatGPT independent re-review."""
        older_head = "1111111111111111111111111111111111111111"
        pr_with_stale_review = dict(self.sample_pr)
        pr_with_stale_review["body"] = (
            "## Primary Issue\nFixes #31\n\n"
            "<!-- AI-REVIEW-RECORD: chatgpt-20261004-pr31-rev1 -->\n"
            "## ChatGPT coordinator verification\n\n"
            f"**Result: CHANGES REQUIRED.** Reviewed head `{older_head}`\n"
            "<!-- AI-REVIEW-RECORD-END -->\n"
        )
        clean_issue = dict(self.sample_issue)
        clean_issue["body"] = "### Objective\nStandard task without pending decisions.\n"

        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if "/pulls/31" in url:
                return MockHttpResponse(pr_with_stale_review)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if "/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "/issues/31" in url:
                return MockHttpResponse(clean_issue)
            if "/actions/runs?head_sha=" in url:
                return MockHttpResponse({
                    "workflow_runs": [
                        {"id": 1, "name": "Build and Test", "run_attempt": 1, "conclusion": "success", "head_sha": self.head_sha}
                    ]
                })
            if "/actions/runs/1/artifacts" in url:
                return MockHttpResponse({"artifacts": []})
            if "/actions/runs/1/jobs" in url:
                return MockHttpResponse({"jobs": []})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        stdout_capture = io.StringIO()
        with patch("sys.stdout", stdout_capture):
            exit_code = generate_handoff.main(["--pr", "31", "--token", "mock-token"])

        self.assertEqual(exit_code, 0)
        output = stdout_capture.getvalue()
        self.assertIn("Next Owner**: **ChatGPT**", output)
        self.assertIn("Coordinator verification of current head", output)

    @patch("urllib.request.urlopen")
    def test_cli_review_routing_pass_on_current_head_does_not_infer_merge_readiness(self, mock_urlopen):
        """Verifies that an approved PASS on the current head displays factually and does not claim merge readiness."""
        pr_with_approved_review = dict(self.sample_pr)
        pr_with_approved_review["body"] = (
            "## Primary Issue\nFixes #31\n\n"
            "<!-- AI-REVIEW-RECORD: chatgpt-20261004-pr31-rev1 -->\n"
            "## ChatGPT coordinator verification\n\n"
            f"**Result: PASS.** Reviewed head `{self.head_sha}`\n"
            "<!-- AI-REVIEW-RECORD-END -->\n"
        )
        clean_issue = dict(self.sample_issue)
        clean_issue["body"] = "### Objective\nStandard task without pending decisions.\n"

        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if "/pulls/31" in url:
                return MockHttpResponse(pr_with_approved_review)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if "/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "/issues/31" in url:
                return MockHttpResponse(clean_issue)
            if "/actions/runs?head_sha=" in url:
                return MockHttpResponse({
                    "workflow_runs": [
                        {"id": 1, "name": "Build and Test", "run_attempt": 1, "conclusion": "success", "head_sha": self.head_sha}
                    ]
                })
            if "/actions/runs/1/artifacts" in url:
                return MockHttpResponse({"artifacts": []})
            if "/actions/runs/1/jobs" in url:
                return MockHttpResponse({"jobs": []})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        stdout_capture = io.StringIO()
        with patch("sys.stdout", stdout_capture):
            exit_code = generate_handoff.main(["--pr", "31", "--token", "mock-token"])

        self.assertEqual(exit_code, 0)
        output = stdout_capture.getvalue()
        self.assertIn("chatgpt-20261004-pr31-rev1`: **PASS**", output)
        self.assertIn("Next Owner**: **ChatGPT**", output)
        self.assertIn("Coordinator verification of current head", output)
        self.assertNotIn("Final review and merge decision", output)

    # =========================================================================
    # R3: Decision Block & Sync State CLI Tests
    # =========================================================================

    @patch("urllib.request.urlopen")
    def test_cli_pending_decision_with_non_empty_source_routes_to_human(self, mock_urlopen):
        """Verifies that PENDING decision status cannot escape Human routing via a non-empty source."""
        pending_issue_with_source = {
            "number": 31,
            "title": "Architectural task",
            "state": "open",
            "body": (
                "### Decision Governance Block\n"
                "- **Decision required**: Option A vs Option B\n"
                "- **Proposed option**: Option A\n"
                "- **Status**: PENDING\n"
                "- **Source of explicit human approval**: ChatGPT recommended Option A in comment 12345\n"
            ),
        }

        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if "/pulls/31" in url:
                return MockHttpResponse(self.sample_pr)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if "/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "/issues/31" in url:
                return MockHttpResponse(pending_issue_with_source)
            if "/actions/runs?head_sha=" in url:
                return MockHttpResponse({"workflow_runs": []})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        stdout_capture = io.StringIO()
        with patch("sys.stdout", stdout_capture):
            exit_code = generate_handoff.main(["--pr", "31", "--token", "mock-token"])

        self.assertEqual(exit_code, 0)
        output = stdout_capture.getvalue()
        self.assertIn("Decision Status**: `PENDING` (BLOCKED: explicit human approval pending)", output)
        self.assertIn("Next Owner**: **Human**", output)
        self.assertIn("Explicit decision required", output)

    @patch("urllib.request.urlopen")
    def test_cli_issue_fetch_failure_reports_unavailable(self, mock_urlopen):
        """Verifies that failed Issue fetch reports UNAVAILABLE, never 'None pending'."""
        import urllib.error

        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if "/pulls/31" in url:
                return MockHttpResponse(self.sample_pr)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if "/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "/issues/31" in url:
                raise urllib.error.HTTPError(url, 404, "Not Found", {}, None)
            if "/actions/runs?head_sha=" in url:
                return MockHttpResponse({"workflow_runs": []})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        stdout_capture = io.StringIO()
        with patch("sys.stdout", stdout_capture):
            exit_code = generate_handoff.main(["--pr", "31", "--token", "mock-token"])

        self.assertEqual(exit_code, 0)
        output = stdout_capture.getvalue()
        self.assertIn("Decision Governance**: UNAVAILABLE", output)
        self.assertNotIn("None pending (standard workflow)", output)

    @patch("urllib.request.urlopen")
    def test_cli_unmigrated_decision_prose_detected(self, mock_urlopen):
        """Verifies that unmigrated decision prose without a governance block routes to Human preflight."""
        prose_issue = {
            "number": 31,
            "title": "Architectural task with prose requirement",
            "state": "open",
            "body": "This task requires an architectural choice: human approval required before implementation.",
        }

        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if "/pulls/31" in url:
                return MockHttpResponse(self.sample_pr)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if "/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "/issues/31" in url:
                return MockHttpResponse(prose_issue)
            if "/actions/runs?head_sha=" in url:
                return MockHttpResponse({"workflow_runs": []})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        stdout_capture = io.StringIO()
        with patch("sys.stdout", stdout_capture):
            exit_code = generate_handoff.main(["--pr", "31", "--token", "mock-token"])

        self.assertEqual(exit_code, 0)
        output = stdout_capture.getvalue()
        self.assertIn("Decision Governance**: UNMIGRATED PROSE DECISION", output)
        self.assertIn("Next Owner**: **Human**", output)

    @patch("urllib.request.urlopen")
    def test_cli_non_default_pr_base_branch(self, mock_urlopen):
        """Verifies reporting when PR targets a non-default base branch."""
        non_default_pr = dict(self.sample_pr)
        non_default_pr["base"] = {"sha": self.base_sha, "ref": "feature-base"}

        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if "/pulls/31" in url:
                return MockHttpResponse(non_default_pr)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if "/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "/git/ref/heads/feature-base" in url:
                return MockHttpResponse({"object": {"sha": self.base_sha}})
            if "/issues/31" in url:
                return MockHttpResponse(self.sample_issue)
            if "/actions/runs?head_sha=" in url:
                return MockHttpResponse({"workflow_runs": []})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        stdout_capture = io.StringIO()
        with patch("sys.stdout", stdout_capture):
            exit_code = generate_handoff.main(["--pr", "31", "--token", "mock-token"])

        self.assertEqual(exit_code, 0)
        output = stdout_capture.getvalue()
        self.assertIn("NON-DEFAULT PR TARGET (feature-base != main", output)

    @patch("urllib.request.urlopen")
    def test_cli_default_branch_ref_unavailable_reports_unknown(self, mock_urlopen):
        """Verifies that unavailable default branch reports UNKNOWN, never '(synced)'."""
        import urllib.error

        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if "/pulls/31" in url:
                return MockHttpResponse(self.sample_pr)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if "/git/ref/heads/main" in url:
                raise urllib.error.HTTPError(url, 404, "Not Found", {}, None)
            if "/issues/31" in url:
                return MockHttpResponse(self.sample_issue)
            if "/actions/runs?head_sha=" in url:
                return MockHttpResponse({"workflow_runs": []})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        stdout_capture = io.StringIO()
        with patch("sys.stdout", stdout_capture):
            exit_code = generate_handoff.main(["--pr", "31", "--token", "mock-token"])

        self.assertEqual(exit_code, 0)
        output = stdout_capture.getvalue()
        self.assertIn("`unknown` (UNKNOWN (default branch ref unavailable))", output)
        self.assertNotIn("`unknown` (synced)", output)

    # =========================================================================
    # Authorized Reduced Closeout Regression Tests (Issue #31 / PR #33)
    # =========================================================================

    @patch("urllib.request.build_opener")
    @patch("urllib.request.urlopen")
    def test_cli_invalid_checkout_sha_with_wrong_pr_merge_ref(self, mock_urlopen, mock_build_opener):
        """Verifies that invalid checkout SHA ('not-a-sha') on wrong-PR merge ref emits UNPROVEN and no merge readiness."""
        prov_bytes = create_provenance_zip({
            "run_id": "10",
            "run_attempt": "2",
            "head_sha": "not-a-sha",
            "ref": "refs/pull/999/merge",
        })

        clean_issue = dict(self.sample_issue)
        clean_issue["body"] = "### Objective\nStandard task without pending decisions.\n"

        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if "/pulls/31" in url:
                return MockHttpResponse(self.sample_pr)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if "/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "/issues/31" in url:
                return MockHttpResponse(clean_issue)
            if "/actions/runs?head_sha=" in url:
                return MockHttpResponse({
                    "workflow_runs": [
                        {
                            "id": 10,
                            "name": "Build and Test",
                            "run_attempt": 2,
                            "conclusion": "success",
                            "head_sha": self.head_sha,
                        }
                    ]
                })
            if "/actions/runs/10/artifacts" in url:
                return MockHttpResponse({
                    "artifacts": [
                        {
                            "id": 1,
                            "name": "build-provenance",
                            "archive_download_url": "https://api.github.com/artifacts/prov.zip",
                        }
                    ]
                })
            if "/actions/runs/10/attempts/2/jobs" in url:
                return MockHttpResponse({"jobs": []})
            if "prov.zip" in url:
                return MockHttpResponse(prov_bytes)
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen
        mock_opener = MagicMock()
        mock_opener.open.side_effect = fake_urlopen
        mock_build_opener.return_value = mock_opener

        stdout_capture = io.StringIO()
        with patch("sys.stdout", stdout_capture):
            exit_code = generate_handoff.main(["--pr", "31", "--token", "mock-token"])

        self.assertEqual(exit_code, 0)
        output = stdout_capture.getvalue()
        self.assertIn("Tested Checkout SHA**: UNAVAILABLE / UNPROVEN", output)
        self.assertNotIn("Tested Checkout SHA**: `not-a-sha`", output)
        self.assertIn("Provenance checkout SHA is invalid: 'not-a-sha'", output)
        self.assertIn("Next Owner**: **ChatGPT**", output)
        self.assertNotIn("Final review and merge decision", output)

    @patch("urllib.request.urlopen")
    def test_cli_same_prefix_different_full_review_sha(self, mock_urlopen):
        """Verifies that a review sharing the 7-character prefix but having a different full 40-hex SHA does not match current head."""
        different_full_sha = self.head_sha[:7] + "f" * 33
        pr_with_prefix_review = dict(self.sample_pr)
        pr_with_prefix_review["body"] = (
            "## Primary Issue\nFixes #31\n\n"
            "<!-- AI-REVIEW-RECORD: chatgpt-20261004-pr31-rev1 -->\n"
            "## ChatGPT coordinator verification\n\n"
            f"**Result: PASS.** Reviewed head `{different_full_sha}`\n"
            "<!-- AI-REVIEW-RECORD-END -->\n"
        )
        clean_issue = dict(self.sample_issue)
        clean_issue["body"] = "### Objective\nStandard task without pending decisions.\n"

        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if "/pulls/31" in url:
                return MockHttpResponse(pr_with_prefix_review)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if "/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "/issues/31" in url:
                return MockHttpResponse(clean_issue)
            if "/actions/runs?head_sha=" in url:
                return MockHttpResponse({
                    "workflow_runs": [
                        {"id": 1, "name": "Build and Test", "run_attempt": 1, "conclusion": "success", "head_sha": self.head_sha}
                    ]
                })
            if "/actions/runs/1/artifacts" in url:
                return MockHttpResponse({"artifacts": []})
            if "/actions/runs/1/jobs" in url:
                return MockHttpResponse({"jobs": []})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        stdout_capture = io.StringIO()
        with patch("sys.stdout", stdout_capture):
            exit_code = generate_handoff.main(["--pr", "31", "--token", "mock-token"])

        self.assertEqual(exit_code, 0)
        output = stdout_capture.getvalue()
        # Should record the review, but route to ChatGPT verification of current head, not Human merge
        self.assertIn("chatgpt-20261004-pr31-rev1`: **PASS**", output)
        self.assertIn("Next Owner**: **ChatGPT**", output)
        self.assertIn("Coordinator verification of current head", output)
        self.assertNotIn("Final review and merge decision", output)

    @patch("urllib.request.urlopen")
    def test_cli_pass_on_different_base_with_missing_provenance_and_trx(self, mock_urlopen):
        """Verifies that PASS against a different base branch commit with missing provenance/TRX never routes to Human merge."""
        different_base_sha = "f" * 40
        pr_with_diff_base_review = dict(self.sample_pr)
        pr_with_diff_base_review["body"] = (
            "## Primary Issue\nFixes #31\n\n"
            "<!-- AI-REVIEW-RECORD: chatgpt-20261004-pr31-rev1 -->\n"
            "## ChatGPT coordinator verification\n\n"
            f"**Result: PASS.** Reviewed head `{self.head_sha}` against base `{different_base_sha}`\n"
            "<!-- AI-REVIEW-RECORD-END -->\n"
        )
        clean_issue = dict(self.sample_issue)
        clean_issue["body"] = "### Objective\nStandard task without pending decisions.\n"

        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if "/pulls/31" in url:
                return MockHttpResponse(pr_with_diff_base_review)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if "/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "/issues/31" in url:
                return MockHttpResponse(clean_issue)
            if "/actions/runs?head_sha=" in url:
                return MockHttpResponse({
                    "workflow_runs": [
                        {"id": 1, "name": "Build and Test", "run_attempt": 1, "conclusion": "success", "head_sha": self.head_sha}
                    ]
                })
            if "/actions/runs/1/artifacts" in url:
                return MockHttpResponse({"artifacts": []})
            if "/actions/runs/1/jobs" in url:
                return MockHttpResponse({"jobs": []})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        stdout_capture = io.StringIO()
        with patch("sys.stdout", stdout_capture):
            exit_code = generate_handoff.main(["--pr", "31", "--token", "mock-token"])

        self.assertEqual(exit_code, 0)
        output = stdout_capture.getvalue()
        self.assertIn("Tested Checkout SHA**: UNAVAILABLE / UNPROVEN", output)
        self.assertIn("TRX Test Totals**: UNAVAILABLE", output)
        self.assertIn("Next Owner**: **ChatGPT**", output)
        self.assertIn("Coordinator verification of current head", output)
        self.assertNotIn("Final review and merge decision", output)

    @patch("urllib.request.urlopen")
    def test_cli_incomplete_result_containing_pass_does_not_route_to_merge(self, mock_urlopen):
        """Verifies that review with 'Result: INCOMPLETE - some tests PASS' is parsed as INCOMPLETE and does not route to merge."""
        pr_with_incomplete_review = dict(self.sample_pr)
        pr_with_incomplete_review["body"] = (
            "## Primary Issue\nFixes #31\n\n"
            "<!-- AI-REVIEW-RECORD: chatgpt-20261004-pr31-rev1 -->\n"
            "## ChatGPT coordinator verification\n\n"
            f"**Result: INCOMPLETE - some tests PASS.** Reviewed head `{self.head_sha}`\n"
            "<!-- AI-REVIEW-RECORD-END -->\n"
        )
        clean_issue = dict(self.sample_issue)
        clean_issue["body"] = "### Objective\nStandard task without pending decisions.\n"

        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if "/pulls/31" in url:
                return MockHttpResponse(pr_with_incomplete_review)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if "/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "/issues/31" in url:
                return MockHttpResponse(clean_issue)
            if "/actions/runs?head_sha=" in url:
                return MockHttpResponse({
                    "workflow_runs": [
                        {"id": 1, "name": "Build and Test", "run_attempt": 1, "conclusion": "success", "head_sha": self.head_sha}
                    ]
                })
            if "/actions/runs/1/artifacts" in url:
                return MockHttpResponse({"artifacts": []})
            if "/actions/runs/1/jobs" in url:
                return MockHttpResponse({"jobs": []})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        stdout_capture = io.StringIO()
        with patch("sys.stdout", stdout_capture):
            exit_code = generate_handoff.main(["--pr", "31", "--token", "mock-token"])

        self.assertEqual(exit_code, 0)
        output = stdout_capture.getvalue()
        self.assertIn("chatgpt-20261004-pr31-rev1`: **INCOMPLETE**", output)
        self.assertNotIn("chatgpt-20261004-pr31-rev1`: **PASS**", output)
        self.assertIn("Next Owner**: **ChatGPT**", output)
        self.assertIn("Coordinator verification of current head", output)
        self.assertNotIn("Final review and merge decision", output)

    @patch("urllib.request.urlopen")
    def test_cli_mismatched_ci_run_head_sha_reported_as_uncertain(self, mock_urlopen):
        """Verifies that workflow run with mismatched head_sha is flagged as uncertainty."""
        mismatched_head_sha = "d" * 40

        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if "/pulls/31" in url:
                return MockHttpResponse(self.sample_pr)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if "/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "/issues/31" in url:
                return MockHttpResponse(self.sample_issue)
            if "/actions/runs?head_sha=" in url:
                return MockHttpResponse({
                    "workflow_runs": [
                        {
                            "id": 1,
                            "name": "Build and Test",
                            "run_attempt": 1,
                            "conclusion": "success",
                            "head_sha": mismatched_head_sha,
                        }
                    ]
                })
            if "/actions/runs/1/artifacts" in url:
                return MockHttpResponse({"artifacts": []})
            if "/actions/runs/1/jobs" in url:
                return MockHttpResponse({"jobs": []})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        stdout_capture = io.StringIO()
        with patch("sys.stdout", stdout_capture):
            exit_code = generate_handoff.main(["--pr", "31", "--token", "mock-token"])

        self.assertEqual(exit_code, 0)
        output = stdout_capture.getvalue()
        self.assertIn("CI run head SHA mismatch", output)

    @patch("urllib.request.urlopen")
    def test_cli_unknown_decision_status_scoped_block(self, mock_urlopen):
        """Verifies that an unrecognized decision status in the governance block reports UNKNOWN."""
        malformed_issue = {
            "number": 31,
            "title": "Task with unrecognized status",
            "state": "open",
            "body": (
                "### Objective\nStandard task.\n\n"
                "### Decision Governance Block\n"
                "- **Decision required**: Option A vs Option B\n"
                "- **Proposed option**: Option A\n"
                "- **Status**: UNRECOGNIZED_STATUS\n"
                "- **Source of explicit human approval**: None\n"
            ),
        }

        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if "/pulls/31" in url:
                return MockHttpResponse(self.sample_pr)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if "/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "/issues/31" in url:
                return MockHttpResponse(malformed_issue)
            if "/actions/runs?head_sha=" in url:
                return MockHttpResponse({"workflow_runs": []})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        stdout_capture = io.StringIO()
        with patch("sys.stdout", stdout_capture):
            exit_code = generate_handoff.main(["--pr", "31", "--token", "mock-token"])

        self.assertEqual(exit_code, 0)
        output = stdout_capture.getvalue()
        self.assertIn("Decision Governance**: `UNKNOWN (UNRECOGNIZED_STATUS)`", output)

    @patch("urllib.request.urlopen")
    def test_cli_ambiguous_provenance_candidates_rejected(self, mock_urlopen):
        """Verifies that ambiguous build-provenance candidates are rejected as uncertain."""
        def fake_urlopen(req, *args, **kwargs):
            url = req.full_url if hasattr(req, "full_url") else str(req)
            if "/pulls/31" in url:
                return MockHttpResponse(self.sample_pr)
            if f"/repos/{self.repo}" in url and "/git" not in url and "/pulls" not in url and "/issues" not in url and "/actions" not in url:
                return MockHttpResponse({"default_branch": "main"})
            if "/git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "/issues/31" in url:
                return MockHttpResponse(self.sample_issue)
            if "/actions/runs?head_sha=" in url:
                return MockHttpResponse({
                    "workflow_runs": [
                        {"id": 1, "name": "Build and Test", "run_attempt": 1, "conclusion": "success", "head_sha": self.head_sha}
                    ]
                })
            if "/actions/runs/1/artifacts" in url:
                return MockHttpResponse({
                    "artifacts": [
                        {"id": 10, "name": "build-provenance", "archive_download_url": "https://api.github.com/prov1.zip"},
                        {"id": 11, "name": "build-provenance", "archive_download_url": "https://api.github.com/prov2.zip"},
                    ]
                })
            if "/actions/runs/1/jobs" in url:
                return MockHttpResponse({"jobs": []})
            return MockHttpResponse({})

        mock_urlopen.side_effect = fake_urlopen

        stdout_capture = io.StringIO()
        with patch("sys.stdout", stdout_capture):
            exit_code = generate_handoff.main(["--pr", "31", "--token", "mock-token"])

        self.assertEqual(exit_code, 0)
        output = stdout_capture.getvalue()
        self.assertIn("Ambiguous build-provenance artifacts: found 2 candidates; rejected", output)
        self.assertIn("Tested Checkout SHA**: UNAVAILABLE / UNPROVEN", output)


if __name__ == "__main__":
    unittest.main()

