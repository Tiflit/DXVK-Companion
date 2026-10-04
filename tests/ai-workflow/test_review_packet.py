import datetime
import io
import json
import os
import sys
import tempfile
import unittest
import zipfile
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

    def test_production_file_with_test_in_name_classified_as_production(self):
        # A file like src/.../TestModeHelper.cs must be classified under Production Changes, NOT Changed Tests (F12).
        manifest = [
            {"filename": "src/DXVKCompanion/Testing/TestModeHelper.cs", "additions": 10, "deletions": 2, "status": "modified", "patch": "@@ -1 +1 @@\n+code"},
            {"filename": "tests/DXVKCompanion.PhaseA.Tests/SomeTests.cs", "additions": 5, "deletions": 0, "status": "modified", "patch": "@@ -1 +1 @@\n+test"},
        ]
        packet_md = generate_review_packet.build_packet_content(
            repo="Tiflit/DXVK-Companion",
            pr_number=19,
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
        )
        # Check production section contains TestModeHelper
        prod_idx = packet_md.find("## 6. Production Changes")
        test_idx = packet_md.find("## 5. Changed Tests")
        helper_idx = packet_md.find("`src/DXVKCompanion/Testing/TestModeHelper.cs`", prod_idx)
        self.assertNotEqual(helper_idx, -1, "TestModeHelper.cs must appear under Production Changes section")
        test_section = packet_md[test_idx:prod_idx]
        self.assertNotIn("TestModeHelper.cs", test_section)

    def test_generate_packet_orchestration_happy_path(self):

        # Tests end-to-end event -> API -> artifact download -> TRX & provenance parsing -> packet & diff (F1, F2).
        import io
        import tempfile
        import zipfile

        def make_zip(entries):
            bio = io.BytesIO()
            with zipfile.ZipFile(bio, "w") as zf:
                for k, v in entries.items():
                    zf.writestr(k, v.encode("utf-8") if isinstance(v, str) else v)
            return bio.getvalue()

        trx_xml = (
            '<?xml version="1.0" encoding="utf-8"?>'
            '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
            '  <ResultSummary outcome="Completed">'
            '    <Counters total="67" passed="67" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" notRunnable="0" notExecuted="0" />'
            '  </ResultSummary>'
            '</TestRun>'
        )
        prov_json = (
            '{"head_sha": "faec613332c3a7d5fcee44fc8b257839d150dddf",'
            ' "ref": "refs/pull/19/merge",'
            ' "sha": "faec613332c3a7d5fcee44fc8b257839d150dddf",'
            ' "run_id": "37164136438",'
            ' "run_attempt": "1"}'
        )

        class MockClient(generate_review_packet.GitHubClient):
            def __init__(self):
                super().__init__(token="dummy", repo="Tiflit/DXVK-Companion")

            def get_pr(self, num):
                return {
                    "head": {"sha": "0abef8d34a836494cdd882b844c781e32fa35322"},
                    "base": {"sha": "e7b6e0640d9a22077fb515b1dfc2a277e987785e"},
                    "title": "Harden provenance",
                    "html_url": "https://github.com/Tiflit/DXVK-Companion/pull/19",
                    "body": "## Primary Issue\n\nFixes #11\n\n## Verification\n\nAll tests pass.\n",
                }

            def get_issue(self, num):
                return {"title": "Issue 11", "body": "### Allowed paths\n- scripts/ai-workflow/**\n"}

            def get_workflow_run_jobs(self, run_id, attempt=None):
                return [{"name": "build-and-test", "conclusion": "success", "steps": []}]

            def compare_commits(self, base_sha, head_sha):
                return {
                    "files": [
                        {"filename": "scripts/ai-workflow/generate_review_packet.py", "additions": 10, "deletions": 2, "status": "modified", "patch": "@@ -1 +1 @@\n+code"},
                        {"filename": "docs/AI-PILOT-LOG.md", "additions": 5, "deletions": 0, "status": "modified", "patch": None},
                    ]
                }

            def get_run_artifacts(self, run_id):
                return [
                    {"name": "build-provenance", "archive_download_url": "https://api.github.com/art/prov/zip"},
                    {"name": "phase-a-test-results", "archive_download_url": "https://api.github.com/art/trx/zip"},
                ]

            def download_bytes(self, url, max_bytes=50*1024*1024, timeout=30):
                if "prov" in url:
                    return make_zip({"build-provenance.json": prov_json})
                if "trx" in url:
                    return make_zip({"phase-a-tests.trx": trx_xml})
                raise ValueError(url)

        event = {
            "workflow_run": {
                "id": 37164136438,
                "run_attempt": 1,
                "head_sha": "0abef8d34a836494cdd882b844c781e32fa35322",
                "pull_requests": [{
                    "number": 19,
                    "base": {"sha": "e7b6e0640d9a22077fb515b1dfc2a277e987785e"},
                    "head": {"sha": "0abef8d34a836494cdd882b844c781e32fa35322"},
                }],
            }
        }

        with tempfile.TemporaryDirectory() as tmpdir:
            out_dir = Path(tmpdir) / "packet"
            summary_p = Path(tmpdir) / "summary.md"
            github_out_p = Path(tmpdir) / "output.txt"

            res = generate_review_packet.generate_packet(
                event=event,
                client=MockClient(),
                output_dir=out_dir,
                step_summary_path=summary_p,
                github_output_path=github_out_p,
            )
            self.assertEqual(res, 0)
            packet_file = out_dir / "review_packet.md"
            self.assertTrue(packet_file.exists())
            packet_text = packet_file.read_text(encoding="utf-8")

            # Check parsed provenance
            self.assertIn("Tested checkout SHA: `faec613332c3a7d5fcee44fc8b257839d150dddf`", packet_text)
            self.assertIn("Checkout relationship: `synthetic merge ref refs/pull/19/merge`", packet_text)
            # Check parsed TRX
            self.assertIn("Total tests: 67", packet_text)
            self.assertIn("Passed: 67", packet_text)
            # Check diff artifact
            diff_file = out_dir / "full-diff-pr-19.diff"
            self.assertTrue(diff_file.exists())
            diff_text = diff_file.read_text(encoding="utf-8")
            self.assertIn("Patch omitted by GitHub API for docs/AI-PILOT-LOG.md", diff_text)

    def test_generate_packet_declines_ambiguous_pr_associations(self):
        # Multiple associated PRs without explicit disambiguation must fail (F3).
        event = {
            "workflow_run": {
                "id": 12345,
                "run_attempt": 1,
                "head_sha": "0abef8d34a836494cdd882b844c781e32fa35322",
                "pull_requests": [
                    {"number": 19, "base": {"sha": "e7b6e0640d9a22077fb515b1dfc2a277e987785e"}},
                    {"number": 20, "base": {"sha": "e7b6e0640d9a22077fb515b1dfc2a277e987785e"}},
                ],
            }
        }
        client = generate_review_packet.GitHubClient(token="dummy", repo="Tiflit/DXVK-Companion")
        with tempfile.TemporaryDirectory() as tmpdir:
            res = generate_review_packet.generate_packet(
                event=event,
                client=client,
                output_dir=Path(tmpdir),
                override_pr_number=None,  # No disambiguation provided
            )
            self.assertEqual(res, 1)

    def test_generate_packet_disambiguates_multiple_prs_with_arg(self):
        # Multiple associated PRs with valid --pr-number succeeds (F3).
        event = {
            "workflow_run": {
                "id": 12345,
                "run_attempt": 1,
                "head_sha": "0abef8d34a836494cdd882b844c781e32fa35322",
                "pull_requests": [
                    {"number": 19, "base": {"sha": "e7b6e0640d9a22077fb515b1dfc2a277e987785e"}},
                    {"number": 20, "base": {"sha": "e7b6e0640d9a22077fb515b1dfc2a277e987785e"}},
                ],
            }
        }
        class MockClient(generate_review_packet.GitHubClient):
            def __init__(self):
                super().__init__(token="dummy", repo="Tiflit/DXVK-Companion")
            def get_pr(self, num):
                return {"head": {"sha": "0abef8d34a836494cdd882b844c781e32fa35322"}, "base": {"sha": "e7b6e0640d9a22077fb515b1dfc2a277e987785e"}, "title": "PR 19"}
            def get_issue(self, num):
                return {}
            def get_workflow_run_jobs(self, run_id, attempt=None):
                return []
            def compare_commits(self, base_sha, head_sha):
                return {"files": []}
            def get_run_artifacts(self, run_id):
                return []

        with tempfile.TemporaryDirectory() as tmpdir:
            res = generate_review_packet.generate_packet(
                event=event,
                client=MockClient(),
                output_dir=Path(tmpdir),
                override_pr_number=19,
            )
            self.assertEqual(res, 0)

    def test_generate_packet_detects_base_movement(self):
        # If live PR base has moved beyond triggering event's tested base SHA, emit warning (F4).
        event = {
            "workflow_run": {
                "id": 12345,
                "run_attempt": 1,
                "head_sha": "0abef8d34a836494cdd882b844c781e32fa35322",
                "pull_requests": [{
                    "number": 19,
                    "base": {"sha": "1111111111111111111111111111111111111111"},
                }],
            }
        }
        class MockClient(generate_review_packet.GitHubClient):
            def __init__(self):
                super().__init__(token="dummy", repo="Tiflit/DXVK-Companion")
            def get_pr(self, num):
                # Live base moved to 22222...
                return {
                    "head": {"sha": "0abef8d34a836494cdd882b844c781e32fa35322"},
                    "base": {"sha": "2222222222222222222222222222222222222222"},
                    "title": "PR 19",
                }
            def get_issue(self, num): return {}
            def get_workflow_run_jobs(self, run_id, attempt=None): return []
            def compare_commits(self, base_sha, head_sha): return {"files": []}
            def get_run_artifacts(self, run_id): return []

        with tempfile.TemporaryDirectory() as tmpdir:
            res = generate_review_packet.generate_packet(
                event=event,
                client=MockClient(),
                output_dir=Path(tmpdir),
                override_pr_number=19,
            )
            self.assertEqual(res, 0)
            packet_text = (Path(tmpdir) / "review_packet.md").read_text(encoding="utf-8")
            self.assertIn("BASE-BRANCH MOVEMENT: BASE MOVED", packet_text)
            self.assertIn("Tested Base SHA: `1111111111111111111111111111111111111111`", packet_text)
            self.assertIn("Live Base SHA: `2222222222222222222222222222222222222222`", packet_text)

    def test_generate_packet_rejects_attempt_mismatch(self):
        # Provenance from a different attempt must be rejected (F5).
        import io, zipfile

        prov_json = (
            '{"head_sha": "faec613332c3a7d5fcee44fc8b257839d150dddf",'
            ' "ref": "refs/pull/19/merge",'
            ' "run_id": "12345",'
            ' "run_attempt": "2"}'  # Attempt 2!
        )
        bio = io.BytesIO()
        with zipfile.ZipFile(bio, "w") as zf:
            zf.writestr("build-provenance.json", prov_json)
        zip_bytes = bio.getvalue()

        event = {
            "workflow_run": {
                "id": 12345,
                "run_attempt": 1,  # Attempt 1!
                "head_sha": "0abef8d34a836494cdd882b844c781e32fa35322",
                "pull_requests": [{"number": 19, "base": {"sha": "1111111111111111111111111111111111111111"}}],
            }
        }
        class MockClient(generate_review_packet.GitHubClient):
            def __init__(self):
                super().__init__(token="dummy", repo="Tiflit/DXVK-Companion")
            def get_pr(self, num): return {"head": {"sha": "0abef8d34a836494cdd882b844c781e32fa35322"}, "base": {"sha": "1111111111111111111111111111111111111111"}}
            def get_issue(self, num): return {}
            def get_workflow_run_jobs(self, run_id, attempt=None): return []
            def compare_commits(self, base_sha, head_sha): return {"files": []}
            def get_run_artifacts(self, run_id):
                return [{"name": "build-provenance", "archive_download_url": "https://api.github.com/prov"}]
            def download_bytes(self, url, max_bytes=50*1024*1024, timeout=30): return zip_bytes

        with tempfile.TemporaryDirectory() as tmpdir:
            res = generate_review_packet.generate_packet(
                event=event,
                client=MockClient(),
                output_dir=Path(tmpdir),
                override_pr_number=19,
            )
            self.assertEqual(res, 0)
            packet_text = (Path(tmpdir) / "review_packet.md").read_text(encoding="utf-8")
            self.assertIn("Attempt mismatch: provenance recorded attempt 2 vs triggering attempt 1", packet_text)

    def test_missing_event_base_declines_comparison_and_reports_unknown(self):
        # F4: Missing event base must NEVER fall back to live base as tested evidence.
        # It must report Tested Base SHA as unknown/incomplete and decline source comparison.
        event = {
            "workflow_run": {
                "id": 12345,
                "run_attempt": 1,
                "head_sha": "0abef8d34a836494cdd882b844c781e32fa35322",
                "pull_requests": [{"number": 19, "base": {}}],  # Missing base SHA!
            }
        }
        compare_called = []
        class MockClient(generate_review_packet.GitHubClient):
            def __init__(self):
                super().__init__(token="dummy", repo="Tiflit/DXVK-Companion")
            def get_pr(self, num):
                return {
                    "head": {"sha": "0abef8d34a836494cdd882b844c781e32fa35322"},
                    "base": {"sha": "2222222222222222222222222222222222222222"},
                    "title": "PR 19",
                    "changed_files": 1,
                }
            def get_issue(self, num): return {}
            def get_workflow_run_jobs(self, run_id, attempt=None): return []
            def compare_commits(self, base_sha, head_sha):
                compare_called.append((base_sha, head_sha))
                return {"files": []}
            def get_run_artifacts(self, run_id): return []

        with tempfile.TemporaryDirectory() as tmpdir:
            res = generate_review_packet.generate_packet(
                event=event,
                client=MockClient(),
                output_dir=Path(tmpdir),
                override_pr_number=19,
            )
            self.assertEqual(res, 0)
            packet_text = (Path(tmpdir) / "review_packet.md").read_text(encoding="utf-8")
            # Tested base must NOT be 2222... (live base)
            self.assertNotIn("Tested Base SHA: `2222222222222222222222222222222222222222`", packet_text)
            self.assertIn("Tested Base SHA: `unknown`", packet_text)
            self.assertIn("Live Base SHA: `2222222222222222222222222222222222222222`", packet_text)
            self.assertIn("comparison declined", packet_text.lower())
            # compare_commits must NOT have been called with live base or unknown base
            self.assertEqual(len(compare_called), 0)

    def test_base_provenance_disagreement_detected(self):
        # F4: If provenance contains a base SHA that disagrees with event base SHA, detect and flag it.
        prov_json = (
            '{"head_sha": "faec613332c3a7d5fcee44fc8b257839d150dddf",'
            ' "base_sha": "3333333333333333333333333333333333333333",'  # Differs from event base 1111...
            ' "ref": "refs/pull/19/merge",'
            ' "run_id": "12345",'
            ' "run_attempt": "1"}'
        )
        bio = io.BytesIO()
        with zipfile.ZipFile(bio, "w") as zf:
            zf.writestr("build-provenance.json", prov_json)
        zip_bytes = bio.getvalue()

        event = {
            "workflow_run": {
                "id": 12345,
                "run_attempt": 1,
                "head_sha": "0abef8d34a836494cdd882b844c781e32fa35322",
                "pull_requests": [{"number": 19, "base": {"sha": "1111111111111111111111111111111111111111"}}],
            }
        }
        class MockClient(generate_review_packet.GitHubClient):
            def __init__(self):
                super().__init__(token="dummy", repo="Tiflit/DXVK-Companion")
            def get_pr(self, num): return {"head": {"sha": "0abef8d34a836494cdd882b844c781e32fa35322"}, "base": {"sha": "1111111111111111111111111111111111111111"}}
            def get_issue(self, num): return {}
            def get_workflow_run_jobs(self, run_id, attempt=None): return []
            def compare_commits(self, base_sha, head_sha): return {"files": []}
            def get_run_artifacts(self, run_id):
                return [{"name": "build-provenance", "archive_download_url": "https://api.github.com/prov"}]
            def download_bytes(self, url, max_bytes=50*1024*1024, timeout=30): return zip_bytes

        with tempfile.TemporaryDirectory() as tmpdir:
            res = generate_review_packet.generate_packet(
                event=event,
                client=MockClient(),
                output_dir=Path(tmpdir),
                override_pr_number=19,
            )
            self.assertEqual(res, 0)
            packet_text = (Path(tmpdir) / "review_packet.md").read_text(encoding="utf-8")
            self.assertIn("Base SHA disagreement", packet_text)

    def test_github_client_jobs_attempt_filtering_rejects_unmatched_jobs(self):
        # F5: When attempt endpoint 404s, fallback to run-level jobs must filter by attempt
        # and NEVER return unmatched jobs from other attempts.
        class MockClient(generate_review_packet.GitHubClient):
            def __init__(self):
                super().__init__(token="dummy", repo="Tiflit/DXVK-Companion")
            def fetch_json(self, url, timeout=30):
                if "/attempts/2/jobs" in url:
                    import urllib.error
                    raise urllib.error.HTTPError(url, 404, "Not Found", {}, None)
                if "/actions/runs/123/jobs" in url:
                    return {"jobs": [
                        {"id": 1, "run_attempt": 1, "name": "build-and-test"},
                        {"id": 2, "run_attempt": 1, "name": "other-job"},
                    ]}
                return {}

        client = MockClient()
        jobs = client.get_workflow_run_jobs(run_id="123", attempt="2")
        # Must NOT return attempt 1 jobs when attempt 2 was requested!
        self.assertEqual(jobs, [])

    def test_missing_provenance_identifiers_marked_incomplete(self):
        # F5: build-provenance missing run_id or run_attempt must be rejected as incomplete.
        prov_json = (
            '{"head_sha": "faec613332c3a7d5fcee44fc8b257839d150dddf",'
            ' "ref": "refs/pull/19/merge"}'  # Missing run_id and run_attempt!
        )
        bio = io.BytesIO()
        with zipfile.ZipFile(bio, "w") as zf:
            zf.writestr("build-provenance.json", prov_json)
        zip_bytes = bio.getvalue()

        event = {
            "workflow_run": {
                "id": 12345,
                "run_attempt": 1,
                "head_sha": "0abef8d34a836494cdd882b844c781e32fa35322",
                "pull_requests": [{"number": 19, "base": {"sha": "1111111111111111111111111111111111111111"}}],
            }
        }
        class MockClient(generate_review_packet.GitHubClient):
            def __init__(self):
                super().__init__(token="dummy", repo="Tiflit/DXVK-Companion")
            def get_pr(self, num): return {"head": {"sha": "0abef8d34a836494cdd882b844c781e32fa35322"}, "base": {"sha": "1111111111111111111111111111111111111111"}}
            def get_issue(self, num): return {}
            def get_workflow_run_jobs(self, run_id, attempt=None): return []
            def compare_commits(self, base_sha, head_sha): return {"files": []}
            def get_run_artifacts(self, run_id):
                return [{"name": "build-provenance", "archive_download_url": "https://api.github.com/prov"}]
            def download_bytes(self, url, max_bytes=50*1024*1024, timeout=30): return zip_bytes

        with tempfile.TemporaryDirectory() as tmpdir:
            res = generate_review_packet.generate_packet(
                event=event,
                client=MockClient(),
                output_dir=Path(tmpdir),
                override_pr_number=19,
            )
            self.assertEqual(res, 0)
            packet_text = (Path(tmpdir) / "review_packet.md").read_text(encoding="utf-8")
            self.assertIn("Tested checkout SHA: `incomplete`", packet_text)
            self.assertIn("Incomplete provenance identity", packet_text)

    def test_duplicate_phase_a_artifacts_marked_unavailable(self):
        # F5: Multiple phase-a-test-results artifacts without unambiguous attempt window attribution must mark TRX unavailable.
        event = {
            "workflow_run": {
                "id": 12345,
                "run_attempt": 2,  # Rerun attempt 2
                "head_sha": "0abef8d34a836494cdd882b844c781e32fa35322",
                "pull_requests": [{"number": 19, "base": {"sha": "1111111111111111111111111111111111111111"}}],
            }
        }
        class MockClient(generate_review_packet.GitHubClient):
            def __init__(self):
                super().__init__(token="dummy", repo="Tiflit/DXVK-Companion")
            def get_pr(self, num): return {"head": {"sha": "0abef8d34a836494cdd882b844c781e32fa35322"}, "base": {"sha": "1111111111111111111111111111111111111111"}}
            def get_issue(self, num): return {}
            def get_workflow_run_jobs(self, run_id, attempt=None): return []
            def compare_commits(self, base_sha, head_sha): return {"files": []}
            def get_run_artifacts(self, run_id):
                # Two duplicate artifacts for the run
                return [
                    {"id": 1, "name": "phase-a-test-results", "archive_download_url": "https://api.github.com/trx1", "created_at": "2026-10-03T10:00:00Z"},
                    {"id": 2, "name": "phase-a-test-results", "archive_download_url": "https://api.github.com/trx2", "created_at": "2026-10-03T11:00:00Z"},
                ]
            def download_bytes(self, url, max_bytes=50*1024*1024, timeout=30):
                return b"PK\x05\x06" + b"\x00" * 18

        with tempfile.TemporaryDirectory() as tmpdir:
            res = generate_review_packet.generate_packet(
                event=event,
                client=MockClient(),
                output_dir=Path(tmpdir),
                override_pr_number=19,
            )
            self.assertEqual(res, 0)
            packet_text = (Path(tmpdir) / "review_packet.md").read_text(encoding="utf-8")
            self.assertIn("Status: `UNAVAILABLE`", packet_text)
            self.assertIn("Ambiguous artifact attribution", packet_text)

    def test_capped_compare_response_with_all_patches_present_is_not_complete_manifest(self):
        # F12: If compare endpoint returns 300 files and all 300 files have a patch,
        # but PR changed 350 files, manifest completeness must be marked incomplete/capped,
        # and separated from patch availability.
        event = {
            "workflow_run": {
                "id": 12345,
                "run_attempt": 1,
                "head_sha": "0abef8d34a836494cdd882b844c781e32fa35322",
                "pull_requests": [{"number": 19, "base": {"sha": "1111111111111111111111111111111111111111"}}],
            }
        }
        class MockClient(generate_review_packet.GitHubClient):
            def __init__(self):
                super().__init__(token="dummy", repo="Tiflit/DXVK-Companion")
            def get_pr(self, num):
                return {
                    "head": {"sha": "0abef8d34a836494cdd882b844c781e32fa35322"},
                    "base": {"sha": "1111111111111111111111111111111111111111"},
                    "title": "Large PR",
                    "changed_files": 350,  # 350 files changed in PR!
                }
            def get_issue(self, num): return {}
            def get_workflow_run_jobs(self, run_id, attempt=None): return []
            def compare_commits(self, base_sha, head_sha):
                # 300 files returned (capped at GitHub API limit), ALL with patches!
                return {
                    "files": [
                        {"filename": f"file_{i}.txt", "patch": "@@ -1 +1 @@\n+x", "additions": 1, "deletions": 0}
                        for i in range(300)
                    ]
                }
            def get_run_artifacts(self, run_id): return []

        with tempfile.TemporaryDirectory() as tmpdir:
            res = generate_review_packet.generate_packet(
                event=event,
                client=MockClient(),
                output_dir=Path(tmpdir),
                override_pr_number=19,
            )
            self.assertEqual(res, 0)
            diff_file = Path(tmpdir) / "full-diff-pr-19.diff"
            diff_text = diff_file.read_text(encoding="utf-8")
            packet_text = (Path(tmpdir) / "review_packet.md").read_text(encoding="utf-8")

            # Must NOT claim manifest is complete just because all 300 patches are present!
            self.assertIn("Manifest completeness: incomplete", diff_text)
            self.assertIn("Patch availability: all returned patches present (300/300)", diff_text)
            self.assertIn("Manifest completeness: `incomplete", packet_text)


if __name__ == "__main__":
    unittest.main()


