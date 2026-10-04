import datetime
import json
import os
import sys
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ai-workflow"))

import generate_review_packet


class TestReviewPacket(unittest.TestCase):
    """Regression tests for review packet provenance, ordering, race conditions, and budgeting."""

    def setUp(self):
        self.sample_manifest = [
            {"filename": "src/DXVKCompanion/DxvkManager.cs", "additions": 10, "deletions": 5, "status": "modified", "patch": "@@ -1,5 +1,10 @@\n+change"},
            {"filename": "tests/DXVKCompanion.PhaseA.Tests/DxvkManagerTests.cs", "additions": 20, "deletions": 2, "status": "modified", "patch": "@@ -1,2 +1,20 @@\n+test"},
            {"filename": "docs/AI-DEVELOPMENT-WORKFLOW.md", "additions": 5, "deletions": 1, "status": "modified", "patch": "@@ -1,1 +1,5 @@\n+doc"},
        ]
        self.sample_ci_jobs = [
            {"name": "build-and-test", "conclusion": "success", "steps": [
                {"name": "Restore application", "conclusion": "success"},
                {"name": "Build application", "conclusion": "success"},
                {"name": "Run Phase A tests", "conclusion": "success"},
            ]}
        ]

    def test_complete_provenance_identity_with_build_provenance(self):
        packet_md = generate_review_packet.build_packet_content(
            repo="Tiflit/DXVK-Companion",
            pr_number=9,
            pr_title="Prevent DXVK deployment for DX12/Vulkan",
            pr_url="https://github.com/Tiflit/DXVK-Companion/pull/9",
            reviewed_head_sha="c4d0f846b4031b08e9e3444c803abe37cc171890",
            base_sha="3a66376446d847434542410a2a3626a070639aa8",
            live_pr_head_sha="c4d0f846b4031b08e9e3444c803abe37cc171890",
            run_id="37091615107",
            run_attempt="1",
            run_head_sha="c4d0f846b4031b08e9e3444c803abe37cc171890",
            build_provenance={
                "head_sha": "8156ffa10f071f8fcc7b9a20f81c7564b9c58f25",
                "ref": "refs/pull/9/merge",
                "run_id": "37091615107",
                "run_attempt": "1",
            },
            task_contract={"issue_number": 6, "title": "Prevent DXVK deployment", "body": "### Objective\nPrevent deployment"},
            ci_jobs=self.sample_ci_jobs,
            trx_totals={"status": "passed", "total": 96, "passed": 96, "failed": 0, "skipped": 0, "source": "phase-a-tests.trx"},
            manifest=self.sample_manifest,
            capture_time="2026-10-03T20:00:00Z",
        )

        # Verify identity fields
        self.assertIn("Repository: `Tiflit/DXVK-Companion`", packet_md)
        self.assertIn("PR: #9", packet_md)
        self.assertIn("Triggering run ID: `37091615107` (attempt 1)", packet_md)
        self.assertIn("Run head SHA: `c4d0f846b4031b08e9e3444c803abe37cc171890`", packet_md)
        self.assertIn("Tested checkout SHA: `8156ffa10f071f8fcc7b9a20f81c7564b9c58f25`", packet_md)
        self.assertIn("Checkout relationship: `synthetic merge ref refs/pull/9/merge`", packet_md)
        self.assertIn("Base SHA: `3a66376446d847434542410a2a3626a070639aa8`", packet_md)
        self.assertIn("Metadata capture time: `2026-10-03T20:00:00Z`", packet_md)

    def test_missing_build_provenance_is_explicitly_incomplete_never_guessed(self):
        packet_md = generate_review_packet.build_packet_content(
            repo="Tiflit/DXVK-Companion",
            pr_number=9,
            pr_title="Prevent DXVK deployment",
            pr_url="https://github.com/Tiflit/DXVK-Companion/pull/9",
            reviewed_head_sha="c4d0f846b4031b08e9e3444c803abe37cc171890",
            base_sha="3a66376446d847434542410a2a3626a070639aa8",
            live_pr_head_sha="c4d0f846b4031b08e9e3444c803abe37cc171890",
            run_id="37091615107",
            run_attempt="1",
            run_head_sha="c4d0f846b4031b08e9e3444c803abe37cc171890",
            build_provenance=None,  # Missing!
            task_contract=None,
            ci_jobs=self.sample_ci_jobs,
            trx_totals={"status": "unavailable", "reason": "No TRX artifact found"},
            manifest=self.sample_manifest,
            capture_time="2026-10-03T20:00:00Z",
        )

        # Must report incomplete checkout identity rather than guessing run_head_sha
        self.assertIn("Tested checkout SHA: `incomplete`", packet_md)
        self.assertNotIn("Tested checkout SHA: `c4d0f846b4031b08e9e3444c803abe37cc171890`", packet_md)
        self.assertIn("build-provenance.json was unavailable", packet_md)

    def test_stale_pr_head_detection(self):
        packet_md = generate_review_packet.build_packet_content(
            repo="Tiflit/DXVK-Companion",
            pr_number=9,
            pr_title="PR title",
            pr_url="https://github.com/Tiflit/DXVK-Companion/pull/9",
            reviewed_head_sha="commit_A_1111111111111111111111111111111111111111",
            base_sha="base_0000000000000000000000000000000000000000",
            live_pr_head_sha="commit_B_2222222222222222222222222222222222222222",  # PR advanced!
            run_id="12345",
            run_attempt="1",
            run_head_sha="commit_A_1111111111111111111111111111111111111111",
            build_provenance=None,
            task_contract=None,
            ci_jobs=self.sample_ci_jobs,
            trx_totals={"status": "passed", "total": 96, "passed": 96, "failed": 0, "skipped": 0, "source": "phase-a-tests.trx"},
            manifest=self.sample_manifest,
            capture_time="2026-10-03T20:00:00Z",
        )

        # Must label current-head staleness clearly
        self.assertIn("CURRENT-HEAD STALENESS: STALE", packet_md)
        self.assertIn("commit_B_2222222222222222222222222222222222222222", packet_md)
        self.assertIn("commit_A_1111111111111111111111111111111111111111", packet_md)

    def test_packet_section_ordering(self):
        packet_md = generate_review_packet.build_packet_content(
            repo="Tiflit/DXVK-Companion",
            pr_number=9,
            pr_title="Title",
            pr_url="https://github.com/...",
            reviewed_head_sha="c4d0f846b4031b08e9e3444c803abe37cc171890",
            base_sha="3a66376446d847434542410a2a3626a070639aa8",
            live_pr_head_sha="c4d0f846b4031b08e9e3444c803abe37cc171890",
            run_id="12345",
            run_attempt="1",
            run_head_sha="c4d0f846b4031b08e9e3444c803abe37cc171890",
            build_provenance=None,
            task_contract={"issue_number": 6, "title": "Issue Title", "body": "Issue Body"},
            ci_jobs=self.sample_ci_jobs,
            trx_totals={"status": "passed", "total": 96, "passed": 96, "failed": 0, "skipped": 0, "source": "phase-a-tests.trx"},
            manifest=self.sample_manifest,
            capture_time="2026-10-03T20:00:00Z",
        )

        # Order must be:
        # 1. Identity & Contract
        # 2. CI & Test totals
        # 3. Complete manifest
        # 4. Changed tests diff
        # 5. Production changes diff
        # 6. Workflow/docs & Review focus
        idx_identity = packet_md.find("## 1. Revision and Evidence Identity")
        idx_contract = packet_md.find("## 2. Task Contract")
        idx_ci = packet_md.find("## 3. Deterministic CI and Test Totals")
        idx_manifest = packet_md.find("## 4. Complete Changed Files Manifest")
        idx_tests = packet_md.find("## 5. Changed Tests")
        idx_prod = packet_md.find("## 6. Production Changes")
        idx_focus = packet_md.find("## 7. Workflow, Documentation, and Review Focus")

        self.assertTrue(0 <= idx_identity < idx_contract < idx_ci < idx_manifest < idx_tests < idx_prod < idx_focus)

    def test_budgeting_preserves_production_context_and_explicit_truncation(self):
        # Create a massive test patch and a production patch
        large_test_patch = "\n".join([f"+test line {i}" for i in range(1500)])
        large_prod_patch = "\n".join([f"+prod line {i}" for i in range(1500)])
        manifest = [
            {"filename": "tests/Test1.cs", "additions": 1500, "deletions": 0, "status": "modified", "patch": large_test_patch},
            {"filename": "src/Prod1.cs", "additions": 1500, "deletions": 0, "status": "modified", "patch": large_prod_patch},
        ]

        packet_md = generate_review_packet.build_packet_content(
            repo="Tiflit/DXVK-Companion",
            pr_number=9,
            pr_title="Title",
            pr_url="https://github.com/...",
            reviewed_head_sha="head",
            base_sha="base",
            live_pr_head_sha="head",
            run_id="12345",
            run_attempt="1",
            run_head_sha="head",
            build_provenance=None,
            task_contract=None,
            ci_jobs=[],
            trx_totals={"status": "passed", "total": 1, "passed": 1, "failed": 0, "skipped": 0, "source": "t.trx"},
            manifest=manifest,
            capture_time="2026-10-03T20:00:00Z",
            max_diff_lines_per_section=200,
        )

        # Both test and production sections must have content, and both must explicitly announce truncation
        self.assertIn("## 5. Changed Tests", packet_md)
        self.assertIn("## 6. Production Changes", packet_md)
        self.assertIn("prod line", packet_md)
        self.assertIn("test line", packet_md)
        self.assertIn("[Section diff truncated at 200 lines; inspect complete diff artifact]", packet_md)

    def test_safe_data_handling_with_special_characters(self):
        malicious_title = '"><script>alert(1)</script>; rm -rf /; `whoami`\nNew line'
        packet_md = generate_review_packet.build_packet_content(
            repo="Tiflit/DXVK-Companion",
            pr_number=9,
            pr_title=malicious_title,
            pr_url="https://github.com/...",
            reviewed_head_sha="head",
            base_sha="base",
            live_pr_head_sha="head",
            run_id="12345",
            run_attempt="1",
            run_head_sha="head",
            build_provenance=None,
            task_contract={"issue_number": 6, "title": malicious_title, "body": malicious_title},
            ci_jobs=[],
            trx_totals={"status": "unavailable", "reason": "none"},
            manifest=self.sample_manifest,
            capture_time="2026-10-03T20:00:00Z",
        )
        self.assertIn("PR: #9", packet_md)
        self.assertIn(malicious_title, packet_md)


if __name__ == "__main__":
    unittest.main()
