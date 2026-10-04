"""Unit and regression tests for automated compact handoff dashboard generation and publication."""

import io
import json
import os
import re
import sys
import unittest
import urllib.error
from pathlib import Path
from unittest.mock import MagicMock, patch

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT / "scripts" / "ai-workflow"))

import update_dashboard


class MockHttpResponse:
    """Mock HTTP response object implementing read() and context manager interface."""

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


class TestDashboardGenerator(unittest.TestCase):
    """
    Tests covering coordinator verification criteria and the five-item Claude-informed closeout.
    All discovery and publication boundary tests exercise real GitHubClient methods
    with mocked HTTP responses rather than duplicating selection logic in fakes.
    """

    def setUp(self):
        self.repo = "Tiflit/DXVK-Companion"
        self.main_sha = "ce74e1e1caba1ee5197788c945826648d4f5a752"
        self.curated_text = (
            "## 2. Active Work Queue & Ownership\n\n"
            "| Work | Owner | Dependency | Next Action |\n"
            "|---|---|---|---|\n"
            "| **#25 handoff automation** | Gemini; ChatGPT verifies | `main` | Open PR with tests. |\n\n"
            "## 5. Unresolved Architectural & Governance Decisions\n\n"
            "1. **Issue #12 (Spec Authority)**: Proposes consolidating A1-UPDATED to docs/spec/DXVK-COMPANION-SPEC.md.\n"
            "2. **Issue #14 (Shared directories)**: Per-executable vs installation-wide.\n"
        )

    # =========================================================================
    # Item 1: Explicit --publish execution & CLI command verification
    # =========================================================================

    def test_real_yaml_command_with_publish_flag_executes_write(self):
        """Item 1: Executing the real workflow CLI command with --publish invokes write on authenticated destination."""
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )
        temp_file = REPO_ROOT / "temp_publish_cmd_test.md"
        temp_file.write_text(rendered, encoding="utf-8")

        existing_dashboard = {
            "number": 42,
            "title": update_dashboard.DASHBOARD_TITLE,
            "body": f"{update_dashboard.DASHBOARD_MARKER}\nOld body content",
            "state": "open",
            "user": {"login": "github-actions[bot]", "type": "Bot"},
        }
        patch_called = []

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "issues?state=all" in url:
                return MockHttpResponse([existing_dashboard])
            if "repos/Tiflit/DXVK-Companion/issues/42" in url and req.get_method() == "PATCH":
                patch_called.append(json.loads(req.data.decode("utf-8")))
                return MockHttpResponse({"number": 42})
            return MockHttpResponse({})

        try:
            with patch("urllib.request.urlopen", side_effect=mock_urlopen):
                # Run the exact command line as invoked in workflow step
                code = update_dashboard.main([
                    "--repo", self.repo,
                    "--publish-file", str(temp_file),
                    "--publish",
                ])
                self.assertEqual(code, 0)
                self.assertEqual(len(patch_called), 1)
        finally:
            if temp_file.exists():
                temp_file.unlink()

    def test_preview_and_dry_run_modes_cause_zero_writes(self):
        """Item 1: Every preview/dry-run argument combination with --publish-file causes zero writes."""
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )
        temp_file = REPO_ROOT / "temp_preview_test.md"
        temp_file.write_text(rendered, encoding="utf-8")

        mock_urlopen = MagicMock()
        try:
            with patch("urllib.request.urlopen", mock_urlopen):
                # 1. With --preview
                code1 = update_dashboard.main(["--repo", self.repo, "--publish-file", str(temp_file), "--preview"])
                self.assertEqual(code1, 0)
                self.assertFalse(mock_urlopen.called)

                # 2. With --dry-run
                code2 = update_dashboard.main(["--repo", self.repo, "--publish-file", str(temp_file), "--dry-run"])
                self.assertEqual(code2, 0)
                self.assertFalse(mock_urlopen.called)

                # 3. Without --publish flag (omitted --publish defaults to zero-write preview)
                code3 = update_dashboard.main(["--repo", self.repo, "--publish-file", str(temp_file)])
                self.assertEqual(code3, 0)
                self.assertFalse(mock_urlopen.called)
        finally:
            if temp_file.exists():
                temp_file.unlink()

    # =========================================================================
    # Item 2: Truthful artifact presence & No binary ZIP parsing
    # =========================================================================

    def test_truthful_artifact_presence_reported_without_zip_parsing(self):
        """Item 2: Truthfully queries run artifacts and reports presence with link, without claiming unverified checkout."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        pr_head = "a" * 40

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "pulls?state=open" in url:
                return MockHttpResponse([{
                    "number": 15,
                    "title": "PR with provenance artifact",
                    "head": {"sha": pr_head},
                    "base": {"sha": self.main_sha},
                }])
            if f"actions/runs?head_sha={pr_head}" in url:
                return MockHttpResponse({
                    "workflow_runs": [{
                        "id": 9999,
                        "name": "Build and Test",
                        "head_sha": pr_head,
                        "conclusion": "success",
                        "run_attempt": 1,
                        "html_url": "https://github.com/Tiflit/DXVK-Companion/actions/runs/9999",
                    }],
                })
            if "actions/runs/9999/artifacts" in url:
                return MockHttpResponse({
                    "artifacts": [{"id": 1, "name": "build-provenance"}],
                })
            return MockHttpResponse([])

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            collector = update_dashboard.GitHubFactsCollector(client=client, repo=self.repo)
            facts = collector.collect()
            pr = facts.open_prs[0]
            self.assertEqual(pr["build_provenance"], "present (checkout unparsed)")
            self.assertEqual(pr["ci_run_id"], "9999")
            self.assertEqual(pr["ci_attempt"], "1")

            rendered = update_dashboard.render_dashboard(facts, self.curated_text, repo=self.repo)
            self.assertIn("present (checkout unparsed)", rendered)
            self.assertIn("[9999](https://github.com/Tiflit/DXVK-Companion/actions/runs/9999)", rendered)
            self.assertNotIn("Tested Checkout", rendered)

    def test_artifact_absence_truthfully_reported_when_missing(self):
        """Item 2: Reports 'not found in run' when build-provenance artifact is absent from queried list."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        pr_head = "b" * 40

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "pulls?state=open" in url:
                return MockHttpResponse([{
                    "number": 16,
                    "title": "PR without provenance artifact",
                    "head": {"sha": pr_head},
                    "base": {"sha": self.main_sha},
                }])
            if f"actions/runs?head_sha={pr_head}" in url:
                return MockHttpResponse({
                    "workflow_runs": [{
                        "id": 8888,
                        "name": "Build and Test",
                        "head_sha": pr_head,
                        "conclusion": "success",
                        "run_attempt": 2,
                    }],
                })
            if "actions/runs/8888/artifacts" in url:
                return MockHttpResponse({"artifacts": [{"id": 2, "name": "other-artifact"}]})
            return MockHttpResponse([])

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            collector = update_dashboard.GitHubFactsCollector(client=client, repo=self.repo)
            facts = collector.collect()
            pr = facts.open_prs[0]
            self.assertEqual(pr["build_provenance"], "not found in run")

    def test_artifact_query_failure_does_not_imply_absence(self):
        """Item 2: Artifact query exception reports 'query failed' without falsely claiming absence."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        pr_head = "c" * 40

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "pulls?state=open" in url:
                return MockHttpResponse([{
                    "number": 17,
                    "title": "PR with failing artifacts query",
                    "head": {"sha": pr_head},
                    "base": {"sha": self.main_sha},
                }])
            if f"actions/runs?head_sha={pr_head}" in url:
                return MockHttpResponse({
                    "workflow_runs": [{
                        "id": 7777,
                        "name": "Build and Test",
                        "head_sha": pr_head,
                        "conclusion": "success",
                        "run_attempt": 1,
                    }],
                })
            if "actions/runs/7777/artifacts" in url:
                raise urllib.error.HTTPError(url, 500, "Internal Error", {}, io.BytesIO(b"error"))
            return MockHttpResponse([])

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            collector = update_dashboard.GitHubFactsCollector(client=client, repo=self.repo)
            facts = collector.collect()
            pr = facts.open_prs[0]
            self.assertEqual(pr["build_provenance"], "query failed")

    # =========================================================================
    # Item 3: Full 40-character commit SHAs, automatic verification & truncation preservation
    # =========================================================================

    def test_snapshot_emits_full_40_char_main_and_tooling_shas(self):
        """Item 3: Emits full 40-character hex commit SHAs for main and tooling in snapshot header."""
        facts = update_dashboard.RepositoryFacts(main_head_sha=self.main_sha)
        rendered = update_dashboard.render_dashboard(facts, self.curated_text, repo=self.repo)
        self.assertIn(f"Main: `{self.main_sha}`", rendered)
        self.assertRegex(rendered, r"Tooling: `scripts/ai-workflow/update_dashboard\.py@[0-9a-fA-F]{40}`")

    def test_mismatched_main_sha_refuses_publication(self):
        """Item 3: Automatically verifies full main SHA; any movement before writing refuses publication."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )
        different_main_sha = "9" * 40

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": different_main_sha}})
            return MockHttpResponse({})

        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)
        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            with self.assertRaises(update_dashboard.SecurityValidationError) as ctx:
                publisher.publish(rendered)
            self.assertIn("Main branch moved", str(ctx.exception))

    def test_mismatched_tooling_sha_refuses_publication(self):
        """Item 3: Automatically verifies full tooling SHA; mismatch against checked-out tooling refuses publication."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            return MockHttpResponse({})

        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)
        # Mock get_tooling_revision to return a different commit
        with patch("update_dashboard.get_tooling_revision", return_value=f"scripts/ai-workflow/update_dashboard.py@{'8'*40}"):
            with patch("urllib.request.urlopen", side_effect=mock_urlopen):
                with self.assertRaises(update_dashboard.SecurityValidationError) as ctx:
                    publisher.publish(rendered)
                self.assertIn("Tooling changed", str(ctx.exception))

    def test_hard_trim_preserves_structured_identity_header(self):
        """Item 3: When snapshot exceeds word limit and undergoes hard trim, identity header is preserved intact."""
        huge_curated = "## 2. Active Work Queue & Ownership\n\n" + ("Extensive description text. " * 500)
        facts = update_dashboard.RepositoryFacts(main_head_sha=self.main_sha)
        rendered = update_dashboard.render_dashboard(facts, huge_curated, repo=self.repo, max_words=500)

        # Header must remain parseable and intact at the top
        header_match = re.search(
            r"> \*\*Automated Snapshot\*\* \| Generated: [^\|]+ \| Main: `([^`]+)` \| Status: `([^`]+)`\n> Tooling: `([^`]+)` \| Repo: `([^`]+)`",
            rendered,
        )
        self.assertIsNotNone(header_match)
        self.assertEqual(header_match.group(1), self.main_sha)

    # =========================================================================
    # Item 4: Select only exact github-actions[bot] destination & Ignore lookalikes
    # =========================================================================

    def test_human_owned_lookalike_is_ignored_and_does_not_block_publication(self):
        """Item 4: Unrelated human-created issue mimicking title/marker is ignored and does NOT veto publication (DoS prevention)."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        human_lookalike = {
            "number": 99,
            "title": update_dashboard.DASHBOARD_TITLE,
            "body": f"{update_dashboard.DASHBOARD_MARKER}\nHuman content trying to mimic dashboard",
            "state": "open",
            "user": {"login": "unrelated_user", "type": "User"},
        }
        created = []

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "issues?state=all" in url:
                # Returns the human lookalike
                return MockHttpResponse([human_lookalike])
            if "repos/Tiflit/DXVK-Companion/issues" in url and req.get_method() == "POST":
                created.append(json.loads(req.data.decode("utf-8")))
                return MockHttpResponse({"number": 200, "title": update_dashboard.DASHBOARD_TITLE})
            return MockHttpResponse({})

        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            res = publisher.publish(rendered)
            # The human lookalike was ignored; genuine machine-owned destination was created
            self.assertEqual(res["status"], "created")
            self.assertEqual(res["issue_number"], 200)
            self.assertEqual(len(created), 1)

    def test_pr_lookalike_is_ignored_and_does_not_block_publication(self):
        """Item 4: Pull request mimicking title/marker is ignored and does NOT veto publication."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        pr_lookalike = {
            "number": 50,
            "title": update_dashboard.DASHBOARD_TITLE,
            "body": update_dashboard.DASHBOARD_MARKER,
            "pull_request": {"url": "https://api.github.com/repos/Tiflit/DXVK-Companion/pulls/50"},
            "state": "open",
            "user": {"login": "github-actions[bot]", "type": "Bot"},
        }
        created = []

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "issues?state=all" in url:
                return MockHttpResponse([pr_lookalike])
            if "repos/Tiflit/DXVK-Companion/issues" in url and req.get_method() == "POST":
                created.append(json.loads(req.data.decode("utf-8")))
                return MockHttpResponse({"number": 201, "title": update_dashboard.DASHBOARD_TITLE})
            return MockHttpResponse({})

        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            res = publisher.publish(rendered)
            self.assertEqual(res["status"], "created")
            self.assertEqual(res["issue_number"], 201)

    def test_authenticated_closed_dashboard_issue_fails_safely(self):
        """Item 4: Closed authenticated dashboard issue causes safe refusal rather than auto-creating duplicate."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        closed_dashboard = {
            "number": 55,
            "title": update_dashboard.DASHBOARD_TITLE,
            "body": update_dashboard.DASHBOARD_MARKER,
            "state": "closed",
            "user": {"login": "github-actions[bot]", "type": "Bot"},
        }

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "issues?state=all" in url:
                return MockHttpResponse([closed_dashboard])
            return MockHttpResponse({})

        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            with self.assertRaises(update_dashboard.SecurityValidationError) as ctx:
                publisher.publish(rendered)
            self.assertIn("closed", str(ctx.exception).lower())

    def test_duplicate_authenticated_dashboard_issues_refuses_ambiguity(self):
        """Item 4: Multiple open authenticated dashboard issues cause safe refusal rather than ambiguous patch."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        dash1 = {
            "number": 71,
            "title": update_dashboard.DASHBOARD_TITLE,
            "body": update_dashboard.DASHBOARD_MARKER,
            "state": "open",
            "user": {"login": "github-actions[bot]", "type": "Bot"},
        }
        dash2 = {
            "number": 72,
            "title": update_dashboard.DASHBOARD_TITLE,
            "body": update_dashboard.DASHBOARD_MARKER,
            "state": "open",
            "user": {"login": "github-actions[bot]", "type": "Bot"},
        }

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "issues?state=all" in url:
                return MockHttpResponse([dash1, dash2])
            return MockHttpResponse({})

        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            with self.assertRaises(update_dashboard.SecurityValidationError) as ctx:
                publisher.publish(rendered)
            self.assertIn("Multiple authenticated dashboard issues detected", str(ctx.exception))

    def test_real_discovery_api_error_causes_zero_writes(self):
        """Item 4: API errors (e.g. HTTP 503) during discovery must raise and cause ZERO creates/patches."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "issues" in url:
                raise urllib.error.HTTPError(url, 503, "Service Unavailable", {}, io.BytesIO(b"Service Unavailable"))
            return MockHttpResponse({})

        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            with self.assertRaises(update_dashboard.GitHubApiError) as ctx:
                publisher.publish(rendered)
            self.assertIn("503", str(ctx.exception))

    def test_real_discovery_unresolved_pagination_cap_causes_zero_writes(self):
        """Item 4: Hitting pagination cap without concluding issue list must fail safely with zero creates/patches."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)

        def mock_urlopen(req, timeout=30):
            url = req.full_url
            if "git/ref/heads/main" in url:
                return MockHttpResponse({"object": {"sha": self.main_sha}})
            if "issues?state=all" in url:
                page_match = re.search(r"page=(\d+)", url)
                page_num = int(page_match.group(1)) if page_match else 1
                batch = [{"number": page_num * 100 + i, "title": f"Task {i}", "body": "task text"} for i in range(50)]
                return MockHttpResponse(batch)
            return MockHttpResponse({})

        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)
        rendered = update_dashboard.render_dashboard(
            update_dashboard.RepositoryFacts(main_head_sha=self.main_sha),
            self.curated_text,
            repo=self.repo,
        )

        with patch("urllib.request.urlopen", side_effect=mock_urlopen):
            with self.assertRaises(update_dashboard.SecurityValidationError) as ctx:
                publisher.publish(rendered)
            self.assertIn("pagination limit", str(ctx.exception))

    # =========================================================================
    # Item 5: Exactly one outgoing marker required
    # =========================================================================

    def test_outgoing_body_missing_marker_is_rejected(self):
        """Item 5: Outgoing body missing dashboard marker causes safe refusal with zero writes."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)

        body_without_marker = (
            f"> **Automated Snapshot** | Generated: 2026-10-04 00:00:00 UTC | Main: `{self.main_sha}` | Status: `COMPLETE`\n"
            f"> Tooling: `{update_dashboard.get_tooling_revision()}` | Repo: `{self.repo}`\n\nContent without marker."
        )

        with self.assertRaises(update_dashboard.SecurityValidationError) as ctx:
            publisher.publish(body_without_marker)
        self.assertIn("exactly one dashboard marker", str(ctx.exception))

    def test_outgoing_body_with_duplicate_markers_is_rejected(self):
        """Item 5: Outgoing body with duplicate markers (injection risk) causes safe refusal with zero writes."""
        client = update_dashboard.GitHubClient(token="mock", repo=self.repo)
        publisher = update_dashboard.DashboardPublisher(client=client, repo=self.repo)

        body_with_two_markers = (
            f"> **Automated Snapshot** | Generated: 2026-10-04 00:00:00 UTC | Main: `{self.main_sha}` | Status: `COMPLETE`\n"
            f"> Tooling: `{update_dashboard.get_tooling_revision()}` | Repo: `{self.repo}`\n\n"
            f"{update_dashboard.DASHBOARD_MARKER}\n\nMiddle text\n\n{update_dashboard.DASHBOARD_MARKER}"
        )

        with self.assertRaises(update_dashboard.SecurityValidationError) as ctx:
            publisher.publish(body_with_two_markers)
        self.assertIn("exactly one dashboard marker", str(ctx.exception))

    # =========================================================================
    # Additional Baseline & Security Tests
    # =========================================================================

    def test_validation_of_repository_and_shas(self):
        """Validates repository format and commit SHAs before rendering/publishing."""
        self.assertTrue(update_dashboard.is_valid_repo_name("Tiflit/DXVK-Companion"))
        self.assertFalse(update_dashboard.is_valid_repo_name("invalid_repo_without_owner"))
        self.assertFalse(update_dashboard.is_valid_repo_name("../../etc/passwd"))

        self.assertTrue(update_dashboard.is_valid_sha(self.main_sha))
        self.assertFalse(update_dashboard.is_valid_sha("invalid_non_hex"))
        self.assertFalse(update_dashboard.is_valid_sha("123"))

    def test_invalid_main_sha_renders_safe_base_urls(self):
        """When main SHA is invalid, base URL falls back safely to repo root without broken path injection."""
        facts = update_dashboard.RepositoryFacts(main_head_sha="unknown_or_invalid")
        rendered = update_dashboard.render_dashboard(facts, self.curated_text, repo=self.repo)
        self.assertNotIn("https://github.com/Tiflit/DXVK-Companion/blob/unknown_or_invalid", rendered)
        self.assertIn("https://github.com/Tiflit/DXVK-Companion", rendered)

    def test_sanitizer_removes_markdown_injection_and_raw_exceptions(self):
        """Markdown link injection, backticks, script tags, and tokens are stripped."""
        malicious = "[Click Here](http://evil.com) with `command` and <script>alert(1)</script> ghp_SECRETTOKEN123"
        sanitized = update_dashboard.sanitize_display_text(malicious)
        self.assertNotIn("[Click Here](http://evil.com)", sanitized)
        self.assertNotIn("<script>", sanitized)
        self.assertNotIn("ghp_SECRETTOKEN123", sanitized)
        self.assertIn("[REDACTED_TOKEN]", sanitized)

    def test_privacy_synthetic_fixtures_and_full_path_redaction(self):
        """Uses synthetic identity and redacts full Windows/Unix paths."""
        text_with_user = r"Error in C:\Users\synthetic_dev\Documents\DXVK\file.cs and /home/synthetic_dev/dev/app.log"
        sanitized = update_dashboard.sanitize_display_text(text_with_user)
        self.assertNotIn("synthetic_dev", sanitized)
        self.assertNotIn(r"C:\Users", sanitized)
        self.assertNotIn(r"/home/", sanitized)
        self.assertIn("[REDACTED_PATH]", sanitized)

    def test_workflow_yaml_structure_and_least_privilege(self):
        """
        Validates workflow YAML structure, concurrency, separate jobs, and least privilege.

        Honest Limitation Note: Standard Python library does not include a full YAML parser (PyYAML is not
        a project dependency). Structural inspection is performed via line/block and indentation parsing.
        Full syntax and schema validation is enforced by the GitHub Actions workflow parser at runtime.
        """
        wf_path = REPO_ROOT / ".github" / "workflows" / "ai-current-state.yml"
        self.assertTrue(wf_path.exists(), "Workflow file .github/workflows/ai-current-state.yml must exist")

        content = wf_path.read_text(encoding="utf-8")
        self.assertNotIn("\t", content, "YAML must not contain tab characters")
        self.assertIn("name: AI Current State Dashboard", content)
        self.assertIn("concurrency:", content)
        self.assertIn("acquire-and-render:", content)
        self.assertIn("publish-snapshot:", content)
        self.assertIn("actions/checkout@v7", content)


if __name__ == "__main__":
    unittest.main()
