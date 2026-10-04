"""Unit and regression tests for compact task handoff generator."""

from __future__ import annotations

import io
import json
import os
import sys
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ai-workflow"))

import generate_handoff
from parse_trx import TrxTotals


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


class TestHandoffGenerator(unittest.TestCase):
    """Tests for generate_handoff.py covering full identities, budget, decision preflight, and evidence."""

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
            live_default_branch_ref="main",
            live_default_branch_sha=self.main_sha,
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
            live_default_branch_ref="main",
            live_default_branch_sha=self.main_sha,
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


if __name__ == "__main__":
    unittest.main()
